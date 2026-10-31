using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Together;

// host copies of a body. live players already keep their body across a descent.
// this file is the copy that survives a process restart, and a disconnect whose suitcase was lost.
public static class PlayerCheckpoint {
    const float Interval = 30f;

    static float _wait;
    static int _cursor;
    static bool _writing;
    // mp species lock. separate from the bep config gg uses for a solo run.
    static readonly Dictionary<string, string> _species = new Dictionary<string, string>();
    static readonly Dictionary<string, int> _speciesSlot = new Dictionary<string, int>();

    public static void Tick() {
        if (_writing || !Net.IsServer || !Util.IsWorldGenerated())
            return;
        var world = WorldGeneration.world;
        if (world == null || world.generatingWorld)
            return;

        _wait += Time.unscaledDeltaTime;
        if (_wait < Interval)
            return;
        _wait = 0f;

        var players = new List<ScavPlayer>(ScavPlayer.BodyToPlayerDict.Values);
        if (players.Count == 0)
            return;
        if (_cursor >= players.Count)
            _cursor = 0;

        for (int n = 0; n < players.Count; n++) {
            int i = (_cursor + n) % players.Count;
            var plr = players[i];
            if (plr == null || plr.body == null)
                continue;
            _cursor = i + 1;
            WriteOne(plr, false);
            if (!File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, SavesystemPatch.mpsaveheaderfile)))
                WriteRules(true);
            return;
        }
    }

    public static bool HasPlayerFile(ScavPlayer plr) {
        if (plr == null)
            return false;
        return File.Exists(Path.Combine(Folder(plr), "save.sv"));
    }

    // called before RegenerateWorld, while biomeDepth is still the layer they just finished.
    public static void WriteLayerTransition() {
        if (!Net.IsServer)
            return;
        foreach (var plr in ScavPlayer.BodyToPlayerDict.Values) {
            if (plr == null || plr.body == null)
                continue;
            WriteOne(plr, true);
        }
        WriteRules(false);
    }

    public static void WriteOne(ScavPlayer plr, bool nextLayer) {
        if (SavesystemPatch.PreserveSave)
            return;
        if (_writing || plr == null || plr.body == null || PlayerCamera.main == null)
            return;
        if (!Net.IsServer)
            return;

        _writing = true;
        var previousPath = SavesystemPatch.savedatapathreplacement;
        try {
            var folder = Folder(plr);
            Directory.CreateDirectory(folder);
            // read before WriteForced replaces the file
            TryReadSpecies(plr.GetPersistentId(), out string keepSpecies, out int keepSlot);
            SavesystemPatch.savedatapathreplacement = folder;
            SaveSystem_SaveGame_MultiplayerPatch.WriteForced(plr.body);
            if (!nextLayer && WorldGeneration.world != null)
                StoreCurrentBiome(folder, WorldGeneration.world.biomeDepth);
            if (!string.IsNullOrEmpty(keepSpecies))
                StampSpecies(folder, keepSpecies, keepSlot);
            if (plr.IsLocal)
                CopyHostFlag(folder);
        } catch (Exception e) {
            log.error($"PlayerCheckpoint.WriteOne {plr}\n{e}");
        } finally {
            SavesystemPatch.savedatapathreplacement = previousPath;
            _writing = false;
        }
    }

    public static bool TryLoadBodyOnly(ScavPlayer plr) {
        if (SavesystemPatch.PreserveSave)
            return false;
        if (!Net.IsServer || plr == null || plr.body == null)
            return false;
        if (plr.ServerPlayerState != null && plr.ServerPlayerState.loaded_save)
            return false;
        var folder = Folder(plr);
        if (!File.Exists(Path.Combine(folder, "save.sv")))
            return false;
        if (PlayerCamera.main == null)
            return false;

        var snap = WorldRunSnapshot.Capture();
        var pos = plr.body.transform.position;
        var previousPath = SavesystemPatch.savedatapathreplacement;
        var camera = PlayerCamera.main;
        var previousBody = camera.body;
        BeginGearCapture();
        try {
            SavesystemPatch.savedatapathreplacement = folder;
            SaveSystem.loadedRun = true;
            SaveSystem_HasSave_MultiplayerPatch.force = true;
            camera.body = plr.body;
            SaveSystem_LoadGame_MultiplayerPatch.ForceLoad();

            // stock leaves a second item for the same slot loose at this spot. the spawn move does not bring those along.
            EndGearCapture(plr);
            RegisterBody(plr.body);

            plr.body.transform.position = pos;
            if (plr.ServerPlayerState != null) {
                plr.ServerPlayerState.loaded_save = true;
                plr.ServerPlayerState.did_give_spawn_location_from_a_save = false;
            }
            return true;
        } catch (Exception e) {
            log.error($"PlayerCheckpoint.TryLoadBodyOnly {plr}\n{e}");
            return false;
        } finally {
            EndGearCapture(plr);
            snap.Restore();
            ApplyLocalCraftMarkers();
            SaveSystem_HasSave_MultiplayerPatch.force = false;
            SavesystemPatch.savedatapathreplacement = previousPath;
            camera.body = previousBody;
        }
    }

    public static void RegisterBody(Body body) {
        if (!Net.IsServer || body == null)
            return;
        foreach (var item in body.GetAllItemsThorough()) {
            if (item != null && NetObjectRegistry.ObjectShouldBeSynced(item.gameObject))
                NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(item.gameObject);
        }
    }

    public static void RememberSpecies(string persistentId, string species, int slot) {
        if (string.IsNullOrEmpty(persistentId) || string.IsNullOrEmpty(species))
            return;
        _species[persistentId] = species;
        _speciesSlot[persistentId] = slot;
        StampSpecies(Path.Combine(SavesystemPatch.mpsavefolder, persistentId), species, slot);
    }

    public static bool TryReadSpecies(string persistentId, out string species, out int slot) {
        species = null;
        slot = 0;
        if (string.IsNullOrEmpty(persistentId))
            return false;
        if (_species.TryGetValue(persistentId, out species) && !string.IsNullOrEmpty(species)) {
            _speciesSlot.TryGetValue(persistentId, out slot);
            return true;
        }
        if (!ReadSpeciesFile(Path.Combine(SavesystemPatch.mpsavefolder, persistentId), out species, out slot))
            return false;
        _species[persistentId] = species;
        _speciesSlot[persistentId] = slot;
        return true;
    }

    // the first-craft payout is per player. the stock recipe flags in the file are one global table.
    internal static void StampCrafted(string folder, List<int> crafted) {
        if (crafted == null)
            return;
        var path = Path.Combine(folder, "save.sv");
        if (!File.Exists(path))
            return;
        try {
            var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            var arr = new JArray();
            for (int i = 0; i < crafted.Count; i++)
                arr.Add(crafted[i]);
            root["craftedBefore"] = arr;
            File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Newtonsoft.Json.Formatting.None)));
        } catch (Exception e) {
            log.error($"PlayerCheckpoint.StampCrafted\n{e}");
        }
    }

    internal static void ReadCrafted(string folder) {
        if (!Net.IsServer || PlayerCamera.main == null || PlayerCamera.main.body == null)
            return;
        if (!PlayerCamera.main.body.TryGetNetBody(out NetBody nb) || nb == null || !nb.IsPlayer)
            return;
        var path = Path.Combine(folder, "save.sv");
        if (!File.Exists(path))
            return;
        JArray arr;
        try {
            var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            arr = root["craftedBefore"] as JArray;
        } catch (Exception e) {
            log.error($"PlayerCheckpoint.ReadCrafted\n{e}");
            return;
        }
        if (arr == null)
            return;
        nb.ResetMind();
        for (int i = 0; i < arr.Count; i++) {
            if (arr[i] == null || arr[i].Type != JTokenType.Integer)
                continue;
            int index = arr[i].Value<int>();
            if (!nb.RecipesHasCrafterBefore.Contains(index))
                nb.RecipesHasCrafterBefore.Add(index);
        }
        PlayerCheckpoint.SendCrafted(nb);
    }

    // the craft screen reads the global flag. the host's screen should match the host's own list.
    internal static void ApplyLocalCraftMarkers() {
        if (!Net.IsServer || Recipes.recipes == null)
            return;
        if (!Util.TryGetLocalBody(out Body body) || body == null)
            return;
        if (!body.TryGetNetBody(out NetBody nb) || nb == null || nb.RecipesHasCrafterBefore == null)
            return;
        for (int i = 0; i < Recipes.recipes.Count; i++) {
            Recipe recipe = Recipes.recipes[i];
            if (recipe != null)
                recipe.hasMadeBefore = nb.RecipesHasCrafterBefore.Contains(recipe.index);
        }
    }

    // the craft screen is local. a rejoin has a fresh table, so the owner has to be told which ones they already made.
    internal static void SendCrafted(NetBody nb) {
        if (!Net.IsServer || nb == null || !nb.IsPlayer || nb.plr == null || nb.plr.IsLocal)
            return;
        var list = nb.RecipesHasCrafterBefore;
        int n = list == null ? 0 : list.Count;
        var writer = Net.CreateWriter(NetmsgId.CLIENT_CraftedBefore);
        writer.Put((ushort)n);
        for (int i = 0; i < n; i++)
            writer.Put(list[i]);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, nb.plr.playerId);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CraftedBefore)]
    static void Client_CraftedBefore(knetid _, NetDataReader reader) {
        reader.Get(out ushort count);
        var got = new HashSet<int>();
        for (int i = 0; i < count; i++) {
            reader.Get(out int index);
            got.Add(index);
        }
        if (Recipes.recipes == null)
            return;
        for (int i = 0; i < Recipes.recipes.Count; i++) {
            Recipe recipe = Recipes.recipes[i];
            if (recipe != null)
                recipe.hasMadeBefore = got.Contains(recipe.index);
        }
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null && cam.craftingPanel != null && cam.craftingPanel.activeSelf)
            cam.RefreshRecipeList();
    }

    static void StampSpecies(string folder, string species, int slot) {
        var path = Path.Combine(folder, "save.sv");
        if (!File.Exists(path))
            return;
        try {
            var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            root["ggSpecies"] = species;
            root["ggSpeciesSlot"] = slot;
            File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Newtonsoft.Json.Formatting.None)));
        } catch (Exception e) {
            log.error($"PlayerCheckpoint.StampSpecies\n{e}");
        }
    }

    static bool ReadSpeciesFile(string folder, out string species, out int slot) {
        species = null;
        slot = 0;
        var path = Path.Combine(folder, "save.sv");
        if (!File.Exists(path))
            return false;
        try {
            var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            species = root.Value<string>("ggSpecies");
            slot = root.Value<int?>("ggSpeciesSlot") ?? 0;
            return !string.IsNullOrEmpty(species);
        } catch {
            return false;
        }
    }

    static string Folder(ScavPlayer plr) {
        return Path.Combine(SavesystemPatch.mpsavefolder, plr.GetPersistentId());
    }

    static void CopyHostFlag(string folder) {
        var from = Path.Combine(folder, "save.sv");
        var to = Path.Combine(SavesystemPatch.mpsavefolder, "save.sv");
        if (File.Exists(from))
            File.Copy(from, to, true);
    }

    // SaveGame stores biomeDepth + 1. a mid-layer tick wants the layer they are standing on.
    static void StoreCurrentBiome(string folder, int biomeDepth) {
        var path = Path.Combine(folder, "save.sv");
        var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
        root["biome"] = biomeDepth;
        File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Newtonsoft.Json.Formatting.None)));
    }

    public static void WriteRules(bool midlevel) {
        try {
            if (WoundView.view == null || WoundView.view.cInfo == null || WoundView.view.cInfo.Length < 3)
                return;
            Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
            var dict = new Dictionary<string, object>();
            dict["hello"] = "Multiplayer Mod created by Krokosha666";
            dict["MPVERSION"] = Plugin.MOD_VERSION;
            dict["GAMEVERSION"] = Application.version;
            dict["SAVEID"] = WoundView.view.cInfo[2];
            dict["LEVEL"] = midlevel;
            dict["RULES"] = Multiplayer.rules;
            dict["MODIFIER"] = midlevel ? (WorldMP.GetLayerModifier()?.modifierIndex ?? -1) : -1;
            dict["PLRPOS"] = new Dictionary<string, (float, float)>();
            var text = Newtonsoft.Json.JsonConvert.SerializeObject(dict, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(Path.Combine(SavesystemPatch.mpsavefolder, SavesystemPatch.mpsaveheaderfile), text);
        } catch (Exception e) {
            log.error($"PlayerCheckpoint.WriteRules\n{e}");
        }
    }

    // items born while a save is being applied. stock parks the extras at the body, then the spawn move leaves them there.
    static bool _capture;
    static readonly List<Item> _born = new List<Item>();
    static readonly Dictionary<ushort, List<Item>> _gear = new Dictionary<ushort, List<Item>>();

    internal static void BeginGearCapture() {
        _capture = true;
        _born.Clear();
    }

    internal static void NoteGear(Item item) {
        if (!_capture || item == null)
            return;
        if (!_born.Contains(item))
            _born.Add(item);
    }

    // safe to call twice. the second one finds the list already stored.
    internal static void EndGearCapture(ScavPlayer plr) {
        if (!_capture && _born.Count == 0)
            return;
        _capture = false;
        if (plr == null) {
            _born.Clear();
            return;
        }
        ushort id = plr.playerId;
        if (id == 0) {
            _born.Clear();
            return;
        }
        if (!_gear.TryGetValue(id, out List<Item> list)) {
            list = new List<Item>();
            _gear[id] = list;
        }
        for (int i = 0; i < _born.Count; i++) {
            Item item = _born[i];
            if (item != null && !list.Contains(item))
                list.Add(item);
        }
        _born.Clear();
        Reclaim(plr.body, list);
    }

    internal static void ReclaimGear(ushort playerId) {
        if (playerId == 0 || !_gear.TryGetValue(playerId, out List<Item> list))
            return;
        if (!ScavPlayer.TryGetPlayerFromClientId(playerId, out ScavPlayer plr) || plr == null)
            return;
        Reclaim(plr.body, list);
    }

    internal static void ForgetGear(ushort playerId) {
        _gear.Remove(playerId);
    }

    // in a slot, on a limb, or inside a bag counts. sitting on the body root does not, the spawn pass tries again.
    static bool Seated(Item item, Body body) {
        Transform parent = item.transform.parent;
        if (parent == null)
            return false;
        if (parent.GetComponentInParent<Body>() != body)
            return false;
        if (parent.GetComponent<InventorySlot>() != null)
            return true;
        if (parent.GetComponent<Limb>() != null)
            return true;
        return parent.GetComponent<Container>() != null;
    }

    static void Reclaim(Body body, List<Item> list) {
        if (body == null || list == null)
            return;
        int moved = 0;
        for (int i = 0; i < list.Count; i++) {
            Item item = list[i];
            if (item == null || Seated(item, body))
                continue;
            try {
                Transform before = item.transform.parent;
                if (Seat(body, item) && item.transform.parent != before)
                    moved++;
            } catch (Exception e) {
                log.error($"PlayerCheckpoint.Reclaim {item}\n{e}");
            }
        }
        if (moved > 0 && log.verbose)
            log.l($"load gear back on {body} x{moved}");
    }

    // empty slot, then a bag that is already on him, then wear. last resort rides the body so the teleport cannot drop it.
    static bool Seat(Body body, Item item) {
        if (item.Stats != null && item.Stats.wearable && TryWear(body, item))
            return true;
        if (TrySlot(body, item))
            return true;
        if (TryBag(body, item))
            return true;
        if (item.rb != null)
            item.rb.simulated = false;
        item.transform.SetParent(body.transform);
        item.transform.localPosition = Vector3.zero;
        return true;
    }

    static bool TryWear(Body body, Item item) {
        Item worn = body.GetWearableBySlotID(item.Stats.wearSlotId);
        if (worn == item)
            return true;
        if (worn != null)
            return false;
        body.WearWearable(item);
        return item.transform.parent != null && item.transform.parent.GetComponent<Limb>() != null;
    }

    // a hand that is about to drop its contents is not a seat. the next frame would throw it back onto the old spot.
    static bool TrySlot(Body body, Item item) {
        if (body.slots == null)
            return false;
        bool handsOnly = item.Stats != null && item.Stats.onlyHoldInHands;
        for (int i = 0; i < body.slots.Length; i++) {
            InventorySlot slot = body.slots[i];
            if (slot == null || body.HoldingItem(i))
                continue;
            if (handsOnly && !slot.isHand)
                continue;
            if (slot.dropWhenUnconscious && slot.limb != null && !slot.canPickUp)
                continue;
            body.PickUpItem(item, i, true);
            if (item.transform.parent == slot.transform)
                return true;
            HeldSeat.Seat(body, item, i);
            if (item.transform.parent == slot.transform)
                return true;
        }
        return false;
    }

    static bool TryBag(Body body, Item item) {
        if (item.Stats != null && item.Stats.wearable && item.TryGetComponent(out Container own) && own.GetHoldingWeight() > 0f)
            return false;
        if (TryBagList(item, body.GetSurfaceInventoryItems()))
            return true;
        return TryBagList(item, body.GetAllWearables());
    }

    static bool TryBagList(Item item, IEnumerable<Item> hosts) {
        if (hosts == null)
            return false;
        foreach (Item host in hosts) {
            if (host == null || host == item || host.container == null)
                continue;
            if (!host.container.CanHoldItem(item))
                continue;
            item.transform.position = host.transform.position;
            ItemSync.Container_ForceLoadItem(item, host.container);
            if (item.transform.parent == host.container.transform)
                return true;
        }
        return false;
    }

    struct WorldRunSnapshot {
        bool hadWorld;
        int biomeDepth;
        int totalTraveled;
        float lootRarity;
        float trapRarity;
        Dictionary<string, object> runSettings;
        int caloriesConsumed;
        float savedRunTime;
        int[] woundInfo;
        (bool made, int intel)[] recipes;
        int biomeOverride;
        bool unchipped;

        internal static WorldRunSnapshot Capture() {
            var s = new WorldRunSnapshot();
            var world = WorldGeneration.world;
            if (world != null) {
                s.hadWorld = true;
                s.biomeDepth = world.biomeDepth;
                s.totalTraveled = world.totalTraveled;
                s.lootRarity = world.lootRarityMultiplier;
                s.trapRarity = world.trapRarityMultiplier;
                s.biomeOverride = (int)world.biomeOverride;
                s.unchipped = world.unchippedMode;
                s.runSettings = WorldGeneration.runSettings;
            }
            if (PlayerCamera.main != null)
                s.caloriesConsumed = PlayerCamera.main.caloriesConsumed;
            s.savedRunTime = SaveSystem.savedRunTime;
            if (WoundView.view != null && WoundView.view.cInfo != null && WoundView.view.cInfo.Length >= 4) {
                s.woundInfo = new int[4];
                Array.Copy(WoundView.view.cInfo, s.woundInfo, 4);
            }
            if (Recipes.recipes != null) {
                s.recipes = new (bool, int)[Recipes.recipes.Count];
                for (int i = 0; i < Recipes.recipes.Count; i++) {
                    var recipe = Recipes.recipes[i];
                    if (recipe != null)
                        s.recipes[i] = (recipe.hasMadeBefore, recipe.INT);
                }
            }
            return s;
        }

        internal void Restore() {
            var world = WorldGeneration.world;
            if (hadWorld && world != null) {
                world.biomeDepth = biomeDepth;
                world.totalTraveled = totalTraveled;
                world.lootRarityMultiplier = lootRarity;
                world.trapRarityMultiplier = trapRarity;
                world.biomeOverride = (WorldGeneration.OverrideSceneType)biomeOverride;
                world.unchippedMode = unchipped;
                if (runSettings != null)
                    WorldGeneration.runSettings = runSettings;
            }
            if (PlayerCamera.main != null)
                PlayerCamera.main.caloriesConsumed = caloriesConsumed;
            SaveSystem.savedRunTime = savedRunTime;
            if (woundInfo != null && WoundView.view != null && WoundView.view.cInfo != null && WoundView.view.cInfo.Length >= 4) {
                for (int i = 0; i < 4; i++)
                    WoundView.view.cInfo[i] = woundInfo[i];
            }
            if (recipes != null && Recipes.recipes != null) {
                int n = Math.Min(recipes.Length, Recipes.recipes.Count);
                for (int i = 0; i < n; i++) {
                    var recipe = Recipes.recipes[i];
                    if (recipe == null)
                        continue;
                    recipe.hasMadeBefore = recipes[i].made;
                    recipe.INT = recipes[i].intel;
                }
            }
        }
    }
}

// stock TryLoadGame instantiates each saved item before it parents it. this is the only window that sees the ones the slot refused.
[HarmonyPatch(typeof(Item), "Awake")]
static class Item_Awake_LoadGear {
    static void Postfix(Item __instance) {
        PlayerCheckpoint.NoteGear(__instance);
    }
}
