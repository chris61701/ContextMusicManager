// ───────────────────────────────────────────────────────────────────────────────
// SPATIAL SMOOTHNESS — the metric that reproduces what the ear catches
// ───────────────────────────────────────────────────────────────────────────────
// portals.js reports the p99 adjacency jump over every air voxel in a POI. That number
// looked excellent (1.7 dB against the shipped model's 65) while the renders still
// lurched audibly, because a walk threads through a few hundred voxels and the bad ones
// cannot move a percentile taken over hundreds of thousands. Three separate "fixes"
// passed that metric without fixing anything.
//
// The governing requirement, in the author's words: smoothness must come from the FIELD,
// not from slewing. Time-smoothing makes the output path-dependent -- stand still and you
// are holding a value that depends on how you arrived rather than where you are. Sprinting
// past a door SHOULD change fast. What must not exist is a cliff in the field.
//
// So the right measure is the WORST spatial gradient along a route, in dB per block, plus
// where it is. That is scale-free (independent of walking speed), it has no time constant
// in it, and it is exactly the quantity the ear objects to.
//
// Reference points: a listener walks ~1.5 blocks/s. At that speed 1 dB/block is a gentle
// 1.5 dB/s. Anything past ~3 dB/block reads as a step rather than a slope.
//
// Usage:  node smooth.js [--cell=4]
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump, makeEvaluator } = require("./judge3d.js");
const REGIONS = process.env.CMM_REGIONS === "1";
const Rg = require("./regions.js");
const { buildGraph, makeNewEvaluator, SLOPE } = require("./portals.js");

const CELL = parseInt((process.argv.find(a => a.startsWith("--cell=")) || "=4").split("=")[1], 10) || 4;
// Sample at the model's OWN resolution. The evaluator takes integer voxels, so sampling
// finer than that and dividing by the finer spacing multiplies every per-voxel change by the
// oversampling factor -- at 0.25 blocks it reported 16 dB/block for what is really a 4 dB
// step between adjacent voxels. Inflating the number I am trying to drive down is not a
// harmless error: it made a real 4 dB defect look like an impossible one.
// (Evaluating at continuous listener positions is a separate improvement; the game's ear
// position is a float, so the model should eventually accept one.)
const STEP = 0.25;   // the model takes float positions now, so sub-block sampling is honest

// Routes worth measuring, on the synthetic geometry. These mirror the render scenarios so a
// number here can be tied to something audible there.
const ROUTES = [
  { name: "approach",  dump: "synth_wall-wood_door-air_open-1.json",
    from: [40, 3, 20], to: [22, 3, 11], then: [10, 3, 11] },
  { name: "pastdoor",  dump: "synth_wall-wood_door-air_open-1.json",
    from: [23, 3, 3], to: [23, 3, 21] },
  { name: "throughdoor", dump: "synth_wall-wood_door-air_open-1.json",
    from: [26, 3, 11], to: [10, 3, 11] },
  { name: "alongwall-shut", dump: "synth_wall-wood_door-wood_open-0.json",
    from: [21, 3, 3], to: [21, 3, 21] },
];

function samples(r) {
  const legs = [[r.from, r.to]];
  if (r.then) legs.push([r.to, r.then]);
  const pts = [];
  for (const [a, b] of legs) {
    const d = Math.hypot(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
    const n = Math.max(1, Math.round(d / STEP));
    for (let i = 0; i <= n; i++) {
      const u = i / n;
      pts.push([a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u]);
    }
  }
  return pts;
}

function measureRoute(r) {
  const D = loadDump(path.join(__dirname, "synth", r.dump));
  const em = D.emitters[0];
  const G = REGIONS ? Rg.asGraph(D, 0.62) : buildGraph(D, CELL);
  const nEval = makeNewEvaluator(D, G, em);
  const oEval = makeEvaluator(D, em);
  const pts = samples(r);
  const out = {};
  for (const [name, fn] of [["old", p => { const q = oEval(Math.round(p[0]), Math.round(p[1]), Math.round(p[2])); return q.att > 0 ? 20 * Math.log10(q.att) : -120; }],
                            ["new", p => nEval(p[0], p[1], p[2]).total]]) {
    const lv = pts.map(fn);
    let worst = 0, worstAt = 0, sum = 0, n = 0, over1 = 0, over3 = 0;
    for (let i = 1; i < lv.length; i++) {
      const dist = Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1], pts[i][2] - pts[i - 1][2]);
      if (dist < 1e-6) continue;
      const grad = Math.abs(lv[i] - lv[i - 1]) / dist;   // dB per block
      sum += grad; n++;
      if (grad > 1) over1++;
      if (grad > 3) over3++;
      if (grad > worst) { worst = grad; worstAt = i; }
    }
    out[name] = { worst, mean: sum / (n || 1), over1: 100 * over1 / (n || 1), over3: 100 * over3 / (n || 1),
                  at: pts[worstAt], lv };
  }
  return out;
}

function main() {
  const dir = path.join(__dirname, "synth");
  if (!fs.existsSync(dir)) { console.error("run: node synth.js"); process.exit(1); }
  console.log(`\nWORST SPATIAL GRADIENT along a route, dB per block. Sampled every ${STEP} block, cell=${CELL}.`);
  console.log(`A walk is ~1.5 blocks/s, so 1 dB/block is a gentle 1.5 dB/s; past ~3 dB/block reads as a step.\n`);
  console.log(`${"route".padEnd(16)}${"model".padEnd(6)}${"worst".padStart(8)}${"mean".padStart(8)}${">1".padStart(8)}${">3".padStart(8)}   worst at`);
  console.log("-".repeat(74));
  const bad = [];
  for (const r of ROUTES) {
    const m = measureRoute(r);
    for (const k of ["old", "new"]) {
      const v = m[k];
      const at = v.at.map(q => q.toFixed(0)).join(",");
      console.log(`${(k === "old" ? r.name : "").padEnd(16)}${k.padEnd(6)}${v.worst.toFixed(2).padStart(8)}${v.mean.toFixed(2).padStart(8)}${v.over1.toFixed(1).padStart(7)}%${v.over3.toFixed(1).padStart(7)}%   (${at})`);
    }
    if (m.new.worst > m.old.worst + 0.5) bad.push(`${r.name}: new ${m.new.worst.toFixed(2)} vs old ${m.old.worst.toFixed(2)} dB/block at (${m.new.at.map(q => q.toFixed(0)).join(",")})`);
    console.log();
  }
  if (bad.length) {
    console.log("REGRESSIONS vs the shipped model (this is the list to drive to empty):");
    for (const b of bad) console.log("  " + b);
  } else {
    console.log("No route where the new model is materially rougher than the shipped one.");
  }
  console.log();
}
main();
