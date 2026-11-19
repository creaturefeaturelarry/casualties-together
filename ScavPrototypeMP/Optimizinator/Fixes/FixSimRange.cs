using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// stock sim was a 6x6 stamp, and the host renderer's 5x5 still counted past it.
/// spiders and grabber plants ignored the stamp and stopped when the host chunk renderer turned off, two chunks from the camera.
/// each player now stamps their chunk plus the eight around it. in mp that union is the only sim test, and the spider and grabber use it too.
/// </summary>
internal static class FixSimRange
{
	internal static long Grids;

	static FieldInfo _table;
	static bool _gateMiss;

	// this file's counters on the 10s report.
	internal static string Evidence() => "simGrid=" + Grids;

	// false skips stock _UpdatePlayerVisibleChunks. we clear the table and stamp a 3x3 per player ourselves.
	internal static bool Rebuild()
	{
		bool[,] table = Table();
		if (table == null)
			return true;
		int width = table.GetLength(0);
		int height = table.GetLength(1);
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
				table[x, y] = false;
		}
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists)
			return false;
		int players = 0;
		foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict)
		{
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
	internal static bool Range(Vector2 pos, ref bool __result)
	{
		if (!Net.IsRunning)
			return true;
		if (WorldGeneration.world == null)
		{
			__result = false;
			return false;
		}
		__result = SharedMain.PositionIsInSimulatedChunk(pos);
		return false;
	}

	// first get_enabled in the spider update was "is my chunk renderer on". replace that with WorldInSimRange.
	[HarmonyPatch(typeof(SpiderHandler), "Update")]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Spider(IEnumerable<CodeInstruction> instructions)
	{
		return Gate(instructions, "SpiderHandler.Update");
	}

	// same renderer check as the spider.
	[HarmonyPatch(typeof(GrabberPlant), "Update")]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Plant(IEnumerable<CodeInstruction> instructions)
	{
		return Gate(instructions, "GrabberPlant.Update");
	}

	// CHUNKS_VISIBLE_TO_PLAYERS is the bool grid stock already consults.
	static bool[,] Table()
	{
		if (_table == null)
			_table = AccessTools.Field(typeof(SharedMain), "CHUNKS_VISIBLE_TO_PLAYERS");
		return _table != null ? _table.GetValue(null) as bool[,] : null;
	}

	// chunk under pos, plus one chunk each way. out of range indexes are skipped.
	internal static void Mark(bool[,] table, WorldGeneration world, Vector2 pos)
	{
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		int maxX = table.GetLength(0) - 1;
		int maxY = table.GetLength(1) - 1;
		for (int y = chunk.y - 1; y <= chunk.y + 1; y++)
		{
			if ((uint)y > (uint)maxY)
				continue;
			for (int x = chunk.x - 1; x <= chunk.x + 1; x++)
			{
				if ((uint)x > (uint)maxX)
					continue;
				table[x, y] = true;
			}
		}
	}

	// pop the renderer, push WorldInSimRange(transform.position). only the first get_enabled is the chunk check.
	static IEnumerable<CodeInstruction> Gate(IEnumerable<CodeInstruction> instructions, string name)
	{
		var list = new List<CodeInstruction>(instructions);
		for (int i = 0; i < list.Count; i++)
		{
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
		if (!_gateMiss)
		{
			_gateMiss = true;
			OptimizerMain.Log.LogWarning("[KrokV5Opt] sim range gate missed " + name);
		}
		return list;
	}
}
