using HarmonyLib;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;
using static GunScript;
using Random = UnityEngine.Random;
using System.Net.WebSockets;

namespace Together;



// is this thing obsolete?
// this is guranteed to run only from PlayerCamera.Update
[HarmonyPatch(typeof(Body), nameof(Body.UseItemInHand))]
public static class Body_UseItemInHand_MultiplayerPatch {

    public static bool IsUsingItemInHand = false;

    private static bool _hostLight;
    private static ushort _hostLightId;

    private struct StateHold {
        public byte state;
        public float until;
    }

    struct PendingUse {
        public bool on;
        public ushort item;
        public knetid body;
    }

    static PendingUse _pending;
    static readonly Dictionary<ushort, StateHold> StateHolds = new Dictionary<ushort, StateHold>();

    // true while this byte is older than the click. the hold stays up for the whole wait,
    // so a matching byte does not let an older one through afterwards.
    internal static bool StaleState(ushort id, byte incoming) {
        byte held;
        if (!HeldState(id, out held)) {
            return false;
        }
        return incoming != held;
    }

    internal static void HoldState(ushort id, byte state) {
        if (id == 0) {
            return;
        }
        StateHolds[id] = new StateHold { state = state, until = Time.realtimeSinceStartup + 2f };
    }

    internal static bool HeldState(ushort id, out byte state) {
        state = 0;
        StateHold hold;
        if (!StateHolds.TryGetValue(id, out hold)) {
            return false;
        }
        if (Time.realtimeSinceStartup >= hold.until) {
            StateHolds.Remove(id);
            return false;
        }
        state = hold.state;
        return true;
    }

    // the click already ran. tell the host, and hold this copy until that round trip lands.
    internal static void AnnouncePredictedUse(ushort itemId, knetid bodyNet) {
        if (Net.IsServer || (itemId == 0)) {
            return;
        }
        if (ItemSync.TryGetItemSyncInfo((knetid)itemId, out SyncInfo si) && (si is not null)) {
            si.ArmPredictedUse();
            if ((si.item is not null) && si.item.TryGetComponent(out CustomItemBehaviour cib) && ItemSync.CustomItemBehaviourStateCanBeJustByte(si.item)) {
                HoldState(itemId, (byte)cib.state);
            }
        }
        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestUseItem, (knetid)itemId, bodyNet, true);
    }

    public static bool Prefix(Body __instance) {
        _hostLight = false;
        if (!Util.IsWorldGenerated()) { return false; }

        if (!Net.IsRunning) return true;
        
        var body = __instance;
        _pending.on = false;

        if (Util.IsBodyLocal(body)) { // its local 

            //OG: Body.UseItemInHand
            if (body.conscious && body.GetItem(body.handSlot) is var item && item != null && item.Stats != null && item.Stats.usable && item.Stats.usableWithLMB) {
                // guns still skip the next health snapshot. their shots already go out on their own.
                if (CombatStuff.ItemIsUsedForAttacking(item) && NetObjectRegistry.TryGetSyncInfoOrRegister(item.gameObject, out var si))
                    si.SetIgnoreTimeForRoundTrip();
                else if (!Net.IsServer && !CombatStuff.ItemIsUsedForAttacking(item)
                    && body.TryGetNetBody(out var nb) && nb.IsLocal
                    && ItemSync.TryGetSyncInfo(item, out var useSi)) {
                    // run the use here. the host confirms the same click after.
                    _pending.on = true;
                    _pending.item = useSi.syncId;
                    _pending.body = nb.netId;
                } else if (Net.IsServer && ItemSync.CustomItemBehaviourStateCanBeJustByte(item) && ItemSync.TryGetSyncInfo(item, out SyncInfo lightSi)) {
                    // the host click never takes the request path. other clients still need the mode it landed on.
                    _hostLight = true;
                    _hostLightId = lightSi.syncId;
                }
            }

            IsUsingItemInHand = true;
        } 
        
        return true;
    }
    public static void Postfix(Body __instance) {
        if (!Net.IsRunning) return;

        if (_pending.on) {
            AnnouncePredictedUse(_pending.item, _pending.body);
            _pending.on = false;
        }
        if (_hostLight) {
            _hostLight = false;
            SyncInfo lightSi;
            NetBody nb;
            if (ItemSync.TryGetItemSyncInfo((knetid)_hostLightId, out lightSi) && __instance.TryGetNetBody(out nb)) {
                Body_UseItem_MultiplayerPatch.SendLightMode(lightSi, nb);
            }
        }
        
        var body = __instance;

        if (Net.IsServer) // is host or server
        {
        } else if (Util.GetLocalBody() == body) { // its local 
            IsUsingItemInHand = false;
        }
        
    }
}





[HarmonyPatch(typeof(Body), nameof(Body.UseItem))]
public static class Body_UseItem_MultiplayerPatch {
    public static Item current_used_item = null;

    public static bool Prefix(Body __instance, ref SyncInfo __state, Item item) {
        current_used_item = item;
        __state = null;

        if (!Util.IsWorldGenerated()) return false;
        if (!Net.IsRunning) return true;

        if (item && __instance.TryGetNetBody(out var pb) && pb.IsLocal) { // check if its local
            if (ItemSync.TryGetSyncInfo(item, out var si)) {
                if (item.Stats.usable) {
                    if (CombatStuff.ItemIsUsedForAttacking(item)) { // weapons are handled separately by the attack and gun shoot but whatever
                        if (si.IsGun()) {
                            var gst =  item.GetOrAddComponent<TrackerGunScript>();
                            gst.body = __instance;
                            gst.pbody = pb;
                        }
                        // Attack items commands are sent from the attack functions
                    } else {
                        __state = si;

                        pb.RollUseRandom();
                    }
                }
            } else {
                // it will check if the item is ignored for network
                NetObjectRegistry.Client_DeleteUnregisteredSyncableObject(item.gameObject);
            }
        }
        
        return true;
    }
    public static void Postfix(Body __instance, ref SyncInfo __state, Item item) {
        current_used_item = null;
        if (__state != null) { // If is registered item and not an attack item 

            // same click the hand path uses. local use already happened.
            Body_UseItemInHand_MultiplayerPatch.AnnouncePredictedUse(__state.syncId, __instance.GetNetBody().netId);

        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_RequestUseItem)]
    static void Server_RequestUseItem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid item);
        reader.Get(out knetid target_person);

        if (
            ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var giverPlr, out var giver) && giver.body.conscious 
            && NetBody.TryGetNetBodyFromId(target_person, out var target_nb) 
            && NetObjectRegistry.TryGetSyncInfo(item, out var si) && si.go != null && si.IsItem()
        ) {

            if (
                (giver == target_nb || !MedicalSync.IsRefusingHelp(target_nb.body)) 
                && KM.DistanceCheck(giver, target_nb.body, SharedMain.max_player_interaction_distance * 1.6f) 
                && (si.item.Stats.usable || si.item.Stats.wearable) 
                && ItemSync.CheckIfBodyReachThisItem(si, giver.body) 
                && !CombatStuff.ItemIsUsedForAttacking(si.item) 
            ) {
                var useItemFunc = () => {
                    if (giver == target_nb && giver.IsLocal) {
                        // Don't do it
                    } else {
                        ItemSync.BetterUseItem(target_nb.body, si.item);
                    }
                    
                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(Vector2.Lerp(giver.position, target_nb.position, 0.5f), $"S: RequestUseItem {si}");

                    byte lightState = 255;
                    if ((si.go is not null) && (si.item is not null) && si.item.TryGetComponent(out CustomItemBehaviour light) && ItemSync.CustomItemBehaviourStateCanBeJustByte(si.item)) {
                        lightState = (byte)light.state;
                    }

                    var writer = Net.CreateWriter(NetmsgId.CLIENT_RequestUseItemRelay);
                    writer.Put(clientId);
                    writer.Put(item);
                    writer.Put(target_person);
                    writer.Put(lightState);
                    Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
                    // WRITER END;
                };


                if (Multiplayer.rules.ConsentForFeeding && target_nb != giver && target_nb.body.conscious && target_nb.IsPlayer) {
                    ServerMain.PromptPlayer(target_nb.plr, "FEED_OFFER", 
                        $"<body:{giver.netId}> <key:plrint_feed_prompt>", 
                        $"<key:{(
                                si.item.Stats.wearable ? "plrint_feed_receive_wearable" :
                                (si.item.IsEdible() ? "plrint_feed_receive_food" :
                                ((si.item.Stats.category == "water"
                                ) ? "plrint_feed_receive_water"
                                : "plrint_useitem"))
                        )}> \"<item:{si.syncId}>\"", 
                    (answer) => {
                        if (answer == 0) {
                            if (si.go == null) {
                                giverPlr.Server_DoAlertSingle($"<bodyc:{target_nb.netId}> <key:giveitem_noitem>.");
                                return;
                            }
                            if (ItemSync.ConfirmItemOffer(giver, target_nb, si.item)) {
                                useItemFunc();

                                giverPlr.Server_DoAlertSingle($"<bodyc:{target_nb.netId}> <key:accepted>.");
                            }
                        } else {
                            giverPlr.Server_DoAlertSingle($"<bodyc:{target_nb.netId}> <key:declined>.");
                        }
                    });
                } else {
                    useItemFunc();
                }
            } else { // at this point, real player should get an alert that they refuse
                log.serverdeny($"RequestUseItem {giverPlr} -> {target_nb} - {si}", Vector2.Lerp(giverPlr.body.transform.position, target_nb.position, 0.5f));
            }
        }
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_RequestUseItemRelay)]
    static void Client_RequestUseItemRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid user);
        reader.Get(out knetid item);
        reader.Get(out knetid target);
        // 255 means this use is not a light. older hosts omit the byte.
        byte lightState = 255;
        if (!reader.EndOfData) {
            reader.Get(out lightState);
        }

        if (
            NetBody.TryGetNetBodyFromId(target, out var target_nb) &&
            NetObjectRegistry.TryGetSyncInfo(item, out var si) && si.go != null
        ) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS && ScavPlayer.LocalPlayer != null) DebugHelp.OnNetEvent(Vector2.Lerp(ScavPlayer.LocalPlayer.body.transform.position, target_nb.position, 0.8f), $"C: RequestUseItemRelay {si}");

            // the relay used to run the toggle on its own. the snapshot still had the old mode, so the beam blinked.
            if ((lightState != 255) && (si.item is not null) && si.item.TryGetComponent(out CustomItemBehaviour light)) {
                light.state = lightState;
                Body_UseItemInHand_MultiplayerPatch.HoldState(si.syncId, lightState);
                Sound.Play("flashlighttoggle", si.item.transform.position);
            } else {
                ItemSync.BetterUseItem(target_nb.body, si.item);
            }
        }
    }

    internal static void SendLightMode(SyncInfo si, NetBody nb) {
        if ((si is null) || (si.item is null) || !si.item.TryGetComponent(out CustomItemBehaviour light)) {
            return;
        }
        knetid user = nb.plr != null ? nb.plr.playerId : default;
        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_RequestUseItemRelay);
        writer.Put(user);
        writer.Put(si.syncId);
        writer.Put(nb.netId);
        writer.Put((byte)light.state);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(user));
    }

}


[HarmonyPatch(typeof(Body), nameof(Body.Attack))]
public static class Body_Attack_MultiplayerPatch {

    public static void DoToolSpecificPVPDamage(Item used_tool, Limb target, float rule_mult, float multiplier_strength_vs_armor) {
        switch (used_tool.id) {
            case "sledgehammer": { // idk man maybe add more big tools here
                if (UnityEngine.Random.value < 0.5 * rule_mult) {

                    if (UnityEngine.Random.value < 0.3 * rule_mult) {
                        target.BreakBone();
                    } else {
                        target.Dislocate();
                    }
                }

                if (target.isHead) {
                    target.body.brainHealth -= 10 * multiplier_strength_vs_armor;
                    target.body.consciousness -= 50f * multiplier_strength_vs_armor; // bonk 
                }
                if (UnityEngine.Random.value < 0.7 * rule_mult) {
                    if (target == target.body.GetUpperTorso()) { // is upper torso, disrupt their breathing
                        target.body.respiratoryRate = Mathf.Max(0, target.body.respiratoryRate - 50f);
                    }
                }

                target.body.eyePanicTime += 0.5f;
                target.body.eyeScareTime += 1.0f;

                break;
            }
            default: {

                break;
            }
        }
    }


