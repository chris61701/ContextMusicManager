// Two adjacent listener positions across the pasthatch step; the fan's patches, side by side.
"use strict";
const path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");
const D = loadDump(path.join(__dirname, "synth", "synth_floor-wood_hatch-air_open-1.json"));
const G = Rg.asGraph(D, 0.62);
const ev = makeNewEvaluator(D, G, D.emitters[0]);
const z = +process.argv[2];
console.error(`--- listener 9,8,${z} ---`);
const r = ev(9, 8, z);
console.log(`z=${z}  total=${r.total.toFixed(3)}  solid=${(r.arrivals.find(a=>a.isSrc)||{db:-120}).db.toFixed(3)}`);
