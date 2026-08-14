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
// ── TERM 5: PER-ARRIVAL DELAY ────────────────────────────────────────────────
// A cave echo is the same sound arriving REPEATEDLY, separated in time. One shared reverb whose
// only time parameter is a decay cannot produce that at any setting -- an exponential tail is a
// wash by construction, which is exactly what the author described as "faint reverb that
// eventually died". Every arrival already carries its route length, so it already has an arrival
// time; this was simply never wired up. At roughly a block per metre a 60-block cave route lands
// ~175 ms behind the direct sound, a plainly audible discrete echo.
const SPEED_BLOCKS_PER_SEC = 343;
// How fast a delay may move. A sprinting player changes a path length by a few blocks per second;
// anything faster is the candidate set rearranging rather than motion, and a delay line whose read
// pointer moves is a PITCH SHIFT, so an unbounded jump is heard as a bend. Generous enough for a
// vehicle, tight enough that a route swap glides instead of warping.
const DELAY_SLEW_BLOCKS_PER_SEC = +(process.env.CMM_DSLEW || 8);
// Switches so each renderer change can be isolated instead of argued about:
//   CMM_NODECOR=1   no per-slot diffusers (arrivals sum coherently, as before)
//   CMM_NOSPREAD=1  no area-source pan softening (transmitted sound localises as a point)
//   CMM_ATSPK=1     put transmitted sound back at the SPEAKER instead of at the barrier
//   CMM_DSLEW=n     delay slew limit in blocks/s (large = effectively unlimited)
// Measured WORSE and therefore off by default: approach went 3.0 -> 7.9 dB/s with the diffusers in.
// The reasoning behind them still looks sound (an arrival stands for a bundle of paths, which add
// incoherently, which is why the model sums power) but the measurement disagrees, and the
// measurement wins. Kept behind CMM_DECOR=1 so it can be revisited with evidence rather than redone
// from scratch. The real cause of the flanging turned out to be the merged transmitted arrival
// jumping position, which is fixed properly above.
const NO_DECOR = process.env.CMM_DECOR !== "1";
const NO_SPREAD = process.env.CMM_NOSPREAD === "1";
const AT_SPEAKER = process.env.CMM_ATSPK === "1";
const MAX_DELAY = Math.ceil(SPEED_BLOCKS_PER_SEC ? (200 / SPEED_BLOCKS_PER_SEC) * SR : 1) + 4;

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
// ── AND THE FILTER MUST NOT ALSO BE A FADER ───────────────────────────────────
// A one-pole lowpass throws away energy: its power gain on a flat spectrum is c/(2-c), which at
// fc = 200 Hz is -19 dB. But the model's level ALREADY contains the barrier's attenuation -- that
// is what solidDb is. So filtering on top of it charged for the wall twice, and transmitted sound
// has been rendering up to 19 dB too quiet, with the error varying as the cutoff moved. MODEL.md's
// line is "a wall is a filter, not a fader"; without this it was a filter AND a fader.
//
// Compensating restores unity power so the filter only SHAPES. The white-noise expression is used
// rather than a per-source-spectrum one because the correction only has to be smooth and close --
// what matters is that heavy filtering stops silently costing level.
const lpMakeup = c => Math.sqrt((2 - c) / Math.max(1e-6, c));
// Air travels through air: only distance dulls it, and gently.
const airFc = len => 20000 * Math.exp(-Math.max(0, len) / 140);
// Transmission is inherently lowpass -- highs do not survive mass. This is the cue that
// makes a wall sound like a wall instead of a fader.
const solidFc = matDb => 20000 * Math.pow(10, -Math.max(0, matDb) / 26);
// The diffuse field has been absorbed off many surfaces before it reaches anyone, so it is dull,
// and it has travelled further than the direct sound by construction. Both are stand-in constants
// until the multi-tap tail exists.
// The transmitting patch of a barrier is about the size of the Fresnel disc the model integrates
// over, so that is how big the radiator is for localisation purposes.
const BARRIER_RADIUS_BLOCKS = 3.0;
const DIFFUSE_FC = 3500;
const DIFFUSE_EXTRA_BLOCKS = 9;

