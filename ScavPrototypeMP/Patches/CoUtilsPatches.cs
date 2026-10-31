using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;
using KrokV5Optimization;
using System.Diagnostics;

namespace Together;




[HarmonyPatch(typeof(Liquids), nameof(Liquids.HighGradeStimulantStep))]
static class Liquids_HighGradeStimulantStep_MultiplayerPatch {
    static void Prefix(Limb limb) {
        if (!Net.IsRunning) return;
        limb.GetCoUtilsInstance().SelectForMPOverride();
    }
}
[HarmonyPatch(typeof(Liquids), nameof(Liquids.LowGradeStimulantStep))]
static class Liquids_LowGradeStimulantStep_MultiplayerPatch {
    static void Prefix(Limb limb) {
        if (!Net.IsRunning) return;
        limb.GetCoUtilsInstance().SelectForMPOverride();
    }
}

[HarmonyPatch(typeof(CoUtils), nameof(CoUtils.instance), MethodType.Getter)]
public static class CoUtils_instance_MultiplayerPatch {
    public static CoUtils original_local_instance = null;//TODO private and use GetLocalCoUtilsInstance instead
    public static CoUtils cur_override_instance = null;//TODO private and use SelectForMPOverride instead
    
    static bool Prefix(ref CoUtils __result) {
        if (!Net.IsRunning) return true;

        if (cur_override_instance != null) {
            __result = cur_override_instance;
        } else {
            __result = CoUtilsMultiplayer.GetLocalCoUtilsInstance();
        }

        return false;
    }
}


[HarmonyPatch(typeof(CoUtils), nameof(CoUtils.DoTimedOp))]
static class CoUtils_DoTimedOp_MultiplayerPatch {
    static bool Prefix(CoUtils __instance, ref string id, ref Action operation, ref float duration) {
#if DEBUG
        if (log.verbose) log.devevent($"CoUtils.DoTimedOp called (id={id},duration={duration}) {__instance.GetPlayer().ToSafeString()} {new StackTrace(1,true)}", __instance.GetPlayer()?.pos ?? Vector2.zero);
#endif
        if (!Net.IsRunning) return true;

        var originalOp = operation;

        operation = () => {
            __instance.SelectForMPOverride();// To fix midgradestimulant and others that get the instance while running inside CoUtils
            originalOp();
        };

        return true;
    }
}


public static class CoUtilsMultiplayer {

    public static CoUtils GetLocalCoUtilsInstance() {
        if (CoUtils._inst == null) {
            //OG: CoUtils.instance getter V7.0.1
            GameObject obj = new GameObject("CoUtilsInstance");
            CoUtils_instance_MultiplayerPatch.original_local_instance = obj.AddComponent<CoUtils>();
            UnityEngine.Object.DontDestroyOnLoad(obj);
            CoUtils._inst = CoUtils_instance_MultiplayerPatch.original_local_instance;
        }
        if (CoUtils_instance_MultiplayerPatch.original_local_instance == null)
            CoUtils_instance_MultiplayerPatch.original_local_instance = CoUtils._inst;

        return CoUtils._inst;
    }

    public static ScavPlayer GetPlayer(this CoUtils cu) {
        if (CoUtils._inst != cu) {
            if (cu.TryGetComponent(out Body b) && b.TryGetNetPlayer(out var p)) {
                return p;
            }
            if (cu.transform.parent != null && cu.transform.parent.TryGetComponent(out ScavPlayer plr)) {
                return plr;
            }
        }
        return ScavPlayer.LocalPlayer;
    }
    public static void SelectForMPOverride(this CoUtils cu) {
        if (cu == null) {
#if DEBUG
            throw new ArgumentNullException(nameof(cu));
#endif
            CoUtils_instance_MultiplayerPatch.cur_override_instance = null;
            return;
        }
        CoUtils_instance_MultiplayerPatch.cur_override_instance = cu;
    }
    public static void DeselectMPOverride() {
        //log.l($"Deselect {new StackTrace()}");
        CoUtils_instance_MultiplayerPatch.cur_override_instance = null;
    }

    public static CoUtils GetCoUtilsInstance(this Limb limb) {
        return limb.body.GetCoUtilsInstance();
    }
    public static CoUtils GetCoUtilsInstance(this Body body) {
        if (body != null)
            return body.GetOrAddComponent<CoUtils>();
        return GetLocalCoUtilsInstance();
    }
    public static CoUtils GetCoUtilsInstance(this NetBody nb) {
        if (nb != null && nb.body != null)
            return nb.body.GetCoUtilsInstance();
        return GetLocalCoUtilsInstance();
    }
    public static CoUtils GetCoUtilsInstance(this ScavPlayer plr) {
        if (plr != null) {
            if (plr.body != null)
                return plr.body.GetCoUtilsInstance();
            if (plr.CoUtilsInstance != null)
                return plr.CoUtilsInstance;
        }
        return GetLocalCoUtilsInstance();
    }

}






