using HarmonyLib;
using CasualtiesTogetherUtils;
using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using LiteNetLib;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using UnityEngine.UI;

namespace Together;









class TrackerWorld : MonoBehaviour {

    void Awake() {

    }
    void Start() {
        InvokeRepeating(nameof(Update_60s), 1, 60);
    }
    void LateUpdate() {
#if !RELEASE
        var world = WorldGeneration.world; // make loading screen transparent
        if (world != null) {
            foreach (var item in world.genRects) {
                if (item.TryGetComponent(out Image img)) {
                    img.color = new Color(img.color.r, img.color.g, img.color.b, 0.02f);
                }
            }
        }
#endif
    }

    void Update_60s() {
        if (Net.IsServer && SharedMain.LocalWorldIsGenerated) {
            SaveLocationSnapshots();
        }
    }

    internal void SaveLocationSnapshots() {
        foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (!plr.ServerPlayerState.is_loaded_in) {
                continue;
            }
            plr.ServerPlayerState.SaveLocationSnapshot();

        }
    }

}




public class WorldMP : CasualtiesTogetherSingleton {


    #region EVENTS

    public static event Action OnWorldgenFinish;


    #endregion



    public static Dictionary<string, object> GetNormalRunSettings() {
        Dictionary<string, object> dict = new();
        foreach (KeyValuePair<string, object> presetValue in RunSettings.GetPreset("normal").presetValues) {
            dict.Add(presetValue.Key, presetValue.Value);
        }
        return dict;
    }

    public static Dictionary<string, object> runSettings {
        get {
            Dictionary<string, object> prerunset = null;
            if (PreRunScript.instance != null) {
                prerunset = PreRunScript.instance.runSettings;
            }
            if (Util.IsInMainMenu()) {
                return prerunset;
            }
            return (WorldGeneration.runSettings ?? prerunset ?? GetNormalRunSettings());
        }
    }
    public struct RunPrefs : INetSerializeByMemcpy {
        public bool tutorial;
        public bool unchipped;

        public RunPrefs ReadPrefs() {
            tutorial = PlayerPrefsExtended.GetBool("tutorial");
            if (runSettings != null) {
                unchipped = (bool)runSettings["unchipped"];
            }
            return this;
        }
        public void ApplyPrefs(bool abide_rules = true, bool apply_tutorial = true) {
            if (apply_tutorial) {
                PlayerPrefsExtended.SetBool("tutorial", tutorial);
            }
            if (abide_rules && !rules.UnchippedIsIndividual) {
                if (runSettings != null) {
                    runSettings["unchipped"] = unchipped;
                }
            }
        }
    }

    public static string CompileRunSettings() {
        string input = JsonConvert.SerializeObject(SaveSystem.DicToTupleList(runSettings), Formatting.None, new JsonSerializerSettings {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        });
        return Convert.ToBase64String(Util.Compress(Encoding.UTF8.GetBytes(input)));
    }
    public static Dictionary<string, object> ReadRunSettings(string base64) {
        Dictionary<string, object> dict = GetNormalRunSettings();
        try {
            base64 = Encoding.UTF8.GetString(Util.Decompress(Convert.FromBase64String(base64)));
            JArray jArray = JArray.Parse(base64);

            foreach (KeyValuePair<string, object> item2 in SaveSystem.TupleListToDic(jArray.ToObject<List<(string, string)>>())) {
                dict[item2.Key] = item2.Value;
            }
        } catch (Exception e) {
            log.error($"ReadRunSettings: {e.Message}\n{new StackTrace()}");
        }
        return dict;
    }
    public static void ApplyRunSettings(in string base64) {
        ApplyRunSettings(ReadRunSettings(base64));
    }
    public static void ApplyRunSettings(Dictionary<string, object> newRunSettings) {
        var oldset = runSettings;
        if (PreRunScript.instance != null) {
            PreRunScript.instance.runSettings = newRunSettings;
            PreRunScript.instance.currentPreset = 6;
            PreRunScript.instance.UpdateAllSettingDisplays();
        }
        WorldGeneration.runSettings = newRunSettings;
        if (oldset != null) {
            if (Net.IsClient) {
                if (rules.UnchippedIsIndividual) { // set it back
                    newRunSettings["unchipped"] = oldset.GetValueOrDefault("unchipped", false);
                }
            }
        }
    }


