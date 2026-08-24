using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MusicUtils.Enums;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONTEXT STACK
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager : MonoBehaviour
    {
        public static Dictionary<string, Contexts> contexts = new Dictionary<string, Contexts>();
        public static Dictionary<string, ContextCache> cacheMap = new Dictionary<string, ContextCache>();
        public static List<ActiveContext> _contextStack = new List<ActiveContext>();
        public static Contexts currentContext;

        // Cancellation token for in-flight transitions. Bumped on EVERY context change
        // (TriggerMusic, skip, auto-advance, fade-to-silence). Volatile because the native
        // start worker thread reads it to drop superseded requests (see StartNativeContext).
        private static volatile int _transitionId = 0;
        public static string _pendingContext = null;

        // When true, Conductor.Update() runs freely and BackgroundMusicMono is unblocked.
        public static bool vanillaIsActive = false;
        // Tracks what the game's Conductor wants to play even while it's blocked.
        public static string LastKnownGameSection = "None";

        public void InitContexts()
        {
            // Defaults:
            // fallback="Stack", resume=true, stale=120, loop=false, cool=5, fadeintime=5, bypass=false,
            // once=false, warmup=0.  fadeintime = fade-in seconds; warmup = dwell seconds before viable.
            void Add(string name, string path, string fallback = "Stack",
                     bool resume = true, int stale = 120, bool loop = false,
                     int cool = 5, float fadeintime = 5f, bool bypass = false,
                     int priority = 0, bool once = false, float volume = -1f,
                     int warmup = 0, string warmgroup = null, bool subfolders = false,
                     string link = null, bool combat = false)
            {
                contexts[name] = new Contexts(name, path, fallback, resume, stale,
                                              loop, cool, fadeintime, bypass, priority, once, volume,
                                              warmup, warmgroup, subfolders, link);
                // Explicitly-marked FIGHT music, same meaning as combat="true" in contexts.xml.
                // The naming convention catches Combat_*; this is for the ones it cannot, like
                // Low_Health_Panic — a fight sting in everything but its name. Without it here,
                // a install with no contexts.xml would silently stop honouring the mute for it.
                if (combat) MarkCombatContext(name);
            }

            Add("Death",               "events/death",          resume:false, stale:0,  cool:3, fadeintime:0.1f, bypass:true, priority:100, once:true);
            Add("Low_Health_Panic",    "events/low_health",     stale:30,     cool:5,   fadeintime:2, bypass:true, priority:90, combat:true);
            Add("Blood_Moon_Combat",   "combat/blood_moon",     "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:71, link:"Bloodmoon");
            Add("Vehicle_Gyrocopter",  "vehicle/gyrocopter",    "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_Bicycle",     "vehicle/bicycle",       "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_Minibike",    "vehicle/minibike",      "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_Motorcycle",  "vehicle/motorcycle",    "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_4x4",         "vehicle/4x4",           "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle",             "vehicle",               stale:30,     cool:5,   fadeintime:3, bypass:true, priority:69, warmup:1);
            Add("POI_Cleared",         "events/poi_cleared",    stale:60,                        bypass:true, priority:91, once:true, fadeintime:0);
            // Three combat tiers. The combat/ root folder IS the base (Low) tier; _high and
            // _boss subfolders hold the escalations (underscore-prefixed so the top-level
            // directory scan never confuses them with the per-biome subfolders). Low bridges
            // to vanilla via VanillaIfPanicked. Mirrors contexts.xml.
            Add("Combat_Boss",         "combat/_boss",          "Combat_High",       stale:20, cool:3, fadeintime:3, bypass:true, priority:52);
            Add("Combat_High",         "combat/_high",          "Combat_Low",        stale:20, cool:3, fadeintime:3, bypass:true, priority:51, link:"Combat");
            Add("Combat_Low",          "combat",                "VanillaIfPanicked", stale:20, cool:3, fadeintime:3, bypass:true, priority:50);
            // Biome-specific combat tiers (mirror contexts.xml). Each biome's combat/<biome>
            // folder mirrors the generic layout (root = Low, _high / _boss = escalations).
            // Each variant outranks its generic counterpart so biome combat music wins when
            // its folder has tracks, else falls back to the matching generic tier. The resolver
            // only stacks the variant for the one active tier, so the 60–62 band never inverts.
            Add("Combat_Boss_Burnt_Forest", "combat/burnt_forest/_boss", "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Burnt_Forest", "combat/burnt_forest/_high", "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Burnt_Forest",  "combat/burnt_forest",       "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Desert",       "combat/desert/_boss",       "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Desert",       "combat/desert/_high",       "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Desert",        "combat/desert",             "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Forest",       "combat/forest/_boss",       "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Forest",       "combat/forest/_high",       "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Forest",        "combat/forest",             "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Snow",         "combat/snow/_boss",         "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Snow",         "combat/snow/_high",         "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Snow",          "combat/snow",               "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Wasteland",    "combat/wasteland/_boss",    "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Wasteland",    "combat/wasteland/_high",    "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Wasteland",     "combat/wasteland",          "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Spawn",               "events/spawn",          resume:false, stale:0,  cool:3, fadeintime:0.1f, bypass:true, priority:45, once:true);
            Add("Tension",             "tension",               stale:30,                                      priority:30, warmup:2, link:"Suspense");
            Add("Trader_Joel",         "trader/joel",           "Trader",                                      priority:26, link:"TraderJoel");
            Add("Trader_Jen",          "trader/jen",            "Trader",                                      priority:26, link:"TraderJen");
            Add("Trader_Rekt",         "trader/rekt",           "Trader",                                      priority:26, link:"TraderRekt");
            Add("Trader_Bob",          "trader/bob",            "Trader",                                      priority:26, link:"TraderBob");
            Add("Trader_Hugh",         "trader/hugh",           "Trader",                                      priority:26, link:"TraderHugh");
            Add("Trader",              "trader",                "VanillaIfDMS",                                priority:25);
            Add("Home_Night",          "home/night",            "Home",                                        priority:15, warmup:5, warmgroup:"home", link:"HomeNight");
            Add("Home",                "home",                                                                 priority:15, warmup:5, warmgroup:"home", link:"HomeDay");
            Add("Swimming",            "swimming",                                                            priority:23, warmup:5);
            // A TIER FALLS BACK TO ITS OWN TIER, not to the generic infested bed. An empty
            // poi/infested/t5 now plays the T5 music rather than dropping to poi/infested, which
            // keeps the tier -- the thing the player is actually reading as difficulty -- and only
            // loses the infested colour. It also lengthens the chain, because POI_Tn falls back to
            // POI in turn: t5 -> POI_T5 -> POI. The old chain ended at POI_Infested.
            Add("POI_Infested_T5",     "poi/infested/t5",       "POI_T5",      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T4",     "poi/infested/t4",       "POI_T4",      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T3",     "poi/infested/t3",       "POI_T3",      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T2",     "poi/infested/t2",       "POI_T2",      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T1",     "poi/infested/t1",       "POI_T1",      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T0",     "poi/infested/t0",       priority:21, warmup:10, warmgroup:"poi");
            Add("POI_Infested",        "poi/infested",          priority:20, warmup:10, warmgroup:"poi");
            Add("POI_T5",              "poi/t5",                "POI",        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T4",              "poi/t4",                "POI",        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T3",              "poi/t3",                "POI",        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T2",              "poi/t2",                "POI",        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T1",              "poi/t1",                "POI",        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T0",              "poi/t0",                priority:17, warmup:5, warmgroup:"poi");
            Add("POI",                 "poi",                   priority:16, warmup:5, warmgroup:"poi");
            Add("Ambient_Forest_Night",      "ambient/forest/_night",       "Ambient_Night",             priority:12);
            Add("Ambient_Forest",            "ambient/forest",              "Ambient",                   priority:11);
            Add("Ambient_Burnt_Forest_Night","ambient/burnt_forest/_night", "Ambient_Night",             priority:12);
            Add("Ambient_Burnt_Forest",      "ambient/burnt_forest",        "Ambient",                   priority:11);
            Add("Ambient_Desert_Night",      "ambient/desert/_night",       "Ambient_Night",             priority:12);
            Add("Ambient_Desert",            "ambient/desert",              "Ambient",                   priority:11);
            Add("Ambient_Snow_Night",        "ambient/snow/_night",         "Ambient_Night",             priority:12);
            Add("Ambient_Snow",              "ambient/snow",                "Ambient",                   priority:11);
            Add("Ambient_Wasteland_Night",   "ambient/wasteland/_night",    "Ambient_Night",             priority:12);
            Add("Ambient_Wasteland",         "ambient/wasteland",           "Ambient",                   priority:11);
            Add("Ambient_Night",             "ambient/_night",              "Stack",                     priority:11);
            Add("Ambient",                   "ambient",                    "VanillaIfDMS", fadeintime:4,            priority:10, link:"Exploration");

            // Multiplayer presence. Detection is relationship-only and ignores the server's
            // PvP mode; the warmup dwell keeps someone merely passing through from
            // interrupting. A stranger outranks ambient/home/trader exploration music.
            Add("Online_Stranger",           "testing/online/stranger",    stale:30, priority:29, warmup:3);
            Add("Online_Friend",             "testing/online/friendly",    stale:30, priority:14, warmup:3);

            // The world-emitter / personal-radio source. Nothing auto-activates it: no vanilla
            // link and no detection wires it to a game state, so the resolver never puts it on
            // the stack. It is here because WorldEmitterSource points at it BY NAME, and an
            // install with no contexts.xml would otherwise have the radio aimed at nothing.
            Add("Radio",                     "Radio",                      priority:10, volume:0.5f);
            // Sentinels — no path, no files
            Add("Silence",  "", "Silence",  resume:false, stale:0, cool:0, fadeintime:0, bypass:true, priority:0, link:"None");
            Add("Vanilla",  "", "Vanilla",  resume:false, stale:0, cool:0, fadeintime:0, bypass:true, priority:-1);
            Add("Menu",     "menu", "Vanilla", resume:false, stale:0, cool:5, fadeintime:2, bypass:true, priority:0);

            // Build the vanilla section→context reverse map from the built-in Links (the
            // external contexts.xml load, when present, rebuilds it again with its own),
            // and the combat-context set the mute keybind covers.
            RebuildVanillaLinkMap();
            RebuildCombatContextSet();
        }

        public static Contexts GetContext(string contextIn) =>
            contextIn != null && contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        // Diagnostics for the WarmUp dwell: throttle the per-frame "still warming"
        // spam to ~one line/second per context, and remember which contexts are
        // warming so we can log a single "now viable" line when each one clears.
        private static readonly Dictionary<string, float> _warmLogThrottle =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _warmingNow =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // True once a context has been on the stack long enough to satisfy its WarmUp
        // dwell. WarmUp <= 0 (the default) is always satisfied immediately. A context
        // that left and re-entered gets a fresh FirstSeenAt, so the dwell restarts.
        private static bool DwellSatisfied(ActiveContext active)
        {
            Contexts ctx = GetContext(active.ContextName);
            int warm = ctx?.WarmUp ?? 0;
            if (warm <= 0) return true;

            float now = Time.realtimeSinceStartup;
            float remaining = warm - (now - active.FirstSeenAt);
            if (remaining > 0f)
            {
                // Held back by dwell — this is the usual reason a context that's
                // clearly "active" still isn't playing. Log it (throttled) so it's
                // visible instead of a silent skip.
                if (!_warmLogThrottle.TryGetValue(active.ContextName, out float last) || now - last >= 1f)
                {
                    _warmLogThrottle[active.ContextName] = now;
                    CMMConfig.D($"[CMM] {active.ContextName} warming up — {remaining:F1}s left before it can trigger (WarmUp={warm}s)");
                }
                _warmingNow.Add(active.ContextName);
                return false;
            }

            if (_warmingNow.Remove(active.ContextName))
            {
                _warmLogThrottle.Remove(active.ContextName);
                CMMConfig.D($"[CMM] {active.ContextName} warmup complete — now viable to trigger");
            }
            return true;
        }

        // FirstSeenAt for a context about to join the stack. Normally this is just
        // "now", which (re)starts its WarmUp dwell. But if the context belongs to a
        // WarmGroup and a sibling in that group is already on the stack, inherit the
        // group's OLDEST FirstSeenAt so the dwell carries across the handoff instead
        // of restarting — e.g. a day/night swap (Home -> Home_Night) stays viable
        // continuously rather than dropping out for the full WarmUp and letting a
        // lower context leak through. The outgoing sibling is still present here
        // because stack inserts run before the absence-based eviction each scan.
        internal static float InheritedFirstSeen(string contextName, float now)
        {
            string group = GetContext(contextName)?.WarmGroup;
            if (string.IsNullOrEmpty(group)) return now;
            float oldest = now;
            foreach (ActiveContext ac in _contextStack)
            {
                if (ac.ContextName == contextName) continue;
                if (string.Equals(GetContext(ac.ContextName)?.WarmGroup, group,
                                   StringComparison.OrdinalIgnoreCase))
                    oldest = Mathf.Min(oldest, ac.FirstSeenAt);
            }
            return oldest;
        }

        private static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validation cache: keys are "fullpath|size|mtimeTicks" of files that passed
        // QuickValidate this session. Checked in FileWatchLoop before re-decoding a "new"
        // file, so moving a folder's files out and back (same size+mtime) doesn't
        // re-validate every one. Kept across removals on purpose; a changed file gets a
        // new key and is re-checked. In-memory only (a fresh launch re-validates, though
        // already-measured files short-circuit via LoudnessStore.IsMeasured).
        private static readonly HashSet<string> _validatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracks all contexts currently open on the ResolvePlayableContext call stack.
        // Prevents Combat_High → Combat_Low → (stack walk) → Combat_High mutual recursion.
        private static readonly HashSet<string> _resolvingContexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Combat-music mute (keybind, persisted) ────────────────────────────
        // One switch for ALL combat contexts (Alt+Home by default). While muted,
        // every combat context resolves as if it had no files — the fallback chain
        // and the stack decide what plays through fights instead — and the linked
        // vanilla Combat/Bloodmoon sections are suppressed exactly like a
        // DisabledVanillaSections entry (see VanillaSectionSuppressed), so the
        // Combat_Low → VanillaIfPanicked handoff can't leak the game's own combat
        // music back in. PERSISTED (2026-08-20): the toggle writes CombatMusicMuted back
        // to settings.xml, because "I don't want combat music" is a preference and not a
        // mute-for-now — having to press it again every launch was the complaint. It was
        // session-only before, on the reasoning that contexts.xml and
        // DisabledVanillaSections were the durable knobs; those are per-context surgery,
        // and this is one switch, so it earns its own.
        private static bool _combatMusicMuted;

        // Every context the mute covers: any whose NAME contains "combat"
        // (Combat_Low/High/Boss, the per-biome variants, Blood_Moon_Combat — and any
        // user-defined context following the same naming). Precomputed on every
        // contexts (re)load so the per-tick IsUserDisabled stays a set lookup.
        private static readonly HashSet<string> _combatContexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // TENSION: the same idea for the pre-fight cue. Its own flag and its own set, because
        // the two are muted independently even though one key drives both — see CycleMusicMute.
        private static bool _tensionMusicMuted;
        private static readonly HashSet<string> _tensionContexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool CombatMusicMuted => _combatMusicMuted;
        internal static bool TensionMusicMuted => _tensionMusicMuted;
        internal static int CombatContextCount => _combatContexts.Count;
        internal static int TensionContextCount => _tensionContexts.Count;

        // THE MUTE KEY IS A THREE-STOP CYCLE, not a toggle:
        //
        //     both on  →  combat + tension off  →  combat off  →  both on
        //
        // Tension is combat music that has not started yet, so the FIRST stop is the loud one:
        // someone reaching for this key in a fight wants the fight music gone, and leaving the
        // stalking cue running underneath just moves the noise. The middle stop exists because
        // tension alone is genuinely wanted by some — it is the mood without the drums.
        //
        // A hand-edited file can hold tension-muted-alone, which the cycle never produces. That
        // is a legitimate setting, not a corrupt one, so it is not "repaired" on load; the next
        // keypress simply lands on all-on, the one stop that is unambiguous from anywhere.
        internal static void CycleMusicMute()
        {
            if (!_combatMusicMuted && !_tensionMusicMuted) { _combatMusicMuted = true;  _tensionMusicMuted = true;  }
            else if (_combatMusicMuted && _tensionMusicMuted) { _combatMusicMuted = true;  _tensionMusicMuted = false; }
            else                                              { _combatMusicMuted = false; _tensionMusicMuted = false; }
        }

        // Set them outright, from the loaded config. Separate from the cycle so a settings
        // (re)load cannot advance the state by accident — a load says what the value IS, a
        // keypress says it changed, and conflating the two is how a hot-reload ends up
        // un-muting someone mid-fight.
        internal static void SetCombatMusicMuted(bool muted) => _combatMusicMuted = muted;
        internal static void SetTensionMusicMuted(bool muted) => _tensionMusicMuted = muted;

        internal static bool IsUserDisabled(string contextName) =>
            (_combatMusicMuted && _combatContexts.Contains(contextName))
            || (_tensionMusicMuted && _tensionContexts.Contains(contextName));

        // (Re)derives _combatContexts from the loaded context table. Called wherever
        // RebuildVanillaLinkMap is (InitContexts + CMMConfigLoader.LoadContexts).
        public static void RebuildCombatContextSet()
        {
            _combatContexts.Clear();
            foreach (Contexts ctx in contexts.Values)
            {
                if (ctx.Context == null) continue;
                // The naming convention, plus anything that declared itself. The convention
                // alone missed Low_Health_Panic, which is a fight sting in everything but its
                // name: with combat music off you still got the panic cue mid-fight, which is
                // the one thing the toggle exists to stop.
                if (ctx.Context.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0
                    || _combatMarked.Contains(ctx.Context))
                    _combatContexts.Add(ctx.Context);
            }

            // The tension set is rebuilt HERE rather than behind its own entry point, so no
            // caller can refresh one set and leave the other stale. They are read by the same
            // keypress and must always describe the same contexts table.
            //
            // Name convention only: there is exactly one shipped tension context, and unlike
            // combat — where Low_Health_Panic is a fight sting whose name says otherwise and
            // needed combat="true" — nothing in the table is tension music under another name.
            // If that stops being true, mirror _combatMarked rather than widening the match.
            _tensionContexts.Clear();
            foreach (Contexts ctx in contexts.Values)
            {
                if (ctx.Context == null) continue;
                if (ctx.Context.IndexOf("tension", StringComparison.OrdinalIgnoreCase) >= 0)
                    _tensionContexts.Add(ctx.Context);
            }
        }

        // Contexts that carry combat="true" in contexts.xml. Filled by the loader as it parses,
        // BEFORE RebuildCombatContextSet runs over the finished table, and cleared at the start
        // of every (re)load so a marker removed by hand actually goes away on hot-reload.
        private static readonly HashSet<string> _combatMarked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static void MarkCombatContext(string name)
        {
            if (!string.IsNullOrEmpty(name)) _combatMarked.Add(name);
        }
        public static void ClearCombatMarks() => _combatMarked.Clear();
        // Only the EXPLICIT marks. The writer needs these and not the name-convention
        // matches, or it would stamp combat="true" onto every Combat_* context and the
        // generated file would be noisier than the one it replaces.
        internal static bool IsExplicitCombatContext(string name) => _combatMarked.Contains(name);

        // Walks the fallback chain to find the nearest playable context.
        // Returns "Vanilla" when the chain terminates at a Vanilla fallback.
        // Returns null when the chain terminates at Silence (explicit silence).
        private static string ResolvePlayableContext(string contextName)
        {
            // Re-entry guard: if this context is already open somewhere above us on the
            // call stack, returning null here breaks the cycle rather than overflowing.
            if (_resolvingContexts.Contains(contextName)) return null;
            _resolvingContexts.Add(contextName);
            try
            {
                return ResolvePlayableContextCore(contextName);
            }
            finally
            {
                _resolvingContexts.Remove(contextName);
            }
        }

        private static string ResolvePlayableContextCore(string contextName)
        {
            string current = contextName;
            for (int i = 0; i < 5; i++)
            {
                // A combat context muted by the keybind never plays its own files; it
                // falls through its fallback chain like an empty folder, and any
                // Vanilla handoff it reaches is separately gated by governance
                // (VanillaSectionSuppressed), so its linked section stays quiet too.
                if (!IsUserDisabled(current) && ContextHasFiles(current)) return current;
                Contexts ctx = GetContext(current);
                string fallback = ctx?.Fallback ?? "Silence";
                if (fallback == "Vanilla") return "Vanilla";
                if (fallback == "Stack")
                    return ResolveViaStack();
                // Resolves to Vanilla only when the game's threat detection is Panicked AND the
                // vanilla combat/bloodmoon section that handoff would expose isn't disabled. When
                // it IS disabled the section obeys its linked context's rules: we skip the handoff
                // and walk the rest of the stack, so our own lower-priority music (e.g. a POI
                // theme) keeps playing instead of being torn down for a section we'd only mute.
                // Otherwise falls through to Stack so no music change occurs.
                if (fallback == "VanillaIfPanicked")
                {
                    bool dmsEnabled = GamePrefsCompat.DynamicMusicEnabled();
                    var localPlayer = GameManager.Instance?.World?.GetPrimaryPlayer();
                    bool panicked = dmsEnabled && localPlayer?.ThreatLevel?.Category == ThreatLevelType.Panicked;
                    if (panicked && !PanicHandoffSectionDisabled())
                        return "Vanilla";
                    string stacked = ResolveViaStack();
                    if (stacked != null) return stacked;
                    // Panicked but the vanilla combat section is disabled and nothing else is on
                    // the stack: let the Vanilla handoff stand so governance plays the Exploration
                    // ambient (or silence, if that's disabled too) rather than cutting out here.
                    return panicked ? "Vanilla" : null;
                }
                // Resolves to Vanilla only when DMS is enabled AND the section the handoff would
                // expose isn't disabled. A disabled section obeys its linked context's rules: it
                // falls through to the stack first (so e.g. your own ambient keeps playing at a
                // trader whose vanilla music you turned off), landing on the Vanilla handoff only
                // as a last resort so governance can play Exploration. Otherwise falls through to
                // the stack so ambient/home/etc. keeps playing instead of vanilla.
                if (fallback == "VanillaIfDMS")
                {
                    bool dmsEnabled = GamePrefsCompat.DynamicMusicEnabled();
                    // Every VanillaIfDMS context — including the five traders — hands off to the
                    // warm, muted Conductor via the generic "Vanilla" path: we unmute the DMS
                    // (edge-triggered) and let the game play its own clip.
                    if (dmsEnabled && !ProposedVanillaSectionDisabled())
                        return "Vanilla";
                    string stacked = ResolveViaStack();
                    if (stacked != null) return stacked;
                    return dmsEnabled ? "Vanilla" : null;
                }
                if (fallback == "Silence" || fallback == current) return null;
                current = fallback;
            }
            return null;
        }

        // Walks the active context stack in priority order and returns the first context that
        // resolves to something playable (its own files, or a nested Vanilla/Stack handoff).
        // The shared body of the Stack / VanillaIf* fallbacks. Re-entrancy is bounded by the
        // per-context guard in ResolvePlayableContext.
        private static string ResolveViaStack()
        {
            foreach (ActiveContext active in _contextStack)
            {
                if (!DwellSatisfied(active)) continue;   // still warming up — not yet viable
                string resolved = ResolvePlayableContext(active.ContextName);
                if (resolved != null) return resolved;
            }
            return null;
        }

        private static string GetTopContext()
        {
            foreach (ActiveContext active in _contextStack)
            {
                if (!DwellSatisfied(active)) continue;   // still warming up — not yet viable
                string playable = ResolvePlayableContext(active.ContextName);
                if (playable != null) return playable;
            }
            // Null signals "nothing playable and DMS is off" — caller should fade to
            // silence rather than activating vanilla and releasing the Conductor.
            return GamePrefsCompat.DynamicMusicEnabled() ? "Vanilla" : null;
        }

        // ── Vanilla handoff ───────────────────────────────────────────────────

        public static void ActivateVanilla()
        {
            _pendingContext = "Vanilla";
            vanillaIsActive = true;
            // Drop the governed-section anchor. It was last updated while the Conductor was
            // MUTED under CMM's own music, so it names whatever the game happened to propose
            // back then — nothing the player actually heard. Handing off with that stale
            // anchor let the cmmOwns/warming hold pin audible vanilla to the wrong section
            // for minutes (Trader Rekt, log 17-29-54: anchor poisoned, held ~7 min). None ⇒
            // the game's live proposal stands and re-anchors on the first governed frame.
            _lastGovernedSection = SectionType.None;
            currentContext = GetContext("Vanilla");
            FadeToSilence();
            if (inGame)
            {
                SetVanillaMusicVolume(0f);
                if (_vanillaFadeInCoroutine != null) { CMM_CoRoutines.StopCoroutine(_vanillaFadeInCoroutine); _vanillaFadeInCoroutine = null; }
                _vanillaFadeInCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeInVanillaCoroutine(CrossFadeDuration));
            }
            else
            {
                // A menu-vanilla fade (out-to-muted, or a prior fade-in) may still be in
                // flight (files were added then removed quickly). Cancel it so it can't
                // fight the fade-in we're starting.
                if (_menuVanillaFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_menuVanillaFadeCoroutine); _menuVanillaFadeCoroutine = null; }
                // Fade the vanilla title theme back IN over the crossfade window rather than a
                // bare unmute — an instant unmute blared it back at full volume with no ramp.
                if (Instance != null && CMM_CoRoutines != null)
                    _menuVanillaFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeInMenuVanillaCoroutine(CrossFadeDuration));
                else
                    MenuMusicPatch.SetMenuMusicMuted(false);
            }
            CMMConfig.D($"[CMM] Vanilla activated ({(inGame ? "in-game" : "menu")})");
        }

        public static void DeactivateVanilla()
        {
            vanillaIsActive = false;
            _pendingContext = null;
            if (inGame)
            {
                if (_vanillaFadeInCoroutine != null) { CMM_CoRoutines.StopCoroutine(_vanillaFadeInCoroutine); _vanillaFadeInCoroutine = null; }
                CMM_CoRoutines.StartCoroutine(Instance.FadeOutVanillaCoroutine(CrossFadeDuration));
            }
            else
            {
                // Fade the vanilla menu music out over the crossfade window instead of
                // an instant mute, so it doesn't abruptly cut when CMM's menu music
                // (which fades in over the same window) takes over after files appear.
                // Runner unavailable (e.g. the world-exit prefix during teardown) → hard
                // mute; this runs inside Harmony prefixes, so it must never throw.
                if (Instance != null && CMM_CoRoutines != null)
                {
                    if (_menuVanillaFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_menuVanillaFadeCoroutine); _menuVanillaFadeCoroutine = null; }
                    _menuVanillaFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutMenuVanillaCoroutine(CrossFadeDuration));
                }
                else
                    MenuMusicPatch.SetMenuMusicMuted(true);
            }
            CMMConfig.D("[CMM] Reclaiming from Vanilla.");
        }

        // ── Vanilla section governance ────────────────────────────────────────
        // While CMM lets the game's Dynamic Music System play (the "Vanilla" fallback),
        // the game's Conductor still picks its own sections and would snap straight to
        // e.g. trader music the instant you step into a trader area — with none of CMM's
        // anti-flicker WarmUp dwell, and bleeding the game's trader track through before
        // CMM's own trader music is ready. A vanilla section may only fire when its LINKED
        // CMM context (contexts.xml `link="…"`) says its rules allow it.
        //
        // Reverse map: game vanilla SectionType → the CMM context that governs it, built
        // from each context's Link. Rebuilt on every contexts (re)load (InitContexts +
        // CMMConfigLoader.LoadContexts). Main-thread only, so a plain Dictionary is fine.
        private static readonly Dictionary<SectionType, string> _sectionToContext =
            new Dictionary<SectionType, string>();

        // (Re)builds _sectionToContext by parsing each context's Link string to a game
        // SectionType. Later duplicate links for the same section win with a warning;
        // unparseable link values are logged and skipped. None never needs a mapping.
        public static void RebuildVanillaLinkMap()
        {
            _sectionToContext.Clear();
            foreach (Contexts ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Link)) continue;
                if (!Enum.TryParse(ctx.Link, true, out SectionType section))
                {
                    Log.Warning($"[CMM] Context '{ctx.Context}' has an unknown link=\"{ctx.Link}\" — ignoring.");
                    continue;
                }
                if (section == SectionType.None) continue;   // None = silence, never gated
                if (_sectionToContext.TryGetValue(section, out string existing) && existing != ctx.Context)
                    Log.Warning($"[CMM] Vanilla section {section} linked by both '{existing}' and '{ctx.Context}' — using '{ctx.Context}'.");
                _sectionToContext[section] = ctx.Context;
            }
        }

        // The section the Conductor is actually playing (what we last let through). A hold
        // returns this so the current ambient keeps going rather than cutting to something new.
        // Reset on world exit (Patch_GameEnded → ResetVanillaGovernance) so a stale value can't
        // force a wrong section on the next world's first pick.
        public static SectionType _lastGovernedSection = SectionType.None;
        // Rising-edge guard so the "holding" diagnostic logs once per hold episode instead of
        // every frame (Conductor.Update calls Select() each frame).
        private static SectionType _lastLoggedHold = SectionType.None;
        // Governance-owned WarmUp timer for the section the game is currently proposing. Timed
        // off the GAME's proposal (not CMM's context stack) so a mapped section is held for its
        // linked context's full WarmUp even during the scan-tick window before CMM has
        // registered the context. Without this, the section would slip through that gap and fire
        // before the dwell ("Trader_Rekt fires before the warmup"). Main-thread only.
        private static SectionType _pendingVanillaSection = SectionType.None;
        private static float _pendingVanillaSince;

        // Clears all vanilla-governance state. Called on world exit.
        public static void ResetVanillaGovernance()
        {
            _lastGovernedSection = SectionType.None;
            _lastLoggedHold = SectionType.None;
            _pendingVanillaSection = SectionType.None;
            _gameProposedSection = SectionType.None;
        }

        // The raw section the game's SectionSelector last proposed (before governance rewrites
        // it), cached each frame so the resolution layer can tell whether handing off to vanilla
        // right now would expose a user-disabled section. Set even while CMM owns audio — the
        // Conductor still runs Select every frame — so it stays current. Location-based sections
        // (trader/home/exploration) change slowly enough that this one-frame-old value is safe to
        // read; the fast-changing combat handoff uses PanicHandoffSectionDisabled instead.
        private static SectionType _gameProposedSection = SectionType.None;

        // A vanilla section is suppressed when the user disabled it in settings.xml
        // (DisabledVanillaSections), OR its governing CMM context is covered by the
        // live combat-music mute, OR the personal radio owns the music layer (every
        // vanilla section stays quiet while the radio plays) — all paths behave
        // identically downstream.
        private static bool VanillaSectionSuppressed(SectionType section) =>
            PersonalRadio.SuppressesMusic
            || CMMConfig.DisabledVanillaSections.Contains(section)
            || (_sectionToContext.TryGetValue(section, out string governing) && IsUserDisabled(governing));

        // True when the vanilla section the game currently wants to play is disabled —
        // used by the VanillaIfDMS fallback (its exposed section is location-based).
        private static bool ProposedVanillaSectionDisabled() =>
            _gameProposedSection != SectionType.None
            && VanillaSectionSuppressed(_gameProposedSection);

        // True when the vanilla section a PANIC (combat) handoff would expose is disabled. The
        // game plays Bloodmoon on a blood moon, otherwise Combat. Reads the game's authoritative
        // blood-moon flag directly rather than the cached proposal, so it's already correct on the
        // very frame combat starts and never briefly tears our own music down before the proposal
        // catches up.
        private static bool PanicHandoffSectionDisabled()
        {
            if (CMMConfig.DisabledVanillaSections.Count == 0 && !_combatMusicMuted
                && !_tensionMusicMuted && !PersonalRadio.SuppressesMusic)
                return false;
            World world = GameManager.Instance?.World;
            bool bloodMoon = world != null && world.IsWorldEvent(World.WorldEvent.BloodMoon);
            return VanillaSectionSuppressed(bloodMoon ? SectionType.Bloodmoon : SectionType.Combat);
        }

        // The vanilla section to play in place of a disabled one the game proposed. The game
        // forces trader/home sections by LOCATION even mid-combat (PlayerTracker checks the
        // trader area before threat), so blindly falling to Exploration would drop combat music
        // when you disable a trader you're fighting next to. Instead reproduce what the game
        // would pick from THREAT alone — Combat/Bloodmoon when panicked, Suspense when spooked —
        // skipping anything that's itself disabled, and bottoming out at Exploration or silence.
        // Never returns a trader/home (location) section, so it can't land on another disabled one.
        private static SectionType SituationalVanillaFallback()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            ThreatLevelType threat = player?.ThreatLevel?.Category ?? ThreatLevelType.Safe;

            if (threat == ThreatLevelType.Panicked)
            {
                World world = GameManager.Instance?.World;
                bool bloodMoon = world != null && world.IsWorldEvent(World.WorldEvent.BloodMoon);
                SectionType panic = bloodMoon ? SectionType.Bloodmoon : SectionType.Combat;
                if (!VanillaSectionSuppressed(panic)) return panic;
            }
            else if (threat == ThreatLevelType.Spooked)
            {
                if (!VanillaSectionSuppressed(SectionType.Suspense))
                    return SectionType.Suspense;
            }
            return VanillaSectionSuppressed(SectionType.Exploration)
                ? SectionType.None
                : SectionType.Exploration;
        }

        // Governs the section the game's SectionSelector just picked. Returns what should
        // actually play so a vanilla section obeys the rules of its linked context. Called
        // from a postfix on DynamicMusic.SectionSelector.Select (main thread, same as the
        // scan loop — no locking needed).
        public static SectionType GovernVanillaSection(SectionType proposed)
        {
            _gameProposedSection = proposed;
            SectionType previous = _lastGovernedSection;
            SectionType governed = GovernVanillaSectionCore(proposed, previous);
            _lastGovernedSection = governed;
            return governed;
        }

        private static SectionType GovernVanillaSectionCore(SectionType proposed, SectionType previous)
        {
            if (!inGame || MusicStates.DefaultMusicEnabled) return proposed;  // CMM off → game controls

            // The game's Conductor autonomously proposed a section the user disabled. By the time
            // we reach here CMM has already decided to let vanilla play: the VanillaIf* fallbacks
            // divert to the CMM stack first whenever the exposed section is disabled (so a POI
            // theme or your own ambient is kept instead), which means nothing on the stack claimed
            // this moment. With no CMM music available, substitute the vanilla section the
            // situation actually calls for — SituationalVanillaFallback reproduces the game's
            // THREAT choice (combat/bloodmoon/suspense), so disabling e.g. a trader you're fighting
            // next to still yields combat music instead of dropping to Exploration. Checked ahead
            // of the link map so it applies whether or not the section has a linked CMM context.
            if (proposed != SectionType.None && VanillaSectionSuppressed(proposed))
            {
                SectionType fallback = SituationalVanillaFallback();
                if (_lastLoggedHold != proposed)
                {
                    _lastLoggedHold = proposed;
                    CMMConfig.D($"[CMM] Vanilla section {proposed} disabled — falling back to {fallback}.");
                }
                _pendingVanillaSection = SectionType.None;
                return fallback;
            }

            // Vanilla is the ACTIVE music layer: CMM's own resolution already ran and chose it,
            // so nothing on the CMM stack claimed this moment. The warming/cmmOwns hold below
            // exists to keep the game's section inaudible while CMM plays (or is about to);
            // applying it while vanilla is audible instead pins the Conductor to the anchor —
            // wrong music indefinitely when CMM never claims the linked context (the Trader
            // Rekt hold). Let the game's live proposal stand; if CMM claims the context later,
            // DeactivateVanilla's crossfade covers the takeover. Suppressed sections are still
            // rewritten above.
            if (vanillaIsActive)
            {
                _pendingVanillaSection = SectionType.None;
                _lastLoggedHold = SectionType.None;
                return proposed;
            }

            // Which CMM context governs this vanilla section? Unlinked (or None) → defer to the
            // game, and forget any in-flight warmup so the next mapped section starts fresh.
            if (!_sectionToContext.TryGetValue(proposed, out string ctx))
            {
                _pendingVanillaSection = SectionType.None;
                _lastLoggedHold = SectionType.None;
                return proposed;
            }

            // Already settled on this section (or nothing to hold onto after a world load) —
            // let it stand and keep the dwell anchored to the live section.
            if (proposed == previous || previous == SectionType.None)
            {
                _pendingVanillaSection = proposed;
                _lastLoggedHold = SectionType.None;
                return proposed;
            }

            // Time the linked context's WarmUp dwell from the GAME's first continuous proposal
            // of this section — robust to the lag before CMM's scan registers the context.
            float now = Time.realtimeSinceStartup;
            if (_pendingVanillaSection != proposed) { _pendingVanillaSection = proposed; _pendingVanillaSince = now; }
            int warm = GetContext(ctx)?.WarmUp ?? 0;
            bool warming = (now - _pendingVanillaSince) < warm;   // linked context still in its WarmUp dwell

            // CMM will play its OWN track for this context, so the game's version must never be
            // heard (keeps holding through the handoff crossfade). Traders also fall back to the
            // generic "Trader" folder.
            bool cmmOwns = ContextHasFiles(ctx)
                || (ctx.StartsWith("Trader_", StringComparison.OrdinalIgnoreCase) && ContextHasFiles("Trader"));

            if (warming || cmmOwns)
            {
                if (_lastLoggedHold != proposed)
                {
                    _lastLoggedHold = proposed;
                    CMMConfig.D($"[CMM] Vanilla section governed: holding {previous} instead of {proposed} " +
                                $"(linked {ctx}: warming={warming}, cmmOwns={cmmOwns})");
                }
                return previous;
            }
            _lastLoggedHold = SectionType.None;
            return proposed;   // warmup elapsed and CMM has no music for it → fire the vanilla section
        }

        // Cached reflection handles for MixerController — resolved once on first use.
        private static MethodInfo _setDMVMethod;
        private static object _mixerInstance;
        private static volatile bool _mixerResolved;
        private static float _currentVanillaVolume = 0f;
        // True only while SetVanillaMusicVolume is invoking the game's SetDynamicMusicVolume,
        // so Patch_DmsVolumeReclamp can tell CMM's own dmsVol writes (mute + vanilla fade
        // ramps) from the game's slider/snapshot re-applies, which it re-clamps to muted.
        // Main-thread only, so a plain static is sufficient.
        internal static bool _ownDmsWrite;

        public static void SetVanillaMusicVolume(float vol, bool force = false)
        {
            // Only push to the game's DMS mixer when the value actually changes. The mute is
            // edge-triggered now (asserted on world entry, ramped by the vanilla fade
            // coroutines) rather than re-asserted every Conductor.Update frame, so skip
            // redundant identical writes — but always proceed on the first call so the mixer
            // reflection handles get resolved and the initial mute is applied. `force`
            // bypasses the skip when the game has clobbered dmsVol out from under our cache
            // (music-slider / snapshot re-apply); see Patch_DmsVolumeReclamp.
            if (!force && _mixerResolved && vol == _currentVanillaVolume) return;
            _currentVanillaVolume = vol;
            if (!_mixerResolved)
            {
                _mixerResolved = true;
                var t = typeof(DynamicMusic.Conductor).Assembly.GetType("DynamicMusic.MixerController");
                _mixerInstance = t?.GetField("instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null);
                _setDMVMethod = t?.GetMethod("SetDynamicMusicVolume",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_setDMVMethod == null)
                    Log.Warning("[CMM] MixerController.SetDynamicMusicVolume not found — Vanilla volume control unavailable.");
                else
                    CMMConfig.D($"[CMM] MixerController resolved — instance={_mixerInstance != null} method={_setDMVMethod.Name}");
            }
            if (_setDMVMethod != null && _mixerInstance != null)
            {
                // Mark this as OUR write so Patch_DmsVolumeReclamp ignores it — otherwise the
                // postfix would clamp our own vanilla fade ramps (and re-clamp) back to 0.
                _ownDmsWrite = true;
                try { _setDMVMethod.Invoke(_mixerInstance, new object[] { vol }); }
                finally { _ownDmsWrite = false; }
            }
        }

        // ── Cache cleanup ─────────────────────────────────────────────────────
        // Stops playback and resets the per-context selection/bookkeeping state
        // before the menu screen loads. Immediate (hard-cut) form — the
        // game-exit path uses FadeOutAndCleanup() to fade first; this remains for the
        // no-runner fallback and any caller that genuinely wants an instant teardown.
        public static void CleanupAllCaches()
        {
            StopMusicCoroutines();
            StopNativeIfActive(0f);
            DisposeAllCaches();
        }

        // ── Game-exit graceful teardown ───────────────────────────────────────
        // Fades the playing channel(s) to silence over the crossfade window, THEN
        // releases the caches and brings up the menu music — so returning to the menu
        // fades out instead of hard-cutting (CMM_Audio_Object is DontDestroyOnLoad, so
        // the fade coroutine survives the world unload). Falls back to the immediate
        // teardown when no coroutine runner is available.
        public static void FadeOutAndCleanup()
        {
            // Stop the in-flight transition coroutines now (not inside the fade): the
            // fade below is itself assigned to _crossFadeCoroutine, both so a second
            // exit can cancel it and so VolumeSyncLoop stays suppressed while it runs.
            StopMusicCoroutines();

            if (Instance == null || CMM_CoRoutines == null)
            {
                StopNativeIfActive(0f);
                DisposeAllCaches();
                SendContextUpdate("Menu");
                return;
            }

            // Hold off the menu-screen auto-trigger (MenuMusicPatch) so the menu music can't
            // fade in until the game music has fully faded out (cleared at the end of the coroutine).
            _menuMusicHold = true;
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutAndCleanupCoroutine());
        }

        private System.Collections.IEnumerator FadeOutAndCleanupCoroutine()
        {
            // Quit-to-menu: CONTINUE the in-game-menu fade to silence rather than restarting or
            // hard-cutting it. Entering the ESC menu already ramped the output gain toward 0 over
            // PauseFadeDuration (the pause duck); re-asserting the same target here keeps it heading
            // to 0 from wherever it currently is (a no-op if it already reached 0, a smooth finish if
            // the player quit mid-fade). If we somehow got here without a pause having engaged (e.g. a
            // disconnect that never paused), this simply starts the same graceful fade. The fade is
            // CONSUMER-side (output gain in the audio callback), so it completes in real time even
            // through the world-unload main-thread freeze.
            CmmMixer.Instance?.SetOutputGain(0f, PauseFadeDuration);

            // Wait out the fade (real post-freeze frame time — Time.deltaTime doesn't advance while
            // the main thread is frozen, and the consumer fade runs in real time regardless), so the
            // music is fully silent before we tear the voices down and the menu track starts over
            // silence rather than during the freeze. _menuMusicHold keeps the menu auto-trigger
            // (MenuMusicPatch) suppressed until we release it below.
            float elapsed = 0f;
            while (elapsed < PauseFadeDuration) { elapsed += Time.deltaTime; yield return null; }

            // Silent now — hard-stop the faded-out voices and release caches. Menu Play opens/reuses
            // the device and snaps the output gain back to full (ResetGain), so the menu track is
            // audible even though the fade left the output gain at 0.
            StopNativeIfActive(0f);
            CmmMixer.Instance?.StopAllNow();
            DisposeAllCaches();
            // Clear the pause-duck edge state — we're leaving the world, and a stale _pauseDucked
            // would make the NEXT session's first ESC a no-op (the edge guard sees paused==true).
            _pauseDucked = false;
            _crossFadeCoroutine = null;

            _menuMusicHold = false;
            SendContextUpdate("Menu");
        }

        // Stop any in-flight music coroutines so none re-triggers a read (or a fresh
        // play) on a reader we're about to dispose.
        private static void StopMusicCoroutines()
        {
            if (_trackEndCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);   _trackEndCoroutine   = null; }
            if (_crossFadeCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);  _crossFadeCoroutine  = null; }
        }

        // Reset per-context cache state on teardown. The native mixer owns its own audio
        // buffers (freed by StopNativeIfActive / StopAllNow), so there are no Unity clips
        // or readers to dispose here anymore — just clear the path/selection bookkeeping.
        private static void DisposeAllCaches()
        {
            foreach (var cache in cacheMap.Values)
            {
                cache.directorySnapshot = null;
                cache.lastPlayedFile = null;
                cache.stagedFile = null;
                cache.resumePosition = 0f;
                cache.lastLeftTime = -1f;
            }

            _invalidFiles.Clear();
            _validatedFiles.Clear();
            _contextsWithFilesDirty = true;
        }

        // ── Manual push/pop ───────────────────────────────────────────────────

        public static void PushContext(string contextName)
        {
            if (_contextStack.All(c => c.ContextName != contextName))
            {
                Contexts ctx = GetContext(contextName);
                if (ctx == null) return;
                if (ResolvePlayableContext(contextName) == null) return;
                float nowPush = Time.realtimeSinceStartup;
                _contextStack.Add(new ActiveContext
                {
                    ContextName = contextName,
                    Priority = ctx.Priority,
                    ActivatedAt = nowPush,
                    FirstSeenAt = InheritedFirstSeen(contextName, nowPush),
                    Manual = true
                });
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            SendContextUpdate(GetTopContext(), 0);
        }

        public static void PopContext(string contextName)
        {
            _contextStack.RemoveAll(c => c.ContextName == contextName);
            _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            SendContextUpdate(GetTopContext(), 0);
        }
    }
}
