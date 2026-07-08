"use strict";
// Headless judgement harness: runs the exact CMM model port (from acoustics-bench.html)
// + the idealized physics reference over all scenarios, many emitter placements,
// and applies automated anomaly detectors.
const fs = require("fs");
const REAL = JSON.parse(fs.readFileSync(__dirname + "/scenarios.json", "utf8"));

const BLOCKS = {
  " ": { name: "air",            loss: 0,    refDb: 0,  reflect: 0    },
  "~": { name: "water",          loss: 0.2,  refDb: 15, reflect: 0.85 },
  "L": { name: "ladder",         loss: 0,    refDb: 0,  reflect: 0    },
  "F": { name: "frame/bars",     loss: 0,    refDb: 1,  reflect: 0    },
  "G": { name: "glass pane",     loss: 0.15, refDb: 22, reflect: 0.85 },
  ".": { name: "deco/trim",      loss: 0.06, refDb: 3,  reflect: 0.3  },
  "D": { name: "door (shut)",    loss: 0.25, refDb: 27, reflect: 0.45 },
  "S": { name: "stone/concrete", loss: 0.9,  refDb: 45, reflect: 0.9  },
  "M": { name: "metal",          loss: 1.0,  refDb: 55, reflect: 0.95 },
  "W": { name: "wood",           loss: 0.5,  refDb: 27, reflect: 0.45 },
  "T": { name: "terrain",        loss: 0.7,  refDb: 35, reflect: 0.3  },
  "s": { name: "stone partial",  loss: 0.45, refDb: 22, reflect: 0.9  },
  "m": { name: "metal partial",  loss: 0.5,  refDb: 27, reflect: 0.95 },
  "w": { name: "wood partial",   loss: 0.25, refDb: 14, reflect: 0.45 },
};
const SOLID = ch => "SMWTGD".includes(ch) || "smw".includes(ch);

function synth(name) {
  const W = 60, H = 60, g = Array.from({ length: H }, () => Array(W).fill(" "));
  const fill = (x0, y0, x1, y1, c) => { for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) if (x>=0&&y>=0&&x<W&&y<H) g[y][x] = c; };
  if (name === "tunnel_twist") {
    fill(0, 20, 59, 59, "T");
    let x = 2, y = 24; const carve = (dx, dy, n) => { for (let i = 0; i < n; i++) { fill(x-1, y-1, x+1, y+1, " "); x += dx; y += dy; } };
    carve(1,0,14); carve(0,1,8); carve(1,0,12); carve(0,1,10); carve(1,0,14); carve(0,1,8); carve(1,0,12);
    fill(0,0,59,19," ");
    return { rows: g.map(r => r.join("")), emit: [x-2, y], listen: [3, 18] };
  }
  if (name === "ladder_shaft") {
    fill(0, 12, 59, 59, "T");
    fill(28, 12, 30, 52, " ");
    for (let y = 12; y <= 52; y++) g[y][29] = "L";
    fill(20, 50, 40, 56, " ");
    fill(20, 49, 40, 49, "S"); fill(20, 57, 40, 57, "S"); fill(19, 49, 19, 57, "S"); fill(41, 49, 41, 57, "S");
    fill(28, 49, 30, 49, " ");
    return { rows: g.map(r => r.join("")), emit: [30, 54], listen: [29, 10] };
  }
  if (name === "vault") {
    fill(4, 4, 55, 55, " ");
    const box = (x0,y0,x1,y1,c,t) => { for (let i=0;i<t;i++){ fill(x0+i,y0+i,x1-i,y0+i,c); fill(x0+i,y1-i,x1-i,y1-i,c); fill(x0+i,y0+i,x0+i,y1-i,c); fill(x1-i,y0+i,x1-i,y1-i,c);} };
    box(8, 8, 51, 51, "W", 1);  g[30][8] = "D";
    box(16, 16, 43, 43, "S", 1); g[16][30] = "D";
    box(24, 24, 35, 35, "M", 2);
    return { rows: g.map(r => r.join("")), emit: [29, 29], listen: [5, 30] };
  }
  if (name === "pond") {
    fill(0, 0, 59, 59, " ");
    for (let y = 14; y <= 45; y++) for (let x = 18; x <= 42; x++) {
      const dx = (x-30)/13, dy = (y-30)/17; if (dx*dx + dy*dy < 1) g[y][x] = "~";
    }
    return { rows: g.map(r => r.join("")), emit: [30, 30], listen: [50, 30] };
  }
}

