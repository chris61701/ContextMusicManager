// SMOOTHNESS OF THE OCCLUSION, NOT OF THE LEVEL.
//
// The 3 dB/block criterion was written against a model whose distance law was nearly flat, so
// every gradient it measured was geometry. With a physical inverse-square law that is no longer
// true: 6 dB per doubling means the near field is legitimately steep -- one block to two is 6
// dB/block -- and nobody has ever complained that a speaker gets louder as they walk up to it.
//
// The bridge now transports only the RATIO against free field, and the game's own curve supplies
// distance. So the quantity that must be smooth is the ratio. Measuring the absolute level
// measures the distance law and calls physics a defect.
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");

for (const f of fs.readdirSync(path.join(__dirname, "dumps")).filter(f => f.endsWith(".json"))) {
  const D = loadDump(path.join(__dirname, "dumps", f));
  const G = Rg.asGraph(D, 0.62);
  const em = D.emitters[0];
  const ev = makeNewEvaluator(D, G, em);
  const y = em.y + 1;
  const occl = (x, z) => {
    const d = Math.hypot(x - em.x, y - em.y, z - em.z);
    return ev(x, y, z).total - (-distDb(Math.max(1, d)));   // level minus free field = occlusion
  };
  let n = 0, worstL = 0, worstO = 0;
  for (let z = D.z0 + 2; z < D.z0 + D.sz - 2; z++)
    for (let x = D.x0 + 2; x < D.x0 + D.sx - 2; x++) {
      const i = (x - D.x0) + D.sx * ((z - D.z0) + D.sz * (y - D.y0));
      const j = (x + 1 - D.x0) + D.sx * ((z - D.z0) + D.sz * (y - D.y0));
      if (G.open[i] <= 0.02 || G.open[j] <= 0.02) continue;
      const lv = Math.abs(ev(x + 1, y, z).total - ev(x, y, z).total);
      const oc = Math.abs(occl(x + 1, z) - occl(x, z));
      n++;
      if (lv > worstL) worstL = lv;
      if (oc > worstO) worstO = oc;
    }
  console.log(`${f.slice(0,26).padEnd(28)} ${String(n).padStart(6)} pairs   `
    + `worst LEVEL ${worstL.toFixed(2).padStart(6)}   worst OCCLUSION ${worstO.toFixed(2).padStart(6)} dB/block`);
}
