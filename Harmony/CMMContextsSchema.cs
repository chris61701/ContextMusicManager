using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // THE LAYOUT OF contexts.xml.
    //
    // Unlike CMMSettingsSchema this carries no values. The context table itself is
    // InitContexts() — that is the source of truth for what a context IS, and it
    // was made complete and correct in 3ff8c77 before any of this could be trusted.
    // What a generated file also needs is the part the code cannot hold: the
    // documentation users read, and the order and grouping that make 71 contexts
    // legible. That is what lives here.
    //
    // So: names and order and prose here, attribute VALUES from the live table at
    // write time. A context added to InitContexts but not listed here is still
    // written out (appended at the end) — the writer treats this as layout, not as
    // a gate, so forgetting to add a line here degrades to "undocumented", never to
    // "missing".
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class ContextEntry
    {
        public string Comment;    // a comment block
        public string Name;       // a context, by name; values come from the live table
        public bool   Gap;        // a blank line
    }

    internal static class CMMContextsSchema
    {
        // Bump when contexts are added or their built-in attributes change. Stamped
        // into the written file as <Contexts schema="N">.
        internal const int CurrentSchema = 1;

        static ContextEntry Cmt(string t) => new ContextEntry { Comment = t };
        static ContextEntry Ctx(string n) => new ContextEntry { Name = n };
        static ContextEntry Gap()         => new ContextEntry { Gap = true };

        // Attribute order and column widths, measured from the file that shipped up to
        // 1.0.5 so a generated file lines up the way the hand-maintained one did.
        internal static readonly string[] AttrOrder =
        {
            "name",
            "source",
            "subfolders",
            "fallback",
            "combat",
            "resume",
            "stale",
            "loop",
            "cool",
            "fadeintime",
            "warmup",
            "warmgroup",
            "bypass",
            "priority",
            "once",
            "volume",
            "link"
        };

        internal static readonly Dictionary<string, int> AttrWidth = new Dictionary<string, int>
        {
            { "name", 33 },
            { "source", 36 },
            { "subfolders", 18 },
            { "fallback", 28 },
            { "combat", 13 },
            { "resume", 14 },
            { "stale", 11 },
            { "loop", 12 },
            { "cool", 8 },
            { "fadeintime", 16 },
            { "warmup", 11 },
            { "warmgroup", 16 },
            { "bypass", 14 },
            { "priority", 14 },
            { "once", 12 },
            { "volume", 12 },
            { "link", 18 }
        };

        // The header comment, written above the root element.
        internal const string Header = @"Context definitions for Context Music Manager.
This file is hot-reloaded — save while the game is running to apply changes.

Attributes:
  name      - unique identifier (used in logs and fallback references)
  source    - where this context's music comes from. One of:
                • a subfolder under /Music (e.g. source=""ambient""),
                • an absolute folder path anywhere on disk (e.g. source=""D:\MyMusic\Ambient""), or
                • a playlist file ending in .m3u / .m3u8, relative to /Music or absolute
                  (e.g. source=""home/chill.m3u"").
              (empty = sentinel, no audio). Absolute folders and playlist tracks still get
              loudness normalization + per-track volume.
              PLAYLISTS: a playlist is a standard, file-only .m3u — one track path per line;
              blank lines and #-comment lines ignored; relative track entries resolve against
              the PLAYLIST FILE'S OWN folder; absolute entries used as-is. It is a plain pool —
              GlobalTrackOrder still decides play order (Sequential follows the file's line
              order). The old attribute name 'path' still loads as a deprecated alias.
              FOLDER + PLAYLISTS: if a FOLDER source also contains .m3u files (top level only),
              the pool is the loose music files PLUS the tracks listed by those .m3u's, deduped
              (a track counted once even if both loose and listed). EXCEPTION: with
              subfolders=""true"" the folder recurses for music and .m3u files are ignored
              entirely (not expanded, not played). Editing an in-folder .m3u hot-reloads.
  fallback  - what to try when this context has no files:
                Stack             = check remaining active contexts by priority
                VanillaIfDMS      = vanilla when Dynamic Music is on, else Stack
                VanillaIfPanicked = vanilla when threat=Panicked, else Stack
                Vanilla / Silence / <another context name>
  combat    - true = this is FIGHT music, so the combat-music toggle (Alt+Home) silences it
              along with the Combat_* tiers  (default: false). The toggle otherwise covers
              any context whose NAME contains ""combat"", which is a naming convention and
              misses themes that are combat music without saying so (Low_Health_Panic is
              one). Mark your own that way and the toggle will honour them too.
  resume    - continue from where we left off when re-entering  (default: true)
  stale     - seconds before an abandoned resume position is forgotten  (0 = never resume)
  loop      - loop the clip rather than picking a new track on end
  cool      - seconds before this context can fire again after leaving
  fadeintime - fade-in duration in seconds (the crossfade ramp when this context's music starts);
               fractional values allowed, e.g. 0.5
  warmup    - seconds this context must remain CONTINUOUSLY active before it becomes
              eligible to trigger music  (0 = trigger immediately; an anti-flicker dwell
              so momentary contexts don't interrupt what's already playing)
  warmgroup - optional tag shared by contexts that hand off to each other (e.g. a
              day/night pair). When one joins while a group sibling is still active,
              it inherits the group's oldest warmup start instead of restarting its
              own — so a day↔night swap stays viable continuously instead of dropping
              out for the full warmup and letting a lower context play briefly. Each
              context still uses its OWN warmup value; keep group members' warmups
              equal for a predictable dwell.
  bypass    - skip cooldown check on entry  (use for stingers/events)
  priority  - higher value wins; equal priority never interrupts a playing context
  once      - play to completion then automatically return to previous context
  volume    - per-context volume multiplier (0.0–1.0) that replaces the global
              VolumeModifier for this context  (omit / negative = use global)
  subfolders - look for music in this folder AND all of its subfolders (default:
              false = only files sitting directly in the folder). If a subfolder is
              itself another context's source folder, the tracks there belong to that more
              specific context, not this one — so each track only ever plays in a
              single context and you never get the same song double-counted.";

        internal static readonly ContextEntry[] Layout =
        {

            Gap(),
            Cmt(@"── Events ──────────────────────────────────"),
            Ctx("Death"),
            Ctx("Low_Health_Panic"),
            Ctx("Spawn"),

            Gap(),
            Cmt(@"── Vehicles ───────────────────────────────"),
            Ctx("Vehicle_Gyrocopter"),
            Ctx("Vehicle_Bicycle"),
            Ctx("Vehicle_Minibike"),
            Ctx("Vehicle_Motorcycle"),
            Ctx("Vehicle_4x4"),
            Ctx("Vehicle"),

            Gap(),
            Cmt(@"── Swimming ───────────────────────────────"),
            Cmt(@"Player is in deep enough water to swim. 8s dwell so brief dips don't
interrupt; falls back to the stack so exploration music resumes on exit."),
            Ctx("Swimming"),

            Gap(),
            Cmt(@"── Points of Interest ─────────────────────"),
            Cmt(@"POI_Cleared fires once when the last sleeper in a POI is killed. Priority must
outrank lingering combat: clearing a POI means you were just fighting, so combat
(generic 50-52 AND biome 60-62) is still on the stack during the brief eviction
window. At 91 the stinger wins over all combat tiers, Blood_Moon (71), and
Low_Health_Panic (90); only Death (100) supersedes it."),
            Ctx("POI_Cleared"),

            Gap(),
            Cmt(@"Infested and non-infested POI contexts share warmgroup=""poi"" so the clear handoff
(POI_Infested_Tn -> POI_Tn) is continuous: the joining non-infested context inherits
the group's oldest warmup start instead of restarting its own dwell, so it's viable
immediately and ambient never leaks through the gap while the POI_Cleared stinger
fades. Each context still uses its own warmup value."),

            Gap(),
            Cmt(@"Infested: playing while sleeper volumes still hold live enemies."),
            Ctx("POI_Infested_T5"),
            Ctx("POI_Infested_T4"),
            Ctx("POI_Infested_T3"),
            Ctx("POI_Infested_T2"),
            Ctx("POI_Infested_T1"),
            Ctx("POI_Infested_T0"),
            Ctx("POI_Infested"),

            Gap(),
            Cmt(@"Non-infested: inside a POI with no live sleepers (cleared or never infested)."),
            Ctx("POI_T5"),
            Ctx("POI_T4"),
            Ctx("POI_T3"),
            Ctx("POI_T2"),
            Ctx("POI_T1"),
            Ctx("POI_T0"),
            Ctx("POI"),

            Gap(),
            Cmt(@"── Combat ────────────────────────────────────"),
            Cmt(@"Three combat tiers selected by the weighted scoring in context.lua (COMBAT_TIERS). The
combat/ root folder IS the base (low) tier; the escalations live in its _high and
_boss subfolders, underscore-prefixed so the directory scan never confuses them with
the per-biome subfolders below. Boss → High → Low; Low bridges to vanilla combat
music (when DMS is on and the game threat is Panicked). Folder scanning is top-level
only, so combat/ (Low) does NOT pick up files sitting in _high / _boss / <biome>."),
            Cmt(@"Blood_Moon_Combat: combat during a blood moon night (highest combat priority)."),
            Ctx("Blood_Moon_Combat"),
            Ctx("Combat_Boss"),
            Ctx("Combat_High"),
            Ctx("Combat_Low"),

            Gap(),
            Cmt(@"Biome-specific combat tiers. Each biome gets a combat/<biome> folder that mirrors
the generic layout: the biome folder root is that biome's low tier, with _high and
_boss subfolders for escalation. The resolver puts the biome variant on the stack
alongside the active generic tier whenever combat fires in a known biome (e.g.
Combat_High + Combat_High_Forest). Each variant outranks its generic counterpart
(priority band 60–62) so biome combat music wins when its folder has tracks, and
falls back to the matching generic tier when the folder is empty. Only the one active
tier gets a variant on the stack, so the band never inverts across tiers.
Blood_Moon_Combat stays generic (no biome split). Empty folders are intentional:
ship only the biome/tier combinations you have music for."),
            Cmt(@"Burnt forest"),
            Ctx("Combat_Boss_Burnt_Forest"),
            Ctx("Combat_High_Burnt_Forest"),
            Ctx("Combat_Low_Burnt_Forest"),
            Cmt(@"Desert"),
            Ctx("Combat_Boss_Desert"),
            Ctx("Combat_High_Desert"),
            Ctx("Combat_Low_Desert"),
            Cmt(@"Forest"),
            Ctx("Combat_Boss_Forest"),
            Ctx("Combat_High_Forest"),
            Ctx("Combat_Low_Forest"),
            Cmt(@"Snow"),
            Ctx("Combat_Boss_Snow"),
            Ctx("Combat_High_Snow"),
            Ctx("Combat_Low_Snow"),
            Cmt(@"Wasteland"),
            Ctx("Combat_Boss_Wasteland"),
            Ctx("Combat_High_Wasteland"),
            Ctx("Combat_Low_Wasteland"),

            Gap(),
            Cmt(@"── Tension ───────────────────────────────────"),
            Ctx("Tension"),

            Gap(),
            Cmt(@"── Online players ────────────────────────────"),
            Cmt(@"A non-friendly player (neither party member nor ACL friend) is within
PlayerDetectRange. Multiplayer only; never fires in single-player. Priority 29
sits just under Tension (30): a real fight (Combat 50+) still wins, but the
presence of a stranger outranks ambient/home/trader exploration music. The
warmup dwell keeps a player merely passing through from interrupting. Detection
is relationship-only and ignores the server PvP mode (PlayerKillingMode)."),
            Ctx("Online_Stranger"),
            Ctx("Online_Friend"),

            Gap(),
            Cmt(@"── Traders ───────────────────────────────────"),
            Ctx("Trader_Joel"),
            Ctx("Trader_Jen"),
            Ctx("Trader_Rekt"),
            Ctx("Trader_Bob"),
            Ctx("Trader_Hugh"),
            Ctx("Trader"),

            Gap(),
            Cmt(@"── Home ────────────────────────────────────────"),
            Cmt(@"Home plays by day (and is the default); Home_Night provides night-exclusive
tracks and falls back to Home when it has no files.
Priority 19 puts Home ABOVE every plain POI context (16-18) and BELOW every
infested one (20-22), so a base built in an ordinary POI plays your home music,
while an infested building still takes over. Both Home contexts share priority
19 on purpose - equal priority never interrupts, so the day/night handoff runs
through the shared warmgroup instead of one outranking the other. Give them
different numbers and one will win permanently."),
            Ctx("Home_Night"),
            Ctx("Home"),

            Gap(),
            Cmt(@"── Ambient ───────────────────────────────────"),
            Cmt(@"The biome base plays by day (and is the everyday default); each biome's
optional _Night context (in a _night subfolder) provides night-exclusive tracks.
Night fallback order: biome _Night -> generic Ambient_Night -> (Stack) the biome's
OWN day theme -> generic Ambient. The day theme is reachable because the resolver
also puts the biome day context on the stack at night, and Ambient_Night falls back
to Stack to find it. Biome _Night is priority 12 (above the day context, 11) so the
night theme wins whenever it has tracks."),
            Ctx("Ambient_Forest_Night"),
            Ctx("Ambient_Forest"),
            Ctx("Ambient_Burnt_Forest_Night"),
            Ctx("Ambient_Burnt_Forest"),
            Ctx("Ambient_Desert_Night"),
            Ctx("Ambient_Desert"),
            Ctx("Ambient_Snow_Night"),
            Ctx("Ambient_Snow"),
            Ctx("Ambient_Wasteland_Night"),
            Ctx("Ambient_Wasteland"),
            Ctx("Ambient_Night"),
            Ctx("Ambient"),

            Gap(),
            Cmt(@"── Radio ───────────────────────────────────────"),
            Cmt(@"The world-emitter ""Radio"" pool as a first-class context. source=""Radio"" resolves the same folder
the WorldEmitterSource uses (under MusicPath). NOTE: as written this context is DEFINED but never
auto-activates — no link to a vanilla DMS section and no detection wires it to a game state, so
the resolver never puts it on the stack. It needs a trigger (a link, or code) before it will play
as your (non-diegetic) soundtrack."),
            Ctx("Radio"),

            Gap(),
            Cmt(@"── Menu ────────────────────────────────────────"),
            Ctx("Menu"),

            Gap(),
            Cmt(@"── Sentinels — required, do not remove ──────────"),
            Ctx("Silence"),
            Ctx("Vanilla"),
        };
    }
}