const K = {
  Range: 30, MinDist: 1, Falloff: 2.5, Muffle: 0.9, WallDist: 35,
  MaxDist: 30, HardStop: 68, FloodR: 32, Wet: 0.6, Carry: 0.8, RangeMult: 1.5, DepthAmt: 0.7,
};

let grid = [], GW = 0, GH = 0, emit = [0, 0], fld = null, refFld = null, probeCache = null;
const at = (x, y) => (x >= 0 && y >= 0 && x < GW && y < GH) ? grid[y][x] : "T";
const loss = (x, y) => BLOCKS[at(x, y)].loss;

// ── CMM flood (Dial's, CostScale 4, MaxBucket 2048 — shipped values) ──
const CS = 4, MAXB = 2048;
function flood(sx, sy) {
  const n = GW * GH, cost = new Int32Array(n).fill(-1), wall = new Int32Array(n);
  const wcs = Math.round(K.WallDist * CS);
  const bh = new Int32Array(MAXB + 1).fill(-1), nxt = new Int32Array(n);
  const si = sx + sy * GW; cost[si] = 0; bh[0] = si; nxt[si] = -1;
  for (let c = 0; c <= MAXB; c++) {
    let i = bh[c];
    while (i !== -1) {
      const ni = nxt[i];
      if (cost[i] === c) {
        const x = i % GW, y = (i / GW) | 0, pw = wall[i];
        for (const [dx, dy] of [[1,0],[-1,0],[0,1],[0,-1]]) {
          const nx2 = x + dx, ny2 = y + dy;
          if (nx2 < 0 || ny2 < 0 || nx2 >= GW || ny2 >= GH) continue;
          const j = nx2 + ny2 * GW;
          const we = Math.round(wcs * loss(nx2, ny2));
          const nc = c + CS + we;
          if (nc > MAXB) continue;
          if (cost[j] !== -1 && nc >= cost[j]) continue;
          cost[j] = nc; wall[j] = pw + we; nxt[j] = bh[nc]; bh[nc] = j;
        }
      }
      i = ni;
    }
  }
  return { cost, wall };
}
function segLoss(x0, y0, x1, y1) {
  let sum = 0; const dx = x1 - x0, dy = y1 - y0, steps = Math.max(Math.abs(dx), Math.abs(dy)) * 2;
  for (let i = 1; i <= steps; i++) {
    const x = Math.round(x0 + dx * i / steps), y = Math.round(y0 + dy * i / steps);
    if (x === x0 && y === y0) continue;
    sum += loss(x, y) / 2;
  }
  return sum;
}
const smooth = (a, b, x) => { const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
// v29.1: wall term scales with range (fixed dB, not fixed meters); detour stays real blocks.
function shape(raw, solid, solidGate) {
  const k = Math.max(0.05, K.Range / 30), hs = K.HardStop;
  const detour = Math.max(0, raw - solid);
  const md = Math.max(0.001, K.MaxDist * k);
  const audible = md * Math.tanh((detour + solid * k) / md);
  const gate = smooth(0.55 * hs, hs, solidGate);
  return { extra: audible + gate * K.Range, gate };
}
const DIRS = Array.from({ length: 16 }, (_, i) => [Math.cos(i * Math.PI / 8), Math.sin(i * Math.PI / 8)]);
function probeAt(x, y) {
  if (probeCache[x + y * GW]) return probeCache[x + y * GW];
  let hits = 0, sumR = 0, dists = [];
  for (const [dx, dy] of DIRS) {
    let hit = false, dist = 24;
    for (let t = 1; t <= 24; t++) {
      const px = Math.round(x + dx * t), py = Math.round(y + dy * t);
      const ch = at(px, py);
      if (SOLID(ch) || ch === "~") { hit = true; dist = t; sumR += BLOCKS[ch].reflect; break; }
    }
    if (hit) hits++; dists.push(dist);
  }
  dists.sort((a, b) => a - b);
  const r = { enc: hits / 16, reflect: hits ? sumR / hits : 0, roofed: hits > 10 };
  probeCache[x + y * GW] = r;
  return r;
}
// v29 gate-flap fix: solidGate = min wall over the reached 3×3 neighbourhood (any route), not the
// wall that rode the single min-cost path (2D mirror of SampleArrival's wallMin).
function wallMinAt(x, y) {
  let mn = Infinity;
  for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
    const nx = x + dx, ny = y + dy;
    if (nx < 0 || ny < 0 || nx >= GW || ny >= GH) continue;
    const j = nx + ny * GW;
    if (fld.cost[j] !== -1 && fld.wall[j] < mn) mn = fld.wall[j];
  }
  return mn === Infinity ? 0 : mn;
}
function cmmAt(x, y) {
  const i = x + y * GW;
  const d = Math.hypot(x - emit[0], y - emit[1]);
  const man = Math.abs(x - emit[0]) + Math.abs(y - emit[1]);
  const cheb = Math.max(Math.abs(x - emit[0]), Math.abs(y - emit[1]));
  let raw, solid, solidGate, branch;
  if (fld.cost[i] !== -1 && cheb <= K.FloodR) {
    raw = Math.max(0, fld.cost[i] / CS - man); solid = fld.wall[i] / CS; solidGate = wallMinAt(x, y) / CS; branch = "R";
  } else {
    let bx = x, by = y, ok = false;
    const steps = Math.max(1, Math.max(Math.abs(x - emit[0]), Math.abs(y - emit[1])));
    for (let t = 0; t <= steps; t++) {
      const px = Math.round(x + (emit[0] - x) * t / steps), py = Math.round(y + (emit[1] - y) * t / steps);
      const pc = Math.max(Math.abs(px - emit[0]), Math.abs(py - emit[1]));
      if (pc <= K.FloodR && fld.cost[px + py * GW] !== -1) { bx = px; by = py; ok = true; break; }
    }
    if (ok) {
      const bi = bx + by * GW;
      const remW = segLoss(x, y, bx, by) * K.WallDist;
      const remM = Math.abs(x - bx) + Math.abs(y - by);
      raw = Math.max(0, fld.cost[bi] / CS + remM + remW - man);
      solid = fld.wall[bi] / CS + remW; solidGate = wallMinAt(bx, by) / CS; branch = "B";
    } else {
      raw = segLoss(x, y, emit[0], emit[1]) * K.WallDist; solid = raw; solidGate = raw; branch = "F";
    }
  }
  const { extra, gate } = shape(raw, solid, solidGate);
  const dEff = d + extra;
  const mn = K.MinDist, mx = K.Range, span = Math.max(0.0001, mx - mn);
  const att = dEff <= mn ? 1 : dEff >= mx ? 0 : Math.pow((mx - dEff) / span, K.Falloff);
  const depth = d + Math.max(0, raw - solid);
  const pr = probeAt(x, y);
  const roomWet = pr.enc * pr.reflect * (pr.roofed ? 1 : 0.5);
  // v29: power decay over the whole reach at half the dry falloff exponent (was flat until 85%)
  const rmx = mx * K.RangeMult;
  const attWet = d >= rmx ? 0 : Math.pow((rmx - d) / rmx, K.Falloff * 0.5);
  const depthFac = Math.exp(-1.2 * K.DepthAmt * Math.min(2, Math.max(0, (depth - mn) / 48)));
  const matDamp = Math.pow(0.5, solidGate / (0.5 * K.WallDist));
  const carry = roomWet * K.Wet * K.Carry * (1 - gate) * matDamp * depthFac * attWet;
  const local = roomWet * K.Wet * att;
  const total = Math.sqrt(att * att + carry * carry + local * local);
  return { d, raw, solid, solidGate, gate, extra, dEff, att, depth, carry, local, total, branch };
}
// ── reference model ──
function refFloodFn(sx, sy) {
  const n = GW * GH, CAP = 4096, Q = 4;
  const best = new Int32Array(n).fill(-1), len = new Float32Array(n), db = new Float32Array(n);
  const bh = new Int32Array(CAP + 1).fill(-1), nxt = new Int32Array(n);
  const si = sx + sy * GW; best[si] = 0; bh[0] = si; nxt[si] = -1;
  for (let c = 0; c <= CAP; c++) {
    let i = bh[c];
    while (i !== -1) {
      const ni = nxt[i];
      if (best[i] === c) {
        const x = i % GW, y = (i / GW) | 0;
        for (const [dx, dy] of [[1,0],[-1,0],[0,1],[0,-1]]) {
          const nx2 = x + dx, ny2 = y + dy;
          if (nx2 < 0 || ny2 < 0 || nx2 >= GW || ny2 >= GH) continue;
          const j = nx2 + ny2 * GW;
          const stepDb = BLOCKS[at(nx2, ny2)].refDb;
          const nc = c + Q + Math.round(stepDb * 0.8 * Q);
          if (nc > CAP) continue;
          if (best[j] !== -1 && nc >= best[j]) continue;
          best[j] = nc; len[j] = len[i] + 1; db[j] = db[i] + stepDb;
          nxt[j] = bh[nc]; bh[nc] = j;
        }
      }
      i = ni;
    }
  }
  return { len, db, best };
}
function refAt(x, y) {
  const i = x + y * GW;
  if (refFld.best[i] === -1) return { dryDb: -80, amp: 0 };
  const L = Math.max(1, refFld.len[i]);
  const dryDb = -(20 * Math.log10(L) + refFld.db[i]);
  const dryAmp = Math.pow(10, Math.max(-60, dryDb) / 20);
  const pr = probeAt(x, y);
  const wet = pr.enc * pr.reflect * Math.sqrt(dryAmp);
  return { dryDb, amp: Math.min(1, Math.hypot(dryAmp, wet)) };
}
const db = a => a <= 1e-4 ? -80 : 20 * Math.log10(a);

