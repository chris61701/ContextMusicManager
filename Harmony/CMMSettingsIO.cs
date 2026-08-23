using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Xml;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // SETTINGS IO — the two halves that drive off CMMSettingsSchema: applying a
    // parsed XML chain onto the CMMConfig / ContextMusicManager fields, and
    // rendering the file back out from those same descriptors.
    //
    // The binding is by reflection rather than a lambda per setting: 133 settings
    // is 266 hand-written accessors otherwise, and the table stops being something
    // you can read or generate. The cost is that a typo'd Target is a runtime
    // problem instead of a compile error, so BindAll() resolves every field once
    // at startup and LOUDLY reports any that does not exist — a schema that does
    // not bind is a bug in the mod, not in the user's file.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CMMSettingsIO
    {
        // Target string -> the static field it names. Resolved once.
        private static readonly Dictionary<string, FieldInfo> _fields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private static bool _bound;

        private static FieldInfo Resolve(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (_fields.TryGetValue(target, out FieldInfo cached)) return cached;

            FieldInfo fi = null;
            int dot = target.LastIndexOf('.');
            if (dot > 0)
            {
                string typeName = "CMM." + target.Substring(0, dot);
                string fieldName = target.Substring(dot + 1);
                Type t = typeof(CMMSettingsIO).Assembly.GetType(typeName);
                if (t != null)
                    fi = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            }
            _fields[target] = fi;
            return fi;
        }

        // Resolve every Target once and shout about anything that does not exist.
        // Called at the top of the first load; cheap (133 GetField calls) and it
        // turns a silent no-op setting into a startup error.
        internal static void BindAll()
        {
            if (_bound) return;
            _bound = true;
            int bad = 0;
            foreach (SchemaEntry e in AllEntries())
            {
                SettingDef s = e.Setting;
                if (s == null || s.Target == null) continue;   // null Target = read by hand
                if (Resolve(s.Target) == null)
                {
                    Log.Error($"[CMM] settings schema: <{s.Name}> targets '{s.Target}', "
                            + "which is not a public static field. This setting will do nothing.");
                    bad++;
                }
            }
            if (bad == 0) CMMConfig.D("[CMM] settings schema bound: all targets resolve.");
        }

        internal static IEnumerable<SchemaEntry> AllEntries()
        {
            foreach (SchemaEntry e in CMMSettingsSchema.Settings) yield return e;
            foreach (SchemaEntry e in CMMSettingsSchema.Radio)    yield return e;
        }

        // ── Text <-> value ────────────────────────────────────────────────────

        // The written form of a value. Invariant culture throughout: a config file
        // written on a comma-decimal locale must still parse on a period-decimal one.
        internal static string Format(SettingDef s, object v)
        {
            if (v == null) return string.Empty;
            switch (s.Kind)
            {
                case SettingKind.Bool:  return ((bool)v) ? "true" : "false";
                case SettingKind.Int:   return ((int)v).ToString(CultureInfo.InvariantCulture);
                case SettingKind.Float: return ((float)v).ToString("0.0###########", CultureInfo.InvariantCulture);
                default:                return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        // Parse the file's text into the setting's own type. Returns false — leaving
        // `value` untouched — when the text is not valid for the type, which is how
        // a typo'd value falls back to the current one instead of resetting it.
        internal static bool TryParse(SettingDef s, string text, out object value)
        {
            value = null;
            if (text == null) return false;
            string t = text.Trim();
            switch (s.Kind)
            {
                case SettingKind.Bool:
                    if (bool.TryParse(t, out bool b)) { value = b; return true; }
                    return false;
                case SettingKind.Int:
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    { value = i; return true; }
                    return false;
                case SettingKind.Float:
                    if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    { value = f; return true; }
                    return false;
                case SettingKind.String:
                    value = text;   // NOT trimmed: a string setting may want its spaces
                    return true;
                case SettingKind.Enum:
                    FieldInfo fi = Resolve(s.Target);
                    if (fi == null) return false;
                    try { value = Enum.Parse(fi.FieldType, t, true); return true; }
                    catch { return false; }
            }
            return false;
        }

        // Compare two values of this setting's type. Used by the upgrade path to ask
        // "is this still sitting on the old default?", so it must compare PARSED
        // values — "5" and "5.0" and " true " are the same setting untouched, and a
        // text compare would call them customised and strand them on the old value.
        // EVERY default a setting has ever had, not just the most recent one. `WasDefault`
        // may be a single value or an object[] of them, and a file is migrated if it still
        // holds ANY of them.
        //
        // This exists because the single-value form silently destroys its own history. Change a
        // default twice and the second edit overwrites the first `was`, so anyone who never
        // launched the version in between is holding a value the rule no longer recognises —
        // and an unrecognised value reads as a deliberate user override and is left alone
        // forever. That is exactly what happened to the radio timings: a file still at schema 8
        // held the original 1100 ms, the schema-9 rule that would have moved it was replaced by
        // a schema-10 rule looking for 7000, and the file kept 1100 through both.
        internal static bool MatchesAnyFormerDefault(SettingDef s, object current)
        {
            if (s.WasDefault == null) return false;
            object[] arr = s.WasDefault as object[];
            if (arr == null) return SameValue(s, current, s.WasDefault);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && SameValue(s, current, arr[i])) return true;
            return false;
        }

        internal static bool SameValue(SettingDef s, object a, object b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            switch (s.Kind)
            {
                case SettingKind.Bool:  return (bool)a == (bool)b;
                case SettingKind.Int:   return (int)a == (int)b;
                case SettingKind.Float: return (float)a == (float)b;
                case SettingKind.String:
                    return string.Equals(Convert.ToString(a), Convert.ToString(b), StringComparison.Ordinal);
                default:
                    return string.Equals(Convert.ToString(a), Convert.ToString(b),
                                         StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Field access ──────────────────────────────────────────────────────

        internal static object Get(SettingDef s)
        {
            FieldInfo fi = Resolve(s.Target);
            return fi != null ? fi.GetValue(null) : s.Default;
        }

        internal static void Set(SettingDef s, object v)
        {
            FieldInfo fi = Resolve(s.Target);
            if (fi == null || v == null) return;
            try { fi.SetValue(null, v); }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] settings schema: could not assign <{s.Name}> "
                          + $"to {s.Target} ({ex.Message}).");
            }
        }

        // ── Applying a loaded chain ───────────────────────────────────────────

        // Walk every setting in the schema and take its value from the first file in
        // the chain that has it. A setting the chain does not mention keeps whatever
        // it already holds — which is the C# field initializer on a first load, and
        // the previous value on a hot-reload. Same semantics the hand-written
        // ReadBool/ReadFloat/... calls had; this is the table doing it instead.
        internal static int Apply(XmlElement[] roots)
        {
            BindAll();
            int applied = 0;
            foreach (SchemaEntry e in AllEntries())
            {
                SettingDef s = e.Setting;
                if (s == null || s.Target == null) continue;   // hand-read (MusicPath)
                XmlNode n = FindIn(roots, s.Name);
                if (n == null) continue;
                if (!TryParse(s, n.InnerText, out object v)) continue;
                Set(s, v);
                applied++;
            }
            return applied;
        }

        // First root that has the tag wins — settings.xml before radio.xml, and (in
        // Debug) settings.dev.xml before either.
        internal static XmlNode FindIn(XmlElement[] roots, string tag)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                XmlNode n = roots[i].SelectSingleNode(tag);
                if (n != null) return n;
            }
            return null;
        }

        // The raw text of a setting as it appears in the chain, or null if absent.
        internal static string RawText(XmlElement[] roots, string tag)
            => FindIn(roots, tag)?.InnerText;
    }
}
