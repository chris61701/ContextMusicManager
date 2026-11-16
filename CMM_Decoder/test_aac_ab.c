/* A/B harness for the AAC backend swap (fdk-aac + minimp4 -> FFmpeg). Loads TWO builds of
 * CMM_Decoder.dll side by side and, for every file given, checks:
 *
 *   open      both builds open it; rate / channels / totalFrames side by side
 *   length    decoded frame count == the build's own totalFrames (the mixer and the loudness
 *             cache trust that number)
 *   audio     RMS and peak per build; then the two decodes are aligned (the new one trims
 *             MP4 encoder priming, the old did not) and compared as SNR in dB
 *   seek      NEW build: seek to 10/50/90 % and compare against a linear decode of the same
 *             span (max abs difference)
 *   memory    NEW build: open_memory vs open_file produce the same samples
 *   leak      NEW build: native live-heap delta across repeated open/read/close
 *   reference with CMM_REF=<wav>: each build aligned against the original audio. SNR is
 *             reported full-band and low-passed: HE-AAC rebuilds its top octave from noise
 *             (SBR), so two correct decoders differ there sample by sample; below the
 *             crossover they must agree.
 *
 * Modes (environment):
 *   CMM_PROBE=1      open + read 5 s with both builds only; lists every file whose outcome
 *                    differs (the old build failing where the new opens = newly playable;
 *                    the reverse = a regression)
 *   CMM_BIG=1        huge files (multi-GB videos): open time, native heap peak during the open,
 *                    time to read 10 s, then seek to the middle and read 10 s, both builds
 *   CMM_STRESS=<n>   NEW build only: n threads decode the given files concurrently, each
 *                    result checked against a single-threaded decode (the bcc062b race)
 * A single argument @list.txt reads one UTF-8 path per line.
 *
 * Build:  gcc -O2 -municode -o test_aac_ab.exe test_aac_ab.c
 * Run:    test_aac_ab.exe <old.dll> <new.dll> <file>...
 * .mkv/.webm go through the container API, everything else through the AAC API. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <wchar.h>

typedef struct cmm_decoder cmm_decoder;
typedef cmm_decoder* (*open_file_fn)(const wchar_t*, int*, int*, long long*, int*);
typedef cmm_decoder* (*open_mem_fn)(const unsigned char*, long long, int*, int*, long long*, int*);
typedef int  (*read_fn)(cmm_decoder*, float*, int);
typedef int  (*seek_fn)(cmm_decoder*, long long);
typedef void (*close_fn)(cmm_decoder*);
typedef void (*meminfo_fn)(long long*, long long*, long long*, long long*, long long*);
typedef void (*resetpeak_fn)(void);

typedef struct {
    open_file_fn aac_open, mkv_open; open_mem_fn aac_mem;
    read_fn aac_read, mkv_read; seek_fn aac_seek, mkv_seek; close_fn aac_close, mkv_close;
    meminfo_fn meminfo;
    open_file_fn wav_open; read_fn wav_read; close_fn wav_close;
    resetpeak_fn reset_peak;
} api;

static int load(const wchar_t* path, api* a)
{
    HMODULE m = LoadLibraryW(path);
    if (!m) { fwprintf(stderr, L"cannot load %ls (%lu)\n", path, GetLastError()); return -1; }
#define P(f, n) a->f = (void*)GetProcAddress(m, n)
    P(aac_open, "cmm_aac_open_file"); P(aac_mem, "cmm_aac_open_memory"); P(aac_read, "cmm_aac_read_float");
    P(aac_seek, "cmm_aac_seek"); P(aac_close, "cmm_aac_close");
    P(mkv_open, "cmm_container_open_file"); P(mkv_read, "cmm_container_read_float");
    P(mkv_seek, "cmm_container_seek"); P(mkv_close, "cmm_container_close");
    P(meminfo, "cmm_meminfo");
    P(reset_peak, "cmm_meminfo_reset_peak");
    P(wav_open, "cmm_wav_open_file"); P(wav_read, "cmm_wav_read_float"); P(wav_close, "cmm_wav_close");
#undef P
    return 0;
}

typedef struct { cmm_decoder* d; int mkv; const api* a; int ch, sr; long long total; double open_ms; } dec;

static double now_ms(void) { LARGE_INTEGER f, c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c); return 1000.0 * c.QuadPart / f.QuadPart; }

static int dopen(dec* x, const api* a, const wchar_t* path, int mkv)
{
    int e = 0; x->a = a; x->mkv = mkv; x->ch = x->sr = 0; x->total = 0;
    double t = now_ms();
    x->d = (mkv ? a->mkv_open : a->aac_open)(path, &x->ch, &x->sr, &x->total, &e);
    x->open_ms = now_ms() - t;
    return x->d ? 0 : -1;
}
static int dread(dec* x, float* o, int n) { return (x->mkv ? x->a->mkv_read : x->a->aac_read)(x->d, o, n); }
static int dseek(dec* x, long long f)     { return (x->mkv ? x->a->mkv_seek : x->a->aac_seek)(x->d, f); }
static void dclose(dec* x)                { if (x->d) (x->mkv ? x->a->mkv_close : x->a->aac_close)(x->d); x->d = NULL; }

/* Decode everything; keep mono-summed samples (for alignment) up to `keep`. */
static long long drain(dec* x, double* rms, float* peak, float* mono, long long keep)
{
    static float buf[8192 * 8];
    long long frames = 0; double ss = 0; float pk = 0; int got;
    while ((got = dread(x, buf, 8192)) > 0) {
        for (int i = 0; i < got; i++) {
            float m = 0;
            for (int c = 0; c < x->ch; c++) {
                float v = buf[(size_t)i * x->ch + c];
                ss += (double)v * v; float av = v < 0 ? -v : v; if (av > pk) pk = av; m += v;
            }
            if (mono && frames + i < keep) mono[frames + i] = m / x->ch;
        }
        frames += got;
    }
    *rms = frames ? 10.0 * log10(ss / ((double)frames * x->ch) + 1e-30) : -999; *peak = pk;
    return frames;
}

