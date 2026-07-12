"use strict";
// ─────────────────────────────────────────────────────────────────────────────
// judge3d — replay the CMM world-emitter acoustics model over REAL POI geometry
// exported by `cmm acsweep` (see Harmony/EmitterAcousticsSweep.cs), validate the
// port against the in-game parity floods, and run anomaly detectors over every
// air cell. 3D successor of judge.js (which runs hand-drawn 2D grids).
//
//   node judge3d.js [--dir dumps] [--poi substring] [--viewer viewer/]
//
// The model port mirrors Harmony/EmitterAcoustics.cs exactly (Dial's bucket
// flood, CostScale 4, MaxBucket 2048, NodeBudget 8000, boundary blend, shaped
// occlusion, hard-stop gate). Known, accepted divergence: the dump quantizes
// CellLoss to 1/32 steps (the live flood used full floats), so parity is judged
// by tolerance, not bit-equality.
// ─────────────────────────────────────────────────────────────────────────────
const fs = require("fs"), path = require("path"), zlib = require("zlib");

// ── shipped constants (EmitterAcoustics.cs) ─────────────────────────────────
const CS = 4, MAXB = 2048, NODE_BUDGET = 8000, MAXR = 32, DIM = 65, DIM2 = DIM * DIM;
const REF_RANGE = 30;          // ShapeOcclusion RefRangeBlocks
const RANGE_MULT = 1.5;        // WorldEmitterReverbRangeMult (not in dump config)
const DEPTH_AMT = 0.7;         // carry depth grading (judge.js K.DepthAmt)
const FLAG = { air: 1, water: 2, seeThrough: 4, terrain: 8, solidCube: 16, emitter: 32 };

// Unity Mathf.RoundToInt is banker's rounding (0.5 → even).
function bround(v) {
  const f = Math.floor(v), d = v - f;
  if (d > 0.5) return f + 1;
  if (d < 0.5) return f;
  return (f % 2 === 0) ? f : f + 1;
}
const smooth = (a, b, x) => { if (b <= a) return x >= b ? 1 : 0; const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
const db = a => a <= 1e-4 ? -80 : 20 * Math.log10(a);

// ── dump loader ──────────────────────────────────────────────────────────────
function loadDump(file) {
  const d = JSON.parse(fs.readFileSync(file, "utf8"));
  const { x, y, z, sx, sy, sz } = d.bounds;
  const n = sx * sy * sz;
  const lossB = Buffer.from(d.loss, "base64");
  const flags = Buffer.from(d.flags, "base64");
  if (lossB.length !== n || flags.length !== n) throw new Error(`${d.poi}: bad array sizes`);
  const loss = new Float32Array(n);
  for (let i = 0; i < n; i++) loss[i] = lossB[i] / d.lossScale;
  return {
    poi: d.poi, cfg: d.config, x0: x, y0: y, z0: z, sx, sy, sz, n,
    fields: d.fields || null, fieldSamples: d.fieldSamples || null,   // dump v2 (Phase 2)
    loss, flags, emitters: d.emitters, floods: d.floods, blocks: d.blocks,
    blockIdx: Buffer.from(d.block, "base64"),
    idx(wx, wy, wz) { return (wx - x) + sx * ((wz - z) + sz * (wy - y)); },
    inB(wx, wy, wz) { return wx >= x && wx < x + sx && wy >= y && wy < y + sy && wz >= z && wz < z + sz; },
    // Loss for the flood/rays: outside the dump box, below = solid ground, else open air.
    lossAt(wx, wy, wz) {
      if (!this.inB(wx, wy, wz)) return wy < y ? 0.7 : 0;
      return this.loss[this.idx(wx, wy, wz)];
    },
    isAir(wx, wy, wz) { return this.inB(wx, wy, wz) && (this.flags[this.idx(wx, wy, wz)] & FLAG.air) !== 0; },
  };
}

// ── exact mirror of the shipped ear-anchored flood (FloodBegin + FloodStep) ──
// Returns local 65³ cost/wall fields anchored at `ear` (cell coords), exactly as
// EmitterAcoustics leaves them: Dial's buckets, LIFO within a bucket, stale-skip,
// relax order +x,-x,+y,-y,+z,-z, NodeBudget truncation mid-bucket.
function mirrorFlood(D, earX, earY, earZ, effR, wallDist, budget = NODE_BUDGET, maxR = MAXR) {
  const dim = maxR * 2 + 1, dim2 = dim * dim;
  const cost = new Int32Array(dim * dim * dim).fill(-1);   // -1 = Inf
  const wall = new Int32Array(dim * dim * dim);
  const next = new Int32Array(dim * dim * dim);
  const head = new Int32Array(MAXB + 1).fill(-1);
  const minX = earX - maxR, minY = earY - maxR, minZ = earZ - maxR;
  const wcs = bround(wallDist * CS);
  const si = maxR + maxR * dim + maxR * dim2;
  cost[si] = 0; head[0] = si; next[si] = -1;
  let settled = 0;

  const relax = (wx, wy, wz, c, pw) => {
    if (wy < 0 || wy >= 256) return;
    if (wx < earX - effR || wx > earX + effR || wy < earY - effR || wy > earY + effR ||
        wz < earZ - effR || wz > earZ + effR) return;
    const j = (wx - minX) + (wy - minY) * dim + (wz - minZ) * dim2;
    const wallEnter = bround(wcs * D.lossAt(wx, wy, wz));
    const nc = c + CS + wallEnter;
    if (nc > MAXB) return;
    if (cost[j] !== -1 && nc >= cost[j]) return;
    cost[j] = nc; wall[j] = pw + wallEnter;
    next[j] = head[nc]; head[nc] = j;
  };

  outer:
  for (let c = 0; c <= MAXB && settled < budget; c++) {
    let i = head[c];
    while (i !== -1) {
      if (settled >= budget) break outer;
      const ni = next[i];
      if (cost[i] === c) {
        settled++;
        const lz = (i / dim2) | 0, rem = i - lz * dim2, ly = (rem / dim) | 0, lx = rem - ly * dim;
        const wx = lx + minX, wy = ly + minY, wz = lz + minZ, pw = wall[i];
        relax(wx + 1, wy, wz, c, pw); relax(wx - 1, wy, wz, c, pw);
        relax(wx, wy + 1, wz, c, pw); relax(wx, wy - 1, wz, c, pw);
        relax(wx, wy, wz + 1, c, pw); relax(wx, wy, wz - 1, c, pw);
      }
      i = ni;
    }
  }
  return { cost, wall, minX, minY, minZ, settled, earX, earY, earZ, effR, dim, dim2 };
}

// SampleArrival mirror: min cost / min wall over the 3×3×3 around a cell.
function sampleArrival(F, wx, wy, wz) {
  let best = -1, bestWall = 0, minWall = Infinity;
  for (let dz = -1; dz <= 1; dz++) for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
    const lx = wx + dx - F.minX, ly = wy + dy - F.minY, lz = wz + dz - F.minZ;
    if (lx < 0 || lx >= F.dim || ly < 0 || ly >= F.dim || lz < 0 || lz >= F.dim) continue;
    const id = lx + ly * F.dim + lz * F.dim2, c = F.cost[id];
    if (c === -1) continue;
    if (best === -1 || c < best) { best = c; bestWall = F.wall[id]; }
    if (F.wall[id] < minWall) minWall = F.wall[id];
  }
  return { cost: best, wall: bestWall, wallMin: minWall === Infinity ? 0 : minWall };
}

