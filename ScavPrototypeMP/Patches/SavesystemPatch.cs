using HarmonyLib;
using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;



[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.HasSave))]
public static class SaveSystem_HasSave_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions);


    internal static bool force = false;
    private static bool Prefix(ref bool __result) {
        if (force) {
            __result = true;
            force = false;
            return false;
        }
        // vanilla path is the save.sv the menu reads, so only that file counts.
        string path = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();
        SavesystemPatch.savedatapathreplacement = path;
        if (path == SavesystemPatch.vanillasavefolder) {
            __result = SavesystemPatch.HasVanillaSaveFile();
        } else {
            __result = SavesystemPatch.HasAnySaveFile();
        }
        return false;
    }
}










[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
public static class SaveSystem_SaveGame_MultiplayerPatch {
    public static WorldGeneration world => WorldGeneration.world;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions);

    static bool force = false;
    static bool wrote;

    // checkpoint writes one body into a folder that is already selected. the wipe prefix must not run.
    internal static void WriteForced(Body body) {
        var camera = PlayerCamera.main;
        Body previous = camera != null ? camera.body : null;
        force = true;
        try {
            if (camera != null)
                camera.body = body;
            SaveSystem.SaveGame();
        } finally {
            force = false;
            if (camera != null)
                camera.body = previous;
        }
    }

    static bool Prefix() {
        wrote = false;
        // a practice world must not overwrite the real mp_save.
        // debug map with the host setting off still keeps saveandquit as a map-only write.
        if (SavesystemPatch.PreserveSave && !force) {
            if (SavesystemPatch.InDebugMap)
                SavesystemPatch.WriteDebugWorld();
            return false;
        }
        if (!force) {
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.SessionSaveFolder;
            var OG_LOCAL_BODY = Util.GetLocalBody();

            try {

                if (Multiplayer.IsNetworkActiveAndIsServer()) {

                    if (!Directory.Exists(SavesystemPatch.SessionSaveFolder)) {
                        Directory.CreateDirectory(SavesystemPatch.SessionSaveFolder);
                    }

                    var midlevel = !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();

                    try {

                        // because force is false it has to be user clicking on the save button
                        // make folders for each player name
                        // and rerun SaveGame function for every player (set the PlayerCamera.main.body or override the body getter)
                        // and last do the local character but for default  SavesystemPatch.GetPersistentDataPath()  so that the game knows that there is a save 


                        foreach (var bodyplr in ScavPlayer.BodyToPlayerDict) {
                            
                            SavesystemPatch.savedatapathreplacement = Path.Combine(SavesystemPatch.SessionSaveFolder, bodyplr.Value.GetPersistentId());

                            try {
                                try {
                                    if (!midlevel) { // saved on finish, must respawn characters
                                        if (!bodyplr.Key.alive) {

                                            if (Multiplayer.rules.CanReviveOnNextLevel()) {
                                                //bodyplr.Value.Server_RespawnCharacter(PlayerCamera.main.body.transform.position, !Multiplayer.rules.RespawnKeepInventory);
                                                bodyplr.Value.Server_RespawnCharacter(OG_LOCAL_BODY.transform.position, !midlevel);
                                            }

                                            
                                        }

                                    }
                                    bodyplr.Value.ResetEntropy();
                                } catch (Exception e) {
                                    log.error($"SaveGame_MultiplayerPatch: PREPARING TO SAVE {bodyplr.Value.ToString()}:\n{e.ToString()}");
                                }

                                if (!Directory.Exists(SavesystemPatch.savedatapathreplacement)) {
                                    Directory.CreateDirectory(SavesystemPatch.savedatapathreplacement);
                                }

                                force = true;
                                PlayerCamera.main.body = bodyplr.Key;
                                SaveSystem.SaveGame();
                            } catch (Exception e) {
                                log.error($"SaveGame_MultiplayerPatch: SAVING {bodyplr.Value.ToString()}:\n{e.ToString()}");
                            }
                        }

                        PlayerCamera.main.body = OG_LOCAL_BODY;
                        SavesystemPatch.savedatapathreplacement = SavesystemPatch.SessionSaveFolder;
                        force = false;
                        if (SavesystemPatch.InDebugMap)
                            SavesystemPatch.WriteDebugWorld();


                    }   catch (Exception e) {
                        log.error($"SaveGame_MultiplayerPatch: SAVING PLAYERS\n{e.ToString()}");
                    }

                    

                    // save rules
                    {
                        var dict = new Dictionary<string, object>();

                        dict["hello"] = "Multiplayer Mod created by Krokosha666";
                        dict["MPVERSION"] = Plugin.MOD_VERSION;
                        dict["GAMEVERSION"] = Application.version;
                        dict["SAVEID"] = WoundView.view.cInfo[2];
                        dict["LEVEL"] = midlevel;
                        dict["RULES"] = Multiplayer.rules;
                        dict["MODIFIER"] = midlevel ? (WorldMP.GetLayerModifier()?.modifierIndex ?? -1) : -1;
                        dict["PLRPOS"] = ScavPlayer.BodyToPlayerDict.ToDictionary(x => x.Value.GetPersistentId(), x => (x.Key.transform.position.x, x.Key.transform.position.y));
#if FULLGAME
                        dict[AchievementBreakSync.SaveHeaderKey] = SteamManager.disableAchievements;
                        dict["PLRCHARS"] = ScavPlayer.BodyToPlayerDict.ToDictionary(
                            x => x.Value.GetPersistentId(),
                            x => x.Key != null ? x.Key.charType : (int)x.Value.selectedCharacter);
#endif

                        string serialized = JsonConvert.SerializeObject(dict, Formatting.Indented);
#if !RELEASE 
                        log.l($"SHIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIII:\n{serialized}");
#endif
                        var rulespath = Path.Combine(SavesystemPatch.SessionSaveFolder, SavesystemPatch.mpsaveheaderfile);

                        File.WriteAllText(rulespath, serialized);
                    }


#if !RELEASE
                    // saving world data
                    if (midlevel) {
                        try {
                            // tiles
                            {
                                var worldtilesfile = Path.Combine(SavesystemPatch.mpsavefolder, "world_tiles.bin");

                                File.WriteAllBytes(worldtilesfile, SavesystemPatch.SerializeWorldBlocks());
                            }
                            // fluids
                            {
                                var worldtilesfile = Path.Combine(SavesystemPatch.mpsavefolder, "world_fluid.bin");

                                File.WriteAllBytes(worldtilesfile, SavesystemPatch.SerializeWorldFluids());
                            }

                            

                            // TODO all objects lol?
                            log.warn("TODO SAVESYSTEM OBJECTS");

                        } catch (Exception e) {
                            log.error($"SaveGame_MultiplayerPatch: SAVING WORLD\n{e.ToString()}");
                        }
                    }
#endif

                }

            }  catch (Exception e) {
                log.error($"SaveGame_MultiplayerPatch\n{e.ToString()}");
            }
            SaveSystem_HasSave_MultiplayerPatch.force = false;
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetPersistentDataPathToSave();
            force = false;
            PlayerCamera.main.body = OG_LOCAL_BODY;

        }
        log.l($"SAVING TO FILE: {SavesystemPatch.savedatapathreplacement}");
        wrote = true;
        return true;
    }

    // stock save writes the shared recipe table. the payout list is per player, so it rides along after.
    static void Postfix() {
        // the vanilla write just replaced save.sv. merge the level spawn back in.
        if (!force && wrote)
            WorldSpawn.Stamp();
        if (!wrote || PlayerCamera.main == null || PlayerCamera.main.body == null)
            return;
        if (!PlayerCamera.main.body.TryGetNetBody(out NetBody nb) || nb == null || !nb.IsPlayer)
            return;
        PlayerCheckpoint.StampCrafted(SavesystemPatch.savedatapathreplacement, nb.RecipesHasCrafterBefore);
    }
}