    public static WorldGeneration world => WorldGeneration.world;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static byte[,] fluid => FluidManager.main.fluid;

    public static MultiplayerGameRules rules => Multiplayer.rules;
    public static bool verbose => Multiplayer.verbose;


    static bool mainmenu_is_screen_darkening = false;

    internal static byte[] aaaaaaaaaaaaaaaaaaaaaaaaaaa = null; // SAVELOAD_worldtiles
    internal static byte[] bbbbbbbbbbbbbbbbbbbbbbbbbbb = null; // SAVELOAD_worldobj
    internal static byte[] ccccccccccccccccccccccccccc = null; // SAVELOAD_worldfluid

    internal static Sprite whiteSquare = null;


    #region SYNCED_VARIABLES

    public static bool earthquake_enabled;
    public static bool allow_continue_to_next_layers;

    #endregion

    void Awake() {
        ResetWorldParameters();



        //var E5E5E5FF = new Color(0.898f, 0.898f, 0.898f);
        var E5E5E5FF = Color.white;

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, E5E5E5FF);
        texture.SetPixel(1, 0, E5E5E5FF);
        texture.SetPixel(1, 1, E5E5E5FF);
        texture.SetPixel(0, 1, E5E5E5FF);
        texture.filterMode = FilterMode.Point; // nearest neighbor

        whiteSquare = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void Start() {


    }
    void OnSceneLoaded(Scene a, LoadSceneMode b) {
        aaaaaaaaaaaaaaaaaaaaaaaaaaa = null;
        bbbbbbbbbbbbbbbbbbbbbbbbbbb = null;
        ccccccccccccccccccccccccccc = null;
    }
    void FixedUpdate() {
        if (Util.IsInWorld()) {
            if (mainmenu_is_screen_darkening) {
                mainmenu_is_screen_darkening = false;
            }
            if (
                !earthquake_enabled && world.earthquakeTime <= 0 || 
                world.biomeOverride == WorldGeneration.OverrideSceneType.Debug// to fix client-side infinite earthquake for no reason
            ) {
                world.earthquakeTime = 0;
                world.earthquakeDelay = 1000f;
                world.earthquakeIntensity = 0;
            }
        }
    }
    void Update() {
        if (!Net.IsRunning) return;

        if (SharedMain.LocalWorldIsGenerated) {


        }
    }

    public static LayerModifier GetLayerModifier() {
        foreach (var item in LayerModifier.availableModifiers) {
            if (item.active) {
                return item;
            }
        }
        return null;
    }

    public static void ResetWorldParameters() {
        earthquake_enabled = true;
        allow_continue_to_next_layers = true;
    }
    // stuff like earthquakes are disabled
    public static void ResetWorldParametersPeaceful() {
        ResetWorldParameters();
        earthquake_enabled = false;

        SetRadlinePlayerPrefs(false);
    }

    public static void SetRadlinePlayerPrefs(bool enabled) {
        PlayerPrefs.SetInt("radlinedisable", enabled ? 0 : 1);
    }
    public static void SetTutorialPlayerPrefs(bool enabled) {
        PlayerPrefs.SetInt("tutorial", enabled ? 1 : 0);
    }


    public static void LoadDebugWorld() {
        _CheckIfCanLoadAWorld();
        SetTutorialPlayerPrefs(false);
        // the scene starts with whatever loadedRun the menu left behind. leave the save file where it is.
        SavesystemPatch.PreserveSave = true;
        SaveSystem.loadedRun = false;

        if (Multiplayer.IsNetworkActiveAndIsServer()) {
            ServerMain.Server_Announce_GAME_START();
        }

        SceneManager.LoadScene("SampleScene");

        Util.CallLambdaWhen(() => { return WorldGeneration.world != null; }, () => {
            WorldGeneration.world.biomeOverride = WorldGeneration.OverrideSceneType.Debug;
        });

    }

