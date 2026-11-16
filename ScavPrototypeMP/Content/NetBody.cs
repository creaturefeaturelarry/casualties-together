using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Together;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;














public class BodyDamageEvent {
    public override string ToString() {
        if (this == null) return "";
        return $"BodyDamageEvent({type}, {attackerBody.ToSafeString()} {attackerBuilding.ToSafeString()} {victim.ToSafeString()})";
    }

    public enum DamageEventType {
        CustomDeathMessage,
        PlayerAttack,
        BuildingAttack,
        Revive,
        Environment,
    }
    public DamageEventType type;
    public DateTime timestamp;
    public DateTime expirationTime;

    public bool IsPlayerAttack() {
        return type == DamageEventType.PlayerAttack && attackerBody != null;
    }
    public bool IsBuildingAttack(out SyncInfoGameObjectTracker tracker) {
        tracker = null;
        var b = (
                type == DamageEventType.PlayerAttack // was this a misinput?
                || type == DamageEventType.BuildingAttack
            ) && attackerBuilding != null && attackerBuilding.TryGetTracker(out tracker);
        return b;
    }
    public bool IsSuicide() {
        return type == DamageEventType.PlayerAttack && attackerBody == victim;
    }

    public Dictionary<string, object> data;

    public NetBody attackerBody;
    public BuildingEntity attackerBuilding;
    public NetBody victim;
    public Vector2 attackerPos;
    public Vector2 victimPos;

    public Item weapon;
    public string weaponItemId;

    public string attackerBuildingId;

    public float damage;

    public string deathMessage;

    public BodyDamageEvent() {
    }
    public BodyDamageEvent(string deathText, NetBody target, double expiration = 30) {
        this.type = DamageEventType.CustomDeathMessage;
        victim = target;
        if (target != null) {
            victimPos = target.position;
        }
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(expiration);

        deathMessage = deathText;
    }
    public BodyDamageEvent(DamageEventType type, NetBody target) {
        this.type = type;
        victim = target;
        if (target != null) {
            victimPos = target.position;
        }
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(30);
    }
    public BodyDamageEvent(NetBody evil, NetBody target, Item weapon, float dmg) {
        this.type = DamageEventType.PlayerAttack;
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(30);

        attackerPos = evil.position;
        victimPos = target.position;
        victim = target;
        attackerBody = evil;
        this.weapon = weapon;
        if (weapon != null) {
            weaponItemId = weapon.id;
        }
        this.damage = dmg;

        attackerBuildingId = evil.ToString();

    }
    public BodyDamageEvent(BuildingEntity evil, NetBody target, float dmg) {
        this.type = DamageEventType.BuildingAttack;
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(20);

        attackerPos = evil.transform.position;
        victimPos = target.position;
        victim = target;
        attackerBuilding = evil;
        this.damage = dmg;

        attackerBuildingId = evil.id;

    }
}













public class NetBody : MonoBehaviour {
    public static implicit operator Body(NetBody nb) => nb.body;

    #region STATIC_NETBODY_GETTERS

    public static bool TryGetLocalNetBody(out NetBody nb) {
        if (PlayerCamera.main != null && PlayerCamera.main.body != null && PlayerCamera.main.body.TryGetComponent(out nb))
            return true;
        nb = null;
        return false;
    }
    public static NetBody GetLocalNetBodyNullable() {
        if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
            return PlayerCamera.main.body.GetNetBody();
        }
        return null;
    }

    public static (NetBody, float) GetNearestBodyFallbackToDead(Vector2 pos) {
        NetBody nearestAliveCon = null;
        float nearestAliveConDist = float.PositiveInfinity;

        NetBody nearestAlive = null;
        float nearestAliveDist = float.PositiveInfinity;

        NetBody nearestAny = null;
        float nearestAnyDist = float.PositiveInfinity;

        foreach (var npc in NetBody.all_instances) {
            var dist = KM.DistanceSqr(npc.transform.position, pos);

            if (npc.body.alive && npc.body.conscious) {
                if (dist < nearestAliveConDist) {
                    nearestAliveConDist = dist;
                    nearestAliveCon = npc;
                }
            }

            if (npc.body.alive) {
                if (dist < nearestAliveDist) {
                    nearestAliveDist = dist;
                    nearestAlive = npc;
                }
            }

            if (dist < nearestAnyDist) {
                nearestAnyDist = dist;
                nearestAny = npc;
            }
        }

        if (nearestAliveCon != null)
            return (nearestAliveCon, nearestAliveConDist);

        if (nearestAlive != null)
            return (nearestAlive, nearestAliveDist);

        return (nearestAny, nearestAnyDist);
    }

    public static (NetBody, float) GetNearestBodyPrioritizeAlive(Vector2 pos) {
        float nearest = float.PositiveInfinity;
        NetBody nearestnpc = null;
        foreach (var npc in NetBody.all_instances) {

            var dist = KM.DistanceSqr(npc.transform.position, pos);
            var d2 = dist;

            if (!npc.body.alive) {
                d2 += 200;
            }

            if (!npc.body.conscious) {
                d2 += 200;
            }

            if (d2 < nearest) {
                nearest = dist;
                nearestnpc = npc;
            }
        }
        return (nearestnpc, nearest);
    }



    public static (NetBody, float) GetNearestBody(Vector2 pos, bool must_be_alive = true, bool must_be_conscious = false) {
        float nearest = float.PositiveInfinity;
        NetBody nearestnpc = null;
        foreach (var npc in NetBody.all_instances) {

            if (must_be_alive) {
                if (!npc.body.alive) {
                    continue;
                }
            }
            if (must_be_conscious) {
                if (!npc.body.conscious) {
                    continue;
                }
            }

            var dist = KM.DistanceSqr(npc.transform.position, pos);
            if (dist < nearest) {
                nearest = dist;
                nearestnpc = npc;
            }
        }
        return (nearestnpc, nearest);
    }

    public static (NetBody, float) GetNearestConsciousBodyToThisBody(NetBody thisone) {
        float nearest = float.PositiveInfinity;
        NetBody nearestnpc = null;
        foreach (var nb in NetBody.all_instances) {
            if ((nb.body.conscious || nb.body.sleeping) && thisone != nb) {
                var dist = KM.DistanceSqr(nb.transform.position, thisone.transform.position);
                if (dist < nearest) {
                    nearest = dist;
                    nearestnpc = nb;
                }
            }
        }
        return (nearestnpc, nearest);
    }


    public static void GetBodiesInRadius(Vector2 pos, float radius, List<NetBody> list) {
        radius *= radius; // square it

        foreach (var body in NetBody.all_instances) {
            if (KM.DistanceCheck_PreSqr(body.transform.position, pos, radius)) {
                list.Add(body);
            }
        }
    }
    public static List<NetBody> GetBodiesInRadius(Vector2 pos, float radius) {
        radius *= radius; // square it

        var b = new List<NetBody>();
        foreach (var body in NetBody.all_instances) {
            if (KM.DistanceCheck_PreSqr(body.transform.position, pos, radius)) {
                b.Add(body);
            }
        }
        return b;
    }

    public static NetBody GetNetBodyFromId(knetid netId) => _PRIV_NetIdToNetBody.GetValueSafe(netId);
    public static bool TryGetNetBodyFromId(knetid netId, out NetBody nb) => _PRIV_NetIdToNetBody.TryGetValue(netId, out nb);




    public static readonly List<NetBody> all_instances = new();
    public static IReadOnlyDictionary<knetid, NetBody> NetIdToNetBody => _PRIV_NetIdToNetBody;
    private static readonly Dictionary<knetid, NetBody> _PRIV_NetIdToNetBody = new();
    void OnEnable() => all_instances.Add(this);
    void OnDisable() => all_instances.Remove(this);

    #endregion



    public double _lasttime_nobraindamage_from_circulation = 100000000;
    public SyncBody syncBody { get; internal set; }
    public knetid netId {
        get {
            if (Net.IsRunning) {
                if (syncBody == null) {
                    throw new Exception("NetBody has no SyncBody!");
                }
            } else {
                if (syncBody == null) {
                    return 0;
                }
            }
            return syncBody.Id;
        }
    }



    public void RollAttackRandom() {
        if (syncBody == null) return;
        UnityEngine.Random.InitState((this.syncBody.drng_attackCounter.Value++) * (this.netId + 1));
    }
    public void RollUseRandom() {
        if (syncBody == null) return;
        UnityEngine.Random.InitState((this.syncBody.drng_useCounter.Value++) * (this.netId + 1));
    }
    public void RollDamageRandom() {
        if (syncBody == null) return;
        UnityEngine.Random.InitState((this.syncBody.drng_damageCounter.Value++) * (this.netId + 1));
    }

    public Dictionary<string, string> MindMemory = new();
    public List<int> RecipesHasCrafterBefore = new();//TODO Consider moving RecipesHasCrafterBefore to MindMemory
    public void ResetMind() {
        MindMemory.Clear();
        RecipesHasCrafterBefore.Clear();
    }

    // server only!
    readonly MaxCapacityQueue<BodyDamageEvent> DamageHistory = new(100);
    public BodyDamageEvent GetLastDamageEvent() {

        List<BodyDamageEvent> events;
        { // forget everything before last revive
            events = plr.netBody.DamageHistory.JustGiveTheQueue().ToList();
            var foundRevive = events.FindIndex(x => x.type == BodyDamageEvent.DamageEventType.Revive);
            if (foundRevive != -1) {
                events = events.Skip(foundRevive).ToList();
            }
        }

        var unexpired = events.Where(x => x.expirationTime > DateTime.UtcNow).Reverse().ToList();

        if (unexpired.Count > 0) {
            return unexpired[0];
        }

        return null;
    }
    public void OnDamageEvent(BodyDamageEvent dmg) {
        DamageHistory.Enqueue(dmg);
#if !RELEASE
        log.l($"DAMAGE LOG: EVENT: {dmg}");
#endif
    }




    #region EVENTS

    public static event Action<ScavPlayer> OnPlayerSleep;
    public static event Action<ScavPlayer> OnPlayerDeath;

    #endregion

    public bool IsLocal => Util.IsBodyLocal(body);


    public string playerName {
        get {
            if (IsPlayer) return plr.playerName;
            return bodyName;
        }
    }

    public override string ToString() {
        if (this == null) {
            return "NB(NULL)";
        }
        if (IsPlayer) {
            return $"NB(ID:{syncBody?.Id}, PLRID:{plr.playerId}, name:{playerName})";
        } else {
            return $"NB(ID:{syncBody?.Id}, name:{playerName})";
        }
    }

    public double lastTimeBark;
    public double lastTimeCprPress;
    public int cpr_cur_perfect_press_count;
    public bool chip_punishment_protocol = false;
    // scatter penalty: mood offset 0..-30, -1 / 5s out of range, +2 / 5s when back
    public float scatterMoodOffset = 0f;
    float scatterMoodTick;

    void Awake() {

        {
            body = this.GetComponent<Body>();

            body.moveDir = Vector2.zero;
            body.crouching = false;

            localoverride_showInfection = new bool[body.limbs.Length];

            visual = this.GetOrAddComponent<CharStatusVisuals>();
        }

        this.idle_anim_allowed = true;

    }
    internal void OnFoundNetPlayerInitFinish() {
        plr.body = body;
        ApplyNameAndColor(playerName, plr.playerColor);

        unchipped = plr.unchipped;

        ScavPlayer.BodyToPlayerDict[body] = plr;
    }




    public void BackupLocalPlayerInit() {

        if (plr == null && Net.IsRunning && ScavPlayer.LocalPlayer != null) {
            plr = ScavPlayer.LocalPlayer;

#if !RELEASE
            Plugin.log.LogWarning("BackupLocalSCIInit ACTUALLY WORKED YAY!!!!!!!");
#endif

            OnFoundNetPlayerInitFinish();
        }

    }

    protected void Start() {

        _lastHealthPacketReceiveTick = (ushort)(Net.ServerTick - 1);
        _lastSyncPacketReceiveTick = (ushort)(Net.ServerTick - 1);


        if (plr == null) {
            plr = ScavPlayer.GetPlayerFromBody(body);
            if (plr != null) {
                OnFoundNetPlayerInitFinish();
            }

            if (IsLocal) {
                BackupLocalPlayerInit();
            }
        }

        {
            InvokeRepeating("SlowerUpdate", UnityEngine.Random.value * 2, 0.1f);


            body.talker.text.richText = false;

#if !RELEASE
            if (!IsLocal && !this.IsPlayer) {//to make thumbnail screenshot
                body.targetLookPos = pos + Vector2.right * 100 * (UnityEngine.Random.value > 0.5f ? -1 : 1) + Vector2.up * ((UnityEngine.Random.value - 0.5f) * 2);
                if (UnityEngine.Random.value > 0.5f) {
                    body.happiness = UnityEngine.Random.Range(-80, 200);
                }
                body.GetFace().eyeLight.enabled = false;

                if (UnityEngine.Random.value > 0.7f)
                    body.SwitchDir();
            }
#endif
        }
    }

    public double timeOfDeath { get; protected set; }
    public double timeHasBeenDead => Time.unscaledTimeAsDouble - timeOfDeath;

    protected void OnDeath() {
        ServerMain._ded_server_switch_counter = 0;

        try {
            if (plr != null) {
                ServerMain.OnPlayerDeath(plr);
                if (Net.IsServer)
                    MedicalSync.Server_QueueSendCharacterHealth(this, true);

                plr.minigame_current_type = 255;
                plr.minigame_currentItem = null;

                plr.locationPingArrow.SetActive(false);
                plr.locationPingCircle.SetActive(false);

                NetBody.OnPlayerDeath?.Invoke(plr);
            }
        } catch (Exception e) {
            log.error(e);
        }

        {
            timeOfDeath = Time.unscaledTimeAsDouble;
        }

    }
    protected void OnSleep() {

        if (Multiplayer.rules.DisableSleep) {
            return;
        }

        if (plr != null) {
            ServerMain.OnPlayerSleep(plr);

            NetBody.OnPlayerSleep?.Invoke(plr);

        }

    }


    public float irradiateIntensity = 0;
    public float timeSinceRadUpdate = 0;

