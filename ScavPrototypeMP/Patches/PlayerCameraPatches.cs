using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Steamworks;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Random = UnityEngine.Random;
using System.Diagnostics;

namespace Together;



#region TOOLTIP_OVERRIDES


//override player description
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandlePlayerHover))]
public static class PlayerCamera_HandlePlayerHover_MultiplayerPatch {

    public static bool is_hovering_over_another_player = false;

    public static void Postfix(PlayerCamera __instance) {

        if (Net.IsRunning) {
            var pc = __instance;
            var body = pc.body;

            is_hovering_over_another_player = false;

            // OG: PlayerCamera.HandleWorldHover()
            Component collider2D = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Limb"))?.GetComponent<Limb>()?.body;
            if (!collider2D) {
                collider2D = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Body"));
            }
            if (collider2D) {
                var npc = (collider2D.GetComponent<NetBody>());

                if (npc != null) {
                    bool is_pb = npc.IsPlayer;

                    if (!Util.IsBodyLocal(npc.body)) {

                        var mouseOverName_text = "";
                        var mouseOverDescription_text = "";

                        is_hovering_over_another_player = true;

                        npc.DoMouseHoverTooltip(ref mouseOverName_text, ref mouseOverDescription_text);

                        GlobalDark.main.SetTooltip((mouseOverName_text, mouseOverDescription_text));

                    }
                }
            }


        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ItemHoverDescription))]
static class PlayerCamera_ItemHoverDescription_MultiplayerPatch {
    public static void Postfix(Item item, ref (string, string) __result) {

        ClientMain.DoVerboseObjInfo(item, ref __result.Item1, ref __result.Item2);

    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.DoAlert))]
static class PlayerCamera_DoAlert_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance) {
        if (__instance != null && __instance.alertText != null)
            __instance.alertText.richText = true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleItemHover))]
static class PlayerCamera_HandleItemHover_MultiplayerPatch {
    public static Component last_focused_thing = null;
    internal static void SetDifferentThingamabob(Component item) {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            if (last_focused_thing != item) {
                // it will automatically check if its registered and shit
                if (NetObjectRegistry.ObjectShouldBeSynced(item.gameObject) && NetObjectRegistry.TryGetSyncInfoOrRegister(item.gameObject, out var si)) {
                    // making sure that the object is registered, or gone for the client 
                }
            }
            last_focused_thing = item;
        }
    }
    static void Postfix(PlayerCamera __instance, Item item) {
        SetDifferentThingamabob(item);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleBuildingHover))]
static class PlayerCamera_HandleBuildingHover_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance, BuildingEntity build) {
        PlayerCamera_HandleItemHover_MultiplayerPatch.SetDifferentThingamabob(build);

        ClientMain.DoVerboseObjInfo(build, ref GlobalDark.main.tooltipText.name, ref GlobalDark.main.tooltipText.desc);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleBlockHover))]
static class PlayerCamera_HandleBlockHover_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance, Collider2D hitcol) {
        if (log.verbose) {
            var worldpoint = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            var blockpos = WorldGeneration.world.WorldToBlockPos(worldpoint);
            var blockid = WorldGeneration.world.GetBlock(blockpos);
            BlockInfo blockInfo = WorldGeneration.world.GetBlockInfo(blockid);
            var fluidid = FluidManager.main.GetLiquid(blockpos.x, blockpos.y);

            GlobalDark.main.tooltipText.desc = $"{GlobalDark.main.tooltipText.desc}\nBLOCKPOS: {blockpos}\nBLOCK ID: {blockid}\nSLEEPQUAL: {blockInfo.sleep}";
            if (fluidid != 0) {
                GlobalDark.main.tooltipText.desc += $"\nFLUID ID: {fluidid}";
            }

        }
    }
}

[HarmonyPatch(typeof(GlobalDark), nameof(GlobalDark.Awake))]
internal static class GlobalDark_Awake_MultiplayerPatch {
    internal static void SetBetaBuildMPText() {
        if (GlobalDark.main != null && Plugin.SUCCESFULLY_INITIALIZED) {
            var betaBuild = GlobalDark.main.betaBuild;
            if (betaBuild != null) {
                //betaBuild.text = "This is a beta build (" + Application.version + "); Things are subject to change.";
                //betaBuild.text += " (Demo)";
                betaBuild.text = "This is a beta build (" + Application.version + "); ";
                betaBuild.text += $" (Modified) (MP V{Plugin.FULL_MOD_VERSION})";
            }
        }
    }

    static void Postfix(GlobalDark __instance) {
        SetBetaBuildMPText();
    }
}

#endregion // TOOLTIP_OVERRIDES








// stock LateUpdate nulls currentTrader after the menu check has already passed.
// put the opened trader back before the next reader, including the next Update's close.
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.LateUpdate))]
public static class PlayerCamera_LateUpdate_TraderHold {
    private static void Postfix(PlayerCamera __instance) {
        TraderPinLog.Hold(__instance);
    }
}

//override some UI
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.Update))]
public static class PlayerCamera_Update_MultiplayerPatch {
    static bool prev_radialOpen = false;

