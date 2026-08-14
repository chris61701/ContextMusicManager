// ───────────────────────────────────────────────────────────────────────────────
// DIFFUSE SANITY — reverberation must not arrive where sound cannot
// ───────────────────────────────────────────────────────────────────────────────
// The author, on every demo: "the second half always has so much reverb/echo, is that
// intentional?" It was not, and the measurement is damning. Diffuse energy minus direct energy,
// median over standable voxels:
//
//     synthetic sealed box      +6.3 dB
//     real store                +1.8 dB   (p90 +21.7)
//     real cave                 +9.2 dB
//     real bunker              +96.6 dB   <-- not a reverberant room, a broken calculation
//
// The bunker's room fields are perfectly ordinary (-19 to -40 dB). What is wrong is that its DIRECT
// sound is about -99 dB, correctly, because it is sealed concrete -- and the diffuse field still
// arrives at -3 dB. Reverberation is reaching places sound cannot reach.
//
// WHY NO EXISTING GATE CAUGHT IT, which is the part worth keeping:
// the diffuse field is the term that FLATTENS spatial gradients. Every acceptance metric here
// measures either total level against a band, or dB/block smoothness. An inflated diffuse field
// makes the smoothness metrics BETTER while destroying the sound, so the acceptance criteria were
// actively rewarding the defect. A suite of level metrics cannot see a balance error, in the same
// way it could not see comb filtering.
//
// So this gates the BALANCE, which no other harness looks at:
//
//   REVERB_MAX_OVER   diffuse may not exceed direct+aperture energy by more than this, anywhere.
//                     Physically the reverberant field DOES exceed the direct beyond the critical
//                     distance, and by a lot in a live room, so this is deliberately generous. It
//                     is a bound on absurdity, not a target.
//   REACH             wherever diffuse energy is audible, an air/transmission path must also be
//                     audible. A room the direct sound cannot reach must not fill with reverb.
//
//   node diffuse.js
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const P = require("./portals.js");
const { checkDump } = require("./dumpcheck.js");

// Derived rather than picked. The reverberant field legitimately exceeds the direct sound past the
// critical distance, by 10*log10(16*pi*r^2/R). For the smallest genuinely live room in these
// captures (bunker room 1, R = 203) at the far end of a plausible audible range (~50 blocks) that
// is 10*log10(16*pi*2500/203) = +28 dB. So a tail in the high twenties is theory-consistent and not
// a defect; 35 leaves headroom above the largest such case without admitting absurdity.
//
// PROVISIONAL. A tighter bound is only meaningful once the distance law is settled: the model
// attenuates exponentially at 0.35 dB/block while 16*pi*r^2/R assumes inverse-square, and at 12
// blocks those differ by 17 dB, so a per-voxel comparison against theory would flag the mismatch
// rather than the model. Fixing that is the open architectural question.
const REVERB_MAX_OVER = 35;
const REACH_FLOOR = -90;      // dB: below this the dry path is inaudible, so reverb must be too
const STRIDE = 2;
// AUDIBILITY FLOOR. This gate claims to test "audible reverb where sound cannot reach", and it must
// actually do that rather than compare ratios between two inaudible numbers. A ratio of 80 dB
// between signals at -40 and -120 dB is arithmetic, not a defect anyone can hear. So a voxel only
// counts if its reverb is within this range of the LOUDEST point in the capture, i.e. if it is
// genuinely part of the mix. Without it the gate flagged 16 voxels sitting at -40 dB and below.
const AUDIBLE_RANGE = 40;

