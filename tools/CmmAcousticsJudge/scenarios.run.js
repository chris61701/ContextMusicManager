// ───────────────────────────────────────────────────────────────────────────────
// SCENARIO RUN — the spec, measured, on controlled geometry
// ───────────────────────────────────────────────────────────────────────────────
// Runs both models over every case synth.js generates and reports the level in ROOM B
// relative to the reference level in ROOM A at a matched distance. That relative number
// is what the spec is written in: "roughly half as loud" is about -6 dB.
//
// Usage:  node scenarios.run.js
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump, makeEvaluator } = require("./judge3d.js");
const { buildGraph, makeNewEvaluator, makeOpenTest } = require("./portals.js");
const { MATRIX } = require("./synth.js");

const CELL = 4;

// Sample room B at a matched straight-line distance band from the speaker, so the
// comparison isolates the wall/door and not the distance.
const BAND = [14, 26];

function levelsFor(file) {
  const D = loadDump(file);
  const raw = JSON.parse(fs.readFileSync(file, "utf8"));
  const R = raw.regions;
  const em = D.emitters[0];
  const open = makeOpenTest(D);
  const G = buildGraph(D, CELL);
  const nEval = makeNewEvaluator(D, G, em);
  const oEval = makeEvaluator(D, em);

  const out = { A: { o: [], n: [] }, B: { o: [], n: [] }, tag: raw.tag, poi: D.poi };
  const inRegion = (r, x, y, z) => x >= r.x0 && x <= r.x1 && y >= r.y0 && y <= r.y1 && z >= r.z0 && z <= r.z1;

  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = x + D.sx * (z + D.sz * y);
    if (!open[i]) continue;
    const d = Math.hypot(x - em.x, y - em.y, z - em.z);
    if (d < BAND[0] || d > BAND[1]) continue;
    const which = inRegion(R.roomA, x, y, z) ? "A" : inRegion(R.roomB, x, y, z) ? "B" : null;
    if (!which) continue;
    const nr = nEval(x, y, z);
    const or = oEval(x, y, z);
    out[which].o.push(or.att > 0 ? 20 * Math.log10(or.att) : -120);
    out[which].n.push(nr.total);
    if (which === "B") {
      (out.Bair = out.Bair || []).push(nr.airDb);
      (out.Bsol = out.Bsol || []).push(Math.max(-120, nr.solidDb));
    }
  }
  const mean = a => a.length ? a.reduce((s, v) => s + v, 0) / a.length : NaN;
  return {
    tag: raw.tag, poi: D.poi,
    nA: out.A.n.length, nB: out.B.n.length,
    oldA: mean(out.A.o), oldB: mean(out.B.o),
    newA: mean(out.A.n), newB: mean(out.B.n),
    newBair: mean(out.Bair || []), newBsol: mean(out.Bsol || []),
  };
}

function main() {
  const dir = path.join(__dirname, "synth");
  const files = MATRIX.map(m => {
    const poi = `synth_wall-${m.wall}_door-${m.door}_open-${m.open}`;
    return { tag: m.tag, file: path.join(dir, poi + ".json") };
  }).filter(f => fs.existsSync(f.file));
  if (!files.length) { console.error("no synth dumps -- run: node synth.js"); process.exit(1); }

  console.log(`\nLevel in ROOM B relative to ROOM A, both sampled at ${BAND[0]}-${BAND[1]} blocks from the speaker.`);
  console.log(`The spec is written in these terms: "roughly half as loud" ~ -6 dB.\n`);
  console.log(`${"case".padEnd(50)}${"OLD".padStart(8)}${"NEW".padStart(8)}   ${"new: air / solid".padStart(18)}`);
  console.log("-".repeat(88));
  const rows = [];
  for (const f of files) {
    const r = levelsFor(f.file);
    const oldRel = r.oldB - r.oldA, newRel = r.newB - r.newA;
    rows.push({ ...r, oldRel, newRel });
    const airsol = `${r.newBair.toFixed(1)} / ${r.newBsol.toFixed(0)}`;
    console.log(`${f.tag.padEnd(50)}${oldRel.toFixed(1).padStart(8)}${newRel.toFixed(1).padStart(8)}   ${airsol.padStart(18)}`);
  }

  // Scenario #4: the flanking pair
  const vw = rows.find(r => r.tag.includes("VAULT door in WOOD"));
  const vc = rows.find(r => r.tag.includes("VAULT door in CONCRETE"));
  if (vw && vc) {
    console.log(`\nScenario #4 (flanking) — the SAME vault door, two walls:`);
    console.log(`  in a WOOD wall     old ${vw.oldRel.toFixed(1)} dB   new ${vw.newRel.toFixed(1)} dB   <- should NOT seal (sound flanks the wall)`);
    console.log(`  in a CONCRETE wall old ${vc.oldRel.toFixed(1)} dB   new ${vc.newRel.toFixed(1)} dB   <- should seal`);
    console.log(`  separation:        old ${(vw.oldRel - vc.oldRel).toFixed(1)} dB   new ${(vw.newRel - vc.newRel).toFixed(1)} dB   <- bigger = the model can tell them apart`);
  }

  // Scenario #6: gradual?
  const swing = rows.filter(r => r.tag.startsWith("S6") || r.tag.includes("door SHUT, wood") || r.tag.includes("door OPEN, wood"))
                    .sort((a, b) => a.newRel - b.newRel);
  if (swing.length >= 3) {
    console.log(`\nScenario #6 (gradual) — wood door through its swing:`);
    for (const s of swing) console.log(`  ${s.tag.padEnd(30)} old ${s.oldRel.toFixed(1).padStart(7)}   new ${s.newRel.toFixed(1).padStart(7)}`);
  }
  console.log();
}
main();
