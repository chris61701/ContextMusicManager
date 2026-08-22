using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // FINDING THE ADD-ON MUSIC FOLDERS.
    //
    // Split out of MusicRoots because this half needs the game's mod list and
    // MusicRoots must stay Unity-free so the offline harnesses can link it.
    //
    // Runs at launch and again on `cmm scan`. Not on a timer.
    //
    // TWO PASSES, because the loaded-mod list alone is not enough.
    //
    //   1. ModManager.GetLoadedMods() — every mod the game accepted. Gives Mod.Path
    //      directly and covers BOTH mod roots (the user-data one and the one in the
    //      install directory), which listing a single directory would not. Verified
    //      identical (name, arity, List<Mod>) on A20, A21, 2.6, 3.0 and 3.1.
    //
    //   2. The mod ROOTS those paths sit in, listed directly. The loaded list is
    //      fixed when the game starts, so a pack dropped in while it runs is invisible
    //      to pass 1 forever — and "restart the game" is a poor answer when the thing
    //      you added is a folder of music files. A music pack is DATA: the game not
    //      having loaded it means its code did not run, and a music pack has no code.
    //      So a folder is taken if it holds a ModInfo.xml (the same marker the game
    //      requires, so removing that file still disables it) and a Music folder.
    //
    // The roots come from the loaded mods' own parents rather than a game API, because
    // ModsBasePath is private before 2.6. That finds every root with at least one
    // loaded mod in it, which in practice is all of them — CMM itself is in one, and
    // TFP_Harmony is usually in the other.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class MusicRootsDiscovery
    {
        internal const string MusicFolderName = "Music";
        // A Wabbajack-style list can be enormous; past this we are no longer serving a
        // real setup, and every root costs a stat per watch tick.
        private const int MaxAddonRoots = 32;

        // Mod.Name is a property on A21/2.6/3.x and DOES NOT EXIST on A20 — the shipped
        // DLL is one build fanned out to all of them, so a direct call would throw
        // MissingMethodException when the enclosing method is JITted. Display only;
        // FolderName (present everywhere) is the fallback.
        private static readonly Func<Mod, string> _modName = StringAccessor<Mod>("Name");

        private static Func<T, string> StringAccessor<T>(string member)
        {
            PropertyInfo prop = typeof(T).GetProperty(member);
            FieldInfo    fld  = prop == null ? typeof(T).GetField(member) : null;
            if (prop == null && fld == null) return _ => null;
            var e = Expression.Parameter(typeof(T), "e");
            Expression access = prop != null ? Expression.Property(e, prop)
                                             : (Expression)Expression.Field(e, fld);
            try { return Expression.Lambda<Func<T, string>>(access, e).Compile(); }
            catch
            {
                return prop != null ? (Func<T, string>)(t => (string)prop.GetValue(t))
                                    : (t => (string)fld.GetValue(t));
            }
        }

        private static string NameOf(Mod m)
        {
            string n = null;
            try { n = _modName(m); } catch { }
            if (!string.IsNullOrEmpty(n)) return n;
            try { return m.FolderName; } catch { return "?"; }
        }

        // Rebuilds the add-on root list. Main thread. Returns how many were found.
        internal static int Discover(string modPath)
        {
            if (!CMMConfig.AddonMusicScanEnabled)
            {
                // Hard off: MusicPath and nothing else. Clearing rather than skipping matters
                // on a hot-reload -- turning it off must drop the folders already in use, not
                // merely stop looking for new ones.
                MusicRoots.SetAddons(new MusicRoot[0]);
                CMMConfig.D("[CMM] add-on music scanning is off (AddonMusicScanEnabled=false).");
                return 0;
            }

            var found = new List<MusicRoot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string ownMusic = MusicRoots.Normalize(Path.Combine(modPath, MusicFolderName));

                // ── pass 1: what the game loaded ──────────────────────────────
                foreach (Mod m in ModManager.GetLoadedMods())
                {
                    if (m == null) continue;
                    string root;
                    try { root = m.Path; } catch { continue; }
                    if (string.IsNullOrEmpty(root)) continue;

                    string music;
                    // Mod.Path is assembled from a forward-slash base plus a backslash, so it
                    // is mixed-separator on Windows. Normalize before any comparison.
                    try { music = MusicRoots.Normalize(Path.Combine(root, MusicFolderName)); }
                    catch { continue; }

                    // Our own Music folder IS the default MusicPath; MusicRoots dedups against
                    // the primary too, but skipping here keeps the log honest.
                    if (string.Equals(music, ownMusic, StringComparison.OrdinalIgnoreCase)) continue;

                    bool exists;
                    try { exists = Directory.Exists(music); } catch { continue; }
                    if (!exists) continue;

                    if (!seen.Add(music)) continue;
                    found.Add(new MusicRoot { Path = music, Label = NameOf(m) });
                    if (found.Count >= MaxAddonRoots) break;
                }

                // The roots those mods live in — the parent of each mod folder.
                foreach (Mod m in ModManager.GetLoadedMods())
                {
                    try
                    {
                        string mp = m?.Path;
                        if (string.IsNullOrEmpty(mp)) continue;
                        string parent = Path.GetDirectoryName(MusicRoots.Normalize(mp));
                        if (!string.IsNullOrEmpty(parent)) modRoots.Add(parent);
                    }
                    catch { }
                }

                // ── pass 2: mods added since the game started ─────────────────
                int hot = 0;
                foreach (string root in modRoots)
                {
                    if (found.Count >= MaxAddonRoots) break;
                    string[] candidates;
                    try { candidates = Directory.GetDirectories(root); } catch { continue; }
                    Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
                    foreach (string cand in candidates)
                    {
                        if (found.Count >= MaxAddonRoots) break;
                        try
                        {
                            // Same marker the game itself requires, so deleting ModInfo.xml
                            // still disables a pack exactly the way it always has.
                            if (!File.Exists(Path.Combine(cand, "ModInfo.xml"))) continue;
                            string music = MusicRoots.Normalize(Path.Combine(cand, MusicFolderName));
                            if (string.Equals(music, ownMusic, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!Directory.Exists(music)) continue;
                            if (!seen.Add(music)) continue;          // already found by pass 1
                            found.Add(new MusicRoot
                            {
                                Path = music,
                                Label = Path.GetFileName(cand) + " (not loaded)",
                            });
                            hot++;
                        }
                        catch { }
                    }
                }
                if (hot > 0)
                    Log.Out($"[CMM] {hot} music folder(s) found in mods the game has NOT loaded "
                          + "(added since startup). Their music plays; any code they carry does not "
                          + "until the game is restarted.");
                if (found.Count >= MaxAddonRoots)
                    Log.Warning($"[CMM] hit the {MaxAddonRoots}-folder limit; ignoring any further "
                              + $"{MusicFolderName} folders.");
            }
            catch (Exception ex)
            {
                // A failure here must cost the FEATURE, not the mod: MusicPath alone still works.
                Log.Warning($"[CMM] could not scan installed mods for {MusicFolderName} folders "
                          + $"({ex.Message}). Using MusicPath only.");
                MusicRoots.SetAddons(new MusicRoot[0]);
                return 0;
            }

            MusicRoots.SetAddons(found);
            int n = MusicRoots.AddonCount;
            if (n > 0)
            {
                var names = new List<string>();
                foreach (MusicRoot r in MusicRoots.All)
                    if (!r.IsPrimary) names.Add(r.Label);
                Log.Out($"[CMM] add-on music: {n} mod(s) supplying music — {string.Join(", ", names.ToArray())}. "
                      + "`cmm roots` for detail.");
            }
            else CMMConfig.D("[CMM] add-on music: no other mod has a Music folder.");
            return n;
        }
    }
}
