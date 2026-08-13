// ───────────────────────────────────────────────────────────────────────────────
// SYNTHETIC GEOMETRY — controlled test cases for the spec scenarios
// ───────────────────────────────────────────────────────────────────────────────
// The POI dumps captured whatever state the doors happened to be in, so they cannot
// isolate "door shut" from "door open". Four of the six spec scenarios hinge on exactly
// that comparison (#1, #2, #4, #6), so they are untestable from real dumps.
//
// This builds dump-shaped geometry we control completely: two rooms, one wall, one
// doorway, one speaker. Wall material, door material and door openness are parameters.
// It emits the same JSON schema as `cmm acsweep`, so both models read it unchanged and
// the existing judges/measures apply.
//
// Scenario #4 is the important one it unlocks: the SAME vault door in a wooden wall
// versus in a concrete wall. Flanking says the first seals nothing and the second seals
// completely, and a min-over-routes model cannot express the difference.
//
// Usage:  node synth.js               -> writes synth/*.json for the whole matrix
//         node synth.js --list        -> just print the matrix
// ───────────────────────────────────────────────────────────────────────────────
"use strict";
const fs = require("fs");
const path = require("path");

// Room layout, in local voxel coords. Two rooms side by side in x, split by one wall.
//   x: 0                 W                    SX
//      | room A (speaker) |#| room B (listener) |
const SX = 44, SY = 12, SZ = 24;
const WALLX = 18;                  // the dividing wall plane (1 block thick)
const DOOR = { y0: 1, y1: 3, z0: 10, z1: 12 };   // doorway opening in that wall (2w x 3h)
const SPK = { x: 7, y: 3, z: 11 };                // speaker in room A
const SHELL = 1;                                  // outer shell thickness

// Material loss in FULL-WALL units, matching EmitterAcoustics.CellLoss conventions
// (metal 1.0, stone/concrete 0.9, wood 0.5, and AirtightLoss 4.0 for a sealed vault).
const MAT = { air: 0.0, wood: 0.5, concrete: 0.9, metal: 1.0, vault: 4.0 };

const CFG = { wallDistance: 35, range: 100, minDistance: 1, falloff: 2.5, maxDistance: 30,
              hardStop: 68, floodRadius: 32, pathRouting: true, directional: true,
              reverbRange: 64, reverbWet: 0.6, reverbCarry: 0.8, costScale: 4,
              fieldCache: true, fieldRadius: 48 };

const FLAG = { air: 1, water: 2, seeThrough: 4, terrain: 8, solidCube: 16, emitter: 32 };
const LOSS_SCALE = 32;   // uint8 / 32 -> up to 7.97, enough for the vault's 4.0

// index order must match the dumps: x + sx*(z + sz*y)
const li = (x, y, z) => x + SX * (z + SZ * y);

