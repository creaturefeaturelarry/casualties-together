using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.EnterpriseServices;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;


public struct PlayerSavedState {
    public bool unchipped;
    public int savedonlayer;
    public Vector2 position;

    public Color24 plrcolor;

    public CharacterHealthPainkillerStateSyncPacket painkiller;
    public CharacterHealthStateSyncPacket health;

    public List<int> RecipesHasCrafterBefore = null;
    public Dictionary<string, string> MindMemory = null;

    public GameObject invholder;
    public Item[] inventory;

    public PlayerSavedState(Body body) {
        position = body.transform.position;
        savedonlayer = WorldGeneration.world.biomeDepth;

        health = new CharacterHealthStateSyncPacket();
        health.Write(body);
        if (body.TryGetComponent(out Painkillers pnkkkkdksksd)) {
            painkiller = new CharacterHealthPainkillerStateSyncPacket(body);
        }

        if (body.TryGetNetBody(out var nb)) {
            unchipped = nb.unchipped;
            if (nb.IsPlayer) {
                RecipesHasCrafterBefore = new (nb.RecipesHasCrafterBefore);
                MindMemory = new (nb.MindMemory);
                plrcolor = nb.plr.playerColor;
            }
        }



        if (Multiplayer.rules.SavePlayerInventory) {
            invholder = new GameObject($"SAVESTATE_{nb}");
            inventory = new Item[8];


            var l = new List<Item>(8);
            for (int i = 0; i < body.slots.Length; i++) {
                var t = body.GetItem(i);
                if (t != null) {
                    l.Add(t);
                    inventory[i] = t;
                }
            }
            l.AddRange(body.GetAllWearables());

            foreach (var item in l) {
                item.transform.SetParent(invholder.transform);
            }

            invholder.SetActive(false);
        }

    }

    public void Apply(Body body) {
        // Is on the same layer
        if (Multiplayer.rules.SavePlayerPosition && savedonlayer == WorldGeneration.world.biomeDepth) {
            if (body.TryGetNetBody(out var nb)) {
                if (nb.IsPlayer) {
                    nb.plr.ServerPlayerState.did_give_spawn_location_from_a_save = true;
                }
            }
            body.transform.position = position;
        }
        if (Multiplayer.rules.SavePlayerState) {
            health.Apply(body);
            painkiller.Apply(body);
            if (body.TryGetNetBody(out var nb)) {
                nb.SetUnchipped(unchipped);
                if (nb.IsPlayer) {
                    if (MindMemory != null) {
                    	nb.MindMemory = MindMemory;	
                    }
                    if (RecipesHasCrafterBefore != null) {
                        nb.RecipesHasCrafterBefore = RecipesHasCrafterBefore;
                        PlayerCheckpoint.SendCrafted(nb);
                    }
                    nb.plr.playerColor = plrcolor;
                }
            }
        }
        if (Multiplayer.rules.SavePlayerInventory && invholder != null && inventory != null) {
            invholder.SetActive(true);

            for (int i = 0; i < inventory.Length; i++) {
                var item = inventory[i];
                if (item != null) {
                    item.transform.parent = null;
                    body.PickUpItem(item,i,true);
                }
            }

            var l = new List<Item>(8);
            foreach (Transform t in invholder.transform) {
                if (t != null && t.TryGetComponent(out Item item)) {
                    l.Add(item);
                }
            }

            foreach (var item in l) { // wear the wearables
                body.AutoPickUpItem(item);
            }

        }
        // because the state gets delete instantly just delete the thing
        if (invholder != null) {
            GameObject.Destroy(invholder);
        }
    }

    // the holder still owns the items. used when a later layer loads the file instead.
    public void Discard() {
        if (invholder != null) {
            GameObject.Destroy(invholder);
            invholder = null;
        }
    }

}






public struct LastBeforeGenerationState : INetSerializeByMemcpy {
    public MultiplayerGameRules rules;

    public UnityEngine.Random.State randomstate;// its 16 bytes
    public byte biomeOverride;
    public bool radlinedisable;
    public bool unchipped;
    public byte biomeDepth;
    public float lootRarityMultiplier;
    public float trapRarityMultiplier;
    public int totalTraveled;

    public bool censorMood;
    public LastBeforeGenerationState() {

        this.rules = Multiplayer.rules;

        this.randomstate = UnityEngine.Random.state;

        biomeOverride = (byte)WorldGeneration.world.biomeOverride;
        biomeDepth = (byte)WorldGeneration.world.biomeDepth;
        lootRarityMultiplier = WorldGeneration.world.lootRarityMultiplier;
        trapRarityMultiplier = WorldGeneration.world.trapRarityMultiplier;
        totalTraveled = WorldGeneration.world.totalTraveled;

        censorMood = Body.censorMood;

        this.unchipped = WorldGeneration.unchipped;
    }
    // CALL ONLY RIGHT BEFORE GENERATION
    public void Apply() {


        UnityEngine.Random.state = this.randomstate;

        Multiplayer.rules = this.rules;

        WorldGeneration.world.biomeOverride = (WorldGeneration.OverrideSceneType)biomeOverride;
        WorldGeneration.world.biomeDepth = biomeDepth;
        WorldGeneration.world.lootRarityMultiplier = lootRarityMultiplier;
        WorldGeneration.world.trapRarityMultiplier = trapRarityMultiplier;
        WorldGeneration.world.totalTraveled = totalTraveled;

        Body.censorMood = censorMood;

        if (!Multiplayer.rules.UnchippedIsIndividual) {
            WorldGeneration.world.SetUnchipped(this.unchipped);
        }

    }

    public static int GetByteSize() {
        return System.Runtime.InteropServices.Marshal.SizeOf(typeof(LastBeforeGenerationState));
    }
}

public struct SerializableRandomState : INetSerializeByMemcpy { // its 16 bytes
    public UnityEngine.Random.State State;
}




abstract public class BaseComponentTracker<T> : MonoBehaviour where T : MonoBehaviour {
    public T og { get; private set; }
    public SyncInfoGameObjectTracker tracker => GetComponent<SyncInfoGameObjectTracker>();

    protected abstract void TrackerAwake();
    void Awake() { 
        og = GetComponent<T>();
        TrackerAwake();
    }

}


[EntityFlags(EntityFlags.UpdateOnClient)]
public class ServerWorldStateSingleton : SingletonEntityLogic {

    public SyncVar<Vector2_4byte_512> shuttleDoorPosition;

    public SyncVar<float> timeScale;


    public SyncVar<bool> server_is_generating_world;
    public SyncVar<bool> radlineactive;
    public SyncVar<float> radlinestate;
    public SyncVar<float> layerTimeSpent;
    public SyncVar<float> earthquakeDelay;
    public SyncVar<float> earthquakeTime;
    public SyncVar<bool> has_surface_bg;
    public SyncVar<Color32> surface_color;
    public SyncVar<float> rainintensity;
    public SyncVar<float> ambientLightIntensity;


