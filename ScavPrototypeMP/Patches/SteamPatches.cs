using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Together;

#if FULLGAME

[HarmonyPatch(typeof(SteamManager), nameof(SteamManager.Awake))]
static class SteamManager_Awake_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
       
        var from = AccessTools.Method(typeof(SteamAPI), nameof(SteamAPI.Init));
        var to = AccessTools.Method(typeof(KSteam), nameof(KSteam.CheckSteam));

        foreach (CodeInstruction instruction in instructions) {
            if (instruction.operand as MethodBase == from && instruction.opcode == OpCodes.Call) {
                instruction.operand = to;
            }

            yield return instruction;
        }
    }

    static bool Prefix(SteamManager __instance) {
        if (Plugin.FORCE_NO_STEAM) {
            return false;
        }
        return true;
    }
}

//TODO Use transpiler to patch achievements in Body!


#endif

