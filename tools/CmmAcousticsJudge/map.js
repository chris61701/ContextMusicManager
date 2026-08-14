// ───────────────────────────────────────────────────────────────────────────────
// MAP — draw the scenario, top down, from the actual dump
// ───────────────────────────────────────────────────────────────────────────────
// "I need to picture where I am." Every listening test so far has been described in prose and
// numbers, which is hopeless for geometry. A scenario is a shape, so draw the shape -- and draw it
// from the dump the model actually evaluates, not from memory, so the picture cannot drift from
// what is being rendered.
//
//   node map.js <scenario>      pastdoor | approach | swing | overhatch | overhatch-shut
//   node map.js <dump.json> [--y=3]
//
// Legend: # solid   (blank) air   S speaker   o an opening the model found
//         0-9 the listener's position at that many seconds along the walk, ^v<> the facing
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");

// The same paths render.js walks. Kept here deliberately: if these disagree with render.js the
// picture lies, so they are stated once in a form that is easy to eyeball against it.
const SCENARIOS = {
  pastdoor:        { dump: "synth_wall-wood_door-air_open-1.json", y: 3, secs: 16,
                     from: [23, 3, 3], to: [23, 3, 21], yaw: Math.PI,
                     note: "strafe along the wall past the OPEN doorway, facing the wall" },
  approach:        { dump: "synth_wall-wood_door-air_open-1.json", y: 3, secs: 20,
                     from: [40, 3, 20], via: [22, 3, 11], to: [10, 3, 11], yaw: Math.PI,
                     note: "far corner, in through the doorway, up to the speaker" },
  walkby:          { dump: "synth_wall-wood_door-air_open-1.json", y: 3, secs: 16,
                     from: [23, 3, 3], to: [23, 3, 21], yaw: Math.PI / 2,
                     note: "walk FORWARD past an open door on your LEFT (the natural version)" },
  swing:           { dump: "synth_wall-wood_door-wood_open-0.json", y: 3, secs: 18,
                     from: [26, 3, 11], to: [26, 3, 11], yaw: Math.PI,
                     note: "stand still on-axis while the door opens then shuts" },
  overhatch:       { dump: "synth_floor-wood_hatch-air_open-1.json", y: 8, secs: 16,
                     from: [9, 8, 3], to: [9, 8, 20], yaw: Math.PI,
                     note: "walk the storey ABOVE the speaker, across an open hatch" },
  "overhatch-shut": { dump: "synth_floor-wood_hatch-wood_open-0.json", y: 8, secs: 16,
                     from: [9, 8, 3], to: [9, 8, 20], yaw: Math.PI,
                     note: "the same walk with the hatch shut" },
};

function draw(D, y, opts) {
  const G = Rg.asGraph(D, 0.62);
  const w = D.sx, h = D.sz;
  const grid = [];
  for (let z = 0; z < h; z++) {
    const row = [];
    for (let x = 0; x < w; x++) {
      const wx = D.x0 + x, wz = D.z0 + z;
      row.push(D.inB(wx, y, wz) && G.open[D.idx(wx, y, wz)] > 0.5 ? " " : "#");
    }
    grid.push(row);
  }
  // openings the model found, at or near this height
  for (const A of G.apertures || []) {
    if (Math.abs(A.py - (y + 0.5)) > 2.5) continue;
    const x = Math.round(A.px) - D.x0, z = Math.round(A.pz) - D.z0;
    if (x >= 0 && x < w && z >= 0 && z < h) grid[z][x] = "o";
  }
  // speakers
  for (const em of D.emitters) {
    const x = em.x - D.x0, z = em.z - D.z0;
    if (x >= 0 && x < w && z >= 0 && z < h) grid[z][x] = "S";
  }
  // the walk, stamped every 2 s
  if (opts) {
    const legs = opts.via ? [[opts.from, opts.via], [opts.via, opts.to]] : [[opts.from, opts.to]];
    const totalLen = legs.reduce((s, [a, b]) => s + Math.hypot(b[0] - a[0], b[2] - a[2]), 0) || 1;
    for (let t = 0; t <= opts.secs; t += 2) {
      const u = t / opts.secs;
      let want = u * totalLen, p = null;
      for (const [a, b] of legs) {
        const d = Math.hypot(b[0] - a[0], b[2] - a[2]);
        if (want <= d || legs.indexOf([a, b]) === legs.length - 1) {
          const f = d < 1e-6 ? 0 : Math.min(1, want / d);
          p = [a[0] + (b[0] - a[0]) * f, a[2] + (b[2] - a[2]) * f];
          break;
        }
        want -= d;
      }
      if (!p) continue;
      const x = Math.round(p[0]) - D.x0, z = Math.round(p[1]) - D.z0;
      if (x >= 0 && x < w && z >= 0 && z < h) grid[z][x] = String((t / 2) % 10);
    }
  }
  return grid;
}