    static void Prefix(PlayerCamera __instance) {
        if (Net.IsRunning) {
            TraderPinLog.Hold(__instance);
            prev_radialOpen = __instance.radialOpen;
            TraderPinLog.Sample("update");
        }
    }
    static void Postfix(PlayerCamera __instance) {

        // panel still up on a body that disconnected, or the hud got left off after a bad close.
        if (__instance.mainView != null && !__instance.mainView.activeSelf) {
            bool panel = __instance.woundView != null && __instance.woundView.activeSelf;
            if (!panel || PlayerCamera_ToggleWoundView_MultiplayerPatch.HealthTargetGone())
                PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(__instance);
        } else if (UIInGame.interaction_menu_target_body != null && UIInGame.interaction_menu_target_body.syncBody == null) {
            UIInGame.StopPlayerInteractionMenu();
        }

        // shutdown the tooltip
        if (Con._DEV_HIDEHUD) {
            GlobalDark.main?.SetTooltip(("", ""));
            __instance.hoverSquare.enabled = false;
        }


        if (Net.IsRunning) {
            var pc = __instance;
            var body = pc.body;

            if (!prev_radialOpen && pc.radialOpen) {

                UIInGame.StopPlayerInteractionMenu();

            } else {

                if (Input.GetKeyDown(KeyBinds.GetBind("iteminteract"))) {

                    // close the menu by clicking anywhere
                    if (UIInGame.interaction_menu_target_body != null && !UIInGame.interaction_menu_focused && !PlayerCamera_HandlePlayerHover_MultiplayerPatch.is_hovering_over_another_player) {
                        UIInGame.StopPlayerInteractionMenu();
                    }
                }

                if (pc.radialOpen) {
                    if (Util.IsInWoundView()) {
                        pc.radialOpen = false;
                    }
                } else {
                    InvButton_get_body_MultiplayerPatch.focused_body = null;
                }



                try {
                    var retur = __instance.deathStats.Find("Return"); // TODO make it make a menu to choose if to disconnect/ go to main menu/ cancel
                    if (retur != null) {
                        retur.gameObject.SetActive(Net.IsServer);
                        //retur.GetComponent<TextMeshProUGUI>().text = Lang.Get("endscreencalories");
                    }
                } catch (Exception e) {
#if !RELEASE
                    log.error(e);
#endif
                }
            }

        }

        // Keep the vanilla body-based reading for living players. Once the
        // local player is dead, make the reading follow the spectator camera.
        UpdateSpectatorDepthMeter(__instance);
    }

    // by @VtLoader
    // The vanilla depth meter reads PlayerCamera.body, which is correct while
    // alive but stale after death when the spectator/free camera moves away.
    static void UpdateSpectatorDepthMeter(PlayerCamera camera) {
        if (!Net.IsRunning || camera == null || camera.body == null || camera.body.alive
            || (!UIInGame.SpectatorModeActive && !Con._DEV_FREECAM)) {
            return;
        }

        var world = WorldGeneration.world;
        var depthLine = camera.depthLine;
        if (world == null || depthLine == null || depthLine.parent == null || world.height <= 0) {
            return;
        }

        var depthUnits = (float)world.halfHeight - camera.transform.position.y;
        var meterHeight = depthLine.parent.GetComponent<RectTransform>().rect.size.y;
        depthLine.anchoredPosition = new Vector2(
            0f,
            -depthUnits / world.height * meterHeight
        );

        var depthLabel = depthLine.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        if (depthLabel != null) {
            var totalDepthMeters = depthUnits * 0.3f + world.totalTraveled;
            depthLabel.text = $"{Mathf.RoundToInt(totalDepthMeters)}m";
        }
    }


}

//override screen shaders for spectator mode
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleScreenShaders))]
public static class PlayerCamera_HandleScreenShaders_MultiplayerPatch {
    public static bool forceDefaultShaderForNextFrame = false;

    public static bool CURRENTLY_OVERRIDING_VISUALS = false;

    public static bool ShouldOverrideVisualsToDefault() {
        return UIInGame.SpectatorModeActive || forceDefaultShaderForNextFrame || Con._DEV_FREECAM || Con._DEV_NO_VISION_EFFECT
                ;
    }

