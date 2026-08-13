// ───────────────────────────────────────────────────────────────────────────────
// SCENARIO RUN — the spec, measured at PROBE POINTS, as excess loss
// ───────────────────────────────────────────────────────────────────────────────
// Averaging whole rooms was too blunt to test the spec. It mixed a listener standing in
// the doorway's sight line with one tucked in a far corner where the door is not visible,
// and it let corner listeners whose straight line grazes the concrete shell drag the mean
// down. Both #1 and #2 were unreadable as a result.
//
// The number reported is the level RELATIVE TO THE CONTROL PROBE -- same room, clear line of
// sight, at its own distance. Not excess over free field: that measure was correct for a dry
// model and became meaningless the moment a diffuse field existed, because a diffuse field is
// distance-INDEPENDENT while excess-over-free-field adds SLOPE*distance, so a far probe read
// +10.2 dB purely for being far. The control probe itself read +4.6 instead of 0.
//
// Relative-to-control is also what the spec is actually written in: "nearly as loud as line of
// sight", "roughly half as loud", "faint" are all comparisons against what you hear with a clear
// view in the same space -- which, in a real room, includes that room's reverberation.
//
// Usage:  node scenarios.run.js
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump, makeEvaluator } = require("./judge3d.js");
const REGIONS = process.env.CMM_REGIONS === "1";
const Rg = require("./regions.js");
const { buildGraph, makeNewEvaluator, SLOPE } = require("./portals.js");
const { MATRIX, SPK } = require("./synth.js");

const CELL = 4;

// Probes, chosen to separate the cases the room average conflated.
const PROBES = [
  { k: "A clear",   x: 7,  y: 3, z: 20, note: "same room, clear line — THE CONTROL, 0 by definition" },
  { k: "B on-axis", x: 30, y: 3, z: 11, note: "next room, straight through the doorway" },
  { k: "B off-set", x: 30, y: 3, z: 20, note: "next room, off the doorway's sight line" },
  { k: "B far",     x: 40, y: 3, z: 20, note: "next room, far corner" },
];

// What the spec asks for, as excess loss, per case. null = not specified.
// Bands restated in the RELATIVE-TO-CONTROL metric, from the author's own words. The metric had
// to change when the model gained a diffuse field (excess-over-free-field is meaningless once a
// distance-independent term exists), so the bands had to be restated too -- but from the words,
// not fitted to whatever the model currently produces. Moving a band to accommodate a missing
// term would hide the term.
const TARGET = {
  // "nearly as loud as line-of-sight" -- a couple of dB below standing in the room
  "S2 door OPEN, wood wall":                       { "B on-axis": [-4, 0],    label: "nearly line-of-sight" },
  // "clearly quieter but obviously still music... roughly half as loud"
  "S1 door SHUT, wood wall":                       { "B on-axis": [-9, -4],   label: "~half as loud" },
  // "faint, bass-heavy, no detail"
  "S3 door SHUT, concrete wall":                   { "B off-set": [-45, -20], label: "faint, bass-heavy" },
  // flanks: the door's rating stops mattering, so it reads much like the wood wall beside it
  "S4 VAULT door in WOOD wall (should NOT seal)":  { "B on-axis": [-18, -6],  label: "flanks: reads as the wood wall" },
  "S4 VAULT door in CONCRETE wall (should seal)":  { "B on-axis": [-50, -25], label: "faint: limited by 1-block concrete" },
  // "nothing"
  "S4 VAULT in 3-thick CONCRETE (bunker)":         { "B on-axis": [-200, -45], label: "nothing" },
};


