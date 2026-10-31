using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// every server frame replaced the chunk-hash grid. 0 already means empty, so clear the existing grid.
/// </summary>
internal static class FixChunkHashes
{
	internal static long Reused;

	// this file's counters on the 10s report.
	internal static string Evidence() => "chunkHash=" + Reused;

	// replace `new uint[w, h]` inside LateUpdate with Rent. other newobj calls stay.
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var rent = AccessTools.Method(typeof(FixChunkHashes), nameof(Rent));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.opcode == OpCodes.Newobj && IsGridCtor(instruction.operand))
			{
				yield return new CodeInstruction(OpCodes.Call, rent);
				continue;
			}
			yield return instruction;
		}
	}

	// true only for the 2d uint chunk-hash grid, not every array LateUpdate allocates.
	static bool IsGridCtor(object operand)
	{
		if (!(operand is System.Reflection.ConstructorInfo ctor))
			return false;
		System.Type type = ctor.DeclaringType;
		return type != null
		       && type.IsArray
		       && type.GetArrayRank() == 2
		       && type.GetElementType() == typeof(uint);
	}

	internal static uint[,] Rent(int width, int height)
	{
		uint[,] grid = WorldChunkSync.Server_ChunkHashes;
		if (grid == null || grid.GetLength(0) != width || grid.GetLength(1) != height)
			return new uint[width, height];
		System.Array.Clear(grid, 0, grid.Length);
		Reused++;
		return grid;
	}
}
