// Why pasthatch steps. The transmitted (through-the-floor) term is the one that ripples, so
// this prints THAT arrival's own numbers -- where it radiates from, how thick the barrier is
// there, and its level -- against listener z. If the radiator jumps, the defect is patch
// selection; if it sits still while the level steps, the defect is the level integral.
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");

const D = loadDump(path.join(__dirname, "synth", "synth_floor-wood_hatch-wood_open-0.json"));
const em = D.emitters[0];
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, em);

console.log(`emitter at ${em.x},${em.y},${em.z}  dims ${D.sx}x${D.sy}x${D.sz}`);
console.log(`   z |  solidDb   step |  radiates-from        thick | barrier-mid`);
let prev = null;
for (let z = 3; z <= 20; z += 0.25) {
  const r = ev(9, 8, z);
  const s = r.arrivals.find(a => a.isSrc);
  const db = s ? s.db : -120;
  const step = prev === null ? 0 : (db - prev) / 0.25;
  prev = db;
  const from = s ? `${s.px.toFixed(2)},${s.py.toFixed(2)},${s.pz.toFixed(2)}` : "-";
  const bm = r.bm ? `${r.bm.x.toFixed(2)},${r.bm.y.toFixed(2)},${r.bm.z.toFixed(2)} t=${r.bm.thick.toFixed(2)}` : "-";
  const mark = Math.abs(step) > 3 ? "  <<<" : "";
  console.log(`${z.toFixed(2).padStart(5)} |${db.toFixed(2).padStart(9)}${step.toFixed(2).padStart(7)} |  ${from.padEnd(20)} | ${bm}${mark}`);
}