function main() {
  const a0 = process.argv[2];
  if (!a0) { console.error("usage: node map.js <scenario|dump.json> [--y=N]"); process.exit(1); }
  const sc = SCENARIOS[a0];
  const file = sc ? path.join(__dirname, "synth", sc.dump) : a0;
  const yArg = (process.argv.find(a => a.startsWith("--y=")) || "").split("=")[1];
  const D = loadDump(file);
  const y = yArg !== undefined ? +yArg : (sc ? sc.y : D.emitters[0].y);
  const grid = draw(D, y, sc);

  console.log(`\n${a0}${sc ? "  —  " + sc.note : ""}`);
  console.log(`top-down slice at y=${y}   (${D.sx} x ${D.sz} blocks)\n`);
  const lbl = z => String(D.z0 + z).padStart(4);
  // x ruler
  let ruler = "     ";
  for (let x = 0; x < D.sx; x++) ruler += ((D.x0 + x) % 10 === 0 ? "|" : " ");
  console.log(ruler + "   <- x grows right");
  for (let z = 0; z < D.sz; z++) console.log(lbl(z) + " " + grid[z].join(""));
  console.log(`\n     ^ z grows DOWN this page`);
  console.log(`\nlegend:  # solid    S speaker    o opening the model found`);
  console.log(`         0-9 = you, every 2 seconds of the walk (0 = start)`);
  if (sc) {
    // Facing and right-ear are computed from THE SAME expressions render.js uses, not from an ad hoc
    // rule. The ad hoc version was right for one scenario and wrong for the next -- it announced the
    // right ear pointing +x where the renderer has it at -x -- and a picture that disagrees with the
    // audio is worse than no picture at all.
    //     fwd   = (cos yaw, sin yaw)   as [x, z]
    //     right = (-sin yaw, cos yaw)
    const name = (dx, dz) => Math.abs(dx) > Math.abs(dz)
      ? (dx < 0 ? "LEFT (-x)" : "RIGHT (+x)")
      : (dz < 0 ? "UP the page (-z)" : "DOWN the page (+z)");
    const fx = Math.cos(sc.yaw), fz = Math.sin(sc.yaw);
    const rx = -fz, rz = fx;
    console.log(`\nyou FACE ${name(fx, fz)} for the whole walk, so your RIGHT ear points ${name(rx, rz)}.`);
    const legs = sc.via ? [[sc.from, sc.via], [sc.via, sc.to]] : [[sc.from, sc.to]];
    const em = D.emitters[0];
    const side = p => {
      const dx = em.x + 0.5 - p[0], dz = em.z + 0.5 - p[2];
      const lat = (dx * rx + dz * rz) / (Math.hypot(dx, dz) || 1);
      return lat < -0.25 ? "your LEFT" : lat > 0.25 ? "your RIGHT" : "straight ahead or behind";
    };
    console.log(`the speaker is ${side(legs[0][0])} at the start and `
              + `${side(legs[legs.length - 1][1])} at the end.`);
  }
  console.log();
}
main();
