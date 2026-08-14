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
const { makeOpenTest, segDbAt } = require("./portals.js");

const OPEN_EPS = 0.02;
// First-Fresnel-zone area in blocks^2 at typical room geometry: radius ~2.5 blocks for music
// wavelengths over ~10 block path legs. The coherent extent of the wavefront an opening samples.
const FRESNEL_AREA = 20.0;
const SEG_MIN_STEP = 6;      // blocks: shortest a corridor segment may be
const SEG_STEP_MULT = 2.5;   // segment length as a multiple of the space's own thickness
// Share of a region's boundary that must open onto the world before it counts as outdoors. Measured
// separation is total -- enclosed regions read 0.0%, open ones 12% and up -- so this is not delicate.
const SKY_SPLIT = 0.02;
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
      regions.push({ id, n: 0, sx: 0, sy: 0, sz: 0, core: thick[i], surf: 0, absorb: 0, edges: new Map() });
      label[i] = id;
    } else if (seen < 0) {
      // Thin and isolated -- a crack with no wider space attached. Still a region, but it must
      // not become a SEED, because the thinnest layer of air forms a connected SHELL hugging
      // every wall surface in the map, and as one region that shell bridges everything: its
      // persistence against any neighbour is zero, so it glued the whole POI into one space.
      // A wall-hugging skin is not a room. Below SEED_MIN, voxels join a room instead of
      // founding one.
      const id = regions.length;
      regions.push({ id, n: 0, sx: 0, sy: 0, sz: 0, core: thick[i], surf: 0, absorb: 0, edges: new Map() });
      label[i] = id;
    } else {
      // join the (thickest-neighbour) region; a conflict means we are ON a boundary, and the
      // boundary voxel still has to belong somewhere -- give it to the first, and record the
      // contact below when portals are built.
      label[i] = seen;
    }
    const R = regions[label[i]];
    R.n++; R.sx += x; R.sy += y; R.sz += z;
    // Surface area and area-weighted absorption of the enclosure, for the diffuse field
    // (MODEL.md term 4: R = S*alpha/(1-alpha)). Every face this voxel presents to something
    // non-open is a piece of the room's boundary. Absorption comes from what that face is made
    // of: dense hard material is REFLECTIVE (low alpha), soft porous material absorbs -- so
    // alpha runs opposite to the transmission loss fraction, which is a density proxy.
    for (const [dx, dy, dz] of nb6) {
      const nx = x + dx, ny = y + dy, nz = z + dz;
      const oob = nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz;
      const f = oob ? 0 : open[li(nx, ny, nz)];
      if (f > OPEN_EPS) continue;                       // opening, not surface
      // Out of bounds is OPEN SKY, not a wall: sound crossing it never comes back, so it is a
      // perfect absorber. Treating it as reflective gave the outdoor region a room constant and a
      // critical distance of 10.7 blocks -- implying a strong diffuse field outdoors, where there
      // is no enclosure at all. With alpha ~1 the outdoor room constant goes huge and its diffuse
      // field vanishes, which is the correct behaviour and falls out rather than being special-cased.
      // Absorption for a FURNISHED space, not a bare shell. 0.6*(1-loss) gave bare concrete
      // alpha 0.06, which is correct for a bare concrete box and wrong for any room a player is
      // in: real rooms have floors, furniture and clutter, and land at 0.15-0.35. An
      // unrealistically reverberant test box makes every diffuse-field number unrepresentative.
      const alpha = oob ? 0.95
        : Math.max(0.10, Math.min(0.85, 0.12 + 0.5 * (1 - Math.min(1, D.loss[li(nx, ny, nz)]))));
      R.surf++; R.absorb += alpha;
    }
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
  // ── INDOORS AND OUTDOORS ARE DIFFERENT ACOUSTIC SPACES ────────────────────────
  // No pinch test can separate them, because a bunker entrance or a cave mouth is a perfectly wide
  // opening -- there IS no pinch. So the watershed merged 777,541 voxels of the bunker capture, 97.6%
  // of all its open space, into one region spanning open sky, terrain AND tortuous underground rock
  // voids. That single region then has to share one reverberant field and one set of sight lines, and
  // it produced 40.6 dB steps between voxels ONE BLOCK apart with identical aperture sets: in a
  // winding void, lines to distant openings are blocked or clear almost at random.
  //
  // Whether a space is open to the world is measurable before any merging and it separates cleanly
  // with no overlap: on the bunker the large raw regions read 74-99% of their boundary onto the world
  // while the interior ones read 0.0%. So that boundary is an APERTURE between two spaces, never a
  // seam within one, and the merge must not cross it however wide it is.
  const rawSky = new Float64Array(regions.length);
  {
    const sx = D.sx, sy = D.sy, sz = D.sz;
    const li = (x, y, z) => x + sx * (z + sz * y);
    const nb6 = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]];
    const oob = new Float64Array(regions.length), tot = new Float64Array(regions.length);
    for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
      const i = li(x, y, z); const id = label[i];
      if (id === undefined || id < 0) continue;
      for (const [dx, dy, dz] of nb6) {
        const nx = x + dx, ny = y + dy, nz = z + dz;
        const o = nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz;
        if (!o && open[li(nx, ny, nz)] > OPEN_EPS) continue;
        tot[id]++; if (o) oob[id]++;
      }
    }
    for (let i = 0; i < regions.length; i++) rawSky[i] = tot[i] > 0 ? oob[i] / tot[i] : 0;
  }
  const skyOob = new Float64Array(regions.length), skySurf = new Float64Array(regions.length);
  for (let i = 0; i < regions.length; i++) { skyOob[i] = rawSky[i]; skySurf[i] = 1; }
  const isOutdoor = a => (skyOob[a] / Math.max(1e-9, skySurf[a])) > SKY_SPLIT;

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
    if (isOutdoor(ra) !== isOutdoor(rb)) continue;        // an enclosure never merges with the sky
    parent[rb] = ra;
    coreOf[ra] = Math.max(coreOf[ra], coreOf[rb]);       // deeper now, so harder to absorb the next
    skyOob[ra] += skyOob[rb]; skySurf[ra] += skySurf[rb];
    merged++;
  }
  // ── 5. SPLIT ELONGATED REGIONS ────────────────────────────────────────────────
  // A tunnel has no pinch, so the merge criterion correctly keeps it whole -- and that is wrong
  // acoustically, because the model assumes LINE OF SIGHT within a region and you cannot see round
  // a bend. Measured on the real cave dump: the whole tunnel system came out as one 382k-voxel
  // region and the longest air route the model found was exactly the straight-line distance, ratio
  // 1.00 everywhere. No tunnel routing happened at all, which is why caves could never work.
  //
  // So a region that is much longer than it is thick is cut into segments along its long axis, at
  // intervals of a few times its thickness. The cuts become ordinary boundaries with the local
  // cross-section as their area -- which is what corridor subdivision does in any portal system.
  const segOf = new Int32Array(D.n).fill(-1);
  let segCount = 0;
  {
    const acc = new Map();
    for (let i = 0; i < D.n; i++) {
      if (open[i] <= OPEN_EPS) continue;
      const root = find(label[i]);
      let a = acc.get(root);
      if (!a) { a = { n: 0, x0: 1e9, y0: 1e9, z0: 1e9, x1: -1e9, y1: -1e9, z1: -1e9, tsum: 0 }; acc.set(root, a); }
      const y = Math.floor(i / (D.sx * D.sz)), rem = i - y * D.sx * D.sz;
      const z = Math.floor(rem / D.sx), x = rem - z * D.sx;
      a.n++; a.tsum += thick[i];
      if (x < a.x0) a.x0 = x; if (x > a.x1) a.x1 = x;
      if (y < a.y0) a.y0 = y; if (y > a.y1) a.y1 = y;
      if (z < a.z0) a.z0 = z; if (z > a.z1) a.z1 = z;
    }
    const plan = new Map();
    for (const [root, a] of acc) {
      const ex = [a.x1 - a.x0 + 1, a.y1 - a.y0 + 1, a.z1 - a.z0 + 1];
      const axis = ex.indexOf(Math.max(...ex));
      const meanT = Math.max(1, a.tsum / a.n);
      const step = Math.max(SEG_MIN_STEP, meanT * SEG_STEP_MULT);
      if (ex[axis] / step < 2) continue;                    // compact enough: leave it whole
      plan.set(root, { axis, step, lo: [a.x0, a.y0, a.z0][axis] });
    }
    for (let i = 0; i < D.n; i++) {
      if (open[i] <= OPEN_EPS) continue;
      const root = find(label[i]);
      const p = plan.get(root);
      if (!p) { segOf[i] = 0; continue; }
      const y = Math.floor(i / (D.sx * D.sz)), rem = i - y * D.sx * D.sz;
      const z = Math.floor(rem / D.sx), x = rem - z * D.sx;
      const c = [x, y, z][p.axis];
      segOf[i] = Math.floor((c - p.lo) / p.step);
    }
    for (let i = 0; i < D.n; i++) if (segOf[i] > segCount) segCount = segOf[i];
    segCount++;
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
           // Map a RAW watershed label to its final, remapped region id. Callers must use this
           // rather than find() alone: ports carry remapped ids, and mixing the two id spaces
           // left room A with no edges at all -- disconnected from the graph, so no aperture
           // route existed and only the transmission path survived.
           regionOf: a => remap.get(find(a)), segOf, segCount,
           // final id combines the merged region and its corridor segment
           finalOf: (rawLabel, i) => remap.get(find(rawLabel)) * segCount + (segOf[i] < 0 ? 0 : segOf[i]) };
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

