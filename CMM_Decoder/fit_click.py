"""fit_click.py -- fit a band-noise click SHAPE to a reference recording, by ear-free measurement.

The reference never ships and never enters the repository: only the numbers this prints do. A
shape is the mixer's own parameter set (cmm_mix_set_click_shape), so the fit renders through the
SHIPPED CMM_Decoder.dll -- the exact code the game runs -- measures the render exactly the way it
measured the reference, and corrects until they agree:
  per octave band: peak level, and ms from that peak to 20 dB down
  the low-band swell (how late the 60-500 Hz thump peaks)
  the impact cluster: how many, how far apart, how fast they fade
  the release: a second event's delay, level and brightness relative to the first

  python fit_click.py <dll> <out_dir> <kind>=<wav>[@split_ms][:main] ...
    kind      on | off | dial
    split_ms  where the release event starts (default: the first quiet gap found)
    :main     which event is the click: 1 (default) or 2 (fit the second, use the first as release)
e.g.
  python fit_click.py dist/CMM_Decoder.dll out on=ref/power.wav@170 off=ref/power.wav@170:2 dial=ref/dial.wav@105
Writes <out_dir>/fitted_sequence.wav (power on, eight dial detents, power off) and prints the three
shape strings for settings.xml."""
import sys, os, wave, ctypes
import numpy as np
from scipy import signal

R = 48000
EDGES = [(60, 250), (250, 500), (500, 1000), (1000, 2000), (2000, 4000), (4000, 8000), (8000, 20000)]
SOS = [signal.butter(4, [lo, min(hi, R / 2 - 200)], btype='band', fs=R, output='sos') for lo, hi in EDGES]
KIND = {'on': 0, 'off': 1, 'dial': 2}

def load(p):
    w = wave.open(p); n = w.getnframes(); ch = w.getnchannels(); sw = w.getsampwidth(); rate = w.getframerate()
    raw = w.readframes(n)
    x = (np.frombuffer(raw, dtype=np.int16).astype(float) / 32768 if sw == 2 else
         np.frombuffer(raw, dtype=np.int32).astype(float) / 2147483648)
    x = x.reshape(-1, ch).mean(axis=1)
    if rate != R: x = signal.resample_poly(x, R, rate)
    return x - x.mean()

def env1ms(y):
    return np.sqrt(np.maximum(signal.convolve(y * y, np.ones(48) / 48, mode='same'), 1e-24))

def onset(x):
    e = env1ms(x); return int(np.argmax(e > e.max() * 0.05))

def measure(x):
    """Per band: peak dB (re the loudest band peak), t20 ms after the peak, peak time ms after onset."""
    t0 = onset(x)
    envs = [env1ms(signal.sosfilt(s, x)) for s in SOS]
    top = max(e.max() for e in envs)
    lv, t20, tp = [], [], []
    for e in envs:
        i = int(np.argmax(e)); lv.append(20 * np.log10(e[i] / top))
        after = np.where(e[i:] < e[i] * 0.1)[0]
        t20.append(1000 * after[0] / R if len(after) else 1000 * (len(e) - i) / R)
        tp.append(1000 * (i - t0) / R)
    return np.array(lv), np.array(t20), np.array(tp), top

def impacts(x):
    t0 = onset(x)
    fast = np.sqrt(np.maximum(signal.convolve(x * x, np.ones(12) / 12, mode='same'), 1e-24))
    pk, _ = signal.find_peaks(fast, height=fast.max() * 10 ** (-28 / 20), distance=int(0.0022 * R))
    pk = [p for p in pk if p >= t0][:8]
    if len(pk) < 2: return 1, 3.0, 10.0
    lv = 20 * np.log10(fast[pk] / fast[pk].max())
    gaps = np.diff(pk) * 1000 / R
    k = np.arange(len(pk))
    fall = -np.polyfit(k, lv, 1)[0] if len(pk) > 2 else max(3.0, -(lv[1] - lv[0]))
    return len(pk), float(np.median(gaps)), float(np.clip(fall, 1, 30))

def split_events(x, split_ms):
    if split_ms is None:                                   # first gap of >= 40 ms below -35 dB
        e = env1ms(x); quiet = e < e.max() * 10 ** (-35 / 20); run = 0
        for i in range(onset(x), len(e)):
            run = run + 1 if quiet[i] else 0
            if run > int(0.04 * R):
                j = i
                while j < len(e) and quiet[j]: j += 1
                split_ms = 1000 * (j - int(0.002 * R)) / R; break
    if split_ms is None: return x, None, None
    s = int(split_ms * R / 1000)
    return x[:s], x[s:], split_ms

