using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;



namespace Together;











#region RADLINE_PATCH

[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Activate))]
static class RadiationLine_Activate_MultiplayerPatch {
    static bool Prefix(RadiationLine __instance) {
        return __instance.timeGone != -10;
    }
}
[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Deactivate))]
static class RadiationLine_Deactivate_MultiplayerPatch {
    public static void Postfix(RadiationLine __instance) {
        RadiationLine_Update_MultiplayerPatch.timeGone = 0;
    }
}
[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Update))]
public static class RadiationLine_Update_MultiplayerPatch {
    public static float timeGone = 0;
    public static float timeGone_limit => WorldGeneration.world.height - 8;

    public static bool someone_is_getting_fried = false;
    public static void Postfix(RadiationLine __instance) {
        var real_frying = false;
        if (Net.IsRunning) {
            if (!__instance.active) {
                return;
            }


            var line = RadiationLine.line;
            var og_timegone = line.timeGone;
            if (og_timegone == 0f) {
                timeGone = 0f;
            } else {

                //OG: RadiationLine.Update V5t5
                float line_fullspeed = 1.5f;
                const float line_slowspeed = 0.2f;


                if (ServerMain.CheckIfShouldStartRadlineForStragglers(2) && !someone_is_getting_fried) {
                    line_fullspeed *= 1.7f;
                }

                if (Multiplayer.rules.DisableSleep) {
                    // this will not slow down with disabled sleep
                    timeGone += Time.deltaTime * 1f * line_fullspeed;
                } else {
                    //OG: RadiationLine.Update
                    timeGone += Time.deltaTime * (ServerMain.CheckIfEveryoneAliveIsActuallyConscious() ? 1f : line_slowspeed) * line_fullspeed;
                }


                if (timeGone > timeGone_limit) {
                    timeGone = timeGone_limit;
                }

                line.timeGone = (timeGone);

                foreach (var bodynpc in NetBody.all_instances) {
                    var body = bodynpc.body;
                    if (body.alive && body.transform.position.y > line.transform.position.y) {
                        real_frying = true;

                        float a = body.transform.position.y - line.transform.position.y;


                        if (Multiplayer.rules.RadlineExtraDamage) {// mp: faster killing death stragglers
                            if (a > 40f || (!body.conscious && !body.sleeping)) {
                                body.brainHealth -= Time.deltaTime * Mathf.Clamp((100 - body.brainHealth) * 0.3f, 1, 30); // layerTimeSpent
                                body.GetHead().pain += Time.deltaTime;
                            }
                            if (a > 20f) {
                                body.GetHead().pain += Time.deltaTime;
                            }
                        }


                        PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(bodynpc, a * 0.05f);



                        a = Mathf.Min(a, 10f);
                        body.radiationSickness += a * Time.deltaTime * 0.03f;
                        body.eyeScareTime = 1f;
                    }
                }
            }
        }
        someone_is_getting_fried = real_frying;
    }
}


#endregion





// Fix this bug:
//NullReferenceException
//  at(wrapper managed-to-native) UnityEngine.Component.GetComponentFastPath(UnityEngine.Component, System.Type, intptr)
//  at UnityEngine.Component.GetComponent[T] ()[0x00021] in <c39a522eee05469b8171a6cfeb646c59>:0 
//  at TourniquetScript+<>c.<Start>b__5_1(Limb x) [0x00000] in <ab887c07f51841c98ea015c9c29ae1af>:0 
//  at OnLimbWound.Update() [0x00000] in <ab887c07f51841c98ea015c9c29ae1af>:0 
[HarmonyPatch(typeof(OnLimbWound), nameof(OnLimbWound.Update))]
static class OnLimbWound_Update_MultiplayerPatch {
    static bool Prefix(OnLimbWound __instance) {
        if (__instance.limb == null || __instance.cond(__instance.limb)) {
            OnLimbWound.Destroy(__instance.gameObject);
        }
        return false;
    }
}

















#region VOMIT_OVERRIDE 




[HarmonyPatch(typeof(Vomiter), nameof(Vomiter.VomitBlood))]
public static class Vomiter_VomitBlood_MultiplayerPatch {
    public static bool Prefix(Vomiter __instance)  {
        return Vomiter_Vomit_MultiplayerPatch.VerifyAndAnnounceVomit(__instance, true);
    }
}



[HarmonyPatch(typeof(Vomiter), nameof(Vomiter.Vomit))]
public static class Vomiter_Vomit_MultiplayerPatch {

    public static void ForceNoWarning(Vomiter __instance, bool isblood) {
        if (!isblood) {
            __instance.vomiting = true;
        }

        var body = __instance.GetComponent<Body>();
        if (body.IsBodyLocal() && body.TryGetNetBody(out NetBody nb)) {
            nb.SetNetHealthSyncIgnoreTime(0.1f);
        }

        __instance.StartCoroutine(isblood ? "DoBloodVomit" : "DoVomit"); // call it directly to not do the pre-vomit sound
    }
    public static void AnnounceVomit(knetid plr, bool isblood) {
        var writer = Net.CreateWriter(NetmsgId.CLIENT_VomiterVomit);

        writer.Put(plr);
        writer.Put(isblood);

        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);

