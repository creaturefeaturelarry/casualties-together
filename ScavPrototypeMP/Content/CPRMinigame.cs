using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.PlayerLoop;
using UnityEngine.UI;



namespace Together;


public class CPRMinigame : Minigame {



    public CPRHandler handler = game.GetOrAddComponent<CPRHandler>();   

    public Body LOCAL_BODY => Util.GetLocalBody();
    public Body healer => LOCAL_BODY;



    public Body pacient;
    public Limb limb; // DONT USE THIS, ITS ONLY FOR MINIGAME SYNC SO DONT REMOVE THIS


    public bool stupid => healer.skills.INT < CPRHandler.SMART_INT_REQUIREMENT;


    public override HandSpriteType HandType() {
        return HandSpriteType.Grasp;
    }

    public string GetGuideText() {
        if (stupid) {
            return Lang.Get("cpr_minigame_guide_stupid");
        } else {
            return Lang.Get("cpr_minigame_guide");
        }
    }

    public override string GuideLocaleString() {
        return GetGuideText(); 
    }

    public override bool NeedsItem() {
        return false;
    }

    public CPRMinigame(Body body) {
        this.pacient = body;
        this.limb = body.GetUpperTorso();
    }

    public override void Start() {
        handler.pacient = pacient;

        game.CreateScreen("Special/SelfHarmMinigame");
        foreach (var item in game.spawnedMiniGame.GetComponentsInChildren<Image>()) {
            var c = item.color;
            c.a = 0;
            item.color = c;
        }

        handler.MG_CreateSecondHand();
    }


    public override void Update(List<RaycastResult> uiCasts) {

        if (pacient == null) {
            MinigameMPManager.Note("cpr end, patient gone");
            handler.MG_End();
            PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
            return;
        }
        if (!KM.DistanceCheck(pacient,LOCAL_BODY,SharedMain.max_player_interaction_distance*2)) {
            MinigameMPManager.Note("cpr end, too far");
            handler.MG_End();
            PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
            return;
        }

        game.guideText.text = GuideLocaleString();

        handler.MG_Update(uiCasts);
    }



    public override void PhysicsUpdate(float deltaTime) {
        handler.MG_PhysicsUpdate(deltaTime);
    }

    // not necessary cuz its override in MG_Update
    public override float HandRotOffset() {
        return handler.cpr_rot;
    }
}


public class CPRHandler : MonoBehaviour {


    public double last_click_time = 0;
    public double pressed_time = 0;

    public float last_full_cycle_time = 0;
    public float last_hold_time = 0;

    public float last_nohold_time_permin = 0;
    public float last_hold_time_permin = 0;

    public float cur_cpr_rate = 0;

    public static float CPR_RATE_SMOOTHING = 0.3f;


    public bool is_minigame = true;

    public void Awake() {
        healer = Util.GetLocalBody();
    }

    public void LateUpdate() {
        if (is_minigame) {
            if (game.currentMinigame == null) {
                Destroy(this);
                return;
            }
        }
    }



    public static RangeF AcceptableRate = new RangeF(65, 220); // 100-120 but more forgiving
    


    public void MG_End() {
        game.EndMinigame();
        Destroy(this);
    }

    public void MG_CreateSecondHand() {

        {
            var ni = GameObject.Instantiate(MinigameBase.main.guideText.gameObject, game.spawnedMiniGame, false);
            var aaaaaaaaaaaaaaaaa = ni.GetComponent<RectTransform>();
            var ttx = ni.GetComponent<TextMeshProUGUI>();
            ttx.text = Lang.Get("click_to_cpr");
            aaaaaaaaaaaaaaaaa.offsetMin = Vector2.zero;
            aaaaaaaaaaaaaaaaa.offsetMax = Vector2.zero;
            aaaaaaaaaaaaaaaaa.anchorMin = Vector2.one * 0.25f;
            aaaaaaaaaaaaaaaaa.anchorMax = Vector2.one * 0.75f;
            ttx.color *= 0.5f;
            ttx.fontSize *= 1.3f;
        }

        var n = GameObject.Instantiate(MinigameBase.main.handTransform.gameObject, game.spawnedMiniGame, false);
        MG_SecondHandTransform = n.GetComponent<RectTransform>();
        MG_SecondHandSprite = n.GetComponent<Image>();


        var sc = MinigameBase.main.handTransform.localScale;
        sc.x *= -1f;
        MG_SecondHandTransform.localScale = sc;
        MG_SecondHandSprite.color = new Color(1,1,1,0);//og Color(r: 1, g: 1, b: 1, a: 0.8235294f)
    }

    public Body healer;
    public Body pacient;