    public static void LoadVanillaGeneratedWorld(bool loadsave = false) {
        _CheckIfCanLoadAWorld();
        SavesystemPatch.PreserveSave = false;
        ResetWorldParameters();

        //current_instruction = null;
        SetRadlinePlayerPrefs(true); // After 7.0.1 it's always enabled, only the timer is edited in run settings 

        LoadWorldScene(loadsave);
    }
    public static void LoadVanillaTutorialWorld(bool fadeout = true) {
        _CheckIfCanLoadAWorld();
        // same as map debug. the tutorial button must not apply or replace mp_save.
        SavesystemPatch.PreserveSave = true;
        SaveSystem.loadedRun = false;

        ResetWorldParameters();

        SetTutorialPlayerPrefs(true);
        SetRadlinePlayerPrefs(false);

        //current_instruction = null;

        // save file stays. just dont keep the last run's net ids.
        NetObjectRegistry.WipeSessionEntities();

        LoadWorldScene(false, fadeout);
    }


    static GameObject SKY_BACKGROUND = null;

    public static void CreateSkyBackground() {
        //OG: WorldGeneration.Start  V5t4 
        CreateSkyBackground(world.skyColors.Evaluate(UnityEngine.Random.value), (UnityEngine.Random.Range(0f, 1f) < 0.3f) ? 1f : 0f);
    }
    public static void CreateSkyBackground(Color skyColor, float rainIntensity) {
        SKY_BACKGROUND = Utils.Create("Special/wallholes", Vector2.zero, 0);

        var spr = SKY_BACKGROUND.GetComponent<SpriteRenderer>();
        spr.sprite = whiteSquare;
        spr.gameObject.transform.localScale *= 99999f;

        world.skyMaterial.SetColor("_TopColor", skyColor);
        world.skyMaterial.SetFloat("_RainIntensity", rainIntensity);

        var audio = SKY_BACKGROUND.GetComponent<AudioSource>();
        audio.maxDistance = 100000;
        audio.spatialBlend = 0.001f;
        audio.volume *= 0.7f;

        SKY_BACKGROUND.name = "MP_SURFACE_BACKGROUND";

        if (Multiplayer.IsNetworkActiveAndIsServer()) {
            ServerMain.Server_SendWorldState();
        }
    }
    public static void RemoveSkyBackground() {
        if (SKY_BACKGROUND != null) {
            GameObject.Destroy(SKY_BACKGROUND);

            if (Multiplayer.IsNetworkActiveAndIsServer()) {
                ServerMain.Server_SendWorldState();
            }
        }
    }
    public static bool HasSkyBackground(out (Color,float) skyinfo) {
        if (SKY_BACKGROUND != null) {
            skyinfo = (
                world.skyMaterial.GetColor("_TopColor"),
                world.skyMaterial.GetFloat("_RainIntensity")
            );
            return true;
        }
        skyinfo = (default(Color), 0);
        return false;
    }
    public static bool HasSkyBackground() {
        return SKY_BACKGROUND != null;
    }



    public static void LoadWorldScene(bool loadsave = false, bool fadeout = true) {
        if (world != null) {
            throw new Exception("Ur aready in the world cuh");
        }
        mainmenu_is_screen_darkening = true;
        //if (current_instruction != null) {
        //    SetTutorialPlayerPrefs(current_instruction.tutorial);
        //}
        SaveSystem.loadedRun = loadsave;


        if (fadeout) {
            var prs = PreRunScript.instance;
            if (prs) {
                mainmenu_is_screen_darkening = true;
                prs.StartCoroutine(prs.WaitLoad());
            } else {
                throw new Exception("PreRunScript DOES NOT EXIST ?! WHAT ?????");
            }
        } else {
            SceneManager.LoadScene("SampleScene");
        }
    }
    static void _CheckIfCanLoadAWorld() {
        if (Util.IsInWorld()) {
            throw new Exception("World is already loaded.");
        }
        if (mainmenu_is_screen_darkening) {
            throw new Exception("World is already loading.");
        }
    }











