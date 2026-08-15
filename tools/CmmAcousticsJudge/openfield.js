// A SPEAKER IN AN OPEN FIELD. Nothing between it and the listener at any distance, so occlusion
// must be zero everywhere. The disc is a circle PERPENDICULAR to the source-listener line; for a
// listener on the same level that circle is vertical, and its lower half is inside the GROUND.
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");
const D = loadDump(path.join(__dirname, "dumps", "bunker_00.100_20260711_133928.json"));
const G = Rg.asGraph(D, 0.62);
// the rooftop speaker: 2123,42,1028 sits high, with open sky around it
const em = D.emitters.find(e => e.y > 40) || D.emitters[0];
const ev = makeNewEvaluator(D, G, em);
const li = (x,y,z) => (x-D.x0) + D.sx*((z-D.z0) + D.sz*(y-D.y0));
console.log(`open-air speaker ${em.x},${em.y},${em.z}`);
console.log(`   d |  total   free   OCCL | arrivals`);
for (const dz of [1,2,3,5,8,12,18,25,35]) {
  const x = em.x, y = em.y, z = em.z - dz;
  if (!D.inB(x,y,z) || !D.isAir(x,y,z)) { console.log(`${String(dz).padStart(5)} | not air`); continue; }
  const r = ev(x, y, z);
  const ff = -distDb(dz, 0.5);
  const kinds = (r.arrivals||[]).map(a => (a.isSrc?"src":a.isDiffuse?"diff":"ap") + " " + a.db.toFixed(0)).join("  ");
  console.log(`${String(dz).padStart(5)} |${r.total.toFixed(1).padStart(7)}${ff.toFixed(1).padStart(7)}`
    + `${(r.total-ff).toFixed(1).padStart(7)} | ${kinds}`);
}
