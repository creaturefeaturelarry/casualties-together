#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// each peer keeps the scientist it generated, so the lines stay on that species.
// nobody starts until every player has spawned and cleared the arrival card.
// the first living body with x < 17 starts it for everyone.
// hp is one pool on the host. the shot still goes through the existing broadcast.
public static class ScientistGate {
    public static bool Go;
    public static BuildingEntity HealthBuilding;

    static readonly HashSet<knetid> Ready = new();
    static bool _sentReady;
    static bool _hostSelfReady;
    static bool _applyingHp;

    public static void Reset() {
        Go = false;
        _sentReady = false;
        _hostSelfReady = false;
        Ready.Clear();
    }

    public static void KeepLocal(EndLabScript lab) {
        if (lab == null) {
            return;
        }
        MarkChain(lab.scientist != null ? lab.scientist.gameObject : null, false);
        MarkChain(lab.scientistAnim != null ? lab.scientistAnim.gameObject : null, false);
        MarkChain(lab.scientistTalker != null ? lab.scientistTalker.gameObject : null, false);
        foreach (Transform t in lab.GetComponentsInChildren<Transform>(true)) {
            if (t != null && t.CompareTag("Scientist")) {
                MarkChain(t.gameObject, true);
            }
        }
    }

    static void MarkChain(GameObject go, bool claimHealth) {
        Transform t = go != null ? go.transform : null;
        while (t != null && t.GetComponent<EndLabScript>() == null) {
            if (t.GetComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>() == null) {
                t.gameObject.AddComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>();
            }
            BuildingEntity building = t.GetComponent<BuildingEntity>();
            if (building != null) {
                if (claimHealth || HealthBuilding == null) {
                    HealthBuilding = building;
                }
                return;
            }
            t = t.parent;
        }
    }

