using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace Together;




[HarmonyPatch(typeof(Item), nameof(Item.OnCollisionEnter2D))]
public static class Item_OnCollisionEnter2D_MultiplayerPatch {
    public static bool Prefix(Item __instance, Collision2D collision) {
        if (collision.collider.TryGetComponent(out Item oitem)) { // item against item dont make the noise
            if (collision.relativeVelocity.magnitude < 20f) {
                return false;
            }
        }
        return true;
    }

}








[HarmonyPatch(typeof(GeigerCounterAudio), nameof(GeigerCounterAudio.Update))]
internal static class GeigerCounterAudio_Update_MultiplayerPatch {
    internal static bool Prefix(GeigerCounterAudio __instance) {
        if (Net.IsRunning) {
            if (!__instance.item.battery.hasCharge) {
                __instance.active = false;
            }
            if (!__instance.active) {
                __instance.audioSource.Stop();
                return false;
            }
            if (!__instance.audioSource.isPlaying) {
                __instance.audioSource.Play();
            }
            float irradiateIntensity = 0;

            var overider = __instance.GetOrAddComponent<BodyGetterOverrider>();
            if (overider.TryGetBodyFromParent(out var body) && body.TryGetNetBody(out var nb)) {
                irradiateIntensity = nb.irradiateIntensity;
            }

            int clipIndex = __instance.GetClipIndex(irradiateIntensity);
            if (__instance.audioSource.clip != __instance.radiationClips[clipIndex]) {
                __instance.audioSource.clip = __instance.radiationClips[clipIndex];
                __instance.audioSource.Play();
            }

            return false;
        }

        return true;
    }
}









[HarmonyPatch(typeof(AutoPump), nameof(AutoPump.Update))]
static class AutoPump_Update_MultiplayerPatch {

    static void AutoPump_Update_Rewritten(AutoPump ap) {

        ap.coolDown -= Time.deltaTime;
        ap.wearCheckTime -= Time.deltaTime;

        if (ap.transform.transform.parent == null) {
            ap.worn = false;
            return;
        }
        var bgo = ap.GetOrAddComponent<BodyGetterOverrider>();
        var body = bgo.GetBody();

        if (ap.wearCheckTime < 0f) {
            ap.wearCheckTime = 1f;
            ap.worn = bgo.TryGetBodyFromParent(out body) && body.HasWearable(ap.item);
        }

        if (ap.worn && ap.item.battery.hasCharge) {
            ap.item.battery.DrainCharge(Time.deltaTime / 1200f);
            if (body.bloodPressure < 85f) {
                body.bloodPressure += 44f;
                ap.item.battery.DrainCharge(0.002f);
                Sound.Play("autopump", ap.item.transform.position, body.IsBodyLocal(), pitchShift: true, null, 0.6f);
            }
        }


    }


    static bool Prefix(AutoPump __instance) {
        if (!Net.IsRunning) return true;

        AutoPump_Update_Rewritten(__instance);

        return false;
    }

}




[HarmonyPatch(typeof(WatchScript), nameof(WatchScript.Update))]
static class WatchScriptUpdate_MultiplayerPatch {
    static bool Prefix(WatchScript __instance) {
        if (!Net.IsRunning) return true;
        var bovr = __instance.GetOrAddComponent<BodyGetterOverrider>();
        var b = bovr.GetBody();
        if (b != null) {
            if (!b.IsBodyLocal()) {
                return false; // make the watch not work on remote peers 
            }
        }
        return true;
    }
}


[HarmonyPatch(typeof(GrapplingHook), nameof(GrapplingHook.Use))]
internal static class GrapplingHook_Use_MultiplayerPatch {
    static void Prefix(GrapplingHook __instance, Body body, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main.shaker);
    }
    static void Postfix(GrapplingHook __instance, Body body, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        if (!body.IsBodyLocal()) { // reset the camera shake
            __state.Apply(PlayerCamera.main.shaker);
        }
        // the hook is its own rigidbody. the gun staying in hand does not keep the chunk the hook flies into.
        if (__instance.hook != null)
            SimKeep.Arm(__instance.hook.gameObject);
    }
}






// rewritten
[HarmonyPatch(typeof(Item), nameof(Item.DoAmputate))]
public static class Item_DoAmputate_MultiplayerPatch {
    public static readonly HashSet<string> ItemsThatCanAmputate = new();

    static bool Prefix(Item item, Limb limb) {
        ItemsThatCanAmputate.Add(item.id);

        if (!Net.IsRunning) return true;

        if (PlayerCamera_ApplyWoundItem_MultiplayerPatch.last_wounditem_user != null && PlayerCamera_ApplyWoundItem_MultiplayerPatch.last_wounditem_user.IsBodyLocal()) {

            // allow cannibalism
            if (limb.body != PlayerCamera_ApplyWoundItem_MultiplayerPatch.last_wounditem_user) {
                if (limb.CanAmputate()) {
                    //OG: Item.DoAmputate V5t5
                    MinigameBase.main.StartMinigame(new AmputationMinigame(limb), item);
                    return false;
                }
            }

            // do the default amputation checks
            return true;
        }


        return false;
    }
}










[HarmonyPatch(typeof(FreshItemDrop), nameof(FreshItemDrop.Start))]
static class FreshItemDrop_Start_MultiplayerPatch {

    //OG: BuildingEntity.Update() V5t4  where it spawns the item drops after becoming condition 0
    const float max_distance_to_keep = 8f;

    static void Postfix(FreshItemDrop __instance) {
        if (Net.IsRunning) {
            var np = ScavPlayer.GetDistanceToNearestLivingPlayer(__instance.transform.position);
            if (np.Item2 > max_distance_to_keep * max_distance_to_keep) {
                UnityEngine.Object.Destroy(__instance);
            }
        } else {
            if (!KM.DistanceCheck(__instance.transform.position, PlayerCamera.main.body.transform.position, max_distance_to_keep)) {
                UnityEngine.Object.Destroy(__instance);
            }
        }
    }

}


[HarmonyPatch(typeof(FreshItemDrop), nameof(FreshItemDrop.OnDestroy))]
static class FreshItemDrop_OnDestroy_ErrorfixPatch {
    static bool Prefix(FreshItemDrop __instance) {

        __instance.rb.gravityScale = 1;

        if (__instance.part != null) {
            Component.Destroy(__instance.part.gameObject);
        }
        if (__instance.outline != null) { // cuz this part causes unecessary errors
            Component.Destroy(__instance.outline.gameObject);
        }

        return false;
    }
}