    public Limb mainhand => healer.GetPrimaryHand();
    public Limb secondhand => healer.GetSecondaryHand();

    public RectTransform MG_SecondHandTransform;
    public Image MG_SecondHandSprite;

    
    public static int SMART_INT_REQUIREMENT = 12;
    public static int MINIMUM_STR_REQUIREMENT = 3;
    public static float GetHandMaxTotalForce(Body body) {
        var hand1 = body.limbs[5];
        var hand2 = body.limbs[8];
        var maxforce = Mathf.Max(hand1.totalForce, hand2.totalForce);
        return maxforce;
    }
    public static bool CheckIfStrongEnoughToPerformCPR(Body body) {
        var maxforce = GetHandMaxTotalForce(body);
        if ((float)body.skills.STR * maxforce > MINIMUM_STR_REQUIREMENT) {
            return true;
        }
        return false;
    }
    public static bool CheckIfBodyIsNotOkAndNeedCPR(Body body) {
        if ((body.heartRate < 50 && body.bloodOxygen < 100 && body.bloodPressure < 30) || body.heartRate < 5 || body.isDying || body.isCriticallyDying) {
            return true;
        }
        return false;
    }
    public static bool IsCPRBeingPerformedOnThisBody(Body b, out ScavPlayer who) {
        foreach (var plr in ScavPlayer.AllLivingPlayers) {
            if (plr.body.conscious && plr.IsInMinigame) {
                var isincprminigame =
                    plr.minigame_current_type == MinigameMPManager.GetMinigameTypeId(typeof(CPRMinigame))
                    || plr.minigame_current_type == MinigameMPManager.GetMinigameTypeId(typeof(AEDMinigame));

                if (isincprminigame && plr.minigame_targetobject.TryGetNetBody(out var nb) && nb.body == b) {
                    who = plr;
                    return true;
                }
            }
        }
        who = null;
        return false;
    }
    public static bool IsCPRBeingPerformedOnThisBody_IsMeOrNobody(Body b) {
        return !IsCPRBeingPerformedOnThisBody(b, out var who) || who.IsLocal;
    }
    public static bool IsCPRBeingPerformedOnThisBody_IsThisGuyOrNobody(Body b, ScavPlayer plr) {
        return !IsCPRBeingPerformedOnThisBody(b, out var who) || who == plr;
    }


    public bool stupid => healer.skills.INT < SMART_INT_REQUIREMENT;
    public bool is_in_cpr_zone = false;
    public bool is_ui_overlapping = false;
    public bool ready_for_cpr = false;
    public bool clicking_into_cpr = false;
    public bool clicking_not_into_cpr = false;

    public string cpr_status = "";
    public double cpr_status_last_change = 0;

    public float cpr_rot = 0;
    float smooth_cpr_handscale = 1;
    float cpr_press_scalar => KM.RemapClamped(smooth_cpr_handscale,1, CPR_SCALE_PRESS_TARGET,0,1);
    

    static float CPR_SCALE_PRESS_TARGET = 0.55f;

#if TESTING
    void OnGUI() {

        if (is_minigame) {

            var scaled = UIBullshit.ScaleSize(500.91f, 611.41f);
            var mainpanel = new Rect(0, 0, scaled.x, scaled.y);
            mainpanel.center = new Vector2(Screen.width - scaled.x*0.5f, UIBullshit.GetHalfScreenSize().y );

            GUI.Box(mainpanel, "");
            GUILayout.BeginArea(mainpanel);
            try {
                GUILayout.Label("TODO TIMES N SHIT");
                GUILayout.Label("cpr_press_scale: " + Math.Round(cpr_press_scalar, 2).ToString());
                //GUILayout.Label("pressed_time: " + Math.Round(pressed_time,2).ToString());
                //GUILayout.Label("unpressed_time: " + (Math.Round(last_click_time, 2).ToString()));
                GUILayout.Label($"last_hold_time_permin: {Math.Round(last_hold_time_permin)}");
                GUILayout.Label($"last_nohold_time_permin: {Math.Round(last_nohold_time_permin)}");
                GUILayout.Label($"cur_cpr_rate: {Math.Round(cur_cpr_rate)}");
                

            } catch (Exception e) {
                log.error( "CPR ONGUI " + e.ToString());
            }
            GUILayout.EndArea();
        }

    }
#endif

    public static float CPR_STAMINA_USE_MULTIPLIER = 1;
    public static void CPR_UseStaminaOnPress(Body body) {
        body.stamina -= 0.9f * CPR_STAMINA_USE_MULTIPLIER;
        body.temperature += 0.02f * CPR_STAMINA_USE_MULTIPLIER;
    }

