using CasualtiesTogetherUtils;
using KrokV5Optimization;
using HarmonyLib;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using Together;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using static UnityEngine.GraphicsBuffer;
using Random = UnityEngine.Random;

namespace Together;




// players view extends to 4:3   50 x 40    16:9   40x25      so 64 radius is perfect  I THINK THIS CHANGED AFTER V5 PRETEST 5



public class ItemDespawnerIfUntouched :MonoBehaviour {
    void Awake() {
       
    }

    public float despawntime = 60;
    float timer = 0;
    void Update() {
        if (Multiplayer.IsNetworkActiveAndIsClient() || transform.parent != null) { // is in a container or inventory
            timer = 0;
            return;
        }

        timer += Time.deltaTime;

        if (timer > despawntime) {
            timer = -1f;
            NetObjectRegistry.SafeDestroyObject(gameObject);
        }
    }
}



static class GravBagBoom {
    // marks a grav bag that already played its blast, so the retry does not play it again.
    sealed class GravBagDetonated : MonoBehaviour { }

    internal static bool Armed(Item item, float condition) {
        return item != null && item.id == "gravbag" && condition <= 0.005f
            && item.battery != null && item.battery.hasBattery;
    }

    internal static void Play(Item item, float condition) {
        if (!Armed(item, condition) || item.GetComponent<GravBagDetonated>() != null)
            return;
        item.gameObject.AddComponent<GravBagDetonated>();
        WorldGeneration.CreateExplosion(new ExplosionParams {
            position = item.transform.position,
            disfigureChance = 0.15f
        });
    }
}

[HarmonyPatch(typeof(CustomItemBehaviour), nameof(CustomItemBehaviour.Update))]
public static class CustomItemBehaviour_Update_MultiplayerPatch {
    static float last_zoomtime = 0;

    public static bool Prefix(CustomItemBehaviour __instance) {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            var item = __instance.GetComponent<Item>();

            last_zoomtime = PlayerCamera.main.zoomTime;

            if (item.id == "jetpack") { // jetpack uses keybind shit bruuh i dont wana
                var pb = item.GetComponentInParent<NetBody>();
                if (pb) { 
                
                    if (pb.IsLocal) {
                        return true;
                    }

                    //OG: CustomItemBehaviour.Update  
                    if (item.condition > 0f && !item.isWet && (bool)item.transform.parent && item.transform.parent.TryGetComponent<Limb>(out var component2) && component2.body.conscious && !component2.body.currentClimbable && component2.body.moveDir.y > 0.1f) { 
                        item.condition -= Time.deltaTime * 0.0135f;
                    }
                }

                return false;
            } else if (item.id == "lightbulb") {
                var pb = item.GetComponentInParent<NetBody>();
                if (pb != null && pb.IsLocal && Net.IsClient) {
                    __instance.state = 0; // Make it not trigger the explosion on client, so it wouldn't drop ahead of time
                }
            } else if ((item.id == "craftingbottle") && NetObjectRegistry.IsRegistered(item.gameObject) && item.TryGetComponent(out WaterContainerItem bottle) && (bottle.CurrentTotal <= 0f)) {
                // stock deletes the mesh as soon as the bottle reads empty. the entity is still
                // on the host, so the next packet builds it again.
                if (Net.IsServer && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo bottleSi) && (bottleSi is not null) && !bottleSi.IsDestroyed) {
                    bottleSi.Destroy();
                }
                return false;
            } else if (GravBagBoom.Armed(item, item.condition)) {
                // spill first. the blast has to see the contents, and destroying the bag used to take them with it.
                if (item.TryGetComponent(out Container box)) {
                    BagSleep.WakeContents(box);
                    box.ContainerBroke();
                    if (Net.IsServer && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out var bagSi) && bagSi != null && !bagSi.IsDestroyed)
                        ItemSync.ReleaseNamedContents(bagSi);
                }
                GravBagBoom.Play(item, item.condition);
                if (Net.IsServer) {
                    if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out var si) && si != null && !si.IsDestroyed)
                        si.Destroy();
                    else
                        UnityEngine.Object.Destroy(item.gameObject);
                }
                return false;
            }
            // btw campfire is okay, its checks only the local player always which unintendedly does what i want it to
        }
        return true;
    }
    public static void Postfix(CustomItemBehaviour __instance) {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            var item = __instance.GetComponent<Item>();

            switch (item.id) {
                case "autozoomgoggles": {
                    if ((bool)item.transform.parent && item.transform.parent.TryGetComponent<Limb>(out var l) && item.battery.hasCharge) {
                        if (Util.IsBodyLocal(l.body)) {
                            PlayerCamera.main.zoomTime = 0.5f;
                        } else {
                            PlayerCamera.main.zoomTime = last_zoomtime;
                        }
                    }
                    break;
                }
                case "rangefinder":{
                    if ((bool)item.transform.parent && item.transform.parent.TryGetComponent<InventorySlot>(out var component) && (component == component.body.slots[0] || component == component.body.slots[1])) {
                        float z = Vector2.SignedAngle(Vector2.right, (component.body.targetLookPos - __instance.transform.position).normalized);
                        item.transform.eulerAngles = new Vector3(0f, 0f, z);
                    }
                    break;
                }

                // so that flashlights turn properly for remote players 
                case "emergencylight":
                case "flashlight": {
                    if ((bool)item.transform.parent && item.transform.parent.TryGetComponent<InventorySlot>(out var component) && (component == component.body.slots[0] || component == component.body.slots[1])) {
                        float num2 = Vector2.SignedAngle(Vector2.right, (component.body.targetLookPos - item.transform.position).normalized);
                        item.transform.eulerAngles = new Vector3(0f, 0f, num2 - 90f);
                    }
                    // stock clears the mode when charge reads empty. the click already picked one,
                    // and the host copy of that mode can still be a snapshot behind.
                    SyncInfo lightSi;
                    byte held;
                    if (ItemSync.TryGetSyncInfo(item, out lightSi) && Body_UseItemInHand_MultiplayerPatch.HeldState(lightSi.syncId, out held)) {
                        __instance.state = held;
                        if (__instance.transform.childCount > 0) {
                            Light2D beam = __instance.transform.GetChild(0).GetComponent<Light2D>();
                            if (beam is not null) {
                                beam.intensity = held switch {
                                    1 => 1f,
                                    2 => 0.5f,
                                    3 => (((int)(Time.time * 18f) % 2) == 0) ? 1f : 0f,
                                    _ => 0f,
                                };
                            }
                        }
                    }
                    break;
                }

            }
        }
    }
}


[HarmonyPatch(typeof(Item), nameof(Item.Awake))]
static class Item_Awake_MultiplayerPatch {
    static void Prefix(Item __instance) {

    }
}



[HarmonyPatch(typeof(Item), nameof(Item.Start))]
static class Item_Start_MultiplayerPatch {
    static void Postfix(Item __instance)  {
        if (!Net.IsRunning) return;

        if (__instance.id == "campfire") {
            __instance.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }

        bool watch = EvidenceBag.WatchingItem(__instance);
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            var np = ScavPlayer.GetDistanceToNearestLivingPlayer(__instance.transform.position);

            if (np.Item2 < 64f * 64f) {
                NetObjectRegistry.NewGO(__instance.gameObject);
            }
        }
        // craft parents the result first and registers it here, a frame later. the menu refresh during the craft ran before this id existed.
        if (__instance != null)
            CraftCommit.NoteCarriedSync(__instance);
        if (watch)
            EvidenceBag.AfterItemStart(__instance);
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.Update))]
public static class Item_Update_MultiplayerPatch {

    // give @a bigpack foliagebag foliagebag pizzaslice pistol dynamite pizzaslice pistol dynamite pizzaslice pistol dynamite pizzaslice pistol dynamite
    // spawn pizzaslice



    static bool Prefix(Item __instance) {
#if !RELEASE
        if (Net.IsRunning) {
            if (__instance.affect == null) {
                log.error($"AFFECT IS NULL !!!  {__instance}");
            }
        }
#endif
        ItemSync.SpentDestroy(__instance);
        // the host already freed this id. a cleared go made OnDestroy miss the mesh, and the
        // condition early-out below would keep that closed fruit in the slot.
        if (Net.IsClient && __instance != null && NetObjectRegistry.TryGetSyncInfo(__instance, out SyncInfo deadSi) && deadSi.IsDestroyed) {
            UnityEngine.Object.Destroy(__instance.gameObject);
            return false;
        }
        // stock destroys the mesh on this machine too. the id is still in the slot, so the bag gets built again.
        if (Net.IsClient && ItemSync.DyingContainer(__instance)) {
            ItemSync.ParentTestChest("chest spill client", __instance, __instance.gameObject);
            if (__instance.TryGetComponent(out Container box))
                box.ContainerBroke();
            return false;
        }
        // the host still has a sliver. deleting the mesh here makes the next packet build it again.
        if (Net.IsClient && (__instance.Stats is not null) && __instance.Stats.destroyAtZeroCondition && (__instance.condition <= 0f) && NetObjectRegistry.IsRegistered(__instance.gameObject)) {
            return false;
        }
        return true;
    }

    static void Postfix(Item __instance) {
        if (__instance != null)
            ChunkSleep.Watch(__instance);
        if (__instance != null)
            BagSleep.Note(__instance);
        // lit dynamite keeps its chunk even when it was dropped instead of thrown.
        if (__instance != null && __instance.id == "dynamite" && __instance.TryGetComponent(out CustomItemBehaviour fuse))
        {
            object[] data = fuse.data;
            if (data != null && data.Length > 0 && data[0] is bool lit && lit)
                SimKeep.ArmIfIdle(__instance.gameObject);
        }
        // use actions write the field directly, so this is the net that catches a late subtract
        if (__instance != null && __instance.condition < 0f)
            __instance.condition = 0f;

        if (Net.IsRunning) {
            if (__instance.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker)) {
                if ( tracker.isVisible && 
                    //OG: Item.OnWillRenderObject   V5t2     it was moved here to work outside of hosts view
                    __instance.canGetWet && Mathf.Abs(__instance.transform.position.x) < (float)WorldGeneration.world.halfWidth && Mathf.Abs(__instance.transform.position.y) < (float)WorldGeneration.world.halfHeight && FluidManager.main.HasLiquid(WorldGeneration.world.WorldToBlockPos(__instance.transform.position))
                ) {
                    __instance.wetTime = Time.time;
                }
            }
        }
    }




    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
        var codes = new List<CodeInstruction>(instructions);

#if DEBUG && false
        if (log.verbose) {
            Plugin.log.LogWarning($"TRANSPILER: Item_Update BEFORE !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            for (global::System.Int32 j = 0; j < codes.Count; j++) {
                Plugin.log.LogWarning($"TRANSPILER: Item_Update {j} - {codes[j]}");
            }
            Plugin.log.LogWarning($"TRANSPILER: Item_Update BEFORE !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        }
#endif

        int i = 0;

        while (!(codes[i].ToString().Contains("get_enabled") && codes[i].opcode == OpCodes.Callvirt && codes[i].ToString().Contains("Renderer"))) { // find the end of that chunk finder
            i++;
        }
        i++;
#if DEBUG
        Plugin.log.LogWarning($"TRANSPILER: Item_Update TRANSPILER get_enabled found at {i}"); // should be 9
#endif

        codes.RemoveRange(0, i);

        var newInstructions = new List<CodeInstruction>();

        //ldarg.0
        //call	instance class [UnityEngine.CoreModule]UnityEngine.Transform [UnityEngine.CoreModule]UnityEngine.Component::get_transform()
        //callvirt	instance valuetype [UnityEngine.CoreModule]UnityEngine.Vector3 [UnityEngine.CoreModule]UnityEngine.Transform::get_position()
        //call	valuetype [UnityEngine.CoreModule]UnityEngine.Vector2 [UnityEngine.CoreModule]UnityEngine.Vector2::op_Implicit(valuetype [UnityEngine.CoreModule]UnityEngine.Vector3)
        //call	bool [KrokoshaCasualtiesMP]KrokoshaCasualtiesMP.SharedMain::MPinSimRange(valuetype [UnityEngine.CoreModule]UnityEngine.Vector2)

        newInstructions.Add(new CodeInstruction(OpCodes.Ldarg_0));
        newInstructions.Add(new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(UnityEngine.Component), "get_transform")));
        newInstructions.Add(new CodeInstruction(OpCodes.Callvirt,AccessTools.Method(typeof(UnityEngine.Transform), "get_position")));
        newInstructions.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UnityEngine.Vector2), "op_Implicit", new Type[] { typeof(UnityEngine.Vector3) } )));
        newInstructions.Add(new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SharedMain), nameof(SharedMain.WorldInSimRange))));


#if DEBUG
        if (log.verbose) {
            Plugin.log.LogWarning($"TRANSPILER: Item_Update TRANSPILER 0 {codes[0]}");
            Plugin.log.LogWarning($"TRANSPILER: Item_Update TRANSPILER 1 {codes[1]}");
        }
#endif

        codes.InsertRange(0, newInstructions);

#if DEBUG && false
        if (log.verbose) { 
            Plugin.log.LogWarning($"TRANSPILER: Item_Update !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            for (global::System.Int32 j = 0; j < codes.Count; j++) {
                Plugin.log.LogWarning($"TRANSPILER: Item_Update {j} - {codes[j]}");
            }
            Plugin.log.LogWarning($"TRANSPILER: Item_Update !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        } 
        if (log.verbose) {
            Plugin.log.LogWarning($"TRANSPILER: Item_Update TRANSPILER SUCCESS :)  ");
        }
#endif

        return codes;
    }


}




