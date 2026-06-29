/* Reproduce the live dr_flac assert by decoding with game-like read patterns.
 * Usage: test_flac_assert <file.flac> <seekFrame> <chunkFrames>
 * _wassert prints "Assertion failed: <expr>, file dr_flac.h, line N" to stderr
 * then abort()s — so a nonzero/abnormal exit + that stderr line = reproduced. */
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "CMM_Decoder.h"

int main(int argc, char** argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    if (argc < 4) { printf("usage: %s <file> <seekFrame> <chunkFrames>\n", argv[0]); return 1; }
    long long seekFrame = atoll(argv[2]);
    int chunk = atoi(argv[3]);

    wchar_t wpath[2048];
    mbstowcs(wpath, argv[1], 2048);

    int ch=0, sr=0, err=0; long long tot=0;
    cmm_decoder* d = cmm_flac_open_file(wpath, &ch, &sr, &tot, &err);
    if (!d) { printf("open failed err=%d\n", err); return 2; }
    printf("opened ch=%d sr=%d tot=%lld  seek=%lld chunk=%d\n", ch, sr, tot, seekFrame, chunk);

    if (seekFrame >= 0) {
        int r = cmm_flac_seek(d, seekFrame);
        printf("seek(%lld) -> %d\n", seekFrame, r);
    }
    float* out = malloc(sizeof(float) * (size_t)chunk * ch);
    long long total = 0; int got; long long calls = 0;
    while ((got = cmm_flac_read_float(d, out, chunk)) > 0) {
        total += got; calls++;
    }
    free(out);
    cmm_flac_close(d);
    printf("OK read %lld frames in %lld calls\n", total, calls);
    return 0;
}