// SegmentLoss mirror (Amanatides–Woo DDA, endpoints excluded), cell centers.
function segLoss(D, x0, y0, z0, x1, y1, z1) {
  const fx = x0 + 0.5, fy = y0 + 0.5, fz = z0 + 0.5;
  const tx = x1 + 0.5, ty = y1 + 0.5, tz = z1 + 0.5;
  let dx = tx - fx, dy = ty - fy, dz = tz - fz;
  const dist = Math.hypot(dx, dy, dz);
  if (dist < 0.01) return 0;
  dx /= dist; dy /= dist; dz /= dist;
  let x = x0, y = y0, z = z0;
  const stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0, stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0, stepZ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
  const tMax = (o, d, c, s) => Math.abs(d) < 1e-6 ? Infinity : ((s > 0 ? c + 1 : c) - o) / d;
  const tDel = d => Math.abs(d) < 1e-6 ? Infinity : Math.abs(1 / d);
  let tMaxX = tMax(fx, dx, x, stepX), tMaxY = tMax(fy, dy, y, stepY), tMaxZ = tMax(fz, dz, z, stepZ);
  const tDx = tDel(dx), tDy = tDel(dy), tDz = tDel(dz);
  let loss = 0;
  for (let g = 0; g < 256; g++) {
    if (x === x1 && y === y1 && z === z1) break;
    if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { x += stepX; tMaxX += tDx; }
    else if (tMaxY <= tMaxZ) { y += stepY; tMaxY += tDy; }
    else { z += stepZ; tMaxZ += tDz; }
    if (x === x1 && y === y1 && z === z1) break;
    if (y < 0 || y >= 256) continue;
    loss += D.lossAt(x, y, z);
  }
  return loss;
}

// ShapeOcclusion mirror. Returns { extra, gate }.
function shapeOcclusion(cfg, rawExtra, solid, solidGate, range) {
  let k = range / REF_RANGE; if (k < 0.05) k = 0.05;
  let detour = rawExtra - solid; if (detour < 0) detour = 0;
  solid *= k;
  rawExtra = detour + solid;
  let ceiling = cfg.maxDistance * k; if (ceiling < 0.001) ceiling = 0.001;
  const audible = ceiling * Math.tanh(rawExtra / ceiling);
  const hardStop = cfg.hardStop * k;
  const gate = smooth(hardStop * 0.55, hardStop, solidGate * k);
  return { extra: audible + gate * range, gate };
}

