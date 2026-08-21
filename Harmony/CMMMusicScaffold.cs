using System;
using System.IO;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // THE MUSIC FOLDER TREE — one folder per context, with a placeholder naming it.
    //
    // This used to be an MSBuild task that read Config/contexts.xml at package time.
    // It had been broken since the `path` attribute was renamed to `source`: the task
    // still asked for `path`, got "", and skipped every context — while logging
    // "Ensured Music context folders" as if it had worked. 1.0.4 shipped 11 folders;
    // 1.0.5 shipped an empty Music directory and nobody noticed, because the only
    // symptom is a new user having nowhere obvious to put their files.
    //
    // Doing it at runtime instead fixes that and is better anyway:
    //   * it follows MusicPath, so the folders appear where the music actually goes
    //     rather than only inside the mod,
    //   * contexts a user adds themselves get folders too,
    //   * and there is no second copy of the context list for the build to read, which
    //     is what made the bug invisible in the first place.
    //
    // STRICTLY ADDITIVE. It creates missing directories and, only for a directory it
    // just created, drops a placeholder. It never writes into a folder that already
    // exists, never deletes, never overwrites. The music library is irreplaceable and
    // this code runs on every launch.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CMMMusicScaffold
    {
        private const string Note =
            "Placeholder so mod managers don't delete this empty folder. "
          + "Drop your music files here (you can delete this file once you do).";

        internal static void EnsureFolders(string modPath)
        {
            try
            {
                string root = ContextMusicManager.MusicDirectory;
                if (string.IsNullOrEmpty(root)) return;

                // Create the ROOT only when it is our own default (<mod>/Music). If the user
                // pointed MusicPath somewhere else and it is not there, that means the drive
                // is not mounted — J:\ is removable on this project's own dev machine — and
                // conjuring a fresh empty tree over a missing library would be the wrong
                // answer to that in every case.
                string ownRoot = Path.Combine(modPath, "Music");
                bool isOwn;
                try { isOwn = string.Equals(Path.GetFullPath(root), Path.GetFullPath(ownRoot),
                                            StringComparison.OrdinalIgnoreCase); }
                catch { isOwn = false; }

                if (!Directory.Exists(root))
                {
                    if (!isOwn)
                    {
                        CMMConfig.D($"[CMM] music folder scaffold skipped — {root} does not exist "
                                  + "and is not the mod's own Music folder.");
                        return;
                    }
                    Directory.CreateDirectory(root);
                }

                int made = 0;
                foreach (Contexts ctx in ContextMusicManager.contexts.Values)
                {
                    if (ctx == null) continue;
                    string src = ctx.Source;
                    if (string.IsNullOrEmpty(src)) continue;      // Vanilla / Silence sentinels
                    if (ctx.IsPlaylist) continue;                 // a .m3u is a file, not a folder
                    if (Path.IsPathRooted(src)) continue;         // absolute source: the user's own place

                    string dir;
                    try
                    {
                        dir = Path.Combine(root, src.Replace('/', Path.DirectorySeparatorChar)
                                                    .Replace('\\', Path.DirectorySeparatorChar));
                    }
                    catch { continue; }

                    if (Directory.Exists(dir)) continue;          // never touch an existing folder
                    try
                    {
                        Directory.CreateDirectory(dir);
                        // Named after the context that feeds from here, so a glance at the tree
                        // says which folder is which. Unsupported extension, so the scanner
                        // ignores it.
                        File.WriteAllText(Path.Combine(dir, "." + ctx.Context), Note);
                        made++;
                    }
                    catch { /* one bad name must not stop the rest */ }
                }

                if (made > 0)
                    Log.Out($"[CMM] Created {made} missing music folder(s) under {root}.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] could not scaffold music folders ({ex.Message}).");
            }
        }
    }
}
