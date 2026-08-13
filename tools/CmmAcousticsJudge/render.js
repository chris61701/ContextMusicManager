// ───────────────────────────────────────────────────────────────────────────────
// OFFLINE RENDERER — hear the model before any of it touches the game
// ───────────────────────────────────────────────────────────────────────────────
// Every question that actually matters here is perceptual, and no metric substitutes for
// it. This walks a listener along a defined path through dump geometry, evaluates BOTH
// models per audio block, and writes a stereo WAV each. Same geometry, same path, same
// music -- so old versus new is a direct A/B, repeatable forever, with no game build and
// no rebuild loop.
//
// What each model contributes per block:
//   NEW — two arrivals, rendered independently:
//         AIR    positioned at the ENTRY PORTAL (so it comes from the doorway, not
//                through the wall), lowpassed gently by route length.
//         SOLID  positioned at the speaker, lowpassed hard by transmission dB -- which is
//                what makes a wall sound like a wall rather than a volume drop.
//         Summed. Never a min.
//   OLD — one arrival at the speaker, gain from the shipped power curve on dEff, and the
//         shipped brightness cue 1 - muffle*clamp((dEff-mn)/24).
//
// Gains, pan and filter cutoffs are slewed per block so nothing zippers; the arrival slots
// have stable identity (air/solid), so the filter state is continuous.
//
// Usage:  node render.js [--music=<path.wav>] [--seconds=N] [--only=name]
// Output: renders/<path>_<model>.wav
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump, makeEvaluator } = require("./judge3d.js");
const { buildGraph, makeNewEvaluator, SLOPE } = require("./portals.js");

const SR = 48000, BLOCK = 512, CELL = 4;
const STEREO_WIDTH = 0.8;      // matches WorldEmitterStereoWidth
const OLD_MUFFLE = 0.9;        // matches WorldEmitterMuffle (not carried in the dump config)
const OLD_MUFFLE_REF = 24;     // CMM_EMIT_MUFFLE_REF
const SLEW_MS = 40;            // parameter glide

// ── WAV io (PCM16 only; the test corpus is mono 16-bit 48k) ───────────────────
function readWav(file) {
  const b = fs.readFileSync(file);
  if (b.toString("ascii", 0, 4) !== "RIFF") throw new Error("not RIFF");
  let i = 12, fmt = null, data = null;
  while (i + 8 <= b.length) {
    const id = b.toString("ascii", i, i + 4), sz = b.readUInt32LE(i + 4);
    if (id === "fmt ") fmt = { af: b.readUInt16LE(i + 8), ch: b.readUInt16LE(i + 10),
                               rate: b.readUInt32LE(i + 12), bits: b.readUInt16LE(i + 22) };
    if (id === "data") { data = b.subarray(i + 8, i + 8 + sz); break; }
    i += 8 + sz + (sz & 1);
  }
  if (!fmt || !data) throw new Error("missing fmt/data");
  if (fmt.af !== 1 || fmt.bits !== 16) throw new Error(`need PCM16, got af=${fmt.af} bits=${fmt.bits}`);
  const frames = Math.floor(data.length / 2 / fmt.ch);
  const mono = new Float32Array(frames);
  for (let f = 0; f < frames; f++) {
    let s = 0;
    for (let c = 0; c < fmt.ch; c++) s += data.readInt16LE((f * fmt.ch + c) * 2) / 32768;
    mono[f] = s / fmt.ch;
  }
  return { mono, rate: fmt.rate };
}

function writeWav(file, L, R) {
  const n = L.length, bytes = n * 4;
  const b = Buffer.alloc(44 + bytes);
  b.write("RIFF", 0, "ascii"); b.writeUInt32LE(36 + bytes, 4); b.write("WAVE", 8, "ascii");
  b.write("fmt ", 12, "ascii"); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20);
  b.writeUInt16LE(2, 22); b.writeUInt32LE(SR, 24); b.writeUInt32LE(SR * 4, 28);
  b.writeUInt16LE(4, 32); b.writeUInt16LE(16, 34);
  b.write("data", 36, "ascii"); b.writeUInt32LE(bytes, 40);
  const clip = v => Math.max(-32768, Math.min(32767, Math.round(v * 32767)));
  for (let i = 0; i < n; i++) { b.writeInt16LE(clip(L[i]), 44 + i * 4); b.writeInt16LE(clip(R[i]), 46 + i * 4); }
  fs.writeFileSync(file, b);
}

