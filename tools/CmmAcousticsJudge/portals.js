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
function segDb(D, x0, y0, z0, x1, y1, z1) {
  const fx = x0 + 0.5, fy = y0 + 0.5, fz = z0 + 0.5;
  const tx = x1 + 0.5, ty = y1 + 0.5, tz = z1 + 0.5;
  let dx = tx - fx, dy = ty - fy, dz = tz - fz;
  const dist = Math.hypot(dx, dy, dz);
  if (dist < 0.01) return 0;
  dx /= dist; dy /= dist; dz /= dist;
  let x = x0, y = y0, z = z0;
  const sX = dx > 0 ? 1 : dx < 0 ? -1 : 0, sY = dy > 0 ? 1 : dy < 0 ? -1 : 0, sZ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
  const tM = (o, d, c, s) => Math.abs(d) < 1e-6 ? Infinity : ((s > 0 ? c + 1 : c) - o) / d;
  const tD = d => Math.abs(d) < 1e-6 ? Infinity : Math.abs(1 / d);
  let tmx = tM(fx, dx, x, sX), tmy = tM(fy, dy, y, sY), tmz = tM(fz, dz, z, sZ);
  const tdx = tD(dx), tdy = tD(dy), tdz = tD(dz);
  let db = 0;
  for (let g = 0; g < 512; g++) {
    if (x === x1 && y === y1 && z === z1) break;
    if (tmx <= tmy && tmx <= tmz) { x += sX; tmx += tdx; }
    else if (tmy <= tmz) { y += sY; tmy += tdy; }
    else { z += sZ; tmz += tdz; }
    if (x === x1 && y === y1 && z === z1) break;
    if (y < 0 || y >= 256) continue;
    const f = D.lossAt(x, y, z);
    if (f > 1e-4) db += Math.min(DB_VOXEL_MAX, DB_MAT * Math.pow(f, MAT_POW));
  }
  return db;
}

const sstep = (a, b, x) => { if (b <= a) return x >= b ? 1 : 0; const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
const dbToPow = d => Math.pow(10, d / 10);
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

  const split = new Map();
  for (const nd of nodes) {
    const k = `${nd.cxs},${nd.cys},${nd.czs}`;
    split.set(k, (split.get(k) || 0) + 1);
  }
  let multi = 0;
  for (const v of split.values()) if (v > 1) multi++;

  return { open, nodeOf, nodes, portals, cx, cy, cz, cell,
           cellsWithAir: split.size, cellsSplit: multi };
}

