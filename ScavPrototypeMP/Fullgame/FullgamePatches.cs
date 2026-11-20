using CasualtiesTogetherUtils;
using DiscordRPC;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEngine.Random;

namespace Together;


public static class FullgameCompatExtensions {
    private static readonly MethodInfo _propCanRead = ReflectionHelp.PropertyGetter(typeof(Body), "canRead");
    private static readonly MethodInfo _propFinishSpot = ReflectionHelp.PropertyGetter(typeof(DislocationMinigame), "FinishSpot");
    private static readonly FieldInfo _fieldDismemberChance = ReflectionHelp.Field(typeof(ExplosionParams), "dismemberChance");
    private static readonly MethodInfo _methodHasHearingProtection = ReflectionHelp.Method(typeof(Body), "HasHearingProtection");

    public static bool CanRead(this Body b) {
        if (_propCanRead == null) {
            return !b.IsMindwiped();
        }
        return (bool)_propCanRead.Invoke(b, new object[0]);
    }
    public static float GetDismemberChance(this ExplosionParams ep) {
        if (_fieldDismemberChance == null) {
            return 0;
        }
        return (float)_fieldDismemberChance.GetValue(ep);
    }
    public static void SetDismemberChance(this ExplosionParams ep, float value) {
        if (_fieldDismemberChance == null || ep == null) {
            return;
        }
        _fieldDismemberChance.SetValue(ep, value);
    }
    public static bool MPHasHearingProtection(this Body b) {
        if (_methodHasHearingProtection == null) {
            return b.HasWearable("earmuffs");
        }
        return (bool)_methodHasHearingProtection.Invoke(b, new object[0]);
    }
    public static Vector2 GetFinishSpot(this DislocationMinigame dm) {
        if (_propFinishSpot == null || dm == null) {
            return new Vector2(375f, 54.6f);
        }
        return (Vector2)_propFinishSpot.Invoke(dm, new object[0]);
    }

}


public class FullgameMultiplayerPatcher : CasualtiesTogetherSingleton {
    bool _lastCutsceneActive;
    bool _cutsceneArmed;

    void Start() {
#if RELEASE
        if (Util.GameIsDemo()) {
            return;
        }
#endif

        Traverse.Create(Constants.DefaultMineExplosionParams).Field("dismemberChance").SetValue(0.15f);
    }

#if FULLGAME
    void Update() {
        if (!Net.IsRunning || !Util.IsInWorld() || WorldGeneration.world == null) {
            _cutsceneArmed = false;
            return;
        }
        bool active = WorldGeneration.world.cutsceneActive;
        if (!_cutsceneArmed) {
            _lastCutsceneActive = active;
            _cutsceneArmed = true;
            return;
        }
        if (active == _lastCutsceneActive) {
            return;
        }
        _lastCutsceneActive = active;
        if (!Net.IsServer) {
            return;
        }
        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CutsceneActive);
        writer.Put(active);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CutsceneActive)]
    static void Client_CutsceneActive(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out bool active);
        if (!Util.IsInWorld() || WorldGeneration.world == null) {
            return;
        }
        WorldGeneration.world.cutsceneActive = active;
        SharedMain.IsInCutscene = active;
    }
#endif
}










#if FULLGAME



[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.SelectCharacter))]
static class PreRunScript_SelectCharacter_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) {
            return true;
        }
        if (CharacterSelectWait.SpeciesLocked) {
            CharacterSelectWait.EnforceLockedSpecies();
            return false;
        }
        return true;
    }

    static void Postfix(PreRunScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        // portraits only change the pick; Ready on the start/load button commits it.
        CharacterSelectWait.OnLocalCharacterBrowsed(PreRunScript.characterSelected);
    }
}






[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.CheckForCheats))]
static class ConsoleScript_CheckForCheats_MultiplayerPatch {
    static void Prefix(ConsoleScript __instance, ref bool __state) {
        __state = SteamManager.disableAchievements;
        if (!Net.IsRunning) {
            return;
        }
        Con.ConFailIfCheatsDisabled(); // throw in multiplayer when cheats are off
    }

    // stock just sets SteamManager.disableAchievements on this machine - push to the whole lobby/save
    static void Postfix(bool __state) {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        // only the first break for this session - don't re-alert every subsequent cheat
        if (__state || !SteamManager.disableAchievements) {
            return;
        }
        AchievementBreakSync.Server_Broadcast(showAlert: true);
    }
}




[HarmonyPatch(typeof(CustomItemBehaviour), nameof(CustomItemBehaviour.CryoActivate))]
public static class CustomItemBehaviour_CryoActivate_MultiplayerPatch {
    static float last_zoomtime = 0;

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        IEnumerable<CodeInstruction> codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);

        return codes;
    }
    static void Prefix(CustomItemBehaviour __instance) {
        if (!Net.IsRunning) {
            return;
        }
        BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody(); // pick nearest body now
    }
}



[HarmonyPatch]
static class GuardSpawner_MultiplayerPatch {

    class TrackerGuardSpawner : BaseComponentTracker<GuardSpawner> {
        protected override void TrackerAwake() {
            
        }

