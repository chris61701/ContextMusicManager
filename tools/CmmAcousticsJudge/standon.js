// OCCLUSION standing next to each speaker in a REAL capture.
//
// Every close-range measurement so far came from a synthetic scene where a speaker stands in an
// empty room. A real radio sits on a shelf, against a wall, under a ceiling -- and the
// transmission disc samples three blocks in every direction from the source, so geometry BESIDE
// the speaker can be read as occlusion BETWEEN it and the listener. That is exactly the case a
// scene of my own construction cannot contain, and it is where the report lives:
// "it gets muffled when I'm closer to it, especially while right on top of it".
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator, distDb } = require("./portals.js");

for (const f of fs.readdirSync(path.join(__dirname, "dumps")).filter(f => f.endsWith(".json"))) {
  const D = loadDump(path.join(__dirname, "dumps", f));
  const G = Rg.asGraph(D, 0.62);
  console.log(`\n${f.slice(0, 34)}`);
  console.log(`  speaker           listen at    d    model   free   OCCL`);
  for (let i = 0; i < Math.min(6, D.emitters.length); i++) {
    const em = D.emitters[i];
    const ev = makeNewEvaluator(D, G, em);
    // nearest standable air next to the speaker -- where a player would be
    let best = null;
    for (let dy = 0; dy <= 1; dy++)
      for (let dz = -2; dz <= 2; dz++)
        for (let dx = -2; dx <= 2; dx++) {
          const x = em.x + dx, y = em.y + dy, z = em.z + dz;
          if (!D.isAir(x, y, z) || !D.isAir(x, y + 1, z)) continue;
          const d = Math.hypot(dx, dy, dz);
          if (d < 0.9) continue;
          if (!best || d < best.d) best = { x, y, z, d };
        }
    if (!best) { console.log(`  ${em.x},${em.y},${em.z}  no standable neighbour`); continue; }
    const r = ev(best.x, best.y, best.z);
    const ff = -distDb(best.d, 0.5);
    const occl = r.total - ff;
    const mark = occl < -3 ? "   <<< muffled with nothing in the way" : "";
    console.log(`  ${(em.x+","+em.y+","+em.z).padEnd(18)}`
      + `${(best.x+","+best.y+","+best.z).padEnd(13)}${best.d.toFixed(1).padStart(4)}`
      + `${r.total.toFixed(1).padStart(8)}${ff.toFixed(1).padStart(7)}${occl.toFixed(1).padStart(7)}${mark}`);
  }
}