    static void Postfix(PlayerCamera __instance) {
        if (Net.IsRunning) {
            var pc = __instance;
            var body = pc.body;

            CURRENTLY_OVERRIDING_VISUALS = ShouldOverrideVisualsToDefault();
            if (
                CURRENTLY_OVERRIDING_VISUALS
            ) {
                forceDefaultShaderForNextFrame = false;


                __instance.blackAmount = 0;


                __instance.consciousnessFade.color = new Color(0f, 0f, 0f, 0f);
                __instance.consciousnessVignette.color = new Color(0f, 0f, 0f, 0f);
                __instance.consciousnessECG.active = false;

                __instance.mainScreenPass.SetFloat("_Pain", 0f);
                __instance.mainScreenPass.SetFloat("_LowOxyAmt", 0f);
                __instance.mainScreenPass.SetFloat("_BloodAmount", 1f);
                __instance.mainScreenPass.SetFloat("_Stamina", 1f);
                __instance.mainScreenPass.SetFloat("_Consciousness", 1f); // only this one is used
                __instance.mainScreenPass.SetFloat("_Consiousness", 1f); // lol why is there 2 ?
                __instance.mainScreenPass.SetFloat("_FrostAmount", 0f);
                __instance.mainScreenPass.SetFloat("_OverheatAmount", 0f);
                __instance.mainScreenPass.SetFloat("_BadFocusAmount", 0f);
                __instance.mainScreenPass.SetFloat("_InsanityFX", 0f);
                __instance.mainScreenPass.SetFloat("_BrightMult", 1f);
                __instance.mainScreenPass.SetVector("_PlayerPos", PlayerCamera.main.transform.position);
                __instance.mainScreenPass.SetFloat("_Dirtyness", 0f); // apparently default in the .asset is 0.032681547
                __instance.mainScreenPass.SetFloat("_SaturationMult", 1f);


                __instance.blurPass.SetFloat("_BlurIntensity", 0f);
                __instance.blurPass.SetFloat("_DropletAmount", 0f);
                __instance.blurPass.SetFloat("_HungerIntensity", 0f);
                __instance.blurPass.SetFloat("_SicknessAmount", 0f);
                __instance.blurPass.SetFloat("_Insanity", 0f);
                __instance.blurPass.SetFloat("_WaterAmount", 0f);

                __instance.bonusPass.SetFloat("_Intensity", 0f);
                __instance.bonusPass.SetFloat("_Distort", 0f);
                __instance.bonusPass.SetFloat("_FlipScreen", 0f);

                __instance.damagePass.SetInt("_EyeDamage", 0);
                __instance.damagePass.SetFloat("_BrainDamage", 0f);
                __instance.damagePass.SetFloat("_Bloodloss", 0f);
                __instance.damagePass.SetVector("_Pos", PlayerCamera.main.transform.position);
                __instance.damagePass.SetFloat("_DirectionMult", 0f); // but 1 is default
                __instance.damagePass.SetFloat("_RadAmount", 0f);



                WorldGeneration.world.fogMat.SetVector("_PlayerPos", PlayerCamera.main.transform.position);

                if (!body.alive) {
                    PlayerCamera.main.endScreen.gameObject.SetActive(false);
                }

                // reset the sound cutoff to default
                WorldGeneration.world.soundMixerGroup.audioMixer.SetFloat("SoundCutoff", 22000f);// default is at 22000
                __instance.underWaterSource.volume = 0;


            } else {
                if (!Util.IsUnchipped() || PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.forceSetIrradiateIntensity) {
                    PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.forceSetIrradiateIntensity = false;
                    if (ScavPlayer.TryGetLocalNetBody(out var nb)) {
                        __instance.damagePass.SetFloat("_RadAmount", Mathf.Clamp01(nb.irradiateIntensity));
                        PlayerCamera.main.irradiateIntensity = nb.irradiateIntensity;
                        PlayerCamera.main.timeSinceRadUpdate = nb.timeSinceRadUpdate;
                    }
                }
            }

        } else {
            CURRENTLY_OVERRIDING_VISUALS = false;
        }
    }
}



//override radial menu open on key down
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPickupFromWorld))]
static class PlayerCamera_UIOverrideTryPickupFromWorld_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance, ref Item __state, UsableObject usableObject) {
        __state = __instance.dragItem;
    }
    static void Postfix(PlayerCamera __instance, ref Item __state, UsableObject usableObject) {
        if (Net.IsRunning) {
            var pc = __instance;
            var body = pc.body;

            if (pc.dragItem) {

                //make items in inventory not pick up if theyre not registered
                if (!NetObjectRegistry.IsRegistered(pc.dragItem.gameObject) && NetObjectRegistry.ObjectShouldBeSynced(pc.dragItem.gameObject)) {
                    NetObjectRegistry.SafeDestroyObject(pc.dragItem.gameObject);

                    // dont allow picking up items in inventory that arent registered yet, cuz they will be soon
                    //pc.dragItem = null;

                    if (__state == null) {
                        Plugin.log.LogWarning($"User tried to drag a world item that is not registered yet! {pc.dragItem.name} at {pc.dragItem.transform.position} ");
                        NetObjectRegistry.AlertObjectNotRegistered(true);
                    }

                }

            } else {

                // player just opened radial menu, but like ima try without it to be able to interact with players and radial menu closes automatically

                // OG: PlayerCamera.TryPickupFromWorld()
                Component collider2D = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Body", "Limb"));

                if (collider2D != null) {

                    if (!Util.TryGetBodyFromObject(collider2D, out var targetbody)) {
                        return;
                    }

                    if (!targetbody.IsBodyLocal() && targetbody.TryGetNetBody(out var nb)) {
                        __instance.radialOpen = false;
                        if (UIInGame.interaction_menu_target_body != nb) {
                            UIInGame.StartPlayerInteractionMenu(nb);
                        } else {
                            UIInGame.StopPlayerInteractionMenu();
                        }
                    } else {
                        //KrokoshaPlugin.Logger.LogWarning($"TEMP DEV: not hovering over another playuer (Opened radial) DELETEME ");
                    }

                } else if (!pc.dragItem &&
                    // OG: PlayerCamera.TryPickupFromWorld()
                    (bool)usableObject && Vector2.Distance(body.transform.position, usableObject.transform.position) < 10f * usableObject.rangeMultiplier
                ) {
                    
                }
               
            }
        }

    }
}


