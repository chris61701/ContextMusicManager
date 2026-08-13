// ───────────────────────────────────────────────────────────────────────────────
// REGION SEGMENTATION — rooms and doorways found from the geometry, no grid
// ───────────────────────────────────────────────────────────────────────────────
// Bottom-up replacement for the cell grid (author's suggestion, and it is better than the
// top-down subdivision I was proposing). Top-down keeps the grid: split a cell into eight and
// the boundaries are still at arbitrary positions, so a doorway can still straddle one. Every
// hard problem this session came from exactly that -- a wall sitting INSIDE a cell, a throat
// measured RELATIVE TO a cell, discontinuities AT cell boundaries.
//
// Bottom-up throws the grid away. Start at voxel resolution and merge outward while the air
// stays wide; stop where it pinches. The boundaries then land on the geometry, and a boundary
// between two regions IS an aperture -- by construction, not by a heuristic.
//
// Method: a watershed on the "openness depth" field.
//   1. THICKNESS. For every open voxel, the distance to the nearest non-open voxel. Large in
//      the middle of a room, small in a doorway, smallest in a crack. This is the quantity a
//      constriction is defined by.
//   2. SEED at local maxima of thickness -- the centres of rooms.
//   3. FLOOD outward from seeds in DECREASING thickness order, so each region grows from its
//      middle towards its edges. Where two regions meet, that surface is a constriction: it is
//      the last place reached, hence the thinnest point between two wide spaces. That is a
//      doorway.
//   4. MERGE regions whose shared boundary is not actually a pinch -- if the boundary is nearly
//      as thick as the thinner region's core, the two are one space that happened to have two
//      local maxima. One parameter, and a physical one.
//
// Output shape is deliberately the same as buildGraph's so the rest of the model can consume
// it: nodes (regions) with a position and a throat, portals (boundaries) with an area and a
// centroid, and apertures.
//
// Usage:  node regions.js <dump.json> [--merge=0.6]
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const { loadDump } = require("./judge3d.js");
const { makeOpenTest } = require("./portals.js");

const OPEN_EPS = 0.02;

// ── 1. thickness: chamfer distance from every open voxel to the nearest closed one ──
// Two sweeps of a 3x3x3 chamfer give a good enough Euclidean approximation and are O(n).
function thickness(D, open) {
  const n = D.n, sx = D.sx, sy = D.sy, sz = D.sz;
  const li = (x, y, z) => x + sx * (z + sz * y);
  const t = new Float32Array(n);
  const BIG = 1e9;
  for (let i = 0; i < n; i++) t[i] = open[i] > OPEN_EPS ? BIG : 0;
  // neighbour offsets with distances, split into "already visited" halves per sweep direction
  const nb = [];
  for (let dz = -1; dz <= 1; dz++) for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
    if (!dx && !dy && !dz) continue;
    nb.push([dx, dy, dz, Math.hypot(dx, dy, dz)]);
  }
  const fwd = nb.filter(([dx, dy, dz]) => (dy < 0) || (dy === 0 && dz < 0) || (dy === 0 && dz === 0 && dx < 0));
  const bwd = nb.filter(o => !fwd.includes(o));
  const sweep = (order, list) => {
    for (const [x, y, z] of order) {
      const i = li(x, y, z);
      if (t[i] === 0) continue;
      let best = t[i];
      for (const [dx, dy, dz, w] of list) {
        const nx = x + dx, ny = y + dy, nz = z + dz;
        if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) { best = Math.min(best, w); continue; }
        const v = t[li(nx, ny, nz)] + w;
        if (v < best) best = v;
      }
      t[i] = best;
    }
  };
  const fwdOrder = [], bwdOrder = [];
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) fwdOrder.push([x, y, z]);
  for (let i = fwdOrder.length - 1; i >= 0; i--) bwdOrder.push(fwdOrder[i]);
  sweep(fwdOrder, fwd);
  sweep(bwdOrder, bwd);
  return t;
}

// ── 2/3. watershed: seed at local maxima, flood in decreasing thickness ──────────
function segment(D, open, thick) {
  const n = D.n, sx = D.sx, sy = D.sy, sz = D.sz;
  const li = (x, y, z) => x + sx * (z + sz * y);
  const label = new Int32Array(n).fill(-1);
  // voxels sorted by DECREASING thickness -- bucket sort on 0.5-block bins is plenty
  const order = [];
  for (let i = 0; i < n; i++) if (open[i] > OPEN_EPS) order.push(i);
  order.sort((a, b) => thick[b] - thick[a]);

  const regions = [];
  const nb6 = [[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]];
  for (const i of order) {
    const y = Math.floor(i / (sx * sz)), rem = i - y * sx * sz;
    const z = Math.floor(rem / sx), x = rem - z * sx;
    // labels already assigned among 6-neighbours
    let seen = -1, conflict = false;
    for (const [dx, dy, dz] of nb6) {
      const nx = x + dx, ny = y + dy, nz = z + dz;
      if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;
      const l = label[li(nx, ny, nz)];
      if (l < 0) continue;
      if (seen < 0) seen = l; else if (l !== seen) conflict = true;
    }
    if (seen < 0) {
      // no labelled neighbour: this is a local maximum, so a new room centre
      const id = regions.length;
      regions.push({ id, n: 0, sx: 0, sy: 0, sz: 0, core: thick[i], edges: new Map() });
      label[i] = id;
    } else {
      // join the (thickest-neighbour) region; a conflict means we are ON a boundary, and the
      // boundary voxel still has to belong somewhere -- give it to the first, and record the
      // contact below when portals are built.
      label[i] = seen;
    }
    const R = regions[label[i]];
    R.n++; R.sx += x; R.sy += y; R.sz += z;
    if (thick[i] < (R.minT === undefined ? Infinity : R.minT)) R.minT = thick[i];
  }
  return { label, regions };
}

