// Break a single bad gradient down to the speaker and the TERM responsible.
//
// The real captures report one number per voxel -- the sum over every speaker, which is what
// the game outputs and therefore the only honest measure -- but that sum is useless for
// diagnosis: it says a step exists, not which of eight speakers stepped or which of its four
// arrival terms did. This prints exactly that, at a voxel and each of its six neighbours.
//
//   node worst.js <dump-substring> <wx> <wy> <wz>
"use strict";
const fs = require("fs"), path = require("path");
const { loadDump } = require("./judge3d.js");
const Rg = require("./regions.js");
const { makeNewEvaluator } = require("./portals.js");

const want = process.argv[2];
const P = [+process.argv[3], +process.argv[4], +process.argv[5]];
const file = fs.readdirSync(path.join(__dirname, "dumps")).find(f => f.includes(want));
if (!file) { console.error("no dump matching " + want); process.exit(1); }
const D = loadDump(path.join(__dirname, "dumps", file));
const G = Rg.asGraph(D, 0.62);
console.log(`${file}\n${D.emitters.length} speakers, ${G.nodes.length} nodes\n`);

const evs = D.emitters.map(em => ({ em, f: makeNewEvaluator(D, G, em) }));
const pow = db => Math.pow(10, db / 10);
const dbOf = p => p > 1e-12 ? 10 * Math.log10(p) : -120;

// total over every speaker, the way the game sums them
function totalAt(p) {
  let s = 0;
  for (const e of evs) s += pow(e.f(p[0], p[1], p[2]).total);
  return dbOf(s);
}

const NB = [[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]];
const here = totalAt(P);
console.log(`at ${P.join(",")}  total ${here.toFixed(2)} dB`);
let worstNb = null, worstD = 0;
for (const n of NB) {
  const q = [P[0]+n[0], P[1]+n[1], P[2]+n[2]];
  const t = totalAt(q);
  const d = t - here;
  if (Math.abs(d) > Math.abs(worstD)) { worstD = d; worstNb = q; }
  console.log(`  ${n.join(",").padEnd(8)} -> ${q.join(",").padEnd(18)} ${t.toFixed(2).padStart(8)}  delta ${d.toFixed(2).padStart(7)}`);
}
if (!worstNb) process.exit(0);

console.log(`\nWORST NEIGHBOUR ${worstNb.join(",")}  (${worstD.toFixed(2)} dB). Per speaker:\n`);
console.log(`spk  at                     here      there     delta |  term that moved`);
for (let i = 0; i < evs.length; i++) {
  const { em, f } = evs[i];
  const a = f(P[0], P[1], P[2]), b = f(worstNb[0], worstNb[1], worstNb[2]);
  const d = b.total - a.total;
  // which of the four terms accounts for it
  const terms = [["air", a.airDb, b.airDb], ["solid", a.solidDb, b.solidDb], ["diff", a.diffDb, b.diffDb]];
  const moved = terms.map(([n, x, y]) => `${n} ${x.toFixed(1)}->${y.toFixed(1)}`).join("  ");
  const mark = Math.abs(d) > 1 ? " <<<" : "";
  console.log(`${String(i).padEnd(4)} ${(em.x+","+em.y+","+em.z).padEnd(20)}`
    + `${a.total.toFixed(2).padStart(9)}${b.total.toFixed(2).padStart(10)}${d.toFixed(2).padStart(10)} | ${moved}${mark}`);
  // Where the loudest arrival comes from, and how much material its leg crossed. If every
  // speaker's dominant arrival shares ONE position, they are all routing through the same
  // opening and the step belongs to that opening's leg, not to any speaker.
  const ent = e => e.entry ? `${e.entry.px.toFixed(1)},${e.entry.py.toFixed(1)},${e.entry.pz.toFixed(1)}`
                             + ` legMat ${e.entry.legMat.toFixed(1)}` : "-";
  console.log(`       via  ${ent(a).padEnd(34)} -> ${ent(b)}`);
}

// ── the listener's own state, which is what a whole-scene step usually means ──
// Every speaker moving by the same amount cannot be speaker geometry; it is something the
// listener carries. Region membership is the candidate: the diffuse field is uniform per
// region and the routed legs start from the listener's node, so crossing a boundary moves
// both at once for every source simultaneously.
{
  const li = (wx, wy, wz) => (wx - D.x0) + D.sx * ((wz - D.z0) + D.sz * (wy - D.y0));
  const show = p => {
    const i = li(p[0], p[1], p[2]);
    const nid = G.nodeOf[i];
    const nd = nid >= 0 ? G.nodes[nid] : null;
    const rm = nd ? G.rooms[nd.room] : null;
    return nd
      ? `node ${nid} (${nd.n} vox)  room ${nd.room} (${rm.n} vox, surf ${rm.surf.toFixed(0)}, `
        + `alpha ${rm.alpha.toFixed(3)}, R ${rm.roomR.toFixed(1)}, sky ${(100*rm.skyFrac).toFixed(1)}%)`
      : "OUTSIDE any node";
  };
  const showOpen = p => {
    const i = li(p[0], p[1], p[2]);
    return `open ${G.open[i].toFixed(3)}  loss ${D.lossAt(p[0],p[1],p[2]).toFixed(3)}  air ${D.isAir(p[0],p[1],p[2])}`;
  };
  console.log(`\nlistener state`);
  console.log(`  here  ${P.join(",").padEnd(18)} ${show(P)}`);
  console.log(`  there ${worstNb.join(",").padEnd(18)} ${show(worstNb)}`);
  console.log(`  here  ${showOpen(P)}`);
  console.log(`  there ${showOpen(worstNb)}`);
  // and the column above/below, since a partly-filled voxel is usually furniture
  for (const dy of [-1, 0, 1, 2])
    console.log(`    y${(P[1]+dy)}: here ${G.open[li(P[0],P[1]+dy,P[2])].toFixed(3)}`
      + `  there ${G.open[li(worstNb[0],worstNb[1]+dy,worstNb[2])].toFixed(3)}`);
}