[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToMainMenu))]
static class PlayerCamera_ToMainMenu_MultiplayerPatch {
    static void Prefix(WorldGeneration __instance) {
        // dont carry someone elses health panel into the menu. it leaves the inventory hud off.
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);

        NetObjectRegistry.UnregisterAllObjectsButKeepInventory();

        Con.UnNoclip();

        if (Net.IsRunning) {
            UIInGame.StopSpectatorMode();

            if (Net.IsClient) {
                if (
                    Multiplayer.showMultiplayerMenu// cuz its set to true from the server call, stupidly, but i use this
                ) {

                } else {
                    Multiplayer.DoMultiplayerStatusMessageLog("Disconnecting because im going to main menu on my own!");
                    Multiplayer._JustDisconnect();
                    Chat.LogMessage(Chat.NAMELABEL_LOCALSYSTEM, "Left the server.");
                }
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Announcing GoBackToMainMenu.");

                var writer = Net.CreateWriter(NetmsgId.CLIENT_GoBackToMainMenu);
                writer.Put(0);
                Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
                // WRITER END;

                Chat.Server_ChatAnnouncement($"<key:host_gotomainmenu>");
            }
        }

        Multiplayer.showMultiplayerMenu = true;
    }

}



[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OpenCraftScreen))]
public static class PlayerCamera_OpenCraftScreen_MultiplayerPatch {
    public static bool Prefix(PlayerCamera __instance) {
        if (Net.IsRunning) {
            if (!__instance.craftingPanel.activeSelf && UIInGame.SpectatorModeActive) {
                return false;
            }
        }
        return true;
    }
}



[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetTimeScale))]
public static class PlayerCamera_SetTimeScale_MultiplayerPatch {
    public static bool force = false;

    static bool Prefix(PlayerCamera __instance, PlayerCamera.SpeedType speed) {
        if (Net.IsRunning) {
            // the escape menu is the only caller of Paused. relaying it freezes
            // every client, and the local restore never runs on their machines.
            if (speed == PlayerCamera.SpeedType.Paused)
                return false;

            if (Net.IsClient && ClientMain.ServerWorldState != null && ClientMain.ServerWorldState.timeScale.Value == 0) // Server is paused
                return false;

            var num = 1f;
            if (Net.IsClient && ClientMain.ServerWorldState != null) {
                num = ClientMain.ServerWorldState.timeScale.Value;
            }

            if (force) {
                force = false;
            } else if (Multiplayer.rules.DisableTimeManipulation) {
                Time.timeScale = num;
                return false;
            } else if (speed != __instance.curTimeScale) {

                if (ServerMain.CheckIfAnyoneIsMoving()) {
                    Time.timeScale = num;
                    return false; // disallow if someone is moving
                }

                if (Net.IsClient) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SetTimeScale, (ushort)speed, true);
                } else {
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_SetTimeScaleRelay, (ushort)speed, true);
                }
            }
        }

        if (__instance.curTimeScale != speed) {
#if DEBUG 
            log.l($"TimeScale set to: {speed} {new StackTrace()}");
#endif 
        }

        return true;
    }

}


































// to see wearables of another person from that really cool "Inventory" interaction menu button
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.UpdateWearables))]
public static class PlayerCamera_UpdateWearables_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance, ref Body __state) {
        if (__instance.body == null) {
            __instance.body = Util.GetLocalBody();
        }
        __state = __instance.body;
        if (InvButton_get_body_MultiplayerPatch.focused_body != null) {
            __instance.body = InvButton_get_body_MultiplayerPatch.focused_body;
        }
    }
    static void Postfix(PlayerCamera __instance, ref Body __state) {
        __instance.body = __state;
    }
}

// to disable the use circle 
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPerformRadialAction))]
public static class PlayerCamera_TryPerformRadialAction_MultiplayerPatch {
    static bool Prefix(ref bool __result) {
        if (InvButton_get_body_MultiplayerPatch.focused_body != null) {
            __result = false;
            return false;
        }
        return true;
    }
}

//override radial menu open on key down
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleWhileDragging))]
static class PlayerCamera_HandleWhileDragging_MultiplayerPatch {

    static bool draining_liquid = false;

    static void Prefix(PlayerCamera __instance, ref bool __state, List<RaycastResult> uiCasts) {
        __state = __instance.radialOpen;
    }

    static void Postfix(PlayerCamera __instance, ref bool __state, List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) return;

        var pc = __instance;
        var body = pc.body;

