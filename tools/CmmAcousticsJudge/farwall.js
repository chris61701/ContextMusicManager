"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");
const D = loadDump(path.join(__dirname, "synth", "synth_wall-wood_door-wood_open-0.json"));
const em = D.emitters[0];
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, em);
console.log(`   x |  total   free   OCCL |  solid   diff | dominant`);
for (let x = 12; x <= 40; x += 2) {
  const r = ev(x, 3, 11);
  const d = Math.hypot(x - em.x, 0, 0);
  const ff = -distDb(d, 0.5);
  console.log(`${String(x).padStart(5)} |${r.total.toFixed(1).padStart(7)}${ff.toFixed(1).padStart(7)}`
    + `${(r.total - ff).toFixed(1).padStart(7)} |${r.solidDb.toFixed(1).padStart(7)}${r.diffDb.toFixed(1).padStart(7)}`
    + `  ${r.diffDb > r.solidDb ? "DIFFUSE" : "through-wall"}`);
}
