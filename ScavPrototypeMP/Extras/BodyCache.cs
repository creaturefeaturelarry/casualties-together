using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using KrokV5Optimization;

namespace Together;

/// <summary>
/// Body.Update asks for Painkillers every frame, and the half-second pass asks for MindwipeScript.
/// the result stays until the component is gone. a miss is tried again after a short wait, so a later add still lands.
/// visuals, sounds, and the ground cast stay every frame.
/// </summary>
[HarmonyPatch]
internal static class BodyCache
{
	const int RetryFrames = 45;

	sealed class Slot
	{
		internal Painkillers Pain;
		internal MindwipeScript Wipe;
		internal int PainFrame = -999;
		internal int WipeFrame = -999;
	}

	static readonly Dictionary<int, Slot> Slots = new Dictionary<int, Slot>(16);

	internal static Painkillers Painkillers(Body body)
	{
		Slot slot = SlotFor(body);
		if (slot.Pain != null)
			return slot.Pain;
		if (Time.frameCount - slot.PainFrame < RetryFrames)
			return null;
		slot.PainFrame = Time.frameCount;
		slot.Pain = body.GetComponent<Painkillers>();
		return slot.Pain;
	}

	internal static MindwipeScript Mindwipe(Body body)
	{
		Slot slot = SlotFor(body);
		if (slot.Wipe != null)
			return slot.Wipe;
		if (Time.frameCount - slot.WipeFrame < RetryFrames)
			return null;
		slot.WipeFrame = Time.frameCount;
		slot.Wipe = body.GetComponent<MindwipeScript>();
		return slot.Wipe;
	}

	[HarmonyPatch(typeof(Body), nameof(Body.Update))]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Update(IEnumerable<CodeInstruction> instructions)
	{
		return Swap(instructions, typeof(Painkillers), nameof(Painkillers));
	}

	[HarmonyPatch(typeof(Body), nameof(Body.HandlePeriodicChecks))]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Periodic(IEnumerable<CodeInstruction> instructions)
	{
		return Swap(instructions, typeof(MindwipeScript), nameof(Mindwipe));
	}

	static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, System.Type component, string method)
	{
		MethodInfo call = AccessTools.Method(typeof(BodyCache), method);
		bool found = false;
		foreach (CodeInstruction code in instructions)
		{
			if (!found && code.opcode == OpCodes.Callvirt && code.operand is MethodInfo site
				&& site.IsGenericMethod && site.GetGenericArguments()[0] == component)
			{
				code.opcode = OpCodes.Call;
				code.operand = call;
				found = true;
			}
			yield return code;
		}
		if (!found && OptimizerMain.Log != null)
			OptimizerMain.Log.LogWarning("[KrokV5Opt] body cache missed " + component.Name);
	}

	static Slot SlotFor(Body body)
	{
		int id = body.GetInstanceID();
		if (!Slots.TryGetValue(id, out Slot slot))
		{
			slot = new Slot();
			Slots[id] = slot;
		}
		return slot;
	}
}
