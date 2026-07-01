/* test_mix.c — standalone smoke test for the native MIXER (cmm_mix_*).
 * ─────────────────────────────────────────────────────────────────────────
 * Opens a decoder (as the managed side would) and hands it to the native mixer
 * via cmm_mix_add, then lets the mixer's own producer thread decode→mix→output.
 * Proves the whole producer loop runs natively end-to-end with NO managed code.
 * A second file argument tests a crossfade (same-bus, any sample rate).
 *
 * Build (mingw, from CMM_Decoder dir):
 *   gcc -O2 -I. test_mix.c dist/CMM_Decoder.dll -o test_mix.exe
 * Run (CMM_Decoder.dll beside the exe):
 *   ./test_mix.exe "song1.flac" ["song2.mp3"]
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

/* extension → (open fn, mixer kind) — kind matches AudioDecoder.ClassifyReader. */
typedef struct { const wchar_t* ext; open_fn open; int kind; } backend;
static const backend BACKENDS[] = {
    { L".opus", cmm_opus_open_file,      0 },
    { L".ogg",  cmm_vorbis_open_file,    2 },
    { L".flac", cmm_flac_open_file,      1 },
    { L".mp3",  cmm_mp3_open_file,       3 },
    { L".wav",  cmm_wav_open_file,       4 },
    { L".aiff", cmm_wav_open_file,       4 },
    { L".aif",  cmm_wav_open_file,       4 },
    { L".m4a",  cmm_aac_open_file,       5 },
    { L".mp4",  cmm_aac_open_file,       5 },
    { L".aac",  cmm_aac_open_file,       5 },
    { L".mkv",  cmm_container_open_file, 6 },
    { L".webm", cmm_container_open_file, 6 },
};

static int ends_with_ci(const wchar_t* s, const wchar_t* suffix) {
    size_t ls = wcslen(s), lf = wcslen(suffix);
    if (lf > ls) return 0;
    const wchar_t* a = s + (ls - lf);
    for (size_t i = 0; i < lf; i++)
        if (towlower(a[i]) != towlower(suffix[i])) return 0;
    return 1;
}
static const backend* pick_backend(const wchar_t* path) {
    for (size_t i = 0; i < sizeof(BACKENDS)/sizeof(BACKENDS[0]); i++)
        if (ends_with_ci(path, BACKENDS[i].ext)) return &BACKENDS[i];
    return NULL;
}

/* Open a file → hand it to the mixer. Returns the voice id (0 on failure). */
static long long add_file(const char* utf8path, float fade, int snapTight) {
    wchar_t path[4096];
    size_t n = mbstowcs(path, utf8path, sizeof(path)/sizeof(path[0]) - 1);
    if (n == (size_t)-1) { fprintf(stderr, "bad path encoding\n"); return 0; }
    path[n] = L'\0';

    const backend* be = pick_backend(path);
    if (!be) { fprintf(stderr, "unsupported extension: %ls\n", path); return 0; }

    int channels = 0, sampleRate = 0, err = 0;
    long long totalFrames = 0;
    cmm_decoder* dec = be->open(path, &channels, &sampleRate, &totalFrames, &err);
    if (!dec) { fprintf(stderr, "decode open failed (err=%d) for %ls\n", err, path); return 0; }

    printf("add: %ls\n  %d Hz, %d ch, %lld frames (%.1fs), kind=%d\n",
           path, sampleRate, channels, totalFrames,
           sampleRate > 0 ? (double)totalFrames / sampleRate : 0.0, be->kind);

    long long id = cmm_mix_add(dec, be->kind, sampleRate, channels, totalFrames,
                               fade, 0 /*loop*/, 0 /*startFrame*/, 1.0f /*trackGain*/,
                               -1.0f /*duckOthersTo*/, snapTight);
    if (id == 0) fprintf(stderr, "cmm_mix_add rejected %ls\n", path);
    return id;
}

int main(int argc, char** argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <file1> [file2]\n", argv[0]); return 2; }

    int err = 0;
    /* 48k stereo bus; tight ~200ms (9600), max ~15s (720000) — same as the managed config. */
    if (cmm_mix_start(48000, 2, 9600, 720000, &err) != 0) {
        fprintf(stderr, "cmm_mix_start failed (err=%d)\n", err); return 1;
    }
    cmm_mix_set_master(1.0f);
    printf("mixer started (48k stereo). playing...\n");

    long long id = add_file(argv[1], 1.0f, 1);
    if (id == 0) return 1;

    /* Poll the playhead for a while; crossfade to file2 (if given) after 6s. */
    int crossfaded = 0;
    for (int t = 0; t < 40; t++) {   /* up to 20s */
        SLEEP_MS(500);
        double pos = 0, len = 0; int active = 0;
        cmm_mix_get_state(id, &pos, &len, &active);
        double rem = cmm_mix_remaining(id);
        printf("  [%4.1fs] pos=%.2f len=%.2f active=%d remaining=%.2f\n",
               t * 0.5, pos, len, active, rem);

        if (argc >= 3 && !crossfaded && t == 12) {
            printf("--- crossfading to file2 ---\n");
            long long id2 = add_file(argv[2], 3.0f, 1);
            if (id2) { id = id2; crossfaded = 1; }
        }
        if (active == 0 && (argc < 3 || crossfaded)) { printf("track ended.\n"); break; }
    }

    cmm_mix_stop(1.0f, 1);
    SLEEP_MS(1200);
    printf("done.\n");
    return 0;
}