[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.TryLoadGame))]
public static class SaveSystem_LoadGame_MultiplayerPatch {
    public static WorldGeneration world => WorldGeneration.world;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => SavesystemPatch.StripSaveDelete(SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions));


    static bool force = false;
    internal static bool deleteVanillaAfter;

    // late join applies one folder. the multiplayer postfix must not run its lobby walk.
    internal static void ForceLoad() {
        force = true;
        try {
            SaveSystem.TryLoadGame();
        } finally {
            force = false;
        }
    }

    static bool Prefix(ref bool __state) {
        deleteVanillaAfter = false;
        // spawn was saved before any character. keep the point and do not parse this file as a body.
        if (!force && SavesystemPatch.InDebugMap && WorldSpawn.SpawnOnlyFile()) {
            WorldSpawn.Read();
            if (SavesystemPatch.PendingLoadMpSave)
                SavesystemPatch.ReadDebugWorld();
            __state = false;
            SaveSystem.loadedRun = false;
            return false;
        }
        // tutorial, and debug map with the host setting off, do not load player bodies.
        // a debug load still restores the map files into the generation buffers.
        if (SavesystemPatch.PreserveSave && !force) {
            if (SavesystemPatch.InDebugMap && SavesystemPatch.PendingLoadMpSave)
                SavesystemPatch.ReadDebugWorld();
            __state = false;
            SaveSystem.loadedRun = false;
            return false;
        }
        // continue may lose loadedRun across WaitLoad; sticky flag keeps the lobby walk alive.
        if (!force && SavesystemPatch.PendingLoadMpSave && Net.IsRunning && Net.IsServer) {
            SaveSystem.loadedRun = true;
        }
        __state = SaveSystem.loadedRun;
        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            return false;
        }

        if (!force) {
            var OG_LOCAL_BODY = Util.GetLocalBody();
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();
            PlayerCamera.main.body = OG_LOCAL_BODY;

        }

        if (!SaveSystem.loadedRun) { // og wouldnt trigger
            return false;
        }

        // A lobby save transfer can choose a host who has never played this run.
        // Its root file supplies world metadata, not another player's body. The
        // postfix still restores each existing participant from their own folder.
        if (!force && TransferTransport.SkipTransferredRootBody()) {
            log.l("Host transfer: new host has no saved character; keeping their fresh character.");
            return false;
        }

        

        if (!force) {
            if (Net.IsRunning) {
                SaveSystem.loadedRun = false;
                return true;
            }
        }

        deleteVanillaAfter = !Net.IsRunning;
        log.l($"LOADING SAVE FROM DIRECTORY: {SavesystemPatch.savedatapathreplacement}");

        return true;
    }
    static void Postfix(ref bool __state) {
        // the per-player load. the lobby walk calls back in with force set, and this is the pass that applied the body.
        if (force)
            PlayerCheckpoint.ReadCrafted(SavesystemPatch.savedatapathreplacement);
        if (deleteVanillaAfter) {
            deleteVanillaAfter = false;
            try {
                var vanilla = SavesystemPatch.vanillasavefolder + "\\save.sv";
                if (File.Exists(vanilla))
                    File.Delete(vanilla);
            } catch (Exception e) {
                log.error($"LoadGame_MultiplayerPatch: solo delete\n{e.ToString()}");
            }
        }
        if (!force ) {
            SaveSystem.loadedRun = __state;

            SavesystemPatch.savedatapathreplacement = SavesystemPatch.SessionSaveFolder;
            var OG_LOCAL_BODY = Util.GetLocalBody();
            try {

                var mpsavedir = SavesystemPatch.SessionSaveFolder;
                if (Multiplayer.IsNetworkActiveAndIsServer() && SaveSystem.loadedRun && Directory.Exists(mpsavedir)) {
                    if (SavesystemPatch.InDebugMap)
                        SavesystemPatch.ReadDebugWorld();
                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("savesystem_loading_mp_save"));
                    if (SavesystemPatch.TryPeekMpWorldState(out var peek) && WorldGeneration.world != null) {
                        SavesystemPatch.ApplyPeekedWorldState(WorldGeneration.world, peek);
                        log.l($"LoadGame: applied mp_save world peek depth={peek.biomeDepth} midlevel={peek.midlevel} traveled={peek.totalTraveled}");
                    }

                    try {
                        var rulespath = Path.Combine(mpsavedir, SavesystemPatch.mpsaveheaderfile);
                        if (File.Exists(rulespath)) {

                           


                            bool MIDLEVEL = false;
                            Dictionary<string, (float, float)> PLRPOS = new Dictionary<string, (float, float)>();

                            // read rules
                            {
                                var text = File.ReadAllText(rulespath);

                                var jObject = JObject.Parse(text);
                                var MPVERSION = (string)jObject.GetValue("MPVERSION");
                                var GAMEVERSION = (string)jObject.GetValue("GAMEVERSION");

                                var SAVEID = (int)jObject.GetValue("SAVEID");
                                MIDLEVEL = (bool)jObject.GetValue("LEVEL");
                                PLRPOS = jObject.GetValue("PLRPOS").ToObject<Dictionary<string, (float, float)>>();


                                if (MIDLEVEL) {
                                    var MODIFIER = (int)jObject.GetValue("MODIFIER");
                                    if (MODIFIER != -1) {
                                        world.StartCoroutine(Util._CoroutineCallLambdaWhen(Util.IsWorldGenerated, () => {
                                            var mod = LayerModifier.availableModifiers[MODIFIER];
                                            mod.Initialize(world);
                                            mod.active = true;
                                        }));
                                    }
                                }


                                if (MIDLEVEL) {
                                    log.l($"TODO: LEVEL THING FORGOR WHAT ITS FOR");
                                }

                                if (MPVERSION != Plugin.MOD_VERSION) {
                                    log.warn($"LoadGame_MultiplayerPatch: Mismatching mod version! file:{MPVERSION} current: {Plugin.MOD_VERSION}");
                                }
                                if (GAMEVERSION != Application.version) {
                                    log.warn($"LoadGame_MultiplayerPatch: Mismatching game version! file:{GAMEVERSION} current: {Application.version}");
                                }

                                if (SAVEID != WoundView.view.cInfo[2]) {
#if !RELEASE
                                    log.warn("LoadGame_MultiplayerPatch: Save ID doesn't match the save.sv!");
#endif
                                }

                                if (jObject.TryGetValue("RULES", out var RULES)) {
                                    Multiplayer.rules = RULES.ToObject<MultiplayerGameRules>();
                                    if (SavesystemPatch.DebugWorldUsesSave) {
                                        Multiplayer.rules.DebugWorldSaveSystem = true;
                                    }
                                }
#if FULLGAME
                                if (jObject.TryGetValue(AchievementBreakSync.SaveHeaderKey, out var CHEATED)
                                    && CHEATED != null && CHEATED.Type != JTokenType.Null
                                    && CHEATED.Value<bool>()) {
                                    AchievementBreakSync.ApplyLocal(showAlert: true);
                                }
#endif
                            }

#if !RELEASE
                            if (MIDLEVEL) {
                                try {
                                    {
                                        var tilespath = Path.Combine(mpsavedir, "world_tiles.bin");
                                        if (File.Exists(tilespath)) {
                                            WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa = File.ReadAllBytes(tilespath);
                                        }
                                    }
                                    {
                                        var fluidspath = Path.Combine(mpsavedir, "world_fluid.bin");
                                        if (File.Exists(fluidspath)) {
                                            WorldMP.ccccccccccccccccccccccccccc = File.ReadAllBytes(fluidspath);
                                        }
                                    }
                                    {
                                        var objectspath = Path.Combine(mpsavedir, "world_objects.bin");
                                        if (File.Exists(objectspath)) {
                                            WorldMP.bbbbbbbbbbbbbbbbbbbbbbbbbbb = File.ReadAllBytes(objectspath);
                                        }
                                    }
                                }  catch (Exception e) {
                                    log.error($"LoadGame_MultiplayerPatch: READING WORLD\n{e.ToString()}");
                                }
                            }
#endif

                            // now read all the players


                            try {
                                SharedMain.CreatePlayerCharacters();

                                foreach (var bodyplr in ScavPlayer.BodyToPlayerDict) {
                                    var presistentid = bodyplr.Value.GetPersistentId();
                                    SavesystemPatch.savedatapathreplacement = Path.Combine(mpsavedir, presistentid);


#if !RELEASE && false // nah fuck this shit 
                                    if (MIDLEVEL && PLRPOS.TryGetValue(presistentid, out var plrpos)) {
                                        bodyplr.Key.transform.position = new Vector2(plrpos.Item1, plrpos.Item2);
                                        bodyplr.Value.Server_RemindPlayersCurrentState();  
                                    }
#endif
                                    
                                    if (Directory.Exists(SavesystemPatch.savedatapathreplacement)) {
                                        try {
                                            SaveSystem_HasSave_MultiplayerPatch.force = true;
                                            SaveSystem.loadedRun = true;
                                            force = true;
                                            PlayerCamera.main.body = bodyplr.Key;
#if !RELEASE 
                                            log.l("PATH BEFORE CALLING TryLoadGame: " + SavesystemPatch.savedatapathreplacement);
#endif
                                            SaveSystem.TryLoadGame();

                                            // immidiately register allat
                                            foreach (var item in bodyplr.Key.GetAllItemsThorough()) {
                                                if (NetObjectRegistry.ObjectShouldBeSynced(item.gameObject)) {
                                                    var si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(item.gameObject);
                                                    //if (si != null)  NetObjectRegistry.Server_QueueSync(si);
                                                    
                                                }// tp 0,-512 @a
                                            }

                                            bodyplr.Value.ServerPlayerState.loaded_save = true;
                                        } catch (Exception e) {
                                            log.error($"LoadGame_MultiplayerPatch: READING {bodyplr.Value.ToString()}:\nPATH: {SavesystemPatch.savedatapathreplacement}\n{e.ToString()}");
                                        }
                                    } else {
                                        log.warn($"LoadGame_MultiplayerPatch: SAVE FILE NOT FOUND FOR {bodyplr.Value}");
                                    }
                                }

                                if (Con.IsConsoleOpen()) { // close it
                                    ConsoleScript.instance.ToggleActiveState();
                                }

                            } catch (Exception e) {
                                log.error($"LoadGame_MultiplayerPatch: READING PLAYERS\n{e.ToString()}");
                            }

                            SaveSystem.loadedRun = true;
                            SaveSystem_HasSave_MultiplayerPatch.force = false;
                            SavesystemPatch.savedatapathreplacement = mpsavedir;
                            force = false;
                            PlayerCamera.main.body = OG_LOCAL_BODY;
                            PlayerCheckpoint.ApplyLocalCraftMarkers();


                            try {
                                foreach (var bodyplr in ScavPlayer.BodyToPlayerDict) {
                                    if (!bodyplr.Value.ServerPlayerState.loaded_save) {
                                        var msg = $"LoadGame_MultiplayerPatch: COULD NOT FIND PLAYER TO LOAD: {bodyplr.Value}";
                                        log.warn(msg);
                                    }
                                }
                            } catch (Exception e) {
                                log.error($"LoadGame_MultiplayerPatch: TEST\n{e.ToString()}");
                            }


                        } else {
                            throw new Exception("Multiplayer rules file does not exist.");
                        }
                    }
                    catch (Exception e) {
                        log.error($"LoadGame_MultiplayerPatch: READING\n{e.ToString()}");
                    }


                    SaveSystem.loadedRun = true;
                    SavesystemPatch.PendingLoadMpSave = false;
                    SaveSystem_HasSave_MultiplayerPatch.force = false;
                    SavesystemPatch.savedatapathreplacement = SavesystemPatch.vanillasavefolder;
                    force = false;
                    PlayerCamera.main.body = OG_LOCAL_BODY;
                }
            } catch (Exception e) {
                log.error($"LoadGame_MultiplayerPatch\n{e.ToString()}");
            }
            SaveSystem_HasSave_MultiplayerPatch.force = false;
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.vanillasavefolder;
            force = false;
            PlayerCamera.main.body = OG_LOCAL_BODY;
        }
    }
}