// ── shared WALL area between regions ──────────────────────────────────────────
// The segmentation records where regions touch through OPENINGS. It says nothing about where they
// touch through MATERIAL, and that is what drives a sealed room's reverberation: room B's surface
// is driven by room A's field hitting the wall they share. Computing a sealed room's field from a
// ray to its centroid instead put ~7 dB of pure distance into a quantity that has no distance in
// it, which is why a shut interior door read -15 dB against the author's "roughly half as loud".
//
// For every open voxel, march each of the six directions through up to WALL_MAX_THICK solid voxels.
// Emerging into a DIFFERENT region means those two regions share wall there: accumulate the face
// area and the power transmission along that short crossing.
const WALL_MAX_THICK = 3;
function wallLinks(D, open, label, regionOf) {
  // `label` here is the FINAL region id per voxel and regionOf is the identity.
  const sx = D.sx, sy = D.sy, sz = D.sz, li = (x, y, z) => x + sx * (z + sz * y);
  const dirs = [[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]];
  const links = new Map();
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
    const i = li(x, y, z);
    if (open[i] <= OPEN_EPS) continue;
    const a = regionOf(label[i]);
    if (a === undefined || a < 0) continue;
    for (const [dx, dy, dz] of dirs) {
      let nx = x + dx, ny = y + dy, nz = z + dz, steps = 0;
      // must start with material, or this is an opening and the aperture path already has it
      if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;
      if (open[li(nx, ny, nz)] > OPEN_EPS) continue;
      while (steps < WALL_MAX_THICK) {
        nx += dx; ny += dy; nz += dz; steps++;
        if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) { steps = -1; break; }
        if (open[li(nx, ny, nz)] > OPEN_EPS) break;
      }
      if (steps < 0 || steps >= WALL_MAX_THICK) continue;
      const j = li(nx, ny, nz);
      if (open[j] <= OPEN_EPS) continue;
      const b = regionOf(label[j]);
      if (b === undefined || b < 0 || b === a) continue;
      const lossDb = segDbAt(D, D.x0 + x + 0.5, D.y0 + y + 0.5, D.z0 + z + 0.5,
                                D.x0 + nx + 0.5, D.y0 + ny + 0.5, D.z0 + nz + 0.5);
      const lo = Math.min(a, b), hi = Math.max(a, b), k = lo * 1e7 + hi;
      let L = links.get(k);
      if (!L) { L = { a: lo, b: hi, area: 0, pow: 0 }; links.set(k, L); }
      L.area += 1;
      L.pow += Math.pow(10, -lossDb / 10);
    }
  }
  // area-weighted mean power transmission per link
  return [...links.values()].map(L => ({ a: L.a, b: L.b, area: L.area, tau: L.pow / L.area }));
}

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
  const sx = D.sx, sy = D.sy, sz = D.sz, li = (x, y, z) => x + sx * (z + sz * y);

  // Final region id per voxel: the merged region combined with its corridor segment. Rebuilt from
  // scratch rather than patched onto build()'s ports, because the segment cuts create boundaries
  // that pass never saw.
  // TWO LEVELS OF STRUCTURE, and conflating them broke the gate immediately.
  //   ROOM    = the merged region. One acoustic enclosure, ONE diffuse field.
  //   SEGMENT = a corridor slice of a room. A routing node, so a bend is not line of sight.
  // Cutting a room into segments for routing also gave each segment its own diffuse field, and a
  // segment has less surface than its room and so a different room constant -- crossing a cut
  // INSIDE one room stepped the level (approach went to 11.3 dB/block). A room's reverberation
  // does not care how the pathfinder subdivides it.
  const rawOf = new Int32Array(D.n).fill(-1);
  const roomOfVox = new Int32Array(D.n).fill(-1);
  const seen = new Map(), roomSeen = new Map();
  for (let i = 0; i < D.n; i++) {
    if (open[i] <= OPEN_EPS) continue;
    const f = r.finalOf(label[i], i);
    if (f === undefined || Number.isNaN(f)) continue;
    let id = seen.get(f);
    if (id === undefined) { id = seen.size; seen.set(f, id); }
    rawOf[i] = id;
    const rm = r.regionOf(label[i]);
    let rid = roomSeen.get(rm);
    if (rid === undefined) { rid = roomSeen.size; roomSeen.set(rm, rid); }
    roomOfVox[i] = rid;
  }
  const nodeOf = rawOf;
  const N = seen.size;

  // per-region volume, centroid, enclosure surface and absorption
  const st = Array.from({ length: N }, () => ({ n: 0, sx: 0, sy: 0, sz: 0, surf: 0, absorb: 0 }));
  const nb6 = [[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]];
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
    const i = li(x, y, z);
    const id = nodeOf[i];
    if (id < 0) continue;
    const a = st[id];
    a.n++; a.sx += x; a.sy += y; a.sz += z;
    for (const [dx, dy, dz] of nb6) {
      const nx = x + dx, ny = y + dy, nz = z + dz;
      const oob = nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz;
      if (!oob && open[li(nx, ny, nz)] > OPEN_EPS) continue;     // opening or same air, not surface
      const alpha = oob ? 0.95
        : Math.max(0.10, Math.min(0.85, 0.12 + 0.5 * (1 - Math.min(1, D.loss[li(nx, ny, nz)]))));
      a.surf++; a.absorb += alpha;
      if (oob) a.sky++;              // a face onto the world, not onto a surface: energy leaves here
    }
  }
  const nRooms = roomSeen.size;
  const rst = Array.from({ length: nRooms }, () => ({ n: 0, surf: 0, absorb: 0, sky: 0 }));
  const nodeRoom = new Int32Array(N).fill(0);
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
    const i = li(x, y, z); const id = nodeOf[i];
    if (id < 0) continue;
    const rid = roomOfVox[i];
    nodeRoom[id] = rid;
    const a = rst[rid];
    a.n++;
    for (const [dx, dy, dz] of nb6) {
      const nx = x + dx, ny = y + dy, nz = z + dz;
      const oob = nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz;
      if (!oob && open[li(nx, ny, nz)] > OPEN_EPS) continue;
      const alpha = oob ? 0.95
        : Math.max(0.10, Math.min(0.85, 0.12 + 0.5 * (1 - Math.min(1, D.loss[li(nx, ny, nz)]))));
      a.surf++; a.absorb += alpha;
      if (oob) a.sky++;              // a face onto the world: energy leaves here rather than bouncing
    }
  }
  const rooms = rst.map((a, id) => ({
    id, n: Math.max(1, a.n),
    surf: Math.max(1, a.surf), alpha: a.surf > 0 ? a.absorb / a.surf : 0.5,
    // Share of the boundary that is open to the world rather than a surface. A reverberant field is
    // a property of an ENCLOSURE; where the boundary is sky, energy leaves instead of bouncing.
    skyFrac: a.surf > 0 ? (a.sky || 0) / a.surf : 1,
    get roomR() { const al = Math.max(0.02, Math.min(0.95, this.alpha)); return this.surf * al / (1 - al); },
    walls: [],
  }));

  const nodes = st.map((a, id) => ({
    id, n: Math.max(1, a.n), throat: 1,
    px: D.x0 + a.sx / Math.max(1, a.n) + 0.5,
    py: D.y0 + a.sy / Math.max(1, a.n) + 0.5,
    pz: D.z0 + a.sz / Math.max(1, a.n) + 0.5,
    tpx: D.x0 + a.sx / Math.max(1, a.n) + 0.5,
    tpy: D.y0 + a.sy / Math.max(1, a.n) + 0.5,
    tpz: D.z0 + a.sz / Math.max(1, a.n) + 0.5,
    room: nodeRoom[id],
    edges: [],
  }));

  // boundaries between final regions, from real voxel faces
  const pm = new Map();
  const addFace = (a, b, wx, wy, wz, aw) => {
    const lo = Math.min(a, b), hi = Math.max(a, b), k = lo * 1e7 + hi;
    let p = pm.get(k);
    if (!p) { p = { a: lo, b: hi, area: 0, sx: 0, sy: 0, sz: 0 }; pm.set(k, p); }
    p.area += aw; p.sx += wx * aw; p.sy += wy * aw; p.sz += wz * aw;
  };
  for (let y = 0; y < sy; y++) for (let z = 0; z < sz; z++) for (let x = 0; x < sx; x++) {
    const i = li(x, y, z); const a = nodeOf[i];
    if (a < 0) continue;
    const chk = (nx, ny, nz, cx, cy, cz) => {
      if (nx >= sx || ny >= sy || nz >= sz) return;
      const j = li(nx, ny, nz); const b = nodeOf[j];
      if (b < 0 || b === a) return;
      addFace(a, b, D.x0 + cx, D.y0 + cy, D.z0 + cz, Math.min(open[i], open[j]));
    };
    chk(x + 1, y, z, x + 1, y + 0.5, z + 0.5);
    chk(x, y + 1, z, x + 0.5, y + 1, z + 0.5);
    chk(x, y, z + 1, x + 0.5, y + 0.5, z + 1);
  }
  const portals = [];
  for (const p of pm.values()) {
    const id = portals.length;
    portals.push({
      a: p.a, b: p.b, area: p.area, openFrac: Math.min(1, p.area / FRESNEL_AREA),
      px: p.sx / p.area, py: p.sy / p.area, pz: p.sz / p.area,
      x0: Math.floor(p.sx / p.area) - 1, y0: Math.floor(p.sy / p.area) - 1, z0: Math.floor(p.sz / p.area) - 1,
      x1: Math.floor(p.sx / p.area), y1: Math.floor(p.sy / p.area), z1: Math.floor(p.sz / p.area),
    });
    nodes[p.a].edges.push(id); nodes[p.b].edges.push(id);
  }
  // Wall links drive a ROOM's reverberation, so they are room-to-room.
  const walls = wallLinks(D, open, roomOfVox, a => a);
  for (const w of walls) {
    if (w.a >= nRooms || w.b >= nRooms) continue;
    rooms[w.a].walls.push({ other: w.b, area: w.area, tau: w.tau });
    rooms[w.b].walls.push({ other: w.a, area: w.area, tau: w.tau });
  }
  // Only a boundary between DIFFERENT ROOMS is an aperture. A corridor cut inside one room exists
  // purely so the pathfinder does not treat a bend as line of sight -- it is not a constriction and
  // it does not radiate. Treating every portal as an aperture put phantom radiators at every cut,
  // which stepped the level as a listener crossed one (approach 8.5 dB/block).
  const apertures = portals
    .filter(p => nodes[p.a].room !== nodes[p.b].room)
    .map(p => ({ nid: p.a, px: p.px, py: p.py, pz: p.pz, throat: p.openFrac,
                 // both rooms, so a listener anywhere can take a neighbour's reverberation through
                 // this opening without needing to be in a segment that happens to touch it
                 roomA: nodes[p.a].room, roomB: nodes[p.b].room }));
  // A cut is also not a constriction for routing purposes: it passes everything.
  for (const p of portals) if (nodes[p.a].room === nodes[p.b].room) p.openFrac = 1;
  return { open, nodeOf, nodes, rooms, portals, apertures, walls, cell: 4, thick,
           cellsWithAir: N, cellsSplit: 0, regionCount: N, roomCount: nRooms };
}
module.exports.asGraph = asGraph;
