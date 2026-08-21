using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CMM
{
    // The synthetic world: one primary music folder plus 30 mod-style packs, with the
    // music deliberately spread unevenly and awkwardly. Real directories and real
    // files on disk — the whole point is to exercise the same System.IO paths the game
    // does, including the ones that only misbehave against a filesystem.
    internal sealed class Env : IDisposable
    {
        internal string Base { get; }
        internal string Primary { get; }
        internal readonly List<string> PackMusic = new List<string>();   // <pack>/Music
        internal const int PackCount = 30;

        // Contexts the world is built around. Nested pairs on purpose (poi / poi/t3) so
        // the "deepest context owns the file" rule is under test, not assumed.
        internal readonly Dictionary<string, Contexts> Contexts =
            new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);

        internal Env()
        {
            Base = Path.Combine(Path.GetTempPath(), "cmmroots_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Primary = Path.Combine(Base, "primary", "Music");
            Directory.CreateDirectory(Primary);
            for (int i = 0; i < PackCount; i++)
            {
                string m = Path.Combine(Base, "mods", "pack" + i.ToString("00"), "Music");
                PackMusic.Add(m);
                // pack29 is declared but its Music folder is NEVER created: a pack that
                // contributes nothing must cost one Directory.Exists and nothing else.
                if (i != 29) Directory.CreateDirectory(m);
            }
            Build();
        }

        // ── context table ─────────────────────────────────────────────────────
        internal Contexts Ctx(string name, string source, bool subfolders = false)
        {
            var c = new Contexts(name, source, "Stack", true, 120, false, 5, 5f, false, 0, false, -1f,
                                 0, null, subfolders, null);
            Contexts[name] = c;
            return c;
        }

        internal static void Track(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            // Not a real audio file; nothing here decodes. AudioDecoder.IsSupported is
            // extension-only, which is exactly the code under test.
            File.WriteAllText(Path.Combine(dir, name), "synthetic " + name);
        }

        internal static void Playlist(string dir, string name, params string[] entries)
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("# synthetic playlist");
            foreach (string e in entries) sb.AppendLine(e);
            File.WriteAllText(Path.Combine(dir, name), sb.ToString());
        }

        private void Build()
        {
            Ctx("Ambient", "ambient");
            Ctx("Combat", "combat");
            Ctx("POI", "poi", subfolders: true);      // recursive: crawler territory
            Ctx("POI_T3", "poi/t3");                  // nested INSIDE the recursive one
            Ctx("Menu", "menu");
            Ctx("Radio", "Radio");
            Ctx("Silence", "");                       // sentinel: no source at all
            Ctx("Listed", "lists/mix.m3u");           // playlist source
            Ctx("Elsewhere", Path.Combine(Base, "absolute", "jazz"));   // absolute source

            // ── primary ──
            Track(Path.Combine(Primary, "ambient"), "p_ambient_1.ogg");
            Track(Path.Combine(Primary, "ambient"), "p_ambient_2.mp3");
            Track(Path.Combine(Primary, "ambient"), "notes.txt");            // unsupported
            Track(Path.Combine(Primary, "combat"), "p_combat_1.ogg");
            Track(Path.Combine(Primary, "menu"), "p_menu_1.flac");
            // flat context with a subfolder: its contents must NOT appear
            Track(Path.Combine(Primary, "combat", "deeper"), "p_combat_hidden.ogg");
            // recursive primary, with a nested child context whose folder it must not claim
            Track(Path.Combine(Primary, "poi"), "p_poi_root.ogg");
            Track(Path.Combine(Primary, "poi", "t1"), "p_poi_t1.ogg");
            Track(Path.Combine(Primary, "poi", "t3"), "p_poi_t3.ogg");        // owned by POI_T3
            // playlist source, pointing at files that live outside any context folder
            Track(Path.Combine(Base, "pool"), "listed_a.ogg");
            Track(Path.Combine(Base, "pool"), "listed_b.ogg");
            Playlist(Path.Combine(Primary, "lists"), "mix.m3u",
                     Path.Combine(Base, "pool", "listed_a.ogg"),
                     Path.Combine(Base, "pool", "listed_b.ogg"));
            // absolute source
            Track(Path.Combine(Base, "absolute", "jazz"), "abs_1.ogg");
            // radio stations under the primary
            Track(Path.Combine(Primary, "Radio", "Country"), "p_country_1.ogg");
            Track(Path.Combine(Primary, "Radio", "Rock"), "p_rock_1.ogg");

            // ── packs, deliberately uneven ──
            for (int i = 0; i < PackCount; i++)
            {
                string m = PackMusic[i];
                if (i == 29) continue;                       // no Music folder at all

                if (i % 3 == 0)                              // 10 packs feed Ambient
                    Track(Path.Combine(m, "ambient"), $"k{i:00}_ambient.ogg");
                if (i % 5 == 0)                              // 6 packs feed Combat
                    Track(Path.Combine(m, "combat"), $"k{i:00}_combat.mp3");
                if (i % 7 == 0)                              // 5 packs feed the recursive POI
                {
                    Track(Path.Combine(m, "poi"), $"k{i:00}_poi.ogg");
                    // A subfolder inside a PACK folder: must be ignored even though POI is
                    // recursive, because recursion applies to the primary only.
                    Track(Path.Combine(m, "poi", "deep"), $"k{i:00}_poi_deep_IGNORED.ogg");
                }
                if (i == 4 || i == 11)                       // two packs, one context each
                    Track(Path.Combine(m, "menu"), $"k{i:00}_menu.ogg");

                // DUPLICATE FILENAME across packs and with the primary: different files at
                // different paths, so all of them must play.
                if (i % 4 == 0)
                    Track(Path.Combine(m, "ambient"), "p_ambient_1.ogg");

                // A pack shipping a playlist inside a flat context folder: the folder's
                // pool is its loose files UNIONED with what the .m3u lists.
                if (i == 8)
                {
                    Track(Path.Combine(Base, "pool"), "k08_listed.ogg");
                    Playlist(Path.Combine(m, "ambient"), "extra.m3u",
                             Path.Combine(Base, "pool", "k08_listed.ogg"));
                }

                // Folders matching no context at all — must be silently ignored.
                if (i == 15) Track(Path.Combine(m, "not_a_context"), "k15_stray.ogg");

                // Radio: two packs supply a station named the same as the primary's, and
                // one supplies a brand new station. Same-named must MERGE.
                if (i == 2 || i == 6)
                    Track(Path.Combine(m, "Radio", "Country"), $"k{i:00}_country.ogg");
                if (i == 6)
                    Track(Path.Combine(m, "Radio", "Jazz"), "k06_jazz.ogg");

                // Playlist and absolute sources must never be extended: give two packs a
                // folder that would match if we were careless.
                if (i == 20) Track(Path.Combine(m, "lists"), "k20_should_not_load.ogg");
                if (i == 21) Track(Path.Combine(m, "absolute", "jazz"), "k21_should_not_load.ogg");
            }
        }

        // Point MusicRoots at this world. `packs` lets a test use a subset.
        internal void Activate(int packs = PackCount)
        {
            MusicRoots.SetPrimary(Primary);
            var addons = new List<MusicRoot>();
            for (int i = 0; i < packs && i < PackMusic.Count; i++)
                addons.Add(new MusicRoot { Path = PackMusic[i], Label = "pack" + i.ToString("00") });
            MusicRoots.SetAddons(addons);
        }

        internal string Dir(int pack, string rel) => Path.Combine(PackMusic[pack], rel);

        public void Dispose()
        {
            try { Directory.Delete(Base, true); } catch { }
        }
    }
}
