"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");
const D = loadDump(path.join(__dirname, "dumps", "store_electronics_02.755_20260711_133916.json"));
const G = Rg.asGraph(D, 0.62);
const em = D.emitters[0];
const ev = makeNewEvaluator(D, G, em);
const y = em.y + 1;
console.log(`speaker ${em.x},${em.y},${em.z}`);
console.log(`   d |  total   free   OCCL |    air  solid   diff | room(listener)`);
const li = (x,yy,z) => (x-D.x0) + D.sx*((z-D.z0) + D.sz*(yy-D.y0));
let shown = 0;
for (let z = D.z0+2; z < D.z0+D.sz-2 && shown < 8; z += 3)
  for (let x = D.x0+2; x < D.x0+D.sx-2 && shown < 8; x += 3) {
    if (!D.isAir(x,y,z) || !D.isAir(x,y+1,z)) continue;
    if (G.open[li(x,y,z)] < 0.9) continue;
    const d = Math.hypot(x-em.x, y-em.y, z-em.z);
    if (d < 24 || d > 44) continue;
    const r = ev(x, y, z);
    const ff = -distDb(d, 0.5);
    const nid = G.nodeOf[li(x,y,z)];
    const rm = nid >= 0 ? G.rooms[G.nodes[nid].room] : null;
    console.log(`${d.toFixed(0).padStart(5)} |${r.total.toFixed(1).padStart(7)}${ff.toFixed(1).padStart(7)}`
      + `${(r.total-ff).toFixed(1).padStart(7)} |${r.airDb.toFixed(1).padStart(7)}${r.solidDb.toFixed(1).padStart(7)}`
      + `${r.diffDb.toFixed(1).padStart(7)} | ${rm ? `room ${G.nodes[nid].room} sky ${(100*rm.skyFrac).toFixed(0)}% n=${rm.n}` : "-"}`);
    shown++;
  }
