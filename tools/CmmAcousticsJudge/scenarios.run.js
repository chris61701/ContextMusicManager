// ───────────────────────────────────────────────────────────────────────────────
// SCENARIO RUN — the spec, measured at PROBE POINTS, as excess loss
// ───────────────────────────────────────────────────────────────────────────────
// Averaging whole rooms was too blunt to test the spec. It mixed a listener standing in
// the doorway's sight line with one tucked in a far corner where the door is not visible,
// and it let corner listeners whose straight line grazes the concrete shell drag the mean
// down. Both #1 and #2 were unreadable as a result.
//
// So: named probe points, and the number reported is EXCESS LOSS over free field --
//     excess = level + SLOPE * distance
// Free field is 0. A shut interior door is whatever the barrier itself costs, independent
// of how far away the probe happens to be. That is exactly the unit the spec is written
// in: "roughly half as loud" is -6 dB of excess.
//
// Usage:  node scenarios.run.js
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump, makeEvaluator } = require("./judge3d.js");
const { buildGraph, makeNewEvaluator, SLOPE } = require("./portals.js");
const { MATRIX, SPK } = require("./synth.js");

const CELL = 4;

// Probes, chosen to separate the cases the room average conflated.
const PROBES = [
  { k: "A clear",   x: 7,  y: 3, z: 20, note: "same room, clear line — the control, should be ~0" },
  { k: "B on-axis", x: 30, y: 3, z: 11, note: "next room, straight through the doorway" },
  { k: "B off-set", x: 30, y: 3, z: 20, note: "next room, off the doorway's sight line" },
  { k: "B far",     x: 40, y: 3, z: 20, note: "next room, far corner" },
];

// What the spec asks for, as excess loss, per case. null = not specified.
const TARGET = {
  "S2 door OPEN, wood wall":                        { "B on-axis": [-3, 0],   label: "nearly line-of-sight" },
  "S1 door SHUT, wood wall":                        { "B on-axis": [-9, -4],  label: "~half as loud (-6)" },
  "S3 door SHUT, concrete wall":                     { "B off-set": [-40, -20], label: "faint, bass-heavy" },
  // Corrected bands. A vault door in a WOOD wall should sound like a WOOD WALL (~-6),
  // because that is what flanking means -- the door's rating stops mattering. The earlier
  // -20..-8 band was my error, not the model's. And "nothing" needs a wall thick enough
  // that flanking through it is dead too; a 1-block concrete wall limits at the concrete.
  "S4 VAULT door in WOOD wall (should NOT seal)":    { "B on-axis": [-10, -3], label: "flanks: reads as the WOOD wall" },
  "S4 VAULT door in CONCRETE wall (should seal)":    { "B on-axis": [-45, -20], label: "faint: limited by 1-block concrete" },
  "S4 VAULT in 3-thick CONCRETE (bunker)":           { "B on-axis": [-200, -45], label: "nothing" },
};

function run(file) {
  const D = loadDump(file);
  const raw = JSON.parse(fs.readFileSync(file, "utf8"));
  const em = D.emitters[0];
  const G = buildGraph(D, CELL);
  const nEval = makeNewEvaluator(D, G, em);
  const oEval = makeEvaluator(D, em);
  const out = { tag: raw.tag, probes: {} };
  for (const p of PROBES) {
    const d = Math.hypot(p.x - em.x, p.y - em.y, p.z - em.z);
    const free = -SLOPE * d;                       // free-field reference at this distance
    const nr = nEval(p.x, p.y, p.z);
    const or = oEval(p.x, p.y, p.z);
    const oDb = or.att > 0 ? 20 * Math.log10(or.att) : -120;
    out.probes[p.k] = {
      d, oldExcess: oDb - free, newExcess: nr.total - free,
      air: nr.airDb - free, sol: Math.max(-120, nr.solidDb) - free,
    };
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
  const order = ["S1 door SHUT, wood wall", "S6 door 1/3 open", "S6 door 2/3 open", "S2 door OPEN, wood wall"];
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