// ── one-pole lowpass coefficient from a cutoff in Hz ─────────────────────────
const coefFor = fc => 1 - Math.exp(-2 * Math.PI * Math.max(60, Math.min(20000, fc)) / SR);
// Air travels through air: only distance dulls it, and gently.
const airFc = len => 20000 * Math.exp(-Math.max(0, len) / 140);
// Transmission is inherently lowpass -- highs do not survive mass. This is the cue that
// makes a wall sound like a wall instead of a fader.
const solidFc = matDb => 20000 * Math.pow(10, -Math.max(0, matDb) / 26);

// ── arrival extraction ────────────────────────────────────────────────────────
function arrivalsNew(ev, em, lx, ly, lz) {
  const r = ev(Math.round(lx), Math.round(ly), Math.round(lz));
  const out = [];
  // One arrival per kept entry portal, each at its OWN position -- so the sound comes from
  // the doorway it actually came through, and two doorways into a room both contribute.
  // Keying the slot by portal id keeps each one's filter and pan state continuous even as
  // the set changes; taking only the loudest made the image snap between portals.
  // EACH ARRIVAL'S WEIGHT MUST BE APPLIED. The model weights arrivals in the power domain
  // (pow += w * dbToPow(db)) -- the Fresnel weight is what stops an aperture collinear with the
  // direct path being counted twice. Rendering a.db at face value discarded it, so the moment a
  // door went fully open and its apertures appeared they were summed at FULL strength: the model
  // said -8.82 dB and the renderer produced -5.37, a 3.5 dB jump in one step. Author heard it as
  // the volume "seemingly doubling" at the end of the swing, and was right.
  for (const a of (r.airs || []))
    out.push({ id: `air${a.id}`, db: a.db, w: a.w === undefined ? 1 : a.w, at: [a.px, a.py, a.pz], fc: airFc(a.len) });
  if (!out.length) out.push({ id: "air", db: -120, w: 1, at: [em.x + 0.5, em.y + 0.5, em.z + 0.5], fc: airFc(r.d) });
  // SOLID — through the barrier, so it points at the speaker and is heavily dulled.
  out.push({ id: "solid", db: r.solidDb, w: 1, at: [em.x + 0.5, em.y + 0.5, em.z + 0.5], fc: solidFc(r.matDb) });
  return out;
}

function arrivalsOld(ev, em, cfg, lx, ly, lz) {
  const r = ev(Math.round(lx), Math.round(ly), Math.round(lz));
  const db = r.att > 0 ? 20 * Math.log10(r.att) : -120;
  let nd = (r.dEff - cfg.minDistance) / OLD_MUFFLE_REF;
  nd = Math.max(0, Math.min(1, nd));
  const bright = 1 - OLD_MUFFLE * nd;              // shipped cue: one number, saturates fast
  return [{ id: "direct", db, w: 1, at: [em.x + 0.5, em.y + 0.5, em.z + 0.5], fc: 200 + bright * bright * 19800 }];
}