        void Update() {
            // host CreateGuard opens this door. clients never ran that, so the surgery entrance stayed solid.
            if (Net.IsClient) {
                if ((bool)EndLabScript.instance && EndLabScript.instance.doingEscape) {
                    og.openProgress = 0f;
                    Collider2D[] cols = og.GetComponentsInChildren<Collider2D>(true);
                    for (int i = 0; i < cols.Length; i++) {
                        if (cols[i] != null) {
                            cols[i].enabled = false;
                        }
                    }
                }
                return;
            }

            if ((bool)EndLabScript.instance && EndLabScript.instance.doingEscape) {
                og.openProgress += Time.deltaTime;
            }

            (NetBody near, float b) = NetBody.GetNearestBody(og.transform.position, true);
            float num = Mathf.Sqrt(b);
            if (!(num < og.minDistanceToSpawn) && !(num > og.maxDistanceToSpawn) && og.guardsToSpawn > 0 && !(og.openProgress < og.minTimeBetweenSpawn)) {
                og.CreateGuard();
            }
        }
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.Start))]
    static bool StartPrefix(GuardSpawner __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        __instance.GetOrAddComponent<TrackerGuardSpawner>();

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.Update))]
    static bool UpdatePrefix() {
        if (!Net.IsRunning) {
            return true;
        }

        return false;
    }
}

// stock aim is the camera body, so a host standing back sees a frozen guard.
// the host runs the guard and aims at whoever is closest. clients copy that pose.
[HarmonyPatch(typeof(GuardScript), "body", MethodType.Getter)]
static class GuardScript_Body_Nearest_MultiplayerPatch {
    static bool Prefix(GuardScript __instance, ref Body __result) {
        if (!Net.IsRunning || !Net.IsServer) {
            return true;
        }
        __result = GuardHost.Nearest(__instance);
        return __result == null;
    }
}

[HarmonyPatch(typeof(GuardScript), "Update")]
static class GuardScript_Update_Host_MultiplayerPatch {
    static bool Prefix(GuardScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            GuardHost.Hold(__instance);
            return false;
        }
        return true;
    }

    static void Postfix(GuardScript __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null) {
            return;
        }
        GuardHost.AfterSim(__instance);
    }
}

sealed class GuardPose : MonoBehaviour {
    public bool Has;
    public Vector2 Pos;
    public float Scale;
    public float Arm;
    public float Head;
    public float Laser;
    public byte Walk;
    public byte Flags;
    public ushort Target;
    public bool Died;
    public float SwingUntil;
    public float ShotUntil;
}

static class GuardHost {
    const byte Fired = 1;
    const byte Melee = 2;
    const byte Dead = 4;
    const byte Stunned = 8;

    struct Sent {
        public int Ammo;
        public bool Melee;
        public bool Dead;
        public float Next;
    }

    static readonly Dictionary<int, Sent> Last = new Dictionary<int, Sent>();
    static int _lastPruneAt = StaleIds.FirstThreshold;

    [CTPostInit]
    static void Init() {
        ClientMain.ClientCustomCommandsDict["guard_pose"] = (cmd, reader) => {
            reader.Get(out ushort id);
            reader.Get(out Vector2 pos);
            reader.Get(out float scale);
            reader.Get(out float arm);
            reader.Get(out float head);
            reader.Get(out float laser);
            reader.Get(out byte walk);
            reader.Get(out byte flags);
            reader.Get(out ushort target);
            Apply(id, pos, scale, arm, head, laser, walk, flags, target);
        };
    }

