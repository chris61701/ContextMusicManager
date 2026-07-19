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
            "  cmm nativestop           - stop the native-output player"
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

            Out("unknown subcommand '" + _params[0] + "'.\n" + Help);
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
