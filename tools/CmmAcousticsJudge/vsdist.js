// OCCLUSION vs DISTANCE over every standable point of a real capture. Occlusion that trends
// POSITIVE with range means the model is saying "louder than free field", which the bridge can
// only publish as NO occlusion -- so a speaker behind a wall stops being occluded at all once
// you walk far enough away. That is "muffling is inconsistent; I can hear it when I'm very far
// away and it's behind a wall".
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");

for (const f of ["bunker_00.100_20260711_133928.json", "store_electronics_02.755_20260711_133916.json"]) {
  const D = loadDump(path.join(__dirname, "dumps", f));
  const G = Rg.asGraph(D, 0.62);
  const em = D.emitters[0];
  const ev = makeNewEvaluator(D, G, em);
  const y = em.y + 1;
  const bins = new Map();
  for (let z = D.z0 + 2; z < D.z0 + D.sz - 2; z++)
    for (let x = D.x0 + 2; x < D.x0 + D.sx - 2; x++) {
      if (!D.isAir(x, y, z) || !D.isAir(x, y + 1, z)) continue;
      const k = (x - D.x0) + D.sx * ((z - D.z0) + D.sz * (y - D.y0));
      if (G.open[k] < 0.9) continue;
      const d = Math.hypot(x - em.x, y - em.y, z - em.z);
      if (d < 2 || d > 60) continue;
      const b = Math.floor(d / 8) * 8;
      const occl = ev(x, y, z).total - (-distDb(d, 0.5));
      let e = bins.get(b); if (!e) { e = { n: 0, s: 0, clamped: 0 }; bins.set(b, e); }
      e.n++; e.s += occl; if (occl >= 0) e.clamped++;
    }
  console.log(`\n${f.slice(0, 30)}  (speaker ${em.x},${em.y},${em.z})`);
  console.log(`  dist band   n   mean OCCL   % with NO occlusion possible`);
  for (const b of [...bins.keys()].sort((a, c) => a - c))
    { const e = bins.get(b);
      console.log(`    ${String(b).padStart(2)}-${String(b+8).padStart(2)} ${String(e.n).padStart(6)}`
        + `${(e.s/e.n).toFixed(1).padStart(11)} dB ${(100*e.clamped/e.n).toFixed(0).padStart(10)}%`); }
}