        // WRITER END;
    }

    internal static bool VerifyAndAnnounceVomit(Vomiter __instance , bool isblood ) {
        if (Net.IsRunning) {

            var body = __instance.GetComponent<Body>();

            if (!body.alive) { // cant vomit cuz ded
                return false;
            }

            if ( body.TryGetNetBody(out var pb))  {
                if (Net.IsServer) {
                    AnnounceVomit(pb.netId, isblood);
                }
            }

            if (body.IsBodyLocal()) {
                return true;
            } else {
                ForceNoWarning(__instance, isblood);
                return false;
            }
        }

        return true;
    }

    public static bool Prefix(Vomiter __instance)  {
        //var vomiting = Traverse.Create(__instance).Field("vomiting").GetValue<bool>();

        if (__instance.vomiting) { // cuz it only adds a number
            return true;
        }

        return VerifyAndAnnounceVomit(__instance,false);
    }


}







#endregion



[HarmonyPatch(typeof(Body), nameof(Body.Eat))]
static class Body_Eat_MultiplayerPatch {
    static void Prefix(Body __instance) {
        if (__instance.IsBodyLocal() && __instance.TryGetNetBody(out var nb)) {
            nb.SetNetHealthSyncIgnoreTime();
        }
    }
}
[HarmonyPatch(typeof(Body), nameof(Body.Drink))]
static class Body_Drink_MultiplayerPatch {
    static void Prefix(Body __instance) {
        if (__instance.IsBodyLocal() && __instance.TryGetNetBody(out var nb)) {
            nb.SetNetHealthSyncIgnoreTime();
        }
    }
}





[HarmonyPatch(typeof(Body), nameof(Body.StartClimbing))]
public static class Body_StartClimbing_MultiplayerPatch {

    public static Climbable last_interacted_climbable = null;
    public static Body last_interacted_body = null;
    static void Prefix(Body __instance, Climbable climbable) {
        last_interacted_climbable = climbable;
        last_interacted_body = __instance;
    }
    static void Postfix(Body __instance, Climbable climbable) {
        last_interacted_climbable = null;
        if (Multiplayer.IsNetworkActiveAndIsServer() && Util.IsWorldGenerated() && __instance.currentClimbable != null) {
            if (__instance.currentClimbable.TryGetComponent<TrackerClimbableEntity>(out var tracker)) {
#if TESTING 
                log.l($"FORCE RESYNCING ROPE !!");
#endif
                tracker.announce_reliable = false;
                tracker.Server_Announce();
            }
        }
    }
}



[HarmonyPatch(typeof(Body), nameof(Body.GetTotalEncumberance))]
static class Body_GetTotalEncumberance_MultiplayerPatch {
    public static void Postfix(Body __instance, ref  float __result) {
        if (Net.IsRunning) {
            if (__instance.TryGetComponent<NetBody>(out var npc)) {
                if (npc.carrying_person != null) {
                    var realweight = npc.carrying_person.GetRealFullEncumberanceOfBody() ; // this also gets encumberence from carryong other players, so it stacks
                    __result += realweight * Multiplayer.rules.PiggybackWeightMultiplier ;
                }
            }
        }
    }
}





// i wanna avoid doalerts here
[HarmonyPatch(typeof(Body), nameof(Body.DoPickupCheck))]
static class Body_DoPickupCheck_MultiplayerPatch {
    static bool Prefix(Body __instance, Item item, ref bool noAlerts, ref bool __result) {
        var body = __instance;
        if (!body.conscious || Util.IsGeneratingWorld()) {
            noAlerts = true;
        }
        if (Net.IsRunning) {
            if (!body.IsBodyLocal() || !body.conscious) {
                // skip the entire thing if its not my local player character
                noAlerts = true;
                __result = true;
                return false;
            }
            // the stock distance check uses the item. a bag child can be hundreds of units off its bag.
            ItemSync.SnapOntoContainer(item);
        }
        return true;
    }
}

// a bag on the ground still runs this. a missing wear limb used to throw and the drag never finished.
[HarmonyPatch(typeof(Body), nameof(Body.HasWearable), typeof(Item))]
static class Body_HasWearable_Item_MultiplayerPatch {
    static bool Prefix(Body __instance, Item item, ref bool __result) {
        if (item == null || item.Stats == null || __instance.LimbByName(item.Stats.desiredWearLimb) == null) {
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HasWearable), typeof(string))]
static class Body_HasWearable_Id_MultiplayerPatch {
    static bool Prefix(Body __instance, string itemid, ref bool __result) {
        if (itemid == null || Item.GlobalItems == null || !Item.GlobalItems.ContainsKey(itemid)) {
            __result = false;
            return false;
        }
        var info = Item.GlobalItems[itemid];
        if (info == null || __instance.LimbByName(info.desiredWearLimb) == null) {
            __result = false;
            return false;
        }
        return true;
    }
}




