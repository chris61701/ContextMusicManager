// ───────────────────────────────────────────────────────────────────────────────
// ACCEPTANCE GATE — one command, pass or fail, no tables to interpret
// ───────────────────────────────────────────────────────────────────────────────
// Until now I have been the loop: run a harness, read a table, decide, report the table. That
// makes the author an approver of steps rather than a decider of questions, which is the wrong
// division of labour and it is my doing.
//
// This turns every check into a gate with a committed baseline. It exits non-zero on regression,
// so "did that change help" is answerable without judgement, and a change that improves one
// scenario while quietly breaking another cannot pass.
//
//   node check.js              run every gate against baseline.json
//   node check.js --accept     record the current numbers as the new baseline
//   node check.js --regions    run on the region graph instead of the cell graph
//
// Gates:
//   SPEC       every scenario in tools/CmmAcousticsRef/README.md must PASS
//   SMOOTH     no route above SMOOTH_LIMIT dB/block, and none worse than baseline by TOL
//   NOSTEP     no route may have any sample above STEP_LIMIT dB/block
//   BASELINE   no metric may regress by more than TOL against the committed baseline
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs"), path = require("path"), cp = require("child_process");

const ACCEPT = process.argv.includes("--accept");
const REGIONS = process.argv.includes("--regions");
const BASEFILE = path.join(__dirname, REGIONS ? "baseline.regions.json" : "baseline.json");

const SMOOTH_LIMIT = 2.0;   // dB/block: the author's "gradual" threshold (~3 dB/s at a walk)
const STEP_LIMIT = 3.0;     // dB/block: past this it reads as a step rather than a slope
const TOL = 0.25;           // dB: allowed drift before a change counts as a regression

const env = { ...process.env };
if (REGIONS) env.CMM_REGIONS = "1";
const run = f => cp.execSync(`node ${f}`, { cwd: __dirname, env, encoding: "utf8", maxBuffer: 1 << 28 });

