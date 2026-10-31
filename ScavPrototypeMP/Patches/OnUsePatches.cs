using HarmonyLib;
using System;
using System.Collections.Generic;
using System.EnterpriseServices;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using LiteNetLib;
using Unity.VisualScripting;
using UnityEngine;
using CasualtiesTogetherUtils;
using KrokV5Optimization;

namespace Together;





[HarmonyPatch(typeof(BioTerminalScript), nameof(BioTerminalScript.OnUse))]
public static class BioTerminalScript_OnUse_MultiplayerPatch {

    //OG: BioTerminalScript.OnUse  
    public static void OnUseRewritten(Body user, BuildingEntity building) {
        bool flag = false;
        foreach (Item item in user.GetAllItemsThorough()) {
            if (item.TryGetComponent<WaterContainerItem>(out var component) && component.AmountOf("redblood") >= 100f) {
                flag = true;
                component.Drain(component.CalculateDrainSingleLiquid("redblood", 100f));
                break;
            }
        }
        if (flag) {
            building.Backgroundify();
            Sound.Play("beep", building.transform.position);
            Collider2D[] array = Physics2D.OverlapCircleAll(building.transform.position, 6f);
            for (int i = 0; i < array.Length; i++) {
                if (array[i].TryGetComponent<BuildingEntity>(out var component2) && component2.id == "reinforceddoor") {
                    component2.Backgroundify();
                }
            }
        }
    }



    static void Postfix(BioTerminalScript __instance) {
        if (Net.IsRunning) {
            var tr = __instance.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
            if (tr.is_backgroundified && NetObjectRegistry.TryGetSyncInfo(__instance.gameObject,out var si)) {
                //success

                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_BioTerminalScriptOpenShitheadBruh, si.syncId, true);
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_BioTerminalScriptOpenShitheadBruh)]
    static void Server_BioTerminalScriptOpenShitheadBruh(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"Server_BioTerminalScriptOpenShitheadBruh actually works lol {clientId} ");
#endif
        reader.Get(out knetid bioterminal_syncid);

        ScavPlayer user;
        Body body;
        SyncInfo si;
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out user, out body) &&
            NetObjectRegistry.TryGetSyncInfo(bioterminal_syncid, out si) && 
            (clientId == 0 ||
             ( user.body.conscious
                 && si.IsBuilding() && 
                 si.go.TryGetComponent<TrackerBuildingEntityBackgroundified>(out var tr) && !tr.is_backgroundified && si.go.TryGetComponent<BioTerminalScript>(out var biot)
                 && KM.DistanceCheck(body, si.building,20)
            ))
        ) {

            if (clientId != 0) { 
                OnUseRewritten(body, si.building);
            }
            ServerMain.Server_AnnounceSound(si.go.transform.position, "beep", ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"S: open BioTerminal {user}");

        }
    }

}
















[HarmonyPatch(typeof(DrillPod), nameof(DrillPod.OnUse))]
public static class DrillPod_OnUse_MultiplayerPatch {

    //OG: DrillPod.OnUse  
    public static void OnUseRewritten(Body user, DrillPod drel) {
        if (user.FindByIdThorough("drillrepairkit", out var it)) {

#if DEBUG
            log.l($"DrillPod OnUse OnUseRewritten CALLED !!!!!!!!!!!!! ");
#endif

            UnityEngine.Object.Destroy(it.gameObject);
            ForceRepairDrill(drel);
        }
    }
    //OG: DrillPod.OnUse  
    public static void ForceRepairDrill(DrillPod drel) {

#if DEBUG
        log.l($"DrillPod OnUse ForceRepairDrill  CALLED !!!!!!!!!!!!! ");
#endif

        Traverse.Create(drel).Field("working").SetValue(true);
        Sound.Play("drillpodrepair", drel.transform.position, twoDimensional: false, pitchShift: false);
        drel.GetComponent<BuildingEntity>().description = Locale.GetBuilding("drillpoddscfixed");
        UnityEngine.Object.Destroy(drel.GetComponent<UsableObject>());
    }



    static void Prefix() {
        if (Net.IsRunning) {

        }
    }


    static void Postfix(DrillPod __instance) {
        if (Net.IsRunning) {
            if ( NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si)) {
                //success

#if DEBUG
                Plugin.log.LogWarning($"Client_SendSimpleMessageToServer  DrillPodRepairTypeShit ");
#endif

                si.SetIgnoreTimeForRoundTrip();

                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_DrillPodRepairTypeShit, si.syncId, true);
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_DrillPodRepairTypeShit)]
    static void Server_DrillPodRepairTypeShit(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"Server_DrillPodRepairTypeShit actually works lol {clientId} ");
