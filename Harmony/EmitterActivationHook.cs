#if GV_V3
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // THE TUNING GESTURE, MADE OUT OF THE GAME'S OWN PARTS.
    //
    // Hold the activate key on a speaker → the vanilla radial opens → move to a station → release.
    // That is not an imitation of the block-interaction gesture; it IS it. The mod draws nothing:
    // it answers four questions the game already asks of every block the player looks at, and the
    // game does the prompt, the hold timing, the radial, the mouse tracking and the release.
    //
    //   HasBlockActivationCommands  →  true, so a prompt and a radial appear at all
    //   GetActivationText           →  "{0} Tune Ham Radio ([DECEA3]Classic Rock[-])"
    //   GetBlockActivationCommands  →  Automatic, every station, Silent
    //   OnBlockActivated(name, …)   →  the choice
    //
    // The first version of this drew its own IMGUI prompt and list and read EntityPlayerLocal.
    // HitInfo directly, because the game's prompt pipeline (PlayerMoveController.TryHandleBlock)
    // is a LOCAL FUNCTION whose mangled name is a compiler artifact and cannot be patched safely.
    // That reasoning was right about TryHandleBlock and wrong about the conclusion: the four
    // methods above are ordinary public virtuals on Block, they are what TryHandleBlock CALLS, and
    // answering them gets the native look for free rather than by imitation.
    //
    // ── The arity split, which Harmony solves for free here ─────────────────────────────────
    // All four carry the same 2.6→3.x divergence as IsSeeThrough: 2.6 takes an extra `int _clrIdx`.
    //
    //   3.x   GetBlockActivationCommands(WorldBase _world, BlockValue _blockValue, Vector3i _blockPos, EntityAlive _entityFocusing)
    //   2.6   GetBlockActivationCommands(WorldBase _world, BlockValue _blockValue, int _clrIdx, Vector3i _blockPos, EntityAlive _entityFocusing)
    //
    // Elsewhere that split costs a compat shim per member (EmitterCompat.IsSeeThrough,
    // HasVanillaUse). Not here: Harmony binds a patch's parameters BY NAME, so a postfix that
    // declares only `_blockPos` and `__result` matches both shapes, and the parameter we do not
    // want is simply not named. The targets are resolved by name too, so nothing in this file
    // emits a token that has to exist on a version it does not run on.
    //
    // ── Why two target types ────────────────────────────────────────────────────────────────
    // radioHam and cntStoreElectronicsBookShelfSpeaker are plain decoration (they extend
    // decoEntityMetalMaster / decoEntityPolymerMaster with no Class property), so they are `Block`
    // itself. cntStoreShelfElectronicsSingleBottomSpeaker inherits Class=CompositeTileEntity and a
    // loot list from cntStoreShelfElectronicsSingleBottomPC, so it is BlockCompositeTileEntity —
    // which OVERRIDES all four and does NOT call base. Patching only `Block` would silently miss
    // the shelf speaker; patching only the override would miss the other two.
    //
    // And this is why the gesture no longer needs a modifier. The shelf speaker's loot command and
    // our station commands are entries in the SAME radial, which is exactly how the game already
    // handles a block that can do more than one thing. Nothing is stolen from anybody.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class EmitterActivation
    {
        // Our commands carry this prefix in their `text`, which is what OnBlockActivated is handed
        // back and what the radial localizes. The station name rides after it, so the round trip
        // needs no side table keyed by index — the game hands us the identity of the thing chosen.
        internal const string CmdPrefix = "cmmTune:";

        // The radial's block-command path renders `Localization.Get("blockcommand_" + text)`, and
        // Localization.Get returns the KEY UNCHANGED when it is missing — so an unregistered
        // command shows up as the literal "blockcommand_cmmTune:Classic Rock". Localization
        // .mDictionary is a public static Dictionary<string,string[]> indexed by language column,
        // so the fix is to register the entry rather than to patch the radial: one row per choice,
        // filled at both the current and default language columns.
        private static readonly HashSet<string> _localized = new HashSet<string>(StringComparer.Ordinal);

        internal static void RegisterLabel(string cmdText, string label)
        {
            try
            {
                string key = "blockcommand_" + cmdText;
                if (!_localized.Add(key)) return;
                var dict = Localization.mDictionary;
                if (dict == null) return;
                int cur = Localization.currentLanguageIndex, def = Localization.defaultLanguageIndex;
                int n = Math.Max(Math.Max(cur, def), 0) + 1;
                var row = new string[n];
                for (int i = 0; i < n; i++) row[i] = label;
                dict[key] = row;
            }
            catch { /* a missing label is cosmetic; never break a block's activation for it */ }
        }

        // Both declaring types, resolved by name. A null (a type or member this game does not have)
        // is dropped rather than returned: Harmony aborts the WHOLE PatchAll on a null target, and
        // taking the emitter layer's UI down with the rest of the mod would be a poor trade.
        internal static IEnumerable<MethodBase> Targets(string name)
        {
            foreach (string typeName in new[] { "Block", "BlockCompositeTileEntity" })
            {
                Type t = AccessTools.TypeByName(typeName);
                MethodBase m = t == null ? null : AccessTools.DeclaredMethod(t, name);
                if (m != null) yield return m;
            }
        }

        internal static bool Ready()
            => EmitterCompat.RuntimeSupported && ContextMusicManager.inGame;

        // Icons are drawn as "ui_game_symbol_" + icon by the radial's block path, so these are
        // names the atlas actually has — taken from what vanilla blocks already use.
        internal const string IconStation = "electric_switch";
        internal const string IconAuto    = "search";
        internal const string IconSilent  = "x";
    }

    // A speaker must ANSWER the game's "is there anything to do here" question, or no prompt and
    // no radial ever appear. Decoration answers false by default, which is why a ham radio was
    // inert until now.
    [HarmonyPatch]
    internal static class Patch_EmitterHasCommands
    {
        static bool Prepare() => EmitterCompat.RuntimeSupported;
        static IEnumerable<MethodBase> TargetMethods() => EmitterActivation.Targets("HasBlockActivationCommands");

        static void Postfix(Vector3i _blockPos, ref bool __result)
        {
            if (__result) return;                       // the block already had its own reasons
            try
            {
                if (!EmitterActivation.Ready()) return;
                EmitterRegistry.Emitter e;
                if (EmitterTuner.EmitterAt(_blockPos, out e)) __result = true;
            }
            catch { /* a block must never break because of us */ }
        }
    }

    // The line under the crosshair. PlayerMoveController runs string.Format(text, keyMarkup) on
    // whatever comes back, so the returned string carries exactly one {0} and no other braces —
    // the station name is escaped, because a folder called "Rock {Live}" would otherwise throw
    // inside the game's own formatter.
    //
    // The shape matches vanilla's own `tooltipUnlocked` ("{0} Use {1} ([DECEA3]Unlocked[-])"), so
    // it reads as the game's prompt because it is built to the game's pattern.
    [HarmonyPatch]
    internal static class Patch_EmitterActivationText
    {
        static bool Prepare() => EmitterCompat.RuntimeSupported;
        static IEnumerable<MethodBase> TargetMethods() => EmitterActivation.Targets("GetActivationText");

        static void Postfix(Vector3i _blockPos, ref string __result)
        {
            try
            {
                if (!EmitterActivation.Ready()) return;
                EmitterRegistry.Emitter e;
                if (!EmitterTuner.EmitterAt(_blockPos, out e)) return;
                // A block with its own text (the shelf speaker says "Search") keeps it: the radial
                // is where both live, and overwriting it would hide the loot from the player.
                if (!string.IsNullOrEmpty(__result)) return;
                __result = "{0} Tune " + Escape(Pretty(e.Name))
                         + " ([DECEA3]" + Escape(EmitterTuner.CurrentLabel(e)) + "[-])";
            }
            catch { }
        }

        // Block names are internal ids (radioHam, cntStoreElectronicsBookShelfSpeaker). The
        // localized block name is the right thing to show, and GetLocalizedBlockName is the game's
        // own accessor for it — but it needs the Block, and all we carry in the snapshot is the
        // registry's display name, so this is the fallback tidy-up for the id form.
        private static string Pretty(string name)
            => name == "radioHam" ? "Radio" : "Speaker";

        // string.Format runs over this twice (ours, then PlayerMoveController's). Braces from a
        // station folder name would be read as placeholders by the second pass.
        private static string Escape(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("{", "{{").Replace("}", "}}");
    }

    // The radial's contents. Appended to whatever the block already offers, so the shelf speaker
    // keeps its loot entry and gains ours below it.
    [HarmonyPatch]
    internal static class Patch_EmitterCommands
    {
        static bool Prepare() => EmitterCompat.RuntimeSupported;
        static IEnumerable<MethodBase> TargetMethods() => EmitterActivation.Targets("GetBlockActivationCommands");

        // The XUi radial is 13 segments (windowRadial repeat_count="13"). Past that the entries
        // exist but have nowhere to draw, so the list is capped and the drop is LOGGED rather than
        // silently truncated — a station that cannot be chosen should not look like one that can.
        private const int RadialSegments = 13;
        private static readonly List<string> _choices = new List<string>();
        private static bool _warnedFull;

        static void Postfix(Vector3i _blockPos, ref BlockActivationCommand[] __result)
        {
            try
            {
                if (!EmitterActivation.Ready()) return;
                EmitterRegistry.Emitter e;
                if (!EmitterTuner.EmitterAt(_blockPos, out e)) return;

                EmitterTuner.EnsureLoaded();
                EmitterTuner.BuildChoices(_choices);
                if (_choices.Count == 0) return;

                var have = __result ?? new BlockActivationCommand[0];
                int room = RadialSegments - have.Length;
                if (room <= 0) return;
                int take = Math.Min(room, _choices.Count);
                if (take < _choices.Count && !_warnedFull)
                {
                    _warnedFull = true;
                    Log.Warning($"[CMM] Emitter tuner: {_choices.Count - take} station(s) do not fit "
                              + $"the {RadialSegments}-segment radial and are not offered here.");
                }

                string current = EmitterTuner.CurrentLabel(e);
                // A NEW ARRAY, never an append in place: BlockCompositeTileEntity returns a CACHED
                // field (`commands`), and growing the game's own array would leak our entries into
                // every composite block in the world and grow it again on every look.
                var outp = new BlockActivationCommand[have.Length + take];
                Array.Copy(have, outp, have.Length);
                for (int i = 0; i < take; i++)
                {
                    string choice = _choices[i];
                    string text = EmitterActivation.CmdPrefix + choice;
                    EmitterActivation.RegisterLabel(text, choice);
                    string icon = choice == EmitterTuner.AutoLabel   ? EmitterActivation.IconAuto
                                : choice == EmitterTuner.SilentLabel ? EmitterActivation.IconSilent
                                :                                      EmitterActivation.IconStation;
                    // BUILT BY FIELD, NOT BY CONSTRUCTOR. BlockActivationCommand's ctor gained a
                    // fifth parameter (eventName) after A21, so `new BlockActivationCommand(a,b,c,d)`
                    // compiles to a FIVE-argument call against 3.x that A20/A21 do not declare --
                    // a reference this never executes but still emits, and CmmApiDiff counts
                    // references, not executions. The four fields are identical on all four
                    // versions. `highlighted` marks the one already playing, so the radial opens
                    // showing you where you are rather than making you remember.
                    var cmd = default(BlockActivationCommand);
                    cmd.text = text;
                    cmd.icon = icon;
                    cmd.enabled = true;
                    cmd.highlighted = choice == current;
                    outp[have.Length + i] = cmd;
                }
                __result = outp;
            }
            catch { /* a block must never break because of us */ }
        }
    }

    // The choice. The radial hands OnBlockActivated the command's `text`, so the station name comes
    // back with it and there is no index to keep in sync with the list that produced it.
    //
    // A PREFIX, not a postfix, and it swallows ours: BlockCompositeTileEntity forwards an unknown
    // command name to its tile entity, which logs and can misbehave on a name it has never heard.
    // Vanilla commands fall straight through untouched.
    [HarmonyPatch]
    internal static class Patch_EmitterActivated
    {
        static bool Prepare() => EmitterCompat.RuntimeSupported;
        static IEnumerable<MethodBase> TargetMethods() => EmitterActivation.Targets("OnBlockActivated");

        static bool Prefix(string _commandName, Vector3i _blockPos, ref bool __result)
        {
            try
            {
                if (_commandName == null || !_commandName.StartsWith(EmitterActivation.CmdPrefix,
                                                                     StringComparison.Ordinal))
                    return true;                                   // not ours — run the original
                if (!EmitterActivation.Ready()) { __result = false; return false; }

                EmitterRegistry.Emitter e;
                if (!EmitterTuner.EmitterAt(_blockPos, out e)) { __result = false; return false; }

                EmitterTuner.Apply(e, _commandName.Substring(EmitterActivation.CmdPrefix.Length));
                __result = true;
                return false;                                      // handled; skip the original
            }
            catch { }
            return true;
        }
    }
}
#endif
