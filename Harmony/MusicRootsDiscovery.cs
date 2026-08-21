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
    // Runs once at launch and again on `cmm scan`. Not on a timer: the game's mod
    // list is fixed for the session — mods cannot be installed while it runs — so
    // re-probing would only ever find the same set. A pack's folder VANISHING needs
    // no discovery either, because a folder that is not there contributes no tracks
    // and its absence is already part of the change signature.
    //
    // Uses ModManager.GetLoadedMods() rather than listing a Mods directory, for two
    // reasons: the game loads from TWO roots (the user-data Mods folder and the one
    // in the install directory) and this covers both, and it reflects what the game
    // actually accepted — a mod with a broken ModInfo.xml never loads, so it should
    // not contribute music either. Verified identical (name, arity, List<Mod>) on
    // A20, A21, 2.6, 3.0 and 3.1.
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
            var found = new List<MusicRoot>();
            try
            {
                string ownMusic = MusicRoots.Normalize(Path.Combine(modPath, MusicFolderName));

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

                    found.Add(new MusicRoot { Path = music, Label = NameOf(m) });
                    if (found.Count >= MaxAddonRoots)
                    {
                        Log.Warning($"[CMM] more than {MaxAddonRoots} mods carry a {MusicFolderName} "
                                  + "folder; ignoring the rest. Raise MaxAddonRoots if this is real.");
                        break;
                    }
                }
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