        if (__instance.radialOpen) {
            if (InvButton_get_body_MultiplayerPatch.focused_body != null) {
                var realcurpos = Camera.main.WorldToScreenPoint(InvButton_get_body_MultiplayerPatch.focused_body.transform.position);
                InvButton_get_body_MultiplayerPatch.focused_body_position_smooth = Vector2.Lerp(InvButton_get_body_MultiplayerPatch.focused_body_position_smooth, realcurpos, 5f * Time.deltaTime);
                __instance.radialMenu.transform.position = InvButton_get_body_MultiplayerPatch.focused_body_position_smooth;
                //__instance.radialText.enabled = false;
                if (InvButton_get_body_MultiplayerPatch.focused_body.TryGetNetBody(out var npc)) {
                    __instance.radialText.text = npc.bodyName;
                }
                __instance.radialCircle.enabled = false;

                // close the menu if target player wandered off too far
                if (!KM.DistanceCheck(InvButton_get_body_MultiplayerPatch.focused_body.transform.position, PlayerCamera.main.body.transform.position, SharedMain.max_player_interaction_distance * 2f)) {
                    __instance.radialOpen = false;
                    InvButton_get_body_MultiplayerPatch.focused_body = null;
                    __instance.radialCircle.enabled = true;
                }
            } else {
                __instance.radialCircle.enabled = true;
                InvButton_get_body_MultiplayerPatch.focused_body_position_smooth = __instance.radialMenu.transform.position;
            }
        }



        // player just opened radial menu, but like ima try without it to be able to interact with players and radial menu closes automatically
        if (!__state && __instance.radialOpen && !Util.IsInWoundView()) {

            // OG: PlayerCamera.TryPickupFromWorld()
            Component collider2D = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Body", "Limb"));

            if (collider2D != null) {
                Body targetbody;

                if (collider2D.TryGetComponent<Limb>(out var _limb)) {
                    targetbody = _limb.body;
                } else if (collider2D.TryGetComponent<Body>(out var _body)) {
                    targetbody = _body;
                } else {
                    return;
                }

                if (!targetbody.IsBodyLocal() && targetbody.TryGetNetBody(out var nb)) {
                    __instance.radialOpen = false;

                    if (UIInGame.interaction_menu_target_body == null) {
                        UIInGame.StartPlayerInteractionMenu(nb);
                    }
                } else {
                    if (UIInGame.interaction_menu_target_body != null) { // dont open the inventory , cuz like i could be trying interacting with someone that is very close to me or something but idk man 
                        __instance.radialOpen = false;
                    }
                }

            }
        } else {
            var currently_draining = false;
            var dragItem = __instance.dragItem;

            // check if hovering over liquid draining button
            foreach (RaycastResult uiCast in uiCasts) {

                if (uiCast.gameObject == __instance.liquidDrainObject) {
                    //OG:  PlayerCamera.HandleWhileDragging  V5t4 + LiquidAffect check
                    if ((bool)__instance.dragItem && !dragItem.IsInWater()) {
                        
                        if (ItemSync.TryGetSyncInfo(__instance.dragItem, out var si)) {
                            si.SetIgnoreTimeForRoundTrip();
                        }

                        WaterContainerItem component = __instance.dragItem.GetComponent<WaterContainerItem>();
                        //component.Drain(component.CalculateDrain(0.2f * Time.deltaTime * component.Capacity)); done by the original

                        currently_draining = component.CurrentTotal > 0;
                    }
                    break;
                }
            }

            if (draining_liquid && !currently_draining) {
                if (Net.IsClient && __instance.dragItem && ItemSync.TryGetSyncInfo(__instance.dragItem, out var si) && si.IsLiquidContainer()) {
#if !RELEASE
                    Plugin.log.LogWarning($"PlayerCamera_HandleWhileDragging_MultiplayerPatch {__instance.dragItem}");
#endif
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_DrainWaterContainer, si.syncId, si.liquidcontainer.CurrentTotal, true);
                }
            }
            draining_liquid = currently_draining;
        }

        // close interaction menu if dragging too far
        if (UIInGame.interaction_menu_target_body != null && __instance.dragItem != null) {
            //OG: PlayerCamera.HandleWhileDragging
            if ((Mathf.Abs(Camera.main.WorldToScreenPoint(UIInGame.interaction_menu_target_body.body.transform.position).x - Input.mousePosition.x) > 500f * PlayerCamera.uiScale && !__instance.tradeMenu.activeSelf) || body.consciousness < 20f) {
                UIInGame.StopPlayerInteractionMenu();
            }
        }



    }


    [ServerReceiver((byte)NetmsgId.SERVER_DrainWaterContainer)]
    static void Server_DrainWaterContainer(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid watercontainer_syncid);
        reader.Get(out float newtotal);

#if DEBUG
        log.l($"received DrainWaterContainer  new ml:{newtotal} ");
#endif
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) &&
            ItemSync.TryGetItemSyncInfo(watercontainer_syncid, out var si) && si.IsLiquidContainer() && Util.IsFinite(newtotal) &&
            ItemSync.CheckIfBodyReachThisItem(si, body)
        ) {
            var lq = si.liquidcontainer;

            var curtotal = si.liquidcontainer.CurrentTotal;

            if (!lq.item.IsInWater() && newtotal >= 0f && curtotal > newtotal) {

                lq.Drain(lq.CalculateDrain(curtotal - newtotal));

            }
        }

    }



}





