using System.Collections.Generic;
using UnityEngine;

// Console commands for testing CMM's squad-shared combat music without a second
// account. A dummy spawned by the vanilla `se` command has no game client driving
// it, so its position is never set - it sits at the world origin, falls through
// the void, and NRE-spams the fall-rescue handler. spawndummy spawns the same
// playerMale entity AT THE CALLER'S FEET, where the chunk is loaded, so it stands
// still and works as an aggro target / nearby-player for the music detection.
//
// Both commands are discovered automatically (SdtdConsole.RegisterCommands scans
// every loaded assembly for IConsoleCommand) and default to permission level 0,
// i.e. admins only.

namespace CMMTestDummy
{
    // The server ticks gravity for a clientless player entity, but on a headless
    // dedi the player model/collider never fully initializes, so the dummy sinks
    // through the ground no matter where it spawns. Rather than fight the physics,
    // every spawned dummy is PINNED: each GameUpdate its motion is zeroed and its
    // position snapped back to the spawn spot if it drifted. The pin wins against
    // gravity and the fall-through rescue alike.
    public class TestDummyMod : IModApi
    {
        public void InitMod(Mod _modInstance)
        {
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
        {
            DummyPin.Tick();
        }
    }

    public static class DummyPin
    {
        public static readonly Dictionary<int, Vector3> Pinned = new Dictionary<int, Vector3>();
        private static readonly List<int> _gone = new List<int>();

        public static void Tick()
        {
            if (Pinned.Count == 0) return;
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { Pinned.Clear(); return; }

            _gone.Clear();
            foreach (KeyValuePair<int, Vector3> kv in Pinned)
            {
                EntityPlayer p = world.GetEntity(kv.Key) as EntityPlayer;
                if (p == null || p.IsDead()) { _gone.Add(kv.Key); continue; }
                p.motion = Vector3.zero;
                p.fallDistance = 0f;
                if ((p.position - kv.Value).sqrMagnitude > 0.04f)
                    p.SetPosition(kv.Value);
            }
            for (int i = 0; i < _gone.Count; i++) Pinned.Remove(_gone[i]);
        }
    }

    public class ConsoleCmdCmmSpawnDummy : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new string[] { "spawndummy" };

        public override string getDescription() =>
            "CMM test: spawn a clientless dummy player next to you";

        public override string getHelp() =>
            "spawndummy [distanceMeters]\n" +
            "Spawns a playerMale dummy `distanceMeters` (default 3, max 60) away in the\n" +
            "direction you are LOOKING, snapped to the ground at that spot.\n" +
            "Must be run from a connected player's console, not the server terminal.\n" +
            "The dummy is pinned in place every tick. Clean up with removedummies.";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            SdtdConsole console = SingletonMonoBehaviour<SdtdConsole>.Instance;
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { console.Output("No world loaded."); return; }

            EntityPlayer caller = null;
            if (_senderInfo.RemoteClientInfo != null)
                world.Players.dict.TryGetValue(_senderInfo.RemoteClientInfo.entityId, out caller);
            if (caller == null)
            {
                console.Output("spawndummy must be run by a connected player (it spawns at your position).");
                return;
            }

            float dist = 3f;
            if (_params.Count >= 1 && !float.TryParse(_params[0], out dist)) dist = 3f;
            if (dist < 1f) dist = 1f;
            if (dist > 60f) dist = 60f;   // keep it inside the loaded-chunk radius around you

            // Walk out `dist` meters along the caller's (replicated) look direction,
            // flattened to horizontal, then snap to the ground at that column so the
            // dummy stands on terrain. GetHeight reads the loaded chunk; if the column
            // is somehow unloaded it returns 0 and we fall back to the caller's own y.
            Vector3 look = caller.GetLookVector();
            look.y = 0f;
            if (look.sqrMagnitude < 0.001f) look = Vector3.forward;
            look.Normalize();

            Vector3 pos = caller.position + look * dist;
            int ground = world.GetHeight((int)pos.x, (int)pos.z);
            pos.y = ground > 0 ? ground + 1.1f : caller.position.y + 0.2f;

            EntityPlayer dummy = EntityFactory.CreateEntity(EntityClass.FromString("playerMale"), pos) as EntityPlayer;
            if (dummy == null) { console.Output("Failed to create playerMale entity."); return; }
            world.SpawnEntityInWorld(dummy);
            DummyPin.Pinned[dummy.entityId] = pos;   // hold it in place every GameUpdate

            console.Output("Spawned dummy player id " + dummy.entityId + " at " + pos +
                           " (health " + dummy.Health + ", pinned). Remove with: removedummies");
        }
    }

    public class ConsoleCmdCmmRemoveDummies : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new string[] { "removedummies" };

        public override string getDescription() =>
            "CMM test: remove every clientless dummy player";

        public override string getHelp() =>
            "removedummies\n" +
            "Removes every player entity that has no connected client - i.e. spawned\n" +
            "dummies, including any stuck under the world. Real players are untouched\n" +
            "(they always have a ClientInfo).";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            SdtdConsole console = SingletonMonoBehaviour<SdtdConsole>.Instance;
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { console.Output("No world loaded."); return; }

            ClientInfoCollection clients = SingletonMonoBehaviour<ConnectionManager>.Instance.Clients;
            List<int> ids = new List<int>();
            for (int i = 0; i < world.Players.list.Count; i++)
            {
                EntityPlayer p = world.Players.list[i];
                if (p != null && !(p is EntityPlayerLocal) && clients.ForEntityId(p.entityId) == null)
                    ids.Add(p.entityId);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                DummyPin.Pinned.Remove(ids[i]);
                world.RemoveEntity(ids[i], EnumRemoveEntityReason.Despawned);
            }

            console.Output("Removed " + ids.Count + " dummy player(s).");
        }
    }
}

