using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using static WorldGeneration;

namespace CasualtiesMultiplayerAPI;







public static class GamemodeManager {

    static GamemodeBase gamemode = null;

    public static bool HasGamemode() {
        return gamemode != null;
    }
    public static GamemodeBase GetGamemode() {
        return gamemode;
    }
    public static void DeleteGamemode() {
        if (HasGamemode()) 
            GameObject.Destroy(gamemode.gameObject);    
    }

    public static Type[] GetAllAvailableGamemodes() {
        return AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypesSafely()).Where(myType => !myType.IsAbstract && myType.IsSubclassOf(typeof(GamemodeBase))).ToArray();
    }

    public static T SetGamemode<T>() where T : GamemodeBase {
        return (T)SetGamemode(typeof(T));
    }
    public static GamemodeBase SetGamemode(Type gamemodetype) {
        DeleteGamemode();

        if (!gamemodetype.IsSubclassOf(typeof(GamemodeBase))) {
            log.error($"{gamemodetype}  IS NOT A GAMEMODE");
            return null;
        }

        if (!Multiplayer.IsNetworkActiveAndIsServer()) {
            log.error("GAMEMODE CAN BE SET ONLY ON SERVER");
            return null;
        }

        var go = new GameObject("KROKMP_GAMEMODE_" + gamemodetype.Name);
        gamemode = (GamemodeBase)go.AddComponent(gamemodetype);
        GameObject.DontDestroyOnLoad(go);
        return gamemode;
    }
}


public abstract class GamemodeBase : MonoBehaviour {
    public virtual void Init(string[] args) {

    }


    protected virtual void Update() {
        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            GamemodeManager.DeleteGamemode();
        }
    }
}


public class BattleRoyale : GamemodeBase {

    public static WorldGeneration world => WorldGeneration.world;
    public static uint width => world.width;
    public static uint height => world.height;
    public static uint chunkWidth => world.chunkWidth;
    public static uint chunkHeight => world.chunkHeight;
    public static uint halfWidth => world.halfWidth;
    public static uint halfHeight => world.halfHeight;

    public static int happening_layer = 0;

    public override void Init(string[] args) {

        var layer = 0;
        if (args.Length > 0) {
            layer = (int)Con.con.ParseFloat(args[0]);
        }
        log.l($"Battleroyale Layer: {layer}");
        
        BattleRoyale.happening_layer = layer;

    }

    void Awake() {
        SetMainMenuRules();
        //Multiplayer.rules.DServerMinPlrsToStart = (ushort)Mathf.Max(Multiplayer.rules.DServerMinPlrsToStart,6);
        Multiplayer.rules.AutoContinue = true;
        Multiplayer.rules.SelfharmWitnessMoodDebuff = -10;
        Multiplayer.rules.PVPMoodDebuff = -8;
        Multiplayer.rules.LayerFinishPlrPercent = 101;
        Multiplayer.rules.LateJoinAllowed = true;
        Multiplayer.rules.LateJoinSpectate = true;
        Multiplayer.rules.SavePlayerInventory = false;
        WorldMP.SetRadlinePlayerPrefs(false);
    }

    void Start() {

        WorldMP.OnWorldgenFinish += OnWorldgenFinish;
        NetBody.OnPlayerDeath += OnPlayerDeath;
        ScavPlayer.OnPlayerJoined += OnPlayerJoined;
        ScavPlayer.OnPlayerLeft += OnPlayerLeft;
        SceneManager.sceneLoaded += OnSceneLoaded;


        InvokeRepeating("SUpdate", 1f, 1f);
        InvokeRepeating("SlowUpdate", 1f, 10f);
        InvokeRepeating("VerySlowUpdate", 10f, 30f);
        
        log.l("Battleroyal gamemode activated");
        Chat.Server_ChatAnnouncement("Gamemode set to battle royale");
    }

