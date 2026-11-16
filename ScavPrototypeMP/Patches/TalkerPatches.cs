using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
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

// locale.GetCharacter(string) uses the camera body, so remote eats come out as experiment.
// the override body wins, then whoever is speaking.
public static class SpeechCharContext {
    public static Body speaker;
    // body.update / talker.update. a leftover override from the other player must not pick lines
    public static int scope;

    public struct Pin {
        public bool run;
        public CoUtils prev;
    }

    static int ClampChar(int chr) {
        if (chr < 0) {
            return 0;
        }
        if (chr > 2) {
            return 2;
        }
        return chr;
    }

    public static int ResolveCharType() {
        // only trust the override while a body or talker update is actually on the stack.
        // layer arrival (wwmr / ascententer) runs later, and the last talker used to stay selected.
        if (scope > 0) {
            CoUtils cu = CoUtils_instance_MultiplayerPatch.cur_override_instance;
            if (cu != null && cu.TryGetComponent(out Body overrideBody) && overrideBody != null) {
                return ClampChar(overrideBody.charType);
            }
        }
        if (speaker != null) {
            return ClampChar(speaker.charType);
        }
        if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
            return ClampChar(PlayerCamera.main.body.charType);
        }
        return 0;
    }

    public static void PinPrefix(Body body, ref Pin pin) {
        pin.run = false;
        if (!Net.IsRunning || body == null) {
            return;
        }
        pin.run = true;
        pin.prev = CoUtils_instance_MultiplayerPatch.cur_override_instance;
        scope++;
        speaker = body;
#if FULLGAME
        CharacterSpawn.EnsureBodySpecies(body);
#endif
        body.GetCoUtilsInstance().SelectForMPOverride();
    }

    public static void PinPostfix(Body body, Pin pin) {
        if (!pin.run) {
            return;
        }
        if (speaker == body) {
            speaker = null;
        }
        if (scope > 0) {
            scope--;
        }
        CoUtils_instance_MultiplayerPatch.cur_override_instance = pin.prev;
    }
}

[HarmonyPatch(typeof(Locale), nameof(Locale.GetCharacter), new Type[] { typeof(string) })]
static class Locale_GetCharacter_Speaker_MultiplayerPatch {
    static bool Prefix(string str, ref List<string> __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = Locale.GetCharacter(str, SpeechCharContext.ResolveCharType());
        return false;
    }
}

[HarmonyPatch(typeof(Talker), "Update")]
static class Talker_Update_SpeechChar_MultiplayerPatch {
    static void Prefix(Talker __instance, ref SpeechCharContext.Pin __state) {
        SpeechCharContext.PinPrefix(__instance != null ? __instance.body : null, ref __state);
    }

    static void Postfix(Talker __instance, SpeechCharContext.Pin __state) {
        SpeechCharContext.PinPostfix(__instance != null ? __instance.body : null, __state);
    }
}

// eatGood/Bad/etc call GetCharacter before Talk - pin speaker for that path too
[HarmonyPatch(typeof(Talker), nameof(Talker.EatGood))]
[HarmonyPatch(typeof(Talker), nameof(Talker.EatBad))]
[HarmonyPatch(typeof(Talker), nameof(Talker.EatMediocre))]
[HarmonyPatch(typeof(Talker), nameof(Talker.RefuseEat))]
static class Talker_EatLines_SpeechChar_MultiplayerPatch {
    static void Prefix(Talker __instance, ref SpeechCharContext.Pin __state) {
        SpeechCharContext.PinPrefix(__instance != null ? __instance.body : null, ref __state);
    }

    static void Postfix(Talker __instance, SpeechCharContext.Pin __state) {
        SpeechCharContext.PinPostfix(__instance != null ? __instance.body : null, __state);
    }
}

