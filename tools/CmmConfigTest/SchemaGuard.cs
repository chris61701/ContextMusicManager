using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // THE GUARDRAIL.
    //
    // The whole upgrade story rests on one thing being done by hand: when you
    // change a default, you must bump CurrentSchema and record the old value with
    // changedIn:/was:. Forget it and nothing breaks, nothing warns, and the change
    // silently reaches nobody who already has a config file — which is the failure
    // this design exists to prevent, so it must not be possible to reintroduce it
    // by forgetting.
    //
    // schema-baseline.txt is the committed snapshot of the last released table.
    // Every difference between it and the live schema has to be ACCOUNTED FOR:
    //
    //   default changed  ⇒ DefaultChangedIn must equal CurrentSchema, and
    //                      WasDefault must equal the baseline's value.
    //   setting added    ⇒ Since must equal CurrentSchema.
    //   setting removed  ⇒ reported, and allowed (removals cannot strand anyone;
    //                      the key just goes inert in existing files).
    //   anything changed ⇒ CurrentSchema must be greater than the baseline's.
    //
    // Re-baseline with `dotnet run -- --accept` once the bump is real.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class SchemaGuard
    {
        const string FileName = "schema-baseline.txt";

        struct Row { public string Kind, Default; }

        static Dictionary<string, Row> Snapshot()
        {
            var d = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (SchemaEntry e in CMMSettingsIO.AllEntries())
            {
                SettingDef s = e.Setting;
                if (s == null) continue;
                d[s.Name] = new Row { Kind = s.Kind.ToString(), Default = CMMSettingsIO.Format(s, s.Default) };
            }
            return d;
        }

        static string Render(int schema, Dictionary<string, Row> rows)
        {
            var sb = new StringBuilder();
            sb.Append("# CMM settings baseline — the last released schema.").Append('\n');
            sb.Append("# Regenerate with: dotnet run -- --accept   (only after a real CurrentSchema bump)").Append('\n');
            sb.Append("# REBUILD THIS TOOL FIRST. It COMPILES CMMSettingsSchema.cs directly (see the csproj), so a").Append('\n');
            sb.Append("# stale binary silently checks a stale schema. This file sat at 5 while the code was at 42,").Append('\n');
            sb.Append("# and the guard reported 33 failures against a CurrentSchema of 17 baked in weeks earlier.").Append('\n');
            sb.Append("# Copying a fresh ContextMusicManager.dll beside the exe does nothing - it is not read.").Append('\n');
            sb.Append("schema=").Append(schema).Append('\n');
            foreach (var kv in rows.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('|').Append(kv.Value.Kind).Append('|').Append(kv.Value.Default).Append('\n');
            return sb.ToString();
        }

        internal static int Run(string toolDir, bool accept)
        {
            int fail = 0;
            string path = Path.Combine(toolDir, FileName);
            var live = Snapshot();
            int cur = CMMSettingsSchema.CurrentSchema;

            Console.WriteLine("── schema guard " + new string('─', 38));

            if (accept)
            {
                File.WriteAllText(path, Render(cur, live));
                Console.WriteLine($"  baseline rewritten at schema {cur} ({live.Count} settings).");
                return 0;
            }

            if (!File.Exists(path))
            {
                File.WriteAllText(path, Render(cur, live));
                Console.WriteLine($"  no baseline yet — created at schema {cur} ({live.Count} settings).");
                return 0;
            }

            var baseRows = new Dictionary<string, Row>(StringComparer.Ordinal);
            int baseSchema = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("schema=")) { int.TryParse(line.Substring(7), out baseSchema); continue; }
                string[] p = line.Split('|');
                if (p.Length == 3) baseRows[p[0]] = new Row { Kind = p[1], Default = p[2] };
            }
            // An unreadable stamp must not read as "everything is new since 0", which would pass
            // every check silently. Fall back to the current schema, which fails loudly instead.
            if (baseSchema <= 0) baseSchema = cur;

            var byName = new Dictionary<string, SettingDef>(StringComparer.Ordinal);
            foreach (SchemaEntry e in CMMSettingsIO.AllEntries())
                if (e.Setting != null) byName[e.Setting.Name] = e.Setting;

            Console.WriteLine($"  baseline schema {baseSchema} ({baseRows.Count} settings), code at {cur}.");

            var changed = new List<string>();
            var added = new List<string>();
            var removed = new List<string>();

            foreach (var kv in live)
            {
                if (!baseRows.TryGetValue(kv.Key, out Row b)) { added.Add(kv.Key); continue; }
                if (b.Kind != kv.Value.Kind)
                {
                    fail++;
                    Console.WriteLine($"  FAIL  {kv.Key}: type changed {b.Kind} → {kv.Value.Kind}. "
                                    + "Existing files hold the old form; this needs a hand-written migration.");
                    continue;
                }
                SettingDef s = byName[kv.Key];
                if (!SameParsed(s, b.Default, kv.Value.Default)) changed.Add(kv.Key);
            }
            foreach (var kv in baseRows) if (!live.ContainsKey(kv.Key)) removed.Add(kv.Key);

            // Every changed default must be declared.
            foreach (string name in changed)
            {
                SettingDef s = byName[name];
                Row b = baseRows[name];
                string now = live[name].Default;
                // AGAINST THE BASELINE'S SCHEMA, NOT THE CURRENT ONE.
                //
                // This asked for DefaultChangedIn == CurrentSchema, which is right only if the
                // baseline is regenerated on every single bump. It is not: the baseline is the
                // last RELEASED schema, and a release cycle runs a dozen bumps deep -- 43 here
                // while the code sat at 56. So every legitimate change made during the cycle
                // failed, eighteen of them, and a guard that always fails is a guard nobody reads.
                //
                // The invariant that actually protects a user is the migration's own test
                // (CMMSettingsFile: `s.DefaultChangedIn > fileSchema`), and the oldest file in the
                // wild is stamped at the baseline. So: strictly greater than the baseline.
                if (s.DefaultChangedIn <= baseSchema)
                {
                    fail++;
                    Console.WriteLine($"  FAIL  {name}: default {b.Default} → {now} but DefaultChangedIn is "
                                    + $"{s.DefaultChangedIn}, which is not past the released schema "
                                    + $"{baseSchema}. Pass changedIn: {cur}, was: {b.Default} — otherwise "
                                    + "this change reaches nobody who already has a config file.");
                }
                else if (!AnyFormerDefaultMatches(s, b.Default))
                {
                    fail++;
                    Console.WriteLine($"  FAIL  {name}: was: is '{DescribeWas(s)}' "
                                    + $"but the released default was '{b.Default}'. Users sitting on "
                                    + $"'{b.Default}' will not be migrated.");
                }
                else Console.WriteLine($"  ok    {name}: {b.Default} → {now}, declared correctly");
            }

            // Every added setting must say when it arrived.
            foreach (string name in added)
            {
                SettingDef s = byName[name];
                if (s.Since <= baseSchema)
                {
                    fail++;
                    Console.WriteLine($"  FAIL  {name}: new setting with since: {s.Since}, which is not past "
                                    + $"the released schema {baseSchema}. Existing files would never have it "
                                    + "backfilled.");
                }
                else Console.WriteLine($"  ok    {name}: new in schema {s.Since}");
            }

            foreach (string name in removed)
                Console.WriteLine($"  note  {name}: removed from the schema (goes inert in existing files)");

            bool anyChange = changed.Count > 0 || added.Count > 0 || removed.Count > 0;
            if (anyChange && cur <= baseSchema)
            {
                fail++;
                Console.WriteLine($"  FAIL  the schema changed but CurrentSchema is still {cur} "
                                + $"(baseline {baseSchema}). Bump it.");
            }
            if (!anyChange)
                Console.WriteLine($"  ok    schema unchanged since the baseline ({live.Count} settings, schema {baseSchema})");
            else if (fail == 0)
                Console.WriteLine($"  ok    all {changed.Count + added.Count + removed.Count} change(s) declared — "
                                + "re-run with --accept to update the baseline");

            return fail;
        }

        static bool SameParsed(SettingDef s, string a, string b)
        {
            if (CMMSettingsIO.TryParse(s, a, out object pa) && CMMSettingsIO.TryParse(s, b, out object pb))
                return CMMSettingsIO.SameValue(s, pa, pb);
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    
        // `was:` may carry EVERY default a setting has ever had, not just the previous one —
        // CMMSettingsIO.MatchesAnyFormerDefault takes an object[] for exactly that. The guard
        // formatted s.WasDefault as a single scalar, so the first setting to use the array form
        // AND actually differ from the baseline crashed it with an InvalidCastException instead
        // of reporting anything (WorldEmitterVolume, schema 43). The two had drifted because the
        // array form only ever appeared on settings whose baseline already agreed, which never
        // reached this branch.
        static IEnumerable<object> FormerDefaults(SettingDef s)
        {
            if (s.WasDefault == null) yield break;
            if (s.WasDefault is object[] arr) { foreach (object o in arr) yield return o; }
            else yield return s.WasDefault;
        }

        static bool AnyFormerDefaultMatches(SettingDef s, string baselineDefault)
        {
            foreach (object o in FormerDefaults(s))
                if (SameParsed(s, CMMSettingsIO.Format(s, o), baselineDefault)) return true;
            return false;
        }

        static string DescribeWas(SettingDef s)
        {
            var parts = new List<string>();
            foreach (object o in FormerDefaults(s)) parts.Add(CMMSettingsIO.Format(s, o));
            return parts.Count == 0 ? "null" : string.Join(" / ", parts);
        }
}
}
