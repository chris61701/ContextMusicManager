using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

// ── A FAKE GAME THAT CAN DO THE THING THE REAL ONE DOES ──────────────────────────────────────
//
// Only one behaviour is being reproduced and it is the one that caused the bug: a world load
// EMPTIES CraftingManager's recipe list and REASSIGNS block ids. Everything here exists to make
// that reproducible on demand; nothing here decides anything CraftingGate should be deciding.

namespace UnityEngine
{
    internal static class Time { internal static float realtimeSinceStartup = 0f; }
}

// The game's logger.
public static class Log
{
    public static List<string> Lines = new List<string>();
    public static void Out(string s)     { Lines.Add(s); }
    public static void Warning(string s) { Lines.Add("WRN " + s); }
    public static void Error(string s)   { Lines.Add("ERR " + s); }
}

// ── items and blocks ────────────────────────────────────────────────────────────────────────
public sealed class ItemValue
{
    public int type;
    public ItemValue(int t) { type = t; }
}

public sealed class ItemClass
{
    public int Id;
    public string Name;
    private readonly bool _isBlock;

    public ItemClass(int id, string name, bool isBlock) { Id = id; Name = name; _isBlock = isBlock; }
    public bool IsBlock() => _isBlock;
    public string GetItemName() => Name;
    public string[] Groups;

    // The world's item table, rebuilt whenever the world is.
    internal static readonly Dictionary<int, ItemClass> ById = new Dictionary<int, ItemClass>();
    internal static readonly Dictionary<string, int> IdByName =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public static ItemValue GetItem(string name)
    {
        int id;
        // ItemValue.None (type 0) rather than a throw, which is what makes building recipes in
        // code safe on a version that lacks the block.
        return new ItemValue(IdByName.TryGetValue(name, out id) ? id : 0);
    }

    public static ItemClass GetForId(int id)
    {
        ItemClass ic;
        return ById.TryGetValue(id, out ic) ? ic : null;
    }
}

public sealed class Block
{
    public string[] GroupNames = { "Decor/Miscellaneous" };
    internal static readonly Dictionary<int, Block> _list = new Dictionary<int, Block>();
    public static BlockList list = new BlockList();
    public sealed class BlockList { public Block this[int i] { get { Block b; return _list.TryGetValue(i, out b) ? b : null; } } }
}

// ── recipes ─────────────────────────────────────────────────────────────────────────────────
public sealed class ItemStack
{
    public ItemValue itemValue; public int count;
    public ItemStack(ItemValue v, int c) { itemValue = v; count = c; }
}

public sealed class Recipe
{
    public int itemValueType;
    public int count;
    public float craftingTime;
    public string craftingArea;
    public bool UseIngredientModifier;
    public bool IsLearnable;
    public readonly List<ItemStack> ingredients = new List<ItemStack>();

    public string GetName()
    {
        ItemClass ic = ItemClass.GetForId(itemValueType);
        return ic == null ? string.Empty : ic.GetItemName();
    }
}

public static class CraftingManager
{
    public static List<Recipe> recipes = new List<Recipe>();

    private static readonly List<Recipe> nonScrapable = new List<Recipe>();
    public static ReadOnlyCollection<Recipe> NonScrapableRecipes =
        new ReadOnlyCollection<Recipe>(nonScrapable);

    public static List<Recipe> GetAllRecipes() => recipes;
    public static void AddRecipe(Recipe r) { recipes.Add(r); }
    public static void ClearRecipe(Recipe r) { recipes.Remove(r); }

    // The cache the crafting window actually reads. CraftingGate binds this by NAME through
    // AccessTools, because A20 does not have it — so the signature here has to match the real
    // one exactly or the test would pass a bind that fails in the game.
    public static int PostInits;
    public static void PostInit()
    {
        PostInits++;
        nonScrapable.Clear();
        nonScrapable.AddRange(recipes);
    }

    // WHAT A WORLD LOAD DOES. The list is emptied and reloaded from XML, and block ids are
    // handed out afresh — which is the whole bug: a recipe cached from the last world holds an
    // id that now belongs to something else, or to nothing.
    public static void LoadWorld(bool shiftIds = true)
    {
        recipes.Clear();
        nonScrapable.Clear();
        Game.BuildItemTable(shiftIds ? Game.IdBase += 37 : Game.IdBase);
        // vanilla's own recipes come back
        for (int i = 0; i < 1500; i++) recipes.Add(new Recipe { itemValueType = -1 });
        PostInit();
    }
}

// ── session shape ───────────────────────────────────────────────────────────────────────────
public sealed class ConnectionManager
{
    public bool IsSinglePlayer = true;
}

public class SingletonMonoBehaviour<T> where T : class
{
    public static T Instance;
}

public static class GameManager
{
    public static bool IsDedicatedServer;
}

namespace CMM
{
    internal static class CMMConfig
    {
        internal static bool DebugLogging = true;
        internal static void D(string s) { Log.Out(s); }
    }

    internal static class ContextMusicManager { internal static bool inGame = true; }
}

// ── the world the test drives ───────────────────────────────────────────────────────────────
internal static class Game
{
    internal static int IdBase = 1000;
    internal static bool HamRadioExists = true;

    internal static readonly string[] Blocks =
    {
        "radioHam", "cntStoreElectronicsBookShelfSpeaker",
    };
    internal static readonly string[] Items =
    {
        "resourceElectricParts", "resourceMechanicalParts",
        "resourceScrapPolymers", "resourceDuctTape", "resourceWood",
    };

    internal static void BuildItemTable(int idBase)
    {
        IdBase = idBase;
        ItemClass.ById.Clear();
        ItemClass.IdByName.Clear();
        Block._list.Clear();
        int id = idBase;
        foreach (string b in Blocks)
        {
            if (b == "radioHam" && !HamRadioExists) continue;
            var ic = new ItemClass(id, b, true);
            ItemClass.ById[id] = ic; ItemClass.IdByName[b] = id;
            Block._list[id] = new Block();
            id++;
        }
        foreach (string it in Items)
        {
            ItemClass.ById[id] = new ItemClass(id, it, false);
            ItemClass.IdByName[it] = id;
            id++;
        }
    }
}