#endif
        reader.Get(out knetid bioterminal_syncid);

        ScavPlayer user;
        Body body;
        DrillPod drel = null ;
        SyncInfo si = null;
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out user, out body) &&
            NetObjectRegistry.TryGetSyncInfo(bioterminal_syncid, out si) && 
            si.go.TryGetComponent<DrillPod>(out drel) &&
            (   clientId == 0 ||
             (  user.body.conscious && 
                si.IsBuilding() &&
                 !Traverse.Create(drel).Field("working").GetValue<bool>()  && 
                 KM.DistanceCheck(body, si.building, 20)
            ))
        ) {
#if DEBUG
            Plugin.log.LogWarning($" Server_DrillPodRepairTypeShit    IS WORKING   YAYYAAYAYYAYAYAYA  {user}  dril:  {drel}  si: {si} ");
#endif

            if (clientId != 0) {
                OnUseRewritten(body, drel);
            }
            ServerMain.Server_AnnounceSound(si.go.transform.position, "drillpodrepair", ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"S: DrillPod Repair {user}");

            return;
        }
#if DEBUG
        Plugin.log.LogWarning($" Server_DrillPodRepairTypeShit DENIED !!!!!!!!!!!!!!!!!!!!!!  {user}  dril:  {drel}  si: {si} ");
#endif
    }


}















// OnUse is by the structure of the mod, only triggerable by local player 
[HarmonyPatch(typeof(LifepodButton), nameof(LifepodButton.OnUse))]
public static class LifepodButton_OnUse_MultiplayerPatch {




    static bool Prefix(LifepodButton __instance) {
        if (Net.IsRunning) {
            var parentbuilding = __instance.transform.parent.GetComponent<BuildingEntity>();
#if !RELEASE
            if (log.verbose) log.l($"Lifepod button pressed: parent:{__instance.transform.parent} building:{parentbuilding}  si:{(NetObjectRegistry.TryGetSyncInfo(parentbuilding.gameObject, out var si) ? si.ToString() : "NULL")} ");
#endif
            if (parentbuilding ) {
                if (NetObjectRegistry.TryGetSyncInfoOrRegister(parentbuilding.gameObject, out var si)) {

                    //continue  to the postfix

                } else {
                    log.warn($"Attempted to use unregistered lifepod buttons {parentbuilding.id} (at {(Vector2)parentbuilding.transform.position}), aborting.");

                    NetObjectRegistry.AlertObjectNotRegistered(true);
                    return false;
                }
            }
        }
        return true;
    }
    static void Postfix(LifepodButton __instance) {
        if (Net.IsRunning) {
            var parentbuilding = __instance.transform.parent.GetComponent<BuildingEntity>();

            if (parentbuilding) {
                if (NetObjectRegistry.TryGetSyncInfo(parentbuilding.gameObject, out var si)) {

                    //success
                    if (log.verbose) log.l($"Lifepod button pressed: type:{__instance.type} si:{si} ");

                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_LifepodButtonActivate, si.syncId, (byte)__instance.type, (byte)__instance.controller.heatState, true);

                }
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_LifepodButtonActivate)]
    static void Server_LifepodButtonActivate(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid lifepodbuttonactivate_syncid);
        reader.Get(out byte buttontype);
        reader.Get(out byte heatState);
#if !RELEASE
        Plugin.log.LogWarning($"Server_LifepodButtonActivate {clientId}:\nsyncid={lifepodbuttonactivate_syncid}\nbuttontype={buttontype}\nheatState={heatState} ");
#endif

