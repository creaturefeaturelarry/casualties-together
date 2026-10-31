using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;


namespace Together;








public class Server_PlayerState {

    public int didmutewarn;

    public int fps = 60;
    public int tps = 60;

    public bool admin_privilege = false;

    public float layer_transition_x_offset;

    public double last_cursormove = 0;

    public bool loaded_save = false;
    public bool did_give_spawn_location = false;
    public bool did_give_spawn_location_from_a_save = false;
    public bool finished_worldgen = false;
    public bool is_loaded_in = false; // this is determined by the type of packets that are sent by the client, that are guranteed to be sent only after world generated
    public bool[,] known_chunks;

    Dictionary<string, double> cooldowns = new();

    public struct LocSnapshot {
        public bool alive;
        public Vector2_4byte_512 pos;
    }

    public List<LocSnapshot> cur_location_history;
    public List<List<LocSnapshot>> location_history = new();

    public void SaveLocationSnapshot() {
        if (this.cur_location_history == null) {
            this.cur_location_history = new();
            this.location_history.Add(this.cur_location_history);
        }

        if (this.cur_location_history.Count > 400) {
            this.cur_location_history.RemoveAt(UnityEngine.Random.Range(1, 399));
            this.cur_location_history.RemoveAt(UnityEngine.Random.Range(1, 398));
        }

        this.cur_location_history.Add(new Server_PlayerState.LocSnapshot {
            alive = plr.IsAlive(),
            pos = plr.pos
        });
    }

    public ScavPlayer plr;

    public Server_PlayerState(ScavPlayer p) {
        plr = p;
        OnWorldGenerate();
        OnSceneChangeOrWorldStartGenerate();
    }
    public void ResetKnownChunks() {
        
        known_chunks = new bool[WorldChunkSync.SYNCCHUNK_TILESIZE, WorldChunkSync.SYNCCHUNK_TILESIZE];

        // host knows about everything
        if (plr.IsLocal) {
            for (int y = 0; y < WorldChunkSync.SYNCCHUNK_TILESIZE; y++) {
                for (int x = 0; x < WorldChunkSync.SYNCCHUNK_TILESIZE; x++) {
                    known_chunks[x, y] = true;
                }
            }
        }
    }

    // only called right before WorldGeneration.FinishWorldGeneration
    public void OnWorldGenerate() {
        ResetKnownChunks();
    }

    public void OnWorldgenStart() {
        ResetKnownChunks();
        did_give_spawn_location = false;
        finished_worldgen = false;
    }
    // only called in OnSceneLoaded and WorldGeneration.GenerateWorld
    public void OnSceneChangeOrWorldStartGenerate() {
        did_give_spawn_location = false;
        is_loaded_in = false;
        loaded_save = false;
    }

    public bool Cooldown(in string id, float cooldown) {
        if (cooldowns.TryGetValue(id, out var cd)) {
            var diff = Time.realtimeSinceStartupAsDouble - cd;
            if (diff < cooldown) {
                if(log.verbose) log.serverdeny($"COOLDOWN, TOO FAST, {plr}, req: {cooldown} cur: {diff} ");
                return false;
            }
        }
        cooldowns[id] = Time.realtimeSinceStartupAsDouble;
        return true;
    }


}






public class ScavPlayer : MonoBehaviour {

    public readonly Dictionary<string, object> CUSTOM_LOCAL_DATA = new();
    public BasePlayer BasePlayer { get; internal set; }

    public Server_PlayerState ServerPlayerState = null;

    public ulong SteamId;
    public Texture2D IconLarge;
    public Texture2D IconMedium;
    public Texture2D IconSmall;
    public Texture2D IconAnyLargeToSmall => IconLarge ?? IconMedium ?? IconSmall;
    public Texture2D IconAnySmallToLarge => IconSmall ?? IconMedium ?? IconLarge;

    public Sprite IconLargeSprite ;
    public Sprite IconMediumSprite;
    public Sprite IconSmallSprite ;


    public List<Sprite> KnownUserTagIcons = new();

    public knetid playerId { get; internal set; }
    public bool IsLocal { get; internal set; }
    public bool IsHost { get; internal set; }


    public Vector2 CursorWorldPos { get { return BasePlayer.LookPos.Value; } set { BasePlayer.LookPos.Value = value; } }
    public Vector2 CursorScreenPos => BasePlayer.CursorPos.InterpolatedValue;
    public Vector2 CameraViewPos => BasePlayer.CameraPos.Value;


    #region PLAYER_COMPONENTS

    internal VoicechatOutput vc_output { get; private set; }

    #endregion
    #region STATIC_SHIT

    public static event Action<ScavPlayer> OnPlayerJoined;
    public static event Action<ScavPlayer> OnPlayerLeft;

    public static Dictionary<Body, ScavPlayer> BodyToPlayerDict = new Dictionary<Body, ScavPlayer>();
    public static Dictionary<knetid, ScavPlayer> ClientIdToPlayerDict = new Dictionary<knetid, ScavPlayer>();

    public static List<ScavPlayer> AllLivingPlayers = new();
    public static List<ScavPlayer> AllDeadPlayers = new();

    #endregion

