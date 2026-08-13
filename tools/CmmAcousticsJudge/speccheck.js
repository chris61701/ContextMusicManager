// ───────────────────────────────────────────────────────────────────────────────
// SPEC CHECK — does a wall cost a CONSISTENT amount, and the right amount?
// ───────────────────────────────────────────────────────────────────────────────
// portals.js showed the new model's field is spatially smooth. Smooth is not the
// same as correct: a uniformly wrong field is perfectly continuous. This measures
// LEVELS against tools/CmmAcousticsRef/README.md.
//
// Method: bucket every in-range air voxel by (distance band, material on the straight
// line to the speaker), then report the mean level per bucket. The cost of a wall is
// then the level in the "1 wall" bucket minus the level in the "0 walls" bucket AT THE
// SAME DISTANCE. Doing that at several distances tests the central thesis directly:
//
//   • The shipped model prices occlusion as extra virtual DISTANCE, fed into a curve
//     whose slope varies ~20x across the range. So the same wall must cost different
//     dB depending on where the listener stands, and that spread is the defect.
//   • The new model prices it in dB against an exponential curve, where the distance
//     term cancels algebraically. The cost must be flat across distance bands.
//
// A flat column for the new model and a spreading column for the old one is the proof.
// It also gives the absolute number to check against spec #1 ("shut interior door →
// roughly half as loud", i.e. about -6 dB).
//
// Usage:  node speccheck.js <dump.json> [--emitter=0] [--cell=4]
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const { loadDump, makeEvaluator, segLoss } = require("./judge3d.js");
const { buildGraph, makeNewEvaluator, makeOpenTest } = require("./portals.js");

const DIST_BANDS = [[3, 8], [8, 15], [15, 25], [25, 40], [40, 60]];
// Material on the straight line, in full-wall units. CellLoss gives wood ~0.25/cell,
// stone ~0.9, metal 1.0 — so these bands are roughly "a door", "a wall", "two walls",
// "serious material".
const WALL_BANDS = [[0, 0.05], [0.05, 0.4], [0.4, 0.8], [0.8, 1.6], [1.6, 3.2]];
const WALL_LABEL = ["open", "~door", "~1 wall", "~2 wall", "heavy"];

function bandOf(v, bands) {
  for (let i = 0; i < bands.length; i++) if (v >= bands[i][0] && v < bands[i][1]) return i;
  return -1;
}