[HarmonyPatch(typeof(WaterContainerItem), nameof(WaterContainerItem.UpdateCondition))]
static class WaterContainerItem_UpdateCondition_Clamp {
    // condition is fill/capacity. a drink that steps past empty used to store a negative and the host showed it.
    static void Postfix(WaterContainerItem __instance) {
        if (__instance == null)
            return;
        Item item = __instance.GetComponent<Item>();
        if (item != null && item.condition < 0f)
            item.condition = 0f;
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.HandleDecay))]
static class Item_HandleDecay_MultiplayerPatch {
    // copypasted code cuz patcher breaks it for some reason,  i only needed to add the bodygetteroverrider
    static bool Prefix(Item __instance) {
        // the host float is the condition. a client that also rots crosses zero while that float is still above it.
        if (Net.IsClient && (__instance is not null) && NetObjectRegistry.IsRegistered(__instance.gameObject)) {
            return false;
        }

        float decMultiplier = __instance.decayMultiplier * (__instance.isWet ? 6f : 1f) * WorldGeneration.globalDecayRate;
        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.NoDecayWithoutContainerItem) != 0 && __instance.cont.itemCount == 0) {
            decMultiplier *= 0f;
        }

        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.NoDecayWhenNotWorn) != 0 && (!__instance.transform.parent || (bool)__instance.ParentContainer())) {
            decMultiplier *= 0f;
        }

        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.NoDecayWhenStill) != 0 && 
            (!__instance.transform.parent 
                || __instance.GetOrAddComponent<BodyGetterOverrider>().GetBody().rb.velocity.magnitude < 0.5f) // only change is here
        ) {
            decMultiplier *= 0f;
        }

        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.BatteryDecay) != 0) {
            float mult = ((__instance.battery.preset == BatteryItem.BatteryPreset.Large) ? 3f : ((__instance.battery.preset == BatteryItem.BatteryPreset.Medium) ? 1f : 0.5f));
            __instance.battery.DrainCharge(__instance.Stats.rotSpeed * 0.01f * mult * decMultiplier * Time.deltaTime);
        } else {
            __instance.condition -= __instance.Stats.rotSpeed * decMultiplier * Time.deltaTime * 0.01f;
        }

        __instance.condition = Mathf.Clamp01(__instance.condition);
        return false;
    }

}


[HarmonyPatch(typeof(Item), nameof(Item.OnWillRenderObject))]
static class Item_OnWillRenderObject_MultiplayerPatch {
    static bool Prefix(Item __instance) {
        if (!Net.IsRunning) return true;

        // i moved the wetness calculation to Update, cuz items can be out of view

        if (__instance.TryGetComponent<SyncInfoGameObjectTracker>( out var tracker)) {
            tracker.isInSimulationRange = true;
            tracker.isVisible = true;
            return false;
        }
        
        return true;
    }
}








[HarmonyPatch(typeof(Item), nameof(Item.SetupItems))]
public static class Item_SetupItems_Listener {

    public static Dictionary<string, byte> LiquidIdRegistry = new();
    public static string[] LiquidNetIdToId;


    static void Postfix() {
#if !RELEASE && false // dump item information 
        int maxvalue = 0;

        foreach (var i in Item.GlobalItems) {

            var val = i.Value.DefaultValue();
            if (i.Value is LiquidItemInfo) {
                //OG: ItemInfo.GetValue  V5t2
                // 50 is maximum liquids value added to the watercontainer items
                val += 50;
            }
            maxvalue = Math.Max(maxvalue, val);

        }

        Plugin.log.LogWarning("############### Item_SetupItems_ImportantValuesDumper START ###########################");

        Plugin.log.LogWarning($"total item count: {Item.GlobalItems.Count}");

        Plugin.log.LogWarning($"max trader price: {maxvalue}");

        Plugin.log.LogWarning("############### Item_SetupItems_ImportantValuesDumper END #############################\n");


        Plugin.log.LogWarning("############### Item Tools list START ###########################");

        foreach (var i in Item.GlobalItems) {
            if (i.Value.HasTag("tool")) {
                Plugin.log.LogWarning($"Tool: lmb:{i.Value.usableWithLMB}\t{i.Key} ");
            }
        }
        Plugin.log.LogWarning("############### ITEM USABLE LMB:");

        foreach (var i in Item.GlobalItems) {
            if (i.Value.usableWithLMB) {
                Plugin.log.LogWarning($"ULMB: lmb:{i.Value.usableWithLMB}\tistool:{i.Value.HasTag("tool")}\t{i.Key} ");
            }
        }

        Plugin.log.LogWarning("############### Item Tools list END #############################\n");


        Plugin.log.LogWarning("############### Liquids Registry START ###########################");  // V5t4 therea re 49 liquids

        Plugin.log.LogWarning($"total liquid types count: {Liquids.Registry.Count}");

        Plugin.log.LogWarning("############### Liquids Registry END #############################");

#endif
       
        if (Plugin.dump_game_ids) {
            Plugin.log.LogInfo($"\ntotal item count: {Item.GlobalItems.Count}");
            Plugin.log.LogInfo($"############### DUMPING ALL ITEM IDs START ###########################\n{
                (String.Join("\n", Item.GlobalItems.Keys))
            }");

            Plugin.log.LogInfo("############### DUMPING ALL ITEM IDs END #############################\n");
        }




        {
            LiquidIdRegistry.Clear();
            LiquidNetIdToId = new string[Liquids.Registry.Count];
            byte counter = 0;
            foreach (var item in Liquids.Registry) {
                LiquidIdRegistry.Add(item.Key, counter);
                LiquidNetIdToId[counter] = item.Key;
                counter++;
            }

#if !RELEASE
            Plugin.log.LogWarning("Created liquid id to hash dictionary!");
#endif
        }




        // PATCH BODY useAction


        // patch the block placing to be better for multiplayer

        //OG: Item.SetupItems    V6.1
        Item.GlobalItems["scrapmetal"].useAction = delegate (Body body, Item item) {

            if (body.canPlaceBlock && !(item.condition < 0.24f)) {
            } else {
                return; // OG does that
            }

            Vector2 vector = body.transform.position + (body.targetLookPos - body.transform.position).normalized * 5f;
            RaycastHit2D raycastHit2D = Physics2D.Linecast(body.transform.position, vector, LayerMask.GetMask("Ground"));
            if ((bool)raycastHit2D) {
                vector = raycastHit2D.point + raycastHit2D.normal * 0.2f;
            }
            body.armsAnimator.Play("ArmsSwing", -1, 0f);
            var blockpos = WorldGeneration.world.WorldToBlockPos(vector);
            if (WorldGeneration.world.GetBlock(blockpos) <= 0) {
                Sound.Play("scrapmetal", vector);

                if (Util.IsBodyLocal(body)) {

                    WorldGeneration.world.SetBlock(blockpos, 3);
                    item.condition -= 0.25f;

                    if (ItemSync.TryGetSyncInfo(item, out var si)) {

                        var writer = Net.CreateWriter(NetmsgId.SERVER_ItemPlaceBlock);
                        writer.Put(si.syncId);
                        writer.Put(vector);
                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                        // WRITER END;

                    }
                }

            }
        };
        //OG: Item.SetupItems    V5t2
        var og_action = Item.GlobalItems["scaffoldingpack"].useAction;
        Item.GlobalItems["scaffoldingpack"].useAction = delegate (Body body, Item item) {

            if (body.canPlaceBlock ) {
            } else {
                return; // OG does that
            }

            Vector2 vector = body.transform.position + (body.targetLookPos - body.transform.position).normalized * 5f;
            RaycastHit2D raycastHit2D = Physics2D.Linecast(body.transform.position, vector, LayerMask.GetMask("Ground"));
            if ((bool)raycastHit2D) {
                vector = raycastHit2D.point + raycastHit2D.normal * 0.2f;
            }
            body.armsAnimator.Play("ArmsSwing", -1, 0f);
            var blockpos = WorldGeneration.world.WorldToBlockPos(vector);
            if (WorldGeneration.world.GetBlock(blockpos) <= 0) {
                Sound.Play("scrapmetal", vector);

                if (Util.IsBodyLocal(body)) {

                    WorldGeneration.world.SetBlock(blockpos, 21);
                    item.condition -= 0.01f;
                    if (item.condition <= 0f) {
                        body.attackCooldown = 0.5f;
                    }

                    if (ItemSync.TryGetSyncInfo(item, out var si)) {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_ItemPlaceBlock);
                        writer.Put(si.syncId);
                        writer.Put(vector);
                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                        // WRITER END;
                    }
                }
            }
        };



        //OG: Item.SetupItems       V501
        Item.GlobalItems["mp3player"].useAction = delegate (Body body, Item item) {
            item.battery.DrainCharge(0.005f);
            if (Util.IsBodyLocal(body) && item.condition > 0) {
                MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player = item;
                Utils.Create("Special/MP3SongSelect", PlayerCamera.main.mainCanvas.transform);
                if (PlayerCamera.main.radialOpen) PlayerCamera.main.radialOpen = false;
            } else {
                Util.PlayWorldSoundOnScreenIfInRange("flashlighttoggle", item.transform.position, 0.7f, 1);
            }
        };
        ReplaceItemUseAction("epda", (body,item,ogAction) => {
            if (body.IsBodyLocal()) {
                ogAction(body, item);
            } else {
                if (item.condition > 0f) {
                    item.battery.DrainCharge(0.126f);
                    var epda = item.GetComponent<EPdaScript>();

                    //OG: EPdaScript.Use V7.0.1
                    if (
                        (!body.IsMindwiped())
                    ) {
                        if (body.TryGetNetBody(out var nb)) {
                            //TODO_FigureOutPerPlayerHasBeenReadSaveableProperty
                        } else {
                            if (!epda.hasBeenRead) {
                                epda.hasBeenRead = true;
                                body.skills.AddExp(2, 35f);
                            }
                        }
                        var data = item.GetComponent<AnyDataContainerComponent>();
                        if (data.WasntTouched("hasBeenRead" + body.GetInstanceID())) {
                            epda.hasBeenRead = true;
                            body.skills.AddExp(2, 35f);
                        }
                    }

                    //MP
                    Util.PlayWorldSoundOnScreenIfInRange("flashlighttoggle", item.transform.position, 0.7f, 1);
                }
            }
        });


        //OG: Item.SetupItems       V7.0.1
        Item.GlobalItems["handcrank"].useAction = delegate (Body body, Item item) {
            if (Util.IsBodyLocal(body)) {
                MinigameBase.main.StartMinigame(new HandCrankMinigame(), item);
            }
        };


        // patch ammo shit 
        ItemInfo.Use improved_ammo_use_lamda = delegate (Body body, Item item) {
            var ammo = item.GetComponent<AmmoScript>();
            //OG: AmmoScript.UnloadRound  V5t4
            if (ammo.rounds > 0) {
                // both sides run the use. only the host spawns the round, or the client grows a second one.
                if (!Net.IsRunning || Net.IsServer) {
                    GameObject gameObject = Utils.Create(AmmoScript.AmmoTypeToItem(ammo.ammoType), ammo.transform.position, 0);
                    body.AutoPickUpItem(gameObject.GetComponent<Item>());
                }
                if (!Net.IsRunning || Net.IsServer || Util.IsBodyLocal(body))
                    ammo.rounds--;
                Sound.Play("gunloadshell", item.transform.position);
            }
        };

        Item.GlobalItems["riflemagazine"].useAction = improved_ammo_use_lamda;
        Item.GlobalItems["smallmagazine"].useAction = improved_ammo_use_lamda;
        Item.GlobalItems["boxof12gauge"].useAction = improved_ammo_use_lamda;


        // PATCH LIMB useLimbAction


        // patch items that use playercamera for shit, we dont need that


        //OG: Item.SetupItems    V5t2
        Item.GlobalItems["roselight"].useLimbAction = delegate (Limb limb, Item item) {
            item.condition = 0f;
            if (limb.infected) {
                PlayerCamera.main.showInfection[Array.IndexOf(limb.body.limbs, limb)] = true;
                Sound.Play("goo", limb.transform.position);
            }
        };