    static internal IEnumerator Patched_GenerateWorld(WorldGeneration world) {
        world.loadingObject.SetActive(value: true);
        world.generatingWorld = true;
        SharedMain.LocalWorldIsGenerating = true;
        WorldGeneration_ApplyLayerModifiers_MultiplayerPatch.sent_to_clients = false;
        ClientMain._last_reminderpack_while_generating_received = false;
        client_technically_finished_worldgen_now_just_waiting_for_server = false;

        if (Net.IsRunning) {

            Body_PlaceBody_MultiplayerPatch.has_spawn_location = false;

            try {
                WorldGeneration_GenerateWorld_MultiplayerPatch.InitializeSpecialResourcePrefabs();
            } catch (Exception ex) { log.error("InitializeSpecialResourcePrefabs\n" + ex.ToString()); }

            SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;

            if (Net.IsClient) {
                ClientMain.ServerIsGeneratingWorld = true;

                //Multiplayer.DoMultiplayerStatusMessageLog("GenerateWorld: Waiting for server to send the seed.");
                world.loadingText.text = Lang.Get("worldgen_waitforserver_seed");
                log.l("CLIENT: Waiting for worldgen params...");

                // start a coroutine that checks if when is a seed
                while (WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used) {
                    ClientMain.ServerIsGeneratingWorld = true;
                    if (!Net.IsRunning) {
                        yield break;
                    }
                    SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
                    yield return null;
                }
                log.l("CLIENT: Received the worldgen params, starting world generation!");

                WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.Apply();
                WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = true;

                


            } else {

                WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams = new LastBeforeGenerationState(); // update the generationstate

                ServerMain.Server_AnnounceSeed(ServerMain.AllClientIdsExceptHost);



                if (Multiplayer.rules.LayerFinishKeepXOffset) { // move all players to center
                    var leftx = 0f;
                    var rightx = 0f;
                    bool firstfound = false;
                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        var body = item.Key;
                        var plr = item.Value;
                        if (body != null && body.alive) {
                            var n = Mathf.Clamp(body.transform.position.x, -world.width * 0.49f, world.width * 0.49f);
                            plr.ServerPlayerState.layer_transition_x_offset = n ;
                            if (!firstfound) {
                                firstfound = true;
                                leftx = n;
                                rightx = n;
                            } else {
                                leftx = Mathf.Min(leftx, n);
                                rightx = Mathf.Max(rightx, n);
                            }
                        } else {
                            plr.ServerPlayerState.layer_transition_x_offset = 0;
                        }
                    }
                    var centerx = Mathf.Lerp(leftx, rightx, 0.5f);

                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        var body = item.Key;
                        var plr = item.Value;
                        if (body != null && body.alive) {
                            plr.ServerPlayerState.layer_transition_x_offset = Mathf.Clamp(plr.ServerPlayerState.layer_transition_x_offset - centerx, -world.width * 0.49f, world.width * 0.49f);
                            body.transform.position = new Vector2(plr.ServerPlayerState.layer_transition_x_offset, body.transform.position.y);
                        } 
                    }
                }
            }


