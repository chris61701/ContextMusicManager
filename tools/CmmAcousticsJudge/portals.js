// ───────────────────────────────────────────────────────────────────────────────
// PORTAL-GRAPH ACOUSTICS — offline prototype (Phase 1)
// ───────────────────────────────────────────────────────────────────────────────
// Candidate replacement for the ear-anchored voxel flood. Three ideas, all of which
// the shipped model lacks:
//
//   1. TOPOLOGY IS COARSE, GEOMETRY IS EXACT. Space is diced into `cell`-sized boxes
//      purely as a filing index. The graph decides WHICH WAY sound arrives; the
//      LENGTH is then measured in continuous world coordinates along the real portal
//      points. So cell size costs node count, never accuracy — and the output is
//      continuous in listener position, not quantised to the grid.
//
//   2. A CELL IS NOT A NODE. Each cell is flood-filled internally; every disconnected
//      pocket of open air inside it becomes its OWN node. A 1-block wall through the
//      middle of a cell therefore yields two unconnected nodes instead of leaking
//      sound across it. This is what makes the grid safe on esoteric POI layouts
//      (author, 2026-08-13) and it kills the whole class of "thin slab leak" bugs
//      that BoundaryToward has.
//
//   3. EVERYTHING IS dB. Route cost, material transmission and aperture constriction
//      are all decibels, so Dijkstra compares "detour 20 blocks" against "through one
//      stone wall" in the unit that actually matters. The shipped model prices these
//      in "virtual blocks", whose dB value varies ~20x across the range and diverges
//      at the range edge — which is why no setting of WallDistance/MaxDistance/
//      HardStop could ever be consistent.
//
// Output per listener voxel is the ENERGY SUM of two arrivals (never a min):
//     • AIR   — through the portal graph. Real walked length, real arrival bearing.
//     • SOLID — the straight line, paying material transmission loss.
// Summing is required by the spec, not a refinement: a vault door in a wooden wall
// seals nothing because sound flanks around it, and a min-over-routes model cannot
// express that (see tools/CmmAcousticsRef/README.md, principle A).
//
// Usage:  node portals.js <dump.json> [--cell=4] [--emitter=0] [--compare]
//
// This is a PROTOTYPE for measurement only. Apertures here are derived from the
// dump's per-voxel loss/air flags; true per-face aperture from block collision
// bounds is Phase 7 (deliberately after the core is audible).
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs");
const { loadDump, makeEvaluator, segLoss } = require("./judge3d.js");

// ── model constants (the NEW model's whole tuning surface — compare to 41 dials) ──
// ── material -> dB, SUPERLINEAR ──────────────────────────────────────────────
// A flat mapping (dB = 14 * lossFraction) put wood at 7 dB and concrete at 12.6 -- only
// 5.6 dB apart, so light barriers over-attenuated and heavy ones under-attenuated at the
// same time. The spec implies a 40+ dB spread between "shut interior door, still obviously
// music" and "sealed vault assembly, nothing". Mass law agrees: transmission loss climbs
// steeply with density, it is not proportional to it. Calibrated so wood ~6 dB and
// concrete ~30 dB per voxel:
//     dB = DB_MAT * frac^MAT_POW,  clamped per voxel
// giving cloth ~0.04, glass 1.4, wood 6.0, dirt 15, concrete 30, metal 40, vault 60.
const DB_MAT = 40.1;        // scale: one voxel of the densest ordinary material (frac 1.0)
const MAT_POW = 2.74;       // curve: (0.9/0.5)^POW = 5, i.e. concrete costs 5x wood
const DB_VOXEL_MAX = 60.0;  // per-voxel ceiling (an airtight vault leaf)
const SLOPE = 0.35;         // distance attenuation, dB per block (exponential curve)
const OPEN_LOSS = 0.05;     // per-voxel loss at/below which a voxel is acoustically open
const KEEP_PORTALS = 4;     // entry portals retained per node (multi-route melding)
const AIR_FLAG = 1;
// Offsets for the transmission fan (see makeNewEvaluator): the listener itself plus
// +/-2 blocks on each axis, so the ray set can find a weaker patch of barrier nearby.
const SOLID_FAN = [[0,0,0],[2,0,0],[-2,0,0],[0,2,0],[0,-2,0],[0,0,2],[0,0,-2]];
const MIN_TRANSMIT_DB = 3;   // below this a ray is an opening, not a partition
const CLUSTER_BLOCKS = 4;    // entries closer than this are the same opening, not two
const APERTURE_MAX = 0.75;   // a node pinched below this much of its own cross-section IS an aperture
const PRUNE_DB = 25;         // ignore an arrival this far below the strongest: <0.4% of energy