    #region NetPlayer_SafeBodyGetters
    public bool IsAlive() {
        if (body == null) {
            return false;
        }
        return body.alive;
    }
    public bool IsUnchipped() {
        if (body == null) {
            return unchipped;
        }
        if (netBody == null) {
            return unchipped;
        }
        return netBody.unchipped;
    }
    public bool ImpairedSpeech() {
        if (body == null) {
            return false;
        }
        return body.alive && body.talker.impairedSpeech;
    }
    public bool IsAliveAndNotCriticallyDying() {
        if (body == null) {
            return false;
        }
        return (body.alive && !body.isCriticallyDying);
    }
    public bool IsDeadOrCriticallyDying() {
        if (body == null) {
            return false;
        }
        return (body.IsDeadOrCriticallyDying());
    }
    public bool IsConscious() {
        if (body == null) {
            return false;
        }
        return body.conscious;
    }
    public bool IsDead() {
        if (body != null) {
            return !body.alive;
        }
        return false;
    }
    public bool IsAliveAndMindwiped() {
        if (IsAlive()) {
            return body.IsMindwiped();
        }
        return false;
    }
    public bool IsAliveAndNotConscious() {
        if (body != null && body.alive) {
            return !body.conscious;
        }
        return false;
    }
    public bool IsAliveAndUnconsciousAndNotSleeping() {
        if (body != null && body.alive) {
            return !body.conscious && !body.sleeping;
        }
        return false;
    }
    #endregion

    #region GLOBAL_UTILS



    public static List<NetBody> GetPlayerBodiesInRadius(Vector2 pos, float radius) {
        radius *= radius; // square it

        var b = new List<NetBody>();
        foreach (var body in ScavPlayer.BodyToPlayerDict.Keys) {
            if (KM.DistanceCheck_PreSqr(body.transform.position, pos, radius) && body.TryGetNetBody(out var c)) {
                b.Add(c);
            }
        }
        return b;
    }

    public static List<ScavPlayer> GetPlayersInRadius(Vector2 pos, float radius) {
        radius *= radius; // square it

        var b = new List<ScavPlayer>();
        foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (KM.DistanceCheck_PreSqr(plr.pos, pos, radius)) {
                b.Add(plr);
            }
        }
        return b;
    }
    
    public static (NetBody, float) GetNearestConsciousPlayerToThisBody(NetBody thisone) {
        float nearest = float.PositiveInfinity;
        NetBody nearestsci = null;
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            var pb = item.Key.GetComponent<NetBody>();
            if ((pb.body.conscious || pb.body.sleeping) && thisone != (pb as NetBody)) {
                var dist = KM.DistanceSqr(pb.transform.position, thisone.transform.position);
                if (dist < nearest) {
                    nearest = dist;
                    nearestsci = item.Key.GetComponent<NetBody>();
                }
            }
        }
        return (nearestsci, nearest);
    }
    /// <summary>
    ///  warning: distance is squared!
    /// </summary>
    /// <param name="target"></param>
    /// <returns></returns>
    public static (ScavPlayer, float) GetDistanceToNearestLivingPlayer(Vector2 target) {
        float nearest = float.PositiveInfinity;
        ScavPlayer nearestsci = null;
        foreach (var plr in AllLivingPlayers) {
            var body = plr.body;
            if (plr.body == null) continue;
            var dist = KM.DistanceSqr(body.transform.position, target);
            if (dist < nearest) {
                nearest = dist;
                nearestsci = plr;
            }
        }
        return (nearestsci, nearest);
    }


    #endregion

    public bool LateJoin = false;

    public bool unchipped; 

    public double levelPlayTime = 0;
    public CoUtils CoUtilsInstance;

#if false
    public class BondInfo : INetSerializeByMemcpy {
        public ushort timesHealed = 0;
        public ushort itemsUsed = 0;
        public ushort timesAccessedInventory = 0;
        public ushort timesHeAccessedMyInventory = 0;
        public float timeCarried = 0;
        public float timePiggybacked = 0;
        public float timeStoodClose = 0; // within 10 units of each other

        public float TODO;

        public BondInfo() { 
        
        }
    }

    public Dictionary<string, BondInfo> all_bonding = new();