            foreach (var item in ScavPlayer.BodyToPlayerDict.Values) {
                item.pos = Vector2.zero;
                if (!item.IsLocal && item.body != null) {
                    item.netBody.SetNetIgnoreTime(1);
                }
                if (item.ServerPlayerState != null) {
                    item.ServerPlayerState.OnWorldgenStart();
                }
            }

        }

        Multiplayer._InvokeOnSceneChangeOrWorldStartGenerate();


        //if (log.verbose)
            log.l("WorldGeneration.GenerateWorld");
        yield return WorldGeneration_GenerateWorld_MultiplayerPatch.GenerateWorld(world);



    }




    static internal IEnumerator Patched_WorldPlacePlayer() {

        SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
        ServerMain._ded_server_switch_counter = -20;

        if (Net.IsRunning) {
            if (Net.IsClient) {
                ClientMain.ServerIsGeneratingWorld = true;
                ClientMain._last_reminderpack_while_generating_received = false;

                // ask the host to teleport me to spawn point
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_WorldPlacePlayer, WorldGeneration.unchipped, true);
                log.l("CLIENT: Waiting at WorldPlacePlayer, for server to send me a spawn location.");


                world.loadingText.text = Lang.Get("worldgen_waitforserver_spawn");
                double asked = Time.realtimeSinceStartupAsDouble;
                while (Multiplayer.IsNetworkActiveAndIsClient() && (!ClientMain._last_reminderpack_while_generating_received)) {
                    yield return new WaitForSecondsRealtime(0.25f);
                    if (!Multiplayer.IsNetworkActiveAndIsClient()) {
                        yield break;
                    }
                    yield return new WaitForSecondsRealtime(0.25f);
                    if (!Multiplayer.IsNetworkActiveAndIsClient()) {
                        yield break;
                    }
                    // the first ask can land before the host has this player, or after the host already placed them.
                    if (Time.realtimeSinceStartupAsDouble - asked >= 2.0) {
                        asked = Time.realtimeSinceStartupAsDouble;
                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_WorldPlacePlayer, WorldGeneration.unchipped, true);
                        log.l("CLIENT: still waiting for a spawn location, asking again.");
                    }
                }
                if (ClientMain._last_reminderpack_while_generating_received) {
                    if (ScavPlayer.TryGetLocalNetBody(out var lpb)) {
                        lpb.OnReceiveSyncPacket(ClientMain._last_reminderpack_while_generating, true);
                    } 
                }
                log.l("CLIENT: Received location for WorldPlacePlayer.");
            }
        }

        yield return WorldGeneration_WorldPlacePlayer_MultiplayerPatch.WorldPlacePlayer(world); 
        log.l("WorldGeneration.WorldPlacePlayer");
    }

    static float WORLDCLEAR_MAX_HALT_TIME = 0.04f;

    static IEnumerator FinishWorldGen_ClientClearWorldObjects() {
        var items = FindObjectsOfType<Item>(true).Select(x => x.gameObject);
        var builds = FindObjectsOfType<BuildingEntity>(true).Select(x => x.gameObject);

        var aa = items.Union(builds).ToList();
        
        world.loadingText.text = Lang.Get("worldgen_waitforserver_delete");
        var time = Time.realtimeSinceStartupAsDouble;
        for (int i = 0; i < aa.Count; i++) {
            var x = aa[i];
            if (x == null || NetObjectRegistry.ObjectShouldNotBeSynced(x) || NetObjectRegistry.IsRegistered(x)) {
                // dont destroy it
            } else { 
                GameObject.Destroy(x);

                if (Time.realtimeSinceStartupAsDouble - time > WORLDCLEAR_MAX_HALT_TIME) {
                    yield return null;
                    time = Time.realtimeSinceStartupAsDouble;
                }
            }

        }

        yield break;
    }

    public static bool client_technically_finished_worldgen_now_just_waiting_for_server = false;
    static internal IEnumerator Patched_FinishWorldGeneration() {

        SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
        ServerMain._ded_server_switch_counter = -20;

        if (Net.IsRunning) {

            foreach (var item in ScavPlayer.BodyToPlayerDict) {
                item.Value.ResetEntropy();
                item.Value.levelPlayTime = 0;
            }

            if (world.doPod) {
                foreach (var obj in ScavPlayer.BodyToPlayerDict) {
                    var body = obj.Key;
                    if (!obj.Value.IsLocal) {
                        //OG: WorldGeneration.FinishWorldGeneration V501
                        body.hearingLoss += 15f;
                        body.hunger -= 10f;
                        body.thirst -= 15f;
                    }
                }
            }

            WorldChunkSync.server_chunksync_queue.Clear();
            if (Net.IsClient) {
                WorldChunkSync.singleton.timer_TilemapSync = 10;
                WorldChunkSync.singleton.timer_TilemapFluidSync = 10;
                ClientMain.ServerIsGeneratingWorld = true;
                client_technically_finished_worldgen_now_just_waiting_for_server = true;


                yield return FinishWorldGen_ClientClearWorldObjects();

                ClientMain.ServerIsGeneratingWorld = true;

                // ask the host to teleport me to spawn point
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_FinishedWorldgen, WorldGeneration.unchipped, true);
                log.l("CLIENT: Finished worldgen, sending message to server.");


                world.loadingText.text = Lang.Get("worldgen_waitforserver");
                while (Multiplayer.IsNetworkActiveAndIsClient() && (ClientMain.ServerIsGeneratingWorld || !ClientMain._last_reminderpack_while_generating_received)) { // wait until client receives the world state packet
                    WorldChunkSync.TilemapSyncUpdate();
                    yield return new WaitForSecondsRealtime(0.01f);
                    NetObjectRegistry._ObjectSyncUpdateLoopUniversal(false);
                    yield return new WaitForSecondsRealtime(0.3233f);
                    NetObjectRegistry._ObjectSyncUpdateLoopUniversal(false);
                    yield return new WaitForSecondsRealtime(0.3334f);
                    NetObjectRegistry._ObjectSyncUpdateLoopUniversal(false);
                    yield return new WaitForSecondsRealtime(0.3334f);
                }
                yield return new WaitForSecondsRealtime(1f);


                if (ClientMain._last_reminderpack_while_generating_received)  {
                    if (ScavPlayer.TryGetLocalNetBody(out var lpb)) {
                        lpb.OnReceiveSyncPacket(ClientMain._last_reminderpack_while_generating, true);
                    } 
                }
            } 
        }