// ── going around must COST something ──────────────────────────────────────────
// First measurement pass charged only path LENGTH, so the air arrival won 90-100% of
// walled buckets with a detour of merely 1.15-1.4x and sat 20-30 dB above the solid
// arrival. Material became nearly irrelevant and levels came out 3-4x too weak against
// the spec. The missing physics is the author's own words: "sound should bounce off and
// be absorbed by objects" -- energy is lost diffracting round an obstruction, not just
// travelling.
//
// Two terms, and CRUCIALLY neither may charge for open air. A straight path through
// unobstructed space must cost distance and nothing else, or a 40-block open-air route
// would accrue ~10 spurious cell-crossing penalties.
//
//   CONSTRICTION — a portal narrower than a full cell face radiates less power through.
//     Referenced to the FULL face (cell^2 voxel-faces), so open air is exactly 0 dB and
//     a doorway or a crack bites in proportion to how tight it is.
//   BEND — charged only when the route CHANGES DIRECTION at a portal, from the angle
//     between the incoming and outgoing legs. Straight through is free; a right angle
//     costs half of BEND_DB; a doubling-back costs all of it. This is what makes three
//     corners meaningfully quieter than a path 1.4x longer.
// CONSTRICTION IS A BOTTLENECK, NOT A TOLL. Charging it at every cell boundary the route
// crossed was wrong twice over: an open route accumulated a fee per boundary (a wide-open
// doorway cost 8.6 dB where the spec wants near line-of-sight), while a genuine pinch got
// DILUTED among its neighbours (a 2-voxel crack measured -8.8 against -8.6 for a fully
// open door -- indistinguishable, where the spec wants gradual). Power through a chain of
// openings is limited by the NARROWEST one; a corridor of constant width does not keep
// charging. So the route carries a running MINIMUM aperture and pays for that once.
//
// Dijkstra stays valid on the total: the additive part only grows, and the bottleneck can
// only tighten, so total cost is non-decreasing along any route.
//
// BEND is charged only where sound must actually DIFFRACT -- scaled by how constricted the
// portal is. In open space changing direction is free (a source radiates every way at
// once); it costs only when squeezing past an edge. Without that scaling, portal centroids
// in open air form a staircase on any diagonal route and every step billed a 90-degree
// turn, which is most of where that spurious 8.6 dB came from.
const BEND_DB = 6.0;        // dB for a full reversal through a fully-pinched portal
const MIN_APERTURE_DB = -30; // floor on the bottleneck's constriction (a pinhole is not infinite loss)

// Transmission loss along the straight line, summing the SUPERLINEAR per-voxel dB.
// Cannot reuse judge3d's segLoss: that sums loss FRACTIONS, and the curve has to be
// applied per voxel before summing or a 2-voxel wall reads as one twice as dense.
// Amanatides-Woo DDA, endpoints excluded, mirroring segLoss's traversal exactly.
// Voxel-index convenience wrapper. The real work takes float WORLD positions, because the
// game's ear position is a float and snapping it to a voxel centre puts a staircase in the
// output at one step per block that no field smoothness can remove. It is also a correctness
// fix, not just a resolution one: the DDA below starts at floor() of its origin, and passing
// a fractional coordinate to the old signature left `x` non-integral, so lossAt() indexed the
// voxel array fractionally, read undefined, and silently reported ZERO material. Sub-block
// sampling was measuring nothing.
function segDb(D, x0, y0, z0, x1, y1, z1) {
  return segDbAt(D, x0 + 0.5, y0 + 0.5, z0 + 0.5, x1 + 0.5, y1 + 0.5, z1 + 0.5);
}

function segDbAt(D, fx, fy, fz, tx, ty, tz) {
  // A LINE INTEGRAL, not a voxel count. Counting whole voxels made the result flip by a
  // material's full value as the ray angle changed: a ONE-voxel-thick wall clips one or two
  // of its voxels depending on the angle, so a diagonal crossing read 12 dB where a
  // perpendicular one read 6, and a single block of listener movement could swap them. That
  // was the root of the 10-16 dB/block cliffs -- not the portal graph, which is why fixing
  // the candidate set three different ways never removed them, and why alongwall-shut was
  // rough despite barely involving the graph at all.
  //
  // Weighting each voxel by the length of the ray actually inside it is both smooth and more
  // correct: a diagonal path through a wall IS slightly longer than a perpendicular one, and
  // it varies continuously with angle instead of stepping.
  let dx = tx - fx, dy = ty - fy, dz = tz - fz;
  const dist = Math.hypot(dx, dy, dz);
  if (dist < 0.01) return 0;
  dx /= dist; dy /= dist; dz /= dist;
  let x = Math.floor(fx), y = Math.floor(fy), z = Math.floor(fz);
  const x0 = x, y0 = y, z0 = z;
  const x1 = Math.floor(tx), y1 = Math.floor(ty), z1 = Math.floor(tz);
  const sX = dx > 0 ? 1 : dx < 0 ? -1 : 0, sY = dy > 0 ? 1 : dy < 0 ? -1 : 0, sZ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
  const tM = (o, d, c, s) => Math.abs(d) < 1e-6 ? Infinity : ((s > 0 ? c + 1 : c) - o) / d;
  const tD = d => Math.abs(d) < 1e-6 ? Infinity : Math.abs(1 / d);
  let tmx = tM(fx, dx, x, sX), tmy = tM(fy, dy, y, sY), tmz = tM(fz, dz, z, sZ);
  const tDx = tD(dx), tDy = tD(dy), tDz = tD(dz);
  let db = 0, tPrev = 0;
  for (let g = 0; g < 1024; g++) {
    const tNext = Math.min(tmx, tmy, tmz, dist);
    const seg = tNext - tPrev;                    // ray length inside the CURRENT voxel
    if (seg > 1e-6 && y >= 0 && y < 256 && !(x === x0 && y === y0 && z === z0) && !(x === x1 && y === y1 && z === z1)) {
      const f = D.lossAt(x, y, z);
      if (f > 1e-4) db += Math.min(DB_VOXEL_MAX, DB_MAT * Math.pow(f, MAT_POW)) * seg;
    }
    if (tNext >= dist - 1e-9) break;
    tPrev = tNext;
    if (tmx <= tmy && tmx <= tmz) { x += sX; tmx += tDx; }
    else if (tmy <= tmz) { y += sY; tmy += tDy; }
    else { z += sZ; tmz += tDz; }
  }
  return db;
}