[HarmonyPatch(typeof(Body), nameof(Body.OnCollisionEnter2D))]
public static class Body_OnCollisionEnter2D_MultiplayerPatch {
    public static bool Prefix(Body __instance, Collision2D collision)  {
        var body = __instance ;
        if (!Util.IsBodyLocal(body))  { // dont shake the camera, do only visual body stuff

            //OG: Body.OnCollisionEnter2D  V5t2
            body.standLerpTime = 0.58f;
            body.crouchAmount -= body.lastTimeStepVelocity.y * 0.03f;
            if (body.lastTimeStepVelocity.y < 0f - body.jumpSpeed - 1f)
            {
                body.visualBodyOffset += new Vector2(0f, body.lastTimeStepVelocity.y * 0.035f);
                if (body.lastTimeStepVelocity.y < 0f - body.jumpSpeed - 5f) {
                    body.skills.AddExp(1, 0.5f);
                }
            }
            return false;
        }

        if (__instance.IsBodyLocal()) {
            var go = collision.gameObject;
            if (go != null && go.TryGetTracker(out var tracker)) {
                if (tracker.syncInfo.IsContactDangerous()) {
                    tracker.syncInfo.relaxed_combat_sync = true;
                    //tracker.syncInfo.SetIgnoreTimeForRoundTrip(0);
                }
            }
        }

        return true;
    }
    
}





[HarmonyPatch(typeof(DrillPod), "Update")]
public static class DrillPod_Update_MultiplayerPatch {



    public class DrillPodMultiplayerRewrite : MonoBehaviour{

        public bool working => drill.working;

        public DrillPod drill => GetComponent<DrillPod>();

        internal float timeHeld = 0f;

        void Update() {

            if (!Net.IsRunning) {
                return;
            }

            if (DrillPod_Update_MultiplayerPatch.didTeleport) {
                return; // we are teleporting
            }

            if (Multiplayer.rules.CheckIfLayerContinueIsDisabled()) {
                return;
            }
            
            var w = working;


            //OG: DrillPod.Update  THE ENTIRE FUNCTION I REIMPLEMENTED IT HERE

            var localbody = Util.GetLocalBody();
            var localbody_is_starting_it = BodyIsStartingIt(drill, localbody);

            if (w && localbody_is_starting_it) {
                timeHeld += Time.deltaTime;
                PlayerCamera.main.bonusActionBarTime = timeHeld * 0.2f;


                if (timeHeld > 5f && !didTeleport) {

                    var required_count = ServerMain.GetNumberOfPlayersRequiredToFinishLayer();

                    if (!CheckIfEveryoneIsInTheDrill(drill, out var current, out var required)) {
                        log.l($"DrillPod:  Local player tries to use DrillPod but not everyone is inside. count: {current}/{required}");

                        Util.DoAlert(String.Format(Lang.Get("drillpod_not_enough_plrs"), current, required));
                        timeHeld = 0f;
                    }
                }

            } else if (PlayerCamera.main.bonusActionBarTime > 0f) {
                PlayerCamera.main.bonusActionBarTime = 0f;
            }

            if (w) {

                if (Net.IsServer) {// SERVER

                    var canstartdrill = CheckIfEveryoneIsInTheDrill(drill, out var current, out var required);

                    if (!canstartdrill) {
                        timeHeld = 0f;
                    }

                    foreach (var bodysci in ScavPlayer.BodyToPlayerDict) {
                        var body = bodysci.Key;

                        var islocal = Util.IsBodyLocal(body);

                        //OG: DrillPod.Update  THE ENTIRE FUNCTION I REIMPLEMENTED IT HERE

                        if (w && !didTeleport && BodyIsStartingIt(drill, body)) {
#if DEBUG
                            //log.l($"DrillPod:  server knows, {body.transform.parent} is trying to start the drill canstartdrill:{canstartdrill}  timeheld:{timeHeld}");
#endif
                            if (canstartdrill) {
                                timeHeld += Time.deltaTime;


                                if (timeHeld > 5f && !didTeleport) {
                                    didTeleport = true;
                                    WorldGeneration.world.doPod = true;

                                    string str;
                                    if (required == 1) {
                                        str = $"<key:plr_started_drillpod> <plr:{bodysci.Value.playerId}>";
                                    } else {
                                        str = $"<key:all_started_drillpod>";
                                    }

                                    // announce continuerun
                                    Chat.Server_ChatAnnouncement(str);
                                    Util.DoAlert(Lang.ParseTaggedString(str));

                                    WorldGeneration.world.StartCoroutine("RegenerateWorld", true);
                                    Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
                                }

                            }

                        }


                    }

                }

            }



        }
    }