class Synth:
    def __init__(self, dll):
        self.d = ctypes.CDLL(os.path.abspath(dll))
        self.d.cmm_mix_set_click_shape.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_float), ctypes.c_int]
        self.d.cmm_mix_set_click_shape.restype = ctypes.c_int
        self.d.cmm_mix_render_click.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_float), ctypes.c_int]
        self.d.cmm_mix_set_click_tone_kind.argtypes = [ctypes.c_int] + [ctypes.c_float] * 7
        self.buf = (ctypes.c_float * R)()
    def vary(self, v):
        for k in range(3): self.d.cmm_mix_set_click_tone_kind(k, 8000, .7, 40, .5, 2000, .5, v)
    def set(self, kind, p):
        arr = (ctypes.c_float * len(p))(*p); self.d.cmm_mix_set_click_shape(kind, arr, len(p))
    def render(self, kind):
        n = self.d.cmm_mix_render_click(kind + 2, R, self.buf, R)
        return np.frombuffer(self.buf, dtype=np.float32, count=n).astype(float).copy()

def band_envs_db(x, t0, span):
    """Each band's 1 ms RMS envelope in dB re the loudest band moment, from t0 for `span` samples."""
    envs = [env1ms(signal.sosfilt(s, x)) for s in SOS]
    top = max(e.max() for e in envs)
    out = []
    for e in envs:
        seg = e[t0:t0 + span]
        if len(seg) < span: seg = np.concatenate([seg, np.full(span - len(seg), 1e-12)])
        out.append(20 * np.log10(seg / top + 1e-12))
    return np.array(out), top

def model_env(span, hits, t20, swell_ms):
    """The synth's envelope for one band (vary 0): impulses at the hits, a decay to -20 dB in t20
    ms, slewed by the swell, then the same 1 ms RMS the measurement uses. Linear amplitude."""
    imp = np.zeros(span)
    for t_ms, a in hits:
        i = int(t_ms * R / 1000)
        if i < span: imp[i] += a
    K = 10 ** (-1 / (t20 * R / 1000))
    env = signal.lfilter([1.0], [1.0, -K], imp)
    if swell_ms > 0.05:
        S = 1 - np.exp(-1 / (swell_ms * R / 1000))
        env = signal.lfilter([S], [1.0, -(1 - S)], env)
    return np.sqrt(np.maximum(signal.convolve(env * env, np.ones(48) / 48, mode='same'), 1e-24))

FLOOR_DB = 35.0
def fit_band(ref_db, hits, swell_ms, span):
    """Best (level dB, t20 ms) for one band against its measured envelope curve. Both curves are
    compared above a floor FLOOR_DB under the band's peak: a real recording never falls below its
    noise floor and room, so without the floor the troughs a fast decay digs between impacts
    dominate the error and the fit always prefers a long ring -- which is how the first two fits
    came out ringing."""
    best = (1e18, 0.0, 10.0)
    fl = ref_db.max() - FLOOR_DB
    rc = np.maximum(ref_db, fl)
    for t20 in np.geomspace(0.5, 120, 48):
        m = 20 * np.log10(model_env(span, hits, t20, swell_ms) + 1e-12)
        off0 = ref_db.max() - m.max()
        for off in off0 + np.arange(-8, 8.01, 0.5):
            err = np.mean((rc - np.maximum(m + off, fl)) ** 2)
            if err < best[0]: best = (err, off, t20)
    return best

