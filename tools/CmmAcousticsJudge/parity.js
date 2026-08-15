// ───────────────────────────────────────────────────────────────────────────────
// PARITY — the JS half of the native port's acceptance check
// ───────────────────────────────────────────────────────────────────────────────
// Prints, for one capture, exactly the aggregate figures CMM_Decoder/test_acoustics.exe
// prints for the same voxels, so a divergence between the model and its port is a
// number rather than a mystery discovered three stages later.
//
// This exists because of the most expensive mistake of the offline work: for days the
// renderer evaluated the cell grid while every gate ran the region graph, so a great
// deal of green measurement described a system nobody was listening to. A port is that
// hazard in its purest form -- two implementations that are supposed to agree -- so it
// gets a diff at every stage rather than a judgement at the end.
//
//   node parity.js dumps/<capture>.json
//   node parity.js --all
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const P = require("./portals.js");
const { checkDump } = require("./dumpcheck.js");

const THICK_CAP = 6.0, OPEN_EPS = 0.02;

function stats(file) {
  const D = loadDump(file);
  const open = P.makeOpenTest(D);
  const thick = Rg.thickness(D, open);
  let o = 0, full = 0, cap = 0, s = 0;
  for (let i = 0; i < D.n; i++) {
    if (open[i] > OPEN_EPS) { o++; s += thick[i]; if (thick[i] >= THICK_CAP) cap++; }
    if (open[i] >= 0.999) full++;
  }
  return { n: D.n, open: o, full, cap, mean: o ? s / o : 0 };
}

function report(label, file) {
  const r = stats(file);
  console.log(`${label.padEnd(28)} open=${String(r.open).padStart(8)}  fullyOpen=${String(r.full).padStart(8)}`
            + `  atCap=${String(r.cap).padStart(8)}  meanThick=${r.mean.toFixed(4)}`);
}

function main() {
  if (process.argv.includes("--all")) {
    const dir = path.join(__dirname, "dumps");
    for (const f of fs.readdirSync(dir).filter(x => x.endsWith(".json"))) {
      const full = path.join(dir, f);
      if (!checkDump(full).ok) continue;
      report(f.split("_2026")[0], full);
    }
    const sdir = path.join(__dirname, "synth");
    for (const f of ["synth_wall-wood_door-air_open-1.json", "synth_wall-wood_door-wood_open-0.json",
                     "synth_floor-wood_hatch-wood_open-0.json"]) {
      if (fs.existsSync(path.join(sdir, f))) report(f.replace(/\.json$/, ""), path.join(sdir, f));
    }
    return;
  }
  const f = process.argv[2];
  if (!f) { console.error("usage: node parity.js <dump.json> | --all"); process.exit(1); }
  report(path.basename(f).split("_2026")[0], f);
}
main();