    internal static bool didTeleport = false;


    public static bool BodyIsInside(DrillPod drill, Body body) {
        //OG: DrillPod.Update 
        return (Mathf.Abs(body.transform.position.x - drill.transform.position.x) < 1.5f && Mathf.Abs(body.transform.position.y - drill.transform.position.y) < 5f);
    }
    public static bool BodyIsStartingIt(DrillPod drill, Body body) {
        //OG: DrillPod.Update 
        return (BodyIsInside(drill, body) && body.crouching);
    }
    public static bool CheckIfEveryoneIsInTheDrill(DrillPod drill, out int current, out int required) {
        return ServerMain.CheckIfCanFinishLayerCustomCondition((ScavPlayer item) => {
            return BodyIsInside(drill, item.body);
        }, out current, out required);
    }

    public static bool Prefix(DrillPod __instance) {
        if (Net.IsRunning) {
            __instance.GetOrAddComponent<DrillPodMultiplayerRewrite>();

            return false;
        }
        return true;
    }
}










[HarmonyPatch(typeof(Body), nameof(Body.HandleVisuals))]
public static class Body_HandleVisuals_MultiplayerPatch {
    static void Prefix(Body __instance, Painkillers pnk, ref bool __state) {
        __state = __instance.inWater;
        if (!Util.IsBodyLocal(__instance)) {// patch the SetDroplets, so that remote peers dont create phantom water overlay
            __instance.inWater = false;
            __instance.lastLiquidColor = Color.clear;
        }
    }
    static void Postfix(Body __instance, ref bool __state) {
        __instance.inWater = __state;
    }
}


[HarmonyPatch(typeof(Body), nameof(Body.Update))]
public static class Body_Update_MultiplayerPatch{
    static void Prefix(Body __instance) {
        if (!Net.IsRunning) return;
        __instance.GetCoUtilsInstance().SelectForMPOverride();
    }

