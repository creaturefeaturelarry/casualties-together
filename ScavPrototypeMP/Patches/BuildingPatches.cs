using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace Together;




[HarmonyPatch(typeof(OilPipeScript), nameof(OilPipeScript.Start))]
static class OilPipeScript_Start_MultiplayerPatch {
    static void Postfix(OilPipeScript __instance) {
        __instance.lastOilTime = Time.time + UnityEngine.Random.Range(1f, 30f);
        __instance.coolTime *= (Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count) + 0.5f);
    }
}

[HarmonyPatch(typeof(OilPipeScript), nameof(OilPipeScript.Update))]
static class OilPipeScript_Update_MultiplayerPatch {
    static bool Prefix(OilPipeScript __instance) {
        // stock indexes fluid[] with no check. during a tilemap rebuild that array is null
        // or the cell is off the grid, and the pump throws instead of waiting.
        if (!PumpReady(__instance))
            return false;
        if (!Net.IsRunning)
            return true;
        // the lifepod pump is not a synced object. each machine runs its own.
        // if the host skips it, the next fluid chunk is empty and the client's pool vanishes.
        if (Net.IsServer && __instance.TryGetComponent(out BuildingEntity building) && building.id == "lifepodpump")
            return true;
        return SharedMain.PositionIsInSimulatedChunk(__instance.transform.position);
    }

    static bool PumpReady(OilPipeScript pipe) {
        if (pipe == null)
            return false;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists || world.generatingWorld)
            return false;
        FluidManager fluids = FluidManager.main;
        if (fluids == null || fluids.fluid == null)
            return false;
        Vector2Int cell = world.WorldToBlockPos(pipe.transform.position);
        int w = fluids.fluid.GetLength(0);
        int h = fluids.fluid.GetLength(1);
        return cell.x >= 0 && cell.y >= 0 && cell.x < w && cell.y < h;
    }
}

// stock only rumbles if the host camera is close. use the sim chunks instead, same as oil pipes.
[HarmonyPatch(typeof(GeyserScript), nameof(GeyserScript.OnTriggerEnter2D))]
static class GeyserScript_OnTriggerEnter2D_MultiplayerPatch {
    static bool Prefix(GeyserScript __instance, Collider2D collision) {
        if (!Net.IsRunning)
            return true;
        if (!collision.attachedRigidbody || collision.attachedRigidbody.bodyType != RigidbodyType2D.Dynamic)
            return false;
        if (!SharedMain.PositionIsInSimulatedChunk(__instance.transform.position))
            return false;
        __instance.TryRumble();
        return false;
    }
}

// shake, hp rumble, and the fluid spit. runs if somebodys in those chunks, so a far client still gets it.
[HarmonyPatch(typeof(GeyserScript), nameof(GeyserScript.Update))]
static class GeyserScript_Update_MultiplayerPatch {
    static bool Prefix(GeyserScript __instance) {
        if (!Net.IsRunning)
            return true;
        return SharedMain.PositionIsInSimulatedChunk(__instance.transform.position);
    }
}





[HarmonyPatch(typeof(ElderThornbackBehaviour), nameof(ElderThornbackBehaviour.Update))]
static class ElderThornbackBehaviour_Update_MultiplayerPatch {
    static ElderThornbackBehaviour_Update_MultiplayerPatch() {
        // stays awake outside the 3x3, so the opt out can be checked in game
        ChunkSleep.Ignore(typeof(ElderThornbackBehaviour));
    }

    static bool Prefix(ElderThornbackBehaviour __instance) {
        if (!Net.IsRunning) return true;

        //OG: ElderThornbackBehaviour.Update V7.0.1

        // far
        foreach (var nb in NetBody.GetBodiesInRadius(__instance.transform.position, ElderThornbackBehaviour.maxDistance * 2.25f)) {
            if (nb.IsBodyLocal()) { continue; }

            var body = nb.body;

            body.adrenaline = 100f;

            if (body.horrifiedLevel < 50f) {
                body.horrifiedLevel = 50f;
            }
            if (body.sleeping) {
                body.energy = Mathf.Max(body.energy, 10f);
            }
        }

        // close
        foreach (var nb in NetBody.GetBodiesInRadius(__instance.transform.position, ElderThornbackBehaviour.maxDistance)) {
            if (nb.IsBodyLocal()) { continue; }
            var body = nb.body;

            if (__instance.stage != 0) {
                body.focusedLevel = 100f;
            }

            body.stamina += 1f;
            body.energy = Mathf.Max(body.energy, 15f);

            if (body.sleeping) {
                body.sleeping = false;
            }

            body.adrenaline = 50f;
            body.horrifiedLevel = 100f;
        }


        return true;
    }
}


[HarmonyPatch(typeof(ElderThornbackBehaviour), nameof(ElderThornbackBehaviour.OnDestroy))]
static class ElderThornbackBehaviour_OnDestroy_MultiplayerPatch {
    static bool Prefix(ElderThornbackBehaviour __instance) {
        if (__instance.build == null) {
            return false;
        }
        if (!Net.IsRunning) return true;
        if (__instance.build.GetHealth() <= 0) {
            foreach (var nb in NetBody.GetBodiesInRadius(__instance.transform.position, ElderThornbackBehaviour.maxDistance)) {
                if (nb.IsBodyLocal()) { continue; }

                var body = nb.body;
                body.horrifiedLevel = 0f;
                body.happiness += 40f;
                body.caffeinated += 600f;
            }
        }
        return true;
    }
}