// disable talker saying random shit and feelings for remote players
[HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new Type[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) })]
public static class Talker_Talk_MultiplayerPatch {
    public static void Force(Talker __instance, List<string> lines, Limb limb = null, bool force = false, bool resetTalkTimer = false) {
        force_run = true;
        __instance.Talk(lines, limb, force, resetTalkTimer);

        force_run = false;
    }

    public static void ForceNoSpeechImpairment(this Talker talker, string message, bool resetTalkTimer = false) {
#if FULLGAME
        // chat / yap announce skip Talk() - still need species note font (Dune vs Experiment)
        TalkerSpeciesFont.Apply(talker);
#endif

        talker.currentString = message;

        talker.text.text = "";
        talker.timeSinceTalked = (0f);

        if (resetTalkTimer) {
            talker.newTalkTime = (0f);
        }
#if FULLGAME
        TalkerPixelExempt.SetYelling(talker, message);
#endif
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
#if FULLGAME
        var og_doingEscape = EndLabScript.instance != null && EndLabScript.instance.doingEscape;
        if (og_doingEscape) {
            EndLabScript.instance.doingEscape = false;
        }
#endif

        var distortedmsg = message;
        try {
            talker.currentString = ("");

            const bool bypassSkip = true;

            List<string> talkerInput;
            if (bypassSkip) {
                talkerInput = new List<string> { "-" + message };
            } else {
                talkerInput = new List<string> { message };
            }
            Talker_Talk_MultiplayerPatch.Force(talker, talkerInput, null, true, false); // make the Talk function do speech impairment effects

            distortedmsg = talker.currentString; // find the distorted message

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
            } else {
                if (bypassSkip) {
                    distortedmsg = distortedmsg.Substring(1, distortedmsg.Length - 1);// trim the "-" (added to work around the distortion skip)
                }
            }
        } finally {
#if FULLGAME
            if (EndLabScript.instance != null) {
                EndLabScript.instance.doingEscape = og_doingEscape;
            }
#endif
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
                    if (Net.IsClient && !Util.IsBodyLocal(__instance.body)) {
                        return false;
                    }
                    // listen-server: host yap is simulated on server only (avoid double Talk + announce)
                    if (Net.IsClient && Net.IsServer && Util.IsBodyLocal(__instance.body)) {
                        return false;
                    }
                    if (Net.IsServer && __instance.body.TryGetNetBody(out var yapNb)
                        && yapNb.IsPlayer && yapNb.plr != null && !yapNb.plr.IsLocal) {
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
#if FULLGAME
        // player specimens only - scientistTalker / NPC talk must not get pixel-exempt mirrors
        if (!string.IsNullOrEmpty(__instance.currentString) && TalkerSpeciesFont.IsPlayerTalker(__instance)) {
            TalkerPixelExempt.SetYelling(__instance, __instance.currentString);
        }
#endif
        if (Net.IsRunning && !force_run) {
            var curstr = __instance.currentString;
            var oksendit = delegate (knetid iiii, byte tttttt) {
                if (Time.realtimeSinceStartupAsDouble - lastSentTime > sendCooldown) {
                    lastSentTime = Time.realtimeSinceStartupAsDouble;
                    ServerMain.TalkerSayAnnounce(iiii, tttttt, curstr, ServerMain.AllClientIdsExceptHost);
                }
            };

            if (Net.IsClient && !Net.IsServer && !String.IsNullOrEmpty(curstr)
                && Multiplayer.rules.CharacterYapPublic
                && __instance.body != null && Util.IsBodyLocal(__instance.body)) {
                if (Time.realtimeSinceStartupAsDouble - lastSentTime > sendCooldown) {
                    lastSentTime = Time.realtimeSinceStartupAsDouble;
                    if (__instance.body.TryGetNetPlayer(out var yapPlr)) {
                        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_PlayerTalkerSay);
                        writer.Put(yapPlr.playerId);
                        writer.Put(curstr);
                        Net.Client_Send(DeliveryMethod.Unreliable, writer);
                    }
                }
            }

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