    // returns the damage dealt, kinda
    public static float DoPVPMeleeDamage(Body attacker, Limb target, AttackInfo atk, float armstrength, Item used_tool, out Vector2 knockback) {

        var attack_dir = KM.NormalTo(attacker.limbs[1].transform.position, attacker.targetLookPos);

        knockback = new Vector2(attack_dir.x, 0.4f);
        knockback *= atk.knockBack * 0.04f;

        {
            //OG: Body.Attack V5t4  the part where it checks if the building is an animal,   i wanna do the same cooldown addition here
            attacker.attackCooldown *= 3.5f * atk.attackCooldownMult;
            if (Util.IsBodyLocal(attacker)) {
                PlayerCamera.main.lastAttackCool = attacker.attackCooldown;
            }

        }


        var og_hp = target.skinHealth + target.muscleHealth;

        var rule_mult = Multiplayer.rules.PVPDamageMultiplier;
        var multiplier_strength_vs_armor = (armstrength / target.GetArmorReduction()) * rule_mult;
        if (!target.body.conscious) { // if theyre not conscious they cant defend themselves
            multiplier_strength_vs_armor *= 2f;

            knockback *= 0.8f;
            if (!target.body.alive) {
                knockback *= 0.8f;
                multiplier_strength_vs_armor = armstrength * 2.0f;
                if (atk.unarmed) {
                    knockback *= 0.1f;
                }
            }
        }

        { // base damage

            target.body.traumaAmount += 0.2f * multiplier_strength_vs_armor;
            attacker.traumaAmount += Multiplayer.rules.PVPMoodDebuff;
            if (attacker.happiness > -5) {
                attacker.happiness -= Multiplayer.rules.PVPMoodDebuff;
            }


            // do simple pain ,muscle damage and shit, BTW DONT DO MUCH RNG DEPENDENT SHIT I DONT HAVE DETERMINISTIC RNG IMPLEMENTED YET  


            target.skinHealth -= atk.damage * 0.3f * multiplier_strength_vs_armor;
            target.muscleHealth -= atk.damage * 0.17f * multiplier_strength_vs_armor;
            target.bleedAmount += atk.damage * 0.02f * multiplier_strength_vs_armor;
            target.body.adrenaline += 35f;
            target.pain = Math.Max(target.pain + 5f * multiplier_strength_vs_armor, atk.damage * multiplier_strength_vs_armor);

            target.DamageWearables(atk.damage * 0.009f * multiplier_strength_vs_armor);

            target.body.DoGoreSound(); //fx

            target.body.eyeScareTime += 0.5f;
        }


        {

            if (used_tool == null) { // unarmed 
                if (attacker.clawHealth > 18f) { // claws are not too dull
                    target.skinHealth -= atk.damage * 0.17f * multiplier_strength_vs_armor;
                    target.bleedAmount += atk.damage * 0.07f * multiplier_strength_vs_armor;

                    target.DamageWearables(atk.damage * 0.005f * multiplier_strength_vs_armor);

                    target.body.eyeScareTime += 1.0f;
                }
            } else {

                if (armstrength > 0.5f) { // strong enough
                    DoToolSpecificPVPDamage(used_tool,target,rule_mult,multiplier_strength_vs_armor);
                }


                var cuttingquality = used_tool.Stats.qualities?.FirstOrDefault((CraftingQuality q) => q.id == "cutting");// check if the tool has a cutting quality
                if (cuttingquality != null) {
                    float cutmarkiplier = Math.Max(20f, cuttingquality.amount) / 100f;   // machete is 200, flimsyknife is 8 , idk man

                    //stab
                    target.skinHealth -= atk.damage * 0.6f * multiplier_strength_vs_armor * cutmarkiplier;
                    target.muscleHealth -= atk.damage * 0.02f * multiplier_strength_vs_armor * cutmarkiplier;
                    target.bleedAmount += atk.damage * 0.5f * multiplier_strength_vs_armor * cutmarkiplier;
                    target.body.adrenaline += 10f;

                    target.pain += 10f * rule_mult;

                    target.DamageWearables(atk.damage * 0.03f * multiplier_strength_vs_armor * cutmarkiplier);

                    // london is pretty scary brah
                    target.body.eyePanicTime += 2.0f;
                    target.body.eyeScareTime += 6.0f;
                }
            }


            if (armstrength > 0.5f) { // strong enough

                if (Multiplayer.rules.PVPCombatDismember && target.isHead && target.muscleHealth < 30f && target.skinHealth < 40f) { // if head is pretty damaged, disfigure em
                    if (UnityEngine.Random.value * (target.muscleHealth + target.skinHealth) / 100f < 0.1f) {

                        if (UnityEngine.Random.value < 0.7) {
                            target.body.RemoveEye();
                        } else {
                            target.body.Disfigure();
                        }

                    }
                }


            }

            

            if (target.muscleHealth <= 5f && target.skinHealth <= 5f) { // if the limb is really damaged, allow to amputate it
                if (Net.IsServer) {
                    if ((Multiplayer.rules.PVPCombatDismember || !target.body.alive) && ((target.body.IsDeadOrCriticallyDying()) || UnityEngine.Random.value < 0.6)) {
                        CombatStuff.BetterDismember(target);
                    }
                }
            }

            
            if (!target.body.conscious && target.body.isDying ) {
                if (target.body.alive) {
                    attacker.happiness -= Multiplayer.rules.PVPMoodDebuff;
                }
                if (!attacker.specialCrying && Multiplayer.rules.PVPMoodDebuff > 0) {
                    attacker.StartCoroutine("Cry");
                }
            }

        }

        var totaldamage = og_hp - (target.skinHealth + target.muscleHealth) ;

        if (Net.IsServer) {

            if (target.isVital) { // critical damage
                if (target.muscleHealth < 40f && target.skinHealth < 30f) {
                    var hp = Mathf.Clamp01(1 - (target.muscleHealth + target.skinHealth) / 70) * 2.0f + 1.0f;

                    var dmg = totaldamage * 0.03f * armstrength * hp ;
                    target.body.internalBleeding += dmg;
                }
            }
            if (target.muscleHealth < 20f && target.skinHealth < 20f) {
                if (target.isHead) {
                    var hp = Mathf.Clamp01(1 - (target.muscleHealth + target.skinHealth) / 40)*2.0f + 1.0f;

                    var dmg = totaldamage * 0.04f * armstrength * hp ;
                    target.body.brainHealth -= dmg;
                }
            }


            var total_resilience_exp = Mathf.Abs(totaldamage / 50f);

            target.body.skills.AddExp(1, total_resilience_exp); // add resilience exp to the poor bastard 

#if !RELEASE
            Plugin.log.LogWarning($"DoFriendlyFireMeleeDamage: add resilience exp to the poor bastard: {total_resilience_exp} total damage: {totaldamage} ");
#endif

        }

        if (target.TryGetNetBody(out var nb) && attacker.TryGetNetBody(out var attackerNb)) {
            nb.OnDamageEvent(new BodyDamageEvent(attackerNb, nb, used_tool, totaldamage));
        }

        return totaldamage; // return damage dealt for the damagenumber thingy
    }

     


    //OG: Body.Attack
    public static float DamageBuilding(Body attacker, AttackInfo atk, BuildingEntity be, float armstrength, bool do_sound) {


        var building_dmg = (be.animal ? atk.damage : atk.structuralDamage) * armstrength * ((atk.metalMoreDamage && be.metallic) ? 10f : 1f);

        float healthBefore = be.GetHealth();
        be.SetHealth(be.GetHealth() - building_dmg);

        if (Net.IsRunning && be != null) {
            EvidenceBag.Arm();
            EvidenceBag.Line("damage " + EvidenceBag.Side()
                + " before=" + healthBefore.ToString("0.###")
                + " minus=" + building_dmg.ToString("0.##")
                + " local=" + Util.IsBodyLocal(attacker)
                + " " + EvidenceBag.Building(be));
        }

        if (do_sound) { 
            Sound.Play(be.hitSound, be.transform.position);
        }

        if (be.animal) {
            be.gameObject.SendMessage("AnimalHit", atk.damage * armstrength);

            {
                //OG: Body.Attack V5t4  the part where it checks if the building is an animal,  since i override the attack function i have to do it here
                attacker.attackCooldown *= 3.5f * atk.attackCooldownMult;
                if (Util.IsBodyLocal(attacker)) {
                    PlayerCamera.main.lastAttackCool = attacker.attackCooldown;
                }
            }
        }

        return building_dmg;
    }


    //OG: Body.Attack V5t4  the part where it assigns that float that is later multiplied everywhere in damage output
    public static float CalculateAttackPower(in Body attacker, in AttackInfo atk) {
        float totalArmPower = 1f;

        int slot = attacker.handSlot;
        if (atk.physicalSwing) {
            totalArmPower = attacker.slots[slot].armPowerMult;
            if (slot == 1) {
                totalArmPower *= 0.75f;
            }
            totalArmPower *= 1f + attacker.skills.STRFrom10 * 0.0334f;
        }

        if (atk.unarmed) {
            totalArmPower *= attacker.clawDamageCurve.Evaluate(attacker.clawHealth);
        }
        return totalArmPower;
    }


    //OG: Body.Attack V6.0 steam playtest
    public static void BodyAttack_StolenHitEffectsOrWhatever(Body attacker, AttackInfo atk, float attackpowerscalething, Vector2 atkdir) {


        var slot = attacker.handSlot;
        if (attacker.standing) {
            attacker.rb.AddForce((Vector2)(-atkdir * atk.knockBack * attackpowerscalething), ForceMode2D.Impulse);
        } else {
            attacker.limbs[1].rb.AddForce((Vector2)(-atkdir * atk.knockBack * attackpowerscalething), ForceMode2D.Impulse);
        }
        if (atk.unarmed) {

			attacker.clawHealth -= 0.3f;
			if (attacker.clawHealth < 20f && UnityEngine.Random.value < 0.1f) {
				attacker.slots[slot].limb.skinHealth -= 3f;
				attacker.slots[slot].limb.muscleHealth -= 2f;
				attacker.slots[slot].limb.pain += 12f;
				attacker.slots[slot].limb.bleedAmount += UnityEngine.Random.Range(0.35f, 0.85f);
			}

        }
        if (atk.physicalSwing) {
            attacker.dirtyness += atk.cooldown * 1f;
            //attacker.skills.AddExp(0, atk.damage / 300f);
        }

    }



    public static bool breakTheAttackLoop = false; 


    public static Body LastAttacker = null;
    static AttackInfo LastAttackInfo = new AttackInfo();

    static Limb _LastHitLimb;
    static BuildingEntity _LastHitBuilding;

    class _MyAttackState {
        public float PlayerCamera_main_lastAttackCool = 0;
        public bool candoattack = false;
        public Vector2 targetlookpos;
        public Vector2 limb1pos;
        public AttackInfo atk;
        public Body body;
        public float atkpwr;
    }

    // return true if its not a body
    static bool BodyAttack_CheckIfItWasABody(RaycastHit2D hit) {
        var limb = hit.collider.GetComponent<Limb>();
        var body = hit.collider.GetComponent<Body>();
        if (limb == null) {
            if (body == null) {
                return false; // its not a body
            } else {
                limb = body.GetClosestLimb(hit.point);
            }
        } else {
            body = limb.body;
        }
        // CHECK IF FRIENDLY FIRE IS ALLOWED HERE

        if (Util.IsBodyLocal(body)) {
            return false; // cant hit myself brah, selfharm does its own thang
        }

        var me = LastAttacker.GetNetBody();
        var npc = body.GetNetBody();

        if (me.CanAttackThisGuy(body)) { // can hit em
            _LastHitLimb = limb;

            breakTheAttackLoop = true; // i assume piercing will never be a thing

            return true; // tell it that it was a hit
        }


        return false;
    }

    static bool BodyAttack_CheckIfItWasABuilding(RaycastHit2D hit) {
        var b = hit.collider.GetComponent<BuildingEntity>();
        if (b != null ) {
            if (b.cantHit) {
                return false;
            }
            var islocal = Util.IsBodyLocal(LastAttacker);
            var isphysactive = ScavMPBuildingSynchronizer.BuildingHasActivePhysics(b);

            if (islocal) {
                if (b.TryGetSyncInfo(out var si)) {
                    si.SetIgnoreTimeForRoundTrip();
                }
            }

            if (StaticHitValidation && isphysactive) {
                return true; // skip it
            }
      
            if (isphysactive) {
                _LastHitBuilding = b;
                breakTheAttackLoop = true;  // i assume piercing will never be a thing
                return true; // i will apply damage myself im my handlers
            }



            return false;// do the damage for static hit validation
        }
        return false;
    }
    
   public  static bool StaticHitValidation = false;

