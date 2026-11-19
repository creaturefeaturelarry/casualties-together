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

namespace Together;



// disable talker saying random shit and feelings for remote players
[HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new Type[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) })]
public static class Talker_Talk_MultiplayerPatch {
    public static void Force(Talker __instance, List<string> lines, Limb limb = null, bool force = false, bool resetTalkTimer = false) {
        force_run = true;
        __instance.Talk(lines, limb, force, resetTalkTimer);

        force_run = false;
    }

    public static void ForceNoSpeechImpairment(this Talker talker, string message, bool resetTalkTimer = false) {

        talker.currentString = message;

        talker.text.text = "";
        talker.timeSinceTalked = (0f);

        if (resetTalkTimer) {
            talker.newTalkTime = (0f);
        }
    }
    public static string DistortString(this Talker talker, in string message) {

        MindwipeScript og_mindwipe = null;
        if (talker.body != null) {
            og_mindwipe = talker.body.mindWipe;
            if (!Multiplayer.rules.MindwipeDisablesChat) {
                talker.body.mindWipe = null;
            }
        }
        var og_currentString = talker.currentString;
        var og_timeSinceTalked = talker.timeSinceTalked;
        var og_newTalkTime = talker.newTalkTime;
        var og_instancetext = talker.text.text;

        talker.currentString = ("");

        Talker_Talk_MultiplayerPatch.Force(talker, new List<string> { message }, null, true, false); // make the Talk function do speech impairment effects

        var distortedmsg = talker.currentString; // find the distorted message

        // mp: because if unconscious, u dont talk, so instead of sending an empty message just replace everything with blanks 
        if (String.IsNullOrEmpty(distortedmsg)) {
            StringBuilder stringBuilder = new StringBuilder(message);
            for (int l = 0; l < message.Length; l++) {
                var c = stringBuilder[l];
                if (Chat.CharCanBeReplacedWithBlank(c)) {
                    stringBuilder[l] = '_';
                }
            }
            distortedmsg = stringBuilder.ToString();
        }

        // reset everything to originals
        talker.currentString = (og_currentString);
        talker.timeSinceTalked = (og_timeSinceTalked);
        talker.newTalkTime = (og_newTalkTime);
        talker.text.text = og_instancetext;
        if (talker.body != null) {
            talker.body.mindWipe = og_mindwipe;
        }

        return distortedmsg;
    }

    static bool force_run = false;


    static bool Prefix(Talker __instance) {

        if (Net.IsRunning && !force_run) {
            if (__instance.trader != null) {
                if (Net.IsClient) {
                    return false;
                }
            } else if (Multiplayer.rules.CharacterYapPublic) {
                if (__instance.body != null) {
                    if (Net.IsClient) {
                        return false;
                    }
                }
            } else {
                if (__instance.body != null && !Util.IsBodyLocal(__instance.body)) {
                    return false;
                }
            }
        }
        return true;
    }
    static double lastSentTime = -999999;
    static double sendCooldown = 0.05f;

    static void Postfix(Talker __instance) {
        if (Net.IsRunning && !force_run) {
            var curstr = __instance.currentString;
            var oksendit = delegate (knetid iiii, byte tttttt) {
                if (Time.realtimeSinceStartupAsDouble - lastSentTime > sendCooldown) {
                    lastSentTime = Time.realtimeSinceStartupAsDouble;
                    ServerMain.TalkerSayAnnounce(iiii, tttttt, curstr, ServerMain.AllClientIdsExceptHost);
                }
            };

            if (Net.IsServer && !String.IsNullOrEmpty(curstr)) {
                if (__instance.trader != null) {
                    if (NetObjectRegistry.TryGetSyncInfo(__instance.trader, out var si)) {
                        oksendit(si.syncId, 0);
                    }
                } else if (Multiplayer.rules.CharacterYapPublic) {
                    if (__instance.body != null) {
                        if (__instance.body.TryGetNetPlayer(out var plr)) {
                            oksendit(plr.playerId, 1);
                        }
                    }
                } else {
                    if (__instance.body != null && !Util.IsBodyLocal(__instance.body)) {

                    }
                }
            }
        }
        force_run = false;


    }
}




