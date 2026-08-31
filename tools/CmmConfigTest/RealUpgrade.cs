using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace CMM
{
    // ── UPGRADING FROM THE LAST RELEASE, USING THE FILE THAT ACTUALLY SHIPPED ────────────────
    //
    // UpgradeMatrix writes a two-line radio.xml to exercise one setting's migration. That proves
    // the rule; it does not prove the thing a player does, which is install 1.0.5 over a
    // Config/settings.xml written by 1.0.4b and never touched since.
    //
    // The fixture is that exact file, lifted out of the released 1.0.4b archive. It has NO SCHEMA
    // ATTRIBUTE -- 1.0.4b shipped its config rather than writing it, so the oldest files in the
    // wild carry no stamp at all, and every "did this reach existing installs" question turns on
    // an unstamped file being treated as older than everything.
    internal static class RealUpgrade
    {
        static int _fail;
        static void Ok(string m)  => Console.WriteLine("  ok    " + m);
        static void Bad(string m) { _fail++; Console.WriteLine("  FAIL  " + m); }
        static void Is(bool c, string m) { if (c) Ok(m); else Bad(m); }

        internal static int Run(string repo)
        {
            _fail = 0;
            Console.WriteLine("── upgrading a real 1.0.4b settings.xml " + new string('─', 21));

            string fixture = Path.Combine(repo, "tools", "CmmConfigTest", "fixtures", "settings-1.0.4b.xml");
            if (!File.Exists(fixture))
            {
                Console.WriteLine("  note  no 1.0.4b fixture — skipped");
                return 0;
            }

            string dir = Path.Combine(Path.GetTempPath(), "cmm-realupgrade");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "settings.xml");
            File.Copy(fixture, path, true);

            // What the player had before the upgrade, verbatim.
            var before = Read(path, out string beforeStamp);
            Is(beforeStamp == null, "the 1.0.4b file carries no schema stamp (" + before.Count + " settings)");

            // A DELIBERATE CHOICE, so the migration has something it must not touch. Picked
            // because its default has moved twice since, which is exactly when a migration is
            // most likely to overwrite somebody.
            const string Held = "CrossFadeDuration";
            string heldValue = null;
            if (before.ContainsKey(Held))
            {
                heldValue = "7.5";
                Set(path, Held, heldValue);
            }

            CMMSettingsFile.Ensure(path, "CMMSettings", CMMSettingsSchema.Settings, "settings.xml");
            var after = Read(path, out string afterStamp);

            int cur = CMMSettingsSchema.CurrentSchema;
            Is(afterStamp == cur.ToString(), $"stamped at the current schema after the upgrade (got {afterStamp ?? "none"}, want {cur})");

            // EVERY setting THIS FILE owns must be in it, or the player can never see it and
            // never change it. Settings and radio are two schemas and two files; asking for all
            // of them here was the test being wrong, not the migration.
            Is(Missing(CMMSettingsSchema.Settings, after, out string missNames, out int missN),
               missN == 0 ? "every setting in the settings schema reached the upgraded file ("
                            + after.Count + " total)"
                          : missN + " setting(s) never reached settings.xml: " + missNames);

            // NOTHING THE PLAYER CHOSE IS LOST. A value that is not one of the schema's former
            // defaults is theirs, and must survive untouched.
            if (heldValue != null)
                Is(after.TryGetValue(Held, out string h) && h == heldValue,
                   $"a value the player chose is kept ({Held} = {heldValue})");

            // ...and the file is still a file.
            bool wellFormed = true;
            try { var d2 = new XmlDocument(); d2.Load(path); } catch { wellFormed = false; }
            Is(wellFormed, "the upgraded file is still well-formed XML");

            // The migration must be idempotent: running it again changes nothing at all.
            string once = File.ReadAllText(path);
            CMMSettingsFile.Ensure(path, "CMMSettings", CMMSettingsSchema.Settings, "settings.xml");
            Is(File.ReadAllText(path) == once, "running the upgrade a second time changes nothing");

            Console.WriteLine($"  note  1.0.4b had {before.Count} settings; the upgraded file has {after.Count}.");

            // ── AND THE FILE THAT DID NOT EXIST YET ──────────────────────────────────────────
            //
            // 1.0.4b had no world speakers and so no radio.xml. Upgrading has to WRITE one, whole,
            // rather than leaving the feature configured by nothing -- and a fresh file is the
            // only path where every default is chosen by the code rather than migrated.
            string radio = Path.Combine(dir, "radio.xml");
            if (File.Exists(radio)) File.Delete(radio);
            CMMSettingsFile.Ensure(radio, "CMMRadioSettings", CMMSettingsSchema.Radio, "radio.xml");
            Is(File.Exists(radio), "a 1.0.4b install with no radio.xml gets one written");
            if (File.Exists(radio))
            {
                var rAfter = Read(radio, out string rStamp);
                Is(rStamp == cur.ToString(), $"the new radio.xml is stamped at {cur} (got {rStamp ?? "none"})");
                Is(Missing(CMMSettingsSchema.Radio, rAfter, out string rMissNames, out int rMissN),
                   rMissN == 0 ? "every radio setting is in it (" + rAfter.Count + " total)"
                               : rMissN + " radio setting(s) missing: " + rMissNames);
            }

            return _fail;
        }

        // True when nothing is missing. Names at most a handful, because a list of 135 is not a
        // report, it is a wall.
        static bool Missing(SchemaEntry[] schema, Dictionary<string, string> have,
                            out string names, out int count)
        {
            var missing = new List<string>();
            foreach (SchemaEntry e in schema)
                if (e.Setting != null && !have.ContainsKey(e.Setting.Name)) missing.Add(e.Setting.Name);
            count = missing.Count;
            names = count == 0 ? "" : string.Join(", ", missing.GetRange(0, Math.Min(6, count)).ToArray())
                                    + (count > 6 ? $" (+{count - 6} more)" : "");
            return count == 0;
        }

        static Dictionary<string, string> Read(string path, out string stamp)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var doc = new XmlDocument();
            doc.Load(path);
            stamp = doc.DocumentElement.HasAttribute("schema")
                  ? doc.DocumentElement.GetAttribute("schema") : null;
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
                if (n.NodeType == XmlNodeType.Element) d[n.Name] = n.InnerText;
            return d;
        }

        static void Set(string path, string name, string value)
        {
            var doc = new XmlDocument();
            doc.Load(path);
            if (doc.DocumentElement.SelectSingleNode(name) is XmlElement el) el.InnerText = value;
            doc.Save(path);
        }
    }
}
