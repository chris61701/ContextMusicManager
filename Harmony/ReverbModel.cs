using System;
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE REVERB RULE, ON ITS OWN, WITH NO GAME IN IT.
//
// Both functions here used to live inside EmitterAcoustics.cs, which is #if GV_V3 and needs a
// World, a Block and a BlockValue to compile — so neither could be driven by a test, and the one
// harness that renders emitter audio (CmmEmitSim) faked ComputeReverb with three constants. The
// mapping from "what the room is" to "what the reverb does" had therefore never been exercised
// anywhere, which is a poor place to start changing it from.
//
// Neither function touches the world. Given a size, a reflectivity, an openness and a roof they
// are arithmetic, and given a material name so is the other. Same reasoning, and the same shape,
// as EmitterSlots: the rule lives where a test can reach it and the caller keeps the geometry.
//
// Version-neutral by construction — no UnityEngine, no game types, no #if.
// ─────────────────────────────────────────────────────────────────────────────────────────────
namespace CMM
{
    internal static class ReverbModel
    {
        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // Where the size cue stops telling the reverb anything. sizeTail is (roomSize - 2) / this,
        // clamped to 1, so every space at or above (2 + SizeTailSpan) blocks asks for exactly the
        // same tail. Named rather than inline because it is the number the cavern work has to
        // move: a 20-block room and a 200-block cavern are currently indistinguishable here.
        internal const float SizeTailSpan = 18f;
        internal const float SizeTailFloor = 2f;

        // ROOM → REVERB. wet is the level, room is the tail length, damp is how dark it gets.
        internal static void ComputeReverb(float roomSize, float reflect, float openFrac, bool roofed,
                                           out float wet, out float room, out float damp)
        {
            float enclosure = 1f - openFrac;                              // 0 = wide open, 1 = sealed room
            float roof = roofed ? 1f : 0.5f;                             // open sky dries it — softly, not a 3× cliff on one up-ray
            // LEVEL: enclosed × reflective (no size penalty — a small stone room still rings). Linear in
            // enclosure (was enclosure², which crushed partially-open big spaces).
            wet  = Clamp01(enclosure * reflect * roof);
            // TAIL LENGTH. A ray probe only measures the longest SIGHTLINE, which underestimates a winding or
            // tight tunnel: no straight line is long even though the sealed stone space rings for ages (the
            // length is hidden around the bends). So blend two cues and take the max: (1) sightline size — a
            // big open cavern gets its long tail here; (2) a "trapped" term — a fully ENCLOSED, hard
            // (reflective / low-absorption) space rings regardless of how far you can see, so a narrow sealed
            // stone tunnel finally gets a real tail. Absorptive (cloth/dirt) or open spaces stay dry.
            float sizeTail = (roomSize - SizeTailFloor) / SizeTailSpan;            // long-sightline halls
            float trapTail = enclosure * Clamp01((reflect - 0.4f) / 0.5f);         // sealed + hard ⇒ rings even if narrow
            room = Clamp01(sizeTail > 0.6f * trapTail ? sizeTail : 0.6f * trapTail);
            damp = Clamp01(0.25f + 0.7f * (1f - reflect));                // dead materials → darker/shorter tail
        }

        // SurfaceCategory → acoustic reflectivity (0 = dead/absorptive, 1 = hard/reflective), cached.
        // Distinct from transmission loss: glass blocks little sound but reflects a lot; cloth absorbs both.
        private static readonly Dictionary<string, float> _reflectCache =
            new Dictionary<string, float>(StringComparer.Ordinal);

        internal static float MaterialReflect(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return 0.6f;
            lock (_reflectCache)
                if (_reflectCache.TryGetValue(cat, out float v)) return v;
            string c = cat.ToLowerInvariant();
            float r;
            if (c.Contains("metal") || c.Contains("iron") || c.Contains("steel"))            r = 0.95f;
            else if (c.Contains("stone") || c.Contains("concrete") || c.Contains("brick")
                     || c.Contains("cobble") || c.Contains("rock") || c.Contains("cinder"))   r = 0.9f;
            else if (c.Contains("glass"))                                                     r = 0.85f;
            else if (c.Contains("plastic") || c.Contains("polymer") || c.Contains("rubber"))   r = 0.6f;
            else if (c.Contains("wood") || c.Contains("lumber"))                              r = 0.45f;
            else if (c.Contains("dirt") || c.Contains("earth") || c.Contains("soil")
                     || c.Contains("sand") || c.Contains("gravel") || c.Contains("clay")
                     || c.Contains("snow"))                                                    r = 0.3f;
            else if (c.Contains("cloth") || c.Contains("organic") || c.Contains("plant")
                     || c.Contains("hay") || c.Contains("leaves") || c.Contains("wool")
                     || c.Contains("cotton"))                                                  r = 0.15f;
            else                                                                              r = 0.6f;
            lock (_reflectCache) _reflectCache[cat] = r;
            return r;
        }
    }
}