function main() {
  const args = process.argv.slice(2);
  const file = args.find(a => !a.startsWith("--"));
  const emIdx = parseInt((args.find(a => a.startsWith("--emitter=")) || "=0").split("=")[1], 10) || 0;
  const cell = parseInt((args.find(a => a.startsWith("--cell=")) || "=4").split("=")[1], 10) || 4;
  if (!file) { console.error("usage: node speccheck.js <dump.json> [--emitter=N] [--cell=N]"); process.exit(1); }

  const D = loadDump(file);
  const em = D.emitters[emIdx];
  const range = D.cfg.range;
  const open = makeOpenTest(D);
  const G = buildGraph(D, cell);
  const nEval = makeNewEvaluator(D, G, em);
  const oEval = makeEvaluator(D, em);

  // acc[model][distBand][wallBand] = { n, sum }
  const acc = [[], []];
  for (let m = 0; m < 2; m++)
    for (let i = 0; i < DIST_BANDS.length; i++) {
      acc[m][i] = [];
      for (let j = 0; j < WALL_BANDS.length; j++) acc[m][i][j] = { n: 0, sum: 0 };
    }

  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = x + D.sx * (z + D.sz * y);
    if (!open[i]) continue;
    const wx = D.x0 + x, wy = D.y0 + y, wz = D.z0 + z;
    const d = Math.hypot(wx - em.x, wy - em.y, wz - em.z);
    if (d > range) continue;
    const di = bandOf(d, DIST_BANDS); if (di < 0) continue;
    const walls = segLoss(D, em.x, em.y, em.z, wx, wy, wz);
    const wi = bandOf(walls, WALL_BANDS); if (wi < 0) continue;

    const nr = nEval(wx, wy, wz);
    const or = oEval(wx, wy, wz);
    const oDb = or.att > 0 ? 20 * Math.log10(or.att) : -120;
    acc[0][di][wi].n++; acc[0][di][wi].sum += oDb;
    const a1 = acc[1][di][wi];
    a1.n++; a1.sum += nr.total;
    a1.air = (a1.air || 0) + nr.airDb;
    a1.sol = (a1.sol || 0) + Math.max(-120, nr.solidDb);
    a1.airWins = (a1.airWins || 0) + (nr.airDb > nr.solidDb ? 1 : 0);
    a1.detour = (a1.detour || 0) + (Number.isFinite(nr.airLen) ? nr.airLen / Math.max(1, nr.d) : 0);
    a1.detourN = (a1.detourN || 0) + (Number.isFinite(nr.airLen) ? 1 : 0);
  }

  console.log(`\n${D.poi}  emitter[${emIdx}] ${em.name} @ (${em.x},${em.y},${em.z})  range=${range} cell=${cell}`);

  for (const [mi, name] of [[0, "OLD (shipped)"], [1, "NEW (portal graph)"]]) {
    console.log(`\n── ${name} — mean level in dB per bucket ──`);
    let hdr = "  distance   ";
    for (const l of WALL_LABEL) hdr += l.padStart(10);
    console.log(hdr);
    for (let i = 0; i < DIST_BANDS.length; i++) {
      let line = `  ${(DIST_BANDS[i][0] + "-" + DIST_BANDS[i][1] + " blk").padEnd(11)}`;
      for (let j = 0; j < WALL_BANDS.length; j++) {
        const a = acc[mi][i][j];
        line += (a.n < 20 ? "-" : (a.sum / a.n).toFixed(1)).padStart(10);
      }
      console.log(line);
    }

    console.log(`  ── cost of the material, vs the "open" bucket at the SAME distance ──`);
    const costs = [[], [], [], []];
    for (let i = 0; i < DIST_BANDS.length; i++) {
      const base = acc[mi][i][0];
      let line = `  ${(DIST_BANDS[i][0] + "-" + DIST_BANDS[i][1] + " blk").padEnd(11)}`;
      line += "".padStart(10);
      for (let j = 1; j < WALL_BANDS.length; j++) {
        const a = acc[mi][i][j];
        if (base.n < 20 || a.n < 20) { line += "-".padStart(10); continue; }
        const c = (a.sum / a.n) - (base.sum / base.n);
        costs[j - 1].push(c);
        line += c.toFixed(1).padStart(10);
      }
      console.log(line);
    }
    let sline = "  SPREAD     " + "".padStart(10);
    for (let j = 0; j < 4; j++) {
      const c = costs[j];
      sline += (c.length < 2 ? "-" : (Math.max(...c) - Math.min(...c)).toFixed(1)).padStart(10);
    }
    console.log(sline + "   <- how much the SAME material's cost varies with listener distance");
  }

  // Which arrival is carrying the sound? If AIR dominates even in the walled buckets,
  // the straight-line material is largely irrelevant there (flanking — sound is coming
  // round through a doorway), and the bucket label "1 wall" describes the straight line
  // rather than what the listener actually hears.
  console.log(`\n── NEW: which arrival dominates, and how far the air route detours ──`);
  console.log(`  (airWin% = air arrival louder than solid; detour = air route length / straight distance)`);
  let hdr = "  distance   ";
  for (const l of WALL_LABEL) hdr += l.padStart(14);
  console.log(hdr);
  for (let i = 0; i < DIST_BANDS.length; i++) {
    let line = `  ${(DIST_BANDS[i][0] + "-" + DIST_BANDS[i][1] + " blk").padEnd(11)}`;
    for (let j = 0; j < WALL_BANDS.length; j++) {
      const a = acc[1][i][j];
      if (a.n < 20) { line += "-".padStart(14); continue; }
      const pct = (100 * (a.airWins || 0) / a.n).toFixed(0);
      const det = a.detourN ? (a.detour / a.detourN).toFixed(2) : "-";
      line += `${pct}%/${det}x`.padStart(14);
    }
    console.log(line);
  }
  console.log(`\n── NEW: the two arrivals separately (mean dB) ──`);
  console.log(hdr);
  for (let i = 0; i < DIST_BANDS.length; i++) {
    let line = `  ${(DIST_BANDS[i][0] + "-" + DIST_BANDS[i][1] + " blk").padEnd(11)}`;
    for (let j = 0; j < WALL_BANDS.length; j++) {
      const a = acc[1][i][j];
      if (a.n < 20) { line += "-".padStart(14); continue; }
      line += `${(a.air / a.n).toFixed(1)}/${(a.sol / a.n).toFixed(0)}`.padStart(14);
    }
    console.log(line);
  }
  console.log();
}

main();
