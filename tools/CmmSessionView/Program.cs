using System;
using System.IO;
using System.Windows.Forms;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // CmmSessionView — WinForms entry point. Builds a SessionOptions for the viewer
    // (realtime mixer so real-time mode can actually PLAY; decode on so the mixer is
    // wired) and hands it to the top-down renderer form.
    //
    //   dotnet run --project tools/CmmSessionView -- [music-dir] [--seed n] [--day-minutes m]
    // ─────────────────────────────────────────────────────────────────────────────
    internal static class ViewerProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            var opt = new SessionOptions
            {
                MixerMode = SessionMixerMode.Realtime,  // real device — real-time mode plays audio
                Decode = true,                          // wire the mixer (runtime-gated per speed mode)
                // Skip the loudness model: its ctor prewarm measures one track PER CONTEXT (~200
                // full R128 decodes) synchronously, which would stall the window for many seconds.
                // Real-time audio still plays (unity gain). A background prewarm is a later phase.
                NoLoudness = true,
                ReloadMin = 0,   // no hot-reload flips in the viewer (loads Config/context.lua directly)
                DayMinutes = 60f,
                Seed = 1,
            };

            bool startFast = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed": opt.Seed = int.Parse(args[++i]); break;
                    case "--day-minutes": opt.DayMinutes = float.Parse(args[++i]); break;
                    case "--tick": opt.Tick = float.Parse(args[++i]); break;
                    case "--no-emitters": opt.Emitters = false; break;
                    case "--fast": startFast = true; break;   // launch straight into max-speed (silent)
                    case "-v": opt.Verbose = true; break;
                    default: if (opt.MusicDir == null) opt.MusicDir = args[i]; break;
                }
            }
            if (opt.MusicDir == null) opt.MusicDir = @"C:\_Games\CMM_TestMusic";

            if (!Directory.Exists(opt.MusicDir))
            {
                MessageBox.Show(
                    $"Music library not found:\n{opt.MusicDir}\n\nThe viewer needs a library to play. " +
                    "Pass a folder as the first argument.",
                    "CmmSessionView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            AppDomain.MonitoringIsEnabled = true;

            SessionRunner runner;
            try { runner = new SessionRunner(opt); }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start the session:\n\n" + ex, "CmmSessionView",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!runner.ScriptActive)
            {
                MessageBox.Show("No resolver loaded (MoonSharp/context.lua failed).", "CmmSessionView",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SessionViewForm(runner, startFast));
        }
    }
}