        //OG: Item.SetupItems       V6.1
        Item.GlobalItems["blueprint"].useAction = delegate(Body body, Item item) {
            item.condition = 0f;
            body.skills.AddExp(2, 30f);
            var recipeindex = item.GetComponent<BlueprintScript>().recipeIndex;
            Recipe recipe = Recipes.recipes[recipeindex];
            if (Net.IsRunning) {
                if (Net.IsServer) {
                    recipe.INT = 0;
                    string other = Locale.GetOther("learnedrecipe");
                    other = other.Replace("r1", Locale.GetItem(recipe.simpleName));
                    PlayerCamera.main.DoAlert(other);

                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_BlueprintThing, (knetid)recipeindex, true, false);
                }
            } else {
                recipe.INT = 0;
                string other = Locale.GetOther("learnedrecipe");
                other = other.Replace("r1", Locale.GetItem(recipe.simpleName));
                PlayerCamera.main.DoAlert(other);
            }
            if (Util.IsBodyLocal(body)) {
                PlayerCamera.main.selectedRecipe = recipe.index;
                if (!PlayerCamera.main.craftingPanel.activeSelf) {
                    PlayerCamera.main.OpenCraftScreen();
                } else {
                    PlayerCamera.main.RefreshRecipeList();
                }
            }
            Sound.Play("combine", item.transform.position);
        };

        //OG: Item.SetupItems       V5t2
        Item.GlobalItems["terrainscanner"].useAction = delegate (Body body, Item item) {
            if (item.condition > 0.01f) {
                if (Util.IsBodyLocal(body)) { 
                
                    if ((bool)ScannerScript.main) {
                        UnityEngine.Object.Destroy(ScannerScript.main.gameObject);
                    }
                    (UnityEngine.Object.Instantiate(Resources.Load("Special/ScannerUI"), PlayerCamera.main.mainView.transform) as GameObject).GetComponent<ScannerScript>().pos = item.transform.position;
                
                } else {
                    Util.PlayWorldSoundOnScreenIfInRange("flashlighttoggle", item.transform.position, 0.7f, 1);
                }
                item.battery.DrainCharge(0.02f);
            }
        };

		//OG: Item.SetupItems       V510
		Item.GlobalItems["sleepingbag"].useAction = delegate (Body body, Item item) {
            if (Multiplayer.rules.DisableSleep) {

                if (body.IsBodyLocal()) {
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
                    Util.DoAlert(Lang.Get("sleep_is_disabled"));
                }
                return;
            }

            Body_get_canTakeNap_MultiplayerPatch.force = true;
            if (body.canTakeNap && !body.usingSleepingBag) {
				item.condition -= 0.0501f;
                
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
                body.TakeANap();
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = false;
                if (body.IsBodyLocal()) {
                    
                    if (ScavPlayer.LocalPlayer != null) {
                        if (ScavPlayer.LocalPlayer.TryGetNetBody(out var nb))
                            nb.SetNetHealthSyncIgnoreTime(1f);
                    }
                }

                body.usingSleepingBag = true;
				RaycastHit2D raycastHit2D = Physics2D.Raycast(body.transform.position, Vector2.down, 100f, LayerMask.GetMask("Ground"));
				if ((bool)raycastHit2D) {
					
                    var sleepbag = Utils.Create("Special/sleepingbaguse", raycastHit2D.point + Vector2.up, 0f);
                    GameObject.Destroy(sleepbag,10);
				}
			} else {
                if (body.IsBodyLocal())
				    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
			}
		};

		

		// FLUIDS:

        //No fluid patches are necessary anymore, empty section
        


        if (log.verbose) Plugin.log.LogInfo("Patched items!");
	}

    public delegate void ReplacementItemUse(Body body, Item item, ItemInfo.Use originalUse);

    public static void ReplaceItemUseAction(string itemId, ReplacementItemUse action) {
        if (!Item.GlobalItems.TryGetValue(itemId, out var info)) {
            log.error($"ReplaceItemUseAction: Item \"{itemId}\" not found! {new StackTrace()}");
            return;
        }
        var original = info.useAction;
        info.useAction = (b,i) => {
            try {
                if (action != null) {
                    action(b, i, original);
                } else {
                    original(b, i);
                }
            } catch (Exception e) {
                log.error($"Item Use Action: {e}");
            }
        };
	}


    


    [ClientReceiver((byte)NetmsgId.CLIENT_BlueprintThing)]
    static void ClientReceiver_BlueprintThing(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid recipeindex);

        Recipe recipe = Recipes.recipes[(int)recipeindex];
        recipe.INT = 0;
        string other = Locale.GetOther("learnedrecipe");
        other = other.Replace("r1", Locale.GetItem(recipe.simpleName));
        PlayerCamera.main.DoAlert(other);
    }


    [ServerReceiver((byte)NetmsgId.SERVER_ItemPlaceBlock)] 
    static void Server_ItemPlaceBlock(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid itemsyncid);
        reader.Get(out Vector2 place_pos);


        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) && body.conscious &&
            NetObjectRegistry.TryGetSyncInfo(itemsyncid, out var si) && si.IsItem() && ItemSync.CheckIfBodyReachThisItem(si, body) 
        ) {
            var item = si.item;

            var is_scrapmetal = si.item.id == "scrapmetal";
            var is_scaffoldingpack = si.item.id == "scaffoldingpack";

            if (is_scrapmetal || is_scaffoldingpack) {
                var blockpos = WorldGeneration.world.WorldToBlockPos(place_pos);
                if (WorldGeneration.world.GetBlock(blockpos) <= 0) {
                    if (is_scrapmetal) {
                        WorldGeneration.world.SetBlock(blockpos, 3);
                        item.condition -= 0.25f;
                    } else if (is_scaffoldingpack) {
                        WorldGeneration.world.SetBlock(blockpos, 21);
                        item.condition -= 0.01f;
                    }

                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent((place_pos), $"S: ItemPlaceBlock {si}");
                }

            }


        }
    }

}







[HarmonyPatch(typeof(Body), nameof(Body.DropWearable))]
public static class Body_DropWearable_MultiplayerPatch{
    static bool force_this_shit = false;
    public static void Force(Body __instance, Item item)    {
        force_this_shit = true;
        if (__instance != null)
            __instance.DropWearable(item);
        force_this_shit = false;
        // stock no-ops when GetWearable misses, and the sprite copies stay on the back.
        HeldSeat.Detach(item);
    }

    public static void Postfix(Body __instance, Item item)    {
		HeldSeat.Release(item);
		if (!Net.IsRunning)
			return;

		// forced pickups are only from scripted, or a part of a larget methods, we can ignore force
		if (!force_this_shit && Util.IsWorldGenerated())  {
            var body = __instance;
            if (Util.IsBodyLocal(body) && !ItemSync.ItemCanBeIgnoredForNetwork(item) )   {
                MoveCommit.SubmitLive(item);
            }
        }
    }
}




[HarmonyPatch(typeof(Body), nameof(Body.WearWearable) )]
public static class Body_WearWearable_MultiplayerPatch{
    

    static void Postfix(Body __instance, Item item )  {
        if (!Net.IsRunning)
            return;
        if (item != null && item.TryGetComponent(out BodyGetterOverrider bgo)) { bgo.SetBody(__instance); }

        if (Util.IsWorldGenerated()) {
            var body = __instance;
            if (Util.IsBodyLocal(body) && !ItemSync.ItemCanBeIgnoredForNetwork(item) && body.GetWearable(item.id) == item) {
                MoveCommit.SubmitLive(item);
            }
        }
    }
}


//OG: SwapSlots is called only from PlayerCamera once
[HarmonyPatch(typeof(Body), nameof(Body.SwapSlots) )]
static class Body_SwapSlots_MultiplayerPatch {
    static void Prefix() {
        MoveCommit.Push();
    }
    static void Postfix(Body __instance, int slot1, int slot2) {
        if (!__instance.IsBodyLocal()) {
            MoveCommit.Pop();
            return;
        }
        ItemSync.RecordAndApplyInvState(Util.GetLocalBody());
        MoveCommit.EndSwap(__instance, slot1, slot2);
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
static class Container_UnloadItem_MultiplayerPatch {
    static void Prefix(Container __instance, Item item, Body body ) {
        if (ItemSync.TryGetSyncInfo(item, out var si)) {
			if (Net.IsServer) {
				//NetObjectRegistry.Server_QueueSync(si);
			}
			if (PlayerCamera_TryPerformInventoryAction_MultiplayerPatch.is_inside_TryPerformInventoryAction) {
				ItemSync.ItemContainerChanged.Add(si);
				si.SetIgnoreTimeForRoundTrip(1);
			}
		}
    }

    // bag unload never goes through DropItem, so the click colliders stayed off and the body stayed asleep
    static void Postfix(Item item) {
        HeldSeat.Release(item);
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.LoadItem))]
static class Container_LoadItem_MultiplayerPatch {
    // a joined item keeps its save position while parented to the bag. the 10 unit check then refuses the move.
    static void Prefix(Item item) {
        if (Net.IsRunning)
            ItemSync.SnapOntoContainer(item);
    }

    // the old prefix announced before LoadItem ran, so a refusal still told the host the item moved.
    static void Postfix(Container __instance, Item item) {
        if (!Net.IsRunning || item == null || __instance == null)
            return;
        if (item.transform.parent != __instance.transform)
            return;
        if (!PlayerCamera_TryPerformInventoryAction_MultiplayerPatch.is_inside_TryPerformInventoryAction)
            return;
        if (!ItemSync.TryGetSyncInfo(item, out var si))
            return;
        ItemSync.ItemContainerChanged.Add(si);
        if (log.verbose)
            log.l($"Container.LoadItem {si}");
        si.SetIgnoreTimeForRoundTrip(1);
    }
}



[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPerformInventoryAction))]
public static class PlayerCamera_TryPerformInventoryAction_MultiplayerPatch {

    public static bool is_inside_TryPerformInventoryAction = false;

    static bool Prefix(PlayerCamera __instance, ref bool __result, RaycastResult hit, List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) return true;
        
        if (Util.IsWorldGenerated()) {
            
            if (!hit.gameObject.TryGetComponent(out InvButton component) || !component.Overlaps(uiCasts)) {
                is_inside_TryPerformInventoryAction = false;
                return false;
            }
            if (component.GetItem() == __instance.dragItem) {
                __result = true;
                is_inside_TryPerformInventoryAction = false;
                return false;
            }
            Item item = component.GetItem();
            if ((bool)item && (bool)item.battery) {


                if (ItemSync.TryGetSyncInfo(item, out var si1) && ItemSync.TryGetSyncInfo(__instance.dragItem, out var si2)) { 
                
                    if (
                        __instance.dragItem.Stats.HasTag("tool") &&
                        //OG: BatteryItem.UnloadBattery V5t4
                        item.battery.hasBattery
                    ) {

                        var writer = Net.CreateWriter(NetmsgId.SERVER_ItemBatteryManipulation);

                        writer.Put(false); // unload the battery
                        writer.Put(si1.syncId); // target item
                        writer.Put(si2.syncId); // tool being used

                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);


                        item.battery.UnloadBattery();  // do the original action

                        __result = true;
                        return false;
                    }
                    if (__instance.dragItem.Stats.HasTag("battery") &&
                        //OG: BatteryItem.LoadBattery V5t4
                        !item.battery.hasBattery && 
                        !(__instance.dragItem.Stats.GetType() != typeof(BatteryInfo)) 
                    ) {

                        var writer = Net.CreateWriter(NetmsgId.SERVER_ItemBatteryManipulation);

                        writer.Put(true); // load the battery
                        writer.Put(si1.syncId); // target item
                        writer.Put(si2.syncId); // battery being loaded

                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);


                        item.battery.LoadBattery(__instance.dragItem); // do the original action


                        __result = true;

                        return false;
                    }
                }
            }









            // a bag button used to unload first. LoadItem only parents on success, so a refusal leaves the item where it was.
            if ((bool)item && item.TryGetComponent(out Container dest) && (bool)__instance.dragItem) {
                is_inside_TryPerformInventoryAction = true;
                bool expand = Input.GetKey(KeyBinds.GetBind("expanddesc")) && item != __instance.dragItem;
                if (!expand) {
                    if (dest.CanHoldItem(__instance.dragItem))
                        hit.gameObject.transform.localScale = Vector3.one * 1.5f;
                    else
                        hit.gameObject.transform.localScale = Vector3.one * 0.8f;
                    if (MovedInto(dest, __instance.dragItem))
                        __instance.PlayBackpackSound();
                    __result = true;
                    return false;
                }
                if (Poured(dest, __instance.dragItem)) {
                    __instance.PlayBackpackSound();
                    __result = true;
                    return false;
                }
                // nothing moved. still finish combine and the slot here, so the original unload-first branch does not run.
                __result = FinishSlotOrCombine(__instance, component, item);
                return false;
            }

            is_inside_TryPerformInventoryAction = true;

        }
        return true; // do the original function as intended
    }



    static void Postfix() {
        is_inside_TryPerformInventoryAction = false;
    }

    // true when LoadItem actually took it. a refusal keeps the old parent.
    static bool MovedInto(Container dest, Item item) {
        if (dest == null || item == null)
            return false;
        Transform before = item.transform.parent;
        dest.LoadItem(item);
        return item.transform.parent == dest.transform && before != dest.transform;
    }

    static bool Poured(Container dest, Item bag) {
        var kids = new List<Item>();
        foreach (Transform child in bag.transform) {
            if (child.TryGetComponent(out Item kid))
                kids.Add(kid);
        }
        bool any = false;
        foreach (Item kid in kids) {
            if (MovedInto(dest, kid))
                any = true;
        }
        return any;
    }

    // the tail of TryPerformInventoryAction, after a bag pour that moved nothing.
    static bool FinishSlotOrCombine(PlayerCamera cam, InvButton button, Item under) {
        Item drag = cam.dragItem;
        if ((bool)under && cam.body.CanCombine(under, drag)) {
            cam.body.CombineItems(under, drag);
            return true;
        }
        if (!button.isBody)
            return false;
        if (drag.Stats.wearable && !drag.Stats.wearableCanBeHeld) {
            cam.DoAlert(Locale.GetOther("alertpickupwearable"));
            return true;
        }
        if (cam.body.HoldingItem(button.slot) && cam.body.HoldingItem(drag)) {
            cam.body.SwapSlots(button.slot, cam.body.SlotOf(drag));
            button.transform.localScale = Vector3.one * 1.5f;
        } else {
            if (cam.body.HoldingItem(drag))
                cam.body.DropItem(drag);
            if (cam.body.HoldingItem(button.slot))
                cam.body.DropItem(button.slot);
            cam.body.PickUpItem(drag, button.slot);
            button.transform.localScale = Vector3.one * 1.5f;
        }
        return true;
    }
    


    [ServerReceiver((byte)NetmsgId.SERVER_ItemBatteryManipulation)]
    static void Server_ItemBatteryManipulation(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out bool load);
        reader.Get(out knetid syncid_item_target);
        reader.Get(out knetid syncid_item2);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) &&
            ItemSync.TryGetItemSyncInfo(syncid_item_target, out var item_si) &&
            ItemSync.CheckIfBodyReachThisItem(item_si,pb.body,20,false) &&
            ItemSync.TryGetItemSyncInfo(syncid_item2, out var item_tool_or_battery) &&
            ItemSync.CheckIfBodyReachThisItem(item_tool_or_battery, pb.body, 20, false) 
        ) {

            if (log.verbose) Plugin.log.LogInfo($"ItemBatteryManipulation check success {plr} -> {item_si} + {item_tool_or_battery}");

            var tool = item_tool_or_battery.item;
            var item = item_si.item;
            if (item.battery) { 
            
                if (!load && tool.Stats.HasTag("tool") ) {
                    // i have to do spawning myself, fuck the unnecessary PlayerCamera.main.body everywhere

                    //OG: BatteryItem.UnloadBattery  V7.0.1
                    Sound.Play("batteryinsert", item.transform.position);
                    Item component = (UnityEngine.Object.Instantiate(Resources.Load(item.battery.batteryType), item.transform.position, Quaternion.identity) as GameObject).GetComponent<Item>();
                    component.condition = item.condition;
                    // stock unload copies the star onto the battery it spawns. skipSpawn drops that.
                    component.favourited = item.battery.batteryWasFavourited;
                    pb.body.AutoPickUpItem(component);


                    item.battery.UnloadBattery(true);  // do the original action but force it to not spawn the battery

                } else if (load && tool.Stats.HasTag("battery") ) {
                    item.battery.LoadBattery(tool); // do the original action
                }

                //NetObjectRegistry.Server_QueueSync(item_si);

            }

            return;
        }
        log.serverdeny($"ItemBatteryManipulation denied for: {DebugHelp.GetPlayerFullDebugString(clientId)}  item1:{syncid_item_target}  item2:{syncid_item2}");        
    }

}

