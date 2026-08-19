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
// Overridable so the value can be swept against the gate rather than argued about. The native
// side made the same constant runtime-settable for the same reason -- the author's first in-game
// verdict was that a wall does not sound like a wall, and that is a judgement about THIS number.
const DB_MAT = process.env.CMM_DBMAT === undefined ? 40.1 : +process.env.CMM_DBMAT;
                            // scale: one voxel of the densest ordinary material (frac 1.0)
// Sweepable with DB_MAT: together they set how far APART a door and a wall are priced, which is
// the defect underneath 'a wall sounds like barely anything'. dB = DB_MAT * frac^MAT_POW, so
// MAT_POW is the SPREAD (a door's frac is low, a wall's high) and DB_MAT the overall scale.
const MAT_POW = process.env.CMM_MATPOW === undefined ? 2.74 : +process.env.CMM_MATPOW;       // curve: (0.9/0.5)^POW = 5, i.e. concrete costs 5x wood
const DB_VOXEL_MAX = 60.0;
const VERT_MULT = 2.6;      // a voxel crossed vertically stands for a floor assembly, not a wall  // per-voxel ceiling (an airtight vault leaf)
const SLOPE = 0.35;
// ── DISTANCE, AS A LAW RATHER THAN A SLOPE ───────────────────────────────────
// SLOPE dB/block is exponential, and at 0.35 it is nearly flat: eight blocks costs 2.8 dB
// where inverse-square costs 18. Two consequences, both heard in game and neither fixable
// by tuning:
//
//   a speaker sounds the same distance away wherever you stand; and
//   a DETOUR is almost free -- routing three times as far around through a doorway costs
//   3.5 dB where the physical answer is 9.5 -- so being behind a wall with an open door
//   elsewhere in the house is barely quieter than standing in front of the speaker.
//
// The second is the live complaint: "wall 100, speaker right behind the wall, still sounds
// like it isn't muffled at all". No material constant can fix it, because the sound is not
// coming through the material; it is coming AROUND, and the way around is priced at nothing.
//
// The bridge now transports a RATIO against this model's own free field, so what matters is
// the law's SHAPE, not its scale -- which is what makes changing it safe now and unsafe
// before. Routing still orders on SLOPE*length (Dijkstra wants an additive metric and length
// is one, monotone either way); only the LEVEL uses the law.
// DEFAULT ON. CMM_EXPLAW=1 restores the shipped exponential law for an A/B.
const INV_SQ = process.env.CMM_EXPLAW !== "1";
// DEFAULT ON, for the same reason: it is what makes the room boundary continuous.
const SA_COUPLE = process.env.CMM_NOSA !== "1";
//
// A RADIATOR IS NOT A POINT, and 1/r says otherwise the moment you stand next to one. The
// derivative of 20*log10(r) is 8.7/r dB per block, so at one block it is 8.7 dB/block and at half
// a block it is 17 -- a cliff, purely from the algebra, sitting exactly where a listener walks:
// through the doorway. That is the roughness the law change bought, and it is not a modelling
// error to be tuned out. It is the near field of a point source, and there are no point sources
// here: a speaker is a block, and an opening is a doorway-sized sheet of air.
//
// A finite radiator of radius a is effectively a plane wave for r << a (no falloff -- you are
// inside its near field) and 1/r for r >> a. sqrt(r^2 + a^2) is the standard way to write that,
// and it removes the singularity without touching the far field, which is where every
// calibration lives. Same reasoning as measuring to an opening's EXTENT rather than its centroid,
// which is already in the model for the diffuse coupling.
const distDb = (len, rad = 0.5) =>
  INV_SQ ? 20 * Math.log10(Math.max(1, Math.hypot(len, rad))) : SLOPE * len;
// An opening's own radius, from its area: the scale over which it stops looking like a point.
const apRad = A => Math.max(0.5, Math.sqrt(Math.max(1, A.area || 1) / Math.PI));         // distance attenuation, dB per block (exponential curve)
const OPEN_LOSS = 0.05;     // per-voxel loss at/below which a voxel is FULLY open
const OPEN_FULL = 0.5;      // ...and at/above which it is fully closed; between, a fraction
const OPEN_EPS  = 0.02;     // openness below this does not connect at all
const KEEP_PORTALS = 4;     // entry portals retained per node (multi-route melding)
const AIR_FLAG = 1;
// Offsets for the transmission fan (see makeNewEvaluator): the listener itself plus
// +/-2 blocks on each axis, so the ray set can find a weaker patch of barrier nearby.
// ── transmission sampling: a FRESNEL-ZONE DISC, area-weighted ─────────────────
// A 7-ray axis cross reduced by max/mean/sum was the wrong instrument, confirmed by
// exhaustion: every reduction traded scenario correctness against smoothness, because a
// winner-takes-all operator steps when the winning ray crosses a feature edge, while a
// count-normalised average divides by the number of samples and so misreports a barrier
// whose patches differ (a vault door beside a wood wall came out 8 dB too quiet).
//
// What transmission through a distributed barrier actually is: an integral over the barrier
// of what each patch re-radiates. So sample a DISC perpendicular to the source-listener
// line, weight each sample by its AREA SHARE, and energy-sum. Then:
//   - it is smooth, because the disc moves continuously with the listener and each sample's
//     material now varies continuously too (segDb became a line integral);
//   - the weakest patch dominates without any winner-takes-all step, because an energy sum
//     is dominated by its largest term -- which is what flanking needs;
//   - the normalisation is by AREA, not by sample count, so adding samples refines the
//     estimate instead of shifting it.
//
// The radius is the first Fresnel zone's scale: sqrt(lambda*d)/2 is about 2 blocks at 500 Hz
// over 23 m and about 4 at 100 Hz, so a few blocks is the right order -- the old cross was
// roughly the right SIZE and wrong in every other respect.
const DIFF_VOX = process.env.CMM_DIFFVOX ? process.env.CMM_DIFFVOX.split(',').map(Number) : null;
const FAN_DBG = process.env.CMM_FANDBG === undefined ? null : +process.env.CMM_FANDBG;
const CAND_DBG = process.env.CMM_CANDDBG ? process.env.CMM_CANDDBG.split(',').map(Number) : null;
// ── THE ZONE'S RADIUS IS GEOMETRY, NOT A CONSTANT ────────────────────────────
// This was a flat 3.0 blocks wherever the barrier happened to be, and that is wrong in the one
// place it matters most. The first Fresnel zone's radius is
//
//     r = sqrt(lambda * d1 * d2 / (d1 + d2))
//
// which COLLAPSES as the barrier approaches either end of the path. A speaker sits inside its
// own block, so for the near barrier -- the speaker's own casing -- d1 is about a quarter of a
// block and the true radius is ~0.6, not 3.0. At 3.0 the disc splayed samples nearly three
// blocks off-axis on a five-block path: they were not sampling a barrier surface at all, they
// were sampling completely different routes, some clipping the hatch rim and some not. Each one
// flipped between 0 and 5-11 dB independently as the listener moved, and the sum stepped. That
// is the whole of pasthatch's 2.40 dB/block, and it is why adding samples made it WORSE -- more
// samples of an incoherent set is more things to flip.
//
// Mid-path the formula gives sqrt(1.7*d/4), which is 2.9 blocks over 20 -- so the old constant
// was right for the case it was fitted on and wrong everywhere else. Nothing needs recalibrating.
// OFF BY DEFAULT, and the reason is worth keeping. The geometric radius is the correct
// physics and it fixes what it was aimed at -- pasthatch 2.40 -> 1.93 dB/block, under the
// limit for the first time. But it makes the disc SMALLER almost everywhere (a wall five
// blocks away gets 1.46 rather than 3.0), and at that size 15 samples under-resolve it: the
// real captures roughened in step (bunker 2.57 -> 3.26, cave 1.31 -> 1.90).
//
// So the flat 3.0 was doing two jobs -- a zone radius, badly, and a spatial smoother, well --
// and only the first can be replaced on its own. Finishing this means raising the sample count
// to match the smaller disc, which is a cost decision on the native side, not a free swap.
// Enable with CMM_FZGEO=1.
// DEFAULT ON. It is the physics, and it is what removes the spurious occlusion the disc
// reported beside real shelf-mounted speakers (-3.3 dB in the bunker, -2.1 dB in the store,
// with nothing in the way -> -0.7 and -0.6). Dismissed once on a smoothness number measured
// under the OLD distance law; under the current one it costs nothing: spec 9/9, passivity
// unchanged, and occlusion roughness still better than the shipped model on every valid
// capture. CMM_FZFIXED=1 restores the flat 3.0-block disc.
const FZ_GEO = process.env.CMM_FZFIXED !== "1";
const FRESNEL_R_FIXED = 3.0;
const FRESNEL_LAMBDA = 1.7;              // ~200 Hz, the band that carries through structure
                                         // (same value as WAVELEN_BLOCKS below, declared here so
                                         //  the zone radius does not depend on declaration order)