    void HandStoppedCPRPress() {

        var holdtime = Mathf.Max(0.001f, (float)(Time.unscaledTimeAsDouble - pressed_time));
        var lastcycletime = Mathf.Max(0.001f, (float)(Time.unscaledTimeAsDouble - last_click_time));
        var lastclicktime = Mathf.Max(0.001f, lastcycletime - holdtime);

        var widened = AcceptableRate.Widen(10f);

        last_click_time = Time.unscaledTimeAsDouble;

        var torate = 60;

        var holdtime_permin = torate / (holdtime * 2f);
        var lastclicktime_permin = torate / (lastclicktime * 2f);
        var permin_middle = Mathf.LerpUnclamped(holdtime_permin, lastclicktime_permin, 0.5f);
        holdtime_permin = Mathf.LerpUnclamped(holdtime_permin, permin_middle, CPR_RATE_SMOOTHING);
        lastclicktime_permin = Mathf.LerpUnclamped(lastclicktime_permin, permin_middle, CPR_RATE_SMOOTHING);


        last_full_cycle_time = lastcycletime;
        last_hold_time = holdtime;

        last_hold_time_permin = holdtime_permin;
        last_nohold_time_permin = lastclicktime_permin;

        var averaged_rate = Mathf.LerpUnclamped(torate / (lastcycletime), permin_middle, CPR_RATE_SMOOTHING);
        cur_cpr_rate = Mathf.Lerp(cur_cpr_rate, averaged_rate, 0.5f);


        if (pacient.TryGetComponent(out NetBody pacient_nb)) {
            var writer = Net.CreateWriter(NetmsgId.SERVER_CPR_PRESSED);

            writer.Put((knetid)pacient_nb.netId);
            writer.Put((float)holdtime);
            writer.Put((float)lastclicktime);
            writer.Put((float)averaged_rate);

            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

            // WRITER END;
        }



        if (!stupid) {
            if (AcceptableRate.IsInRange(holdtime_permin) && AcceptableRate.IsInRange(lastclicktime_permin)) {
                cpr_status = Lang.Get("cpr_minigame_good");
            } else {
                if (AcceptableRate.IsInRange(averaged_rate)) {
                    cpr_status = Lang.Get("cpr_minigame_bad_timing");
                } else if (permin_middle > AcceptableRate.Center()) {
                    cpr_status = Lang.Get("cpr_minigame_fast");
                } else {
                    cpr_status = Lang.Get("cpr_minigame_slow");
                }
            }
            cpr_status_last_change = Time.realtimeSinceStartupAsDouble;
        }


        //Lang.Get("cpr_minigame_slow")


    }


    static float CHANCE_TO_BREAK_RIBS = 0.008f;
    static int REQUIRED_PRESS_COUNT_FOR_HEART_KICKSTART = 30;

    [ServerReceiver((byte)NetmsgId.SERVER_CPR_PRESSED)]
    static void ServerReceiver_CPR_PRESSED(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid pacient_netid);
        reader.Get(out float holdtime);
        reader.Get(out float lastclicktime);
        reader.Get(out float averaged_rate); // is overriden
        