// ── portals: contact area between differently-labelled regions ──────────────────
function contacts(D, open, label, regions, thick) {
  const sx = D.sx, sy = D.sy, sz = D.sz;
  const li = (x, y, z) => x + sx * (z + sz * y);
  const map = new Map();
  const add = (a, b, wx, wy, wz, aw, th) => {
    const lo = Math.min(a, b), hi = Math.max(a, b), k = lo * 1e7 + hi;
    let p = map.get(k);
    if (!p) { p = { a: lo, b: hi, area: 0, sx: 0, sy: 0, sz: 0, thick: 0 }; map.set(k, p); }
    p.area += aw; p.sx += wx * aw; p.sy += wy * aw; p.sz += wz * aw;
    if (th > p.thick) p.thick = th;
  };
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
    const i = li(x, y, z);
    if (open[i] <= OPEN_EPS) continue;
    const a = label[i];
    const chk = (nx, ny, nz, cx, cy, cz) => {
      if (nx >= sx || ny >= sy || nz >= sz) return;
      const j = li(nx, ny, nz);
      if (open[j] <= OPEN_EPS) return;
      const b = label[j];
      if (b === a) return;
      add(a, b, D.x0 + cx, D.y0 + cy, D.z0 + cz, Math.min(open[i], open[j]), Math.min(thick[i], thick[j]));
    };
    chk(x + 1, y, z, x + 1, y + 0.5, z + 0.5);
    chk(x, y + 1, z, x + 0.5, y + 1, z + 0.5);
    chk(x, y, z + 1, x + 0.5, y + 0.5, z + 1);
  }
  return [...map.values()].map(p => ({
    a: p.a, b: p.b, area: p.area, thick: p.thick,
    px: p.sx / p.area, py: p.sy / p.area, pz: p.sz / p.area,
  }));
}

function build(D, mergeRatio = 0.6) {
  const open = makeOpenTest(D);
  const t0 = Date.now();
  const thick = thickness(D, open);
  const tThick = Date.now() - t0;
  const t1 = Date.now();
  const { label, regions } = segment(D, open, thick);
  const tSeg = Date.now() - t1;
  const t2 = Date.now();
  let ports = contacts(D, open, label, regions, thick);
  const tPort = Date.now() - t2;

  // ── 4. merge regions whose shared boundary is not really a pinch ───────────────
  // If the boundary is nearly as thick as the thinner region's core, these are one space that
  // happened to have two local maxima -- an artifact of the watershed, not a doorway.
  const parent = regions.map(r => r.id);
  const find = a => { while (parent[a] !== a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };
  let merged = 0;
  ports.sort((p, q) => q.thick - p.thick);
  for (const p of ports) {
    const ra = find(p.a), rb = find(p.b);
    if (ra === rb) continue;
    const core = Math.min(regions[ra].core, regions[rb].core);
    if (p.thick >= mergeRatio * core) { parent[rb] = ra; merged++; }
  }
  const remap = new Map();
  for (const r of regions) { const root = find(r.id); if (!remap.has(root)) remap.set(root, remap.size); }
  const kept = remap.size;
  const finalPorts = [];
  const seen = new Map();
  for (const p of ports) {
    const a = remap.get(find(p.a)), b = remap.get(find(p.b));
    if (a === b) continue;
    const k = Math.min(a, b) * 1e7 + Math.max(a, b);
    if (seen.has(k)) { const q = seen.get(k); q.area += p.area; if (p.thick > q.thick) q.thick = p.thick; continue; }
    const q = { a: Math.min(a, b), b: Math.max(a, b), area: p.area, thick: p.thick, px: p.px, py: p.py, pz: p.pz };
    seen.set(k, q); finalPorts.push(q);
  }
  return { thick, label, regions, rawRegions: regions.length, kept, ports: finalPorts,
           timing: { tThick, tSeg, tPort }, merged };
}

function main() {
  const file = process.argv.find(a => !a.startsWith("--") && a.endsWith(".json"));
  const mr = parseFloat((process.argv.find(a => a.startsWith("--merge=")) || "=0.6").split("=")[1]) || 0.6;
  if (!file) { console.error("usage: node regions.js <dump.json> [--merge=0.6]"); process.exit(1); }
  const D = loadDump(file);
  const r = build(D, mr);
  console.log(`\n${D.poi}  ${D.n} voxels  merge=${mr}`);
  console.log(`  thickness  ${r.timing.tThick} ms`);
  console.log(`  watershed  ${r.timing.tSeg} ms   ->  ${r.rawRegions} raw regions`);
  console.log(`  contacts   ${r.timing.tPort} ms`);
  console.log(`  merged ${r.merged} boundaries  ->  ${r.kept} REGIONS, ${r.ports.length} boundaries`);
  const areas = r.ports.map(p => p.area).sort((a, b) => b - a);
  console.log(`  boundary areas (voxel-faces): max ${areas[0] ? areas[0].toFixed(0) : 0}, median ${areas.length ? areas[areas.length >> 1].toFixed(1) : 0}`);
  console.log(`  region sizes: ${r.regions.length ? "" : ""}${[...new Set()].length ? "" : ""}`);
  let tmax = 0; for (let i = 0; i < r.thick.length; i++) if (r.thick[i] < 1e8 && r.thick[i] > tmax) tmax = r.thick[i];
  console.log(`  thickest point in the map: ${tmax.toFixed(1)} blocks from anything solid\n`);
}
if (require.main === module) main();
module.exports = { build, thickness, segment };