// rewritten
[HarmonyPatch(typeof(BatteryRecharger), nameof(BatteryRecharger.OnUse))]
public static class BatteryRecharger_OnUse_MultiplayerPatch {



    public static bool Copypasted_OnUse(BatteryRecharger charger, Item item, Body body = null) {
        //OG: BatteryRecharger.OnUse V600 steam playtest 1

        var isFull = charger.items[0] != null && charger.items[1] != null;

        if ((bool)item && item.Stats is BatteryInfo && !isFull && !charger.items.Contains(item)) {

            ItemSync.SafeUnloadItem(ItemSync.ItemGetContainerInfo(item));

            int firstAvailable = !charger.items[0] ? 0 : 1;


            item.transform.rotation = Quaternion.identity;
            item.transform.position = charger.targets[firstAvailable].position;
            item.rb.rotation = 0f;
            item.rb.position = item.transform.position;

            item.GetComponent<SpriteRenderer>().sortingOrder = charger.GetComponent<SpriteRenderer>().sortingOrder + 1;

            charger.joints[firstAvailable] = item.gameObject.AddComponent<FixedJoint2D>();
            charger.joints[firstAvailable].frequency = 20f;
            charger.joints[firstAvailable].autoConfigureConnectedAnchor = true;
            charger.joints[firstAvailable].connectedAnchor = charger.targets[firstAvailable].position;
            charger.items[firstAvailable] = item;
            Util.PlayWorldSoundOnScreenIfInRange("batteryinsert", charger.transform.position, 1, 1);
            if (charger.firstTime) {
                charger.GetComponent<Talker>().Talk(Locale.GetOther("rechargingstationcheer"));
                if (Net.IsServer) {
                    Item component = Utils.Create("mp3player", charger.transform.position, 0f).GetComponent<Item>();
                    body?.AutoPickUpItem(component);
                    component.battery.UnloadBattery(skipSpawn: true);
                }
                charger.firstTime = (false);
            }


            if (Net.IsRunning) {
                if (NetObjectRegistry.TryGetSyncInfo(item, out var battery_si)) {

                    if (Net.IsClient) {

                        battery_si.SetIgnoreTimeForRoundTrip(0.5f);

                    } else {
                        if (NetObjectRegistry.TryGetSyncInfo(charger, out var charger_si)) {

                            var writer = Net.CreateWriter(NetmsgId.CLIENT_BatteryRechargerOnUseRelay);
                            writer.Put(battery_si.syncId);
                            writer.Put(charger_si.syncId);
                            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                            // WRITER END;
                        }
                    }
                }

            }
            return true;
        }

        return false;
    }



    static bool Prefix(BatteryRecharger __instance) {
        if (!Net.IsRunning) return true;

        //OG: BatteryRecharger.OnUse V5t5
        Item item = PlayerCamera.main.body.GetItem(PlayerCamera.main.body.handSlot);

        if (Copypasted_OnUse(__instance, item, PlayerCamera.main.body)) {
            if (Net.IsClient) {
                if (NetObjectRegistry.TryGetSyncInfoOrRegister(item, out var si) && NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var chasi)) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_BatteryRechargerOnUse, si.syncId, chasi.syncId, true);
                } else {
                    NetObjectRegistry.AlertObjectNotRegistered(true);
                }
            }
        }

        return false;
    }




    [ServerReceiver((byte)NetmsgId.SERVER_BatteryRechargerOnUse)]
    static void ServerReceiver_MinigameSchrapnelLocations(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid batteryid);
        reader.Get(out knetid chargerid);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) && body.conscious && NetObjectRegistry.TryGetSyncInfo(batteryid, out var battery_si) && NetObjectRegistry.TryGetSyncInfo(chargerid, out var charger_si)) {

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(charger_si.transformPosition, $"BatteryRecharger.OnUse {plr} {battery_si} {charger_si} ");

            Copypasted_OnUse(charger_si.go.GetComponent<BatteryRecharger>(), battery_si.item);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BatteryRechargerOnUseRelay)]
    static void ClientReceiver_MinigameSchrapnelLocations(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid batteryid);
        reader.Get(out knetid chargerid);

        if (NetObjectRegistry.TryGetSyncInfo(batteryid, out var battery_si) && NetObjectRegistry.TryGetSyncInfo(chargerid, out var charger_si)) {

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(charger_si.transformPosition, $"BatteryRechargerOnUseRelay {battery_si} {charger_si} ");

            Copypasted_OnUse(charger_si.go.GetComponent<BatteryRecharger>(), battery_si.item);
        }
    }



}






// patch the sound
[HarmonyPatch(typeof(CaveTickSpawner), nameof(CaveTickSpawner.OnTriggerEnter2D))]
static class CaveTickSpawner_SoundPatch {
    static void Prefix(CaveTickSpawner __instance, Collider2D other, ref bool __state) {
        __state = __instance.started;
    }
    static void Postfix(CaveTickSpawner __instance, Collider2D other, ref bool __state) {
        if (__instance.started && !__state) {
            Sound_Play_MultiplayerPatch.force = true;
            Sound.Play("caveticks", __instance.transform.position, twoDimensional: false, pitchShift: false, null, 0.7f);
        }
    }
}