    static float lastgunangle = 0.0f;
    static void Postfix(Body __instance)  {
        CoUtilsMultiplayer.DeselectMPOverride();

        var body = __instance;
        // override some values for non-myself characters
        if (!Util.IsBodyLocal(body)) {
            body.liquidDrinkTime = -0.5f; // this is handled by client by sending whenever they actually drank

            //OG: WorldGeneration.Update  V5t4  why is it even there???? thats what it should be! in the body!
            if (Util.world.biomeDepth == 5 && body.temperature < 32.5f) {
                body.snowAmount = Mathf.MoveTowards(body.snowAmount, 1f, Time.deltaTime * 0.0125f);
            } else {
                body.snowAmount = Mathf.MoveTowards(body.snowAmount, 0f, Time.deltaTime * 0.02f);
            }
        }

        var npc = body.GetComponent<NetBody>();
        if (npc == null) { 
            return;
        }

        if (!Net.IsRunning) return;

        if (!WorldGeneration.world.generatingWorld) {



            // point finger
            if (body.TryGetNetPlayer(out var plr)) {

                if (plr.is_pointingfingerat && !body.armsAnimator.GetBool("gun")) {

                    //OG: Body.Update
                    float num21 = body.limbs[1].transform.eulerAngles.z;
                    if (num21 > 180f) {
                        num21 -= 360f;
                    }

                    float num7 = 1f;
                    if (!body.isRight) {
                        num7 = -1f;
                    }

                    //OG: Body.Update
                    body.armsAnimator.SetBool("gun", true);

                    float value2 = Mathf.Lerp(lastgunangle,
                        Vector2.SignedAngle(body.transform.right * num7, (plr.is_pointingfingeratTARGET - (Vector2)body.limbs[1].transform.position).normalized) * num7 - num21 * num7,
                        Time.deltaTime * 8f * body.slots[body.handSlot].armPowerMult
                    );
                    body.armsAnimator.SetFloat("gunangle", value2);
                    lastgunangle = value2;

                } else {
                    lastgunangle = body.armsAnimator.GetFloat("gunangle");
                }

            }




            if (body.alive) {

                // override some values for non-myself characters
                if (!Util.IsBodyLocal(body)) {
                    body.attackCooldown -= Time.deltaTime * 0.05f; // speed it up a bit for remote peers
                }

                {
                    // do radiation visuals

                    var standingOn = body.standingOn;

                    //OG: Body.Update V5t4   im checking if its actually the local body doing this
                    if (body.grounded && standingOn != null && standingOn.toxicity > 0f) {
                        body.radiationSickness += standingOn.toxicity * Time.deltaTime;
                        PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(npc, standingOn.toxicity * 0.25f);
                    }
                }

                var nearestpossiblemedic = ScavPlayer.GetNearestConsciousPlayerToThisBody(npc);
                var last_standing = nearestpossiblemedic.Item1 == null; // no one else was found, this person is the last one
                var is_dying_alone = (
                        last_standing || // theres no one else alive
                        nearestpossiblemedic.Item2 > 1600 // or everyones too far (  40 blocks )
                    );
                var isLastAlive = ScavPlayer.AllLivingPlayers.Count == 1 && body.alive;

                if (ScavPlayer.ClientIdToPlayerDict.Count == 1) {
                    last_standing = false;
                }


                float total_brainhealth_loss = 0;
                {
                    //OG: Body.Update() V520dev
                    if (body.brainDying) {
                        total_brainhealth_loss += 1.5f;
                    } else {
                        if (body.bloodOxygen < 80f) {
                            total_brainhealth_loss += ((80f - body.bloodOxygen) / 600f)
                                * 0.4f; // but sligtly slower
                        }
                    }
                    if (body.temperature > 42f) {
                        total_brainhealth_loss += 0.5f;
                    }
                    if (body.radiationSickness > 0f) {
                        total_brainhealth_loss += body.radiationSickness * 0.0005f;// a little more cuz why not
                    }

                    if (body.TryGetComponent<Painkillers>(out var painkill)) { // speed up unrecoverable overdose death
                        if (!body.conscious && painkill.actualOpiateReception > 50f && body.energy < 40 && body.bloodOxygen < 30) {
                            total_brainhealth_loss += (painkill.actualOpiateReception - 40) * 0.015f;
                        }
                    }
                    if (!body.conscious && body.radiationSickness > 90) {
                        total_brainhealth_loss += 0.3f;
                    }
                }



                if ((!last_standing) && !body.brainDying && body.brainHealth < 100f && body.IsAirBreathable()) { // speed up brain regen if well fed (it shouldnt matter if unconscious
                    
                    float realregenmultiplier = Multiplayer.rules.AdditionalBrainRegen;
                    if (realregenmultiplier > 0f) {
                        var hungerBrainregenMultiplier = Mathf.Clamp(body.hunger / 100f,0, 1.3f) * Mathf.Clamp(body.thirst / 100f, 0, 1.3f) * KM.RemapClamped(body.brainHealth,100,70,0,1);
                        //if (hungerBrainregenMultiplier < 1)  hungerBrainregenMultiplier = hungerBrainregenMultiplier * hungerBrainregenMultiplier;
                        
                        var finalRegen = realregenmultiplier * hungerBrainregenMultiplier - total_brainhealth_loss * 0.7f;
                        if (finalRegen > 0) {
                            body.brainHealth += 0.16f * finalRegen * Time.deltaTime;
                        }
                    }
                    
                }

                // red circle is brainDying (blood pressure under 10 and consciousness under 5).
                // stock DyingFast is 3.5 while that circle is red. plain unconscious stays at normal speed.
                if (body.consciousness < 19f) {

                    if (
                        body.brainDying &&
                        body.lastTimeStepVelocity.magnitude < 7f &&
                        !body.sleeping &&
                        is_dying_alone
                    ) {
                        // slower than DyingFast while someone else is still alive somewhere
                        float timespeedup = 1.0f;
                        if (last_standing) {
                            timespeedup = 3.5f;
                        }
                        if (isLastAlive) {
                            timespeedup = 10f;
                        }
                        
                        if (npc.piggybacking_on != null) {
                            timespeedup = 0;
                        } else if (body.succesfullyRolledLastStand && body.lastStandTime > 280) {
                            timespeedup = 0;
                        }


                        { // speedup death from brain death uh
                            float realmultiplier = timespeedup * Math.Max(Multiplayer.rules.AdditionalHealthDecay, 0f);
                            if (realmultiplier > 0f) {
                                var DELTAMULTIPLIED = Time.deltaTime * realmultiplier;

                                if (body.totalBleedSpeed > 0) {
                                    body.bloodVolume -= body.totalBleedSpeed * DELTAMULTIPLIED;
                                }


                                //OG: Painkillers.Update() V5t4 
                                if (body.TryGetComponent<Painkillers>(out var pain)) {

                                    if (pain.actualOpiateReception > 30f) {
                                        body.energy -= pain.actualOpiateReception * 0.0025f * DELTAMULTIPLIED;
                                    }
                                }

                                body.brainHealth -= total_brainhealth_loss * DELTAMULTIPLIED;

                            }
                        }
                    }
                } else {

                    { // regen
                        float realregenmultiplier = Math.Max(Multiplayer.rules.AdditionalHealthRegen, 0f);
                        if (realregenmultiplier > 0f) {
                            var REGENDELTAMULTIPLIED = Time.deltaTime * realregenmultiplier;

                            if (!body.brainDying && body.IsAirBreathable()) { // faster brain regen
                                body.brainHealth += 0.003f * REGENDELTAMULTIPLIED;
                            }

                            body.hearingLoss = Mathf.Clamp(body.hearingLoss - 0.05f * REGENDELTAMULTIPLIED, 0f, 100f);

                            body.clawHealth = Mathf.Clamp(body.clawHealth + REGENDELTAMULTIPLIED * body.clawGrowthRate * 0.5f, 0f, 100f); // additional 0.5 cuz i dont want it to be too fast

                            body.hungerLimbHealCurrent = body.hungerLimbHealCurrent * realregenmultiplier;

                            if (body.totalBleedSpeed <= 0) {
                                body.bloodVolume = Mathf.MoveTowards(body.bloodVolume, 100f, REGENDELTAMULTIPLIED * body.bloodRegenSpeed);
                            }

                            // speed up stamina regen 
                            if (body.stamina > 60f && ((body.standing && Mathf.Abs(body.moveDir.x) < 0.1f && !npc.exercising) || !body.standing)) {
                                body.stamina += REGENDELTAMULTIPLIED * 0.5f;
                            }


                        }
                    }

                }
            } else {

                if (body.sleeping)
                    body.WakeUp();
            }




            // disable sleep 
            if (Multiplayer.rules.DisableSleep) {
                if (!body.isCriticallyDying) {
                    if (body.conscious) {
                        body.energy = Mathf.MoveTowards(body.energy, 100f, Time.deltaTime);
                    } else {
                        body.energy = Mathf.MoveTowards(body.energy, 100f, Time.deltaTime * 0.25f);
                    }
                }

                if (body.sleeping)
                    body.WakeUp();
            }

            // consciousness regain, wait for what? i want to be able to lose conscoiusness, just not for too long ig
            //if (!body.sleeping) {
            //    body.consciousness = 100f;
            //      if (body.sleeping) body.WakeUp();
            //}
        }



    }
}






