        ScavPlayer user;
        Body body;
        SyncInfo si = null;
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out user, out body) &&
            NetObjectRegistry.TryGetSyncInfo(lifepodbuttonactivate_syncid, out si) && si.IsBuilding()  &&
            KM.DistanceCheck(si.go.transform.position,body.transform.position,20)
        ) {
            var lifepodcontroller = si.go.GetComponentInChildren<LifepodController>();
            
            if (lifepodcontroller != null) {

                if (log.verbose) log.l($"SERVER: Lifepod button pressed: {user}  type:{buttontype}:{(buttontype == 0 ? "HEATER" : "SHOWER")}  heatstate:{heatState} si:{si} ");
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"S: LifepodButtonActivate {buttontype}:{(buttontype == 0 ? "HEATER" : "SHOWER")} HEAT:{heatState}");

                if (buttontype != 0 ) {
                    if ( lifepodcontroller.shower != null) {
                        lifepodcontroller.ActivateShower();
                    } else {
                        log.error_devevent($"LIFEPOD SHOWER BUTTON: {si} DOES NOT HAVE A SHOWER ???????????",si.transformPosition);
                    }
                }

                lifepodcontroller.heatState = Mathf.Max(-1,heatState - 1);
                if (lifepodcontroller.heater != null) {
                    lifepodcontroller.ToggleHeatState();
                } else {
                    log.error_devevent($"LIFEPOD HEAT TOGGLE: {si} DOES NOT HAVE A HEATER ???????????", si.transformPosition);
                }

                var writer = Net.CreateWriter(NetmsgId.CLIENT_LifepodButtonActivateRelay);
                writer.Put(lifepodbuttonactivate_syncid);
                writer.Put((byte)buttontype);
                writer.Put((byte)lifepodcontroller.heatState);
                Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                // WRITER END;

                return;
            }
        }
        
        log.serverdeny($"LifepodButton DENIED, something seems sus: user:{user} si:{si}");

    }




    [ClientReceiver((byte)NetmsgId.CLIENT_LifepodButtonActivateRelay)]
    static void Client_LifepodButtonActivateRelay(knetid __, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid lifepodbuttonactivate_syncid);
        reader.Get(out byte buttontype);
        reader.Get(out byte heatState);


        SyncInfo si;
        if (
            NetObjectRegistry.TryGetSyncInfo(lifepodbuttonactivate_syncid, out si) && si.IsBuilding()
        ) {
            var lifepodcontroller = si.go.GetComponentInChildren<LifepodController>();

            if (lifepodcontroller != null) {

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"C: LifepodButtonActivate {buttontype} {heatState}");

                if (buttontype != 0) {
                    lifepodcontroller.ActivateShower();
                }

                lifepodcontroller.heatState = Mathf.Max(-1, heatState - 1);
                lifepodcontroller.ToggleHeatState();

            }
        }
    }




}

















[HarmonyPatch(typeof(LifepodShower), nameof(LifepodShower.Update))]
public static class LifepodShower_Update_MultiplayerPatch {
    static bool Prefix(LifepodShower __instance) {
        if (Net.IsRunning) {
            //OG: LifepodShower.Update V5t4
            if (Time.time - __instance.timeActive < 3f) {

                // max X 8  only under   max dist 15
                foreach (var item in ScavPlayer.GetPlayerBodiesInRadius(__instance.transform.position, 16)) {
                    if (Mathf.Abs(__instance.transform.position.x - item.transform.position.x) < 8f && __instance.transform.position.y > item.transform.position.y) {

                        var body = item.body;

                        //OG: LifepodShower.Update V5t4
                        body.dirtyness -= Time.deltaTime * 16.7f;
                        body.snowAmount += Time.deltaTime * 0.33f;
                        if (body.snowAmount > 1f) {
                            body.snowAmount = 1f;
                        }
                        body.eyeScareTime = 0.5f;
                        Limb[] limbs = body.limbs;
                        for (int i = 0; i < limbs.Length; i++) {
                            limbs[i].SetDisinfect(120f);
                        }

                    }
                }



            }
            return false;
        }
        return true;
    }
}



















[HarmonyPatch(typeof(Openable), nameof(Openable.OnUse))]
public static class Openable_OnUse_MultiplayerPatch {

    static void Prefix() {
        if (!Net.IsRunning) return;
    }