// the favourite key flips the copy on screen. the host item is the one that gets saved.
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleWhileDragging))]
static class PlayerCamera_Favourite_MultiplayerPatch {
    static Item watched;
    static bool before;

    static void Prefix(List<RaycastResult> uiCasts) {
        watched = null;
        if (!Net.IsRunning || uiCasts == null)
            return;
        if (!Input.GetKeyDown(KeyBinds.GetBind("favourite")))
            return;
        for (int i = 0; i < uiCasts.Count; i++) {
            if (!uiCasts[i].gameObject.TryGetComponent(out InvButton button) || !button.Overlaps(uiCasts))
                continue;
            Item item = button.GetItem();
            if (item != null) {
                watched = item;
                before = item.favourited;
            }
            break;
        }
    }

    static void Postfix() {
        if (!Net.IsRunning || watched == null || watched.favourited == before)
            return;
        if (!ItemSync.TryGetSyncInfo(watched, out SyncInfo si) || si == null)
            return;
        var writer = Net.CreateWriter(NetmsgId.SERVER_ItemFavourited);
        writer.Put(si.syncId);
        writer.Put(watched.favourited);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        if (Net.IsClient)
            ItemSync.PredictFavourite(si.syncId, watched.favourited);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemFavourited)]
    static void Server_ItemFavourited(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid id);
        reader.Get(out bool want);
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body))
            return;
        if (!ItemSync.TryGetItemSyncInfo(id, out SyncInfo si) || si == null || si.item == null)
            return;
        bool ok = body != null && ItemSync.CheckIfBodyReachThisItem(si, body);
        if (ok)
            si.item.favourited = want;
        else if (si.item.favourited == want)
            si.item.favourited = !want;
        si.favourited.Value = si.item.favourited;
        if (!ok && plr != null && !plr.IsLocal)
            ItemSync.EchoFavourite(plr, si);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemFavourited, false)]
    static void Client_ItemFavourited(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid id);
        reader.Get(out bool value);
        ItemSync.ClearFavouritePending(id);
        if (ItemSync.TryGetItemSyncInfo(id, out SyncInfo si) && si != null && si.item != null)
            si.item.favourited = value;
    }
}




[HarmonyPatch(typeof(Body), nameof(Body.CombineItems))]
public static class Body_CombineItems_MultiplayerPatch {



    //OG: Body.CombineItems V5t2  but i just commented out the stupidass playercamera, and also it returns false if it was water container
    public static bool Body_CombineItems(Body combinerguy ,Item it1, Item it2) {
        if (!combinerguy.CanCombine(it1, it2)) {
            return true;
        }
        if ((bool)it1.GetComponent<GunScript>() && (bool)it2.GetComponent<AmmoScript>()) {
            it1.GetComponent<GunScript>().LoadMag(it2.GetComponent<AmmoScript>());
            return true;
        }
        if ((bool)it1.GetComponent<AmmoScript>() && (bool)it2.GetComponent<AmmoScript>()) {
            it1.GetComponent<AmmoScript>().LoadRound(it2.GetComponent<AmmoScript>());
            return true;
        }
        if (it1.TryGetComponent<WaterContainerItem>(out var component) && it2.TryGetComponent<WaterContainerItem>(out var component2)) {
            if (component.SpaceLeft > 0f && it1.id != "craftingbottle") {
                if (Util.IsBodyLocal(combinerguy)) { 
                    PlayerCamera.main.StartLiquidTransfer(component, component2);
                }
            }
            return false;
        }
        float a = 1f - it1.condition;
        it1.condition += Mathf.Min(a, it2.condition);
        it2.condition -= Mathf.Min(a, it2.condition);
        combinerguy.CreateCloudMini(it1.transform.position);
        Util.PlayWorldSoundOnScreenIfInRange("combine", combinerguy.transform.position);
        if (it1.TryGetParentContainer(out var cont) && (cont.AboveHoldingWeight() || it1.totalWeight > cont.maxWeightPerItem)) {
            cont.UnloadItem(it1);
        }
        return true;
    }



    public static bool DoTheCombineAnnouncement(bool isfluid, float ml, Body body, Item it1, Item it2) {

        if (ItemSync.ItemCanBeIgnoredForNetwork(it1) || ItemSync.ItemCanBeIgnoredForNetwork(it2)) {
#if !RELEASE
            if (log.verbose) Plugin.log.LogInfo($"DoTheCombineAnnouncement: the item is ignored for network: {ItemSync.ItemCanBeIgnoredForNetwork(it1)}  {ItemSync.ItemCanBeIgnoredForNetwork(it2)}  ");
#endif
            // just skip it all, idk what we're combining
            return true;
        }


        if (ItemSync.SyncRegistry.TryGetValue(it1.gameObject, out var isi) && ItemSync.SyncRegistry.TryGetValue(it2.gameObject, out var isi2)) {
            isi.SetIgnoreTimeForRoundTrip();
            isi2.SetIgnoreTimeForRoundTrip();

            var didcombine = isfluid || Body_CombineItems(body, it1, it2);

            if (didcombine) {
                var msgname = isfluid ? NetmsgId.SERVER_CombineLiquidItems : NetmsgId.SERVER_BodyCombineItems;

                var writer = Net.CreateWriter(msgname);

                writer.Put(isi.syncId);
                writer.Put(isi2.syncId);
                if (isfluid) {
                    writer.Put(ml);
                }

                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                if (log.verbose) Plugin.log.LogInfo($"BODY_COMBINE_OVERRIDE sending {msgname} {isi} x {isi2}  ");

                // WRITER END;
            }

        } else {
            Plugin.log.LogWarning($"Attempted to combine unregistered items {it1.id} and {it2.id}, aborting.");

            // the thing wasnt registered yet
            NetObjectRegistry.AlertObjectNotRegistered();
            NetObjectRegistry.NewGO(it1.gameObject);
            NetObjectRegistry.NewGO(it2.gameObject);
            return false;
        }
        return false;
    }


    public static bool Prefix(Body __instance, Item it1, Item it2) {
        if (Net.IsRunning && Util.IsWorldGenerated()) {

            if (!Util.IsBodyLocal(__instance)) {
                Body_CombineItems(__instance, it1, it2);    
                return false;
            }

            if (__instance.CanCombine(it1, it2)) {
                return DoTheCombineAnnouncement(false,0, __instance, it1, it2);
            }

            return false; // i reimplemented dat shi, only my version of the function runs
        }
        return true;
    }
}



[HarmonyPatch(typeof(LiquidTransfer), nameof(LiquidTransfer.Finish))]
public static class LiquidTransfer_Finish_MultiplayerPatch {
    public static void Postfix(LiquidTransfer __instance) {
        // forced pickups are only from scripted, or a part of a larget methods, we can ignore force
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            if (Util.IsBodyLocal(PlayerCamera.main.body)) {
                Body_CombineItems_MultiplayerPatch.DoTheCombineAnnouncement(true, __instance.ml, PlayerCamera.main.body, __instance.transferTo.GetComponent<Item>(), __instance.transferFrom.GetComponent<Item>());
            }
        }
    }
}



[HarmonyPatch(typeof(Body),  nameof(Body.SwitchHands) )]
static class Body_SwitchHands_MultiplayerPatch{
    static void Prefix() {
        MoveCommit.Push();
    }
    static void Postfix(Body __instance) {
        if (!__instance.IsBodyLocal()) {
            MoveCommit.Pop();
            return;
        }
        ItemSync.RecordAndApplyInvState(Util.GetLocalBody());
        MoveCommit.EndHands(__instance);
    }
}



[HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
public static class Body_PickUpItem_MultiplayerPatch {
    static void Prefix(Body __instance) {
    }
    public static void Postfix(Body __instance, Item item, int slot, bool force) {
        var body = __instance;
        if (body.IsBodyLocal()) {
            if (PlayerCamera.main.craftingPanel.activeSelf) {
                PlayerCamera.main.RefreshRecipeList();
			}
		}

        if (!Net.IsRunning) return;
        if (SharedMain.LocalWorldIsGenerated) {
            if (Net.IsServer) {
                
            } else {
                if (body.IsBodyLocal()) {
                    if (!ItemSync.ItemCanBeIgnoredForNetwork(item) && body.HoldingItem(item))
                        MoveCommit.SubmitLive(item);
                }
            }
        }
        

        if (item.TryGetComponent(out BodyGetterOverrider bgo)) { bgo.SetBody(__instance); }
	}
}





[HarmonyPatch(typeof(Body), nameof(Body.ThrowItem) )]
static class Body_ThrowItem_MultiplayerPatch{
    static Item _thrown;

    static void Prefix(Body __instance, float force, ref SyncInfo __state)   {
        __state = null;
        _thrown = __instance.GetItem(__instance.handSlot);
        MoveCommit.BeginThrow();
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            var body = __instance;
            var item = _thrown;
            // its me 
            if (Util.IsBodyLocal(body) && item != null ) {
                if (NetObjectRegistry.TryGetSyncInfo(item, out __state)) {
                    var i = Array.IndexOf(ItemSync.Client_last_inventory_state, __state);
                    if (i != -1) {
                        ItemSync.Client_last_inventory_state[i] = null;
                    }

                    __state.SetIgnoreTimeForRoundTrip(1);

                    var writer = Net.CreateWriter(NetmsgId.SERVER_ThrowItem);
                    writer.Put(__state.syncId);
                    writer.Put(force);
                    if (log.verbose) log.l($"CLIENT: requesting ThrowItem: {__state} ");
                    Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

                    // WRITER END;

                }
            }
        }
    }
    static void Postfix() {
        MoveCommit.EndThrow();
        Item thrown = _thrown;
        _thrown = null;
        // still in the hand means the throw did not let go. a loose one keeps the chunk it flies into.
        if (thrown != null && thrown.transform.parent == null)
            SimKeep.Arm(thrown.gameObject);
    }
}



[HarmonyPatch(typeof(Body), nameof(Body.DropItem) , new Type[] { typeof(Item) })]
static class Body_DropItem_MultiplayerPatch {
    // stock craft spend drops on the camera body. that body is the host, and it is not holding the ingredient.
    static void Prefix(Body __instance, Item item, out bool __state) {
        __state = false;
        try {
            __state = __instance != null && item != null && __instance.HoldingItem(item);
        } catch (Exception) { }
    }
	static void Postfix(Body __instance, Item item, bool __state) {
        SlotHold.Release(__instance, item);
        HeldSeat.Release(item);
        // a body that was not holding it did not drop it. publishing that as this player's move checks the wrong person.
        if (__state && __instance != null && __instance.IsBodyLocal())
            MoveCommit.SubmitLive(item);
    }
}







// this is where all the container load unload happens
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleReleaseDragging))]
public static class PlayerCamera_HandleReleaseDragging_MultiplayerPatch {
    static ItemSync.ItemsContainerInfo last_ici = null;

    public static void Prefix(PlayerCamera __instance, ref SyncInfo __state) {
        __state = null;
        if (Net.IsRunning)
            MoveCommit.Push();
        if (Net.IsRunning) {
            var pc = __instance;
            var body = pc.body;
           
            if (pc.dragItem) {
                if (ItemSync.TryGetSyncInfo(pc.dragItem, out var si)) {

                    last_ici = ItemSync.ItemGetContainerInfo(pc.dragItem);
                    __state = si;
                } else {
                    // it will check if the item is ignored for network
                    NetObjectRegistry.Client_DeleteUnregisteredSyncableObject(pc.dragItem.gameObject);
                }
            }
        }
    }
    public static void Postfix(PlayerCamera __instance, ref SyncInfo __state) {
        if (__state != null) {
            var pc = __instance;
            var body = pc.body;
            
            var newici = ItemSync.ItemGetContainerInfo(__state.item); 

            if (newici.JustContainerChanged(last_ici) && newici.inv_slot == null) {
                ItemSync.ItemContainerChanged.Add(__state);
            }

            // the slot packet does not name the bag. keep it so a denial can put the item back.
            if (newici.is_in_locplr_surface_inv && last_ici != null && last_ici.container != null)
                ItemSync.RememberPickupBag(__state.syncId, last_ici.container);
            else
                ItemSync.RememberPickupBag(__state.syncId, null);
            MoveCommit.EndDrag(__state.item);
        } else if (Net.IsRunning) {
            MoveCommit.EndDrag(null);
        }
    }
}