function run(file) {
  const D = loadDump(file);
  const raw = JSON.parse(fs.readFileSync(file, "utf8"));
  const em = D.emitters[0];
  const G = REGIONS ? Rg.asGraph(D, 0.62) : buildGraph(D, CELL);
  const nEval = makeNewEvaluator(D, G, em);
  const oEval = makeEvaluator(D, em);
  const out = { tag: raw.tag, probes: {} };
  const raw2 = {};
  for (const p of PROBES) {
    const d = Math.hypot(p.x - em.x, p.y - em.y, p.z - em.z);
    const nr = nEval(p.x, p.y, p.z);
    const or = oEval(p.x, p.y, p.z);
    const oDb = or.att > 0 ? 20 * Math.log10(or.att) : -120;
    raw2[p.k] = { d, oDb, nDb: nr.total, air: nr.airDb, sol: Math.max(-120, nr.solidDb) };
  }
  // the control: same room, clear line. Everything is quoted against it.
  const ctlO = raw2["A clear"].oDb, ctlN = raw2["A clear"].nDb;
  for (const p of PROBES) {
    const q = raw2[p.k];
    out.probes[p.k] = { d: q.d, oldExcess: q.oDb - ctlO, newExcess: q.nDb - ctlN,
                        air: q.air - ctlN, sol: q.sol - ctlN };
  }
  return out;
}

function verdict(tag, probes) {
  const t = TARGET[tag];
  if (!t) return "";
  for (const k of Object.keys(t)) {
    if (k === "label") continue;
    const [lo, hi] = t[k];
    const v = probes[k].newExcess;
    const ok = v >= lo && v <= hi;
    return `${ok ? "PASS" : "FAIL"}  (${k} ${v.toFixed(1)}, want ${lo}..${hi} — ${t.label})`;
  }
  return "";
}

function main() {
  const dir = path.join(__dirname, "synth");
  const files = MATRIX.map(m => ({
    tag: m.tag,
    file: path.join(dir, `synth_wall-${m.wall}_door-${m.door}_open-${m.open}${m.thick > 1 ? "_t" + m.thick : ""}.json`),
  })).filter(f => fs.existsSync(f.file));
  if (!files.length) { console.error("no synth dumps -- run: node synth.js"); process.exit(1); }

  console.log(`\nEXCESS LOSS over free field (dB). 0 = unobstructed. The spec's "half as loud" = -6.`);
  console.log(`speaker at (${SPK.x},${SPK.y},${SPK.z}); cell=${CELL}\n`);
  for (const p of PROBES) console.log(`  ${p.k.padEnd(10)} (${p.x},${p.y},${p.z})  ${p.note}`);
  console.log();

  const w = 11;
  let hdr = "case".padEnd(46);
  for (const p of PROBES) hdr += (p.k + " o/n").padStart(w + 6);
  console.log(hdr);
  console.log("-".repeat(hdr.length));

  const rows = [];
  for (const f of files) {
    const r = run(f.file);
    rows.push(r);
    let line = f.tag.slice(0, 45).padEnd(46);
    for (const p of PROBES) {
      const q = r.probes[p.k];
      line += `${q.oldExcess.toFixed(0)}/${q.newExcess.toFixed(1)}`.padStart(w + 6);
    }
    console.log(line);
  }

  console.log(`\n── verdict against the spec (new model) ──`);
  for (const r of rows) {
    const v = verdict(r.tag, r.probes);
    if (v) console.log(`  ${r.tag.slice(0, 45).padEnd(46)} ${v}`);
  }

  console.log(`\n── scenario #6, gradual (B on-axis excess) ──`);
  const order = ["S1 door SHUT, wood wall", "S6 door 20% open", "S6 door 40% open",
                 "S6 door 60% open", "S6 door 80% open", "S2 door OPEN, wood wall"];
  for (const tag of order) {
    const r = rows.find(x => x.tag === tag); if (!r) continue;
    const q = r.probes["B on-axis"];
    console.log(`  ${tag.padEnd(30)} old ${q.oldExcess.toFixed(1).padStart(7)}   new ${q.newExcess.toFixed(1).padStart(7)}   (air ${q.air.toFixed(1)} / solid ${q.sol.toFixed(0)})`);
  }

  console.log(`\n── scenario #4, flanking: the SAME vault door in two walls (B on-axis) ──`);
  for (const tag of ["S4 VAULT door in WOOD wall (should NOT seal)", "S4 VAULT door in CONCRETE wall (should seal)"]) {
    const r = rows.find(x => x.tag === tag); if (!r) continue;
    const q = r.probes["B on-axis"];
    console.log(`  ${tag.slice(0, 45).padEnd(46)} old ${q.oldExcess.toFixed(1).padStart(7)}   new ${q.newExcess.toFixed(1).padStart(7)}`);
  }
  console.log();
}
main();