#endif



    public bool IsInMinigame => minigame_current_type != 255;
    public SyncInfo minigame_currentItem_syncinfo {
        get {
            if (ItemSync.TryGetItem(BasePlayer.MinigameCurrentItemId.Value, out var si, out var item)) {
                return si;
            }
            return null;
        }
        set {
            BasePlayer.MinigameCurrentItemId.Value = value.syncId;
        }
    }
    public Item minigame_currentItem {
        get {
            if (ItemSync.TryGetItem(BasePlayer.MinigameCurrentItemId.Value, out var si, out var item)) {
                return item;
            }
            return null;
        }
        set {
            if (value == null || NetObjectRegistry.ObjectShouldNotBeSynced(value.gameObject)) {
                BasePlayer.MinigameCurrentItemId.Value = 0;
                return;
            }
            if (Net.IsServer) {
                var si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(value.gameObject);
                if (si != null) {
                    BasePlayer.MinigameCurrentItemId.Value = si.syncId;
                }
            }
        }
    }
    
    public knetid DragItemId;
    public AnyObjectNetId minigame_targetobject { get { return BasePlayer.MinigameTargetObjectId.Value; } set { BasePlayer.MinigameTargetObjectId.Value = value; }  }
    public ushort minigame_session { get { return BasePlayer.MinigameSessionId.Value; } set { BasePlayer.MinigameSessionId.Value = value; } }
    public Type MinigameType => MinigameMPManager.GetMinigameTypeFromIdNullable(minigame_current_type);
    public byte minigame_current_type { get { return BasePlayer.MinigameTypeId.Value; } set { BasePlayer.MinigameTypeId.Value = value; } } // 255 = none,  other numbers are from the minigame_ids dict
    public Vector2 minigame_handpos = Vector2.zero;
    public Vector2 minigame_mousepos = Vector2.zero;
    public byte minigame_handsprite = 2;

    public bool IsInPauseMenu = false;
    public bool IsInConsole = false;     
    public bool IsAltTab = false;     
    public bool IsChatFocused = false;   
    public bool IsInCraftingMenu = false;   
    public bool IsInTradingMenu = false;    
    public ushort WoundviewTargetNetBodyId = 0;

    public bool ButtonAttack;
    public bool ButtonIteminteract;

    public bool server_mute_vc { get { return BasePlayer.MuteVC.Value; } set { BasePlayer.MuteVC.Value = value; } }
    public bool server_mute_tc { get { return BasePlayer.MuteTC.Value; } set { BasePlayer.MuteTC.Value = value; } }


    public Vector2 pos {
        get {
            if (IsAlive()) {
                return netBody.position;
            }
            return CameraViewPos;
        }
        set {
            if (body != null) {
                netBody.SetBodyPosition(value);
            }
        }
    }


    public const string OBJNAMEPREFIX_PLR = "PlayerObject_";
    public const string OBJNAMEPREFIX_NAMETAG = "PlayerNameTag_";

    public bool nameIsCustom = false;
    public string playerName = "ERROR-NO-NAME";

    public bool _tutorial_finished = false;

    [Obsolete("probably?")]
    public void OnFinishTutorial(bool tp = true) {
        if (!_tutorial_finished) {

            _tutorial_finished = true;

            Chat.Server_ChatAnnouncement($"<plr:{playerId}> <key:plr_finished_tutorial>");

        }
    }

    public bool finishedLayer = false;

    public double ping = -0.01;
    public double rtt_in_seconds_with_margin => ping * 4.1; // should be 2.01,  but more is better placebo
    public int ping_as_ms => (int)(ping * 1000);


    // kid named finger 
    public GameObject locationPingCircle = null;
    public GameObject locationPingArrow = null;


    public GameObject chara => body.transform.parent.gameObject;
    public Body body;
    public NetBody netBody => body.GetComponent<NetBody>();
    public bool TryGetNetBody(out NetBody pb) {
        if (this == null) {
            pb = null;
            return false;
        }
        if (body != null) {
            return body.TryGetNetBody(out pb);
        }
        pb = null;
        return false;
    }


    public static ScavPlayer LocalPlayer;
    [Obsolete("Use NetBody.TryGetLocalNetBody")]
    public static bool TryGetLocalNetBody(out NetBody nb) {
        return NetBody.TryGetLocalNetBody(out nb);
    }
    [Obsolete("Use NetBody.GetLocalNetBodyNullable")]
    public static NetBody GetLocalNetBodyNullable() {
        return NetBody.GetLocalNetBodyNullable();
    }

    public override string ToString() {
        if (this == null) {
            return "PLR(NULL)";
        }
        if (SteamId != 0 && Net.IsRunningSteam) {
            return $"PLR(ID:{playerId}, name:{playerName}, SteamID: {SteamId})";
        }
        return $"PLR(ID:{playerId}, name:{playerName})";
    }

    public static ScavPlayer GetPlayerFromClientId(knetid clientid) {
        return ScavPlayer.ClientIdToPlayerDict.GetValueSafe(clientid);
    }


    public static bool TryGetPlayerFromClientId(knetid clientid, out ScavPlayer plr) => ScavPlayer.ClientIdToPlayerDict.TryGetValue(clientid, out plr);
    public static bool TryGetPlayerAndBodyFromClientId(knetid clientid, out ScavPlayer plr, out Body body) {
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(clientid, out plr) && plr.body != null) {
            body = plr.body;
            return true;
        }
        plr = null;
        body = null;
        return false;
    }
    public static bool TryGetPlayerAndNetBodyFromClientId(knetid clientid, out ScavPlayer plr, out NetBody pb) {
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(clientid, out plr) && plr.body != null) {
            pb = plr.netBody;
            return true;
        }
        plr = null;
        pb = null;
        return false;
    }
    
    
    public static Body GetBodyFromClientId(knetid clientid) {
        var plr = GetPlayerFromClientId(clientid);
        if (plr != null)
        {
            return plr.body;
        }
        return null;
    }
    public static knetid GetClientIdFromBody(Body body) {
        var pb = body.GetComponent<NetBody>();
        return pb.netId;
    }
    public static ScavPlayer GetPlayerFromBody(Body body) {
        return ScavPlayer.BodyToPlayerDict.GetValueSafe(body);
    }

    static TMP_FontAsset s_retroGamingFontAsset = null;

    public void ApplyNameAndColor(string name, Color24 color ) {



        if (BasePlayer != null) {
            if (Net.IsServer) {
                BasePlayer.playerName.Value = name;
                BasePlayer.playerColor.Value = color;
            }
        }


        if (Net.TryGetSteamTransport(out var tsteam)) {
            if (!nameIsCustom) {
                if (SteamId.ToString() == name) {
                    name = KSteam.GetSteamUsername(SteamId);
                } else {
                    nameIsCustom = true;
                }
            }
        } else {
            nameIsCustom = true;
        }


        // check for weird characters 
        {
            if (s_retroGamingFontAsset == null) {
                var ggggfd = GameObject.FindObjectsOfType<TextMeshProUGUI>().Select(x => x.font);
                s_retroGamingFontAsset = ggggfd.FirstOrDefault(x => x != null && x.name == "Retro GamingPix");
            }
            if (s_retroGamingFontAsset != null) {
                name = FontUtils.ReplaceMissingCharacters(s_retroGamingFontAsset, name);
            }
        }


        var changed_name = nameIsCustom && playerName != name;
        var changed_color = playerColor != color;
        playerName = name;
        playerColor = color;
        gameObject.name = ScavPlayer.OBJNAMEPREFIX_PLR + name;


        var color_float = (Color)color;
        var slightlytransparent = new Color(color_float.r, color_float.g, color_float.b, 0.9f);
        var transparent = new Color(color_float.r, color_float.g, color_float.b, 0f);

        if (body != null) {
            netBody.ApplyNameAndColor(name, color);
        }

        if (locationPingCircle) {
            locationPingCircle.name = ("fingerpointercircle_" + playerName);
            var spr = locationPingCircle.GetOrAddComponent<SpriteRenderer>();
            spr.color = transparent;
        }
        if (locationPingArrow) {
            locationPingArrow.name = ("fingerpointerarrow_" + playerName);
            var spr = locationPingArrow.GetOrAddComponent<SpriteRenderer>();
            spr.color = transparent;
        }

    }


    private void FixedUpdate() {

        if (Net.IsServer) {
            if (Util.IsInWorld()) {
                if (Util.IsWorldGenerated()) {

                    if (body != null && !body.IsBodyLocal()) {
                        //OG: PlayerCamera.FixedUpdate V7.0.1 
                        if (ItemSync.TryGetItem(DragItemId, out var si, out var dragItem) && dragItem.rb.simulated && Vector2.Distance(dragItem.transform.position, body.transform.position) < 10f) {
                            dragItem.rb.velocity *= 0.85f;
                            dragItem.rb.angularVelocity *= 0.85f;
                        }
                    }

                } else {
                    ServerPlayerState.is_loaded_in = false;
                }
            }
        }

        if (Util.IsWorldGenerated() && (IsLocal || Net.IsClient || ServerPlayerState.is_loaded_in)) {
            levelPlayTime += Time.fixedUnscaledDeltaTime;
        } else {
            levelPlayTime = 0;
        }
        if (!body) {
            return;
        }


    }
    
    public bool IsInSameTeamAs(ScavPlayer plr) {
        return this.playerColor == plr.playerColor;
    }
    public List<ScavPlayer> GetTeamPlayers() {
        var plrs = new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values);

        plrs.RemoveAll(x => (
            !this.IsInSameTeamAs(x)
        ));

        return plrs;
    }


    void Awake() {
        DontDestroyOnLoad(this.gameObject);
        
        if (Net.IsServer) {
            ServerPlayerState = new Server_PlayerState(this);
        }

        //in Awake nothing is set yet, so do all the stuff in Start
    }

    #region COLOR_BULLSHIT
    public Color24 playerColor = Color.black;


    public static Color[] DEFAULT_TEAMS = new Color[] {
        Color.red,
        Color.blue,
        Color.green,
        Color.yellow,
    };

    public static Color[] NAMETAG_DEFAULT_COLORS = new Color[] {
        Color.red,
        Color.green,
        Color.blue,
        Color.cyan,
        Color.yellow,
        Color.magenta,
        Color.white
    };
    public static Color[] NAMETAG_DEFAULT_COLORS_RANDOMIZED = new Color[] {
        Color.red,
        Color.green,
        Color.blue,
        Color.cyan,
        Color.yellow,
        Color.magenta,
        Color.white
    };
    private static byte SERVER_NAMETAG_COLOR_PICKER_COUNTER = 0;

    public static bool CheckIfPlrColorIsValid(Color24 c) {
        if (c.Sum() < 153) {
            return false;
        }
        return true;
    }

    static Color24 PickNewPlayerColor() {

        for (int i = 0; i < NAMETAG_DEFAULT_COLORS.Length; i++) {
            if (SERVER_NAMETAG_COLOR_PICKER_COUNTER >= NAMETAG_DEFAULT_COLORS.Length) {
                SERVER_NAMETAG_COLOR_PICKER_COUNTER = 0;
            }
            var result = (Color24)NAMETAG_DEFAULT_COLORS[SERVER_NAMETAG_COLOR_PICKER_COUNTER];

            SERVER_NAMETAG_COLOR_PICKER_COUNTER += 1;

            if (ClientIdToPlayerDict.Values.Any(x => x.playerColor == result)) {
                continue;
            }
            return result;
        }

        for (int i = 0; i < 200; i++) {
            var result = new Color24((byte)UnityEngine.Random.Range(0,255), (byte)UnityEngine.Random.Range(0, 255), (byte)UnityEngine.Random.Range(0, 255));

            //dont allow it to be too dark
            if (!CheckIfPlrColorIsValid(result)) {
                result[UnityEngine.Random.Range(0, 3)] = (byte)UnityEngine.Random.Range(200, 255);
            }

            if (ClientIdToPlayerDict.Values.Any(x => x.playerColor == result)) {
                continue;
            }
            return result;
        }

        log.error($"COULDN'T PICK A NEW COLOR FOR A PLAYER AAAAAAAAA WTFFFFF!!?!?!?!?");
        return NAMETAG_DEFAULT_COLORS.PickRandom();
       
    }

    #endregion

    void Start() {


        {
            {
                locationPingCircle = new GameObject("fingerpointercircle");
                var spr = locationPingCircle.AddComponent<SpriteRenderer>();
                spr.sprite = CoopModAssets.circlething;
                spr.color = new Color(0,0,0,0);
                spr.sortingOrder = 6000;
                DontDestroyOnLoad(locationPingCircle);
                locationPingCircle.SetActive(false);
            }
            {
                locationPingArrow = new GameObject("fingerpointerarrow");
                var spr = locationPingArrow.AddComponent<SpriteRenderer>();
                spr.sprite = CoopModAssets.arrowicon;
                spr.color = new Color(0, 0, 0, 0);
                spr.sortingOrder = 6000;
                locationPingArrow.transform.localScale = Vector3.one * 8f;
                DontDestroyOnLoad(locationPingArrow);
                locationPingArrow.SetActive(false);
            }
        }

        var IS_STEAM = Net.Transport != null && Net.Transport is TransportSteamworks;

        if (IS_STEAM) {
            KSteam.LoadPlayerProfilePics(this);
        }
        KnownPersons.LoadTagsFor(this);


        {
            if (Net.IsServer) {

                if (!IsLocal) {
                    try {
                        ServerMain._Server_OnServerNameChange(new List<knetid> { playerId });
                    } catch (Exception e) {
                        log.error( e.ToString());    
                    }
                }

                if (!CheckIfPlrColorIsValid(playerColor)) {
                    playerColor = PickNewPlayerColor();
                }


                name = ScavPlayer.OBJNAMEPREFIX_PLR + playerName;

                var msg = $"{Chat.AddColorTags(playerColor,playerName)}<parsebreak> {Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:plr_joined>")}";

                var newcount = ScavPlayer.ClientIdToPlayerDict.Count + 1;
                if (Multiplayer.rules.AutoContinue && !Util.IsInWorld() && newcount <= Multiplayer.rules.AutoMinPlrsToStart) {
                    msg += $" {newcount}/{Multiplayer.rules.AutoMinPlrsToStart} <key:auto_minimum_to_start>";
                }

                Chat.Server_ChatAnnouncement(msg);

                if (Net.IsServer) { // try to find their old color
                    var pid = this.GetPersistentId();
                    if (ServerMain.Server_LastPlayerStates.TryGetValue(pid, out var val)) {
                        playerColor = val.plrcolor;
                    }
                }
            }
            

            ScavPlayer.ClientIdToPlayerDict[this.playerId] = this;
            vc_output = this.GetOrAddComponent<VoicechatOutput>();

            if (Util.IsInWorld()) {
                LateJoin = true;
            }

            if (IsLocal) {
                vc_output.enabled = false;
                CoUtilsInstance = CoUtilsMultiplayer.GetLocalCoUtilsInstance();

                if (!IS_STEAM) {
                    // set my own name 
                    playerName = UIMainMenu.USERINPUT_NAME;
                }

                name = OBJNAMEPREFIX_PLR + playerName;

                LocalPlayer = this;
            } else {
                //OG: CoUtils.instance getter V5Demo 
                GameObject obj = new GameObject($"CoUtils_{playerId}_{playerName}");
                CoUtilsInstance = obj.AddComponent<CoUtils>();
                obj.transform.SetParent(this.transform);

            }
            CreateCharacter();



            // update all the images, nametags n shi
            ApplyNameAndColor(playerName, playerColor);


            OnPlayerJoined?.Invoke(this);
            InvokeRepeating(nameof(SlowUpdate), 1f, 1f);
            InvokeRepeating(nameof(SlowButFastUpdate), 1f, 0.1f);

            if (Net.IsServer) {
                Multiplayer.ApplyGameRules();

                Util.DelayCallLambda(0.1f, () => {

                    if (this != null && !IsLocal && Util.IsInWorld()) { // and tell him to generate the world NOW

                        var writer = Net.CreateWriter(NetmsgId.StartGameAnnouncement);
                        writer.Put(ServerMain.LastStartGameAnnouncementPacket);
                        writer.Put(WorldMP.CompileRunSettings(), true);
                        Net.Server_SendToClientsVeryReliable( writer, new List<knetid>{ playerId});

                        ServerMain.Server_AnnounceSeed(new List<knetid> { playerId });

                    }
                });
            }
        }



    }

   

    public string GetPersistentId() {
        if (Net.IsRunningSteam) {
            return "STEAM_" + SteamId.ToString();
        }


        return "NAME_" + playername_as_hex;
    }
    public string playername_as_hex => Encoding.UTF8.GetBytes(playerName).ToHexString();

    void SlowUpdate() {

        if (Net.IsServer) {
            if (body != null) {
                if (Util.IsWorldGenerated()) {
                    if (IsLocal) {
                        ServerPlayerState.is_loaded_in = true;
                        ServerPlayerState.did_give_spawn_location = true;
                    }
                }
            }
        }

    }
    public List<NetBody> visiblebodies = new();
    static NetDataWriter ragdollpacketwriter = Net.CreateWriter(NetmsgId.CLIENT_ragdollpack);
    void SlowButFastUpdate() {
        visiblebodies.Clear();
        if (Net.IsServer) {
            if (Util.IsWorldGenerated()) {
                
                NetBody.GetBodiesInRadius(pos,40, visiblebodies);

                //if (ServerPlayerState.is_loaded_in) 
                {
                    //Server_SendRagdollPackets();

                    foreach (var otherBody in Net.ServerEntityManager.GetEntities<SyncBody>())
                        Net.ServerEntityManager.ToggleSyncGroup(
                            (byte)playerId,
                            otherBody,
                            SyncGroup.SyncGroup3,
                            ServerPlayerState.is_loaded_in && !otherBody._standing.Value && !otherBody._isPiggyback.Value && otherBody.nb.plr != this && visiblebodies.Contains(otherBody.nb)
                        );
                }
            }
        }
    }



    // Is called also from LES on sync
    public void SetUnchipped(bool unchipped) {

        if (body != null && netBody.unchipped != unchipped) {
            netBody.SetUnchipped(unchipped);
        }

        if (this.unchipped != unchipped) {
            this.unchipped = unchipped;
        }

        if (IsLocal) {
            Util.SetUnchipped(unchipped);
        }

    }




    public bool HasGunEquipped(out GunScript gun) {
        if (body != null && body.conscious &&
            //OG: PlayerCamera.GunToggleSafety  and others where they check for a gun
            body.HoldingItem(body.handSlot) && body.GetItem(body.handSlot).TryGetComponent(out GunScript component)
        ) {
            gun = component;
            return true;
        }
        gun = null;
        return false;
    }


    public bool CanCommunicateWith_TextChat(ScavPlayer target) {
        if (target == this) {
            return true;
        }
        if (target != null) {
            if (Util.IsTutorialWorld()) {
                return true;
            }
            var targetisalive = target.body == null || target.body.alive;
            var iamalive = body == null || body.alive;
            if (!iamalive && targetisalive && !(Multiplayer.rules.DeadTextchat)) {
                return false;
            }
            return true;
        }
        return false;
    }




    public void ResetEntropy() {
        if (body != null) {
            body.ResetEntropy();
            netBody.SetNetHealthSyncIgnoreTime(0.1f);
        }
    }



    public void CreateCharacter() {
        if (Util.IsInMainMenu()) {
            return;
        }
        if (Net.IsServer) {
            NetBody.CreateNewPlayerCharacter(this);
        }
    }


    public bool Server_HasForcedServerOwnershipOfBody() {
        if (body != null) {
            if (!body.alive) {
                return true;
            }
        }
        return false;
    }

    public void Server_DoAlertSingle(in string msg, bool reliable = true) => Server_DoAlertSingle(msg,false, reliable);
    public void Server_DoAlertSingle(in string msg, bool important, bool reliable) {
        var writer = Net.CreateWriter(NetmsgId.CLIENT_AnnounceDoAlert);
        writer.Put(msg);
        writer.Put(important);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: playerId);
        // WRITER END;
    }



    public void Server_Kick(string message = "Kicked.") {

        if (Net.TryGetSteamTransport(out var _) && KnownPersons.IsSteamUserPrivileged(SteamId)) {
            // Kick protection
            return;
        }
        
        try {
            if (body != null) {
                body.Body_DropAllItems();
            }
        } catch (Exception e) {

        }


        var msg = $"Kicked {playerName}";
        Chat.Server_ChatAnnouncement(msg);
        try {
            ServerMain.Server_SendMPLogMessage(msg + ": " + message, false, new knetid[] { playerId });
        } catch (Exception e) {
            log.error(e);
        }

        Net.Server_Kick(playerId, message);
    }



    public void Server_ReviveCharacter(bool remind_health_now = true, bool unmindwipe = false) {
        if (Util.IsInWorld()) {
            CreateCharacter();

            if (!body.alive) {
                if (!Multiplayer.rules.RespawnKeepSkills) {
                    body.ResetMind();
                }
            }

            body.ResetHealth(unmindwipe);
            

            if (remind_health_now) {
                MedicalSync.Server_SendCharacterHealth(netBody);
            }
        }
    }
    // used for teleporting
    public void Server_RemindPlayersCurrentState(bool keep_velocity = false, bool reliable = true) {

        if (TryGetNetBody(out var nb)) {
            if (!keep_velocity) {
                body.SetVelocity(Vector2.zero);
            }
            nb.Server_RemindPlayersCurrentState( reliable);   
        } else {
            if (log.verbose) log.warn($"Attempted to remind body state of {this} but theres no body.");
        }

    }

    public void Server_TeleportCharacter(Vector2 pos) {
        if (Util.IsInWorld()) {
            CreateCharacter();

            if (IsLocal) {
                UIInGame._DEV_FREECAM_CURPOS = pos;
            }

            netBody.SetBodyPosition(pos);

            if (!this.IsLocal)
                WorldChunkSync.Server_CheckPlrPosChunk(this);

            Server_RemindPlayersCurrentState();
        }
    }
    public void Server_RespawnCharacter(Vector2 pos, bool level_transition = false) {
        if (Util.IsInWorld()) {
            CreateCharacter();

            if (level_transition && !Multiplayer.rules.RespawnKeepInventory) {
                Server_DropAllInventory();
            }

            if (!Multiplayer.rules.RespawnKeepSkills) {
                body.ResetMind();
            }

            var should_unmindwipe = !Multiplayer.rules.RespawnKeepSkills;

            netBody.StopPiggyback();

            Server_ReviveCharacter(true, should_unmindwipe);
            body.ForceStand();

            Server_TeleportCharacter(pos);

        }
    }
    // can be called from client btw  
    public void Server_DropAllInventory() {
        body?.Body_DropAllItems();
    }
    public void Server_HealCharacter() {
        Server_ReviveCharacter(false);
    }




    public bool IsAtTheEndOfLayer() {
        if (body == null) { return false; }
        return ServerMain.DidHeFinishTheLayer(body);
    }




    public const double FINGERPOINTINGTIME_MAX = 3.0;
    public bool is_pointingfingerat = false;
    public byte is_pointingfingeratTYPE = 0;
    public double is_pointingfingeratTIME = -9990.0;
    public Vector2 is_pointingfingeratTARGET = Vector2.zero;

    public void PointFingerAt(Vector2 pos, byte type) {
        var alive = false;
        if (body) {
            alive = body.conscious;
            body.overrideLookTime = (float)(FINGERPOINTINGTIME_MAX - 0.5);
            body.overrideLookPos = pos;
        }

        is_pointingfingerat = ((Multiplayer.rules.CanCommunicateWithTheDeadTC()) || alive);
        is_pointingfingeratTIME = Time.realtimeSinceStartupAsDouble;
        is_pointingfingeratTARGET = pos;
        is_pointingfingeratTYPE = type;
        var spr = locationPingCircle.GetComponent<SpriteRenderer>();
        
        if (type == 0) {
            spr.sprite = CoopModAssets.circlething;
        } else if(type == 1) {
            spr.sprite = CoopModAssets.alert2;
        } else {
            spr.sprite = CoopModAssets.circlething;
        }

    }


