using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // THE CRAFTABLE EMITTERS: BUILT IN CODE, IN SINGLE PLAYER, WHERE THE BLOCK EXISTS.
    //
    // The mod plays music through two in-world blocks that otherwise only exist where a level
    // designer put them. These recipes let a player make their own:
    //
    //   Ham Radio  2 Electrical Parts + 2 Mechanical + 6 Scrap Polymers + 2 Duct Tape
    //   Speaker    2 Electrical Parts + 3 Scrap Polymers + 2 Wood + 1 Duct Tape
    //
    // ── Why this is not a Config/recipes.xml patch, which is what it was first ──────────────
    // Two reasons, and the second one is fatal.
    //
    // First, an XPath patch cannot ask whether the session is single player. It is applied once
    // at config load, long before there is a session, so an XML version had to add the recipes
    // always and take them away again afterwards — correct, but backwards.
    //
    // Second, and this is the one that mattered: A20 HAS NO HAM RADIO BLOCK AT ALL. Not renamed,
    // not moved — there is no radio block of any kind in its blocks.xml. RecipesFromXml does not
    // skip a recipe whose output it cannot resolve, it THROWS ("No item/block with name 'radioHam'
    // existing"), which takes recipe loading down with it. One payload ships to all four game
    // versions, so an XML recipe naming a block that A20 lacks would have bricked crafting on A20
    // the moment anyone installed this build. Nothing in the emitter layer runs on A20 anyway —
    // the recipes were never going to be useful there, only harmful.
    //
    // Building them here fixes both at once. ItemClass.GetItem returns ItemValue.None rather than
    // throwing, so a missing block is a recipe quietly not offered; and "single player" is checked
    // before anything is added rather than after.
    //
    // ── Single player is a STATE, not a launch option ───────────────────────────────────────
    // Re-checked on a timer rather than decided once, because all three of these are "not single
    // player" and the last one can happen at any moment:
    //
    //   * a CLIENT connected to someone else's game — the server never agreed to these recipes;
    //   * a DEDICATED SERVER running the mod — it would hand them to everybody;
    //   * a HOST who started alone and then had somebody join.
    //
    // ConnectionManager.IsSinglePlayer is the game's own answer and is already how ContextResolver
    // decides the same question: false on any client, and false on a host the moment someone else
    // is in the world. Leaving is handled too — the recipes come back.
    //
    // Recipe's fields are identical on A20 and 3.2 (itemValueType, count, ingredients,
    // craftingTime, craftingArea), so building one by hand needs no compat shim.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class CraftingGate
    {
        // What to build. Output block, craft time, then ingredient/count pairs. Counts are
        // Chris's. Always unlocked and craftable in the backpack (no crafting area): this is a
        // convenience a mod adds, not a progression step, and hiding it behind a schematic that
        // does not exist would make it unreachable.
        private sealed class Spec
        {
            public string Output;
            public float Seconds;
            public string[] Items;
            public int[] Counts;
        }

        private static readonly Spec[] Specs =
        {
            new Spec {
                Output = "radioHam", Seconds = 20f,
                Items  = new[] { "resourceElectricParts", "resourceMechanicalParts",
                                 "resourceScrapPolymers", "resourceDuctTape" },
                Counts = new[] { 2, 2, 6, 2 },
            },
            // The bookshelf speaker despite its "cnt" prefix is plain decoration (it extends
            // decoEntityPolymerMaster). The other speaker the mod listens for really is a loot
            // container, and a craftable lootable shelf would be a strange thing to hand a player.
            new Spec {
                Output = "cntStoreElectronicsBookShelfSpeaker", Seconds = 15f,
                Items  = new[] { "resourceElectricParts", "resourceScrapPolymers",
                                 "resourceWood", "resourceDuctTape" },
                Counts = new[] { 2, 3, 2, 1 },
            },
        };

        // Built once and reused, so joining and leaving does not churn objects.
        private static List<Recipe> _built;

        // Null when this game has no such block (A20 has no ham radio at all) or no such
        // ingredient. ItemClass.GetItem answers ItemValue.None rather than throwing, which is the
        // whole reason this can be done safely in code and could not be done safely in XML.
        private static Recipe Build(Spec spec)
        {
            ItemValue outp = ItemClass.GetItem(spec.Output);
            if (outp == null || outp.type == 0) return null;

            var r = new Recipe();
            r.itemValueType = outp.type;
            r.count = 1;
            r.craftingTime = spec.Seconds;
            r.craftingArea = "";            // backpack
            r.UseIngredientModifier = true;
            for (int i = 0; i < spec.Items.Length; i++)
            {
                ItemValue ing = ItemClass.GetItem(spec.Items[i]);
                if (ing == null || ing.type == 0) return null;
                r.ingredients.Add(new ItemStack(ing, spec.Counts[i]));
            }
            return r;
        }

        // CraftingManager.PostInit() re-caches        // CraftingManager.PostInit() re-caches what depends on the recipe list, and it is 2.6+
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

        private static bool _offered;
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

            if (multiplayer != _offered) return;      // already in the right state
            if (multiplayer) Withdraw(); else Offer();
        }

        private static void Offer()
        {
            try
            {
                if (_built == null)
                {
                    _built = new List<Recipe>();
                    for (int i = 0; i < Specs.Length; i++)
                    {
                        Recipe r = Build(Specs[i]);
                        if (r != null) _built.Add(r);
                        else CMMConfig.D($"[CMM] No craftable '{Specs[i].Output}' on this game "
                                       + "version — that recipe is not offered.");
                    }
                }
                if (_built.Count == 0) { _offered = true; return; }

                List<Recipe> all = CraftingManager.GetAllRecipes();
                for (int i = 0; i < _built.Count; i++)
                    if (all == null || !all.Contains(_built[i])) CraftingManager.AddRecipe(_built[i]);
                Refresh();
                CMMConfig.D($"[CMM] Single player — {_built.Count} craftable emitter recipe(s) offered.");
                _offered = true;
            }
            catch (Exception e)
            {
                Log.Warning("[CMM] Could not add the craftable emitter recipes (" + e.Message + ").");
                _offered = true;   // do not retry every tick
            }
        }

        private static void Withdraw()
        {
            try
            {
                if (_built != null)
                {
                    for (int i = 0; i < _built.Count; i++) CraftingManager.ClearRecipe(_built[i]);
                    if (_built.Count > 0)
                    {
                        Refresh();
                        Log.Out($"[CMM] Multiplayer session — withdrew {_built.Count} craftable "
                              + "emitter recipe(s); they are single-player only.");
                    }
                }
                _offered = false;
            }
            catch (Exception e)
            {
                // Leaving them in is the wrong side to fail on, so say so loudly rather than let a
                // server quietly hand out recipes it was never meant to have.
                Log.Warning("[CMM] Could not withdraw the single-player crafting recipes ("
                          + e.Message + "). They may be craftable in this session.");
            }
        }

    }
}