/* 16-tap moving average: a crude low-pass, ~2.7 kHz first null at 44.1 kHz. Below the SBR
 * crossover of any HE-AAC stream, so it compares only what both decoders reproduce exactly. */
static float* lowpass(const float* x, long long n)
{
    float* y = malloc(sizeof(float) * (n > 0 ? n : 1)); double acc = 0;
    for (long long i = 0; i < n; i++) { acc += x[i]; if (i >= 16) acc -= x[i - 16]; y[i] = (float)(acc / 16); }
    return y;
}

/* Best lag of b against a (b[i+lag] ~ a[i]) by normalised correlation, then SNR over the overlap. */
static double align_snr(const float* a, long long na, const float* b, long long nb, int* lagOut, double* lpSnr)
{
    const long long W0 = 44100 * 2, W = 44100 * 3;   /* match on 3 s starting 2 s in */
    if (na < W0 + W + 8192 || nb < W0 + W + 8192) { *lagOut = 0; return NAN; }
    int best = 0; double bestc = -2;
    for (int lag = -8192; lag <= 8192; lag++) {
        double c = 0, ea = 0, eb = 0;
        for (long long i = W0; i < W0 + W; i += 4) {
            long long j = i + lag; if (j < 0 || j >= nb) continue;
            c += (double)a[i] * b[j]; ea += (double)a[i] * a[i]; eb += (double)b[j] * b[j];
        }
        double cc = c / sqrt(ea * eb + 1e-30);
        if (cc > bestc) { bestc = cc; best = lag; }
    }
    double sig = 0, err = 0;
    for (long long i = 8192; i < na - 8192; i++) {
        long long j = i + best; if (j < 0 || j >= nb) continue;
        double d = (double)a[i] - b[j]; sig += (double)a[i] * a[i]; err += d * d;
    }
    *lagOut = best;
    if (lpSnr) {
        float *la = lowpass(a, na), *lb = lowpass(b, nb); double s2 = 0, e2 = 0;
        for (long long i = 8192; i < na - 8192; i++) {
            long long j = i + best; if (j < 0 || j >= nb) continue;
            double d = (double)la[i] - lb[j]; s2 += (double)la[i] * la[i]; e2 += d * d;
        }
        *lpSnr = 10.0 * log10(s2 / (e2 + 1e-30)); free(la); free(lb);
    }
    return 10.0 * log10(sig / (err + 1e-30));
}