    public static Body Nearest(GuardScript guard) {
        if (guard == null) {
            return null;
        }
        Body best = null;
        float bestSqr = float.MaxValue;
        Vector2 origin = guard.transform.position;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive) {
                continue;
            }
            float sqr = ((Vector2)plr.body.transform.position - origin).sqrMagnitude;
            if (sqr < bestSqr) {
                bestSqr = sqr;
                best = plr.body;
            }
        }
        return best;
    }

    public static void AfterSim(GuardScript guard) {
        if (guard.doingMelee) {
            Body hit = Nearest(guard);
            if (hit != null && hit.TryGetNetBody(out NetBody hurt)) {
                MedicalSync.Server_QueueSendCharacterHealth(hurt, true);
            }
        }
        Publish(guard);
    }

    public static void Hold(GuardScript guard) {
        if (guard == null || !guard.TryGetComponent(out GuardPose pose) || !pose.Has) {
            return;
        }
        ApplyPose(guard, pose);
    }

    static void Publish(GuardScript guard) {
        if (ServerMain.AllClientIdsExceptHost == null || ServerMain.AllClientIdsExceptHost.Count == 0) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(guard.gameObject, out SyncInfo si) && !NetObjectRegistry.TryGetSyncInfoOrRegister(guard.gameObject, out si)) {
            return;
        }
        if (si == null) {
            return;
        }
        int key = guard.GetInstanceID();
        StaleIds.Prune<GuardScript, Sent>(Last, ref _lastPruneAt);
        int ammo = Traverse.Create(guard).Field("ammo").GetValue<int>();
        bool melee = guard.doingMelee;
        bool dead = false;
        if (guard.TryGetComponent(out BuildingEntity build)) {
            dead = build.GetHealth() <= 0f;
        }
        bool known = Last.TryGetValue(key, out Sent prev);
        bool fired = known && ammo < prev.Ammo;
        bool meleeEdge = melee && (!known || !prev.Melee);
        bool deadEdge = dead && (!known || !prev.Dead);
        bool due = fired || meleeEdge || deadEdge || !known || Time.time >= prev.Next;
        Last[key] = new Sent { Ammo = ammo, Melee = melee, Dead = dead, Next = due ? Time.time + 0.05f : prev.Next };
        if (!due) {
            return;
        }
        byte flags = 0;
        if (fired) {
            flags |= Fired;
        }
        if (meleeEdge) {
            flags |= Melee;
        }
        if (dead) {
            flags |= Dead;
        }
        if (guard.armSprite != null && guard.armSprite.sprite == guard.armsStun) {
            flags |= Stunned;
        }
        ushort target = 0;
        Body who = Nearest(guard);
        if (who != null && who.TryGetNetBody(out NetBody nb) && nb.plr != null) {
            target = nb.plr.playerId.id;
        }
        float laser = 0f;
        if (guard.laserLine != null) {
            laser = guard.laserLine.transform.localScale.y;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("guard_pose");
        writer.Put((ushort)si.syncId);
        writer.Put((Vector2)guard.transform.position);
        writer.Put(guard.transform.localScale.x);
        writer.Put(guard.arms != null ? guard.arms.eulerAngles.z : 0f);
        writer.Put(guard.head != null ? guard.head.eulerAngles.z : 0f);
        writer.Put(laser);
        writer.Put(WalkFrame(guard));
        writer.Put(flags);
        writer.Put(target);
        Net.Server_SendToClients((fired || meleeEdge || deadEdge) ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    static byte WalkFrame(GuardScript guard) {
        if (guard.myBody == null || guard.bodyMoveSprites == null) {
            return 255;
        }
        for (int i = 0; i < guard.bodyMoveSprites.Length; i++) {
            if (guard.myBody.sprite == guard.bodyMoveSprites[i]) {
                return (byte)i;
            }
        }
        return 255;
    }

    static void Apply(ushort id, Vector2 pos, float scale, float arm, float head, float laser, byte walk, byte flags, ushort target) {
        if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out SyncInfo si) || si.go == null) {
            return;
        }
        if (!si.go.TryGetComponent(out GuardScript guard)) {
            return;
        }
        GuardPose pose = guard.GetOrAddComponent<GuardPose>();
        pose.Has = true;
        pose.Pos = pos;
        pose.Scale = scale;
        pose.Arm = arm;
        pose.Head = head;
        pose.Laser = laser;
        pose.Walk = walk;
        pose.Flags = flags;
        pose.Target = target;
        ApplyPose(guard, pose);
    }

    static void ApplyPose(GuardScript guard, GuardPose pose) {
        guard.transform.position = pose.Pos;
        guard.transform.localScale = new Vector3(Mathf.Abs(pose.Scale) < 0.01f ? 1f : pose.Scale, 1f, 1f);
        Animator anim = null;
        if (guard.transform.childCount > 0) {
            anim = guard.transform.GetChild(0).GetComponent<Animator>();
        }
        if ((pose.Flags & Dead) != 0) {
            if (!pose.Died) {
                pose.Died = true;
                if (anim != null) {
                    anim.enabled = true;
                    anim.Play("GuardFall");
                }
                Collider2D col = guard.GetComponent<Collider2D>();
                if (col != null) {
                    col.enabled = false;
                }
                if (guard.laserLine != null) {
                    guard.laserLine.enabled = false;
                }
                if (guard.laserDot != null) {
                    guard.laserDot.enabled = false;
                }
            }
            return;
        }
        if ((pose.Flags & Fired) != 0 && Time.time >= pose.ShotUntil && guard.barrel != null) {
            pose.ShotUntil = Time.time + 0.12f;
            Sound.Play("pistolshot", guard.transform.position);
            TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = true;
            try {
                TurretScript.Shoot(new FireInfo {
                    pos = guard.barrel.position,
                    dir = guard.barrel.right * Mathf.Sign(pose.Scale == 0f ? 1f : pose.Scale),
                    doTinnitus = false,
                    ignoreBody = false,
                    structureDamage = 90f,
                    animalDamage = 90f,
                    playerDamageMultiplier = 0.35f,
                    ignoreTrans = guard.transform,
                });
            } finally {
                TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = false;
            }
        }
        if ((pose.Flags & Melee) != 0 && Time.time >= pose.SwingUntil) {
            pose.SwingUntil = Time.time + 0.95f;
            if (anim != null) {
                anim.enabled = true;
                anim.Play("GuardSwing", -1, 0f);
            }
            if (NetBody.TryGetLocalNetBody(out NetBody me) && me.plr != null && me.body != null && me.plr.playerId.id == pose.Target) {
                me.body.Ragdoll();
            }
        }
        if (Time.time < pose.SwingUntil) {
            return;
        }
        if (guard.arms != null) {
            guard.arms.rotation = Quaternion.Euler(0f, 0f, pose.Arm);
        }
        if (guard.head != null) {
            guard.head.rotation = Quaternion.Euler(0f, 0f, pose.Head);
        }
        if (guard.laserLine != null) {
            guard.laserLine.transform.localScale = new Vector3(1f, pose.Laser, 1f);
            guard.laserLine.enabled = true;
        }
        if (guard.laserDot != null && guard.laserLine != null) {
            guard.laserDot.transform.localPosition = (Vector2)guard.laserLine.transform.localPosition + Vector2.right * pose.Laser;
            guard.laserDot.enabled = true;
        }
        if (guard.myBody != null) {
            if (pose.Walk == 255) {
                if (guard.bodyStaySprite != null) {
                    guard.myBody.sprite = guard.bodyStaySprite;
                }
            } else if (guard.bodyMoveSprites != null && pose.Walk < guard.bodyMoveSprites.Length) {
                guard.myBody.sprite = guard.bodyMoveSprites[pose.Walk];
            }
        }
        if (guard.armSprite != null) {
            guard.armSprite.sprite = (pose.Flags & Stunned) != 0 ? guard.armsStun : guard.armsNormal;
        }
        if (anim != null) {
            anim.enabled = false;
        }
    }
}










