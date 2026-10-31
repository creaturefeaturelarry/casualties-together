using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteEntitySystem;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
                type == DamageEventType.PlayerAttack // Was this a misinput?
                || type == DamageEventType.BuildingAttack
            ) && attackerBuilding != null && attackerBuilding.TryGetTracker(out tracker);
        return b;
    }
    public bool IsSuicide() {
        return type == DamageEventType.PlayerAttack && attackerBody == victim;
    }

    public Dictionary<string,object> data;

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


public static class Util_MPExtensions {
    public static NetBody GetNetBody(this Limb limb) {
        return limb.body.GetComponent<NetBody>();
    }
    public static NetBody GetNetBody(this Body body) {
        return body.GetComponent<NetBody>();
    }
    public static bool TryGetNetBody(this Limb limb, out NetBody nb) {
        return limb.body.TryGetComponent(out nb);
    }
    public static bool TryGetNetBody(this Body body, out NetBody nb) {
        return body.TryGetComponent(out nb);
    }
    public static bool TryGetNetBody(this Vomiter vomiter, out NetBody nb) {
        return vomiter.TryGetComponent(out nb);
    }
    public static bool TryGetNetPlayer(this Limb limb, out ScavPlayer plr) {
        return limb.body.TryGetNetPlayer(out plr);
    }
    public static bool TryGetNetPlayer(this Body body, out ScavPlayer plr) {
        if (body.TryGetNetBody(out var nb) && nb.IsPlayer) {
            plr = nb.plr;
            return true;
        }
        plr = null;
        return false;
    }

    public static bool TryGetSyncInfo(this Item i, out SyncInfo si) {
        return NetObjectRegistry.TryGetSyncInfo(i, out si);
    }
    public static bool TryGetSyncInfo(this BuildingEntity b, out SyncInfo si) {
        return NetObjectRegistry.TryGetSyncInfo(b, out si);
    }
    //public static bool TryGetTracker(this Item i, out SyncInfoGameObjectTracker tracker) => i.TryGetComponent(out tracker);
    //public static bool TryGetTracker(this BuildingEntity b, out SyncInfoGameObjectTracker tracker) => b.TryGetComponent(out tracker);
    public static bool TryGetTracker(this Component c, out SyncInfoGameObjectTracker tracker) => c.TryGetComponent(out tracker);
    public static bool TryGetTracker(this GameObject go, out SyncInfoGameObjectTracker tracker) => go.TryGetComponent(out tracker);
    





    public static short QuantizeFloatToShort(float value, float max) {
        float normalized = Mathf.Clamp(value / max, -1f, 1f);
        return (short)Math.Round(normalized * short.MaxValue);
    }

    public static float UnquantizeFloatFromShort(short quantizedValue, float max) {
        return (quantizedValue / (float)short.MaxValue) * max;
    }

    public static ushort QuantizeFloatToUShort(float value, float max) {
        float normalized = Mathf.Clamp01(value / max);
        return (ushort)Math.Round(normalized * ushort.MaxValue);
    }

    public static float UnquantizeFloatFromUShort(ushort quantizedValue, float max) {
        return (quantizedValue / (float)ushort.MaxValue) * max;
    }


}











public class NetBody : MonoBehaviour {

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



    /// <summary>
    /// DISTANCE IS SQUARED BTW !!!!!!
    /// </summary>
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
    public knetid netId { get { 
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
        } }



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