        if (
            ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && NetBody.TryGetNetBodyFromId(pacient_netid, out var pacient_nb) 
            
        ) {
            if (!(holdtime.IsFinite() && lastclicktime.IsFinite() && holdtime > 0 && lastclicktime > 0)) {
                //SUS
                return;
            }

            var healer = pb.body;
            var pacient = pacient_nb.body;

            if (!healer.IsBodyLocal()) {
                CPR_UseStaminaOnPress(healer);
            }

            if (!CheckIfStrongEnoughToPerformCPR(healer)) {
                //too weak, it should cancel the minigame
                return;
            }

            var time_passed_since_press = (float)(Time.realtimeSinceStartupAsDouble - pacient_nb.lastTimeCprPress);
            pacient_nb.lastTimeCprPress = Time.realtimeSinceStartupAsDouble;
            if (time_passed_since_press <= 0) {
                return;
            } else if (time_passed_since_press > 2) {
                pacient_nb.cpr_cur_perfect_press_count = 0;
            }

            var hold_diff = Mathf.Abs(holdtime - lastclicktime);
            var fullholdtime = holdtime + lastclicktime;
            var real_rate = 60f / fullholdtime;
            averaged_rate = real_rate;

            if (fullholdtime <= 0) {
                return;
            }
            
            if (!AcceptableRate.IsInRange(real_rate)) {
                pacient_nb.cpr_cur_perfect_press_count = 0;
                return;
            }


            if (time_passed_since_press < 2) {
                pacient_nb.cpr_cur_perfect_press_count ++;
            }


            if (fullholdtime * 0.9f > time_passed_since_press) {
                var diff = time_passed_since_press / fullholdtime;
                
                averaged_rate = (60f / time_passed_since_press);
                fullholdtime = time_passed_since_press;
                holdtime *= diff;
                lastclicktime *= diff;
            }


            float effectivness = Mathf.Clamp01(1 - hold_diff);

            var rate_center = AcceptableRate.Center();
            
            float pumpamount = 0;
            if (effectivness > 0.2f) {

                var isribsbroken = pacient.IsRibsBroken();
                if (isribsbroken) {
                    effectivness = Mathf.Lerp(effectivness, 1, 0.5f);
                }
                //  all these random numbers and multiplications n formulas n shi are completely made up, lets see if it works

                if (effectivness > 0.5f) {

                    if (pacient.bloodVolume > 0) {
                        pumpamount = (Mathf.Clamp(pacient.bloodVolume, 30, 110) * Mathf.Clamp(holdtime*1.13f,0.06f,0.3f)) * effectivness; 
                    }

                    if (pacient.bloodOxygen < 50f && (pacient.IsAirBreathable() || healer.IsAirBreathable())) {
                        pacient.bloodOxygen += pumpamount * 0.7f;
                    }

                    if (pacient.bloodPressure < 100f) {
                        pacient.bloodPressure += pumpamount;
                    }

                    if (averaged_rate > rate_center && effectivness < 0.9f && !isribsbroken && healer.skills.STR > 8) { // if doing it fast and messed up the timing, chance to break ribs
                        float chance_to_break_ribs = (healer.skills.STR - healer.skills.INT*0.7f) * CHANCE_TO_BREAK_RIBS;
                        if (chance_to_break_ribs > 0 && UnityEngine.Random.value < chance_to_break_ribs) {
#if !RELEASE
                            log.warn($"SERVER: :: CPR PRESSED :: BROKE RIBS LOOOOOOOOOOOOOOL chance:{chance_to_break_ribs}");
#endif

                            var l = pacient.GetUpperTorso();
                            DebugHelp.OnNetEvent(l.GetPosition(), $"BROKE RIBS chance:{chance_to_break_ribs}");

                            if (!l.broken) { // code borrowed from BreakBone
                                l.broken = true;
                                l.skinHealth -= 10f;
                                l.pain += UnityEngine.Random.Range(20f, 50f);
                                l.body.adrenaline += 30f;
                                l.MakeBoneSprite();
                                Sound.Play("BoneBreak" + UnityEngine.Random.Range(1, 4), l.transform.position);
                                l.boneHealTimer = 100f;
                                l.body.PromptTalk();
                                l.body.DoGoreSound();
                                l.body.internalBleeding += 5f + chance_to_break_ribs;
                            } else {
                                l.boneHealTimer = 100f;
                            }
                        }
                    }

                    if (pacient.bloodPressure > 80 && pacient_nb.cpr_cur_perfect_press_count > REQUIRED_PRESS_COUNT_FOR_HEART_KICKSTART && pacient.alive && pacient.inCardiacArrest && pacient.bloodVolume > 10) {
                        var roll = UnityEngine.Random.value * effectivness;
                        if (roll > 0.8f) { // kickstart my heart !!! (gamified ROSC or smth)
#if !RELEASE
                            log.warn($"SERVER: :: CPR PRESSED :: KICKSTARTED THE HEART !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
#endif
                            DebugHelp.OnNetEvent(pacient.GetPosition(), $"KICKSTARTED THE HEART  cur roll:{roll}   cur press count:{pacient_nb.cpr_cur_perfect_press_count} ");
                            pacient.heartRate = Mathf.Max(pacient.heartRate, Mathf.LerpUnclamped(pacient.heartRate, averaged_rate * 0.6f, effectivness));
                        }
                    }

                    MedicalSync.Server_QueueSendCharacterHealth(pacient_nb);
                }
            }

            if (log.verbose) {
                var gggggggg = $"SERVER: :: CPR PRESSED :: EFFECT:{effectivness}\tCENTER:{rate_center}\tpumpamount:{pumpamount}\tpress count:{pacient_nb.cpr_cur_perfect_press_count}";
#if !RELEASE
                log.l(gggggggg);
#endif
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pacient_nb.position, gggggggg);
            }


            return;
        }


    }

    public bool aed_minigame_overlapping_with_pads = false;
    public Vector2 last_minigame_mouse_localpos = Vector2.zero;

    public static MinigameBase game => MinigameBase.main;
    public void MG_Update(List<RaycastResult> uiCasts) {
        if ((pacient.standing && pacient.moveDir != Vector2.zero) || !healer.standing) {
            MG_End();
            PlayerCamera.main.DoAlert(Lang.Get("plr_moving"));
            return;
        }

        bool is_aed_minigame = false;
        if (is_minigame && game.currentMinigame != null) {
            is_aed_minigame = game.currentMinigame is AEDMinigame;
            if (Time.realtimeSinceStartupAsDouble - cpr_status_last_change < 5 && !String.IsNullOrEmpty(cpr_status)) {
                game.guideText.text = game.currentMinigame.GuideLocaleString() + "\n" + cpr_status;
            }
        }


        var mouse_localpos = MinigameMPManager.GetLocalMousePosMinigameSpace();

        var last_is_in_cpr_zone = is_in_cpr_zone;
        {
            var sq = 110;
            var hand = Mathf.Abs(game.handPos.x) < sq && Mathf.Abs(game.handPos.y) < sq;
            var mouse = Mathf.Abs(mouse_localpos.x) < sq && Mathf.Abs(mouse_localpos.y) < sq;

            is_in_cpr_zone = hand && mouse;
        }

        if (is_aed_minigame && is_in_cpr_zone) {
            var as_aed = (AEDMinigame)game.currentMinigame;
            if (last_minigame_mouse_localpos != mouse_localpos || last_is_in_cpr_zone != is_in_cpr_zone) {
                aed_minigame_overlapping_with_pads = uiCasts.Count(
                    x => as_aed.pads.Contains(x.gameObject.GetComponent<RectTransform>())
                ) > 0; 
            }
            if (as_aed.currentlyHeld != null) {
                aed_minigame_overlapping_with_pads = true;
            }
        } else {
            aed_minigame_overlapping_with_pads = false;
        }


        is_ui_overlapping = aed_minigame_overlapping_with_pads || uiCasts.Count(
            x => x.gameObject.transform.IsChildOf(game.spawnedMiniGame) &&
                (
                    x.gameObject.GetComponent<Button>() != null
                )
            ) > 0;


        float righthanded = Mathf.Sign(game.handTransform.localScale.x);
        float possible_cprot_target = (stupid ? 30 : 36) * righthanded;


        var og_handSprite = game.handTransform.GetComponent<Image>();
        game.handTransform.localScale *= smooth_cpr_handscale;

        {
            var mirrored_handpos = game.handPos;
            mirrored_handpos.x *= -1;
            MG_SecondHandTransform.anchoredPosition = mirrored_handpos;
            var sc = game.handTransform.localScale;
            sc.x *= -1f;
            MG_SecondHandTransform.localScale = sc;

            game.handTransform.eulerAngles = new Vector3(0f, 0f, game.handVelocity.x * 0.5f + cpr_rot); // OG: MinigameBase.UpdateHandVisuals V520 and it works cuz UpdateHandVisuals is called before update

            //handTransform.eulerAngles = new Vector3(0f, 0f, Mathf.Lerp(-game.handTransform.eulerAngles.z, -possible_cprot_target,0.25f));
            //handTransform.eulerAngles = new Vector3(0f, 0f, Mathf.Lerp(-game.handTransform.eulerAngles.z, -cpr_rot, 0.25f));
            //MG_SecondHandTransform.eulerAngles = -game.handTransform.eulerAngles;
            MG_SecondHandTransform.eulerAngles = new Vector3(0f, 0f, game.handVelocity.x * 0.5f - cpr_rot);
        }

        {
            var c = MG_SecondHandSprite.color;

            float target = 0f;
            if (!clicking_not_into_cpr) {
                if (ready_for_cpr || (!is_ui_overlapping && (game.handPos.x * righthanded > 1 || is_in_cpr_zone))) {
                    target = Mathf.Min(
                        KM.RemapClamped(Mathf.Abs(game.handPos.x), 120, 90, 0f, 0.995f),
                        KM.RemapClamped(Mathf.Abs(game.handPos.y), 120, 90, 0f, 0.9951f)
                    );
                    if (!ready_for_cpr) {
                        target *= target * 0.5f;
                    }
                }
            }
            c.a = Mathf.MoveTowards(c.a, target, Time.deltaTime * 10f);

            MG_SecondHandSprite.color = c;
        }

        {
            var c = og_handSprite.color;

            c.a = KM.RemapClamped(Mathf.Abs(game.handPos.x), 220, 130, 0.8f, 0.99f);

            og_handSprite.color = c;
        }


        if (game.handStoppedClicking) {
            if (clicking_into_cpr) {
                HandStoppedCPRPress();
            }
        }
        if (game.handStartedClicking) {
            if (!KM.DistanceCheck(pacient, healer, SharedMain.max_player_interaction_distance * 2f)) {
                MG_End();
                PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
                return;
            }

            clicking_into_cpr = ready_for_cpr;
            if (clicking_into_cpr) {
                pressed_time = Time.unscaledTimeAsDouble;

                CPR_UseStaminaOnPress(healer);

                if (!CheckIfStrongEnoughToPerformCPR(healer)) {
                    PlayerCamera.main.DoAlert(Lang.Get("ur_too_weak"));
                    if (!is_aed_minigame) {
                        MG_End();
                        return;
                    }
                } else {
                    if (smooth_cpr_handscale > 0.9f) {
                        var is_properly_secured_on_ground = pacient.IsRagdolling() || pacient.grounded;

                        if (is_properly_secured_on_ground) {
                        } else {
                            if (healer.TryGetNetBody(out var nb) && pacient.TryGetNetBody(out var pacient_nb)) {

                                nb.Push(pacient_nb);

                                MG_End();
                                return;
                            }
                        }

                    }
                }

            }
        } else if (!game.handClicking) {
            clicking_into_cpr = false;

            ready_for_cpr = is_in_cpr_zone && !is_ui_overlapping;
        }
        clicking_not_into_cpr = game.handClicking && !clicking_into_cpr;




        float cpr_press_target = 1;
        float cprrot_target = 0;



        if (ready_for_cpr) {


            cprrot_target = possible_cprot_target;

            if (game.handClicking) {
                og_handSprite.sprite = game.handSprites[0];

                cpr_press_target = CPR_SCALE_PRESS_TARGET;

            } else {
                og_handSprite.sprite = game.handSprites[1];
            }
            if (secondhand.dismembered) {
                MG_SecondHandSprite.sprite = game.handSprites[10];
            } else {
                MG_SecondHandSprite.sprite = og_handSprite.sprite;
            }

            if (mainhand.dismembered) {
                og_handSprite.sprite = game.handSprites[10];
            }

        }
        cpr_rot = Mathf.MoveTowards(cpr_rot, cprrot_target, Time.deltaTime * 360f);

        {

            smooth_cpr_handscale = Mathf.Lerp(smooth_cpr_handscale, cpr_press_target, Time.deltaTime *
                GetPushSpeed(Util.GetLocalBody(), pacient, game.handClicking)
            );
        }


        last_minigame_mouse_localpos = mouse_localpos;
    }

    public void MG_PhysicsUpdate(float deltaTime) {
        if (ready_for_cpr) {

            var target = new Vector2(40, 0);
            if (!stupid) {
                target = new Vector2(-40, 5);
            }
            game.handPos = Vector2.Lerp(game.handPos, target * smooth_cpr_handscale, deltaTime * 10f);
            game.handVelocity = Vector2.Lerp(game.handVelocity, Vector2.zero, deltaTime * 10f);
        }
    }


    public static float GetPushSpeed(Body healer, Body pacient, bool pressing) {
        var con = healer.consciousness * 0.01f * 7f;

        float n = 0;

        n += (10f + con + healer.skills.STRFrom10 * 0.5f);

        if (pacient.IsRibsBroken()) {
            n += 5;
            if (!pressing) {
                n = Mathf.Max(18f, n);
            }
        } else {
            if (!pressing) {
                n = Mathf.Max(10f, n);
            }
        }

        if (pressing) {
            n *= Mathf.Clamp(0.6f + GetHandMaxTotalForce(healer) * 0.5f, 0.6f, 1f);
        }

        return n;
    }





}