[HarmonyPatch]
static class CargoDrillPod_MultiplayerPatch {

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.OnUse))]
    static bool StartPrefix(CargoDrillPod __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_CargoDrillPodOnUse);
        writer.Put(AnyObjectNetId.GetNetIdFromGameObject(__instance.gameObject));
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.Update))]
    static bool UpdatePrefix(CargoDrillPod __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        try {
            TryMarkReady(__instance);
        } catch (Exception ex) {
            Together.log.warn("[CargoDrill] Update ready check failed: " + ex.Message);
            if (__instance != null && !__instance.didReady && IsCargoUnlocked()) {
                __instance.didReady = true;
            }
        }
        return false;
    }

    static bool IsCargoUnlocked() {
        if (BlueHeartMp.CargoUnlocked) {
            return true;
        }
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr != null && plr.body != null && plr.body.hasCargo) {
                return true;
            }
        }
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            if (kv.Key != null && kv.Key.hasCargo) {
                return true;
            }
        }
        return false;
    }

    static void TryMarkReady(CargoDrillPod drill) {
        if (drill == null || drill.didReady || !IsCargoUnlocked()) {
            return;
        }
        drill.didReady = true;
        BlueHeartMp.CargoUnlocked = true;
        if (drill.bld == null) {
            drill.bld = drill.GetComponent<BuildingEntity>();
        }
        try {
            UsableObject usable = drill.GetComponent<UsableObject>();
            if (usable != null) {
                usable.toggleString = Locale.GetOther("finaltravel");
            }
        } catch (Exception ex) {
            Together.log.warn("[CargoDrill] toggleString failed: " + ex.Message);
        }
        try {
            if (drill.bld != null) {
                drill.bld.description = Locale.GetBuilding("highpowereddrillpoddsc2");
            }
        } catch (Exception ex) {
            Together.log.warn("[CargoDrill] description failed: " + ex.Message);
        }
        try {
            GameObject tesla = Utils.Create("Special/Tesla", drill.transform);
            if (tesla != null) {
                tesla.transform.localScale = Vector3.one * 2f;
            }
        } catch (Exception ex) {
            Together.log.warn("[CargoDrill] Tesla create failed: " + ex.Message);
        }
    }

    static void RealOnUse(Body b, CargoDrillPod drill) {
        if (drill == null) {
            return;
        }
        try {
            TryMarkReady(drill);
        } catch (Exception ex) {
            Together.log.warn("[CargoDrill] RealOnUse ready failed: " + ex.Message);
            if (!drill.didReady && IsCargoUnlocked()) {
                drill.didReady = true;
            }
        }
        if (!drill.didReady) {
            if (b != null && b.talker != null) {
                b.talker.Talk(Locale.GetCharacter("endpodexamine"));
            }
            return;
        }
        if (drill.started) {
            return;
        }
        if (WorldGeneration.world == null) {
            Together.log.warn("[CargoDrill] ready but WorldGeneration.world is null");
            return;
        }
        drill.started = true;
        try {
            WorldGeneration.world.StartCoroutine("RegenerateWorld", false);
            Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
        } catch (Exception ex) {
            drill.started = false;
            Together.log.warn("[CargoDrill] start regenerate failed: " + ex);
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_CargoDrillPodOnUse)]
    static void Server_aaaaaaaaaaaaaaaaaaa(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out AnyObjectNetId aoni);

        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer user, out Body body) || body == null) {
            return;
        }
        CargoDrillPod drill = null;
        if (aoni.TryGetGameObject(out GameObject go) && go != null) {
            go.TryGetComponent(out drill);
        }
        if (drill == null) {
            drill = UnityEngine.Object.FindObjectOfType<CargoDrillPod>();
        }
        if (drill != null) {
            RealOnUse(body, drill);
        } else {
            Together.log.warn($"[CargoDrill] use from {clientId} but no highpowereddrillpod");
        }
    }

}


// crimson Ascent blue heart (cargo): host starts once, teleports nearby living players, EmbedCargo all of them.
// clients must not start their own cutscene (that duplicated 2D SFX / only impaled the local body).
static class BlueHeartMp {
    public const float TriggerRange = 10f;
    public const float NearbyRange = 48f;
    public static bool ClientCutsceneFromHost;
    public static bool CargoUnlocked;
    static readonly HashSet<Body> _affected = new HashSet<Body>();
    static readonly HashSet<int> _cargoEnsuredBodies = new HashSet<int>();
    static float _nextEnsureAll;

