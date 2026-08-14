// ───────────────────────────────────────────────────────────────────────────────
// DUMP VALIDITY — is this capture usable as ground truth at all?
// ───────────────────────────────────────────────────────────────────────────────
// This file exists because a whole queue item was wrong. "The victorian POI collapses to 1
// region" sat on the list as a segmentation defect for days. It is not one: that dump's emitter
// sits 21 blocks ABOVE the top of any captured structure, in a 21x21x21 box that is 0.1% solid.
// The emitter is in open sky, and open sky IS one region. The segmentation was right and the
// data was wrong.
//
// Two of five dumps are like this. Both stop with three thinning layers just above terrain and
// then hold nothing at all for the next twenty-plus layers, so the building the speaker is in was
// never captured. Every conclusion drawn from those two -- region counts, aperture counts,
// dB/block gradients -- was measuring empty air.
//
// Method rule 4 says validate the instrument against a known-bad case before trusting it. A dump
// IS an instrument. So it gets a gate of its own, and the harnesses refuse to draw conclusions
// from a capture that fails it rather than reporting a number that looks like a model defect.
//
//   node dumpcheck.js            check every dump in dumps/
//   node dumpcheck.js path.json  check one
//
// Exits non-zero if any dump is unusable.
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const { makeOpenTest } = require("./portals.js");

// A speaker is a block in a building. The air immediately around it therefore has to contain a
// useful amount of material -- floor, walls, ceiling. Open sky scores ~0%; a real room's
// neighbourhood scored 26-75% on the three good dumps and 0.1-2.0% on the two bad ones, so the
// populations are three orders of magnitude apart and any threshold between them works.
const LOCAL_R = 10;
const LOCAL_SOLID_MIN = 0.08;   // fraction of a 21^3 box around the emitter that must be material
const ABOVE_STRUCTURE_MAX = 6;  // blocks the emitter may sit above the highest real structure
const LAYER_SOLID = 0.20;       // a layer is "real structure" past this solid fraction

function checkDump(file) {
  const D = loadDump(file);
  const open = makeOpenTest(D);
  const name = path.basename(file).split("_2026")[0];
  const notes = [];

  if (!D.emitters || !D.emitters.length) return { name, ok: false, notes: ["no emitters in dump"] };

  // highest layer that still looks like built geometry
  let topY = -1;
  for (let y = D.y0 + D.sy - 1; y >= D.y0; y--) {
    let s = 0, n = 0;
    for (let x = D.x0; x < D.x0 + D.sx; x += 2)
      for (let z = D.z0; z < D.z0 + D.sz; z += 2) { n++; if (open[D.idx(x, y, z)] <= 0.02) s++; }
    if (s / n > LAYER_SOLID) { topY = y; break; }
  }

  let worst = null;
  for (const em of D.emitters) {
    let s = 0, n = 0;
    for (let x = em.x - LOCAL_R; x <= em.x + LOCAL_R; x++)
      for (let y = em.y - LOCAL_R; y <= em.y + LOCAL_R; y++)
        for (let z = em.z - LOCAL_R; z <= em.z + LOCAL_R; z++) {
          if (!D.inB(x, y, z)) continue;
          n++; if (open[D.idx(x, y, z)] <= 0.02) s++;
        }
    const local = n ? s / n : 0;
    const above = em.y - topY;
    if (!worst || local < worst.local) worst = { local, above, em };
  }

  if (worst.local < LOCAL_SOLID_MIN)
    notes.push(`emitter neighbourhood only ${(100 * worst.local).toFixed(1)}% solid `
             + `(need ${(100 * LOCAL_SOLID_MIN).toFixed(0)}%) — the speaker is not inside captured geometry`);
  if (worst.above > ABOVE_STRUCTURE_MAX)
    notes.push(`emitter sits ${worst.above} blocks above the highest built layer (y=${topY}) `
             + `— the building around it was not captured`);

  return { name, ok: notes.length === 0, notes, local: worst.local, above: worst.above, topY,
           em: worst.em, dims: `${D.sx}x${D.sy}x${D.sz}` };
}

function main() {
  const arg = process.argv[2];
  const files = arg ? [arg]
    : fs.readdirSync(path.join(__dirname, "dumps")).filter(f => f.endsWith(".json"))
        .map(f => path.join(__dirname, "dumps", f));
  console.log("\nDUMP VALIDITY — a capture must contain the building its speaker is in\n");
  const bad = [];
  for (const f of files) {
    const r = checkDump(f);
    console.log(`  ${r.ok ? "ok  " : "BAD "} ${r.name.padEnd(26)} local ${(100 * r.local).toFixed(1).padStart(5)}%  `
              + `emitter ${r.above >= 0 ? "+" : ""}${r.above} vs structure top y=${r.topY}`);
    for (const n of r.notes) console.log(`       ${n}`);
    if (!r.ok) bad.push(r.name);
  }
  if (bad.length) {
    console.log(`\n${bad.length} of ${files.length} dumps unusable: ${bad.join(", ")}`);
    console.log("Do not read model behaviour off these. Recapture with the POI fully loaded.\n");
    process.exit(1);
  }
  console.log(`\nall ${files.length} dumps usable\n`);
}
if (require.main === module) main();
module.exports = { checkDump };