    // return false to do the vanilla shit
    public static bool BodyAttack_DidHitSomething(RaycastHit2D hit) {
        if (!Net.IsRunning) return false;

        if (!StaticHitValidation && BodyAttack_CheckIfItWasABody(hit)) { 
            return true;
        }
        if (BodyAttack_CheckIfItWasABuilding(hit)) {
            return true;
        }
            
        // let it apply all the damages and allat

        return false;
    }


    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {

        var codes = new List<CodeInstruction>(instructions);

        int i = 0;

        i += 100;

        while (!(
            codes[i - 5].opcode == OpCodes.Call &&
            codes[i - 4].opcode == OpCodes.Ldc_I4_1 &&
            codes[i - 3].opcode == OpCodes.Stloc_2 &&
            codes[i - 2].opcode == OpCodes.Ldarg_1 &&
            codes[i - 1].opcode == OpCodes.Ldfld &&
            codes[i].opcode == OpCodes.Brfalse &&

            codes[i - 1].ToString().Contains("piercing") && 
            codes[i - 5].ToString().Contains("CreateCloudSmall")
        )) {
            i++;
        }

        var realhit_check_pos = i;
        var piercingvar_pos = i - 1;
        var breakjump_pos = i;

        if (log.verbose) log.l($"Body.Attack TRANSPILER found the thing check {i}"); // expected around 468

        var continuejump_pos = realhit_check_pos+1; // cuz theres a continue very close
        while (!((codes[continuejump_pos].opcode == OpCodes.Brtrue || codes[continuejump_pos].opcode == OpCodes.Brfalse)  )) {
            continuejump_pos++;
        }
#if DEBUG
        if (log.verbose)
            log.l($"Body.Attack FOUND THE continuejump_pos: {continuejump_pos} ");
        log.l($"continuejump_pos code: {codes[continuejump_pos]} ");
#endif

        var rayhitresult_pos = realhit_check_pos; //
        while (!((codes[rayhitresult_pos].opcode == OpCodes.Ldloca_S ))) {
            rayhitresult_pos++;
        }
#if DEBUG
        if (log.verbose)
            log.l($"Body.Attack FOUND THE rayhitresult_pos: {rayhitresult_pos} ");
        log.l($"rayhitresult_pos code: {codes[rayhitresult_pos]} ");
#endif


        {
            //ldloc.s	raycastHit2D (10)   // take variable refernce from  rayhitresult_pos 
            //call	bool [KrokoshaCasualtiesMP]KrokoshaCasualtiesMP.BodyAttackMultiplayerAnnouncePatch::BodyAttack_DidHitSomething(valuetype [UnityEngine.Physics2DModule]UnityEngine.RaycastHit2D)
            //brfalse.s	473 (0595) ldloca.s raycastHit2D (10)   // this is just right next line, so create a new label pointing to  realhit_check_pos + 1 
            //ldarg.1
            //ldfld	bool AttackInfo::piercing
            //brtrue	628 (077A) ldloc.s V_6 (6)  // take label from continuejump_pos
            //br	637 (078B) ldloc.2     //take label from breakjump_pos


            var newInstructions = new List<CodeInstruction>();

            var firstinstruction = new CodeInstruction(OpCodes.Ldloc_S, codes[rayhitresult_pos].operand);
            firstinstruction.MoveLabelsFrom(codes[realhit_check_pos + 1]); // because the comparetagcheck jumpts there, i need to move the label
            newInstructions.Add(firstinstruction);

            newInstructions.Add(new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(Body_Attack_MultiplayerPatch), nameof(Body_Attack_MultiplayerPatch.BodyAttack_DidHitSomething))));

            
            var endofpatch_label = il.DefineLabel();
            codes[realhit_check_pos + 1].labels.Add(endofpatch_label);

            newInstructions.Add(new CodeInstruction(OpCodes.Brfalse_S, endofpatch_label));

            newInstructions.Add(new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Body_Attack_MultiplayerPatch), nameof(breakTheAttackLoop))));

            newInstructions.Add(new CodeInstruction(OpCodes.Brfalse, codes[continuejump_pos].operand));

            newInstructions.Add(new CodeInstruction(OpCodes.Br, codes[breakjump_pos].operand));

            codes.InsertRange(realhit_check_pos + 1, newInstructions);


#if DEBUG 
            for (int j = realhit_check_pos - 4; j < newInstructions.Count() + 7 + 8; j++) {
                log.l($"DUMP1: {j}: {codes[j]} ");
            }
#endif
        }



        if (log.verbose) {
            Plugin.log.LogWarning($"TRANSPILER: Body.Attack TRANSPILER SUCCESS :) ");
        }
        return codes;
    }




    static bool LastAttackResult = false;
    public static bool ForceOnlyStaminaUse = false;
    public static bool ForceOnlyRetrieveAttackInfo = false;

    static bool Prefix(Body __instance, ref bool __result, ref AttackInfo atk, int slot, ref _MyAttackState __state) {
        
        LastAttackInfo = atk;
        LastAttacker = __instance;

        __state = new _MyAttackState {
            PlayerCamera_main_lastAttackCool = PlayerCamera.main.lastAttackCool,
            targetlookpos = __instance.targetLookPos,
            limb1pos = __instance.limbs[1].transform.position,
            atk = atk,
            atkpwr = CalculateAttackPower(__instance, LastAttackInfo),
            body = __instance,
        };// only possible thing to change that i dont really want for remote peers



        if (ForceOnlyRetrieveAttackInfo) {
            ForceOnlyRetrieveAttackInfo = false;
            return false;
        }

        breakTheAttackLoop = false;

#if DEBUG
        if (__instance.conscious && __instance.attackCooldown <= 0f) {
            log.l($"ATTACC  {Time.time}  ");
        }
#endif



        if (ForceOnlyStaminaUse) { // so it just stamina use validation 
            atk = atk.CloneViaSerialization();
            atk.distance = 0;
            ForceOnlyStaminaUse = false;
        }


        if (__instance.conscious && __instance.attackCooldown <= 0f) { // actually attacking 
            __state.candoattack = true;

            if (Net.IsRunning && Util.IsWorldGenerated()) {

                // what did i even want to do here 

            }
        }

        {
            if (__instance.TryGetNetBody(out var nb) && __state.candoattack) {
                nb.RollAttackRandom();
            }
        }

        return true;
    }
    static void Postfix(Body __instance, ref bool __result, AttackInfo atk, int slot, ref _MyAttackState __state) {
        LastAttackResult = __result;

        StaticHitValidation = false;
        breakTheAttackLoop = false;

        var attackerIsLocal = Util.IsBodyLocal(__instance);

        if (!attackerIsLocal) {
            PlayerCamera.main.lastAttackCool = __state.PlayerCamera_main_lastAttackCool;// only possible thing to change that i dont really want for remote peers, reset it if its not local body
            if (__state.candoattack) {// just to account for packet jitter
                __instance.attackCooldown *= 0.9f;
                __instance.attackCooldown -= 0.05f;
            }
        }

        if (__state.candoattack) { // actually attacked
            if (Net.IsRunning && Util.IsWorldGenerated()) {

                if (_LastHitLimb != null) {
                    if (!_LastHitLimb.body.alive) { // if its cannibalizm, force hit the most far away body parts for maximum meat output .  aw hell nah wtf am i talkin bout 
                        _LastHitLimb = CombatStuff.CannibalismGetFarthestLimbToBeEfficient(_LastHitLimb);
                    }
                }

                var holditem = __instance.GetItem(__instance.handSlot);

                // me, the client, should do the attacks before server validates them  (and the server will just work instantly for validation)
                if (Net.IsClient) {
                    if (_LastHitLimb != null) {

                        var totaldmg = DoPVPMeleeDamage(__instance, _LastHitLimb, __state.atk, __state.atkpwr, Body_UseItem_MultiplayerPatch.current_used_item ?? holditem, out var knock);
                        WorldGeneration.CreateDamageNumber((Vector2)_LastHitLimb.body.limbs[0].transform.position + Vector2.up, (int)totaldmg);

                        DoKnockback(_LastHitLimb.body, new BodyKnockbackPacket { Velocity = knock });

                        if (Net.IsClient && _LastHitLimb.TryGetNetBody(out var pb) && attackerIsLocal) {
                            pb.SetNetHealthSyncIgnoreTime();
                        }
                        
                    } else if (_LastHitBuilding != null) {

                        var totaldmg = DamageBuilding(__instance, __state.atk, _LastHitBuilding, __state.atkpwr, true);
                        WorldGeneration.CreateDamageNumber((Vector2)_LastHitBuilding.transform.position + Vector2.up * 2, (int)totaldmg);

                        if (Net.IsClient && NetObjectRegistry.TryGetSyncInfo(_LastHitBuilding, out  var b_si)) {
                            b_si.SetIgnoreTimeForRoundTrip();
                            EvidenceBag.Line("hit client ignore sync=" + b_si.syncId + " " + EvidenceBag.Building(_LastHitBuilding));
                        } else if (_LastHitBuilding != null) {
                            EvidenceBag.Line("hit client no sync " + EvidenceBag.Building(_LastHitBuilding));
                        }
                    }

                }

                if (Util.IsBodyLocal(__instance) ) {
                    AnyObjectNetId victim_netid = new AnyObjectNetId();
                    {
                        if (_LastHitLimb != null) {
                            if (_LastHitLimb.TryGetNetBody(out var pb)) {
                                victim_netid = new AnyObjectNetId(_LastHitLimb);
                            }
                        } else if (_LastHitBuilding != null) {
                            if (NetObjectRegistry.TryGetSyncInfoOrRegister(_LastHitBuilding, out var victim_si)) {
                                victim_netid = new AnyObjectNetId(victim_si);
                                victim_si.relaxed_combat_sync = true; 
                                victim_si.SetIgnoreTimeForRoundTrip();
                            }
                        }
                    }



                    // tell server about hits or no hits  
                    {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerBodyAttack);

                        writer.Put((Vector2)(__state.targetlookpos));
                        writer.Put((Vector2)(__state.limb1pos));
                        writer.Put(__state.atk.distance);

                        writer.Put(__state.atk.unarmed);

                        if (Body_UseItem_MultiplayerPatch.current_used_item != null && NetObjectRegistry.TryGetSyncInfo(Body_UseItem_MultiplayerPatch.current_used_item.gameObject, out var si1)) {
                            writer.Put(si1.syncId);

                            si1.SetIgnoreTimeForRoundTrip();
                        } else if ( holditem != null && holditem.Stats.usable && CombatStuff.ItemIsUsedForAttacking(holditem) && NetObjectRegistry.TryGetSyncInfo(holditem.gameObject, out var si)) {
                            writer.Put(si.syncId);

                            si.SetIgnoreTimeForRoundTrip();
                        } else {
                            writer.Put((knetid)0);
                        }

                        writer.Put(__result); // attack did hit, but it doesnt really mean nothing 

                        writer.Put(victim_netid);

                        if (_LastHitBuilding != null)
                            EvidenceBag.Line("hit send " + EvidenceBag.Side() + " victim=" + victim_netid + " " + EvidenceBag.Building(_LastHitBuilding));

                        //writer.Truncate();

                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                        // WRITER END;

#if DEBUG
                        log.l($"attacker.stamina {__instance} >>> {__instance.stamina}  ");
#endif
                    }

                } 
            }
        }
        _LastHitBuilding = null;
        _LastHitLimb = null;
    }
    
    
    
    
    // this entire melee system is dependent on the fact that no weapon has the piercing flag :skullemoji:
    [ServerReceiver((byte)NetmsgId.SERVER_PlayerBodyAttack)]
    static void ServerReceiver_PlayerBodyAttack(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        bool worldReady = Util.IsWorldGenerated();
        bool gotAttacker = ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb);
        bool attackerAwake = gotAttacker && plr.body != null && plr.body.conscious;
        if (worldReady && gotAttacker && attackerAwake) {

            if (plr.body.attackCooldown > 0 && !pb.IsLocal) {

                EvidenceBag.Line("hit server cooldown client=" + clientId + " cd=" + plr.body.attackCooldown.ToString("0.###"));
                if (log.verbose) // TODO, for some reason it engages even when not sus !!
                    log.sus($"PlayerBodyAttack {plr} is attacking too fast, cur cooldown: {plr.body.attackCooldown}");

                return;
            }


            reader.Get(out Vector2 targetlookpos);
            reader.Get(out Vector2 limb1pos);
            reader.Get(out float dist_dont_use); // ignore dis why did i even make dis
            reader.Get(out bool unarmed);
            reader.Get(out knetid attackitem_syncid);
            reader.Get(out bool didhit); // unused?
            reader.Get(out AnyObjectNetId hitobj);
             // later is read netlimb or buildingid

            if (targetlookpos.IsFinite() && limb1pos.IsFinite() && dist_dont_use.IsFinite()) {
            } else {
                limb1pos = KM.ClampDistance(limb1pos, pb.transform.position, 5);
            }


            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: PlayerBodyAttack {clientId} to didhit:{didhit}  unarmed:{unarmed} hitobj:{hitobj}");

            SyncInfo tool_si;
            if (!unarmed && NetObjectRegistry.TryGetSyncInfo(attackitem_syncid, out tool_si) && tool_si.IsItem() && CombatStuff.ItemIsUsedForAttacking(tool_si.item) && ItemSync.CheckIfBodyReachThisItem(tool_si, plr.body, 20, true)) {
#if DEBUG
                log.l($"actually using a tool, ok?  {tool_si}  ");
#endif
                if (plr.body.GetItem(plr.body.handSlot) != tool_si.item) {

                    // i think i dont need to do anything here anymore
                }
            } else {
                tool_si = null;
            }


#if DEBUG
            log.l($"RECEIVED ATAK !!! {plr}  {hitobj}   tool_si: {tool_si} ");
#endif


            ForceOnlyRetrieveAttackInfo = true;
            if (tool_si == null) {
#if DEBUG
                Plugin.log.LogWarning($"DOING UNARMED {CombatStuff.default_unarmed_attackinfo?.damage }");
#endif
                plr.body.Attack(CombatStuff.default_unarmed_attackinfo, plr.body.handSlot);
            } else {
#if DEBUG
                Plugin.log.LogWarning($"DOING TOOLED ");
#endif
                ItemSync.BetterUseItem(pb.body, tool_si.item);
            }
#if DEBUG
            Plugin.log.LogWarning($"COPYING atkinfo {LastAttackInfo?.damage} ");
#endif
            var atkinfo = LastAttackInfo.CloneViaSerialization();


            var atkdir = KM.NormalTo(limb1pos, targetlookpos);
            var attackpower = CalculateAttackPower(pb.body, atkinfo);


#if DEBUG
            log.l($"{plr}  {hitobj}   attackpower: {attackpower} ");
#endif

            bool didhitconfirmed = false;

            if (hitobj.IsNothing()) { // At most we hit something static
                Body_Attack_MultiplayerPatch.StaticHitValidation = true; // client doesnt tell if they hit static buildings or blocks, server can figure it out on their own
            } else if (hitobj.TryGetLimb(out var victimlimb)) {
                Body_Attack_MultiplayerPatch.ForceOnlyStaminaUse = true; // we know the hit

                if (
                    victimlimb.TryGetNetBody( out var victimpb) 
                    && KM.DistanceCheck(plr.body, victimpb.body, 15)
                ) {

                    if (pb.CanAttackThisGuy(victimpb.body)) {


                        var totaldmg = DoPVPMeleeDamage(plr.body, victimlimb, atkinfo, attackpower, tool_si?.item, out var knock);

                        if (victimpb.IsPlayer) {
                            var wr = Net.CreateWriter(NetmsgId.CLIENT_Knockback_thing);
                            wr.Put(new BodyKnockbackPacket {
                                Velocity = knock,
                                CloseUI = true,
                            });
                            Net.Server_SendToClients(DeliveryMethod.Unreliable, wr, victimpb.plr.playerId);
                        }
                    
                        if (!victimpb.IsLocal) {
                            DoKnockback(victimlimb.body, new BodyKnockbackPacket { Velocity = knock });
                        }

                        WorldGeneration.CreateDamageNumber(victimpb.GetHeadPos() + Vector2.up, (int)totaldmg);

                        MedicalSync.Server_QueueSendCharacterHealth(victimpb, true);
                        didhitconfirmed = true;
                    }
                    
                }

            } else if (hitobj.TryGetSyncInfo(out var building_si) && building_si.IsBuilding()) {
                Body_Attack_MultiplayerPatch.ForceOnlyStaminaUse = true; // we know the hit
#if DEBUG
                log.l($"{plr}  CHECKING CombatStuff.AttackHitType.Building ");
#endif

                bool inRange = KM.DistanceCheck(plr.body, building_si, 15);
                float healthBefore = building_si.building != null ? building_si.building.GetHealth() : -1f;
                if (inRange) {
#if DEBUG
                    log.l($"{plr}  CALLED DamageBuilding ");
#endif
                    var totaldmg = DamageBuilding(plr.body, atkinfo, building_si.building, attackpower, true);
                    didhitconfirmed = true;


                    WorldGeneration.CreateDamageNumber((Vector2)building_si.building.transform.position + Vector2.up * 2, (int)totaldmg);


                    //NetObjectRegistry.Server_QueueSync(building_si);
                    EvidenceBag.Line("hit server apply dmg=" + totaldmg.ToString("0.##") + " before=" + healthBefore.ToString("0.###") + " client=" + clientId + " " + EvidenceBag.Building(building_si.building));
                } else {
                    EvidenceBag.Line("hit server range client=" + clientId + " before=" + healthBefore.ToString("0.###") + " " + EvidenceBag.Building(building_si.building));
                }
            } else if (hitobj.type == 3) {
                EvidenceBag.Line("hit server unresolved client=" + clientId + " " + hitobj);
            } 


#if DEBUG
            log.l($"{plr}  {hitobj} applied damages ");
#endif

            LastAttackResult = true; // reset it ig man


            plr.body.limbs[1].transform.position = limb1pos;
            plr.body.targetLookPos = targetlookpos;
            if (tool_si == null) {
                plr.body.Attack(CombatStuff.default_unarmed_attackinfo, plr.body.handSlot);
            } else {
                ItemSync.BetterUseItem(pb.body, tool_si.item);
            }

            if (!LastAttackResult && // if the flag wasnt true
                didhitconfirmed// if friendly fire or building damage validated and occured
            ) {
                BodyAttack_StolenHitEffectsOrWhatever(pb.body, atkinfo, attackpower, atkdir); // do the clawhealth use, give xp and allat
            }

            //  send an attack relay to the clients 

            var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerBodyAttack_Relay);

            writer.Put(clientId);
            writer.Put(tool_si != null);
            writer.Put((knetid)(tool_si == null ? 0 : tool_si.syncId));
            writer.Put(limb1pos);
            writer.Put(targetlookpos);

