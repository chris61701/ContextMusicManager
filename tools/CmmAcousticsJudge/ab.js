// ───────────────────────────────────────────────────────────────────────────────
// AB — put the same moment side by side, labelled, in ONE file
// ───────────────────────────────────────────────────────────────────────────────
// "With these general tones, I don't even know what I'm listening for sometimes." That is a flaw
// in how the comparison was presented, not in the listening.
//
// Handing over two files and asking which is better requires holding a sound in memory across a
// file switch, and for a scenario where the listener is MOVING the two files are not even
// comparable moment to moment -- second 4 of one is a different place from second 12 of the other.
//
// So: extract the SAME time window from both renders and play them back to back, each preceded by
// a spoken-equivalent marker (a high blip for A, a low one for B), with a moment of silence
// between. Same position, same music, one difference. Nothing to remember and nothing to count.
//
//   node ab.js <A.wav> <B.wav> <out.wav> [--at=7.6,3.0] [--label=wide,centre]
//     --at      centre,seconds of each window (repeatable, comma separated) — default auto
//     --window  seconds per excerpt (default 3)
//
// Auto window picking finds where the two files differ MOST, since that is the moment worth
// hearing, and adds one quiet stretch as a control.
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path");

const SR = 48000;

function readWav(file) {
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

function writeWav(file, L, R) {
  const n = L.length, bytes = n * 4;
  const b = Buffer.alloc(44 + bytes);
  b.write("RIFF", 0, "ascii"); b.writeUInt32LE(36 + bytes, 4); b.write("WAVE", 8, "ascii");
  b.write("fmt ", 12, "ascii"); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20);
  b.writeUInt16LE(2, 22); b.writeUInt32LE(SR, 24); b.writeUInt32LE(SR * 4, 28);
  b.writeUInt16LE(4, 32); b.writeUInt16LE(16, 34);
  b.write("data", 36, "ascii"); b.writeUInt32LE(bytes, 40);
  const clip = v => Math.max(-32768, Math.min(32767, Math.round(v * 32767)));
  for (let i = 0; i < n; i++) { b.writeInt16LE(clip(L[i]), 44 + i * 4); b.writeInt16LE(clip(R[i]), 46 + i * 4); }
  fs.writeFileSync(file, b);
}

// a short marker tone: high = first of the pair, low = second. Centre panned, quiet.
function marker(hz, secs = 0.16) {
  const n = Math.floor(SR * secs), out = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const env = Math.min(1, i / 240) * Math.min(1, (n - i) / 240);
    out[i] = Math.sin(2 * Math.PI * hz * i / SR) * env * 0.16;
  }
  return out;
}

function main() {
  const [fa, fb, fout] = process.argv.slice(2).filter(a => !a.startsWith("--"));
  if (!fa || !fb || !fout) { console.error("usage: node ab.js A.wav B.wav out.wav [--at=..] [--window=3] [--label=a,b]"); process.exit(1); }
  const arg = k => (process.argv.find(a => a.startsWith(`--${k}=`)) || "").split("=")[1];
  const win = parseFloat(arg("window") || "3");
  const labels = (arg("label") || "A,B").split(",");

  const A = readWav(fa), B = readWav(fb);
  const n = Math.min(A.L.length, B.L.length);

  let centres;
  if (arg("at")) centres = arg("at").split(",").map(Number);
  else {
    // where do they differ most? compare short-term level and short-term L/R balance
    const w = Math.floor(SR * 0.25), m = Math.floor(n / w);
    let best = 0, bestK = 0;
    for (let k = 0; k < m; k++) {
      let al = 0, ar = 0, bl = 0, br = 0;
      for (let j = k * w; j < (k + 1) * w; j++) {
        al += A.L[j] * A.L[j]; ar += A.R[j] * A.R[j];
        bl += B.L[j] * B.L[j]; br += B.R[j] * B.R[j];
      }
      const dLev = Math.abs(10 * Math.log10((al + ar + 1e-12) / (bl + br + 1e-12)));
      const dBal = Math.abs(10 * Math.log10((al + 1e-12) / (ar + 1e-12)) - 10 * Math.log10((bl + 1e-12) / (br + 1e-12)));
      const d = dLev + dBal;
      if (d > best) { best = d; bestK = k; }
    }
    const tBest = bestK * 0.25;
    centres = [tBest, Math.max(win / 2, tBest - 5)];
    console.log(`auto: biggest difference at ${tBest.toFixed(1)}s (${best.toFixed(1)} dB combined), `
              + `plus a control window at ${centres[1].toFixed(1)}s`);
  }

  const segs = [];
  const gap = new Float32Array(Math.floor(SR * 0.35));
  const mHi = marker(1500), mLo = marker(700);
  const guide = [];
  for (const c of centres) {
    const a0 = Math.max(0, Math.floor((c - win / 2) * SR));
    const a1 = Math.min(n, a0 + Math.floor(win * SR));
    for (const [src, mk, lab] of [[A, mHi, labels[0]], [B, mLo, labels[1]]]) {
      segs.push({ mk, L: src.L.subarray(a0, a1), R: src.R.subarray(a0, a1) });
      guide.push(`${lab} @ ${c.toFixed(1)}s`);
    }
  }

  let total = 0;
  for (const s of segs) total += s.mk.length + s.L.length + gap.length;
  const L = new Float32Array(total), R = new Float32Array(total);
  let o = 0;
  const stamps = [];
  for (let i = 0; i < segs.length; i++) {
    const s = segs[i];
    for (let j = 0; j < s.mk.length; j++) { L[o + j] += s.mk[j]; R[o + j] += s.mk[j]; }
    o += s.mk.length;
    stamps.push(`${(o / SR).toFixed(1)}s  ${guide[i]}`);
    L.set(s.L, o); R.set(s.R, o); o += s.L.length;
    o += gap.length;
  }
  writeWav(fout, L, R);
  console.log(`\n${path.basename(fout)}  ${(total / SR).toFixed(1)}s`);
  console.log(`high blip = ${labels[0]},  low blip = ${labels[1]}.  Same moment, twice, one difference.\n`);
  for (const s of stamps) console.log("   " + s);
  console.log();
}
main();
