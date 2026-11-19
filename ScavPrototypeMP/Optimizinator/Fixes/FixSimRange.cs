using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Together;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace KrokV5Optimization;

/// <summary>
/// stock sim was a 6x6 stamp, and the host renderer's 5x5 still counted past it.
/// spiders and grabber plants ignored the stamp and stopped when the host chunk renderer turned off, two chunks from the camera.
/// each player stamps their chunk plus four chunks each way. in mp that union is the only sim test, and the spider and grabber use it too.
/// the host also turns those renderers on, so a chunk update is not stuck waiting for the host camera.
/// </summary>
internal static class FixSimRange {
    internal static long Grids;

    // vanilla draws about two chunks out and forces the next ring off. four covers that window around a client who is not the host.
    internal const int WindowRadius = 4;

    static FieldInfo _table;
    static bool _gateMiss;

    // this file's counters on the 10s report.
    internal static string Evidence() => "simGrid=" + Grids;

    // the first table was 16x16. a deeper map indexes past that, Mark skipped the chunk, and the lookup clamped onto the edge. those rigidbodies stayed static, so the client only ever saw one gravity step.
    static void Fit(WorldGeneration world) {
        if (world == null || !world.worldExists)
            return;
        int w = (int)world.chunkWidth;
        int h = (int)world.chunkHeight;
        if (w < 1 || h < 1)
            return;
        if (_table == null)
            _table = AccessTools.Field(typeof(SharedMain), "CHUNKS_VISIBLE_TO_PLAYERS");
        if (_table == null)
            return;
        bool[,] have = _table.GetValue(null) as bool[,];
        if (have != null && have.GetLength(0) == w && have.GetLength(1) == h)
            return;
        _table.SetValue(null, new bool[w, h]);
    }

    // false skips stock _UpdatePlayerVisibleChunks. we clear the table and stamp the window per player ourselves.
    internal static bool Rebuild() {
        WorldGeneration world = WorldGeneration.world;
        Fit(world);
        bool[,] table = Table();
        if (table == null)
            return true;
        int width = table.GetLength(0);
        int height = table.GetLength(1);
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++)
                table[x, y] = false;
        }
        if (world == null || !world.worldExists)
            return false;
        int players = 0;
        foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict) {
            if (pair.Value == null)
                continue;
            Mark(table, world, pair.Value.pos);
            players++;
        }
        // menu or a tick with no bodies yet. stamp the camera so the local world still simulates.
        if (players == 0 && Camera.main != null)
            Mark(table, world, Camera.main.transform.position);
        // thrown stuff, a fired hook, and a lit stick of dynamite stamp the same 3x3 a player would.
        SimKeep.Stamp(table, world);
        Grids++;
        return false;
    }

    // in mp, ignore the host renderer. offline returns true and stock keeps its own test.
    internal static bool Range(Vector2 pos, ref bool __result) {
        if (!Net.IsRunning)
            return true;
        if (WorldGeneration.world == null) {
            __result = false;
            return false;
        }
        __result = SharedMain.PositionIsInSimulatedChunk(pos);
        return false;
    }

    // first get_enabled in the spider update was "is my chunk renderer on". replace that with WorldInSimRange.
    [HarmonyPatch(typeof(SpiderHandler), "Update")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> Spider(IEnumerable<CodeInstruction> instructions) {
        return Gate(instructions, "SpiderHandler.Update");
    }

    // same renderer check as the spider.
    [HarmonyPatch(typeof(GrabberPlant), "Update")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> Plant(IEnumerable<CodeInstruction> instructions) {
        return Gate(instructions, "GrabberPlant.Update");
    }

    // tentacle lines bail out when the chunk renderer flips off, then the stale points stretch.
    // keep the solve if the creature is in sim range. a renderer that is already on still counts.
    [HarmonyPatch(typeof(IKHandle), "Update")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> Handle(IEnumerable<CodeInstruction> instructions) {
        return GateEither(instructions, "IKHandle.Update");
    }

    // CHUNKS_VISIBLE_TO_PLAYERS is the bool grid stock already consults.
    static bool[,] Table() {
        if (_table == null)
            _table = AccessTools.Field(typeof(SharedMain), "CHUNKS_VISIBLE_TO_PLAYERS");
        return _table != null ? _table.GetValue(null) as bool[,] : null;
    }

    // chunk under pos, plus WindowRadius each way. out of range indexes are skipped.
    internal static void Mark(bool[,] table, WorldGeneration world, Vector2 pos) {
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int maxX = table.GetLength(0) - 1;
        int maxY = table.GetLength(1) - 1;
        for (int y = chunk.y - WindowRadius; y <= chunk.y + WindowRadius; y++) {
            if ((uint)y > (uint)maxY)
                continue;
            for (int x = chunk.x - WindowRadius; x <= chunk.x + WindowRadius; x++) {
                if ((uint)x > (uint)maxX)
                    continue;
                table[x, y] = true;
            }
        }
    }

    // pop the renderer, push WorldInSimRange(transform.position). only the first get_enabled is the chunk check.
    static IEnumerable<CodeInstruction> Gate(IEnumerable<CodeInstruction> instructions, string name) {
        var list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++) {
            if (list[i].opcode != OpCodes.Callvirt || list[i].operand is not MethodInfo method)
                continue;
            if (method.Name != "get_enabled")
                continue;
            var swap = new CodeInstruction(OpCodes.Pop);
            if (list[i].labels != null)
                swap.labels.AddRange(list[i].labels);
            if (list[i].blocks != null)
                swap.blocks.AddRange(list[i].blocks);
            list[i] = swap;
            list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Component), "transform")));
            list.Insert(i + 3, new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Transform), "position")));
            list.Insert(i + 4, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Vector2), "op_Implicit", new[] { typeof(Vector3) })));
            list.Insert(i + 5, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedMain), nameof(SharedMain.WorldInSimRange))));
            return list;
        }
        if (!_gateMiss) {
            _gateMiss = true;
            OptimizerMain.Log.LogWarning("[KrokV5Opt] sim range gate missed " + name);
        }
        return list;
    }

    // leave the chunk renderer bool, then OR WorldInSimRange. only the first Renderer.get_enabled.
    static IEnumerable<CodeInstruction> GateEither(IEnumerable<CodeInstruction> instructions, string name) {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++) {
            if (list[i].opcode != OpCodes.Callvirt || list[i].operand is not MethodInfo method)
                continue;
            if (method.Name != "get_enabled" || method.DeclaringType != typeof(Renderer))
                continue;
            list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Component), "transform")));
            list.Insert(i + 3, new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Transform), "position")));
            list.Insert(i + 4, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Vector2), "op_Implicit", new[] { typeof(Vector3) })));
            list.Insert(i + 5, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedMain), nameof(SharedMain.WorldInSimRange))));
            list.Insert(i + 6, new CodeInstruction(OpCodes.Or));
            return list;
        }
        if (!_gateMiss) {
            _gateMiss = true;
            OptimizerMain.Log.LogWarning("[KrokV5Opt] sim range gate missed " + name);
        }
        return list;
    }
}

