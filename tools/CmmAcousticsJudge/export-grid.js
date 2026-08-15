// ───────────────────────────────────────────────────────────────────────────────
// EXPORT-GRID — hand the native port the same voxels the JS model sees
// ───────────────────────────────────────────────────────────────────────────────
// The port has to be checked against the JS model, not hoped about. Everything that made the offline
// work reliable came from measuring one thing against another, and the single most expensive bug of
// this whole effort was two systems being compared when they were not the same system: the renderer
// evaluated the cell grid for days while every gate ran the region graph.
//
// So the port gets a parity harness before it gets features. This writes the raw voxel grid to a flat
// binary the C side can read with fread and no JSON parser, and every stage of the port is then
// diffable against the JS stage by stage rather than judged at the end.
//
// Deliberately exports LOSS and the AIR FLAG rather than derived openness: the derivation itself is
// part of what must match, so it has to be computed independently on both sides.
//
//   node export-grid.js dumps/<capture>.json out.bin
//   node export-grid.js --all               writes native/grids/<name>.bin for every valid capture
//
// Format (little-endian, which is both platforms here):
//   char[8]  "CMMAC1\0"
//   int32    sx, sy, sz, x0, y0, z0
//   float32  loss[sx*sy*sz]     per-voxel CellLoss, 0..1+
//   uint8    flags[sx*sy*sz]    bit 0 = AIR
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const { checkDump } = require("./dumpcheck.js");

function writeGrid(dumpFile, outFile) {
  const D = loadDump(dumpFile);
  const n = D.n;
  const head = Buffer.alloc(8 + 6 * 4);
  head.write("CMMAC1\0", 0, "ascii");
  head.writeInt32LE(D.sx, 8);
  head.writeInt32LE(D.sy, 12);
  head.writeInt32LE(D.sz, 16);
  head.writeInt32LE(D.x0, 20);
  head.writeInt32LE(D.y0, 24);
  head.writeInt32LE(D.z0, 28);

  const loss = Buffer.alloc(n * 4);
  const flags = Buffer.alloc(n);
  for (let i = 0; i < n; i++) {
    loss.writeFloatLE(D.loss[i], i * 4);
    flags[i] = D.flags[i] & 1;      // AIR only; the rest is unused by the acoustic model
  }
  fs.writeFileSync(outFile, Buffer.concat([head, loss, flags]));

  // emitters alongside, as text — few enough that a parser is not worth writing in C
  const em = (D.emitters || []).map(e => `${e.x} ${e.y} ${e.z}`).join("\n");
  fs.writeFileSync(outFile.replace(/\.bin$/, ".emitters.txt"), em + "\n");
  return { n, bytes: head.length + loss.length + flags.length, emitters: (D.emitters || []).length };
}

function main() {
  if (process.argv.includes("--all")) {
    const dir = path.join(__dirname, "dumps");
    const out = path.join(__dirname, "..", "..", "CMM_Decoder", "grids");
    fs.mkdirSync(out, { recursive: true });
    for (const f of fs.readdirSync(dir).filter(x => x.endsWith(".json"))) {
      const full = path.join(dir, f);
      if (!checkDump(full).ok) { console.log(`skipped (invalid capture): ${f}`); continue; }
      const name = f.split("_2026")[0];
      const r = writeGrid(full, path.join(out, name + ".bin"));
      console.log(`${name.padEnd(26)} ${r.n} voxels, ${(r.bytes / 1e6).toFixed(1)} MB, ${r.emitters} emitters`);
    }
    // and the synthetic geometry, which is where a defect is cheapest to find
    const sdir = path.join(__dirname, "synth");
    if (fs.existsSync(sdir))
      for (const f of ["synth_wall-wood_door-air_open-1.json", "synth_wall-wood_door-wood_open-0.json",
                       "synth_floor-wood_hatch-wood_open-0.json"]) {
        if (!fs.existsSync(path.join(sdir, f))) continue;
        const r = writeGrid(path.join(sdir, f), path.join(out, f.replace(/\.json$/, ".bin")));
        console.log(`${f.slice(0, 26).padEnd(26)} ${r.n} voxels, ${(r.bytes / 1e6).toFixed(1)} MB`);
      }
    return;
  }
  const [inF, outF] = process.argv.slice(2).filter(a => !a.startsWith("--"));
  if (!inF || !outF) { console.error("usage: node export-grid.js <dump.json> <out.bin>   |   --all"); process.exit(1); }
  const r = writeGrid(inF, outF);
  console.log(`${r.n} voxels, ${(r.bytes / 1e6).toFixed(1)} MB, ${r.emitters} emitters`);
}
main();