[HarmonyPatch(typeof(AEDMinigame), nameof(AEDMinigame.Start))]
public static class AEDMinigame_Start_MultiplayerPatch {
    public static void Postfix(AEDMinigame __instance) {
        if (!Net.IsRunning) return;

        if (__instance is AEDMinigame aedmg && !aedmg.limb.body.IsBodyLocal()) {
            var cpr = MinigameBase.main.GetOrAddComponent<CPRHandler>();
            cpr.pacient = aedmg.limb.body;
            cpr.MG_CreateSecondHand();
        }

#if !RELEASE && false
        // insta-setup cheat
        if (ConsoleTogether.CanCheat()) {
            for (int i = 0; i < __instance.pads.Count; i++) {
                __instance.pads[i].localPosition = AEDMinigame.desiredSpots[i];
            }
        }
#endif
    }
}

[HarmonyPatch(typeof(AEDMinigame), nameof(AEDMinigame.Update))]
public static class AEDMinigame_Update_MultiplayerPatch {
    public static int last_state = 0;
    public static bool did_send_charge = false;
    public static void Postfix(AEDMinigame __instance, ref List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) return; 

        if (__instance is AEDMinigame aedmg ) {

            if (aedmg.state == 3 && last_state != aedmg.state) {

                if (MinigameBase.main.currentItem != null && NetObjectRegistry.TryGetSyncInfo(MinigameBase.main.currentItem, out var curitemsi)) {
                    var writer = Net.CreateWriter(NetmsgId.SERVER_AED_CHAAAARGEE);
                    writer.Put(curitemsi.syncId);
                    writer.Put(new LimbNetId(aedmg.limb));

                    Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    // WRITER END;
                } else {
                    NetObjectRegistry.AlertObjectNotRegistered(true);
                }
            }
            last_state = aedmg.state;

            if ( !aedmg.limb.body.IsBodyLocal()) {
                var cpr = MinigameBase.main.GetOrAddComponent<CPRHandler>();
                cpr.pacient = aedmg.limb.body;
                cpr.MG_Update(uiCasts);
            }
        }
        
    }

    [ServerReceiver((byte)NetmsgId.SERVER_AED_CHAAAARGEE)]
    static void ServerReceiver_AED_CHAAAARGEE(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid aed_syncid);
        reader.Get(out LimbNetId netlimb);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) ) {

            if (netlimb.TryGetNetBodyAndLimbSafe(out var pacient, out var target_limb) && NetObjectRegistry.TryGetSyncInfo(aed_syncid, out var aed_si) ) {

                if (aed_si.IsAED() && aed_si.item == plr.minigame_currentItem && ItemSync.CheckIfBodyReachThisItem(aed_si,pb.body)) {

                    if (!aed_si.item.battery.hasCharge) {
                        log.serverdeny($"AED CHARGE for {plr} -> {aed_si}: ran out of battery");
                        return;
                    }
                    if (!plr.ServerPlayerState.Cooldown("AED",3.5f)) {
                        return;
                    }

                    ServerMain.Server_AnnounceSound( target_limb.GetPosition(), "aedcharge", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));

                    if (!plr.IsLocal) {
                        aed_si.item.battery.DrainCharge(0.02f); // state  1

                        Util.DelayCallLambda(3.5f, () => {
                            if (aed_si.item == plr.minigame_currentItem) {
                                if (log.verbose) log.l($"{plr} fired an AED {aed_si}");
                                Item_Defibrillate_MultiplayerPatch.force = true;
                                aed_si.item.Defibrillate(new Item.DefibInfo {
                                    chance = 1  ,
                                    limb = target_limb
                                });
                                aed_si.item.battery.DrainCharge(0.14f);
                            } else {
                                if(log.verbose)log.warn($"{plr} CANCELLED AED ???");
                            }
                        });

                    }

                    return;
                }


                log.serverdeny($"AED CHARGE for {plr} cuz its not aed or he doesnt even have an aed ");
                plr.Server_DoAlertSingle("AED Denied, idk why");
                MinigameMPManager.Server_ForceEndMinigameForPlayer(plr);

                return;
            }


            return;
        }


    }

}




[HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.Update))]
public static class ManualDefibMinigame_Update_MultiplayerPatch {
    public static void Postfix(ManualDefibMinigame __instance) {
        if (!Net.IsRunning) return;

        if (__instance.onTorso && __instance.limb != null && !__instance.limb.IsBodyLocal()) {
            __instance.ecg.followBody = false;
            __instance.ecg.writeHeight = __instance.limb.body.GetECGHeight(__instance.ecg.timeToUpdate - 0.028f);
        }
    }
}

[HarmonyPatch(typeof(Minigame), nameof(Minigame.PhysicsUpdate))]
public static class Minigame_PhysicsUpdate_MultiplayerPatch {
    public static void Postfix(Minigame __instance, ref float deltaTime) {
        if (!Net.IsRunning) return;

        if (__instance is AEDMinigame aedmg) {
            if (!aedmg.limb.body.IsBodyLocal()) {
                var cpr = MinigameBase.main.GetOrAddComponent<CPRHandler>();
                cpr.pacient = aedmg.limb.body;
                cpr.MG_PhysicsUpdate(deltaTime);
            }
        }
    }
}

//nah (old)
//// patch aed minigame phyiscs update
//[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.UpdateHandPhysics))]
//public static class MinigameBase_UpdateHandPhysics_MultiplayerPatch {
//    public static void Postfix(MinigameBase __instance) {
//        if (!Net.running) return;
//
//        if (__instance.currentMinigame == null) return;
//
//        if (__instance.currentMinigame is AEDMinigame aedmg && !aedmg.limb.body.IsBodyLocal()) {
//            MinigameBase.main.GetOrAddComponent<CPRHandler>().MG_PhysicsUpdate(1f / MinigameBase.PHYSICS_UPDATE_RATE);
//        }
//    }
//}