    public static void DistributeEntities_But_for_items_and_load_ammo(GameObject basObj, float minPerChunk, float maxPerChunk, float spawnYOffset = 0f, float randomRotation = 0f, float spawnYOffsetDeviation = 0f, bool spawnInGround = false, PlaceCheckDelegate checkFunc = null,  Vector2 dir = default(Vector2)) {
        if (dir == Vector2.zero) {
            dir = Vector2.down;
        }

        float num = (float)(chunkWidth * chunkHeight) * UnityEngine.Random.Range(minPerChunk, maxPerChunk);
        for (int i = 0; (float)i < num; i++) {
            Vector2 vector = new Vector2(UnityEngine.Random.Range(0L - (long)halfWidth, halfWidth), UnityEngine.Random.Range(0L - (long)halfHeight,  ((float)halfHeight)));
            if (!(!Physics2D.OverlapPoint(vector, LayerMask.GetMask("Ground")) || spawnInGround)) {
                continue;
            }

            RaycastHit2D raycastHit2D = Physics2D.Raycast(vector, dir, CHUNKSIZE, LayerMask.GetMask("Ground"));
            if (!raycastHit2D || !(Math.Abs(raycastHit2D.point.x) < (float)halfWidth - 1f) || !(Math.Abs(raycastHit2D.point.y) < (float)halfHeight - 1f) || (checkFunc != null && !checkFunc(world.WorldToBlockPos(raycastHit2D.point - Vector2.up * 0.5f)))) {
                continue;
            }

            GameObject gameObject = UnityEngine.Object.Instantiate(basObj, raycastHit2D.point - dir * UnityEngine.Random.Range(spawnYOffset - spawnYOffsetDeviation, spawnYOffset + spawnYOffsetDeviation), Quaternion.Euler(0f, 0f, basObj.transform.eulerAngles.z + UnityEngine.Random.Range(0f - randomRotation, randomRotation)));
            if (gameObject.TryGetComponent(out GunScript gun)) {
                
            }
            if (gameObject.TryGetComponent(out AmmoScript ammo)) {
                ammo.rounds = UnityEngine.Random.Range(0,ammo.maxRounds);
            }

        }
    }