// ── DECORRELATION: an arrival is a BUNDLE of paths, not one ray ───────────────
// Rendering arrivals as perfectly coherent copies of the same music makes them comb-filter, and
// with only three to five of them the comb is coarse and sweeps as the listener moves. The author
// heard it exactly: "pastdoor sounds fine if it is me passing several doors one after the other."
// One doorway, several apparent doorways -- that is a sweeping notch, not geometry.
//
// It is also inconsistent with the model, which sums arrivals in POWER. A power sum is the
// incoherent average, and incoherent is the physically right answer here: each modelled arrival
// stands for a whole family of real paths that scattered off different surfaces, and those add
// with random relative phase. Two coherent rays cancel; two bundles do not.
//
// So each slot gets its own pair of Schroeder allpasses. They are magnitude-preserving -- no level
// is changed, and no gate number moves -- but they scramble phase differently per slot, so the
// arrivals stop cancelling. Delay, and therefore the echo cue, is untouched.
const AP1 = [113, 151, 199, 241, 293, 347, 401, 457];
const AP2 = [61, 79, 97, 107, 131, 149, 167, 181];
const AP_G = 0.7;
let slotCtr = 0;
function newSlot() {
  const k = slotCtr++;
  return { g: 0, pan: 0, c: coefFor(20000), lp: 0,
           dl: new Float32Array(MAX_DELAY), dw: 0, dsm: 0, dTgt: 0, fresh: true,
           b1: new Float32Array(AP1[k % AP1.length]), i1: 0,
           b2: new Float32Array(AP2[k % AP2.length]), i2: 0 };
}
// one Schroeder allpass step, in place
function apStep(buf, idx, x) {
  const bv = buf[idx];
  const v = x + AP_G * bv;
  buf[idx] = v;
  return bv - AP_G * v;
}

// ── arrival extraction ────────────────────────────────────────────────────────
function arrivalsNew(ev, em, lx, ly, lz) {
  // DO NOT ROUND. The evaluator takes float positions, and rounding turned a field that is smooth
  // in position into a staircase with a step at every voxel boundary -- the whole inter-voxel change
  // landing inside one 0.25 s window. Measured on the pastdoor walk: the model varies 1.2 dB in
  // total along that path (worst 0.39 dB/s) and the render showed 3.3 dB/s, eight times as rough.
  // The author has been judging this model by ear off these renders, so the roughness they were
  // hearing was partly the renderer's own.
  const r = ev(lx, ly, lz);
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
    out.push({ id: `air${a.id}`, db: a.db, w: a.w === undefined ? 1 : a.w, at: [a.px, a.py, a.pz], fc: airFc(a.len), len: a.len });
  if (!out.length) out.push({ id: "air", db: -120, w: 1, at: [em.x + 0.5, em.y + 0.5, em.z + 0.5], fc: airFc(r.d) });
  // ── THE TRANSMITTED PATH, SPLIT WHERE IT PHYSICALLY SPLITS ───────────────────
  // The model reports how much of this path came through AIR (openFrac) versus through MATERIAL.
  // Those are different sounds from different places and they must be rendered as such:
  //
  //   DIRECT   the clear share -- comes from the SPEAKER, dulled only by distance.
  //   BARRIER  the blocked share -- comes from THE BARRIER ITSELF, dulled hard by mass. This is
  //            the governing principle of the whole model: whatever transmits becomes a source at
  //            its own location. Rendering it at the speaker was flatly wrong, and through a floor
  //            it was audibly wrong -- a point source sweeping left to right underfoot where a
  //            whole ceiling should have been radiating down with barely any direction to it.
  //
  // Merged into one arrival, the renderer had to choose one position and one filter for both, so
  // when a sight line cleared the image JUMPED from barrier to speaker and the tone swung dull to
  // bright. One doorway then sounded like "several doors one after the other". openFrac is
  // continuous, so this crossfade is too: fully clear silences the barrier component, fully blocked
  // silences the direct one, and nothing switches at any point in between.
  //
  // A barrier is also an AREA radiator about the size of the Fresnel disc, so it cannot be
  // localised sharply from close up -- its angular size swamps the direction cue. `spread` pulls its
  // pan toward centre as it gets near: one block away essentially omnidirectional, thirty blocks
  // away nearly a point.
  const of = r.openFrac === undefined ? 1 : r.openFrac;
  if (of > 1e-4)
    out.push({ id: "direct", fixedKey: "direct", db: r.solidDb + 10 * Math.log10(of), w: 1,
               at: [em.x + 0.5, em.y + 0.5, em.z + 0.5], fc: airFc(r.d), len: r.d });
  if (of < 1 - 1e-4) {
    const bm = AT_SPEAKER ? null : r.bm;
    const sAt = bm ? [bm.x, bm.y, bm.z] : [em.x + 0.5, em.y + 0.5, em.z + 0.5];
    const bDist = Math.hypot(sAt[0] - lx, sAt[1] - ly, sAt[2] - lz);
    out.push({ id: "barrier", fixedKey: "barrier", db: r.solidDb + 10 * Math.log10(1 - of), w: 1, at: sAt,
               fc: solidFc(r.matDb), len: r.d,
               spread: NO_SPREAD ? 1 : bDist / (bDist + 2 * BARRIER_RADIUS_BLOCKS) });
  }
  // ── THE DIFFUSE FIELD, which was not being rendered at all ───────────────────
  // airs excludes isSrc AND isDiffuse, and only the isSrc stand-in above was being added back. So
  // every render so far has been the DRY arrivals with nothing underneath them -- which MODEL.md
  // explicitly warns is the wrong thing to listen to: "the position-dependent part is a small
  // perturbation riding on a much larger constant, so an indoor trajectory should be nearly flat.
  // Rendering dry arrivals with nothing underneath them exposes every dB of their variation."
  // The term written to flatten these walks was in the model and missing from the audio.
  //
  // Omnidirectional by nature, so it gets a fixed slot and centre pan rather than being matched by
  // position -- it has no position to match. The single delayed, dulled copy is a STAND-IN for a
  // real tail: it carries the right level and the right "arrives later and duller" cue, but a
  // proper diffuse renderer needs a decaying multi-tap tail, which belongs with the echo work.
  if (r.diffDb !== undefined && r.diffDb > -110)
    out.push({ id: "diffuse", fixedKey: "diffuse", db: r.diffDb, w: 1, omni: true,
               at: [lx, ly, lz], fc: DIFFUSE_FC, len: r.d + DIFFUSE_EXTRA_BLOCKS });
  return out;
}