[HarmonyPatch(typeof(Item), nameof(Item.Defibrillate))]
public static class Item_Defibrillate_MultiplayerPatch {
    public static bool force = false;
    static bool Prefix(Item __instance, Item.DefibInfo info) {
        if (!Net.IsRunning) return true;
        if (force) {
            force = false;
            return true;
        }

        if (MinigameBase.main?.currentItem == __instance && MinigameBase.main.currentMinigame != null) {// check if its run locally
            if (MinigameBase.main.currentMinigame is ManualDefibMinigame md_mg) { // check if its manual defib
                if (NetObjectRegistry.TryGetSyncInfo(MinigameBase.main.currentItem, out var curitemsi)) {

                    if (__instance.IsManualDefibrillator()) {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_ManualDefibrillator_Defibrillate);
                        writer.Put(curitemsi.syncId);
                        writer.Put(new LimbNetId(info.limb));
                        writer.Put((float)info.chance);
                        writer.Put((float)md_mg.currentCharge);

                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                        // WRITER END;
                    }



                    if (Net.IsClient) {
                        if (MinigameBase.main.currentItem != null) {

                            //OG: Item.Defibrillate V600 steam playtest 1

                            info.limb.skinHealth -= 5f;
                            info.limb.pain += 20f;
                            info.limb.body.Ragdoll();
                            if (info.limb != info.limb.body.limbs[1] || !info.limb.body.alive) {
                                return false;
                            }

                            info.limb.body.defibShockedFrames = 20;
                            info.limb.body.heartProg = -1f;
                            info.limb.body.bloodPressure = 40f;
                        } else {
                            NetObjectRegistry.AlertObjectNotRegistered(true);
                        }


                        return false;
                    } else {

                    }

                } else {
                    if (Net.IsClient) {
                        NetObjectRegistry.AlertObjectNotRegistered(true);
                        return false;
                    }
                }
            } 
        } else {
            
        }



