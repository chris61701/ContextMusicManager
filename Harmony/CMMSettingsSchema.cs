using System;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // THE SETTINGS SCHEMA — the single description of every setting CMM has.
    //
    // Before this existed a setting lived in three disconnected places: the field
    // in CMMConfig.cs, the ReadX line in CMMConfigLoader.cs, and the element plus
    // its comment in Config/settings.xml. Nothing tied them together, so a key
    // could sit in the XML with no reader at all and silently do nothing, and a
    // shipped value could drift away from its own C# default — 13 of them had
    // (see f6afaf9), which would have become 13 silent behaviour changes the
    // moment the file stopped shipping.
    //
    // Now this table is the source of truth. The loader reads through it and the
    // writer emits the file from it, so the documentation, the default and the
    // parsing can no longer disagree.
    //
    // ORDER IS THE FILE'S LAYOUT. Entries are emitted in the order they appear
    // here, comments and blank lines included, so this reads like the file it
    // produces.
    //
    // ADDING A SETTING: add the field to CMMConfig, add a line here with
    //   since: <CurrentSchema + 1>, and bump CurrentSchema. Existing users get it
    //   backfilled into their file on the next launch.
    // CHANGING A DEFAULT: bump CurrentSchema, pass the new value as the default
    //   and record the old one with changedIn:/was:. Users still sitting on the
    //   old default get the new one; users who customised it keep theirs. Without
    //   that record the change reaches nobody who already has a file.
    // ─────────────────────────────────────────────────────────────────────────
    internal enum SettingKind { Bool, Int, Float, String, Enum }

    internal sealed class SettingDef
    {
        public string      Name;
        public SettingKind Kind;
        // "CMMConfig.Foo" / "ContextMusicManager.Bar", or NULL for a setting the
        // loader reads by hand because it has no single backing field (MusicPath).
        public string      Target;
        public object      Default;
        public int         Since;              // schema version it first shipped in
        public int         DefaultChangedIn;   // 0 = the default has never changed
        public object      WasDefault;         // the default in force before that change
    }

    // One item of the generated file: a comment block, a setting, or a literal
    // chunk of XML (the DisabledVanillaSections block, which has children).
    internal sealed class SchemaEntry
    {
        public string     Comment;
        public SettingDef Setting;
        public string     Raw;
        // All three above null and this true = a blank line in the written file.
        // The groupings in the shipped files are load-bearing readability, so they
        // are part of the schema rather than an accident of source formatting.
        public bool       Gap;
    }

    internal static class CMMSettingsSchema
    {
        // Bump whenever a setting is added or a default changes. Stamped into the
        // written file as <CMMSettings schema="N"> and read back to decide what to
        // backfill and which default changes still apply.
        internal const int CurrentSchema = 25;

        // ── entry builders (keep the table below readable) ────────────────────
        static SchemaEntry Cmt(string text) => new SchemaEntry { Comment = text };
        static SchemaEntry Raw(string xml)  => new SchemaEntry { Raw = xml };
        static SchemaEntry Gap()            => new SchemaEntry { Gap = true };

        static SchemaEntry Mk(string name, SettingKind kind, string target, object def,
                              int since, int changedIn, object was)
            => new SchemaEntry { Setting = new SettingDef {
                   Name = name, Kind = kind, Target = target, Default = def,
                   Since = since, DefaultChangedIn = changedIn, WasDefault = was } };

        static SchemaEntry B  (string n, string t, bool   d, int since = 1, int changedIn = 0, object was = null)
            => Mk(n, SettingKind.Bool,   t, d, since, changedIn, was);
        static SchemaEntry I  (string n, string t, int    d, int since = 1, int changedIn = 0, object was = null)
            => Mk(n, SettingKind.Int,    t, d, since, changedIn, was);
        static SchemaEntry F  (string n, string t, float  d, int since = 1, int changedIn = 0, object was = null)
            => Mk(n, SettingKind.Float,  t, d, since, changedIn, was);
        static SchemaEntry Str(string n, string t, string d, int since = 1, int changedIn = 0, object was = null)
            => Mk(n, SettingKind.String, t, d, since, changedIn, was);
        static SchemaEntry E  (string n, string t, string d, int since = 1, int changedIn = 0, object was = null)
            => Mk(n, SettingKind.Enum,   t, d, since, changedIn, was);

        // ═══ Config/settings.xml — root <CMMSettings> ════════════════════
        internal static readonly SchemaEntry[] Settings =
        {

            Gap(),
            Cmt(@"How loud CMM tracks play relative to the game's music slider (0.0–1.0)"),
            F("VolumeModifier",                      "CMMConfig.VolumeModifier",                    0.5f),

            Gap(),
            Cmt(@"Also read music from every OTHER installed mod's Music folder, not just the one
MusicPath names. That is what lets a soundtrack ship as its own mod instead of being
copied over CMM's install. Packs mirror the same tree, so a pack supplying POI_T3 has
Music/poi/t3/. `cmm roots` shows which folders are in use; `cmm scan` looks again after
adding one. false = use MusicPath and nothing else."),
            B("AddonMusicScanEnabled",                "CMMConfig.AddonMusicScanEnabled",             true, since: 2),

            Gap(),
            Cmt(@"Where to look for music. Relative paths start from the mod folder; absolute paths go anywhere.
Relative example:  <MusicPath>Music</MusicPath>  (default — resolves to ..\Mods\ContextMusicManager\Music)
Absolute example:  <MusicPath>D:\MyMusic\7Days</MusicPath>"),
            Str("MusicPath",                           null,                                          @"Music"),

            Gap(),
            Cmt(@"── Loudness normalization (EBU R128) ──────────────────────────────
When enabled, each track is measured once (integrated LUFS) and played back
at a per-track gain so quiet and loud tracks sit at a consistent perceived
level. Measurements are cached in Cache/loudness.xml (keyed by file content,
so renames/moves keep their value). A track is measured on demand the first
time it's needed (plus a look-ahead of the current context's next track); on
the menu, one track per context is pre-measured so each context has a ready,
normalized track the first time it's switched to.
  LoudnessTargetLufs  – reference level. Sits below 0 so most corrections are
                        attenuations; AudioSource volume can't exceed 1.0, so
                        very quiet tracks boost only as far as VolumeModifier
                        leaves headroom (≈2× at the 0.5 default).
  LoudnessMaxBoostDb  – cap on how much a quiet track may be boosted (dB).
                        Attenuation is uncapped."),
            B("LoudnessNormalizationEnabled",        "CMMConfig.LoudnessNormalizationEnabled",      true),
            F("LoudnessTargetLufs",                  "CMMConfig.LoudnessTargetLufs",                -18f),
            F("LoudnessMaxBoostDb",                  "CMMConfig.LoudnessMaxBoostDb",                12f),
            Cmt(@"MeasureAhead: how many upcoming tracks to keep loudness-measured AHEAD of playback, per source
(the context player and the world-emitter radio each keep this many ready). Higher = more tracks
pre-normalized so nothing decodes on-demand while playing (smoother on huge / slow-storage
libraries), at a little more background decoding. Default 3. Hot-reloads."),
            I("LoudnessStartHoldMs",                 "CMMConfig.LoudnessStartHoldMs",               3000, since: 17),
            I("LoudnessMeasureAhead",                "CMMConfig.LoudnessMeasureAhead",              3),

            Gap(),
            Cmt(@"Fade between outgoing and incoming context instead of cutting immediately"),
            B("CrossfadeEnabled",                    "CMMConfig.CrossfadeEnabled",                  true),
            F("CrossFadeDuration",                   "ContextMusicManager.CrossFadeDuration",       2f),

            Gap(),
            Cmt(@"When re-entering a context that is still audibly fading out, reverse that fade
(ramp it back up from where it is) instead of reloading the track and fading in
from zero. false = the older reload-from-resume behaviour."),
            B("ReverseFadeReclaim",                  "CMMConfig.ReverseFadeReclaim",                true),

            Gap(),
            Cmt(@"Fade duration when a track ends naturally and transitions to the next"),
            F("TrackTransitionFadeDuration",         "ContextMusicManager.TrackTransitionFadeDuration", 5f),

            Gap(),
            Cmt(@"How often to scan for active context changes (seconds)"),
            F("ContextScanCooldown",                 "ContextMusicManager.ContextScanCooldown",     0.1f),
            Cmt(@"How often to check whether cached context data is stale (seconds)"),
            F("StalenessCheckInterval",              "ContextMusicManager.StalenessCheckInterval",  5f),
            Cmt(@"How often to poll for natural track-end detection (seconds)"),
            F("TrackEndPollInterval",                "ContextMusicManager.TrackEndPollInterval",    0.25f),

            Gap(),
            Cmt(@"Track pick order within a context folder: Random, RandomNoRepeat, Sequential"),
            E("GlobalTrackOrder",                    "ContextMusicManager.GlobalTrackOrder",        @"RandomNoRepeat"),
            Cmt(@"How many recently played tracks a context refuses to repeat (RandomNoRepeat only).

  -1  every track in the folder plays once before any track plays again. When
      it runs out the folder reshuffles and a new pass begins. This is what
      makes a big library actually FEEL big, and it is the default.
   0  only the track that just played is avoided. This was the old behaviour:
      on a 200-track folder a song could still come back almost immediately.
   N  remember the last N tracks and avoid those.

Counted per context and per session -- it starts fresh each launch, and each
context keeps its own pass. Adding music makes the new tracks eligible at once;
removing some does not disturb the pass. Favourited tracks get FavouriteWeight
turns per pass instead of one, so favouriting still means something."),
            I("TrackNoRepeatMemory",                 "CMMConfig.TrackNoRepeatMemory",               -1, since: 3),

            Gap(),
            Cmt(@"── Disable parts of the game's Dynamic Music System (DMS) ───────────
Switch off individual vanilla DMS sections (true = disabled). A disabled
section hands back to your CMM music instead of the game's — falling to
vanilla Exploration, or silence if that's off too, only when you have
nothing for it. Never affects your own CMM tracks. Hot-reloaded."),
            Raw(@"<DisabledVanillaSections>
  <Exploration>false</Exploration>
  <Suspense>false</Suspense>
  <Combat>false</Combat>
  <Bloodmoon>false</Bloodmoon>
  <HomeDay>false</HomeDay>
  <HomeNight>false</HomeNight>
  <TraderJoel>false</TraderJoel>
  <TraderJen>false</TraderJen>
  <TraderRekt>false</TraderRekt>
  <TraderBob>false</TraderBob>
  <TraderHugh>false</TraderHugh>
</DisabledVanillaSections>"),

            Gap(),
            Cmt(@"── Context detection tuning ───────────────────────────────────────
Max distance (game units) an enemy is counted toward combat/tension state."),
            F("EnemyDetectRange",                    "CMMConfig.EnemyDetectRange",                  30f),
            Cmt(@"Multiplayer: max distance (game units) another player counts as ""with you"".
An enemy aggroed onto a party member / friend within this range counts as your
combat, and allies within it scale combat intensity. No effect in single-player."),
            F("PlayerDetectRange",                   "CMMConfig.PlayerDetectRange",                 30f),
            Cmt(@"Multiplayer squad-shared combat: how close (game units) another player must be for
an enemy fighting THEM to put you into combat too. A separate radius from
PlayerDetectRange (which governs the ally/stranger proximity counts), so ""sharing a
squadmate's fight"" and ""who counts as near you"" are tuned independently.
CombatShareRequireAlly: when true, only party members / ACL friends share combat;
set false to share combat with ANY nearby player. No effect in single-player."),
            F("CombatShareRange",                    "CMMConfig.CombatShareRange",                  30f),
            B("CombatShareRequireAlly",              "CMMConfig.CombatShareRequireAlly",            false),
            Cmt(@"Distance (game units) from your bedroll or land claim that counts as ""home""."),
            F("HomeDetectRange",                     "CMMConfig.HomeDetectRange",                   70f),
            Cmt(@"Health fraction (0.0–1.0) below which combat escalates to Low_Health_Panic."),
            F("PlayerHealthDangerThresholdPercent",  "CMMConfig.PlayerHealthDangerThresholdPercent", 0.3f),
            Cmt(@"CombatExitDelay: with no nearby aggro for this many seconds, stop playing combat
music. CombatCooldown: how long after that the encounter's tier stays remembered —
re-aggro within this window resumes the latched intensity instead of restarting at
the bottom tier; past it the next fight starts fresh."),
            F("CombatExitDelay",                     "CMMConfig.CombatExitDelay",                   2f),
            F("CombatCooldown",                      "CMMConfig.CombatCooldown",                    10f),

            Gap(),
            Cmt(@"Enemy-count thresholds, passed to the decision script as cfg.combatLowThreshold
/ cfg.combatHighThreshold. The default context.lua tiers combat via its own
weighted scoring (COMBAT_TIERS / TYPE_WEIGHTS / STATE_MULTS) and ignores these,
so they only take effect in a custom script that reads them."),
            I("CombatLowEnemyThreshold",             "CMMConfig.CombatLowEnemyThreshold",           1),
            I("CombatHighEnemyThreshold",            "CMMConfig.CombatHighEnemyThreshold",          4),

            Gap(),
            Cmt(@"── Playback keybinds ───────────────────────────────────────────────
Live controls for whatever music is playing (menu or in-game):
  Next / Prev track  – skip within the current context's folder, music-player
                       style: each context keeps a play history (25 deep, this
                       session). Prev steps back through what actually played
                       (nothing happens at the very start of the history); Next
                       replays forward through anything you rewound, then picks
                       fresh. Manual skips crossfade at ManualSkipFadeDuration,
                       not the context's own fade times.
  Volume up / down   – nudges the CURRENT track's per-track volume trim
                       (saved in Cache/loudness.xml, persists across sessions).
                       Only audible while LoudnessNormalizationEnabled is true.
  Ban track          – the current track is skipped and never picked again
                       (saved in Cache/loudness.xml; press again while it
                       plays — e.g. after a manual Prev — to unban). Banning
                       EVERY track in a folder mutes that context: it falls
                       through its fallback chain as if the folder were empty
                       (only Prev can still replay a banned track, so you can
                       reach one to unban it).
  Undo ban           – unbans the most recently banned track; press again to
                       walk further back (up to the last 25 bans from this
                       session).
  Favourite track    – the current track is picked FavouriteWeight× more
                       often in the random orders (saved in Cache/loudness.xml;
                       press again to remove). Sequential order ignores it.
  Now Playing        – a TOGGLE that shows/hides a persistent top-left
                       readout (independent of the debug HUD): the current
                       track's title, its context, time remaining/total,
                       and the track's volume trim %. While vanilla music
                       or silence plays it stays up and says so. Keybind
                       notices (ban, favourite, combat, ...) appear on its
                       second line — with the readout hidden, the box pops
                       up briefly just for them. NowPlayingEnabled below
                       picks the state at launch.
  Combat music       – a three-stop CYCLE, not an on/off toggle:
                         1st press – combat AND tension music off
                         2nd press – combat off, tension back on
                         3rd press – both back on
                       Combat covers the Combat tiers, their biome variants
                       and Blood_Moon_Combat — any context whose name
                       contains ""combat"", plus anything marked
                       combat=""true"". Tension covers any context whose
                       name contains ""tension"". Tension is the pre-fight
                       stalking cue, which is why one key covers both:
                       muting fights while it still plays just moves the
                       noise. While muted those contexts resolve through
                       their fallback chains as if empty and the game's own
                       linked sections (Combat/Bloodmoon, Suspense) are held
                       back too, so a fight keeps whatever was playing.
                       SAVED: pressing it writes CombatMusicMuted and
                       TensionMusicMuted below, so the choice survives a
                       restart. Set those here and the game starts muted
                       with no keypress at all. (A mod UPDATE replaces this
                       file and resets them to the shipped defaults.)
Keys use Unity KeyCode names (RightArrow, Delete, Home, F7, Keypad6, ...).
By default all of these require Alt held so they don't collide with game or
chat input, and none sit on letters — the game binds letters (D = strafe,
F = flashlight) and they'd fire while playing. Ctrl is NOT used because
it's the game's crouch toggle. Set the Require flags as you like for other
bindings."),
            B("KeybindsEnabled",                     "CMMConfig.KeybindsEnabled",                   true),
            B("KeybindRequireCtrl",                  "CMMConfig.KeybindRequireCtrl",                false),
            B("KeybindRequireAlt",                   "CMMConfig.KeybindRequireAlt",                 true),
            E("KeyNextTrack",                        "CMMConfig.KeyNextTrack",                      @"RightArrow"),
            E("KeyPrevTrack",                        "CMMConfig.KeyPrevTrack",                      @"LeftArrow"),
            E("KeyVolumeUp",                         "CMMConfig.KeyVolumeUp",                       @"UpArrow"),
            E("KeyVolumeDown",                       "CMMConfig.KeyVolumeDown",                     @"DownArrow"),
            E("KeyBanTrack",                         "CMMConfig.KeyBanTrack",                       @"Delete"),
            E("KeyUndoBan",                          "CMMConfig.KeyUndoBan",                        @"End"),
            E("KeyFavouriteTrack",                   "CMMConfig.KeyFavouriteTrack",                 @"Insert"),
            E("KeyToggleCombatMusic",                "CMMConfig.KeyToggleCombatMusic",              @"Home"),
            Cmt(@"Combat music muted at launch. The Alt+Home toggle rewrites this line when you press
it; edit it by hand for the same effect. true = fights keep whatever music was
already playing, and the game's own Combat/Bloodmoon sections stay held back too."),
            B("CombatMusicMuted",                    "CMMConfig.CombatMusicMuted",                  false),
            Cmt(@"Tension music muted at launch. Alt+Home drives this together with CombatMusicMuted:
one press mutes combat AND tension, a second leaves only combat muted, a third
restores both. Tension is the pre-fight stalking cue, so muting fights while it
still plays only moves the noise — that is why one key covers the pair. Set this
true on its own (the key never produces that combination) to silence ONLY tension."),
            B("TensionMusicMuted",                   "CMMConfig.TensionMusicMuted",                 false, since: 18),
            E("KeyNowPlaying",                       "CMMConfig.KeyNowPlaying",                     @"Keypad7"),
            Cmt(@"Context HUD: a top-left overlay saying WHAT is playing and WHY — the resolved
context tokens (red where a context has no files), the fallback chain actually
walked to reach the playing track, the file and folder, the decision script in
use, and how many tracks are staged and loudness-measured. The key cycles
hidden -> compact -> expanded; expanded adds whether the playing track started
pre-normalized and the next-track readiness of every other context. Purely a
readout: it never influences playback. The reset key re-arms the HUD's memory
and GC gauge from a clean baseline, and only does anything while it is open."),
            Cmt(@"TESTING AID. true = silence the GAME's own audio (footsteps, door slams, ambience,
vanilla music) while CMM's world-emitter speakers and its own music keep playing.
They render through CMM's native mixer, which does not pass through the game's
listener, so this separates the two. Intended for judging emitter behaviour by ear
against loud nearby sound effects; leave it false for normal play."),
            B("DebugMuteGameAudio",                   "CMMConfig.DebugMuteGameAudio",                false, since: 23),
            E("KeyContextHud",                       "CMMConfig.KeyContextHud",                     @"Keypad8", since: 22),
            E("KeyContextHudReset",                  "CMMConfig.KeyContextHudReset",                @"Keypad9", since: 22),
            Cmt(@"TESTING AID. Dumps the acoustic column -- every block between your ear and the
speaker, with its loss, openness, material and whether it blocks movement -- to the
log, from wherever you are standing. Same report as the console's `cmm ac col`, but
without opening the console, which means letting go of the movement keys and
sliding off the spot you were trying to describe."),
            E("KeyAcousticsDump",                    "CMMConfig.KeyAcousticsDump",                  @"Keypad5", since: 24),
            Cmt(@"Personal radio dial (in-game only, works on EVERY game version): the two keys step
through Off → station → station → … → Off, wrapping both ways, so they give you
on, off, next and previous with just PgUp/PgDn. Stations are the subfolders of the
Radio source below (same stations the world radios broadcast); your radio plays the
same world-clock broadcast, non-spatial, replacing the normal music while on. The
tuned station and on/off state persist across sessions (Cache/radio.xml). A station
whose folder is removed goes silent (""off air"") and resumes when it returns."),
            E("KeyRadioNext",                        "CMMConfig.KeyRadioNext",                      @"PageUp"),
            E("KeyRadioPrev",                        "CMMConfig.KeyRadioPrev",                      @"PageDown"),
            Cmt(@"Whether the Now Playing overlay starts visible (the key toggles it live)."),
            B("NowPlayingEnabled",                   "CMMConfig.NowPlayingEnabled",                 false),
            Cmt(@"Per-press change to the current track's volume trim (linear factor)."),
            F("UserTrimStep",                        "CMMConfig.UserTrimStep",                      0.1f),
            Cmt(@"How much more often a favourited track is picked. 1 = no preference.

Scaled to the size of the folder, so it means the same thing whatever the size of
your library: at 3 a favourite comes round roughly once an hour of play, in a
50-track folder and in a 5000-track one alike. Without that scaling the same number
meant ""every 45 minutes"" in a small folder and ""every 19 hours"" in a big one,
which made the favourite key close to useless in exactly the libraries it matters
most in. Folders smaller than 50 tracks are unaffected — it never scales down.
Raise it for more, lower it toward 1 for less. Sequential order ignores favourites."),
            F("FavouriteWeight",                     "CMMConfig.FavouriteWeight",                   3f),
            Cmt(@"Crossfade for the MANUAL next/prev/ban skips, in seconds — a keypress switches
this fast regardless of the context's fadeintime. Automatic advances and
context changes keep using TrackTransitionFadeDuration / the context fades."),
            F("ManualSkipFadeDuration",              "CMMConfig.ManualSkipFadeDuration",            0.25f),

            Gap(),
            Cmt(@"── Native audio output ring buffer ─────────────────────────────────
Music plays through CMM's OWN audio output + mixer thread (miniaudio,
in CMM_Decoder.dll), so decode, mixing and output all run on threads
CMM owns, off Unity's main and audio threads. It plays to the same
default device and shares the game's music-volume slider.
  NativeOutputRingMs – tight output cushion (ms) kept in normal play; also
                       the volume/crossfade latency, so kept small (~200ms).
  NativeOutputMaxRingMs – ceiling (ms) the cushion can ADAPTIVELY grow to
                       when the CPU is starved (world-gen / other apps) or
                       while loading, then snaps back tight on a context
                       change. 15000 ≈ 5.8 MB native ring."),
            I("NativeOutputRingMs",                  "CMMConfig.NativeOutputRingMs",                200),
            I("NativeOutputMaxRingMs",               "CMMConfig.NativeOutputMaxRingMs",             15000),

            Gap(),
            Cmt(@"Run periodic housekeeping on CMM's own scheduler threads instead of as Unity
coroutines, marshalling back to the main thread only for the parts that must
touch game state. Lets that work keep ticking through a main-thread freeze
(world load/unload). false = the original coroutine paths."),
            B("NativeScheduler",                     "CMMConfig.NativeScheduler",                   true),

            Gap(),
            Cmt(@"Pre-build the next track's start request when the current one begins, so the
auto-advance at track end needs no main-thread hop and the next track starts
even while Unity's main thread is frozen. Requires NativeScheduler."),
            B("NativePreStageAdvance",               "CMMConfig.NativePreStageAdvance",             true),

            Gap(),
            Cmt(@"Set to false to silence verbose [CMM] console output.
Log.Warning and Log.Error always emit regardless of this flag."),
            B("DebugLogging",                        "CMMConfig.DebugLogging",                      false),

            Gap(),
            Cmt(@"── Perf instrumentation (see CMMPerf) ─────────────────────────────
When true, the context-change hot path is timed per phase. Each scan tick
times the poll/resolve/gate; each transition logs a ""[CMM PERF] TX"" wall +
GC line; any synchronous span over PerfSpikeMs logs a ""SPIKE"" line the
instant it happens, and StalenessCheckLoop dumps rolling per-phase stats.
Leave off for normal play; turn on to diagnose stutters."),
            B("PerfTiming",                          "CMMConfig.PerfTiming",                        false),
            Cmt(@"Log a SPIKE line for any single synchronous span at/over this many ms."),
            F("PerfSpikeMs",                         "CMMConfig.PerfSpikeMs",                       1.5f),
            Cmt(@"Per-frame stutter watchdog. A frame is flagged in a ""[CMM PERF] STUTTER"" line
(GC gen0/1/2 delta, transition in flight, live coroutine count) when it runs
longer than the device's rolling baseline × PerfFrameSpikeFactor AND clears
PerfFrameSpikeFloorMs. RELATIVE to baseline because on a 30 fps machine an
absolute cut-off just logs the normal framerate. A stutter that is always
tx=idle with GC=0 is NOT caused by this mod. Factor 2.5 / floor 50 ms is a
good start; lower the factor to ~2 to be more sensitive."),
            F("PerfFrameSpikeFactor",                "CMMConfig.PerfFrameSpikeFactor",              2.5f),
            F("PerfFrameSpikeFloorMs",               "CMMConfig.PerfFrameSpikeFloorMs",             50f),
        };

        // ═══ Config/radio.xml — root <CMMRadioSettings> ══════════════════
        internal static readonly SchemaEntry[] Radio =
        {
            Cmt(@"The world EMITTERS (speaker/radio blocks playing spatially in the world) require game
version 3.0+ — on older versions they are inert and this setting (plus the acoustic
WorldEmitter* settings below) is ignored. The RADIO STATIONS themselves and the
personal radio dial (KeyRadioNext/KeyRadioPrev) work on EVERY supported version."),
            B("WorldEmitterEnabled",                 "CMMConfig.WorldEmitterEnabled",               true),
            Cmt(@"The Radio source. Names a context (its Source/fadeintime/volume then apply), or is a raw
folder / .m3u / absolute path.
STATIONS: every SUBFOLDER of that folder is one radio station — the folder name is the
station name. A station folder can hold audio files, .m3u/.m3u8 playlists (expanded; their
entries may point anywhere), or both. Each POI's radios all tune to ONE station (picked
stably per POI); a radio
you place yourself gets a station from its position (move it to retune). Stations broadcast
on the WORLD CLOCK: everyone in the same world hears the same song at the same point, and
rejoining a station resumes wherever its broadcast is now. Songs play whole, back-to-back;
stations are ready INSTANTLY at any library size (no scanning — a join reads only a handful
of file headers). Up to 3 nearest stations are audible at once. Loose tracks directly in
the folder are a FALLBACK single station, used only when no subfolder has playable audio
(a flat folder behaves as before).
REMOVING a station folder mid-game: radios tuned to it go SILENT (no retune — dead air
until the folder returns or you re-enter the world; other stations are unaffected)."),
            Str("WorldEmitterSource",                  "CMMConfig.WorldEmitterSource",                @"Radio"),
            Cmt(@"J:\7D2D Custom Music\Radio, or an .m3u, or an absolute path"),
            F("WorldEmitterRange",                   "CMMConfig.WorldEmitterRange",                 60f),
            Cmt(@"Volume 0..1: the world speakers' own level, multiplied onto the game's ""Overall"" and
""Ambient Sound"" sliders. The emitters follow AMBIENT, not the music slider — a radio
playing out of a block is an environmental sound, so turning the music down leaves it
playing. This is SEPARATE from the Radio context's volume=""..."" in contexts.xml, which
now trims only the personal radio (Alt+PgUp/PgDn): a speaker across the room and a radio
in your head want different levels. Hot-reloads — edit and save while the game runs.
NOTE Ambient defaults to 100% where Music defaults to 60%, so at 0.5 the speakers are
~4.4 dB louder than they used to be; 0.3 reproduces the old level exactly.

RAISED FROM 0.5 TO 1.0. Occlusion used to leak: a route could pass diagonally between two
blocks meeting at a corner and pay nothing, so walls were quietly under-charged everywhere
there was geometry. 0.5 was settled by ear against that leak. With the leak closed the same
speakers are correctly quieter, and 0.5 is no longer the level it was chosen to be. If you
had already changed this setting your value is kept."),
            F("WorldEmitterVolume",                  "CMMConfig.WorldEmitterVolume",                1f,
              since: 1, changedIn: 5, was: 0.5f),
            F("WorldEmitterFade",                    "CMMConfig.WorldEmitterFade",                  1f),
            Cmt(@"Placement (""birth"") fade, seconds: how long a just-placed speaker takes to fade in — both
when it joins a station that's already playing (per-speaker ramp) and when placing it is
what STARTS the station's voice (the placed radio is its first audible speaker nearby).
-1 = inherit the station start fade (the Radio context's fadeintime, else WorldEmitterFade).
0 = pop in at full volume instantly. Tune this to make placing radios feel snappier without
changing station-approach fade-ins or the personal radio's world-entry join."),
            F("WorldEmitterBirthFade",               "CMMConfig.WorldEmitterBirthFade",             0f),
            Cmt(@"Broadcast anchor window, seconds (min 300; default 3600 = 1 h). Station schedules re-anchor
at every multiple of this: the song lineup jumps to a fresh deterministic point once per
window (a playing song still finishes first — nothing is ever cut). Longer = rarer jumps
but slightly more file headers read when joining a station. Everyone in an MP world should
use the same value — it shapes the shared broadcast, like the station folders do."),
            F("RadioAnchorSeconds",                  "CMMConfig.RadioAnchorSeconds",                3600f),
            Cmt(@"Personal-radio UI sounds (synthesized in the native mixer — no audio files): a static hash
while dialing between / joining stations, a click when the radio powers on, a clunk when it
powers off (also plays when the radio powers itself off because every station was deleted).
Volume is 0..1 before the music master — the sounds scale with the radio itself. Both
hot-reload."),
            B("RadioSfxEnabled",                     "CMMConfig.RadioSfxEnabled",                   true),
            F("RadioSfxVolume",                      "CMMConfig.RadioSfxVolume",                    0.5f),
            Cmt(@"Tuning-static character. ALL hot-reload and take effect on the very next ping — even
mid-hash — so dial between stations while editing and hear each change immediately.
  StaticVolume  : the static's own level 0..1; -1 = inherit RadioSfxVolume (clicks always
                  use RadioSfxVolume, so you can run quiet static under audible clicks).
  StaticLowHz   : low cut (50..4000). Lower = fuller/warmer body; higher = thin AM hiss.
  StaticHighHz  : top cap (400..12000, kept above the low cut). Higher = brighter hiss;
                  lower = dull rumble.
  StaticCrackle : 0..1 sputter depth. 0 = smooth ""shhh""; 1 = full pops-and-sputter radio
                  crackle (random dropouts a few ms to ~30 ms long)."),
            // Three defaults deep, so all three former values are listed. A file that never
            // saw schema 9 still holds the originals; the schema-10 rule only knew about the
            // schema-9 values and therefore walked straight past them. See MatchesAnyFormerDefault.
            F("RadioTuneMinStaticMs",                "CMMConfig.RadioTuneMinStaticMs",              100f,
              since: 6, changedIn: 12, was: new object[] { 900f, 4000f, 1500f }),
            F("RadioTuneLeaveMs",                    "CMMConfig.RadioTuneLeaveMs",                  700f,
              since: 6, changedIn: 13, was: new object[] { 260f, 3400f, 1200f }),
            F("RadioTuneArriveMs",                   "CMMConfig.RadioTuneArriveMs",                 1000f,
              since: 6, changedIn: 15, was: new object[] { 1100f, 7000f, 2800f }),
            F("RadioTuneDullness",                   "CMMConfig.RadioTuneDullness",                 0.85f, since: 6),
            F("RadioTuneMononess",                   "CMMConfig.RadioTuneMononess",                 1f, since: 6),
            F("RadioTuneGainPow",                    "CMMConfig.RadioTuneGainPow",                  0.30f, since: 7),
            F("RadioTuneGiveUpMs",                   "CMMConfig.RadioTuneGiveUpMs",                 10000f, since: 6),
            F("RadioRecvKnee",                       "CMMConfig.RadioRecvKnee",                     0.90f,
              since: 8, changedIn: 14, was: new object[] { 0.45f, 0.65f }),
            F("RadioRecvKneeWidth",                  "CMMConfig.RadioRecvKneeWidth",                0.22f, since: 8),
            F("RadioRecvNoise",                      "CMMConfig.RadioRecvNoise",                    0.15f,
              since: 8, changedIn: 16, was: new object[] { 0.45f, 0.55f, 0.31f }),
            F("RadioRecvClicks",                     "CMMConfig.RadioRecvClicks",                   0.18f, since: 8, changedIn: 9, was: 0.22f),
            F("RadioRecvClickRate",                  "CMMConfig.RadioRecvClickRate",                180f,  since: 8, changedIn: 9, was: 220f),
            F("RadioRecvHiFloorHz",                  "CMMConfig.RadioRecvHiFloorHz",                1800f, since: 8),
            F("RadioRecvDeemphHz",                   "CMMConfig.RadioRecvDeemphHz",                 2122f, since: 8),
            F("RadioRecvFloorDb",                    "CMMConfig.RadioRecvFloorDb",                  5f,    since: 9),
            F("RadioRecvMuteWidth",                  "CMMConfig.RadioRecvMuteWidth",                5f,    since: 9),
            F("RadioRecvDrive",                      "CMMConfig.RadioRecvDrive",                    0.15f, since: 9),
            F("RadioRecvWander",                     "CMMConfig.RadioRecvWander",                   0.55f, since: 9),
            F("RadioRecvFlutter",                    "CMMConfig.RadioRecvFlutter",                  0.30f, since: 9),
            F("RadioRecvFlutterHz",                  "CMMConfig.RadioRecvFlutterHz",                14f,   since: 9),
            F("RadioRecvMangle",                     "CMMConfig.RadioRecvMangle",                   1f,     since: 10),
            F("RadioRecvMakeupDb",                   "CMMConfig.RadioRecvMakeupDb",                 9f,     since: 10),
            F("RadioRecvLowCutHz",                   "CMMConfig.RadioRecvLowCutHz",                 400f,   since: 10),
            F("RadioRecvHighCutHz",                  "CMMConfig.RadioRecvHighCutHz",                2600f,  since: 10),
            F("RadioRecvNoiseHold",                  "CMMConfig.RadioRecvNoiseHold",                1f,     since: 10),
            F("RadioRecvNoiseFloor",                 "CMMConfig.RadioRecvNoiseFloor",               0.06f,  since: 10),
            F("RadioRecvWindowA",                    "CMMConfig.RadioRecvWindowA",                  0.28f,  since: 10),
            F("RadioRecvWindowB",                    "CMMConfig.RadioRecvWindowB",                  0.912f, since: 10),
            F("RadioRecvLockAt",                     "CMMConfig.RadioRecvLockAt",                   0.52f,  since: 10),
            F("RadioRecvLockMs",                     "CMMConfig.RadioRecvLockMs",                   9f,     since: 10),
            F("RadioStaticVolume",                   "CMMConfig.RadioStaticVolume",                 -1f),
            F("RadioStaticLowHz",                    "CMMConfig.RadioStaticLowHz",                  50f,
              since: 1, changedIn: 6, was: 350f),
            F("RadioStaticHighHz",                   "CMMConfig.RadioStaticHighHz",                 2100f,
              since: 1, changedIn: 6, was: 2000f),
            F("RadioStaticCrackle",                  "CMMConfig.RadioStaticCrackle",                0.8f),
            Cmt(@"Power-click character, one set per click (all hot-reload — heard on the next dial):
Hz       : tone cap (300..10000) — higher = bright tick, lower = dull thud.
Snap     : 0..1 — balance of the bright noise ping vs the low thump. 1 = all snap
           (a sharp tick), 0 = all thud (a soft pop).
LengthMs : the thump's decay (5..120); the whole click lasts about 3.3× this.
Body     : 0..1 — the shell-knock resonator, the ""tok"" of a real dial body. 0 = off.
BodyHz   : the knock's ring pitch (200..3000; plastic ~900-1400, bakelite ~500-800).
Double   : 0..1 — a second micro-impact ~12ms after the first (the detent's spring
           release then the stop landing — the mechanical ""cl-ick""). 0 = single.
Volume   : that click's own level 0..1; -1 = inherit RadioSfxVolume."),
            F("RadioClickOnHz",                      "CMMConfig.RadioClickOnHz",                    8722f),
            F("RadioClickOnSnap",                    "CMMConfig.RadioClickOnSnap",                  0.72f),
            F("RadioClickOnLengthMs",                "CMMConfig.RadioClickOnLengthMs",              60f),
            F("RadioClickOnBody",                    "CMMConfig.RadioClickOnBody",                  0.37f),
            F("RadioClickOnBodyHz",                  "CMMConfig.RadioClickOnBodyHz",                5100f),
            F("RadioClickOnDouble",                  "CMMConfig.RadioClickOnDouble",                0.8f),
            F("RadioClickOnVolume",                  "CMMConfig.RadioClickOnVolume",                -1f),
            F("RadioClickOffHz",                     "CMMConfig.RadioClickOffHz",                   8722f),
            F("RadioClickOffSnap",                   "CMMConfig.RadioClickOffSnap",                 0.7f),
            F("RadioClickOffLengthMs",               "CMMConfig.RadioClickOffLengthMs",             68f),
            F("RadioClickOffBody",                   "CMMConfig.RadioClickOffBody",                 0.4f),
            F("RadioClickOffBodyHz",                 "CMMConfig.RadioClickOffBodyHz",               4350f),
            F("RadioClickOffDouble",                 "CMMConfig.RadioClickOffDouble",               0.8f),
            F("RadioClickOffVolume",                 "CMMConfig.RadioClickOffVolume",               -1f),

            Gap(),
            Cmt(@"── The DIAL DETENT: turning the tuning knob one station ───────────────────────────────
A different object from the power switch, so a different sound. Powering the radio on is
a TOGGLE — a lever going over centre and then the mechanism hitting its stop: two impacts,
a low heavy shell, the RadioClickOn/Off* sets above. Turning the knob one notch is a
DETENT — a ball dropping into a groove: one bright tick, a small tight shell, gone in a
few milliseconds. Same six controls, aimed somewhere else entirely."),
            F("RadioClickDialHz",                    "CMMConfig.RadioClickDialHz",                  8187f),
            F("RadioClickDialSnap",                  "CMMConfig.RadioClickDialSnap",                0.84f),
            F("RadioClickDialLengthMs",              "CMMConfig.RadioClickDialLengthMs",            44f),
            F("RadioClickDialBody",                  "CMMConfig.RadioClickDialBody",                0.5f),
            F("RadioClickDialBodyHz",                "CMMConfig.RadioClickDialBodyHz",              5949f),
            F("RadioClickDialDouble",                "CMMConfig.RadioClickDialDouble",              0.52f),
            F("RadioClickDialVolume",                "CMMConfig.RadioClickDialVolume",              -1f),

            Gap(),
            Cmt(@"How much each firing differs from the last, 0..1, shared by all three clicks. No two
detents on a real mechanism are identical, and a fixed waveform repeated on every notch is
exactly what gives a UI sound away as a sound EFFECT. It tilts the crack against the shell
in opposite directions, so what changes is the CHARACTER — this one caught more of the
metal, that one more of the housing — rather than the volume; it also jitters the shell's
pitch, the length, and the spacing of the second impact, which is the cue the ear uses to
tell a mechanism from a recording. It also varies HOW LONG each one rings, and does so
asymmetrically: most detents land near LengthMs and the occasional one catches and rattles
a little longer, which is what real hardware does. A symmetric spread would give as many
unnaturally curtailed clicks as long ones, and short outliers read as a dropout rather than
as variety. At the default 0.5 the dial rings between about 15 and 22 ms.
0 = every click identical."),
            F("RadioClickVary",                      "CMMConfig.RadioClickVary",                    0.5f),

            Gap(),
            Cmt(@"── Recorded clicks ────────────────────────────────────────────────────────────────────
true plays the files in the mod's Sounds/ folder instead of synthesizing:
  Sounds/radio_dial.wav    one notch of the tuning dial
  Sounds/radio_power.wav   the power switch (both throws)
Replace them with anything you like — mono or stereo, 8/16/24/32-bit PCM or float WAV.
They are read ONCE at startup and held in RAM (about 66 KB), so a swapped file needs a
restart; the settings around them are live either way. false goes back to the synthesized
clicks, which are also what you get automatically if a file is missing or unreadable: no
sound file can break the radio, only quieten it.

In game: `cmm sfx` shows what loaded, `cmm sfx test spin` fires eight detents in a row
(the only way to judge the variance), and `cmm sfx jitter <hz>` / `samples on|off` /
`vol <0..1>` turn these live for listening. Those are overrides, not saves — put the
value you settle on back in here.

JitterHz is how far the playback RATE wanders from one firing to the next, either side of
the file's own rate. One recording repeated verbatim is exactly what gives a UI sound away
as a sound effect, and the dial is the worst case because you hear eight of them in two
seconds. 2000 Hz against a 48 kHz file is about 4% — a couple of semitones at most, heard
as ""another one of those"" rather than as a different sound. 0 = every firing identical."),
            B("RadioClickSamples",                   "CMMConfig.RadioClickSamples",                 true),
            F("RadioClickSampleJitterHz",            "CMMConfig.RadioClickSampleJitterHz",          2000f),
            Cmt(@"── World-emitter spatial tuning (all hot-reloaded — edit while the game runs) ──
MinDistance: full-volume plateau radius (blocks). Move inside it and the level stays flat;
             beyond it the volume fades to silence at Range.
Falloff:     steepness of the plateau→Range fade. 1 = straight amplitude ramp; >1 steeper
             just past the plateau (more change per step / ""punchier""); <1 gentler near.
Muffle:      how much distance dulls the tone via a low-pass (0 = none, 1 = very muffled far).
             The ""it's over there"" cue, separate from volume. Measured against a FIXED reference
             distance (~24 blocks to full dullness), NOT Range — so it stays effective at large
             Range, and a wall's virtual distance audibly dulls the tone.
Occlusion:   walls make a speaker sound FARTHER (quieter + duller on the same curve), not a hard
             cut, so it fades gradually instead of all-or-nothing. Two curves shape it:
             WallDistance = virtual blocks a full solid wall adds. MaxDistance = the SOFT-KNEE floor
             (tanh): stacking walls muffles with DIMINISHING RETURNS toward this, never silent by
             muffle alone (a box you stand behind stays audible). HardStop = virtual blocks of
             SOLID actually crossed at which it cuts to TRUE silence (a sealed thick vault goes
             dead) — keyed to material only, so an open-air detour never triggers it; denser
             material seals in fewer blocks. Occlusion false = off."),
            F("WorldEmitterMinDistance",             "CMMConfig.WorldEmitterMinDistance",           1f),
            F("WorldEmitterFalloff",                 "CMMConfig.WorldEmitterFalloff",               2.5f),
            F("WorldEmitterMuffle",                  "CMMConfig.WorldEmitterMuffle",                0.9f),
            Cmt(@"StereoWidth 0..1: how hard a left/right speaker pans. 1 = full pan (far ear can drop to silence);
lower keeps the sound in BOTH ears at different volumes (like a real source at 90°). ~0.8 = natural."),
            F("WorldEmitterStereoWidth",             "CMMConfig.WorldEmitterStereoWidth",           0.8f),
            B("WorldEmitterOcclusion",               "CMMConfig.WorldEmitterOcclusion",             true),
            F("WorldEmitterOcclusionWallDistance",   "CMMConfig.WorldEmitterOcclusionWallDistance", 35f),
            F("WorldEmitterOcclusionMaxDistance",    "CMMConfig.WorldEmitterOcclusionMaxDistance",  30f),
            F("WorldEmitterOcclusionHardStop",       "CMMConfig.WorldEmitterOcclusionHardStop",     68f),
            Cmt(@"OcclusionFadeRate: cap (virtual blocks/second) on how fast a speaker's occlusion may RISE from
MOVEMENT, so walking out a doorway / behind a thick wall fades the radio out (~a second) instead
of hard-cutting it the tick the route flips to blocked. Door open/close events bypass the cap
(their timing is tuned separately) and occlusion DROPS are never capped — sound arriving tracks
the event. 0 = no cap (old instant behavior). Hot-reloads."),
            F("WorldEmitterOcclusionFadeRate",       "CMMConfig.WorldEmitterOcclusionFadeRate",     50f),
            Cmt(@"How often the occlusion decision and the speaker point-set refresh, in Hz (1-30). Your
listener position is pushed every FRAME regardless, so distance and panning always track
smoothly — this is only how fast a WALL between you and a speaker catches up, i.e. how
quickly the sound re-shapes when you sprint past cover. Was a hardcoded 4 Hz; 10 costs
about what 4 did before the scan optimisations. Raise it if the shaping still lags behind
you, lower it to claw back CPU. Watch EmitPoll in the [CMM PERF] Phases line for the cost."),
            F("WorldEmitterTickHz",                   "CMMConfig.WorldEmitterTickHz",                10f, since: 20),
            Cmt(@"Mid-swing door acoustics follow the physical GAP IN METRES (swing fraction × the door's largest
dimension), so the same two knobs are right for every door size: a 3 m garage door with a metre
still to go is a huge aperture (full sound); a vault leaf the same fraction open is a hand-width
crack (nearly shut). DoorGapFull = opening (m) at/above which sound is fully open — big doors come
alive almost immediately on open. DoorGapClosed = opening (m) at/below which it's acoustically
shut — the final sliver of crack is ignored, so heavy doors read as a slam. Hot-reloads."),
            F("WorldEmitterDoorGapFull",             "CMMConfig.WorldEmitterDoorGapFull",           1.25f),
            Cmt(@"GapFullOpen: the OPENING direction's full-sound threshold (metres of gap). Kept separate from
GapFull because the two directions want different feels: closing is tuned for when the shut lands
(fade begins early), but sound should FLOOD IN the moment an opening door has a real gap — sharing
one threshold made a close-tuned door slow to come alive on open. Hot-reloads."),
            F("WorldEmitterDoorGapFullOpen",         "CMMConfig.WorldEmitterDoorGapFullOpen",       0.7f),
            F("WorldEmitterDoorGapClosed",           "CMMConfig.WorldEmitterDoorGapClosed",         0.15f),
            Cmt(@"DoorGapOverrides: per-door-type overrides of the three gap thresholds, without touching the rest.
Format: ""match=full/fullOpen/closed;match=..."" — match is a case-insensitive SUBSTRING of the
block name (""garageDoor"" catches wooden/steel/iron variants), ""-"" inherits the global, first match
wins. Default slows the garage/roll-up OPEN ramp to suit their long lift. Hot-reloads."),
            Str("WorldEmitterDoorGapOverrides",        "CMMConfig.WorldEmitterDoorGapOverrides",      @"drawBridge=6/5/1;garageDoor=3/2/0.5;rollUp=2/2/0.5;vaultHatch=0.5/0.3/0.1;ironHatch=1/0.1/0.35;manholeHatch=1/0.1/0.35;woodHatch=0.5/0.1/0.1",
              since: 1, changedIn: 25, was: @"garageDoor=3/2/0.5;rollUp=2/2/0.5;vaultHatch=0.5/0.3/0.1;ironHatch=1/0.1/0.35;manholeHatch=1/0.1/0.35;woodHatch=0.5/0.1/0.1"),
            Cmt(@"DoorSealTypes: comma-separated block-name prefixes of doors that are AIRTIGHT when fully shut and
undamaged. A closed vault door is a pressure seal — TRUE SILENCE via the hard-stop, landing while
the leaf is still visibly closing (the slam) — not just a metal panel's worth of muffle. No volume
knob can reach silence for ordinary doors; this list is what does it, per door type. Any visible
damage state breaches the seal (back to normal material acoustics). Hot-reloads."),
            Str("WorldEmitterDoorSealTypes",           "CMMConfig.WorldEmitterDoorSealTypes",         @"vaultDoor,vaultHatch"),
            Cmt(@"PathRouting: when the direct line to a speaker is blocked, flood-fill through open air to find
whether sound can reach you another way (around a wall, up a stairwell) and how far it travels,
so it routes through openings instead of going near-silent. Deterministic (no ray noise).
FloodRadius = how many blocks the air search reaches from the ear (bigger = finds longer ways
around, more CPU; capped at 32). Set PathRouting false for direct-line only.
A speaker beyond FloodRadius blocks routes to the box edge then straight-lines the rest (boundary
blend), so there's no volume snap at the edge. Set PathRouting false for direct-line only (A/B).
NOTE: a BIG FloodRadius follows a hallway around a corner and keeps the direct sound alive, which
masks the reverb; a small one lets the dry fade so reverb blooms. Default 8 is a middle ground —
dial toward 1 for reverb-forward, toward 24 for routing-forward. Hot-reloads live."),
            B("WorldEmitterPathRouting",             "CMMConfig.WorldEmitterPathRouting",           true),
            I("WorldEmitterFloodRadius",             "CMMConfig.WorldEmitterFloodRadius",           32),
            Cmt(@"FIELD CACHE (""Phase 2""): occlusion comes from one CACHED sound field per speaker (built once,
rebuilt only when doors/blocks change) instead of re-computing around you as you move — walking
past walls and floors now fades smoothly instead of stepping. Door swings and the first seconds
near a newly-met speaker still use the classic path while its field builds. FieldRadius = how far
(blocks) each speaker's field reaches (16-48; bigger = smoother far away, ~3.6 MB per speaker at
48). FieldRefresh = seconds between quiet rebuilds (catches world edits). All hot-reload;
FieldCache false = exact pre-Phase-2 behavior."),
            B("WorldEmitterFieldCache",              "CMMConfig.WorldEmitterFieldCache",            true),
            I("WorldEmitterFieldRadius",             "CMMConfig.WorldEmitterFieldRadius",           48),
            F("WorldEmitterFieldRefresh",            "CMMConfig.WorldEmitterFieldRefresh",          60f),
            Cmt(@"NATIVE ACOUSTICS: the occlusion model that decides how much a wall, floor or door takes away
and which direction the sound reaches you from. Works in DECIBELS rather than in ""extra
distance"", which is what lets a wall be worth the same amount of quiet wherever you stand —
the older model priced it in blocks, so the same wall cost a different amount at every range
and could not be tuned. Distance falloff stays the game's own curve; this only supplies the
occlusion. Runs off the frame loop (a scene is built in ~1.5 ms slices while you walk) and
falls back to the previous model automatically while a scene builds and ALWAYS during a door
swing, so mid-swing door acoustics are unchanged.
false = the previous occlusion model, exactly as it shipped. Live: `cmm acoustics on|off`.

DEFAULTED BACK TO false 2026-08-20, ON THE ONLY INSTRUMENT THAT HAS BEEN RIGHT ABOUT THIS
ALL ALONG: listening. Verdict, A/B'd in game with `cmm acoustics model native|legacy`:
""the occlusion feels completely broken on native, and works on legacy"". The native model
gets several things the old one cannot (many speakers affordably, geometry-bound rebuilds
rather than ear-bound floods), but none of that is worth an occlusion that is wrong, and a
week of fixing individual defects in it has not made it right. It stays one command away
for development; it is not what a player gets.
OcclusionFullDb = how much occlusion (dB) fully dulls the tone; lower = walls sound duller
sooner. Both hot-reload."),
            Cmt(@"── Field occlusion: the rebuild ───────────────────────────────────────────────────────
ONE occlusion field, centred on you, read by every speaker (and later by anything else
that makes noise, including moving things - which is why it is built this way: there is
only ever one pair of ears, and there may be many sources).

The field is rebuilt whole a few times a second rather than patched, so a door opening is
simply in the next one. There is no staleness window, no handover, and no second model to
fall back to - the class of bug that produced ""I open the door, it's clear, then it sounds
shut"" cannot occur here rather than being fixed here.

ON by default since 2026-08-20. `cmm acoustics model native|legacy` pins an old path
for an A/B; `model auto` returns to whatever this says. Give it a few seconds after switching to read the world in;
it is quiet-but-correct close to you while it does, because unread space counts as solid.

DetourDb is what one block of going-the-long-way costs. It is the only tuning constant the
model has beyond the shared material curve - everything else comes from the blocks."),
            B("WorldEmitterFieldOcclusion",          "CMMConfig.WorldEmitterFieldOcclusion",        true),
            F("WorldEmitterFieldDetourDb",           "CMMConfig.WorldEmitterFieldDetourDb",         1.5f),

            Gap(),
            Cmt(@"What one block of material is worth to the field model: dB = WallDb * loss^WallPow, where
loss is the shared block reader's answer (0 = air, 0.5 = wood, 0.9 = concrete, 1 = metal).
Fitted to what the SHIPPED model actually does at range 60, eight blocks out - one wood
block -20.2 dB, one concrete block -50.5 dB - because that is the occlusion people like.
Raise WallDb for heavier walls everywhere; raise WallPow to spread light and heavy
materials further apart. Both hot-reload, so you can turn them while listening and A/B
against `cmm acoustics model auto` from the same spot."),
            F("WorldEmitterFieldWallDb",             "CMMConfig.WorldEmitterFieldWallDb",           59.5f),
            F("WorldEmitterFieldWallPow",            "CMMConfig.WorldEmitterFieldWallPow",          1.56f),

            Gap(),
            Cmt(@"── How occlusion is SHAPED before you hear it ─────────────────────
Two bends, both taken from the older model, where the per-door numbers were
originally tuned. Without them the field applies occlusion as a straight line,
which lands the extremes about right and makes everything in the middle -- a
door, most usefully -- read as ""slightly quieter"" rather than ""shut"".

  Shaping    : false restores the straight line exactly, so this whole change
               is one setting away from being undone.
  CeilingDb  : occlusion saturates toward this, so however far around the sound
               has to travel there is always an audible floor. Stand behind a
               crate and you still hear it. Going the long way can never silence
               a speaker -- only material can.
  HardStopDb : how much MATERIAL silences a speaker outright, eased in from 55%
               of it. This is what a sealed room is. Deliberately unreachable by
               distance alone.

For scale, at the wall settings above: a shut wooden door measures about 20 dB
of material, two of them about 40, a concrete wall about 50. The defaults leave
one wood door heavily muffled but present and a concrete wall gone. Those are a
listening judgement, not a calculation -- all three hot-reload, so change them
while the game runs and hear it."),
            B("WorldEmitterFieldShaping",            "CMMConfig.WorldEmitterFieldShaping",          true,  since: 4),
            F("WorldEmitterFieldCeilingDb",          "CMMConfig.WorldEmitterFieldCeilingDb",        45f,   since: 4),
            F("WorldEmitterFieldHardStopDb",         "CMMConfig.WorldEmitterFieldHardStopDb",       60f,   since: 4),

            Gap(),
            Cmt(@"HOW HARD A PARTLY OPEN FACE ATTENUATES: transmission = openFraction ^ this.
The two settings above say what a SOLID block is worth, and a solid block is already
effectively sealed, so neither of them changes what a real building sounds like. What
decides that is how a face with a gap in it scores. Measured in game across 194 samples
with this at 1 (plain area fraction), the material term never once exceeded 11.1 dB:
every wall in a POI has some opening and the route always found one cheap enough.
Linear area is also too generous to be physical, since a small aperture radiates far
less than its share of the wall.
2 also repays what the cell-shape change cost: faces used to be 3x3 sub-blocks and are
now 1x3, so a one-block hole went from a ninth of a face to a third. Squaring puts it
back at a ninth. A fully open face and a sealed one are unaffected at any value, so
open air stays at 0 dB. Live: cmm field aperture <n>"),
            F("WorldEmitterFieldAperturePow",        "CMMConfig.WorldEmitterFieldAperturePow",      1f),

            Gap(),
            Cmt(@"HOW FAST occlusion may change, dB per second, per speaker. The field re-floods a few
times a second and each flood is an independent answer, so walking past a doorway flips
the route from ""through the opening"" to ""the long way round"" between one flood and the
next. Both answers are right; the STEP between them is not something a real room does.
Measured in game while walking: 2.7 -> 10.7 dB in a quarter second.

Limited in dB, not in the ""extra virtual distance"" the mixer takes, because dB is what
the ear hears and the two are not proportional - the same 14 blocks of extra is 8 dB at
five blocks from a speaker and almost nothing at fifty.

0 turns it off and publishes raw floods, which is the honest way to hear whether a
change is the model or the smoothing. Live: cmm field slew <n>"),
            F("WorldEmitterFieldSlewDb",             "CMMConfig.WorldEmitterFieldSlewDb",           40f),

            Gap(),
            Cmt(@"HOW MUCH OF A DOOR'S SWING THE SOUND WAITS FOR (0..1). Occlusion tracks a door's
ANIMATION, so the seal only completes when the leaf stops moving - half a second to a
second after you press. That is physically right and it reads as lag. Measured latch to
published field is 33-79 ms, so the pipeline is not the delay; this ramp is.
LEAVE THIS AT 1. Openness is already the output of the per-door GAP model below (physical
opening in metres, per-type overrides, asymmetric for opening vs closing, tuned by hand),
and scaling it squashes all of that with one global number.
If a door seals too late, raise WorldEmitterDoorGapClosed instead - globally, or for that
one door type via WorldEmitterDoorGapOverrides. 0 here ignores the animation entirely and
snaps at the latch, which is the only thing this is still good for."),
            F("WorldEmitterDoorSwingFrac",           "CMMConfig.WorldEmitterDoorSwingFrac",         1f),
            F("WorldEmitterDoorMaxLoss",             "CMMConfig.WorldEmitterDoorMaxLoss",           0.6f, since: 21),

            Gap(),
            B("WorldEmitterNativeAcoustics",         "CMMConfig.WorldEmitterNativeAcoustics",       false),
            F("WorldEmitterOcclusionFullDb",         "CMMConfig.WorldEmitterOcclusionFullDb",       8f),
            Cmt(@"Bounce-energy carry: the around-corner reverb is graded by the ENERGY surviving the route's
wall bounces (reflective caves ring around corner after corner until the sound truly runs out
of energy; absorptive wood rooms still eat it fast) instead of by raw route meters. Hot;
false = the old distance-graded carry."),
            B("WorldEmitterReverbBounce",            "CMMConfig.WorldEmitterReverbBounce",          true),
            Cmt(@"Directional occlusion: when the straight line to a speaker runs through solid rock, the sound is
re-aimed to arrive from the OPEN ROUTE (down the corridor) instead of straight through the wall.
Only the direction changes; loudness/muffle are unchanged. Needs PathRouting on. Hot-reloads."),
            B("WorldEmitterOcclusionDirectional",    "CMMConfig.WorldEmitterOcclusionDirectional",  true),
            Cmt(@"Reverb: a room probe (rays from your ear measuring how far and what material the surfaces around
you are) estimates how enclosed/big/reflective the space is and adds room reflections to the
station. The reflected (wet) sound is NOT muffled by occlusion, so behind a wall the direct sound
drops away but the reverb carries — the ""it bounced / it's in the next room"" cue. A big reflective
room (metal warehouse) rings; a small dead one (cloth/dirt) is nearly dry; open air is dry.
ReverbWet = master wet level 0..1 (0 = off). Size/decay/damping are estimated automatically."),
            B("WorldEmitterReverbEnabled",           "CMMConfig.WorldEmitterReverbEnabled",         true),
            F("WorldEmitterReverbWet",               "CMMConfig.WorldEmitterReverbWet",             0.6f),
            Cmt(@"ReverbDecay: tail LENGTH 0..1, independent of Wet (level). Lower = shorter/tighter reverb, higher
= a longer ring. Wet = how loud the reflections are; Decay = how long they hang. Hot-reloads."),
            F("WorldEmitterReverbDecay",             "CMMConfig.WorldEmitterReverbDecay",           0.6f),
            Cmt(@"ReverbRange: how far (blocks) the room probe reaches before a surface reads as ""open sky"". Enclosure
and room size are measured against this, so a space BIGGER than it reads as outdoors and goes dry
(why a large cave lost its reverb). Raise it for big caverns; cost is longer ray marches per reverb
tick (cheap at the few-Hz rate). Hot-reloads."),
            F("WorldEmitterReverbRange",             "CMMConfig.WorldEmitterReverbRange",           64f),
            Cmt(@"ReverbCarry 0..1: level of the CARRY reverb — the deliberate ""muffled music from the next room /
down the tunnel"" cue that survives occlusion of the direct sound. Taste knob: your own room's
reverb (the LOCAL bus) is fed the arriving occluded signal so it follows walls/water/hard-stop
automatically, while the carry responds to real material on its own (fixed physics, not a dial):
it halves per half wall-equivalent crossed — a shut wooden door ≈ −3 dB, metal ≈ −6 dB — and is
hard-blocked entirely at vault thickness; open-air corners carry at full level. 0 = only your own
space reverberates. Replaces the old ReverbOcclusion compromise dial. Hot-reloads."),
            F("WorldEmitterReverbCarry",             "CMMConfig.WorldEmitterReverbCarry",           0.8f),
            Cmt(@"ReverbRangeMult (≥1): the reverb reaches this many times FARTHER than the dry Range, so a brief step
past the dry range in a connected cave doesn't drop the tail. Safe outdoors (no enclosure = no wet, so
a bigger reach carries nothing). 1 = reverb stops at the dry range; 1.5–2 bridges cave dips. Hot-reloads."),
            F("WorldEmitterReverbRangeMult",         "CMMConfig.WorldEmitterReverbRangeMult",       1.5f),
            Cmt(@"ReverbPan 0..1: how much the reverb is panned toward the source's traced (air-route) bearing. 0 = fully
diffuse (surrounds you); 1 = fully located. A middle value keeps the reverb spacious but leans it toward
the source, so at distance — where the wet dominates the faded dry — you can still tell which way the
sound comes from (in a cave, down the tunnel toward the radio). Hot-reloads."),
            F("WorldEmitterReverbPan",               "CMMConfig.WorldEmitterReverbPan",             0.5f),
            Cmt(@"ReverbDepth 0..1: how much the reverb EVOLVES with your acoustic depth from the station — the
air-route distance (real distance + the open-air detour around bends; walls don't count, the seal
gate covers those). Deeper = the wet gets quieter, DARKER and more damped, the way reflections
really decay down a tunnel: a bright splash near the radio, a sinking dark rumble deep in the
cave, no cliff anywhere. 0 = flat wet at any depth (old behaviour). Hot-reloads."),
            F("WorldEmitterReverbDepth",             "CMMConfig.WorldEmitterReverbDepth",           0.7f),
            Cmt(@"Radio coloration: the ""through a speaker"" voice, applied before the distance muffle / occlusion /
reverb (the reflections carry the colored sound). Three independent 0..1 levers; all 0 = clean:
ColorDrive  = soft-clip amount — crushes peaks into speaker breakup/grit (level-compensated, so
              it adds distortion, not volume).
ColorStatic = constant static-hiss bed from the radio (panned + muffled like the music).
ColorVoice  = SPEAKER VOICING — the knob sweeps the band EDGES themselves (20 Hz-320 Hz at the
              bottom, 18 kHz-3 kHz at the top, two poles each) and adds the cone
              resonance a small driver in a small box has. Nothing dry is blended
              back, so the low end really does leave — which is what says ""small
              speaker"". 0 = full-range hi-fi and an exact bypass, 1 = a
              communications speaker.
Drive and Static default OFF. Voice defaults 0.5 — a shelf speaker with a body to it
rather than a hi-fi in a wall. All hot-reload."),
            F("WorldEmitterColorDrive",              "CMMConfig.WorldEmitterColorDrive",            0f),
            F("WorldEmitterColorStatic",             "CMMConfig.WorldEmitterColorStatic",           0f),
            F("WorldEmitterColorVoice",              "CMMConfig.WorldEmitterColorVoice",            0.5f, changedIn: 19, was: 0f),
            Cmt(@"HAM RADIO coloration: the radioHam block's OWN set of the same three levers (the shelf
speakers keep the set above). A ham is a small communications speaker, so it defaults ON —
voiced HARD at 0.9, driven slightly, a whisper of static (""slightly distorted"" is its
character).
Set all three to 0 for a clean ham. All hot-reload."),
            F("WorldEmitterHamColorDrive",           "CMMConfig.WorldEmitterHamColorDrive",         0.3f),
            F("WorldEmitterHamColorStatic",          "CMMConfig.WorldEmitterHamColorStatic",        0.04f),
            F("WorldEmitterHamColorVoice",           "CMMConfig.WorldEmitterHamColorVoice",         0.9f, changedIn: 19, was: 0.65f),
        };
    }
}
