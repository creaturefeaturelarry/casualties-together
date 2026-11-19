using HarmonyLib;
using System.Collections;
using UnityEngine;

namespace Together;

// client MP worldgen skips mid-gen UpdateWorld. After Finish, client must flush
// its own worldBlocks into chunk tilemaps - TilemapSync only repairs hash mismatches, and
// deterministic gen makes hashes match, so skipped UpdateWorld leaves permanent holes
// (EndLab bake near scientist is the sharp case).
static class FrameBuckets {
    // set while ClientFlushWorldTilemaps runs so SkipUpdateWorldOnClient allows the flush.
    internal static bool FlushingClientTilemaps;

    static bool Generating() {
        try {
            if (SharedMain.LocalWorldIsGenerating)
                return true;
            return WorldGeneration.world != null && WorldGeneration.world.generatingWorld;
        } catch {
            return false;
        }
    }

    // spread worldBlocks -> chunks[] after client gen. Full UpdateWorld was 7s+ mid-hollow;
    // one post-gen pass (yielded) is enough for collision/visuals.
    internal static IEnumerator ClientFlushWorldTilemaps(WorldGeneration world) {
        if (world == null || world.chunks == null)
            yield break;

        FlushingClientTilemaps = true;
        try {
            int w = (int)world.chunkWidth;
            int h = (int)world.chunkHeight;
            double slice = Time.realtimeSinceStartupAsDouble;
            const float maxSlice = 0.04f;
            for (int cx = 0; cx < w; cx++) {
                for (int cy = 0; cy < h; cy++) {
                    world.UpdateChunk(new Vector2Int(cx, cy));
                    if (Time.realtimeSinceStartupAsDouble - slice > maxSlice) {
                        if (world.loadingText != null)
                            world.loadingText.text = $"Loading tiles… {cx * h + cy + 1}/{w * h}";
                        yield return null;
                        slice = Time.realtimeSinceStartupAsDouble;
                    }
                }
            }
        } finally {
            FlushingClientTilemaps = false;
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateWorld))]
    static class SkipUpdateWorldOnClient {
        // 201748: client UpdateWorld was 7s+ during hollow/crystalline gen.
        [HarmonyPriority(Priority.First)]
        static bool Prefix() {
            if (FlushingClientTilemaps)
                return true;
            return !(Net.IsRunning && Net.IsClient && Generating());
        }
    }

}

#if FULLGAME
// terrainGenActive for LES pause must track WorldGenerateTerrain MoveNext.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.WorldGenerateTerrain))]
static class FrameBuckets_TerrainGenActive_MultiplayerPatch {
    [HarmonyPriority(Priority.Last)]
    static void Postfix(WorldGeneration __instance, ref IEnumerator __result) {
        if (__result == null)
            return;
        IEnumerator inner = __result;
        __result = Gate(inner, __instance != null ? __instance.biomeDepth : -1);
    }

    static IEnumerator Gate(IEnumerator inner, int depth) {
        CrystalAuthority.ClientGenThrottle.NoteTerrainBegin(depth);
        try {
            while (inner != null && inner.MoveNext())
                yield return inner.Current;
        } finally {
            CrystalAuthority.ClientGenThrottle.NoteTerrainEnd(depth);
        }
    }
}
#endif