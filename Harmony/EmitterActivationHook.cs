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

        // Both declaring types, resolved by name.
        //
        // THIS RESOLVES BY ENUMERATION, NOT BY AccessTools.DeclaredMethod(type, name), AND THE
        // DIFFERENCE TOOK THE WHOLE MOD DOWN ONCE. Block declares TWO OnBlockActivated overloads --
        // (string, ...) for a named radial command and (...) without one for a plain activate --
        // so a name-only lookup throws AmbiguousMatchException. That throw happens inside
        // TargetMethods, which Harmony calls during PatchAll, so it does not merely skip this
        // patch: it aborts PatchAll, and every patch in the mod with it. The log read
        // "Failed initializing ModAPI instance on mod 'ContextMusicManager'" -- no music, no
        // radio, no emitters, from one ambiguous name.
        //
        // Passing an explicit parameter-type array is not the fix either, because the arity
        // differs between 2.6 and 3.x (the extra _clrIdx). So: enumerate the DECLARED methods,
        // match on the name, and disambiguate on the shape that is stable across versions --
        // whether the first parameter is the command name. That is true of the overload we want
        // on both versions and false of the other one on both.
        //
        // Nothing here throws. A missing type, a renamed member, an unexpected shape: all yield
        // an empty list, which patches nothing and leaves the rest of the mod alone. That is the
        // whole point of the [[CmmGuard]] containment convention, and this file learned it the
        // expensive way.
        internal static List<MethodBase> Targets(string name)
        {
            var found = new List<MethodBase>();
            try
            {
                bool wantsCommandName = name == "OnBlockActivated";
                foreach (string typeName in new[] { "Block", "BlockCompositeTileEntity" })
                {
                    Type t = AccessTools.TypeByName(typeName);
                    if (t == null) continue;
                    MethodInfo[] all = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < all.Length; i++)
                    {
                        MethodInfo m = all[i];
                        if (m.Name != name) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        bool hasCommandName = ps.Length > 0 && ps[0].ParameterType == typeof(string);
                        if (hasCommandName != wantsCommandName) continue;
                        found.Add(m);
                        break;                       // one per declaring type
                    }
                }
                if (found.Count == 0)
                    Log.Warning($"[CMM] World emitters: no '{name}' to hook on this game version — "
                              + "speakers still play, but they cannot be tuned in-world.");
            }
            catch (Exception e)
            {
                Log.Warning("[CMM] World emitters: could not resolve '" + name + "' (" + e.Message
                          + ") — in-world tuning is off, everything else is unaffected.");
                found.Clear();
            }
            return found;
        }

        internal static bool Ready()
            => EmitterCompat.RuntimeSupported && ContextMusicManager.inGame;

        // Prepare() for all four patches. Also requires the target to actually RESOLVE, so a
        // version that has renamed one of these declines the patch here rather than handing
        // Harmony an empty method set to interpret.
        internal static bool Supported(string name)
        {
            try { return EmitterCompat.RuntimeSupported && Targets(name).Count > 0; }
            catch { return false; }
        }

        // Which command index carries which two-character badge, filled while the command list is
        // built and read back by the radial postfix a moment later. Keyed by CommandIndex because
        // that is the only identity XUiC_RadialEntry keeps.
        private static readonly Dictionary<int, string> _badges = new Dictionary<int, string>();
        internal static bool HasBadges { get { return _badges.Count > 0; } }
        internal static void BeginEntries(int firstIndex) { _badges.Clear(); }
        internal static void NoteBadge(int commandIndex, string badge)
        {
            if (!string.IsNullOrEmpty(badge)) _badges[commandIndex] = badge;
        }
        internal static string BadgeFor(int commandIndex)
        {
            string b;
            return _badges.TryGetValue(commandIndex, out b) ? b : null;
        }

        // XUiC_Radial, by name. The radial is XUi, which is the part of the game that differs most
        // between versions -- but Open/SetCurrentBlockData/CreateRadialEntry are byte-identical on
        // 2.6 and 3.2 apart from SetCurrentBlockData's extra 2.6 clrIdx, which Harmony's by-name
        // parameter binding absorbs. Resolved defensively all the same: badges and paging are
        // cosmetic next to the station actually changing.
        internal static MethodBase RadialMethod(string name)
        {
            try
            {
                Type t = AccessTools.TypeByName("XUiC_Radial");
                if (t == null) return null;
                MethodInfo[] all = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                              | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (int i = 0; i < all.Length; i++)
                    if (all[i].Name == name) return all[i];
            }
            catch { }
            return null;
        }

        internal static bool SupportedRadial()
        {
            try { return EmitterCompat.RuntimeSupported && RadialMethod("SetCurrentBlockData") != null; }
            catch { return false; }
        }

    }

    // A speaker must ANSWER the game's "is there anything to do here" question, or no prompt and
    // no radial ever appear. Decoration answers false by default, which is why a ham radio was
    // inert until now.
    [HarmonyPatch]
    internal static class Patch_EmitterHasCommands
    {
        static bool Prepare() => EmitterActivation.Supported("HasBlockActivationCommands");
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
        static bool Prepare() => EmitterActivation.Supported("GetActivationText");
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
                string note = EmitterTuner.PageNote();
                __result = "{0} Tune " + Escape(Pretty(e.Name))
                         + " ([DECEA3]" + Escape(EmitterTuner.CurrentLabel(e)) + "[-])"
                         + (note == null ? "" : "  [999999]" + Escape(note) + "[-]");
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
        static bool Prepare() => EmitterActivation.Supported("GetBlockActivationCommands");
        static IEnumerable<MethodBase> TargetMethods() => EmitterActivation.Targets("GetBlockActivationCommands");

        private static readonly List<EmitterTuner.Choice> _choices = new List<EmitterTuner.Choice>();

        static void Postfix(Vector3i _blockPos, ref BlockActivationCommand[] __result)
        {
            try
            {
                if (!EmitterActivation.Ready()) return;
                EmitterRegistry.Emitter e;
                if (!EmitterTuner.EmitterAt(_blockPos, out e)) return;

                var have = __result ?? new BlockActivationCommand[0];
                // The block's OWN commands come first and are counted against the 13 segments, so
                // the menu shrinks to fit rather than overflowing off the ring.
                EmitterTuner.BuildChoices(e, _choices, have.Length);
                if (_choices.Count == 0) return;

                // A NEW ARRAY, never an append in place: BlockCompositeTileEntity returns a CACHED
                // field (`commands`), and growing the game's own array would leak our entries into
                // every composite block in the world and grow it again on every look.
                var outp = new BlockActivationCommand[have.Length + _choices.Count];
                Array.Copy(have, outp, have.Length);
                EmitterActivation.BeginEntries(have.Length);
                for (int i = 0; i < _choices.Count; i++)
                {
                    EmitterTuner.Choice c = _choices[i];
                    string text = EmitterActivation.CmdPrefix + c.Cmd;
                    EmitterActivation.RegisterLabel(text, c.Label);
                    EmitterActivation.NoteBadge(have.Length + i, c.Badge);
                    // BUILT BY FIELD, NOT BY CONSTRUCTOR. BlockActivationCommand's ctor gained a
                    // fifth parameter (eventName) after A21, so `new BlockActivationCommand(a,b,c,d)`
                    // compiles to a FIVE-argument call against 3.x that A20/A21 do not declare -- a
                    // reference this never executes but still emits, and CmmApiDiff counts
                    // references, not executions. The four fields are identical on all four
                    // versions. `highlighted` marks the one already playing, so the radial opens
                    // showing you where you are rather than making you remember.
                    var cmd = default(BlockActivationCommand);
                    cmd.text = text;
                    cmd.icon = c.Icon;
                    cmd.enabled = true;
                    cmd.highlighted = c.Current;
                    outp[have.Length + i] = cmd;
                }
                __result = outp;
            }
            catch { /* a block must never break because of us */ }
        }
    }

    // THE SEGMENT LABELS. The radial's block path calls CreateRadialEntry with _text = "" and only
    // a _selectionText, so vanilla block menus are icons with the name shown in the centre when you
    // hover one. That is fine for four commands with four distinct icons and useless for ten
    // stations that all share one -- you cannot tell them apart without sweeping over each.
    //
    // XUiC_RadialEntry DOES have a text label (the vanilla radial_entry template gives it a 48x48
    // box at font size 28) and a public SetText. It is a corner badge, not a caption: two
    // characters. So the station initials go there and the full name stays in the centre.
    //
    // This runs as a postfix on SetCurrentBlockData, after the game has created every entry, and
    // writes only the entries whose CommandIndex we claimed.
    [HarmonyPatch]
    internal static class Patch_EmitterRadialBadges
    {
        static bool Prepare() => EmitterActivation.SupportedRadial();
        static MethodBase TargetMethod() => EmitterActivation.RadialMethod("SetCurrentBlockData");

        // menuItem is read REFLECTIVELY, and the reason is divergence kind five: it is a public
        // XUiC_RadialEntry[] on 2.6/3.2 and a private List<XUiC_RadialEntry> on A20/A21. The typed
        // access compiled and CmmApiDiff flagged the emitted field reference on both older
        // versions -- one this never executes, since the emitter layer is off there, but the
        // baseline is worth keeping clean. IList covers an array and a List alike.
        private static System.Reflection.FieldInfo _menuItemField;

        static void Postfix(object __instance)
        {
            try
            {
                if (!EmitterActivation.HasBadges || __instance == null) return;
                if (_menuItemField == null)
                {
                    _menuItemField = AccessTools.Field(__instance.GetType(), "menuItem");
                    if (_menuItemField == null) return;
                }
                var items = _menuItemField.GetValue(__instance) as System.Collections.IList;
                if (items == null) return;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i] as XUiC_RadialEntry;
                    if (it == null) continue;
                    string badge = EmitterActivation.BadgeFor(it.CommandIndex);
                    if (badge != null) it.SetText(badge);
                }
            }
            catch { }
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
        static bool Prepare() => EmitterActivation.Supported("OnBlockActivated");
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

                string cmd = _commandName.Substring(EmitterActivation.CmdPrefix.Length);
                if (EmitterTuner.IsMenuAction(cmd))
                {
                    // PAGING CANNOT REOPEN THE RADIAL, and the first attempt to do so was worse
                    // than not paging at all. XUiC_Radial.Update fires the FIRST ENABLED ENTRY on
                    // any frame where the activate key is not held -- that is the same rule that
                    // gives us tap-to-toggle for free. Reopening from here, after the release that
                    // chose "More", therefore invoked entry 0 on the very next frame: clicking
                    // More switched the radio OFF.
                    //
                    // There is no way around it from outside. The radial is driven entirely by
                    // whether the key is down, and by the time a command reaches us it is up. So
                    // paging advances the page and says so, and the next hold opens on it -- the
                    // page is named in the crosshair prompt so it is not a guess.
                    EmitterTuner.NextPage(e);
                    PersonalRadio.TuneSfxDial();
                }
                else EmitterTuner.Apply(e, cmd);
                __result = true;
                return false;                                      // handled; skip the original
            }
            catch { }
            return true;
        }
    }
}
#endif