    public void Server_Serialize() {
        server_is_generating_world.Value = SharedMain.LocalWorldIsGenerating;

        {
            if (RadiationLine.line != null) {
                radlineactive.Value = (RadiationLine.line.active);
                radlinestate.Value = (RadiationLine_Update_MultiplayerPatch.timeGone);
            } else {
                radlineactive.Value = false;
            }
            if (WorldGeneration.world != null) {
                //server_is_in_world = true;
                layerTimeSpent.Value = (WorldGeneration.world.layerTimeSpent);
                earthquakeDelay.Value = (WorldGeneration.world.earthquakeDelay);
                earthquakeTime.Value = (WorldGeneration.world.earthquakeTime);

                // wha why am i putting this here?
                has_surface_bg.Value = WorldMP.HasSkyBackground(out var skyinfo);
                if (has_surface_bg) {
                    surface_color.Value = (skyinfo.Item1);
                    rainintensity.Value = (skyinfo.Item2);
                }

                if (Time.timeScale.IsFinite())
                    timeScale.Value = Time.timeScale;
            } else {
                //server_is_in_world = false;
                layerTimeSpent.Value = 0;
                has_surface_bg.Value = false;
                timeScale.Value = 1;
            }

        }

    }
    public void Deserialize() {

        if (WorldGeneration.world != null) {
            if (has_surface_bg.Value != WorldMP.HasSkyBackground()) {
                if (has_surface_bg.Value) {
                    WorldMP.CreateSkyBackground(surface_color.Value, rainintensity.Value);
                } else {
                    WorldMP.RemoveSkyBackground();
                }
            }

            if (earthquakeDelay.Value < -1) { // when host is asleep, without this, it causes the clients restart earthquake timer while host actually doesnt have any earthquake happennnin
                WorldGeneration.world.earthquakeDelay = 999999;
            } else {
                WorldGeneration.world.earthquakeDelay = earthquakeDelay.Value;
            }

            WorldGeneration.world.earthquakeTime = earthquakeTime.Value;
            WorldGeneration.world.layerTimeSpent = layerTimeSpent.Value;

        }

        if (RadiationLine.line != null && RadiationLine.line.active != radlineactive.Value) {
            if (radlineactive) {
                RadiationLine.line.Activate();
            } else {
                RadiationLine.line.Deactivate();
            }
        }

        if (Util.IsInWorld() && timeScale.Value.IsFinite()) {
            var diff = Time.timeScale - timeScale.Value;
            if (diff >= 1f) {
                
            }
            Time.timeScale = timeScale.Value;
        }


        RadiationLine_Update_MultiplayerPatch.timeGone = radlinestate.Value;


    }





    protected override void OnConstructed() {
        ambientLightIntensity.Value = 0.12f;
        base.OnConstructed();
    }
    
    public ServerWorldStateSingleton(EntityParams entityParams) : base(entityParams) {
        shuttleDoorPosition.Value = new Vector2(0,500);
        timeScale.Value = 1;
    }

    protected override void Update() {
        if (IsServer) {
            Server_Serialize();
        } else if (IsClient) {
            Deserialize();
        }
    }
    protected override void VisualUpdate() {

    }
}




public class ServerMain : MonoBehaviour {

    public static Dictionary<string,PlayerSavedState> Server_LastPlayerStates = new();

    public static float AVERAGE_PING = 0;

    public static int CURRENT_TPS = 0;
    public static int CURRENT_FPS = 0;

    public static byte AVG_CONNECTION_QUALITY = 0;





    public struct StartGameAnnouncementPacket : INetSerializeByMemcpy {
        public WorldMP.RunPrefs prefs;
        public UnityEngine.Random.State randomstate;
        public MultiplayerGameRules rules;
    }


    // MUST BE CALLED MANUALLY OR THE CLIENT WOULDNT KNOW WHATS HAPPENING
    internal static StartGameAnnouncementPacket LastStartGameAnnouncementPacket;
    public static void Server_Announce_GAME_START() {

        var runpref = (new WorldMP.RunPrefs()).ReadPrefs();

        var writer = Net.CreateWriter(NetmsgId.StartGameAnnouncement);

        LastStartGameAnnouncementPacket = new StartGameAnnouncementPacket {
            prefs = runpref,
            randomstate = UnityEngine.Random.state,
            rules = Multiplayer.rules,
        };
        writer.Put(LastStartGameAnnouncementPacket);
        writer.Put(WorldMP.CompileRunSettings(), true);

        if (runpref.tutorial) {
            Chat.Server_ChatAnnouncement(Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:host_started_tutorial>"));
        } else {
            Chat.Server_ChatAnnouncement(Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:host_started>"));
        }

        Multiplayer.DoMultiplayerStatusMessageLog($"### STARTING GAME ###");

        //writer.Truncate();

        Net.Server_SendToClientsVeryReliable( writer, ServerMain.AllClientIdsExceptHost);

        // WRITER END;
        
    }

    public static void Server_SendMPLogMessage(in string msg, in bool is_error = false, IReadOnlyList<knetid> targets = null) {
        var writer = Net.CreateWriter(NetmsgId.CustomLogMessage);
        writer.Put(msg);
        writer.Put(is_error);
        Net.Server_SendToClients( DeliveryMethod.ReliableOrdered , writer, clientIds: targets ?? ServerMain.AllClientIds);
        // WRITER END;
    }

    public static void _Server_OnServerNameChange(IReadOnlyList<knetid> targets = null) {
         
        var writer = Net.CreateWriter(NetmsgId.ServerInfoUpdate);
        writer.Put(Net.MyServerInfo);
        Net.Server_SendToClients( DeliveryMethod.ReliableOrdered , writer, clientIds: targets ?? ServerMain.AllClientIdsExceptHost);
        // WRITER END;

        if (Net.IsRunningSteam) {
            KSteam.Server_UpdateLobbyData();
        }
    }

    public static void Server_InstantiateInstance(Vector2 pos, float rot, string name, IReadOnlyList<knetid> to_who) {

        var writer = Net.CreateWriter(NetmsgId.CLIENT_CreateInstance);

        writer.Put(pos);
        writer.Put(rot);
        writer.Put(name, true);

        //writer.Truncate();

        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: to_who);
        // WRITER END;
    }


    public static void Server_AnnounceAlert(in string msg, bool important, bool reliable = true, IReadOnlyList<knetid> targets = null) {
        if (Net.IsDedicatedServer) {
            Util.DoAlert(msg, important);
        }


        var writer = Net.CreateWriter(NetmsgId.CLIENT_AnnounceDoAlert);
        writer.Put(msg);
        writer.Put(important);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: targets ?? ServerMain.AllClientIds);
        // WRITER END;
    }

    public static bool ForceTalkerSayAndAnnounce(in SyncInfo si, in string msg, IReadOnlyList<knetid> targets = null) {

        if (Util.TryGetTalkerOnObject(si.go, out var foundtalker, out var _)) {
            Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, msg, true);

            if (targets == null) targets = ServerMain.AllClientIdsExceptHost;
            
            return TalkerSayAnnounce(si.syncId, 0, msg, targets);
        }

