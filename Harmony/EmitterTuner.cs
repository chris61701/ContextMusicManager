// Gated with the rest of the world-emitter layer (see EmitterAudioLayer): compiled once against
// 3.x, shipped to every install, and runtime-disabled where EmitterCompat says the game cannot
// support emitters. Emitters themselves now work on 2.6, so this does too.
#if GV_V3
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // WHICH STATION A BUILDING'S SPEAKERS PLAY — the state behind the tuning gesture.
    //
    // Until now the answer was DERIVED and unchangeable: the POI's PrefabInstance.id hashed into
    // the sorted station list (EmitterRegistry.StationHashFromId). Deterministic, MP-consistent and
    // free of persistence — all deliberate — but it meant the answer to "I don't want this station
    // in this house" was to go and rename folders on disk.
    //
    // This holds the override. The GESTURE that sets it lives in EmitterActivationHook, which
    // hangs it off the game's own hold-to-open radial rather than drawing anything of its own.
    //
    // ── Persistence ─────────────────────────────────────────────────────────────────────────
    // Cache\emitters.xml, sectioned BY SAVE. The group key for a POI is its PrefabInstance.id,
    // which is stable within a world and identical on every MP client — but it is an index, not a
    // name, so id 47 in one save is a different building in another. Without the sectioning,
    // tuning a house in one playthrough would silently retune an unrelated building in the next.
    // Other saves' sections are kept verbatim on write, so playing world A never discards world
    // B's choices.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class EmitterTuner
    {
        // Stored for "this building stays silent" — deliberately a name no station folder can have.
        // GroupSnapshot already drops a pin that resolves to no station, so silencing needs no code
        // there: an unresolvable name IS dead air.
        private const string SilentPin = " silent";
        internal const string AutoLabel   = "Automatic";
        internal const string SilentLabel = "Silent";

        private static string _file;
        private static string _saveId = "";
        private static readonly Dictionary<string, string> _pins =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, string>> _otherSaves =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static bool _loaded;

        // A speaker inside a POI is identified BY THE BUILDING; one a player dropped in a field has
        // no building, so it is identified by where it stands. StationHash is deliberately not used
        // for this: the overlapping-prefab dedup in EmitterRegistry rewrites it, and a key that
        // moves under a saved choice would retune a house on its own.
        internal static string GroupKey(EmitterRegistry.Emitter e)
            => e.PoiId != 0
                ? "poi:" + e.PoiId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "blk:" + e.Pos.x + "," + e.Pos.y + "," + e.Pos.z;

        // The player's choice for this speaker, if they made one. Consulted by
        // EmitterAudioLayer.GroupSnapshot BEFORE its own pin map, and deliberately not written into
        // that map: leaving the override outside the cache means changing it lands on the next
        // decision tick with nothing to invalidate.
        internal static bool TryPin(EmitterRegistry.Emitter e, out string station)
        {
            station = null;
            if (_pins.Count == 0) return false;
            if (!_pins.TryGetValue(GroupKey(e), out station)) return false;
            if (station == SilentPin) station = "";   // resolves to no station ⇒ dead air
            return true;
        }

        // Is this block position a detected speaker? The hooks ask this for every block the player
        // looks at, so it is a scan of the snapshot — tens of entries, and only on a look.
        internal static bool EmitterAt(Vector3i pos, out EmitterRegistry.Emitter found)
        {
            var snap = EmitterRegistry.Snapshot;
            for (int i = 0; i < snap.Count; i++)
                if (snap[i].Pos.Equals(pos)) { found = snap[i]; return true; }
            found = default(EmitterRegistry.Emitter);
            return false;
        }

        // What this speaker plays right now, as a label for the prompt: the override if there is
        // one, otherwise whatever the audio layer pinned, otherwise it is not decided yet.
        internal static string CurrentLabel(EmitterRegistry.Emitter e)
        {
            string station;
            if (TryPin(e, out station)) return string.IsNullOrEmpty(station) ? SilentLabel : station;
            station = EmitterAudioLayer.PinnedStationAt(e.Pos);
            return string.IsNullOrEmpty(station) ? "tuning" : station;
        }

        // -- The choices, in radial order ---------------------------------------------------
        // The radial is 13 segments (windowRadial repeat_count="13") and the catalog is 16
        // stations, so the list has to page. The layout that falls out of that, in order:
        //
        //   0        Turn Off / Turn On     <- THE TAP TARGET, see below
        //   1..10    up to ten stations     <- the current page
        //   11       Automatic
        //   12       More                   <- only when there is more than one page
        //
        // SLOT 0 IS LOAD-BEARING. XUiC_Radial.Update takes a release BEFORE displayDelay (0.25 s)
        // -- a tap rather than a hold -- and invokes the FIRST ENABLED ENTRY. So putting the
        // on/off toggle there makes "tap to switch it off" fall out of the game's own input
        // handling, with no timing code of ours. It also means the shelf speaker, whose vanilla
        // loot command sorts before all of ours, still LOOTS on a tap: nothing is stolen.
        internal struct Choice
        {
            public string Label;    // what the centre of the radial reads
            public string Cmd;      // what comes back through OnBlockActivated
            public string Icon;     // atlas symbol
            public string Badge;    // 1-2 chars drawn on the segment itself
            public bool Current;    // highlight: this is what is playing now
        }

        internal const string CmdAuto = "@auto", CmdOff = "@off", CmdOn = "@on", CmdMore = "@more";

        // Which page each group is showing. Transient (not saved): a page is where you are in a
        // menu, not a property of the building.
        private static readonly Dictionary<string, int> _page =
            new Dictionary<string, int>(StringComparer.Ordinal);
        // What a group was tuned to before it was switched off, so "Turn On" has somewhere to go
        // back to. Also transient -- Off itself is persisted, this is only the undo.
        private static readonly Dictionary<string, string> _beforeOff =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal const int RadialSegments = 13;
        internal const int PageSize = 10;

        internal static void BuildChoices(EmitterRegistry.Emitter e, List<Choice> into, int reserved)
        {
            into.Clear();
            EnsureLoaded();
            string key = GroupKey(e);
            string current = CurrentLabel(e);
            bool isOff = current == SilentLabel;

            var stations = RadioStationCatalog.Stations;
            int room = RadialSegments - reserved;                 // what the block's own commands left
            if (room < 3) return;                                 // no sensible menu fits

            bool paged = stations.Count > room - 2;               // 2 fixed: toggle + Automatic
            int perPage = paged ? Math.Min(PageSize, room - 3) : room - 2;
            if (perPage < 1) return;

            int pages = paged ? (stations.Count + perPage - 1) / perPage : 1;
            int page;
            if (!_page.TryGetValue(key, out page) || page >= pages || page < 0) page = 0;
            _page[key] = page;

            // 0 -- the tap target.
            into.Add(new Choice {
                Label = isOff ? "Turn On" : "Turn Off",
                Cmd   = isOff ? CmdOn : CmdOff,
                Icon  = isOff ? IconOn : IconOff,
                Badge = "",
                Current = false });

            for (int i = page * perPage; i < stations.Count && i < (page + 1) * perPage; i++)
            {
                string name = stations[i].Name;
                into.Add(new Choice {
                    Label = name, Cmd = name, Icon = IconStation,
                    Badge = Initials(name), Current = name == current });
            }

            into.Add(new Choice {
                Label = AutoLabel, Cmd = CmdAuto, Icon = IconAuto, Badge = "",
                Current = !isOff && !_pins.ContainsKey(key) });

            if (paged)
                into.Add(new Choice {
                    Label = "More stations (" + (page + 1) + " of " + pages + ")",
                    Cmd = CmdMore, Icon = IconMore, Badge = "", Current = false });
        }

        // Two characters, because that is what the segment's label actually is: the vanilla
        // radial_entry template gives it a 48x48 box at font size 28, which is a corner badge, not
        // a caption. The full name goes to the CENTRE label, where there is room for it.
        // Initials of the first two words beat the first two letters -- "Concrete Jungle" reads as
        // CJ, where "Co" would collide with "Continue FM".
        internal static string Initials(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            char a = '\0', b = '\0';
            bool atWordStart = true;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == ' ' || c == '_' || c == '-') { atWordStart = true; continue; }
                if (atWordStart)
                {
                    if (a == '\0') a = char.ToUpperInvariant(c);
                    else if (b == '\0') { b = char.ToUpperInvariant(c); break; }
                    atWordStart = false;
                }
            }
            if (a == '\0') return "";
            // A one-word name has no second initial, so take its second letter instead, or every
            // C-station would show a bare C.
            if (b == '\0' && name.Length > 1) b = char.ToLowerInvariant(name[1]);
            return b == '\0' ? a.ToString() : new string(new[] { a, b });
        }

        // True when the choice was a MENU action (paging) rather than a tuning.
        internal static bool IsMenuAction(string cmd) => cmd == CmdMore;

        // "page 2 of 2", or null when everything fits on one ring. The crosshair prompt carries
        // this: paging cannot reopen the radial (see the OnBlockActivated hook for why), so the
        // player has to know that the next hold shows a different set rather than discovering it.
        internal static string PageNote()
        {
            int perPage = PageSize;
            int n = RadioStationCatalog.Stations.Count;
            if (n <= RadialSegments - 2) return null;
            int pages = (n + perPage - 1) / perPage;
            if (pages <= 1) return null;
            int page = 0;
            foreach (var kv in _page) { page = kv.Value; break; }   // one menu is open at a time
            if (page >= pages || page < 0) page = 0;
            return "page " + (page + 1) + " of " + pages;
        }

        internal static void NextPage(EmitterRegistry.Emitter e)
        {
            string key = GroupKey(e);
            int page;
            _page.TryGetValue(key, out page);
            _page[key] = page + 1;                 // BuildChoices wraps it back to 0 past the end
        }

        internal static void Apply(EmitterRegistry.Emitter e, string cmd)
        {
            EnsureLoaded();
            string key = GroupKey(e);
            string label = cmd;

            if (cmd == CmdOff)
            {
                string was = CurrentLabel(e);
                if (was != SilentLabel && was != "tuning") _beforeOff[key] = was;
                _pins[key] = SilentPin;
                label = "off";
            }
            else if (cmd == CmdOn)
            {
                string back;
                if (_beforeOff.TryGetValue(key, out back) && back != SilentLabel)
                {
                    _pins[key] = back; label = back;
                }
                else { _pins.Remove(key); label = AutoLabel; }   // nothing to go back to: automatic
            }
            else if (cmd == CmdAuto)
            {
                _pins.Remove(key);
                label = AutoLabel;
            }
            else
            {
                _pins[key] = cmd;
            }
            Save();

            // SAY SOMETHING HAPPENED. A retune used to be silent until the new station's first
            // track opened, which the log times at a second or more -- long enough to read as
            // "nothing happened" and press the key again. The dial click confirms the input
            // immediately; the tuning static then covers the gap, and StartStation's level hold
            // sits inside it, so what used to be an audible ramp is hidden behind the noise a real
            // receiver would make. The audio layer stops the static when the voice lands.
            // THE CLICK ALWAYS, THE STATIC ONLY WHEN A NEW STATION HAS TO BE FOUND. Off, On and
            // Automatic all resume something already known or stop outright, so hiss over them is
            // noise for its own sake -- and switching a radio back on and hearing static reads as
            // a fault, not as tuning. Only picking a DIFFERENT station has a gap worth covering.
            PersonalRadio.TuneSfxDial();
            bool seeking = cmd != CmdOff && cmd != CmdAuto && cmd != CmdOn;
            if (seeking) PersonalRadio.TuneSfxStatic();
            else PersonalRadio.TuneSfxStop();

            Log.Out("[CMM] Emitter tuner: " + e.Name + " at " + e.Pos
                  + (e.PoiId != 0 ? " (whole building)" : "") + " -> " + label);
        }

        // Icons are drawn as "ui_game_symbol_" + name by the radial's block path, so these are all
        // names vanilla blocks already use -- an atlas miss draws nothing at all.
        //
        // A STATION HAS NO ICON. Ten segments all wearing the same light switch told you nothing
        // except that the mod had put something there, and the switch actively read as a control
        // you could throw. The two-character badge is the label, and an empty icon name resolves
        // to no sprite, which leaves the badge on a clean disc. The four CONTROLS keep icons,
        // because they are genuinely different actions and their glyphs distinguish them.
        internal const string IconStation = "";
        internal const string IconAuto    = "wrench";
        internal const string IconOff     = "x";
        internal const string IconOn      = "hand";
        internal const string IconMore    = "map_cursor";

        internal static void Load(string modPath)
        {
            _file = Path.Combine(modPath, "Cache", "emitters.xml");
            _loaded = false;
        }

        // Deferred to first use because the save identity does not exist on the main menu. Re-runs
        // whenever the save changes, so loading a different world swaps the section.
        internal static void EnsureLoaded()
        {
            string id = CurrentSaveId();
            if (_loaded && id == _saveId) return;
            _saveId = id;
            _loaded = true;
            _pins.Clear();
            _otherSaves.Clear();
            if (_file == null || !File.Exists(_file)) return;
            try
            {
                var doc = new XmlDocument();
                doc.Load(_file);
                XmlElement root = doc.DocumentElement;
                if (root == null || root.Name != "CMMEmitters") return;
                foreach (XmlNode saveNode in root.ChildNodes)
                {
                    var se = saveNode as XmlElement;
                    if (se == null || se.Name != "save") continue;
                    string sid = se.GetAttribute("id");
                    Dictionary<string, string> into = sid == _saveId
                        ? _pins
                        : (_otherSaves[sid] = new Dictionary<string, string>(StringComparer.Ordinal));
                    foreach (XmlNode pinNode in se.ChildNodes)
                    {
                        var pe = pinNode as XmlElement;
                        if (pe == null || pe.Name != "pin") continue;
                        string k = pe.GetAttribute("group");
                        if (!string.IsNullOrEmpty(k)) into[k] = pe.GetAttribute("station");
                    }
                }
                if (_pins.Count > 0)
                    CMMConfig.D($"[CMM] Emitter tuner: restored {_pins.Count} tuned building(s) for this save.");
            }
            catch { _pins.Clear(); _otherSaves.Clear(); }
        }

        private static void Save()
        {
            if (_file == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var sb = new StringBuilder();
                sb.Append("<CMMEmitters>\n");
                WriteSave(sb, _saveId, _pins);
                foreach (var kv in _otherSaves) WriteSave(sb, kv.Key, kv.Value);
                sb.Append("</CMMEmitters>\n");
                File.WriteAllText(_file, sb.ToString());
            }
            catch { /* a failed save costs the tuning, not the session */ }
        }

        private static void WriteSave(StringBuilder sb, string id, Dictionary<string, string> pins)
        {
            if (pins.Count == 0) return;
            sb.Append("  <save id=\"").Append(System.Security.SecurityElement.Escape(id ?? "")).Append("\">\n");
            foreach (var kv in pins)
                sb.Append("    <pin group=\"").Append(System.Security.SecurityElement.Escape(kv.Key))
                  .Append("\" station=\"").Append(System.Security.SecurityElement.Escape(kv.Value ?? ""))
                  .Append("\" />\n");
            sb.Append("  </save>\n");
        }

        // GameIO.GetSaveGameDir() is the no-argument overload, present on A20, A21, 2.6 and 3.2
        // alike. It is meaningless outside a world, hence the guard and the catch.
        private static string CurrentSaveId()
        {
            try { return ContextMusicManager.inGame ? (GameIO.GetSaveGameDir() ?? "") : ""; }
            catch { return ""; }
        }
    }
}
#endif