def fit(syn, kind, ref, split_ms, main):
    e1, e2, split_ms = split_events(ref, split_ms)
    if main == 2 and e2 is not None: e1, e2 = e2, e1
    t0 = onset(e1)
    span = min(len(e1) - t0, int(0.08 * R))
    ref_db, top1 = band_envs_db(e1, t0, span)

    # The impact cluster, from the fast envelope; the fade is taken from the first few landings
    # (the tail of a rattle is quieter and irregular, and is what the per-fire jitter supplies).
    fast = np.sqrt(np.maximum(signal.convolve(e1 * e1, np.ones(12) / 12, mode='same'), 1e-24))
    pk, _ = signal.find_peaks(fast, height=fast.max() * 10 ** (-28 / 20), distance=int(0.0022 * R))
    pk = [q for q in pk if q >= t0][:8] or [t0]
    lvl = 20 * np.log10(fast[pk] / fast[pk].max())         # relative to the LOUDEST landing
    lvl = np.clip(lvl, -60, 12)
    n = len(pk)
    htime = (np.array(pk) - pk[0]) * 1000 / R
    hits = [(htime[k], 10 ** (lvl[k] / 20)) for k in range(n)]

    best_total, best = 1e18, None
    for swell in (0, 1, 2, 4, 6, 9, 13, 18, 25):
        res = []
        for b in range(7):
            sw = swell if b <= 1 else (swell / 2 if b == 2 else 0)
            res.append(fit_band(ref_db[b], hits, sw, span))
        total = sum(r[0] for r in res[:3])
        if total < best_total: best_total, best = total, (swell, res)
    swell, res = best
    p = np.zeros(36)
    p[0:7] = [r[1] for r in res]; p[0:7] -= p[0:7].max()
    p[7:14] = [r[2] for r in res]
    p[14], p[15], p[19] = swell, 2.0, n
    p[20:20 + n] = htime; p[28:28 + n] = lvl
    if e2 is not None:
        lv1 = measure(e1)[0]; lv2, _, _, top2 = measure(e2)
        p[17] = 20 * np.log10(top2 / top1)
        p[18] = float(np.clip(np.polyfit(np.arange(7) - 3, lv2 - lv1, 1)[0], -6, 6))
        p[16] = split_ms + (onset(e2) - onset(e1)) * 1000 / R if main == 1 else split_ms

    # Verify through the DLL and correct the LEVELS only (well conditioned now the bands are steep).
    syn.vary(0.0)
    for it in range(4):
        syn.set(kind, p)
        curves = []
        for r in range(8):
            y = syn.render(kind)
            cut = int((p[16] - 5) * R / 1000) if p[16] > 0.5 else len(y)
            yy = y[:cut]; curves.append(band_envs_db(yy, onset(yy), span)[0])
        got = 10 * np.log10(np.mean([10 ** (c / 10) for c in curves], axis=0) + 1e-24)
        pk_ref, pk_got = ref_db.max(axis=1), got.max(axis=1)
        p[0:7] += 0.8 * (pk_ref - pk_got); p[0:7] -= p[0:7].max()
    fl = ref_db.max(axis=1, keepdims=True) - FLOOR_DB
    curve_err = np.sqrt(np.mean((np.maximum(ref_db, fl) - np.maximum(got, fl)) ** 2))
    times = [1, 2, 4, 6, 10, 15, 20, 30, 45]
    print(f'\n{kind}: band level dB at ms after onset (reference / fitted render)')
    print('  band        ' + ''.join(f'{t:>9}' for t in times))
    for b, (lo, hi) in enumerate(EDGES):
        print(f'  {lo/1000:>5.2f}-{hi/1000:<5.1f}k' + ''.join(
            f'{ref_db[b][int(t*R/1000)]:5.0f}/{got[b][int(t*R/1000)]:<3.0f}' for t in times))
    print('  impacts ' + ' '.join(f'{htime[k]:.1f}ms/{lvl[k]:+.0f}' for k in range(n)) +
          f'; swell {swell:.0f} ms; release {p[16]:.0f} ms at {p[17]:+.1f} dB, tilt {p[18]:+.1f} dB/band')
    print(f'  envelope error (each band above its peak -{FLOOR_DB:.0f} dB): {curve_err:.1f} dB RMS')
    return p

def shape_string(p):
    n = int(round(p[19]))
    return ('levels ' + ' '.join(f'{v:.1f}' for v in p[0:7]) +
            ' | decay ' + ' '.join(f'{v:.1f}' for v in p[7:14]) +
            f' | swell {p[14]:.1f} | edge {p[15]:.1f}' +
            ' | hits ' + ' '.join(f'{p[20 + k]:.1f}:{p[28 + k]:.0f}' for k in range(n)) +
            (f' | release {p[16]:.0f} {p[17]:.1f} {p[18]:.1f}' if p[16] > 0.5 else ''))

def write_sequence(syn, path, vary):
    syn.vary(vary)
    seq = np.zeros(R * 4); at = R // 5
    for kind, gap in [(0, 700)] + [(2, 160)] * 7 + [(2, 900), (1, 0)]:
        y = syn.render(kind); seq[at:at + len(y)] += y[:len(seq) - at]; at += gap * R // 1000
    seq = seq[:at + R // 2]
    pk = np.abs(seq).max()
    with wave.open(path, 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(R)
        w.writeframes((np.clip(seq, -1, 1) * 32767).astype(np.int16).tobytes())
    return 20 * np.log10(pk + 1e-12)

def main():
    dll, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    syn = Synth(dll)
    shapes = {}
    for arg in sys.argv[3:]:
        kind, spec = arg.split('=', 1)
        main_ev = 2 if spec.endswith(':2') else 1
        spec = spec.rsplit(':', 1)[0] if spec[-2:] in (':1', ':2') else spec
        wav, split = (spec.rsplit('@', 1) + [None])[:2] if '@' in spec else (spec, None)
        shapes[kind] = fit(syn, KIND[kind], load(wav), float(split) if split else None, main_ev)
    for kind, p in shapes.items(): syn.set(KIND[kind], p)
    pk = write_sequence(syn, os.path.join(out, 'fitted_sequence.wav'), 0.5)
    print(f'\nwrote {os.path.join(out, "fitted_sequence.wav")} (peak {pk:+.1f} dBFS)\n')
    for kind, p in shapes.items():
        print(f'RadioClick{kind.capitalize()}Shape = {shape_string(p)}')

if __name__ == '__main__':
    main()