    public static void SpawnWeapons() {
        world.generatingWorld = true;
        try {
            var m = world.lootRarityMultiplier;

            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("shotgun"), 0.3f * m, 0.4f * m, 0.1f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("rifle"), 0.3f * m, 0.4f * m, 1, 360, 0f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("pistol"), 0.3f * m, 0.4f * m, 0f, 360, 0f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("machete"), 0.6f * m, 0.7f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("sledgehammer"), 1f * m, 1.1f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("bandage"), 1f * m, 1.1f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("combatpen"), 1f * m, 1.1f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("hoodie"), 0.1f * m, 0.2f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("riflemagazine"), 0.5f * m, 0.6f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("smallmagazine"), 0.7f * m, 0.9f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("boxof12gauge"), 0.4f * m, 0.8f * m, 0f, 360, 0.1f, false);
            DistributeEntities_But_for_items_and_load_ammo((GameObject)Resources.Load("scaffoldingpack"), 0.2f * m, 0.22f * m, 0f, 360, 0.1f, false);

        }
        catch (Exception e) {
            log.error(e);
        }
        world.generatingWorld = false;
    }


    void OnWorldgenFinish() {
        ServerMain.Server_AnnounceAlert($"Waiting for players to load...", true);

        RadiationLine.line.Deactivate();

        StartCoroutine(GameStartSequence());
    }
    void OnPlayerDeath(ScavPlayer plr) {
        ServerMain._ded_server_switch_counter = -20;

        if (Util.IsWorldGenerated() && plr.levelPlayTime > 3) {
            var lviing = ScavPlayer.AllLivingPlayers;
            if (lviing.Count == 1) {
                var winner = lviing[0];

                Chat.Server_ChatAnnouncement($"Winner Winner Chicken Dinner! {winner.playerName}");
                

                var msg = $"{winner.playerName} won!!! congrats !!!";
                ServerMain.Server_AnnounceAlert(msg, false);

                Chat.Server_ChatAnnouncement(msg);
            } else {
                ServerMain.Server_AnnounceAlert($"{plr.playerName} died! ({ScavPlayer.AllLivingPlayers.Count}/{ScavPlayer.BodyToPlayerDict.Count})", false);
            }

        }

    }
    void OnPlayerJoined(ScavPlayer plr) {
        
    }
    void OnPlayerLeft(ScavPlayer plr) {

    }
    public static void OnSceneLoaded(Scene a, LoadSceneMode b) {

        var is_world = a.name == "SampleScene";

        if (is_world) {

            Util.CallLambdaWhen(() => { return world != null; },
                () => {
                    world.biomeDepth = happening_layer;
                }
            );

        }
    }


    void SetMainMenuRules() {
        Multiplayer.rules.PVP = false;
        Multiplayer.rules.ScatterPunishDistance = 64999;
    }

    protected override void Update() {
        base.Update();


        if (!Util.IsInWorld()) {
            SetMainMenuRules();
            GAME_ON = false;
        } else {
            if (!Net.IsRunning) {
                Destroy(this);
            }
        }
        // speed up the radline
        if (Util.IsWorldGenerated() && RadiationLine.line.active) {
            RadiationLine_Update_MultiplayerPatch.timeGone += Time.deltaTime;
        }
    }

    void SUpdate() {
        if (GAME_ON && Net.IsRunning) {

            var lviing = ScavPlayer.AllLivingPlayers;


            if (
                Multiplayer.rules.AutoExitWhenAllDied && 
                ServerMain._ded_server_switch_counter > 10 &&
                lviing.Count <= 1
            ) {
                if (lviing.Count == 1) {
                    Chat.Server_ChatAnnouncement($"Winner Winner Chicken Dinner! {lviing[0].playerName}");
                }

                PlayerCamera.main.ToMainMenu();
            }

            if (ScavPlayer.AllLivingPlayers.Any(x => x.netBody.chip_punishment_protocol)) {
                AnnounceDirectionsForAll();
            }
            

        }
    }
    void SlowUpdate() {
        if (GAME_ON && Net.IsRunning) {

            Multiplayer.ApplyGameRules();

            var required_dist  = (KM.RemapClamped(RadiationLine_Update_MultiplayerPatch.timeGone, RadiationLine_Update_MultiplayerPatch.timeGone_limit - 200, 0, 64, 1024));

            foreach (var plr in ScavPlayer.AllLivingPlayers)       {

                var count_plrs_in_range = ScavPlayer.AllLivingPlayers.Count;

                var required_count_plrs_in_range = 0f;
                var max_recorded_dist = 0f;

                if (!plr.IsAlive() || plr.levelPlayTime < 20) {
                } else {
                    if (count_plrs_in_range <= 1) {
                    } else {
                        var plrs = new List<ScavPlayer>(ScavPlayer.AllLivingPlayers);
                        plrs.RemoveAll(x => ( x.levelPlayTime < 20 ));
                        if (Net.IsServer) {
                            plrs.RemoveAll( x => ( !x.ServerPlayerState.is_loaded_in ) );
                        }
                        
                        if (plrs.Count <= 1) {
                        } else {
                            required_count_plrs_in_range = Math.Max(1, (int)Mathf.Ceil(((float)plrs.Count) * 0.5f));

                            var presqr = Util.MetersToTiles(Multiplayer.rules.ScatterPunishDistance);
                            presqr *= presqr;
                            var pos = plr.body.transform.position;
                            count_plrs_in_range = 0;
                            foreach (var x in ScavPlayer.AllLivingPlayers) {
                                var d = KM.DistanceSqr(x.body.transform.position, pos);
                                if (d < presqr) {
                                    count_plrs_in_range++;
                                    if (d > max_recorded_dist) {
                                        max_recorded_dist = d;
                                    }
                                }
                            }
                            max_recorded_dist = Mathf.Sqrt(max_recorded_dist);

                            if (count_plrs_in_range < required_count_plrs_in_range) {
                                plr.netBody.chip_punishment_protocol = true;
                                plr.body.radiationSickness += 0.1f;

                                var aaaaaa = GetDirections(plr, 999999, out var cb);
                                if (aaaaaa != null)
                                    plr.Server_DoAlertSingle($"You're outside play area!\n{cb.bodyName} is on your {aaaaaa}", false);
                                else
                                    plr.Server_DoAlertSingle($"You're outside play area!", false);

                            }
                        }
                    }
                }

            }

        }
    }

    public void AnnounceDirectionsForAll() {
        foreach (var item in ScavPlayer.AllLivingPlayers) {
            var aaaaaa = GetDirections(item, 70, out var cb);
            if (aaaaaa != null)
                item.Server_DoAlertSingle($"{cb.bodyName} is on your {aaaaaa}", true);
        }
    }

    public string GetDirections(ScavPlayer item, float maxdist, out NetBody cbaaaa) {
        
        var plrpos = item.pos;

        var cb = ScavPlayer.GetNearestConsciousPlayerToThisBody(item.netBody);
        cbaaaa = cb.Item1;

        // player is too far from everyone, give them a hint
        if (cb.Item1 != null && cb.Item2 > maxdist * maxdist) {
            var norm = KM.NormalTo(plrpos, cb.Item1.pos);

            var y = "";
            if (norm.y > 0.5f) {
                y = "Top";
            }
            if (norm.y < -0.5f) {
                y = "Bottom";
            }

            float min = 0;
            if (!String.IsNullOrEmpty(y)) {
                min = 0.5f;
            }

            var x = "";
            if (norm.x > min) {
                x = "Right";
            } else if (norm.x < -min) {
                x = "Left";
            }


            return $"{y} {x}";
        }
        return null;
    }

    void VerySlowUpdate() {
        if (GAME_ON && Net.IsRunning) {
            AnnounceDirectionsForAll();
        }
    }

    bool GAME_ON = false;
    private IEnumerator GameStartSequence() {
        Con.RunCommand("godmode 1");

        while (true) {
            var lviing = ScavPlayer.AllLivingPlayers;

            if (lviing.All(x => ( x.levelPlayTime > 20))) {
                break;
            }
            if (!Util.IsInWorld()) yield break;

            yield return new WaitForSecondsRealtime(1f); 
        }

        yield return new WaitForSecondsRealtime(5f);
        if (!Util.IsInWorld()) yield break;


        yield return new WaitForSecondsRealtime(1f);
        if (!Util.IsInWorld()) yield break;

        var plr_positions = new Dictionary<ScavPlayer, Vector2>();

        ServerMain.Server_AnnounceAlert($"...Assigning spawn locations...", true, false);

        var plrs = new List<ScavPlayer>(ScavPlayer.AllLivingPlayers);
        foreach (var item in plrs) {
            Util.Body_DropAllItems(item.body);

            var pos = new Vector2(UnityEngine.Random.Range(-500, 500), UnityEngine.Random.Range(-10, 480));
            {
                var lastdist = float.MinValue;
                for (int i = 0; i < 40; i++) {
                    var newpos = new Vector2(UnityEngine.Random.Range(-500, 500), UnityEngine.Random.Range(-10 - i * 12, 480));

                    if (Physics2D.OverlapCircle(pos, 2, LayerMask.GetMask("Ground"))) {
                        continue;
                    } else {
                        var inWater = FluidManager.main.WaterInfo(world.WorldToBlockPos(pos)).Item1 > 0f;
                        if (inWater) {
                            continue;
                        }
                    }
                    var cb = NetBody.GetNearestBody(newpos, true, true);
                    if (cb.Item2 > lastdist) {
                        lastdist = cb.Item2;
                        pos = newpos;
                        // find ground
                        for (int e = 0; e < 100; e++) {
                            var nnn = pos - Vector2.up;
                            if (Physics2D.OverlapCircle(pos, 1, LayerMask.GetMask("Ground"))) {
                                break;
                            }
                            pos = nnn;
                        }
                    }
                }
            }

            item.body.transform.position = pos;
            WorldChunkSync.Server_CheckPlrPosChunk(item);
            Util.DelayCallLambda(3, () => {
                WorldChunkSync.singleton.timer_TilemapSync = 10;
                item.netBody.StopPiggyback();
                item.body.transform.position = pos;
                item.Server_RemindPlayersCurrentState(false, true);
            });
            plr_positions[item] = pos;


            item.body.skills.AddExp(0, 1000);
            item.body.skills.AddExp(1, 1000);
            item.body.skills.AddExp(2, 1000);

            item.body.ResetHealth();
            yield return new WaitForSecondsRealtime(0.01f);
        }
        WorldChunkSync.singleton.timer_TilemapFluidSync = 10;
        WorldChunkSync.singleton.timer_TilemapSync = 10;

        yield return new WaitForSecondsRealtime(4f);
        if (!Util.IsInWorld()) yield break;

        ServerMain.Server_AnnounceAlert($"5", true, false);
        yield return new WaitForSecondsRealtime(1f);
        Chat.Server_ChatAnnouncement("Welcome to fortnite.");

        ServerMain.Server_AnnounceAlert($"4", true, false);

        Multiplayer.rules.ShowPlayerDirections = false;
        Multiplayer.ApplyGameRules();

        yield return new WaitForSecondsRealtime(1f);
        ServerMain.Server_AnnounceAlert($"3", true, false);
        yield return new WaitForSecondsRealtime(1f);
        WorldChunkSync.singleton.timer_TilemapFluidSync = 10;
        WorldChunkSync.singleton.timer_TilemapSync = 10;
        ServerMain.Server_AnnounceAlert($"2", true,false);
        RadiationLine.line.Activate();
        yield return new WaitForSecondsRealtime(1f);
        ServerMain.Server_AnnounceAlert($"1", true,false);
        yield return null;
        log.l("battle royale: spawning weapons and items");
        SpawnWeapons();
        yield return new WaitForSecondsRealtime(1f);
        WorldChunkSync.singleton.timer_TilemapFluidSync = 10;
        WorldChunkSync.singleton.timer_TilemapSync = 10;
        foreach (var item in plr_positions) {
            try {
                item.Key.body.Body_DropAllItems();

                item.Key.body.transform.position = item.Value;
                item.Key.Server_RemindPlayersCurrentState(false, true);
            }
            catch (Exception e) {
                log.error("BattleRoyale, final spawn sequence ERROR:" +  e.ToString());
            }
        }

        ServerMain.Server_AnnounceAlert($"!!! BATTLE ROYALE STARTS NOW !!!\nBECOME THE LAST SURVIVOR", true);
        yield return new WaitForSecondsRealtime(1f);
        WorldChunkSync.singleton.timer_TilemapFluidSync = 10;
        WorldChunkSync.singleton.timer_TilemapSync = 10;

        Con.RunCommand("godmode 0");
        Con.RunCommand("give @a scaffoldingpack");
        GAME_ON = true;
        Multiplayer.rules.PVP = true;
        Multiplayer.ApplyGameRules();




    }


}











