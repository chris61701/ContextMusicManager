using System;
using System.Collections.Generic;

namespace CMM
{
    internal static class Program
    {
        static int _fail;

        internal static int Main(string[] args)
        {
            Console.WriteLine("CmmCraftTest - the real CraftingGate against a game that reloads");
            Console.WriteLine();

            Fresh_single_player_offers_both();
            A_world_load_puts_them_back();
            Many_world_loads_in_a_row();
            Ids_moving_under_a_surviving_recipe_rebuilds_it();
            Multiplayer_withdraws_and_does_not_churn();
            Leaving_multiplayer_gives_them_back();
            A_client_never_offers();
            A_dedicated_server_never_offers();
            A_version_without_the_block_offers_the_other_one();
            Steady_state_costs_nothing();

            Console.WriteLine(new string('=', 62));
            Console.WriteLine(_fail == 0 ? "every case held." : _fail + " CASE(S) FAILED");
            return _fail == 0 ? 0 : 1;
        }

        // ── the rig ─────────────────────────────────────────────────────────────────────────
        // Ticks are driven, not waited for: CraftingGate throttles itself to five seconds, so the
        // clock moves rather than the test sleeping.
        static void Tick(int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                UnityEngine.Time.realtimeSinceStartup += 6f;
                CraftingGate.Tick();
            }
        }

        static void Reset(bool singlePlayer = true, bool dedi = false, bool hamRadio = true)
        {
            Game.HamRadioExists = hamRadio;
            Game.BuildItemTable(1000);
            CraftingManager.recipes.Clear();
            CraftingManager.PostInit();
            GameManager.IsDedicatedServer = dedi;
            SingletonMonoBehaviour<ConnectionManager>.Instance =
                new ConnectionManager { IsSinglePlayer = singlePlayer };
            ContextMusicManager.inGame = true;
            Log.Lines.Clear();
            ResetGate();
        }

        // Each case needs a process-fresh gate. Done by reflection rather than by adding a reset
        // method to the shipping class: the gate has no reason to expose one, and a test that
        // needs production code widened for it is a test shaping the design.
        static void ResetGate()
        {
            var t = typeof(CraftingGate);
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            t.GetField("_built", F).SetValue(null, null);
            t.GetField("_builtFor", F).SetValue(null, null);
            t.GetField("_offered", F).SetValue(null, false);
            t.GetField("_failures", F).SetValue(null, 0);
            t.GetField("_nextCheck", F).SetValue(null, 0f);
        }

