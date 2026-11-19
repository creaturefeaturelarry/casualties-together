using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together {

    // CUCoreLib loads after this mod. warm its prefab cache at the menu, before a corpse does it mid-gen.
    static class CuCacheWarm {
        internal static void Install(MonoBehaviour host) {
            host.StartCoroutine(Warm());
        }

        static IEnumerator Warm() {
            yield return null;
            WorldGeneration world = WorldGeneration.world;
            if (world != null && world.generatingWorld) {
                KrokV5Optimization.FindTheBug.Write("cache warm skipped, world is generating");
                yield break;
            }
            Type cache = AccessTools.TypeByName("CUCoreLib.Helpers.ResourceCache");
            MethodInfo init = cache == null ? null : AccessTools.Method(cache, "TryInitialize");
            if (init == null) {
                KrokV5Optimization.FindTheBug.Write("cache warm skipped, cache missing");
                yield break;
            }
            KrokV5Optimization.FindTheBug.Write("cache warm before");
            try {
                init.Invoke(null, null);
            } catch (Exception ex) {
                KrokV5Optimization.FindTheBug.Write("cache warm threw " + ex.GetType().Name);
                yield break;
            }
            KrokV5Optimization.FindTheBug.Write("cache warm after");
        }
    }
}