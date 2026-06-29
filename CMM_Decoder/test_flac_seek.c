/* FLAC seek-stress regression test for the cmm_drf_seek past-EOF crash.
 *
 * Opens a FLAC via the FILE* path (cmm_flac_open_file — the path that used the
 * buggy seek callback), then hammers cmm_flac_seek across the whole file and,
 * critically, AT and BEYOND totalFrames where dr_flac's binary search probes
 * past EOF. After each seek it reads a playback-sized buffer (mirrors the
 * resume → SeekBeforePlay → first PCMReaderCallback sequence). Completing
 * without an access violation = pass.
 *
 * Usage: test_flac_seek <file.flac>
 */
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "CMM_Decoder.h"

int main(int argc, char** argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc < 2) { printf("usage: %s <file.flac>\n", argv[0]); return 1; }

    /* argv is narrow; cmm_flac_open_file wants wide (matches the C# LPWStr marshal). */
    wchar_t wpath[2048];
    mbstowcs(wpath, argv[1], 2048);

    int ch = 0, sr = 0, err = 0;
    long long tot = 0;
    cmm_decoder* d = cmm_flac_open_file(wpath, &ch, &sr, &tot, &err);
    if (!d) { printf("open_file failed err=%d\n", err); return 2; }
    printf("opened ch=%d sr=%d totalFrames=%lld  (abi=%d)\n", ch, sr, tot, CMM_Decoder_abi());

    const int BUFFRAMES = 4096;
    float* out = (float*)malloc(sizeof(float) * BUFFRAMES * (ch > 0 ? ch : 2));
    int ok_seeks = 0, fail_seeks = 0, crashes = 0;

    /* Build the target list: a dense sweep across the file, every boundary that
     * matters, and several deliberately past EOF (the bug trigger). */
    long long targets[512];
    int nt = 0;
    for (int i = 0; i <= 200 && nt < 480; i++)        /* sweep 0..100% in 0.5% steps */
        targets[nt++] = (tot * i) / 200;
    targets[nt++] = tot - 1;                            /* last valid frame      */
    targets[nt++] = tot;                               /* exactly at end        */
    targets[nt++] = tot + 1;                           /* 1 past end            */
    targets[nt++] = tot + 44100;                       /* 1s past end           */
    targets[nt++] = tot * 2;                           /* far past end          */
    targets[nt++] = tot + 100000000LL;                 /* absurdly past end     */

    for (int i = 0; i < nt; i++) {
        long long f = targets[i];
        int r = cmm_flac_seek(d, f);                    /* must not crash, any return ok */
        if (r == 0) ok_seeks++; else fail_seeks++;
        /* Read after the seek — this is where a desynced decoder crashes. */
        int got = cmm_flac_read_float(d, out, BUFFRAMES);
        if (got < 0) crashes++;                          /* (a crash would SEGV, not return) */
    }

    /* Resume-style spot checks: seek to a mid position then drain a few buffers. */
    cmm_flac_seek(d, tot / 3);
    long long drained = 0; int got;
    while ((got = cmm_flac_read_float(d, out, BUFFRAMES)) > 0 && drained < tot) drained += got;

    free(out);
    cmm_flac_close(d);
    printf("seeks: ok=%d fail(ret-1)=%d  post-seek-reads done, drained-from-1/3=%lld\n",
           ok_seeks, fail_seeks, drained);
    printf("PASS (no crash across %d seeks incl. past-EOF)\n", nt);
    return 0;
}