    static void Postfix(Openable __instance) {
        if (Net.IsRunning) {
            bool synced = NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si);
            BuildingEntity building = __instance.GetComponent<BuildingEntity>();
            bool opened = synced && building != null && building.GetHealth() == 0f;
            EvidenceBag.Arm();
            EvidenceBag.Line("open client instant=" + __instance.instantOpen
                + " keypad=" + __instance.isKeypad
                + " sent=" + opened
                + " synced=" + synced
                + " " + EvidenceBag.Building(building));
            if (opened) {
                //success

                si.SetIgnoreTimeForRoundTrip();

                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_OpenableOpenTypeShit, si.syncId, true);
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_OpenableOpenTypeShit)]
    static void Server_OpenableOpenTypeShit(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid bioterminal_syncid);

        ScavPlayer user;
        Body body;
        Openable openalbe = null;
        SyncInfo si = null;
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out user, out body) &&
            NetObjectRegistry.TryGetSyncInfo(bioterminal_syncid, out si) &&
            si.go.TryGetComponent<Openable>(out openalbe) && openalbe.instantOpen && // ONLY INSTANTOPEN SHIT IS SENT HERE !!!!!!!!!!!!!
            (clientId == 0 ||
             (user.body.conscious &&
                si.IsBuilding() &&
                 KM.DistanceCheck(body, si.building, 20) 
                  
            ))
        ) {


            if (clientId != 0) {
                openalbe.OnUse();
            }

            EvidenceBag.Arm();
            EvidenceBag.Line("open server ok client=" + clientId + " " + EvidenceBag.Building(si.building));
            
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"S: Openable open {user}, si:{si}");

            return;
        }
        BuildingEntity deniedBuilding = null;
        if (si != null && si.go != null)
            deniedBuilding = si.building;
        EvidenceBag.Line("open server deny client=" + clientId
            + " instant=" + (openalbe != null && openalbe.instantOpen)
            + " sync=" + (si != null ? si.syncId.ToString() : "none")
            + " " + EvidenceBag.Building(deniedBuilding));
#if DEBUG
        Plugin.log.LogWarning($" Server_OpenableOpenTypeShit DENIED !!!!!!!!!!!!!!!!!!!!!!  {user}  openebleeh:  {openalbe}  si: {si} ");
#endif
    }


}











[HarmonyPatch(typeof(ScrapEaterScript), nameof(ScrapEaterScript.OnUse))]
public static class ScrapEaterScript_OnUse_MultiplayerPatch {


    static void Prefix(ScrapEaterScript __instance) {
        if (Net.IsRunning) {
            if (!PlayerCamera.main.body.FindByIdThorough("scrapmetal", out var it)) {
                return;
            }

            if (NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si) ) {
                //success

#if DEBUG
                Plugin.log.LogWarning($"Client_SendSimpleMessageToServer  ScrapEaterScriptUse ");
#endif

                si.SetIgnoreTimeForRoundTrip();

                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_ScrapEaterScriptUse, si.syncId, true);
            }
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_ScrapEaterScriptUse)]
    static void Server_ScrapEaterScriptUse(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"Server_ScrapEaterScriptUse actually works lol {clientId} ");
#endif
        reader.Get(out knetid bioterminal_syncid);

        ScavPlayer user;
        Body body;
        ScrapEaterScript ses = null;
        SyncInfo si = null;
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out user, out body) &&
            NetObjectRegistry.TryGetSyncInfo(bioterminal_syncid, out si) &&
            si.go.TryGetComponent<ScrapEaterScript>(out ses) && // ONLY INSTANTOPEN SHIT IS SENT HERE !!!!!!!!!!!!!
            (clientId == 0 ||
             (user.body.conscious &&
                si.IsBuilding() &&
                 KM.DistanceCheck(body, si.building, 20)

            ))
        ) {
#if DEBUG
            Plugin.log.LogWarning($" Server_ScrapEaterScriptUse    IS WORKING   YAYYAAYAYYAYAYAYA  {user}  si: {si} ");
#endif

            //OG: ScrapEaterScript.OnUse   V5t4  ENTIRE FUNCTION STOLEN

            if (!body.FindByIdThorough("scrapmetal", out var it)) {
                return;
            }
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"S: ScrapEaterScript open {user}, si:{si}");

            if (clientId != 0) { // is not host

                ses.scrapAmount += it.condition;
                it.condition = 0f;
                if (!(ses.scrapAmount >= ScrapEaterScript.target)) {
                    return;
                }
                ses.build.Backgroundify();
                Collider2D[] array = Physics2D.OverlapCircleAll(ses.transform.position, 2f);
                for (int i = 0; i < array.Length; i++) {
                    if (array[i].TryGetComponent<BuildingEntity>(out var component) && component.id == "reinforceddoor") {
                        component.Backgroundify();
                    }
                }

            }
            
            ServerMain.Server_AnnounceSound(si.go.transform.position, "beep", ServerMain.GetListOfClientIdsExceptThis(clientId));


            return;
        }
#if DEBUG
        Plugin.log.LogWarning($" Server_ScrapEaterScriptUse DENIED !!!!!!!!!!!!!!!!!!!!!!  {user}  ses:{ses}  si: {si} ");
#endif
    }


}












internal class OnUsePatches {
}