//make items in inventory not pick up if theyre not registered
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPickupFromUI))]
static class PlayerCamera_TryPickupFromUI_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance, ref List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) return;

        var pc = __instance;
        var body = pc.body;
        var dragItem = __instance.dragItem;

        if (dragItem == null) return;

        if (!NetObjectRegistry.IsRegistered(dragItem.gameObject) && NetObjectRegistry.ObjectShouldBeSynced(dragItem.gameObject)) {
            NetObjectRegistry.Client_DeleteUnregisteredSyncableObject(dragItem.gameObject);

            log.warn($"PlayerCamera.TryPickupFromUI:  User tried to drag an item from UI that is not registered yet! {dragItem?.id} ");

            // dont allow picking up items in inventory that arent registered yet, cuz they will be soon
            dragItem = null;

            NetObjectRegistry.AlertObjectNotRegistered(true);
        }

    }
}






[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
public static class PlayerCamera_HandleInput_MultiplayerPatch {
    static bool Prefix() {

        if (Net.IsRunning) {
            // always allow to move on the end cuz other players might not have still
            if (ServerMain.DidHeFinishTheLayer(PlayerCamera.main.body)) {
                PlayerCamera.main.body.forceWalk = false;
            }
        }

        return
            !Chat.CHAT_textbox_input_focused
            //&& !Con._DEV_FREECAM_CONTROL 
            ;
    }
    static void Postfix() {
        if (Net.IsRunning) {

            var body = Util.GetLocalBody();

            if (body != null && body.alive) {


                if (Input.GetKeyDown(KeyBinds.GetBind("pause"))) {
                    UIInGame.StopPlayerInteractionMenu();
                }

                if (Input.GetKeyDown(KeyBinds.GetBind("bark"))) {
                    //OG: PantSound.Bark
                    if (Util.GetLocalBody().eatTime == 0.5f) { // confirm that it barked

                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerBark, true);
                    }
                }


            }
        }
    }
}



[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SwitchHands))]
public static class PlayerCamera_SwitchHands_MultiplayerPatch {

    public static void Postfix() {
        if (Net.IsRunning) {

            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_SwitchHands, (ushort)(PlayerCamera.main.body.handSlot), true);

        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_SwitchHands)]
    static void Server_PlayerCamera_SwitchHands(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"KrokoshaServerReceiver actually works lol {clientId} ");
#endif

        reader.Get(out ushort handslot);

        if (handslot > 1) {
            Plugin.log.LogError($"SUS: bogus handslot PlayerCamera_SwitchHands {handslot} (plrid: {clientId}, name:{DebugHelp.GetPlayerFullDebugString(clientId)}) ");
            return;
        }

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && plr.IsAlive()) {
            plr.body.handSlot = (int)handslot;

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: PlayerCamera_SwitchHands {handslot}");

            var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerCamera_SwitchHandsRelay);

            writer.Put(clientId);
            writer.Put((byte)handslot);

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);

            // WRITER END;

        }

    }


    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerCamera_SwitchHandsRelay)]
    static void Client_PlayerCamera_SwitchHands(knetid _, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"Client_PlayerCamera_SwitchHands actually works lol  ");
#endif
        reader.Get(out knetid who);
        reader.Get(out byte handslot);

        if (ScavPlayer.TryGetPlayerFromClientId(who, out var plr) && plr.body && !plr.IsLocal) {
            plr.body.handSlot = (int)handslot;

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"C: PlayerCamera_SwitchHandsRelay {handslot}");
        }
    }
}

// its only called from body, so to differentiate between local and remote body ill just rewrite the logic
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.FlashBrain))]
public static class PlayerCamera_FlashBrain_MultiplayerPatch {

    public static void ForceFlashBrain(this PlayerCamera pc) {
        force = true;
        pc.StartCoroutine(pc.FlashBrain());
    }


    public static bool force = false;
    static bool Prefix(PlayerCamera __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }

        if (!force) {
            __result = (new List<object>()).GetEnumerator(); // shut up the coroutine
            return false;
        }
        force = false;


        return true;
    }

}





