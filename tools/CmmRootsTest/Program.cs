using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CMM
{
    internal static class Program
    {
        static int _fail, _pass;

        static void Ok(string m)  { _pass++; Console.WriteLine("  ok    " + m); }
        static void Bad(string m) { _fail++; Console.WriteLine("  FAIL  " + m); }
        static void Is(bool c, string m) { if (c) Ok(m); else Bad(m); }
        static void Eq(int got, int want, string m)
            => Is(got == want, m + (got == want ? "" : $"  (got {got}, want {want})"));
        static void Head(string s) => Console.WriteLine(Environment.NewLine + "── " + s + " "
                                                       + new string('─', Math.Max(0, 56 - s.Length)));

        static string[] Names(IEnumerable<string> paths) =>
            paths.Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        static int Main(string[] args)
        {
            using (var env = new Env())
            {
                Console.WriteLine("CmmRootsTest — " + env.Base);
                Console.WriteLine($"primary + {Env.PackCount} packs (one with no Music folder at all)");

                Resolution(env);
                Enumeration(env);
                Duplicates(env);
                Subfolders(env);
                Playlists(env);
                Claims(env);
                Signature(env);
                Scale(env);
                SingleRootParity(env);
            }

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? $"ALL {_pass} CHECKS PASSED"
                : $"{_fail} FAILED / {_pass + _fail} total");
            return _fail == 0 ? 0 : 1;
        }

        // ── resolution ────────────────────────────────────────────────────────
        static void Resolution(Env env)
        {
            Head("resolution");
            env.Activate();
            Eq(MusicRoots.Count, Env.PackCount + 1, "primary + every pack is a root");
            Is(MusicRoots.All[0].IsPrimary, "root[0] is the primary");
            Is(MusicRoots.Primary == MusicRoots.Normalize(env.Primary), "primary is MusicPath");

            string[] d = MusicRoots.DirsFor(env.Contexts["Ambient"]);
            Eq(d.Length, Env.PackCount + 1, "a relative source resolves in every root");
            Is(d[0].StartsWith(MusicRoots.Primary, StringComparison.OrdinalIgnoreCase),
               "primary folder comes first");

            Eq(MusicRoots.DirsFor(env.Contexts["Silence"]).Length, 0, "a sentinel resolves nowhere");
            Eq(MusicRoots.DirsFor(env.Contexts["Listed"]).Length, 0, "a playlist source resolves to no folder");
            Eq(MusicRoots.DirsFor(env.Contexts["Elsewhere"]).Length, 1,
               "an absolute source resolves to exactly one folder, whatever the root count");

            // dedup: a pack whose Music IS the primary, and the same pack twice
            MusicRoots.SetAddons(new[]
            {
                new MusicRoot { Path = env.Primary,     Label = "self" },
                new MusicRoot { Path = env.PackMusic[0], Label = "a" },
                new MusicRoot { Path = env.PackMusic[0], Label = "a-again" },
            });
            Eq(MusicRoots.Count, 2, "a pack equal to the primary, and a repeat, are both dropped");
            env.Activate();
        }

        // ── enumeration ───────────────────────────────────────────────────────
        static void Enumeration(Env env)
        {
            Head("enumeration");
            env.Activate();

            string[] amb = SourceScan.EnumerateFlatPoolMulti(
                MusicRoots.DirsFor(env.Contexts["Ambient"]), SourceScan._noClaimPrefixes, out _);
            // primary: 2 supported (+1 .txt ignored). packs: i%3==0 -> 10 files,
            // i%4==0 -> 8 dupes (0,4,8,...,28), minus pack 29 which has no folder at all.
            // pack08 also contributes one track via its in-folder playlist.
            Is(amb.Length > 15, $"Ambient unions across packs ({amb.Length} tracks)");
            Is(!amb.Any(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)),
               "unsupported extensions are excluded");
            Is(amb.Any(f => f.IndexOf("k00_ambient", StringComparison.OrdinalIgnoreCase) >= 0),
               "a pack's track is in the pool");
            Is(!amb.Any(f => f.IndexOf("pack29", StringComparison.OrdinalIgnoreCase) >= 0),
               "a pack with no Music folder contributes nothing");
            Is(!amb.Any(f => f.IndexOf("k15_stray", StringComparison.OrdinalIgnoreCase) >= 0),
               "a pack folder matching no context is ignored");

            // order: primary block first, then packs in root order, each sorted
            string[] dirsAmb = MusicRoots.DirsFor(env.Contexts["Ambient"]);
            Is(amb[0].StartsWith(dirsAmb[0], StringComparison.OrdinalIgnoreCase),
               "the primary's tracks come first");
            string[] again = SourceScan.EnumerateFlatPoolMulti(dirsAmb, SourceScan._noClaimPrefixes, out _);
            Is(amb.SequenceEqual(again, StringComparer.Ordinal), "enumeration order is stable across calls");

            // an unreadable folder must cost only itself
            string victim = env.Dir(0, "ambient");
            string moved = victim + "_moved";
            Directory.Move(victim, moved);
            string[] less = SourceScan.EnumerateFlatPoolMulti(dirsAmb, SourceScan._noClaimPrefixes, out _);
            Is(less.Length < amb.Length && less.Length > 10,
               "a vanished pack folder costs only its own tracks");
            Directory.Move(moved, victim);

            // playlist + absolute sources are single-place and unextendable
            string[] listed = PlaylistSource.ReadTracks(
                Path.Combine(MusicRoots.Primary, "lists", "mix.m3u"));
            Eq(listed.Length, 2, "the playlist source lists exactly its own two tracks");
            Is(!listed.Any(f => f.IndexOf("k20_", StringComparison.OrdinalIgnoreCase) >= 0),
               "a pack cannot extend a playlist source");

            string[] abs = SourceScan.EnumerateFlatPoolMulti(
                MusicRoots.DirsFor(env.Contexts["Elsewhere"]), SourceScan._noClaimPrefixes, out _);
            Eq(abs.Length, 1, "an absolute source reads only its own folder");
            Is(!abs.Any(f => f.IndexOf("k21_", StringComparison.OrdinalIgnoreCase) >= 0),
               "a pack cannot extend an absolute source");
        }

        // ── duplicates ────────────────────────────────────────────────────────
        static void Duplicates(Env env)
        {
            Head("duplicates");
            env.Activate();
            string[] dirs = MusicRoots.DirsFor(env.Contexts["Ambient"]);
            string[] amb = SourceScan.EnumerateFlatPoolMulti(dirs, SourceScan._noClaimPrefixes, out _);

            int sameName = amb.Count(f => string.Equals(Path.GetFileName(f), "p_ambient_1.ogg",
                                                        StringComparison.OrdinalIgnoreCase));
            Is(sameName > 1, $"the same FILENAME in several roots yields several tracks ({sameName})");

            Is(amb.Length == amb.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
               "no path appears twice");

            // the identical folder listed twice must not double up
            var twice = new List<string>(dirs) { dirs[1] };
            string[] pool2 = SourceScan.EnumerateFlatPoolMulti(twice.ToArray(),
                                                               SourceScan._noClaimPrefixes, out _);
            Eq(pool2.Length, amb.Length, "the same folder listed twice contributes once");
        }

        // ── subfolders ────────────────────────────────────────────────────────
        static void Subfolders(Env env)
        {
            Head("subfolders");
            env.Activate();

            // flat context: a subfolder under it is invisible
            string[] cbt = SourceScan.EnumerateFlatPoolMulti(
                MusicRoots.DirsFor(env.Contexts["Combat"]), SourceScan._noClaimPrefixes, out _);
            Is(!cbt.Any(f => f.IndexOf("p_combat_hidden", StringComparison.OrdinalIgnoreCase) >= 0),
               "a flat context ignores its own subfolders");

            // recursive context: the PRIMARY tree is walked, pack folders stay flat
            Contexts poi = env.Contexts["POI"];
            Is(poi.IncludeSubfolders, "POI is recursive");
            string[] claims = MusicRoots.ClaimPrefixes(poi, env.Contexts.Values);
            string[] prim = SourceScan.EnumerateFolderPoolRaw(MusicRoots.PrimaryDir(poi), true, claims, out _);
            Is(prim.Any(f => f.IndexOf("p_poi_t1", StringComparison.OrdinalIgnoreCase) >= 0),
               "the recursive primary reaches into its subfolders");

            string[] packDirs = MusicRoots.DirsFor(poi).Skip(1).ToArray();
            string[] packPool = SourceScan.EnumerateFlatPoolMulti(packDirs, SourceScan._noClaimPrefixes, out _);
            Is(packPool.Any(f => f.IndexOf("_poi.ogg", StringComparison.OrdinalIgnoreCase) >= 0),
               "packs contribute to a recursive context");
            Is(!packPool.Any(f => f.IndexOf("_IGNORED", StringComparison.OrdinalIgnoreCase) >= 0),
               "a subfolder INSIDE a pack folder is ignored even for a recursive context");
        }

        // ── playlists ─────────────────────────────────────────────────────────
        static void Playlists(Env env)
        {
            Head("playlists");
            env.Activate();
            string[] amb = SourceScan.EnumerateFlatPoolMulti(
                MusicRoots.DirsFor(env.Contexts["Ambient"]), SourceScan._noClaimPrefixes,
                out string[] pls);
            Is(amb.Any(f => f.IndexOf("k08_listed", StringComparison.OrdinalIgnoreCase) >= 0),
               "a .m3u inside a PACK folder is expanded into the pool");
            Is(pls.Length >= 1, "the in-folder playlist is reported for the change watch");
            Is(!amb.Any(f => f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)),
               "the .m3u file itself is not a track");

            // recursive folders ignore playlists entirely — shipped rule, unchanged
            Contexts poi = env.Contexts["POI"];
            Env.Playlist(Path.Combine(MusicRoots.PrimaryDir(poi)), "ignored.m3u",
                         Path.Combine(env.Base, "pool", "listed_a.ogg"));
            string[] rec = SourceScan.EnumerateFolderPoolRaw(MusicRoots.PrimaryDir(poi), true,
                                                             SourceScan._noClaimPrefixes, out string[] rp);
            Is(!rec.Any(f => f.IndexOf("listed_a", StringComparison.OrdinalIgnoreCase) >= 0),
               "a recursive folder does not expand playlists");
            Eq(rp.Length, 0, "and reports none to watch");
        }

        // ── claims ────────────────────────────────────────────────────────────
        static void Claims(Env env)
        {
            Head("deepest context owns the file");
            env.Activate();
            Contexts poi = env.Contexts["POI"];
            string[] claims = MusicRoots.ClaimPrefixes(poi, env.Contexts.Values);
            Is(claims.Length == 1, $"POI claims against exactly its one nested child ({claims.Length})");
            Is(claims.All(c => c.IndexOf("t3", StringComparison.OrdinalIgnoreCase) >= 0),
               "the claim is POI_T3's folder");

            string[] prim = SourceScan.EnumerateFolderPoolRaw(MusicRoots.PrimaryDir(poi), true, claims, out _);
            Is(!prim.Any(f => f.IndexOf("p_poi_t3", StringComparison.OrdinalIgnoreCase) >= 0),
               "the recursive parent does not swallow the child context's track");
            Is(prim.Any(f => f.IndexOf("p_poi_t1", StringComparison.OrdinalIgnoreCase) >= 0),
               "but does keep a subfolder no context owns");

            // claims are primary-only: a flat context never claims anything
            Eq(MusicRoots.ClaimPrefixes(env.Contexts["Ambient"], env.Contexts.Values).Length, 0,
               "a flat context claims nothing, so it pays nothing");
        }

        // ── the change signature ──────────────────────────────────────────────
        static void Signature(Env env)
        {
            Head("change signature");
            env.Activate();
            Contexts amb = env.Contexts["Ambient"];

            Func<SourceScan.WatchSig> sig = () => SourceScan.ContextWatchSignature(
                MusicRoots.DirsFor(amb), MusicRoots.SloppyFor(amb), false, null);

            long a = sig().Fold;
            Is(sig().Fold == a, "an unchanged world gives the same signature");

            string packDir = env.Dir(3, "ambient");
            Env.Track(packDir, "added.ogg");
            long b = sig().Fold;
            Is(b != a, "a file ADDED to a pack changes the signature");

            File.Delete(Path.Combine(packDir, "added.ogg"));
            long c = sig().Fold;
            Is(c != b, "a file REMOVED from a pack changes the signature");

            // THE ONE A MAX-OF-TIMESTAMPS FOLD MISSES. Deleting a whole folder makes it
            // report MinValue, which is the identity element of max — so the primary's
            // unchanged timestamp would carry the day and the pool would keep dead files.
            string doomed = env.Dir(6, "ambient");
            string stash = doomed + "_stash";
            Directory.Move(doomed, stash);
            long d = sig().Fold;
            Is(d != c, "a whole pack FOLDER disappearing changes the signature");
            Directory.Move(stash, doomed);
            Is(sig().Fold != d, "and reappearing changes it back");

            // a folder that never existed still participates, so creating one is noticed
            string fresh = env.Dir(1, "menu");
            long e = SourceScan.ContextWatchSignature(MusicRoots.DirsFor(env.Contexts["Menu"]),
                        MusicRoots.SloppyFor(env.Contexts["Menu"]), false, null).Fold;
            Env.Track(fresh, "new_menu.ogg");
            long f = SourceScan.ContextWatchSignature(MusicRoots.DirsFor(env.Contexts["Menu"]),
                        MusicRoots.SloppyFor(env.Contexts["Menu"]), false, null).Fold;
            Is(f != e, "creating a context folder in a pack changes the signature");

            // ROOT ORDER is part of the fold: two roots swapped is a different world
            string[] d1 = MusicRoots.DirsFor(amb);
            var swapped = (string[])d1.Clone();
            string tmp = swapped[1]; swapped[1] = swapped[2]; swapped[2] = tmp;
            Is(SourceScan.ContextWatchSignature(swapped, MusicRoots.SloppyFor(amb), false, null).Fold
               != SourceScan.ContextWatchSignature(d1, MusicRoots.SloppyFor(amb), false, null).Fold,
               "folder ORDER is part of the signature");

            // and the separator matters, so ["ab"],["c"] cannot collide with ["a"],["bc"]
            Is(SourceScan.ContextWatchSignature(new[] { "x/ab", "x/c" }, new bool[2], false, null).Fold
               != SourceScan.ContextWatchSignature(new[] { "x/a", "x/bc" }, new bool[2], false, null).Fold,
               "folder names cannot run together in the hash");

            // the future-timestamp trap: a negative age must NOT read as just-touched,
            // or the folder re-enumerates every tick forever.
            Is(!SourceScan.RecentlyTouched(DateTime.UtcNow.AddHours(2), 6.0),
               "a FUTURE timestamp does not read as recently touched");
            Is(SourceScan.RecentlyTouched(DateTime.UtcNow.AddSeconds(-1), 6.0),
               "a timestamp a second ago does");
            Is(!SourceScan.RecentlyTouched(DateTime.MinValue, 6.0), "and MinValue does not");
        }

        // ── scale ─────────────────────────────────────────────────────────────
        static void Scale(Env env)
        {
            Head("scale (30 packs)");
            env.Activate();
            Contexts amb = env.Contexts["Ambient"];
            string[] dirs = MusicRoots.DirsFor(amb);
            bool[] sl = MusicRoots.SloppyFor(amb);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) SourceScan.ContextWatchSignature(dirs, sl, false, null);
            sw.Stop();
            double perSig = sw.Elapsed.TotalMilliseconds / 100.0;
            Console.WriteLine($"  info  signature over {dirs.Length} folders: {perSig:0.00} ms");
            Is(perSig < 25, "a signature tick stays well under the 2 s watch interval");

            sw.Restart();
            for (int i = 0; i < 20; i++)
                SourceScan.EnumerateFlatPoolMulti(dirs, SourceScan._noClaimPrefixes, out _);
            sw.Stop();
            Console.WriteLine($"  info  full enumeration: {sw.Elapsed.TotalMilliseconds / 20.0:0.00} ms");

            // an EMPTY context is the expensive case: it forces a rescan every tick
            Contexts none = env.Ctx("Nothing", "no/such/place");
            string[] nd = MusicRoots.DirsFor(none);
            sw.Restart();
            for (int i = 0; i < 100; i++)
                SourceScan.EnumerateFlatPoolMulti(nd, SourceScan._noClaimPrefixes, out _);
            sw.Stop();
            double perEmpty = sw.Elapsed.TotalMilliseconds / 100.0;
            Console.WriteLine($"  info  empty context across {nd.Length} folders: {perEmpty:0.00} ms");
            Is(perEmpty < 10, "an empty context costs only its Directory.Exists calls");
        }

        // ── one root behaves exactly as before ────────────────────────────────
        static void SingleRootParity(Env env)
        {
            Head("single-root parity");
            MusicRoots.SetPrimary(env.Primary);
            MusicRoots.SetAddons(new MusicRoot[0]);
            Eq(MusicRoots.Count, 1, "no add-ons means one root");

            foreach (string name in new[] { "Ambient", "Combat", "Menu" })
            {
                Contexts c = env.Contexts[name];
                string dir = MusicRoots.PrimaryDir(c);
                string[] oldWay = SourceScan.EnumerateFolderPoolRaw(dir, c.IncludeSubfolders,
                                        SourceScan._noClaimPrefixes, out string[] p1);
                Array.Sort(oldWay, StringComparer.OrdinalIgnoreCase);
                string[] newWay = SourceScan.EnumerateFlatPoolMulti(MusicRoots.DirsFor(c),
                                        SourceScan._noClaimPrefixes, out string[] p2);
                Is(oldWay.SequenceEqual(newWay, StringComparer.Ordinal),
                   $"{name}: the multi-folder path returns exactly the single-folder result");
                Is(p1.Length == p2.Length, $"{name}: same in-folder playlist list");
            }
            env.Activate();
        }
    }
}
