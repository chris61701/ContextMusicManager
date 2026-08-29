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

        // EVERY RADIO IS SAVED ON ITS OWN, by where it stands.
        //
        // This was keyed by BUILDING at first, on the reasoning that a POI's speakers already
        // shared one station, so retuning one should retune them all. That holds right up until
        // somebody builds a base inside a POI -- which people do -- and then their own radio is
        // welded to the derelict hospital's, and neither can be set without moving the other.
        //
        // The DEFAULT is untouched: the automatic assignment is still derived per POI
        // (EmitterRegistry.StationHashFromId), so an untouched building's speakers still all play
        // one station and still agree across multiplayer clients for free. Only a deliberate
        // choice is per radio. The cost is that a building with five speakers takes five tunings
        // to change wholesale -- the price of being able to change one of them at all.
        //
        // A position is also a better key than a PrefabInstance.id: it survives a POI reset, and
        // it means nothing at all in a save where that spot holds a different block.
        internal static string GroupKey(EmitterRegistry.Emitter e)
            => "blk:" + e.Pos.x + "," + e.Pos.y + "," + e.Pos.z;

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
            if (TryPin(e, out station))
            {
                if (string.IsNullOrEmpty(station)) return SilentLabel;
                // A SAVED STATION WHOSE FOLDER IS GONE. The pin is kept on purpose -- rename a
                // folder back, or plug the drive in, and the radio returns to what it was set to
                // -- but the audio layer resolves a name it cannot find to no station at all, so
                // the speaker is silent meanwhile. Saying "Continue FM" over a silent radio would
                // be a lie by omission; this says what is set AND why nothing is coming out.
                if (!RadioStationCatalog.ByName.ContainsKey(station)) return station + " - off air";
                return station;
            }
            station = EmitterAudioLayer.PinnedStationAt(e.Pos);
            return string.IsNullOrEmpty(station) ? "tuning" : station;
        }

        // -- The choices, in radial order ---------------------------------------------------
        // The radial is 13 segments (windowRadial repeat_count="13") and the catalog is 16
        // stations, so the list pages. The layout:
        //
        //   0        Turn Off / Turn On     <- THE TAP TARGET, see below
        //   1..10    up to ten stations     <- the current page
        //   11       previous page          <- only when there is more than one page
        //   12       next page              <- only when there is more than one page
        //
        // SLOT 0 IS LOAD-BEARING. XUiC_Radial.Update takes a release BEFORE displayDelay (0.25 s)
        // -- a tap rather than a hold -- and invokes the FIRST ENABLED ENTRY. So putting the
        // on/off toggle there makes "tap to switch it off" fall out of the game's own input
        // handling, with no timing code of ours. It also means the shelf speaker, whose vanilla
        // loot command sorts before all of ours, still LOOTS on a tap: nothing is stolen.
        //
        // AUTOMATIC IS NOT ON THE RING any more (removed on request, to make room for two arrows
        // rather than one "More"). The derived per-POI assignment is still what an untouched
        // building uses, and "Turn On" falls back to it when there is nothing to restore -- but
        // there is no longer a direct "put this building back to automatic" segment. Deleting the
        // building's line from Cache/emitters.xml is the way back.
        internal struct Choice
        {
            public string Label;    // what the centre of the radial reads
            public string Cmd;      // what comes back through OnBlockActivated
            public string Icon;     // atlas symbol
            public string Badge;    // 1-2 chars drawn on the segment itself
            public bool Current;    // highlight: this is what is playing now
        }

        // The moment of the last deliberate retune, read by EmitterAudioLayer to decide whether a
        // station change deserves the searching noise. Not persisted: on a fresh world entry there
        // has been no retune, which is exactly the answer we want.
        internal static float LastTuneAt = -999f;
        // ...and which speaker it was. The audio layer needs the position, not just the moment:
        // when the station you tuned TO is already playing somewhere else, that one speaker has to
        // be lifted out of it to be swept on its own.
        internal static Vector3i LastTunePos;

        internal const string CmdAuto = "@auto", CmdOff = "@off", CmdOn = "@on";
        internal const string CmdPrev = "@prev", CmdNext = "@next";

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

            bool paged = stations.Count > room - 1;               // 1 fixed: the on/off toggle
            int perPage = paged ? Math.Min(PageSize, room - 3) : room - 1;   // paged also costs 2 arrows
            if (perPage < 1) return;

            int pages = paged ? (stations.Count + perPage - 1) / perPage : 1;
            int page;
            if (!_page.TryGetValue(key, out page) || page >= pages || page < 0) page = 0;
            _page[key] = page;

            // The centre label is the only text the vanilla radial has, and it shows the HOVERED
            // entry. So the page rides on the front of every entry's centre text, on its own line:
            // it then reads above the station name, and it is there whatever you are pointing at
            // rather than only over the arrows.
            string head = paged ? "Page " + (page + 1) + " of " + pages + "\n" : "";

            // 0 -- the tap target.
            into.Add(new Choice {
                Label = head + (isOff ? "Turn On" : "Turn Off"),
                Cmd   = isOff ? CmdOn : CmdOff,
                Icon  = isOff ? IconOn : IconOff,
                Badge = "",
                Current = false });

            for (int i = page * perPage; i < stations.Count && i < (page + 1) * perPage; i++)
            {
                string name = stations[i].Name;
                into.Add(new Choice {
                    Label = head + name, Cmd = name, Icon = IconStation,
                    Badge = Initials(name), Current = name == current });
            }

            if (paged)
            {
                // THE PAGE LIVES IN THE MIDDLE OF THE RING, not in the crosshair prompt: the
                // prompt is about the radio, and which page a menu is on is about the menu. The
                // centre label is the only text the vanilla radial has, and it shows the hovered
                // entry's selection text -- so the arrows say it, and the page NUMBER also rides
                // on both arrows as a badge so it is legible without hovering anything.
                int prev = (page - 1 + pages) % pages, next = (page + 1) % pages;
                // NO BADGE ON AN ARROW. A number crammed next to a glyph made the arrow read as a
                // labelled thing rather than a direction, and the page is said in the centre now
                // -- where it is legible on every entry, not just these two.
                into.Add(new Choice {
                    Label = head + "Previous page", Cmd = CmdPrev, Icon = IconPrev,
                    Badge = "", Current = false });
                into.Add(new Choice {
                    Label = head + "Next page", Cmd = CmdNext, Icon = IconNext,
                    Badge = "", Current = false });
            }
        }

        // The four-character label. RadioBadge is version-neutral and has no game types, so the
        // rules are covered by CmmScheduleTest against the worked list they were derived from --
        // see the header there. Kept out of this file for exactly that reason.
        internal static string Initials(string name) => RadioBadge.For(name);

        // True when the choice was a MENU action (paging) rather than a tuning.
        internal static bool IsMenuAction(string cmd) => cmd == CmdPrev || cmd == CmdNext;

        internal static void StepPage(EmitterRegistry.Emitter e, int dir)
        {
            string key = GroupKey(e);
            int page;
            _page.TryGetValue(key, out page);
            int n = RadioStationCatalog.Stations.Count;
            int pages = Math.Max(1, (n + PageSize - 1) / PageSize);
            _page[key] = ((page + dir) % pages + pages) % pages;   // wraps both ways
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
            // THE CLICK ONLY. The static was here to cover the gap while the new station's level
            // was measured, and it did cover it -- but a WORLD speaker's static played on the
            // music bus, which has no position, so it arrived inside the listener's head instead
            // of out of the radio across the room. Giving a one-shot a position means changing the
            // native audio library, and it is not worth that: StartStation now holds for the level
            // (see its gain gate), so there is no ramp left to hide and a confirmation click is
            // the whole job.
            //
            // The personal radio keeps ITS static, and correctly: that one IS in your head.
            // WHEN A HUMAN LAST TURNED A DIAL. The emitter receiver hisses on a station change,
            // and until now "station change" meant any change — including the ones the world makes
            // on its own. Loading into a world assigns every slot its first station, which counted
            // as a change and greeted the player with static on arrival. Coming into range of a
            // POI, or a nearer station stealing a slot, did the same. None of those are somebody
            // turning a knob, and a receiver only searches because somebody turned a knob.
            LastTuneAt = CmmClock.Now;
            LastTunePos = e.Pos;
            PersonalRadio.TuneSfxDial();

            Log.Out("[CMM] Emitter tuner: " + e.Name + " at " + e.Pos + " -> " + label);
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
        // arrow_left / arrow_right are real sprites in UIAtlas -- vanilla XUi uses them by these
        // exact names. The radial prefixes "ui_game_symbol_", so that is what is left here.
        internal const string IconPrev    = "arrow_left";
        internal const string IconNext    = "arrow_right";

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