#if DEBUG
            log.l($"sending PlayerBodyAttack_Relay {plr} {tool_si}  ");
#endif

            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

            // WRITER END;




        } else {
            EvidenceBag.Line("hit server drop client=" + clientId + " world=" + worldReady + " player=" + gotAttacker + " conscious=" + attackerAwake);
        }
    }



    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerBodyAttack_Relay)]
    static void Client_ReceiveAndVisualizePlayerBodyAttack(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid attacker);
        reader.Get(out bool not_unarmed);
        reader.Get(out knetid tool_syncid);
        reader.Get(out Vector2 limb1pos);
        reader.Get(out Vector2 targetlookpos);

        if (
                     ScavPlayer.TryGetPlayerAndNetBodyFromClientId(attacker, out var plr, out var pb) 
        ) {
            pb.body.attackCooldown = 0;

#if DEBUG
            Plugin.log.LogWarning($"PlayerBodyAttack_Relay  ACTUALLY WORKSKKSKSKSKSKSKS attacker:{plr} ");
#endif

            SyncInfo tool_si;
            if (not_unarmed && NetObjectRegistry.TryGetSyncInfo(tool_syncid, out tool_si) && tool_si.go != null && tool_si.IsItem() && CombatStuff.ItemIsUsedForAttacking(tool_si.item) ) {
#if DEBUG
                Plugin.log.LogWarning($"PlayerBodyAttack_Relay  CONFIRMED THAT THEY USED A TOOL FOR ATTACK  {tool_si}  ");
#endif
            } else {
                tool_si = null;
            }


            // just do the attack, everything hit

            plr.body.limbs[1].transform.position = limb1pos;
            plr.body.targetLookPos = targetlookpos;
            if (tool_si == null) {
                plr.body.Attack(CombatStuff.default_unarmed_attackinfo, plr.body.handSlot);
            } else {
                ItemSync.BetterUseItem(pb.body, tool_si.item);
            }

        }
    }




    [ClientReceiver((byte)NetmsgId.CLIENT_Knockback_thing, false)]
    static void Client_ReceiveKnockback(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out BodyKnockbackPacket knockback);

        var locsci = ScavPlayer.LocalPlayer;
        if (
            locsci != null && locsci.body
        ) {
            
            DoKnockback(locsci.body, knockback);
        }
    }

    static void DoKnockback(Body body, BodyKnockbackPacket knockback) {

        //body.grounded = false;
        //body.liquidSlipTime += 0.1f; // doesnt change much

        if (Util.IsBodyLocal(body)) {

            PlayerCamera.main.shaker.Shake(knockback.Velocity.magnitude);

            if (knockback.CloseUI) {
                MinigameBase.main.EndMinigame();
                Util.ToggleWoundView(false);
            }
        }


        if (body.standing) {
            body.rb.velocity += knockback.Velocity * 1.6f;
            body.lastTimeStepVelocity = body.rb.velocity;
            return;
        }

        Limb[] array = body.limbs;
        for (int i = 0; i < array.Length; i++) {
            array[i].rb.velocity += knockback.Velocity;
        }
        
    }


}
















// !!! current solution relies that   shotsPerFire is only used once in the gun script!!!!

public class TrackerGunScript : BaseComponentTracker<GunScript> {
    public int shotsPerFire { get; private set; }
    public SyncInfo si => tracker.syncInfo;
    public BodyGetterOverrider bgo => this.GetOrAddComponent<BodyGetterOverrider>();
    public Item item => this.GetComponent<Item>();
    public NetBody pbody {
        get {
            var b = bgo.body;
            if (b != null) {
                return b.GetNetBody();
            }
            return null;
        }
        set {
            bgo.body = value.body;
        }
    }
    public Body body {
        get {
            return bgo.GetBody();
		}
        set {
            bgo.body = value;
		}
    }

    public bool isEquipped {
        get {
            var b = body;
            if (b == null) {
                return false;
            }
            return body.GetItem(body.handSlot)?.transform == transform;
        }
    }

    public bool override_shoot_trajectory = false;
    public Vector2 override_shoot_trajectory_origin = Vector2.left;
    public Vector2 override_shoot_trajectory_direction = Vector2.left;

    public bool send_racked_state = false; // queue to send a racked state, set from TryRack 


    // this calls the body getter overrider
    public Body GetBody() {
        return body;
    }

    public bool IsShotgun() {
        return shotsPerFire > 2;
    }


    protected override void TrackerAwake() {   
        shotsPerFire = og.shotsPerFire;
        og.shotsPerFire = 0; // the gun wont shoot anymore


        InvokeRepeating("SlowUpdate", 1.0f, 0.1f);
    }



    public void ScanForBody() {
        if (bgo.TryGetBodyFromParent()) {
            return;
        }

        if (ScavPlayer.GetDistanceToNearestLivingPlayer(transform.position).Item1 is var plr && plr != null) {
            pbody = plr.netBody;
            return;
        }

        body = PlayerCamera.main.body;
    }

    // run every 0.1 secs
    void SlowUpdate() {

        if (gameObject.TryGetTracker(out var tracker) && tracker.isNearby ) { // gun is equipped

            if (isEquipped) {

                if (Multiplayer.rules.PVP) { // do the scare effect i thought about if having gun close to a face
                    float rightorleft = (body.isRight ? 1f : (-1f));
                    var gunlook = og.transform.right * rightorleft;

                    var rayhits = Physics2D.RaycastAll(og.barrel.position, gunlook, 4f);
                    foreach (var rayhit in rayhits) {
                        Limb limb = null;
                        if (!(rayhit.collider.TryGetComponent<Limb>(out limb) && limb.isVital)) {
                            limb = null;
                        }
                        if (limb == null) {
                            if (rayhit.collider.TryGetComponent<NetBody>(out var bbb)) {
                                var reallook = KM.NormalTo(og.barrel.position, bbb.GetHeadPos());
                                
                                if (Vector2.Dot(gunlook, reallook) > 0.9) {
                                    limb = bbb.head;
                                }
                            }

                            if (limb == null) {
                                continue;
                            }
                        }
                        if (limb.body != body && limb.body.happiness > -40) {
                            limb.body.eyeScareTime = Math.Max(limb.body.eyeScareTime, 0.3f);
                        }
                    }
                }
            }

        }


    }