// ── render one path ───────────────────────────────────────────────────────────
function render({ name, states, waypoints, seconds, music, model }) {
  const nSamp = Math.floor(seconds * SR);
  const L = new Float32Array(nSamp), R = new Float32Array(nSamp);
  const slots = new Map();   // stable id -> { g, pan, c, lp }; keyed so state follows an arrival
  const alpha = 1 - Math.exp(-(BLOCK / SR * 1000) / SLEW_MS);
  let slotSeq = 0;
  const MATCH_BLOCKS = 6;   // an arrival within this distance of a slot's last position is it

  const lerpWp = (t) => {
    if (t <= waypoints[0].t) return waypoints[0];
    for (let i = 1; i < waypoints.length; i++) {
      const a = waypoints[i - 1], b = waypoints[i];
      if (t <= b.t) {
        const u = (b.t - a.t) < 1e-6 ? 0 : (t - a.t) / (b.t - a.t);
        return { x: a.x + (b.x - a.x) * u, y: a.y + (b.y - a.y) * u, z: a.z + (b.z - a.z) * u,
                 yaw: a.yaw + (b.yaw - a.yaw) * u };
      }
    }
    return waypoints[waypoints.length - 1];
  };
  // Which geometry state is live at time t (door swing = a list of states over time).
  const stateAt = (t) => {
    let s = states[0];
    for (const st of states) if (t >= st.t) s = st;
    return s;
  };

  for (let base = 0; base < nSamp; base += BLOCK) {
    const n = Math.min(BLOCK, nSamp - base);
    const t = base / SR;
    const wp = lerpWp(t);
    const S = stateAt(t);
    const em = S.em;
    const arr = model === "new"
      ? arrivalsNew(S.evNew, em, wp.x, wp.y, wp.z)
      : arrivalsOld(S.evOld, em, S.cfg, wp.x, wp.y, wp.z);

    // listener basis from yaw (forward in xz, right perpendicular)
    const fwd = [Math.cos(wp.yaw), 0, Math.sin(wp.yaw)];
    const right = [-fwd[2], 0, fwd[0]];

    // MATCH ARRIVALS TO SLOTS BY POSITION, not by identity. Keying on the portal id looked
    // right but churned: the model's candidate set changes membership as the listener moves
    // even while its SUMMED level stays smooth, so slots kept being created and destroyed and
    // their independent slews left a transient -- a 2.8 dB step in the render at a moment the
    // model itself traced perfectly flat. An arrival is the same arrival if it comes from
    // roughly the same place, so match on that and the filter and pan state carry over.
    const free = new Set(slots.keys());
    for (const a of arr) {
      let bestKey = null, bestD = MATCH_BLOCKS;
      for (const k of free) {
        const s = slots.get(k);
        if (!s.at) continue;
        const dd = Math.hypot(s.at[0] - a.at[0], s.at[1] - a.at[1], s.at[2] - a.at[2]);
        if (dd < bestD) { bestD = dd; bestKey = k; }
      }
      if (bestKey === null) { bestKey = `s${slotSeq++}`; slots.set(bestKey, { g: 0, pan: 0, c: coefFor(20000), lp: 0 }); }
      free.delete(bestKey);
      a.key = bestKey;
    }
    // Slots nothing matched this block fade out rather than cutting.
    for (const k of free) {
      const s = slots.get(k);
      if (s.g < 1e-4) { slots.delete(k); continue; }
      arr.push({ key: k, db: -120, w: 1, at: s.at, fc: 20000 });
    }
    for (const a of arr) {
      const s = slots.get(a.key);
      s.at = a.at;
      const wAmp = Math.sqrt(a.w === undefined ? 1 : a.w);   // model weights power; gain is amplitude
      const gTarget = a.db <= -110 ? 0 : Math.pow(10, a.db / 20) * wAmp;
      let dx = a.at[0] - (wp.x + 0.5), dy = a.at[1] - (wp.y + 0.5), dz = a.at[2] - (wp.z + 0.5);
      const dl = Math.hypot(dx, dy, dz) || 1;
      const panTarget = Math.max(-1, Math.min(1, (dx * right[0] + dy * right[1] + dz * right[2]) / dl)) * STEREO_WIDTH;
      const cTarget = coefFor(a.fc);
      for (let i = 0; i < n; i++) {
        s.g += (gTarget - s.g) * alpha / BLOCK * 8;
        s.pan += (panTarget - s.pan) * alpha / BLOCK * 8;
        s.c += (cTarget - s.c) * alpha / BLOCK * 8;
        const x = music[(base + i) % music.length];
        s.lp += s.c * (x - s.lp);
        const th = (s.pan + 1) * 0.78539816;
        const v = s.lp * s.g;
        L[base + i] += Math.cos(th) * v;
        R[base + i] += Math.sin(th) * v;
      }
    }
  }
  // headroom normalise so old/new are comparable without clipping either
  let pk = 0;
  for (let i = 0; i < nSamp; i++) { pk = Math.max(pk, Math.abs(L[i]), Math.abs(R[i])); }
  return { L, R, peak: pk };
}

// ── scenarios ─────────────────────────────────────────────────────────────────
function loadState(file) {
  const D = loadDump(file);
  const em = D.emitters[0];
  const G = buildGraph(D, CELL);
  return { D, em, cfg: D.cfg, evNew: makeNewEvaluator(D, G, em), evOld: makeEvaluator(D, em) };
}