static float g_lpdiff, g_rmsdiff;   /* side results of the last window_diff */
static float window_diff(const api* a, const wchar_t* path, int mkv, long long at, int n)
{
    dec lin, sk; float *x = malloc(sizeof(float) * n * 8), *y = malloc(sizeof(float) * n * 8), tmp[8192 * 8];
    float md = -1;
    if (dopen(&lin, a, path, mkv) || dopen(&sk, a, path, mkv)) goto out;
    long long todo = at; int g;
    while (todo > 0 && (g = dread(&lin, tmp, todo < 8192 ? (int)todo : 8192)) > 0) todo -= g;
    int gl = 0, gs = 0;
    while (gl < n && (g = dread(&lin, x + (size_t)gl * lin.ch, n - gl)) > 0) gl += g;
    if (dseek(&sk, at) != 0) { md = -2; goto out; }
    while (gs < n && (g = dread(&sk, y + (size_t)gs * sk.ch, n - gs)) > 0) gs += g;
    int m = gl < gs ? gl : gs; md = 0;
    for (long long i = 0; i < (long long)m * lin.ch; i++) { float d = fabsf(x[i] - y[i]); if (d > md) md = d; }
    {   /* low band and level of the same window, channel 0 */
        float *cx = malloc(sizeof(float) * m), *cy = malloc(sizeof(float) * m); double ex = 0, ey = 0;
        for (int i = 0; i < m; i++) { cx[i] = x[(size_t)i * lin.ch]; cy[i] = y[(size_t)i * lin.ch]; ex += (double)cx[i] * cx[i]; ey += (double)cy[i] * cy[i]; }
        float *lx = lowpass(cx, m), *ly = lowpass(cy, m); g_lpdiff = 0;
        for (int i = 32; i < m; i++) { float d = fabsf(lx[i] - ly[i]); if (d > g_lpdiff) g_lpdiff = d; }
        g_rmsdiff = (float)(10.0 * log10((ey + 1e-30) / (ex + 1e-30)));
        free(cx); free(cy); free(lx); free(ly);
    }
    if (gl != gs) md = 9;
out:
    dclose(&lin); dclose(&sk); free(x); free(y);
    return md;
}

static int is_mkv(const wchar_t* path)
{
    const wchar_t* ext = wcsrchr(path, L'.');
    return ext && (!_wcsicmp(ext, L".mkv") || !_wcsicmp(ext, L".webm"));
}

/* ── probe: does it open and play 5 s, old vs new ── */
static int probe(const api* A, const api* B, wchar_t** files, int n)
{
    int both = 0, neither = 0, gained = 0, lost = 0;
    float buf[8192 * 8];
    for (int i = 0; i < n; i++) {
        int mkv = is_mkv(files[i]), ok[2] = { 0, 0 }; int sr[2] = { 0, 0 };
        for (int w = 0; w < 2; w++) {
            dec x;
            if (dopen(&x, w ? B : A, files[i], mkv) == 0) {
                long long want = 5LL * x.sr, got = 0; int g;
                while (got < want && (g = dread(&x, buf, 8192)) > 0) got += g;
                ok[w] = got > 0 || x.total == 0; sr[w] = x.sr;
            }
            dclose(&x);
        }
        if (ok[0] && ok[1]) both++;
        else if (!ok[0] && !ok[1]) { neither++; wprintf(L"  neither  %ls\n", files[i]); }
        else if (ok[1]) { gained++; wprintf(L"  NEWLY PLAYABLE (%d Hz)  %ls\n", sr[1], files[i]); }
        else { lost++; wprintf(L"  REGRESSION  %ls\n", files[i]); }
        if ((i + 1) % 500 == 0) { fwprintf(stderr, L"  ... %d/%d\n", i + 1, n); }
    }
    wprintf(L"\nprobe: %d files: both play %d, newly playable %d, REGRESSIONS %d, neither %d\n",
            n, both, gained, lost, neither);
    return lost;
}

/* ── stress: concurrent decodes must equal single-threaded ones ── */
typedef struct { const api* a; wchar_t** files; int nfiles; unsigned long long* ref; volatile LONG next; volatile LONG bad; volatile LONG done; int iters; } stress_ctx;

static unsigned long long checksum(const api* a, const wchar_t* path)
{
    dec x; float buf[8192 * 8]; int g; unsigned long long h = 1469598103934665603ULL;
    if (dopen(&x, a, path, is_mkv(path))) return 0;
    while ((g = dread(&x, buf, 8192)) > 0) {
        const unsigned char* p = (const unsigned char*)buf;
        for (size_t k = 0; k < (size_t)g * x.ch * sizeof(float); k++) h = (h ^ p[k]) * 1099511628211ULL;
    }
    dclose(&x);
    return h;
}

