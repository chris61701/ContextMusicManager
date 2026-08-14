// ───────────────────────────────────────────────────────────────────────────────
// REAL GEOMETRY — the same smoothness question, asked of game-truth captures
// ───────────────────────────────────────────────────────────────────────────────
// smooth.js measures four hand-drawn routes on synthetic two-room geometry. That is the right
// place to develop a term, because the geometry is known and a number can be traced to a cause.
// It is the wrong place to be confident, because real POIs contain shapes nobody thought to
// synthesise -- shelving, stairwells, partial blocks, storefronts, cave branches.
//
// Two defects found within an hour of finally measuring real captures make the case: the
// transmission disc discarded the unblocked share of the Fresnel zone (-3 dB to -22 dB across one
// block, 160 times over in one store), and two of the five captures turn out to be unusable
// altogether. Neither was visible on the synthetic gates, which were green throughout.
//
// So this walks EVERY open voxel of a horizontal slab through the listener's own height and
// reports the worst neighbour-to-neighbour gradient, per dump. It is the p99's replacement done
// properly: worst-case rather than a percentile that averages a defect away, and reported per
// capture rather than pooled, so one bad POI cannot hide behind four good ones.
//
// Invalid captures are skipped, not measured -- see dumpcheck.js for why that matters.
//
//   node real.js            every valid dump
//   node real.js --slabs=3  three slabs per dump instead of one
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const P = require("./portals.js");
const { checkDump } = require("./dumpcheck.js");

const SLABS = parseInt((process.argv.find(a => a.startsWith("--slabs=")) || "=1").split("=")[1], 10) || 1;
const OPEN_MIN = 0.9;   // only measure where a listener could actually stand

function measure(file) {
  const D = loadDump(file);
  const em = D.emitters[0];
  const G = Rg.asGraph(D, 0.62);
  const ev = P.makeNewEvaluator(D, G, em);

  let n = 0, sum = 0, worst = 0, wat = null, over1 = 0, over3 = 0;
  for (let s = 0; s < SLABS; s++) {
    const y = em.y + 1 + s * 2;
    for (let x = D.x0 + 1; x < D.x0 + D.sx - 1; x++)
      for (let z = D.z0 + 1; z < D.z0 + D.sz - 1; z++) {
        if (!D.inB(x, y, z) || G.open[D.idx(x, y, z)] < OPEN_MIN) continue;
        const a = ev(x, y, z);
        if (!isFinite(a.total)) continue;
        for (const [dx, dz] of [[1, 0], [0, 1]]) {
          if (!D.inB(x + dx, y, z + dz) || G.open[D.idx(x + dx, y, z + dz)] < OPEN_MIN) continue;
          const b = ev(x + dx, y, z + dz);
          if (!isFinite(b.total)) continue;
          const g = Math.abs(b.total - a.total);
          n++; sum += g;
          if (g > 1) over1++;
          if (g > 3) over3++;
          if (g > worst) { worst = g; wat = [x, y, z]; }
        }
      }
  }
  return { rooms: G.rooms.length, apertures: G.apertures.length, n, worst, wat,
           mean: sum / (n || 1), over1: 100 * over1 / (n || 1), over3: 100 * over3 / (n || 1) };
}

function main() {
  const dir = path.join(__dirname, "dumps");
  if (!fs.existsSync(dir)) { console.error("no dumps/ directory"); process.exit(1); }
  const files = fs.readdirSync(dir).filter(f => f.endsWith(".json")).map(f => path.join(dir, f));
  console.log(`\nREAL-GEOMETRY GRADIENT, dB per block, over every standable voxel of ${SLABS} slab(s).`);
  console.log(`Past ~3 dB/block reads as a step rather than a slope.\n`);
  console.log(`${"dump".padEnd(26)}${"worst".padStart(7)}${"mean".padStart(7)}${">1".padStart(8)}${">3".padStart(8)}   rooms/aper  worst at`);
  console.log("-".repeat(88));
  let skipped = 0;
  for (const f of files) {
    const name = path.basename(f).split("_2026")[0];
    const v = checkDump(f);
    if (!v.ok) { console.log(`${name.padEnd(26)}   ---- skipped: invalid capture (node dumpcheck.js)`); skipped++; continue; }
    const m = measure(f);
    console.log(`${name.padEnd(26)}${m.worst.toFixed(2).padStart(7)}${m.mean.toFixed(2).padStart(7)}`
      + `${m.over1.toFixed(1).padStart(7)}%${m.over3.toFixed(2).padStart(7)}%   `
      + `${(m.rooms + "/" + m.apertures).padEnd(11)} (${(m.wat || []).join(",")})`);
  }
  if (skipped) console.log(`\n${skipped} capture(s) skipped as invalid — recapture with the POI loaded.`);
  console.log();
}
main();
