using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;
using UnityEngine.UI;

// the Harmony patches that apply a skin when the game builds a limb, face, tail or wound view.
[HarmonyPatch(typeof(Limb), "Awake")]
public class LimbAwakePatch {
    static void Postfix(Limb __instance) {
        // never solo-stamp LoadedSprites while a session is up - remotes would get the local slot.
        if (MpBody.IsActive || Net.IsRunning) {
            if (__instance != null && MpBody.IsActive) {
                Body body = __instance.GetComponentInParent<Body>();
                if (body == null) {
                    SkinTrace.Problem("limb-rebuilt-nobody", __instance.name);
                } else {
                    MpSkinSync.OnLimbRebuilt(body);
                }
            }
            return;
        }

        SpriteReplacerPlugin.ApplyReplacement(__instance);
    }
}

[HarmonyPatch(typeof(FacialExpression), "Start")]
public class FacialExpressionStartPatch {
    static void Postfix(FacialExpression __instance) {
        if (MpBody.IsActive) {
            // head/face finishes after torso - kick pending remote (or local) skin apply now.
            MpSkinSync.OnBodyPartReady(__instance != null ? __instance.body : null);
            return;
        }

        if (Net.IsRunning) {
            // mpBody bridge not ready yet - do not paint local slot onto joiners.
            return;
        }

        SkinFace.ApplyReplacementToFace(__instance);
    }
}

[HarmonyPatch(typeof(TailScript), "Start")]
public class TailScriptStartPatch {
    static void Postfix(TailScript __instance) {
        if (MpBody.IsActive || Net.IsRunning) {
            return;
        }

        SpriteReplacerPlugin.ApplyReplacementToTail(__instance);
    }
}

[HarmonyPatch(typeof(Settings), "ApplyAll")]
public class SettingsApplyAllPatch {
    // the game writes settings after us. put the slot back first so a stale value cannot win.
    static void Prefix() {
        CoreLibIntegration.StoreActiveSlot(SpriteReplacerPlugin.ActiveSlot);
        CoreLibIntegration.StoreBackupCount(SpriteReplacerPlugin.BackupCount);
    }
}

[HarmonyPatch(typeof(WoundView), "Awake")]
public class WoundViewAwakePatch {
    static void Postfix(WoundView __instance) {
        WoundViewIntegration.Note(__instance);
        WoundViewIntegration.Apply(__instance);
    }
}

[HarmonyPatch(typeof(WoundView), "Start")]
public class WoundViewStartPatch {
    static void Postfix(WoundView __instance) {
        WoundViewIntegration.Note(__instance);
        if (__instance == null || __instance.limbImages == null || __instance.limbImages.Length == 0) {
            WoundViewIntegration.Apply(__instance);
            return;
        }

        Image head = __instance.limbImages[0];
        WoundViewIntegration.RememberStock(head);
        if (head != null && head.transform.childCount > 0) {
            WoundViewIntegration.RememberStock(head.transform.GetChild(0).GetComponent<Image>());
        }

        WoundViewIntegration.Apply(__instance);
    }
}

[HarmonyPatch(typeof(WoundView), "UpdateView")]
public class WoundViewUpdatePatch {
    static void Postfix(WoundView __instance) {
        WoundViewIntegration.ShowCustomColors(__instance);
    }
}

[HarmonyPatch(typeof(PlayerCamera), "Awake")]
public class PlayerCameraAwakePatch {
    static void Postfix() {
        WoundViewIntegration.ApplyAll();
    }
}
