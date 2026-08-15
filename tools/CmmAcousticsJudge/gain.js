// PASSIVITY AT THE LISTENER. A speaker cannot make you hear more energy than it emits, so a
// total above 0 dB is not a tuning question, it is the model reporting gain. Nothing checked
// this: the diffuse-passivity gate tests one TERM against its room, never the final sum.
//
// It matters because the bridge clamps: above unity there is no "extra distance" that
// reproduces it, so the point publishes as fully unoccluded. Every voxel over 0 dB is a voxel
// where occlusion is switched off entirely and no material constant can reach.
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");
const bad = require("./dumpcheck.js");

const dir = path.join(__dirname, "dumps");
for (const f of fs.readdirSync(dir).filter(f => f.endsWith(".json"))) {
  const D = loadDump(path.join(dir, f));
  if (bad.isInvalid && bad.isInvalid(D)) { console.log(`${f.slice(0,28).padEnd(30)} skipped (invalid)`); continue; }
  const G = Rg.asGraph(D, 0.62);
  const evs = D.emitters.map(em => makeNewEvaluator(D, G, em));
  let n = 0, over = 0, worst = -999, worstAt = null;
  const y = D.emitters[0].y + 1;
  for (let z = D.z0 + 2; z < D.z0 + D.sz - 2; z += 2)
  for (let x = D.x0 + 2; x < D.x0 + D.sx - 2; x += 2) {
    const i = (x - D.x0) + D.sx * ((z - D.z0) + D.sz * (y - D.y0));
    if (G.open[i] <= 0.02) continue;
    let p = 0;
    for (const e of evs) p += Math.pow(10, e(x, y, z).total / 10);
    const t = p > 1e-12 ? 10 * Math.log10(p) : -120;
    n++;
    if (t > 0) over++;
    if (t > worst) { worst = t; worstAt = [x, y, z]; }
  }
  console.log(`${f.slice(0,28).padEnd(30)} ${String(n).padStart(6)} pts  `
    + `${(100*over/Math.max(1,n)).toFixed(1).padStart(5)}% over 0 dB   worst ${worst.toFixed(1)} dB at ${worstAt}`);
}