// FLOOR AT ONE BLOCK. Not a safety margin -- a resolution limit. The geometry IS a voxel grid,
// so no feature smaller than a block exists in it, and a disc narrower than that is sampling
// below the resolution of its own input: it collapses toward a single ray and steps every time
// that ray crosses a voxel face. Measured: at 0.35 the real captures roughened (bunker 2.57 ->
// 3.26) even as the synthetic hatch improved, which is the signature of under-sampling, not of
// a wrong radius.
const FRESNEL_R_MIN = process.env.CMM_FRMIN === undefined ? 1.0 : +process.env.CMM_FRMIN;
const FRESNEL_R_MAX = 4.0;               // and never wider than the features it is measuring
function fresnelR(d1, d2) {
  const r = Math.sqrt(FRESNEL_LAMBDA * d1 * d2 / Math.max(1e-3, d1 + d2));
  return r < FRESNEL_R_MIN ? FRESNEL_R_MIN : r > FRESNEL_R_MAX ? FRESNEL_R_MAX : r;
}
// Rings as [radius fraction, sample count]; annulus boundaries at 0.25/0.75/1.0 of R give
// the area shares below, summing to 1.
// ── HOW MANY SAMPLES, AND WHY IT IS NOT A FREE PARAMETER ─────────────────────
// The integrand has a genuine STEP in it wherever the barrier has an edge -- the rim of a
// hatch, the jamb of a door. Slide the disc across that edge and a sample crosses from wood
// into open air, and because the sum is over POWER and the open path can be 15 dB louder,
// one sample flipping moves the total by far more than its area share suggests.
//
// This is a QUADRATURE error, not a modelling one, and it shrinks in proportion to the number
// of samples: with 15 samples pasthatch measured 2.40 dB/block, every step landing exactly
// where the barrier midpoint crossed a voxel boundary. Nothing else about the model was wrong
// there -- the barrier midpoint, its thickness and the two legs were all perfectly smooth.
//
// It cannot be fixed by smoothing the material field instead: a ray running PARALLEL to a
// floor then accumulates loss from a floor it never crosses, which is most of what walking an
// upper storey produces (measured: pasthatch-shut 0.25 -> 4.83 dB/block). Nor by a smooth
// radial window, which changes what the integral MEANS. More samples is the honest answer,
// and the area normalisation is what makes it safe -- adding samples refines this estimate
// rather than shifting it, so no other constant has to move.
//
// Four annuli, boundaries at 0.2/0.5/0.8/1.0 of R, shares from their areas.
const FRESNEL_RINGS = [[0.0, 1, 0.0625], [0.5, 6, 0.5], [1.0, 8, 0.4375]];
const FRESNEL_PTS = (() => {
  const out = [];
  for (const [rf, n, share] of FRESNEL_RINGS) {
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2 + (rf * 1.7);   // stagger rings so samples do not line up
      out.push({ rf, ca: Math.cos(a), sa: Math.sin(a), w: share / n });
    }
  }
  return out;
})();
const MIN_TRANSMIT_DB = 3;   // below this a ray is an opening, not a partition
// Calibration standing in for the energy balance an exponential distance law does not provide:
// SLOPE dB/block is not inverse-square, so 4*P_in/R cannot be evaluated in absolute units. This
// offset sets where the diffuse field sits relative to the direct component, and it is the one
// honestly empirical constant in the model.
// 7, not 16. The old value bridged an exponential direct path to a 4P/R diffuse term that
// assumes inverse-square -- 17 dB apart at 12 blocks. With both on the same law the gap is
// gone and what remains is a real calibration: 7 is where all nine spec scenarios pass and
// passivity holds at 0.0-0.1%.
const DIFFUSE_CAL = process.env.CMM_DCAL === undefined ? 7 : +process.env.CMM_DCAL;  // see notes: a fudge over the distance-law mismatch
// ── A SPACE MUST BE BIG ENOUGH TO HAVE A REVERBERANT FIELD AT ALL ─────────────
// 4P/R assumes a diffuse field, which needs a space many wavelengths across so sound can bounce
// many times before being absorbed. The segmentation happily produces ONE-VOXEL regions, and the
// formula applied to those is not just inaccurate, it is explosive: surf 6, R 1.4, so the 4/R term
// becomes +4.7 dB and with the calibration on top +20.7 dB of gain for a space one block wide.
// Measured on the real bunker capture, the diffuse field came out a median of 96.6 dB ABOVE the
// direct sound, which is not a reverberant room but a broken calculation.
//
// The physical test is the mean free path, 4V/S: the average distance sound travels between
// bounces. When that is not large compared to a wavelength there is no diffuse field, only a small
// resonant cavity, and the formula does not apply. Ramped rather than switched so a room does not
// acquire reverberation the instant it crosses a threshold.
//
// This also explains why no gate caught it. The diffuse field is the term that FLATTENS gradients,
// so an inflated one improves every smoothness metric here while wrecking the sound -- the
// acceptance criteria were rewarding a degenerate answer.
const WAVELEN_BLOCKS = 1.7;         // ~200 Hz, the band that carries through structure
const MFP_MIN = 0.5 * WAVELEN_BLOCKS;   // below this, no diffuse field of its own at all
// ── AND A REVERBERANT FIELD IS A PROPERTY OF AN ENCLOSURE ─────────────────────
// The worst case measured on the bunker was a listener buried under 439 dB of rock hearing the
// field at -13 dB while no path delivered anything at all. The cause was not the level: it was that
// the segmentation puts 777,541 voxels -- 97.6% of all open space in that capture, the outdoors and
// the terrain and pockets deep inside stone -- into ONE region, and a uniform field is then applied
// across all of it. "Uniform" is a statement about a closed space where sound bounces many times
// before dying. It is meaningless across the open world.
//
// Sky exposure separates the two cleanly and with no overlap at all: every enclosed room in every
// capture measures 0.0% of its boundary open to the world, while the outdoor regions measure
// 43-57%. Where the boundary is sky, energy leaves instead of reverberating.
// A boundary this much OPENING still reverberates; past OPEN_SHARE_NONE it does not. Same
// physics as the sky pair below -- a hole is a hole whether the far side is another room or the
// weather -- and ramped for the same reason: opening a door must not delete a room's echo.
const OPEN_SHARE_FULL = process.env.CMM_OSF === undefined ? 0.10 : +process.env.CMM_OSF;
const OPEN_SHARE_NONE = process.env.CMM_OSN === undefined ? 0.40 : +process.env.CMM_OSN;
const SKY_FULL = 0.05;   // below this share of open boundary, a full reverberant field
const SKY_NONE = 0.25;   // above it, none at all
                         // indoors, which is what the critical-distance calculation predicts (MODEL.md term 4)
const WALL_AREA_SCALE = 1.0; // shared-wall area is in voxel-faces; 1.0 keeps it in those units
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

// skipFrom / skipTo default TRUE, which is right for a source-to-listener ray: the speaker sits
// inside a solid block of its own and must not be charged for it, and the listener's own voxel is
// not a barrier between them. They must be FALSE when an endpoint is a barrier patch, whose whole
// purpose is to sit inside material -- with both legs skipping it, the barrier vanished entirely
// and every patch read 0.0 dB through a metre of concrete. Counting the patch voxel on both legs
// is not a double count: one leg covers entry-to-patch, the other patch-to-exit, and together they
// sum to exactly one crossing.
function segDbAt(D, fx, fy, fz, tx, ty, tz, skipFrom = true, skipTo = true) {
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
  // ── A ONE-BLOCK FLOOR IS NOT A ONE-BLOCK WALL (author, rule D) ──────────────
  // A voxel does double duty. Used vertically it stands for a whole floor assembly -- deck, joists,
  // ceiling below -- which is far deeper and heavier than the wall a voxel stands for horizontally.
  // So the same material must cost MORE crossed vertically. The anisotropy is a property of what a
  // voxel REPRESENTS, not of physics, which is why no amount of material tuning would find it.
  //
  // Scaled by how vertical the ray is, so it is continuous in direction rather than switching on an
  // axis test: a horizontal ray pays 1x, a vertical one VERT_MULT. Magnitude from the mass law --
  // a floor assembly is roughly 3x a wall's mass, and 20*log10(3) is about 9.5 dB, which on wood's
  // 6 dB is a factor near 2.6.
  const vert = 1 + (VERT_MULT - 1) * Math.abs(dy);
  let db = 0, tPrev = 0;
  for (let g = 0; g < 1024; g++) {
    const tNext = Math.min(tmx, tmy, tmz, dist);
    const seg = tNext - tPrev;                    // ray length inside the CURRENT voxel
    const atFrom = x === x0 && y === y0 && z === z0, atTo = x === x1 && y === y1 && z === z1;
    if (seg > 1e-6 && y >= 0 && y < 256 && !(skipFrom && atFrom) && !(skipTo && atTo)) {
      // Nearest-voxel on purpose. Interpolating the material field here was tried and is worse:
      // it smears a surface into the air beside it, so a ray running PARALLEL to a floor -- which
      // is most of what a listener walking an upper storey produces -- accumulates loss from a
      // floor it never crosses. pasthatch-shut went 0.25 -> 4.83 dB/block on that alone. The
      // aperture-edge step this was meant to fix belongs to the disc's SAMPLING, and is fixed
      // there (see FRESNEL_RINGS).
      const f = D.lossAt(x, y, z);
      if (f > 1e-4) db += Math.min(DB_VOXEL_MAX * vert, DB_MAT * Math.pow(f, MAT_POW) * vert) * seg;
    }
    if (tNext >= dist - 1e-9) break;
    tPrev = tNext;
    if (tmx <= tmy && tmx <= tmz) { x += sX; tmx += tDx; }
    else if (tmy <= tmz) { y += sY; tmy += tDy; }
    else { z += sZ; tmz += tDz; }
  }
  return db;
}