function arrivalsOld(ev, em, cfg, lx, ly, lz) {
  const r = ev(Math.round(lx), Math.round(ly), Math.round(lz));
  const db = r.att > 0 ? 20 * Math.log10(r.att) : -120;
  let nd = (r.dEff - cfg.minDistance) / OLD_MUFFLE_REF;
  nd = Math.max(0, Math.min(1, nd));
  const bright = 1 - OLD_MUFFLE * nd;              // shipped cue: one number, saturates fast
  return [{ id: "direct", db, w: 1, at: [em.x + 0.5, em.y + 0.5, em.z + 0.5], fc: 200 + bright * bright * 19800, len: r.d }];
}

// ── render one path ───────────────────────────────────────────────────────────
function render({ name, states, waypoints, seconds, music, model }) {
  // Only the new model has multiple arrivals to interfere with each other; the old one renders a
  // single arrival, so there is nothing for a diffuser to decorrelate and leaving it untouched keeps
  // the A/B honest about the MODEL rather than about the rendering.
  const decorrelate = model === "new" && !NO_DECOR;
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
      // Position matching exists because the APERTURE set churns -- candidates come and go as the
      // listener moves even while the summed level stays smooth, so keying on identity created and
      // destroyed slots and left transients. That reasoning does not apply to arrivals whose
      // identity is permanent: direct, barrier and diffuse are always the same three roles. Worse,
      // position matching actively broke them, because direct (at the speaker) and barrier (at the
      // barrier) can fall inside the 6-block match radius and steal each other's slots frame to
      // frame -- which cost 5 dB/s on the swing until they were given fixed slots.
      if (a.fixedKey) {
        if (!slots.has(a.fixedKey)) slots.set(a.fixedKey, newSlot());
        free.delete(a.fixedKey); a.key = a.fixedKey; continue;
      }
      let bestKey = null, bestD = MATCH_BLOCKS;
      for (const k of free) {
        const s = slots.get(k);
        if (!s.at) continue;
        const dd = Math.hypot(s.at[0] - a.at[0], s.at[1] - a.at[1], s.at[2] - a.at[2]);
        if (dd < bestD) { bestD = dd; bestKey = k; }
      }
      if (bestKey === null) { bestKey = `s${slotSeq++}`;
        slots.set(bestKey, newSlot()); }
      free.delete(bestKey);
      a.key = bestKey;
    }
    // Slots nothing matched this block fade out rather than cutting.
    for (const k of free) {
      const s = slots.get(k);
      if (s.g < 1e-4) { slots.delete(k); continue; }
      // Keep the slot's own length while it fades. Handing it len 0 drove its delay target to zero,
      // and a delay line whose read pointer travels is a pitch shift -- so every disappearing
      // arrival bent in pitch on its way out.
      arr.push({ key: k, db: -120, w: 1, at: s.at, fc: 20000, len: s.len === undefined ? 0 : s.len });
    }
    for (const a of arr) {
      const s = slots.get(a.key);
      s.at = a.at;
      if (a.len !== undefined) s.len = a.len;
      if (!s.dl) { s.dl = new Float32Array(MAX_DELAY); s.dw = 0; s.dsm = 0; }
      const wAmp = Math.sqrt(a.w === undefined ? 1 : a.w);   // model weights power; gain is amplitude
      const gTarget = a.db <= -110 ? 0 : Math.pow(10, a.db / 20) * wAmp;
      let dx = a.at[0] - (wp.x + 0.5), dy = a.at[1] - (wp.y + 0.5), dz = a.at[2] - (wp.z + 0.5);
      const dl = Math.hypot(dx, dy, dz) || 1;
      const panTarget = a.omni ? 0
        : Math.max(-1, Math.min(1, (dx * right[0] + dy * right[1] + dz * right[2]) / dl))
          * STEREO_WIDTH * (a.spread === undefined ? 1 : a.spread);
      const cTarget = coefFor(a.fc);
      // delay in samples from this arrival's own path length, slewed so a route change glides
      const dTarget = Math.min(MAX_DELAY - 2, Math.max(0, (a.len === undefined ? 0 : a.len) / SPEED_BLOCKS_PER_SEC * SR));
      // A FRESH SLOT STARTS AT ITS TRUE DELAY. Gliding a delay line's read pointer is a pitch
      // shift, so slewing from 0 up to the real delay made every newly appearing arrival warp in
      // pitch -- which is exactly what the author heard walking over an OPEN hatch, where the
      // aperture arrival comes into existence mid-walk. A brand new arrival has no history to glide
      // from, so it should simply begin where it belongs.
      if (s.fresh) { s.dsm = dTarget; s.fresh = false; }
      // And bound how fast an established delay may move. The listener walks ~1.5 blocks/s, so a
      // physical path length cannot change faster than a few blocks per second; anything quicker is
      // the candidate set rearranging, not motion, and it is heard as a pitch bend rather than as
      // Doppler. Clamped per block.
      {
        const maxStep = DELAY_SLEW_BLOCKS_PER_SEC / SPEED_BLOCKS_PER_SEC * SR * (n / SR);
        const dd = dTarget - s.dsm;
        if (Math.abs(dd) > maxStep) s.dTgt = s.dsm + Math.sign(dd) * maxStep; else s.dTgt = dTarget;
      }
      for (let i = 0; i < n; i++) {
        s.g += (gTarget - s.g) * alpha / BLOCK * 8;
        s.pan += (panTarget - s.pan) * alpha / BLOCK * 8;
        s.c += (cTarget - s.c) * alpha / BLOCK * 8;
        s.dsm += (s.dTgt - s.dsm) * alpha / BLOCK * 2;
        const xin = music[(base + i) % music.length];
        // circular delay line, linearly interpolated read
        s.dl[s.dw] = xin;
        const rp = s.dw - s.dsm;
        const r0 = ((Math.floor(rp) % MAX_DELAY) + MAX_DELAY) % MAX_DELAY;
        const r1 = (r0 + 1) % MAX_DELAY;
        const fr = rp - Math.floor(rp);
        const x = s.dl[r0] * (1 - fr) + s.dl[r1] * fr;
        s.dw = (s.dw + 1) % MAX_DELAY;
        s.lp += s.c * (x - s.lp);
        const th = (s.pan + 1) * 0.78539816;
        let vv = s.lp * lpMakeup(s.c);
        if (decorrelate) {
          vv = apStep(s.b1, s.i1, vv); s.i1 = (s.i1 + 1) % s.b1.length;
          vv = apStep(s.b2, s.i2, vv); s.i2 = (s.i2 + 1) % s.b2.length;
        }
        const v = vv * s.g;
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

// ── READ A LURCH INSTEAD OF LISTENING FOR ONE ─────────────────────────────────
// The author's own preference, and a good one: "I don't have to listen to it, unless I'm reading
// it." A sudden level change is exactly what the ear objects to, so print the short-term loudness
// as a sparkline plus the worst rate of change in dB per second. A walk is ~1.5 blocks/s, so the
// 3 dB/block step limit is about 4.5 dB/s -- but a door SWING is allowed to be faster than a walk,
// which is why this is reported rather than gated.
const BLOCKS = " ▁▂▃▄▅▆▇█";
const SETTLE_S = 0.75;   // ignore the render's own start-up ramp when reporting worst dB/s
// A remaining gap between this number and smooth.js is EXPECTED and is not a defect. The model sums
// arrivals in POWER, which is the incoherent statistical average; the renderer sums PRESSURE through
// real delay lines, so arrivals comb-filter and the broadband RMS legitimately wobbles a few dB as
// relative delays sweep. Two coherent arrivals sum anywhere from cancellation to +6 dB. So expect
// the render to read rougher than the field by a few dB/s, and judge the FIELD by smooth.js and
// real.js, the SOUND by these files. Do not chase the difference; it is two different questions.
function envelope(L, R, sr, win = 0.25) {
  const n = Math.floor(L.length / (sr * win));
  const db = [];
  for (let i = 0; i < n; i++) {
    let s = 0;
    const a = Math.floor(i * sr * win), b = Math.floor((i + 1) * sr * win);
    for (let j = a; j < b; j++) s += L[j] * L[j] + R[j] * R[j];
    db.push(10 * Math.log10(s / Math.max(1, b - a) + 1e-12));
  }
  // Skip the settling period. Every slot starts at zero gain and slews up, so the first windows
  // ramp from silence and reported 13.7 dB/s at 0.3 s on every scenario -- the renderer starting,
  // not the field moving.
  let worst = 0, at = 0;
  const first = Math.max(1, Math.ceil(SETTLE_S / win));
  for (let i = first; i < db.length; i++) {
    const g = Math.abs(db[i] - db[i - 1]) / win;
    if (g > worst) { worst = g; at = i * win; }
  }
  // scale the sparkline over this render's own 36 dB window, so shape is visible at any level
  const hi = Math.max(...db), lo = hi - 36;
  const spark = db.map(v => BLOCKS[Math.max(0, Math.min(8, Math.round(8 * (v - lo) / 36)))]).join("");
  return { worst, at, spark, db };
}

function main() {
  const args = process.argv.slice(2);
  const musicArg = (args.find(a => a.startsWith("--music=")) || "").split("=")[1];
  const onlyArg = (args.find(a => a.startsWith("--only=")) || "").split("=")[1];
  const secArg = parseFloat((args.find(a => a.startsWith("--seconds=")) || "=0").split("=")[1]) || 0;

  // ── --noise: make the envelope trace an honest field probe ───────────────────
  // With music as the source, the printed envelope measures the music's own dynamics ADDED to the
  // field's, and the two are not separable by eye. That mattered immediately: the envelope claimed
  // 36 dB/s on the over-hatch walk while smooth.js measured 1.08 dB/block on the very same route,
  // and two measurements disagreeing must be resolved before either is acted on (method rule 2).
  //
  // Steady pink-ish noise has no dynamics of its own, so every dB the envelope shows is the model.
  // Use --noise to read the field and music to judge how it sounds.
  let mono, rate = SR, srcName;
  if (args.includes("--noise")) {
    mono = new Float32Array(SR * 30);
    let b0 = 0, b1 = 0, b2 = 0, seed = 12345;
    const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x40000000 - 1; };
    for (let i = 0; i < mono.length; i++) {
      const w = rnd();
      b0 = 0.99765 * b0 + w * 0.0990460;      // Paul Kellett's pink filter, 3 poles
      b1 = 0.96300 * b1 + w * 0.2965164;
      b2 = 0.57000 * b2 + w * 1.0526913;
      mono[i] = (b0 + b1 + b2 + w * 0.1848) * 0.20;
    }
    srcName = "steady pink noise (field probe: every dB shown is the model)";
  } else {
    const music = musicArg || "C:/_Games/CMM_TestMusic/ambient/Ashen Verge.wav";
    const w = readWav(music);
    mono = w.mono; rate = w.rate;
    if (rate !== SR) console.warn(`warning: music is ${rate} Hz, renderer assumes ${SR}`);
    srcName = path.basename(music);
  }
  console.log(`source: ${srcName}  ${(mono.length / rate).toFixed(1)}s mono\n`);

  const S = f => path.join(__dirname, "synth", f);
  const st = (f, t = 0) => ({ t, ...loadState(S(f)) });

  const OPEN = "synth_wall-wood_door-air_open-1.json";
  const SHUT = "synth_wall-wood_door-wood_open-0.json";
  // Every 5% state the synth generates, so the swing is driven by the animation rather than by how
  // coarsely the geometry happened to be sampled.
  const SWING = n => `synth_wall-wood_door-wood_open-${n}.json`;
  const SWING_STEPS = Array.from({ length: 19 }, (_, i) => +((i + 1) * 0.05).toFixed(2));

  const FOPEN = "synth_floor-wood_hatch-air_open-1.json";
  const FSHUT = "synth_floor-wood_hatch-wood_open-0.json";

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
      // open over 2.4 s from t=3, hold, then shut over 2.4 s from t=12 -- same envelope as before,
      // now with 20 steps in each direction instead of 5.
      states: () => {
        const out = [st(SHUT, 0)];
        SWING_STEPS.forEach((o, i) => out.push(st(SWING(o), 3.0 + 2.4 * (i + 1) / 20)));
        out.push(st(OPEN, 3.0 + 2.4));
        SWING_STEPS.slice().reverse().forEach((o, i) => out.push(st(SWING(o), 12.0 + 2.4 * (i + 1) / 20)));
        out.push(st(SHUT, 12.0 + 2.4));
        return out;
      },
      waypoints: [ { t: 0, x: 26, y: 3, z: 11, yaw: Math.PI },
                   { t: 18, x: 26, y: 3, z: 11, yaw: Math.PI } ],
    },
    {
      // Rule D made audible. A voxel used as a floor now costs 2.6x what the same material costs
      // as a wall, because it stands for a whole floor assembly rather than a partition. Walking
      // the storey ABOVE a speaker should therefore be clearly muffled with the hatch shut, and
      // should open up as you pass over it when the hatch is open -- the vertical twin of pastdoor.
      name: "overhatch",
      note: "walk the storey ABOVE the speaker, across an OPEN hatch, facing across the walk",
      seconds: 16,
      states: () => [st(FOPEN)],
      waypoints: [ { t: 0, x: 9, y: 8, z: 3, yaw: Math.PI },
                   { t: 16, x: 9, y: 8, z: 20, yaw: Math.PI } ],
    },
    {
      name: "overhatch-shut",
      note: "the same walk with the hatch SHUT: quiet, dull and nearly featureless",
      seconds: 16,
      states: () => [st(FSHUT)],
      waypoints: [ { t: 0, x: 9, y: 8, z: 3, yaw: Math.PI },
                   { t: 16, x: 9, y: 8, z: 20, yaw: Math.PI } ],
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
      const e = envelope(r.L, r.R, 44100);
      console.log(`   ${model.padEnd(4)} peak ${(20 * Math.log10(r.peak + 1e-9)).toFixed(1)} dBFS`
        + `  worst ${e.worst.toFixed(1)} dB/s at ${e.at.toFixed(1)}s  -> renders/${path.basename(f)}`);
      console.log(`        ${e.spark}`);
    }
    console.log();
  }
  console.log(`Both models share one normalisation factor per scenario, so level differences you`);
  console.log(`hear between old and new are real and not an artefact of separate normalising.\n`);
}
main();