// a missed slot used to take the item out of the bag before anything accepted it.
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPerformWorldActions))]
static class PlayerCamera_TryPerformWorldActions_MultiplayerPatch {
    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning)
            return true;
        Item drag = __instance.dragItem;
        if (drag == null)
            return false;

        Container source = null;
        bool inBag = drag.transform.parent != null && drag.transform.parent.TryGetComponent(out source);
        float dragged = Vector2.Distance(Input.mousePosition, __instance.clickPos);
        if (__instance.body.HoldingItem(drag) && !__instance.woundView.activeSelf && dragged > 10f)
            __instance.body.DropItem(drag);
        bool onLimb = drag.transform.parent != null && drag.transform.parent.GetComponent<Limb>() != null;
        if (drag.Stats != null && drag.Stats.wearable && dragged > 10f && (onLimb || (bool)__instance.body.GetWearable(drag.id))) {
            __instance.body.DropWearable(drag);
            // GetWearable can miss a bag that is still parented to the back. the copies would stay and the rigidbody would fall through.
            if (drag.transform.parent != null && drag.transform.parent.GetComponent<Limb>() != null)
                HeldSeat.Detach(drag);
        }

        // still over the inventory. the slot did not take it, so it stays in the bag.
        if (UIUtil.IsPointerOverUIElement())
            return false;

        if (inBag && drag.transform.parent != source.transform)
            inBag = false;

        Collider2D hit = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Item"));
        if ((bool)hit && hit.TryGetComponent(out Container dest) && !drag.TryGetComponent<Container>(out _)) {
            Transform before = drag.transform.parent;
            dest.LoadItem(drag);
            if (drag.transform.parent == dest.transform && before != dest.transform) {
                __instance.PlayBackpackSound();
                return false;
            }
            // the bag under the cursor refused. do not orphan it on the ground.
            if (inBag && drag.transform.parent == source.transform)
                return false;
        }

        if (inBag && source != null && drag.transform.parent == source.transform) {
            source.UnloadItem(drag);
            __instance.PlayBackpackSound();
        }
        return false;
    }
}








internal class ItemSync : CasualtiesTogetherSingleton {

    public static bool verbose => Multiplayer.verbose;

    public static bool CustomItemBehaviourStateCanBeJustByte(Item item) {
        return item.id == "flashlight" || item.id == "emergencylight";
    }

    public static Dictionary<GameObject, SyncInfo> SyncRegistry => NetObjectRegistry.SyncRegistry;
    public static Dictionary<knetid, SyncInfo> NetIdToSyncInfoDict => NetObjectRegistry.NetIdToSyncInfoDict;

    public static HashSet<SyncInfo> ItemContainerChanged = new();

    // q drops then picks back up. the loose byte in the middle rubberbands it.
    internal static int SuppressContainerWrite;

    // on while a pickup is dumping something that already had a parent. plain grabs stay quiet.
    internal static bool ParentTestSwap;

    static readonly HashSet<int> ParentTestChestSeen = new();
    static readonly Dictionary<int, int> ParentTestHandStamp = new();

    // logs even with verbose off.
    internal static void ParentTest(string line) {
        if (!Together.log.verbose) return;
        try {
            string side = Net.IsServer ? "host" : "client";
            Plugin.log.LogInfo("[parentTest] " + side + " " + line);
        } catch (Exception) {
        }
    }

    internal static string ParentTestItem(Item item) {
        try {
            if (item == null)
                return "null";
            string parent = item.transform != null && item.transform.parent != null ? item.transform.parent.name : "none";
            bool sim = item.rb != null && item.rb.simulated;
            ushort id = 0;
            byte data = 255;
            ushort cnet = 0;
            if (item.TryGetComponent(out SyncInfoGameObjectTracker tracker) && tracker.syncInfo != null) {
                id = tracker.syncInfo.syncId.id;
                data = tracker.syncInfo.container_data1.Value;
                cnet = tracker.syncInfo.container_netId.Value.id;
            }
            return item.id + "#" + id + " byte=" + data + " cnet=" + cnet + " parent=" + parent + " sim=" + sim;
        } catch (Exception) {
            return item != null ? item.name : "null";
        }
    }

    static int ParentTestKey(int a, int b, int c, int d) {
        unchecked {
            int h = a;
            h = (h * 397) ^ b;
            h = (h * 397) ^ c;
            h = (h * 397) ^ d;
            return h;
        }
    }

    // same chest skip is one line. log again if the parent or sim changes.
    internal static void ParentTestChest(string what, Item item, UnityEngine.Object bag) {
        try {
            int itemId = item != null ? item.GetInstanceID() : 0;
            int parentId = item != null && item.transform.parent != null ? item.transform.parent.GetInstanceID() : 0;
            int bagId = bag != null ? bag.GetInstanceID() : 0;
            int sim = item != null && item.rb != null && item.rb.simulated ? 1 : 0;
            int key = ParentTestKey(what.GetHashCode(), itemId, parentId ^ bagId, sim);
            if (ParentTestChestSeen.Count > 4096)
                ParentTestChestSeen.Clear();
            if (!ParentTestChestSeen.Add(key))
                return;
            string bagName = bag != null ? bag.name : "none";
            ParentTest(what + " " + ParentTestItem(item) + " bag=" + bagName);
        } catch (Exception) {
        }
    }

