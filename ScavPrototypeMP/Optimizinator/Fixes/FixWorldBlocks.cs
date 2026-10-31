using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// a new layer allocated a fresh worldBlocks ushort[,] and fluid byte[,] even when the size matched.
/// same width and height clear the old maps. a real size change still allocates.
/// </summary>
internal static class FixWorldBlocks
{
	internal static long BlocksReused;
	internal static long FluidReused;

	static readonly AccessTools.FieldRef<WorldGeneration, ushort[,]> BlocksRef =
		AccessTools.FieldRefAccess<WorldGeneration, ushort[,]>("worldBlocks");

	static readonly AccessTools.FieldRef<FluidManager, byte[,]> FluidRef =
		AccessTools.FieldRefAccess<FluidManager, byte[,]>("fluid");

	static WorldGeneration _gen;

	// this file's counters on the 10s report.
	internal static string Evidence() => "worldBlocks=" + BlocksReused + " fluidMap=" + FluidReused;

	// InstantiateWorld is an iterator. the new[,] calls live in MoveNext, not on WorldGeneration itself.
	internal static void Arm(Harmony harmony)
	{
		Type iter = null;
		foreach (Type nested in typeof(WorldGeneration).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (nested.Name.IndexOf("InstantiateWorld", StringComparison.Ordinal) >= 0)
			{
				iter = nested;
				break;
			}
		}
		if (iter == null)
			throw new InvalidOperationException("InstantiateWorld iterator missing");
		MethodInfo move = AccessTools.Method(iter, "MoveNext");
		if (move == null)
			throw new InvalidOperationException("InstantiateWorld MoveNext missing");
		harmony.Patch(move,
			prefix: new HarmonyMethod(typeof(FixWorldBlocks), nameof(Prefix)),
			transpiler: new HarmonyMethod(typeof(FixWorldBlocks), nameof(Transpile)));
	}

	// the iterator's <>4__this is the WorldGeneration that owns worldBlocks.
	static void Prefix(object __instance)
	{
		FieldInfo self = __instance.GetType().GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (self != null)
			_gen = self.GetValue(__instance) as WorldGeneration;
	}

	// ushort[,] is the block map. byte[,] is the fluid map. anything else in that iterator stays.
	static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo blocks = AccessTools.Method(typeof(FixWorldBlocks), nameof(RentBlocks));
		MethodInfo fluid = AccessTools.Method(typeof(FixWorldBlocks), nameof(RentFluid));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo ctor)
			{
				Type type = ctor.DeclaringType;
				if (type != null && type.IsArray && type.GetArrayRank() == 2)
				{
					Type element = type.GetElementType();
					if (element == typeof(ushort))
					{
						yield return new CodeInstruction(OpCodes.Call, blocks);
						continue;
					}
					if (element == typeof(byte))
					{
						yield return new CodeInstruction(OpCodes.Call, fluid);
						continue;
					}
				}
			}
			yield return instruction;
		}
	}

	// clear and return the existing map when the new layer is the same size.
	static ushort[,] RentBlocks(int width, int height)
	{
		WorldGeneration gen = _gen;
		if (gen != null)
		{
			ushort[,] existing = BlocksRef(gen);
			if (existing != null && existing.GetLength(0) == width && existing.GetLength(1) == height)
			{
				Array.Clear(existing, 0, existing.Length);
				BlocksReused++;
				return existing;
			}
		}
		return new ushort[width, height];
	}

	// same as RentBlocks, for FluidManager.fluid.
	static byte[,] RentFluid(int width, int height)
	{
		FluidManager manager = FluidManager.main;
		if (manager != null)
		{
			byte[,] existing = FluidRef(manager);
			if (existing != null && existing.GetLength(0) == width && existing.GetLength(1) == height)
			{
				Array.Clear(existing, 0, existing.Length);
				FluidReused++;
				return existing;
			}
		}
		return new byte[width, height];
	}
}