// Where along a ray the barrier actually IS: the midpoint of the span over which material
// exists. Needed because the transmission integral belongs at the barrier, not at the
// listener -- a patch of barrier radiates from its own position (see reference spec G).
// Returns null when the line is clear.
// ── WHERE THE TRANSMITTING SURFACE IS ──────────────────────────────────────────
// Returns the midpoint of the material run NEAREST THE LISTENER, and that run's thickness.
//
// This used to span from the FIRST material along the ray to the LAST, and take the middle of
// that. A speaker IS a solid block, so the span began inside the speaker's own block and ended
// at the far wall -- putting the "barrier" midpoint in OPEN AIR partway along the route. The
// disc built around it then sampled a sphere of air, and patches at radius 3 landed on the far
// side of a one-voxel floor with a clear path to the listener: the dominant patch paid
// 0.0 + 0.0 dB through a solid floor and carried 55% of the power. A leak, not a mis-tuning,
// and it is why walking over a shut hatch spiked 11.7 dB/block.
//
// The right definition comes straight from the governing principle: the surface that radiates
// into the listener's space is the one bounding that space -- the run nearest the LISTENER. That
// is also automatically immune to the source sitting inside a block, and it does the right thing
// with two walls in a row (the inner face is what radiates at you; the outer wall's loss is still
// paid along each leg).
function barrierMid(D, fx, fy, fz, tx, ty, tz) {
  let dx = tx - fx, dy = ty - fy, dz = tz - fz;
  const dist = Math.hypot(dx, dy, dz);
  if (dist < 0.01) return null;
  dx /= dist; dy /= dist; dz /= dist;
  let x = Math.floor(fx), y = Math.floor(fy), z = Math.floor(fz);
  const sX = dx > 0 ? 1 : dx < 0 ? -1 : 0, sY = dy > 0 ? 1 : dy < 0 ? -1 : 0, sZ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
  const tM = (o, d, c, s) => Math.abs(d) < 1e-6 ? Infinity : ((s > 0 ? c + 1 : c) - o) / d;
  const tD = d => Math.abs(d) < 1e-6 ? Infinity : Math.abs(1 / d);
  let tmx = tM(fx, dx, x, sX), tmy = tM(fy, dy, y, sY), tmz = tM(fz, dz, z, sZ);
  const tDx = tD(dx), tDy = tD(dy), tDz = tD(dz);
  // Track material RUNS. A run breaks on any gap of air, so the speaker's own block and a wall
  // beyond it are two runs rather than one long smear.
  // ── A SPEAKER'S OWN BLOCK IS NOT A BARRIER BETWEEN IT AND YOU ────────────────
  // A speaker sits INSIDE a solid block, so the very first voxel of this walk is always
  // material and always opens a run. Standing one block away with clear air between, that run is
  // the ONLY one, so it becomes "the barrier" -- the disc then runs over the speaker's own casing
  // and whatever it is mounted on, and reports occlusion where there is none. Measured: -1.19 dB
  // at one block, -0.62 at 1.5, crossing zero around two. Since anything positive clamps to no
  // attenuation, walking IN from three blocks to one ADDED 1.2 dB of attenuation, heard as "when
  // standing very close it still gets a hint quieter than when standing a few blocks away".
  //
  // segDbAt already refuses to charge the source's own voxel on a source-to-listener ray
  // (skipFrom defaults true, for exactly this reason). Barrier-FINDING has to agree with it, or
  // the two disagree about whether a barrier exists at all.
  const sVx = Math.floor(fx), sVy = Math.floor(fy), sVz = Math.floor(fz);
  let runA = -1, runB = -1, lastA = -1, lastB = -1, tPrev = 0;
  for (let g = 0; g < 1024; g++) {
    const tNext = Math.min(tmx, tmy, tmz, dist);
    if (tNext - tPrev > 1e-6 && y >= 0 && y < 256) {
      const isSrcVox = x === sVx && y === sVy && z === sVz;
      if (!isSrcVox && D.lossAt(x, y, z) > 0.05) {
        if (runA < 0) runA = tPrev;                    // a run opens
        runB = tNext;
      } else if (runA >= 0) {
        lastA = runA; lastB = runB; runA = -1;         // a run closes; remember it
      }
    }
    if (tNext >= dist - 1e-9) break;
    tPrev = tNext;
    if (tmx <= tmy && tmx <= tmz) { x += sX; tmx += tDx; }
    else if (tmy <= tmz) { y += sY; tmy += tDy; }
    else { z += sZ; tmz += tDz; }
  }
  if (runA >= 0) { lastA = runA; lastB = runB; }        // still open at the listener
  if (lastA < 0) return null;
  const t = 0.5 * (lastA + lastB);
  return { x: fx + dx * t, y: fy + dy * t, z: fz + dz * t, thick: lastB - lastA };
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
// OPENNESS IS A FRACTION, not a flag. A boolean test made the graph's topology binary in a
// quantity that is continuous: an 80%-open wood door still carries loss 0.1, over any sane
// threshold, so the air path did not exist AT ALL until the leaf was fully open and then
// snapped in -- a 5.5 dB jump at the end of every swing, and the one thing scenario #6 exists
// to forbid. Principle B says aperture is continuous; this is where that has to be expressed.
//
// So each voxel gets a 0..1 openness, and APERTURE AREA IS THE INTEGRAL OF IT. A door at 80%
// open contributes 0.8 of a voxel's worth of opening; a nearly-shut one contributes a sliver,
// which constriction then charges heavily -- correct, and continuous, rather than absent.
function makeOpenTest(D) {
  const open = new Float32Array(D.n);
  for (let i = 0; i < D.n; i++) {
    if ((D.flags[i] & AIR_FLAG) !== 0) { open[i] = 1; continue; }
    const l = D.loss[i];
    open[i] = l >= OPEN_FULL ? 0 : (l <= OPEN_LOSS ? 1 : 1 - (l - OPEN_LOSS) / (OPEN_FULL - OPEN_LOSS));
  }
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
      if (open[seed] <= OPEN_EPS || nodeOf[seed] !== -1) continue;
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
        const of = open[cur];
        count += of; sxs += vx * of; sys += vy * of; szs += vz * of;
        const ax = vx - x0, ay = vy - y0, az = vz - z0;
        slX[ax] += of; slY[ay] += of; slZ[az] += of;
        pX[0][ax] += vx * of; pY[0][ax] += vy * of; pZ[0][ax] += vz * of;
        pX[1][ay] += vx * of; pY[1][ay] += vy * of; pZ[1][ay] += vz * of;
        pX[2][az] += vx * of; pY[2][az] += vy * of; pZ[2][az] += vz * of;
        const nb = [[vx+1,vy,vz],[vx-1,vy,vz],[vx,vy+1,vz],[vx,vy-1,vz],[vx,vy,vz+1],[vx,vy,vz-1]];
        for (const [nx, ny, nz] of nb) {
          if (nx < x0 || nx >= x1 || ny < y0 || ny >= y1 || nz < z0 || nz >= z1) continue;
          const nid = li(nx, ny, nz);
          if (open[nid] <= OPEN_EPS || nodeOf[nid] !== -1) continue;
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
        for (let q = 0; q < cell; q++) if (sl[q] > 1e-6) { if (lo < 0) lo = q; hi = q; }
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
        if (wi >= 0 && sl[wi] > 1e-6) {
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
  const addFace = (na, nb, wx, wy, wz, aw) => {
    const a = Math.min(na, nb), b = Math.max(na, nb);
    const key = a * 1e7 + b;
    let p = portalMap.get(key);
    if (!p) { p = { a, b, area: 0, sx: 0, sy: 0, sz: 0,
                    x0: Infinity, y0: Infinity, z0: Infinity, x1: -Infinity, y1: -Infinity, z1: -Infinity }; portalMap.set(key, p); }
    p.area += aw; p.sx += wx * aw; p.sy += wy * aw; p.sz += wz * aw;
    // extent, so a path can be pulled taut THROUGH the opening instead of via its centre
    if (wx < p.x0) p.x0 = wx; if (wx > p.x1) p.x1 = wx;
    if (wy < p.y0) p.y0 = wy; if (wy > p.y1) p.y1 = wy;
    if (wz < p.z0) p.z0 = wz; if (wz > p.z1) p.z1 = wz;
  };
  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = li(x, y, z);
    if (open[i] <= OPEN_EPS) continue;
    const a = nodeOf[i];
    // A face passes the SMALLER of the two voxels' openness -- a chain is no more open than
    // its tightest link.
    if (x + 1 < D.sx) { const j = li(x+1,y,z); if (open[j] > OPEN_EPS && nodeOf[j] !== a) addFace(a, nodeOf[j], D.x0+x+1, D.y0+y+0.5, D.z0+z+0.5, Math.min(open[i], open[j])); }
    if (y + 1 < D.sy) { const j = li(x,y+1,z); if (open[j] > OPEN_EPS && nodeOf[j] !== a) addFace(a, nodeOf[j], D.x0+x+0.5, D.y0+y+1, D.z0+z+0.5, Math.min(open[i], open[j])); }
    if (z + 1 < D.sz) { const j = li(x,y,z+1); if (open[j] > OPEN_EPS && nodeOf[j] !== a) addFace(a, nodeOf[j], D.x0+x+0.5, D.y0+y+0.5, D.z0+z+1, Math.min(open[i], open[j])); }
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

  // Openness at a FLOAT position, trilinear over voxel centres. Needed because the transmission
  // disc has to know how much barrier is actually at each patch, and a nearest-voxel lookup would
  // make that a step function of the listener's position -- the exact class of defect this whole
  // model exists to remove. Out of bounds reads as open, so an unknown region never invents a wall.
  const openAtF = (x, y, z) => {
    const gx = x - 0.5, gy = y - 0.5, gz = z - 0.5;
    const bx = Math.floor(gx), by = Math.floor(gy), bz = Math.floor(gz);
    const tx = gx - bx, ty = gy - by, tz = gz - bz;
    let acc = 0;
    for (let i = 0; i < 8; i++) {
      const ex = i & 1, ey = (i >> 1) & 1, ez = (i >> 2) & 1;
      const w = (ex ? tx : 1 - tx) * (ey ? ty : 1 - ty) * (ez ? tz : 1 - tz);
      if (w <= 0) continue;
      const px = bx + ex, py = by + ey, pz = bz + ez;
      acc += w * (D.inB(px, py, pz) ? G.open[D.idx(px, py, pz)] : 1);
    }
    return acc;
  };

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
  // Routed legs need, per aperture, the graph distance from its node to every node and which portal
  // the route entered by. The node graph is tiny -- 33 nodes / 60 portals on the bunker, 55 / 179 on
  // the cave -- so a Dijkstra per aperture is trivial, and it is computed ONCE per emitter here rather
  // than per listener. A linear-scan Dijkstra beats a heap at this size.
  const ROUTED_LEGS = process.env.CMM_ROUTEDLEGS === "1";
  const apRoute = [];
  if (ROUTED_LEGS) {
    // OVER PORTALS, NOT NODES. Routing node-to-node via centroids inflates every leg: in a
    // 52,538-voxel node the centroid is tens of blocks from both portals, so the route accumulates
    // phantom distance and SLOPE*leg makes the arrival far too quiet -- measured as bunker orphan
    // 1.4% -> 15.5%. MODEL.md already says the solve belongs over PORTALS, which is what solveAir
    // does; portal-to-portal within a node is a real distance and needs no centroid.
    const nP = G.portals.length;
    for (const A of G.apertures) {
      const dist = new Float64Array(nP).fill(Infinity);
      const done = new Uint8Array(nP);
      const NA = A.nid >= 0 && A.nid < G.nodes.length ? G.nodes[A.nid] : null;
      if (NA) for (const pid of NA.edges) {
        const P = G.portals[pid];
        dist[pid] = Math.hypot(P.px - A.px, P.py - A.py, P.pz - A.pz);
      }
      for (;;) {
        let u = -1, best = Infinity;
        for (let i = 0; i < nP; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
        if (u < 0) break;
        done[u] = 1;
        const PU = G.portals[u];
        for (const side of [PU.a, PU.b]) {
          if (side < 0 || side >= G.nodes.length) continue;
          for (const pid of G.nodes[side].edges) {
            if (done[pid] || pid === u) continue;
            const PV = G.portals[pid];
            const w = Math.hypot(PV.px - PU.px, PV.py - PU.py, PV.pz - PU.pz);
            if (dist[u] + w < dist[pid]) dist[pid] = dist[u] + w;
          }
        }
      }
      apRoute.push({ dist });
    }
  }

  // ── SOUND ARRIVES AT AN OPENING BY EVERY ROUTE, NOT BY THE BEST ONE ─────────────
  // This took the MINIMUM cost over the portals feeding the aperture's node, which is an
  // argmin: perfectly smooth until the winner changes, and then a step the size of the gap
  // between the two routes. Under the shipped distance law that gap is small -- a route ten
  // blocks longer costs 3.5 dB -- so the steps were survivable. Under a physical law the same
  // switch costs 20*log10 of the length ratio, and every one of them becomes audible lurching.
  // Measured: worst occlusion gradient went 6.37 -> 11.65 dB/block on the store with the law
  // changed and nothing else.
  //
  // The model already knows better than this. Its own rule for the arrival set is "never a max,
  // because an energy sum is dominated by its largest term without any winner-takes-all step".
  // The same argument applies here and was simply not applied: the routes into a node are
  // alternative physical paths, they all carry sound, and their powers add. The sum tracks the
  // minimum within 10*log10(N) and has no step in it anywhere -- a route fading in contributes
  // nothing until it is comparable, instead of seizing the whole arrival the moment it wins.
  //
  // Length is blended by the same power weights, so the reported route length -- which the delay
  // and the distance law both use -- moves continuously too, rather than teleporting with the
  // winner.
  // DEFAULT OFF, and the reason is worth keeping. Summing the routes is the more correct
  // statement -- sound does arrive by all of them -- but measured against the worst occlusion
  // gradient it changed nothing (store 11.65 -> 11.56 dB/block), which is how the theory that
  // route SWITCHING caused the roughness was killed. It is not worth a native port and a
  // parity gap for a change no measurement can see; JS and native agreeing is the discipline
  // that has caught nearly every real bug here. CMM_BLEND=1 to try it again.
  const BLEND = process.env.CMM_BLEND === "1";
  const cands = [{ cost: minSeed, px: sx5, py: sy5, pz: sz5, routeLen: 0, isSrc: true }];
  for (const A of G.apertures) {
    let bestDb = Infinity, bestLen = 0;
    let pSum = 0, pLen = 0;
    const addRoute = (cDb, len) => {
      if (!Number.isFinite(cDb)) return;
      const p = dbToPow(-cDb);
      pSum += p; pLen += p * len;
      if (cDb < bestDb) { bestDb = cDb; bestLen = len; }
    };
    if (seeded.has(A.nid)) {
      const h = Math.hypot(A.px - sx5, A.py - sy5, A.pz - sz5);
      addRoute(seeded.get(A.nid) + SLOPE * h, h);
    }
    for (const pid of G.nodes[A.nid].edges) {
      const pc = cost[pid];
      if (!pc) continue;
      const P = G.portals[pid];
      const hop = Math.hypot(A.px - P.px, A.py - P.py, A.pz - P.pz);
      addRoute(pc.add - constrictDb(pc.bneck) + SLOPE * hop, pc.len + hop);
    }
    if (BLEND && pSum > 0) { bestDb = -powToDb(pSum); bestLen = pLen / pSum; }
    if (!Number.isFinite(bestDb)) continue;
    cands.push({ cost: bestDb - constrictDb(A.throat), px: A.px, py: A.py, pz: A.pz,
                 routeLen: bestLen, throat: A.throat, area: A.area, rad: apRad(A),
                 srcDist: Math.hypot(A.px - sx5, A.py - sy5, A.pz - sz5) });
  }

  // ── AN OPENING IS NOT A SOURCE: THE OPENINGS SHARE ONE SPEAKER ───────────────
  // Every aperture was given its OWN near-full copy of the speaker and the copies were then
  // energy-summed, so a room with many openings delivered several speakers' worth of power. It
  // is not a subtle error: measured over the store capture, 99.2% of standable positions came
  // out ABOVE 0 dB -- the listener receiving more energy than the speaker emits. The bridge
  // cannot express that as an attenuation, so it clamped to fully unoccluded, and occlusion was
  // switched off across essentially a whole POI. That is what "on the other side of a wall it
  // sounds like there's barely anything between me and that wall" was, and why no material
  // constant changed it: those points had already been clamped.
  //
  // No gate caught it because the passivity check tests one TERM against its own room. Nothing
  // compared the SUM at the listener against the source. gain.js is that measurement.
  //
  // A speaker radiates into 4*pi and each opening intercepts the solid angle it subtends,
  // area/dist^2. Those patches are disjoint, so the shares can only add up to the whole -- that
  // is the passivity statement, and it is geometry rather than a clamp.
  //
  // NORMALISED AMONG THE APERTURES rather than against 4*pi, deliberately. The absolute form
  // (share = omega/4pi) is the correct one, but this model charges spreading through an
  // exponential SLOPE rather than inverse-square, so the absolute form double-charges it and
  // sinks a plain open doorway by ~17 dB. The relative form fixes the double COUNT -- which is
  // the defect -- while leaving the absolute scale to the distance law, and it is exactly
  // neutral when there is one aperture, so every single-opening spec scenario is untouched.
  // When the distance law is settled, this becomes omega/4pi and needs no other change.
  // CMM_NOSHARE=1 disables it, so the A/B is measured with one instrument rather than by
  // reverting the code and accidentally reverting the measurement with it (which I did once).
  if (process.env.CMM_NOSHARE !== "1") {
    let omegaSum = 0;
    for (const c of cands) {
      if (c.isSrc) continue;
      c.omega = (c.area || 1) / Math.max(1, c.srcDist * c.srcDist);
      omegaSum += c.omega;
    }
    if (omegaSum > 0)
      for (const c of cands)
        if (!c.isSrc) c.cost -= 10 * Math.log10(c.omega / omegaSum);
  }

  // wx/wy/wz are BLOCK COORDS and may be fractional.
  // ── each region's DIFFUSE FIELD, solved once ─────────────────────────────────
  // A region's field is a property of the room and the source, not of the listener, so it is
  // computed once rather than per sample. Two passes, because the system is coupled:
  //
  //   pass 1  field from the source directly (if the source is in this room) and from power
  //           arriving at this room's OPENINGS
  //   pass 2  add power driven through SHARED WALLS by the neighbouring rooms' pass-1 fields
  //
  // Pass 2 is the term that was missing. A sealed room next to a loud one does build a weak
  // reverberant field, driven by its neighbour's field across the wall they share:
  //
  //     P_in(B, walls) = field(A) x tau(shared wall) x shared area
  //
  // Previously this was estimated from a ray from the source to the region's centroid, which put
  // ~7 dB of pure distance into a quantity that has no distance in it -- a shut interior door read
  // -15 dB where the author's words put it near -6. One Jacobi iteration is enough because wall
  // transmission is a large loss, so the coupling is weak and a second pass would barely move.
  // Solved per ROOM (the acoustic enclosure), never per routing segment. A room has ONE diffuse
  // field however the pathfinder subdivides it -- giving each corridor segment its own stepped the
  // level whenever a listener crossed a cut inside a single room (approach hit 11.3 dB/block).
  //   ROOM    = merged region: one enclosure, one field, one room constant
  //   SEGMENT = routing node, so a bend is not treated as line of sight
  const RM = G.rooms || null;
  const nRooms = RM ? RM.length : 0;
  const roomOf = (rid) => (rid >= 0 && G.nodes[rid] && G.nodes[rid].room !== undefined)
    ? G.nodes[rid].room : -1;
  const fieldOf = new Float64Array(Math.max(1, nRooms));
  if (RM) {
    // pass 1: the source, plus power arriving at this room's real OPENINGS
    const direct = new Float64Array(nRooms);
    for (let rid = 0; rid < G.nodes.length; rid++) {
      const rm = roomOf(rid);
      if (rm < 0) continue;
      let pIn = 0;
      if (seeded.has(rid)) pIn += dbToPow(-seeded.get(rid));
      for (const pid of G.nodes[rid].edges) {
        const pc = cost[pid];
        if (!pc) continue;
        const P = G.portals[pid];
        const other = P.a === rid ? P.b : P.a;
        if (roomOf(other) === rm) continue;      // a routing cut inside the room is not an inlet
        pIn += dbToPow(-(pc.add - constrictDb(pc.bneck)));
      }
      if (pIn > 0) direct[rm] += pIn;
    }
    // ── ENERGY CONSERVATION ──────────────────────────────────────────────────
    // The loop above walks every NODE of a room and, per node, sums over ALL its portals -- so the
    // same sound is counted once per opening per node. A room of ten nodes with four portals each
    // collects forty helpings of the source. Working back from the bunker's field: room 3 implied
    // +11.6 dB of power entering it, i.e. more power than the speaker emits, which is how its
    // reverberation ended up 54 dB louder than the air route that feeds it and audible through
    // 877 dB of rock.
    //
    // A room cannot receive more power than the source radiates. Levels here are relative to the
    // source, so that bound is simply unity. Crude next to summing incident power per opening
    // (intensity x area / 4, which is what the portal areas are for) but this is a hard physical
    // ceiling rather than a fitted constant, and it cannot be wrong in the direction that matters.
    for (let rm = 0; rm < nRooms; rm++) if (direct[rm] > 1) direct[rm] = 1;
    const dbgPin = process.env.CMM_ROOMS === "1" ? Array.from(direct) : null;
    // ── AN OPENING IS AN ABSORBER ────────────────────────────────────────────
    // R counted material surfaces only, so a room's openings were free. They are not: sound leaving
    // through a doorway does not come back, which makes an opening the most absorbing surface a room
    // has. Omitting it let small rooms with large openings reach R = 1 against a 9-block opening, and
    // the room-to-room coupling gain S/R came out at 9.4 -- a room amplifying what enters it ninefold,
    // which is both unphysical and divergent if iterated.
    //
    // Adding the open area to the absorption fixes the level AND makes coupling passive by
    // construction, since a shared opening's own area is part of the receiving room's absorption and
    // so S_a/A_Y can never exceed 1. This is the standard treatment; its absence was the reason the
    // coupling could not be solved at all.
    const openArea = new Float64Array(nRooms);
    for (const A of G.apertures) {
      const a = (A.area === undefined ? 1 : A.area) * (A.throat === undefined ? 1 : A.throat);
      if (A.roomA >= 0 && A.roomA < nRooms) openArea[A.roomA] += a;
      if (A.roomB >= 0 && A.roomB < nRooms) openArea[A.roomB] += a;
    }
    const absorbOf = rm => Math.max(1, RM[rm].roomR) + openArea[rm];

    // How much of a reverberant field each room can sustain at all. Kept so it can be applied to
    // the FINAL field: gating only `direct` left the wall-coupling pass free to give the outdoors a
    // field anyway, and that is precisely where it does the most damage, because the outdoor region
    // is one 777,541-voxel blob spanning sky and solid rock alike. Measured as a dead flat -40.0 dB
    // at every offending voxel: room 0's -56 dB of wall trickle plus the +16 dB calibration.
    const canReverb = new Float64Array(nRooms);
    for (let rm = 0; rm < nRooms; rm++) {
      const R = RM[rm];
      // mean free path 4V/S, in blocks; V is the voxel count, S the counted face area
      const mfp = 4 * Math.max(1, R.n) / Math.max(1, R.surf);
      // ── AN OPENING IS SKY, ACOUSTICALLY ─────────────────────────────────────────
      // The sky test above says it exactly right: "where the boundary is sky, energy leaves
      // instead of reverberating". A DOORWAY is the same statement -- energy crossing it is gone
      // from this room -- and the test was never applied to it. Openings were counted in the
      // absorption (correctly), but a space whose boundary is mostly opening was still granted a
      // FULL reverberant field, and there is no such thing: sound has to bounce many times before
      // being absorbed for a diffuse field to exist, and if most of the boundary is a hole it
      // leaves on the first pass.
      //
      // Measured on the bunker, rooms that were being given a full field:
      //     n=5789  surf=976  openArea=2808   -> boundary 74% opening, field -21.9 dB
      //     n=1603  surf=152  openArea=2309   -> boundary 94% opening, field -20.8 dB
      //     n=87    surf=20   openArea=175    -> boundary 90% opening, field -22.3 dB
      // Those passages are what produced the flat reverb floor a speaker in an OPEN FIELD sat
      // under: their fields radiated back out through the very openings that should have
      // prevented them existing. Past eight blocks that floor became the level, the level stopped
      // falling with distance, and occlusion switched off across the whole POI.
      //
      // Ramped over the same kind of range as the sky test, so a room does not lose its
      // reverberation the moment a door opens -- it loses it in proportion to how much of its
      // boundary is now hole.
      const bnd = Math.max(1, R.surf) + openArea[rm];
      const openShare = openArea[rm] / bnd;
      canReverb[rm] = sstep(MFP_MIN, WAVELEN_BLOCKS, mfp)
                    * (1 - sstep(SKY_FULL, SKY_NONE, R.skyFrac === undefined ? 0 : R.skyFrac))
                    * (1 - sstep(OPEN_SHARE_FULL, OPEN_SHARE_NONE, openShare));
      direct[rm] = direct[rm] > 0 ? canReverb[rm] * 4 * direct[rm] / absorbOf(rm) : 0;
    }
    // pass 2: power driven through SHARED WALLS by the neighbouring rooms. Power INCIDENT on a
    // surface of area S in a diffuse field is P*S/R, not field*S -- the standard relation carries a
    // factor of four cancelling the one in 4P/R, and getting that wrong made the receiving room
    // LOUDER than the source room. One Jacobi iteration suffices: wall transmission is a large
    // loss, so the coupling is weak.
    for (let rm = 0; rm < nRooms; rm++) {
      const node = RM[rm];
      let pWall = 0;
      for (const w of (node.walls || [])) {
        const src = direct[w.other];
        if (src <= 0) continue;
        const other = RM[w.other];
        const pOther = src * absorbOf(w.other) / 4;
        pWall += pOther * w.area / absorbOf(w.other) * w.tau;
      }
      // The calibration belongs to the FIELD's absolute scale, applied once per room here, not to
      // every contribution at the listener. Applied there it multiplied the coupled terms too, which
      // makes the coupling an amplifier -- a passivity test flags it on 100% of voxels, correctly.
      // Numerically identical either way; structurally this is the honest place for it, and it makes
      // "coupling may only attenuate" a statement the model can actually satisfy.
      fieldOf[rm] = canReverb[rm] * (direct[rm] + (pWall > 0 ? 4 * pWall / absorbOf(rm) : 0))
                  * dbToPow(DIFFUSE_CAL);
    }

    // ── PASS 3: APERTURE COUPLING, SOLVED TO CONVERGENCE ─────────────────────────────
    // This is the fix the comment further down has been asking for. Rooms exchange energy through
    // their OPENINGS as well as through their walls, and that exchange belongs here -- in the room
    // fields, where it is a small linear system over 13-55 rooms -- not at the listener, where it
    // was 47 separate ray-cast terms per position.
    //
    // Why it matters beyond tidiness: a ray bundle from the listener to an opening 50 blocks away
    // goes from blocked to clear in ONE block of movement, so the sum of 47 of them lurches. All
    // eight speakers in the store jumped 3-5.6 dB together across a single step, with the
    // reverberant term moving a consistent +5.5 dB -- the signature of a shared, listener-cast
    // term rather than anything about the speakers. Room ambience does not change 5 dB when you
    // take one step, and it cannot be made not to while it is computed that way.
    //
    // Solved here it is a property of the ROOMS, so it is uniform inside each one and cannot
    // lurch. Power leaving room j through an opening of area a is (field_j / 4) * a; it arrives in
    // room i and joins i's field as 4*P/absorb, the same relation the wall pass uses. Jacobi to
    // convergence rather than the wall pass's single iteration, because an OPENING is not a large
    // loss -- energy can cross it repeatedly, so one pass badly under-counts a suite of connected
    // rooms.
    {
      const nbr = Array.from({ length: nRooms }, () => []);
      for (const A of G.apertures) {
        const a = Math.max(1e-6, (A.area || 0) * Math.min(1, A.throat || 1));
        if (A.roomA === undefined || A.roomB === undefined) continue;
        if (A.roomA < 0 || A.roomB < 0 || A.roomA === A.roomB) continue;
        if (A.roomA >= nRooms || A.roomB >= nRooms) continue;
        nbr[A.roomA].push({ other: A.roomB, area: a });
        nbr[A.roomB].push({ other: A.roomA, area: a });
      }
      // Seeded from the direct+wall field above; each sweep adds one more room-crossing of
      // aperture-borne energy. Converges geometrically because every crossing pays the receiving
      // room's absorption, which INCLUDES its openings -- so the loop is a contraction and cannot
      // run away. 24 sweeps is far past convergence at these room counts and costs nothing.
      const cur = Float64Array.from(fieldOf);
      const nxt = new Float64Array(nRooms);
      for (let it = 0; it < 24; it++) {
        for (let rm = 0; rm < nRooms; rm++) {
          let pAp = 0;
          for (const e of nbr[rm]) {
            const fj = cur[e.other];
            if (fj <= 0) continue;
            pAp += (fj / 4) * e.area;          // power leaving j through this opening
          }
          const add = pAp > 0 ? canReverb[rm] * 4 * pAp / absorbOf(rm) : 0;
          nxt[rm] = fieldOf[rm] + add;
        }
        let moved = 0;
        for (let rm = 0; rm < nRooms; rm++) {
          moved = Math.max(moved, Math.abs(nxt[rm] - cur[rm]));
          cur[rm] = nxt[rm];
        }
        if (moved < 1e-12) break;
      }
      for (let rm = 0; rm < nRooms; rm++) fieldOf[rm] = cur[rm];
    }
    if (process.env.CMM_ROOMS === "1") {
      // Per-room terms, to diff against the native port term by term. Sorted lists cannot
      // say WHICH room diverged, and that is the only question once totals are close.
      console.error("ROOM n surf sky openArea absorb canrev pin direct fieldDb");
      for (let rm = 0; rm < nRooms; rm++) {
        const R = RM[rm];
        const f = fieldOf[rm] > 1e-12 ? powToDb(fieldOf[rm]) : -999;
        console.error(`ROOM ${R.n} ${R.surf} ${Math.round(R.skyFrac * R.surf)} `
          + `${openArea[rm].toFixed(3)} ${absorbOf(rm).toFixed(3)} ${canReverb[rm].toFixed(4)} `
          + `${(dbgPin ? dbgPin[rm] : 0).toPrecision(6)} ${direct[rm].toPrecision(6)} ${f.toFixed(2)}`);
      }
    }
    if (process.env.CMM_DIFFDBG === "1") {
      const dbg = [];
      for (let rm = 0; rm < nRooms; rm++) {
        const R = RM[rm];
        const mfp = 4 * Math.max(1, R.n) / Math.max(1, R.surf);
        dbg.push({ rm, n: R.n, surf: R.surf, R: +R.roomR.toFixed(1), mfp: +mfp.toFixed(2),
                   valid: +sstep(MFP_MIN, WAVELEN_BLOCKS, mfp).toFixed(2),
                   directDb: direct[rm] > 0 ? +powToDb(direct[rm]).toFixed(1) : -999,
                   fieldDb: fieldOf[rm] > 0 ? +powToDb(fieldOf[rm]).toFixed(1) : -999,
                   walls: (R.walls || []).length });
      }
      dbg.sort((a, b) => b.fieldDb - a.fieldDb);
      console.error("room  vox   surf       R   mfp valid   directDb  fieldDb  walls");
      for (const d of dbg.slice(0, 8))
        console.error(String(d.rm).padStart(4) + String(d.n).padStart(6) + String(d.surf).padStart(7)
          + String(d.R).padStart(9) + String(d.mfp).padStart(6) + String(d.valid).padStart(6)
          + String(d.directDb).padStart(11) + String(d.fieldDb).padStart(9) + String(d.walls).padStart(7));
    }
  }

  // Exposed so the acceptance gates can test CONSERVATION rather than guess at it: a coupling that
  // carries a room's reverberation into another space may attenuate it, never amplify it. Without the
  // room fields in hand, a gate can only compare the field against the dry path, which is the wrong
  // reference -- a room's field is accumulated energy and is legitimately louder than any single
  // direct arrival at its own opening.
  const fieldDbOf = () => Array.from(fieldOf, v => (v > 0 ? powToDb(v) : -999));

  const evalFn = function (wx, wy, wz) {
    const d = Math.hypot(wx - src.x, wy - src.y, wz - src.z);
    const lx = wx + 0.5, ly = wy + 0.5, lz = wz + 0.5;

    // The SOURCE candidate's last leg uses the transmission fan: a barrier is a distributed
    // radiator, so behind a vault door set in a wooden wall you hear the wall beside you.
    // Two orthonormal vectors perpendicular to the source-listener line, for the disc.
    let ux = lx - sx5, uy = ly - sy5, uz = lz - sz5;
    const ul = Math.hypot(ux, uy, uz) || 1;
    ux /= ul; uy /= ul; uz /= ul;
    let ax = 0, ay = 1, az = 0;
    if (Math.abs(uy) > 0.9) { ax = 1; ay = 0; az = 0; }
    let e1x = uy * az - uz * ay, e1y = uz * ax - ux * az, e1z = ux * ay - uy * ax;
    const e1l = Math.hypot(e1x, e1y, e1z) || 1;
    e1x /= e1l; e1y /= e1l; e1z /= e1l;
    const e2x = uy * e1z - uz * e1y, e2y = uz * e1x - ux * e1z, e2z = ux * e1y - uy * e1x;

    // THE DISC SITS AT THE BARRIER, not at the listener. Offsetting the LISTENER and asking
    // what each ray crosses answers "what is directly between us", which is the wrong question:
    // behind a steel column in a glass wall it charges you for the column, when what you
    // actually hear is the glass beside it radiating into your space. Sampling patches of the
    // barrier and letting each radiate from ITS OWN position gives the right answer -- the
    // glass is a little further away and no more blocked, so walking past barely changes the
    // level, which is what the author describes and what the physics says.
    const bm = barrierMid(D, sx5, sy5, sz5, lx, ly, lz);
    // ── SPLIT THE ZONE INTO ITS OPEN AND BLOCKED SHARES ──────────────────────────
    // The disc integrates over patches that are clear and patches that are solid, and sums them
    // into one number. That number is right, but the two halves are physically DIFFERENT SOUNDS
    // arriving from DIFFERENT PLACES: the clear share came straight from the speaker through air,
    // the blocked share was re-radiated by the barrier. Merging them forced the renderer to pick one
    // position and one filter for both, so as a sight line cleared, the image jumped from the
    // barrier to the speaker and its tone swung dull to bright -- heard by the author as one doorway
    // sounding like "several doors one after the other".
    //
    // Tracking the two shares separately costs one multiply per patch and gives the renderer a
    // CONTINUOUS crossfade, because patch openness is continuous. Fully clear, the barrier component
    // is silent; fully blocked, the direct component is. Nothing switches.
    let fanPow = 0, fanOpenPow = 0;
    if (!bm) {
      fanPow = dbToPow(-distDb(d));                     // clear line: free field, no barrier
      fanOpenPow = fanPow;                              // ... and all of it came through air
    } else {
      // The zone's radius depends on where along the path the barrier sits, so it is computed
      // per evaluation from the two leg lengths rather than being a constant.
      const fzR = FZ_GEO
        ? fresnelR(Math.hypot(bm.x - sx5, bm.y - sy5, bm.z - sz5),
                   Math.hypot(bm.x - lx, bm.y - ly, bm.z - lz))
        : FRESNEL_R_FIXED;
      for (const p of FRESNEL_PTS) {
        const ox = (e1x * p.ca + e2x * p.sa) * (p.rf * fzR);
        const oy = (e1y * p.ca + e2y * p.sa) * (p.rf * fzR);
        const oz = (e1z * p.ca + e2z * p.sa) * (p.rf * fzR);
        const qx = bm.x + ox, qy = bm.y + oy, qz = bm.z + oz;
        // EVERY patch counts, blocked or clear. Skipping the clear ones (on the reasoning that
        // air is not a transmitting surface) threw away the UNBLOCKED share of the zone, so the
        // instant a sight line clipped a corner the level fell off a cliff: measured on the real
        // store dump at -3 dB one block and -22 the next, 160 such samples, because a grazing ray
        // makes barrierMid non-null and every air patch was then discarded.
        //
        // Air is not a transmitter but it IS a passer, and partial obstruction of the first
        // Fresnel zone attenuating gradually is exactly what diffraction does. Weighting by
        // solidity was also a double count: each patch's own material is already integrated by
        // the two legs, half a voxel each. So the honest form is a plain area integral over the
        // zone -- fully blocked gives the wall's loss, fully clear gives free field, and partial
        // gives the smooth in-between with no branch anywhere.
        // (Tried also skipping the source's own block on leg B, on the theory that a speaker's
        // casing should not attenuate its own output. Worth -0.3 dB at half a block and nothing
        // beyond, so it is not the residual and not worth a parity gap. The residual at sub-block
        // range is the fixed disc radius sampling floor and walls three blocks off-axis; at any
        // reachable listening position -- an ear is 1-2 blocks from a block's centre -- occlusion
        // is now within 0.3 dB of zero in open air.)
        const m = segDbAt(D, sx5, sy5, sz5, qx, qy, qz, true, false)    // count the patch itself
                + segDbAt(D, qx, qy, qz, lx, ly, lz, false, true);
        const len = Math.hypot(qx - sx5, qy - sy5, qz - sz5) + Math.hypot(qx - lx, qy - ly, qz - lz);
        // THE DISC MEASURES TRANSMISSION. DISTANCE IS THE DIRECT PATH'S JOB.
        // Each patch was charged for its OWN path length, source->patch->listener, which is
        // longer than the direct line by the patch's offset. Under the shipped flat law that was
        // worth ~0.3 dB and did no harm. Under a physical law it is fatal: a speaker sits inside
        // its own block, so barrierMid always finds a "barrier" right at the source, the disc
        // then integrates over patches up to three blocks off-axis, and their paths cost 15 dB
        // against a free field of 0. Measured standing half a block from a speaker in open air
        // with nothing in the way: -12.6 dB of occlusion. Reported from the game as "it gets
        // muffled when I'm closer to it, especially while right on top of it".
        //
        // The path DIFFERENCE across a Fresnel zone is a phase difference -- it is what makes the
        // zones interfere -- and this sum is incoherent, over power, so that difference carries no
        // energy cost here. What the disc is measuring is what FRACTION of the wavefront gets
        // through the barrier. Charging the direct distance once, and letting each patch differ
        // only by the material it crosses, is that quantity: with no material anywhere the disc
        // sums to exactly free field, which is the answer it always should have given.
        const pw = p.w * dbToPow(-(distDb(d) + m));
        fanPow += pw;
        // "Came through air" is a property of the PATH, not of where the sample point happens to sit.
        // This asked openAtF(patch) -- is the point itself in air -- which is a different question and
        // the wrong one: a patch sitting in air on the FAR SIDE of a barrier still had to cross that
        // barrier to be reached. Through a shut wooden floor it reported 82% of the energy as having
        // come through air, so the renderer placed that 82% at the speaker and panned it at full
        // strength, and a slab two blocks underfoot localised about as sharply as a doorway across an
        // open room. The author heard it twice and was right twice.
        //
        // The honest test is how much material the two legs actually crossed. Under MIN_TRANSMIT_DB
        // the path is an opening; above it, a partition. That constant already exists for exactly
        // this distinction, and ramping over it keeps the split continuous.
        fanOpenPow += (1 - sstep(0, 2 * MIN_TRANSMIT_DB, m)) * pw;
        if (FAN_DBG !== null && Math.abs(lz - FAN_DBG) < 0.01)
          console.error(`    patch r=${(p.rf*fzR).toFixed(2)} at ${qx.toFixed(2)},${qy.toFixed(2)},${qz.toFixed(2)}`
            + `  m=${m.toFixed(2).padStart(7)}  len=${len.toFixed(2)}  w=${p.w.toFixed(4)}`
            + `  pw=${pw.toExponential(3)}`);
      }
      if (FAN_DBG !== null && Math.abs(lz - FAN_DBG) < 0.01)
        console.error(`  bm=${bm.x.toFixed(2)},${bm.y.toFixed(2)},${bm.z.toFixed(2)} t=${bm.thick.toFixed(3)}  fanPow=${fanPow.toExponential(4)}`);
      // If the disc found no barrier at all, the run it located is not a surface the listener is
      // behind -- fall back to the straight-line estimate rather than reporting silence.
      if (fanPow <= 0) { fanPow = dbToPow(-(distDb(d) + segDbAt(D, sx5, sy5, sz5, lx, ly, lz))); fanOpenPow = 0; }
    }
    const fanDb = fanPow > 0 ? -powToDb(fanPow) : 120;
    const centerMat = segDbAt(D, sx5, sy5, sz5, lx, ly, lz);
    // which region/node the listener stands in (floored: the position is a float now)
    const nd = D.inB(Math.floor(wx), Math.floor(wy), Math.floor(wz))
      ? G.nodeOf[D.idx(Math.floor(wx), Math.floor(wy), Math.floor(wz))] : -1;

    // ── ROUTED LEGS: TWO VARIANTS TRIED AND BOTH REJECTED, WITH NUMBERS ─────────
    // The straight leg from a global aperture to the listener is only valid where line of sight holds,
    // and through a void that bends it is nonsense -- 225 candidates dropped at 80-930 dB of legMat
    // while the diffuse field arrived at -18.9 dB through the same openings. The obvious fix is to
    // route the leg instead. Every route must cross a portal bounding the listener's OWN node, and
    // line of sight inside a node is guaranteed by construction, so the node's own portals look like
    // the complete and correct arrival set. Measured, they are not:
    //
    //   REPLACE the global apertures with the node's portals:  bunker orphan 1.4% -> 15.3%.
    //     The node's own portals do not carry enough. A listener deep in a void has few of them and
    //     their routed costs are high, so removing the straight-leg candidates loses more than the
    //     routing gains.
    //   UNION of both sets (can only raise a level, double count bounded at 3 dB):
    //     better on reverb balance (cave max 27.4 -> 11.6, store 16.7 -> 16.4) but
    //     cave smoothness 1.59 -> 4.08 dB/block, over the step limit.
    //
    // That regression is MODEL.md's rule earning itself again: a candidate set that changes with
    // listener position steps the field. Adding the node's portals makes the set change at every node
    // boundary, and the cave -- all corridors, so all boundaries -- pays for it.
    //
    // WHAT SHOULD WORK, for the next pass: keep the FIXED global aperture set and make only the LEG
    // routed. Attenuate each aperture along the graph route from its node to the listener's node, plus
    // the final in-node straight hop where a line is legitimate. The node graph is tiny (33 nodes, 60
    // portals on the bunker), so a Dijkstra from each aperture over NODES is cheap and precomputable
    // per aperture. Fixed set, routed legs, no topology deciding WHETHER anything arrives -- only how
    // much. The per-node term still steps at boundaries, but by the difference in routed distance
    // between adjacent nodes, which is bounded by their shared portal geometry rather than unbounded.
    const arrivals = [];
    let pow = 0, bestDb = -Infinity, bestIdx = -1;
    for (let i = 0; i < cands.length; i++) {
      const c = cands[i];
      let px = c.px, py = c.py, pz = c.pz;
      let leg = Math.hypot(px - lx, py - ly, pz - lz);
      let db, legMat;
      if (c.isSrc) { legMat = centerMat; db = -(c.cost + fanDb); }
      else {
        // ROUTED LEG. The candidate SET stays fixed -- every aperture, always -- so nothing steps in
        // or out of it, which is what killed the previous two attempts. Only the LEG changes: rather
        // than a straight line from a distant opening through whatever rock lies between, it follows
        // the graph route from that opening's node to the listener's node, and only the final in-node
        // hop is a straight line, which is the one place line of sight is guaranteed. The arrival is
        // positioned at the portal it actually entered by, so it also comes from the right direction.
        const R = ROUTED_LEGS && nd >= 0 ? apRoute[i - 1] : null;
        if (R) {
          // best portal of the listener's own node: routed distance to it plus the in-node hop, which
          // is the one leg where a straight line is legitimate
          let bl = Infinity, bp = -1;
          for (const pid of G.nodes[nd].edges) {
            const dd = R.dist[pid];
            if (!isFinite(dd)) continue;
            const P = G.portals[pid];
            const tot = dd + Math.hypot(P.px - lx, P.py - ly, P.pz - lz);
            if (tot < bl) { bl = tot; bp = pid; }
          }
          // Compare on the resulting LEVEL, not on distance. The routed path is always longer
          // geometrically and almost always carries far less material, so a distance test rejects it
          // exactly when it is needed -- with `bl < leg` the routed option essentially never won and
          // the numbers came out identical to the straight-line baseline to every digit.
          //
          // Taking the louder of the two is choosing a better ESTIMATOR of one candidate's
          // attenuation, not selecting between physical paths, so it does not conflict with the
          // never-a-max rule that governs the arrival sum.
          if (bp >= 0) {
            const P = G.portals[bp];
            const mS = legDb(D, px, py, pz, lx, ly, lz);
            const mR = legDb(D, P.px, P.py, P.pz, lx, ly, lz);
            // A MAX IS A SWITCH, AND A SWITCH MOVES THE ARRIVAL'S POSITION. Taking the louder
            // of the two estimators was defensible -- it is choosing a better estimate of ONE
            // candidate's attenuation, not selecting between physical paths -- but it is still
            // a hard changeover, and it does not only step the LEVEL: the arrival's position
            // jumps from the aperture to a portal, so the sound's apparent direction jumps with
            // it. That is the most audible kind of discontinuity this model can produce.
            //
            // Blended by power instead. Where one estimator clearly wins it dominates and the
            // result is what the max gave; where they are comparable the position slides
            // between them rather than snapping.
            const dbS = -(distDb(leg) + mS), dbR = -(distDb(bl) + mR);
            if (!BLEND) {
              if (dbR > dbS) { px = P.px; py = P.py; pz = P.pz; leg = bl; }
            } else {
              const wS = dbToPow(dbS), wR = dbToPow(dbR), wT = wS + wR;
              if (wT > 0) {
                const f = wR / wT;
                px += (P.px - px) * f; py += (P.py - py) * f; pz += (P.pz - pz) * f;
                leg += (bl - leg) * f;
              }
            }
          }
        }
        // THE SAME BUNDLE THE DIFFUSE TERM USES. Both answer one question -- how much of the
        // wavefront gets from this opening to the listener -- so measuring one with a five-ray bundle
        // and the other with a single ray made the model contradict itself. It did so spectacularly:
        // at 2.3% of the bunker's standable voxels EVERY dry candidate was dropped with legMat of
        // 80-930 dB while the routed costs were a modest 18-59, and the diffuse field arrived at
        // -18.9 dB through the very same openings. Sound cannot be present as reverberation and
        // absent as a direct arrival along one route; one of the two measurements had to be wrong,
        // and it was the ray.
        legMat = legDb(D, px, py, pz, lx, ly, lz);
        db = INV_SQ
           ? -((c.cost - SLOPE * c.routeLen) + distDb(c.routeLen + leg, c.rad || 0.5) + legMat)
           : -(c.cost + SLOPE * leg + legMat);
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
      const geoExcess = Math.hypot(px - sx5, py - sy5, pz - sz5) + leg - d;
      // Tried additionally gating this on whether the listener can SEE the source (an aperture
      // being a way around a blocked path, so irrelevant with a clear line). Well motivated and
      // empirically wrong: centerMat transitions at the same wall plane, so both terms then
      // jumped together and the doorway crossing went from 8.4 to 10.3 dB/block. Left out.
      const w = c.isSrc ? 1 : sstep(0, 2, geoExcess);
      if (CAND_DBG && Math.abs(lx - CAND_DBG[0] - 0.5) < 0.01
                   && Math.abs(ly - CAND_DBG[1] - 0.5) < 0.01
                   && Math.abs(lz - CAND_DBG[2] - 0.5) < 0.01)
        console.error(`  ${(c.isSrc ? "SRC " : "aper").padEnd(5)}`
          + ` cost=${c.cost === Infinity ? "INF" : c.cost.toFixed(1)}`.padEnd(14)
          + ` leg=${leg.toFixed(1)}`.padEnd(10)
          + ` legMat=${legMat.toFixed(1)}`.padEnd(14)
          + ` excess=${geoExcess.toFixed(1)}`.padEnd(15)
          + ` w=${w.toFixed(3)}`.padEnd(10)
          + ` db=${db > -999 ? db.toFixed(1) : "-inf"}`.padEnd(12)
          + (w <= 1e-4 ? "DROPPED(w)" : db <= -110 ? "DROPPED(db)" : "kept"));
      if (w <= 1e-4 || db <= -110) continue;
      arrivals.push({ db, w, px, py, pz, legMat, len: c.routeLen + leg, isSrc: !!c.isSrc });
      pow += w * dbToPow(db);
      if (db > bestDb) { bestDb = db; bestIdx = arrivals.length - 1; }
    }
    // ── TERM 4: THE DIFFUSE FIELD ────────────────────────────────────────────────
    // In an enclosed space, sound bounces off every surface and builds a field that is
    // essentially UNIFORM -- it does not vary with listener position -- and beyond the critical
    // distance it DOMINATES the direct component. Its absence is why walking past a door felt
    // harsh: every dB of the dry path's variation was heard naked, with nothing underneath it.
    //
    //     P_diffuse = 4 * P_entering_the_space / R,    R = S*alpha/(1-alpha)
    //
    // Power entering the listener's region is the power arriving at each of its boundaries, plus
    // the source itself if it is in that region. Omnidirectional by nature, so no position.
    // COUPLED ACROSS BOUNDARIES, or it steps. A per-region constant is uniform inside a region and
    // different across a boundary, so crossing one jumped 14.5 dB/block at the doorway. Two rooms do
    // have different diffuse levels, but standing in a doorway you hear a BLEND of both -- the
    // fields are coupled through the aperture, and the coupling is what makes the field continuous.
    //
    // Blend by proximity to each boundary, over the boundary's own scale (its radius, from its
    // area). Deep in a room you hear that room's field; at an opening you hear both rooms'
    // half-and-half, which is what a coupled pair of enclosures does.
    // A NEIGHBOUR'S REVERBERATION ARRIVES THROUGH THE APERTURE, like anything else.
    // The governing principle applies to the diffuse field too. Blending neighbouring rooms by
    // centroid distance was continuous but unphysical: it let room A's reverberant field leak
    // into room B without passing through the door, so 42% of what a listener in B heard was A's
    // reverb regardless of whether the door was open -- scenario #6 jumped 12 dB between shut and
    // 20% open and then barely moved, because the door had stopped mattering.
    //
    // Instead: the listener's own region contributes its local field, and each neighbour
    // contributes an ARRIVAL positioned at the shared opening, carrying that room's field through
    // the opening's constriction and spreading from there.
    //
    // This is continuous across a boundary for a reason worth stating: approaching an opening, the
    // neighbour's arrival loses its spreading loss and tends to full strength, so the total tends
    // to (own + neighbour). Cross over and the two swap roles, and the SUM is symmetric. No blend
    // weight is needed and no membership decision survives.
    const roomField = (rid) => { const rm = roomOf(rid); return rm >= 0 ? (fieldOf[rm] || 0) : 0; };

    let diffDb = -120;
    if (nd >= 0) {
      let pTot = roomField(nd);
      // Enumerated over the FIXED SET OF APERTURES, not over the listener's segment's edges.
      // Gating on segment adjacency was gating on bookkeeping: standing one corridor segment deeper
      // into a room you received nothing from the room next door, even a few blocks from its open
      // doorway, so the level jumped 4.1 dB the moment you stepped into the segment that happened
      // to touch it. Distance and constriction already decide how much arrives -- topology must
      // not also decide WHETHER it arrives. Same rule as the air arrivals.
      const myRoom = roomOf(nd);
      for (const A of G.apertures) {
        // NO ROOM-MEMBERSHIP GATE. This skipped every aperture that did not bound the listener's own
        // room, so crossing a room boundary swapped the contributing set wholesale and stepped the
        // field -- 10.1 dB across one block in the store, where the two sides of a boundary drew on
        // completely different sets of third-party rooms. It is the identical mistake the loop above
        // already fixed for SEGMENT adjacency: "topology must not also decide WHETHER it arrives".
        // Distance, constriction and material decide how much; nothing decides whether.
        //
        // An opening radiates the reverberation of BOTH rooms behind it, so both count -- except the
        // listener's own room, whose field they already receive directly as the uniform term, and which
        // would otherwise be counted twice. That exclusion is on which FIELD is taken, never on whether
        // the aperture contributes, so the candidate set stays fixed and nothing can step in or out.
        //
        // THE EXCLUSION STAYS, and the residual step needs a fix elsewhere. Dropping it to buy
        // continuity was measured and is clearly worse: store passivity 0.1% -> 4.6% (failing), bunker
        // smoothness 2.24 -> 2.96, cave 1.65 -> 3.22. Counting a room's own field twice breaks the
        // passivity bound, which is exactly what that bound exists to catch.
        //
        // So the residual boundary step is inherent to sampling only your OWN room's openings, and it is
        // worth stating precisely. At a boundary between rooms A and B the SHARED aperture bridges
        // perfectly -- A's side reads fieldA + fieldB, B's side reads fieldB + fieldA, identical. What
        // does not bridge is the others: A's opening onto C gives fieldC on A's side and nothing on B's,
        // while B's onto D gives fieldD on B's side and nothing on A's. Different sets, contributions
        // that cannot match, hence a step. 98 of the store's 111 remaining over-limit pairs are this.
        //
        // THE FIX IS NOT IN THIS LOOP. Those second-order contributions belong in the ROOM FIELDS: if
        // fieldB already included the energy reaching B from C by way of A, a listener would need only
        // their own room's field plus a near-field term for their own openings, and nothing would be
        // left to differ across a boundary. Pass 2 already does one Jacobi iteration of WALL coupling;
        // the same treatment for APERTURE coupling, solved to convergence over 13-55 rooms, is a small
        // linear system and it removes the entire class rather than trading one symptom for another.
        // ── WHERE THE OPEN-AIR REVERB FLOOR COMES FROM, AND WHAT DOES NOT FIX IT ────────
        // A speaker in an open field sits under a flat -20 dB floor that never varies with
        // position; past ~8 blocks it overtakes the direct sound and becomes the level, which is
        // why occlusion stops working out there. The listener's own region is correctly silent
        // (outdoors, sky-exposed, canReverb ~0). The floor is the BUILDING's interior reverb
        // radiating out of this loop, and the reason it is so loud is visible in the trace:
        //
        //     aper rooms 8/12   field=-20.8  leg=10.6  ->  -29.2 dB
        //     aper rooms 8/12   field=-20.8  leg=13.5  ->  -31.4 dB
        //     aper rooms 8/12   field=-20.8  leg=11.1  ->  -33.7 dB
        //     ... six openings between the SAME pair of rooms, summing to -20.4 dB
        //
        // Each opening hands the listener room 12's ENTIRE field. Between them they can only
        // carry the power that actually crossed that boundary -- the same "an opening is not a
        // source" argument that fixed the direct arrivals, not yet applied to what ROOMS exchange.
        //
        // Capping the sum at free field was tried and is not the answer: it fixes the level
        // (+10.1 -> +2.2 dB at 32-40 blocks) and triples the roughness (store 4.50 -> 16.18
        // dB/block), because a hard clamp steps wherever it engages. The bound has to act on what
        // the rooms exchange, not on what the listener receives.
        // ── ONLY YOUR OWN ROOM'S OPENINGS, NOW THAT THE ROOMS TALK TO EACH OTHER ────────
        // This loop used to run over EVERY aperture in the POI -- 47 of them in the store -- each
        // one a ray bundle cast from the listener to an opening up to 60 blocks away. A bundle
        // like that flips from blocked to clear in a single block, so their sum lurched: all eight
        // store speakers moved 3-5.6 dB together across one step, the reverberant term jumping a
        // consistent +5.5 dB.
        //
        // The membership gate was tried before and correctly reverted, because without pass 3 it
        // stepped 10.1 dB at a room boundary: each side drew on a different set of THIRD-PARTY
        // rooms, and those contributions had nowhere else to come from. Pass 3 is where they come
        // from now -- the fields already contain the energy that reached each room through every
        // opening in the building -- so what is left here is only the NEAR-FIELD correction for
        // standing beside your own doorway, which is a handful of terms, all close, and smooth
        // because proximity is smooth.
        // MEASURED AGAIN WITH PASS 3 IN PLACE, AND IT IS STILL WRONG: store roughness 5.45 ->
        // 10.30 dB/block, bunker 1.89 -> 2.38. Pass 3 brings connected rooms' fields closer but
        // does not make them equal, so crossing a boundary still swaps both the opening set AND
        // the own-room field at once. The prediction in the comment below -- that pass 3 would
        // make this gate safe -- is therefore wrong, and worth leaving recorded as such.
        //   if (A.roomA !== myRoom && A.roomB !== myRoom) continue;
        let pf = 0;
        if (A.roomA !== undefined && A.roomA >= 0 && A.roomA !== myRoom) pf += fieldOf[A.roomA] || 0;
        if (A.roomB !== undefined && A.roomB >= 0 && A.roomB !== myRoom) pf += fieldOf[A.roomB] || 0;
        if (pf <= 0) continue;
        // DISTANCE TO THE OPENING, not to its centre. The store's worst step -- 10.1 dB across one
        // block -- is a room boundary crossing where the field went 7.3 -> -8.0 dB while the direct
        // component barely moved. Standing just inside a room, a hand's width from a wide opening, you
        // should receive the neighbour's reverberation almost undiminished; measuring to the centroid
        // of a storefront-sized boundary charges tens of blocks of distance for being right beside it.
        // Clamping to the opening's extent makes the coupling continuous across the boundary, which is
        // what makes the two sides agree.
        const leg = apDist(A, lx, ly, lz);
        // MATERIAL ON THE WAY OUT OF THE APERTURE COUNTS. This charged only for distance and
        // constriction, so a neighbouring room's reverberation reached the listener through anything
        // at all: measured on the bunker, a listener under 439 dB of rock received the field at
        // -13 dB while no path delivered a thing. An aperture radiating into your space is subject to
        // whatever stands between it and you, exactly as the air arrivals already are -- they compute
        // this same legMat and it was simply missing here.
        const legMat = process.env.CMM_NOLEGMAT === '1' ? 0 : legDb(D, A.px, A.py, A.pz, lx, ly, lz);
        // ── HOW MUCH OF THE OPENING YOU ACTUALLY SEE ────────────────────────────────
        // The neighbour's reverberation came through here as: a CONSTRICTION penalty from the
        // opening's area, plus a spreading term, plus material. The constriction is the problem.
        // It is a fixed penalty applied to everyone outside the room and to nobody inside it, so
        // it lands as a STEP exactly at the boundary -- and the model's own continuity argument
        // ("approaching an opening the neighbour's arrival tends to full strength, cross over and
        // the two swap roles, and the SUM is symmetric") only holds if that penalty vanishes at
        // the opening. It does not.
        //
        // Measured on the store: stepping from the outdoors into a 122-voxel enclosed pocket
        // jumped the diffuse field 14 dB in one block, identically for all eight speakers -- the
        // signature of a listener-side term, and the worst standable step in the capture.
        //
        // Constriction and spreading are the same fact told twice: an opening of radius a seen
        // from distance r subtends 2*pi*(1 - r/sqrt(r^2+a^2)) of the hemisphere. That IS the
        // fraction of the neighbour's field that reaches you. It is 1 with your nose in the
        // doorway -- so the boundary is continuous by construction, not by argument -- and falls
        // to area/(2*pi*r^2) far away, which is the constriction and the spreading together.
        // One continuous geometric quantity replacing two terms, neither of which was continuous.
        const aRad = apRad(A);
        const thru = SA_COUPLE
          ? (1 - leg / Math.sqrt(leg * leg + aRad * aRad)) * dbToPow(-legMat)
          : dbToPow(constrictDb(A.throat) - distDb(leg, aRad) - legMat);
        pTot += pf * thru;
        if (DIFF_VOX && Math.abs(lx - DIFF_VOX[0] - 0.5) < 0.01
                     && Math.abs(ly - DIFF_VOX[1] - 0.5) < 0.01
                     && Math.abs(lz - DIFF_VOX[2] - 0.5) < 0.01 && pf * thru > 0)
          console.error(`    aper rooms ${A.roomA}/${A.roomB}`.padEnd(22)
            + ` field=${powToDb(pf).toFixed(1)}`.padEnd(14)
            + ` leg=${leg.toFixed(1)}`.padEnd(10)
            + ` legMat=${legMat.toFixed(1)}`.padEnd(14)
            + ` constrict=${constrictDb(A.throat).toFixed(1)}`.padEnd(16)
            + ` -> ${powToDb(pf * thru).toFixed(1)} dB`);
      }
      if (pTot > 0) diffDb = powToDb(pTot);
      if (DIFF_VOX && Math.abs(lx - DIFF_VOX[0] - 0.5) < 0.01
                   && Math.abs(ly - DIFF_VOX[1] - 0.5) < 0.01
                   && Math.abs(lz - DIFF_VOX[2] - 0.5) < 0.01)
        console.error(`    own room ${roomOf(nd)} field term = `
          + `${roomField(nd) > 0 ? powToDb(roomField(nd) * mixFade).toFixed(1) : "-inf"} dB`
          + `   TOTAL diffuse = ${diffDb.toFixed(1)} dB`);
    }
    if (diffDb > -110) {
      arrivals.push({ db: diffDb, w: 1, px: lx, py: ly, pz: lz, legMat: 0, len: d, isDiffuse: true });
      pow += dbToPow(diffDb);
      if (diffDb > bestDb) { bestDb = diffDb; bestIdx = arrivals.length - 1; }
    }

    // contribution prune, applied after the fact so the set itself never changed
    let kept = 0;
    for (const a of arrivals) if (a.db >= bestDb - PRUNE_DB) kept++;

    const total = pow > 0 ? powToDb(pow) : -120;
    const dom = arrivals[bestIdx] || null;
    const airs = arrivals.filter(a => !a.isSrc && !a.isDiffuse && a.db >= bestDb - PRUNE_DB);
    const solid = arrivals.find(a => a.isSrc);
    return {
      d, total,
      airDb: airs.length ? powToDb(airs.reduce((s, a) => s + a.w * dbToPow(a.db), 0)) : -120,
      diffDb,
      solidDb: solid ? solid.db : -120,
      matDb: centerMat, airLen: dom ? dom.len : d,
      entry: dom, airs, arrivals, kept,
      // WHERE the transmitted sound radiates FROM, and how big that radiator is. The renderer needs
      // both: placing transmitted sound at the SPEAKER contradicts the governing principle and is
      // audibly wrong through a floor -- the author heard a point source sweeping left-to-right
      // underfoot where a whole ceiling should have been radiating down at them.
      bm: bm ? { x: bm.x, y: bm.y, z: bm.z, thick: bm.thick } : null,
      // fraction of the transmitted-path power that actually came through AIR rather than through
      // material -- the renderer's crossfade between "from the speaker" and "from the barrier"
      openFrac: fanPow > 0 ? Math.max(0, Math.min(1, fanOpenPow / fanPow)) : 1,
      reached: airs.length > 0,
    };
  };
  evalFn.fieldDb = fieldDbOf();
  evalFn.roomOfNode = rid => (rid >= 0 && rid < G.nodes.length ? G.nodes[rid].room : -1);
  return evalFn;
}

// Distance from a point to an opening's extent, not to its centre. Falls back to the centre when the
// adapter did not supply bounds.
function apDist(A, x, y, z) {
  if (A.x0 === undefined) return Math.hypot(A.px - x, A.py - y, A.pz - z);
  const cx = Math.max(A.x0, Math.min(A.x1, x));
  const cy = Math.max(A.y0, Math.min(A.y1, y));
  const cz = Math.max(A.z0, Math.min(A.z1, z));
  return Math.hypot(cx - x, cy - y, cz - z);
}

// ── MATERIAL ALONG A LEG, MEASURED AS A BUNDLE RATHER THAN A RAY ──────────────
// A single straight ray is discontinuous: it clips a corner and the whole term jumps. Charging the
// aperture-to-listener leg with one ray was necessary for correctness -- without it a neighbouring
// room's reverberation reached listeners under hundreds of dB of rock -- but it put 20-33 dB cliffs
// into the field, which is the same defect, and the same cause, as the old whole-voxel raycast.
//
// A listener and an aperture on their own room's boundary are in the SAME connected region, so a
// straight line between them clipping geometry is usually non-convexity rather than real
// obstruction. Energy-summing a small bundle answers "how much of the wavefront gets through"
// instead of "does this one line hit something": a ray grazing a corner contributes little while its
// neighbours carry the arrival, so the result moves smoothly with position.
const LEG_FAN_R = 1.5;    // blocks: bundle radius, about a first-Fresnel zone at room scale
// A CROSS IS NOT AN AREA SAMPLE -- the same mistake the Fresnel disc above was built to
// replace, still sitting here. Five rays at the arms of a plus sign, each weighted 1/5, sample
// a bundle whose integrand has a hard edge in it wherever the leg passes a corner: rays clear
// that corner one at a time, and in a POWER sum one ray of five clearing while the rest stay
// blocked can move the result several dB in a single block. Measured on the store capture --
// the worst real number we have, 4.58 dB/block -- all eight speakers stepped together through
// one shared opening, which is the signature of a shared leg, not of any speaker's geometry.
//
// CMM_LEGDISC=1 swaps in an area-weighted disc of the same radius (centre plus two rings),
// which is what the bundle was always meant to be.
const LEG_DISC = process.env.CMM_LEGDISC === "1";
const LEG_FAN_W = LEG_DISC
  ? (() => {
      const out = [];
      for (const [rf, n, share] of [[0, 1, 0.1], [0.6, 6, 0.45], [1.0, 8, 0.45]])
        for (let i = 0; i < n; i++) {
          const a = (i / n) * Math.PI * 2 + rf * 1.7;
          out.push([rf * Math.cos(a), rf * Math.sin(a), share / n]);
        }
      return out;
    })()
  : [[0, 0, 0.2], [1, 0, 0.2], [-1, 0, 0.2], [0, 1, 0.2], [0, -1, 0.2]];
const LEG_FAN = [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1]];
function legDb(D, ax, ay, az, lx, ly, lz) {
  let ux = lx - ax, uy = ly - ay, uz = lz - az;
  const ul = Math.hypot(ux, uy, uz) || 1;
  ux /= ul; uy /= ul; uz /= ul;
  // any two vectors perpendicular to the leg
  let px = -uz, py = 0, pz = ux;
  let pl = Math.hypot(px, py, pz);
  if (pl < 1e-3) { px = 1; py = 0; pz = 0; pl = 1; }
  px /= pl; py /= pl; pz /= pl;
  const qx = uy * pz - uz * py, qy = uz * px - ux * pz, qz = ux * py - uy * px;
  let pow = 0;
  for (const [a, b, w] of LEG_FAN_W) {
    const ox = (px * a + qx * b) * LEG_FAN_R;
    const oy = (py * a + qy * b) * LEG_FAN_R;
    const oz = (pz * a + qz * b) * LEG_FAN_R;
    pow += w * dbToPow(-segDbAt(D, ax + ox, ay + oy, az + oz, lx, ly, lz));
  }
  return pow > 0 ? -powToDb(pow) : 120;
}

// ── continuity / reach metrics (same shape as judge3d's adjacency measure) ─────
function measure(D, G, evalFn, label, range) {
  const dbAt = new Float32Array(D.n).fill(NaN);
  let airVox = 0, reached = 0;
  for (let y = 0; y < D.sy; y++)
  for (let z = 0; z < D.sz; z++)
  for (let x = 0; x < D.sx; x++) {
    const i = x + D.sx * (z + D.sz * y);
    if (G.open[i] <= OPEN_EPS) continue;
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
module.exports = { buildGraph, solveAir, makeNewEvaluator, measure, makeOpenTest, segDb, segDbAt, SLOPE, distDb,
                   barrierMid, FRESNEL_PTS };   // exported for per-patch diagnosis
