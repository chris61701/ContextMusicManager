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
        // See Weigh() for what this caches and why it is safe to key on array identity.
        private string[] _cachePool;
        private string   _cacheAvoid;
        private int      _cacheMemory;
        private int      _cacheKeep, _cachePlayable;
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

        // The tracks this context may play right now, MATERIALISED — and nothing in the mod
        // calls it. It is the harness's oracle: Select walks the pool without ever building
        // this list, so CmmRotationTest pins the two together by drawing from Select twenty
        // thousand times and asserting the reachable set is exactly what this returns. Both
        // go through Keeps(), so they cannot disagree about the RULE; the test is what stops
        // them disagreeing about the traversal.
        //
        // Select does not use it because at a hundred thousand tracks this array is 781 KB,
        // measured, on every pick — over the 85 KB large-object-heap threshold, which is
        // reached at about 10,600 tracks. Music folders that big are exactly what the
        // recursive crawler exists for.
        //
        // Unlike Select this never refills a spent pass: it is a question, not a move.
        internal string[] Eligible(string[] pool, int memory, string avoid,
                                   TrackFlag banned, TrackFlag favourite, int favouriteAllowance)
        {
            if (pool == null || pool.Length == 0) return pool;

            int keep = 0;
            for (int i = 0; i < pool.Length; i++)
                if (Keeps(pool[i], memory, avoid, banned, favourite, favouriteAllowance)) keep++;
            if (keep == pool.Length) return pool;   // nothing filtered; hand back the original

            var outp = new string[keep];
            int n = 0;
            for (int i = 0; i < pool.Length && n < keep; i++)
                if (Keeps(pool[i], memory, avoid, banned, favourite, favouriteAllowance)) outp[n++] = pool[i];
            return outp;
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
            int keep = 0;
            for (int i = 0; i < pool.Length; i++)
                if (Keeps(pool[i], memory, null, banned, favourite, favouriteAllowance)) keep++;
            return keep;
        }

        // ── The whole RandomNoRepeat selection ────────────────────────────────
        // Bag + one-track avoid + cycle seam + weighted roll, and NOTHING IS ALLOCATED.
        //
        // The obvious shape — filter the pool into a candidates array, then roll over it —
        // costs one array copy per pick, which is fine at a few hundred tracks and is not
        // fine at a hundred thousand: 781 KB, measured, on every pick. Anything over about
        // 10,600 tracks puts that copy on the LARGE OBJECT HEAP, which is only reclaimed by
        // a gen2 collection and is not compacted. Music folders that big are exactly what
        // the recursive crawler exists for, so the array is never built at all. Instead:
        // one pass to total the eligible weight, a draw, and a second pass to walk to the
        // track that draw landed on.
        //
        // The eligibility RULE is shared with Eligible() below (both go through Keeps), so
        // the two cannot drift on what counts as playable — only on how they traverse.
        internal string Select(string[] pool, int memory, string avoid,
                               TrackFlag banned, TrackFlag favourite, float favouriteWeight,
                               TrackRng rng)
        {
            if (pool == null || pool.Length == 0) return null;
            // A single-track folder has nothing to rotate, and letting the bag drain and
            // refill on every play would just spin the cycle counter.
            if (pool.Length == 1) return (banned != null && banned(pool[0])) ? null : pool[0];

            float w = ScaledWeight(favouriteWeight, pool.Length);
            int allowance = Allowance(w);
            if (w < 1f) w = 1f;

            double total;
            int keep, playable;
            Weigh(pool, memory, avoid, banned, favourite, allowance, w, out keep, out playable, out total);

            // Nothing left to play but something COULD be played: the pass is spent, refill.
            // `playable` ignores the bag, so it is what distinguishes "this pass is done"
            // from "there is genuinely nothing else in this folder to switch to" — the
            // latter must not spin the counter.
            if (keep == 0 && playable > 0 && memory != Off)
            {
                Reset();
                Cycle++;
                Weigh(pool, memory, avoid, banned, favourite, allowance, w, out keep, out playable, out total);
            }

            // Still nothing: every track is banned, or the only unbanned one is the track
            // already playing. Re-weigh ignoring both the bag and the avoid so that lone
            // track replays rather than the context falling silent; a zero here means the
            // folder really is entirely banned, which the caller treats as an empty one.
            if (keep == 0)
            {
                Weigh(pool, Off, null, banned, favourite, allowance, w, out keep, out playable, out total);
                if (keep == 0) return null;
                return Walk(pool, Off, null, banned, favourite, allowance, w, total, rng);
            }
            return Walk(pool, memory, avoid, banned, favourite, allowance, w, total, rng);
        }

        // Pass 1: how many tracks are playable, and what do their weights add up to.
        // `total` is the roll's denominator — each favourite counts favouriteWeight×.
        private void Weigh(string[] pool, int memory, string avoid,
                           TrackFlag banned, TrackFlag favourite, int allowance, float w,
                           out int keep, out int playable, out double total)
        {
            // Cached across the picks of one track change: the staged pin, the pre-staged
            // advance and the play itself all ask, and nothing that decides the answer moves
            // between them. Only when no flags are set — those come from keybinds and this
            // class deliberately cannot see the store they live in.
            //
            // Keying on array identity is only safe because ContextCache.directorySnapshot
            // is always REPLACED, never edited in place: every writer (the crawler's partial
            // publishes, the watch diff, the presence scan, the missing-file prune, the
            // config reload) assigns a freshly built array, which is also what makes the
            // snapshot lock-free for its readers.
            bool cacheable = banned == null && favourite == null;
            if (cacheable && _cacheVersion == _version && _cacheMemory == memory
                && ReferenceEquals(pool, _cachePool) && ReferenceEquals(avoid, _cacheAvoid))
            {
                keep = _cacheKeep; playable = _cachePlayable; total = _cacheKeep;
                return;
            }

            keep = 0; playable = 0; total = 0.0;
            for (int i = 0; i < pool.Length; i++)
            {
                string f = pool[i];
                if (banned != null && banned(f)) continue;
                if (avoid != null && string.Equals(f, avoid, StringComparison.OrdinalIgnoreCase)) continue;
                playable++;
                if (memory != Off && !UnderAllowance(f, favourite, allowance)) continue;
                keep++;
                total += (favourite != null && favourite(f)) ? w : 1.0;
            }

            if (cacheable)
            {
                _cachePool = pool; _cacheAvoid = avoid; _cacheMemory = memory;
                _cacheKeep = keep; _cachePlayable = playable; _cacheVersion = _version;
            }
        }

        // Pass 2: walk to the track the draw landed on. `last` covers the floating-point
        // edge where the roll lands exactly on the total.
        private string Walk(string[] pool, int memory, string avoid,
                            TrackFlag banned, TrackFlag favourite, int allowance, float w,
                            double total, TrackRng rng)
        {
            double roll = rng() * total;
            string last = null;
            for (int i = 0; i < pool.Length; i++)
            {
                string f = pool[i];
                if (!Keeps(f, memory, avoid, banned, favourite, allowance)) continue;
                last = f;
                roll -= (favourite != null && favourite(f)) ? w : 1.0;
                if (roll < 0.0) return f;
            }
            return last;
        }

        // ── FavouriteWeight scales with the folder ────────────────────────────
        // A raw multiplier means completely different things at different library sizes,
        // which made the setting almost useless in exactly the libraries it matters most
        // in. MEASURED, one favourite at weight 3: in a 40-track folder it came round
        // every ~13 tracks (about 45 minutes of play); in a 1000-track folder every ~333
        // (about 19 hours), because 3x of one-in-a-thousand is still one-in-a-thousand.
        // Same setting, same intent, wildly different result.
        //
        // So the weight is calibrated against a REFERENCE folder size and scaled from
        // there, which makes it mean the same share of the music everywhere: at weight 3
        // a favourite comes round roughly once an hour of play whether the folder holds
        // fifty tracks or fifty thousand.
        //
        // It never scales DOWN. Below the reference size the raw weight stands, so no
        // existing small-folder setup changes at all — this can only ever give a
        // favourite more turns than it had, never fewer.
        internal const int WeightReferencePool = 50;

        internal static float ScaledWeight(float favouriteWeight, int poolSize)
        {
            if (favouriteWeight <= 1f || poolSize <= WeightReferencePool) return favouriteWeight;
            float scaled = favouriteWeight * poolSize / WeightReferencePool;
            return scaled < favouriteWeight ? favouriteWeight : scaled;
        }

        // A favourite's turns per pass. The weight is a float because it is also the
        // weight in the draw; as an allowance it has to be a whole number of plays.
        //
        // BOTH have to use the scaled value. The allowance alone would do nothing: a
        // favourite drawn at weight 3 out of a thousand is picked 0.3% of the time, so it
        // would never reach a 60-turn allowance before the pass ended. The draw rate is
        // what it actually gets; the allowance is what stops it exceeding that.
        internal static int Allowance(float scaledWeight)
            => scaledWeight < 1f ? 1 : (int)(scaledWeight + 0.5f);

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

            // Same folder-size scaling as the bag, so plain Random and RandomNoRepeat give
            // a favourite the same share of the music rather than differing by library size.
            float w = ScaledWeight(favouriteWeight, candidates.Length);
            if (w < 1f) w = 1f;
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