        // What a player would actually find: the recipe is in the list the crafting window reads,
        // and it names the block it is supposed to make.
        static bool Craftable(string block)
        {
            foreach (Recipe r in CraftingManager.NonScrapableRecipes)
                if (string.Equals(r.GetName(), block, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static int CountOf(string block)
        {
            int n = 0;
            foreach (Recipe r in CraftingManager.recipes)
                if (string.Equals(r.GetName(), block, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        static void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "   pass  " : "   FAIL  ") + what
                              + (detail.Length > 0 ? "  (" + detail + ")" : ""));
            if (!ok) _fail++;
        }

        static void Case(string name) { Console.WriteLine("-- " + name); }

        const string Ham = "radioHam", Spk = "cntStoreElectronicsBookShelfSpeaker";

        // ── the cases ───────────────────────────────────────────────────────────────────────

        static void Fresh_single_player_offers_both()
        {
            Case("a fresh single-player world offers both");
            Reset();
            Tick();
            Check("the ham radio is craftable", Craftable(Ham));
            Check("the speaker is craftable", Craftable(Spk));
            Check("they reached the cache the crafting window reads",
                  CraftingManager.PostInits > 0, "PostInit ran " + CraftingManager.PostInits + "x");
        }

        // THE BUG. Craftable on the first world after launching and on no world after it.
        static void A_world_load_puts_them_back()
        {
            Case("a world load puts them back");
            Reset();
            Tick();
            bool before = Craftable(Ham);
            CraftingManager.LoadWorld();                  // the game reloads its recipe list
            Check("the game's reload really did drop them", !Craftable(Ham),
                  "otherwise this case proves nothing");
            Tick();
            Check("the ham radio is craftable again on the second world", Craftable(Ham));
            Check("the speaker is too", Craftable(Spk));
            Check("and it was craftable on the first world", before);
        }

        static void Many_world_loads_in_a_row()
        {
            Case("five worlds in one session");
            Reset();
            bool all = true;
            for (int w = 0; w < 5; w++)
            {
                Tick();
                if (!Craftable(Ham) || !Craftable(Spk)) all = false;
                CraftingManager.LoadWorld();
            }
            Check("craftable on every one of the five", all);
        }

        // A recipe object that SURVIVES a reload still holds the old block id, so reinstalling the
        // cached one would point it at whatever now owns that id.
        static void Ids_moving_under_a_surviving_recipe_rebuilds_it()
        {
            Case("ids moving under a surviving recipe");
            Reset();
            Tick();
            Recipe was = null;
            foreach (Recipe r in CraftingManager.recipes)
                if (r.GetName() == Ham) { was = r; break; }
            int oldType = was.itemValueType;

            // The ids move but our recipe objects are left in the list — the nastier half.
            Game.BuildItemTable(Game.IdBase += 37);
            CraftingManager.PostInit();
            Tick();

            Recipe now = null;
            foreach (Recipe r in CraftingManager.recipes)
                if (r.GetName() == Ham) { now = r; break; }
            Check("the ham radio still resolves to the ham radio", now != null);
            Check("and it was rebuilt, not reused with a stale id",
                  now != null && now.itemValueType != oldType,
                  now == null ? "no recipe" : oldType + " -> " + now.itemValueType);
            Check("no duplicate left behind", CountOf(Ham) == 1, CountOf(Ham) + " copies");
        }

        static void Multiplayer_withdraws_and_does_not_churn()
        {
            Case("somebody joins a single-player host");
            Reset();
            Tick();
            Check("craftable while alone", Craftable(Ham));

            SingletonMonoBehaviour<ConnectionManager>.Instance.IsSinglePlayer = false;
            Tick();
            Check("withdrawn once somebody joins", !Craftable(Ham));
            Check("the speaker went too", !Craftable(Spk));

            int recipesAfter = CraftingManager.recipes.Count;
            int postInits = CraftingManager.PostInits;
            Tick(20);                                     // two minutes of still-multiplayer
            Check("no churn while it stays multiplayer",
                  CraftingManager.recipes.Count == recipesAfter
                  && CraftingManager.PostInits == postInits,
                  "PostInit " + postInits + " -> " + CraftingManager.PostInits);
        }

        static void Leaving_multiplayer_gives_them_back()
        {
            Case("everybody leaves again");
            Reset();
            Tick();
            SingletonMonoBehaviour<ConnectionManager>.Instance.IsSinglePlayer = false;
            Tick();
            SingletonMonoBehaviour<ConnectionManager>.Instance.IsSinglePlayer = true;
            Tick();
            Check("craftable again once alone", Craftable(Ham) && Craftable(Spk));
            Check("exactly one of each", CountOf(Ham) == 1 && CountOf(Spk) == 1,
                  CountOf(Ham) + " ham, " + CountOf(Spk) + " speaker");
        }

        static void A_client_never_offers()
        {
            Case("a client connected to someone else's game");
            Reset(singlePlayer: false);
            Tick(10);
            Check("never offered", !Craftable(Ham) && !Craftable(Spk));
            Check("the recipe list is untouched", CraftingManager.recipes.Count == 0,
                  CraftingManager.recipes.Count + " recipes");
        }

        static void A_dedicated_server_never_offers()
        {
            Case("a dedicated server");
            Reset(singlePlayer: true, dedi: true);        // IsSinglePlayer true AND a dedi
            Tick(10);
            Check("never offered even though it reads as single player",
                  !Craftable(Ham) && !Craftable(Spk));
        }

        static void A_version_without_the_block_offers_the_other_one()
        {
            Case("a game version with no ham radio (A20)");
            Reset(hamRadio: false);
            Tick();
            Check("no ham radio recipe", !Craftable(Ham));
            Check("the speaker is still offered", Craftable(Spk));
            int postInits = CraftingManager.PostInits;
            Tick(20);
            Check("and it does not retry forever",
                  CraftingManager.PostInits == postInits,
                  "PostInit " + postInits + " -> " + CraftingManager.PostInits);
        }

        // The check runs every five seconds for the whole session, so it must not do work.
        static void Steady_state_costs_nothing()
        {
            Case("steady state");
            Reset();
            Tick();
            int postInits = CraftingManager.PostInits;
            int count = CraftingManager.recipes.Count;
            Tick(60);                                     // six minutes of nothing happening
            Check("no re-offer, no re-cache, no growth",
                  CraftingManager.PostInits == postInits && CraftingManager.recipes.Count == count,
                  "PostInit " + postInits + " -> " + CraftingManager.PostInits
                  + ", recipes " + count + " -> " + CraftingManager.recipes.Count);
        }
    }
}