const sstep = (a, b, x) => { if (b <= a) return x >= b ? 1 : 0; const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
const dbToPow = d => Math.pow(10, d / 10);
// One aperture's constriction loss, in dB, from how open it is relative to its local
// cross-section. Applied ONCE per route, at the bottleneck.
const constrictDb = f => Math.max(MIN_APERTURE_DB, 10 * Math.log10(Math.max(1e-4, f)));
const powToDb = p => p <= 1e-12 ? -120 : 10 * Math.log10(p);

// ── open/closed test ───────────────────────────────────────────────────────────
// Game truth first (the air flag), then acoustically-open non-air: see-through
// railings/ladders/broken windows already carry ~0 loss from CellLoss, so a low loss
// value is the dump's way of saying "sound passes freely here".
function makeOpenTest(D) {
  const open = new Uint8Array(D.n);
  for (let i = 0; i < D.n; i++)
    open[i] = ((D.flags[i] & AIR_FLAG) !== 0 || D.loss[i] <= OPEN_LOSS) ? 1 : 0;
  return open;
}

// ── graph build ────────────────────────────────────────────────────────────────
// Pass 1: per cell, label connected pockets of open voxels (6-connected flood inside
// the cell only). Pass 2: for each pair of adjacent cells, walk the shared boundary
// voxel-face by voxel-face and accumulate aperture area per (nodeA, nodeB) pair.
function buildGraph(D, cell) {
  const open = makeOpenTest(D);
  const cx = Math.ceil(D.sx / cell), cy = Math.ceil(D.sy / cell), cz = Math.ceil(D.sz / cell);
  const nodeOf = new Int32Array(D.n).fill(-1);   // voxel → node id
  const nodes = [];
  const stack = new Int32Array(cell * cell * cell);

  // local (dump-relative) index helpers
  const li = (x, y, z) => x + D.sx * (z + D.sz * y);

  for (let ccz = 0; ccz < cz; ccz++)
  for (let ccy = 0; ccy < cy; ccy++)
  for (let ccx = 0; ccx < cx; ccx++) {
    const x0 = ccx * cell, y0 = ccy * cell, z0 = ccz * cell;
    const x1 = Math.min(x0 + cell, D.sx), y1 = Math.min(y0 + cell, D.sy), z1 = Math.min(z0 + cell, D.sz);
    for (let z = z0; z < z1; z++)
    for (let y = y0; y < y1; y++)
    for (let x = x0; x < x1; x++) {
      const seed = li(x, y, z);
      if (!open[seed] || nodeOf[seed] !== -1) continue;
      // New pocket: flood it, staying inside this cell.
      const id = nodes.length;
      let sp = 0, count = 0, sxs = 0, sys = 0, szs = 0;
      // Per-axis slice occupancy inside this cell, for the node's THROAT (see below).
      const slX = new Int32Array(cell), slY = new Int32Array(cell), slZ = new Int32Array(cell);
      // positional sums per slice, so the throat has a LOCATION and not just a size
      const pX = [new Float64Array(cell), new Float64Array(cell), new Float64Array(cell)];
      const pY = [new Float64Array(cell), new Float64Array(cell), new Float64Array(cell)];
      const pZ = [new Float64Array(cell), new Float64Array(cell), new Float64Array(cell)];
      stack[sp++] = seed; nodeOf[seed] = id;
      while (sp > 0) {
        const cur = stack[--sp];
        const vy = Math.floor(cur / (D.sx * D.sz));
        const rem = cur - vy * D.sx * D.sz;
        const vz = Math.floor(rem / D.sx), vx = rem - vz * D.sx;
        count++; sxs += vx; sys += vy; szs += vz;
        const ax = vx - x0, ay = vy - y0, az = vz - z0;
        slX[ax]++; slY[ay]++; slZ[az]++;
        pX[0][ax] += vx; pY[0][ax] += vy; pZ[0][ax] += vz;
        pX[1][ay] += vx; pY[1][ay] += vy; pZ[1][ay] += vz;
        pX[2][az] += vx; pY[2][az] += vy; pZ[2][az] += vz;
        const nb = [[vx+1,vy,vz],[vx-1,vy,vz],[vx,vy+1,vz],[vx,vy-1,vz],[vx,vy,vz+1],[vx,vy,vz-1]];
        for (const [nx, ny, nz] of nb) {
          if (nx < x0 || nx >= x1 || ny < y0 || ny >= y1 || nz < z0 || nz >= z1) continue;
          const nid = li(nx, ny, nz);
          if (!open[nid] || nodeOf[nid] !== -1) continue;
          nodeOf[nid] = id; stack[sp++] = nid;
        }
      }
      // THROAT — the node's own narrowest cross-section, as a fraction of a full cell
      // face. A doorway set in a wall that happens to sit INSIDE a cell is invisible to
      // cell-boundary portals: room A's air, the door voxels and room B's air all flood
      // into ONE node, so the model read a 3-voxel gap as freely open (S6 came out a
      // two-step function -- shut, then identical for 1/3, 2/3 and fully open). Pocket
      // splitting stops DISCONNECTED regions merging; it does nothing about NARROWLY
      // connected ones. Measuring the min slice occupancy along each spanned axis
      // recovers the aperture wherever it happens to fall, and open air reads 1.0.
      const throatAxis = (sl) => {
        let lo = -1, hi = -1;
        for (let s = 0; s < cell; s++) if (sl[s] > 0) { if (lo < 0) lo = s; hi = s; }
        if (lo < 0 || hi === lo) return Infinity;   // spans one slice: not a throat
        let m = Infinity;
        for (let s = lo; s <= hi; s++) if (sl[s] < m) m = sl[s];
        return m;
      };
      // Normalise against the node's OWN typical cross-section, not an absolute cell face.
      // Referencing cell^2 made every cell touching a floor or ceiling read as constricted
      // (a 3-block-tall room can never fill a 4x4 face), so ordinary open rooms lost ~2 dB
      // and the control probe never reached 0. What matters acoustically is whether the
      // space PINCHES relative to itself.
      const throatWhich = (sl) => {
        let lo = -1, hi = -1;
        for (let q = 0; q < cell; q++) if (sl[q] > 0) { if (lo < 0) lo = q; hi = q; }
        if (lo < 0 || hi === lo) return -1;
        let m = Infinity, mi = -1;
        for (let q = lo; q <= hi; q++) if (sl[q] < m) { m = sl[q]; mi = q; }
        return mi;
      };
      const tA = [throatAxis(slX), throatAxis(slY), throatAxis(slZ)];
      const tRaw = Math.min(tA[0], tA[1], tA[2]);
      const avgSlice = Math.max(1, count / cell);
      const throat = Number.isFinite(tRaw) ? Math.min(1, tRaw / avgSlice) : 1;
      // Where the pinch IS — the centroid of the narrowest slice. This becomes the apparent
      // source position for anything that came through it (see the arrival rule below).
      let tpx = D.x0 + sxs / count + 0.5, tpy = D.y0 + sys / count + 0.5, tpz = D.z0 + szs / count + 0.5;
      if (Number.isFinite(tRaw)) {
        const axis = tA.indexOf(tRaw);
        const sl = axis === 0 ? slX : axis === 1 ? slY : slZ;
        const wi = throatWhich(sl);
        if (wi >= 0 && sl[wi] > 0) {
          tpx = D.x0 + pX[axis][wi] / sl[wi] + 0.5;
          tpy = D.y0 + pY[axis][wi] / sl[wi] + 0.5;
          tpz = D.z0 + pZ[axis][wi] / sl[wi] + 0.5;
        }
      }
      nodes.push({ id, n: count, cxs: ccx, cys: ccy, czs: ccz, throat, tpx, tpy, tpz,
                   // centroid in WORLD coords (used only as a fallback point)
                   px: D.x0 + sxs / count + 0.5, py: D.y0 + sys / count + 0.5, pz: D.z0 + szs / count + 0.5,
                   edges: [] });
    }
  }

  // Pass 2 — apertures. One sweep per axis over every open voxel; if the +axis
  // neighbour is open and in a DIFFERENT node, that voxel face is aperture between them.
  // Accumulate area and a face centroid so the route has a real point to measure to.
  const portalMap = new Map();   // "a,b" → { a, b, area, sx, sy, sz }
  const addFace = (na, nb, wx, wy, wz) => {
    const a = Math.min(na, nb), b = Math.max(na, nb);
    const key = a * 1e7 + b;
    let p = portalMap.get(key);
    if (!p) { p = { a, b, area: 0, sx: 0, sy: 0, sz: 0,
                    x0: Infinity, y0: Infinity, z0: Infinity, x1: -Infinity, y1: -Infinity, z1: -Infinity }; portalMap.set(key, p); }
    p.area++; p.sx += wx; p.sy += wy; p.sz += wz;
    // extent, so a path can be pulled taut THROUGH the opening instead of via its centre
    if (wx < p.x0) p.x0 = wx; if (wx > p.x1) p.x1 = wx;
    if (wy < p.y0) p.y0 = wy; if (wy > p.y1) p.y1 = wy;
    if (wz < p.z0) p.z0 = wz; if (wz > p.z1) p.z1 = wz;
  };
  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = li(x, y, z);
    if (!open[i]) continue;
    const a = nodeOf[i];
    if (x + 1 < D.sx) { const j = li(x+1,y,z); if (open[j] && nodeOf[j] !== a) addFace(a, nodeOf[j], D.x0+x+1, D.y0+y+0.5, D.z0+z+0.5); }
    if (y + 1 < D.sy) { const j = li(x,y+1,z); if (open[j] && nodeOf[j] !== a) addFace(a, nodeOf[j], D.x0+x+0.5, D.y0+y+1, D.z0+z+0.5); }
    if (z + 1 < D.sz) { const j = li(x,y,z+1); if (open[j] && nodeOf[j] !== a) addFace(a, nodeOf[j], D.x0+x+0.5, D.y0+y+0.5, D.z0+z+1); }
  }

  const portals = [];
  for (const p of portalMap.values()) {
    // openFrac relative to the LOCAL cross-section (the smaller adjoining node's average
    // slice), for the same reason as the node throat above: a full-height opening between
    // two cells of a low room is not a constriction, however small it is in absolute terms.
    const refA = Math.max(1, nodes[p.a].n / cell), refB = Math.max(1, nodes[p.b].n / cell);
    const ref = Math.min(refA, refB);
    const q = { a: p.a, b: p.b, area: p.area,
                px: p.sx / p.area, py: p.sy / p.area, pz: p.sz / p.area,
                x0: p.x0, y0: p.y0, z0: p.z0, x1: p.x1, y1: p.y1, z1: p.z1,
                openFrac: Math.min(1, p.area / ref) };
    const id = portals.length;
    portals.push(q);
    nodes[p.a].edges.push(id);
    nodes[p.b].edges.push(id);
  }

  // ── APERTURES ────────────────────────────────────────────────────────────────
  // The real openings in the air space: nodes whose own cross-section pinches. Unlike
  // portals -- which are artifacts of where the cell grid happens to fall -- an aperture is
  // a feature of the geometry, so it is stable, sparse, and canonical. That matters because
  // the arrival set can then be FIXED: a set that never changes as the listener moves cannot
  // put a step in the field, which is what every previous keying (own node, one hop, spatial
  // radius) did. Cliffs of 11-16 dB/block all traced to the candidate set changing.
  const apertures = [];
  for (const nd of nodes) {
    if (nd.throat >= APERTURE_MAX) continue;
    apertures.push({ nid: nd.id, px: nd.tpx, py: nd.tpy, pz: nd.tpz, throat: nd.throat });
  }

  const split = new Map();
  for (const nd of nodes) {
    const k = `${nd.cxs},${nd.cys},${nd.czs}`;
    split.set(k, (split.get(k) || 0) + 1);
  }
  let multi = 0;
  for (const v of split.values()) if (v > 1) multi++;

  return { open, nodeOf, nodes, portals, apertures, cx, cy, cz, cell,
           cellsWithAir: split.size, cellsSplit: multi };
}

