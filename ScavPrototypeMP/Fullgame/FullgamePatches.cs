using CasualtiesTogetherUtils;
using DiscordRPC;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib;
using System;
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

    void Start() {
#if RELEASE
        if (Util.GameIsDemo()) {
            return;
        }
#endif

        Traverse.Create(Constants.DefaultMineExplosionParams).Field("dismemberChance").SetValue(0.15f);
    }
}










#if FULLGAME



[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.SelectCharacter))]
static class PreRunScript_SelectCharacter_MultiplayerPatch {
    static void Postfix(PreRunScript __instance) {
        if (!Net.IsRunning) return;

        //TODO sb.characterSelected is never sent to the server yet
        //ClientMain.Client_SendCharacterSelected(PreRunScript.characterSelected);
    }
}






[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.CheckForCheats))]
static class ConsoleScript_CheckForCheats_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) return true;

        return false;
    }
}




[HarmonyPatch(typeof(CustomItemBehaviour), nameof(CustomItemBehaviour.CryoActivate))]
public static class CustomItemBehaviour_CryoActivate_MultiplayerPatch {
    static float last_zoomtime = 0;

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);

        return codes;
    }
    static void Prefix(CustomItemBehaviour __instance) {
        if (!Net.IsRunning) return;
        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody(); // Pick nearest body now
    }
}



[HarmonyPatch]
static class GuardSpawner_MultiplayerPatch {

    class TrackerGuardSpawner : BaseComponentTracker<GuardSpawner> {
        protected override void TrackerAwake() {
            
        }

        void Update() {
            if ((bool)EndLabScript.instance && EndLabScript.instance.doingEscape) {
                og.openProgress += Time.deltaTime;
            }

            if (Net.IsClient) return;

            var (near,b) = NetBody.GetNearestBody(og.transform.position, true);
            float num = Mathf.Sqrt(b);
            if (!(num < og.minDistanceToSpawn) && !(num > og.maxDistanceToSpawn) && og.guardsToSpawn > 0 && !(og.openProgress < og.minTimeBetweenSpawn)) {
                og.CreateGuard();
            }
        }
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.Start))]
    static bool StartPrefix(GuardSpawner __instance) {
        if (!Net.IsRunning) return true;

        __instance.GetOrAddComponent<TrackerGuardSpawner>();

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.Update))]
    static bool UpdatePrefix() {
        if (!Net.IsRunning) return true;
        


        return false;
    }
}










[HarmonyPatch]
static class CargoDrillPod_MultiplayerPatch {

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.OnUse))]
    static bool StartPrefix(CargoDrillPod __instance) {
        if (!Net.IsRunning) return true;

        var writer = Net.CreateWriter(NetmsgId.___19);
        writer.Put(1);
        writer.Put(AnyObjectNetId.GetNetIdFromGameObject(__instance.gameObject));
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.Update))]
    static bool UpdatePrefix(CargoDrillPod __instance) {
        if (!Net.IsRunning) return true;

        if (ScavPlayer.AllLivingPlayers.Any(x => x.body.hasCargo) && !__instance.didReady) {
            __instance.GetComponent<UsableObject>().toggleString = Locale.GetOther("finaltravel");
            __instance.bld.description = Locale.GetBuilding("highpowereddrillpoddsc2");
            Utils.Create("Special/Tesla", __instance.transform);
            __instance.didReady = true;
        }

        return false;
    }


    static void RealOnUse(Body b, CargoDrillPod drill) {
        if (!drill.didReady) {
            b.talker.Talk(Locale.GetCharacter("endpodexamine"));
        } else if (!drill.started) {
            drill.started = true;
            WorldGeneration.world.StartCoroutine("RegenerateWorld", false);
            Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
        }
    }


    [ServerReceiver((byte)NetmsgId.___19)]
    static void Server_aaaaaaaaaaaaaaaaaaa(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out AnyObjectNetId aoni);

        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var user, out var body)
            && aoni.TryGetGameObject(out var go)
        ) {
            if (go.TryGetComponent(out CargoDrillPod drill)) {
                RealOnUse(body, drill);
            }
        }
    }

}














#endif







