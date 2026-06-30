/* test_out.c — standalone smoke test for the miniaudio output path (cmm_out_*).
 * ─────────────────────────────────────────────────────────────────────────
 * Decodes a file with the CMM_Decoder backends and plays it to the default
 * output device through cmm_out_open/write/close — proving the output layer +
 * ring buffer work end-to-end, with zero managed/Unity involvement. This is the
 * native dress rehearsal for the managed mixer thread (it does exactly what that
 * thread will: decode a chunk → push to the ring → repeat, pacing off the ring).
 *
 * Build (mingw, from the CMM_Decoder dir):
 *   gcc -O2 -I. test_out.c dist/CMM_Decoder.dll -o test_out.exe
 * Run (CMM_Decoder.dll must sit beside the exe, or in dist/ on PATH):
 *   ./test_out.exe "C:\path\to\song.flac"
 * Supported by extension: .opus .ogg .flac .mp3 .wav .aiff .aif .m4a .mp4 .aac
 *
 * NOTE: plays the file in REAL TIME to completion (Ctrl+C to stop early). The
 * point is to hear continuous, glitch-free audio — any stutter means the
 * producer/ring pacing needs tuning (or the device couldn't be opened).
 */
#include "CMM_Decoder.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

#ifdef _WIN32
#include <windows.h>
#define SLEEP_MS(ms) Sleep(ms)
#else
#include <unistd.h>
#define SLEEP_MS(ms) usleep((unsigned)(ms) * 1000u)
#endif

typedef cmm_decoder* (*open_fn)(const wchar_t*, int*, int*, long long*, int*);
typedef int          (*read_fn)(cmm_decoder*, float*, int);
typedef void         (*close_fn)(cmm_decoder*);

typedef struct { const wchar_t* ext; open_fn open; read_fn read; close_fn close; } backend;

static const backend BACKENDS[] = {
    { L".opus", cmm_opus_open_file,      cmm_opus_read_float,      cmm_opus_close      },
    { L".ogg",  cmm_vorbis_open_file,    cmm_vorbis_read_float,    cmm_vorbis_close    },
    { L".flac", cmm_flac_open_file,      cmm_flac_read_float,      cmm_flac_close      },
    { L".mp3",  cmm_mp3_open_file,       cmm_mp3_read_float,       cmm_mp3_close       },
    { L".wav",  cmm_wav_open_file,       cmm_wav_read_float,       cmm_wav_close       },
    { L".aiff", cmm_wav_open_file,       cmm_wav_read_float,       cmm_wav_close       },
    { L".aif",  cmm_wav_open_file,       cmm_wav_read_float,       cmm_wav_close       },
    { L".m4a",  cmm_aac_open_file,       cmm_aac_read_float,       cmm_aac_close       },
    { L".mp4",  cmm_aac_open_file,       cmm_aac_read_float,       cmm_aac_close       },
    { L".aac",  cmm_aac_open_file,       cmm_aac_read_float,       cmm_aac_close       },
    { L".mkv",  cmm_container_open_file, cmm_container_read_float, cmm_container_close },
    { L".webm", cmm_container_open_file, cmm_container_read_float, cmm_container_close },
};

static int ends_with_ci(const wchar_t* s, const wchar_t* suffix)
{
    size_t ls = wcslen(s), lf = wcslen(suffix);
    if (lf > ls) return 0;
    const wchar_t* a = s + (ls - lf);
    for (size_t i = 0; i < lf; i++)
        if (towlower(a[i]) != towlower(suffix[i])) return 0;
    return 1;
}

static const backend* pick_backend(const wchar_t* path)
{
    for (size_t i = 0; i < sizeof(BACKENDS) / sizeof(BACKENDS[0]); i++)
        if (ends_with_ci(path, BACKENDS[i].ext)) return &BACKENDS[i];
    return NULL;
}

int main(int argc, char** argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: %s <audio-file>\n", argv[0]);
        return 2;
    }

    /* Convert argv[1] to a wide path (the *_open_file ABI takes wchar_t*). ASCII
     * test paths convert fine; non-ASCII would need the wide CRT entry point. */
    wchar_t path[4096];
    size_t n = mbstowcs(path, argv[1], sizeof(path) / sizeof(path[0]) - 1);
    if (n == (size_t)-1) { fprintf(stderr, "bad path encoding\n"); return 2; }
    path[n] = L'\0';

    const backend* be = pick_backend(path);
    if (!be) { fprintf(stderr, "unsupported extension: %ls\n", path); return 2; }

    int channels = 0, sampleRate = 0, err = 0;
    long long totalFrames = 0;
    cmm_decoder* dec = be->open(path, &channels, &sampleRate, &totalFrames, &err);
    if (!dec) { fprintf(stderr, "decode open failed (err=%d)\n", err); return 1; }
    printf("decoding: %ls\n  %d Hz, %d ch, %lld frames (%.1fs)\n",
           path, sampleRate, channels, totalFrames,
           sampleRate > 0 ? (double)totalFrames / sampleRate : 0.0);

    cmm_out* out = cmm_out_open(sampleRate, channels, 0 /*default ring*/, &err);
    if (!out) {
        fprintf(stderr, "cmm_out_open failed (err=%d)\n", err);
        be->close(dec);
        return 1;
    }
    printf("output device opened (default endpoint, shared mode). playing...\n");

    /* Decode → push loop, exactly the shape the managed mixer thread will use.
     * Pull a chunk, then write it to the ring; if the ring is momentarily full,
     * sleep and retry the remainder instead of busy-spinning or dropping. */
    enum { CHUNK_FRAMES = 2048 };
    float* buf = (float*)malloc((size_t)CHUNK_FRAMES * channels * sizeof(float));
    if (!buf) { fprintf(stderr, "OOM\n"); cmm_out_close(out); be->close(dec); return 1; }

    long long played = 0;
    for (;;) {
        int got = be->read(dec, buf, CHUNK_FRAMES);
        if (got <= 0) break;   /* EOF (0) or decode error (<0) */

        int pushed = 0;
        while (pushed < got) {
            int w = cmm_out_write(out, buf + (size_t)pushed * channels, got - pushed);
            pushed += w;
            if (pushed < got) SLEEP_MS(5);   /* ring full — let the device drain */
        }
        played += got;
    }

    /* Let the ring finish draining (≈ ring size) before tearing the device down,
     * so we don't cut off the tail. */
    printf("decoded %lld frames; draining tail...\n", played);
    SLEEP_MS(400);

    free(buf);
    cmm_out_close(out);
    be->close(dec);
    printf("done.\n");
    return 0;
}
