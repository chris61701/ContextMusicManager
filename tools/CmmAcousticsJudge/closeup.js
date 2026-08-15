// OCCLUSION standing next to a speaker in the open. Must be ~0 dB: nothing is in the way.
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");
const D = loadDump(path.join(__dirname, "synth", "synth_wall-wood_door-air_open-1.json"));
const em = D.emitters[0];
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, em);
console.log(` dist |  total  freefield  OCCLUSION`);
for (const dz of [0.5, 1, 1.5, 2, 3, 4, 6, 8, 12]) {
  const r = ev(em.x, em.y, em.z + dz);
  const ff = -distDb(dz, 0.5);
  console.log(`${dz.toFixed(1).padStart(5)} |${r.total.toFixed(2).padStart(7)}${ff.toFixed(2).padStart(10)}`
    + `${(r.total - ff).toFixed(2).padStart(11)} dB`);
}