    // hand or hotbar. byte 0 while its still in the slot is the rubberband. a real drop logs the same thing.
    internal static void ParentTestHand(Item item) {
        try {
            if (item == null || item.transform == null)
                return;
            if (!item.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
                return;
            Transform parent = item.transform.parent;
            bool inSlot = parent != null && parent.TryGetComponent(out InventorySlot _);
            byte data = tracker.syncInfo.container_data1.Value;
            if (!inSlot && data < 200)
                return;
            ushort cnet = tracker.syncInfo.container_netId.Value.id;
            int parentId = parent != null ? parent.GetInstanceID() : 0;
            int sim = item.rb != null && item.rb.simulated ? 1 : 0;
            int stamp = ParentTestKey(data, cnet, parentId, sim);
            int key = item.GetInstanceID();
            if (ParentTestHandStamp.TryGetValue(key, out int prev) && prev == stamp)
                return;
            if (ParentTestHandStamp.Count > 4096)
                ParentTestHandStamp.Clear();
            ParentTestHandStamp[key] = stamp;
            string tag = inSlot && data == 0 ? "swap apply loose" : "swap seen";
            ParentTest(tag + " " + ParentTestItem(item));
        } catch (Exception) {
        }
    }

    static bool ParentTestWatch(Item item, byte slot, Body body) {
        if (item == null || body == null)
            return false;
        if (item.transform.parent != null)
            return true;
        Item occ = body.GetItem(slot);
        return occ != null && occ != item;
    }

    public class ItemsContainerInfo {
        public bool has_container ;
        public bool is_netbody;
        public bool is_in_locplr_surface_inv;
        public bool is_in_surface_inv;
        public knetid knetid;
        // Version of knetid at the time of this read. 0 when there is no container.
        public byte containerVer;

        public Item item;

        public bool is_wearing;
        public Limb limb;

        public knetid body_netid;
        public InventorySlot inv_slot;
        public byte inv_slot_number;
        public ScavPlayer plr;
         
        public Container container;
        public SyncInfo container_si;

        public NetBody bodynpc;

        public byte containerExtraData1;

        public bool JustContainerChanged(ItemsContainerInfo otherici) {

            return container != otherici.container;
        }
        public bool Equals(ItemsContainerInfo otherici) {

            return has_container == otherici.has_container  &&
                is_netbody == otherici.is_netbody &&
                item == otherici.item &&
                is_wearing == otherici.is_wearing &&
                limb == otherici.limb &&
                body_netid == otherici.body_netid &&
                inv_slot_number == otherici.inv_slot_number &&
                container == otherici.container;
        }


        public override string ToString() {
            var str =  "ICI-";
            if (!has_container) {
                str += "NONE";
            } else {
                if (is_wearing) {
                    str += $"WEAR-({bodynpc})";
                } else if (container_si != null) {
                    str += $"CONT-{container_si}";
                    if (bodynpc != null) {
                        str += $"-({bodynpc.ToString()})";
                    }
                } else if (inv_slot != null) {
                    str += $"INV-{inv_slot_number.ToString()}-({bodynpc})";
                }  
            }
            return str;
        }

    }

    public static ItemsContainerInfo ItemGetContainerInfo(Item item) {

        var ici = new ItemsContainerInfo();
        
        ici.item = item;
        try {

            var p = item.transform.parent;
            if ((bool)p) {
                if (item.Stats.wearable && p.TryGetComponent(out Limb limbslot)) {
                    ici.has_container = true;
                    ici.is_netbody = true;
                    ici.is_wearing = true;
                    ici.is_in_surface_inv = true;

                    ici.limb = limbslot;

                    if (limbslot.body.TryGetNetBody(out ici.bodynpc)) {
                        ici.plr = ici.bodynpc.plr;
                        ici.body_netid = ici.bodynpc.netId;
                        ici.knetid = ici.body_netid;
                        ici.containerVer = ContainerLink.VersionOf(ici.bodynpc);
                    } else {
                        Plugin.log.LogError($"Item is being worn, but NetBody was not found from body wtf, item: {item.name}\n{new StackTrace()}");
                    }
                } else if (p.TryGetComponent(out InventorySlot invslot)) {
                    ici.has_container = true;
                    ici.is_netbody = true;

                    ici.inv_slot = invslot;
                    ici.inv_slot_number = (byte)invslot.slot;

                    ici.is_in_surface_inv = true;
                    if (invslot.body.IsBodyLocal()) {
                        ici.is_in_locplr_surface_inv = true;
                    }


                    if (invslot.body.TryGetNetBody(out ici.bodynpc)) {
                        if (ici.bodynpc.syncBody == null) {
                            return new ItemsContainerInfo();
                        }
                        ici.plr = ici.bodynpc.plr;
                        ici.body_netid = ici.bodynpc.netId;
                        ici.knetid = ici.body_netid;
                        ici.containerVer = ContainerLink.VersionOf(ici.bodynpc);
                    } else {
                        Plugin.log.LogError($"Item is in inventory slot, but NetBody was not found from body wtf, item: {item.name}\n{new StackTrace()}");
                    }

                } else if (p.TryGetComponent<Container>(out var cont)) {
                    ici.has_container = true;

                    // GetComponentInParent ignores a turned off item, so a slept bag
                    // child looked like it was in nobody's inventory.
                    ici.bodynpc = ParentBody(item.transform);
                    if (ici.bodynpc == null) {
                        ici.is_netbody = false;
                    } else {
                        if (ici.bodynpc.syncBody == null) {
                            return new ItemsContainerInfo();
                        }
                        ici.is_netbody = true;
                        ici.body_netid = ici.bodynpc.netId;
                        ici.plr = ici.bodynpc.plr;
                    }

                    ici.container = cont;

                    if (NetObjectRegistry.TryGetSyncInfoOrRegister(p, out var isi)) {
                        ici.container_si = isi;
                        ici.knetid = isi.syncId;
                        ici.containerVer = isi.Version;
                    }

                }
            }

            {
                if (ici.is_wearing) {
                    ici.containerExtraData1 = (byte)(100);
                } else if (ici.is_in_surface_inv) {
                    ici.containerExtraData1 = (byte)(ici.inv_slot_number + 200);
                } else if (ici.has_container) {
                    ici.containerExtraData1 = 1;
                } else {
                    ici.containerExtraData1 = 0;
                }
            }
        } catch (Exception e) {
            log.error($"ItemGetContainerInfo: {e.Message} : {ici.ToString()}\n{new StackTrace()}");
        }

        return ici;
    }

    public static ItemsContainerInfo ItemGetContainerInfo(GameObject go) {
        return ItemGetContainerInfo(go.GetComponent<Item>());
    }


    public static void Body_ForceWearWearable(Body body, Item item) {

        Limb limb = body.LimbByName(item.Stats.desiredWearLimb);

        if (limb.dismembered) {
            return;
        }

        Item wearableBySlotID = body.GetWearableBySlotID(item.Stats.wearSlotId);
        if (wearableBySlotID != null ) {
            if (wearableBySlotID == item) { //its already equipped
                return;
            }
            body.DropWearable(wearableBySlotID);
        }
        if (item.transform.parent != null) {
            ItemSync.SafeUnloadItem(item);
        }

        //OG: Body.WearWearable V7.0-pre1
        if (item.TryGetParentContainer(out var cont)) {
            cont.UnloadItem(item);
            if (body.IsBodyLocal())
                PlayerCamera.main.PlayBackpackSound();
        }
        item.rb.simulated = false;
        item.GetComponent<SpriteRenderer>().sortingOrder = limb.GetComponent<SpriteRenderer>().sortingOrder + item.Stats.wearableVisualOffset;
        item.transform.SetParent(limb.transform);
        item.transform.localScale = Vector3.one;
        item.transform.localRotation = Quaternion.identity;
        item.transform.localPosition = Vector3.zero;
        if (item.TryGetComponent<Wearable>(out var component)) {
            component.CreateSprites(body);
        }

    }

	public static void Container_ForceLoadItem(Item item, Container container) {
        //OG: Container.LoadItem V6.1
        if (BrokenContainer(container))
            ParentTestChest("chest FAIL load", item, container != null ? container.gameObject : null);
        Container left = null;
        if (item != null && item.TryGetParentContainer(out Container was))
            left = was;
        BagSleep.Wake(item);
        item.transform.SetParent(container.transform);
        item.rb.simulated = false;
        item.transform.position = container.GetComponent<Collider2D>().bounds.center;
        item.GetComponent<SpriteRenderer>().enabled = container.itemsVisible;
        if (container.itemsVisible) {
            item.GetComponent<SpriteRenderer>().sortingOrder = container.GetComponent<SpriteRenderer>().sortingOrder;
            item.transform.localEulerAngles = new Vector3(0f, 0f, 180f);
        }
        if ((bool)item.GetComponent<Wearable>()) {
            item.GetComponent<Wearable>().ClearSprites();
        }
        Container.UpdateItemLight(item.gameObject, !container.itemsVisible);

		BagSleep.Refresh(container);
        if (left != container)
            BagSleep.Refresh(left);
    }

    public static bool ConfirmItemOffer(NetBody giver, NetBody taker, Item item) {
        if (item == null || giver == null || taker == null) return false;

        var ici = ItemGetContainerInfo(item);
        if (!ici.has_container || ici.bodynpc == giver) {
            return true;
        }
        return false;
    }

    public static void ForcePickupItem(NetBody body, Item item) {
        ItemSync.SafeUnloadItem(item);
        item.transform.position = body.GetPosition();
        body.body.ForceAutoPickUpItem(item);
    }

    public static void SafeUnloadItem(Item item) {
        SafeUnloadItem(ItemGetContainerInfo(item));
    }
    public static void SafeUnloadItem(ItemsContainerInfo ici)  {
        if (!KrokV5Optimization.FixInvSync.SkipLoose(ici))
            return;
        if (ici.has_container) {
            SyncInfo si = null;
            if (ici.item.TryGetSyncInfo(out si)) {
                si._position.Value = ici.item.transform.position;
            }
            if (ici.is_wearing) {
                if (NetBody.TryGetNetBodyFromId(ici.body_netid, out var nb) ) {
                    Body_DropWearable_MultiplayerPatch.Force(nb.body, ici.item);
                }
            } else if (ici.inv_slot != null) {
                ici.inv_slot.body.DropItem(ici.item);
                if (si != null) {// without this item gets dropped when its put in a container,  and it works cuz this SafeUnloadItem doesnt get called from the original inventory interaction funcs, so is all good
                    var i = Array.IndexOf(Client_last_inventory_state, si);
                    if (i != -1) {
                        Client_last_inventory_state[i] = null;
                    }
                }
            } else {
                ici.container.UnloadItem(ici.item);
                if (PlayerCamera.main.currentContainer == ici.container) {
                    PlayerCamera.main.RepopulateContainer();
                }
            }

        }
    }
    public static void BetterUseItem( Body body, Item item) {
        ItemSync.SetBodyOverride(item, body);
        if (item.Stats.wearable) {
            ItemSync.Body_ForceWearWearable(body, item);
            if (item.transform.parent != null) {
                if (body.IsBodyLocal())
                    PlayerCamera.main.UpdateWearables();
                return;
            }
            item.transform.position = body.transform.position;
        }
        if (item.Stats.usable) {
            item.Stats.useAction(body, item);
            if (item != null && item.condition < 0f)
                item.condition = 0f;
            SpentDestroy(item);
        }
    }
    public static void SetBodyOverride(Item item, Body body) {
        body.GetCoUtilsInstance().SelectForMPOverride();
        item.GetOrAddComponent<BodyGetterOverrider>().SetBody(body);
    }

    // stock only deletes a spent item from Item.Update, and that returns early outside sim range.
    // a bite on a far client sets 0 and the entity stays until the host walks over.
    internal static bool DyingContainer(Item item) {
        if (item == null || item.condition > 0f || item.Stats == null || !item.Stats.destroyAtZeroCondition)
            return false;
        if (GravBagBoom.Armed(item, item.condition))
            return false;
        if (item.GetComponent<Container>() == null)
            return false;
        return NetObjectRegistry.IsRegistered(item.gameObject);
    }

    // client already spilled this. putting it back turns physics off.
    internal static bool BrokenContainer(Container box) {
        if (box == null || !box.TryGetComponent(out Item bag))
            return false;
        return DyingContainer(bag);
    }

    internal static bool BrokenBag(SyncInfo bag, Container box) {
        if (BrokenContainer(box))
            return true;
        if (bag == null || bag.go == null || !bag.go.TryGetComponent(out Item holder))
            return false;
        return DyingContainer(holder);
    }

    internal static void SpentDestroy(Item item) {
        if (!Net.IsServer || item == null || item.condition > 0f)
            return;
        if (item.Stats == null || !item.Stats.destroyAtZeroCondition)
            return;
        // an empty battery grav bag still rots. one with a cell detonates in GravBagBoom.
        if (GravBagBoom.Armed(item, item.condition))
            return;
        if (item.TryGetComponent(out Container box)) {
            ParentTestChest("chest spill host", item, item.gameObject);
            box.ContainerBroke();
            if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo bag) && bag != null && !bag.IsDestroyed) {
                // spilled items still store this id. clearing it is what stops the next spawn inheriting the bag.
                ReleaseNamedContents(bag);
                bag.Destroy();
            } else {
                UnityEngine.Object.Destroy(item.gameObject, 0.05f);
            }
            return;
        }
        UnityEngine.Object.Destroy(item.gameObject);
    }

    internal static void ReleaseNamedContents(SyncInfo bag) {
        ushort id;
        try {
            id = bag.syncId;
        } catch (Exception) {
            return;
        }
        if (id == 0)
            return;
        List<SyncInfo> named = null;
        foreach (SyncInfo other in NetObjectRegistry.SyncRegistry.Values) {
            if (other == null || other == bag)
                continue;
            try {
                if ((ushort)other.container_netId.Value != id)
                    continue;
            } catch (Exception) {
                continue;
            }
            if (named == null)
                named = new List<SyncInfo>();
            named.Add(other);
        }
        if (named == null)
            return;
        for (int i = 0; i < named.Count; i++) {
            named[i].WriteContainerInfo();
            ParentTestChest("chest host clear", named[i].item, bag.go);
        }
    }
    

    public static bool ItemIsInContainer(Item item) {
        return item.transform.parent != null;
    }
    public static bool ItemIsInContainer(GameObject o) {
        return o.transform.parent != null;
    }
    public static bool ItemIsInContainer(SyncInfo si) {
        return si.go.transform.parent != null;
    } 
    public static bool ItemIsRegistered(Item item) {
        return NetObjectRegistry.IsRegistered(item.gameObject);
    }


    public static void RecordAndApplyInvState(Body body) {
        for (int i = 0; i < body.slots.Length; i++) {
            var item = body.GetItem(i);
            Client_last_inventory_state[i] = NetObjectRegistry.GetSyncInfo(item);
        }
    }

    public static SyncInfo[] RecordInvState(Body body) {
        var aaaaa = new SyncInfo[8];
        for (int i = 0; i < body.slots.Length; i++) {
            var item = body.GetItem(i);
            aaaaa[i] = NetObjectRegistry.GetSyncInfo(item);
        }
        return aaaaa;
    }
    internal static SyncInfo[] Client_last_inventory_state = new SyncInfo[8];

    // bag the local drag left, keyed by the item. a denied pickup parents back here.
    static readonly Dictionary<knetid, Container> PickupBag = new();

    internal static void RememberPickupBag(knetid id, Container bag) {
        if (bag == null)
            PickupBag.Remove(id);
        else
            PickupBag[id] = bag;
    }

    internal static bool TakePickupBag(knetid id, out Container bag) {
        if (!PickupBag.TryGetValue(id, out bag))
            return false;
        PickupBag.Remove(id);
        return bag != null;
    }

    // the drag note is gone. a bag byte still names the container the pickup left.
    static Container BagFromByte(SyncInfo isi) {
        if (isi == null || isi.container_data1.Value != 1)
            return null;
        if (!ContainerLink.TryBag(isi, out SyncInfo bagSi) || bagSi == null || !bagSi.IsContainer())
            return null;
        return bagSi.container;
    }

    // client told the host where this item went. hold the reseat until that byte comes back.
    struct PendingParent {
        internal byte Data;
        internal knetid Net;
        internal float Until;
    }

    static readonly Dictionary<knetid, PendingParent> PendingParentAnnounce = new();
    static readonly List<knetid> PendingParentDead = new();
    const float PendingParentSeconds = 2f;

    static void RememberPendingParent(SyncInfo si) {
        if (si == null || !Net.IsClient || si.syncId.id == 0)
            return;
        PendingParentAnnounce[si.syncId] = new PendingParent {
            Data = si.container_data1.Value,
            Net = si.container_netId.Value,
            Until = Time.realtimeSinceStartup + PendingParentSeconds,
        };
    }

    internal static void ClearPendingParent(knetid id) {
        PendingParentAnnounce.Remove(id);
    }

    static void ExpirePendingParents() {
        if (PendingParentAnnounce.Count == 0)
            return;
        float now = Time.realtimeSinceStartup;
        PendingParentDead.Clear();
        foreach (var pair in PendingParentAnnounce) {
            if (pair.Value.Until < now)
                PendingParentDead.Add(pair.Key);
        }
        for (int i = 0; i < PendingParentDead.Count; i++)
            PendingParentAnnounce.Remove(PendingParentDead[i]);
    }

    // true: leave it. host byte still disagrees with the announce. match or deadline clears and returns false.
    internal static bool HoldContainerReseat(SyncInfo si) {
        if (si == null || !Net.IsClient)
            return false;
        if (!PendingParentAnnounce.TryGetValue(si.syncId, out var row))
            return false;
        if (row.Until < Time.realtimeSinceStartup
            || (si.container_data1.Value == row.Data && si.container_netId.Value.id == row.Net.id)) {
            PendingParentAnnounce.Remove(si.syncId);
            return false;
        }
        return true;
    }

    // empty slot, but the item is sitting in a bag. that is not a hand drop.
    static bool SlotEmptyBecauseBagged(SyncInfo si) {
        if (si == null)
            return false;
        if (si.container_data1.Value == 1)
            return true;
        if (si.go == null)
            return false;
        Transform parent = si.go.transform.parent;
        return parent != null && parent.GetComponent<Container>() != null;
    }

    // hand, belt, and worn still drop. a bag parent or a bag byte does not.
    static bool ItemDropHitsBag(SyncInfo isi, Item item) {
        if (item == null)
            return false;
        Transform parent = item.transform.parent;
        if (parent != null) {
            if (parent.GetComponent<InventorySlot>() != null || parent.GetComponent<Limb>() != null)
                return false;
            if (parent.GetComponent<Container>() != null)
                return true;
        }
        return isi != null && isi.container_data1.Value == 1;
    }


    public static void Client_AnnounceContainerChange(SyncInfo si)  {
        if (!KrokV5Optimization.BagPlace.BeforeAnnounce(si))
            return;
        if (si == null || si.item == null)
            return;
        // drag or wear already sent this. dont send it again.
        MoveCommit.SubmitLive(si.item);
    }




    public static void Server_AnnounceCombineSound(SyncInfo si1, SyncInfo si2, knetid combinerguyguy) {
        var it1 = si1.item;
        var it2 = si2.item;

        string sound_name = null;

        var gun = it1.GetComponent<GunScript>();
        var ammo1 = it1.GetComponent<AmmoScript>();
        var ammo2 = it2.GetComponent<AmmoScript>();

        //OG: Body.CombineItems
        if ((bool)gun && (bool)ammo2) {
            //OG: GunScript.LoadMag
            var ammo = ammo2;
            if (ammo.ammoType == gun.ammoType) {
                if (gun.racked && gun.roundInChamber == GunScript.RoundInChamber.None && ammo.itemType == AmmoScript.AmmoItemType.Round) {
                    sound_name = "gunloadshell";
                } else if (!gun.hasMag && gun.feedType == GunScript.FeedType.Mag && ammo.itemType == AmmoScript.AmmoItemType.Magazine) {
                    sound_name = "gunloadmag";
                } else if (gun.feedType == GunScript.FeedType.Direct && ammo.itemType == AmmoScript.AmmoItemType.Round && gun.roundsInMag < gun.magCapacity) {
                    sound_name = "gunloadshell";
                }
            }
        } else if ((bool)ammo1 && (bool)ammo2) {
            //OG: AmmoScript.LoadRound
            var ammo = ammo2;
            if (ammo.itemType == AmmoScript.AmmoItemType.Round && ammo1.itemType == AmmoScript.AmmoItemType.Magazine && ammo.ammoType == ammo1.ammoType && ammo1.rounds < ammo1.maxRounds) {
                sound_name = "gunloadshell";
            }
        } else {
            //OG: Body.CombineItems
            sound_name = "combine";
        }
        if (sound_name != null) {
            if (verbose) Plugin.log.LogInfo($"Sending combine sound: {sound_name} ");
            ServerMain.Server_AnnounceSound(it1.transform.position, sound_name, ServerMain.GetListOfClientIdsExceptThisAndHost(combinerguyguy));
        }
    }







    struct FavPending {
        internal bool want;
        internal double until;
    }
    static readonly Dictionary<knetid, FavPending> FavPendingMap = new Dictionary<knetid, FavPending>();

    // keep the local star until the host copy catches up. an older sample would wipe it.
    internal static void PredictFavourite(knetid id, bool want) {
        FavPendingMap[id] = new FavPending {
            want = want,
            until = Time.realtimeSinceStartupAsDouble + ClientMain.RTT_IN_SECONDS_with_margin + 1.0
        };
    }

    internal static void ClearFavouritePending(knetid id) {
        FavPendingMap.Remove(id);
    }

    internal static void ApplyFavourite(Item item, knetid id, bool value) {
        if (item == null)
            return;
        if (FavPendingMap.TryGetValue(id, out FavPending pending)) {
            if (pending.want == value || Time.realtimeSinceStartupAsDouble >= pending.until)
                FavPendingMap.Remove(id);
            else
                return;
        }
        item.favourited = value;
    }

    internal static void EchoFavourite(ScavPlayer plr, SyncInfo si) {
        if (plr == null || si == null || si.item == null)
            return;
        var writer = Net.CreateWriter(NetmsgId.CLIENT_ItemFavourited);
        writer.Put(si.syncId);
        writer.Put(si.item.favourited);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
    }

    public static bool TryGetItem(knetid syncid, out SyncInfo isi, out Item item) {
        if (NetIdToSyncInfoDict.TryGetValue(syncid, out var isii) && isii.go != null && isii.IsItem()) {
            isi = isii;
            item = isii.item;
            return true;
        }
        isi = null;
        item = null;
        return false;
    }
    public static bool TryGetItemSyncInfo(Item item, out SyncInfo isi) {
        if (SyncRegistry.TryGetValue(item.gameObject, out var isii) ) {
            isi = isii;
            return true;
        }
        isi = null;
        return false;
    }


    // parent walk works while this object is off. GetComponentInParent does not.
    static NetBody ParentBody(Transform start) {
        for (Transform t = start; t != null; t = t.parent) {
            if (t.TryGetComponent(out NetBody nb))
                return nb;
        }
        return null;
    }

    static bool OnBody(Transform start, Body body) {
        if (start == null || body == null)
            return false;
        for (Transform t = start; t != null; t = t.parent) {
            if (t.TryGetComponent(out InventorySlot slot) && slot.body == body)
                return true;
            if (t.TryGetComponent(out Limb limb) && limb.body == body)
                return true;
            if (t.TryGetComponent(out NetBody nb) && nb.body == body)
                return true;
        }
        return false;
    }

    // in a bag or slot on this body, even if the item itself is turned off
    // and the transform has not caught up to the container byte yet.
    static bool Carried(Item item, Body body) {
        if (item == null || body == null)
            return false;
        if (OnBody(item.transform, body))
            return true;
        if (!item.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
            return false;
        SyncInfo si = tracker.syncInfo;
        byte data = si.container_data1.Value;
        if (data == 0)
            return false;
        if (data >= 100)
            return ContainerLink.TryBody(si, out NetBody nb) && nb.body == body;
        if (!ContainerLink.TryBag(si, out SyncInfo bag) || bag.go == null)
            return false;
        return OnBody(bag.go.transform, body);
    }

    public static bool CheckIfBodyReachThisItem(Item item, Body my_body, float maxdist = 20f, bool check_obstruction = false) {
        return CheckIfBodyReachThisItem(item.gameObject,my_body,maxdist, check_obstruction);
    }
    public static bool CheckIfBodyReachThisItem(SyncInfo item, Body my_body, float maxdist = 20f, bool check_obstruction = false) {
        return CheckIfBodyReachThisItem(item.go, my_body, maxdist, check_obstruction);
    }
#if !RELEASE
    const bool CheckIfBodyReachThisItem_VERBOSE =  false;
#endif

    // parented to a bag, but the transform is still at the save coordinate. put it on the bag.
    internal static void SnapOntoContainer(Item item) {
        if (item == null)
            return;
        Transform parent = item.transform.parent;
        if (parent == null || !parent.TryGetComponent(out Container box))
            return;
        Vector3 at = box.transform.position;
        if ((item.transform.position - at).sqrMagnitude < 0.0001f)
            return;
        item.transform.position = at;
    }

    // stock LoadItem refuses with no alert. check the same gates before an unload.
    internal static bool LoadWouldTake(Container dest, Item item, out string why) {
        why = "missing";
        if (dest == null || item == null)
            return false;
        if (item.TryGetComponent(out Container nested) && nested.GetHoldingWeight() > 0f) {
            why = "stacking";
            return false;
        }
        Item bagItem = dest.GetComponent<Item>();
        if (bagItem != null && bagItem.TryGetParentContainer(out _)) {
            why = "bag in a bag";
            return false;
        }
        if (bagItem == item) {
            why = "itself";
            return false;
        }
        if (!dest.CanHoldItem(item)) {
            why = "weight";
            return false;
        }
        if (Vector2.Distance(item.transform.position, dest.transform.position) >= 10f) {
            why = "distance";
            return false;
        }
        why = null;
        return true;
    }

    // its more like Check if can take and manipulate the file, without violating any rules
    public static bool CheckIfBodyReachThisItem(GameObject item, Body my_body, float maxdist = 20f, bool check_obstruction = false) {
        if (item != null && item.TryGetComponent(out Item held)) {
            SnapOntoContainer(held);
            // slept bag contents stay off. they are still in the bag, so the world distance does not apply.
            if (Carried(held, my_body))
                return true;
        }
        var pb = item.GetComponentInParent<NetBody>();
        // if i have the item, obviously i can reach it
        if (pb != null && pb.body == my_body) {
            return true;
        }
        var ici = ItemGetContainerInfo(item);

        if (!CheckIfBodyCanStealThisItem(item, my_body, ici)) {
#if !RELEASE
            if (CheckIfBodyReachThisItem_VERBOSE && verbose) Plugin.log.LogWarning($"CheckIfBodyReachThisItem: CheckIfBodyCanStealThisItem failed ");
#endif
            return false;
        }

        if (!KM.DistanceCheck(item.transform.position, my_body.transform.position, maxdist)) {
#if !RELEASE
            if (CheckIfBodyReachThisItem_VERBOSE && verbose) Plugin.log.LogWarning($"CheckIfBodyReachThisItem: dist2dsqrcheck failed ");
#endif
            return false;
        }

        if (check_obstruction) {
            if (ici.is_netbody) {
                if (!Util.AccurateRaycastInteractionCheckObstruction(my_body, ici.plr.body, false)) {
#if !RELEASE
                    if (CheckIfBodyReachThisItem_VERBOSE && verbose) Plugin.log.LogWarning($"CheckIfBodyReachThisItem: is_player AccurateRaycastInteractionCheck failed ");
#endif
                    return false;
                }
            } else {
                if (!Util.AccurateRaycastInteractionCheck(my_body, item.transform.position, false)) {
#if !RELEASE
                    if (CheckIfBodyReachThisItem_VERBOSE && verbose) Plugin.log.LogWarning($"CheckIfBodyReachThisItem: no plr AccurateRaycastInteractionCheck failed ");
#endif
                    return false;
                }
            }
        }

        return true;
    }

    internal static void Client_SendDoubleItemPickup(Item i1, Item i2) {
        var si1 = NetObjectRegistry.GetSyncInfo(i1);
        var si2 = NetObjectRegistry.GetSyncInfo(i2);

        bool any = false;
        if (si1 != null) {
            si1.SetIgnoreTimeForRoundTrip(1);
            any = true;
        }
        if (si2 != null) {
            si2.SetIgnoreTimeForRoundTrip(1);
            any = true;
        }
        if (!any) {
            return;
        }


        var writer = Net.CreateWriter(NetmsgId.SERVER_ItemPickup);
        if (si1 != null) {
            var ici = ItemSync.ItemGetContainerInfo(si1.item);
            writer.Put(ici.inv_slot_number);
            writer.Put(si1.syncId);
        }
        if (si2 != null) {
            var ici2 = ItemSync.ItemGetContainerInfo(si2.item);
            writer.Put(ici2.inv_slot_number);
            writer.Put(si2.syncId);
        }

        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        
    }

    static void DoClientInventoryUpdate() {
        var newstate = RecordInvState(Util.GetLocalBody());

        SyncInfo old = null;
        SyncInfo nn  = null;
        try {
            for (int i = 0; i < newstate.Length; i++) {
                old = (Client_last_inventory_state[i]);
                nn = (newstate[i]);

                if (old != null && old.go == null) {
                    old = null;
                }

				if (old != nn) {
                    if (nn == null) {
                        if (!newstate.Contains(old)) { // it may have been just moved to another slot
                            if (SlotEmptyBecauseBagged(old)) {
                                // already in a bag. clearing the slot so the next tick does not send it either.
                                Client_last_inventory_state[i] = null;
                            } else if (old.item != null && !MoveCommit.Held(old.item) && !SlotHold.Owns(Util.GetLocalBody(), i, old.item) && !CraftCommit.Yielded(old.item)) {
                                MoveCommit.SubmitLive(old.item);
                            }

                        }
                    } else if (nn.item != null && !MoveCommit.Held(nn.item)) {
                        MoveCommit.SubmitLive(nn.item);
                    }
                }
            }
        } catch (Exception e) {
            log.error($"DoClientInventoryUpdate: old: {old}  nn: {nn} {e.ToString()}");
        }

        for (int i = 0; i < newstate.Length; i++) {
            Client_last_inventory_state[i] = newstate[i];
        }
    }

    void Update() {
        
    }
    void FixedUpdate() {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated() && Net.IsClient) {
            ExpirePendingParents();
            if (ItemSync.ItemContainerChanged.Count > 0) {
                foreach (var item in ItemSync.ItemContainerChanged) {
                    Client_AnnounceContainerChange(item);
                }
                ItemSync.ItemContainerChanged.Clear();
            }

            DoClientInventoryUpdate();
        }
    }

    public static bool CheckIfBodyCanStealThisItem(GameObject item, Body my_body, ItemsContainerInfo ici = null) {
        return CheckIfBodyCanStealThisItem(item.GetComponent<Item>(),my_body, ici);
    }

    public static bool CheckIfBodyCanStealThisItem(Item item, Body my_body, ItemsContainerInfo ici = null) {
        ici ??= ItemGetContainerInfo(item);
        if (ici.has_container) { 
            if (ici.bodynpc != null)
            {
                if (Multiplayer.rules.NoInventoryLock) {
                    return true;
                }
            
                var otherbody = ici.bodynpc.body;
                var is_on_consious_player = otherbody != my_body && otherbody.conscious && (ici.is_wearing || ici.is_netbody);
                if (is_on_consious_player) {
                    return false;
                }
            }
        }
        return true;
    }


    public static bool TryGetSyncInfo(Item item, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(item.gameObject, out var isi)) {
            si = isi;
            return true;
        }
        si = null;
        return false;
    }
    public static bool TryGetItemSyncInfo(knetid item_syncid, out SyncInfo si) {
        if (NetIdToSyncInfoDict.TryGetValue(item_syncid, out var isi) && isi.IsItem()) {
            si = isi;
            return si != null;
        }
        si = null;
        return false;
    }


    public static bool ItemCanBeIgnoredForNetwork(Item item) {
        if (item == null) {
            return true;
        }
        return NetObjectRegistry.ObjectShouldNotBeSynced(item.gameObject);
    }


    public const float max_items_distance_to_consider = 240f;
    public const float max_plr_view_distance = 64f; // i dont count goggles cuz fuck it

    public const float max_items_distance_to_consider_sqr = max_items_distance_to_consider * max_items_distance_to_consider;
    public const float max_plr_view_distance_sqr = max_plr_view_distance * max_plr_view_distance;


    internal const int max_items_to_check_per_iteration = 30;

    internal static int cur_items_check_index = 0;
    internal static List<Item> cur_items_check = new List<Item>();

    







    [ServerReceiver((byte)NetmsgId.SERVER_BodyCombineItems)]
    static void ServerReceiver__BodyCombineItems(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syncid1);
        reader.Get(out knetid syncid2);

        // ignore this request if still didnt generate world or is not conscious
        if (WorldGeneration.world && !WorldGeneration.world.generatingWorld && ScavPlayer.GetBodyFromClientId(clientId) is var body && body != null && body.conscious) {

            if (TryGetItem(syncid1, out var isi1, out var item1) && TryGetItem(syncid2, out var isi2, out var item2)) {

                // check if not too far
                if (CheckIfBodyReachThisItem(isi1, body) && CheckIfBodyReachThisItem(isi2, body)) {
                    // is host
                    if (!Util.IsBodyLocal(body)) {
                        var cancombine = body.CanCombine(isi1.item, isi2.item);

                        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: BodyCombineItems {cancombine} {clientId} {isi1} x {isi2} ");

                        if (cancombine) {
                            Server_AnnounceCombineSound(isi1, isi2,clientId);
                            Body_CombineItems_MultiplayerPatch.Body_CombineItems(body, isi1.item, isi2.item);
                        }
                    }
                    // i will not send double cuz idk how to check for destroyed things properly 

                    NetObjectRegistry.SafeNewGO(isi1.go);
                } else {
                    if (verbose) Plugin.log.LogWarning($"SERVER BodyCombineItems: {isi1} {isi2} , but client was too far!");
                }
            }
        }
    }





    [ServerReceiver((byte)NetmsgId.SERVER_CombineLiquidItems)]
    static void ServerReceiver__CombineLiquidItems(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syncid1);
        reader.Get(out knetid syncid2);
        reader.Get(out float amt);


        // ignore this request if still didnt generate world or is not conscious
        if (Util.IsWorldGenerated() && ScavPlayer.GetBodyFromClientId(clientId) is var body && body != null && body.conscious && Util.IsFinite(amt)) {

            if (TryGetItem(syncid1, out var isi1, out var item1) && TryGetItem(syncid2, out var isi2, out var item2) && isi1.IsLiquidContainer() && isi2.IsLiquidContainer()) {

                // check if not too far
                if (CheckIfBodyReachThisItem(isi1, body) && CheckIfBodyReachThisItem(isi2, body)) {
                    // is host
                    if (!Util.IsBodyLocal(body)) {
                        var cancombine = body.CanCombine(isi1.item, isi2.item);

                        if (verbose) Plugin.log.LogInfo($"Server received CombineLiquidItems {cancombine} {isi1} x {isi2} ");
                        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: CombineLiquidItems {cancombine} {clientId} {isi1} x {isi2} ");

                        if (cancombine) {
                            ServerMain.Server_AnnounceSound(isi1.go.transform.position, "waterpour", ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
                            body.CombineLiquids(isi1.liquidcontainer, isi2.liquidcontainer, amt);
                        }
                    }

                    // i will not send double cuz idk how to check for destroyed things properly 

                    NetObjectRegistry.SafeNewGO(isi1.go);

                } else {
                    if (verbose) Plugin.log.LogWarning($"SERVER CombineLiquidItems: {isi1} {isi2} , but client was too far!");
                }
            }
        }

    }





    [ServerReceiver( (byte)NetmsgId.SERVER_ItemChangeContainer)]
    static void ServerReceiver__ItemChangeContainer(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out bool hascontainer);
        reader.Get(out knetid syncid);
        reader.Get(out knetid container_syncid);

        // ignore this request if still didnt generate world or is not conscious
        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) && body.conscious) {

            if (TryGetItem(syncid, out var si, out var item)) {

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: ItemChangeContainer {plr} load: {hascontainer}  ids: {syncid} -> {container_syncid} ");

                SnapOntoContainer(item);
                if (CheckIfBodyReachThisItem(si, body, 20, true)) {
                    if (hascontainer) {
                        if (!TryGetItem(container_syncid, out var contsi, out _) || !contsi.IsContainer() || contsi.container == null) {
                            if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  hascontainer FAILED  ids: {si} -> {container_syncid}  no bag");
                            return;
                        }
                        if (!CheckIfBodyReachThisItem(contsi, body, 20, true)) {
                            if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  hascontainer FAILED  ids: {si} -> {contsi}  bag out of reach");
                            return;
                        }
                        var ici = ItemGetContainerInfo(item);
                        // already parented here. the snap above puts it on the bag, so a later use sees the real spot.
                        if (ici.container_si == contsi)
                            return;
                        // unload only after the bag will take it. a refusal used to leave the item in neither place.
                        if (!LoadWouldTake(contsi.container, item, out var why)) {
                            if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  hascontainer FAILED  ids: {si} -> {contsi}  {why}");
                            return;
                        }
                        bool inBag = ici.container != null && ici.inv_slot == null && !ici.is_wearing;
                        if (!inBag)
                            SafeUnloadItem(ici);
                        contsi.container.LoadItem(item);
                        if (item.transform.parent != contsi.container.transform) {
                            if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  hascontainer FAILED  ids: {si} -> {contsi}  load refused");
                            return;
                        }

                        if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  LOAD ITEM  ids: {si} -> {contsi}  ");

                        NetObjectRegistry.SafeNewGO(si.go);
                        return;
                    } else {
                        if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  UNLOAD ITEM  ids: {si}  ");

                        SafeUnloadItem(ItemGetContainerInfo(item));
                        // a worn bag unloads as a container. if it is still on the limb, the sprite copy stays and the drop never lands.
                        Limb limb = item.transform.parent != null ? item.transform.parent.GetComponent<Limb>() : null;
                        if (limb != null && limb.body != null)
                            Body_DropWearable_MultiplayerPatch.Force(limb.body, item);

                        NetObjectRegistry.SafeNewGO(si.go);
                        return;
                    }
                }
            }
            if (verbose) Plugin.log.LogInfo($"SERVER received ItemChangeContainer  {plr}  FAILED  ids: {syncid} -> {container_syncid}  ");
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_ThrowItem)]
    static void ServerReceiver__ThrowItem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syncid);
        reader.Get(out float force);


        // ignore this request if still didnt generate world
        if (Util.IsWorldGenerated() && Util.IsFinite(force)) {
            if (
                NetIdToSyncInfoDict.TryGetValue(syncid, out var isi) 
                && isi.IsItem()
                && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)
            ) {
                var body = pb.body;

                var person_is_host = Util.IsBodyLocal(body);

                // assuming Throw will always be from handSlot
                if (person_is_host || body.GetItem(body.handSlot) == isi.item) {
                    force = Mathf.Clamp01(force);

                    // in the hand is enough. a blank container note is byte 0, which also means not ready,
                    // and that used to skip the drop while the thrower already let go locally.
                    if (!person_is_host)
                        body.ThrowItem(force);

                    
                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: ThrowItem {clientId} {isi} {force} ");

                    NetObjectRegistry.SafeNewGO(isi.go);

                    return;
                }
            }
            if (verbose) Plugin.log.LogWarning($"Server declines ThrowItem: {syncid} {isi}  {force} ");
        }


    }


    // check only. dropping the other hand here used to stick a loose byte.
    static (Item, byte, Container) _Server_ReadOneItem(ScavPlayer plr, ref LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out byte invslot);
        reader.Get(out knetid item_syncid);
        if (plr.body == null || plr.body.slots == null || invslot >= plr.body.slots.Length) {
            DenyPickup(plr, item_syncid, "bad slot " + invslot);
            return (null, invslot, null);
        }
        if (TryGetItem(item_syncid, out var isi, out var item)) {
            if (plr.body.conscious && CheckIfBodyReachThisItem(item, plr.body)) {
                if (!HostCanSeat(plr.body, item, invslot, out var why)) {
                    DenyPickup(plr, item_syncid, why);
                    return (null, invslot, null);
                }
                Container bag = null;
                var ici = ItemGetContainerInfo(item);
                if (ici != null && ici.container != null)
                    bag = ici.container;
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"S: ItemPickup slot:{invslot} {isi} ");
                return (item, invslot, bag);
            }
            DenyPickup(plr, item_syncid, "out of reach");
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"S: DENIED ItemPickup {plr} {isi} ", Color.red);
            return (null, invslot, null);
        }
        DenyPickup(plr, item_syncid, "missing item");
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"S: DENIED ItemPickup {plr} {item_syncid} ", Color.red);
        return (null, invslot, null);
    }

    static void DenyPickup(ScavPlayer plr, knetid itemId, string why) {
        var writer = Net.CreateWriter(NetmsgId.CLIENT_ItemPickupDENY);
        writer.Put(itemId);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
        log.serverdeny($"ItemPickup {plr} {itemId}: {why}");
    }

    // PickUpItem with force still refuses a blocked slot and a hands-only item. check that before unloading.
    internal static bool HostCanSeat(Body body, Item item, int slot, out string why) {
        why = "slot blocked";
        if (item == null || item.Stats == null)
            return false;
        InventorySlot hold = body.slots[slot];
        if (hold == null)
            return false;
        if (item.Stats.onlyHoldInHands && !hold.isHand) {
            why = "hands only";
            return false;
        }
        int kids = hold.transform.childCount;
        if (kids > 1)
            return false;
        if (kids == 1 && hold.transform.GetChild(0).GetComponent<Item>() == null)
            return false;
        why = null;
        return true;
    }

    static void SeatPickup(Body body, Item item, byte slot, Container bag) {
        body.PickUpItem(item, slot, true);
        if (item != null && body.HoldingItem(item))
            return;
        if (bag != null && item != null) {
            Container_ForceLoadItem(item, bag);
            if (TryGetItemSyncInfo(item, out var si))
                si.WriteContainerInfo();
            log.serverdeny($"ItemPickup seat failed slot {slot} {item}, back in bag");
            return;
        }
        log.serverdeny($"ItemPickup seat failed slot {slot} {item}");
    }


    [ServerReceiver((byte)NetmsgId.SERVER_ItemPickup)]
    static void ServerReceiver__ItemPickup(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) || plr.body == null)
            return;
        var item1 = _Server_ReadOneItem(plr, ref reader);
        var item2 = default((Item, byte, Container));
        bool has2 = !reader.EndOfData;
        if (has2)
            item2 = _Server_ReadOneItem(plr, ref reader);
        if (item1.Item1 == null && (!has2 || item2.Item1 == null))
            return;

        // read both hands first. the drop in the middle shouldnt say its on the ground.
        bool watch = ParentTestWatch(item1.Item1, item1.Item2, plr.body)
            || (has2 && ParentTestWatch(item2.Item1, item2.Item2, plr.body));
        if (watch)
            ParentTest("swap begin " + ParentTestItem(item1.Item1) + " slot " + item1.Item2
                + " | " + ParentTestItem(item2.Item1) + " slot " + (has2 ? item2.Item2.ToString() : "-"));
        var touched = new List<Item>(4);
        ParentTestSwap = watch;
        SuppressContainerWrite++;
        try {
            if (has2 && item2.Item1 != null)
                MoveIntoSlot(plr.body, item2.Item1, item2.Item2, item2.Item3, touched);
            if (item1.Item1 != null)
                MoveIntoSlot(plr.body, item1.Item1, item1.Item2, item1.Item3, touched);
        } finally {
            SuppressContainerWrite--;
            ParentTestSwap = false;
        }
        for (int i = 0; i < touched.Count; i++) {
            if (touched[i] == null || !TryGetItemSyncInfo(touched[i], out var si) || si == null)
                continue;
            si.WriteContainerInfo();
            if (watch)
                ParentTest("swap end " + ParentTestItem(touched[i]));
        }
    }

    // empty the slot, pull it out, seat it. caller is holding the container write.
    static void MoveIntoSlot(Body body, Item item, byte slot, Container bag, List<Item> touched) {
        Item occupant = body.GetItem(slot);
        if (occupant != null && occupant != item) {
            body.DropItem(slot);
            if (!touched.Contains(occupant))
                touched.Add(occupant);
        }
        SafeUnloadItem(item);
        if (!touched.Contains(item))
            touched.Add(item);
        SeatPickup(body, item, slot, bag);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemPickupDENY)]
    static void ClientReceiver_ItemPickupDENY(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid item_syncid);
        ClearPendingParent(item_syncid);

        if (ItemSync.TryGetItem(item_syncid, out var isi, out var item)) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(isi.transformPosition, $"C: DENIED ItemPickup {isi} ", Color.red);

            Container bag = null;
            if (!ItemSync.TakePickupBag(item_syncid, out bag))
                bag = BagFromByte(isi);

            var ici = ItemSync.ItemGetContainerInfo(item);
            if (ici.is_in_surface_inv) {
                if (ici.bodynpc != null && ici.bodynpc.IsBodyLocal()) {
                    var i = Array.IndexOf(ItemSync.Client_last_inventory_state, isi);
                    if (i >= 0) {
                        ItemSync.Client_last_inventory_state[i] = null;
                    }
                }
                ItemSync.SafeUnloadItem(ici);
                if (bag != null) {
                    ItemSync.Container_ForceLoadItem(item, bag);
                    isi.WriteContainerInfo();
                    log.devevent($"CLIENT: ItemPickup denied {isi}, back in bag", item.transform.position);
                }
                isi.lastUpdateTick = Net.ServerTick; // reset the ignore time 
            } else if (bag != null && item.transform.parent == null) {
                ItemSync.Container_ForceLoadItem(item, bag);
                isi.WriteContainerInfo();
                log.devevent($"CLIENT: ItemPickup denied {isi}, back in bag", item.transform.position);
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemDrop)]
    static void ServerReceiver__ItemDropWearable(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syncid);