    public static List<Body> CollectNearby(Vector2 heartPos, float range) {
        List<Body> list = new List<Body>();
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            Body body = kv.Key;
            if (body == null || !body.alive) {
                continue;
            }
            if (Vector2.Distance(body.transform.position, heartPos) <= range) {
                list.Add(body);
            }
        }
        return list;
    }

    public static List<Body> CollectAllBodies() {
        List<Body> list = new List<Body>();
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            Body body = kv.Key;
            if (body != null && !list.Contains(body)) {
                list.Add(body);
            }
        }
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null || plr.body == null || list.Contains(plr.body)) {
                continue;
            }
            list.Add(plr.body);
        }
        return list;
    }

    public static Body FindTriggerBody(Vector2 heartPos) {
        Body best = null;
        float bestDist = TriggerRange;
        foreach (Body body in CollectNearby(heartPos, TriggerRange)) {
            if (!body.standing) {
                continue;
            }
            float d = Vector2.Distance(body.transform.position, heartPos);
            if (d < bestDist) {
                bestDist = d;
                best = body;
            }
        }
        return best;
    }

    public static void HostTeleportNearby(BlueHeart heart, List<Body> nearby) {
        _affected.Clear();
        Vector2 heartPos = heart.transform.position;
        int i = 0;
        foreach (Body body in nearby) {
            if (body == null) {
                continue;
            }
            _affected.Add(body);
            float angle = (i / (float)Mathf.Max(1, nearby.Count)) * Mathf.PI * 2f;
            Vector2 dest = heartPos + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 2.5f;
            body.forceNoMovement = true;
            if (body.TryGetNetBody(out NetBody nb)) {
                nb.SetBodyPosition(dest);
                body.SetVelocity(Vector2.zero);
                nb.Server_RemindPlayersCurrentState(true);
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            } else {
                body.transform.position = dest;
            }
            i++;
        }
    }

    public static void HostAnnounceCutscene() {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_BlueHeartCutscene);
        writer.Put(true);
        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void ApplyCargoEffectsNoSfx(Body body) {
        if (body == null || body.hasCargo) {
            return;
        }
        body.hasCargo = true;
        if (body.cargo != null) {
            body.cargo.SetActive(true);
        }
        Limb[] limbs = body.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            limbs[i].bleedAmount *= 0.2f;
        }
        body.internalBleeding *= 0.2f;
        body.radiationSickness *= 0.25f;
        body.limbs[2].muscleHealth = 20f;
        body.limbs[2].skinHealth = 20f;
        body.limbs[2].pain += 80f;
        body.limbs[2].bleedAmount += 15f;
        body.Ragdoll();
        body.shock = 50f;
    }

    public static void HostEmbedAffected(Body speaker, Vector2 gorePos) {
        // one shared gore so N embeds don't stack for clients/host
        Sound.Play("gore", gorePos, twoDimensional: false, pitchShift: false);
        Body talkBody = (speaker != null && _affected.Contains(speaker) && speaker.alive) ? speaker : null;
        int embedded = 0;
        foreach (Body body in _affected) {
            if (body == null || body.hasCargo) {
                continue;
            }
            ApplyCargoEffectsNoSfx(body);
            embedded++;
            if (talkBody == null && body.alive) {
                talkBody = body;
            }
            if (body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
        if (embedded > 0 || AnyBodyHasCargo()) {
            CargoUnlocked = true;
        }
        if (talkBody != null && talkBody.talker != null) {
            talkBody.talker.TalkDelayed(3f, Locale.GetCharacter("cargoimpale"), null, force: false, resetTalkTimer: true, 9);
            talkBody.talker.TalkDelayed(11f, Locale.GetCharacter("cargoimpale2"), null, force: false, resetTalkTimer: true, 9);
            talkBody.talker.TalkDelayed(20f, Locale.GetCharacter("cargoimpale3"), null, force: false, resetTalkTimer: true, 9);
        }
        _affected.Clear();
    }

    static bool AnyBodyHasCargo() {
        foreach (Body body in BodyToPlayerSafe()) {
            if (body != null && body.hasCargo) {
                return true;
            }
        }
        return false;
    }

    static IEnumerable<Body> BodyToPlayerSafe() {
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            if (kv.Key != null) {
                yield return kv.Key;
            }
        }
    }

    public static bool IsProtectedCargoDrillBuilding(BuildingEntity be) {
        if (be == null) {
            return false;
        }
        if (be.GetComponent<CargoDrillPod>() != null) {
            return true;
        }
        string id = be.id;
        if (string.IsNullOrEmpty(id)) {
            return false;
        }
        if (id == "highpowereddrillpod") {
            return true;
        }
        return id.StartsWith("highpowereddrillpod", StringComparison.OrdinalIgnoreCase);
    }

    // ascent load-in: late join and layer transition both arrive through FinishedWorldgen.
    // if they still have no cargo, embed in place. players who already pulled the heart stay clear.
    public static void Server_TryApplyCargoToLateJoiner(ScavPlayer plr) {
        Server_EnsureCargoOnAscentLoad(plr);
    }

    public static void Server_EnsureCargoOnAscentLoad(ScavPlayer plr) {
        if (!Net.IsServer || plr == null || plr.body == null) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 10 || WorldGeneration.world.generatingWorld) {
            return;
        }
        if (WorldGeneration.GetRunSettingBool("endless")) {
            return;
        }

        Body body = plr.body;
        int bodyId = body.GetInstanceID();
        if (MacroFleshRebuilderMp.HasRemovedHeart(plr.playerId)) {
            _cargoEnsuredBodies.Add(bodyId);
            return;
        }
        if (body.hasCargo) {
            _cargoEnsuredBodies.Add(bodyId);
            CargoUnlocked = true;
            return;
        }

        if (!_cargoEnsuredBodies.Add(bodyId)) {
            return;
        }
        if (body.cargo == null || body.limbs == null || body.limbs.Length <= 2) {
            _cargoEnsuredBodies.Remove(bodyId);
            return;
        }

        ApplyCargoEffectsNoSfx(body);
        CargoUnlocked = true;
        if (body.TryGetNetBody(out NetBody nb)) {
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
        Together.log.l($"[BlueHeart] ascent load-in cargo applied to {plr}");
    }

    // host safety net: BlueHeart is destroyed at the end of the cutscene, so this also runs from Plugin.Update.
    public static void Server_EnsureAllMissingCargoOnAscent() {
        if (!Net.IsServer) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 10
            || WorldGeneration.world.generatingWorld) {
            return;
        }
        if (Time.unscaledTime < _nextEnsureAll) {
            return;
        }
        _nextEnsureAll = Time.unscaledTime + 1f;

        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            Server_EnsureCargoOnAscentLoad(plr);
        }
    }

    public static void ResetCargoEnsureState() {
        _cargoEnsuredBodies.Clear();
        _nextEnsureAll = 0f;
    }
}

