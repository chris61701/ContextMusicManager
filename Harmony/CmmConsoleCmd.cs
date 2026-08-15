using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace CMM
{
    // In-game console command: `cmm rescan <context|all>` (+ experimental native-audio subs).
    //
    // WHY THIS IS GENERATED AT RUNTIME instead of a plain `class X : ConsoleCmdAbstract`:
    // the base class's abstract member diverges across game versions in BOTH name AND
    // accessibility — A20 `public abstract GetCommands()`, A21 `protected abstract getCommands()`,
    // 2.6/3.0 `public abstract getCommands()`. A single statically-compiled override has one fixed
    // name and one fixed accessibility, so it cannot satisfy A21 (protected) and 2.6/3.0 (public)
    // at once — and a TypeLoad failure would poison our assembly's Assembly.GetTypes(), which is
    // also how the game discovers our IModApi entry point (it could block mod init entirely on the
    // Alphas). Implementing IConsoleCommand directly is no escape: 3.0's interface adds members
    // returning the DeviceFlag type, which is absent on A20/A21 → TypeLoad failure there.
    //
    // So we emit a ConsoleCmdAbstract subclass at load, overriding exactly the abstract methods
    // THIS version's base declares (via DefineMethodOverride, which binds by explicit MethodImpl and
    // is immune to the name/accessibility differences), with bodies that delegate to the static
    // *Impl handlers below. Extending ConsoleCmdAbstract (rather than IConsoleCommand) means the base
    // supplies all the version-specific extras (AllowedDeviceTypes/PrimaryCommand/…) so we never
    // name DeviceFlag. Discovery (ReflectionHelpers.FindTypesImplementingBase) only scans
    // ModManager's loaded assemblies, never a dynamic one, so we register the instance ourselves via
    // a Harmony postfix on SdtdConsole.RegisterCommands() (see Patch_RegisterCmmConsole).
    public static class CmmConsole
    {
        static readonly string[] Names = { "cmm" };
        const string Desc = "Context Music Manager controls";
        const string Help =
            "Usage:\n" +
            "  cmm rescan <context>     - re-scan one subfolder context now\n" +
            "  cmm rescan all           - re-scan every subfolder context now\n" +
            "  cmm rescan               - same as 'all'\n" +
            "  cmm nativeplay <context> - (experimental) play a track via CMM's own\n" +
            "                             audio output, off Unity's threads\n" +
            "  cmm nativenext           - crossfade to another track in that context\n" +
            "  cmm nativestop           - stop the native-output player\n" +
            "  cmm acoustics            - world-emitter acoustic model: state\n" +
            "  cmm acoustics on|off     - switch between the new and shipped model live\n" +
            "  cmm acoustics rebuild    - force a scene rebuild\n" +
            "  cmm acoustics here       - level at the player, broken down by arrival\n" +
            "  cmm acoustics wall <n>   - how much material blocks (default 40.1, higher = more)\n" +
            "  cmm acoustics verb <n>   - room-sound level behind walls (default 7, lower = less)"
#if GV_V3
            + "\n" +
            "  cmm acsweep <poi|here>   - export a POI's acoustic voxel grid + parity\n" +
            "                             floods for the offline judge (also: list/status/cancel)"
#endif
            ;

        // ── Command behavior (the emitted overrides call straight into these) ─────────
        // Public so the runtime-generated type (a separate dynamic assembly) can `call` them.
        public static string[] NamesImpl() => Names;
        public static string DescImpl() => Desc;
        public static string HelpImpl() => Help;

        public static void ExecuteImpl(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params == null || _params.Count == 0) { Out(Help); return; }

            string sub = _params[0].ToLowerInvariant();
            if (sub == "rescan")
            {
                string target = _params.Count > 1 ? _params[1] : "all";
                Out(ContextMusicManager.RescanCommand(target));
                return;
            }
            if (sub == "nativeplay")
            {
                if (_params.Count < 2) { Out("usage: cmm nativeplay <context>"); return; }
                Out(ContextMusicManager.NativeDebugPlay(_params[1]));
                return;
            }
            if (sub == "nativenext")
            {
                Out(ContextMusicManager.NativeDebugNext());
                return;
            }
            if (sub == "nativestop")
            {
                Out(ContextMusicManager.NativeDebugStop());
                return;
            }
#if GV_V3
            if (sub == "acsweep")
            {
                Out(EmitterSweep.Command(_params));
                return;
            }
#endif

            if (sub == "acoustics" || sub == "ac")
            {
                Out(AcousticsCommand(_params));
                return;
            }

            Out("unknown subcommand '" + _params[0] + "'.\n" + Help);
        }

        // ── cmm acoustics ─────────────────────────────────────────────────────────────
        // The A/B switch and the state readout for the native acoustic model. Exists because the
        // difference between the two models is a listening judgement, and a listening judgement
        // needs to be made in game, on the same speaker, seconds apart -- not from two recordings
        // rendered an hour apart by a scaffold that turned out to have bugs of its own.
        //
        //   cmm acoustics            what the bridge is doing right now
        //   cmm acoustics on|off     switch models live, no rebuild, no relog
        //   cmm acoustics rebuild    force a scene rebuild (after changing geometry)
        //   cmm acoustics here       the model's level at the player, broken down by arrival
        static string AcousticsCommand(List<string> p)
        {
            string arg = p.Count > 1 ? p[1].ToLowerInvariant() : "";

            if (arg == "on" || arg == "off")
            {
                CMMConfig.WorldEmitterNativeAcoustics = (arg == "on");
                if (arg == "off") AcousticBridge.Reset();
                return "native acoustics " + (arg == "on" ? "ON" : "OFF")
                     + " (the other model is one 'cmm acoustics " + (arg == "on" ? "off" : "on") + "' away)";
            }

            // ── the two knobs that decide how much a wall is worth ────────────────────────
            // Turned in game, by ear, because that is the only instrument that can judge them.
            // Both are scene inputs, so each sets and then rebuilds -- the number is live within
            // about half a second, which is fast enough to hear the difference while standing in
            // the same spot.
            if (arg == "wall" || arg == "verb")
            {
                if (!NativeAcoustics.Available) return "native unavailable";
                double mat, pow, cal;
                NativeAcoustics.cmm_ac_get_tuning(out mat, out pow, out cal);
                if (p.Count > 2 && double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                                                   System.Globalization.CultureInfo.InvariantCulture, out double v))
                {
                    if (arg == "wall") NativeAcoustics.cmm_ac_set_tuning(v, -1, -1);
                    else               NativeAcoustics.cmm_ac_set_tuning(-1, -1, v);
                    double was = (arg == "wall") ? mat : cal;   // captured BEFORE the set
                    AcousticBridge.InvalidateGeometry();
                    return (arg == "wall" ? "material scale " : "reverb level ") + v.ToString("0.##")
                         + "  (was " + was.ToString("0.##") + ")"
                         + "; rebuilding, live in ~0.5 s";
                }
                return "material scale " + mat.ToString("0.##") + " (default 40.1 -- higher = walls block more)"
                     + "\n  reverb level " + cal.ToString("0.##") + " (default 7 -- lower = less room sound behind walls)"
                     + "\n  usage: cmm acoustics wall <n> | cmm acoustics verb <n>";
            }

            if (arg == "rebuild")
            {
                AcousticBridge.InvalidateGeometry();
                return "scene invalidated; the scheduler rebuilds it within ~0.5 s";
            }

            if (arg == "here")
            {
                var pl = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (pl == null) return "no player";
                if (!AcousticBridge.Ready) return "no scene yet — " + AcousticBridge.LastStatus;
                return AcousticBridge.DescribeAt(pl.getHeadPosition());
            }

            return "native acoustics: " + (CMMConfig.WorldEmitterNativeAcoustics ? "ON" : "OFF")
                 + (NativeAcoustics.Available ? "" : "  [native unavailable]")
                 + "\n  scene:     " + AcousticBridge.LastStatus
                 + "\n  saturated: " + AcousticBridge.SaturatedPoints + " point(s) clamped at last publish"
                 + "\n  usage:     cmm acoustics on|off|rebuild|here";
        }

        static void Out(string s)
        {
            Log.Out("[CMM] " + s.Replace("\n", "\n[CMM] "));
        }

        // ── Runtime registration ──────────────────────────────────────────────────────
        static bool _registered;
        static IConsoleCommand _instance;

        // Called from the SdtdConsole.RegisterCommands postfix. Idempotent. Injects the emitted
        // command into the console's live registries via reflection (private on A20/A21, public on
        // 2.6/3.0): m_Commands (the master list, live-wrapped by m_CommandsReadOnly for listing/help)
        // and m_CommandsAllVariants (the alias→command dictionary the executor looks up).
        public static void Register(object sdtdConsole)
        {
            if (_registered || sdtdConsole == null) return;
            try
            {
                IConsoleCommand cmd = BuildCommand();
                if (cmd == null) return;

                Type consoleType = sdtdConsole.GetType();
                var list = AccessTools.Field(consoleType, "m_Commands")?.GetValue(sdtdConsole) as IList;
                var dict = AccessTools.Field(consoleType, "m_CommandsAllVariants")?.GetValue(sdtdConsole) as IDictionary;
                if (list == null || dict == null)
                {
                    Log.Warning("[CMM] console command registries not found; 'cmm' unavailable this session.");
                    return;
                }

                if (dict.Contains(Names[0])) { _registered = true; return; }  // someone already has it
                list.Add(cmd);
                foreach (var n in Names)
                    if (!dict.Contains(n)) dict[n] = cmd;

                _registered = true;
                Log.Out("[CMM] 'cmm' console command registered (runtime-generated for this game version).");
            }
            catch (Exception e)
            {
                Log.Error("[CMM] failed to register 'cmm' console command (music unaffected).");
                Log.Exception(e);
            }
        }

        // Emit + instantiate a ConsoleCmdAbstract subclass matching the loaded base's abstract
        // methods. Cached; returns null (logged) if emission fails — the mod's music is unaffected.
        static IConsoleCommand BuildCommand()
        {
            if (_instance != null) return _instance;
            try
            {
                Type baseType = typeof(ConsoleCmdAbstract);
                var an = new AssemblyName("CMM.Console.Dynamic");
                AssemblyBuilder ab = AppDomain.CurrentDomain.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
                ModuleBuilder mod = ab.DefineDynamicModule(an.Name);
                TypeBuilder tb = mod.DefineType("CMM.CmmConsoleCmdRuntime",
                    TypeAttributes.Public | TypeAttributes.Class, baseType);
                tb.DefineDefaultConstructor(MethodAttributes.Public);

                // Override every abstract method the loaded base declares (getCommands/GetCommands,
                // getDescription/GetDescription, Execute — the exact set differs by version).
                foreach (MethodInfo bm in baseType.GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (bm.IsAbstract) EmitOverride(tb, bm);
                }
                // GetHelp/getHelp is virtual (not abstract) — override it too so `help cmm` prints
                // our usage instead of the base's null.
                MethodInfo help = FindHelpMethod(baseType);
                if (help != null && help.IsVirtual && !help.IsAbstract) EmitOverride(tb, help);

                Type built = tb.CreateType();
                _instance = (IConsoleCommand)Activator.CreateInstance(built);
                return _instance;
            }
            catch (Exception e)
            {
                Log.Error("[CMM] could not generate the console command type for this game version.");
                Log.Exception(e);
                return null;
            }
        }

        // Emit an override whose body just forwards to the matching static *Impl handler.
        static void EmitOverride(TypeBuilder tb, MethodInfo baseMethod)
        {
            ParameterInfo[] ps = baseMethod.GetParameters();
            var paramTypes = new Type[ps.Length];
            for (int i = 0; i < ps.Length; i++) paramTypes[i] = ps[i].ParameterType;

            MethodBuilder mb = tb.DefineMethod(baseMethod.Name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
                baseMethod.ReturnType, paramTypes);

            MethodInfo impl = SelectImpl(baseMethod);
            ILGenerator il = mb.GetILGenerator();
            // Forward the instance method's args (arg0 = this) to the static impl, then return.
            for (int i = 0; i < impl.GetParameters().Length; i++) il.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
            il.Emit(OpCodes.Call, impl);
            il.Emit(OpCodes.Ret);

            tb.DefineMethodOverride(mb, baseMethod);  // explicit MethodImpl — name/accessibility-proof
        }

        // Map a base method to its handler by shape: string[] → names, void → Execute, string → help
        // or description (by name).
        static MethodInfo SelectImpl(MethodInfo baseMethod)
        {
            if (baseMethod.ReturnType == typeof(string[])) return typeof(CmmConsole).GetMethod(nameof(NamesImpl));
            if (baseMethod.ReturnType == typeof(void))     return typeof(CmmConsole).GetMethod(nameof(ExecuteImpl));
            if (baseMethod.Name.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0)
                return typeof(CmmConsole).GetMethod(nameof(HelpImpl));
            return typeof(CmmConsole).GetMethod(nameof(DescImpl));
        }

        static MethodInfo FindHelpMethod(Type baseType)
        {
            foreach (MethodInfo m in baseType.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.ReturnType == typeof(string) && m.GetParameters().Length == 0
                    && m.Name.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0)
                    return m;
            return null;
        }
    }

    // Registers the runtime-generated `cmm` command right after the game finishes its own command
    // discovery, on the same SdtdConsole instance. Name-resolved + Prepare()-guarded so it no-ops if
    // the method is ever absent/renamed rather than throwing.
    [HarmonyPatch]
    public class Patch_RegisterCmmConsole
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(SdtdConsole), "RegisterCommands");
        static bool Prepare() => TargetMethod() != null;
        static void Postfix(SdtdConsole __instance)
        {
            // Guarded: Register runs Reflection.Emit against a version-divergent base class;
            // a failure must not escape into SdtdConsole.RegisterCommands (console init).
            try { CmmConsole.Register(__instance); }
            catch (Exception e) { CmmGuard.Report("SdtdConsole.RegisterCommands postfix", e); }
        }
    }
}