#if !RELEASE
        if (aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) {
            world.loadingText.text = "Loading world tiles...";
            yield return null;
            if (aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) {
                SavesystemPatch.DeserializeWorldBlocks(aaaaaaaaaaaaaaaaaaaaaaaaaaa);
            }
            world.loadingText.text = "Loading world fluids...";
            yield return null;

            if (ccccccccccccccccccccccccccc != null) {
                SavesystemPatch.DeserializeWorldFluids(ccccccccccccccccccccccccccc);
            }
            world.loadingText.text = "Loading world objects...";
            yield return null;
            if (bbbbbbbbbbbbbbbbbbbbbbbbbbb != null) {
                try {
                    var uncompressed = Util.Decompress(bbbbbbbbbbbbbbbbbbbbbbbbbbb);

                    using var stream = new MemoryStream(uncompressed);
                    using var reader = new BinaryReader(stream);


                }
                catch (Exception e) {
                    log.error($"Failed to load objects: {e}");
                }
            }
            yield return null;
        }
        
#endif

        if (Net.IsRunning) {

            if (Net.IsServer) {
                foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
                    plr.ServerPlayerState.OnWorldGenerate();
                }
                WorldChunkSync.FluidTilemapSyncUpdate();
            }
            WorldChunkSync.TilemapSyncUpdate();
        }

        if (Util.TryGetLocalBody(out var LOCBODY) && !LOCBODY.alive) { UIInGame.StartSpectatorMode(); }

        yield return WorldGeneration_FinishWorldGeneration_MultiplayerPatch.FinishWorldGeneration(world);
        
        log.l("WorldGeneration.FinishWorldGeneration");


        if (Util.TryGetLocalBody(out LOCBODY) && !LOCBODY.alive) { UIInGame.StartSpectatorMode();  }

        aaaaaaaaaaaaaaaaaaaaaaaaaaa = null;
        bbbbbbbbbbbbbbbbbbbbbbbbbbb = null;
        ccccccccccccccccccccccccccc = null;

        SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
        ServerMain._ded_server_switch_counter = -20;

        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            if (ClientMain._last_reminderpack_while_generating_received) {
                if (ScavPlayer.TryGetLocalNetBody(out var lpb)) {
                    log.l($"CLIENT: Received starter char state packet, that im applying just now. POS: {ClientMain._last_reminderpack_while_generating.Position}");
                    lpb.OnReceiveSyncPacket(ClientMain._last_reminderpack_while_generating, true);
                } else {
                    log.error($"CLIENT: Received starter char state packet, BUT I HAVE NO BODY !!!! POS: {ClientMain._last_reminderpack_while_generating.Position}");
                }
            } 
        }

        if (Net.IsRunning) {

            Multiplayer.ApplyGameRules();

            ServerMain.ResetPlayersFinishedStatus();
            WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = true;

            DrillPod_Update_MultiplayerPatch.didTeleport = false;
            if (PlayerCamera.main != null) {
                PlayerCamera.main.bonusActionBarTime = 0f;
            }

            ServerMain._DEV_ENABLE_HP_SYNC = true;
            WorldChunkSync._TILE_SYNC_ENABLED = true;
            WorldChunkSync._FLUID_SYNC_ENABLED = true;

            TutorialHandler_Update_MarkiplierPatch.did_multiplayer_warning = false;
            TutorialHandler_Update_MarkiplierPatch.everyone_finish_time = 0f;

            foreach (var item in ScavPlayer.BodyToPlayerDict)
                item.Value.ResetEntropy();

            if (Net.IsServer) {
                if (Multiplayer.rules.CanReviveOnNextLevel()) {
                    // offline stashes stay. a descent used to drop them, so a rejoin after the layer change was a fresh body.
                    // people still in the lobby are respawned from the body they already have.

                    var did_respawn_someone = false;
                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        if (!item.Key.alive) {
                            did_respawn_someone = true;
                            Plugin.log.LogInfo($"Respawed {item.Value.playerName} ");
                            item.Value.Server_RespawnCharacter(PlayerCamera.main.body.transform.position, true);
                        }
                    }
                    if (did_respawn_someone) {
                        var ggggg = "respawned_dead_plrs";
                        Plugin.log.LogInfo(Lang.GetEN(ggggg));
                        PlayerCamera.main.StartCoroutine(PlayerCamera.main.DoAlertDelayed(Lang.Get(ggggg), important: false, 10f));
                    }
                }

                try {
                    // because all objects were unregistered its better to re register players inventories as soon as possible 
                    foreach (var npc in NetBody.all_instances) {
                        var items = npc.body.GetAllItemsThorough();
                        // items in containers are put in the array after their containers. i dont need to reverse it 
                        foreach (var item in items) {
                            NetObjectRegistry.NewGO(item.gameObject);
                        }
                    }
                } catch (Exception ex) {
                    log.error($"{ex.Message}\n{new StackTrace()}");
                }
            } else {
                Con._DEV_CHATSPY = false;
            }
            var locplrbody = ScavPlayer.LocalPlayer?.body;
            if (locplrbody != null) {
                ScavPlayer.LocalPlayer.SetUnchipped(WorldGeneration.unchipped);
            }


            try {
                SharedMain.CreatePlayerCharacters();
            } catch (Exception e) {
                Plugin.log.LogFatal($"FAILED CREATING CHARACTERS WHAT  {e} ");
            }

        } else { // we are not in multiplayer, enforce that runinbackground is false
            Application.runInBackground = Plugin.OG_RUNINGACKGROUND;
        }

        try {
            OnWorldgenFinish?.Invoke();
        } catch (Exception e) {
            log.error($"OnWorldgenFinish.Invoke()\n{e.ToString()}");
        }

        SharedMain.LocalWorldIsGenerating = false;

