using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together.UI.TeammateList;

[HarmonyPatch(typeof(Talker), "Talk", new Type[] {
    typeof(List<string>),
    typeof(Limb),
    typeof(bool),
    typeof(bool)
})]
internal static class TalkYapPatch {
    private static void Postfix(Talker __instance) {
        if ((Object)(object)__instance == (Object)null || TeammateListHost.Roster == null) {
            return;
        }
        TeammateListHost.Roster.NoteYap(__instance.body, Roster.CurrentLine(__instance));
    }
}