[HarmonyPatch(typeof(Body), nameof(Body.Awake))]
public static class Body_Awake_MultiplayerPatch {
    public static Vector2 origColSize = new Vector2(1.6f, 5.0f);
    static void Postfix(Body __instance) {
        origColSize = __instance.origColSize;
    }
}

    


[HarmonyPatch(typeof(Body), nameof(Body.Start))] 
public static class Body_Start_MultiplayerPatch{


    public static string BodyToString(this Body b) {
        if (b == null) {
            return "BODY(NULL)";
        }
        if (b.TryGetNetBody(out var nb)) {
            return nb.ToString();
        }
        return $"BODY({b.transform.parent?.name}->{b})";
    }

#if !RELEASE
    static void Prefix(Body __instance) {
        var pb = __instance.GetComponent<NetBody>();
        string pid = "NULL";

    }
#endif 
    static void Postfix(Body __instance) {
        var pb = __instance.GetComponent<NetBody>();
        string pid = "NULL";

        try {

            if (pb != null && pb.IsPlayer) {
                if (Net.IsServer) {

                    pid = pb.plr.GetPersistentId();

                    // map debug and the tutorial start empty. the file and the disconnect copy both stay untouched.
                    if (!SavesystemPatch.PreserveSave && ServerMain.Server_LastPlayerStates.TryGetValue(pid, out var val)) {
                        int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : val.savedonlayer;
                        // a descent destroys the net entities on those items. the file written at disconnect still has them.
                        if (val.savedonlayer != depth && pb.plr.LateJoin && PlayerCheckpoint.HasPlayerFile(pb.plr)) {
                            val.Discard();
                            PlayerCheckpoint.TryLoadBodyOnly(pb.plr);
                        } else {
                            val.Apply(__instance);
                            PlayerCheckpoint.RegisterBody(__instance);
                        }

                        ServerMain.Server_LastPlayerStates.Remove(pid);
                    } else if (!SavesystemPatch.PreserveSave && pb.plr.LateJoin) {
                        PlayerCheckpoint.TryLoadBodyOnly(pb.plr);
                    }
                }
            }

        } catch (Exception e) {
            log.error($"Body_Start_MultiplayerPatch SERVER READING SAVE:\nPlayerBody: {pb}\npid: {pid}\n" + e.ToString() );
        }

        Physics2D.IgnoreLayerCollision(LayerMask.GetMask("Player"), LayerMask.GetMask("Player") , true);

        foreach (var other_body in GameObject.FindObjectsOfType<Body>())
        {

            if (other_body != __instance) { 
            
                var my_chara = __instance.transform.parent;
                var other_chara = other_body.transform.parent;

                var a = my_chara.GetComponentsInChildren<Collider2D>();
                var b = other_chara.GetComponentsInChildren<Collider2D>();

                // ignore collisions with other players
                for (global::System.Int32 i = 0; i < a.Length; i++)
                {
                    var ca = a[i];  
                    for (global::System.Int32 e = 0; e < b.Length; e++)
                    {
                        var cb = b[e];

                        Physics2D.IgnoreCollision(ca, cb, true);

                    }
                }

            }

            
        }




    }
}





