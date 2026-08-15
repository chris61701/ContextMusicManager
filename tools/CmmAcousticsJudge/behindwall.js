// "On the other side of a wall it sounds like there's barely anything between me and that
// wall" (author, in game, first listen of the native model).
//
// A wall that does not block is one of two things: the transmitted path is too loud, or
// something is arriving that never crossed the wall at all. This prints the split, standing
// at increasing distance behind a SHUT wall, so the answer is a number rather than a guess.
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");

const dump = process.argv[2] || "synth_wall-wood_door-wood_open-0.json";
const D = loadDump(path.join(__dirname, "synth", dump));
const em = D.emitters[0];
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, em);

const pow = db => Math.pow(10, db / 10);
console.log(`${dump}\nemitter ${em.x},${em.y},${em.z}   (wall runs across x)\n`);
console.log(`  x |   total |    air   solid   diff |  share of total: air  solid   diff`);
for (let x = 12; x <= 40; x += 2) {
  const r = ev(x, 3, 11);
  const t = pow(r.total);
  const a = pow(r.airDb), s = pow(r.solidDb), df = pow(r.diffDb);
  const pc = v => (100 * v / t).toFixed(0).padStart(5) + "%";
  console.log(`${String(x).padStart(4)} |${r.total.toFixed(2).padStart(8)} |`
    + `${r.airDb.toFixed(1).padStart(7)}${r.solidDb.toFixed(1).padStart(8)}${r.diffDb.toFixed(1).padStart(7)} |`
    + `           ${pc(a)}  ${pc(s)}  ${pc(df)}`);
}