#if !RELEASE
        log.warn($"SERVER_ItemDrop: {DebugHelp.GetPlayerFullDebugString(clientId)} syncid: {syncid}"); 
#endif

        // ignore this request if still didnt generate world
        if (Util.IsWorldGenerated()) {
            if (TryGetItem(syncid, out var isi, out var item)) {
                if (
                    ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)
                    && pb.body.conscious
                    && item.transform.parent != null
                    && CheckIfBodyReachThisItem(item, pb.body)
                ) {
                    // check if item is actually on them, i dont even need to check if its
                    // a bag unload arrives as ItemChangeContainer. this drop is the hand or the belt.
                    if (ItemDropHitsBag(isi, item)) {
                        log.serverdeny($"ItemDrop {syncid} already in a bag");
                        return;
                    }

                    SafeUnloadItem(item);

                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pb.position, $"S: ItemDrop {DebugHelp.GetPlayerFullDebugString(clientId)} {isi} ");

                    NetObjectRegistry.SafeNewGO(isi.go);

                    return;
                }
            }
        }

#if !RELEASE
        log.warn($"DENIED SERVER_ItemDrop ");
#endif
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemWearing)]
    static void ServerReceiver__ItemWearing(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syncid);

        // ignore this request if still didnt generate world
        if (Util.IsWorldGenerated()) {
            if (TryGetItem(syncid, out var isi, out var item) && item.Stats.wearable) {
                if (ScavPlayer.GetBodyFromClientId(clientId) is var body && body != null && body.alive) {


                    //OG: Body.DoPickupCheck
                    bool flag2 = KM.DistanceCheck(isi.go.transform.position, body.transform.position, ((10f + 4f)));

                    if (flag2) {

                        // check if item is already in someones inventory
                        if (CheckIfBodyCanStealThisItem(item, body)) {
                            // i shouldnt do this on host
                            if (!Util.IsBodyLocal(body)) {
                                SafeUnloadItem(item);
                                item.transform.position = body.transform.position;

                                ItemSync.Body_ForceWearWearable(body,item);

                            }
                        }


                        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: ItemWearing {DebugHelp.GetPlayerFullDebugString(clientId)} {isi} ");

                        NetObjectRegistry.SafeNewGO(isi.go);
                        return;
                    }

                }
            }
        }
        if (verbose) Plugin.log.LogWarning($"Server declines itemwearing: {DebugHelp.GetPlayerFullDebugString(clientId)} {syncid} ");


    }





}
