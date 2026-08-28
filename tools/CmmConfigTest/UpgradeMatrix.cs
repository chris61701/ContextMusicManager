using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // THE RC3 UPGRADE MATRIX — what actually happens to a real install.
    //
    // FileTests proves the migration MECHANICS against synthetic schemas. This proves the
    // OUTCOME for the two default changes RC3 actually ships, driven through the REAL
    // CMMSettingsFile.Ensure with the REAL CMMSettingsSchema.Radio.
    //
    // The two behave differently and that difference is the whole point:
    //   • WorldEmitterVolume 1.0 -> 0.5 is a SETTING, recorded with changedIn/was, so it
    //     reaches people who already have radio.xml.
    //   • The fade times are in contexts.xml, which is written once and then OWNED by the
    //     user. Nothing migrates it. An existing install keeps its old fades, and saying
    //     otherwise on a release page produces testers who correctly report the change did
    //     nothing. That asymmetry is asserted here so it cannot be forgotten again -- it has
    //     already caught the project out three times.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class UpgradeMatrix
    {
        static int _fail;
        static void Ok(string m)  => Console.WriteLine("  ok    " + m);
        static void Bad(string m) { _fail++; Console.WriteLine("  FAIL  " + m); }
        static void Is(bool c, string m) { if (c) Ok(m); else Bad(m); }

        static string _dir;
        static string RadioPath => Path.Combine(_dir, "radio.xml");

        // Write a radio.xml as it would exist on an install stamped at `schema` with
        // WorldEmitterVolume set to `value` (null = the setting absent entirely).
        static void Given(int schema, string value)
        {
            if (File.Exists(RadioPath)) File.Delete(RadioPath);
            if (schema < 0) return;                       // no file at all: a fresh install
            var sb = new System.Text.StringBuilder();
            sb.Append("<CMMRadioSettings schema=\"").Append(schema).Append("\">\n");
            if (value != null) sb.Append("  <WorldEmitterVolume>").Append(value).Append("</WorldEmitterVolume>\n");
            sb.Append("</CMMRadioSettings>\n");
            File.WriteAllText(RadioPath, sb.ToString());
        }

        static string After()
        {
            CMMSettingsFile.Ensure(RadioPath, "CMMRadioSettings", CMMSettingsSchema.Radio, "radio.xml");
            var doc = new XmlDocument(); doc.Load(RadioPath);
            XmlNode n = doc.DocumentElement.SelectSingleNode("WorldEmitterVolume");
            return n?.InnerText;
        }

        static bool Same(string a, string b)
            => double.TryParse(a, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double x)
            && double.TryParse(b, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double y)
            && Math.Abs(x - y) < 1e-6;

        static void Case(string label, int schema, string had, string want)
        {
            Given(schema, had);
            string got = After();
            Is(Same(got, want), $"{label,-52} {had ?? "(absent)"} -> {got} (wanted {want})");
        }

        internal static int Run(string repoRoot)
        {
            _fail = 0;
            _dir = Path.Combine(Path.GetTempPath(), "cmm_upgrade_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);
            Console.WriteLine("\n══ RC3 upgrade matrix ════════════════════════════════");

            try
            {
                Console.WriteLine("\n  world speaker volume (radio.xml — DOES migrate)");
                Case("fresh install, no file",                      -1,  null,  "0.5");
                Case("had the old default 1.0, untouched",          42,  "1",   "0.5");
                Case("had the ORIGINAL default 0.5 (pre-schema-5)",  4,  "0.5", "0.5");
                Case("chose 0.8 deliberately",                      42,  "0.8", "0.8");
                Case("chose 0.25 deliberately",                     42,  "0.25","0.25");
                Case("chose 0.0 (muted deliberately)",              42,  "0",   "0");
                Case("already at the new schema, untouched",        43,  "0.5", "0.5");
                Case("already at the new schema, customised",       43,  "0.9", "0.9");
                // Deleted-stays-deleted is deliberate (FileTests covers the rule). The file is
                // left alone, and the runtime falls back to the COMPILED default -- which is now
                // 0.5 -- so the player still hears the new level. Assert the effective value, not
                // the file contents; an earlier draft of this case checked the file and "failed"
                // on correct behaviour.
                Given(42, null);
                string absent = After();
                Is(absent == null && Same(SchemaDefault("WorldEmitterVolume"), "0.5"),
                   "deleted setting stays deleted, compiled default 0.5 applies at runtime");
                Case("unstamped file (reads as schema 0)",           0,  "1",   "0.5");

                // The one that CANNOT be asserted by running Ensure, because contexts.xml is
                // not a settings file at all — so assert the fact that makes it true.
                Console.WriteLine("\n  fade times (contexts.xml — does NOT migrate)");
                string ctxSrc = Path.Combine(repoRoot, "Harmony", "ContextMusicManager.ContextStack.cs");
                string code = File.Exists(ctxSrc) ? File.ReadAllText(ctxSrc) : "";
                Is(code.Contains("fadeintime:1.2f"),
                   "the code default for the flickery contexts is 1.2 s");
                Is(!Contains(CMMSettingsSchema.Settings, "fadeintime")
                   && !Contains(CMMSettingsSchema.Radio, "fadeintime"),
                   "fadeintime is NOT a schema setting, so nothing can migrate it");
                Is(code.Contains("only reach a FRESH install") || code.Contains("FRESH install"),
                   "the code says so where someone bumping it will read it");
                Console.WriteLine("        \u2192 an existing contexts.xml keeps its old fades; only a fresh");
                Console.WriteLine("          install (or deleting the file) picks up 1.2 s.");
            }
            finally
            {
                try { Directory.Delete(_dir, true); } catch { }
            }
            return _fail;
        }

        // The schema is the authority on the compiled default, and it is what CMMConfig is
        // initialised from -- so read it here rather than linking the whole config class.
        static string SchemaDefault(string name)
        {
            foreach (SchemaEntry e in CMMSettingsSchema.Radio)
                if (e.Setting != null && e.Setting.Name == name)
                    return Convert.ToString(e.Setting.Default, System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }

        static bool Contains(SchemaEntry[] entries, string name)
        {
            foreach (SchemaEntry e in entries)
                if (e.Setting != null && e.Setting.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
