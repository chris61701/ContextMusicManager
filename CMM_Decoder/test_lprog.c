/* test_lprog.c — progressive loudness (cmm_lprog_*) against the one-shot it refines.
 *
 * The claim being made by the progressive surface is narrow and checkable: the value it
 * reports once it reaches eof must be the SAME measurement cmm_loudness_analyze would
 * have produced, and the values it reports before then must be useful approximations of
 * it. This measures both on real files, so the answer covers real gating behaviour
 * (silence, intros, wide dynamics) rather than a synthetic tone.
 *
 * Build (mingw, from CMM_Decoder dir):
 *   gcc -O2 -I. test_lprog.c dist/CMM_Decoder.dll -o test_lprog.exe
 * Run:
 *   ./test_lprog.exe <chunkSeconds> <file> [file...]
 *
 * Paths arrive as ANSI argv, so non-ASCII filenames are mangled before we ever see
 * them (this cost a session once, appearing as six decoder regressions). Wide argv is
 * read from the OS directly instead.
 */
#include "CMM_Decoder.h"

#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>
#include <wchar.h>
#include <windows.h>

/* kind values match AudioDecoder.ClassifyReader / the LD_READ table in cmm_loudness.c. */
enum { K_OPUS = 0, K_FLAC, K_VORBIS, K_MP3, K_WAV, K_AAC, K_CONTAINER };

static int kind_of(const wchar_t* path) {
    const wchar_t* dot = wcsrchr(path, L'.');
    if (!dot) return -1;
    if (!_wcsicmp(dot, L".opus")) return K_OPUS;
    if (!_wcsicmp(dot, L".ogg") || !_wcsicmp(dot, L".oga")) return K_VORBIS;
    if (!_wcsicmp(dot, L".flac")) return K_FLAC;
    if (!_wcsicmp(dot, L".mp3"))  return K_MP3;
    if (!_wcsicmp(dot, L".wav") || !_wcsicmp(dot, L".wave") ||
        !_wcsicmp(dot, L".aiff") || !_wcsicmp(dot, L".aif")) return K_WAV;
    if (!_wcsicmp(dot, L".m4a") || !_wcsicmp(dot, L".mp4") || !_wcsicmp(dot, L".aac") ||
        !_wcsicmp(dot, L".m4v") || !_wcsicmp(dot, L".3gp")) return K_AAC;
    if (!_wcsicmp(dot, L".mkv") || !_wcsicmp(dot, L".webm")) return K_CONTAINER;
    return -1;
}

typedef cmm_decoder* (*open_fn)(const wchar_t*, int*, int*, long long*, int*);
static const open_fn OPEN[7] = {
    cmm_opus_open_file, cmm_flac_open_file, cmm_vorbis_open_file,
    cmm_mp3_open_file,  cmm_wav_open_file,  cmm_aac_open_file, cmm_container_open_file
};

static double now_ms(void) {
    LARGE_INTEGER f, t;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t);
    return 1000.0 * t.QuadPart / f.QuadPart;
}

int main(void) {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argc < 3) {
        printf("usage: test_lprog.exe <chunkSeconds> <file> [file...]\n");
        return 2;
    }
    double chunkSeconds = _wtof(argv[1]);
    if (chunkSeconds <= 0) chunkSeconds = 300.0;

    int files = 0, pass = 0;
    double worstFinal = 0, worstFirst = 0;

    for (int i = 2; i < argc; i++) {
        const wchar_t* path = argv[i];
        int kind = kind_of(path);
        if (kind < 0) { wprintf(L"SKIP (ext) %ls\n", path); continue; }

        int ch = 0, sr = 0, err = 0;
        long long total = 0;
        cmm_decoder* d = OPEN[kind](path, &ch, &sr, &total, &err);
        if (!d) { wprintf(L"FAIL open err=%d  %ls\n", err, path); continue; }

        /* Reference: the whole-file one-shot. Takes ownership of d. */
        double t0 = now_ms();
        double refLufs = 0, refPeak = 0;
        int refValid = cmm_loudness_analyze(d, kind, sr, ch, &refLufs, &refPeak);
        double refMs = now_ms() - t0;

        /* Progressive over a fresh handle on the same file. */
        d = OPEN[kind](path, &ch, &sr, &total, &err);
        if (!d) { wprintf(L"FAIL reopen err=%d  %ls\n", err, path); continue; }
        cmm_lprog* p = cmm_lprog_begin(d, kind, sr, ch);
        if (!p) { wprintf(L"FAIL lprog_begin  %ls\n", path); continue; }

        files++;
        double durMin = (sr > 0 && total > 0) ? (double)total / sr / 60.0 : 0.0;
        wprintf(L"\n%ls\n  %.1f min  %d Hz  %d ch   ref %s%.2f LUFS (%.0f ms)\n",
                path, durMin, sr, ch,
                refValid ? L"" : L"INVALID ", refLufs, refMs);

        double firstLufs = 0; int haveFirst = 0;
        double lastLufs = 0; int lastValid = 0, eof = 0, steps = 0;
        double tStart = now_ms(), prevMs = tStart;
        while (!eof) {
            double lufs, peak, secs;
            int valid = cmm_lprog_step(p, chunkSeconds, &lufs, &peak, &secs, &eof);
            double ms = now_ms(); double stepMs = ms - prevMs; prevMs = ms;
            steps++;
            if (valid && !haveFirst) { haveFirst = 1; firstLufs = lufs; }
            /* Print the first few and the last; the middle of a long file is monotone. */
            if (steps <= 4 || eof)
                wprintf(L"    +%6.1f s  %s%7.2f LUFS  (%+5.2f vs final)  %.0f ms\n",
                        secs, valid ? L"" : L"----- ", lufs,
                        refValid ? lufs - refLufs : 0.0, stepMs);
            lastLufs = lufs; lastValid = valid;
        }
        double progMs = now_ms() - tStart;
        cmm_lprog_free(p);

        /* At eof the two must agree to the bit — same blocks, same gating, same order.
         * A tolerance here would hide a real divergence, so this is exact. */
        int agree = (lastValid == refValid) && (!refValid || lastLufs == refLufs);
        double dFinal = refValid && lastValid ? fabs(lastLufs - refLufs) : 0;
        double dFirst = refValid && haveFirst ? fabs(firstLufs - refLufs) : 0;
        if (dFinal > worstFinal) worstFinal = dFinal;
        if (dFirst > worstFirst) worstFirst = dFirst;

        wprintf(L"  %s  final delta %.6f dB   first-slice delta %.2f dB   %d steps, %.0f ms "
                L"(%.2fx one-shot)\n",
                agree ? L"PASS" : L"FAIL", dFinal, dFirst, steps, progMs,
                refMs > 0 ? progMs / refMs : 0.0);
        if (agree) pass++;
    }

    wprintf(L"\n%d/%d files agree at eof. worst final delta %.6f dB, "
            L"worst first-slice delta %.2f dB\n", pass, files, worstFinal, worstFirst);
    return pass == files ? 0 : 1;
}