    void FixedUpdate() {
        if (
            pbody != null //&& pbody.is_local
        ) {
            if (isEquipped && pbody.piggybacking_on != null && !body.standing) {

                var flipped = body.isRight ? 1f : -1f;

                // PIGGYBACK, AIM THE GUN,  ROTATE THE RAGDOLL HANDS 
                var hand = body.slots[body.handSlot].limb;

                var direction = (Vector2)body.targetLookPos - hand.rb.position;

                if (direction.sqrMagnitude > 2 && hand.rb.simulated) {

                    float targetAngle = Mathf.Atan2(direction.x, -direction.y) * Mathf.Rad2Deg;

                    float anglediff = (targetAngle - hand.rb.rotation);

                    var delta = 400f * Time.fixedDeltaTime * hand.totalForce * Mathf.Clamp01(1 - og.gasTime * 4);
                    float anglestepped = Mathf.MoveTowardsAngle(hand.rb.rotation, targetAngle, delta * (30 + Mathf.Abs(anglediff)));

                    hand.rb.MoveRotation(anglestepped);

                    //if (Mathf.Abs(anglediff) > 2f) 
                    {
                        var wrist = hand.connectedLimbs[0];
                        var arm = wrist.connectedLimbs[0];

                        if (wrist.rb.simulated)
                            wrist.rb.MoveRotation(Mathf.MoveTowardsAngle(wrist.rb.rotation, targetAngle + 20f * flipped, delta * 8f));
                        if (arm.rb.simulated)
                            arm.rb.MoveRotation(Mathf.MoveTowardsAngle(arm.rb.rotation, targetAngle - 20f * flipped, delta * 12f));
                    }
                    hand.rb.MoveRotation(anglestepped);
                }
            }
        }
    }
    public void ApplyRecoil() {
        if (pbody?.piggybacking_on != null) {
            var flipped = body.isRight ? 1f : -1f;

            var hand = body.slots[body.handSlot].limb;
            hand.rb.velocity += (Vector2)hand.transform.right * (flipped * og.knockBack * 8);
            hand.rb.rotation += flipped * 0.1f;
        }
    }


    bool _thing_to_emulate_firingPinStruck_only_here = false;
    void Update() {
        if (pbody && pbody.IsLocal) {
            if (send_racked_state) {
                if (og.racked == og.lastRacked) { // its concrete
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_GunRack, og.racked, true);
                    send_racked_state = false;
                }
            }
            //OG: GunScript.Update V5t4
            if ( og.firingPinStruck && !_thing_to_emulate_firingPinStruck_only_here ) {
                _thing_to_emulate_firingPinStruck_only_here = true;

                if (og.gasTime > 0) {
                } else {
                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.transformPosition, $"C sending: GunEmptyClick ");
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_GunEmptyClick, tracker.syncId, false);
                }
            } else {
                _thing_to_emulate_firingPinStruck_only_here = og.firingPinStruck;
            }
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_GunEmptyClick)]
    static void Server_GunEmptyClick(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid gun_syncid);

#if DEBUG
        Plugin.log.LogInfo($"SERVER: received GunEmptyClick");
#endif

        if (
            ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var shooter) && shooter.body.conscious &&
            ItemSync.TryGetItemSyncInfo(gun_syncid, out var gun_si) && gun_si.IsGun() &&
            ItemSync.CheckIfBodyReachThisItem(gun_si, shooter.body)
        ) {
            var gst = gun_si.go.GetOrAddComponent<TrackerGunScript>();

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(gun_si.transformPosition, $"S: GunEmptyClick {DebugHelp.GetPlayerFullDebugString(clientId)}");

            ServerMain.Server_AnnounceSound(gst.transform.position, "guntrigger", ServerMain.GetListOfClientIdsExceptThis(clientId) );
        }
    }






#if (!RELEASE) 
    void OnGUI() {
        if (log.verbose && isEquipped) {
            var ogfont = GUI.skin.label.fontSize;

            var pos = (Vector2)Camera.main.WorldToScreenPoint(transform.position);
            pos = new Vector2(pos.x, Screen.height - pos.y);

            if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {
                const float bsize = 17;
                const float elsize = 13;

                float y = pos.y;

                GUI.skin.label.normal.textColor = UnityEngine.Color.white;

                GUI.skin.label.fontSize = 10;
                GUI.Label(new Rect(pos.x, y, 400, bsize), "chamber: " + og.roundInChamber.ToString());
                y += elsize;
                GUI.Label(new Rect(pos.x, y, 400, bsize), "ammo: " + og.roundsInMag);
                y += elsize;
                GUI.Label(new Rect(pos.x, y, 100, bsize), "racked: " + og.racked);
                y += elsize;
#if (!RELEASE) 
                if (pbody != null && pbody.syncBody.drng_attackCounter != null) {
                    GUI.Label(new Rect(pos.x, y, 100, bsize), "rroll: " + pbody.syncBody.drng_attackCounter.Value);
                    y += elsize;
                }
#endif
            }


            GUI.skin.label.normal.textColor = UnityEngine.Color.white;
            GUI.skin.label.fontSize = ogfont;
        }
    }
#endif

}


[HarmonyPatch(typeof(GunScript), nameof(GunScript.Fire))]
public static class GunScript_Fire_MultiplayerPatch {

    public static int MAX_HITS_PER_LIMB_AT_ONCE = 4;



    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        

        // We convert the instructions to a list so we can more easily navigate them
        var instructionList = instructions.ToList();
        int i = 0;

        bool didit = false;