[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.Start))]
public static class PreRunScript_Start_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions);

    static void Prefix() {
        SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();
    }
}




// one landing point for the current level. it lives in save.sv and replaces the generated spawn.
public static class WorldSpawn {
    public static bool Set;
    public static Vector2 Pos;
    static int biome;
    const string KeyX = "mpWorldSpawnX";
    const string KeyY = "mpWorldSpawnY";
    const string KeyBiome = "mpWorldSpawnBiome";

    public static void Clear() {
        Set = false;
    }

    public static bool Applies(WorldGeneration world) {
        if (!Set || world == null)
            return false;
        // a debug map is one level. a generated layer only keeps the point saved on that depth.
        if (world.biomeOverride == WorldGeneration.OverrideSceneType.Debug)
            return true;
        return world.biomeDepth == biome;
    }

    public static void Assign(Vector2 pos) {
        Set = true;
        Pos = pos;
        biome = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0;
        Stamp();
    }

    public static void Read() {
        Set = false;
        string path = Path.Combine(SavesystemPatch.SessionSaveFolder, "save.sv");
        if (!File.Exists(path))
            return;
        try {
            JObject root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            if (root[KeyX] == null || root[KeyY] == null)
                return;
            Pos = new Vector2(root[KeyX].Value<float>(), root[KeyY].Value<float>());
            biome = root[KeyBiome] != null ? root[KeyBiome].Value<int>() : 0;
            Set = true;
            log.l($"World spawn loaded {Pos} biome {biome}");
        } catch (Exception e) {
            log.warn($"WorldSpawn.Read: {e.Message}");
        }
    }