        return true;
    }



    [ServerReceiver((byte)NetmsgId.SERVER_ManualDefibrillator_Defibrillate)]
    static void ServerReceiver_ManualDefibrillator_Defibrillate(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid md_syncid);
        reader.Get(out LimbNetId netlimb);
        reader.Get(out float IGNORE_chance);
        reader.Get(out float currentCharge);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)) {

            if (!currentCharge.IsFinite() || currentCharge < 10 || currentCharge > 200) { // cheater
                log.sus($"{plr} tried to defibrillate with some weirdass currentCharge: {currentCharge}");
                return;
            }

            if (netlimb.TryGetNetBodyAndLimbSafe(out var pacient, out var target_limb) && NetObjectRegistry.TryGetSyncInfo(md_syncid, out var manualdefib_si)) {
                if (manualdefib_si.IsManualDefibrillator() && manualdefib_si.item == plr.minigame_currentItem && ItemSync.CheckIfBodyReachThisItem(manualdefib_si, pb.body)) {

                    if (!manualdefib_si.item.battery.hasCharge) {
                        log.serverdeny($"MANUALDEFIB CHARGE for {plr} -> {manualdefib_si}: ran out of battery");
                        return;
                    }
                    if (!plr.ServerPlayerState.Cooldown("MANUALDEFIB", 0.2f)) {
                        return;
                    }

                    ServerMain.Server_AnnounceSound(target_limb.GetPosition(), "manualdefib", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));

                    if (!plr.IsLocal) {
                        if (log.verbose) log.l($"{plr} fired a MANUALDEFIB {manualdefib_si}");

                        //OG: ManualDefibMinigame.ChanceToWork V520dev1
                        var real_chance = 1f - Mathf.Abs(target_limb.body.fibrillationProgress - currentCharge * 0.5f) / 40f;

                        Item_Defibrillate_MultiplayerPatch.force = true;
                        manualdefib_si.item.Defibrillate(new Item.DefibInfo {
                            chance = real_chance,
                            limb = target_limb
                        });
                        manualdefib_si.item.battery.DrainCharge(currentCharge / 4000f);

                        
                    }

                    return;
                }


                log.serverdeny($"MANUALDEFIB CHARGE for {plr} cuz its not manualdefib or he doesnt even have a it ");
                plr.Server_DoAlertSingle("MANUALDEFIB Denied, idk why");
                MinigameMPManager.Server_ForceEndMinigameForPlayer(plr);

                return;
            }


            return;
        }


    }

}













