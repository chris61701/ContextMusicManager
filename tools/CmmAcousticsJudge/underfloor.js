// Directly BELOW a speaker through a shut wooden floor. Mirrors "I was standing under the room
// where the speaker was", where the game reported only 3.4 dB of occlusion.
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");
const D = loadDump(path.join(__dirname, "synth", "synth_floor-wood_hatch-wood_open-0.json"));
const em = D.emitters[0];
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, em);
const li = (x,y,z)=>(x-D.x0)+D.sx*((z-D.z0)+D.sz*(y-D.y0));
console.log(`speaker ${em.x},${em.y},${em.z}   (floor above it; listener below)`);
console.log(` dy |  total   free   OCCL |  air   solid | openness of the column`);
for (let dy = 1; dy <= 6; dy++) {
  const y = em.y + dy;
  if (!D.inB(em.x, y, em.z)) break;
  const r = ev(em.x, y, em.z);
  const d = dy;
  const ff = -distDb(d, 0.5);
  let col = "";
  for (let k = 0; k <= dy; k++) col += G.open[li(em.x, em.y + k, em.z)].toFixed(1) + " ";
  console.log(`${String(dy).padStart(3)} |${r.total.toFixed(1).padStart(7)}${ff.toFixed(1).padStart(7)}`
    + `${(r.total - ff).toFixed(1).padStart(7)} |${r.airDb.toFixed(1).padStart(7)}${r.solidDb.toFixed(1).padStart(7)} | ${col}`);
}