        while (i < instructionList.Count) {
            var code = instructionList[i];
            if (
                code.opcode == OpCodes.Call && 
                code.operand is MethodInfo method && 
                method != null && 
                method.Name == "get_body" && 
                method.DeclaringType == typeof(GunScript)
            ) {
                
                var getComponentMethod = typeof(Unity.VisualScripting.ComponentHolderProtocol).GetMethod(nameof(Unity.VisualScripting.ComponentHolderProtocol.GetOrAddComponent), BindingFlags.Static | BindingFlags.Public);
                var getComponentGeneric = getComponentMethod.MakeGenericMethod(typeof(TrackerGunScript));

                var getTheOtherPartMethod = typeof(TrackerGunScript).GetMethod(nameof(TrackerGunScript.GetBody), BindingFlags.Public | BindingFlags.Instance);

                yield return new CodeInstruction(OpCodes.Call, getComponentGeneric); // get the component
                yield return new CodeInstruction(OpCodes.Callvirt, getTheOtherPartMethod); // call GetBody
                i ++; 
                didit = true;
#if DEBUG
                Plugin.log.LogInfo($"GunScript_Fire_MultiplayerPatch: Succesfully patched the body getters {i} ");
#endif
            } else {
                yield return instructionList[i];
                i++; 
            }
        }
        if (!didit) {
            Plugin.log.LogError($"GunScript_Fire_MultiplayerPatch: Failed to patch the body getters ");
        }
    }



    //TODO my custom randomizer thing
    public static Vector2 RollVerticalSpread(GunScript gun) {
        return gun.transform.up * (UnityEngine.Random.Range(-1f, 1f) * gun.verticalSpread);
    }


    public static void Prefix(GunScript __instance, bool suicide, ref GunScript __state ) {
        __state = GunScript_Update_MultiplayerPatch.current_executing_gun;
        if (__state == null) {
            GunScript_Update_MultiplayerPatch.current_executing_gun = __instance;
        }

        var gst = __instance.GetOrAddComponent<TrackerGunScript>();

        var shotsPerFire = __instance.shotsPerFire;
        var body = PlayerCamera.main.body;
        if (gst != null) {
            gst.ScanForBody();
            body = gst.body;
            shotsPerFire = gst.shotsPerFire;

            gst.ApplyRecoil();
        }
        var npc = body.GetNetBody();
        var islocal = Util.IsBodyLocal(body);

    }
    static void Postfix(GunScript __instance, bool suicide, ref GunScript __state) {
        try {
            _GunFirePostfix_CheckForHitsAndTellServer(__instance, suicide);
        } catch (Exception e) {
            log.error(e);
        }

        GunScript_Update_MultiplayerPatch.current_executing_gun = __state;
    }



    static void _GunFirePostfix_CheckForHitsAndTellServer(GunScript gun, bool suicide) {

        var gst = gun.GetOrAddComponent<TrackerGunScript>();

        var shotsPerFire = gun.shotsPerFire;
        var body = PlayerCamera.main.body;
        if (gst != null) {
            gst.ScanForBody();
            body = gst.body;
            shotsPerFire = gst.shotsPerFire;
        }
        var npc = body.GetNetBody();
        var islocal = Util.IsBodyLocal(body);

        List<SyncInfo> hitBuildings = new(); // guranteed to be only physics active buildings
        List<Limb> hitLimbs = new();


        if (!islocal && !suicide) {
            TurretScript_Shoot_MultiplayerPatch.HitOnlyStatic = true;
        }


        //OG: GunScript.Fire  V5t4
        float rightorleft = (body.isRight ? 1f : (-1f));
        for (int i = 0; i < shotsPerFire; i++) {
            TurretScript_Shoot_MultiplayerPatch.LastActiveShooterIsSuicide = suicide;
            TurretScript_Shoot_MultiplayerPatch.LastActiveShooter = body.GetNetBody();
            TurretScript_Shoot_MultiplayerPatch.LastActiveShooterGun = gst;

            if (gst.override_shoot_trajectory) {

                TurretScript.Shoot(new FireInfo {
                    pos = (gst.override_shoot_trajectory_origin), // i changed this to be only barrel position
                    dir = ((((gst.override_shoot_trajectory_direction) + RollVerticalSpread(gun) * rightorleft))),
                    doTinnitus = false,
                    ignoreBody = false, // always false, my transpiled hooks will handle the suicide condition
                    structureDamage = gun.structureDamage,
                    animalDamage = gun.animalDamage,
                    forceHead = false // suicide is handled differently here
                });
            } else {

                TurretScript.Shoot(new FireInfo {
                    pos = (gun.barrel.position), // i changed this to be only barrel position
                    dir = (suicide ? KM.NormalTo(gun.barrel.position, body.limbs[(int)Body.LimbNum.Head].rb.position) :  // this is my addition, to avoid forceHead
                        ((((Vector2)gun.transform.right) + RollVerticalSpread(gun)) * rightorleft)),
                    doTinnitus = false,
                    ignoreBody = false, // always false, my transpiled hooks will handle the suicide condition
                    structureDamage = gun.structureDamage,
                    animalDamage = gun.animalDamage,
                    forceHead = false // suicide is handled differently here
                });
            }

            hitBuildings.AddRange(TurretScript_Shoot_MultiplayerPatch.JustHitBuildings);
            hitLimbs.AddRange(TurretScript_Shoot_MultiplayerPatch.JustHitLimbs);
        }

        hitLimbs = Util.LimitMaxRepeats(hitLimbs, Math.Min(gst.shotsPerFire, MAX_HITS_PER_LIMB_AT_ONCE)); // limit it so the server wouldnt get mad 

        hitLimbs = hitLimbs.Where(l => {
            if (l.body.TryGetNetBody(out var vvvvv)) {
                return true;
            } else {
                return false;
            }
        }).ToList();

        gst.override_shoot_trajectory = false;
        gst.send_racked_state = true; // idk man, the jamming mechanic will be hard to sync but whatever  


        if (Multiplayer.IsNetworkActiveAndIsClient()) { // server will handle the damage in its validator
            // apply all the damage
            TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst, hitLimbs, hitBuildings);
        }



        if (Net.IsRunning) {

            if (islocal) {
                gst.si.SetIgnoreTimeForRoundTrip(0.4f);


                // suicide is handled by server anyway, so client has to do at least the visuals
                if (!suicide) {

#if !RELEASE 
                    if (hitBuildings.Count > 0) log.l($"GUN SHOOT, HIT BUILDINGS: {String.Join(" ", hitBuildings)} ");
                    if (hitLimbs.Count > 0) log.l($"GUN SHOOT, HIT LIMBS: {String.Join(" ", hitLimbs)} ");
#endif       

                    // gun syncid
                    // start
                    // end
                    // hit body count (max 30) 
                    // player ids and limb index
                    // hit building count (max 10)
                    // buildings syncids (they must be physics active, like monsters)



                    var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerBodyShoot);

                    writer.Put(Net.ServerTick);
                    writer.Put(gst.tracker.syncId);
                    writer.Put((Vector2)gun.barrel.position);
                    writer.Put((Vector2)(gun.transform.right * rightorleft));
                    writer.Put((bool)(gun.racked)); // if it jammed or not


                    byte hitlimbsamount = (byte)Math.Min(hitLimbs.Count, 40);
                    writer.Put(hitlimbsamount);
                    for (byte i = 0; i < hitlimbsamount; i++) {// theyre guranteed to be only of playerbody
                        var limb = hitLimbs.ElementAt(i);
                        writer.Put(new LimbNetId(limb));
                    }

                    byte hitbuildingsamount = (byte)Math.Min(hitBuildings.Count, 40);
                    writer.Put(hitbuildingsamount);
                    for (byte i = 0; i < hitbuildingsamount; i++) {// theyre guranteed to be registered and physics active
                        var b = hitBuildings.ElementAt(i);
                        writer.Put(b.syncId);
                    }

                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                    // WRITER END;


                }
            }

            if (suicide && Net.IsServer) {
                // if head wasnt hit, force hit it (this indeed does happen)
                if (!hitLimbs.Contains(npc.head)) {
                    //log.l($"suicide gunshot didnt hit the head lol, forcing it. ");

                    TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(npc.head, gst, false);
                }
            }

        }




        TurretScript_Shoot_MultiplayerPatch.HitOnlyStatic = false;

    }


    static bool ServerValidateShootHit(TrackerGunScript gst, Vector2 gun_barrelpos, Vector2 gun_direction, List<Limb> limbs_hit, List<SyncInfo> buildings_hit) {


        // YOU KNOW WHAT, FUCK THIS VALIDATION, LET CHEATERS CHEAT , PROBABLY I SHOULD MAKE THE REMEMBERING OF PREVIOUS FRAMES N SHI TO BACKTRACK AND VALIDATE HITS, BUT NAH IT AINT A COMPETITIVE GAME

        //List<Limb> valid_limbs_hit = new();
        //// validate that the thing is not too far from bullet trajectory
        //foreach (var limb in limbs_hit) {
        //    //TODO remove the items  if they didnt validated
        //
        //    for (int i = 0; i < gst.shotsPerFire; i++) {
        //        RaycastHit2D rayresult = Physics2D.Raycast(gun_barrelpos, gun_direction, 200f, LayerMask.GetMask("Ground")); // find out the max distance of travel of the bullet
        //
        //        // todo get closest position on ray to the transform pos, then get closest point, check distance
        //
        //        var postocheckdist = limb.GetComponent<Collider2D>().ClosestPoint();
        //    }
        //}
        //
        ////TODO move it to the validate thing 
        //RaycastHit2D rayresult = Physics2D.Raycast(gun_barrelpos, gun_direction, 200f, LayerMask.GetMask("Ground")); // find out the max distance of travel of the bullet
        //
        //foreach (var item in buildings_hit) {
        //}

        return true;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerBodyShoot)]
    static void ServerReceiver__PlayerBodyShoot(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ushort tick);
        reader.Get(out knetid gun_syncid);

        if (
            Util.IsWorldGenerated() &&
            ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var shooter) && shooter.body.conscious &&
            ItemSync.TryGetItemSyncInfo(gun_syncid, out var gun_si) && gun_si.IsGun() && (gun_si.gun.roundsInMag > 0 || gun_si.gun.roundInChamber == RoundInChamber.Round || shooter.IsLocal) &&
            ItemSync.CheckIfBodyReachThisItem(gun_si,shooter.body) 
        ) {
            var gst = gun_si.go.GetOrAddComponent<TrackerGunScript>();
            gst.body = shooter.body;
            gst.pbody = shooter;

            reader.Get(out Vector2 gun_barrelpos);
            reader.Get(out Vector2 gun_direction);
            reader.Get(out bool _new_racked_prob_ignore);
            gun_direction.Normalize();

            if (gun_direction == Vector2.zero || !gun_direction.IsFinite()) {
                Plugin.log.LogWarning($"SUS: PlayerBodyShoot: {plr} -> Weirdass gun shoot direction.");
                return;
            }

            // check if barrel pos is close enough to the gun or the body
            if (!gun_barrelpos.IsFinite() || (!KM.DistanceCheck(gun_barrelpos,gun_si.gun.barrel.position,10) && !KM.DistanceCheck(gun_barrelpos, shooter.body.GetClosestLimb(gun_barrelpos).transform.position, 2))) {
                Plugin.log.LogWarning($"SUS: PlayerBodyShoot: {plr} -> Shoot origin is not near the guns actual barrel position.");
                return;
            }

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(gun_si.transformPosition, $"S: PlayerBodyShoot {DebugHelp.GetPlayerFullDebugString(clientId)}", Color.magenta);


            List<Limb> limbs_hit = new List<Limb>(); // these should not be able to repeat

            reader.Get(out byte hit_limbs_amount); // guranteed 0 with friendly fire off
            for (byte i = 0; i < hit_limbs_amount; i++) {// theyre guranteed to be only of playerbody

                reader.Get(out LimbNetId limbnetid);

                if (limbnetid.TryGetNetBodyAndLimbSafe(out var nb, out var lim) ) {
                    if (shooter.CanAttackThisGuy(nb.body)) {
                        limbs_hit.Add(lim);
                    } else {
                        Plugin.log.LogWarning($"SUS: PlayerBodyShoot: {plr} -> They hit someone they can't attack ({nb}).");
                    }
                } else {
                    Plugin.log.LogWarning($"SUS: PlayerBodyShoot: {plr} -> They hit a non-existant limb????");
                }
            }

            limbs_hit = Util.LimitMaxRepeats(limbs_hit, Math.Min(gst.shotsPerFire, MAX_HITS_PER_LIMB_AT_ONCE)); 
            

            List<SyncInfo> buildings_hit = new List<SyncInfo>(); 

            reader.Get(out byte hit_buildings_amount);
            for (byte i = 0; i < hit_buildings_amount; i++) {// theyre guranteed to be registered and physics active

                reader.Get(out knetid building_syncid);

                if (NetObjectRegistry.TryGetSyncInfo(building_syncid, out var bsi) ) {
                    if ( bsi.IsBuilding() && NetObjectRegistry.IsObjectDynamic(bsi.go)) {
                        if (bsi.building.cantHit) {
                            Plugin.log.LogWarning($"SUS: PlayerBodyShoot: {plr} -> They hit a building with the cantHit flag on.");
                        } else {
                            buildings_hit.Add(bsi);
                        }
                    } else {
                        Plugin.log.LogWarning($"SUS: PlayerBodyShoot: {plr} -> They hit a static building or it isnt even a building ({bsi}).");
                    }
                } 
            }


            // check max amount of repeats in buildings_hit 
            buildings_hit = Util.LimitMaxRepeats(buildings_hit, gst.shotsPerFire );

            if (!ServerValidateShootHit(gst,gun_barrelpos,gun_direction,limbs_hit,buildings_hit)) {
                return;
            }



            // tell everyone about the gunshot, including the host, so that we know
            var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerBodyShoot_Relay);

            writer.Put(clientId);
            writer.Put(gst.tracker.syncId);

            writer.Put(gun_barrelpos);
            writer.Put(gun_direction);

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

            // WRITER END;

            GunScript_JamChance_MultiplayerPatch.ForceJam = gun_si.gun.racked == false;

            // do the visuals (and debuffs for the shooter ) it for the server
            if (!Util.IsBodyLocal(shooter.body)) {
                PlayerBodyShoot_VisualsTypeShit(clientId, gst.si,gun_barrelpos,gun_direction);
            }

            GunScript_JamChance_MultiplayerPatch.ForceJam = false;

            gun_si.gun.racked = _new_racked_prob_ignore;
            gun_si.gun.lastRacked = false ; // gun only shoots if its not racked

            if (gun_si.gun.firingMode != FiringMode.Pump) {
                if (_new_racked_prob_ignore) {
                    gst.og.gasTime = gun_si.gun.desiredGasTime;
                }
            }



            TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst,limbs_hit,buildings_hit);

        }
    }



    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerBodyShoot_Relay, false)]
    static void Client_PlayerBodyShoot_Relay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid shooter_clientid);
        reader.Get(out knetid gun_syncid);

        reader.Get(out Vector2 gun_barrel_origin);
        reader.Get(out Vector2 gun_barrel_direction);

        if (ItemSync.TryGetItemSyncInfo(gun_syncid, out var gun_si) && gun_si.IsGun()) {

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(gun_si.transformPosition, $"C: PlayerBodyShoot_Relay {DebugHelp.GetPlayerFullDebugString(shooter_clientid)}");

            PlayerBodyShoot_VisualsTypeShit(shooter_clientid,gun_si,gun_barrel_origin,gun_barrel_direction);
        }

    }
    static void PlayerBodyShoot_VisualsTypeShit( knetid shooter_clientid,SyncInfo gun_si, Vector2 gun_barrel_origin,Vector2 gun_barrel_direction) { 

        var gun = gun_si.gun;
        var gst = gun_si.go.GetOrAddComponent<TrackerGunScript>();

        gst.override_shoot_trajectory = true;
        gst.override_shoot_trajectory_origin = gun_barrel_origin;
        gst.override_shoot_trajectory_direction = gun_barrel_direction;

        // set necessary booleans to force the gun to shoot
        gun.triggerPressed = true;
        gun.firingPinStruck = false;

        if (gun.roundInChamber != RoundInChamber.Round) {
            if (gun.roundInChamber == RoundInChamber.Casing) { // force eject the casing , because the gun didnt  jam on their end
                (UnityEngine.Object.Instantiate(Resources.Load("casing"), gun.transform.position, gun.transform.rotation) as GameObject).GetComponent<Rigidbody2D>().velocity = gun.transform.up * 12f;
            }

            gun.roundInChamber = RoundInChamber.Round;
            gun.roundsInMag--;
        }

        gun.lastRacked = false;
        gun.racked = false;
        gun.safe = false;


        gst.ApplyRecoil();

        // force run the update ig
        gun_si.gun.Update();

    }



    //idk man
    public static bool ValidateBulletHit(TrackerGunScript gst) {

        return false;
    }





}

// MAKE IT RETURN ALWAYS 0 FOR REMOTE PEERS
[HarmonyPatch(typeof(GunScript), nameof(GunScript.JamChance))]
public static class GunScript_JamChance_MultiplayerPatch {

    public static bool ForceJam = false; // decided by the client

    static void Postfix(GunScript __instance, ref float __result ) {


        var gst = __instance.GetOrAddComponent<TrackerGunScript>();
        if (gst.body.TryGetNetBody(out var nb)) {

            if (!nb.IsLocal) {
                if (ForceJam) {
                    ForceJam = false;
                    __result = 3f; // force it to jam
                } else {
                    __result = -1f; // force it to never jam, client handles that
                }

            }
        }

    }
}

[HarmonyPatch(typeof(TurretScript), nameof(TurretScript.Shoot))]
public static class TurretScript_Shoot_MultiplayerPatch {

    public static bool HitOnlyStatic = false;
    public static bool LastActiveShooterIsSuicide = false;
    public static NetBody LastActiveShooter = null;
    public static TrackerGunScript LastActiveShooterGun = null;

    public static List<SyncInfo> JustHitBuildings = new();
    public static List<Limb> JustHitLimbs = new();

    // returns false to damage the building
    public static bool Shoot_DidHitABuilding(BuildingEntity building) { // its guranteed to get called after the cantHit was already checked
        if (!Net.IsRunning) return false;

        
        if (ScavMPBuildingSynchronizer.BuildingHasActivePhysics(building)) {

            if (NetObjectRegistry.TryGetSyncInfo(building.gameObject,out var si)) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(building.transform.position, $"Shoot: Noted hit building", Color.green);

                JustHitBuildings.Add(si);
            }

            if (LastActiveShooter != null) {
                return true; // dont damage it because im doing it in the gun fire patch
            }

            if (HitOnlyStatic) {
                return true;// dont damage it because i wanna only do the effects
            }
        }
        
