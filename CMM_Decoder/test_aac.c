/* Standalone sanity test for the AAC backend: decodes a file via both the
 * memory and file-open paths, compares frame count + sample peak (bit-parity
 * like CmmBench --verify-fileopen), then exercises a seek. Build:
 *   gcc -O2 -o test_aac.exe test_aac.c dist/CMM_Decoder.dll
 * Run: ./test_aac.exe <file> */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

typedef struct cmm_decoder cmm_decoder;
extern cmm_decoder* cmm_aac_open_memory(const unsigned char*, long long, int*, int*, long long*, int*);
extern cmm_decoder* cmm_aac_open_file(const wchar_t*, int*, int*, long long*, int*);
extern int  cmm_aac_read_float(cmm_decoder*, float*, int);
extern int  cmm_aac_seek(cmm_decoder*, long long);
extern void cmm_aac_close(cmm_decoder*);
extern int  CMM_Decoder_abi(void);

static long long drain(cmm_decoder* d, int ch, float* peak) {
    float buf[4096]; long long frames = 0; float pk = 0; int got;
    while ((got = cmm_aac_read_float(d, buf, 4096 / (ch > 0 ? ch : 2))) > 0) {
        for (int i = 0; i < got * ch; i++) { float a = buf[i] < 0 ? -buf[i] : buf[i]; if (a > pk) pk = a; }
        frames += got;
    }
    *peak = pk; return frames;
}

int main(int argc, char** argv) {
    if (argc < 2) { printf("usage: test_aac <file>\n"); return 2; }
    printf("ABI=%d\n", CMM_Decoder_abi());

    /* memory path */
    FILE* f = fopen(argv[1], "rb");
    if (!f) { printf("open fail\n"); return 1; }
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 0, SEEK_SET);
    unsigned char* raw = malloc(sz); fread(raw, 1, sz, f); fclose(f);

    int chM=0, srM=0, errM=0; long long tfM=0;
    cmm_decoder* dm = cmm_aac_open_memory(raw, sz, &chM, &srM, &tfM, &errM);
    if (!dm) { printf("open_memory FAIL err=%d\n", errM); return 1; }
    float pkM; long long fM = drain(dm, chM, &pkM);
    printf("MEMORY: ch=%d sr=%d totalFrames=%lld decoded=%lld peak=%.5f\n", chM, srM, tfM, fM, pkM);
    cmm_aac_close(dm);

    /* file path */
    wchar_t wpath[4096]; mbstowcs(wpath, argv[1], 4096);
    int chF=0, srF=0, errF=0; long long tfF=0;
    cmm_decoder* df = cmm_aac_open_file(wpath, &chF, &srF, &tfF, &errF);
    if (!df) { printf("open_file FAIL err=%d\n", errF); return 1; }
    float pkF; long long fF = drain(df, chF, &pkF);
    printf("FILE:   ch=%d sr=%d totalFrames=%lld decoded=%lld peak=%.5f\n", chF, srF, tfF, fF, pkF);
    cmm_aac_close(df);

    int parity = (chM==chF && srM==srF && fM==fF && pkM==pkF);
    printf("PARITY: %s\n", parity ? "PASS" : "FAIL");

    /* seek test: reopen memory, seek to ~half, read a bit */
    cmm_decoder* ds = cmm_aac_open_memory(raw, sz, &chM, &srM, &tfM, &errM);
    long long target = fM / 2;
    int sr = cmm_aac_seek(ds, target);
    float sbuf[4096]; int sgot = cmm_aac_read_float(ds, sbuf, 1024);
    printf("SEEK to %lld: rc=%d firstRead=%d frames\n", target, sr, sgot);
    cmm_aac_close(ds);

    free(raw);
    return parity ? 0 : 1;
}