[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.lineOfSightEnabled), MethodType.Getter)]
static class WorldGeneration_lineOfSightEnabled_MultiplayerPatch {
    static void Postfix(WorldGeneration __instance, ref bool __result) {
        if (__result && PlayerCamera_HandleScreenShaders_MultiplayerPatch.CURRENTLY_OVERRIDING_VISUALS) {
            __result = false;
            if (PlayerCamera.main != null)
                PlayerCamera.main.LOSHole.GetComponent<VisionMask>().ClearMask();
        }
    }
}
















[HarmonyPatch(typeof(FluidManager), nameof(FluidManager.DrinkLiquid))]
static class FluidManagerDrinkLiquidPatch {
    static void Postfix(FluidManager __instance, Vector2Int pos, Body body) {
        if (Net.IsRunning) {
            if (Util.IsBodyLocal(body) && body.alive) {
#if DEBUG
                if (log.verbose) log.l($"REQUESTING LIQUID DRINK  ");
#endif
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"C: PlayerDrinkLiquid");

                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerDrinkLiquid, true);

            }
        }
    }
}




[HarmonyPatch(typeof(ShuttleStartOpen), nameof(ShuttleStartOpen.Start))]
static class ShuttleStartOpen_Start_MultiplayerPatch {
    static void Postfix(ShuttleStartOpen __instance) {
        if (!Net.IsRunning) return;
        if (ClientMain.ServerWorldState != null) {
            if (Net.IsServer) {
                ClientMain.ServerWorldState.shuttleDoorPosition.Value = __instance.transform.position;
            } else {
                __instance.transform.position = ClientMain.ServerWorldState.shuttleDoorPosition.Value;
            }
        }
    }
}


[HarmonyPatch(typeof(ShuttleStartOpen), nameof(ShuttleStartOpen.Update))]
static class ShuttleStartOpen_Update_MultiplayerPatch {
    static float timer = 0;
    static void Postfix(ShuttleStartOpen __instance) {
        if (Util.IsWorldGenerated() && Util.TryGetLocalBody(out var b)) {
            if (!KM.DistanceCheck(__instance.transform.position,b.transform.position,12)) {
                
                timer += Time.unscaledDeltaTime;
                if (timer > 5) {
                    if (log.verbose) log.l("ShuttleStartOpen Auto-deleted because local player is too far.");

                    foreach (var item in __instance.doors) {
                        GameObject.Destroy(item);
                    }
                    GameObject.Destroy(__instance);
                }
            }
        } else {
            timer = 0;
        }
    }
}






#if TESTING

public class _BleedParticleStartTrackerComponent : MonoBehaviour { 
    // do nothing, i just tag my shit with a component
}

[HarmonyPatch(typeof(BleedParticle), "Start")]
public static class BleedParticleStartTrackerPatch {

    public static void Postfix(BleedParticle __instance) {

        var wallBleed = __instance.wallBleed;
        var groundBleed = __instance.groundBleed;

        wallBleed.GetOrAddComponent<_BleedParticleStartTrackerComponent>();
        groundBleed.GetOrAddComponent<_BleedParticleStartTrackerComponent>();

    }
}
#endif






















[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.WaitLoad))]
public static class ScavGameStartPatch {
    public static bool starting_game = false;


    static bool Prefix(PreRunScript __instance, ref IEnumerator __result) {

        if (Net.IsRunning) {

            if (starting_game)  {
                Multiplayer.DoMultiplayerStatusMessageLog("Client is starting game by request of server.");

                starting_game = false;
                return true;
            } else if (Net.IsClient)  {
                Multiplayer.DoMultiplayerStatusMessageLog("You can't start the game, server can.");
                starting_game = false;
                __result = (new List<object>()).GetEnumerator(); // shut up the coroutine
                return false;
            }  else   {
                starting_game = true;
                __instance.UpdateAllSettingDisplays(); // make sure the PlayerPrefs values are there

                ServerMain.Server_Announce_GAME_START();


                Multiplayer.showMultiplayerMenu = false;
            }

        }   else     {
            // singleplayer, hide this
            Multiplayer.showMultiplayerMenu = false;
        }


        starting_game = false;

        return true;
    }
    
}












#region TUTORIAL_OVERRIDES


[HarmonyPatch(typeof(TutorialHandler), nameof(TutorialHandler.Start))]
public static class TutorialHandler_Start_MarkiplierPatch {
    static void Postfix(TutorialHandler __instance) {
        if (Net.IsRunning) {
            Util.DelayCallLambda(1, () => {
                Util.DoAlert("Multiplayer Tutorial is not done!\nsorry!", true);
            });
            __instance.StartCourse(typeof(SandboxCourse));
            __instance.courseSelectScreen.SetActive(value: false);
        }
    }
}

[HarmonyPatch(typeof(TutorialHandler), nameof(TutorialHandler.Update))]
public static class TutorialHandler_Update_MarkiplierPatch {
    internal static bool did_multiplayer_warning = false;
    internal static double everyone_finish_time = 0;
    internal static float server_sync_timer = 0;

