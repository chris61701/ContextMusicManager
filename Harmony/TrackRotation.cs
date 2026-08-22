using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // TRACK ROTATION — "play everything before playing anything twice".
    //
    // THE PROBLEM THIS FIXES. `RandomNoRepeat` has always been weaker than its
    // name: it excludes exactly ONE track, the one just played. Over a 200-track
    // folder that still lets a track come back on the very next pick, and the
    // birthday effect makes an early repeat likely within a single session — so a
    // large library does not FEEL large, which is the entire reason for having one.
    //
    // WHAT IT DOES. Each context remembers what it has played this CYCLE. A track
    // is eligible until it has had its allowance of plays; when nothing eligible is
    // left the cycle ends and everything is eligible again. That is a shuffled bag,
    // and it is the literal guarantee: every track plays once before any track plays
    // twice.
    //
    // ONE RULE COVERS BOTH MODES. Eligibility is always `plays < allowance`. The
    // memory setting only decides how much history is kept:
    //   • memory = -1 (whole pool)  nothing is ever dropped, so the bag drains and
    //                               refills — the strong guarantee.
    //   • memory =  N (a window)    only the last N plays are remembered, so a track
    //                               becomes eligible again once N others have played.
    //   • memory =  0 (off)         no memory at all; the caller's one-track avoid
    //                               is all that is left. This is the pre-1.0.5 behaviour.
    //
    // FAVOURITES SURVIVE. A strict bag would silently kill the favourite keybind:
    // one play per cycle each, exactly like everything else. So a favourite's
    // ALLOWANCE is FavouriteWeight plays per cycle rather than one. It stays in the
    // bag longer, which reproduces "picked more often" over a whole cycle instead of
    // only within a single roll. Bans are the mirror image: a banned track is never
    // eligible, and — the part that matters — it is not counted as eligible either,
    // or a folder with banned tracks in it would never finish a cycle.
    //
    // WHAT COMMITS. Picking is NOT playing. Three of the six PickNextFile callers
    // are speculative — the staged pin, the pre-staged advance, the missing-file
    // replacement walk — and their picks are routinely discarded. If a pick consumed
    // the bag, the bag would drain without a note being heard. So the commit happens
    // at PLAY time, from ContextCache.CommitToHistory, and only on the path where
    // that method decides the track is genuinely new. Resumes and Prev/Next replays
    // move the history cursor instead, and therefore cost the rotation nothing —
    // which is exactly the required "a resumed track must not consume a bag slot".
    //
    // SESSION-ONLY, DELIBERATELY. The cycle is not persisted. A restored bag would
    // have to be reconciled against a library that may have changed underneath it,
    // and it buys nothing: the first pick of a session is uniform over the whole pool
    // either way. Repetition is only noticeable WITHIN a listening session, and that
    // is precisely what this covers.
    //
    // Unity-free and dependency-free on purpose — the track flags arrive as delegates
    // and so does the random source — so the offline harness links THIS file and tests
    // the shipped algorithm rather than a hand-ported copy that drifts. That is also
    // why the WHOLE random selection lives here, weighted roll included, and not just
    // the bag: the parts that had to be tested together (a bag that runs out, the
    // one-track avoid that spans the seam, bans that must not stall a cycle) are the
    // parts a hand-port would have got subtly wrong.
    // ─────────────────────────────────────────────────────────────────────────
    internal delegate bool TrackFlag(string path);

    // Uniform in [0,1). Supplied by the caller so the harness can drive a fixed seed.
    internal delegate double TrackRng();

    internal sealed class TrackRotation
    {
        internal const int Off       =  0;   // no memory beyond the caller's one-track avoid
        internal const int WholePool = -1;   // remember everything until the cycle drains

        // One entry per PLAY, oldest first — the window's eviction order.
        private readonly List<string> _order = new List<string>();
        // Plays per track within the remembered span. Kept in step with _order.
        private readonly Dictionary<string, int> _plays =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Diagnostics only (`cmm rotation`): which pass through the library we are on,
        // and how many plays it has taken. Cycle starts at 1 — the first pass IS a cycle.
        internal int Cycle = 1;
        internal int Plays;                  // plays this cycle, INCLUDING evicted ones
        internal int Remembered => _order.Count;
        internal int DistinctRemembered => _plays.Count;

        // ── Eligible-set cache ────────────────────────────────────────────────
        // A track change asks for a pick three or four times over — the staged pin, the
        // pre-staged advance, the play itself — and nothing that decides eligibility
        // moves between them. Recomputing costs a dictionary probe per track per call,
        // which on a ten-thousand-track folder is milliseconds of main thread and a
        // fresh 80 KB array, three times, for an identical answer.
        //
        // Only cached when NO ban or favourite flags are in play. Those come from
        // keybinds and can change at any moment, and TrackRotation deliberately knows
        // nothing about the store they live in, so it cannot tell when they have. With
        // no flags set — the overwhelmingly common case — the answer depends on exactly
        // three things, all checked by reference or counter.
        //
        // KEYING ON ARRAY IDENTITY IS ONLY SAFE BECAUSE ContextCache.directorySnapshot is
        // always REPLACED, never edited in place — every writer (the crawler's partial
        // publishes, the watch diff, the presence scan, the missing-file prune, the
        // config reload) assigns a freshly built array, which is also what makes the
        // snapshot lock-free for the reader threads. If anything ever starts mutating one
        // in place, this cache goes stale silently and the bag starts handing out tracks
        // that are no longer in the folder.
        private string[] _cachePool, _cacheOut;
        private string   _cacheAvoid;
        private int      _version;          // bumped whenever what has played changes
        private int      _cacheVersion = -1;

        internal void Reset()
        {
            _order.Clear();
            _plays.Clear();
            Plays = 0;
            _version++;
        }

        // A track just STARTED PLAYING. Called once per genuinely-new track start.
        internal void Commit(string file, int memory)
        {
            if (string.IsNullOrEmpty(file) || memory == Off) return;
            _order.Add(file);
            int c;
            _plays.TryGetValue(file, out c);
            _plays[file] = c + 1;
            Plays++;
            _version++;
            // Window mode: forget the oldest play once the window is full. Whole-pool
            // mode never trims — the cycle ends by exhaustion instead (see Eligible).
            if (memory > 0)
                while (_order.Count > memory) DropOldest();
        }

        private void DropOldest()
        {
            string old = _order[0];
            _order.RemoveAt(0);
            int c;
            if (!_plays.TryGetValue(old, out c)) return;
            if (c <= 1) _plays.Remove(old); else _plays[old] = c - 1;
        }

        // The tracks this context may play right now: `pool` minus what it has already
        // used up this cycle, minus banned tracks, minus `avoid` (the track just
        // played). Returns `pool` itself — no allocation — whenever nothing is filtered
        // out, which is every pick of a folder with no bans and nothing played yet.
        //
        // The avoid filter is FUSED IN rather than applied by the caller afterwards.
        // Done separately it costs a second full-array copy on every pick, which at a
        // ten-thousand-track folder is 80 KB of garbage and a millisecond of main
        // thread for nothing.
        internal string[] Eligible(string[] pool, int memory, string avoid,
                                   TrackFlag banned, TrackFlag favourite, int favouriteAllowance)
        {
            if (pool == null || pool.Length == 0) return pool;

            bool cacheable = banned == null && favourite == null;
            if (cacheable && _cacheVersion == _version
                && ReferenceEquals(pool, _cachePool) && ReferenceEquals(avoid, _cacheAvoid))
                return _cacheOut;

            int keep, playable;
            Count(pool, memory, avoid, banned, favourite, favouriteAllowance, out keep, out playable);

            // Nothing left to play but something COULD be played: the cycle is spent, so
            // refill. `playable` counts tracks that are neither banned nor the just-played
            // one, and it is what stops the counter spinning on a folder where nothing
            // else exists to switch to — one unbanned track, or one track full stop.
            if (keep == 0 && playable > 0 && memory != Off)
            {
                Reset();
                Cycle++;
                Count(pool, memory, avoid, banned, favourite, favouriteAllowance, out keep, out playable);
            }

            string[] outp;
            if (keep == 0 || keep == pool.Length)
            {
                // keep == 0: all banned, or the pool is nothing but `avoid` — the caller's
                // roll deals with both. keep == pool.Length: nothing was filtered, so hand
                // back the original rather than copying it.
                outp = pool;
            }
            else
            {
                outp = new string[keep];
                int n = 0;
                for (int i = 0; i < pool.Length && n < keep; i++)
                    if (Keeps(pool[i], memory, avoid, banned, favourite, favouriteAllowance)) outp[n++] = pool[i];
            }

            if (cacheable)
            {
                _cachePool = pool; _cacheAvoid = avoid; _cacheOut = outp; _cacheVersion = _version;
            }
            return outp;
        }

        // keep     = pickable right now (not banned, not `avoid`, still under allowance)
        // playable = would be pickable if the cycle refilled (not banned, not `avoid`)
        private void Count(string[] pool, int memory, string avoid,
                           TrackFlag banned, TrackFlag favourite, int favouriteAllowance,
                           out int keep, out int playable)
        {
            keep = 0; playable = 0;
            // Fresh cycle with no bans and no avoid: everything qualifies, and skipping
            // the per-track dictionary probe is what keeps a big folder cheap.
            if (_plays.Count == 0 && banned == null && avoid == null)
            {
                keep = playable = pool.Length;
                return;
            }
            for (int i = 0; i < pool.Length; i++)
            {
                string f = pool[i];
                if (banned != null && banned(f)) continue;
                if (avoid != null && string.Equals(f, avoid, StringComparison.OrdinalIgnoreCase)) continue;
                playable++;
                if (memory == Off || UnderAllowance(f, favourite, favouriteAllowance)) keep++;
            }
        }

        private bool Keeps(string f, int memory, string avoid,
                           TrackFlag banned, TrackFlag favourite, int favouriteAllowance)
            => (banned == null || !banned(f))
            && (avoid == null || !string.Equals(f, avoid, StringComparison.OrdinalIgnoreCase))
            && (memory == Off || UnderAllowance(f, favourite, favouriteAllowance));

        private bool UnderAllowance(string f, TrackFlag favourite, int favouriteAllowance)
        {
            int c;
            if (!_plays.TryGetValue(f, out c)) return true;
            int allowance = (favourite != null && favourite(f) && favouriteAllowance > 1)
                          ? favouriteAllowance : 1;
            return c < allowance;
        }

        // How many of `pool` are still to come this cycle — the number the console shows.
        internal int RemainingIn(string[] pool, int memory, TrackFlag banned, TrackFlag favourite,
                                 int favouriteAllowance)
        {
            if (pool == null) return 0;
            int keep, playable;
            Count(pool, memory, null, banned, favourite, favouriteAllowance, out keep, out playable);
            return keep;
        }

        // ── The whole RandomNoRepeat selection ────────────────────────────────
        // Bag + one-track avoid + cycle seam in one filter, then the weighted roll.
        // Returns null only when every track in the pool is banned, which the caller
        // treats as an empty folder.
        internal string Select(string[] pool, int memory, string avoid,
                               TrackFlag banned, TrackFlag favourite, float favouriteWeight,
                               TrackRng rng)
        {
            if (pool == null || pool.Length == 0) return null;
            // A single-track folder has nothing to rotate, and letting the bag drain and
            // refill on every play would just spin the cycle counter.
            if (pool.Length == 1) return (banned != null && banned(pool[0])) ? null : pool[0];

            string[] candidates = Eligible(pool, memory, avoid, banned, favourite,
                                           Allowance(favouriteWeight));

            // No retry needed on a null. Eligible either filtered the banned tracks out of
            // `candidates` — in which case the roll cannot come up empty — or handed back
            // the whole pool untouched, and the roll's own ban handling is then the only
            // thing that can return null. Either way a null here means the folder really is
            // entirely banned, which the caller treats as an empty one.
            //
            // The case that used to need the retry (everything except the playing track is
            // banned) now falls out of Eligible: keep lands at 0, there is nothing to
            // refill to, and the pool comes back whole so the roll replays that one track.
            return Roll(candidates, rng, banned, favourite, favouriteWeight);
        }

        // A favourite's turns per pass. FavouriteWeight is a float because it is a weight
        // in the single roll below; as an allowance it has to be a whole number of plays,
        // so 3.0 means "three turns per pass through the library".
        internal static int Allowance(float favouriteWeight)
            => favouriteWeight < 1f ? 1 : (int)(favouriteWeight + 0.5f);

        // ── The weighted roll ─────────────────────────────────────────────────
        // Uniform over the candidates, except that banned tracks are excluded and each
        // favourite counts favouriteWeight× in the draw. Returns null when every
        // candidate is banned. With no flags anywhere (the common case) this is a single
        // uniform pick; with flags it is one O(n) pass of set lookups and no allocation.
        internal static string Roll(string[] candidates, TrackRng rng,
                                    TrackFlag banned, TrackFlag favourite, float favouriteWeight)
        {
            if (candidates == null || candidates.Length == 0) return null;
            if (banned == null && favourite == null)
                return candidates[Index(rng, candidates.Length)];

            float w = favouriteWeight < 1f ? 1f : favouriteWeight;
            int eligible = 0, favs = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (banned != null && banned(candidates[i])) continue;
                eligible++;
                if (favourite != null && favourite(candidates[i])) favs++;
            }
            if (eligible == 0) return null;   // all banned → caller resolves like an empty folder

            double roll = rng() * ((eligible - favs) + favs * (double)w);
            string last = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (banned != null && banned(candidates[i])) continue;
                last = candidates[i];
                roll -= (favourite != null && favourite(candidates[i])) ? w : 1.0;
                if (roll < 0.0) return candidates[i];
            }
            return last;   // fp edge (the roll landed exactly on the total)
        }

        private static int Index(TrackRng rng, int length)
        {
            int i = (int)(rng() * length);
            return i < 0 ? 0 : (i >= length ? length - 1 : i);   // guard an rng that returns 1.0
        }
    }
}