        return false;
    }
    public static bool ForceTalkerSayAndAnnounce(in NetBody pb, in string msg, IReadOnlyList<knetid> targets = null) {

        if (Util.TryGetTalkerOnObject(pb.gameObject, out var foundtalker, out var _)) {
            Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, msg, true);

            if (targets == null)  targets = ServerMain.AllClientIdsExceptHost;
            
            return TalkerSayAnnounce(pb.netId, 1, msg, targets);
        }

        return false;
    }
    public static bool TalkerSayAnnounce(in knetid id, in byte type, in string msg, IReadOnlyList<knetid> targets = null) {

        var writer = Net.CreateWriter(NetmsgId.CLIENT_force_talker_say);
        writer.Put(id);
        writer.Put(type);
        writer.Put(msg);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: targets ?? ServerMain.AllClientIds);
        // WRITER END;
            
        return true;
    }

    public static bool _DEV_ENABLE_HP_SYNC = true;


    public static GameObject CHARACTER_PREFAB = null;
    public static Body CHARACTER_PREFAB_BODY => CHARACTER_PREFAB.transform.GetChild(0).GetComponent<Body>();


    public static IReadOnlyList<knetid> AllClientIds => _real_AllClientIds;
    public static IReadOnlyList<knetid> AllClientIdsExceptHost => _real_AllClientIdsExceptHost;
    public static IReadOnlyList<ScavPlayer> AllPlayersExceptHost => _real_AllPlayersExceptHost;

    private static List<knetid> _real_AllClientIds = new List<knetid>();
    private static List<knetid> _real_AllClientIdsExceptHost = new List<knetid>();
    private static List<ScavPlayer> _real_AllPlayersExceptHost = new List<ScavPlayer>();



    public static IEnumerator GiveSpawnLocationToPlayer(knetid clientId) {
#if TESTING
        log.l($"GiveSpawnLocationToPlayer called  clientId:{clientId}");
#endif

        ScavPlayer plr;
        NetBody pb;

        while (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out plr, out pb) || !Util.IsWorldGenerated() || !Body_PlaceBody_MultiplayerPatch.has_spawn_location ) { // wait until the world is generated

            if (plr != null) {
                plr.ResetEntropy();
            }

            yield return null;
        }

        if (plr != null) {
            plr.ResetEntropy();
#if TESTING
            log.l($"GiveSpawnLocationToPlayer finished waiting for {plr}");
#endif

            if (!pb.body.alive && Multiplayer.rules.LateJoinSpectate) {
                // dont give them any spawn location, cuz they were moved to outside of the map
            } else {
                if (plr.ServerPlayerState.did_give_spawn_location_from_a_save) {
                    plr.ServerPlayerState.did_give_spawn_location_from_a_save = false;
                } else {
                    if (
                        !Multiplayer.rules.LayerFinishKeepXOffset // dont keep x offset
                        || WorldGeneration.world.totalTraveled <= 0  // if its the first layer all spawn in the pod
                        || plr.LateJoin // if late join it must pick random player to spawn on
                    ) {
                        LateSpawnLocation(pb);
                    } else {
                        pb.SetBodyPosition(Util.PlaceBody_FindSpawnLocation(plr.ServerPlayerState.layer_transition_x_offset));
                    }
                }
            }

            if (!pb.IsBodyLocal()) {
                log.l($"SERVER: Sending a spawn location for {pb} to {pb.transform.position} ");

                pb.body.SetVelocity(Vector2.zero);
                pb.Server_RemindPlayersCurrentState();
            } else {
#if !RELEASE
                log.warn("SERVER: LateSpawnLocation actually gets called for local body lol");
#endif
            }

            if (true) {
                plr.ServerPlayerState.SaveLocationSnapshot();
            }

            WorldChunkSync.singleton.timer_TilemapSync = 10;
        }
    }


    public static int GetAlivePlayerCountPercent(float scale) {
        var LivingPlayerCount = ScavPlayer.AllLivingPlayers.Count;
        if (LivingPlayerCount == 0)
            return 1;


        int result = Mathf.CeilToInt(((float)LivingPlayerCount) * scale);

        return Mathf.Clamp(result, 1, LivingPlayerCount);
    }
    public static int GetNumberOfPlayersRequiredToFinishLayer() {
        return GetAlivePlayerCountPercent(((float)Multiplayer.rules.LayerFinishPlrPercent) * 0.01f);
    }
    public static bool CheckIfEnoughPeopleAreAtLayerFinish() => CheckIfEnoughPeopleAreAtLayerFinish(out int current, out int required);
    public static bool CheckIfEnoughPeopleAreAtLayerFinish(out int current, out int required) {
        if (Multiplayer.rules.CheckIfLayerContinueIsDisabled()) {
            current = 0;
            required = 1000;
            return false;
        }
        return CheckIfCanFinishLayerCustomCondition((ScavPlayer plr) => { return (plr.pos.y < (float)(0L - (long)WorldGeneration.world.halfHeight) + 3.1f); }, out current, out required);
    }
    public static bool CheckIfShouldStartRadlineForStragglers(byte minimum = 1) {
        if (Multiplayer.rules.StragglerRadlinePercent < 100) {
            ServerMain.CheckIfEnoughPeopleAreAtLayerFinish(out int current, out int required);
            int required_for_radline = 0;
            if (ScavPlayer.AllLivingPlayers.Count > 1 && 
                required > 0 && 
                current != ScavPlayer.AllLivingPlayers.Count && 
                current >= minimum 
            ) {
                required_for_radline = GetAlivePlayerCountPercent(Multiplayer.rules.StragglerRadlinePercent * 0.01f);
                if (required_for_radline > 0 && current >= required_for_radline) {
                    return true;
                }
            }
        }
        return false;
    }
    public static bool CheckIfCanFinishLayerCustomCondition(Func<ScavPlayer, bool> func, out int current, out int required) {

        required = GetNumberOfPlayersRequiredToFinishLayer();

        current = 0;

        foreach (var item in ScavPlayer.AllLivingPlayers) {
            // OG: WorldGeneration.ContinueRun V5t4
            if ((!Net.IsServer || item.ServerPlayerState.is_loaded_in) && func(item)) {
                current++;

                if (current >= required) {
                    return true;
                }
            }
        }

        return false;
    }



    public static void LateSpawnLocation(NetBody b) {
        var pb = GetPlayerBodyHostOrAnyoneAlive(b.plr, b); 
        var shouldSpawnOnPlayer = Multiplayer.rules.SpawnOnPlayer && pb != null && pb.body.alive;

        if (shouldSpawnOnPlayer && IsInWorld(pb.pos)) {
#if TESTING
            log.warn($"LateSpawnLocation: {b}.SetBodyPosition({pb.pos});");
#endif
            b.SetBodyPosition(pb.pos);
        } else {
#if TESTING
            log.warn($"LateSpawnLocation: {b}.body.PlaceBody();");
#endif
            if (RadiationLine.line != null) {
                Body_PlaceBody_MultiplayerPatch.SpawnLocation.y = Mathf.Min(Body_PlaceBody_MultiplayerPatch.SpawnLocation.y, RadiationLine.line.transform.position.y);
            }

            b.transform.position = Body_PlaceBody_MultiplayerPatch.SpawnLocation;
        }
        // the load sat extras at the old spot. now that he has moved, put them back on him.
        if (b != null && b.plr != null)
            PlayerCheckpoint.ReclaimGear(b.plr.playerId);
    }
    public static bool IsInWorld(in Vector2 pos) {
        return Mathf.Abs(pos.x) < WorldChunkSync.WORLDSIZE*0.5f && Mathf.Abs(pos.y) < WorldChunkSync.WORLDSIZE * 0.5f;
    }

    public static NetBody GetPlayerBodyHostOrAnyoneAlive(ScavPlayer for_who = null, NetBody except = null) {
        NetBody pb = null;
        NetBody pb_team = null;
        if (!Net.IsDedicatedServer) {
            pb = ScavPlayer.GetLocalNetBodyNullable();
        }
        if (pb == null || !pb.body.conscious) {
            foreach (var item in ScavPlayer.BodyToPlayerDict) {
                if (item.Key.conscious && except != item.Value.netBody && for_who != item.Value && IsInWorld(item.Key.transform.position) && item.Value.ServerPlayerState.did_give_spawn_location && item.Value.ServerPlayerState.is_loaded_in ) {
                    if (item.Key == Util.GetLocalBody()) { // cuz its a default fallback
                        continue;
                    }
                    if (Multiplayer.rules.Teams && for_who != null) { 
                        if (item.Value.IsInSameTeamAs(for_who)) {
                            pb_team = item.Value.netBody;
                        }
                    }
                    return item.Value.netBody;
                }
            }
        }
        if (pb_team != null) {
            return pb_team;
        }
        return pb;
    }

    public static bool CheckIfEveryoneAliveIsActuallyConscious() {
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if ( item.Key.alive ) {
                if (!item.Key.conscious) {
                    return false;
                }
            }
        }
        return true;
    }





    public static List<knetid> GetListOfClientIdsExceptThis(NetBody exclude) {
        if (exclude.IsPlayer) {
            return GetListOfClientIdsExceptThis(exclude.plr.playerId);
        } else {
            return new List<knetid>(AllClientIds);
        }
    }

    public static List<knetid> GetListOfClientIdsExceptThis(knetid exclude) {
        var l = new List<knetid>();
        foreach (var item in ScavPlayer.ClientIdToPlayerDict.Values) {
            var plr = item.GetComponent<ScavPlayer>();
            if (plr != null && ( exclude != plr.playerId)) {
                l.Add(plr.playerId);
            }
        }
        return l;
    }
    public static List<knetid> GetListOfClientIdsExceptThisAndHost(knetid exclude) {
        var l = new List<knetid>(_real_AllClientIdsExceptHost);
        l.Remove(exclude);
        return l;
    }
    public static List<knetid> GetListOfClientIdsExceptThisAndHost(NetBody nb) {
        var l = new List<knetid>(_real_AllClientIdsExceptHost);
        if (nb.IsPlayer) {
            l.Remove(nb.plr.playerId);
        }
        return l;
    }


    public static bool CheckIfAnyoneIsMoving() {
        foreach (var plr in ScavPlayer.AllLivingPlayers) {
            if ( plr.body.conscious) {
                if ((plr.body.moveDir != Vector2.zero)) {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool CheckIfAnyoneFinishedLayer(out ScavPlayer who) {
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if ( item.Key.alive && item.Key.conscious) {
                
                if (DidHeFinishTheLayer(item.Key)) {
                    who = item.Value;
                    return true;
                }
            }
        }
        who = null;
        return false;
    }
    public static bool DidHeFinishTheLayer(Body body) {
        // OG: WorldGeneration.ContinueRun
        if ((body.transform.position.y < (float)(0L - (long)WorldGeneration.world.halfHeight) + 3.1f)) {
            return true;
        }
         
        return false;
    }
    public static bool CheckIfEveryoneIsSleeping() {
        bool everyonesleeping = false;
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if (item.Key.alive) {
                if (item.Key.consciousness > 20f) { // concsiousness is lost at 30,  but doing 20 to make sure   (suggestion by the "dawn" guy or whoever it is)
                    return false;
                } else {
                    everyonesleeping = true;
                }
            }
        }
        return everyonesleeping;
    }
    public static void ResetPlayersFinishedStatus() {

        foreach (var item in ScavPlayer.ClientIdToPlayerDict.Values) {
            item._tutorial_finished = false;
            item.finishedLayer = false;
        }
    }







    double tpscalc_lasttime = 0;
    int tpscalc_framecounter = 0;

    internal static void _UpdateSpecialPlayerLists() {
        _real_AllClientIdsExceptHost.Clear();
        _real_AllPlayersExceptHost.Clear();
        _real_AllClientIds.Clear();
        if (!Net.IsRunning) return;

        foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
            _real_AllClientIds.Add(plr.playerId);
            if (plr != null && !plr.IsHost) {
                _real_AllClientIdsExceptHost.Add(plr.playerId);
                _real_AllPlayersExceptHost.Add(plr);
            }
        }
    }

    private void FixedUpdate() {

        {
            tpscalc_framecounter += 1;

            var time = Time.realtimeSinceStartupAsDouble;
            if (time - tpscalc_lasttime >= 1) {
                tpscalc_lasttime = time;
                CURRENT_TPS = tpscalc_framecounter;
                tpscalc_framecounter = 0;
                if (Net.IsServer) {
                    ClientMain.SERVER_TPS = CURRENT_TPS;
                    if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.ServerPlayerState != null) {
                        ScavPlayer.LocalPlayer.ServerPlayerState.tps = CURRENT_TPS;
                    }
                }
            }
        }

        _UpdateSpecialPlayerLists();
        if (!Net.IsRunning) return;


        // for server, move the local body somewhere far offscreen so that it doesnt cause anything bad
        if ( Net.IsDedicatedServer && Util.TryGetLocalBody(out var locbody)) {
            (locbody).ResetHealth();
            Util.Body_DropAllItems(locbody);

            locbody.ForceStand();
            if (Util.IsWorldGenerated()) {
                locbody.transform.position = new Vector2(999999, 999999);
            }
            locbody.rb.gravityScale = 0;
            locbody.rb.velocity = Vector2.zero;
            locbody.rb.angularVelocity = 0;
        }


        if (!Util.IsInWorld() && Multiplayer.rules.AutoContinue) { // is in main menu and has the autocontinue rule 

            if (Net.IsServer) { // is server 

                if (ScavPlayer.ClientIdToPlayerDict.Count >= (Multiplayer.rules.AutoMinPlrsToStart)) { // there is at least minimum required players 

                    ServerMain._ded_server_switch_counter += Time.deltaTime;
                    if (ServerMain._ded_server_switch_counter > 20f) { // auto-start the run when someone has joined
                        ServerMain._ded_server_switch_counter = -100;

                        var prs = PreRunScript.instance;
                        if (prs) {

                            var msg = "autostarting_run";
                            Multiplayer.DoMultiplayerStatusMessageLog(Lang.GetEN(msg));
                            Chat.Server_ChatAnnouncement("<key:autostarting_run>");

                            prs.StartCoroutine(prs.WaitLoad());
                        } else {
                            Multiplayer.DoMultiplayerStatusMessageError($"Failed to autostart the run. Theres no PreRunScript???? what???");

                            ServerMain._ded_server_switch_counter = 10;
                        }
                    }
                } else {
                    ServerMain._ded_server_switch_counter = 0;
                }
            }

        }


        
        
    }


    double fpscalc_lasttime = 0;
    int fpscalc_framecounter = 0;

    public const float SyncFrequency = 0.09f;
    public const float SyncFrequencyRare = 5.0f;
    public const float SyncFrequencySecond = 1.0f;

    internal static float timer_UpdateSyncClientsToClients = 0;
    internal static float timer_RareUpdateSyncClients = 0;
    internal static float timer_SecondUpdateSyncClients = 1;
    void Update() {

        {
            fpscalc_framecounter += 1;

            var time = Time.realtimeSinceStartupAsDouble;
            if (time - fpscalc_lasttime >= 1) {
                fpscalc_lasttime = time;
                CURRENT_FPS = fpscalc_framecounter;
                fpscalc_framecounter = 0;
                if (Net.IsServer) {
                    ClientMain.SERVER_FPS = CURRENT_FPS;
                    if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.ServerPlayerState != null) {
                        ScavPlayer.LocalPlayer.ServerPlayerState.fps = CURRENT_FPS;
                    }
                }
            }
        }

        if (!Multiplayer.IsNetworkActiveAndIsServer()) return;
        
        timer_UpdateSyncClientsToClients += Time.unscaledDeltaTime;
        timer_RareUpdateSyncClients += Time.unscaledDeltaTime;
        timer_SecondUpdateSyncClients += Time.unscaledDeltaTime;

        if (timer_RareUpdateSyncClients > SyncFrequencyRare) {
            timer_RareUpdateSyncClients = 0;
            RareUpdateSyncClients();
        } else if (timer_SecondUpdateSyncClients > SyncFrequencySecond) {
            timer_SecondUpdateSyncClients = 0;
            SecondUpdateSyncClients();
        } else if (timer_UpdateSyncClientsToClients > SyncFrequency) {
            timer_UpdateSyncClientsToClients = 0;
            UpdateSyncClientsToClients();
        }

    }

    void LateUpdate() {
        if (!Net.IsRunning) return;

        HandleSleepUpdate();

        HandleDedicatedServerUpdate();

    }

    public static float _ded_server_switch_counter = 0;
    static void HandleDedicatedServerUpdate() {

        if (!Util.IsGeneratingWorld()) {
            ServerMain._ded_server_switch_counter += Time.unscaledDeltaTime;
        }

        if (Util.IsWorldGenerated()) {
            if (Net.IsDedicatedServer) {

                //  timer that switches the player every couple seconds
                // but also go to main menu if theres no one on the server long enough
                if (_ded_server_switch_counter > 10f) {
                    if (Multiplayer.rules.AutoContinue) {
                        if (
                            (Multiplayer.rules.AutoExitWhenAllLeft && ScavPlayer.ClientIdToPlayerDict.Count == 0)
                        ) { // if everyone left go to main menu
                            _ded_server_switch_counter = -30;

                            var msg = $"Auto-exiting to main menu because everyone left the server.";
                            Multiplayer.DoMultiplayerStatusMessageLog(msg);
                            Chat.Server_ChatAnnouncement(msg);

                            PlayerCamera.main.ToMainMenu();
                            return;
                        } else if (
                            (Multiplayer.rules.AutoExitWhenAllDied && ScavPlayer.AllLivingPlayers.Count == 0)
                        ) { // if everyone died go to main menu
                            _ded_server_switch_counter = -5;

                            var msg = $"Auto-exiting to main menu because everyone died.";
                            Multiplayer.DoMultiplayerStatusMessageLog(msg);
                            Chat.Server_ChatAnnouncement(msg);

                            PlayerCamera.main.ToMainMenu();
                            return;
                        }
                    }
                }
            }
        }

        

    }

    static void HandleSleepUpdate() {

        if (Multiplayer.rules.DisableSleep) {
            if (Util.IsInWoundView()) {
                WoundView.view.sleepTip.tipName = Lang.Get("sleep_is_disabled"); 
                WoundView.view.sleepTip.tipDesc = Lang.Get("sleep_disabled_desc");
            }
        } else {

            if (Multiplayer.rules.DisableTimeManipulation) {
                if (ServerMain.CheckIfEveryoneIsSleeping()) {
                    Time.timeScale = 25f; // i pray that settime gets after called sometime and sets its back to 1
                } else {
                    if (Time.timeScale == 25f) {
                        Time.timeScale = 1f;
                    }
                }
            } else {

                if (ServerMain.CheckIfEveryoneIsSleeping()) {
                    if (Net.IsServer) {
                        PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.UnconsciousFast);
                    }
                }
            }
        }
    }


    private void Start() {
        {
            LastStartGameAnnouncementPacket = new StartGameAnnouncementPacket {
                prefs = (new WorldMP.RunPrefs()).ReadPrefs(),
                randomstate = UnityEngine.Random.state,
                rules = Multiplayer.rules,
            };
        }

        InvokeRepeating(nameof(update10s), 10, 10);

        ScavPlayer.OnPlayerJoined += (ScavPlayer plr) => {
            _lastSentRunSettings = "";
        };

        ScavPlayer.OnPlayerLeft += (ScavPlayer plr) => {
            _real_AllClientIds.Remove(plr.playerId);
            _real_AllClientIdsExceptHost.Remove(plr.playerId);
            _real_AllPlayersExceptHost.Remove(plr);
        };

        Multiplayer.RegisterCustomServerReceiver("CustomCommand", RunClientCustomCommand);

        {
            var t = "plrdeath";
            AllDeathMessageKeys.AddRange(Lang.EN.Keys.Where(x => x == t || (x.TryTrimStart(t, out var trimmed))));
        }
    }
    static readonly List<string> AllDeathMessageKeys = new List<string>();

    void update10s() {
        if (Net.IsServer && Net.IsRunning) {
            _Server_OnServerNameChange();
            Net.MyServerInfo.SetValues();
            Net.CurrentServerInfo.SetValues();
        }
    }





    public static void OnPlayerSleep(ScavPlayer plr) {

        PlayerCamera.main.DoAlert(plr.playerName + Lang.Get("on_plr_sleep"));

    }

    public static string PickRandomDeathMessageLangKey(string t) {
        string key = AllDeathMessageKeys.Where(x => x == t || (x.TryTrimStart(t, out var trimmed) && byte.TryParse(trimmed, out var _))).PickRandom();

        return key;
    }
    public static string PickRandomDeathMessageTag(string t) {
        return $"<key:{PickRandomDeathMessageLangKey(t)}>";
    }
    static string GenerateDeathMessage(ScavPlayer plr) {

        // TODO multiple variations like in terraria or minecraft

        var nb = plr.netBody;
        var body = nb.body;

        StringBuilder deathMessage = new StringBuilder($"<plrc:{plr.playerId}> <color={Chat.DefaultChatColors.Kill.ToHex()}>");

        int brokenBoneCount = 0;
        int dislocationCount = 0;
        foreach (var l in body.limbs) {
            if (l.broken) {
                brokenBoneCount++;
            }
            if (l.dislocated) {
                dislocationCount++;
            }
        }

        var lastAttack = plr.netBody.GetLastDamageEvent();

#if !RELEASE 
        log.l($"Player died. Last damage event: {lastAttack}");
#endif



        if (lastAttack != null) {
            if (!String.IsNullOrEmpty(lastAttack.deathMessage)) {
                deathMessage.Append(lastAttack.deathMessage);
            } else if (lastAttack.IsPlayerAttack()) {

                if (lastAttack.victim == lastAttack.attackerBody) {
                    if (lastAttack.damage == PlayerCamera_StartSelfDestruct_MultiplayerPatch.SelfDestructIdentifier) {
                        deathMessage.Append($"<key:plrdeath_suicide0>");
                    } else {
                        deathMessage.Append($"{PickRandomDeathMessageTag("plrdeath_suicide")}");
                    }
                } else {
                    deathMessage.Append($"<key:plrdeath_plrkill> <bodyc:{lastAttack.attackerBody.netId}>");
                    if (lastAttack.weapon != null && lastAttack.weapon.TryGetTracker(out var tracker)) {
                        deathMessage.Append($" <key:plrdeath_plrkill_using> <obj:{tracker.syncId}>");
                    }
                }
                
                deathMessage.Append(".");

            } else if (lastAttack.IsBuildingAttack(out var tracker)) {

                if (lastAttack.attackerBuilding.TryGetComponent(out SpiderHandler s)) {
                    deathMessage.Append($"<key:plrdeath_creaturekill>");
                } else {
                    deathMessage.Append($"<key:plrdeath_objkill>");
                }
                deathMessage.Append($" <obj:{tracker.syncId}>");
                
                
            } else {
                lastAttack = null;
            }
        }

        if (lastAttack == null) {
            if (!body.IsAirBreathable() && body.bloodOxygen <= 1 && body.inWater) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_drown"));
            } else if (body.bloodVolume <= 1 && body.totalBleedSpeed > 0.02f) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_bleed"));
            } else if (body.TryGetComponent(out Painkillers pain) && pain.opiateReception > 90) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_overdose"));
            } else if (brokenBoneCount > 13 || dislocationCount > 13) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_brokenbones"));
            } else if (body.hunger <= 1) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_starve"));
            } else if (body.temperature < 27) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_freeze"));
            } else if (body.transform.position.y > RadiationLine.line.transform.position.y) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_radline"));
            } else if (body.radiationSickness > 95) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_radiation"));
            } else if (body.septicShock > 81) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_sepsis"));
            } else {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath"));
            }
        }

        deathMessage.Append(".</color>");

        return deathMessage.ToString();
    }

    public static void OnPlayerDeath(ScavPlayer plr) {

        //Plugin.log.LogInfo($"{plr.playerName} died.");

        if (Net.IsServer) {

            var nb = plr.netBody;
            var body = nb.body;

            if (Multiplayer.rules.TrackPlayerDeaths) { 
                var deathMessage = GenerateDeathMessage(plr);
                Chat.Server_ChatAnnouncement(deathMessage);
            }


            // make witnesses sadder, nah its done by the corpse script 
            //YOU_SHOULD_KILL_YOURSELF_NOOOW.DoMoodPenaltyInRadius(pb.body, Multiplayer.rules.DeathWitnessMoodPenalty , 70);


            if (Util.IsTutorialWorld()) {
                plr.OnFinishTutorial(false); // finished cuz died and cant move 
            }

            if (ScavPlayer.AllLivingPlayers.Count == 0) {
                var fgdg = ("<key:everyone_is_dead>");
                Chat.Server_ChatAnnouncement(Chat.AddColorTags(Chat.DefaultChatColors.Server, fgdg));
                Server_AnnounceAlert(fgdg, true, true, null);

                PlayerCamera.main.DoAlert(Lang.Get("everyone_is_dead_host") );
            }

        } 

        // this will make witnesses stare at the dead body 
        var corpse = plr.GetOrAddComponent<CorpseScriptMultiplayerRewrite>();
        corpse.animalCorpse = false;
    }




    public static void Server_AnnounceSeed(IReadOnlyList<knetid> to_who) {
        var writer = Net.CreateWriter(NetmsgId.GenerateWorld_SeedAnnounce);
        writer.Put(WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams);
        Net.Server_SendToClientsVeryReliable( writer, to_who);

        // WRITER END;
    }


    public static void Server_AnnounceSound(Vector2 pos,string name, IReadOnlyList<knetid> to_who) {

        var writer = Net.CreateWriter(NetmsgId.PlayThisSoundHere);

        writer.Put(pos);
        writer.Put(name,true);

        //writer.Truncate();

        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: to_who);
        // WRITER END;
    }




    public static void Server_SendWorldState() {

        var writer = Net.CreateWriter(NetmsgId.CurrentWorldStateSync);

        writer.Put(new SerializableRandomState { State = UnityEngine.Random.state });
        writer.Put(Util.IsWorldGenerated());


        Net.Server_SendToClients( DeliveryMethod.Unreliable, writer,  ServerMain.AllClientIdsExceptHost);

        // WRITER END;
    }




    public static void PromptPlayer(ScavPlayer plr, string promptId, string title, string text, Action<byte> action) {
        var key = (uint)((new System.Random()).Next());

        var realKey = $"PROMPT_{promptId}_{key}";

        plr.CUSTOM_LOCAL_DATA[realKey] = action;

        try {
            var writer = Net.CreateWriter(NetmsgId.CLIENT_CustomPrompt);
            writer.Put(key);
            writer.Put(promptId);
            writer.Put(title);
            writer.Put(text);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
        } catch (Exception e) {
            log.error(e);
        }

        Util.DelayCallLambda(30, () => {
            if (plr != null) {
                plr.CUSTOM_LOCAL_DATA.Remove(realKey);
            }
        });

    }




    private void SecondUpdateSyncClients() {
       
        {
            { // cuz this function is only called by the server ima reset all my ping numbers
                if (ScavPlayer.LocalPlayer != null) {
                    ScavPlayer.LocalPlayer.ping = 0;
                }
                ClientMain.LOCAL_PING = 0;
            }

            var writer = Net.CreateWriter(NetmsgId.CurrentPingOfAllPlayers);

            foreach (var w in ScavPlayer.ClientIdToPlayerDict.Values) {

                writer.Put(true);

                writer.Put((knetid)w.playerId);
                writer.Put((float)w.ping);

                if (writer.Length > 1000) {
                    break;
                }
            }
            writer.Put(false);

            if (Net.TryGetSteamTransport(out var tsteam)) {
                tsteam.Server_SendToClients(Steamworks.Constants.k_nSteamNetworkingSend_Unreliable, writer, ServerMain.AllClientIdsExceptHost);
            } else {
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
            }
            // WRITER END;
        }


        {
            var curRunSettings = WorldMP.CompileRunSettings();
            var curRules = JsonConvert.SerializeObject(Multiplayer.rules, Formatting.None);;

            if (_lastSentRunSettings != curRunSettings || _lastSentRules != curRules) {
                _lastSentRunSettings = curRunSettings;
                _lastSentRules = curRules;

                var writer = Net.CreateWriter(NetmsgId.CLIENT_RunSettings);
                writer.Put(curRunSettings, true);
                writer.Put(Multiplayer.rules);
                writer.CompressWriter();
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
            }
        }

    }
    internal static string _lastSentRunSettings = "";
    internal static string _lastSentRules = "";


    private void RareUpdateSyncClients() {
        if (!Util.IsWorldGenerated()) return;

        Server_SendWorldState();
        
    }



    private void UpdateSyncClientsToClients() {
        // if is server
        if (!Util.IsInWorld()) return;



        { // allow skipping layer, when whoever first reaches the end
            var world = WorldGeneration.world;

            if (!WorldGeneration.world.doingRegen && !WorldGeneration.world.generatingWorld && WorldGeneration.world.worldExists) {

                ScavPlayer new_finisher = null;

                foreach (var item in ScavPlayer.BodyToPlayerDict) {
                    if (item.Key.alive && item.Key.conscious) {

                        // OG: WorldGeneration.ContinueRun
                        if ((item.Value.IsAtTheEndOfLayer())) {

                            if (!item.Value.finishedLayer) { 
                            
                                new_finisher = item.Value;

                                item.Value.finishedLayer = true;

                                break;
                            }
                        }
                    }
                }

                bool can_autocontinue = false;

                if (new_finisher != null ) {
                    var msg = String.Format(Lang.Get("plr_finished_layer"), new_finisher.playerName);
                    if (!new_finisher.IsLocal) { 
                        PlayerCamera.main.DoAlert(msg);  
                    }
                    Chat.Server_ChatAnnouncement(msg);


                }
                    

                if (ServerMain.CheckIfEnoughPeopleAreAtLayerFinish(out int current, out int required)) {
                    world.savePanel.SetActive(value: true);
                    can_autocontinue = true;
                } else if (CheckIfShouldStartRadlineForStragglers()) {
                    if (Util.IsWorldGenerated() && !RadiationLine.line.active) {
                        RadiationLine.line.Activate();

                        ScavPlayer highest_plr = ScavPlayer.AllLivingPlayers.First();
                        foreach (var item in ScavPlayer.AllLivingPlayers) {
                            // OG: WorldGeneration.ContinueRun V5t4
                                        
                            if (item.netBody.GetHeadPos().y > highest_plr.netBody.GetHeadPos().y) {
                                highest_plr = item;
                            }
                            if (!item.finishedLayer) {
                                item.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("straggler_radline"));
                            }
                        }

                        RadiationLine_Update_MultiplayerPatch.timeGone = world.halfHeight - highest_plr.netBody.GetHeadPos().y - 64;
                        
                        //RadiationLineUpdatePatch.timeGone = 899,0034 = 512 - -451,0034 - 64

                    }
                }

                if (can_autocontinue && Multiplayer.rules.AutoContinue) {

                    //OG: WorldGeneration.ContinueRun

                    if (!WorldGeneration.world.doingRegen && !world.generatingWorld && world.worldExists) {
                        world.generatingWorld = true;

                        Plugin.log.LogInfo($"SERVER: Autocontinuing the run! ");
                        Chat.Server_ChatAnnouncement($"<key:autocontinue_run>");


                        world.savePanel.SetActive(value: false);
                        world.StartCoroutine(world.RegenerateWorld());

                    }
                }

            }

        }

    }





    





    public static void _RegisterServerReceivers() { // SERVER RECEIVERS  SERVER RECEIVERS  SERVER RECEIVERS  SERVER RECEIVERS  SERVER RECEIVERS  SERVER RECEIVERS  SERVER RECEIVERS 

#if DEBUG
        Plugin.log.LogInfo("REGISTERING SERVER Receivers ");
#endif
        foreach (var item in
            Multiplayer.all_network_receivers_from_attributes
        ) {
            if (item.Key.GetReceiverType() == NetworkMessageReceiverAttribute.ReceiverType.Server) {
                Net.RegisterServerReceiver(
                    item.Key.GetMessageId(),
                    (knetid clientId, LiteNetLib.Utils.NetDataReader reader) => {
                        try {
                            item.Value.Invoke(clientId, reader);    
                        } catch (Exception e) {
                            log.error($"SUS: caused error in Server receiver {item.Key.GetMessageName()} ({DebugHelp.GetPlayerFullDebugString(clientId)}) \n{e.ToString()}");
                        }
                    }
                );
            }
        }

    }










    [ServerReceiver((byte)NetmsgId.SERVER_RequestPiggyback)]
    static void ServerReceiver__RequestPiggyback(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid this_guy);

        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out NetBody plrbody) && NetBody.TryGetNetBodyFromId(this_guy, out NetBody guybody) && plrbody != guybody) {

            if (
                plr.IsLocal ||
                (plr.body.conscious && guybody.IsPiggybackable() &&
                plrbody.StartPiggyback(guybody,true,true))
            ) {

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(
                        plr.body.transform.position,
                        $"S: RequestPiggyback {clientId} to {this_guy}"
                    );

                if (log.verbose)
                    log.l($"RequestPiggyback success: {plr} -> {guybody}");
                return;
            }
            plr.Server_DoAlertSingle("SERVER: Denied Piggyback.");


            log.serverdeny($"RequestPiggyback {plr} -> {guybody}");

            plr.Server_RemindPlayersCurrentState();
        }

    }
    
    [ServerReceiver((byte)NetmsgId.SERVER_RequestStopCarryPiggyback)]
    static void ServerReceiver__RequestStopCarryPiggyback(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        
        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var plrbody) ) {

            if (
                plr.IsConscious() &&
                plr.netBody.piggybacking_on != null
            ) {

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(
                        plr.body.transform.position,
                        $"S: RequestStopCarryPiggyback {plr} "
                    );

                plr.netBody.StopPiggyback();

                if (log.verbose)
                    log.l($"RequestStopCarryPiggyback success: {plr} ");
                return;
            }
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_GiveItem)]
    static void ServerReceiver__GiveItem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid itemId);
        reader.Get(out knetid this_guy);

        if (Util.IsWorldGenerated() 
            && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var giverPlr, out NetBody giverNetBody) 
            && NetBody.TryGetNetBodyFromId(this_guy, out NetBody receiverNetBody) && giverNetBody != receiverNetBody 
            && ItemSync.TryGetItemSyncInfo(itemId, out var si) 
            && KM.DistanceCheck(giverNetBody,receiverNetBody,20)
        ) {
            var ici = ItemSync.ItemGetContainerInfo(si.item);
            if (ici.bodynpc != receiverNetBody) {// Check if it's not already taken

                if (receiverNetBody.body.conscious && receiverNetBody.IsPlayer) {
                    PromptPlayer(receiverNetBody.plr, "ITEM_OFFER", $"<body:{giverNetBody.netId}> <key:plrint_give_prompt>", $"<key:plrint_take_prompt> \"<item:{si.syncId}>\"?", (answer) => {
                        if (answer == 0) {
                            if (ItemSync.ConfirmItemOffer(giverNetBody, receiverNetBody, si.item)) {
                                ItemSync.ForcePickupItem(receiverNetBody, si.item);

                                giverPlr.Server_DoAlertSingle($"<bodyc:{receiverNetBody.netId}> <key:accepted>.");
                            }
                        } else {
                            giverPlr.Server_DoAlertSingle($"<bodyc:{receiverNetBody.netId}> <key:declined>.");
                        }
                    });
                } else {
                    ItemSync.ForcePickupItem(receiverNetBody, si.item);
                }

            }
        }
    }
    [ServerReceiver((byte)NetmsgId.SERVER_CustomPromptAnswer)]
    static void ServerReceiver__CustomPromptAnswer(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint key);
        reader.Get(out string promptId);
        reader.Get(out byte answer);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            var realKey = $"PROMPT_{promptId}_{key}";
            try {
                if (
                    plr.CUSTOM_LOCAL_DATA.TryGetValue(realKey, out var offerAction) && offerAction is Action<byte> act
                ) {
                    act(answer);
                }
            } catch (Exception e) {
                log.error(e);
            }
            plr.CUSTOM_LOCAL_DATA.Remove(realKey);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCarryPerson)]
    static void ServerReceiver__RequestCarryPerson(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid this_guy);

        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out NetBody plrbody) && NetBody.TryGetNetBodyFromId(this_guy, out NetBody nb2) && plrbody != nb2) {

            if (
                plrbody.carrying_person == null &&
                plr.body.conscious == true
            ) {

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(
                        plr.body.transform.position,
                        $"S: RequestCarryPerson {clientId} to {this_guy}"
                    );

                if (
                    nb2.CanBeCarriedBySomeone() &&
                    nb2.StartPiggyback(plr.netBody, true, true)
                ) {
                    if (log.verbose) log.l($"RequestCarryPerson success: {plr} -> {nb2}");
                    return;
                }
            }
            plr.Server_DoAlertSingle("SERVER: Denied Carry.");
            

            log.serverdeny($"RequestCarryPerson {plr} -> {nb2}");
        }

    }
    [ServerReceiver((byte)NetmsgId.SERVER_RequestStopCarryPerson)]
    static void ServerReceiver__RequestStopCarryPerson(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (
            Util.IsWorldGenerated() &&
            ScavPlayer.TryGetPlayerAndBodyFromClientId( clientId, out var plr, out var body) &&
            plr.IsConscious() && 
            plr.netBody.carrying_person != null
        ) {
            var sci2 = plr.netBody.carrying_person;

            sci2.StopPiggyback(); // it sends the reminder

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(plr.body.transform.position,   $"S: StopCarryPerson {plr} to {sci2}");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Tutorial_Finished)]
    static void ServerReceiver__Tutorial_Finished(
        knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.GetPlayerFromClientId(clientId) is var plr &&
            plr != null) {
            if (!Util.IsTutorialWorld()) {
                log.serverdeny($"SUS: {plr} sent Tutorial_Finished when we are not even in a tutorial.");
                return;
            }

            if (!plr._tutorial_finished) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(plr.body.transform.position,
                                          $"S: Tutorial_Finished {clientId}");

                plr.OnFinishTutorial();
            }
        }
    }



    [ServerReceiver((byte)NetmsgId.PlayerPointFingerAt)]
    static void ServerReceiver__PlayerPointFingerAt(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && Util.IsInWorld()) {
            reader.Get(out Vector2 pos);
            reader.Get(out byte t);

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, $"S: PlayerPointFingerAt {DebugHelp.GetPlayerFullDebugString(clientId)}");

            plr.PointFingerAt(pos, t);

            var writer = Net.CreateWriter(NetmsgId.PlayerPointFingerAtRelay);
            writer.Put(clientId);
            writer.Put(pos);
            writer.Put(t);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
            // WRITER END;
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerJump)]
    static void ServerReceiver__PlayerJump(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body)
            && body.conscious && body.standing && Util.IsWorldGenerated()
        ) {
            body.Jump();
        }
    }

    [ServerReceiver((byte)NetmsgId.PlayerImpactDamage)]
    static void ServerReceiver__PlayerImpactDamage(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (!Util.IsWorldGenerated()) {  // dont do it if world didnt generate yet
            return;
        }

        reader.Get(out byte i);
        reader.Get(out float f);
        reader.Get(out Vector2 dir);
        reader.Get(out AnyObjectNetId obj);
        var force = Mathf.Abs(f);

        if (
            ScavPlayer.GetBodyFromClientId(clientId) is var body && body != null &&
            body.alive && Util.IsFinite(f) && dir.IsFinite()
        ) {
#if !RELEASE
            Plugin.log.LogWarning($"Server recieved  PlayerImpactDamage {i} {f} {dir}  for plr {clientId}");
#endif

            var limb = body.limbs[i];

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(limb.transform.position, $"S: Impact {limb.name} force:{force} dir:{dir}");

            Limb_ImpactDamage_MultiplayerPatch.Force(limb, force, dir);


            if (limb.TryGetNetBody(out var nb) && obj.TryGetBuilding(out var b, out var si) && b.TryGetComponent(out DamagingCrate crate)) {
                nb.OnDamageEvent(new BodyDamageEvent(b, nb, force));
            }

        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerBark)]
    static void ServerReceiver__PlayerBark(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(
                clientId,
                out var plr,
                out var body
            ) &&
            body.conscious
        ) {
            if (!body.IsBodyLocal()) {
                body.eatTime = 0;
                body.GetComponent<PantSound>().Bark();
            }
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"S: Bark");
            Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_PlayerBarkRelay, clientId, false, false);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Push_Ego_Naxuy)]
    static void ServerReceiver__Push_Ego_Naxuy( knetid clientId, LiteNetLib.Utils.NetDataReader reader ) {
        reader.Get(out knetid target);

        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(
                clientId, out var pusher_plr, out var pusher) &&
            pusher.conscious && pusher.standing &&
            pusher_plr.ServerPlayerState.Cooldown("push", NetBody.PUSH_COOLDOWN) &&  
            NetBody.TryGetNetBodyFromId(target, out var target_nb) &&
            KM.DistanceCheck(pusher, target_nb.body, SharedMain.max_player_interaction_distance * 1.2f)
        ) {
            pusher_plr.netBody.Push(target_nb);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SetTimeScale)]
    static void ServerReceiver__SetTimeScale(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ushort sc);

        if ((PlayerCamera.SpeedType)sc == PlayerCamera.SpeedType.Paused)
            return;

        if (
            Util.IsInWorld() && ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)
        ) {
            Plugin.log.LogInfo( $"SetTimeScale: Received SetTimeScale from a client {clientId} {plr.ToString()}  ");

            PlayerCamera.main.SetTimeScale((PlayerCamera.SpeedType)sc, true);  // it already does the relay thing

            PlayerCamera.main.DoAlert(String.Format(Lang.Get("plr_set_timescale"), plr.playerName, $"{sc}"));
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ClientCharacterSyncUpdate)]
    static void ServerReceiver__ClientCharacterSyncUpdate(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader = reader.DecompressReader();
        reader.Get(out ClientToServer_NetBodySyncPacket pack);

        if (Util.IsWorldGenerated()) {
            if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)) {
                if (!plr.IsLocal) {
                    plr.ServerPlayerState.is_loaded_in = true;
                    if (pb.alive && !plr.Server_HasForcedServerOwnershipOfBody()) {
                        {

                            var seqDiff = LiteEntitySystem.Utils.SequenceDiff(pack.Tick, pb._lastSyncPacketReceiveTick);
                            if (
                                seqDiff <= 0 && ClientMain._last_reminderpack_while_generating_received
                            ) {
#if !RELEASE
                                if (log.verbose) log.l($"OnReceiveSyncPacket {pb}: received packet that is too old, ignoring it.");
#endif
                                return;
                            }
                            pb._lastSyncPacketReceiveTick = pack.Tick;

                            if (pb.body != null) {
                                pack.Apply(pb);
                            }
                        }

                        if (pack.IncludeRagData() && !reader.EndOfData && pb.piggybacking_on == null) {
                           ClientMain.ReadRagdollPacket(ref reader, pb.body);
                        }
                    }
                } else {
                    //shouldnt happen
                }
            } else {
                log.error($"player {clientId} doesnt have a playerprefab object ?????  ClientCharacterSyncUpdate");
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerDrinkLiquid)]
    static void ServerReceiver__PlayerDrinkLiquid(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            if (Util.IsWorldGenerated() && plr.IsAlive()) {
                if (log.verbose)
                    log.l($"Server approves drink for: {plr} ");
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(plr.body.transform.position,
                                          $"S: PlayerDrinkLiquid {plr}");

                var drinkblockpos = WorldGeneration.world.WorldToBlockPos(
                    plr.body.transform.position - Vector3.up * 2.5f);

                var there_is_a_fluid =
                    FluidManager.main.GetLiquid(drinkblockpos.x, drinkblockpos.y);
                if (!plr.IsLocal) {
                    FluidManager.main.DrinkLiquid(drinkblockpos, plr.body);
                }
                if (there_is_a_fluid != 0 || plr.IsLocal) {
                    ServerMain.Server_AnnounceSound(plr.body.transform.position, "drink", ServerMain.AllClientIdsExceptHost);
                }

                MedicalSync.Server_QueueSendCharacterHealth(plr.netBody);
                return;
            }
            log.sus($"PlayerDrinkLiquid {plr} is ded or sum  ");
        }
    }




    internal static Dictionary<string, Action<ScavPlayer, string, string[]>> ServerCustomCommandsDict = new();
    internal static void RunClientCustomCommand(ScavPlayer plr, LiteNetLib.Utils.NetDataReader reader) {
        var com = reader.GetString();

        var args = com.Trim().Split(' ');

        if (ServerCustomCommandsDict.TryGetValue(args[0], out var action)) {
            try {
                action.Invoke(plr, com, args);
            } catch (Exception ex) {
                Con.Server_SendConsoleLog(ex.Message, plr);
            }
        } else {
            // unkown command 
        }
    }







}