    public static void Tick() {
        if (!Net.IsRunning || Go || EndLabScript.instance == null) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
            return;
        }
        if (!_sentReady && LocalArrivalClear()) {
            _sentReady = true;
            if (Net.IsServer) {
                _hostSelfReady = true;
            } else {
                NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
                writer.Put("scientist_ready");
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            }
        }
        if (!Net.IsServer || !EveryoneReady()) {
            return;
        }
        if (!AnyoneInRoom()) {
            return;
        }
        Go = true;
        NetDataWriter go = Multiplayer.CreateNamedWriter("CustomCommand");
        go.Put("scientist_go");
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, go, ServerMain.AllClientIdsExceptHost);
    }

    public static void MarkReady(knetid id) {
        Ready.Add(id);
    }

    public static void ReportDamage(float delta) {
        if (delta <= 0.05f || HealthBuilding == null || _applyingHp || ConcurrentEndings.ScientistDead) {
            return;
        }
        if (Net.IsServer) {
            BroadcastHealth(HealthBuilding.health);
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("scientist_hp " + delta.ToString("R", CultureInfo.InvariantCulture));
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static void HostAcceptDamage(float delta) {
        if (!Net.IsServer || delta <= 0.05f || HealthBuilding == null) {
            return;
        }
        ApplyRaw(Mathf.Max(0f, HealthBuilding.health - delta));
        BroadcastHealth(HealthBuilding != null ? HealthBuilding.health : 0f);
    }

    public static void ApplyRemoteHealth(float value) {
        if (HealthBuilding == null) {
            return;
        }
        ApplyRaw(value);
    }

    static void BroadcastHealth(float value) {
        if (!Net.IsServer) {
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("scientist_hp");
        writer.Put(value);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
    }

    static void ApplyRaw(float value) {
        if (HealthBuilding == null) {
            return;
        }
        _applyingHp = true;
        try {
            HealthBuilding.health = Mathf.Max(0f, value);
        } finally {
            _applyingHp = false;
        }
    }

    static bool LocalArrivalClear() {
        WorldGeneration world = WorldGeneration.world;
        PlayerCamera cam = PlayerCamera.main;
        if (world == null || cam == null || cam.body == null) {
            return false;
        }
        if (world.generatingWorld || world.biomeDepth != 11 || !world.worldExists) {
            return false;
        }
        if (ScrollableText.textExists || SharedMain.IsInCutscene || world.cutsceneActive) {
            return false;
        }
        Talker talker = cam.body.talker;
        if (talker != null && talker.text != null && !string.IsNullOrEmpty(talker.text.text) && !talker.FinishedTalking()) {
            return false;
        }
        try {
            float alert = Traverse.Create(cam).Field("alertTime").GetValue<float>();
            if (alert > 0f) {
                return false;
            }
        } catch (Exception) {
        }
        return true;
    }

    static bool EveryoneReady() {
        if (PlayerCamera.main != null && PlayerCamera.main.body != null && !_hostSelfReady) {
            return false;
        }
        foreach (ScavPlayer player in ScavPlayer.AllPlayers) {
            if (player == null || player.IsLocal) {
                continue;
            }
            if (!Ready.Contains(player.playerId)) {
                return false;
            }
        }
        return true;
    }

    static bool AnyoneInRoom() {
        foreach (ScavPlayer player in ScavPlayer.AllPlayers) {
            if (player == null || player.body == null || !player.body.alive) {
                continue;
            }
            if (player.body.transform.position.x < 17f) {
                return true;
            }
        }
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        return local != null && local.alive && local.transform.position.x < 17f;
    }

    // vanilla yields twice before the scientist speaks: once for worldgen, once for x < 17.
    // the lab theme is played on the second step. run that on this machine as soon as the
    // local world exists. dialogue still waits for scientist_go.
    public static IEnumerator Drive(EndLabScript lab) {
        IEnumerator inner = EndLabScript_ScientistCutscene_Gate.Original(lab);
        if (!inner.MoveNext()) {
            yield break;
        }
        yield return inner.Current;
        if (lab == null) {
            yield break;
        }
        if (!inner.MoveNext()) {
            yield break;
        }
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = 1f;
        }
        yield return new WaitUntil(() => Go || lab == null);
        if (lab == null) {
            yield break;
        }
        while (inner.MoveNext()) {
            yield return inner.Current;
        }
    }

    public static IEnumerator DeferredStart(EndLabScript lab) {
        yield return new WaitUntil(() => PlayerCamera.main != null && PlayerCamera.main.body != null && WorldGeneration.world != null);
        if (lab == null) {
            yield break;
        }
        KeepLocal(lab);
        PlayerCamera.main.blockItemUsage = true;
        PlayerCamera.main.body.forceWalk = true;
        lab.scientistCoroutine = lab.StartCoroutine(lab.ScientistCutscene());
        LampScript[] lamps = UnityEngine.Object.FindObjectsByType<LampScript>(FindObjectsSortMode.None);
        foreach (LampScript lamp in lamps) {
            lab.lights.Add(lamp);
        }
        GuardScript.anyGuardKilled = false;
        if (lab.injector != null) {
            lab.injector.DrainAll();
            lab.injector.AddLiquid("localanesthetic", 347f);
        }
        if (lab.surgicalDevice != null && lab.surgicalDevice.GetComponent<SurgicalDeviceBehavior>() == null) {
            lab.surgicalDevice.gameObject.AddComponent<SurgicalDeviceBehavior>();
        }
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict["scientist_ready"] = (plr, cmd, args) => {
            if (plr != null) {
                MarkReady(plr.playerId);
            }
        };
        ServerMain.ServerCustomCommandsDict["scientist_hp"] = (plr, cmd, args) => {
            if (args == null || args.Length < 2) {
                return;
            }
            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float delta)) {
                return;
            }
            HostAcceptDamage(delta);
        };
        ClientMain.ClientCustomCommandsDict["scientist_go"] = (cmd, reader) => {
            Go = true;
        };
        ClientMain.ClientCustomCommandsDict["scientist_hp"] = (cmd, reader) => {
            ApplyRemoteHealth(reader.GetFloat());
        };
    }
}

[HarmonyPatch(typeof(EndLabScript), "Awake")]
static class EndLabScript_Awake_KeepScientist_MultiplayerPatch {
    static void Postfix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        ScientistGate.KeepLocal(__instance);
    }
}

[HarmonyPatch(typeof(EndLabScript), "Start")]
static class EndLabScript_Start_KeepScientist_MultiplayerPatch {
    static bool Prefix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        ScientistGate.KeepLocal(__instance);
        if (PlayerCamera.main == null || PlayerCamera.main.body == null) {
            __instance.StartCoroutine(ScientistGate.DeferredStart(__instance));
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(EndLabScript), "ScientistCutscene")]
static class EndLabScript_ScientistCutscene_Gate {
    [HarmonyReversePatch]
    public static IEnumerator Original(EndLabScript instance) {
        throw new NotImplementedException();
    }

    static bool Prefix(EndLabScript __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = ScientistGate.Drive(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(EndLabScript), "Update")]
static class EndLabScript_Update_ScientistGate_MultiplayerPatch {
    static void Postfix() {
        ScientistGate.Tick();
    }
}

[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.health), MethodType.Setter)]
static class BuildingEntity_Health_ScientistShare_MultiplayerPatch {
    static void Prefix(BuildingEntity __instance, ref float value, ref float __state) {
        if (Net.IsRunning && __instance == ScientistGate.HealthBuilding && value < 0f) {
            value = 0f;
        }
        __state = (!Net.IsRunning || __instance != ScientistGate.HealthBuilding) ? float.NaN : __instance.health;
    }

    static void Postfix(BuildingEntity __instance, float __state) {
        if (float.IsNaN(__state)) {
            return;
        }
        ScientistGate.ReportDamage(__state - __instance.health);
    }
}
#endif