// ── air-route solve ────────────────────────────────────────────────────────────
// Dijkstra from the source's node, in dB. Edge cost = distance between the portal
// points travelled plus the portal's constriction. Keeps the best KEEP_PORTALS entry
// points per node, so a big room reached through two doorways offers both — which is
// what makes route switching a crossfade instead of a step (and is the melding the
// spec asks for).
function solveAir(D, G, src) {
  const best = new Array(G.nodes.length);   // node → [{ portal, db, px,py,pz }]

  // SEED FROM THE AIR AROUND THE SPEAKER, not from its own voxel. A speaker IS a solid
  // block, so it never has an air node of its own — seeding from it made every real
  // emitter unreachable (reach 0%; only the dumps' `virtual` probe points, which sit in
  // air, appeared to work). Radiating into the neighbouring air is also the physically
  // correct statement, and it handles a speaker embedded in a wall between two rooms:
  // every distinct adjacent air pocket gets seeded, so it feeds both sides.
  //
  // The seed's entry POINT is the speaker itself with zero cost, so a listener sharing
  // the pocket measures plain line-of-sight distance from the speaker (not from some
  // neighbouring voxel), and the near field stays exact.
  // Seed EVERY distinct air pocket within SEED_R, each paying for the material between
  // the speaker and it. Stopping at the first pocket found was wrong in a way that would
  // have shipped silently: a `cntStoreShelf...Speaker` sits INSIDE a shelf, so radius 1
  // finds the enclosed void in the furniture and never reaches the store floor two
  // blocks out — every shelf speaker would have been inaudible. Paying segLoss to each
  // pocket keeps it honest: the void is free, the floor costs the shelf panel, and a
  // genuinely sealed room (the bunker) still seeds nothing beyond its own air, so
  // transmission correctly carries all of it.
  const SEED_R = 3;
  const srcPt = { px: src.x + 0.5, py: src.y + 0.5, pz: src.z + 0.5 };
  const seedDb = new Map();   // node → cheapest dB to start radiating into it
  for (let dz = -SEED_R; dz <= SEED_R; dz++)
  for (let dy = -SEED_R; dy <= SEED_R; dy++)
  for (let dx = -SEED_R; dx <= SEED_R; dx++) {
    const wx = src.x + dx, wy = src.y + dy, wz = src.z + dz;
    if (!D.inB(wx, wy, wz)) continue;
    const nd = G.nodeOf[D.idx(wx, wy, wz)];
    if (nd < 0) continue;
    const dist = Math.hypot(dx, dy, dz);
    const cost = SLOPE * dist + segDb(D, src.x, src.y, src.z, wx, wy, wz);
    const prev = seedDb.get(nd);
    if (prev === undefined || cost < prev) seedDb.set(nd, cost);
  }
  const seeded = seedDb;
  if (seeded.size === 0) return { best, srcNode: -1, seeded: 0 };

  // Priority queue (binary heap) over (node, entryPoint) states.
  const heap = [];
  const push = (it) => { heap.push(it); let i = heap.length - 1;
    while (i > 0) { const p = (i - 1) >> 1; if (heap[p].db <= heap[i].db) break; [heap[p], heap[i]] = [heap[i], heap[p]]; i = p; } };
  const pop = () => { const top = heap[0], last = heap.pop();
    if (heap.length) { heap[0] = last; let i = 0;
      for (;;) { const l = 2*i+1, r = l+1; let m = i;
        if (l < heap.length && heap[l].db < heap[m].db) m = l;
        if (r < heap.length && heap[r].db < heap[m].db) m = r;
        if (m === i) break; [heap[m], heap[i]] = [heap[i], heap[m]]; i = m; } }
    return top; };

  // Seeds have no incoming leg, so their first hop is charged no bend (from === entry),
  // and an unpinched bottleneck (1 = a full cell face).
  const constrictDb = f => Math.max(MIN_APERTURE_DB, 10 * Math.log10(Math.max(1e-4, f)));
  for (const [nd, cost] of seeded) {
    const seed = { portal: -1, db: cost, add: cost, bneck: 1, len: 0,
                   cx: srcPt.px, cy: srcPt.py, cz: srcPt.pz, ...srcPt };
    best[nd] = [seed];
    push({ node: nd, ...seed, qx: srcPt.px, qy: srcPt.py, qz: srcPt.pz });
  }

  let popped = 0;
  while (heap.length) {
    const cur = pop();
    popped++;
    const entries = best[cur.node];
    // Skip if this state is no longer among the kept entries for its node.
    if (!entries || !entries.some(e => e.db === cur.db && e.px === cur.px)) continue;
    for (const pid of G.nodes[cur.node].edges) {
      const P = G.portals[pid];
      const other = P.a === cur.node ? P.b : P.a;
      const leg = Math.hypot(P.px - cur.px, P.py - cur.py, P.pz - cur.pz);
      if (leg < 1e-6) continue;
      // BEND: angle between the leg that brought us to this entry point and the leg
      // leaving through this portal. cos=+1 straight (free), cos=0 right angle (half),
      // cos=-1 doubled back (full). Open-air straight runs therefore pay nothing.
      let bend = 0;
      const ix = cur.px - cur.qx, iy = cur.py - cur.qy, iz = cur.pz - cur.qz;
      const ilen = Math.hypot(ix, iy, iz);
      if (ilen > 1e-6) {
        const cos = (ix * (P.px - cur.px) + iy * (P.py - cur.py) + iz * (P.pz - cur.pz)) / (ilen * leg);
        // scaled by how PINCHED this portal is: turning in open space is free
        bend = BEND_DB * (1 - Math.max(-1, Math.min(1, cos))) / 2 * (1 - P.openFrac);
      }
      // LENGTH: summing leg-by-leg between portal CENTROIDS staircases on any route that
      // is not axis-aligned, which showed up as the control probe reading -2.3 dB in an
      // open room where it must read 0 -- the same Manhattan-inflation defect the shipped
      // model has, in a new form. An UNOBSTRUCTED route is physically a straight line, so
      // measure it as one; only once the route has actually squeezed through something
      // does the bent polyline become the honest measure.
      // Track the tightest constriction AND where it is: whichever of the node's throat or
      // this portal is narrower, if either beats the running minimum.
      const nodeCur = G.nodes[cur.node];
      const nBneckPre = Math.min(cur.bneck, nodeCur.throat, P.openFrac);
      let ccx = cur.cx, ccy = cur.cy, ccz = cur.cz;
      if (nBneckPre < cur.bneck - 1e-9) {
        if (nodeCur.throat <= P.openFrac) { ccx = nodeCur.tpx; ccy = nodeCur.tpy; ccz = nodeCur.tpz; }
        else { ccx = P.px; ccy = P.py; ccz = P.pz; }
      }
      // BLEND, never switch. A hard threshold here put a step in the route length exactly
      // where a listener walks through a doorway -- visible as a 2.4 dB lurch in 50 ms on
      // the approach render, against the shipped model's worst of 0.9. An open route is a
      // straight line and a squeezed one follows its bent polyline; between them it eases.
      const straight = Math.hypot(P.px - srcPt.px, P.py - srcPt.py, P.pz - srcPt.pz);
      const openMix = sstep(0.70, 0.98, nBneckPre);
      const nLen = openMix * straight + (1 - openMix) * (cur.len + leg);
      const nAdd = openMix * (SLOPE * straight) + (1 - openMix) * (cur.add + SLOPE * leg) + bend;
      // The route crossed this node to reach the portal, so it passed the node's throat
      // as well as the portal's own opening. Both feed the running bottleneck.
      const nBneck = nBneckPre;
      const nd = nAdd - constrictDb(nBneck);   // constriction paid ONCE, at the bottleneck
      let arr = best[other];
      if (!arr) { arr = best[other] = []; }
      // keep the KEEP_PORTALS cheapest, one per distinct portal
      const existing = arr.findIndex(e => e.portal === pid);
      if (existing >= 0) { if (arr[existing].db <= nd) continue; arr.splice(existing, 1); }
      else if (arr.length >= KEEP_PORTALS && nd >= arr[arr.length - 1].db) continue;
      arr.push({ portal: pid, db: nd, add: nAdd, bneck: nBneck, len: nLen, px: P.px, py: P.py, pz: P.pz, cx: ccx, cy: ccy, cz: ccz });
      arr.sort((a, b) => a.db - b.db);
      if (arr.length > KEEP_PORTALS) arr.length = KEEP_PORTALS;
      push({ node: other, db: nd, add: nAdd, bneck: nBneck, len: nLen, px: P.px, py: P.py, pz: P.pz,
             cx: ccx, cy: ccy, cz: ccz, qx: cur.px, qy: cur.py, qz: cur.pz });
    }
  }
  return { best, srcNode: -2, popped, seeded: seeded.size };
}