// ── seeded PRNG for reproducible emitter placement ──
function mulberry32(a) { return function() { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

function pickEmitters(seedBase, count) {
  const open = [];
  for (let y = 1; y < GH - 1; y++) for (let x = 1; x < GW - 1; x++)
    if (at(x, y) === " ") {
      let solidN = 0; for (const [dx, dy] of [[1,0],[-1,0],[0,1],[0,-1]]) if (SOLID(at(x+dx, y+dy))) solidN++;
      if (solidN >= 1) open.push([x, y]);
    }
  if (!open.length)   // no structure-adjacent cells (e.g. pond): any open cell
    for (let y = 1; y < GH - 1; y++) for (let x = 1; x < GW - 1; x++) if (at(x, y) === " ") open.push([x, y]);
  const rnd = mulberry32(seedBase), out = [];
  for (let k = 0; k < count && open.length; k++) out.push(open[Math.floor(rnd() * open.length)]);
  return out;
}

// loudness profile along a listener path (for canonical walk-throughs)
function profile(label, path) {
  const parts = path.map(([x, y]) => {
    if (SOLID(at(x, y)) || at(x, y) === ".") return `(${x},${y})=WALL`;
    const c = cmmAt(x, y), r = refAt(x, y);
    return `${db(c.total).toFixed(0)}/${db(r.amp).toFixed(0)}`;
  });
  console.log(`    profile ${label} (cmm/ref dB): ${parts.join("  ")}`);
}

// ═════════ detectors ═════════
function judge(scenName, emitter) {
  emit = emitter;
  probeCache = new Array(GW * GH).fill(null);
  fld = flood(emit[0], emit[1]);
  refFld = refFloodFn(emit[0], emit[1]);
  const cm = new Array(GW * GH).fill(null), rf = new Array(GW * GH).fill(null);
  const airCells = [];
  for (let y = 0; y < GH; y++) for (let x = 0; x < GW; x++) {
    const ch = at(x, y);
    if (ch === " " || ch === "L" || ch === "F" || ch === "~") {
      cm[x + y * GW] = cmmAt(x, y); rf[x + y * GW] = refAt(x, y); airCells.push([x, y]);
    }
  }
  const findings = [];
  const F = (kind, sev, msg) => findings.push({ kind, sev, msg });

  // 1. divergence stats (only within plausible CMM reach: d <= Range*RangeMult)
  let nBoth = 0, sumAbs = 0, big = [], leakIn = [], deadIn = [];
  for (const [x, y] of airCells) {
    const c = cm[x + y * GW], r = rf[x + y * GW];
    if (c.d > K.Range * K.RangeMult) continue;
    const cdb = db(c.total), rdb = db(r.amp);
    if (cdb > -70 || rdb > -70) {
      const delta = cdb - rdb;
      if (cdb > -70 && rdb > -70) { nBoth++; sumAbs += Math.abs(delta); }
      if (delta > 15 && cdb > -35) big.push({ x, y, cdb, rdb, c });
      if (cdb > -30 && rdb <= -60) leakIn.push({ x, y, cdb, rdb, c });   // CMM clearly audible, physics says sealed
      if (rdb > -25 && cdb <= -60 && c.d < K.Range * 0.8) deadIn.push({ x, y, cdb, rdb, c }); // physics loud, CMM dead, well in range
    }
  }
  big.sort((a, b) => (b.cdb - b.rdb) - (a.cdb - a.rdb));
  if (leakIn.length) {
    const w = leakIn.sort((a,b)=>b.cdb-a.cdb)[0];
    F("leak", 3, `${leakIn.length} cells CMM-audible where physics says sealed; worst (${w.x},${w.y}) cmm ${w.cdb.toFixed(0)}dB vs ref ${w.rdb.toFixed(0)}dB [branch ${w.c.branch}, solid ${w.c.solid.toFixed(1)}, gate ${w.c.gate.toFixed(2)}]`);
  }
  if (deadIn.length) {
    const w = deadIn.sort((a,b)=>b.rdb-a.rdb)[0];
    F("dead", 2, `${deadIn.length} in-range cells physics-loud but CMM silent; worst (${w.x},${w.y}) ref ${w.rdb.toFixed(0)}dB vs cmm ${w.cdb.toFixed(0)}dB [branch ${w.c.branch}, dEff ${w.c.dEff.toFixed(1)}, raw ${w.c.raw.toFixed(1)}]`);
  }

  // 2. adjacent-cell cliffs (same medium, total jumps > 12 dB while both in audible band)
  const cliffs = [];
  for (const [x, y] of airCells) {
    const c = cm[x + y * GW]; if (!c) continue;
    for (const [dx, dy] of [[1,0],[0,1]]) {
      const x2 = x + dx, y2 = y + dy;
      if (x2 >= GW || y2 >= GH) continue;
      const c2 = cm[x2 + y2 * GW]; if (!c2) continue;
      const a = db(c.total), b2 = db(c2.total);
      if (Math.max(a, b2) > -40 && Math.abs(a - b2) > 12) {
        const zone = (c.d < K.Range && c2.d < K.Range) ? "dry" : "revtail";
        cliffs.push({ x, y, dx, dy, a, b2, zone, br: c.branch + c2.branch, g: [c.gate, c2.gate], sg: [c.solidGate, c2.solidGate] });
      }
    }
  }
  for (const zone of ["dry", "revtail"]) {
    const zs = cliffs.filter(c => c.zone === zone);
    if (!zs.length) continue;
    zs.sort((p, q) => Math.abs(q.a - q.b2) - Math.abs(p.a - p.b2));
    const w = zs[0];
    F("cliff-" + zone, zone === "dry" ? 3 : 2, `${zs.length} adjacent air-cell jumps >12dB (${zone} zone); worst (${w.x},${w.y})→(${w.x+w.dx},${w.y+w.dy}): ${w.a.toFixed(0)}→${w.b2.toFixed(0)}dB [branches ${w.br}, gate ${w.g[0].toFixed(2)}→${w.g[1].toFixed(2)}, solidGate ${w.sg[0].toFixed(1)}→${w.sg[1].toFixed(1)}]`);
  }

  // 3. non-monotonic loudness on straight all-air rays from the emitter
  let nonMono = null;
  for (let a = 0; a < 32; a++) {
    const ang = a * Math.PI / 16, dx = Math.cos(ang), dy = Math.sin(ang);
    let prev = null, clearSoFar = true;
    for (let t = 1; t <= K.Range; t++) {
      const x = Math.round(emit[0] + dx * t), y = Math.round(emit[1] + dy * t);
      if (x < 1 || y < 1 || x >= GW-1 || y >= GH-1) break;
      if (at(x, y) !== " ") { clearSoFar = false; break; }
      const c = cm[x + y * GW]; if (!c) break;
      const v = db(c.att);   // dry only: reverb entering a room can legitimately swell total
      if (prev !== null && v > prev + 3 && v > -50 && clearSoFar)
        if (!nonMono || (v - prev) > nonMono.rise) nonMono = { x, y, t, rise: v - prev, prev, v };
      prev = v;
    }
  }
  if (nonMono) F("nonmono", 3, `dry loudness RISES ${nonMono.rise.toFixed(1)}dB moving AWAY along clear air at (${nonMono.x},${nonMono.y}) t=${nonMono.t} (${nonMono.prev.toFixed(0)}→${nonMono.v.toFixed(0)}dB)`);

  // 4. flood-ring artifact: branch R vs B mismatch across the FloodR chebyshev ring
  let ringWorst = null, ringN = 0;
  for (const [x, y] of airCells) {
    const c = cm[x + y * GW];
    const cheb = Math.max(Math.abs(x - emit[0]), Math.abs(y - emit[1]));
    if (cheb !== K.FloodR + 1) continue;
    // neighbor one step toward emitter (inside ring)
    const sx = x - Math.sign(x - emit[0]) * (Math.abs(x-emit[0]) >= Math.abs(y-emit[1]) ? 1 : 0);
    const sy = y - Math.sign(y - emit[1]) * (Math.abs(y-emit[1]) >= Math.abs(x-emit[0]) ? 1 : 0);
    const c2 = cm[sx + sy * GW]; if (!c2 || (sx===x && sy===y)) continue;
    const jump = Math.abs(db(c.total) - db(c2.total));
    ringN++;
    if (jump > 6 && Math.max(db(c.total), db(c2.total)) > -50)
      if (!ringWorst || jump > ringWorst.jump) ringWorst = { x, y, jump, out: db(c.total), inn: db(c2.total), br: c.branch + c2.branch };
  }
  if (ringWorst) F("ring", 2, `flood-boundary ring jump ${ringWorst.jump.toFixed(1)}dB at (${ringWorst.x},${ringWorst.y}) in ${ringWorst.inn.toFixed(0)} vs out ${ringWorst.out.toFixed(0)}dB [${ringWorst.br}] (${ringN} ring cells checked)`);

  // 5. reverb sanity: carry louder than the dry+local sum somewhere close & un-occluded
  let carryOdd = null;
  for (const [x, y] of airCells) {
    const c = cm[x + y * GW];
    if (c.solid < 1 && c.d < K.Range && c.carry > 2.5 * Math.max(c.att, 1e-6) && c.carry > 0.05)
      if (!carryOdd || c.carry / Math.max(c.att,1e-6) > carryOdd.ratio) carryOdd = { x, y, ratio: c.carry / Math.max(c.att,1e-6), c };
  }
  if (carryOdd) F("carrydom", 2, `carry bus dominates dry ×${carryOdd.ratio.toFixed(1)} in un-occluded cell (${carryOdd.x},${carryOdd.y}) d=${carryOdd.c.d.toFixed(1)} (att ${carryOdd.c.att.toFixed(3)}, carry ${carryOdd.c.carry.toFixed(3)})`);

  // 6. unreached-inside-flood: cells inside FloodR whose flood cost saturated (MaxBucket seal)
  let unreached = 0, unreachedAudible = null;
  for (const [x, y] of airCells) {
    const cheb = Math.max(Math.abs(x - emit[0]), Math.abs(y - emit[1]));
    if (cheb <= K.FloodR && fld.cost[x + y * GW] === -1) {
      unreached++;
      const c = cm[x + y * GW];
      if (db(c.total) > -40 && (!unreachedAudible || c.total > unreachedAudible.c.total)) unreachedAudible = { x, y, c };
    }
  }
  if (unreachedAudible) {
    const w = unreachedAudible;
    F("sealleak", 3, `${unreached} in-radius cells flood-SEALED (cost cap) yet audible via branch ${w.c.branch}: (${w.x},${w.y}) ${db(w.c.total).toFixed(0)}dB, solid ${w.c.solid.toFixed(1)}, gate ${w.c.gate.toFixed(2)}`);
  }

  const meanAbs = nBoth ? (sumAbs / nBoth) : 0;
  return { findings, meanAbs, nBoth, nBig: big.length, big: big.slice(0, 2), nLeak: leakIn.length, nCliff: cliffs.length, unreached };
}

// ═════════ run ═════════
module.exports = {
  synth, flood, refFloodFn, cmmAt, refAt, probeAt, smooth, db, K, BLOCKS,
  setGrid(rows) { grid = rows.map(r => r.split("")); GH = grid.length; GW = grid[0].length; return { GW, GH }; },
  setEmit(e) { emit = e; probeCache = new Array(GW * GH).fill(null); fld = flood(e[0], e[1]); refFld = refFloodFn(e[0], e[1]); return fld; },
  at, getGW: () => GW,
};
if (module.parent) return;
const IDS = ["house_floor","house_section","store_floor","cave_floor","cave_section","tunnel_twist","ladder_shaft","vault","pond"];
for (const id of IDS) {
  const s = REAL[id] || synth(id);
  grid = s.rows.map(r => r.split(""));
  GH = grid.length; GW = grid[0].length;
  const emitters = [];
  if (id === "pond") emitters.push([14, 30]);
  else if (s.emit && grid[s.emit[1]][s.emit[0]] === " ") emitters.push(s.emit);
  for (const e of pickEmitters(id.length * 7919 + 17, 3)) emitters.push(e);
  console.log(`\n════ ${id} (${GW}x${GH}) ════`);
  let first = true;
  for (const e of emitters) {
    if (at(e[0], e[1]) !== " ") continue;
    const r = judge(id, e);
    const head = `emit(${e[0]},${e[1]})  mean|Δ|=${r.meanAbs.toFixed(1)}dB over ${r.nBoth} cells  big>15dB:${r.nBig}  cliffs:${r.nCliff}  sealed-in-radius:${r.unreached}`;
    console.log(`  ${head}`);
    for (const f of r.findings.sort((a,b)=>b.sev-a.sev)) console.log(`    [${f.kind}] ${f.msg}`);
    for (const b of r.big) console.log(`    [big+] (${b.x},${b.y}) cmm ${b.cdb.toFixed(0)} vs ref ${b.rdb.toFixed(0)}dB [${b.c.branch}] d=${b.c.d.toFixed(1)} solid=${b.c.solid.toFixed(1)}`);
    if (first) {   // canonical walk-through profiles (globals still hold this emitter's fields)
      if (id === "ladder_shaft")
        profile("climb shaft (bottom→top)", [[28,52],[28,48],[28,44],[28,40],[28,36],[28,32],[28,28],[28,24],[28,20],[28,16],[28,13]]);
      if (id === "tunnel_twist")
        profile("walk out of tunnel", [[50,50],[44,50],[42,46],[36,42],[30,42],[28,36],[22,32],[16,28],[10,24],[4,24],[3,18]]);
      if (id === "vault")
        profile("vault→outside (through 3 shells)", [[31,29],[38,29],[46,29],[53,29],[57,29]]);
      if (id === "pond")
        profile("across pond west→east", [[20,30],[26,30],[32,30],[38,30],[44,30],[50,30]]);
      if (id === "house_floor")
        profile("room→room→outside", [[15,4],[20,4],[25,4],[30,4],[34,4],[40,4],[46,4]]);
    }
    first = false;
  }
}