[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToggleTradeMenu))]
static class PlayerCamera_ToggleTradeMenu_MultiplayerPatch {
    public static GameObject RECRUITButton = null;

    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) return true;


        var og_movebutton = __instance.traderMoveButon;

        if (og_movebutton != null) {
            if (RECRUITButton == null) {
                try {
                    RECRUITButton = GameObject.Instantiate(og_movebutton.gameObject, og_movebutton.transform.parent, false);
                    RECRUITButton.SetActive(true);
                    RECRUITButton.name = "BUTTON_MP_recruit";


                    var rt = RECRUITButton.GetComponent<RectTransform>();
                    var offset = new Vector2(rt.sizeDelta.x + 15, 0);
                    rt.offsetMax += offset;
                    rt.offsetMin += offset;

                    rt.SetSiblingIndex(og_movebutton.transform.GetSiblingIndex());
                    var img = RECRUITButton.transform.GetChild(0).GetComponent<Image>();
                    img.sprite = CoopModAssets.recruit;
                    var button = RECRUITButton.GetComponent<UnityEngine.UI.Button>();
                    button.onClick.RemoveAllListeners();
                    button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                    button.onClick.AddListener(() => {
                        if (PlayerCamera.main.currentTrader != null && NetObjectRegistry.TryGetSyncInfoOrRegister(PlayerCamera.main.currentTrader.build, out var si)) {
                            if (Net.IsServer) {
                                TraderCommit.Submit(TraderCommit.Op.Recruit, PlayerCamera.main.body, si);
                            } else {
                                LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_TraderRecruit);
                                writer.Put(si.syncId);
                                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                            }
                            PlayerCamera.main.ToggleTradeMenu();
                        }
                    });

                } catch (Exception e) {
                    log.error("RECRUITButton creation: " + e.ToString());
                }
            }

        }



        return true;
    }

    public static List<ScavPlayer> GetPlayersEligibleForRespawn() {
        return new List<ScavPlayer>(ScavPlayer.AllDeadPlayers);
    }
    public static ScavPlayer Server_SelectMostSuitablePlayerToRespawn() {
        var rrrr = GetPlayersEligibleForRespawn();
        if (rrrr.Count == 0) {
            return null;
        }
        rrrr.Sort((a, b) => {
            if (!a.IsAltTab && b.IsAltTab) {
                return -1;
            }
            if (a.ServerPlayerState.last_cursormove > b.ServerPlayerState.last_cursormove) {
                return -1;
            } else if (a.ServerPlayerState.last_cursormove == b.ServerPlayerState.last_cursormove) {
                return 0;
            } else {
                return 1;
            }
        });
        return rrrr[0];
    }

    public static int RANDOM_ITEMS_TO_GIVE_AFTER_RESPAWN = 3;


    // spawn trader1 player

    internal static void PerformRecruit(SyncInfo trader_si) {
        if ((trader_si == null) || (trader_si.go == null) || trader_si.IsDestroyed || !Multiplayer.rules.CanReviveFromTrader()) {
            return;
        }
        TrackerTrader trader_tracker = trader_si.go.GetOrAddComponent<TrackerTrader>();
        ScavPlayer available_plr = Server_SelectMostSuitablePlayerToRespawn();
        if ((available_plr == null) || !trader_tracker.CanBeRecruited_ForRespawn()) {
            return;
        }
        log.l($"Recruiting {trader_si} -> to respawn {available_plr}");
        available_plr.Server_RespawnCharacter(trader_si.transformPosition + Vector2.up * 1.5f, true);
        for (int i = 0; i < Math.Min(trader_tracker.og.items.Count, RANDOM_ITEMS_TO_GIVE_AFTER_RESPAWN); i++) {
            int randindex = UnityEngine.Random.Range(0, trader_tracker.og.items.Count);
            TraderItem item = trader_tracker.og.items[randindex];
            trader_tracker.og.items.RemoveAt(randindex);
            GameObject spawned = Utils.Create(item.id, trader_si.go.transform.position, 0f);
            if (spawned.TryGetComponent(out AmmoScript component) && (component.itemType == AmmoScript.AmmoItemType.Magazine)) {
                component.rounds = UnityEngine.Random.Range((int)((float)component.maxRounds * 0.5f), component.maxRounds);
            }
            available_plr.body.AutoPickUpItem(spawned.GetComponent<Item>());
        }
        GameObject.Destroy(trader_si.go);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderRecruit)]
    static void ServerReceiver_RecruitPressed(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid trader_syncid);
        if (TraderSync.Server_TraderInteractionCheck(clientId, trader_syncid, "MP_RECRUIT", out ScavPlayer plr, out SyncInfo trader_si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Recruit, plr.body, trader_si);
            return;
        }
        SyncInfo missed;
        if (NetObjectRegistry.TryGetSyncInfo(trader_syncid, out missed) && (missed.trader != null)) {
            TraderCommit.PublishTo(missed.trader, clientId);
        }
    }




}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.RefreshTraderInventories))]
static class PlayerCamera_RefreshTraderInventories_MultiplayerPatch {

    public static GameObject RECRUITButton => PlayerCamera_ToggleTradeMenu_MultiplayerPatch.RECRUITButton;

    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) return true;

        TraderPinLog.LogRefreshFault(__instance);

        if (RECRUITButton != null && __instance.currentTrader != null) {
            var tt = __instance.currentTrader.GetOrAddComponent<TrackerTrader>();

            var is_interactable = tt.CanBeRecruited_ForRespawn();

            ScavPlayer available_plr = null;
            if (Net.IsServer) {
                available_plr = PlayerCamera_ToggleTradeMenu_MultiplayerPatch.Server_SelectMostSuitablePlayerToRespawn();
            } else {
                var aaa = PlayerCamera_ToggleTradeMenu_MultiplayerPatch.GetPlayersEligibleForRespawn();
                if (aaa.Count > 0) {
                    available_plr = aaa[0];
                }
            }
            if (available_plr == null) {
                is_interactable = false;
            }


            var tooltip = RECRUITButton.GetComponent<UITooltip>();
            tooltip.GetComponent<UnityEngine.UI.Button>().interactable = is_interactable;
            tooltip.skipLocale = true;

            tooltip.tipName = Lang.Get("trader_recruit");
            if (is_interactable) {
                tooltip.tipDesc = Lang.Get("trader_recruitdesc");
            } else {
                if (available_plr == null) {
                    tooltip.tipDesc = Lang.Get("trader_recruitdesc_no_plr");
                } else {
                    tooltip.tipDesc = Lang.Get("trader_recruitdesc_no_rep");
                }
            }

        }

        if (RECRUITButton != null) {
            RECRUITButton.SetActive(Multiplayer.rules.CanReviveFromTrader());
        }


        return true;
    }
}