#if !RELEASE

    void OnGUI() {

        if (log.verbose) {
            var ogfont = GUI.skin.label.fontSize;

            var pos = (Vector2)Camera.main.WorldToScreenPoint((Vector2)body.targetLookPos);
            pos = new Vector2(pos.x, Screen.height - pos.y);

            //if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) 
            {
                const float bsize = 17;
                const float elsize = 13;

                float y = -elsize * 1.5f;

                GUI.skin.label.normal.textColor = UnityEngine.Color.white;

                GUI.skin.label.fontSize = 10;
                GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), $"CURSOR: {this.ToString()}");
                y += elsize;
            }

            GUI.skin.label.normal.textColor = UnityEngine.Color.white;
            GUI.skin.label.fontSize = ogfont;
        }

    }

#endif


    void Update() {
        HugSession.TickVelocity();
        HeadpatSession.Tick();

        {//OG: PlayerCamera.HandleRadiationOverlay
            if (timeSinceRadUpdate > 0.05f) {
                irradiateIntensity -= Time.unscaledDeltaTime * ((irradiateIntensity > 1f) ? 2f : 0.5f);
            }
            timeSinceRadUpdate += Time.unscaledDeltaTime;
        }

        if (plr == null && Net.IsRunning && IsLocal) {
            BackupLocalPlayerInit();
            if (log.verbose) log.l($"Still searching for local SCI wtf");
        }

        {
            // carriers and anyone being carried stay out of the idle sit. the carried body ragdolls.
            bool suppressIdleSit = !idle_anim_allowed || carrying_person != null || piggybacking_on != null;
            if (suppressIdleSit) {
                if (body.idleTime + Time.deltaTime > 11f) {
                    body.idleTime = 11f;
                }
                ClearSitAnims(body);
            }

            if (!Util.IsBodyLocal(body) && body.conscious) {
                Item item = body.GetItem(body.handSlot);

                //OG: PlayerCamera.HandleAttacks V5t2 

                if (prev_useiteminhand) {
                    if ((!item || !item.Stats.usableWithLMB || item.Stats.autoAttack)) {
                        body.UseItemInHand();
                    }
                } else {
                    if (prev_useiteminhand != useiteminhand) {
                        if ((!item || !item.Stats.usableWithLMB || !item.Stats.autoAttack)) {
                            body.UseItemInHand();
                        }
                    }
                }
                prev_useiteminhand = useiteminhand;
            }
        }


        if (body.alive) {

        } else {
            body.halfMinuteCheckTime = 0; // specifically to not trigger brain damage problems, it shouldnt cause any other problems
        }

        if (Net.IsServer) {
            TickScatterMoodPenalty();
        }


    }

    void TickScatterMoodPenalty() {
        if (body == null || !IsPlayer) {
            return;
        }
        if (!body.alive || !SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive()) {
            if (scatterMoodOffset != 0f || scatterMoodTick != 0f) {
                if (body.alive) {
                    ApplyScatterMoodOffset(0f);
                } else {
                    scatterMoodOffset = 0f;
                }
                scatterMoodTick = 0f;
            }
            return;
        }

        if (chip_punishment_protocol) {
            body.eyePanicTime = 1f;
            // old radiation tick (replaced by mood offset):
            // body.radiationSickness += Time.deltaTime;
            scatterMoodTick += Time.deltaTime;
            while (scatterMoodTick >= 5f) {
                scatterMoodTick -= 5f;
                ApplyScatterMoodOffset(Mathf.Max(scatterMoodOffset - 1f, -30f));
            }
            return;
        }

        // back in range - clear the offset at +2 per 5 seconds
        if (scatterMoodOffset >= 0f) {
            scatterMoodOffset = 0f;
            scatterMoodTick = 0f;
            return;
        }
        scatterMoodTick += Time.deltaTime;
        while (scatterMoodTick >= 5f && scatterMoodOffset < 0f) {
            scatterMoodTick -= 5f;
            ApplyScatterMoodOffset(Mathf.Min(scatterMoodOffset + 2f, 0f));
        }
    }

    void ApplyScatterMoodOffset(float nextOffset) {
        float delta = nextOffset - scatterMoodOffset;
        scatterMoodOffset = nextOffset;
        if (delta == 0f || body == null) {
            return;
        }
        body.happiness = Mathf.Clamp(body.happiness + delta, -100f, 100f);
        last_mood = body.happiness;
        MedicalSync.Server_QueueSendCharacterHealth(this, false);
    }

    protected void LateUpdate() {
        HugSession.TickArms();

        // consoleScript noclip writes carrier transform in Update - after FixedUpdate pin.
        if (piggybacking_on != null && body != null) {
            HandlePiggybackUpdate();
        }

        if (!last_alive && !body.alive) { // because body is not losing its temperature when dead for some reason
            body.temperature = Mathf.Lerp(36.6f, WorldGeneration.world.ambientTemperature, Mathf.Clamp01((float)((Time.timeAsDouble - timeOfDeath) / 30.0)));
            body.reversedControls = false;
        } else {
            if (!SharedMain.LocalWorldIsGenerated) return;

            if (body.happiness < last_mood && NearBodies.Count > 0) {
                var happier_body_count = 0;
                foreach (var ob in NearBodies) {
                    if (ob.body.happiness > body.happiness) {
                        happier_body_count++;
                    }
                }

                var l = Mathf.Min(((float)happier_body_count) / 8f, 0.5f); // i want this to cap out at 4 players near
                body.happiness = Mathf.Lerp(body.happiness, last_mood, l);
            }

            last_mood = body.happiness;




            if (NearBodiesDistanceIs5) {
                //TODO if sleeping bump up the sleep qual
                // BUT CURRENTLY NOT POSSIBLE CUZ ITS DONE IN ONE FRAME LIKE THAT IN THE Body AA
            }

            NetBody cprPacient = null;
            var doCPR = IsPlayer && CPRHandler.PlayerIsPerformingCPR(plr, out cprPacient);

            if (doCPR) {
                if (plr != null) {
                    var targetHeight = 0f;
                    if (plr.BasePlayer.ButtonAttack.Value) {
                        targetHeight = 1f;
                    }
                    cprPress = Mathf.Lerp(cprPress, targetHeight, Time.deltaTime * CPRHandler.GetPushSpeed(this, cprPacient, plr.BasePlayer.ButtonAttack.Value));
                }

                if (cprPacient.body.IsRagdolling() || cprPacient.body.crouching) {
                    body.crouching = true;
                }

            } else {
                if (lastCpr) {
                    cprPress = 0;
                    body.armsAnimator.StopPlayback();
                    body.armsAnimator.Play("Grounded");
                }
            }
            lastCpr = doCPR;

        }
    }
    internal float cprPress = 0;
    bool lastCpr = false;


    public void HearinglossDistortMessage(NetBody yapper, ref string message, ref string chattag) {

        // repeat 100 1 say_HELLO_HELLO_HELLO_HELLO_HELLO_HELLO_HELLO

        string distortionChars = GlobalDark.main.distortionChars;

        StringBuilder stringBuilder = new StringBuilder(message);

        if (Multiplayer.rules.HearingLossChat) {

            float distortionAmount = 0;
            if (Voicechat.VCRULE_mindwiperule && this.body.IsMindwiped()) {
                distortionAmount = 0.94f;
            } else {
                // brain damage means ur brain cant comprehend anything 
                distortionAmount = Mathf.Max(distortionAmount, KM.RemapClamped(this.body.brainHealth, 85f, 0f, 0f, 0.8f));
            }


            // DISTORT BY BRAIN DAMAGE   repeat 100 1 say_HELLO_EVERYBODY_MY_NAME
            if (distortionAmount > 0) {
                for (int i = 0; i < message.Length; i++) {
                    if (!char.IsWhiteSpace(stringBuilder[i]) && UnityEngine.Random.value < distortionAmount) {
                        stringBuilder[i] = distortionChars[UnityEngine.Random.Range(0, distortionChars.Length)];
                    }
                }
            }
        }



        var doing_proximity = (Multiplayer.rules.OnlyProximityChat || (Multiplayer.rules.UnchippedProximityChat && this.unchipped));

        var hearability = 1.1f;

        var distance_to_yapper = 10f;
        if (doing_proximity) {
            distance_to_yapper = Vector2.Distance(yapper.pos, this.pos);

            if (distance_to_yapper > Voicechat.MAX_HEAR_DISTANCE) {
                message = "";
                return;
            }

            hearability = Mathf.Clamp01(Body.Remap(distance_to_yapper, Voicechat.MAX_HEAR_DISTANCE * 0.4f, Voicechat.MAX_HEAR_DISTANCE, 1.01f, 0.001f));
        }

        if (Multiplayer.rules.HearingLossChat) {
            if (this.body.hearingLoss > 30f) {
                hearability =
                    Mathf.Min(
                        hearability,
                        Mathf.Clamp01(Body.Remap(this.body.hearingLoss, 30f, 100f, 1f, 0f))
                    );
            }
        }

        if (hearability < 1f) {
            for (int i = 0; i < message.Length; i++) {
                if (Chat.CharCanBeReplacedWithBlank(stringBuilder[i]) && UnityEngine.Random.value > hearability) {
                    stringBuilder[i] = '_';
                }
            }
        }



        message = stringBuilder.ToString();


        if (String.IsNullOrWhiteSpace(chattag)) {
            if (doing_proximity) {
                if (distance_to_yapper > 60) {
                    chattag = Lang.MarkMsgAsLocaleKey("plr_chattag_far");
                }
            }
        }


    }

    public void DoMouseHoverTooltip(ref string tooltipName_text, ref string tooltipDescription_text) {
        tooltipName_text = this.bodyName;
        if (!this.body.alive) {
            tooltipName_text += Lang.Get("plr_hover_dead");
        } else if (this.body.sleeping) {
            tooltipName_text += Lang.Get("plr_hover_sleeping");
        } else if (!this.body.conscious) {
            tooltipName_text += Lang.Get("plr_hover_unconscious");
        }

        if (log.verbose) {
            tooltipName_text += " (NPC ID: " + this.netId + " )";
            if (this.IsPlayer) {
                tooltipName_text += " (PLR ID: " + this.plr.playerId + " )";
            }

            tooltipDescription_text = "";
            if (this.IsPlayer) {
                tooltipDescription_text = "\n!!! " + (Net.IsClient ? "CLIENT" : "SERVER") + " looking at " + (this.netId != 0 ? "CLIENT" : "SERVER") + "\n";
            }
            tooltipDescription_text += "body.alive: " + this.body.alive + "\n";
            tooltipDescription_text += "body.conscious: " + this.body.conscious + "\n";
            tooltipDescription_text += "body.standing: " + this.body.standing + "\n";
            tooltipDescription_text += "body.isCriticallyDying: " + (this.body.isCriticallyDying) + "\n";
            tooltipDescription_text += "body.isDying: " + (this.body.isDying) + "\n";

            if (this.IsPlayer) {
                if (this.plr.minigame_current_type == 255) {
                    tooltipDescription_text += "plr.minigame: None (255)\n";
                } else {
                    if (MinigameMPManager.TryGetMinigameTypeFromId(this.plr.minigame_current_type, out var mgtype)) {
                        tooltipDescription_text += $"plr.minigame: {mgtype.Name}  ({this.plr.minigame_current_type})\n";
                    } else {
                        tooltipDescription_text += "plr.minigame: UNKNOWN  ({this.plr.minigame_current_type})\n";
                    }
                }
                tooltipDescription_text += "pointing: " + this.plr.is_pointingfingerat + "\n";
                tooltipDescription_text += "healing: " + this.plr.WoundviewTargetNetBodyId + "\n";
            }
            tooltipDescription_text += "carrying: " + (this.carrying_person) + "\n";
            tooltipDescription_text += "piggyback: " + (this.piggybacking_on) + "\n";
            tooltipDescription_text += "piggyback stack: " + (CountPiggybackStackDown()) + "\n";



            tooltipDescription_text += "temperature: " + Math.Round(this.body.temperature, 2) + "\n";


            if (this.IsPlayer) {
                if (Net.IsServer) {
                    tooltipDescription_text += "did_give_spawn_location: " + (this.plr.ServerPlayerState.did_give_spawn_location) + "\n";
                    tooltipDescription_text += "is_loaded_in: " + (this.plr.ServerPlayerState.is_loaded_in) + "\n";
                }
                tooltipDescription_text += "exist_time: " + ((int)this.plr.levelPlayTime) + "\n";
                if (this.plr.IsInMinigame) {
                    tooltipDescription_text += "mg_session: " + (this.plr.minigame_session) + "\n";
                    tooltipDescription_text += "mg_obj: " + (this.plr.minigame_targetobject) + "\n";
                    tooltipDescription_text += "mg_item: " + (this.plr.minigame_currentItem) + "\n";
                }
            }


            if (syncBody != null) {
                tooltipDescription_text += "ReadingRagdollData: " + syncBody.readingRagdollData.ToString() + "\n";
                if (syncBody.TryGetOwnerTick(out var tick)) {
                    tooltipDescription_text += "SyncBody Tick: " + tick.ToString() + "\n";
                }
            }
            var coUtils = body.GetCoUtilsInstance();
            if (coUtils != null) {
                if (coUtils.activeOps != null) {
                    if (coUtils.activeOps.Count == 0) {
                    } else {
                        tooltipDescription_text += ("CoUtils:\n");
                        foreach (var item in coUtils.activeOps) {
                            tooltipDescription_text += (item.ToString()) + ("\n");
                        }
                    }
                }
            }

        } else {
            tooltipDescription_text = "<sprite index=20>Interact.";
        }
    }




    // shit that is getting called from transpilers
    public void CameraShakeOverrideFunc(float num) {
        if (body.IsBodyLocal()) {
            PlayerCamera.main.shaker.Shake(num);
        }
    }




    void OnDestroy() {
        // deferred Destroy can run after a species-swap replacement already owns the same
        // syncBody id - only unregister / tear down LES if this instance still owns it.
        SyncBody sb = syncBody;
        if (sb != null) {
            knetid id = sb.Id;
            if (_PRIV_NetIdToNetBody.TryGetValue(id, out var mapped) && ReferenceEquals(mapped, this)) {
                _PRIV_NetIdToNetBody.Remove(id);
            }
        }
        if (body != null) {
            ScavPlayer.BodyToPlayerDict.Remove(body);
        }

        if (MoodleManager.main != null && MoodleManager.main.body == body && Util.TryGetLocalBody(out var locbody)) { // fix nullreference if player disconnects
            MoodleManager.main.body = locbody;
        }

        if (Net.IsServer && sb != null) {
            // species mesh rebuild nulls syncBody first so the entity stays alive
            if (sb.nb == null || ReferenceEquals(sb.nb, this)) {
                syncBody = null;
                sb.Destroy();
            }
        }

    }








    public byte butchering_dropmeat_skip_counter = 0;

    #region NET_STUFF



    public bool _SERVER_healthsync_skip_frame = false;
    // same tick as the last snapshot still applies. trap confirms use this.
    internal bool healthConfirm;
    public ushort _lastHealthPacketReceiveTick = 0;
    public ushort _lastSyncPacketReceiveTick = 0;
    public void SetNetIgnoreTime(float additionaltime = 0f) {
        if (Net.IsClient) {
            var t = ClientMain.RTT_IN_SECONDS_with_margin;
            if (IsPlayer && !plr.IsLocal) {
                t += (float)plr.rtt_in_seconds_with_margin;
            }
            additionaltime += t;
        } else {
            if (this.IsPlayer) {
                additionaltime += (float)plr.rtt_in_seconds_with_margin;
            }
        }
        var newTick = (ushort)(Net.ServerTick + ((additionaltime) * Net.ServerEntitySendRate));
        var seqDiff = LiteEntitySystem.Utils.SequenceDiff(newTick, _lastSyncPacketReceiveTick);
        if (seqDiff <= 0) {
        } else {
            _lastSyncPacketReceiveTick = newTick;
        }
    }
    public void SetNetHealthSyncIgnoreTime(float additionaltime = 0f) {
        // host snapshot is the injury. a future tick here made fresh packets look old.
    }



    #endregion


    // but not really
    #region PLAYER_VARIABLES 

    public bool IsPlayer => plr != null;
    public ScavPlayer plr { get; internal set; }
    public CharStatusVisuals visual { get; protected set; }
    public Rigidbody2D rb => body.rb;

    public void SetPlayer(ScavPlayer newPlayer) {
        ScavPlayer.BodyToPlayerDict.Remove(body);
        if (IsPlayer) {
            this.plr.body = null;
        }
        plr = newPlayer;
        if (plr != null) {
            plr.body = this.body;
            ScavPlayer.BodyToPlayerDict.Add(plr.body, plr);
            ApplyNameAndColor(plr.playerName, plr.playerColor);
            SetUnchipped(plr.unchipped);
        }
    }


    private string _cur_bodyname = null;
    public string bodyName {
        get {
            return _cur_bodyname ?? syncBody?.Id.ToString() ?? "NO_NAME_BODY";
        }
        set {
            ApplyNameAndColor(value, color);
        }
    }
    protected Color24 _color;
    public Color color {
        get {
            if (IsPlayer) {
                return plr.playerColor;
            }

            return _color;
        }
        set {
            _color = value;
        }
    }

    public void ApplyNameAndColor(string name, UnityEngine.Color color) {
        _cur_bodyname = name;
        this.color = color;
        this.body.name = "Body_" + bodyName;

        visual.ApplyNameAndColor();
    }



    #endregion

    public static void SpawnStarterPack(Body body) {
        //OG: WorldGeneration.WorldPlacePlayer V7.0.1
        if (WorldGeneration.world.totalTraveled <= 0 && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.None && !SaveSystem.loadedRun) {
            switch (WorldGeneration.GetRunSettingInt("startingsupplies")) {
                case 1:
                body.PickUpItem(Utils.Create("emergencylight", body.transform.position, 0f).GetComponent<Item>(), 3, force: true);
                break;
                case 2:
                body.PickUpItem(Utils.Create("lantern", body.transform.position, 0f).GetComponent<Item>(), 3, force: true);
                body.PickUpItem(Utils.Create("dogfood", body.transform.position, 0f).GetComponent<Item>(), 4, force: true);
                body.PickUpItem(Utils.Create("waterbottle", body.transform.position, 0f).GetComponent<Item>(), 5, force: true);
                body.PickUpItem(Utils.Create("trashbag", body.transform.position, 0f).GetComponent<Item>(), 1, force: true);
                break;
            }
            if (DateTime.Now.Month == 12) {
                Utils.Create("present", (Vector2)body.transform.position + Vector2.right * UnityEngine.Random.Range(-2f, 2f), 0f);
            }
        }
    }

    public static NetBody CreateNewNPC(Vector2 pos, string name = null, SyncBody sb = null) {

        _Internal_RegisterLiteNetLib(null, null, ref sb);

        var nb = _Internal_CreateNetBody(sb, pos, name, null);
        var chara = nb.chara;

        chara.name = $"NPC_ID_{sb.Id}_NAME_{name}";

        nb.ApplyNameAndColor(name, nb.color);

        return nb;
    }
    internal static NetBody CreateNewPlayerCharacter(ScavPlayer plr, SyncBody sb = null) {
        if (plr.body == null) {
            var had_sb = sb != null;
            _Internal_RegisterLiteNetLib(null, plr, ref sb);

            if (sb.nb == null) {
                sb.nb = _Internal_CreateNetBody(sb, Body_PlaceBody_MultiplayerPatch.SpawnLocation, plr.playerName, plr);
            }
            var nb = sb.nb;

            nb.SetPlayer(plr);

            if (Net.IsServer && had_sb) {
                var pid = plr.GetPersistentId();

                bool hasSavedPlrState = ServerMain.Server_LastPlayerStates.TryGetValue(pid, out var val);
                // a practice world ignores the file, so the body matches a player who has no save.
                bool hasPlayerFile = !SavesystemPatch.PreserveSave && !hasSavedPlrState && PlayerCheckpoint.HasPlayerFile(plr);
                bool hasSuitcase = hasSavedPlrState || hasPlayerFile;
                if (plr.LateJoin) {
                    bool endLab = WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 11;
                    // endLab: brand-new late joiners spectate. Rejoiners with a suitcase keep playing.
                    // also honor the global LateJoinSpectate rule on any layer.
                    bool forceSpectate = (!hasSuitcase && Multiplayer.rules.LateJoinSpectate)
                        || (endLab && !hasSuitcase);

                    if (forceSpectate) {
                        string why = endLab && !hasSuitcase
                            ? "late-joined EndLab without suitcase"
                            : "late-joined and LateJoinSpectate is on";
                        if (log.verbose) log.l($"Killing {plr} cuz {why}.");

                        plr.body.GetHead().Dismember();
                        plr.body.GetLowerTorso().Dismember();
                        plr.body.brainHealth = 0;
                        plr.body.heartRate = 0;
                        plr.body.bloodOxygen = 0;
                        plr.body.bloodPressure = 0;
                        MedicalSync.Server_QueueSendCharacterHealth(nb, true);
                        Util.CallLambdaWhen(() => {
                            if (!Util.IsInWorld()) {
                                return true; // break out if not in world
                            }
                            return plr.ServerPlayerState.is_loaded_in; // trigger only when fully loaded in
                        }, () => {
                            Util.DelayCallLambda(1, () => {
                                Multiplayer.Server_SendSimpleMessageToOneClient(NetmsgId.CLIENT_forcespectator, plr.playerId);
                                string alert = endLab && !hasSuitcase
                                    ? "Late join into Ending Lab — spectator only (no suitcase)."
                                    : "Late join, server put you in spectator mode.";
                                plr.Server_DoAlertSingle(alert, true, true);
                            });
                        });
                    }
                }

                if (!hasSavedPlrState && !hasPlayerFile) {
                    SpawnStarterPack(plr.body);
                }
            }

            return nb;
        } else {
            _Internal_RegisterLiteNetLib(plr.netBody, plr, ref sb);
        }
        return plr.netBody;
    }
    internal static void _Internal_RegisterLiteNetLib(NetBody nb, ScavPlayer plr, ref SyncBody sb) {
        if (sb == null && nb != null) {
            sb = nb.syncBody;
        }
        if (sb == null) {
            if (Net.IsServer) {
                sb = Net.ServerEntityManager.AddEntity<SyncBody>(e => {
                    e.nb = nb;
                    if (plr != null) {
                        e.SetParent(plr.BasePlayer);
#if FULLGAME
                        CharacterSpawn.ApplySelectedToSyncBody(e, plr);
#endif
                    }
                });
            } else {
                throw new Exception("Server Only.");
            }
        }
#if FULLGAME
        else if (Net.IsServer && plr != null) {
            CharacterSpawn.ApplySelectedToSyncBody(sb, plr);
        }
#endif
        if (nb != null) {
            if (sb != null) {
                nb.syncBody = sb;
                NetBody._PRIV_NetIdToNetBody[sb.Id] = nb;
            }
        }
#if !RELEASE 
        log.l($"_Internal_RegisterLiteNetLib: sbId:{sb?.Id} - {nb}  stack: {new StackTrace()}");
#endif  
    }

    internal static NetBody _Internal_CreateNetBody(SyncBody sb, Vector2 pos, string name = null, ScavPlayer plr = null) {
#if !RELEASE
        log.l($"Creating Netbody: {sb} - {name}  stack: {new StackTrace()}");
#endif  

        if (name == null) {
            name = "bot_" + sb.Id.ToString();
        }

#if FULLGAME
        int chr = CharacterSpawn.ResolveCharForPlayer(plr, sb);
        var chara = CharacterSpawn.InstantiateCharacter(chr, name);
#else
        var chara = UnityEngine.Object.Instantiate(ServerMain.CHARACTER_PREFAB, Vector3.zero, Quaternion.identity);
        chara.name = "Character_" + name;
        chara.SetActive(true);
#endif

        var body = chara.GetComponentInChildren<Body>();
        body.name = "Body_" + name;
        body.targetLookPos = new Vector2(1000, 460);

        body.transform.position = pos;
#if FULLGAME
        body.charType = CharacterSpawn.ClampChar(chr);
        body.skills.Setup(body.charType);
        if (plr != null) {
            plr.selectedCharacter = (byte)body.charType;
        }
#endif

        var nb = body.GetOrAddComponent<NetBody>();
        sb.nb = nb;
        //nb.netId = sb.Id;
        if (sb != null) {
            nb.syncBody = sb;
#if FULLGAME
            if (Net.IsServer) {
                sb.characterSelected.Value = (byte)CharacterSpawn.ClampChar(chr);
            }
#endif
        }
        NetBody._PRIV_NetIdToNetBody[sb.Id] = nb;



        return nb;
    }

    // species swap rebuilds the mesh but must keep the SyncBody entity.
    // clears ownership so deferred OnDestroy cannot Destroy() SyncBody or wipe the
    // replacement's NetId map entry.
    internal static void DetachForMeshRebuild(NetBody npc) {
        if (npc == null) {
            return;
        }
        SyncBody sb = npc.syncBody;
        if (sb != null) {
            if (_PRIV_NetIdToNetBody.TryGetValue(sb.Id, out var mapped) && ReferenceEquals(mapped, npc)) {
                _PRIV_NetIdToNetBody.Remove(sb.Id);
            }
            if (ReferenceEquals(sb.nb, npc)) {
                sb.nb = null;
            }
        }
        npc.syncBody = null;
        npc.plr = null;
    }

    public static void DestroyNPC(NetBody npc) {
        if (npc != null) {
            if (npc.IsPlayer) {
                ScavPlayer.AllLivingPlayers.Remove(npc.plr);
                ScavPlayer.AllDeadPlayers.Remove(npc.plr);
            }
            if (npc.IsBodyLocal()) {
                return;
            }
            if (npc.transform.parent != null) {
                Destroy(npc.transform.parent.gameObject);
            }
            Destroy(npc.gameObject);
        }
    }
    public static void DestroyNPC(Body body) {
        if (body != null) {
            if (body.transform.parent != null) {
                Destroy(body.transform.parent.gameObject);
            }
            Destroy(body.gameObject);
        }
    }







    public bool alive => body.alive;
    public bool exercising => body.exercising;
    public Body body { get; private set; }
    public GameObject chara => this.transform.parent.gameObject;
    public Limb head => body.limbs[(int)Body.LimbNum.Head];

    private bool _unchipped = false;
    public bool unchipped {
        get {
            if (syncBody == null) {
                return _unchipped;
            }
            return syncBody.Unchipped.Value;
        }
        private set {
            _unchipped = value;
            if (syncBody != null) {
                syncBody.Unchipped.Value = value;
            }
            if (plr != null) {
                _unchipped = value;
            }
        }
    }


    public void SetUnchipped(bool unchipped) {

        _unchipped = unchipped;
        if (this.unchipped != unchipped) {
            this.unchipped = unchipped;
        }

        if (IsLocal) {
            Util.SetUnchipped(unchipped);
        }

        if (plr != null) {
            if (plr.unchipped != unchipped) {
                plr.SetUnchipped(unchipped);
            }
        }

    }


    public bool IsBodyLocal() {
        return body.IsBodyLocal();
    }

    public bool idle_anim_allowed = false; // TODO SYNC WITH DELTA 

    // basically Left mouse button substitute
    // also, this shouldnt be used for combat sync!!
    public bool useiteminhand; //  (this was the old attack sync system)

    public bool[] localoverride_showInfection;




    public Vector2 GetPosition() {
        if (!body.standing) {
            return body.limbs[(int)Body.LimbNum.UpTorso].rb.position;
        } else {
            return transform.position;
        }
    }
    public Vector2 GetHeadPos() {
        return body.limbs[(int)Body.LimbNum.Head].transform.position;
    }



    public static float DistanceForNearBodies = 40;
    public List<NetBody> NearBodies = new(); // only living btw
    public bool NearBodiesDistanceIs5 = false;
    protected bool last_alive = true;
    protected bool last_sleep = false;
    // realtimeSinceStartup when this body entered solo-sleep bonus; -1 when awake / together-sleep
    internal float soloSleepBonusStart = -1f;
    void SlowerUpdate() {
#if FULLGAME
        // body.Update / WakeUp / HandleBody all read charType continuously (milky sleep,
        // dune immunity, insulation, …) - keep it pinned to Ready/synced species, not just eats.
        if (IsPlayer && body != null) {
            CharacterSpawn.EnsureBodySpecies(body);
        }
#endif

        //OG: PlayerCamera.HandleLimbInfectionShows  V5t5 
        for (int i = 0; i < localoverride_showInfection.Length; i++) {
            if (!body.limbs[i].infected) {
                localoverride_showInfection[i] = false;
            }
            if (body.limbs[i].infectionAmount > 25f) {
                localoverride_showInfection[i] = true;
            }
        }


        if (Net.IsServer) {
            if (this is NetBody pb && pb.IsPlayer) {
                if (body.alive) {
                    pb.chip_punishment_protocol = (SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive());
                    if (pb.chip_punishment_protocol) {
                        pb.chip_punishment_protocol = SharedMain.CheckIfShouldActivateDispersionPunishmentProtocol(pb.plr, out var max_recorded_dist, out var count_plrs_in_range, out var required_count_plrs_in_range);
                    }
                } else {
                    pb.chip_punishment_protocol = false;
                }
            }
        }

        {
            NearBodies.Clear();

            var radius = DistanceForNearBodies * DistanceForNearBodies; // square it

            foreach (var body in NetBody.all_instances) {
                if (body is NetBody && body.IsPlayer && body != this && body.alive && KM.DistanceCheck_PreSqr(body.transform.position, this.transform.position, radius)) {
                    NearBodies.Add((NetBody)body);
                }
            }

            UpdateBodySleepShare();
            UpdateBodyTempShare();
        }


        if (body.alive != last_alive) {
            butchering_dropmeat_skip_counter = 0;

            if (head.TryGetComponent(out FacialExpression face)) {
                face.eyeLight.enabled = body.alive
                ;
            }


            if (!body.alive && last_alive) {
                OnDeath();
            } else {
                // воскрес
                if (Util.IsBodyLocal(body)) {
#if FULLGAME
                    if (!ConcurrentEndings.KeepSpectating)
#endif
                        UIInGame.StopSpectatorMode();
                }
                body.GetCoUtilsInstance()?.CancelAll();

                if (Net.IsClient) {
                    if (!Multiplayer.rules.RespawnKeepSkills) {
                        body.ResetMind();
                    }
                }
            }
        }
        if (!Multiplayer.rules.DisableSleep) {
            if (body.sleeping && !last_sleep) {
                OnSleep();
                // solo-sleep energy bonus ramps from this timestamp (skipped while everyone sleeps)
                soloSleepBonusStart = Time.realtimeSinceStartup;
            } else if (!body.sleeping) {
                soloSleepBonusStart = -1f;
            }
        } else {
            soloSleepBonusStart = -1f;
        }

        if (IsBodyLocal() && !(Net.IsRunning && WorldMP.RunUnchippedLock.Locked)) {
            unchipped = Util.IsUnchipped();
        }

        last_sleep = body.sleeping;
        last_alive = body.alive;
    }



    void UpdateBodySleepShare() {
        // energy now trends via median sleepCycleSpeed mult; only track nearby sleepers for later sleep-quality hooks
        NearBodiesDistanceIs5 = false;
        if (Multiplayer.rules.DisableSleep || !body.alive || !body.sleeping || NearBodies.Count == 0) {
            return;
        }
        foreach (NetBody onb in NearBodies) {
            if (onb != null && KM.DistanceCheck_PreSqr(this.transform.position, onb.transform.position, 30)) {
                NearBodiesDistanceIs5 = true;
                return;
            }
        }
    }
    void UpdateBodyTempShare() {
        if (!body.alive) return;
        // temperature is medical / host-authoritative
        if (Net.IsRunning && !Net.IsServer) return;

        const float body_temperature_share_speed = 0.12f * 0.1f;

        var bc = body.GetComponent<BoxCollider2D>();
        var touching = Physics2D.OverlapBoxAll(bc.transform.position, bc.size, 0f);
        foreach (var item2 in touching) {

            Body otherbody = null;
            if (item2.TryGetComponent<Limb>(out var limb)) {
                otherbody = limb.body;
            }

            if (otherbody != null && otherbody != body && otherbody.alive && otherbody.temperature > 20) {

                float dist1 = (otherbody.temperature - default_body_temperature);
                float dist2 = (body.temperature - default_body_temperature);

                float dist1abs = Math.Abs(dist1);
                float dist2abs = Math.Abs(dist2);

                float goal = dist2abs > dist1abs ? dist1 : dist2;

                float avg = Mathf.Lerp(
                    Mathf.Lerp(dist1, dist2, 0.5f),
                    goal,
                    0.33f // lean towards default body temperature
                ) + default_body_temperature;

                otherbody.temperature = Mathf.MoveTowards(otherbody.temperature, avg, body_temperature_share_speed);
                body.temperature = Mathf.MoveTowards(body.temperature, avg, body_temperature_share_speed);

                break;
            }
        }
    }
    public const float default_body_temperature = 37f;





    public SByteScalarVec2 moveDir;
    public float last_mood = 0;


    public Vector2 position => transform.position;
    public Vector2 pos => (body.standing) ? ((Vector2)body.transform.position) : ((Vector2)body.limbs[(int)Body.LimbNum.UpTorso].transform.position);

    public void SetBodyPosition(Vector2 pos) {
        if (!body.standing) {

            var rb = body.limbs[(int)Body.LimbNum.UpTorso].rb;

            var l = new List<(Vector2, Rigidbody2D)>();
            foreach (var limbe in body.limbs) {
                l.Add((limbe.rb.position, limbe.rb));
            }

            var diff = pos - rb.position;

            //TODO try to use transform.position
            rb.position = pos;

            foreach (var joint in l) {
                joint.Item2.position = (joint.Item1 + diff);
            }

        } else {
            body.transform.position = pos;
        }
    }



    private bool prev_useiteminhand = false;


    void FixedUpdate() {

        HandlePiggybackUpdate();


        if (Net.IsRunning) {
            if ((body.baseLimb.dismembered || ClientMain.IsInTemporaryWorldPause() || body.transform.position.y > 6000)) {
                body.moveDir = Vector2.zero;
                body.rb.velocity = Vector2.zero;
                body.rb.gravityScale = 0f;
            } else {
                if (SharedMain.LocalWorldIsGenerating && !WorldMP.client_technically_finished_worldgen_now_just_waiting_for_server) {
                    body.ForceStand();
                    body.moveDir = Vector2.zero;
                    body.rb.velocity = Vector2.zero;
                    body.rb.gravityScale = 0f;
                }
            }
        }

        if (!IsBodyLocal()) {
            //decay the input

            if (Net.IsServer) {
                var time_since_last_packet = (float)(Net.ServerTick - _lastSyncPacketReceiveTick) / (float)Net.ServerEntitySendRate;

                if (time_since_last_packet > 0.055f) { // timing out, stop em
                    body.moveDir = Vector2.zero;
                }
            }
        } else {
            moveDir = (SByteScalarVec2)body.moveDir;
        }

    }



    // check if can friendly fire
    public bool CanAttackThisGuy(Body target) {
        var targetnpc = target.GetComponent<NetBody>();
        if (targetnpc != null) { // dont damage the piggybackers or carriers
            if (carrying_person == targetnpc) {
                return false;
            }
            if (piggybacking_on == targetnpc) {
                return false;
            }
        }
        if (!target.alive) { // ignore the friendly fire since we're doing cannibalizm now !!!!
            return true;
        }
        if (!Multiplayer.rules.PVP) {
            return false;
        }
        if (target == body) { // suicide bypasses this function anyway
            return false;
        }
        if (targetnpc != null) { // dont damage the piggybackers or carriers
            if (targetnpc.IsPlayer) {
                if (targetnpc.plr.levelPlayTime < 1) {
                    return false;
                }
                if (this.IsPlayer) {
                    if (Multiplayer.rules.Teams) {
                        if (targetnpc.plr.playerColor == this.plr.playerColor) {
                            return false;
                        }
                    }
                }
            }
        }
        return true;
    }


    public int CountPiggybackStackUp() {
        var ggg = this;
        int i;
        for (i = 0; i < 1000; i++) {
            if (ggg.carrying_person != null) {
                ggg = ggg.carrying_person;
            } else {
                break;
            }
        }
        return i;
    }
    public int CountPiggybackStackDown() {
        var ggg = this;
        int i;
        for (i = 0; i < 1000; i++) {
            if (ggg.piggybacking_on != null) {
                ggg = ggg.piggybacking_on;
            } else {
                break;
            }
        }
        return i;
    }
    public NetBody GetPiggybackStackTop() {
        var ggg = this;
        int i;
        for (i = 0; i < 1000; i++) {
            if (ggg.carrying_person != null) {
                if (ggg.carrying_person == this) {
                    log.error("PIGGYBACK GOT RECURSIVE !!!!!");
                    ggg.StopPiggyback();
                    break;
                }
                ggg = ggg.carrying_person;
            } else {
                break;
            }
        }
        return ggg;
    }
    public List<NetBody> GetPiggybackStackAll() {
        List<NetBody> aaaaa = new List<NetBody> { this };
        var ggg = this;
        int i;
        for (i = 0; i < 1000; i++) {
            if (ggg.piggybacking_on != null) {
                if (ggg.piggybacking_on == this) {
                    log.error($"PIGGYBACK GOT RECURSIVE !!!!! {String.Join(" ", aaaaa)} !!!"); // this doesnt happen
                    ggg.StopPiggyback();
                    break;
                }
                ggg = ggg.piggybacking_on;
                aaaaa.Add(ggg);
            } else {
                break;
            }
        }
        ggg = this;
        for (i = 0; i < 1000; i++) {
            if (ggg.carrying_person != null) {
                if (ggg.carrying_person == this) {
                    log.error($"PIGGYBACK GOT RECURSIVE !!!!! {String.Join(" ", aaaaa)} !!!"); // this doesnt happen
                    ggg.StopPiggyback();
                    break;
                }
                ggg = ggg.carrying_person;
                aaaaa.Add(ggg);
            } else {
                break;
            }
        }
        return aaaaa;
    }
    public bool IsPiggybackable() {
        if (piggybacking_on != null) {
            var c = CountPiggybackStackDown();
            if (c < Multiplayer.rules.PiggybackMaxStack) {
                return true;
            } else {
                return false;
            }
        }
        return body.standing;
    }




    public const float KG_to_encumburenceUnit = 0.17f;
    public float GetRealFullEncumberanceOfBody() {
        var enc = body.GetTotalEncumberance();
        //OG: Woundview.UpdateView() // this body weight to kilogram calculation
        var bodyweight = (body.weightOffset * 0.34f + 50f);

        return enc * 0.7f + (bodyweight * KG_to_encumburenceUnit);
    }



    public NetBody piggybacking_on = null;
    public NetBody carrying_person = null;
    // stale client sync still has the ground position until the pickup packet lands
    public float piggybackStartedUnscaled;
    bool carryPoseAligned;

    // charType: 0 experiment, 1 milky, 2 dune - milky sits lower half while carried
    public bool WantsMilkyCarrySit() {
        return body != null && body.charType == 1 && piggybacking_on != null;
    }

    // milky is smaller - sit closer / slightly lower on the carrier than Experiment/Dune
    const float MilkyCarrySideSep = 0.55f;
    const float MilkyCarryDownNudge = 0.25f;

    public static void ClearSitAnims(Body b) {
        if (b == null) {
            return;
        }
        if (b.bodyAnimator != null) {
            var bodyClips = b.bodyAnimator.GetCurrentAnimatorClipInfo(0);
            if (bodyClips.Length != 0 && bodyClips[0].clip != null && bodyClips[0].clip.name == "ExperimentSit") {
                b.bodyAnimator.Play("Grounded");
                b.standLerpTime = 0f;
            }
        }
        if (b.armsAnimator != null) {
            var armClips = b.armsAnimator.GetCurrentAnimatorClipInfo(0);
            if (armClips.Length != 0 && armClips[0].clip != null && armClips[0].clip.name == "ArmsSit") {
                b.armsAnimator.Play("Grounded");
            }
        }
    }

    public static void ForceSitAnims(Body b) {
        if (b == null) {
            return;
        }
        if (b.bodyAnimator != null) {
            b.bodyAnimator.Play("ExperimentSit");
        }
        if (b.armsAnimator != null) {
            b.armsAnimator.Play("ArmsSit");
        }
    }

    // body sit only - arms/head stay free so physics can still react while carried
    public static void ForceMilkyCarrySitBodyAnim(Body b) {
        if (b == null) {
            return;
        }
        if (b.bodyAnimator != null) {
            b.bodyAnimator.Play("ExperimentSit");
        }
        if (b.armsAnimator != null) {
            var armClips = b.armsAnimator.GetCurrentAnimatorClipInfo(0);
            if (armClips.Length != 0 && armClips[0].clip != null && armClips[0].clip.name == "ArmsSit") {
                b.armsAnimator.Play("Grounded");
            }
        }
    }

    static bool IsMilkyCarrySitLockedLimb(Body b, Limb limb) {
        if (b == null || limb == null) {
            return false;
        }
        if (limb.isLegLimb) {
            return true;
        }
        return ReferenceEquals(limb, b.GetLowerTorso());
    }

    // standing HandleVisuals copies animLimb -> limb; ragdoll skips that. drive lower half from ExperimentSit.
    public static void ApplyMilkyCarrySitLowerPose(Body b) {
        if (b == null || b.bodyAnimator == null || b.limbs == null) {
            return;
        }
        ForceMilkyCarrySitBodyAnim(b);
        float facing = b.isRight ? 1f : -1f;
        Limb down = b.GetLowerTorso();
        Limb legF = b.limbs.Length > (int)Body.LimbNum.LegF ? b.limbs[(int)Body.LimbNum.LegF] : null;
        Limb legB = b.limbs.Length > (int)Body.LimbNum.LegB ? b.limbs[(int)Body.LimbNum.LegB] : null;
        ApplyAnimPoseToLockedLimb(b, down, facing);
        ApplyAnimPoseToLockedLimb(b, legF, facing);
        ApplyAnimPoseToLockedLimb(b, legB, facing);
    }

    static void ApplyAnimPoseToLockedLimb(Body b, Limb limb, float facing) {
        if (limb == null || limb.animLimb == null || limb.rb == null) {
            return;
        }
        Vector2 local = b.bodyAnimator.transform.InverseTransformPoint(limb.animLimb.position);
        local *= b.heightMultiplier;
        limb.transform.localPosition = local;
        limb.transform.eulerAngles = new Vector3(0f, 0f, limb.animLimb.eulerAngles.z * facing);
        limb.rb.position = limb.transform.position;
        limb.rb.rotation = limb.transform.eulerAngles.z;
        limb.rb.velocity = Vector2.zero;
        limb.rb.angularVelocity = 0f;
    }

    // handleVisuals cancels sit when !standing (piggyback Ragdoll). drop a leftover sit clip, then leave the ragdoll alone.
    public static void ApplyPiggybackCarryPose(Body b) {
        if (b == null || !b.TryGetComponent(out NetBody nb) || nb.piggybacking_on == null) {
            return;
        }
        ClearSitAnims(b);
    }

    // called from FixedUpdate; also LateUpdate so we follow noclip (moves carrier in Update).
    void HandlePiggybackUpdate() {

        if (carrying_person != null) {
            body.idleTime = 0f; // disable idle animations

            if (Net.IsServer) {
                if (!IsPiggybackable()) {  // drop the carried person cuz i ragdolled
                    carrying_person.StopPiggyback();
                }
            }
        }

        if (piggybacking_on != null) {
            body.idleTime = 0f; // disable idle animations

            body.Ragdoll();

            var targetbody = piggybacking_on.body;
            if (!piggybacking_on.IsPiggybackable()) { // if carrier ragdolled, cancel piggyback now
                StopPiggyback();
                return;
            }

            body.ForceSwitchDir(targetbody.isRight);

            var targettorsorb = targetbody.limbs[(int)Body.LimbNum.UpTorso].rb;
            var torsorb = body.limbs[(int)Body.LimbNum.UpTorso].rb;

            float side = (body.isRight ? 1f : (-1f));
            var standupamount = Vector2.up * (1 - piggybacking_on.body.crouchAmount);
            Vector2 target_uppertorso_pos = targettorsorb.position + standupamount + (Vector2.left * side);
            if (Physics2D.OverlapCircle(target_uppertorso_pos, 1, LayerMask.GetMask("Ground")) != null) {
                target_uppertorso_pos = Vector2.LerpUnclamped(piggybacking_on.pos + standupamount, target_uppertorso_pos, 0.1f);
            }
            body.crouchAmount = piggybacking_on.body.crouchAmount;

            if (body.alive) {
                // alive: shift the whole pose onto the back and lerp limb velocity toward the carrier.
                // that is the old piggyback. only a dead body stays loose.
                SetBodyPosition(target_uppertorso_pos);
                Vector2 carrierVel = targetbody.rb.velocity;
                foreach (Limb item in body.limbs) {
                    if (item == null || item.rb == null) {
                        continue;
                    }
                    item.rb.velocity = Vector2.Lerp(item.rb.velocity, carrierVel, 0.2f);
                }
                torsorb.velocity = carrierVel;
                Limb upperLimb = body.GetUpperTorso();
                Rigidbody2D upper = upperLimb != null && upperLimb.rb != null ? upperLimb.rb : torsorb;
                upper.velocity = carrierVel;
                Limb head = body.GetHead();
                if (head != null && head.rb != null) {
                    head.rb.velocity = carrierVel;
                }
                torsorb.rotation = targettorsorb.rotation;
                torsorb.angularVelocity = targettorsorb.angularVelocity;
                Limb carrierLower = targetbody.GetLowerTorso();
                if (carrierLower != null && carrierLower.rb != null && Mathf.DeltaAngle(carrierLower.rb.rotation, upper.rotation) > 10f) {
                    upper.rotation = Mathf.MoveTowardsAngle(upper.rotation, carrierLower.rb.rotation, Time.fixedDeltaTime * 100f);
                }
            } else {
                // dead stays a loose pile. turn it upright once, then only the chest is driven.
                Vector2 chestDelta = target_uppertorso_pos - torsorb.position;
                float upright = Mathf.DeltaAngle(torsorb.rotation, targettorsorb.rotation);
                bool turnUpright = !carryPoseAligned && Mathf.Abs(upright) > 8f;
                Quaternion turn = Quaternion.Euler(0f, 0f, upright);
                Vector2 pivot = torsorb.position;
                foreach (Limb item in body.limbs) {
                    if (item == null || item.rb == null) {
                        continue;
                    }
                    Vector2 p = item.rb.position;
                    if (turnUpright) {
                        p = pivot + (Vector2)(turn * (p - pivot));
                        item.rb.MoveRotation(item.rb.rotation + upright);
                    }
                    item.rb.position = p + chestDelta;
                }
                if (turnUpright) {
                    carryPoseAligned = true;
                }
                torsorb.velocity = targetbody.rb.velocity;
                torsorb.angularVelocity = targettorsorb.angularVelocity;
                torsorb.MoveRotation(targettorsorb.rotation);
            }

        }

    }



    public bool CanBeCarriedBySomeone() {
        if (ConcurrentEndings.ScientistDead && plr != null && !plr.IsUnchipped()) {
            return false;
        }
        if (Multiplayer.rules.AlwaysAllowCarry) {
            return true;
        }
        return !body.standing; // just check if ragdolling
    }

    public bool IsPiggybacking() {
        return piggybacking_on != null;
    }
    public bool IsCarrying() {
        return carrying_person != null;
    }

    public bool CanPiggyback(NetBody target, bool check_distance = false, bool force = false) {
        if (this == target) {
            return false;
        }
        if (EndingCarry.Blocked(this, target)) {
            return false;
        }
        if (piggybacking_on == target || body == null || target.body == null) {
            return false;
        }
        if (target.carrying_person != null) {
            return false;
        }
        if (!(force || target.IsPiggybackable())) {
            return false;
        }


        {
            var up = this.CountPiggybackStackUp();
            var down = target.CountPiggybackStackDown();

            if (down + up + 1 > Multiplayer.rules.PiggybackMaxStack) {
#if !RELEASE
                log.warn("CanPiggyback: Piggyback stack max");
#endif
                return false;
            }
        }


        if (check_distance) {

            if (IsLocal) { // i wanna piggyback
                if (!KM.DistanceCheck(body, target.body, SharedMain.max_player_interaction_distance)) {
                    return false;
                }
            } else if (Net.IsServer) {
                if (!KM.DistanceCheck(body, target.body, SharedMain.max_player_interaction_distance * 1.75f)) { // i am server, i verify that player can piggyback
                    return false;
                }
            }// if im cliend im letting that slide 
        }

        if (IsLocal && !force) {
            if (Util.QuickRaycastInteractionCheck(body.transform.position, target.body.transform.position, true)) {
                // continue to the return true;  :)
            } else {
                PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
            }
        }

        return true;

    }



    public bool StartPiggyback(NetBody target, bool check_distance = false, bool force = false) {
        if (CanPiggyback(target, check_distance, force)) {

            StopPiggyback(true);

            if (log.verbose) log.l($"{this} Started piggybacking {target}");

            piggybacking_on = target;
            target.carrying_person = this;
            piggybackStartedUnscaled = Time.unscaledTime;

            // play climbing sound effect, maybe backpack is good enough
            //OG: PlayerCamera.PlayBackpackSound (but only the sound id)
            Sound.Play("backpack" + UnityEngine.Random.Range(1, 3), target.carrying_person.body.transform.position, twoDimensional: false, pitchShift: false, target.carrying_person.body.transform, 1f, UnityEngine.Random.Range(0.8f, 1.2f));

            target.body.rb.velocity = Vector2.Lerp(target.body.rb.velocity, body.rb.velocity, 0.5f);
            HandlePiggybackUpdate();

            if (IsPlayer) {

                if (IsBodyLocal() && !force && Net.IsClient) { // check if its called from ui, by local player
                    ClientMain.Client_SendCharacterSyncPacket();
                }

                if (Net.IsServer) {
                    if (!IsBodyLocal()) {
                        body.SetVelocity(Vector2.zero);
                        Server_RemindPlayersCurrentState();
                    }
                } else {
                    SetNetIgnoreTime();
                }
            }

            return true;
        }
        return false;
    }
    public bool StopPiggyback(bool isinternal = false) {
        if (body != null && piggybacking_on != null) {

            if (log.verbose) log.l($"{this} Stopped piggybacking {piggybacking_on}");



            var carrier = piggybacking_on;
            carryPoseAligned = false;
            piggybacking_on.carrying_person = null;
            piggybacking_on = null;

            body.ForceStand();

            // teleport to the carriers position, to not clip into walls
            if (carrier) {
                SetBodyPosition(carrier.body.transform.position);
            }

            if (IsPlayer) {
                if (!isinternal) {
                    if (IsBodyLocal()) {
                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestStopCarryPiggyback);
                    }
                    if (Net.IsServer) {
                        if (!IsBodyLocal()) {
                            Server_RemindPlayersCurrentState(true);
                        }
                    }
                    SetNetIgnoreTime(0.1f);
                }
            }

            return true;
        }
        return false;
    }



    public bool CanPush(NetBody victim) {
        if (this == victim) {
            return false;
        }

        var the_guy_is_piggybacking_or_carrying_me = victim.piggybacking_on == this || victim.carrying_person == this;

        if (the_guy_is_piggybacking_or_carrying_me) {
            return false;
        }

        if (!(body.conscious && body.standing)) {
            return false;
        }

        return true;
    }


    public const float PUSH_COOLDOWN = 1f;
    public const float PUSH_VELOCITY = 15f;
    static float PUSH_STAMINA_USE_MULTIPLIER = 1;
    double lastPushTime;
    public void Push(NetBody victim) {

        if (!CanPush(victim)) {
            return;
        }
        {
            if (Time.realtimeSinceStartupAsDouble - lastPushTime < PUSH_COOLDOWN) {
                return;
            }
            lastPushTime = Time.realtimeSinceStartupAsDouble;
        }


        var vel = (PUSH_VELOCITY * Mathf.Clamp(1 + Mathf.Max(0, this.body.skills.STRFrom10 - victim.body.skills.STRFrom10) * 0.1f, 0.2f, 3f));

        body.stamina -= 1f * PUSH_STAMINA_USE_MULTIPLIER;
        body.temperature += 0.03f * PUSH_STAMINA_USE_MULTIPLIER;

        if (Net.IsClient) {
            var targetbody = victim.body;
            Util.PlayWorldSoundOnScreenIfInRange("landsmall1", victim.pos);

            targetbody.SetVelocity(targetbody.rb.velocity + KM.NormalTo(this.body.rb.position, targetbody.rb.position, out var _) * vel);
            // ANNOUNCE PUSH!!!!!
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_Push_Ego_Naxuy, victim.netId, true);
        } else {

            victim.body.Ragdoll();

            victim.body.SetVelocity(victim.rb.velocity + KM.NormalTo(this.rb.position,
                                                          victim.rb.position,
                                                          out var _) *
                                                    vel);

            victim.Server_RemindPlayersCurrentState();

            ServerMain.Server_AnnounceSound(victim.body.rb.position, "landsmall1", IsPlayer ? ServerMain.GetListOfClientIdsExceptThis(this.plr.playerId) : ServerMain.AllClientIds);

            if (victim.IsPlayer) {
                var writer = Net.CreateWriter(NetmsgId.CLIENT_Knockback_thing);
                writer.Put(new BodyKnockbackPacket {
                    Velocity = Vector2.zero,
                    CloseUI = true,
                });
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, victim.plr.playerId);
            }

        }

    }

    public void Hug(NetBody other) {
        HugSession.Request(this, other);
    }

    public void Pat(NetBody other) {
        HeadpatSession.Request(this, other);
    }

    public void Bite(NetBody other) {
        DuneBiteSession.Request(this, other);
    }

    public static void TryDuneKeybindBite() {
        DuneBiteSession.TryKeybindBite();
    }

    public static void DuneBiteSessionTick() {
        DuneBiteSession.ServerTickAuto();
    }

    // UIChoicePrompt auto-accepts dune bite when local body cannot consent
    public static bool DuneBiteSessionCannotConsent(Body body) {
        return DuneBiteSession.CannotConsent(body);
    }





    public void OnReceiveSyncPacket(in BodyTeleportPacket pack, bool force = false) {
        if (body != null) {
            if (syncBody != null) {
                syncBody.ApplyBodyTeleportPacket(pack);
            } else {
                Together.SyncBody.ApplyBodyTeleportPacket(this, pack);
            }
        }
    }


    public void Server_RemindPlayersCurrentState(bool reliable = true) {
        SetNetIgnoreTime();

        body.moveDir = Vector2.zero;
        syncBody.WriteBodyData();

        if (body.IsBodyLocal()) return;

        var writer = Net.CreateWriter(NetmsgId.CLIENT_BodyTeleportPacket);
        writer.Put(IsPlayer ? plr.playerId : ushort.MaxValue);
        writer.Put(netId);
        writer.Put(new BodyTeleportPacket(this));
        if (IsPlayer) {
            Net.Server_SendTo(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, plr);
        } else {
            Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        }

        // WRITER END;

    }

    // hug sits with push. ask, they accept, then a short pull-together and a small mood bump.
    static class HugSession {
        const float AcceptDistance = 5f;
        const float VisualDistance = 6f;
        const float DurationSeconds = 3f;
        const float RequestCooldown = 1f;
        const double HardLockSeconds = 10.0;
        const double MoodCooldownSeconds = 600.0;
        const float MoodRampStart = 0.7f;
        const float MoodRampFloor = 0.01f;

        static readonly Dictionary<ushort, double> MoodReadyAt = new Dictionary<ushort, double>();
        static readonly Dictionary<ushort, ushort> PendingFrom = new Dictionary<ushort, ushort>();
        static readonly List<Pose> Poses = new List<Pose>();
        static int _velocityFrame = -1;
        static int _armFrame = -1;

        sealed class Pose {
            public Body A;
            public Body B;
            public double Ends;
        }

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict["hug_ask"] = (plr, cmd, args) => {
                if (args == null || args.Length < 2 || !ushort.TryParse(args[1], out ushort targetId)) {
                    return;
                }
                if (!ScavPlayer.TryGetPlayerFromClientId(targetId, out ScavPlayer target)) {
                    return;
                }
                Ask(plr, target);
            };
            ClientMain.ClientCustomCommandsDict["hug_go"] = (cmd, reader) => {
                reader.Get(out ushort a);
                reader.Get(out ushort b);
                BeginLocal(a, b);
            };
        }

        public static void Request(NetBody self, NetBody other) {
            if (self == null || other == null || self.plr == null || other.plr == null || self == other) {
                return;
            }
            if (!Huggable(self.body) || !Huggable(other.body) || Tied(self, other)) {
                return;
            }
            if (!Near(self.body, other.body, AcceptDistance)) {
                if (PlayerCamera.main != null) {
                    PlayerCamera.main.DoAlert(Lang.Get("hug_far"));
                }
                return;
            }
            if (Net.IsServer) {
                Ask(self.plr, other.plr);
                return;
            }
            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("hug_ask " + other.plr.playerId.id.ToString());
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        static void Ask(ScavPlayer from, ScavPlayer target) {
            if (!Net.IsServer || from == null || target == null || from == target) {
                return;
            }
            if (from.netBody == null || target.netBody == null) {
                return;
            }
            if (!from.ServerPlayerState.Cooldown("hug", RequestCooldown)) {
                from.Server_DoAlertSingle("<key:hug_soon>");
                return;
            }
            if (!Huggable(from.body) || !Huggable(target.body) || Tied(from.netBody, target.netBody)) {
                return;
            }
            if (!Near(from.body, target.body, AcceptDistance)) {
                from.Server_DoAlertSingle("<key:hug_far>");
                return;
            }
            if (InPose(from.body) || InPose(target.body)) {
                from.Server_DoAlertSingle("<key:hug_busy>");
                return;
            }
            ushort targetId = target.playerId.id;
            PendingFrom[targetId] = from.playerId.id;
            ServerMain.PromptPlayer(target, "HUG", "<body:" + from.netBody.netId.ToString() + ">", "<key:plrint_hug_ask>", (answer) => {
                if (!PendingFrom.TryGetValue(targetId, out ushort stillFrom) || stillFrom != from.playerId.id) {
                    return;
                }
                PendingFrom.Remove(targetId);
                if (answer != 0) {
                    from.Server_DoAlertSingle("<bodyc:" + target.netBody.netId.ToString() + "> <key:declined>.");
                    return;
                }
                Start(from, target);
            });
        }

        static void Start(ScavPlayer a, ScavPlayer b) {
            if (a == null || b == null || a.netBody == null || b.netBody == null) {
                return;
            }
            if (!Huggable(a.body) || !Huggable(b.body) || !Near(a.body, b.body, AcceptDistance)) {
                a.Server_DoAlertSingle("<key:hug_far>");
                b.Server_DoAlertSingle("<key:hug_far>");
                return;
            }
            if (InPose(a.body) || InPose(b.body)) {
                a.Server_DoAlertSingle("<key:hug_busy>");
                return;
            }
            ApplyMood(a, b);
            BeginLocal(a.playerId.id, b.playerId.id);
            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("hug_go");
            writer.Put(a.playerId.id);
            writer.Put(b.playerId.id);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
        }

        static void BeginLocal(ushort aId, ushort bId) {
            if (!ScavPlayer.TryGetPlayerFromClientId(aId, out ScavPlayer a) || a.body == null) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(bId, out ScavPlayer b) || b.body == null) {
                return;
            }
            Pose pose = new Pose {
                A = a.body,
                B = b.body,
                Ends = Time.realtimeSinceStartupAsDouble + DurationSeconds,
            };
            Poses.Add(pose);
            // same species lines traders use on a successful hug
            if (a.body.talker != null) {
                List<string> lines = Locale.GetCharacter("traderhugsuccess", a.body.charType);
                if (lines != null && lines.Count > 0) {
                    Talker_Talk_MultiplayerPatch.Force(a.body.talker, lines, null, true, true);
                }
            }
        }

        public static void TickVelocity() {
            if (_velocityFrame == Time.frameCount) {
                return;
            }
            _velocityFrame = Time.frameCount;
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = Poses.Count - 1; i >= 0; i--) {
                Pose pose = Poses[i];
                if (pose.A == null || pose.B == null || now >= pose.Ends || !Near(pose.A, pose.B, VisualDistance)) {
                    Poses.RemoveAt(i);
                    continue;
                }
                ApplyPose(pose.A, pose.B);
            }
        }

        public static void TickArms() {
            if (_armFrame == Time.frameCount) {
                return;
            }
            _armFrame = Time.frameCount;
            for (int i = 0; i < Poses.Count; i++) {
                Pose pose = Poses[i];
                if (pose.A == null || pose.B == null) {
                    continue;
                }
                ApplyArms(pose.A, pose.B);
            }
        }

        static void ApplyMood(ScavPlayer a, ScavPlayer b) {
            double now = Time.realtimeSinceStartupAsDouble;
            Reward(a.body, MoodScale(a.playerId.id, now));
            Reward(b.body, MoodScale(b.playerId.id, now));
            MoodReadyAt[a.playerId.id] = now;
            MoodReadyAt[b.playerId.id] = now;
            if (a.netBody != null) {
                MedicalSync.Server_QueueSendCharacterHealth(a.netBody, true);
            }
            if (b.netBody != null) {
                MedicalSync.Server_QueueSendCharacterHealth(b.netBody, true);
            }
        }

        static float MoodScale(ushort id, double now) {
            if (!MoodReadyAt.TryGetValue(id, out double started)) {
                return 1f;
            }
            double elapsed = Math.Max(0.0, now - started);
            if (elapsed >= MoodCooldownSeconds) {
                MoodReadyAt.Remove(id);
                return 1f;
            }
            if (elapsed < HardLockSeconds) {
                return 0f;
            }
            float progress = Mathf.Clamp01((float)(elapsed / MoodCooldownSeconds));
            if (progress < MoodRampStart) {
                return 0f;
            }
            if (progress >= 1f) {
                return 1f;
            }
            float ramp = Mathf.Clamp01((progress - MoodRampStart) / (1f - MoodRampStart));
            return Mathf.Lerp(MoodRampFloor, 1f, Mathf.Pow(ramp, 1.6f));
        }

        static void Reward(Body body, float scale) {
            if (body == null || scale <= 0f) {
                return;
            }
            float delta = UnityEngine.Random.Range(1, 6) * scale;
            body.happiness = Mathf.Clamp(body.happiness + delta, -100f, 100f);
        }

        static bool Huggable(Body body) {
            return body != null && body.alive && body.conscious;
        }

        static bool Tied(NetBody a, NetBody b) {
            return a.piggybacking_on == b || b.piggybacking_on == a || a.carrying_person == b || b.carrying_person == a;
        }

        static bool Near(Body a, Body b, float max) {
            if (a == null || b == null) {
                return false;
            }
            Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
            return delta.sqrMagnitude <= max * max;
        }

        static bool InPose(Body body) {
            for (int i = 0; i < Poses.Count; i++) {
                Pose pose = Poses[i];
                if (pose.A == body || pose.B == body) {
                    return true;
                }
            }
            return false;
        }

        static void ApplyPose(Body a, Body b) {
            Vector2 posA = a.transform.position;
            Vector2 posB = b.transform.position;
            Vector2 direction = posB - posA;
            if (direction.sqrMagnitude < 0.001f) {
                direction = Vector2.right;
            }
            Vector2 normal = direction.normalized;
            Vector2 center = (posA + posB) * 0.5f;
            Vector2 targetA = center - normal * 0.42f;
            Vector2 targetB = center + normal * 0.42f;
            targetA.y = posA.y;
            targetB.y = posB.y;
            a.SetVelocity((targetA - posA) * 7.5f);
            b.SetVelocity((targetB - posB) * 7.5f);
            a.eyeCloseTime = Mathf.Max(a.eyeCloseTime, 0.2f);
            b.eyeCloseTime = Mathf.Max(b.eyeCloseTime, 0.2f);
            a.targetLookPos = new Vector2(posB.x, posB.y + 1f);
            b.targetLookPos = new Vector2(posA.x, posA.y + 1f);
        }

        static void ApplyArms(Body a, Body b) {
            Vector2 direction = b.transform.position - a.transform.position;
            if (direction.sqrMagnitude < 0.001f) {
                direction = Vector2.right;
            }

            Vector2 normal = direction.normalized;
            Vector2 side = new Vector2(-normal.y, normal.x);
            Vector2 aTarget = (Vector2)b.transform.position - normal * 0.18f;
            Vector2 bTarget = (Vector2)a.transform.position + normal * 0.18f;
            ApplyArmPair(a, aTarget + side * 0.22f, aTarget - side * 0.22f);
            ApplyArmPair(b, bTarget - side * 0.22f, bTarget + side * 0.22f);
        }

        static void ApplyArmPair(Body body, Vector2 upperTarget, Vector2 lowerTarget) {
            if (body.limbs == null || body.limbs.Length == 0) {
                return;
            }
            Limb leftForearm = LimbAt(body, 4);
            Limb leftHand = LimbAt(body, 5);
            Limb rightForearm = LimbAt(body, 7);
            Limb rightHand = LimbAt(body, 8);
            PullLimb(leftForearm, upperTarget);
            Vector2 leftFrom = leftForearm != null ? (Vector2)leftForearm.transform.position : upperTarget;
            PullLimb(leftHand, Vector2.Lerp(leftFrom, upperTarget, 0.8f));
            PullLimb(rightForearm, lowerTarget);
            Vector2 rightFrom = rightForearm != null ? (Vector2)rightForearm.transform.position : lowerTarget;
            PullLimb(rightHand, Vector2.Lerp(rightFrom, lowerTarget, 0.8f));
        }

        static Limb LimbAt(Body body, int index) {
            if (body.limbs == null || index < 0 || index >= body.limbs.Length) {
                return null;
            }
            Limb limb = body.limbs[index];
            if (limb == null || limb.dismembered) {
                return null;
            }
            return limb;
        }

        static void PullLimb(Limb limb, Vector2 target) {
            if (limb == null) {
                return;
            }
            Vector2 next = Vector2.Lerp(limb.transform.position, target, 0.82f);
            limb.transform.position = new Vector3(next.x, next.y, limb.transform.position.z);
        }
    }

    // dune player "Feed on" / K / auto-hunger -> consent (skipped if uncon), traderbite damage + eatFlesh + +35 hunger
    static class DuneBiteSession {
        const float AskDistance = 5f;
        const float HealthPanelRangeMult = 2f;
        const float RequestCooldown = 0.1f;
        const float AutoHungerMax = 10f;
        const float AutoChance = 0.05f;
        const float HungerGain = 35f;
        const float UnconsciousAmpChance = 0.01f;

        static readonly string[] BiteLines = {
            "Mmmm.. m..",
            "Th-ank...",
            "Fri-end tas-ty....",
        };

        static readonly Dictionary<ushort, ushort> PendingFrom = new Dictionary<ushort, ushort>();

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict["dune_bite_ask"] = (plr, cmd, args) => {
                if (args == null || args.Length < 2 || !ushort.TryParse(args[1], out ushort targetId)) {
                    return;
                }
                if (!ScavPlayer.TryGetPlayerFromClientId(targetId, out ScavPlayer target)) {
                    return;
                }
                bool seenUnconscious = args.Length >= 3 && args[2] == "1";
                Ask(plr, target, seenUnconscious);
            };
            ClientMain.ClientCustomCommandsDict["dune_bite_go"] = (cmd, reader) => {
                reader.Get(out ushort duneId);
                reader.Get(out ushort victimId);
                PlayLocalFx(duneId, victimId);
            };
        }

        // local K / Feed on: bite whoever is under the cursor (or the interaction target)
        public static void TryKeybindBite() {
            if (!Net.IsRunning || !Util.IsWorldGenerated()) {
                return;
            }
            ScavPlayer me = ScavPlayer.LocalPlayer;
            if (me == null || me.netBody == null || !IsDune(me.body)) {
                return;
            }
            if (Con.IsConsoleOpen() || (PlayerCamera.main != null && PlayerCamera.main.radialOpen)) {
                return;
            }
            NetBody target = PlayerCamera_HandlePlayerHover_MultiplayerPatch.hoveredPlayerBody;
            if (target == null && UIInGame.interaction_menu_target_body != null) {
                target = UIInGame.interaction_menu_target_body;
            }
            if (target == null || target == me.netBody || !target.IsPlayer) {
                return;
            }
            Request(me.netBody, target);
        }

        public static void Request(NetBody self, NetBody other) {
            if (self == null || other == null || self.plr == null || other.plr == null || self == other) {
                return;
            }
            if (!IsDune(self.body) || !Biteable(other.body)) {
                return;
            }
            if (!Near(self.body, other.body, AskDistance)) {
                if (PlayerCamera.main != null) {
                    PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                }
                return;
            }
            if (Net.IsServer) {
                Ask(self.plr, other.plr, CannotConsent(other.body));
                return;
            }
            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            // the biter's copy is what everyone can see. the host body can still say "awake" and would otherwise pop the prompt.
            string ko = CannotConsent(other.body) ? " 1" : " 0";
            writer.Put("dune_bite_ask " + other.plr.playerId.id.ToString() + ko);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        static void Ask(ScavPlayer from, ScavPlayer target, bool seenUnconscious = false) {
            if (!Net.IsServer || from == null || target == null || from == target) {
                return;
            }
            if (from.netBody == null || target.netBody == null || from.ServerPlayerState == null) {
                return;
            }
            if (!from.ServerPlayerState.Cooldown("dune_bite", RequestCooldown)) {
                from.Server_DoAlertSingle("<key:hug_soon>");
                return;
            }
            if (!IsDune(from.body) || !Biteable(target.body)) {
                return;
            }
            if (!Near(from.body, target.body, AskDistance * 1.2f)) {
                from.Server_DoAlertSingle("<key:interact_obstructed>");
                return;
            }
            // asleep / KO / low consciousness cannot consent - feed immediately
            if (seenUnconscious || CannotConsent(target.body)) {
                ApplyBite(from.netBody, target.netBody);
                return;
            }
            ushort targetId = target.playerId.id;
            PendingFrom[targetId] = from.playerId.id;
            // same prompt shell as give/hug; Yes/No via DUNE_BITE id
            ServerMain.PromptPlayer(target, "DUNE_BITE",
                "<body:" + from.netBody.netId.ToString() + ">",
                "<key:plrint_bite_ask>",
                (answer) => {
                    if (!PendingFrom.TryGetValue(targetId, out ushort stillFrom) || stillFrom != from.playerId.id) {
                        return;
                    }
                    PendingFrom.Remove(targetId);
                    // victim may have gone KO after the prompt was sent
                    if (CannotConsent(target.body)) {
                        if (!IsDune(from.body) || !Biteable(target.body) || !Near(from.body, target.body, AskDistance * 1.4f)) {
                            from.Server_DoAlertSingle("<key:interact_obstructed>");
                            return;
                        }
                        ApplyBite(from.netBody, target.netBody);
                        return;
                    }
                    if (answer != 0) {
                        from.Server_DoAlertSingle("<bodyc:" + target.netBody.netId.ToString() + "> <key:declined>.");
                        return;
                    }
                    if (!IsDune(from.body) || !Biteable(target.body) || !Near(from.body, target.body, AskDistance * 1.4f)) {
                        from.Server_DoAlertSingle("<key:interact_obstructed>");
                        return;
                    }
                    ApplyBite(from.netBody, target.netBody);
                    from.Server_DoAlertSingle("<bodyc:" + target.netBody.netId.ToString() + "> <key:accepted>.");
                });
        }

        // host: once/sec, hungry dunes (≤10) may auto-bite nearest player in health-panel range
        public static void ServerTickAuto() {
            if (!Net.IsServer || !Util.IsWorldGenerated()) {
                return;
            }
            float range = SharedMain.max_player_interaction_distance * HealthPanelRangeMult;
            float rangeSqr = range * range;
            foreach (ScavPlayer dunePlr in ScavPlayer.AllLivingPlayers) {
                if (dunePlr == null || dunePlr.netBody == null || dunePlr.body == null) {
                    continue;
                }
                if (!IsDune(dunePlr.body) || dunePlr.body.hunger > AutoHungerMax) {
                    continue;
                }
                if (UnityEngine.Random.value >= AutoChance) {
                    continue;
                }
                NetBody nearest = null;
                float nearestSqr = float.PositiveInfinity;
                Vector2 dunePos = dunePlr.body.transform.position;
                foreach (ScavPlayer other in ScavPlayer.AllLivingPlayers) {
                    if (other == null || other == dunePlr || other.netBody == null || other.body == null) {
                        continue;
                    }
                    if (!Biteable(other.body)) {
                        continue;
                    }
                    float distSqr = ((Vector2)other.body.transform.position - dunePos).sqrMagnitude;
                    if (distSqr <= rangeSqr && distSqr < nearestSqr) {
                        nearestSqr = distSqr;
                        nearest = other.netBody;
                    }
                }
                if (nearest != null) {
                    ApplyBite(dunePlr.netBody, nearest);
                }
            }
        }

        static void ApplyBite(NetBody dune, NetBody victim) {
            if (dune == null || victim == null || dune.body == null || victim.body == null) {
                return;
            }
            Body bitten = victim.body;
            List<Limb> limbs = bitten.limbs.Skip(3).Where(x => x != null && !x.dismembered).ToList();
            if (limbs.Count == 0) {
                return;
            }
            Limb limb = limbs.PickRandom();
            limb.muscleHealth -= UnityEngine.Random.Range(25f, 40f);
            limb.skinHealth -= UnityEngine.Random.Range(12f, 20f);
            limb.bleedAmount += UnityEngine.Random.Range(6f, 12f);
            limb.pain += UnityEngine.Random.Range(37f, 55f);
            bitten.adrenaline += 40f;

            // 1% chance to rip off an extremity when feeding on the unconscious
            if (!bitten.conscious && UnityEngine.Random.value < UnconsciousAmpChance) {
                List<Limb> extremities = bitten.limbs
                    .Where(x => x != null && !x.dismembered && !x.isAbdomen && !x.isHead && !x.isVital)
                    .ToList();
                if (extremities.Count > 0) {
                    extremities.PickRandom().Dismember();
                }
            }

            dune.body.hunger = Mathf.Clamp(dune.body.hunger + HungerGain, -50f, 125f);

            victim.OnDamageEvent(new BodyDamageEvent(dune, victim, null, 30f));
            MedicalSync.ForceHealth(victim);
            MedicalSync.ForceHealth(dune);

            PlayLocalFx(dune.plr != null ? dune.plr.playerId.id : (ushort)0, victim.plr != null ? victim.plr.playerId.id : (ushort)0);

            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("dune_bite_go");
            writer.Put(dune.plr != null ? dune.plr.playerId.id : (ushort)0);
            writer.Put(victim.plr != null ? victim.plr.playerId.id : (ushort)0);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
        }

        static void PlayLocalFx(ushort duneId, ushort victimId) {
            if (!ScavPlayer.TryGetPlayerFromClientId(duneId, out ScavPlayer dunePlr) || dunePlr.body == null) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(victimId, out ScavPlayer victimPlr) || victimPlr.body == null) {
                return;
            }
            Body duneBody = dunePlr.body;
            Body victimBody = victimPlr.body;
            victimBody.DoGoreSound();
            Sound.Play("eatFlesh", victimBody.transform.position);
            if (duneBody.talker != null) {
                string line = BiteLines[UnityEngine.Random.Range(0, BiteLines.Length)];
                Talker_Talk_MultiplayerPatch.Force(duneBody.talker, new List<string> { line }, null, true, true);
            }
        }

        static bool IsDune(Body body) {
            return body != null && body.alive && body.charType == 2;
        }

        // matches UI KO / asleep: conscious is consciousness>30; status "unconscious" uses <=20; sleeping can keep consciousness high
        internal static bool CannotConsent(Body body) {
            if (body == null || !body.alive) {
                return true;
            }
            if (body.sleeping || !body.conscious || body.consciousness <= 30f) {
                return true;
            }
            return false;
        }

        static bool Biteable(Body body) {
            return body != null && body.alive && body.limbs != null && body.limbs.Skip(3).Any(x => x != null && !x.dismembered);
        }

        static bool Near(Body a, Body b, float max) {
            if (a == null || b == null) {
                return false;
            }
            Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
            return delta.sqrMagnitude <= max * max;
        }
    }

}