    //OG: TutorialHandler.PreventFromDying V520dev1
    public static void PreventFromDying_Stolen(Body body) {
        if (!body.alive) return;

        if (body.thirst < 11f) {
            body.thirst = 11f;
        }
        if (body.hunger < 11f) {
            body.hunger = 11f;
        }
        if (body.bloodPressure < 80f) {
            body.bloodPressure = 80f;
        }
        if (body.fibrillationProgress > 95f) {
            body.fibrillationProgress = 95f;
        }
        if (body.brainHealth < 96f) {
            body.brainHealth = 96f;
        }
        if (body.happiness < -30f) {
            body.happiness = -30f;
        }
    }


    static void Postfix(TutorialHandler __instance) {
#if !RELEASE 
        if (Input.GetKeyDown(KeyCode.B)) {
            __instance.speakerTalker.Skip();
            Util.GetLocalBody().forceWalk = false;
        }
#endif  

        if (!Net.IsRunning) return;

        foreach (var item in ScavPlayer.BodyToPlayerDict.Keys) {
            PreventFromDying_Stolen(item);
        }

        if (Net.IsServer) {

            server_sync_timer += Time.unscaledDeltaTime;
            if (server_sync_timer > 0.05f) {
                var writer = Net.CreateWriter(NetmsgId.CLIENT_Tutorial_GlobalState);

                writer.Put((Vector2)__instance.handPos);
                if (__instance.grabInfo.grabbed != null) {
                    writer.Put(AnyObjectNetId.GetNetIdFromGameObject(__instance.grabInfo.grabbed.gameObject));
                } else {
                    writer.Put(new AnyObjectNetId());
                }


                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);

                // WRITER END;
            }

        }

        if (__instance.grabInfo.grabbed != null) {
            if (__instance.grabInfo.grabbed.TryGetComponent(out NetBody nb) ) {
                nb.StopPiggyback();
            }
        }


#if !RELEASE
        __instance.speakerUI.text = "";
#endif


        if (!WorldGeneration.world.generatingWorld && Util.IsTutorialWorld() && PlayerCamera.main.body) {

            // OG: TutorialHandler.Update()   // but im just checking if its below -50 here
            if (!did_multiplayer_warning) {

                PlayerCamera.main.DoAlertDelayed(Lang.Get("tutorial_mp_alert"), true, 4);

                did_multiplayer_warning = true;
            }
            if (did_multiplayer_warning) { // allow runnin
                PlayerCamera.main.body.forceWalk = false;
            }



            PlayerCamera.main.body.consciousness = Mathf.Max(PlayerCamera.main.body.consciousness, 10f + (Body.consciousnessRiseRate + 10f) * Time.deltaTime); // idk i hope this fixes the issue when plr gets tossed into the main menu


            if (everyone_finish_time != 0) {

                // annouce go back to main menu
                if (Time.realtimeSinceStartupAsDouble - everyone_finish_time > 2f && Net.IsServer) {  // when the text box disappears
                    log.l("TutorialHandler_Update_MarkiplierPatch: running ToMainMenu.");
                    PlayerCamera.main.ToMainMenu();
                }
            } else {

            }

        }

    }


    [ClientReceiver((byte)NetmsgId.CLIENT_Tutorial_GlobalState)]
    static void ClientReceiver__Tutorial_GlobalState(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 handPos);
        reader.Get(out AnyObjectNetId grabObj);

        if (TutorialHandler.main != null) {
            TutorialHandler.main.handPos = handPos;
            if (grabObj.TryGetGameObject(out var go)) {
                if (TutorialHandler.main.grabInfo.grabbed != go.transform) {
                    TutorialHandler.main.Grab(go.transform);
                }
            } else {
                if (TutorialHandler.main.grabInfo.grabbed != null) {
                    TutorialHandler.main.Ungrab();
                }
            }
        }
    }
}



#endregion










// Patch unchipped modifier to be properly synced in MP

[HarmonyPatch(typeof(UnchippedModifier), nameof(UnchippedModifier.Initialize))]
internal static class UnchippedModifier_Initialize_MultiplayerPatch {
    internal static Dictionary<NetBody, bool> OriginalUnchippedStates = new();
    static void Prefix(UnchippedModifier __instance) {
        if (!Net.IsRunning) return;

        foreach (var nb in NetBody.all_instances) {
            OriginalUnchippedStates[nb] = nb.unchipped;
        }

    }
}

[HarmonyPatch(typeof(UnchippedModifier), nameof(UnchippedModifier.Disable))]
static class UnchippedModifier_Disable_MultiplayerPatch {
    static void Postfix(UnchippedModifier __instance) {
        if (!Net.IsRunning) return;

        if (UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates == null) {
            UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates = new();
        }
        try {
            foreach (var state in UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates) {
                if (state.Key != null) {
                    state.Key.SetUnchipped(state.Value);
                }
            }
        } catch (Exception e) {
            log.error(e);
        }
        UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates.Clear();
    }
}