        return false;
    }

    // returns false to damage the body
    public static bool Shoot_DidHitABody(RaycastHit2D hit2D) {
        if (!Net.IsRunning) return false;


        var limb = hit2D.collider.GetComponent<Limb>();
        var body = hit2D.collider.GetComponent<Body>();
        if (limb == null) {
            if (body == null) {
                return true; // the og code is not gona get triggered anyway
            }
        } else {
            body = limb.body;
        }
        limb = body.GetClosestLimb(hit2D.point); // original code does it like that, i do the same because it brings very specific results

#if DEBUG
        log.l($"Shoot_DidHitABody limb: {limb}  rayhit:{hit2D.collider.name}");
#endif
        if (LastActiveShooter == null) { // a turret is the shooter
#if DEBUG
            Plugin.log.LogDebug($"Turret hit {limb}");
#endif

            if (last_fire_info.ignoreTrans != null && last_fire_info.ignoreTrans.TryGetComponent(out TurretScript turret) && body.TryGetNetBody(out var nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(turret.build, nb, 10));
            }

            // remote bodies are a stamped pose. the owner already rolled this shot.
            if (Net.IsRunning && body != null && !body.IsBodyLocal())
                return true;

            if (Net.IsRunning)
                TrapClaim.NoteTurret(body, limb);

            return false; // BANG , do the vanilla damage
        }

        bool actually_commiting_suicide = false;

        var do_damage = false; // dont damage em by default

        if (limb.TryGetNetBody(out var bodyn) ) {
            actually_commiting_suicide = LastActiveShooterIsSuicide && bodyn == LastActiveShooter;

            if (actually_commiting_suicide) {
                do_damage = true; // let em kill themselves
            } else if (LastActiveShooter.CanAttackThisGuy(limb.body)) { // additional checks
                do_damage = true; // damage the poor guy
            } 
            if (HitOnlyStatic) {
                do_damage = false;
            }

            if (do_damage && bodyn is NetBody) {  // the council has decided to kill the him
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(bodyn.GetHeadPos(), $"Shoot: Noted hit body", Color.green);
                JustHitLimbs.Add(limb);
            } else {
                // nah ts doesnt work
            }
        }

#if DEBUG
        if (log.verbose && !HitOnlyStatic) log.l($"Shoot_DidHitABody limb: {limb} body: {bodyn} -> damage allowed:{do_damage} ");
#endif

        if (actually_commiting_suicide) {
            return false; // allow full damage for suicide
        }

        // allow damaging npcs i guess, why am i even doing this its not a thing even 
        if ((bodyn == null || !(bodyn.IsPlayer)) && Multiplayer.rules.PVP) {
            return false;
        }

        
        
        return true; // i completely override the body damage because of friendly fire specifics
    }


    public static void ApplyShootDamages(TrackerGunScript gst, IEnumerable<Limb> hitLimbs, IEnumerable<SyncInfo> hitBuildings) {
        var attackerIsLocal = Util.IsBodyLocal(gst.body);
        
        var hitLimbsDuplicateCount = hitLimbs.GroupBy(x => x, ReferenceEqualityComparer.Instance).ToDictionary(a => (Limb)a.Key, b => b.Count());

		Dictionary<NetBody, float> plrdamages = new();
		foreach (var limb in hitLimbs) {
			var dmg = TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(limb, gst, true);

			if (limb.TryGetNetBody(out var nb)) {
				if (!plrdamages.ContainsKey(nb)) {
					plrdamages[nb] = 0;
				}
				plrdamages[nb] += dmg;
			}
		}
		{
			List<Body> amputatedonplrs = new();
			foreach (var limbcount in hitLimbsDuplicateCount) {
				var limb = limbcount.Key;

#if !RELEASE
				if (log.verbose) log.l($"{limb} WAS HIT {hitLimbsDuplicateCount[limb]} TIMES ");
#endif

				if (amputatedonplrs.Contains(limb.body) || (!Multiplayer.rules.PVPCombatDismember && limb.body.alive)) {
					continue;
				}

				{
					if (
                        limbcount.Value >= 3  // all three max bullets hit the limb
					) {
						if (
						    KM.DistanceCheck(gst.transform.position, limb.transform.position, 16) // check if actually close
						) {
							CombatStuff.BetterDismember(limb, !limb.isHead);
							if (limb.dismembered) {
								amputatedonplrs.Add(limb.body);
							}
						} 
					}
				}
			}
		}


		foreach (var dmg in plrdamages) { // visualize the damage
            var p = dmg.Key.GetHeadPos() + Vector2.up;
            WorldGeneration.CreateDamageNumber(p, (int)dmg.Value);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(p, $"Shoot: Damaged body {dmg.Key}", Color.red);
            dmg.Key.body.DoGoreSound();

            if (!dmg.Key.body.conscious && dmg.Key.body.isDying) {
                gst.body.happiness -= Multiplayer.rules.PVPMoodDebuff*2;
                if (!gst.body.specialCrying && Multiplayer.rules.PVPMoodDebuff > 0) {
                    gst.body.StartCoroutine("Cry");
                }
            }

            {
                dmg.Key.OnDamageEvent(new BodyDamageEvent(gst.pbody,dmg.Key,gst.item, dmg.Value));
            }

            {
                if (Net.IsServer) {
                    MedicalSync.Server_QueueSendCharacterHealth(dmg.Key, true);
                } else {
                    if (attackerIsLocal)
                        dmg.Key.SetNetHealthSyncIgnoreTime();
                }
            }
        }

        foreach (var building in hitBuildings) {
            
            var dmg = TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageBuilding(building.building, gst);

            if (Net.IsServer) {
                //NetObjectRegistry.Server_QueueSync(building);
            } else {
                if (attackerIsLocal)
                    building.SetIgnoreTimeForRoundTrip();
            }
            

            {
                // do the visuals
                if (building.building.animal) {
                    Sound.Play("gore" + Random.Range(1, 6), building.transformPosition);

                }
                var p = building.transformPosition + Vector2.up * 2;
                WorldGeneration.CreateDamageNumber(p, (int)(dmg));
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(p, $"Shoot: Damaged building", Color.red);
            }
        }
    }


    //OG: TurretScript.Shoot V5t4  the part where it applies damage to building, but minus the sound and damagenumber part, and that random number moved all the way to here
    public static float Stolen_ShootApplyDamageBuilding(BuildingEntity b, TrackerGunScript gst) {
        float num = Random.Range(0.85f, 1.15f);

        var damage = (b.animal ? (gst.og.animalDamage * num) : (gst.og.structureDamage * num));

        b.SetHealth(b.GetHealth() - damage);
        if (b.animal) {
            b.gameObject.SendMessage("AnimalHit", damage);
        }
        return damage;
    }


    //OG: TurretScript.Shoot V5t4  the part where it applies damage to body // TODO TS IS OUTDATED  FireInfo.playerDamageMultiplier WAS ADDED, BUT IS IT USED THO? 
    public static float Stolen_ShootApplyDamageLimb(Limb limb, TrackerGunScript gst, bool use_pvp_damage_model) {
        limb.body.adrenaline = 100f;

        var armor = limb.GetArmorReduction();

        var rule_mult = Multiplayer.rules.PVPDamageMultiplier;

        var limbindex = Util.GetLimbIndex(limb);

        float damage = 0;

        if (use_pvp_damage_model) { // SLIGHTLY NERFED DAMAGE FOR MP FRIENDLY FIRE
            armor /= (rule_mult == 0 ? 0.00001f : rule_mult);

            if (rule_mult != 0) {
                limb.pain += Random.Range(50f, 70f) / armor;

                damage = gst.og.animalDamage / armor * 0.5f;

                limb.skinHealth -= damage;
                limb.muscleHealth -= damage;
                limb.bleedAmount += Random.Range(30f, 40f) / armor;
                limb.shrapnel = Math.Max(limb.shrapnel, Random.Range(0, (int)Mathf.Clamp(5f * rule_mult, 1, 5)));

                limb.DamageWearables(0.8f * rule_mult);
            }

        } else {// ORIGINAL TURRET DAMAGE
            rule_mult = 1;
            limb.pain += Random.Range(80f, 100f) / armor;

            damage = Random.Range(70f, 100f) / armor; // for completeness

            limb.skinHealth -= damage;
            limb.muscleHealth -= Random.Range(70f, 100f) / armor;
            limb.bleedAmount += Random.Range(30f, 40f) / armor;
            limb.shrapnel = 5;

            limb.DamageWearables(1f);
        }
        if (limb.isVital || limb.isHead) {
            limb.body.internalBleeding += Random.Range(18f, 22f) / armor;
        }
        if (Random.Range(0f, 1f) < 0.3f * rule_mult) {
            limb.BreakBone();
        }
        if (limb.isHead) {

            if (use_pvp_damage_model) { // SLIGHTLY NERFED DAMAGE FOR MP FRIENDLY FIRE
                limb.pain += Random.Range(10f, 50f) / armor;

                limb.body.consciousness -= 35f / armor;
                //dont make it an instant death 
                if (limb.body.brainHealth > 20f) {
                    limb.body.brainHealth = Math.Max(4f,(limb.body.brainHealth - Random.Range(10f, 70f) / armor));
                } else {
                    limb.body.brainHealth -= Random.Range(5f, Math.Max(limb.body.brainHealth-1f,9f)) / armor;
                }

                if (Multiplayer.rules.PVPCombatDismember && Random.value * rule_mult > 0.2f && !limb.body.GetHead().dismembered) {
                    if (Random.value > 0.5f) {
                        limb.body.Disfigure();
                    } else {
                        limb.body.RemoveEye();
                    }
                } 
            } else { // default turret damage 
                limb.body.consciousness = 0f;
                limb.body.brainHealth -= Random.Range(10f, 85f) / armor;

                if (Random.value > 0.5f) {
                    limb.body.Disfigure();
                } else {
                    limb.body.RemoveEye();
                }
            }

        }



        if ((Multiplayer.rules.PVPCombatDismember || !limb.body.alive) && use_pvp_damage_model ) {
            var markiplier =  1f;
            var markiplier2 = 3f;
            if (limb.body.IsDeadOrCriticallyDying()) {
                markiplier = 2f;
                if (gst.IsShotgun()) {
                    markiplier2 = 52f; 
                } else {
                    markiplier2 = 5f;
                }
            }
            if ( limb.muscleHealth <= markiplier && limb.skinHealth <= markiplier2 ) { // if the limb is really damaged, allow to amputate it

                if (Net.IsServer) {
                    
                    if (UnityEngine.Random.value < 0.2 * markiplier * rule_mult) {
                        var head = limb.body.limbs[(int)Body.LimbNum.Head];
                        if (Util.GetLimbIndex(limb) <= 2 && !head.dismembered) { // shoot their head off first ig 
                            CombatStuff.BetterDismember(head, true);
                        } else {
                            CombatStuff.BetterDismember(limb, true);
                        }
                    }
                }
            }
            
        }

        return damage;
    }


    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {

        var codes = new List<CodeInstruction>(instructions);

        int i = 0;

        i += 100;



        while (!(
            codes[i-3].opcode == OpCodes.Ldloc_S &&
            codes[i-2].opcode == OpCodes.Ldfld &&
            codes[i-1].opcode == OpCodes.Brtrue &&
            codes[i].opcode == OpCodes.Ldloc_S &&

            codes[i - 2].ToString().Contains("cantHit") // check if for that Ldfld

        )) { 
            i++;
        }

        if (log.verbose) Plugin.log.LogWarning($"TRANSPILER: TurretScript Shoot TRANSPILER found the building if statement {i}"); // expected around 117
        var building_check_location = i;


        i += 60;


        while (!(
            codes[i - 3].opcode == OpCodes.Ldloc_S &&
            codes[i - 2].opcode == OpCodes.Ldfld &&
            codes[i - 1].opcode == OpCodes.Stloc_S &&
            codes[i].opcode == OpCodes.Ldloc_S &&

            codes[i - 2].ToString().Contains("Limb") && // check for that body field thingy
            codes[i - 2].ToString().Contains("body") // check for that body field thingy

        )) { 
            i++;
        }

        var body_check_location = i;


        if (log.verbose) Plugin.log.LogWarning($"TRANSPILER: TurretScript Shoot TRANSPILER found teh body damage part {i}"); // expected around 251




        var continuejumpoperandindex = body_check_location - 1;
        while (!(codes[continuejumpoperandindex].opcode == OpCodes.Brtrue  )) {
            continuejumpoperandindex--;
        }

#if DEBUG
        if (log.verbose)
            Plugin.log.LogWarning($"TRANSPILER: TurretScript Shoot FOUND THE CONTINUE JUMP REF AT: {continuejumpoperandindex}  {codes[continuejumpoperandindex]}  type: {codes[continuejumpoperandindex].operand.GetType()}  ");
#endif



		{
			//ldloc.s body(7)
			//call    bool[KrokoshaCasualtiesMP] KrokoshaCasualtiesMP.TurretScript_Shoot_MultiplayerPatch::Shoot_DidHitABody(class Body)
			//brtrue  404 (0547) ldloc.3 

			var newInstructions = new List<CodeInstruction>();
            

            var bodyoperandindex = body_check_location - 1;
            while (!(codes[bodyoperandindex].operand is LocalBuilder lb && lb.LocalType == typeof(Body))) {
                bodyoperandindex--;
            }


			var rayhitoperandindex = body_check_location - 1;
            while (!(codes[rayhitoperandindex].operand is LocalBuilder lb && lb.LocalType == typeof(RaycastHit2D))) {
                rayhitoperandindex--;
            }

			newInstructions.Add(new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(TurretScript_Shoot_MultiplayerPatch), nameof(TurretScript_Shoot_MultiplayerPatch.Shoot_DidHitABody))));

            newInstructions.Add(new CodeInstruction(OpCodes.Brtrue, codes[continuejumpoperandindex].operand));

            newInstructions.Add(new CodeInstruction(OpCodes.Ldloc_S, codes[bodyoperandindex].operand));

            codes[body_check_location].operand = codes[rayhitoperandindex].operand;
            codes.InsertRange(body_check_location+1, newInstructions);

		}



		{
            //call    bool[KrokoshaCasualtiesMP] KrokoshaCasualtiesMP.TurretScript_Shoot_MultiplayerPatch::Shoot_DidHitABuilding(class BuildingEntity)
            //brtrue  404 (0547) ldloc.3 
            //ldloc.s buildingEntity(5)

            var newInstructions = new List<CodeInstruction>();
            newInstructions.Add(new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(TurretScript_Shoot_MultiplayerPatch), nameof(TurretScript_Shoot_MultiplayerPatch.Shoot_DidHitABuilding))));
            newInstructions.Add(new CodeInstruction(OpCodes.Brtrue, codes[continuejumpoperandindex].operand));
            newInstructions.Add(new CodeInstruction(OpCodes.Ldloc_S, codes[building_check_location].operand));
            codes.InsertRange(building_check_location + 1, newInstructions);
		}



		if (log.verbose) {
            Plugin.log.LogWarning($"TRANSPILER: TurretScript Shoot TRANSPILER SUCCESS :) ");
        }
        return codes;
    }

    public static FireInfo last_fire_info;
    static void Prefix(FireInfo info) {
        TrapClaim.BeginShot();
        JustHitBuildings.Clear();
        JustHitLimbs.Clear();
        last_fire_info = info;

        // original only damages ears of local player if close enough
        if (info.doTinnitus) {
            foreach (var nb in NetBody.GetBodiesInRadius(info.pos,20)) {
                if (nb.IsBodyLocal()) { // its done by og func
                    continue;
                }
                nb.body.hearingLoss += UnityEngine.Random.Range(10f, 15f);
                nb.body.eyePanicTime = 0.6f;
                nb.body.eyeCloseTime = 4f;
                nb.body.eyeScareTime = 8f;
                nb.body.talker.Talk(Locale.GetCharacter("loud"));
            }
        }

        if (LastActiveShooter != null) {
            LastActiveShooter.RollAttackRandom();
        }
    }
    static void Postfix() {
        TrapClaim.EndShot();
        LastActiveShooterIsSuicide = false;
        LastActiveShooter = null;
        LastActiveShooterGun = null;
    }


}