[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.StartSelfDestruct))]
static class PlayerCamera_StartSelfDestruct_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance) {

        var writer = Net.CreateWriter(NetmsgId.SERVER_selfdestruct);
        writer.Put(true);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        // WRITER END;

        if (ScavPlayer.TryGetLocalNetBody(out var locnb)) {
            locnb.SetNetHealthSyncIgnoreTime(4);
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_selfdestruct)]
    static void ServerReceiver_selfdestruct(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && pb.alive && plr.ServerPlayerState.Cooldown("selfdestruct", 6)) {

            log.devevent($"Selfdestruct {pb}", plr.pos, true);

            Util.StartCoroutine(SelfDestructSequence(pb));
        }
    }
    public static readonly float SelfDestructIdentifier = 5434534; // Just a random unique number

    static IEnumerator SelfDestructSequence(NetBody nb) {
        var body = nb.body;
        List<knetid> to_who = ServerMain.AllClientIds.ToList();
        if (nb.IsPlayer) {
            to_who.Remove(nb.plr.playerId);
        }


        body.eyeScareTime = 10f;
        if (!nb.IsBodyLocal())
            body.talker.Talk(Locale.GetCharacter("selfdestruct"), null, force: false, resetTalkTimer: true);
        ServerMain.Server_AnnounceSound(body.GetPosition(), "selfdestruct1", to_who);
        yield return new WaitForSeconds(3f);
        body.eyeCloseTime = 10f;
        yield return new WaitForSeconds(1.15f);

        ServerMain.Server_AnnounceSound(body.GetPosition(), "gore", to_who);
        ServerMain.Server_InstantiateInstance(body.limbs[0].transform.position, 0, "BloodExplosion", to_who);
        ServerMain.Server_InstantiateInstance(body.limbs[0].transform.position, 0, "Special/ExplosionParticle", to_who);

        nb.OnDamageEvent(new BodyDamageEvent(nb, nb, null, SelfDestructIdentifier));

        yield return new WaitForSeconds(0.02f);
        body.brainHealth = 0f;
        body.consciousness = 0f;
        body.heartRate = 0f;
        body.limbs[0].muscleHealth = 0f;
        body.limbs[0].skinHealth = 0f;
        body.limbs[0].bleedAmount = 100f;
        body.Disfigure();
        body.RemoveEye();
        body.RemoveEye();

    }

}



[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetIrradiateIntensity))]
public static class PlayerCamera_SetIrradiateIntensity_MultiplayerPatch {
    public static bool forceSetIrradiateIntensity = false;
    public static void RealSetIrradiateIntensity(float rad) {
        if (ScavPlayer.TryGetLocalNetBody(out var nb)) {
            RealSetIrradiateIntensity(nb, rad);
        }
    }
    // must replace all original SetIrradiateIntensity to call this
    public static void RealSetIrradiateIntensity(NetBody nb, float rad) {
        nb.irradiateIntensity = Mathf.Max(nb.irradiateIntensity, rad);
        if (rad >= nb.irradiateIntensity) {
            nb.timeSinceRadUpdate = 0f;
        }
    }
    static bool Prefix(PlayerCamera __instance) {
        if (Net.IsRunning) {
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleUnconsciousScreen))]
public static class vyosna_dawn_HandleUnconsciousScreen_Patch {
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        List<int> OccurrencesToRemove = new List<int> { 0, 1 };
        int CurrentOccurrence = 0;
        int i = 0;
        while (i < instructions.Count() && CurrentOccurrence < OccurrencesToRemove.Count) {
            MethodBase method;
            if (list[i].ToString().Contains(nameof(PlayerCamera.SetTimeScale)) && OccurrencesToRemove.Contains(CurrentOccurrence) && (method = (list[i].operand as MethodBase)) != null) {
                list[i].opcode = OpCodes.Nop;
                list[i].operand = null;
                int GoBackwards = method.GetParameters().Length;
                if (!method.IsStatic) {
                    GoBackwards++;
                }
                for (int Key = i; Key >= i - GoBackwards; Key--) {
                    list[Key].opcode = OpCodes.Nop;
                    list[Key].operand = null;
                }
                CurrentOccurrence++;
            }
            i++;
        }
        return list;
    }
}








[HarmonyPatch(typeof(MoodleManager), nameof(MoodleManager.AddMoodle))]
static class MoodleManager_AddMoodle_Patch {

    static bool Prefix(MoodleManager __instance, ref int intensity) {
        //Add an intensity safeguard
        if (intensity >= __instance.backgroundIcons.Length) {
            intensity = __instance.backgroundIcons.Length;
        }
        return true;
    }
}



    