function survey(file, label) {
  const D = loadDump(file);
  const G = Rg.asGraph(D, 0.62);
  const em = D.emitters[0];
  const ev = P.makeNewEvaluator(D, G, em);
  const y = em.y + 1;
  // loudest point in the capture, so audibility can be judged relative to it
  let peak = -999;
  for (let x = D.x0 + 1; x < D.x0 + D.sx - 1; x += STRIDE * 2)
    for (let z = D.z0 + 1; z < D.z0 + D.sz - 1; z += STRIDE * 2) {
      if (!D.inB(x, y, z) || G.open[D.idx(x, y, z)] < 0.9) continue;
      const t = ev(x, y, z).total;
      if (isFinite(t) && t > peak) peak = t;
    }
  const floor = peak - AUDIBLE_RANGE;

  const over = [];
  let orphan = 0, n = 0;
  for (let x = D.x0 + 1; x < D.x0 + D.sx - 1; x += STRIDE)
    for (let z = D.z0 + 1; z < D.z0 + D.sz - 1; z += STRIDE) {
      if (!D.inB(x, y, z) || G.open[D.idx(x, y, z)] < 0.9) continue;
      const r = ev(x, y, z);
      if (!isFinite(r.total)) continue;
      // Sum the UNPRUNED arrivals. `airs` is pruned to PRUNE_DB below the loudest arrival, and the
      // loudest is often the diffuse field itself -- so a loud diffuse term prunes away the very dry
      // paths being compared against it, and the harness would report its own pruning as a defect.
      // (That pruning is a real modelling question too: the diffuse field is not a path, so using it
      // as the reference for path pruning is dubious. But it must not be measured as reach.)
      let dry = Math.pow(10, r.solidDb / 10);
      for (const a of (r.arrivals || [])) {
        if (a.isSrc || a.isDiffuse) continue;
        dry += (a.w === undefined ? 1 : a.w) * Math.pow(10, a.db / 10);
      }
      const dryDb = dry > 0 ? 10 * Math.log10(dry) : -999;
      n++;
      if (r.diffDb <= -110) continue;
      if (r.diffDb < floor) continue;          // inaudible in the mix: not a defect anyone can hear
      over.push(r.diffDb - dryDb);
      // reverb where the dry path is inaudible: a place sound cannot reach is filling anyway
      if (dryDb < REACH_FLOOR && r.diffDb > REACH_FLOOR) orphan++;
    }
  over.sort((a, b) => a - b);
  const q = f => over.length ? over[Math.min(over.length - 1, Math.floor(f * over.length))] : 0;
  return { label, n, med: q(0.5), p90: q(0.9), max: over.length ? over[over.length - 1] : 0,
           orphanPct: n ? 100 * orphan / n : 0, peak, audible: over.length };
}

function main() {
  const rows = [];
  const sdir = path.join(__dirname, "synth");
  for (const f of ["synth_wall-wood_door-air_open-1.json", "synth_floor-wood_hatch-wood_open-0.json"])
    if (fs.existsSync(path.join(sdir, f))) rows.push(survey(path.join(sdir, f), "synth " + f.slice(6, 26)));
  const ddir = path.join(__dirname, "dumps");
  if (fs.existsSync(ddir))
    for (const f of fs.readdirSync(ddir).filter(x => x.endsWith(".json"))) {
      if (!checkDump(path.join(ddir, f)).ok) continue;
      rows.push(survey(path.join(ddir, f), "real " + f.split("_2026")[0].slice(0, 20)));
    }

  console.log(`\nDIFFUSE BALANCE — reverberation must not arrive where sound cannot\n`);
  console.log(`${"geometry".padEnd(30)}${"median".padStart(8)}${"p90".padStart(7)}${"max".padStart(7)}`
            + `${"orphan".padStart(9)}   verdict`);
  console.log("-".repeat(78));
  const fails = [];
  for (const r of rows) {
    const bad = r.max > REVERB_MAX_OVER || r.orphanPct > 1;
    console.log(`${r.label.padEnd(30)}${r.med.toFixed(1).padStart(8)}${r.p90.toFixed(1).padStart(7)}`
      + `${r.max.toFixed(1).padStart(7)}${(r.orphanPct.toFixed(1) + "%").padStart(9)}   ${bad ? "FAIL" : "ok"}`);
    if (r.max > REVERB_MAX_OVER)
      fails.push(`${r.label}: diffuse sits ${r.max.toFixed(1)} dB over the dry path (limit ${REVERB_MAX_OVER})`);
    if (r.orphanPct > 1)
      fails.push(`${r.label}: ${r.orphanPct.toFixed(1)}% of voxels have audible reverb where the dry path is inaudible`);
  }
  console.log(`\nmedian/p90/max = diffuse minus dry, in dB. orphan = share of standable voxels where`);
  console.log(`the dry path is below ${REACH_FLOOR} dB but reverb is not — sound that arrived by no route.\n`);
  if (fails.length) {
    console.log("FAIL"); for (const f of fails) console.log("  " + f);
    console.log();
    process.exit(1);
  }
  console.log("diffuse balance ok\n");
}
main();
