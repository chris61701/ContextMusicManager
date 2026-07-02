/* Vorbis (libvorbisfile) smoke test: open_file (buffered ≤cap or streaming >cap,
 * per the DLL's CMM_BUFFER_MAX), full read, seek-back + re-read, then open_memory.
 * Build against a DLL, e.g.:
 *   gcc -O2 -I. test_vorbis.c -L dist -lCMM_Decoder -o build/test_vorbis.exe
 * Usage: test_vorbis.exe "path\to\file.ogg" */
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "CMM_Decoder.h"

int main(int argc, char** argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc < 2) { printf("usage: test_vorbis <file.ogg>\n"); return 1; }

    wchar_t wpath[1024];
    if (mbstowcs(wpath, argv[1], 1024) == (size_t)-1) { printf("bad path\n"); return 1; }

    int ch = 0, sr = 0, err = 0; long long tot = 0;
    cmm_decoder* d = cmm_vorbis_open_file(wpath, &ch, &sr, &tot, &err);
    if (!d) { printf("open_file FAILED err=%d\n", err); return 2; }
    printf("open_file: ch=%d sr=%d totalFrames=%lld\n", ch, sr, tot);

    float* out = (float*)malloc(sizeof(float) * 4096 * (ch ? ch : 2));
    int got = cmm_vorbis_read_float(d, out, 4096);
    printf("first read frames=%d  sample[0]=%f\n", got, got > 0 ? out[0] : 0.0f);

    long long total = got;
    while ((got = cmm_vorbis_read_float(d, out, 4096)) > 0) total += got;
    printf("total frames read=%lld (expected ~%lld)\n", total, tot);

    /* seek back to frame 0 and re-read one block — proves ov_pcm_seek works. */
    int sk = cmm_vorbis_seek(d, 0);
    got = cmm_vorbis_read_float(d, out, 4096);
    printf("seek(0)=%d, re-read frames=%d sample[0]=%f\n", sk, got, got > 0 ? out[0] : 0.0f);

    /* seek to the middle. */
    long long mid = tot / 2;
    sk = cmm_vorbis_seek(d, mid);
    got = cmm_vorbis_read_float(d, out, 4096);
    printf("seek(%lld)=%d, read frames=%d\n", mid, sk, got);
    cmm_vorbis_close(d);

    /* open_memory path: slurp the whole file and decode from RAM. */
    FILE* f = fopen(argv[1], "rb");
    if (f) {
        fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
        unsigned char* buf = (unsigned char*)malloc(n);
        fread(buf, 1, n, f); fclose(f);
        int ch2 = 0, sr2 = 0, err2 = 0; long long tot2 = 0;
        cmm_decoder* dm = cmm_vorbis_open_memory(buf, n, &ch2, &sr2, &tot2, &err2);
        free(buf);  /* open_memory copies — safe to free immediately */
        if (!dm) { printf("open_memory FAILED err=%d\n", err2); free(out); return 3; }
        long long mtotal = 0;
        while ((got = cmm_vorbis_read_float(dm, out, 4096)) > 0) mtotal += got;
        printf("open_memory: ch=%d sr=%d totalFrames=%lld, read=%lld\n", ch2, sr2, tot2, mtotal);
        cmm_vorbis_close(dm);
    }

    free(out);
    printf("OK\n");
    return 0;
}