#if !RELEASE
    void OnGUI() {
        if (log.verbose) {
            if (IsInMinigame && minigame_targetobject.TryGetGameObject(out var go)) {

                var ogfont = GUI.skin.label.fontSize;

                var pos = (Vector2)Camera.main.WorldToScreenPoint(go.transform.position);
                pos = new Vector2(pos.x, Screen.height - pos.y);

                if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {

                    const float bsize = 17;
                    const float elsize = 13;

                    float y = 0;

                    GUI.skin.label.normal.textColor = UnityEngine.Color.white;

                    GUI.skin.label.fontSize = 10;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400, 200), $"MG: {this}\nT: {minigame_current_type}\nSESSION: {minigame_session}\nTARGET: {go}\nITEM: {minigame_currentItem_syncinfo}" );
                    
                }




                GUI.skin.label.normal.textColor = UnityEngine.Color.white;
                GUI.skin.label.fontSize = ogfont;

            }
        }
    }
#endif

    private void Update() {

        // its not me and its alive
        if (IsAlive()) {
            if (Net.IsServer) { // server-only
                if (!IsLocal) {
                    if (IsInMinigame) {
                        if (minigame_currentItem != null && ItemSync.CheckIfBodyReachThisItem(minigame_currentItem, body)) {
                            if (minigame_currentItem.IsManualDefibrillator() && minigame_currentItem.battery.hasCharge) {
                                //OG: ManualDefibMinigame.Update V520dev1
                                minigame_currentItem.battery.DrainCharge(Time.deltaTime / 800f);
                            }
                        }
                    }
                }
            }
        }
        

    }


    void LateUpdate() {

        _UpdateFingerPointVisual();

    }
    void _UpdateFingerPointVisual() {


        if (is_pointingfingerat) {
            if (!Util.IsInWorld()  || (Time.realtimeSinceStartupAsDouble - this.is_pointingfingeratTIME > ScavPlayer.FINGERPOINTINGTIME_MAX )) {
                this.is_pointingfingerat = false;
                locationPingCircle.SetActive(false);
                locationPingArrow.SetActive(false);
            }
        } else {
            
        }
        if (locationPingCircle != null && locationPingArrow != null) {
            var ttttt = is_pointingfingeratTIME + Time.realtimeSinceStartupAsDouble;

            var circle_spr = locationPingCircle.GetComponent<SpriteRenderer>();
            var new_alpha = Mathf.MoveTowards(circle_spr.color.a, is_pointingfingerat ? 1 : 0, Time.deltaTime * 2f);

            var keep_anim = new_alpha != 0;
            if (locationPingCircle.activeSelf != keep_anim) {
                locationPingCircle.SetActive(keep_anim);
            }
            if (locationPingCircle.activeSelf) {
                var targetpos = is_pointingfingeratTARGET;
                var sine = ((float)Math.Sin(ttttt * 4.0));
                if (is_pointingfingeratTYPE == 1) {
                    targetpos += Vector2.up*(4 + sine *0.7f) ;
                }

                locationPingCircle.transform.position = KM.v3v2z(targetpos, -2.601f);
                locationPingCircle.transform.localScale = Vector3.one * (1f + sine * 0.3f) * 32f * 0.5f;

                circle_spr.color = playerColor.ToColorWithAlpha(
                    new_alpha
                );
            }

            var actually_do_arrow = body != null && keep_anim && IsConscious() && locationPingCircle.activeSelf && !KM.DistanceCheck(is_pointingfingeratTARGET, pos, 8f);
            if (locationPingArrow.activeSelf != actually_do_arrow) {
                locationPingArrow.SetActive(actually_do_arrow);
            }
            if (locationPingArrow.activeSelf) {
                var dir = KM.NormalTo(is_pointingfingeratTARGET, body.transform.position, out var _);
                locationPingArrow.transform.position = KM.v3v2z(((Vector2)body.transform.position - (dir * 4f) + (dir * (float)Math.Sin(ttttt * 4.0) * 1f)), -2.6f);
                locationPingArrow.transform.eulerAngles = new Vector3(0, 0, Vector2.SignedAngle(Vector2.right, dir) + 180f);

                var arrow_spr = locationPingArrow.GetComponent<SpriteRenderer>();
                arrow_spr.color = circle_spr.color;
            }

        }
    }


    public void DestroyCharacterIfNoLocal() {
        if (body != null) {
            if (body != PlayerCamera.main.body) {
                if (body == InvButton_get_body_MultiplayerPatch.focused_body) {
                    InvButton_get_body_MultiplayerPatch.focused_body = null;
                }
                if (WoundView.view != null && WoundView.view.body == body) {
                    WoundView.view.body = PlayerCamera.main.body;
                }


                Server_DropAllInventory();

                ScavPlayer.BodyToPlayerDict.Remove(body);

                NetBody.DestroyNPC(body); 
                body = null;
            }
        }
    }

    public void OnDestroy() {

        try {
            if (Util.IsInWorld() && body != null && Multiplayer.rules.DisconnectShouldSaveAnything) {
#if TESTING
                log.l($"Saved player state of {this}");
#endif
                // the suitcase is taken apart on the next descent. the file is what a later layer can load.
                PlayerCheckpoint.WriteOne(this, false);
                ServerMain.Server_LastPlayerStates[GetPersistentId()] = (new PlayerSavedState(body));
            }
        } catch (Exception e) {
            log.error($"{nameof(ScavPlayer)}.{nameof(OnDestroy)}: saving plr state: " + e.ToString());
        }


        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(this.playerId, out var currentPlayer) &&
            ReferenceEquals(currentPlayer, this)) {
            ScavPlayer.ClientIdToPlayerDict.Remove(this.playerId);
        }
        ServerMain._UpdateSpecialPlayerLists();
        SharedMain.ForceUpdatePlayerLists();
        try {
            OnPlayerLeft?.Invoke(this);
        } catch (Exception e) {
            log.error($"{nameof(ScavPlayer)}.{nameof(OnDestroy)}: OnPlayerLeft event: " + e.ToString());
        }
        try {
            if (Net.IsServer && Net.IsRunning) {
                var msg = $"{Chat.AddColorTags(playerColor, playerName)}<parsebreak> {Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:plr_left>")}";
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.ParseTaggedString(msg));
                Chat.Server_ChatAnnouncement(msg);
            }
        } catch (Exception e) {
            log.error($"{nameof(ScavPlayer)}.{nameof(OnDestroy)}: disconnect handling: " + e.ToString());
        }

#if TESTING
        if (log.verbose)   { log.l($"{this}: OnDestroy  {this.name}  islocal: {IsLocal}  hasbody: { body != null}"); }
#endif

        DestroyCharacterIfNoLocal();

        if (BasePlayer != null) {
            if (Net.ServerEntityManager != null) {
                BasePlayer.Destroy();
                BasePlayer = null;
            }
        }

        if (locationPingCircle) {
            Destroy(locationPingCircle);
        }
        if (locationPingArrow) {
            Destroy(locationPingArrow);
        }

#if DO_QUEUE_IDS
        if (clientId != 0 && !Net._QUEUED_PLAYER_IDS_TO_REUSE.Contains(clientId))
            Net._QUEUED_PLAYER_IDS_TO_REUSE.Enqueue(clientId);
#endif
    }





}