    // a debug folder can hold just the spawn before anyone has a character save.
    public static bool SpawnOnlyFile() {
        string path = Path.Combine(SavesystemPatch.SessionSaveFolder, "save.sv");
        if (!File.Exists(path))
            return false;
        try {
            JObject root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            return root["body"] == null && root[KeyX] != null;
        } catch {
            return false;
        }
    }

    public static void Stamp() {
        if (!Set)
            return;
        try {
            string folder = SavesystemPatch.SessionSaveFolder;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "save.sv");
            JObject root;
            if (File.Exists(path))
                root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            else if (SavesystemPatch.InDebugMap)
                root = new JObject();
            else
                return;
            root[KeyX] = Pos.x;
            root[KeyY] = Pos.y;
            root[KeyBiome] = biome;
            File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Formatting.None)));
        } catch (Exception e) {
            log.error($"WorldSpawn.Stamp\n{e}");
        }
    }
}

public static class SavesystemPatch {
    public static string savedatapathreplacement = "";
    // set for map debug and the tutorial. those sessions neither read nor write mp_save.
    internal static bool PreserveSave;
    // debug map started with DebugWorldSaveSystem. the save file's rules must not turn it back off.
    internal static bool DebugWorldUsesSave;
    // continue/LoadRun: sticky across WaitLoad→scene load so Start still treats this as a loaded run
    // even if something clears SaveSystem.loadedRun before TryLoadGame.
    internal static bool PendingLoadMpSave;
    const string _mpsavefolder_name = "mp_save";
    const string _mpdebugfolder_name = "mp_debug_save";
    public static string mpsavefolder => Path.Combine(Application.persistentDataPath, _mpsavefolder_name);
    public static string mpdebugfolder => Path.Combine(Application.persistentDataPath, _mpdebugfolder_name);
    // true for the whole debug-map session. saves for that session stay out of mp_save.
    internal static bool InDebugMap;
    // empty is the default slot at the root of mp_debug_save. a name is a folder under it.
    internal static string DebugMapName;
    internal static string SessionSaveFolder => InDebugMap ? DebugFolder(DebugMapName) : mpsavefolder;