// ── per-voxel evaluation: AIR arrival + SOLID arrival, energy-summed ───────────
const constrictDbEv = f => Math.max(MIN_APERTURE_DB, 10 * Math.log10(Math.max(1e-4, f)));

function makeNewEvaluator(D, G, src) {
  const { best } = solveAir(D, G, src);
  const cellSize = G.cell;
  return function (wx, wy, wz) {
    const d = Math.hypot(wx - src.x, wy - src.y, wz - src.z);
    // Material on the LISTENER's own line of sight -- distinct from the fan's best ray,
    // which deliberately looks for a weaker patch of barrier nearby.
    const centerMat = segDb(D, src.x, src.y, src.z, wx, wy, wz);

    // AIR: min over this node's kept entry portals of (route dB + last leg), where the
    // last leg is EXACT continuous geometry from the portal point to the listener.
    // Every KEPT entry portal is a real arrival, and they are SUMMED. Taking only the
    // loudest made the perceived direction snap between portals as the winner changed --
    // audible as the stereo image jumping around on a walk (L-R swinging several dB
    // block to block in the render). Summing them is also the melding the spec asks for:
    // two doorways into one room both stay live, and one taking over from the other is a
    // crossfade rather than a switch. `airs` carries them for the renderer; `airDb` is
    // their energy sum for the measurement paths.
    // Kept entries are CLUSTERED before summing. Several of them are usually different cell
    // faces of the SAME opening carrying the same sound, and summing those inflated an open
    // doorway to +3 dB above free field -- the same double-count as air-vs-solid, one level
    // down. Genuinely separate openings (two doorways into one room) are far apart, so
    // spatial clustering separates the cases: one arrival per cluster, best of each, summed.
    let airPow = 0, airLen = Infinity, entry = null;
    const airs = [];
    const nd = G.nodeOf[D.idx(wx, wy, wz)];
    if (nd >= 0 && best[nd]) {
      // The listener's OWN node has a throat that the route has not paid for yet -- a node's
      // constriction is charged when a route LEAVES it through a portal. So standing IN a
      // doorway cost nothing while one step beyond it cost the entire throat, which is the
      // 2.8 dB lurch the approach render showed at exactly the moment of walking through.
      // Spread that charge over the aperture's own scale: at the throat itself, nothing; a
      // cell away from it, all of it.
      const myNode = G.nodes[nd];
      const throatDist = Math.hypot(myNode.tpx - (wx + 0.5), myNode.tpy - (wy + 0.5), myNode.tpz - (wz + 0.5));
      const throatW = sstep(0, cellSize, throatDist);
      const cands = [];
      for (const e of best[nd]) {
        const leg = Math.hypot(e.px - (wx + 0.5), e.py - (wy + 0.5), e.pz - (wz + 0.5));
        const tightened = Math.min(e.bneck, myNode.throat);
        const extra = throatW * (constrictDbEv(tightened) - constrictDbEv(e.bneck));
        // An UNOBSTRUCTED route is the straight line from source to listener, so its level
        // must be measured that way. Measuring source->portal->listener instead overstates
        // it by however far the portal sits off that line -- and by a DIFFERENT amount for
        // each node's chosen portal, which is exactly the 2.2 dB step the trace showed at a
        // node boundary, plus a level that fell then rose while walking straight at the
        // speaker. Blended on the route's bottleneck, so squeezed routes keep the honest
        // bent-polyline measure.
        // STRING PULL at the final opening. The route's non-distance losses are kept, but the
        // geometry is re-measured through the point of the opening CLOSEST TO the straight
        // line from source to listener, rather than through its centroid. Two adjacent nodes
        // choosing different portals then converge on nearly the same taut path, which is
        // what removes the node-boundary step -- and it is what makes "coarse topology,
        // exact geometry" actually true rather than merely intended.
        const P = e.portal >= 0 ? G.portals[e.portal] : null;
        let pulledLen = e.len + leg;
        if (P) {
          const cx2 = Math.max(P.x0, Math.min(P.x1 + 1, 0.5 * (src.x + 0.5 + wx + 0.5)));
          const cy2 = Math.max(P.y0, Math.min(P.y1 + 1, 0.5 * (src.y + 0.5 + wy + 0.5)));
          const cz2 = Math.max(P.z0, Math.min(P.z1 + 1, 0.5 * (src.z + 0.5 + wz + 0.5)));
          pulledLen = Math.hypot(cx2 - (src.x + 0.5), cy2 - (src.y + 0.5), cz2 - (src.z + 0.5))
                    + Math.hypot(cx2 - (wx + 0.5), cy2 - (wy + 0.5), cz2 - (wz + 0.5));
          pulledLen = Math.max(d, pulledLen);
        }
        const losses = e.db - SLOPE * e.len;          // bend + constriction + seed material
        const oMix = sstep(0.70, 0.98, e.bneck);
        const viaPortal = losses + SLOPE * pulledLen;
        const direct = SLOPE * d + (constrictDbEv(1) - constrictDbEv(e.bneck));
        const usedLen = oMix * d + (1 - oMix) * pulledLen;
        const cand = -(oMix * direct + (1 - oMix) * viaPortal - extra);
        // APPARENT POSITION. Sound comes from the last aperture it squeezed through -- an
        // unconstricted route comes from the source itself. Using the entry PORTAL centroid
        // instead put the apparent source on a cell FACE, so direction moved in cell-sized
        // jumps and the stereo image lurched as the winning portal changed (mean L-R step
        // 3.3 dB per second on a walk, against 0.15 for the shipped model). A constriction
        // is a fixed feature of the geometry, so the bearing to it varies smoothly as the
        // listener moves -- and it is also what the author asked for: a gap reads as a
        // localised source AT the gap.
        // Where it SEEMS to come from, blended on whether the listener can SEE the source.
        // That is the physical discriminator: with a clear line the sound comes from the
        // speaker, and with the line blocked it comes from the last aperture it squeezed
        // through. Blending on the route's bottleneck instead put a hard switch right where
        // someone walks through a doorway. Ramped over the first few dB of blockage, so
        // stepping out from behind a wall slides the image rather than teleporting it.
        const seeW = sstep(0, 6, centerMat);
        const apx = (1 - seeW) * (src.x + 0.5) + seeW * e.cx;
        const apy = (1 - seeW) * (src.y + 0.5) + seeW * e.cy;
        const apz = (1 - seeW) * (src.z + 0.5) + seeW * e.cz;
        if (cand > -110) cands.push({ id: e.portal, db: cand, px: apx, py: apy, pz: apz, len: usedLen });
      }
      cands.sort((a, b) => b.db - a.db);
      for (const c of cands) {
        let dup = false;
        for (const k of airs)
          if (Math.hypot(k.px - c.px, k.py - c.py, k.pz - c.pz) < CLUSTER_BLOCKS) { dup = true; break; }
        if (dup) continue;
        airs.push(c);
        airPow += dbToPow(c.db);
        if (entry === null) { entry = c; airLen = c.len; }
      }
    }
    const airDb = airPow > 0 ? powToDb(airPow) : -120;

    // SOLID (transmitted) arrival. Two corrections the probe measurements forced:
    //
    // 1. A FAN, not one ray. A barrier is a distributed radiator: standing directly behind
    //    a vault door set in a WOODEN wall, what you hear is the wall beside you, not the
    //    door. A single straight ray reported -60 dB there (the door), where the spec says
    //    it must not seal. Taking the best of a small fan of offset listener positions
    //    finds the weakest nearby point of the barrier, which is what actually radiates.
    //    +/-2 blocks is also about the scale over which real hearing averages.
    // 2. NOT DOUBLE-COUNTED with the air arrival. When the straight line is clear, the
    //    "transmitted" path IS the direct sound and the graph route is the same path, so
    //    summing them inflated open space by 3 dB (the control probe read +2.0 where it
    //    must read 0). Transmission is weighted in only once there is something to
    //    transmit through, ramped over the first few dB so there is no discontinuity.
    // Only rays that are genuinely BLOCKED count as transmission. A fan ray that finds a
    // clear line has found an OPENING, not a partition -- and that opening is the air
    // arrival's business, where it correctly pays the aperture's constriction. Letting such
    // a ray through as unattenuated "transmission" bypassed that charge and made scenario
    // #6 non-monotonic: a shut door (-5.3, through the leaf) came out LOUDER than the same
    // door 1/3 open (-7.0, squeezing through the crack), because opening it handed the
    // solid path a free ride.
    let solidDb = -Infinity, matDb = 0;
    for (const [ox, oy, oz] of SOLID_FAN) {
      const px = wx + ox, py = wy + oy, pz = wz + oz;
      if (!D.inB(px, py, pz)) continue;
      const m = segDb(D, src.x, src.y, src.z, px, py, pz);
      if (m < MIN_TRANSMIT_DB) continue;         // an opening, not a barrier
      const dd = Math.hypot(px - src.x, py - src.y, pz - src.z);
      const cand = -(SLOPE * dd + m);
      if (cand > solidDb) { solidDb = cand; matDb = m; }
    }
    if (solidDb === -Infinity) { solidDb = -120; matDb = 0; }

    const total = powToDb(dbToPow(airDb) + dbToPow(solidDb));
    return { d, airDb, solidDb, total, airLen, matDb, entry, airs,
             reached: nd >= 0 && !!best[nd] };
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
module.exports = { buildGraph, solveAir, makeNewEvaluator, measure, makeOpenTest, segDb, SLOPE };