static DWORD WINAPI stress_thread(LPVOID v)
{
    stress_ctx* c = (stress_ctx*)v;
    for (;;) {
        LONG k = InterlockedIncrement(&c->next) - 1;
        if (k >= c->iters) break;
        int f = (int)(((unsigned)k * 2654435761u) % (unsigned)c->nfiles);
        if (checksum(c->a, c->files[f]) != c->ref[f]) { InterlockedIncrement(&c->bad); wprintf(L"  MISMATCH under concurrency: %ls\n", c->files[f]); }
        InterlockedIncrement(&c->done);
    }
    return 0;
}

static int stress(const api* B, wchar_t** files, int n, int threads)
{
    stress_ctx c = { B, files, n, malloc(sizeof(unsigned long long) * n), 0, 0, 0, n * 8 };
    for (int i = 0; i < n; i++) c.ref[i] = checksum(B, files[i]);
    long long l0 = 0, l1 = 0; if (B->meminfo) B->meminfo(&l0, 0, 0, 0, 0);
    double t = now_ms();
    HANDLE* th = malloc(sizeof(HANDLE) * threads);
    for (int i = 0; i < threads; i++) th[i] = CreateThread(NULL, 0, stress_thread, &c, 0, NULL);
    WaitForMultipleObjects(threads, th, TRUE, INFINITE);
    if (B->meminfo) B->meminfo(&l1, 0, 0, 0, 0);
    wprintf(L"\nstress: %d threads, %ld decodes in %.1f s: %ld mismatched, native live heap %+lld bytes\n",
            threads, c.done, (now_ms() - t) / 1000, c.bad, l1 - l0);
    return (int)c.bad + (l1 != l0);
}

/* ── big: what a multi-GB video costs to open and play from ── */
static int big(const api* A, const api* B, wchar_t** files, int n)
{
    static float buf[8192 * 8]; int bad = 0;
    for (int i = 0; i < n; i++) {
        const wchar_t* name = wcsrchr(files[i], L'\\'); name = name ? name + 1 : files[i];
        wprintf(L"\n%ls\n", name);
        for (int w = 0; w < 2; w++) {
            const api* a = w ? B : A; dec x; long long l0 = 0, pk = 0, l1 = 0;
            if (a->reset_peak && a->meminfo) { a->meminfo(&l0, 0, 0, 0, 0); a->reset_peak(); }
            int ok = dopen(&x, a, files[i], is_mkv(files[i])) == 0;
            if (a->meminfo) a->meminfo(&l1, &pk, 0, 0, 0);
            if (!ok) { wprintf(L"  %ls: FAIL to open (%.0f ms)\n", w ? L"new" : L"old", x.open_ms); if (w) bad++; continue; }
            double t = now_ms(); long long got = 0; int g;
            while (got < 10LL * x.sr && (g = dread(&x, buf, 8192)) > 0) got += g;
            double tRead = now_ms() - t;
            t = now_ms(); int sk = dseek(&x, x.total / 2); long long got2 = 0;
            while (sk == 0 && got2 < 10LL * x.sr && (g = dread(&x, buf, 8192)) > 0) got2 += g;
            double tSeek = now_ms() - t;
            wprintf(L"  %ls: open %8.1f ms  %5d Hz %d ch  %.2f h  | heap peak %+.1f MB, held %+.1f MB | read 10 s %6.1f ms | seek 50%% + 10 s %7.1f ms %ls\n",
                    w ? L"new" : L"old", x.open_ms, x.sr, x.ch, x.total / (double)(x.sr ? x.sr : 1) / 3600.0,
                    (pk - l0) / 1048576.0, (l1 - l0) / 1048576.0, tRead, tSeek,
                    (sk == 0 && got2 >= 10LL * x.sr) ? L"" : L"SEEK FAILED");
            if (w && (sk != 0 || got2 < 10LL * x.sr)) bad++;
            dclose(&x);
        }
    }
    return bad;
}