// ── per-cell model evaluation against ONE emitter (reciprocal: flood seeded at
// the emitter; each air cell plays "the ear"). Mirrors FinishSpeakers' math. ──
// Largest radius whose flood COMPLETES within the node budget (the "budget-coherent
// radius" of fix 1.2). Binary search, a handful of floods — the in-game equivalent is a
// cheap adaptive controller (shrink when capped, grow when slack).
function clampedRadius(D, em, budget) {
  let lo = 4, hi = Math.min(Math.max(D.cfg.floodRadius, 1), MAXR), best = lo;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const F = mirrorFlood(D, em.x, em.y, em.z, mid, D.cfg.wallDistance, budget);
    if (F.settled < budget) { best = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return best;
}

// opts: { anchorOwn (fix 1.1), clampR (fix 1.2), fan (fix 1.3, line count),
//         budget (Phase 2: lift the node budget so the field fills its box),
//         maxR/effR (Phase 2 sizing: bigger cached field box) }
function makeEvaluator(D, em, opts = {}) {
  const budget = opts.budget || NODE_BUDGET;
  const maxR = opts.maxR || MAXR;
  let effR = opts.effR || Math.min(Math.max(D.cfg.floodRadius, 1), maxR);
  if (opts.clampR && !opts.budget) effR = clampedRadius(D, em, budget);
  const F = mirrorFlood(D, em.x, em.y, em.z, effR, D.cfg.wallDistance, budget, maxR);
  // Lazy caches over the dump grid (shared across cells; boundary marches revisit cells).
  const arrCost = new Int32Array(D.n).fill(-2), arrWallMin = new Int32Array(D.n);
  const arrival = (wx, wy, wz) => {
    if (!D.inB(wx, wy, wz)) return sampleArrival(F, wx, wy, wz);
    const i = D.idx(wx, wy, wz);
    if (arrCost[i] === -2) { const a = sampleArrival(F, wx, wy, wz); arrCost[i] = a.cost; arrWallMin[i] = a.wallMin; return a; }
    return { cost: arrCost[i], wallMin: arrWallMin[i] };
  };

  const evalCell = function (wx, wy, wz) {
    const d = Math.hypot(wx - em.x, wy - em.y, wz - em.z);
    const man = Math.abs(wx - em.x) + Math.abs(wy - em.y) + Math.abs(wz - em.z);
    const cheb = Math.max(Math.abs(wx - em.x), Math.abs(wy - em.y), Math.abs(wz - em.z));
    let raw, solid, solidGate, branch;
    const lx = wx - F.minX, ly = wy - F.minY, lz = wz - F.minZ;
    const inDim = lx >= 0 && lx < F.dim && ly >= 0 && ly < F.dim && lz >= 0 && lz < F.dim;
    const c0 = inDim ? F.cost[lx + ly * F.dim + lz * F.dim2] : -1;
    if (c0 !== -1 && cheb <= F.effR) {
      raw = Math.max(0, c0 / CS - man);
      solid = F.wall[lx + ly * F.dim + lz * F.dim2] / CS;
      solidGate = arrival(wx, wy, wz).wallMin / CS;
      branch = "R";
    } else {
      // BoundaryToward mirror: march start → emitter, anchor at the first point the flood
      // reached. Fix 1.1 (anchorOwn): the POINT ITSELF must be reached (its cost already
      // carries every wall on its route — nothing can hop a slab); baseline uses the
      // shipped 3×3×3 neighbourhood min. Fix 1.3 (fan): try several jittered start
      // lines and keep the acoustically best anchor (min shaped extra).
      const march = (sx0, sy0, sz0) => {
        const ddx = em.x - sx0, ddy = em.y - sy0, ddz = em.z - sz0;
        const steps = Math.max(Math.abs(ddx), Math.abs(ddy), Math.abs(ddz));
        for (let s = 1; s <= steps; s++) {
          const px = sx0 + bround(ddx * s / steps), py = sy0 + bround(ddy * s / steps), pz = sz0 + bround(ddz * s / steps);
          let bCost = -1, bWall = 0;
          if (opts.anchorOwn) {
            const plx = px - F.minX, ply = py - F.minY, plz = pz - F.minZ;
            if (plx >= 0 && plx < F.dim && ply >= 0 && ply < F.dim && plz >= 0 && plz < F.dim) {
              const pid = plx + ply * F.dim + plz * F.dim2;
              if (F.cost[pid] !== -1) { bCost = F.cost[pid]; bWall = F.wall[pid]; }
            }
          } else {
            const a = arrival(px, py, pz);
            if (a.cost >= 0) { bCost = a.cost; bWall = a.wallMin; }
          }
          if (bCost >= 0) {
            const routed = bCost / CS;
            const remMan = Math.abs(wx - px) + Math.abs(wy - py) + Math.abs(wz - pz);
            const remWall = segLoss(D, px, py, pz, wx, wy, wz) * D.cfg.wallDistance;
            return { raw: Math.max(0, routed + remMan + remWall - man),
                     solid: bWall / CS + remWall, solidGate: bWall / CS };
          }
        }
        return null;
      };

      // Fan starts: the cell itself ± 1 block on the two axes perpendicular to the
      // dominant direction (adjacent listener cells share most of these lines → smooth).
      const starts = [[wx, wy, wz]];
      if ((opts.fan || 1) > 1) {
        const adx = Math.abs(em.x - wx), ady = Math.abs(em.y - wy), adz = Math.abs(em.z - wz);
        const axes = adx >= ady && adx >= adz ? [[0,1,0],[0,0,1]] : ady >= adz ? [[1,0,0],[0,0,1]] : [[1,0,0],[0,1,0]];
        for (const [ax, ay, az] of axes) { starts.push([wx + ax, wy + ay, wz + az], [wx - ax, wy - ay, wz - az]); }
      }
      let bestCand = null, bestExtra = Infinity;
      for (const [sx0, sy0, sz0] of starts) {
        const cand = march(sx0, sy0, sz0);
        if (!cand) continue;
        const sh = shapeOcclusion(D.cfg, cand.raw, cand.solid, cand.solidGate, D.cfg.range);
        if (sh.extra < bestExtra) { bestExtra = sh.extra; bestCand = cand; }
      }
      if (bestCand) {
        raw = bestCand.raw; solid = bestCand.solid; solidGate = bestCand.solidGate;
        branch = "B";
      } else {
        raw = segLoss(D, em.x, em.y, em.z, wx, wy, wz) * D.cfg.wallDistance;
        solid = raw; solidGate = raw;
        branch = "F";
      }
    }
    const { extra, gate } = shapeOcclusion(D.cfg, raw, solid, solidGate, D.cfg.range);
    const dEff = d + extra;
    const mn = D.cfg.minDistance, mx = D.cfg.range, span = Math.max(1e-4, mx - mn);
    const att = dEff <= mn ? 1 : dEff >= mx ? 0 : Math.pow((mx - dEff) / span, D.cfg.falloff);
    let airDetour = raw - solid; if (airDetour < 0) airDetour = 0;
    const depth = d + airDetour;
    return { d, raw, solid, solidGate, gate, extra, dEff, att, depth, branch };
  };
  evalCell.effR = effR;
  return evalCell;
}

// ── room probe (ProbeRoom mirror; reach capped at 24 for the mass pass) ──────
const PROBE_DIRS = (() => {
  const a = [[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]];
  for (const sy of [0.577, -0.577]) for (const sx of [0.577, -0.577]) for (const sz of [0.577, -0.577]) a.push([sx, sy, sz]);
  a.push([0.707,0,0.707],[-0.707,0,0.707],[0.707,0,-0.707],[-0.707,0,-0.707]);
  return a;
})();
const PROBE_REACH = 24;

// Reflectivity approximated from transmission loss (the dump doesn't carry
// SurfaceCategory; glass's high-reflect/low-loss split is lost — noted).
function reflectOf(D, i) {
  if (D.flags[i] & FLAG.water) return 0.85;
  if (D.flags[i] & FLAG.terrain) return 0.3;
  const l = D.loss[i];
  return Math.max(0.15, Math.min(0.95, 0.25 + 0.75 * Math.min(1, l)));
}

function makeProber(D) {
  const cache = new Float64Array(D.n).fill(-1);   // packed enc(0..1)*1e3 | roomSize*1e-? — store 3 vals in 2 arrays instead
  const encA = new Float32Array(D.n).fill(-1), refA = new Float32Array(D.n), sizeA = new Float32Array(D.n), roofA = new Uint8Array(D.n);
  return function probe(wx, wy, wz) {
    const ci = D.idx(wx, wy, wz);
    if (encA[ci] >= 0) return { enc: encA[ci], reflect: refA[ci], roomSize: sizeA[ci], roofed: roofA[ci] === 1 };
    let hits = 0, sumR = 0; const dists = [];
    let roofed = false;
    for (let k = 0; k < PROBE_DIRS.length; k++) {
      const [dx, dy, dz] = PROBE_DIRS[k];
      let hit = false, dist = PROBE_REACH;
      for (let t = 1; t <= PROBE_REACH * 2; t++) {
        const s = t * 0.5;
        if (s > PROBE_REACH) break;
        const px = Math.round(wx + dx * s), py = Math.round(wy + dy * s), pz = Math.round(wz + dz * s);
        if (px === wx && py === wy && pz === wz) continue;
        if (!D.inB(px, py, pz)) { if (py < D.y0) { hit = true; dist = s; sumR += 0.3; } break; }
        const i = D.idx(px, py, pz);
        // Probe stops at opaque surfaces: approximated as loss ≥ 0.1 and not see-through.
        if ((D.flags[i] & FLAG.water) || (D.loss[i] >= 0.1 && !(D.flags[i] & FLAG.seeThrough))) {
          hit = true; dist = s; sumR += (D.flags[i] & FLAG.water) ? 0.85 : reflectOf(D, i);
          break;
        }
      }
      if (hit) { hits++; if (k === 2) roofed = true; }
      dists.push(dist);
    }
    dists.sort((a, b) => a - b);
    const top = Math.max(1, PROBE_DIRS.length >> 2);
    let ts = 0; for (let i = dists.length - top; i < dists.length; i++) ts += dists[i];
    const r = { enc: hits / PROBE_DIRS.length, reflect: hits ? sumR / hits : 0, roomSize: ts / top, roofed };
    encA[ci] = r.enc; refA[ci] = r.reflect; sizeA[ci] = r.roomSize; roofA[ci] = roofed ? 1 : 0;
    return r;
  };
}

// Combine dry + reverb buses (mirrors judge.js v29 == the native routing).
function buses(D, cell, pr) {
  const cfg = D.cfg, mn = cfg.minDistance, mx = cfg.range;
  const roomWet = pr.enc * pr.reflect * (pr.roofed ? 1 : 0.5);
  const rmx = mx * RANGE_MULT;
  const attWet = cell.d >= rmx ? 0 : Math.pow((rmx - cell.d) / rmx, cfg.falloff * 0.5);
  const depthFac = Math.exp(-1.2 * DEPTH_AMT * Math.min(2, Math.max(0, (cell.depth - mn) / 48)));
  const matDamp = Math.pow(0.5, cell.solidGate / (0.5 * cfg.wallDistance));
  const carry = roomWet * cfg.reverbWet * cfg.reverbCarry * (1 - cell.gate) * matDamp * depthFac * attWet;
  const local = roomWet * cfg.reverbWet * cell.att;
  const total = Math.sqrt(cell.att * cell.att + carry * carry + local * local);
  return { carry, local, total };
}

// ── idealized-physics reference (3D port of judge.js refFloodFn) ─────────────
// Per-cell transmission dB approximated from loss; spreading loss from walked path length.
function refDbOf(D, i) {
  if (D.flags[i] & FLAG.water) return 15;
  const l = D.loss[i];
  if (l <= 0) return (D.flags[i] & FLAG.seeThrough) ? 1 : 0;
  return Math.min(60, 50 * l + ((D.flags[i] & FLAG.seeThrough) ? 15 : 0)); // intact glass panes read ~22
}
function refFlood(D, em) {
  const Q = 4, CAP = 4096;
  const best = new Int32Array(D.n).fill(-1), len = new Float32Array(D.n), rdb = new Float32Array(D.n);
  const head = new Int32Array(CAP + 1).fill(-1), next = new Int32Array(D.n);
  const si = D.idx(em.x, em.y, em.z);
  best[si] = 0; head[0] = si; next[si] = -1;
  const sx = D.sx, sz = D.sz;
  for (let c = 0; c <= CAP; c++) {
    let i = head[c];
    while (i !== -1) {
      const ni = next[i];
      if (best[i] === c) {
        const wy = D.y0 + ((i / (sx * sz)) | 0), rem = i % (sx * sz), wz = D.z0 + ((rem / sx) | 0), wx = D.x0 + (rem % sx);
        for (const [dx, dy, dz] of [[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]]) {
          const nx = wx + dx, ny = wy + dy, nz = wz + dz;
          if (!D.inB(nx, ny, nz)) continue;
          const j = D.idx(nx, ny, nz);
          const stepDb = refDbOf(D, j);
          const nc = c + Q + Math.round(stepDb * 0.8 * Q);
          if (nc > CAP) continue;
          if (best[j] !== -1 && nc >= best[j]) continue;
          best[j] = nc; len[j] = len[i] + 1; rdb[j] = rdb[i] + stepDb;
          next[j] = head[nc]; head[nc] = j;
        }
      }
      i = ni;
    }
  }
  return { best, len, rdb };
}
function refAt(R, i, pr) {
  if (R.best[i] === -1) return { dryDb: -80, amp: 0 };
  const L = Math.max(1, R.len[i]);
  const dryDb = -(20 * Math.log10(L) + R.rdb[i]);
  const dryAmp = Math.pow(10, Math.max(-60, dryDb) / 20);
  const wet = pr.enc * pr.reflect * Math.sqrt(dryAmp);
  return { dryDb, amp: Math.min(1, Math.hypot(dryAmp, wet)) };
}

// ── parity: mirror each dumped ear-flood, compare cell-for-cell ──────────────
function parity(D) {
  const out = [];
  for (const fl of D.floods) {
    const [ex, ey, ez] = fl.ear;
    const F = mirrorFlood(D, ex, ey, ez, Math.min(Math.max(D.cfg.floodRadius, 1), MAXR), D.cfg.wallDistance);
    const c = Buffer.from(fl.cells, "base64");
    const cells = c.length / 7;
    let exact = 0, within4 = 0, sumAbs = 0, maxAbs = 0, missing = 0, maxAtCost = 0;
    for (let i = 0; i < cells; i++) {
      const o = i * 7;
      const lx = c[o] - 32 + MAXR, ly = c[o + 1] - 32 + MAXR, lz = c[o + 2] - 32 + MAXR;
      const cost = c[o + 3] | (c[o + 4] << 8);
      const mc = F.cost[lx + ly * DIM + lz * DIM2];
      if (mc === -1) { missing++; continue; }
      const dv = Math.abs(mc - cost);
      if (dv === 0) exact++;
      if (dv <= 4) within4++;
      sumAbs += dv; if (dv > maxAbs) { maxAbs = dv; maxAtCost = cost; }
    }
    // dump cells not reached by mirror + mirror cells beyond dump are both "missing-ish";
    // count mirror-side extras too
    let extras = 0;
    const dumped = new Set();
    for (let i = 0; i < cells; i++) { const o = i * 7; dumped.add((c[o] - 32 + MAXR) + (c[o+1] - 32 + MAXR) * DIM + (c[o+2] - 32 + MAXR) * DIM2); }
    for (let i = 0; i < F.cost.length; i++) if (F.cost[i] !== -1 && !dumped.has(i)) extras++;
    out.push({ ear: fl.ear, cells, exact, pctExact: cells ? 100 * exact / cells : 0,
               within4, pctWithin4: cells ? 100 * within4 / cells : 0,
               meanAbs: cells ? sumAbs / cells : 0, maxAbs, maxAtCost, missing, extras,
               settled: F.settled, dumpSettled: fl.settled });
  }
  return out;
}

// ── parity2: validate the C# Phase-2 implementation against this mirror ─────
// (a) each dumped per-speaker FIELD vs mirrorFlood seeded at the speaker, budget lifted;
// (b) each dumped live SampleStation extra[] vs this evaluator in V5 configuration.
function parity2(D) {
  const res = { fields: [], sampleN: 0, sampleMean: 0, sampleMax: 0, sampleWorst: null };
  const fr = D.cfg.fieldRadius || 48;
  for (const fd of D.fields || []) {
    const [px, py, pz] = fd.pos;
    const F = mirrorFlood(D, px, py, pz, fd.effR, D.cfg.wallDistance, 1e9, fd.effR);
    const c = Buffer.from(fd.cells, "base64");
    const cells = c.length / 7;
    let exact = 0, within4 = 0, sumAbs = 0, maxAbs = 0, missing = 0;
    for (let i = 0; i < cells; i++) {
      const o = i * 7;
      const lx = c[o] - 128 + fd.effR, ly = c[o + 1] - 128 + fd.effR, lz = c[o + 2] - 128 + fd.effR;
      const cost = c[o + 3] | (c[o + 4] << 8);
      const mc = F.cost[lx + ly * F.dim + lz * F.dim2];
      if (mc === -1) { missing++; continue; }
      const dv = Math.abs(mc - cost);
      if (dv === 0) exact++;
      if (dv <= 4) within4++;
      sumAbs += dv; if (dv > maxAbs) maxAbs = dv;
    }
    res.fields.push({ pos: fd.pos, cells, pctExact: 100 * exact / Math.max(1, cells),
                      pctWithin4: 100 * within4 / Math.max(1, cells),
                      meanAbs: sumAbs / Math.max(1, cells), maxAbs, missing });
  }
  if (D.fieldSamples && D.fieldSamples.length) {
    const count = Math.min(D.emitters.length, 8);
    const evs = [];
    for (let i = 0; i < count; i++)
      evs.push(makeEvaluator(D, D.emitters[i], { anchorOwn: true, fan: 5, budget: 1e9, maxR: fr, effR: fr }));
    for (const s of D.fieldSamples) {
      const [ex, ey, ez] = s.ear;
      for (let i = 0; i < Math.min(count, s.speakers.length); i++) {
        const mine = evs[i](ex, ey, ez).extra;
        const dv = Math.abs(mine - s.speakers[i].extra);
        res.sampleN++; res.sampleMean += dv;
        if (dv > res.sampleMax) { res.sampleMax = dv; res.sampleWorst = { ear: s.ear, i, cs: s.speakers[i].extra, js: mine }; }
      }
    }
    res.sampleMean /= Math.max(1, res.sampleN);
  }
  return res;
}

// ── detectors (3D ports of judge.js #1-#6) ───────────────────────────────────
function judgeEmitter(D, em, probe, opts = {}) {
  const ev = makeEvaluator(D, em, opts);
  const R = refFlood(D, em);
  const cfg = D.cfg, mx = cfg.range;
  const reach = mx * RANGE_MULT;
  const F = (kind, sev, msg) => findings.push({ kind, sev, msg });
  const findings = [];

  // Evaluate every air cell within reach; store fields for detectors + viewer.
  const n = D.n;
  const totalDb = new Float32Array(n).fill(-999);   // -999 = not evaluated
  const dryDb = new Float32Array(n).fill(-999);
  const gateA = new Float32Array(n);
  const branchA = new Uint8Array(n);                 // R=1 B=2 F=3
  const refDb = new Float32Array(n).fill(-999);
  let nBoth = 0, sumAbs = 0, nEval = 0;
  const leakIn = [], deadIn = [], bigs = [];

  for (let wy = D.y0; wy < D.y0 + D.sy; wy++)
    for (let wz = D.z0; wz < D.z0 + D.sz; wz++)
      for (let wx = D.x0; wx < D.x0 + D.sx; wx++) {
        const i = D.idx(wx, wy, wz);
        if (!(D.flags[i] & (FLAG.air | FLAG.water)) && D.loss[i] > 0.05) continue;   // listeners stand in air-ish cells
        const d = Math.hypot(wx - em.x, wy - em.y, wz - em.z);
        if (d > reach) continue;
        const cell = ev(wx, wy, wz);
        const pr = probe(wx, wy, wz);
        const b = buses(D, cell, pr);
        const r = refAt(R, i, pr);
        nEval++;
        totalDb[i] = db(b.total); dryDb[i] = db(cell.att);
        gateA[i] = cell.gate; branchA[i] = cell.branch === "R" ? 1 : cell.branch === "B" ? 2 : 3;
        refDb[i] = db(r.amp);
        const cdb = totalDb[i], rdb = refDb[i];
        if (cdb > -70 || rdb > -70) {
          if (cdb > -70 && rdb > -70) { nBoth++; sumAbs += Math.abs(cdb - rdb); }
          if (cdb - rdb > 15 && cdb > -35) bigs.push({ wx, wy, wz, cdb, rdb, cell });
          if (cdb > -30 && rdb <= -60) leakIn.push({ wx, wy, wz, cdb, rdb, cell });
          if (rdb > -25 && cdb <= -60 && d < mx * 0.8) deadIn.push({ wx, wy, wz, cdb, rdb, cell });
        }
      }

  if (leakIn.length) {
    const w = leakIn.sort((a, b) => b.cdb - a.cdb)[0];
    F("leak", 3, `${leakIn.length} cells CMM-audible where physics says sealed; worst (${w.wx},${w.wy},${w.wz}) cmm ${w.cdb.toFixed(0)}dB vs ref ${w.rdb.toFixed(0)}dB [${w.cell.branch}, solid ${w.cell.solid.toFixed(1)}, gate ${w.cell.gate.toFixed(2)}]`);
  }
  if (deadIn.length) {
    const w = deadIn.sort((a, b) => b.rdb - a.rdb)[0];
    F("dead", 2, `${deadIn.length} in-range cells physics-loud but CMM silent; worst (${w.wx},${w.wy},${w.wz}) ref ${w.rdb.toFixed(0)}dB vs cmm ${w.cdb.toFixed(0)}dB [${w.cell.branch}, dEff ${w.cell.dEff.toFixed(1)}]`);
  }

  // adjacent-cell cliffs (+x/+y/+z, both evaluated, same-medium)
  const cliffs = { dry: [], revtail: [] };
  for (let wy = D.y0; wy < D.y0 + D.sy - 1; wy++)
    for (let wz = D.z0; wz < D.z0 + D.sz - 1; wz++)
      for (let wx = D.x0; wx < D.x0 + D.sx - 1; wx++) {
        const i = D.idx(wx, wy, wz);
        if (totalDb[i] === -999) continue;
        for (const [dx, dy, dz] of [[1,0,0],[0,1,0],[0,0,1]]) {
          const j = D.idx(wx + dx, wy + dy, wz + dz);
          if (totalDb[j] === -999) continue;
          const a = totalDb[i], b2 = totalDb[j];
          if (Math.max(a, b2) > -40 && Math.abs(a - b2) > 12) {
            const d1 = Math.hypot(wx - em.x, wy - em.y, wz - em.z);
            const zone = d1 < mx ? "dry" : "revtail";
            cliffs[zone].push({ wx, wy, wz, dx, dy, dz, a, b: b2, br: branchA[i] * 10 + branchA[j], g: [gateA[i], gateA[j]] });
          }
        }
      }
  for (const zone of ["dry", "revtail"]) {
    const zs = cliffs[zone];
    if (!zs.length) continue;
    zs.sort((p, q) => Math.abs(q.a - q.b) - Math.abs(p.a - p.b));
    const w = zs[0], brs = "?RBF";
    F("cliff-" + zone, zone === "dry" ? 3 : 2, `${zs.length} adjacent air-cell jumps >12dB (${zone}); worst (${w.wx},${w.wy},${w.wz})→+(${w.dx},${w.dy},${w.dz}): ${w.a.toFixed(0)}→${w.b.toFixed(0)}dB [${brs[(w.br / 10) | 0]}${brs[w.br % 10]}, gate ${w.g[0].toFixed(2)}→${w.g[1].toFixed(2)}]`);
  }

  // non-monotonic dry loudness along clear-air rays from the emitter (26 dirs)
  let nonMono = null;
  const rayDirs = [];
  for (let a = 0; a < 16; a++) rayDirs.push([Math.cos(a * Math.PI / 8), 0, Math.sin(a * Math.PI / 8)]);
  for (let a = 0; a < 8; a++) rayDirs.push([0.866 * Math.cos(a * Math.PI / 4), 0.5, 0.866 * Math.sin(a * Math.PI / 4)], [0.866 * Math.cos(a * Math.PI / 4), -0.5, 0.866 * Math.sin(a * Math.PI / 4)]);
  for (const [dx, dy, dz] of rayDirs) {
    let prev = null;
    for (let t = 1; t <= mx; t++) {
      const wx = Math.round(em.x + dx * t), wy = Math.round(em.y + dy * t), wz = Math.round(em.z + dz * t);
      if (!D.inB(wx, wy, wz)) break;
      const i = D.idx(wx, wy, wz);
      if (!(D.flags[i] & FLAG.air)) break;               // ray must stay in clear air
      if (dryDb[i] === -999) break;
      const v = dryDb[i];
      if (prev !== null && v > prev + 3 && v > -50)
        if (!nonMono || (v - prev) > nonMono.rise) nonMono = { wx, wy, wz, t, rise: v - prev, prev, v };
      prev = v;
    }
  }
  if (nonMono) F("nonmono", 3, `dry loudness RISES ${nonMono.rise.toFixed(1)}dB moving AWAY along clear air at (${nonMono.wx},${nonMono.wy},${nonMono.wz}) t=${nonMono.t} (${nonMono.prev.toFixed(0)}→${nonMono.v.toFixed(0)}dB)`);

  // flood-boundary ring: cheb == effR+1 vs one step inward
  const effR = ev.effR;
  let ringWorst = null, ringN = 0;
  for (let wy = D.y0; wy < D.y0 + D.sy; wy++)
    for (let wz = D.z0; wz < D.z0 + D.sz; wz++)
      for (let wx = D.x0; wx < D.x0 + D.sx; wx++) {
        const cheb = Math.max(Math.abs(wx - em.x), Math.abs(wy - em.y), Math.abs(wz - em.z));
        if (cheb !== effR + 1) continue;
        const i = D.idx(wx, wy, wz);
        if (totalDb[i] === -999) continue;
        const sx2 = wx - Math.sign(wx - em.x) * (Math.abs(wx - em.x) >= cheb ? 1 : 0);
        const sy2 = wy - Math.sign(wy - em.y) * (Math.abs(wy - em.y) >= cheb ? 1 : 0);
        const sz2 = wz - Math.sign(wz - em.z) * (Math.abs(wz - em.z) >= cheb ? 1 : 0);
        if (!D.inB(sx2, sy2, sz2) || (sx2 === wx && sy2 === wy && sz2 === wz)) continue;
        const j = D.idx(sx2, sy2, sz2);
        if (totalDb[j] === -999) continue;
        ringN++;
        const jump = Math.abs(totalDb[i] - totalDb[j]);
        if (jump > 6 && Math.max(totalDb[i], totalDb[j]) > -50)
          if (!ringWorst || jump > ringWorst.jump) ringWorst = { wx, wy, wz, jump, out: totalDb[i], inn: totalDb[j] };
      }
  if (ringWorst) F("ring", 2, `flood-boundary jump ${ringWorst.jump.toFixed(1)}dB at (${ringWorst.wx},${ringWorst.wy},${ringWorst.wz}) in ${ringWorst.inn.toFixed(0)} vs out ${ringWorst.out.toFixed(0)}dB (${ringN} ring cells)`);

  const meanAbs = nBoth ? sumAbs / nBoth : 0;
  const worstCliffDb = cliffs.dry.length ? Math.abs(cliffs.dry[0].a - cliffs.dry[0].b) : 0;
  return { findings, meanAbs, nBoth, nEval, nLeak: leakIn.length, nDead: deadIn.length,
           nCliffDry: cliffs.dry.length, nCliffRev: cliffs.revtail.length,
           worstCliffDb, ringJump: ringWorst ? ringWorst.jump : 0, effR,
           bigs: bigs.sort((a, b) => (b.cdb - b.rdb) - (a.cdb - a.rdb)).slice(0, 2),
           fields: { totalDb, dryDb, gateA, branchA, refDb } };
}

// ── viewer generation (self-contained HTML, gzip+base64 fields, canvas slices) ─
function viewerHtml(D, results) {
  const pack = f32 => {   // quantize dB to int8 (-80..0 → -80..0), -999 → -128
    const a = new Int8Array(f32.length);
    for (let i = 0; i < f32.length; i++) a[i] = f32[i] === -999 ? -128 : Math.max(-127, Math.min(0, Math.round(f32[i])));
    return a;
  };
  const g64 = buf => zlib.gzipSync(Buffer.from(buf.buffer, buf.byteOffset, buf.byteLength)).toString("base64");
  const emitters = results.map((r, i) => ({
    label: `${D.emitters[i].name}${D.emitters[i].virtual ? " (virtual)" : ""} @${D.emitters[i].x},${D.emitters[i].y},${D.emitters[i].z}`,
    x: D.emitters[i].x, y: D.emitters[i].y, z: D.emitters[i].z,
    total: g64(pack(r.fields.totalDb)), dry: g64(pack(r.fields.dryDb)),
    ref: g64(pack(r.fields.refDb)),
    gate: g64(Uint8Array.from(r.fields.gateA, v => Math.round(v * 255))),
    branch: g64(r.fields.branchA),
  }));
  const meta = { poi: D.poi, x0: D.x0, y0: D.y0, z0: D.z0, sx: D.sx, sy: D.sy, sz: D.sz,
                 loss: g64(Buffer.from(D.loss.map(v => Math.min(255, Math.round(v * 32)))).length ? Uint8Array.from(D.loss, v => Math.min(255, Math.round(v * 32))) : new Uint8Array(0)),
                 flags: g64(D.flags), emitters };
  return `<!doctype html><html><head><meta charset="utf-8"><title>acsweep ${D.poi}</title>
<style>
body{font:13px system-ui;margin:0;background:#14161a;color:#cfd4dc;display:flex;flex-direction:column;height:100vh}
#bar{padding:8px 12px;display:flex;gap:14px;align-items:center;flex-wrap:wrap;background:#1b1e24}
#wrap{flex:1;overflow:auto;display:flex;align-items:flex-start;justify-content:center;padding:10px}
canvas{image-rendering:pixelated;border:1px solid #333}
select,input{background:#22262e;color:#cfd4dc;border:1px solid #3a3f4a;border-radius:4px;padding:3px 6px}
#read{font-family:ui-monospace,monospace;white-space:pre}
.lg{display:flex;gap:2px;align-items:center}.lg i{width:14px;height:10px;display:inline-block}
</style></head><body>
<div id="bar">
 <b>${D.poi}</b>
 <label>emitter <select id="em"></select></label>
 <label>field <select id="fld">
   <option value="total">total dB (dry+reverb)</option><option value="dry">dry dB</option>
   <option value="ref">reference dB</option><option value="diff">cmm − ref (dB)</option>
   <option value="gate">hard-stop gate</option><option value="branch">branch (R/B/F)</option>
 </select></label>
 <label>Y <input id="y" type="range" min="0" max="${D.sy - 1}" value="2" style="width:220px"><span id="ylab"></span></label>
 <label>zoom <input id="zm" type="range" min="2" max="14" value="8" style="width:90px"></label>
 <span id="read"></span>
</div>
<div id="wrap"><canvas id="cv"></canvas></div>
<script>
const M=${JSON.stringify(meta)};
async function inflate(b64){const r=await fetch("data:application/octet-stream;base64,"+b64);
 const ds=new DecompressionStream("gzip");const ab=await new Response(r.body.pipeThrough(ds)).arrayBuffer();return new Uint8Array(ab);}
let loss,flags,EM=[];
(async()=>{
 loss=await inflate(M.loss);flags=await inflate(M.flags);
 for(const e of M.emitters)EM.push({label:e.label,x:e.x,y:e.y,z:e.z,
  total:new Int8Array((await inflate(e.total)).buffer),dry:new Int8Array((await inflate(e.dry)).buffer),
  ref:new Int8Array((await inflate(e.ref)).buffer),gate:await inflate(e.gate),branch:await inflate(e.branch)});
 const em=document.getElementById("em");
 M.emitters.forEach((e,i)=>{const o=document.createElement("option");o.value=i;o.textContent=e.label;em.appendChild(o);});
 draw();
})();
const idx=(x,y,z)=>x+M.sx*(z+M.sz*y);
function heat(v){ // -80..0 dB → blue..yellow..red
 const t=Math.max(0,Math.min(1,(v+80)/80));
 const r=Math.min(1,t*2),g=t<.5?t*1.6:1.6-1.2*t,b=Math.max(0,1-t*2.2);
 return [r*255,Math.max(0,g)*255,Math.max(0,b*255)];}
function draw(){
 if(!EM.length)return;
 const e=EM[+document.getElementById("em").value||0];
 const f=document.getElementById("fld").value;
 const y=+document.getElementById("y").value;
 const zm=+document.getElementById("zm").value;
 document.getElementById("ylab").textContent=" y="+(M.y0+y);
 const cv=document.getElementById("cv");cv.width=M.sx*zm;cv.height=M.sz*zm;
 const g=cv.getContext("2d");
 for(let z=0;z<M.sz;z++)for(let x=0;x<M.sx;x++){
  const i=idx(x,y,z);const L=loss[i];
  let col;
  if(f==="branch"){const b=e.branch[i];col=b===1?[80,200,120]:b===2?[230,180,60]:b===3?[220,80,80]:[30,32,38];}
  else if(f==="gate"){const gv=e.gate[i]/255;col=[40+gv*200,40,60+(1-gv)*60];if(e.total[i]===-128)col=[26,28,33];}
  else{
   let v;
   if(f==="diff"){v=(e.total[i]===-128||e.ref[i]===-128)?null:(e.total[i]-e.ref[i]);}
   else v=e[f][i]===-128?null:e[f][i];
   if(v===null)col=[26,28,33];
   else if(f==="diff"){const t=Math.max(-1,Math.min(1,v/30));col=t>0?[120+t*135,120-t*60,120-t*120]:[120+t*90,120+t*20,120-t*135];}
   else col=heat(v);
  }
  if(L>0&&!(flags[i]&1)){const s=Math.min(1,L/32);col=[col[0]*.25+40*s,col[1]*.25+40*s,col[2]*.25+40*s];}
  g.fillStyle="rgb("+col.map(c=>c|0).join(",")+")";
  g.fillRect(x*zm,(M.sz-1-z)*zm,zm,zm);
 }
 // emitter marker if on this slice ±1
 if(Math.abs((e.y-M.y0)-y)<=1){g.strokeStyle="#fff";g.lineWidth=2;
  g.strokeRect((e.x-M.x0)*zm-2,(M.sz-1-(e.z-M.z0))*zm-2,zm+4,zm+4);}
}
for(const id of["em","fld","y","zm"])document.getElementById(id).addEventListener("input",draw);
document.getElementById("cv").addEventListener("mousemove",ev=>{
 if(!EM.length)return;
 const zm=+document.getElementById("zm").value,y=+document.getElementById("y").value;
 const r=ev.target.getBoundingClientRect();
 const x=((ev.clientX-r.left)/zm)|0,z=M.sz-1-(((ev.clientY-r.top)/zm)|0);
 if(x<0||z<0||x>=M.sx||z>=M.sz)return;
 const e=EM[+document.getElementById("em").value||0];const i=idx(x,y,z);
 const b="?RBF"[e.branch[i]]||"?";
 document.getElementById("read").textContent=
  \`(\${M.x0+x},\${M.y0+y},\${M.z0+z}) total \${e.total[i]===-128?"—":e.total[i]+"dB"} dry \${e.dry[i]===-128?"—":e.dry[i]+"dB"} ref \${e.ref[i]===-128?"—":e.ref[i]+"dB"} gate \${(e.gate[i]/255).toFixed(2)} \${b} loss \${(loss[i]/32).toFixed(2)}\`;
});
</script></body></html>`;
}

// ── CLI ──────────────────────────────────────────────────────────────────────
if (require.main === module) {
  const args = process.argv.slice(2);
  const opt = (name, dflt) => { const i = args.indexOf("--" + name); return i >= 0 ? args[i + 1] : dflt; };
  const dir = opt("dir", path.join(__dirname, "dumps"));
  const filter = opt("poi", null);
  const viewerDir = opt("viewer", null);
  const compare = args.includes("--compare");
  const files = fs.readdirSync(dir).filter(f => f.endsWith(".json") && (!filter || f.includes(filter)));

  if (compare) {
    // Fix-variant matrix: baseline vs Phase-1 fixes (1.1 anchor, 1.2 radius clamp,
    // 1.3 march fan) vs Phase 2 (speaker field with the budget lifted to fill its box).
    const VARIANTS = [
      ["V0 baseline (shipped)", {}],
      ["V1 +anchorOwn (1.1)", { anchorOwn: true }],
      ["V2 +radiusClamp (1.2)", { anchorOwn: true, clampR: true }],
      ["V3 +marchFan (Phase 1)", { anchorOwn: true, clampR: true, fan: 5 }],
      ["V4 Phase 2 (full field)", { anchorOwn: true, fan: 5, budget: 1e9 }],
      ["V5 Phase 2 r=48", { anchorOwn: true, fan: 5, budget: 1e9, maxR: 48, effR: 48 }],
    ];
    for (const f of files) {
      const D = loadDump(path.join(dir, f));
      const probe = makeProber(D);
      console.log(`\n════ ${D.poi}  (${D.emitters.length} emitters, cfg floodR ${D.cfg.floodRadius}) ════`);
      console.log("  variant                    cliffs>12dB  worst   ring    leak  dead  mean|Δ|ref  effR   time");
      for (const [label, vopts] of VARIANTS) {
        let cliffs = 0, worst = 0, ring = 0, leak = 0, dead = 0, meanAbs = 0, effR = 0;
        const t0 = Date.now();
        for (const em of D.emitters) {
          const r = judgeEmitter(D, em, probe, vopts);
          cliffs += r.nCliffDry; leak += r.nLeak; dead += r.nDead; meanAbs += r.meanAbs;
          if (r.worstCliffDb > worst) worst = r.worstCliffDb;
          if (r.ringJump > ring) ring = r.ringJump;
          effR = r.effR;
        }
        meanAbs /= Math.max(1, D.emitters.length);
        console.log(`  ${label.padEnd(26)} ${String(cliffs).padStart(10)}  ${worst.toFixed(0).padStart(4)}dB  ${ring.toFixed(0).padStart(4)}dB  ${String(leak).padStart(4)}  ${String(dead).padStart(4)}  ${meanAbs.toFixed(1).padStart(8)}dB  ${String(effR).padStart(4)}  ${((Date.now() - t0) / 1000).toFixed(1)}s`);
      }
    }
    process.exit(0);
  }

  for (const f of files) {
    const D = loadDump(path.join(dir, f));
    console.log(`\n════ ${D.poi}  ${D.sx}x${D.sy}x${D.sz}  wallDist ${D.cfg.wallDistance} range ${D.cfg.range} floodR ${D.cfg.floodRadius} ════`);

    // 1) parity
    const par = parity(D);
    let pe = 0, p4 = 0, pm = 0, pmax = 0, pc = 0;
    for (const p of par) { pe += p.exact; p4 += p.within4; pc += p.cells; pm += p.meanAbs * p.cells; if (p.maxAbs > pmax) pmax = p.maxAbs; }
    console.log(`  parity: ${par.length} floods, ${pc} cells — ${(100 * pe / Math.max(1, pc)).toFixed(1)}% exact, ${(100 * p4 / Math.max(1, pc)).toFixed(1)}% within 1 block, mean|Δ| ${(pm / Math.max(1, pc) / CS).toFixed(3)} blocks, max ${(pmax / CS).toFixed(1)} blocks`);
    const worst = par.slice().sort((a, b) => b.maxAbs - a.maxAbs)[0];
    if (worst) console.log(`  parity worst flood: ear(${worst.ear}) exact ${worst.pctExact.toFixed(1)}% within1blk ${worst.pctWithin4.toFixed(1)}% maxΔ ${worst.maxAbs}@cost${worst.maxAtCost}/${MAXB} missing ${worst.missing} extras ${worst.extras} settled ${worst.settled}/${worst.dumpSettled}`);

    // 1b) Phase-2 parity (dump v2: C# fields + live sampler vs this mirror)
    if (D.fields && D.fields.length) {
      const p2 = parity2(D);
      for (const f of p2.fields)
        console.log(`  parity2 field @(${f.pos}): ${f.cells} cells — ${f.pctExact.toFixed(1)}% exact, ${f.pctWithin4.toFixed(1)}% within 1 block, mean|Δ| ${(f.meanAbs / CS).toFixed(3)} blk, max ${(f.maxAbs / CS).toFixed(1)} blk, missing ${f.missing}`);
      if (p2.sampleN)
        console.log(`  parity2 samples: ${p2.sampleN} (ear,speaker) extras — mean|Δ| ${p2.sampleMean.toFixed(2)} blk, max ${p2.sampleMax.toFixed(1)} blk` +
                    (p2.sampleWorst ? ` (worst ear(${p2.sampleWorst.ear}) spk${p2.sampleWorst.i}: cs ${p2.sampleWorst.cs.toFixed(1)} vs js ${p2.sampleWorst.js.toFixed(1)})` : ""));
    }

    // 2) judge each emitter
    const probe = makeProber(D);
    const results = [];
    for (let ei = 0; ei < D.emitters.length; ei++) {
      const em = D.emitters[ei];
      const t0 = Date.now();
      const r = judgeEmitter(D, em, probe, {});
      results.push(r);
      console.log(`  emitter[${ei}] ${em.name}${em.virtual ? "(virt)" : ""} @(${em.x},${em.y},${em.z})  eval ${r.nEval} cells in ${((Date.now() - t0) / 1000).toFixed(1)}s  mean|Δ|=${r.meanAbs.toFixed(1)}dB/${r.nBoth}  leak:${r.nLeak} dead:${r.nDead} cliffs:${r.nCliffDry}+${r.nCliffRev}`);
      for (const fd of r.findings.sort((a, b) => b.sev - a.sev)) console.log(`      [${fd.kind}] ${fd.msg}`);
      for (const b of r.bigs) console.log(`      [big+] (${b.wx},${b.wy},${b.wz}) cmm ${b.cdb.toFixed(0)} vs ref ${b.rdb.toFixed(0)}dB [${b.cell.branch}] solid=${b.cell.solid.toFixed(1)}`);
    }

    // 3) viewer
    if (viewerDir) {
      fs.mkdirSync(viewerDir, { recursive: true });
      const out = path.join(viewerDir, D.poi.replace(/[^\w.-]/g, "_") + ".html");
      fs.writeFileSync(out, viewerHtml(D, results));
      console.log(`  viewer → ${out}`);
    }
  }
}
module.exports = { loadDump, mirrorFlood, parity, parity2, makeEvaluator, segLoss, shapeOcclusion };