[HarmonyPatch(typeof(GunScript), nameof(GunScript.UnloadMag))]
public static class GunScript_UnloadMag_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return GunScript_Fire_MultiplayerPatch.Transpiler(instructions);   
    }

    public static void Prefix(GunScript __instance) {
        // because i transpiled shit this component basically required
        var tracker = __instance.GetOrAddComponent<TrackerGunScript>();
    }
}








[HarmonyPatch(typeof(GunScript), nameof(GunScript.Update))]
public static class GunScript_Update_MultiplayerPatch {
    public static GunScript current_executing_gun = null;
    static void Prefix(GunScript __instance) {
        current_executing_gun = __instance;
    }
    static void Postfix(GunScript __instance) {
        current_executing_gun = null;
    }
}


[HarmonyPatch(typeof(GunScript), nameof(GunScript.Start))]
public static class GunScript_Start_MultiplayerPatch {
    static void Postfix(GunScript __instance) {
        var tracker = __instance.GetOrAddComponent<TrackerGunScript>();

        // because i transpile shit, its basically required

        __instance.lastRacked = __instance.racked; // to prevent the spawning racking sound or something !!
    }
}
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.GunRack))]
public static class PlayerCamera_GunRack_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            if (ScavPlayer.LocalPlayer.HasGunEquipped(out var gun)) {
                var tracker = gun.GetComponent<SyncInfoGameObjectTracker>();
                if (tracker != null  ) {
                    var gst = gun.GetOrAddComponent<TrackerGunScript>();

                    if (log.verbose) Plugin.log.LogInfo($"local GunRack {tracker.syncInfo}");

                    gst.send_racked_state = true;

                    tracker.syncInfo.SetIgnoreTimeForRoundTrip(0.5f);
                }
            }
        }
    }
}
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.GunEjectMag))]
public static class PlayerCamera_GunEjectMag_MultiplayerPatch {
     static void Postfix(PlayerCamera __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            if (ScavPlayer.LocalPlayer.HasGunEquipped(out var gun)) {
                var tracker = gun.GetComponent<SyncInfoGameObjectTracker>();
                if (tracker != null) {

                    if (log.verbose) Plugin.log.LogInfo($"local GunEjectMag {tracker.syncInfo}");

                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_GunEjectMag, true);

                    tracker.syncInfo.SetIgnoreTimeForRoundTrip(0.4f);
                }
            }
        }
    }
}
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.GunToggleSafety))]
public static class PlayerCamera_GunToggleSafety_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient()) {

            if (ScavPlayer.LocalPlayer.HasGunEquipped(out var gun)) {
                var tracker = gun.GetOrAddComponent<SyncInfoGameObjectTracker>();
                if (tracker != null) {

                    if (log.verbose) Plugin.log.LogInfo($"local GunToggleSafety ");

                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_GunToggleSafety, gun.safe, true);

                    tracker.syncInfo.SetIgnoreTimeForRoundTrip(0.4f);
                }
            }
        }
    }
}









internal class CombatStuff : CasualtiesTogetherSingleton {

    public static AttackInfo default_unarmed_attackinfo {  get {
            //OG: Body.UseItemInHand V7.0.1  Recreate this atk info every time to prevent crashes cuz the damage gets multiplied too much
            return new AttackInfo {
                damage = 30f,
                structuralDamage = 20f,
                distance = 4.5f,
                knockBack = 200f,
                cooldown = 0.2f,
                attackAnim = Resources.Load<GameObject>("ClawAnim"),
                staminaUse = 1f,
                piercing = false,
                swingSounds = new string[4] { "BSSwing1", "BSSwing2", "BSSwing3", "BSSwing4" },
                volume = 0.3f,
                rotateAmount = 11.5f,
                unarmed = true
            };
        }
    }



    void Awake() {
        
       
        
    }

    // cuz breaking limbs like torso makes limbs detach in a way that it leads to very unpredictable and buggy behaviour 
    public static bool CheckIfItsOkeyToDismemberThatLimb(Limb limb) {
        var limbindex = Util.GetLimbIndex(limb);
        if (limb.isHead && (limb.body.IsDeadOrCriticallyDying())) {
            return true;
        } if (Util.GetLimbIndex(limb) <= 2) { // is Head or UpTorso or DownTorso
            if (limb.body.alive) { // dont allow removing important limbs from alive players
                return false;
            }
            if (limb.isHead) { // allow decapitating 
            } else {
                // only allow if all limbs were dismembered
                if (!limb.body.limbs[4].dismembered) {
                    return false;
                }
                if (!limb.body.limbs[7].dismembered) {
                    return false;
                }
                if (!limb.body.limbs[10].dismembered) {
                    return false;
                }
                if (!limb.body.limbs[13].dismembered) {
                    return false;
                }

                if (limb.isVital) { // is UpTorso
                    if (!limb.body.limbs[(int)Body.LimbNum.Head].dismembered) {// only allow if head is removed 
                        return false;
                    }
                } else { // is DownTorso 
                    if (!limb.body.limbs[(int)Body.LimbNum.UpTorso].dismembered) {// only allow if uptorso is removed (but by then it will be already gone because of how Dismember works)
                        return false;
                    }
                }
            }
        } else { // is legs or arms 
            if (limb.body.alive) { // if theyre alive dont allow amputating more than once (arms and legs separate). to keep at least one set of limbs full and keep the game playable 

                bool any_leg_removed = false;
                for (int i = (int)Body.LimbNum.LegF; i < 15; i++) {
                    if (limb.body.limbs[i].dismembered) {
                        any_leg_removed = true;
                        break;
                    }
                }

                bool any_arm_removed = false;
                for (int i = (int)Body.LimbNum.ArmF; i < (int)Body.LimbNum.LegF; i++) {
                    if (limb.body.limbs[i].dismembered) {
                        any_arm_removed = true;
                        break;
                    }
                }

                if (limb.isArm && any_arm_removed) {
                    return false;
                }

                if (limb.isLegLimb && any_leg_removed) {
                    return false;
                }

            }
        }
        return true;
    }

    public static Limb CannibalismGetFarthestLimbToBeEfficient(Limb limb) {
        var limbindex = Util.GetLimbIndex(limb);
        if (limb.isHead) { // allow this straight away
            return limb;
        }
        if (limbindex <= 2) {   // is UpTorso or DownTorso  (cuz head is already done)

            // try to pick limbs that might be still attached 
            var found_something_else = false;
            for (int i = (int)Body.LimbNum.ArmF; i < limb.body.limbs.Count(); i++) {
                var l = limb.body.limbs[i];
                if (
                    (i == 3 || i == 6 || i == 9 || i == 12)
                    
                    && !l.dismembered) {
                    limb = l;
                    found_something_else = true;

                    break;
                }
            }

            if (!found_something_else) { // no other limbs are left, now eat the torso ig

                if (limb.isVital) {
                    if (!limb.body.limbs[(int)Body.LimbNum.Head].dismembered) {
                        return limb.body.limbs[(int)Body.LimbNum.Head];
                    }
                } else if (!limb.body.limbs[(int)Body.LimbNum.UpTorso].dismembered) {
                    return limb.body.limbs[(int)Body.LimbNum.UpTorso];
                }
            }
            
        } else { // is legs or arms 
            var last_heart_dist = limb.distanceToHeart;

            foreach (Limb l in limb.body.limbs) {
                if (l.distanceToHeart > last_heart_dist && !l.dismembered) {
                    limb = l;
                    last_heart_dist = l.distanceToHeart;
                }
            }
        }
        return limb; 
    }


    public static void BetterDismember(Limb limb, bool do_checks_to_not_break_the_game = true) {
        if (limb.dismembered) {
            return;
        }
        if (do_checks_to_not_break_the_game) {
            if (!CheckIfItsOkeyToDismemberThatLimb(limb)) {
                return;
            }
        }

        if (limb.body.TryGetComponent(out NetBody npc)) {

            var notaccidental = limb.body.IsDeadOrCriticallyDying();

            foreach (Limb lowerLimb in TourniquetScript.GetLowerLimbs(limb)) {
                lowerLimb.shrapnel = 0;// original does not do that lol 

                if (lowerLimb.dismembered) {
                    continue;
                }

                // dont drop meat for every limb, its going to be too much
                if (npc.butchering_dropmeat_skip_counter > 0) {
                    npc.butchering_dropmeat_skip_counter = 0;

                    continue;
                }

                if (Util.GetLimbIndex(lowerLimb) <= 2) {
                    var org = Utils.Create("internalorgans", (Vector2)lowerLimb.transform.position + Vector2.up, 0);
                    if (notaccidental)
                        org.AddComponent<FreshItemDrop>();
                    org.GetComponent<Item>().rb.velocity = lowerLimb.rb.velocity * 0.5f + Random.insideUnitCircle * 7f;
                }
                var flesh = Utils.Create("experimentflesh", (Vector2)lowerLimb.transform.position + Vector2.up, 0);
                if (notaccidental)
                    flesh.AddComponent<FreshItemDrop>();
                flesh.GetComponent<Item>().rb.velocity = lowerLimb.rb.velocity * 0.5f + Random.insideUnitCircle * 7f;

                if (!limb.body.alive) {
                    npc.butchering_dropmeat_skip_counter++;
                }
            }
        }



        if (limb.isHead) {
            limb.body.brainHealth = 0;
            limb.body.heartRate = 0;

            // this is to remove the decals over a non existing head
            limb.body.bothEyesGone = false;
            limb.body.eyeGone = false;
            limb.body.disfigured = false;
        }

        limb.Dismember(); // actually if uptorso is getting dismembered. actually downtorso goes with it 
    }




    public static bool ItemIsUsedForAttacking(Item item) {
        return item.Stats.HasTag("tool") || item.Stats.HasTag("gun"); 
    }











    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_GunRack)]
    static void ServerReceiver__GunRack(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out bool racked);
        if (
            ScavPlayer.TryGetPlayerFromClientId(clientId, out var gunner) && gunner.HasGunEquipped(out var gun)
        ) {
            gun.racked = racked;
            NetObjectRegistry.SafeNewGO(gun.gameObject);
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_GunEjectMag)]
    static void ServerReceiver__GunEjectMag(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        if (
            ScavPlayer.TryGetPlayerFromClientId(clientId, out var gunner) && gunner.HasGunEquipped(out var gun)
        ) {
            gun.UnloadMag();
            NetObjectRegistry.SafeNewGO(gun.gameObject);
        }


    }


    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_GunToggleSafety)]
    static void ServerReceiver__GunToggleSafety(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out bool safe);
        if (
            ScavPlayer.TryGetPlayerFromClientId(clientId, out var gunner) && gunner.HasGunEquipped(out var gun)
        ) {
            gun.safe = safe;
            NetObjectRegistry.SafeNewGO(gun.gameObject);
        }

    }


}