    // Server only!
    readonly MaxCapacityQueue<BodyDamageEvent> DamageHistory = new(100);
    public BodyDamageEvent GetLastDamageEvent() {
        
        List<BodyDamageEvent> events;
        { // Forget everything before last revive
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


    public string playerName { get {
            if (IsPlayer) return plr.playerName;
            return bodyName;
        } }

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
            if (!idle_anim_allowed || carrying_person != null) {
                if (body.idleTime + Time.deltaTime > 11f) {
                    body.idleTime = 11f;
                }

                //OG: Body.Update V5t5  the idleTime part
                if (body.bodyAnimator.GetCurrentAnimatorClipInfo(0).Length != 0 && body.bodyAnimator.GetCurrentAnimatorClipInfo(0)[0].clip.name == "ExperimentSit") {
                    body.bodyAnimator.Play("Grounded");
                    body.standLerpTime = 0f;
                }
                if (body.armsAnimator.GetCurrentAnimatorClipInfo(0).Length != 0 && body.armsAnimator.GetCurrentAnimatorClipInfo(0)[0].clip.name == "ArmsSit") {
                    body.armsAnimator.Play("Grounded");
                }
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
            if (chip_punishment_protocol) {
                body.eyePanicTime = 1f;
                body.radiationSickness += Time.deltaTime; // * 2 ;
            }

        }


    }

    protected void LateUpdate() {


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
        }

        if (NearBodiesDistanceIs5) {
            //TODO if sleeping bump up the sleep qual
            // BUT CURRENTLY NOT POSSIBLE CUZ ITS DONE IN ONE FRAME LIKE THAT IN THE Body AA
        }
    }


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
                if ( syncBody.TryGetOwnerTick(out var tick) ) {
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

        NetBody._PRIV_NetIdToNetBody.Remove(netId);
        ScavPlayer.BodyToPlayerDict.Remove(body);

        if (MoodleManager.main != null && MoodleManager.main.body == body && Util.TryGetLocalBody(out var locbody)) { // fix nullreference if player disconnects
            MoodleManager.main.body = locbody;
        }

        if (Net.IsServer) {
            if (syncBody != null) {
                syncBody.Destroy();
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

        var nb = _Internal_CreateNetBody(sb, pos, name);
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
                sb.nb = _Internal_CreateNetBody(sb, Body_PlaceBody_MultiplayerPatch.SpawnLocation, plr.playerName);
            }
            var nb = sb.nb;

            nb.SetPlayer(plr);

            if (Net.IsServer && had_sb) {
                var pid = plr.GetPersistentId();

                bool hasSavedPlrState = ServerMain.Server_LastPlayerStates.TryGetValue(pid, out var val);
                // a practice world ignores the file, so the body matches a player who has no save.
                bool hasPlayerFile = !SavesystemPatch.PreserveSave && !hasSavedPlrState && PlayerCheckpoint.HasPlayerFile(plr);
                if (plr.LateJoin) {
                    if (Multiplayer.rules.LateJoinSpectate) {

                        if (hasSavedPlrState || hasPlayerFile) {

                        } else { // there is no save, a brand new player, so kill them
                            if (log.verbose) log.l($"Killing {plr} cuz he late-joined and LateJoinSpectate is on.");
                            
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
                                    plr.Server_DoAlertSingle("Late join, server put you in spectator mode.",true,true);
                                });
                            });
                        }

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
                    }
                });
            } else {
                throw new Exception("Server Only.");
            }
        }
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

    internal static NetBody _Internal_CreateNetBody(SyncBody sb, Vector2 pos, string name = null) {
#if !RELEASE
        log.l($"Creating Netbody: {sb} - {name}  stack: {new StackTrace()}");
#endif  

        if (name == null) {
            name = "bot_" + sb.Id.ToString();
        }

        var chara = UnityEngine.Object.Instantiate(ServerMain.CHARACTER_PREFAB, Vector3.zero, Quaternion.identity);
        chara.name = "Character_" + name;
        chara.SetActive(true);

        var body = chara.GetComponentInChildren<Body>();
        body.name = "Body_" + name;
        body.targetLookPos = new Vector2(1000, 460);

        body.transform.position = pos;

        var nb = body.GetOrAddComponent<NetBody>();
        sb.nb = nb;
        //nb.netId = sb.Id;
        if (sb != null) {
            nb.syncBody = sb;
        }
        NetBody._PRIV_NetIdToNetBody[sb.Id] = nb;



        return nb;
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
    public bool unchipped { get {
            if (syncBody == null) {
                return _unchipped;
            }
            return syncBody.Unchipped.Value;
        } private set {
            _unchipped = value;
            if (syncBody != null) {
                syncBody.Unchipped.Value = value;
            }
            if (plr != null) {
                _unchipped = value;
            }
        } }


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
    void SlowerUpdate() {

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
            }
        }

        if (IsBodyLocal()) {
            unchipped = Util.IsUnchipped();
        }

        last_sleep = body.sleeping;
        last_alive = body.alive;
    }

    

    void UpdateBodySleepShare() {
        if (Multiplayer.rules.DisableSleep) return;
        if (!body.alive) return;
        if (!body.sleeping) return;
        if (NearBodies.Count == 0) return;

        NearBodiesDistanceIs5 = false;

        var c = 1f / (float)NearBodies.Count;
        foreach (var onb in NearBodies) {
            if ((onb.body.energy - 5f > body.energy) ) {
                body.energy = Mathf.MoveTowards(body.energy, onb.body.energy, 0.5f * c);

                if (KM.DistanceCheck_PreSqr(this.transform.position, onb.transform.position, 30)) {
                    NearBodiesDistanceIs5 = true;
                }
            }
        }

    }
    void UpdateBodyTempShare() {
        if (!body.alive) return;

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
        var aaaaa = new List<NetBody> { this };
        var ggg = this ;
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

    // called from fixeddeltatime
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
            var targetdowntorsorb = targetbody.GetLowerTorso().rb;
            var downtorsorb = body.GetUpperTorso().rb;


            float side = (body.isRight ? 1f : (-1f));
            var standupamount = Vector2.up * (1 - piggybacking_on.body.crouchAmount);
            Vector2 target_uppertorso_pos = targettorsorb.position + (standupamount ) + (Vector2.left * side);
            // check if its in a wall or something
            if (Physics2D.OverlapCircle(target_uppertorso_pos,1, LayerMask.GetMask("Ground")) != null) {
                target_uppertorso_pos = Vector2.LerpUnclamped(piggybacking_on.pos + standupamount, target_uppertorso_pos,0.1f) ;
            }
            body.crouchAmount = piggybacking_on.body.crouchAmount;

            SetBodyPosition(target_uppertorso_pos);

            {
                foreach (var item in body.limbs) {
                    item.rb.velocity = Vector2.Lerp(item.rb.velocity, targetbody.rb.velocity,0.2f);
                }
                torsorb.velocity = targetbody.rb.velocity;
                downtorsorb.velocity = targetbody.rb.velocity;
                body.GetHead().rb.velocity = targetbody.rb.velocity;
            }

            torsorb.rotation = targettorsorb.rotation;
            torsorb.angularVelocity = targettorsorb.angularVelocity;

            if (Mathf.DeltaAngle(targetdowntorsorb.rotation, downtorsorb.rotation) > 10) {
                downtorsorb.rotation = Mathf.MoveTowardsAngle(downtorsorb.rotation,targetdowntorsorb.rotation,Time.fixedDeltaTime * 100f);
            }


            //TODO move the body parts as needed

        }

    }


    public bool CanBeCarriedBySomeone() {
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
                return false ;
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

            if ( victim.IsPlayer ) {
                var writer = Net.CreateWriter(NetmsgId.CLIENT_Knockback_thing);
                writer.Put(new BodyKnockbackPacket {
                    Velocity = Vector2.zero,
                    CloseUI = true,
                });
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, victim.plr.playerId);
            }

        }
            
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


    public void Server_RemindPlayersCurrentState( bool reliable = true) {
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












}









