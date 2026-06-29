/* Decode an MP3 via the FILE* path with game-like patterns, to catch a live
 * dr_mp3 assert(). Usage: test_mp3_assert <file.mp3> <seekFrame> <chunkFrames> */
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
    cmm_decoder* d = cmm_mp3_open_file(wpath, &ch, &sr, &tot, &err);
    if (!d) { printf("open failed err=%d\n", err); return 2; }
    printf("opened ch=%d sr=%d tot=%lld  seek=%lld chunk=%d\n", ch, sr, tot, seekFrame, chunk);

    if (seekFrame >= 0) { int r = cmm_mp3_seek(d, seekFrame); printf("seek(%lld) -> %d\n", seekFrame, r); }

    float* out = malloc(sizeof(float) * (size_t)chunk * ch);
    long long total = 0; int got; long long calls = 0;
    while ((got = cmm_mp3_read_float(d, out, chunk)) > 0) { total += got; calls++; }
    free(out);
    cmm_mp3_close(d);
    printf("OK read %lld frames in %lld calls\n", total, calls);
    return 0;
}
