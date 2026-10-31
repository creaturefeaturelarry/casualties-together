using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// each UI pass did new RectOffset even when left/right/top/bottom had not changed.
/// share one box for the same four numbers.
/// </summary>
internal static class FixRectOffset
{
	internal static long Hit;
	internal static long Miss;

	// dictionary key. two boxes match when all four sides match.
	private struct OffsetKey : IEquatable<OffsetKey>
	{
		internal int L, R, T, B;
		public bool Equals(OffsetKey other) => L == other.L && R == other.R && T == other.T && B == other.B;
		public override bool Equals(object obj) => obj is OffsetKey other && Equals(other);
		public override int GetHashCode()
		{
			unchecked
			{
				int hash = L;
				hash = (hash * 397) ^ R;
				hash = (hash * 397) ^ T;
				return (hash * 397) ^ B;
			}
		}
	}

	private static readonly Dictionary<OffsetKey, RectOffset> Offsets = new Dictionary<OffsetKey, RectOffset>(8);

	// this file's counters on the 10s report.
	internal static string Evidence() => "rectHit=" + Hit + " rectMiss=" + Miss;

	// swap `new RectOffset(l, r, t, b)` for Intern.
	internal static void Arm(Harmony harmony)
	{
		ConstructorInfo offset = AccessTools.Constructor(typeof(RectOffset), new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
		ImguiScan.Arm(harmony, new[] { offset }, AccessTools.Method(typeof(FixRectOffset), nameof(Transpile)));
	}

	// same four numbers share one RectOffset. IMGUI reads it during the pass and does not keep it.
	internal static RectOffset Intern(int left, int right, int top, int bottom)
	{
		var key = new OffsetKey { L = left, R = right, T = top, B = bottom };
		if (Offsets.TryGetValue(key, out RectOffset found))
		{
			Hit++;
			return found;
		}
		var created = new RectOffset(left, right, top, bottom);
		Offsets[key] = created;
		Miss++;
		return created;
	}

	// the four ints are already on the stack in the right order, so the call replaces newobj directly.
	private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
	{
		ConstructorInfo offset = AccessTools.Constructor(typeof(RectOffset), new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
		MethodInfo intern = AccessTools.Method(typeof(FixRectOffset), nameof(Intern));
		foreach (CodeInstruction ins in instructions)
		{
			if (ins.opcode != OpCodes.Newobj || !(ins.operand is ConstructorInfo ctor) || ctor != offset)
			{
				yield return ins;
				continue;
			}
			var call = new CodeInstruction(OpCodes.Call, intern);
			call.labels.AddRange(ins.labels);
			call.blocks.AddRange(ins.blocks);
			yield return call;
		}
	}
}