// vanilla UpdateChunkVisibility only walks mainCam, so a client's chunks stay dark on the host and ChunkScript.Update never runs.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateChunkVisibility))]
static class UpdateChunkVisibility_ClientWindows_MultiplayerPatch {
    static HashSet<long> Now = new HashSet<long>();
    static HashSet<long> Prev = new HashSet<long>();

    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.generatingWorld)
            return;
        TilemapRenderer[,] chunks = __instance.renderChunks;
        if (chunks == null)
            return;

        Now.Clear();
        if (__instance.mainCam != null)
            Cover(__instance, __instance.mainCam.transform.position);
        foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict) {
            if (pair.Value == null)
                continue;
            Cover(__instance, pair.Value.pos);
        }

        int maxX = chunks.GetLength(0);
        int maxY = chunks.GetLength(1);
        foreach (long key in Now)
            Set(chunks, key, maxX, maxY, true);
        foreach (long key in Prev) {
            if (!Now.Contains(key))
                Set(chunks, key, maxX, maxY, false);
        }

        HashSet<long> swap = Prev;
        Prev = Now;
        Now = swap;
    }

    static void Cover(WorldGeneration world, Vector2 pos) {
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int maxX = world.renderChunks.GetLength(0) - 1;
        int maxY = world.renderChunks.GetLength(1) - 1;
        int radius = FixSimRange.WindowRadius;
        for (int y = chunk.y - radius; y <= chunk.y + radius; y++) {
            if ((uint)y > (uint)maxY)
                continue;
            for (int x = chunk.x - radius; x <= chunk.x + radius; x++) {
                if ((uint)x > (uint)maxX)
                    continue;
                Now.Add(((long)x << 32) | (uint)y);
            }
        }
    }

    static void Set(TilemapRenderer[,] chunks, long key, int maxX, int maxY, bool enabled) {
        int x = (int)(key >> 32);
        int y = (int)key;
        if ((uint)x >= (uint)maxX || (uint)y >= (uint)maxY)
            return;
        TilemapRenderer renderer = chunks[x, y];
        if (renderer != null && renderer.enabled != enabled)
            renderer.enabled = enabled;
    }
}