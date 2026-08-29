using System;
using System.Collections.Generic;
using System.IO;
using CMM;

// The disposable world the soak runs against: a real music library on disk, and the
// mutation schedule that keeps changing it underneath the running code.
//
// Real files, in a real folder, mutated by real File.Move / Delete / WriteAllLines.
// No test-only back door into the catalog: if a change goes unnoticed, that is the
// finding, and a harness that reached in to force a rebuild would hide it.

internal sealed class SoakWorld
{
    internal readonly string Root;
    internal readonly string MusicDir;
    internal readonly string RadioDir;

    // THE REAL CONTEXT SET, read from the shipped contexts.xml, with each one's real
    // source folder. Hardcoding a guess here was the first thing this harness got wrong:
    // the decision script answers with names like Ambient_Forest_Night and POI_T2 that no
    // invented list contains, so every resolve looked like a violation and almost nothing
    // was ever selected. A harness that does not know the real context set is not testing
    // the real decision layer, it is testing its own vocabulary.
    internal static string[] ContextNames = new string[0];
    static readonly Dictionary<string, string> _sourceOf =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static bool LoadContextDefinitions(out string whereFrom)
    {
        // Straight from the shipped schema, which is the authoritative list of context
        // NAMES and has no dependencies at all. Reading contexts.xml was the obvious route
        // and the wrong one: that file is generated at first run and lives in the deployed
        // mod, not the repo, so the harness would have depended on a game install.
        //
        // Only the names matter. The folder each one reads from is the harness's own
        // business, because it builds its own library -- but a name the decision script can
        // return and the harness has never heard of is what made every resolve look like a
        // violation on the first run.
        var names = new List<string>();
        foreach (ContextEntry e in CMMContextsSchema.Layout)
            if (!string.IsNullOrEmpty(e.Name)) names.Add(e.Name);

        ContextNames = names.ToArray();
        foreach (string n in names) _sourceOf[n] = n;
        whereFrom = "CMMContextsSchema.Layout";
        return ContextNames.Length > 0;
    }

    readonly Dictionary<string, List<string>> _files =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    readonly Random _rng;
    int _serial;

    internal SoakWorld(string root, int seed)
    {
        Root = root;
        MusicDir = Path.Combine(root, "Music");
        RadioDir = Path.Combine(MusicDir, "radio");
        _rng = new Random(seed);
    }

    // ── Build ────────────────────────────────────────────────────────────────
    // 8 kHz mono keeps a "five minute" track under half a megabyte, so a library of a
    // few hundred tracks costs seconds to build and still decodes for real.
    const int Rate = 8000;

    internal static void Wav(string path, double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        int frames = Math.Max(1, (int)(Rate * seconds));
        int bytes = frames * 2;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(Rate);
            w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
            var buf = new byte[bytes];
            // A different level per track so the loudness measurement has real differences
            // to find rather than measuring the same tone over and over.
            double amp = 2000 + (path.GetHashCode() & 0x3fff);
            for (int i = 0; i < frames; i++)
            {
                short v = (short)(amp * Math.Sin(2 * Math.PI * 440 * i / Rate));
                buf[i * 2] = (byte)(v & 0xff); buf[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }
            w.Write(buf);
        }
    }

    internal void Build(int tracksPerContext, int stations, int tracksPerStation)
    {
        foreach (string ctx in ContextNames)
        {
            var list = new List<string>();
            // Stingers are short on purpose: a file under 400 ms has NO defined integrated
            // loudness at all -- EBU R128 needs one gated 400 ms block -- which is a case the
            // gain policy has to answer once rather than retry for ever.
            bool stinger = ctx == "Death" || ctx == "Spawn" || ctx == "POI_Cleared";
            string dir = Path.Combine(MusicDir, SourceFolder(ctx));
            for (int i = 0; i < tracksPerContext; i++)
            {
                string f = Path.Combine(dir, $"{Safe(ctx)}_{i}.wav");
                Wav(f, stinger ? (i == 0 ? 0.25 : 3) : 20 + i * 7);
                list.Add(f);
            }
            _files[ctx] = list;
        }

        for (int s = 0; s < stations; s++)
        {
            string name = "Station" + s;
            for (int i = 0; i < tracksPerStation; i++)
                Wav(Path.Combine(RadioDir, name, $"s{s}_{i}.wav"), 15 + i * 5);
        }

        // One station is a PLAYLIST rather than a folder, because that is what most of the
        // real library is, and playlists fail in ways folders do not.
        string plDir = Path.Combine(RadioDir, "Playlisted");
        var lines = new List<string> { "#EXTM3U" };
        for (int i = 0; i < tracksPerStation; i++)
        {
            string f = Path.Combine(plDir, $"pl_{i}.wav");
            Wav(f, 12 + i * 4);
            lines.Add(f);
        }
        File.WriteAllLines(Path.Combine(plDir, "Playlisted.m3u"), lines);
    }

