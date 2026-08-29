using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // THE CRAFTABLE EMITTERS, AND WHY THEY DISAPPEAR IN MULTIPLAYER.
    //
    // Config/recipes.xml adds a recipe for the ham radio and one for the bookshelf speaker, so a
    // player can make the blocks this mod plays music through instead of hunting for the ones a
    // level designer placed. Those recipes are meant for SINGLE PLAYER ONLY.
    //
    // An XPath patch cannot express that. It is applied once when the game loads its config, long
    // before there is a session to ask about — so the recipes are always added, and this takes
    // them away again as soon as the answer is knowable. Three cases, all of them "not single
    // player":
    //
    //   * a CLIENT connected to someone else's game — the server does not have these recipes, so
    //     a client that could craft them would be crafting something the host never agreed to;
    //   * a DEDICATED SERVER running the mod — it would hand them to everybody;
    //   * a HOST who started alone and then had someone join — which is why this is re-checked
    //     rather than decided once. Single player is a state, not a launch option.
    //
    // ConnectionManager.IsSinglePlayer is the game's own answer and is already how ContextResolver
    // decides the same question. It is false on any client, and false on a host the moment someone
    // else is in the world.
    //
    // REMOVING IS REVERSIBLE. The Recipe objects are kept, not discarded, so a host whose guest
    // leaves gets them back without reloading. CraftingManager.ClearRecipe and AddRecipe are the
    // game's own public verbs for this.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class CraftingGate
    {
        // The recipes Config/recipes.xml adds, by output name. Kept here rather than read from the
        // XML because this has to agree with that file, and a silent disagreement would mean a
        // recipe surviving into multiplayer — the exact thing this exists to prevent.
        private static readonly string[] Ours =
        {
            "radioHam",
            "cntStoreElectronicsBookShelfSpeaker",
        };

        // CraftingManager.PostInit() re-caches what depends on the recipe list, and it is 2.6+
        // ONLY -- A20 assigns the result of a private cacheNonScrapableRecipes() to a differently
        // shaped field instead. Bound by name, so its absence is a skipped refresh rather than a
        // MissingMethodException the moment somebody joins an A20 game.
        //
        // CmmApiDiff caught the typed call, and this one mattered: unlike the emitter layer, this
        // gate runs on EVERY version, so a member A20 does not have is a real failure there rather
        // than a reference that never executes. Skipping the refresh on A20 is survivable -- the
        // crafting UI reads the recipe list itself, and what goes stale is the scrap cache, which
        // these two recipes are not part of.
        private static MethodInfo _postInit;
        private static bool _postInitBound;

        private static void Refresh()
        {
            if (!_postInitBound)
            {
                _postInitBound = true;
                _postInit = AccessTools.Method(typeof(CraftingManager), "PostInit", new Type[0]);
            }
            if (_postInit != null) _postInit.Invoke(null, null);
        }

        private static readonly List<Recipe> _withheld = new List<Recipe>();
        private static bool _removed;
        private static float _nextCheck;
        private const float CheckInterval = 5f;   // a join is not a per-frame event

        // Called from the mod's Update. Cheap: a clock compare on all but one tick in five seconds,
        // and even then only a list walk when the answer has actually changed.
        internal static void Tick()
        {
            if (!ContextMusicManager.inGame) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + CheckInterval;

            bool multiplayer;
            try
            {
                multiplayer = GameManager.IsDedicatedServer
                    // Defaults to TRUE (single player) only if the manager is missing entirely,
                    // which is the menu, where there is nothing to gate.
                    || !(SingletonMonoBehaviour<ConnectionManager>.Instance?.IsSinglePlayer ?? true);
            }
            catch { return; }

            if (multiplayer == _removed) return;      // already in the right state
            if (multiplayer) Remove(); else Restore();
        }

        private static void Remove()
        {
            try
            {
                _withheld.Clear();
                List<Recipe> all = CraftingManager.GetAllRecipes();
                if (all == null) return;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    Recipe r = all[i];
                    if (r == null || !IsOurs(r.GetName())) continue;
                    _withheld.Add(r);
                }
                for (int i = 0; i < _withheld.Count; i++) CraftingManager.ClearRecipe(_withheld[i]);
                if (_withheld.Count > 0)
                {
                    Refresh();
                    Log.Out($"[CMM] Multiplayer session — withdrew {_withheld.Count} craftable "
                          + "emitter recipe(s); they are single-player only.");
                }
                _removed = true;
            }
            catch (Exception e)
            {
                // Leaving them in is the wrong side to fail on, so say so loudly rather than
                // letting a server quietly hand out recipes it was never meant to have.
                Log.Warning("[CMM] Could not withdraw the single-player crafting recipes ("
                          + e.Message + "). They may be craftable in this session.");
            }
        }

        private static void Restore()
        {
            try
            {
                for (int i = 0; i < _withheld.Count; i++) CraftingManager.AddRecipe(_withheld[i]);
                if (_withheld.Count > 0)
                {
                    Refresh();
                    CMMConfig.D($"[CMM] Single player again — restored {_withheld.Count} craftable "
                              + "emitter recipe(s).");
                }
                _withheld.Clear();
                _removed = false;
            }
            catch { /* they simply stay withheld until the next world entry */ }
        }

        private static bool IsOurs(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < Ours.Length; i++)
                if (string.Equals(name, Ours[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