// ── air-route solve: DIJKSTRA OVER PORTALS, not over nodes ─────────────────────
// The previous solver stored the cheapest route per NODE and let the listener's own node
// select an entry portal. That made node membership -- a discrete quantity -- an input to
// the level, so two adjacent listener positions in different nodes could measure slightly
// different paths and the difference appeared as a step (1.8 dB at a node boundary in the
// approach render, which the author spotted in the waveform). No amount of blending fixes
// that, because the cause is the keying, not the arithmetic.
//
// So the route cost is now a property of each PORTAL. Sound arrives at an opening at some
// cost; what the listener hears is that opening acting as a virtual source, plus the last
// leg from it. Nothing about that depends on which cell the listener is standing in.
//
// State is keyed by portal, carrying which node the route arrived FROM (so the next hop
// emerges on the far side), the additive losses, the running bottleneck and where it is,
// and the previous path point so the bend at this portal can be measured.
function solveAir(D, G, src) {
  const NP = G.portals.length;
  const cost = new Array(NP);       // pid -> { add, bneck, len, cx,cy,cz, from }
  const srcPt = { px: src.x + 0.5, py: src.y + 0.5, pz: src.z + 0.5 };

  // Seed: every distinct air pocket within SEED_R, each paying for the material between
  // the speaker and it (see the note on shelf speakers -- radius 1 finds the void inside
  // the furniture, not the room).
  const SEED_R = 3;
  const seedDb = new Map();
  for (let dz = -SEED_R; dz <= SEED_R; dz++)
  for (let dy = -SEED_R; dy <= SEED_R; dy++)
  for (let dx = -SEED_R; dx <= SEED_R; dx++) {
    const wx = src.x + dx, wy = src.y + dy, wz = src.z + dz;
    if (!D.inB(wx, wy, wz)) continue;
    const nd = G.nodeOf[D.idx(wx, wy, wz)];
    if (nd < 0) continue;
    const c = SLOPE * Math.hypot(dx, dy, dz) + segDb(D, src.x, src.y, src.z, wx, wy, wz);
    const prev = seedDb.get(nd);
    if (prev === undefined || c < prev) seedDb.set(nd, c);
  }
  if (seedDb.size === 0) return { cost, seeded: seedDb, srcPt };

  const heap = [];
  const push = it => { heap.push(it); let i = heap.length - 1;
    while (i > 0) { const p = (i - 1) >> 1; if (heap[p].db <= heap[i].db) break; [heap[p], heap[i]] = [heap[i], heap[p]]; i = p; } };
  const pop = () => { const top = heap[0], last = heap.pop();
    if (heap.length) { heap[0] = last; let i = 0;
      for (;;) { const l = 2 * i + 1, r = l + 1; let m = i;
        if (l < heap.length && heap[l].db < heap[m].db) m = l;
        if (r < heap.length && heap[r].db < heap[m].db) m = r;
        if (m === i) break; [heap[m], heap[i]] = [heap[i], heap[m]]; i = m; } }
    return top; };

  const relaxInto = (pid, from, add, bneck, len, cx, cy, cz, prev) => {
    const db = add - constrictDb(bneck);
    const cur = cost[pid];
    if (cur && cur.add - constrictDb(cur.bneck) <= db) return;
    cost[pid] = { add, bneck, len, cx, cy, cz, from, prev };
    push({ pid, from, add, bneck, len, cx, cy, cz, prev, db });
  };

  // From the source into every portal bounding a seeded pocket.
  for (const [nd, seedCost] of seedDb) {
    const node = G.nodes[nd];
    for (const pid of node.edges) {
      const P = G.portals[pid];
      const leg = Math.hypot(P.px - srcPt.px, P.py - srcPt.py, P.pz - srcPt.pz);
      const bneck = Math.min(1, node.throat, P.openFrac);
      let cx = srcPt.px, cy = srcPt.py, cz = srcPt.pz;
      if (bneck < 1 - 1e-9) {
        if (node.throat <= P.openFrac) { cx = node.tpx; cy = node.tpy; cz = node.tpz; }
        else { cx = P.px; cy = P.py; cz = P.pz; }
      }
      relaxInto(pid, nd, seedCost + SLOPE * leg, bneck, leg, cx, cy, cz, srcPt);
    }
  }

  let popped = 0;
  while (heap.length) {
    const s = pop();
    popped++;
    const live = cost[s.pid];
    if (!live || live.add !== s.add || live.from !== s.from) continue;   // superseded
    const P = G.portals[s.pid];
    const far = P.a === s.from ? P.b : P.a;
    const F = G.nodes[far];
    const inX = P.px - s.prev.px, inY = P.py - s.prev.py, inZ = P.pz - s.prev.pz;
    const inLen = Math.hypot(inX, inY, inZ);
    for (const qid of F.edges) {
      if (qid === s.pid) continue;
      const Q = G.portals[qid];
      const lx = Q.px - P.px, ly = Q.py - P.py, lz = Q.pz - P.pz;
      const leg = Math.hypot(lx, ly, lz);
      if (leg < 1e-6) continue;
      // Bend is charged AT this portal, gated by how pinched it is: turning in open space
      // is free, because a source radiates every way at once.
      let bend = 0;
      if (inLen > 1e-6) {
        const cos = (inX * lx + inY * ly + inZ * lz) / (inLen * leg);
        bend = BEND_DB * (1 - Math.max(-1, Math.min(1, cos))) / 2 * (1 - P.openFrac);
      }
      const nBneck = Math.min(s.bneck, F.throat, Q.openFrac);
      let cx = s.cx, cy = s.cy, cz = s.cz;
      if (nBneck < s.bneck - 1e-9) {
        if (F.throat <= Q.openFrac) { cx = F.tpx; cy = F.tpy; cz = F.tpz; }
        else { cx = Q.px; cy = Q.py; cz = Q.pz; }
      }
      relaxInto(qid, far, s.add + SLOPE * leg + bend, nBneck, s.len + leg, cx, cy, cz,
                { px: P.px, py: P.py, pz: P.pz });
    }
  }
  return { cost, seeded: seedDb, srcPt, popped };
}

