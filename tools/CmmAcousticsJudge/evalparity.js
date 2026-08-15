// Diff the native EVAL lattice against the JS model, point for point. The port is only as
// good as this number: the model is the JS one, and native is a reimplementation of it.
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");
const D = loadDump(process.argv[2]);
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, D.emitters[0]);
let n = 0, sum = 0, worst = 0, worstAt = null;
for (const line of fs.readFileSync(process.argv[3], "utf8").split(/\r?\n/)) {
  const m = line.match(/^EVAL (-?\d+) (-?\d+) (-?\d+) (-?[\d.]+) (\d+)/);
  if (!m) continue;
  const [x, y, z] = [+m[1], +m[2], +m[3]], nat = +m[4];
  const js = ev(x, y, z).total;
  const d = Math.abs(nat - js);
  n++; sum += d;
  if (d > worst) { worst = d; worstAt = [x, y, z, nat, js]; }
}
console.log(`${path.basename(process.argv[2]).slice(0,26).padEnd(28)} ${String(n).padStart(5)} pts  `
  + `mean ${(sum/Math.max(1,n)).toFixed(4)} dB  worst ${worst.toFixed(4)} dB`
  + (worst > 0.2 ? `  at ${worstAt}` : ""));