function build({ wall, door, open }) {
  const n = SX * SY * SZ;
  const loss = Buffer.alloc(n);
  const flags = Buffer.alloc(n);
  const block = Buffer.alloc(n * 2);
  const blocks = ["air", "wall", "door"];

  const put = (i, lossVal, blockId) => {
    loss[i] = Math.min(255, Math.round(lossVal * LOSS_SCALE));
    let f = 0;
    if (lossVal <= 0.001) f |= FLAG.air;
    else f |= FLAG.solidCube;
    flags[i] = f;
    block.writeUInt16LE(blockId, i * 2);
  };

  for (let y = 0; y < SY; y++)
  for (let z = 0; z < SZ; z++)
  for (let x = 0; x < SX; x++) {
    const i = li(x, y, z);
    // outer shell: seal the box so nothing flanks around the OUTSIDE of the building
    const shell = x < SHELL || x >= SX - SHELL || y < SHELL || y >= SY - SHELL || z < SHELL || z >= SZ - SHELL;
    if (shell) { put(i, MAT.concrete, 1); continue; }
    if (x === WALLX) {
      const inDoor = y >= DOOR.y0 && y <= DOOR.y1 && z >= DOOR.z0 && z <= DOOR.z1;
      if (!inDoor) { put(i, MAT[wall], 1); continue; }
      // The doorway. `open` 1 = clear air, 0 = the door leaf fully shut, in between =
      // partially swung, modelled as the aperture it is: the top of the opening clears
      // first (the leaf occupies the lower part), so openness maps to open ROWS.
      const rows = DOOR.y1 - DOOR.y0 + 1;
      const clearFrom = DOOR.y1 - Math.floor(open * rows) + 1;
      const isClear = open >= 0.999 || y >= clearFrom;
      put(i, isClear ? MAT.air : MAT[door], 2);
      continue;
    }
    put(i, MAT.air, 0);
  }

  // mark the speaker cell solid (a speaker IS a block) so the seeding path is exercised
  const si = li(SPK.x, SPK.y, SPK.z);
  put(si, MAT.wood, 1);
  flags[si] |= FLAG.emitter;

  return {
    version: 2,
    poi: `synth_wall-${wall}_door-${door}_open-${open}`,
    poiId: 0,
    poiBounds: { x: 0, y: 0, z: 0, sx: SX, sy: SY, sz: SZ },
    bounds: { x: 0, y: 0, z: 0, sx: SX, sy: SY, sz: SZ },
    order: "index = x + sx*(z + sz*y); coords relative to bounds origin",
    lossScale: LOSS_SCALE,
    flagBits: FLAG,
    config: CFG,
    emitters: [{ x: SPK.x, y: SPK.y, z: SPK.z, name: "synthSpeaker", virtual: false }],
    blocks,
    loss: loss.toString("base64"),
    flags: flags.toString("base64"),
    block: block.toString("base64"),
    // the regions the measure step reports on
    regions: {
      roomA: { x0: SHELL, x1: WALLX - 1, y0: SHELL, y1: SY - SHELL - 1, z0: SHELL, z1: SZ - SHELL - 1 },
      roomB: { x0: WALLX + 1, x1: SX - SHELL - 1, y0: SHELL, y1: SY - SHELL - 1, z0: SHELL, z1: SZ - SHELL - 1 },
      door: DOOR, wallX: WALLX, speaker: SPK,
    },
  };
}

const MATRIX = [
  // scenario #2 — door open: room B should be nearly line-of-sight loud, from the doorway
  { wall: "wood",     door: "air",   open: 1.0,  tag: "S2 door OPEN, wood wall" },
  // scenario #1 — interior door shut: clearly quieter but obviously still music (~ -6 dB)
  { wall: "wood",     door: "wood",  open: 0.0,  tag: "S1 door SHUT, wood wall" },
  // scenario #6 — mid-swing, should be gradual
  { wall: "wood",     door: "wood",  open: 0.33, tag: "S6 door 1/3 open" },
  { wall: "wood",     door: "wood",  open: 0.66, tag: "S6 door 2/3 open" },
  // scenario #3 — heavy wall, doors shut: faint, material dependent
  { wall: "concrete", door: "wood",  open: 0.0,  tag: "S3 door SHUT, concrete wall" },
  // scenario #4 — THE FLANKING TEST. Same vault door, two different walls.
  { wall: "wood",     door: "vault", open: 0.0,  tag: "S4 VAULT door in WOOD wall (should NOT seal)" },
  { wall: "concrete", door: "vault", open: 0.0,  tag: "S4 VAULT door in CONCRETE wall (should seal)" },
];

function main() {
  if (process.argv.includes("--list")) { for (const m of MATRIX) console.log(m.tag); return; }
  const dir = path.join(__dirname, "synth");
  fs.mkdirSync(dir, { recursive: true });
  for (const m of MATRIX) {
    const d = build(m);
    d.tag = m.tag;
    const f = path.join(dir, d.poi + ".json");
    fs.writeFileSync(f, JSON.stringify(d));
    console.log(`${m.tag.padEnd(48)} -> synth/${path.basename(f)}`);
  }
}
if (require.main === module) main();
module.exports = { build, MATRIX, SX, SY, SZ, WALLX, DOOR, SPK };
