// ───────────────────────────────────────────────────────────────────────────────
// WAVCHECK — measure the things the author actually reports hearing
// ───────────────────────────────────────────────────────────────────────────────
// The envelope's worst dB/s is a proxy, and optimising a proxy is how you end up making four
// principled changes in a row while the thing you care about gets worse. The author's reports are
// specific and each one is directly measurable off the rendered file:
//
//   "fades from being dominated by the left channel to the right"  -> PAN TRAVEL, in dB of L/R
//                                                                     imbalance, and how far it swings
//   "its pitch warps briefly"                                      -> SPECTRAL CENTROID WOBBLE, since
//                                                                     a moving delay line shifts pitch
//   "sounds like me passing several doors one after the other"      -> DIPS, the count of distinct
//                                                                     local minima in the envelope
//
// So measure those, per file, and compare old against new.
//
//   node wavcheck.js                 every pair in renders/
//   node wavcheck.js overhatch-shut  one scenario
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");

const WIN = 0.25;          // seconds per analysis window
const DIP_DB = 1.5;        // a dip must be this deep to count as one, so ripple is not a "door"
const SETTLE_S = 0.75;     // ignore the render's own start-up ramp
const PAN_HYST = 2.0;      // dB of L/R imbalance a move must exceed to count as a direction change

function readWavStereo(file) {
  const b = fs.readFileSync(file);
  let i = 12, fmt = null, data = null;
  while (i + 8 <= b.length) {
    const id = b.toString("ascii", i, i + 4), sz = b.readUInt32LE(i + 4);
    if (id === "fmt ") fmt = { ch: b.readUInt16LE(i + 10), rate: b.readUInt32LE(i + 12) };
    if (id === "data") { data = b.subarray(i + 8, i + 8 + sz); break; }
    i += 8 + sz + (sz & 1);
  }
  const n = Math.floor(data.length / 2 / fmt.ch);
  const L = new Float32Array(n), R = new Float32Array(n);
  for (let f = 0; f < n; f++) {
    L[f] = data.readInt16LE(f * fmt.ch * 2) / 32768;
    R[f] = data.readInt16LE((f * fmt.ch + (fmt.ch > 1 ? 1 : 0)) * 2) / 32768;
  }
  return { L, R, rate: fmt.rate };
}

function analyse(file) {
  const { L, R, rate } = readWavStereo(file);
  const w = Math.floor(rate * WIN), n = Math.floor(L.length / w);
  const env = [], bal = [], cen = [];
  for (let k = 0; k < n; k++) {
    let sl = 0, sr = 0, sx = 0, sd = 0, prev = 0;
    for (let j = k * w; j < (k + 1) * w; j++) {
      sl += L[j] * L[j]; sr += R[j] * R[j];
      const m = L[j] + R[j];
      sx += m * m; sd += (m - prev) * (m - prev); prev = m;
    }
    env.push(10 * Math.log10((sl + sr) / w + 1e-12));
    bal.push(10 * Math.log10((sl + 1e-12) / (sr + 1e-12)));   // + = left, - = right
    // zero-crossing-ish brightness proxy: ratio of difference energy to signal energy. Rises with
    // spectral centroid, and a delay line being swept moves it, which is what "pitch warp" is.
    cen.push(Math.sqrt(sd / (sx + 1e-12)));
  }
  const first = Math.ceil(SETTLE_S / WIN);
  const slice = a => a.slice(first);
  const e = slice(env), ba = slice(bal), ce = slice(cen);

  // pan travel: how far the image moves across the file
  const balMin = Math.min(...ba), balMax = Math.max(...ba);

  // brightness wobble: worst window-to-window change, as a percentage
  let cw = 0;
  for (let i = 1; i < ce.length; i++) cw = Math.max(cw, Math.abs(ce[i] - ce[i - 1]) / (ce[i - 1] + 1e-9));

  // PAN REVERSALS. "Several doors one after the other" turned out not to be level dips at all --
  // pastdoor's whole level range is 2.6 dB, far too little to read as separate doorways. What makes
  // one opening sound like several is the IMAGE sweeping across and back repeatedly: each sweep
  // reads as another door going past. So count direction changes in the pan trajectory, ignoring
  // wobbles smaller than PAN_HYST so ordinary jitter is not counted as a reversal.
  let reversals = 0, dir = 0, anchor = ba[0];
  for (let i = 1; i < ba.length; i++) {
    const d = ba[i] - anchor;
    if (Math.abs(d) < PAN_HYST) continue;
    const nd = Math.sign(d);
    if (dir !== 0 && nd !== dir) reversals++;
    dir = nd; anchor = ba[i];
  }

  // dips: local minima at least DIP_DB below both surrounding peaks
  let dips = 0;
  for (let i = 1; i < e.length - 1; i++) {
    if (e[i] > e[i - 1] || e[i] > e[i + 1]) continue;
    let lp = e[i], rp = e[i];
    for (let j = i; j >= 0 && e[j] >= e[j + 1] - 0.01; j--) lp = Math.max(lp, e[j]);
    for (let j = i; j < e.length - 1 && e[j + 1] >= e[j] - 0.01; j++) rp = Math.max(rp, e[j + 1]);
    if (Math.min(lp, rp) - e[i] >= DIP_DB) dips++;
  }
  return { balMin, balMax, travel: balMax - balMin, wobble: 100 * cw, dips, reversals,
           range: Math.max(...e) - Math.min(...e) };
}

function main() {
  const only = process.argv[2];
  const dir = path.join(__dirname, "renders");
  const names = [...new Set(fs.readdirSync(dir).filter(f => f.endsWith(".wav"))
    .map(f => f.replace(/_(old|new)\.wav$/, "")))].filter(nm => !only || nm === only);
  console.log(`\nWHAT THE AUTHOR REPORTS HEARING, measured off the rendered files\n`);
  console.log(`${"scenario".padEnd(17)}${"model".padEnd(6)}${"pan travel".padStart(11)}${"L..R".padStart(14)}`
            + `${"bright wobble".padStart(15)}${"revs".padStart(6)}${"dips".padStart(6)}${"range".padStart(8)}`);
  console.log("-".repeat(77));
  for (const nm of names) {
    for (const model of ["old", "new"]) {
      const f = path.join(dir, `${nm}_${model}.wav`);
      if (!fs.existsSync(f)) continue;
      const a = analyse(f);
      console.log(`${(model === "old" ? nm : "").padEnd(17)}${model.padEnd(6)}`
        + `${a.travel.toFixed(1).padStart(8)} dB`
        + `${(a.balMax.toFixed(1) + ".." + a.balMin.toFixed(1)).padStart(14)}`
        + `${a.wobble.toFixed(1).padStart(14)}%${String(a.reversals).padStart(6)}${String(a.dips).padStart(6)}`
        + `${a.range.toFixed(1).padStart(7)} dB`);
    }
    console.log();
  }
  console.log(`pan travel = how far the image moves L to R. dips = distinct level minima at least`);
  console.log(`${DIP_DB} dB deep, so one doorway should read as one dip, not several.\n`);
}
main();
