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

    // one chunk each way: the player's chunk plus its neighbours. searches stay inside that 3x3.
    internal const int WindowRadius = 1;

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
        Together.BuildingEntity_Update_MultiplayerPatch.NoteWindowMoved();
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

// the line is local. spider legs and vines outside this machine's 3x3 keep their last pose.
[HarmonyPatch(typeof(IKHandle), "Update")]
static class IKHandle_LocalSolve_MultiplayerPatch {
    static bool Prefix(IKHandle __instance) {
        if (!Net.IsRunning || __instance == null)
            return true;
        return Inside(__instance.transform.position);
    }

    internal static bool Inside(Vector2 pos) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists)
            return true;
            if (!CasualtiesTogetherUtils.Util.TryGetLocalBody(out Body body) || body == null)
            return false;
        Vector2Int mine = world.BlockToChunkPos(world.WorldToBlockPos(body.transform.position));
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int dx = chunk.x - mine.x;
        int dy = chunk.y - mine.y;
        if (dx < 0)
            dx = -dx;
        if (dy < 0)
            dy = -dy;
        return dx <= 1 && dy <= 1;
    }
}

// vanilla UpdateChunkVisibility only walks mainCam, so a client's chunks stay dark on the host and ChunkScript.Update never runs.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateChunkVisibility))]
static class UpdateChunkVisibility_ClientWindows_MultiplayerPatch {
    const float OffAfter = 1f;
    static HashSet<long> Now = new HashSet<long>();
    static HashSet<long> Prev = new HashSet<long>();
    static readonly HashSet<long> Held = new HashSet<long>();
    static readonly Dictionary<long, float> Cold = new Dictionary<long, float>();
    static readonly List<long> Drop = new List<long>();
    static readonly List<Collider2D> Cols = new List<Collider2D>(4);

    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || __instance == null)
            return;
        if (__instance.generatingWorld) {
            Held.Clear();
            Cold.Clear();
            return;
        }
        TilemapRenderer[,] chunks = __instance.renderChunks;
        if (chunks == null)
            return;
        if (!Net.IsServer) {
            ClientChunks(__instance, chunks);
            return;
        }

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

    // a client turns on each chunk the view circle touches, colliders included.
    // a chunk that the circle leaves stays up for a second so a step across the border does not thrash it.
    static void ClientChunks(WorldGeneration world, TilemapRenderer[,] chunks) {
        if (!SyncStep.LocalCircle(out Vector2 pos, out float radius))
            return;
        int size = WorldGeneration.CHUNKSIZE;
        if (size < 1)
            size = 64;
        Vector2Int mine = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int maxX = chunks.GetLength(0);
        int maxY = chunks.GetLength(1);
        int reach = Mathf.CeilToInt((radius + size) / size);
        if (reach < 3)
            reach = 3;
        float r2 = radius * radius;
        Now.Clear();
        for (int y = mine.y - reach; y <= mine.y + reach; y++) {
            if ((uint)y >= (uint)maxY)
                continue;
            for (int x = mine.x - reach; x <= mine.x + reach; x++) {
                if ((uint)x >= (uint)maxX)
                    continue;
                long key = ((long)x << 32) | (uint)y;
                TilemapRenderer renderer = chunks[x, y];
                if (Touches(world, x, y, size, pos, r2)) {
                    Now.Add(key);
                    Cold.Remove(key);
                    if (Held.Add(key))
                        Apply(renderer, true);
                    else if (renderer != null && !renderer.enabled)
                        renderer.enabled = true;
                    continue;
                }
                if (Held.Contains(key))
                    continue;
                if (renderer != null && renderer.enabled)
                    Apply(renderer, false);
            }
        }
        float now = Time.unscaledTime;
        Drop.Clear();
        foreach (long key in Held) {
            if (Now.Contains(key))
                continue;
            if (!Cold.TryGetValue(key, out float since)) {
                Cold[key] = now;
                continue;
            }
            if (now - since < OffAfter)
                continue;
            int x = (int)(key >> 32);
            int y = (int)key;
            if ((uint)x < (uint)maxX && (uint)y < (uint)maxY)
                Apply(chunks[x, y], false);
            Drop.Add(key);
        }
        for (int i = 0; i < Drop.Count; i++) {
            Held.Remove(Drop[i]);
            Cold.Remove(Drop[i]);
        }
    }

    static bool Touches(WorldGeneration world, int x, int y, int size, Vector2 pos, float r2) {
        Vector2 a = world.BlockToWorldPos(new Vector2Int(x * size, y * size));
        Vector2 b = world.BlockToWorldPos(new Vector2Int(x * size + size, y * size + size));
        float x0 = a.x < b.x ? a.x : b.x;
        float x1 = a.x > b.x ? a.x : b.x;
        float y0 = a.y < b.y ? a.y : b.y;
        float y1 = a.y > b.y ? a.y : b.y;
        float cx = pos.x < x0 ? x0 : (pos.x > x1 ? x1 : pos.x);
        float cy = pos.y < y0 ? y0 : (pos.y > y1 ? y1 : pos.y);
        float dx = pos.x - cx;
        float dy = pos.y - cy;
        return dx * dx + dy * dy <= r2;
    }

    static void Apply(TilemapRenderer renderer, bool on) {
        if (renderer == null)
            return;
        if (renderer.enabled != on)
            renderer.enabled = on;
        Cols.Clear();
        renderer.GetComponents(Cols);
        bool composite = false;
        for (int i = 0; i < Cols.Count; i++) {
            if (Cols[i] is CompositeCollider2D)
                composite = true;
        }
        for (int i = 0; i < Cols.Count; i++) {
            Collider2D col = Cols[i];
            if (col == null)
                continue;
            if (composite && col is not CompositeCollider2D)
                continue;
            if (col.enabled != on)
                col.enabled = on;
        }
    }
}