    // The decoded length of a file this harness wrote: 8 kHz, 16-bit, mono, 44-byte header.
    // Derived rather than decoded because it is asked once per track change and the answer
    // is arithmetic; the DECODER's view of length is what the schedule tests exercise.
    internal static double LengthOf(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length - 44;
            return bytes > 0 ? bytes / 2.0 / Rate : 1.0;
        }
        catch { return 1.0; }
    }

    internal static string SourceFolder(string context)
        => _sourceOf.TryGetValue(context, out string src) ? src : context;

    static string Safe(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++) if (!char.IsLetterOrDigit(c[i])) c[i] = '_';
        return new string(c).ToLowerInvariant();
    }

    internal IList<string> Files(string context)
        => _files.TryGetValue(context, out List<string> l) ? l : (IList<string>)Array.Empty<string>();

    internal bool HasFiles(string context)
    {
        IList<string> l = Files(context);
        for (int i = 0; i < l.Count; i++) if (File.Exists(l[i])) return true;
        return false;
    }

    // Re-read a context's folder from disk. The soak owns this because it plays the part
    // the file watcher plays in game; the catalog does its own discovery for stations.
    internal void Rescan(string context)
    {
        string dir = Path.Combine(MusicDir, SourceFolder(context));
        var list = new List<string>();
        try { if (Directory.Exists(dir)) list.AddRange(Directory.GetFiles(dir, "*.wav")); }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        _files[context] = list;
    }

    // ── Mutations ────────────────────────────────────────────────────────────
    // Every one is a plain filesystem operation. What the code makes of it is the test.
    internal string Mutate(int which, out string description)
    {
        string ctx = ContextNames[_rng.Next(ContextNames.Length)];
        IList<string> pool = Files(ctx);
        description = null;

        switch (which % 7)
        {
            case 0:   // ADD a track to a context
            {
                string f = Path.Combine(MusicDir, SourceFolder(ctx), $"added_{_serial++}.wav");
                Wav(f, 18);
                Rescan(ctx);
                description = "add " + ctx;
                return ctx;
            }
            case 1:   // DELETE one
            {
                if (pool.Count <= 1) { description = "delete (skipped: last track)"; return ctx; }
                string victim = pool[_rng.Next(pool.Count)];
                try { File.Delete(victim); } catch { }
                Rescan(ctx);
                description = "delete " + Path.GetFileName(victim);
                return ctx;
            }
            case 2:   // RENAME one
            {
                if (pool.Count == 0) { description = "rename (skipped: empty)"; return ctx; }
                string src = pool[_rng.Next(pool.Count)];
                string dst = Path.Combine(Path.GetDirectoryName(src), $"renamed_{_serial++}.wav");
                try { if (File.Exists(src)) File.Move(src, dst); } catch { }
                Rescan(ctx);
                description = "rename " + Path.GetFileName(src);
                return ctx;
            }
            case 3:   // MOVE one to a different context
            {
                if (pool.Count <= 1) { description = "move (skipped: last track)"; return ctx; }
                string other = ContextNames[_rng.Next(ContextNames.Length)];
                if (other == ctx) { description = "move (skipped: same context)"; return ctx; }
                string src = pool[_rng.Next(pool.Count)];
                string dst = Path.Combine(MusicDir, SourceFolder(other), $"moved_{_serial++}.wav");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(src)) File.Move(src, dst);
                }
                catch { }
                Rescan(ctx); Rescan(other);
                description = $"move {ctx} -> {other}";
                return ctx;
            }
            case 4:   // REWRITE a track at a different length (the schedule-shifting case)
            {
                if (pool.Count == 0) { description = "rewrite (skipped: empty)"; return ctx; }
                string f = pool[_rng.Next(pool.Count)];
                try { Wav(f, 5 + _rng.Next(40)); } catch { }
                description = "re-encode " + Path.GetFileName(f);
                return ctx;
            }
            case 5:   // EDIT the playlist station
            {
                string plDir = Path.Combine(RadioDir, "Playlisted");
                string m3u = Path.Combine(plDir, "Playlisted.m3u");
                var keep = new List<string> { "#EXTM3U" };
                try
                {
                    string[] have = Directory.GetFiles(plDir, "*.wav");
                    Array.Sort(have, StringComparer.OrdinalIgnoreCase);
                    // Drop a random entry, and name one that does not exist -- a playlist
                    // pointing at something that is gone is the ordinary failure, not the
                    // exotic one.
                    for (int i = 0; i < have.Length; i++)
                        if (i != _rng.Next(Math.Max(1, have.Length))) keep.Add(have[i]);
                    keep.Add(Path.Combine(plDir, "not-here.wav"));
                    File.WriteAllLines(m3u, keep);
                }
                catch { }
                description = "edit playlist";
                return ctx;
            }
            default:  // TAKE A STATION AWAY, and give it back next time
            {
                string live = Path.Combine(RadioDir, "Station0");
                string hidden = Path.Combine(RadioDir, "_Station0_away");
                try
                {
                    if (Directory.Exists(live)) { Directory.Move(live, hidden); description = "station folder removed"; }
                    else if (Directory.Exists(hidden)) { Directory.Move(hidden, live); description = "station folder restored"; }
                    else description = "station toggle (skipped)";
                }
                catch { description = "station toggle (locked)"; }
                return ctx;
            }
        }
    }
}