// ── per-voxel evaluation: AIR arrivals + SOLID arrival, energy-summed ──────────
function makeNewEvaluator(D, G, src) {
  const { cost, seeded } = solveAir(D, G, src);
  const sx5 = src.x + 0.5, sy5 = src.y + 0.5, sz5 = src.z + 0.5;
  const minSeed = seeded.size ? Math.min(...seeded.values()) : 0;

  // ── the FIXED candidate set ────────────────────────────────────────────────
  // One entry per aperture, plus the source itself. Fixed for the whole field: the listener
  // never gathers, selects by hop count, or searches a radius, because every one of those
  // makes the set a step function of position and puts a cliff in the field. Pruning is by
  // CONTRIBUTION only (PRUNE_DB below the strongest, i.e. under 0.4% of the energy), so the
  // error it can introduce is bounded at hundredths of a dB rather than being topological.
  //
  // An aperture's cost is the cheapest way for sound to arrive AT it -- the best of its own
  // node's portals plus the hop to the pinch point -- and then its own constriction, since
  // anything using it must squeeze through.
  const cands = [{ cost: minSeed, px: sx5, py: sy5, pz: sz5, routeLen: 0, isSrc: true }];
  for (const A of G.apertures) {
    let bestDb = Infinity, bestLen = 0;
    if (seeded.has(A.nid)) {
      bestDb = seeded.get(A.nid) + SLOPE * Math.hypot(A.px - sx5, A.py - sy5, A.pz - sz5);
      bestLen = Math.hypot(A.px - sx5, A.py - sy5, A.pz - sz5);
    }
    for (const pid of G.nodes[A.nid].edges) {
      const pc = cost[pid];
      if (!pc) continue;
      const P = G.portals[pid];
      const hop = Math.hypot(A.px - P.px, A.py - P.py, A.pz - P.pz);
      const cand = pc.add - constrictDb(pc.bneck) + SLOPE * hop;
      if (cand < bestDb) { bestDb = cand; bestLen = pc.len + hop; }
    }
    if (!Number.isFinite(bestDb)) continue;
    cands.push({ cost: bestDb - constrictDb(A.throat), px: A.px, py: A.py, pz: A.pz,
                 routeLen: bestLen, throat: A.throat });
  }

  // wx/wy/wz are BLOCK COORDS and may be fractional.
  return function (wx, wy, wz) {
    const d = Math.hypot(wx - src.x, wy - src.y, wz - src.z);
    const lx = wx + 0.5, ly = wy + 0.5, lz = wz + 0.5;

    // The SOURCE candidate's last leg uses the transmission fan: a barrier is a distributed
    // radiator, so behind a vault door set in a wooden wall you hear the wall beside you.
    let fanDb = Infinity;
    for (const [ox, oy, oz] of SOLID_FAN) {
      const px = lx + ox, py = ly + oy, pz = lz + oz;
      const m = segDbAt(D, sx5, sy5, sz5, px, py, pz);
      const dd = Math.hypot(px - sx5, py - sy5, pz - sz5);
      if (SLOPE * dd + m < fanDb) fanDb = SLOPE * dd + m;
    }
    if (!Number.isFinite(fanDb)) fanDb = 120;
    const centerMat = segDbAt(D, sx5, sy5, sz5, lx, ly, lz);

    const arrivals = [];
    let pow = 0, bestDb = -Infinity, bestIdx = -1;
    for (let i = 0; i < cands.length; i++) {
      const c = cands[i];
      const leg = Math.hypot(c.px - lx, c.py - ly, c.pz - lz);
      let db, legMat;
      if (c.isSrc) { legMat = centerMat; db = -(c.cost + fanDb); }
      else {
        legMat = segDbAt(D, c.px, c.py, c.pz, lx, ly, lz);
        db = -(c.cost + SLOPE * leg + legMat);
      }
      // A candidate whose total route is barely longer than the straight line describes the
      // SAME physical path as the direct sound, not an alternative -- which is the Fresnel
      // criterion, and the reason an aperture sitting on the line of sight must not be added
      // on top of the direct arrival. Ramped over 2 blocks of excess so nothing switches.
      // Excess measured GEOMETRICALLY -- source to aperture to listener against the straight
      // line -- not along the routed polyline. The polyline overestimates (it bends through
      // portal centroids), which made a doorway sitting exactly on the line of sight look like
      // a detour and get summed on top of the direct sound, pushing an open door 1.5 dB ABOVE
      // free field. Fresnel is about the geometric path difference, so measure that.
      const geoExcess = Math.hypot(c.px - sx5, c.py - sy5, c.pz - sz5) + leg - d;
      const w = c.isSrc ? 1 : sstep(0, 2, geoExcess);
      if (w <= 1e-4 || db <= -110) continue;
      arrivals.push({ db, w, px: c.px, py: c.py, pz: c.pz, legMat, len: c.routeLen + leg, isSrc: !!c.isSrc });
      pow += w * dbToPow(db);
      if (db > bestDb) { bestDb = db; bestIdx = arrivals.length - 1; }
    }
    // contribution prune, applied after the fact so the set itself never changed
    let kept = 0;
    for (const a of arrivals) if (a.db >= bestDb - PRUNE_DB) kept++;

    const total = pow > 0 ? powToDb(pow) : -120;
    const dom = arrivals[bestIdx] || null;
    const airs = arrivals.filter(a => !a.isSrc && a.db >= bestDb - PRUNE_DB);
    const solid = arrivals.find(a => a.isSrc);
    return {
      d, total,
      airDb: airs.length ? powToDb(airs.reduce((s, a) => s + a.w * dbToPow(a.db), 0)) : -120,
      solidDb: solid ? solid.db : -120,
      matDb: centerMat, airLen: dom ? dom.len : d,
      entry: dom, airs, arrivals, kept,
      reached: airs.length > 0,
    };
  };
}

