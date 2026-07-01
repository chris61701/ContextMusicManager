using System;
using System.IO;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// ReloadHarness — virtualizes the addon's config/script HOT-RELOAD path so the
// stress benchmark exercises it on a fixed sim-cadence, WITHOUT touching the real
// repo Config/context.lua.
//
// Setup copies the repo's context.lua into a throwaway temp dir and points ScriptHost
// there (ScriptHost.Load). Every `reloadMin` SIM-minutes MaybeFlip() rewrites that copy,
// alternating a VALID script (the real one) with a deliberately BROKEN one (cycling the
// three rejection paths: syntax error, wrong API_VERSION, missing resolve), then calls the
// REAL ScriptHost.CheckReload(). An invalid flip must be rejected and the DLL-embedded
// baseline must take over (ScriptHost.UsingEmbeddedDefault) — a valid flip must reload it
// cleanly. Each flip also bumps a config value + ScriptHost.MarkConfigDirty() to drive the
// cfg re-marshal path. See [[lua-decision-layer]] / [[off-frame-loop-decoupling]].
// ─────────────────────────────────────────────────────────────────────────────

namespace CMM
{
    internal sealed class ReloadHarness
    {
        public string WorkDir { get; private set; }
        public int Flips, ToInvalid, ToValid, EmbeddedEngagements, ConfigBumps;

        private string _scriptPath;         // WorkDir/Config/context.lua
        private string _validSrc;
        private readonly string[] _invalidSrc;
        private int _invalidRotor;
        private double _intervalSec;
        private double _nextFlipAt;
        private bool _currentlyInvalid;

        private ReloadHarness()
        {
            _invalidSrc = new[]
            {
                // 1) Syntax error — DoString throws → rejected.
                "API_VERSION = 1\nfunction resolve(gameState, cfg) return { -- unterminated\n",
                // 2) Wrong API version — validation rejects.
                "API_VERSION = 999\nfunction resolve(gameState, cfg) return {} end\n",
                // 3) No resolve function — validation rejects.
                "API_VERSION = 1\nlocal x = 1\n",
            };
        }

        // Returns null (reload virtualization disabled) when reloadMin<=0 or the repo /
        // source context.lua can't be found — otherwise a ready harness with WorkDir set.
        public static ReloadHarness Setup(double reloadMin, string repoRoot, float tick)
        {
            if (reloadMin <= 0 || repoRoot == null) return null;
            string srcPath = Path.Combine(repoRoot, "Config", "context.lua");
            if (!File.Exists(srcPath)) return null;

            var h = new ReloadHarness();
            try
            {
                h._validSrc = File.ReadAllText(srcPath);
                h.WorkDir = Path.Combine(Path.GetTempPath(), "CmmSession_reload_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path.Combine(h.WorkDir, "Config"));
                h._scriptPath = Path.Combine(h.WorkDir, "Config", "context.lua");
                File.WriteAllText(h._scriptPath, h._validSrc);   // start valid
            }
            catch { return null; }

            h._intervalSec = reloadMin * 60.0;
            h._nextFlipAt = h._intervalSec;
            return h;
        }

        // Called each tick with the current sim-time (seconds). Flips valid↔invalid when due.
        public void MaybeFlip(double simNow)
        {
            if (simNow < _nextFlipAt) return;
            _nextFlipAt += _intervalSec;
            Flips++;

            _currentlyInvalid = !_currentlyInvalid;
            string src;
            if (_currentlyInvalid) { src = _invalidSrc[_invalidRotor++ % _invalidSrc.Length]; ToInvalid++; }
            else { src = _validSrc; ToValid++; }

            try
            {
                File.WriteAllText(_scriptPath, src);
                // Force a distinct, increasing write stamp so CheckReload always sees a change
                // (successive flips are microseconds apart in wall time).
                File.SetLastWriteTimeUtc(_scriptPath, DateTime.UtcNow.AddSeconds(Flips));
            }
            catch { /* transient IO — next flip retries */ }

            ScriptHost.CheckReload();
            if (_currentlyInvalid && ScriptHost.UsingEmbeddedDefault) EmbeddedEngagements++;

            // Config reload: bump a tunable + mark dirty so the cfg re-marshal path runs too.
            CMMConfig.EnemyDetectRange = CMMConfig.EnemyDetectRange > 32f ? 30f : 35f;
            ScriptHost.MarkConfigDirty();
            ConfigBumps++;
        }

        public void Cleanup()
        {
            try { if (WorkDir != null && Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); }
            catch { /* best effort */ }
        }
    }
}