function main() {
  const args = process.argv.slice(2);
  const musicArg = (args.find(a => a.startsWith("--music=")) || "").split("=")[1];
  const onlyArg = (args.find(a => a.startsWith("--only=")) || "").split("=")[1];
  const secArg = parseFloat((args.find(a => a.startsWith("--seconds=")) || "=0").split("=")[1]) || 0;

  const music = musicArg || "C:/_Games/CMM_TestMusic/ambient/Ashen Verge.wav";
  const { mono, rate } = readWav(music);
  if (rate !== SR) console.warn(`warning: music is ${rate} Hz, renderer assumes ${SR}`);
  console.log(`music: ${path.basename(music)}  ${(mono.length / rate).toFixed(1)}s mono\n`);

  const S = f => path.join(__dirname, "synth", f);
  const st = (f, t = 0) => ({ t, ...loadState(S(f)) });

  const OPEN = "synth_wall-wood_door-air_open-1.json";
  const SHUT = "synth_wall-wood_door-wood_open-0.json";
  const T20  = "synth_wall-wood_door-wood_open-0.2.json";
  const T40  = "synth_wall-wood_door-wood_open-0.4.json";
  const T60  = "synth_wall-wood_door-wood_open-0.6.json";
  const T80  = "synth_wall-wood_door-wood_open-0.8.json";

  const SCENARIOS = [
    {
      name: "approach",
      note: "far corner of room B -> through the open doorway -> up to the speaker",
      seconds: 20,
      states: () => [st(OPEN)],
      waypoints: [ { t: 0, x: 40, y: 3, z: 20, yaw: Math.PI },
                   { t: 12, x: 22, y: 3, z: 11, yaw: Math.PI },
                   { t: 20, x: 10, y: 3, z: 11, yaw: Math.PI } ],
    },
    {
      name: "pastdoor",
      note: "strafe along the wall past the open doorway, FACING it (localisation)",
      seconds: 16,
      states: () => [st(OPEN)],
      // Facing must be ACROSS the walk, not along it. Looking down the direction of travel
      // put the doorway permanently on the listener's right, so the image only got more or
      // less right-biased -- an arc, never a sweep -- and equal-power panning carries no
      // front/back cue to make up for it. Facing the wall while strafing puts the doorway
      // left, then centre, then right, which is the thing this scenario exists to test.
      waypoints: [ { t: 0, x: 23, y: 3, z: 3, yaw: Math.PI },
                   { t: 16, x: 23, y: 3, z: 21, yaw: Math.PI } ],
    },
    {
      name: "swing",
      note: "stand still in room B on-axis; the door opens then shuts (gradual cue)",
      seconds: 18,
      states: () => [ st(SHUT, 0), st(T20, 3.0), st(T40, 3.6), st(T60, 4.2), st(T80, 4.8), st(OPEN, 5.4),
                      st(T80, 12.0), st(T60, 12.6), st(T40, 13.2), st(T20, 13.8), st(SHUT, 14.4) ],
      waypoints: [ { t: 0, x: 26, y: 3, z: 11, yaw: Math.PI },
                   { t: 18, x: 26, y: 3, z: 11, yaw: Math.PI } ],
    },
  ];

  const outDir = path.join(__dirname, "renders");
  fs.mkdirSync(outDir, { recursive: true });

  for (const sc of SCENARIOS) {
    if (onlyArg && sc.name !== onlyArg) continue;
    const states = sc.states();
    const seconds = secArg || sc.seconds;
    console.log(`${sc.name}  (${seconds}s) — ${sc.note}`);
    const res = {};
    for (const model of ["old", "new"]) {
      const r = render({ name: sc.name, states, waypoints: sc.waypoints, seconds, music: mono, model });
      res[model] = r;
    }
    // Common normalisation across both models so the A/B is level-fair.
    const pk = Math.max(res.old.peak, res.new.peak, 1e-6);
    const norm = 0.9 / pk;
    for (const model of ["old", "new"]) {
      const r = res[model];
      for (let i = 0; i < r.L.length; i++) { r.L[i] *= norm; r.R[i] *= norm; }
      const f = path.join(outDir, `${sc.name}_${model}.wav`);
      writeWav(f, r.L, r.R);
      console.log(`   ${model.padEnd(4)} peak ${(20 * Math.log10(r.peak + 1e-9)).toFixed(1)} dBFS -> renders/${path.basename(f)}`);
    }
    console.log();
  }
  console.log(`Both models share one normalisation factor per scenario, so level differences you`);
  console.log(`hear between old and new are real and not an artefact of separate normalising.\n`);
}
main();
