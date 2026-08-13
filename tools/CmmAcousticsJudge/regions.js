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
const THICK_CAP = 6.0;  // clearance beyond this is just "open"; do not subdivide it
const SEED_MIN = 1.6;   // a room's core must be at least this open; below it, join rather than seed

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
  // CAP the field. Beyond a few blocks of clearance a space is simply "open" and there is
  // nothing meaningful left to subdivide -- but an uncapped field keeps rising into the sky
  // around a POI, which then generates hundreds of spurious local maxima. The victorian dump is
  // 85% air with a median core of 10.4 blocks (against the mansard's 62% and 4.1), so most of
  // its 381 raw regions were subdivisions of empty sky, and greedy merging over that many edges
  // collapsed the whole POI -- whether the cascade halts was ordering luck, not a criterion.
  // Capping makes all wide-open air one plateau, hence one seed, and the merge graph small.
  for (let i = 0; i < n; i++) if (t[i] > THICK_CAP) t[i] = THICK_CAP;
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
    // Join the THICKEST labelled neighbour, not the first one found. A voxel on the skin of a
    // room belongs to that room, and "first in the 6-neighbour scan order" is arbitrary.
    let seen = -1, seenT = -1;
    for (const [dx, dy, dz] of nb6) {
      const nx = x + dx, ny = y + dy, nz = z + dz;
      if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;
      const j = li(nx, ny, nz);
      const l = label[j];
      if (l < 0) continue;
      if (thick[j] > seenT) { seenT = thick[j]; seen = l; }
    }
    if (seen < 0 && thick[i] >= SEED_MIN) {
      // A genuine local maximum in reasonably open air: a room centre.
      const id = regions.length;
      regions.push({ id, n: 0, sx: 0, sy: 0, sz: 0, core: thick[i], edges: new Map() });
      label[i] = id;
    } else if (seen < 0) {
      // Thin and isolated -- a crack with no wider space attached. Still a region, but it must
      // not become a SEED, because the thinnest layer of air forms a connected SHELL hugging
      // every wall surface in the map, and as one region that shell bridges everything: its
      // persistence against any neighbour is zero, so it glued the whole POI into one space.
      // A wall-hugging skin is not a room. Below SEED_MIN, voxels join a room instead of
      // founding one.
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

function build(D, mergeH = 0.62) {
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

  // ── 4. merge by PERSISTENCE, shallowest basin first ───────────────────────────
  // The pairwise ratio test failed on real geometry for a reason worth recording: it ignores
  // TRANSITIVITY. 1456 of 1771 boundaries passed it (correctly -- the thickness field is
  // saturated flat at 18 blocks across the open sky around a POI, so the watershed splits a
  // plateau arbitrarily and those splits should merge), but that many union-find edges connect
  // all 381 regions into one component, and the whole house collapsed. The "keep" decisions were
  // irrelevant because other paths joined the same regions anyway.
  //
  // Persistence is the standard, well-behaved criterion (the h-minima transform): how DEEP is the
  // basin separating two regions -- min(coreA, coreB) minus the boundary's thickness. A shallow
  // basin is not a room, it is a bump in the same room. Absolute, in blocks, so it means
  // something physical: "a space whose separation from its neighbour is under H blocks of pinch
  // is not a separate space."
  //
  // And process SHALLOWEST FIRST while carrying the merged region's core as the MAX of the two.
  // That is what stops chaining: as a region absorbs its neighbours it gets deeper, so the bar for
  // absorbing the next one rises, and a genuine doorway is never crossed by accident.
  const parent = regions.map(r => r.id);
  const coreOf = regions.map(r => r.core);
  const find = a => { while (parent[a] !== a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };
  let merged = 0;
  // widest-relative-to-core first, so obvious non-boundaries go early and a region's core has
  // grown by the time a genuine doorway is considered
  const byPersistence = ports.slice().sort((p, q) =>
    (q.thick / Math.max(0.01, Math.min(coreOf[q.a], coreOf[q.b]))) -
    (p.thick / Math.max(0.01, Math.min(coreOf[p.a], coreOf[p.b]))));
  for (const p of byPersistence) {
    const ra = find(p.a), rb = find(p.b);
    if (ra === rb) continue;
    // RELATIVE, not absolute. An absolute depth cannot serve rooms of wildly different size:
    // 1.5 blocks of pinch is nothing next to the 18-block-deep open sky around a POI, and
    // everything next to a 2-block closet. Small rooms kept dissolving into the outdoor volume
    // because their own core was barely deeper than their doorway. What matters is the RATIO --
    // how much of its own openness a space loses at its boundary.
    const core = Math.min(coreOf[ra], coreOf[rb]);
    if (p.thick < mergeH * core) continue;               // a real pinch: keep them apart
    parent[rb] = ra;
    coreOf[ra] = Math.max(coreOf[ra], coreOf[rb]);       // deeper now, so harder to absorb the next
    merged++;
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
           timing: { tThick, tSeg, tPort }, merged,
           // the merge's union-find, so callers can map a raw label to its surviving region
           rootOf: a => find(a) };
}

function main() {
  const file = process.argv.find(a => !a.startsWith("--") && a.endsWith(".json"));
  const mr = parseFloat((process.argv.find(a => a.startsWith("--merge=")) || "=0.62").split("=")[1]) || 0.62;
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

// ── adapter: present regions in the shape portals.js already consumes ──────────
// Deliberately minimal, so the model can be run on either structure and compared without
// touching the model at all. The mapping is mostly a simplification:
//
//   region  -> node, with throat = 1. A region has no internal pinch BY CONSTRUCTION -- that
//              is exactly what the segmentation guarantees -- so the whole node-throat
//              apparatus, and its cell-relative normalisation, simply goes away.
//   boundary -> portal AND aperture. They are the same object here: a boundary between two
//              regions IS a constriction, found from the geometry rather than detected by a
//              heuristic on an arbitrary cell.
//
// openFrac is the boundary's open area against the smaller adjoining region's cross-section,
// estimated as n^(2/3) -- a region of n voxels has roughly that much area across its middle.
function asGraph(D, mergeH) {
  const r = build(D, mergeH);
  const { label, thick } = r;
  const open = makeOpenTest(D);

  // regions surviving the merge, renumbered, with weighted centroids
  const idOf = new Map();
  for (const p of r.ports) { if (!idOf.has(p.a)) idOf.set(p.a, idOf.size); if (!idOf.has(p.b)) idOf.set(p.b, idOf.size); }
  const nodeOf = new Int32Array(D.n).fill(-1);
  const acc = new Map();
  const sx = D.sx, sy = D.sy, sz = D.sz, li = (x, y, z) => x + sx * (z + sz * y);
  // r.label holds RAW labels; map them through the merge by re-deriving from ports is unsafe, so
  // rebuild from the label field using the same union the build performed (exposed as r.rootOf).
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
    const i = li(x, y, z);
    if (open[i] <= OPEN_EPS) continue;
    const root = r.rootOf ? r.rootOf(label[i]) : label[i];
    let id = idOf.get(root);
    if (id === undefined) { id = idOf.size; idOf.set(root, id); }
    nodeOf[i] = id;
    let a = acc.get(id);
    if (!a) { a = { n: 0, sx: 0, sy: 0, sz: 0 }; acc.set(id, a); }
    a.n++; a.sx += x; a.sy += y; a.sz += z;
  }
  const nodes = [];
  for (let id = 0; id < idOf.size; id++) {
    const a = acc.get(id) || { n: 1, sx: 0, sy: 0, sz: 0 };
    nodes.push({
      id, n: a.n, throat: 1,
      px: D.x0 + a.sx / a.n + 0.5, py: D.y0 + a.sy / a.n + 0.5, pz: D.z0 + a.sz / a.n + 0.5,
      tpx: D.x0 + a.sx / a.n + 0.5, tpy: D.y0 + a.sy / a.n + 0.5, tpz: D.z0 + a.sz / a.n + 0.5,
      edges: [],
    });
  }
  const portals = [];
  for (const p of r.ports) {
    const a = idOf.get(p.a), b = idOf.get(p.b);
    if (a === undefined || b === undefined || a === b) continue;
    const ref = Math.max(1, Math.pow(Math.min(nodes[a].n, nodes[b].n), 2 / 3));
    const id = portals.length;
    portals.push({
      a, b, area: p.area, openFrac: Math.min(1, p.area / ref),
      px: p.px, py: p.py, pz: p.pz,
      x0: Math.floor(p.px) - 1, y0: Math.floor(p.py) - 1, z0: Math.floor(p.pz) - 1,
      x1: Math.floor(p.px), y1: Math.floor(p.py), z1: Math.floor(p.pz),
    });
    nodes[a].edges.push(id); nodes[b].edges.push(id);
  }
  // A boundary IS an aperture. Position at the boundary centroid, throat from its openness.
  const apertures = portals.map(p => ({
    nid: p.a, px: p.px, py: p.py, pz: p.pz, throat: p.openFrac,
  }));
  return { open, nodeOf, nodes, portals, apertures, cell: 4, thick,
           cellsWithAir: nodes.length, cellsSplit: 0, regionCount: nodes.length };
}
module.exports.asGraph = asGraph;