static unsigned char* slurp(const wchar_t* p, long long* n)
{
    FILE* f = _wfopen(p, L"rb"); if (!f) return NULL;
    _fseeki64(f, 0, SEEK_END); *n = _ftelli64(f); _fseeki64(f, 0, SEEK_SET);
    unsigned char* b = malloc((size_t)*n);
    if (b && fread(b, 1, (size_t)*n, f) != (size_t)*n) { free(b); b = NULL; }
    fclose(f); return b;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 4) { fwprintf(stderr, L"usage: %ls old.dll new.dll files...\n", argv[0]); return 2; }
    api A, B;
    if (load(argv[1], &A) || load(argv[2], &B)) return 2;
    if (argc == 4 && argv[3][0] == L'@') {               /* @list.txt: one UTF-8 path per line */
        FILE* lf = _wfopen(argv[3] + 1, L"rb"); if (!lf) return 2;
        static char line[4096]; int cap = 1024, cnt = 0; wchar_t** v = malloc(sizeof(wchar_t*) * cap);
        v[cnt++] = argv[0]; v[cnt++] = argv[1]; v[cnt++] = argv[2];
        while (fgets(line, sizeof(line), lf)) {
            size_t L = strlen(line); while (L && (line[L - 1] == '\n' || line[L - 1] == '\r')) line[--L] = 0;
            if (!L) continue;
            if (cnt == cap) { cap *= 2; v = realloc(v, sizeof(wchar_t*) * cap); }
            int wl = MultiByteToWideChar(CP_UTF8, 0, line, -1, NULL, 0);
            v[cnt] = malloc(sizeof(wchar_t) * wl); MultiByteToWideChar(CP_UTF8, 0, line, -1, v[cnt], wl); cnt++;
        }
        fclose(lf); argc = cnt; argv = v;
    }
    if (getenv("CMM_BIG"))    return big(&A, &B, argv + 3, argc - 3) ? 1 : 0;
    if (getenv("CMM_PROBE"))  return probe(&A, &B, argv + 3, argc - 3) ? 1 : 0;
    if (getenv("CMM_STRESS")) return stress(&B, argv + 3, argc - 3, atoi(getenv("CMM_STRESS"))) ? 1 : 0;
    const long long KEEP = 44100LL * 20;
    float *ma = malloc(sizeof(float) * KEEP), *mb = malloc(sizeof(float) * KEEP);
    int fails = 0;
    float* mr = NULL; long long nr = 0;
    const char* refp = getenv("CMM_REF");
    if (refp && B.wav_open) {
        wchar_t wr[1024]; MultiByteToWideChar(CP_UTF8, 0, refp, -1, wr, 1024);
        dec r; int ch, sr, e; long long tot; r.a = &B; r.mkv = 0;
        r.d = B.wav_open(wr, &ch, &sr, &tot, &e); r.ch = ch;
        if (r.d) {
            mr = malloc(sizeof(float) * KEEP); float buf[8192 * 8]; int g;
            while (nr < KEEP && (g = B.wav_read(r.d, buf, 8192)) > 0)
                for (int k = 0; k < g && nr < KEEP; k++) { float m = 0; for (int c = 0; c < ch; c++) m += buf[k * ch + c]; mr[nr++] = m / ch; }
            B.wav_close(r.d);
        }
    }

    for (int i = 3; i < argc; i++) {
        const wchar_t* path = argv[i];
        const wchar_t* ext = wcsrchr(path, L'.');
        int mkv = ext && (!_wcsicmp(ext, L".mkv") || !_wcsicmp(ext, L".webm"));
        const wchar_t* name = wcsrchr(path, L'\\'); name = name ? name + 1 : path;
        dec o, n; double ro = 0, rn = 0; float po = 0, pn = 0; long long fo = -1, fn = -1;
        int okO = dopen(&o, &A, path, mkv) == 0, okN = dopen(&n, &B, path, mkv) == 0;
        if (okO) fo = drain(&o, &ro, &po, ma, KEEP);
        if (okN) fn = drain(&n, &rn, &pn, mb, KEEP);
        wprintf(L"\n%ls\n", name);
        wprintf(L"  old: %-4ls %5d Hz %d ch total %10lld decoded %10lld  rms %7.2f dB peak %.3f  open %7.1f ms\n",
                okO ? L"ok" : L"FAIL", o.sr, o.ch, o.total, fo, ro, po, o.open_ms);
        wprintf(L"  new: %-4ls %5d Hz %d ch total %10lld decoded %10lld  rms %7.2f dB peak %.3f  open %7.1f ms\n",
                okN ? L"ok" : L"FAIL", n.sr, n.ch, n.total, fn, rn, pn, n.open_ms);
        int nch = n.ch;
        if (mr && nr > 0) {
            for (int w = 0; w < 2; w++) {
                if (w == 0 ? !okO : !okN) continue;
                const float* m = w == 0 ? ma : mb; long long nm = w == 0 ? fo : fn; if (nm > KEEP) nm = KEEP;
                int lag = 0; double lp = 0, snr = align_snr(mr, nr, m, nm, &lag, &lp);
                wprintf(L"  %ls vs source: lag %+d frames, SNR %.1f dB, low band %.1f dB\n", w == 0 ? L"old" : L"new", lag, snr, lp);
            }
        }
        dclose(&o); dclose(&n);
        if (!okN) { fails++; continue; }

        /* the new build must deliver exactly what it promised */
        long long lenDiff = fn - n.total;
        int lenOk = n.total <= 0 || llabs(lenDiff) == 0 || (mkv && llabs(lenDiff) <= 16);
        wprintf(L"  length: decoded - total = %lld %ls\n", lenDiff, !lenOk ? L"MISMATCH" : lenDiff ? L"ok (Matroska duration is approximate, as before)" : L"ok");
        if (!lenOk) fails++;

        if (okO && o.sr == n.sr && fo > 0) {
            int lag = 0; double lp = 0, snr = align_snr(ma, fo < KEEP ? fo : KEEP, mb, fn < KEEP ? fn : KEEP, &lag, &lp);
            wprintf(L"  vs old: lag %d frames, SNR %.1f dB (low band %.1f dB), rms delta %+.3f dB, length delta %lld\n", lag, snr, lp, rn - ro, fn - fo);
        }

        long long total = fn;
        float worst = 0;
        for (int k = 1; k <= 3; k++) {
            long long at = k == 1 ? total / 10 : k == 2 ? total / 2 : total * 9 / 10;
            float d = window_diff(&B, path, mkv, at, 8192);
            int exact = d >= 0 && d <= 1e-3f;
            int sbrEq = !exact && d >= 0 && g_lpdiff <= 2e-3f && fabsf(g_rmsdiff) < 0.3f;
            wprintf(L"  seek %2d%%: max|diff| %.6f  low band %.6f  level %+.2f dB  %ls\n", k == 1 ? 10 : k == 2 ? 50 : 90,
                    d, g_lpdiff, g_rmsdiff, exact ? L"exact" : sbrEq ? L"same below SBR band" : L"DIFFERENT");
            if (!exact && !sbrEq) worst = 99;
        }
        if (worst > 1e-3f) { wprintf(L"  SEEK MISMATCH\n"); fails++; }

        if (!mkv) {
            long long len; unsigned char* raw = slurp(path, &len);
            if (raw && len < 400LL * 1024 * 1024) {
                int ch, sr, e; long long tot;
                cmm_decoder* dm = B.aac_mem(raw, len, &ch, &sr, &tot, &e);
                dec f; dopen(&f, &B, path, 0);
                float a[8192 * 8], b[8192 * 8]; int ga, gb; long long same = 1, cnt = 0;
                while (dm && f.d && (ga = B.aac_read(dm, a, 8192)) > 0) {
                    gb = B.aac_read(f.d, b, 8192);
                    if (ga != gb || memcmp(a, b, sizeof(float) * ga * nch)) { same = 0; break; }
                    cnt += ga;
                }
                wprintf(L"  memory vs file: %ls (%lld frames)\n", same ? L"identical" : L"DIFFERENT", cnt);
                if (!same) fails++;
                if (dm) B.aac_close(dm);
                dclose(&f);
            }
            free(raw);
        }

        if (B.meminfo) {
            long long l0, l1;
            B.meminfo(&l0, 0, 0, 0, 0);
            for (int r = 0; r < 10; r++) {
                dec x; float tmp[4096 * 8];
                if (dopen(&x, &B, path, mkv) == 0) { dread(&x, tmp, 4096); dseek(&x, total / 3); dread(&x, tmp, 4096); }
                dclose(&x);
            }
            B.meminfo(&l1, 0, 0, 0, 0);
            wprintf(L"  native live heap across 10 open/seek/close: %+lld bytes %ls\n", l1 - l0, l1 == l0 ? L"" : L"LEAK?");
            if (l1 != l0) fails++;
        }
    }
    wprintf(L"\n%d problem(s)\n", fails);
    return fails ? 1 : 0;
}