// ── continuity / reach metrics (same shape as judge3d's adjacency measure) ─────
function measure(D, G, evalFn, label, range) {
  const dbAt = new Float32Array(D.n).fill(NaN);
  let airVox = 0, reached = 0;
  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = x + D.sx * (z + D.sz * y);
    if (!G.open[i]) continue;
    const wx = D.x0 + x, wy = D.y0 + y, wz = D.z0 + z;
    const r = evalFn(wx, wy, wz);
    if (r.d > range) continue;
    airVox++;
    if (r.reached !== false) reached++;
    dbAt[i] = r.total !== undefined ? r.total
            : (r.att > 0 ? 20 * Math.log10(r.att) : -120);
  }
  // adjacency jumps
  let n = 0, sum = 0, mx = 0, j3 = 0, j6 = 0, j12 = 0;
  const jumps = [];
  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = x + D.sx * (z + D.sz * y);
    if (Number.isNaN(dbAt[i])) continue;
    const nbs = [];
    if (x + 1 < D.sx) nbs.push(x + 1 + D.sx * (z + D.sz * y));
    if (y + 1 < D.sy) nbs.push(x + D.sx * (z + D.sz * (y + 1)));
    if (z + 1 < D.sz) nbs.push(x + D.sx * ((z + 1) + D.sz * y));
    for (const j of nbs) {
      if (Number.isNaN(dbAt[j])) continue;
      const dl = Math.abs(dbAt[i] - dbAt[j]);
      n++; sum += dl; jumps.push(dl);
      if (dl > mx) mx = dl;
      if (dl >= 3) j3++; if (dl >= 6) j6++; if (dl >= 12) j12++;
    }
  }
  jumps.sort((a, b) => a - b);
  const p = q => jumps.length ? jumps[Math.min(jumps.length - 1, Math.floor(q * jumps.length))] : 0;
  return { label, airVox, reached, pairs: n, mean: sum / (n || 1), p99: p(0.99), max: mx,
           j3, j6, j12, pct3: 100 * j3 / (n || 1), pct6: 100 * j6 / (n || 1), pct12: 100 * j12 / (n || 1) };
}