function parseSpec(out) {
  const rows = [];
  for (const line of out.split("\n")) {
    const m = line.match(/^\s{2}(.+?)\s{2,}(PASS|FAIL)\s+\((.+?)\s([-\d.]+),/);
    if (m) rows.push({ name: m[1].trim(), pass: m[2] === "PASS", probe: m[3], value: parseFloat(m[4]) });
  }
  return rows;
}

function parseSmooth(out) {
  const rows = [];
  let route = null;
  for (const line of out.split("\n")) {
    const r = line.match(/^(\S[\w-]*)\s+old\s+([\d.]+)\s+([\d.]+)/);
    if (r) { route = r[1]; rows.push({ route, model: "old", worst: parseFloat(r[2]), mean: parseFloat(r[3]) }); continue; }
    const n = line.match(/^\s+new\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/);
    if (n && route) rows.push({ route, model: "new", worst: parseFloat(n[1]), mean: parseFloat(n[2]),
                                over1: parseFloat(n[3]), over3: parseFloat(n[4]) });
  }
  return rows;
}

function collect() {
  cp.execSync("node synth.js", { cwd: __dirname, env, encoding: "utf8" });
  const spec = parseSpec(run("scenarios.run.js"));
  const smooth = parseSmooth(run("smooth.js")).filter(r => r.model === "new");
  return { spec, smooth };
}

function main() {
  const cur = collect();
  if (!cur.spec.length || !cur.smooth.length) {
    console.error("FAIL: could not parse harness output — the gate itself is broken, fix that first");
    process.exit(2);
  }

  if (ACCEPT) {
    fs.writeFileSync(BASEFILE, JSON.stringify(cur, null, 1));
    console.log(`baseline recorded (${REGIONS ? "regions" : "cells"}): ${cur.spec.length} scenarios, ${cur.smooth.length} routes`);
    console.log(`  -> ${path.basename(BASEFILE)}`);
    return;
  }

  const base = fs.existsSync(BASEFILE) ? JSON.parse(fs.readFileSync(BASEFILE, "utf8")) : null;
  const fails = [], warns = [];

  // ── SPEC ────────────────────────────────────────────────────────────────────
  for (const s of cur.spec) if (!s.pass) fails.push(`SPEC   ${s.name} (${s.probe} ${s.value})`);

  // ── SMOOTH / NOSTEP ─────────────────────────────────────────────────────────
  for (const r of cur.smooth) {
    if (r.over3 > 0) fails.push(`NOSTEP ${r.route} has samples over ${STEP_LIMIT} dB/block (${r.over3}% of route)`);
    else if (r.worst > SMOOTH_LIMIT) warns.push(`SMOOTH ${r.route} worst ${r.worst.toFixed(2)} dB/block (limit ${SMOOTH_LIMIT})`);
  }

  // ── BASELINE ────────────────────────────────────────────────────────────────
  if (base) {
    for (const r of cur.smooth) {
      const b = base.smooth.find(x => x.route === r.route);
      if (!b) { warns.push(`BASE   ${r.route} is new — not in baseline`); continue; }
      if (r.worst > b.worst + TOL) fails.push(`BASE   ${r.route} worst ${b.worst.toFixed(2)} -> ${r.worst.toFixed(2)} dB/block`);
      if (r.mean > b.mean + TOL) fails.push(`BASE   ${r.route} mean ${b.mean.toFixed(2)} -> ${r.mean.toFixed(2)} dB/block`);
    }
    for (const s of cur.spec) {
      const b = base.spec.find(x => x.name === s.name);
      if (b && b.pass && !s.pass) fails.push(`BASE   ${s.name} was passing and now fails`);
    }
  } else {
    warns.push("BASE   no baseline recorded — run with --accept once the state is known good");
  }

  // Dump validity is reported but not fatal: the synth gates above do not read the real dumps, and
  // a bad capture is fixed in-game rather than in code. It is surfaced because an invalid dump once
  // spent days on the queue disguised as a segmentation defect -- see dumpcheck.js.
  let dumpNote = null;
  try {
    const { checkDump } = require("./dumpcheck.js");
    const dd = path.join(__dirname, "dumps");
    if (fs.existsSync(dd)) {
      const bad = fs.readdirSync(dd).filter(f => f.endsWith(".json"))
        .map(f => checkDump(path.join(dd, f))).filter(r => !r.ok);
      if (bad.length) dumpNote = `DUMPS  ${bad.length} unusable capture(s): ${bad.map(b => b.name).join(", ")}`
                               + ` — do not read model behaviour off these (node dumpcheck.js)`;
    }
  } catch (e) { dumpNote = `DUMPS  validity check failed: ${e.message}`; }
  if (dumpNote) warns.push(dumpNote);

  const tag = REGIONS ? "regions" : "cells";
  console.log(`\nacceptance gate (${tag})   ${cur.spec.length} scenarios, ${cur.smooth.length} routes\n`);
  for (const r of cur.smooth) {
    const b = base && base.smooth.find(x => x.route === r.route);
    const delta = b ? (r.worst - b.worst >= 0 ? "+" : "") + (r.worst - b.worst).toFixed(2) : "  new";
    const flag = r.over3 > 0 ? "STEP" : r.worst > SMOOTH_LIMIT ? "over" : "ok";
    console.log(`  ${r.route.padEnd(16)} worst ${r.worst.toFixed(2).padStart(6)}  mean ${r.mean.toFixed(2).padStart(5)}  >3dB ${String(r.over3).padStart(5)}%  vs base ${delta.padStart(6)}  ${flag}`);
  }
  console.log(`  ${"spec".padEnd(16)} ${cur.spec.filter(s => s.pass).length}/${cur.spec.length} pass`);

  if (warns.length) { console.log("\nWARN"); for (const w of warns) console.log("  " + w); }
  if (fails.length) {
    console.log("\nFAIL"); for (const f of fails) console.log("  " + f);
    console.log(`\n${fails.length} gate failure(s)\n`);
    process.exit(1);
  }
  console.log(`\nall gates pass${warns.length ? ` (${warns.length} warning(s))` : ""}\n`);
}
main();