[HarmonyPatch(typeof(BlueHeart))]
static class BlueHeart_MultiplayerPatch {
    static BlueHeart_MultiplayerPatch() {
        // chunk sleep was turning the heart off while everyone was still at the pod, which stopped the pulse packets.
        ChunkSleep.Ignore(typeof(BlueHeart));
    }

    static float _nextPulseSend;
    static ushort _pulseSerial;
    static ushort _sendSeq;
    static float _clientTime;
    static float _clientRate = 1f;
    static float _clientRecvAt;
    static ushort _clientSerial;
    static ushort _clientSeq;
    static bool _clientHasPulse;
    static bool _clientHeartbeat;
    static Vector2 _clientHeartPos;
    static bool _clientHasHeartPos;

    internal static string PulseClock() {
        if (!_clientHasPulse) {
            return "has=0";
        }
        return "has=1 t=" + _clientTime.ToString("0.00")
            + " rate=" + _clientRate.ToString("0.00")
            + " age=" + (Time.time - _clientRecvAt).ToString("0.00")
            + " serial=" + _clientSerial
            + " heart=" + (_clientHasHeartPos ? _clientHeartPos.x.ToString("0.0") + "," + _clientHeartPos.y.ToString("0.0") : "-");
    }

    static void HostSendPulse(float timeSincePulsed, float rate, bool pulsed, Vector2 heartPos) {
        if (!Net.IsServer) {
            return;
        }
        if (pulsed) {
            _pulseSerial++;
            FindTheBugReports.Say("pulse", "host-bang serial=" + _pulseSerial
                + " t=" + timeSincePulsed.ToString("0.00")
                + " rate=" + rate.ToString("0.00")
                + " pos=" + heartPos.x.ToString("0.0") + "," + heartPos.y.ToString("0.0"));
        } else if (Time.time < _nextPulseSend) {
            return;
        }
        _nextPulseSend = Time.time + 0.1f;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_BlueHeartPulse);
        writer.Put(timeSincePulsed);
        writer.Put(rate);
        writer.Put(_pulseSerial);
        writer.Put(++_sendSeq);
        writer.Put(heartPos.x);
        writer.Put(heartPos.y);
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BlueHeartPulse)]
    static void Client_BlueHeartPulse(knetid _, NetDataReader reader) {
        reader.Get(out float timeSincePulsed);
        reader.Get(out float rate);
        reader.Get(out ushort serial);
        reader.Get(out ushort seq);
        reader.Get(out float heartX);
        reader.Get(out float heartY);
        if (_clientHasPulse) {
            int ahead = (short)(seq - _clientSeq);
            if (ahead <= 0) {
                return;
            }
            if (serial != _clientSerial) {
                _clientHeartbeat = true;
                FindTheBugReports.Say("pulse", "client-bang serial=" + serial + " seq=" + seq
                    + " t=" + timeSincePulsed.ToString("0.00")
                    + " pos=" + heartX.ToString("0.0") + "," + heartY.ToString("0.0"));
            }
        }
        if (!_clientHasPulse) {
            FindTheBugReports.Say("pulse", "client-first serial=" + serial + " seq=" + seq
                + " t=" + timeSincePulsed.ToString("0.00")
                + " pos=" + heartX.ToString("0.0") + "," + heartY.ToString("0.0"));
        }
        _clientHasPulse = true;
        _clientTime = timeSincePulsed;
        _clientRate = rate;
        _clientRecvAt = Time.time;
        _clientSerial = serial;
        _clientSeq = seq;
        _clientHeartPos = new Vector2(heartX, heartY);
        _clientHasHeartPos = true;
    }

    [HarmonyPrefix]
    [HarmonyPatch("Update")]
    static bool UpdatePrefix(BlueHeart __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsServer) {
            BlueHeartMp.Server_EnsureAllMissingCargoOnAscent();
        }
        if (WorldGeneration.world != null && WorldGeneration.world.generatingWorld) {
            return false;
        }

        // keep trap-clear / pulse / look - but host owns the cutscene trigger; clients never self-start.
        Traverse tr = Traverse.Create(__instance);
        if (!tr.Field<bool>("didTrapRemoval").Value) {
            tr.Field<bool>("didTrapRemoval").Value = true;
            Collider2D[] hits = Physics2D.OverlapAreaAll(
                __instance.transform.position + new Vector3(-64f, -64f),
                __instance.transform.position + new Vector3(32f, 32f),
                LayerMask.GetMask("Ground"));
            for (int i = 0; i < hits.Length; i++) {
                if (hits[i].TryGetComponent(out BuildingEntity be) && !BlueHeartMp.IsProtectedCargoDrillBuilding(be)) {
                    UnityEngine.Object.Destroy(be.gameObject);
                }
            }
        }

        bool cutsceneStarted = tr.Field<bool>("cutsceneStarted").Value;
        if (!cutsceneStarted && Net.IsServer) {
            Body trigger = BlueHeartMp.FindTriggerBody(__instance.transform.position);
            if (trigger != null) {
                tr.Field<bool>("cutsceneStarted").Value = true;
                List<Body> nearby = BlueHeartMp.CollectAllBodies();
                if (nearby.Count == 0) {
                    nearby.Add(trigger);
                }
                BlueHeartMp.HostTeleportNearby(__instance, nearby);
                BlueHeartMp.HostAnnounceCutscene();
                PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false, force: true);
                __instance.StartCoroutine(__instance.Cutscene());
                if (!WorldGeneration.GetRunSettingBool("endless") && trigger.talker != null) {
                    trigger.talker.Talk(Locale.GetCharacter("cargo2"));
                }
                // single obtain sting - clients play their own on announce; host plays here once
                Sound.Play("blueHeartObtain", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            }
        }

        // look / music stay local. the location ring's pulse clock is the host's.
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local == null) {
            return false;
        }
        Vector2 origPos = tr.Field<Vector2>("origPos").Value;
        float pulseSpeedAdd = tr.Field<float>("pulseSpeedAdd").Value;
        float timeSincePulsed = tr.Field<float>("timeSincePulsed").Value;
        float pulseTime = tr.Field<float>("pulseTime").Value;
        bool wasClose = tr.Field<bool>("wasClose").Value;
        Material mat = OwnMat(__instance, tr, tr.Field<Material>("mat").Value);

        float num = Vector2.Distance(__instance.transform.position, local.transform.position) / 500f;
        // stock drops the ring to nothing 500 units above the heart. this layer's spawn
        // is about 560 above it, so the beacon was gone from where everyone landed.
        float num2 = Mathf.Max(local.transform.position.y - __instance.transform.position.y, 0f) / 1400f;
        if (num < 0.075f) {
            PlayerCamera.main.customLookPos = Vector2.Lerp(__instance.transform.position + Vector3.up * 3f, local.transform.position, num / 0.075f);
        }
        if (!wasClose && num < 0.06f) {
            tr.Field<bool>("wasClose").Value = true;
            local.happiness += 5f;
            if (!WorldGeneration.GetRunSettingBool("endless") && !cutsceneStarted && !BlueHeartMp.ClientCutsceneFromHost) {
                local.talker.Talk(Locale.GetCharacter("cargo1"));
            }
        }
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = Mathf.Clamp01((num - 0.1f) * 30f);
        }
        float num3 = 1f + pulseSpeedAdd;
        if (num < 0.5f) {
            num3 += (0.5f - num) * 6f;
        }
        float shownTime;
        if (Net.IsServer) {
            timeSincePulsed += Time.deltaTime * num3;
            pulseTime -= Time.deltaTime * (num3 * num3);
            tr.Field<float>("timeSincePulsed").Value = timeSincePulsed;
            tr.Field<float>("pulseTime").Value = pulseTime;
            bool pulsed = false;
            if (pulseTime < 0f) {
                __instance.Pulse();
                pulsed = true;
                timeSincePulsed = 0f;
                Sound.Play("blueheartbeat", __instance.transform.position, twoDimensional: true, pitchShift: false, null, 1f - num);
            }
            shownTime = timeSincePulsed;
            HostSendPulse(shownTime, num3, pulsed, origPos);
        } else if (_clientHasPulse) {
            if (_clientHasHeartPos && !cutsceneStarted) {
                origPos = _clientHeartPos;
                tr.Field<Vector2>("origPos").Value = origPos;
            }
            shownTime = _clientTime + (Time.time - _clientRecvAt) * _clientRate;
            if (_clientHeartbeat) {
                _clientHeartbeat = false;
                Sound.Play("blueheartbeat", __instance.transform.position, twoDimensional: true, pitchShift: false, null, 1f - num);
            }
        } else {
            shownTime = timeSincePulsed;
        }
        float value = shownTime * 50f;
        float value2 = 0.5f + num * 8f;
        float num4 = (1f - num2) * Mathf.Clamp01((10f - shownTime) * 0.1f);
        if (mat != null) {
            mat.SetFloat("_DesiredDistance", value);
            mat.SetFloat("_EdgeSize", value2);
            mat.SetColor("_Color", Color.cyan.WithAlpha(num4 * num4 * num4));
        }
        __instance.transform.position = origPos + UnityEngine.Random.insideUnitCircle * 0.1f;
        __instance.transform.localScale = new Vector2(1f + Mathf.Sin(Time.time * 3f) * 0.25f + UnityEngine.Random.value * pulseSpeedAdd, 1f);
        return false;
    }

    // the prefab material is one asset. the host's distance was writing its alpha,
    // so a player standing on the heart still saw the host's invisible ring.
    static Material OwnMat(BlueHeart heart, Traverse tr, Material mat) {
        if (heart == null || mat == null || mat.name.Contains(" (Heart)")) {
            return mat;
        }
        Material owned = new Material(mat);
        owned.name = mat.name + " (Heart)";
        tr.Field<Material>("mat").Value = owned;
        // only the pulse ring uses this material. painting it onto every renderer
        // replaced the heart body, so the mesh was gone and the glow was all that remained.
        Renderer[] renderers = heart.GetComponentsInChildren<Renderer>(true);
        int swapped = 0;
        for (int i = 0; i < renderers.Length; i++) {
            Renderer renderer = renderers[i];
            if (renderer != null && renderer.sharedMaterial == mat) {
                renderer.sharedMaterial = owned;
                swapped++;
            }
        }
        Vector3 pos = heart.transform.position;
        FindTheBugReports.Say("pulse", "mat own side=" + (Net.IsServer ? "host" : "client")
            + " swapped=" + swapped
            + " renderers=" + renderers.Length
            + " pos=" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0"));
        return owned;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(BlueHeart.Cutscene))]
    static bool CutscenePrefix(BlueHeart __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = MpCutscene(__instance);
        return false;
    }

    static IEnumerator MpCutscene(BlueHeart heart) {
        // host already played obtain; clients play it on announce
        if (BlueHeartMp.ClientCutsceneFromHost) {
            Sound.Play("blueHeartObtain", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        }
        Sound.Play("blueheartsfx", heart.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);

        Traverse tr = Traverse.Create(heart);
        Vector2 oldPos = tr.Field<Vector2>("origPos").Value;
        Vector2 newPos = oldPos + Vector2.up * 7f;
        var light = heart.GetComponent<UnityEngine.Rendering.Universal.Light2D>();
        float timer = 0f;
        while (timer < 5f) {
            timer += Time.deltaTime;
            tr.Field<float>("pulseSpeedAdd").Value = tr.Field<float>("pulseSpeedAdd").Value + Time.deltaTime * 1.5f;
            tr.Field<Vector2>("origPos").Value = Vector2.Lerp(oldPos, newPos, Utils.InQuad(timer * 0.2f));
            if (light != null) {
                light.intensity = 3f + timer * 30f * Utils.InQuint(timer * 0.2f);
            }
            if (Observer.main != null) {
                Observer.main.distance = 50f - timer * 9f;
            }
            yield return null;
        }

        Vector2 heartPos = heart.transform.position;
        if (PlayerCamera.main != null) {
            PlayerCamera.main.StartCoroutine(PlayerCamera.main.CommenceBlinding());
        }
        UnityEngine.Object.Destroy(heart.gameObject);

        if (!WorldGeneration.GetRunSettingBool("endless")) {
            if (Net.IsServer) {
                Body speaker = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                BlueHeartMp.HostEmbedAffected(speaker, heartPos);
            }
            // clients: cargo+wounds arrive via medical sync (soft rising-edge). don't double EmbedCargo.
            SteamManager.TryUnlockAchievement("MILLIONSTOONE");
            if (MusicManager.main != null) {
                MusicManager.main.cutsceneVolMult = 0f;
            }
        } else if (Net.IsServer) {
            Sound.Play("observerlaugh", heart.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            if (MusicManager.main != null) {
                MusicManager.main.cutsceneVolMult = 1f;
                MusicManager.main.StopSong();
            }
            WorldGeneration.world.biomeDepth = 0;
            WorldGeneration.world.StartCoroutine(WorldGeneration.world.RegenerateWorld());
            // endless talk lines: only host local body (vanilla)
            Body body = PlayerCamera.main.body;
            switch (WorldGeneration.world.endlessLoops) {
                case 0:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop0"), null, force: false, resetTalkTimer: true);
                    body.eyePanicTime = 11f;
                    body.eyeScareTime = 20f;
                    break;
                case 1:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop1"), null, force: false, resetTalkTimer: true);
                    body.eyeCloseTime = 15f;
                    body.eyeScareTime = 20f;
                    SteamManager.TryUnlockAchievement("ROUNDANDROUNDWEGO");
                    break;
                case 2:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop2"), null, force: false, resetTalkTimer: true);
                    body.StartCoroutine(body.Cry());
                    break;
                case 3:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop3"), null, force: false, resetTalkTimer: true);
                    body.eyeScareTime = 12f;
                    break;
                default:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloopdefault"), null, force: false, resetTalkTimer: true);
                    break;
            }
            WorldGeneration.world.endlessLoops++;
        }

        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            if (kv.Key != null) {
                kv.Key.forceNoMovement = false;
            }
        }
        BlueHeartMp.ClientCutsceneFromHost = false;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BlueHeartCutscene)]
    static void Client_BlueHeartCutscene(knetid _, NetDataReader reader) {
        reader.Get(out bool start);
        if (!start || BlueHeart.main == null) {
            return;
        }
        if (Net.IsServer) {
            return;
        }
        BlueHeartMp.ClientCutsceneFromHost = true;
        Traverse tr = Traverse.Create(BlueHeart.main);
        if (tr.Field<bool>("cutsceneStarted").Value) {
            return;
        }
        tr.Field<bool>("cutsceneStarted").Value = true;
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local != null) {
            local.forceNoMovement = true;
        }
        if (PlayerCamera.main != null) {
            PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false, force: true);
        }
        BlueHeart.main.StartCoroutine(BlueHeart.main.Cutscene());
    }
}















#endif