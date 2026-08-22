// CMM Dial Lab — self test.   Run:  node selftest.js
//
// The lab is a browser page with no build step and no test framework, which means a single
// exception on load takes the whole thing out silently: the script is one IIFE, so a throw
// anywhere in setup skips every line after it and the page still renders perfectly. That is
// exactly what happened -- a `const` read from inside a function that ran above its declaration
// killed the page from the day it was written, and it looked fine.
//
// So this does two things, both against the REAL file:
//   1. Boots the script under a stub browser and reports anything that throws.
//   2. Lifts the signal model out verbatim and sweeps the dial across two stations, checking the
//      behaviour the lab's own instructions tell you to listen for.
//
// It cannot tell you whether the numbers sound good. That is what your ears are for.

const fs = require("fs");
const path = require("path");
const HTML = path.join(__dirname, "index.html");
const src = fs.readFileSync(HTML, "utf8");
const js = /<script>([\s\S]*)<\/script>/.exec(src)[1];
const lines = js.split(/\r?\n/);
let failures = 0;
const fail = m => { failures++; console.log("  FAIL  " + m); };
const pass = m => console.log("  ok    " + m);

// ── 1. Every element the script reaches for must exist in the markup ──────────────────────────
const ids = new Set([...src.matchAll(/id="([^"]+)"/g)].map(m => m[1]));
const refs = new Set([...js.matchAll(/getElementById\("([^"]+)"\)/g)].map(m => m[1]));
console.log("\nWiring");
const missing = [...refs].filter(r => !ids.has(r));
missing.length ? fail("referenced but not in the markup: " + missing.join(", "))
               : pass(`${refs.size} referenced elements all exist`);

// Every parameter needs a slider and a readout, or it cannot be tuned.
const P0 = eval("(" + /const P = (\{[\s\S]*?\});/.exec(js)[1] + ")");
const keys = Object.keys(P0);
const cap1 = k => k[0].toUpperCase() + k.slice(1);
const orphan = keys.filter(k => !ids.has("p" + cap1(k)) || !ids.has("o" + cap1(k)));
orphan.length ? fail("parameters with no control: " + orphan.join(", "))
              : pass(`${keys.length} parameters all have a slider and a readout`);

// ── 2. Boot it ────────────────────────────────────────────────────────────────────────────────
console.log("\nBoot");
const mkParam = () => ({ value: 0, setValueAtTime(){return this}, linearRampToValueAtTime(){return this},
                         setTargetAtTime(){return this}, cancelScheduledValues(){return this} });
const mkNode = kind => new Proxy({
  kind, gain: mkParam(), frequency: mkParam(), Q: mkParam(), detune: mkParam(),
  type: "lowpass", buffer: null, loop: false, playbackRate: mkParam(),
  connect: n => n, disconnect(){}, start(){}, stop(){},
}, { get: (t, p) => (p in t ? t[p] : () => mkNode("unknown")) });
const audio = {
  sampleRate: 48000, currentTime: 0, state: "running", destination: mkNode("dest"),
  resume: async () => {}, suspend: async () => {},
  createGain: () => mkNode("gain"), createBiquadFilter: () => mkNode("biquad"),
  createBufferSource: () => mkNode("src"), createChannelSplitter: () => mkNode("split"),
  createChannelMerger: () => mkNode("merge"),
  createBuffer: (ch, len) => ({ numberOfChannels: ch, length: len, sampleRate: 48000,
                                getChannelData: () => new Float32Array(len) }),
  decodeAudioData: async () => ({ numberOfChannels: 2, length: 48000, sampleRate: 48000,
                                  duration: 1, getChannelData: () => new Float32Array(48000) }),
};
const store = {};
const el = id => ({
  id, value: "", textContent: "", innerHTML: "", checked: false, files: [], _h: null,
  style: {}, dataset: {}, width: 800, height: 200,
  classList: { add(){}, remove(){}, toggle(){}, contains: () => false },
  addEventListener(ev, fn){ (this._h ||= {})[ev] = fn; },
  removeEventListener(){}, click(){}, focus(){}, blur(){}, appendChild(){}, removeChild(){},
  setAttribute(){}, getAttribute: () => null,
  querySelector: () => el("q"), querySelectorAll: () => [],
  getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 200 }),
  getContext: () => new Proxy({ canvas: { width: 800, height: 200 } },
                              { get: (t, p) => (p in t ? t[p] : () => ({})) }),
});
const winEvents = {};
Object.assign(globalThis, {
  AudioContext: function(){ return audio; }, webkitAudioContext: function(){ return audio; },
  document: { getElementById: id => (store[id] ||= el(id)), querySelector: () => el("q"),
              querySelectorAll: () => [], addEventListener(){}, createElement: () => el("new"),
              body: el("body"), documentElement: el("html"), hidden: false },
  requestAnimationFrame: () => 0, cancelAnimationFrame: () => {},
  addEventListener: (ev, fn) => { winEvents[ev] = fn; }, removeEventListener: () => {},
  matchMedia: () => ({ matches: false, addEventListener(){}, addListener(){} }),
  setInterval: () => 0, localStorage: { getItem: () => null, setItem(){}, removeItem(){} },
});
globalThis.window = globalThis;
globalThis.getSelection = () => ({ removeAllRanges(){}, addRange(){} });
// Node 20+ ships a read-only `navigator` global, so it has to be redefined rather than assigned.
Object.defineProperty(globalThis, "navigator", {
  value: { clipboard: { writeText: async () => {} } }, configurable: true, writable: true,
});
try {
  new Function(js)();
  pass("script ran to completion, no exception");
} catch (e) {
  fail(`threw on load — ${e.constructor.name}: ${e.message}`);
  console.log("        " + (e.stack || "").split("\n")[1]);
}
const wired = Object.values(store).filter(v => v._h).length;
wired >= 25 ? pass(`${wired} elements have handlers attached`)
            : fail(`only ${wired} elements wired — setup stopped early`);
const exported = (store["export"] || {}).textContent || "";
exported.includes("<RadioDial") ? pass(`export panel generated ${exported.length} chars`)
                                : fail("export panel is empty — updateExport never ran");

// ── 3. The signal model, lifted verbatim and swept ────────────────────────────────────────────
console.log("\nSignal model (two stations 2.4 MHz apart, current settings)");
const from = /function stationResponse/.exec(js).index;
const to = js.indexOf("// ── Frame loop");
const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
const dbToGain = db => Math.pow(10, db / 20);
const stations = [{ freq: 94.0, name: "A" }, { freq: 96.4, name: "B" }];
// Built as a function body so the lifted source keeps its own scope and its free variables
// (P, stations, flutterPhase, the two helpers) are supplied rather than shadowed.
const computeModel = new Function(
  "P", "clamp", "dbToGain", "stations",
  "let flutterPhase = 0;\n" + js.slice(from, to) + "\nreturn computeModel;"
)(P0, clamp, dbToGain, stations);
const at = f => computeModel(f, 0);

const centre = at(94.0), off = at(94.9), dead = at(95.2);
const check = (ok, good, bad) => (ok ? pass(good) : fail(bad));
check(centre.S > 0.99, "signal reaches full at a station centre",
      `signal only reaches ${centre.S.toFixed(3)} at a station centre`);
check(centre.hicut > off.hicut * 1.4,
      `treble opens with signal (${Math.round(off.hicut)} Hz weak -> ${Math.round(centre.hicut)} Hz strong)`,
      "high-cut barely moves — a weak station will not go dull, it will just go quiet");
check(centre.width === 1 && off.width < 1,
      `stereo collapses off-centre (${off.width.toFixed(2)} at the skirt)`,
      "stereo never collapses — blend is doing nothing");
check(centre.noise < off.noise,
      `noise falls as signal rises (${off.noise.toFixed(3)} -> ${centre.noise.toFixed(3)})`,
      "noise does not track signal");
check(dead.mute < 0.5, `soft mute engages between stations (${dead.mute.toFixed(3)})`,
      "soft mute never engages — the dead zone will be full-volume hiss");

// At the EXACT midpoint two identical stations are equal by symmetry, and mute has them near
// silence anyway. What matters is how FAST one wins as you leave it — that is capture, and a real
// receiver's is far harder than people expect.
let width = null;
for (let d = 0; d <= 0.6; d += 0.005) {
  const m = at(95.2 + d);
  if (Math.min(...m.share) < 0.05) { width = d; break; }
}
check(width !== null && width <= 0.35,
      `capture resolves ${width === null ? "" : (width * 1000).toFixed(0) + " kHz"} off the midpoint`,
      "capture never resolves — the two stations blend across the whole gap");

console.log("\nSweep");
console.table([94.0, 94.6, 94.9, 95.2, 95.5, 95.8, 96.4].map(f => {
  const m = at(f);
  return { MHz: f.toFixed(2), signal: m.S.toFixed(3), noise: m.noise.toFixed(3),
           "high-cut Hz": Math.round(m.hicut), stereo: m.width.toFixed(2),
           mute: m.mute.toFixed(3), tuned: m.bestI < 0 ? "-" : stations[m.bestI].name };
}));

console.log(failures ? `\n${failures} FAILURE(S)\n` : "\nAll checks passed.\n");
process.exit(failures ? 1 : 0);