function row(m) {
  return `${m.label.padEnd(22)} ${String(m.airVox).padStart(8)} ${(100*m.reached/(m.airVox||1)).toFixed(1).padStart(6)}% ` +
         `${m.mean.toFixed(2).padStart(7)} ${m.p99.toFixed(1).padStart(7)} ${m.max.toFixed(1).padStart(8)} ` +
         `${m.pct3.toFixed(2).padStart(7)}% ${m.pct6.toFixed(2).padStart(7)}% ${m.pct12.toFixed(2).padStart(7)}%`;
}

// ── main ───────────────────────────────────────────────────────────────────────
function main() {
  const args = process.argv.slice(2);
  const file = args.find(a => !a.startsWith("--"));
  if (!file) { console.error("usage: node portals.js <dump.json> [--cell=4] [--emitter=0] [--compare]"); process.exit(1); }
  const cellArg = args.find(a => a.startsWith("--cell="));
  const emArg = args.find(a => a.startsWith("--emitter="));
  const compare = args.includes("--compare");
  const cells = cellArg ? [parseInt(cellArg.split("=")[1], 10)] : [2, 3, 4];

  const D = loadDump(file);
  const emIdx = emArg ? parseInt(emArg.split("=")[1], 10) : 0;
  const em = D.emitters[emIdx];
  const range = D.cfg.range;

  console.log(`\n${D.poi}  ${D.sx}x${D.sy}x${D.sz} = ${D.n} voxels`);
  console.log(`emitter[${emIdx}] = ${em.name} @ (${em.x},${em.y},${em.z})   range=${range}  floodRadius=${D.cfg.floodRadius}`);
  console.log(`\n${"model".padEnd(22)} ${"airVox".padStart(8)} ${"reach".padStart(7)} ${"mean".padStart(7)} ${"p99".padStart(7)} ${"max".padStart(8)} ${">=3dB".padStart(8)} ${">=6dB".padStart(8)} ${">=12dB".padStart(8)}`);
  console.log("-".repeat(104));

  const results = [];
  if (compare) {
    const t0 = Date.now();
    const oldEval = makeEvaluator(D, em);
    const m = measure(D, { open: makeOpenTest(D) }, oldEval, "OLD (shipped)", range);
    m.ms = Date.now() - t0;
    results.push(m);
    console.log(row(m));
  }

  for (const cell of cells) {
    const t0 = Date.now();
    const G = buildGraph(D, cell);
    const tBuild = Date.now() - t0;
    const t1 = Date.now();
    const ev = makeNewEvaluator(D, G, em);
    const m = measure(D, G, ev, `NEW cell=${cell}`, range);
    m.ms = Date.now() - t1; m.buildMs = tBuild; m.G = G;
    results.push(m);
    console.log(row(m));
  }

  console.log("\ngraph structure");
  for (const m of results) {
    if (!m.G) continue;
    const G = m.G;
    console.log(`  cell=${G.cell}: ${G.nodes.length} nodes, ${G.portals.length} portals, ` +
                `${G.cellsWithAir} cells with air, ${G.cellsSplit} of them split into >1 pocket ` +
                `(${(100*G.cellsSplit/(G.cellsWithAir||1)).toFixed(1)}% — these are the leaks a plain grid would have)`);
    console.log(`           build ${m.buildMs} ms, evaluate ${m.ms} ms`);
  }
  console.log();
}

if (require.main === module) main();
module.exports = { buildGraph, solveAir, makeNewEvaluator, measure, makeOpenTest, segDb, segDbAt, SLOPE };