#if !RELEASE
        Con.RunCommand("spawn glowplant -7,5");
        Con.RunCommand("spawn glowplant -13,5");
        Con.RunCommand("spawn glowplant -18,5");
        Con.RunCommand("spawn glowplant -23,5");
        Con.RunCommand("spawn foliagebag -27,5");
        Con.RunCommand("spawn foliagebag -10,5");
        Con.RunCommand("spawn trader1 6,5");

        Con.RunCommand("spawn dryfoliage -35,5 1 20");
        Con.RunCommand("spawn medcrate -35,10 20 1");
#endif
    }








    [ServerReceiver((byte)NetmsgId.SERVER_WorldPlacePlayer)]
    static void ServerReceiver_WorldPlacePlayer(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (!Util.IsInWorld()) return;

#if !RELEASE
        Plugin.log.LogWarning($"ServerReceiver_WorldPlacePlayer for {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out bool plr_is_unchipped);


        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            if (!plr.ServerPlayerState.did_give_spawn_location) {
                log.devevent($"SERVER: Received WorldPlacePlayer from {plr}",plr.pos, Color.red, true);

                plr.ResetEntropy();

                plr.unchipped = plr_is_unchipped;
                if (plr.body != null) {
                    plr.SetUnchipped(plr_is_unchipped);
                }

                plr.ServerPlayerState.did_give_spawn_location = true;
                plr.StartCoroutine(ServerMain.GiveSpawnLocationToPlayer(clientId));

                WorldChunkSync.singleton.timer_TilemapSync += 0.05f;
                WorldChunkSync.singleton.timer_TilemapFluidSync += 0.05f;
            } else if (plr.body != null && plr.netBody != null && Util.IsWorldGenerated() && Body_PlaceBody_MultiplayerPatch.has_spawn_location) {
                // rejoin asks again. the flag is already set, so the first handler would have dropped it.
                log.l($"SERVER: spawn already handed out, reminding {plr.netBody} at {plr.netBody.transform.position}");
                plr.netBody.Server_RemindPlayersCurrentState();
            }
        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_FinishedWorldgen)]
    static void ServerReceiver_FinishedWorldgen(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        KrokV5Optimization.FixRejoinBaseline.Mark(clientId, out bool queueBaseline);
        try {
            if (!Util.IsInWorld()) return;

    #if !RELEASE
            Plugin.log.LogWarning($"ServerReceiver_FinishedWorldgen for {DebugHelp.GetPlayerFullDebugString(clientId)} ");
    #endif

            reader.Get(out bool plr_is_unchipped);


            if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && !plr.ServerPlayerState.finished_worldgen) {
                log.devevent($"SERVER: Received FinishedWorldgen from {plr}", plr.pos, Color.red, true);

                plr.ResetEntropy();

                plr.unchipped = plr_is_unchipped;
                if (plr.body != null) {
                    plr.SetUnchipped(plr_is_unchipped) ;
                }

                plr.ServerPlayerState.finished_worldgen = true;
                if (!plr.ServerPlayerState.did_give_spawn_location) {
                    plr.ServerPlayerState.did_give_spawn_location = true;
                    plr.StartCoroutine(ServerMain.GiveSpawnLocationToPlayer(clientId));
                }

                WorldChunkSync.singleton.timer_TilemapSync = 10;
                WorldChunkSync.singleton.timer_TilemapFluidSync = 10;

                if (plr.LateJoin) {
                    foreach (LayerModifier layerModifier in LayerModifier.availableModifiers) {
                        if (layerModifier.active) {
                            Multiplayer.Server_SendSimpleMessageToOneClient((byte)NetmsgId.CLIENT_Announce_LayerModifier, plr.playerId, (knetid)layerModifier.modifierIndex, true);
                        }
                    }
                }

                // and sync nearest items right now
                foreach (var item in NetObjectRegistry.GatherClosestObjectsToSync(plr.pos,plr.body,64)) {
                    NetObjectRegistry.SafeNewGO(item);
                }
                if (plr.body != null && plr.body.TryGetNetBody(out NetBody crafted))
                    PlayerCheckpoint.SendCrafted(crafted);
                // the client builds each trader with a fresh roll. this is the host list, reputation, and allotment.
                TraderSync.SendTraderState(clientId);
            } else if (ScavPlayer.TryGetPlayerFromClientId(clientId, out plr)) {
                TraderSync.SendTraderState(clientId);
            }
        } finally {
            KrokV5Optimization.FixRejoinBaseline.After(clientId, queueBaseline);
        }
    }



}

// stock Clear uses FindObjectsOfType, which skips inactive objects.
// chunk sleep turns geysers and hydreeds off, so they were still there on the next layer.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Clear))]
static class WorldGeneration_Clear_InactiveBuildings {
    static void Prefix() {
        BuildingEntity[] buildings = UnityEngine.Object.FindObjectsOfType<BuildingEntity>(true);
        for (int i = 0; i < buildings.Length; i++) {
            BuildingEntity building = buildings[i];
            if (building == null || building.gameObject.activeSelf)
                continue;
            UnityEngine.Object.Destroy(building.gameObject);
        }
    }
}



























