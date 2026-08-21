using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

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
            "  cmm acoustics verb <n>   - room-sound level behind walls (default 7, lower = less)\n" +
            "  cmm quiet [on|off]       - mute the per-second diagnostics (commands still answer)\n" +
            "  cmm field                - field occlusion: the two taste knobs, live\n" +
            "  cmm field detour <n>     - dB per block of detour (diffraction round a corner)\n" +
            "  cmm field wall <n>       - dB for a fully solid block\n" +
            "  cmm acoustics ray        - the route to the nearest speaker, cell by cell\n" +
            "  cmm sfx                  - radio clicks: test, jitter, samples on|off, vol"
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

            // cmm quiet [on|off] — mute the periodic diagnostics.
            //
            // The debug build prints a dozen lines a second (perf phases, stutters, probe rays,
            // station sync, reverb state, the emitter path) and the game console has no pause and
            // no scrollback worth the name. So the readouts meant to make a problem visible were
            // burying the one number being looked for. A diagnostic you cannot read is not a
            // diagnostic. Command REPLIES are unaffected -- `cmm field` still answers -- so the
            // quiet console is the useful one while tuning by ear.
            if (sub == "quiet")
            {
                bool q = _params.Count < 2 || _params[1].ToLowerInvariant() != "off";
                CMMConfig.DebugLogging = !q;
                CMMConfig.PerfTiming   = !q;
                Out(q ? "periodic logging OFF. Commands still answer: try `cmm field`."
                      + "  `cmm quiet off` puts it back."
                      : "periodic logging back ON.");
                return;
            }

            if (sub == "field") { Out(FieldCommand(_params)); return; }

            if (sub == "acoustics" || sub == "ac")
            {
                Out(AcousticsCommand(_params));
                return;
            }
            if (sub == "sfx")
            {
                Out(SfxCommand(_params));
                return;
            }

            Out("unknown subcommand '" + _params[0] + "'.\n" + Help);
        }

        // ── cmm field [detour|wall|pow] <n> ───────────────────────────────────────
        // The field has exactly two pieces of TASTE in it, and taste is a listening judgement --
        // it has to be made in game, on the same speaker, seconds apart. Every previous attempt to
        // settle these from a log cost days and landed on the wrong number.
        //
        //   detour  what a block of going-the-long-way-round costs. This stands in for
        //           DIFFRACTION: sound bending round a corner loses far more than the extra
        //           metres, and at 0.35 a four-block detour was 1.4 dB where the real thing is
        //           10-15. Safe to raise -- excess is material + (pathLength - straightLine) x
        //           detour, so it scales ONLY genuine detour and open air stays at 0.
        //   wall    what a fully solid block is worth, before the shape exponent.
        //   pow     the shape of the partial-block curve.
        //
        // Nothing is written to disk: put the value you settle on into Config/radio.xml.
        static string FieldCommand(List<string> p)
        {
#if GV_V3
            string a = p.Count > 1 ? p[1].ToLowerInvariant() : "";

            // cmm field occl on|off — THE BISECT, and it should have been the first thing built.
            // With occlusion off every speaker publishes a clear line (extra distance 0) and you
            // hear pure distance falloff. If a symptom survives that, it is not occlusion, and no
            // amount of work on the field will touch it. Hours went into the field on the strength
            // of a symptom that was never bisected against it.
            if (a == "occl" && p.Count > 2)
            {
                bool on = p[2].ToLowerInvariant() == "on";
                CMMConfig.WorldEmitterOcclusion = on;
                return "emitter occlusion " + (on ? "ON" : "OFF — every speaker now publishes a "
                     + "clear line; what you hear is distance alone")
                     + ".\n  If a dip survives OFF, the cause is not occlusion.";
            }

            if ((a == "detour" || a == "wall" || a == "pow" || a == "slew" || a == "aperture") && p.Count > 2
                && float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                string was;
                if (a == "detour") { was = CMMConfig.WorldEmitterFieldDetourDb.ToString("0.00"); CMMConfig.WorldEmitterFieldDetourDb = v; }
                else if (a == "wall") { was = CMMConfig.WorldEmitterFieldWallDb.ToString("0.0"); CMMConfig.WorldEmitterFieldWallDb = v; }
                else if (a == "pow") { was = CMMConfig.WorldEmitterFieldWallPow.ToString("0.00"); CMMConfig.WorldEmitterFieldWallPow = v; }
                else if (a == "slew") { was = CMMConfig.WorldEmitterFieldSlewDb.ToString("0.0"); CMMConfig.WorldEmitterFieldSlewDb = v; }
                else { was = CMMConfig.WorldEmitterFieldAperturePow.ToString("0.00"); CMMConfig.WorldEmitterFieldAperturePow = v; }
                // wall/pow change what each BLOCK is worth, so the stored geometry is now wrong and
                // has to be read again; detour is used by the flood alone and lands by itself.
                if (a == "wall" || a == "pow" || a == "aperture") FieldOcclusion.RereadAll();
                return "field " + a + ": " + was + " -> " + v.ToString("0.00")
                     + (a == "wall" || a == "pow" || a == "aperture" ? "  (re-reading the world, a second or two)"
                                                  : "  (next flood, ~0.3 s)")
                     + "\n  " + FieldOcclusion.Status();
            }
            return "field occlusion knobs — dial these by ear while the music plays:"
                 + "\n  detour " + CMMConfig.WorldEmitterFieldDetourDb.ToString("0.00")
                 + "   dB per block of detour. 1-3 is the sensible band: a corner adds a few blocks of path, and diffraction round an edge is 10-15 dB."
                 + "\n  wall   " + CMMConfig.WorldEmitterFieldWallDb.ToString("0.0")
                 + "     dB for a fully solid block"
                 + "\n  pow    " + CMMConfig.WorldEmitterFieldWallPow.ToString("0.00")
                 + "   shape of the partial-block curve"
                 + "\n  apert  " + CMMConfig.WorldEmitterFieldAperturePow.ToString("0.00")
                 + "   how hard a PARTLY OPEN face attenuates (open fraction ^ this). THE one that decides how solid a real building sounds. 1 = linear area, 2 = default, higher = heavier."
                 + "\n  slew   " + CMMConfig.WorldEmitterFieldSlewDb.ToString("0.0")
                 + "     dB per second the level may change (0 = off, raw floods)"
                 + "\n  " + FieldOcclusion.Status()
                 + "\n  " + FieldOcclusion.DescribeNearest()
                 + "\n  occl   " + (CMMConfig.WorldEmitterOcclusion ? "ON" : "OFF")
                 + "      master bypass — off = clear line for every speaker (the bisect)"
                 + "\n  usage: cmm field occl on|off   |   cmm field detour|wall|pow|slew|aperture <n>"
                 + "\n         cmm acoustics ray";
#else
            return "field occlusion: not built into this game version";
#endif
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
        //
        // GATED TO GV_V3, like the model it drives. EmitterAcoustics / AcousticBridge / EmitterAudioLayer
        // are all `#if GV_V3` (they need world APIs the Alphas and 2.6 don't have), and this command was
        // the last caller of them left outside a gate — so `-t:DeployAll` compiled 3.1 and then failed the
        // other three with five CS0103s. Caught only because a full four-version build was run; a 3.1-only
        // build is green either way, which is how it went unnoticed.
        static string AcousticsCommand(List<string> p)
        {
#if GV_V3
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
                // `verb off` IS NOT `verb 0`. The reverb knob is a dB OFFSET, so zero means "no offset" —
                // a 7 dB reduction and nothing more — not "no room sound". The one experiment this knob
                // exists for ("is the reverberant field what bleeds through this wall?") needs the field
                // genuinely gone, and asking for it by the obvious name came back inconclusive because the
                // field was still there at nearly full strength. -120 dB is gone.
                string tok = p.Count > 2 ? p[2] : null;
                bool off = arg == "verb" && tok != null && tok.ToLowerInvariant() == "off";
                if (off || (tok != null && double.TryParse(tok, System.Globalization.NumberStyles.Float,
                                                   System.Globalization.CultureInfo.InvariantCulture, out _)))
                {
                    double v = off ? -120.0
                                   : double.Parse(tok, System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture);
                    if (arg == "wall") NativeAcoustics.cmm_ac_set_tuning(v, -1, NativeAcoustics.Keep);
                    else               NativeAcoustics.cmm_ac_set_tuning(-1, -1, v);
                    double was = (arg == "wall") ? mat : cal;   // captured BEFORE the set
                    AcousticBridge.InvalidateGeometry();
                    return (arg == "wall" ? "material scale " : "reverb level ")
                         + (off ? "OFF" : v.ToString("0.##"))
                         + "  (was " + (was <= -100 ? "OFF" : was.ToString("0.##")) + ")"
                         + "; rebuilding, live in ~0.5 s";
                }
                return "material scale " + mat.ToString("0.##") + " (default 40.1 -- higher = walls block more)"
                     + "\n  reverb level " + (cal <= -100 ? "OFF" : cal.ToString("0.##"))
                     + " (default 7 -- dB, lower = less room sound behind walls; 'off' = none at all)"
                     + "\n  usage: cmm acoustics wall <n> | cmm acoustics verb <n>|off";
            }

            // ── cmm acoustics column ──────────────────────────────────────────────────────
            // Every block between the player and the nearest speaker, with what the model thinks
            // each one IS: the raw loss CellLoss returns, the openness the router derives from it,
            // and the block's name and shape. Exists because three separate guesses about what a
            // floor is made of have now been wrong, and the only thing that can settle it is the
            // actual column in the actual world.
            if (arg == "column" || arg == "col")
            {
                var pl = GameManager.Instance?.World?.GetPrimaryPlayer();
                var w = GameManager.Instance?.World;
                if (pl == null || w == null) return "no player";
                Vector3 ear = pl.getHeadPosition();
                Vector3 spk;
                if (!EmitterAudioLayer.TryGetNearestSpeaker(ear, out spk)) return "no speaker in range";
                var sb = new System.Text.StringBuilder();
                float dist = Vector3.Distance(ear, spk);
                sb.Append("ear ").Append(ear.ToCultureInvariantString())
                  .Append("  ->  speaker ").Append(spk.ToCultureInvariantString())
                  .Append("   d=").Append(dist.ToString("0.0"));
                int steps = Mathf.Clamp(Mathf.CeilToInt(dist * 2f), 1, 64);
                var seen = new HashSet<Vector3i>();
                for (int i = 0; i <= steps; i++)
                {
                    Vector3 sp = Vector3.Lerp(ear, spk, i / (float)steps);
                    var v = new Vector3i(Mathf.FloorToInt(sp.x), Mathf.FloorToInt(sp.y), Mathf.FloorToInt(sp.z));
                    if (!seen.Add(v)) continue;
                    BlockValue bv = w.GetBlock(v.x, v.y, v.z);
                    float loss = bv.isair ? 0f : EmitterAcoustics.DebugCellLoss(bv, w, v.x, v.y, v.z);
                    // the same ramp the router uses, so this reports what it actually sees
                    float open = bv.isair ? 1f
                               : loss >= 0.5f ? 0f
                               : loss <= 0.05f ? 1f
                               : 1f - (loss - 0.05f) / 0.45f;
                    var b = bv.Block;
                    sb.Append("\n  ").Append(v.ToString().PadRight(20))
                      .Append(bv.isair ? "AIR" : (b != null ? b.GetBlockName() : "?"))
                      .Append("  loss ").Append(loss.ToString("0.000"))
                      .Append("  open ").Append(open.ToString("0.00"))
                      .Append(bv.isair || b == null ? "" :
                              "  shape " + (b.shape != null ? b.shape.GetType().Name : "-")
                              + (b.shape != null && b.shape.IsSolidCube ? " CUBE" : " partial"));
                }
                return sb.ToString();
            }

            // cmm acoustics ray — what the FIELD charges along the straight line to the nearest
            // speaker, cell by cell, next to the geometry it read. `column` answers the same
            // question for the other model and at block resolution; this one is about the field.
            if (arg == "ray")
            {
                var plr = GameManager.Instance?.World?.GetPrimaryPlayer();
                var wr = GameManager.Instance?.World;
                if (plr == null || wr == null) return "no player";
                Vector3 rear = plr.getHeadPosition();
                Vector3 rspk;
                // Default to the point the `field nearest:` line is about, NOT the nearest
                // speaker: inside a POI those are different objects, and the nearest one is
                // usually the one already behaving. `ray speaker` asks for the old behaviour.
                bool wantSpeaker = p.Count > 2 && p[2].ToLowerInvariant() == "speaker";
                if (wantSpeaker || !FieldOcclusion.TryGetReportedPoint(out rspk))
                {
                    if (!EmitterAudioLayer.TryGetNearestSpeaker(rear, out rspk))
                        return "no speaker in range";
                }
                return FieldOcclusion.DescribeRay(wr, rear, rspk);
            }

            if (arg == "rebuild")
            {
                AcousticBridge.InvalidateGeometry();
                return "scene invalidated; the scheduler rebuilds it within ~0.5 s";
            }

            // ── cmm acoustics model auto|native|legacy ────────────────────────────────────
            // Hear ONE occlusion model instead of three switching under you. See the comment on
            // EmitterAudioLayer.PinNative for why this exists.
            if (arg == "model")
            {
                string m = p.Count > 2 ? p[2].ToLowerInvariant() : "";
                if (m == "auto" || m == "native" || m == "legacy" || m == "field")
                {
                    EmitterAudioLayer.SetPin(m);
                    if (m == "field")
                        return "occlusion model pinned to FIELD - the rebuild: one field centred on"
                             + " you, read by every speaker.\n  " + FieldOcclusion.Status()
                             + "\n  give it a few seconds to read the world in; it is"
                             + " quiet-but-correct close by while it does.";
                    return "occlusion model pinned to " + m.ToUpperInvariant()
                         + (m == "native" ? "  (never hands back - a door mid-swing will sound wrong for a moment; that is expected)"
                          : m == "legacy" ? "  (native + field cache both skipped)"
                          : "  (ship behaviour: all three, switched live)");
                }
                return "occlusion model: " + EmitterAudioLayer.PinName
                     + "\n  usage: cmm acoustics model auto|native|legacy";
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
                 + "\n  model:     " + EmitterAudioLayer.PinName
                 + "\n  usage:     cmm acoustics on|off|rebuild|here|column|ray [speaker]"
                 + "\n             cmm acoustics wall <n> | verb <n>|off | model auto|native|legacy";
#else
            return "native acoustics: not built into this game version";
#endif
        }

        // ── cmm sfx ───────────────────────────────────────────────────────────────────
        // The radio's clicks, live. The SOUNDS themselves are read once and held in RAM and are
        // deliberately not swappable at runtime, but everything around them is worth turning while
        // listening -- especially the rate variance, which can only be judged by firing several in
        // a row, and `test spin` does that without touching the dial or the station.
        //
        //   cmm sfx                          what is loaded and what it is set to
        //   cmm sfx test [dial|on|off|spin]  fire one, or eight in a row
        //   cmm sfx jitter <hz>              playback-rate variance per firing (0 = identical)
        //   cmm sfx samples on|off           the recorded files vs the synthesized fallback
        //   cmm sfx vol <0..1>               RadioSfxVolume
        //
        // Nothing here writes to disk: these are live overrides for judging by ear, and the answer
        // goes into radio.xml once you like it. Saying so in the reply matters -- a setting that
        // silently vanishes on relog is worse than one that was never offered.
        static string SfxCommand(List<string> p)
        {
            const string Keep = "\n  not saved - put it in radio.xml to keep it";
            string a = p.Count > 1 ? p[1].ToLowerInvariant() : "";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var num = System.Globalization.NumberStyles.Float;

            if (a == "jitter" && p.Count > 2 && float.TryParse(p[2], num, inv, out float hz))
            {
                float was = CMMConfig.RadioClickSampleJitterHz;
                CMMConfig.RadioClickSampleJitterHz = hz < 0f ? 0f : hz;
                PersonalRadio.LoadClickSamples();
                return "click rate variance " + CMMConfig.RadioClickSampleJitterHz.ToString("0")
                     + " Hz (was " + was.ToString("0") + ")"
                     + (hz <= 0f ? " - every firing now identical" : "") + Keep;
            }
            if (a == "samples" && p.Count > 2)
            {
                CMMConfig.RadioClickSamples = p[2].ToLowerInvariant() == "on";
                PersonalRadio.LoadClickSamples();
                return "radio clicks: "
                     + (CMMConfig.RadioClickSamples ? "RECORDED (Sounds/)" : "SYNTHESIZED") + Keep;
            }
            if (a == "vol" && p.Count > 2 && float.TryParse(p[2], num, inv, out float v))
            {
                CMMConfig.RadioSfxVolume = v < 0f ? 0f : v > 1f ? 1f : v;
                return "RadioSfxVolume " + CMMConfig.RadioSfxVolume.ToString("0.##") + Keep;
            }
            if (a == "test")
                return PersonalRadio.TestClick(p.Count > 2 ? p[2].ToLowerInvariant() : "dial");

            return PersonalRadio.DescribeClicks()
                 + "\n  usage: cmm sfx test [dial|on|off|spin] | jitter <hz> | samples on|off"
                 + "\n         cmm sfx vol <0..1>";
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
