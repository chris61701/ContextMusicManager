// What does the model say the DIRECT sound is, standing near a speaker in the open?
// If it is quiet, every knob behaves as reported: muffling a wall changes nothing (you were
// never hearing through the wall) and removing reverb makes everything distant (the reverb WAS
// the level).
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");
const D = loadDump(path.join(__dirname, "synth", "synth_wall-wood_door-air_open-1.json"));
const em = D.emitters[0];
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, em);
console.log(`speaker at ${em.x},${em.y},${em.z}; walking away on the SAME side of the wall`);
console.log(`  dist |   total |  direct/solid    air   diffuse`);
for (let dz = 1; dz <= 8; dz++) {
  const r = ev(em.x, em.y, em.z + dz);
  console.log(`${String(dz).padStart(6)} |${r.total.toFixed(2).padStart(8)} |`
    + `${r.solidDb.toFixed(1).padStart(11)}${r.airDb.toFixed(1).padStart(9)}${r.diffDb.toFixed(1).padStart(9)}`);
}