    internal static bool ValidDebugMapName(string name) {
        if (string.IsNullOrEmpty(name) || name.Length > 32)
            return false;
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            bool digit = c >= '0' && c <= '9';
            if (!letter && !digit)
                return false;
        }
        return true;
    }

    internal static string DebugFolder(string name) {
        if (string.IsNullOrEmpty(name))
            return mpdebugfolder;
        return Path.Combine(mpdebugfolder, name);
    }

    internal static bool HasDebugSave(string name = null) {
        return File.Exists(Path.Combine(DebugFolder(name), "world_tiles.bin"));
    }

    internal static void DeleteDebugSave() {
        if (!Directory.Exists(mpdebugfolder))
            return;
        try {
            foreach (string file in Directory.GetFiles(mpdebugfolder))
                File.Delete(file);
            foreach (string dir in Directory.GetDirectories(mpdebugfolder)) {
                string folder = Path.GetFileName(dir);
                if (folder.StartsWith("STEAM_") || folder.StartsWith("NAME_"))
                    Directory.Delete(dir, true);
            }
        } catch (Exception e) {
            log.error($"DeleteDebugSave\n{e}");
        }
    }

    internal static void WriteDebugWorld() {
        if (WorldGeneration.world == null)
            return;
        string folder = SessionSaveFolder;
        try {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "world_tiles.bin"), SerializeWorldBlocks());
            if (FluidManager.main != null)
                File.WriteAllBytes(Path.Combine(folder, "world_fluid.bin"), SerializeWorldFluids());
            MapPoses.Write(folder);
            log.l("Debug map saved to " + folder);
        } catch (Exception e) {
            log.error($"WriteDebugWorld\n{e}");
        }
    }

    internal static void ReadDebugWorld() {
        string folder = SessionSaveFolder;
        try {
            string tiles = Path.Combine(folder, "world_tiles.bin");
            if (File.Exists(tiles))
                WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa = File.ReadAllBytes(tiles);
            string fluids = Path.Combine(folder, "world_fluid.bin");
            if (File.Exists(fluids))
                WorldMP.ccccccccccccccccccccccccccc = File.ReadAllBytes(fluids);
            MapPoses.Read(folder);
            log.l("Debug map read from " + folder);
        } catch (Exception e) {
            log.error($"ReadDebugWorld\n{e}");
        }
    }
    public static string vanillasavefolder => Application.persistentDataPath;
    public const string mpsaveheaderfile = "mp_rules.json";

    internal struct MpWorldPeek {
        public int biomeDepth;
        public int totalTraveled;
        public float lootRarity;
        public float trapRarity;
        public bool midlevel;
        public bool ok;
    }

    // mid-layer checkpoints store raw biomeDepth; layer-transition / vanilla SaveGame stores depth+1.
    internal static bool TryPeekMpWorldState(out MpWorldPeek peek) {
        peek = default;
        try {
            bool midlevel = false;
            var rulespath = Path.Combine(SessionSaveFolder, mpsaveheaderfile);
            if (File.Exists(rulespath)) {
                var rules = JObject.Parse(File.ReadAllText(rulespath));
                if (rules.TryGetValue("LEVEL", out var lvl) && lvl != null && lvl.Type != JTokenType.Null)
                    midlevel = lvl.Value<bool>();
            }

            string savePath = Path.Combine(SessionSaveFolder, "save.sv");
            if (!File.Exists(savePath) && Directory.Exists(SessionSaveFolder)) {
                foreach (var dir in Directory.GetDirectories(SessionSaveFolder)) {
                    var candidate = Path.Combine(dir, "save.sv");
                    if (File.Exists(candidate)) {
                        savePath = candidate;
                        break;
                    }
                }
            }
            if (!File.Exists(savePath))
                return false;

            var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(savePath)));
            if (root["biome"] == null)
                return false;

            int stored = (int)root["biome"];
            // mid-layer: raw depth. otherwise vanilla depth+1 → convert back.
            int depth = midlevel ? stored : Math.Max(0, stored - 1);
            if (WorldGeneration.world != null && WorldGeneration.world.amountOfLayers > 0)
                depth = Mathf.Clamp(depth, 0, WorldGeneration.world.amountOfLayers - 1);

            peek.biomeDepth = depth;
            peek.midlevel = midlevel;
            peek.totalTraveled = root["totalTraveled"] != null ? (int)root["totalTraveled"] : 0;
            peek.lootRarity = root["lootRarity"] != null ? (float)root["lootRarity"] : 1f;
            peek.trapRarity = root["trapRarity"] != null ? (float)root["trapRarity"] : 1f;
            peek.ok = true;
            return true;
        } catch (Exception e) {
            log.warn($"TryPeekMpWorldState: {e.Message}");
            return false;
        }
    }

    internal static void ApplyPeekedWorldState(WorldGeneration world, in MpWorldPeek peek) {
        if (world == null || !peek.ok)
            return;
        world.debugStartDepth = peek.biomeDepth;
        world.biomeDepth = peek.biomeDepth;
        if (peek.totalTraveled > 0)
            world.totalTraveled = peek.totalTraveled;
        if (peek.lootRarity > 0f)
            world.lootRarityMultiplier = peek.lootRarity;
        if (peek.trapRarity > 0f)
            world.trapRarityMultiplier = peek.trapRarity;
    }

    public static string ReplacementFor_Application_persistentDataPath() {
        return savedatapathreplacement;
    }

    public static bool HasAnySaveFile() {
        return HasVanillaSaveFile() || HasMultiplayerSaveFile();
    }
    public static bool HasOnlyVanillaSaveFile() {
        return HasVanillaSaveFile() && !HasMultiplayerSaveFile();
    }
    public static bool HasVanillaSaveFile() {
        return File.Exists(vanillasavefolder + "\\save.sv");
    }
    public static bool HasMultiplayerSaveFile() {
        if (File.Exists(mpsavefolder + "\\save.sv"))
            return true;
        if (!Directory.Exists(mpsavefolder))
            return false;
        foreach (var dir in Directory.GetDirectories(mpsavefolder)) {
            if (File.Exists(Path.Combine(dir, "save.sv")))
                return true;
        }
        return false;
    }

    public static void UpdatePersistentDataPathCuzMultiplayer() {

        SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();

        if (Util.IsInMainMenu()) {
            var prs = PreRunScript.instance;
            if (prs != null) {
                prs.loadButton.interactable = SaveSystem.HasSave();
            }
        }
    }

    internal static string GetTheOnePersistentDataPathWithSaveFile() {

        if (InDebugMap)
            return SessionSaveFolder;
        if (Net.IsRunning && HasMultiplayerSaveFile()) {
            return mpsavefolder;
        } else {
            return Application.persistentDataPath;
        }
    }
    internal static string GetPersistentDataPathToSave() {
        if (InDebugMap)
            return SessionSaveFolder;
        if (Net.IsRunning) {
            return mpsavefolder;
        } else {
            return Application.persistentDataPath;
        }
    }
    internal static void DeleteMPSave() {
        PendingLoadMpSave = false;
        if (Directory.Exists(mpsavefolder)) {
            try {
                Directory.Delete(mpsavefolder, true);
            } catch (Exception e) {
                log.error($"LoadGame_MultiplayerPatch: DELETING SAVE\n{e.ToString()}");
            }
        }
    }


    internal static IEnumerable<CodeInstruction> TranspilerToReplaceTheApplicationPath(IEnumerable<CodeInstruction> instructions) {

        var codes = new List<CodeInstruction>(instructions);

        for (int i = 0; i < instructions.Count(); i++) {
            var code = codes[i];
            if (code.ToString().Contains("get_persistentDataPath")) {
#if !RELEASE && false
                log.warn("SAVESYSTEM TRANSPILER: replcaed Application.persistentDataPath " + i.ToString());
#endif
                //code.opcode = ;
                //code.operand = AccessTools.Field(typeof(SavesystemPatch), nameof(SavesystemPatch.savedatapathreplacement));

                code.operand = AccessTools.Method(typeof(SavesystemPatch), nameof(SavesystemPatch.ReplacementFor_Application_persistentDataPath));
            }
        }

        return instructions;
    }

    internal static IEnumerable<CodeInstruction> StripSaveDelete(IEnumerable<CodeInstruction> instructions) {
        var delete = AccessTools.Method(typeof(File), nameof(File.Delete), new Type[] { typeof(string) });
        var codes = new List<CodeInstruction>(instructions);
        for (int i = 0; i < codes.Count; i++) {
            if (delete != null && codes[i].Calls(delete))
                codes[i] = new CodeInstruction(OpCodes.Pop);
        }
        return codes;
    }



    #region SERIALIZERS

    public static WorldGeneration world => WorldGeneration.world;
    //public static Traverse worldBlocks_field => Traverse.Create(WorldGeneration.world).Field("worldBlocks");
    //public static ushort[,] worldBlocks => (ushort[,])worldBlocks_field.GetValue();
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static byte[,] fluid => FluidManager.main.fluid;


    public static byte[] SerializeWorldBlocks() {
        var byteblocks = new byte[world.width * world.height];

        var blocks = worldBlocks;
        // flatten the 2d array
        // and convert to bytes cuz the block ids are far from going above 255


        for (int y = 0; y < world.height; y++) {
            for (int x = 0; x < world.width; x++) {
                byteblocks[y * world.height + x] = (byte)blocks[x, y];
            }
        }

        byteblocks = Util.Compress(byteblocks);

        //var base64 = Convert.ToBase64String(byteblocks);
        //
        //Plugin.log.LogInfo($"TEMP DEV: Compressed map data size: {base64.Length}\n#################################\n{base64}\n#################################");
        //
        //return base64;
        return byteblocks;
    }


    public static void DeserializeWorldBlocks(in byte[] mapdata) {
        try {
            var mapblocks = Util.Decompress(mapdata);

            var blocks = worldBlocks;
            for (int y = 0; y < world.height; y++) {
                for (int x = 0; x < world.width; x++) {
                    blocks[x, y] = (ushort)mapblocks[y * world.height + x];
                }
            }

            world.UpdateWorld();
        } catch (Exception e) {
            log.error($"Failed to load map blocks: {e}");
        }
    }



    public static byte[] SerializeWorldFluids() {
        var bytefluids = new byte[world.width * world.height];

        var f = fluid;
        // flatten the 2d array
        // and convert to bytes cuz the block ids are far from going above 255


        for (int y = 0; y < world.height; y++) {
            for (int x = 0; x < world.width; x++) {
                bytefluids[y * world.height + x] = (byte)f[x, y];
            }
        }

        bytefluids = Util.Compress(bytefluids);

        return bytefluids;
    }


    public static void DeserializeWorldFluids(in byte[] mapdata) {
        try {
            var mapfluidss = Util.Decompress(mapdata);

            var f = fluid;
            for (int y = 0; y < world.height; y++) {
                for (int x = 0; x < world.width; x++) {
                    f[x, y] = (byte)mapfluidss[y * world.height + x];
                }
            }

            world.UpdateWorld();
        } catch (Exception e) {
            log.error($"Failed to load map fluid: {e}");
        }
    }



#if !RELEASE
    public static byte[] SerializeWorldObjects() {

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);



        return new byte[0];
    }
#endif


    #endregion




}


