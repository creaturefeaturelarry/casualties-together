using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// liquid list foreach boxed an enumerator on every fill and pour.
/// use a counted loop.
/// </summary>
[HarmonyPatch(typeof(WaterContainerItem))]
internal static class FixLiquidWalk
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "liquidWalk=" + Hits;

	// false skips the getter. stock foreach on List<LiquidStack> boxed an enumerator.
	[HarmonyPatch("CurrentTotal", MethodType.Getter)]
	[HarmonyPrefix]
	static bool Total(WaterContainerItem __instance, ref float __result)
	{
		float sum = 0f;
		var stack = __instance.stack;
		for (int i = 0; i < stack.Count; i++)
			sum += stack[i].amount;
		__result = sum;
		Hits++;
		return false;
	}

	// false skips HasLiquid. same counted scan.
	[HarmonyPatch(nameof(WaterContainerItem.HasLiquid))]
	[HarmonyPrefix]
	static bool Has(WaterContainerItem __instance, string id, ref bool __result)
	{
		var stack = __instance.stack;
		for (int i = 0; i < stack.Count; i++)
		{
			if (stack[i].liquidId == id)
			{
				__result = true;
				Hits++;
				return false;
			}
		}
		__result = false;
		Hits++;
		return false;
	}

	// false skips AmountOf. HasLiquid above is already our prefix, so this does not re-enter stock.
	[HarmonyPatch(nameof(WaterContainerItem.AmountOf))]
	[HarmonyPrefix]
	static bool Amount(WaterContainerItem __instance, string liquid, ref float __result)
	{
		if (!__instance.HasLiquid(liquid))
		{
			__result = 0f;
			return false;
		}
		var stack = __instance.stack;
		for (int i = 0; i < stack.Count; i++)
		{
			if (stack[i].liquidId == liquid)
			{
				__result = stack[i].amount;
				Hits++;
				return false;
			}
		}
		__result = 0f;
		Hits++;
		return false;
	}

	// false skips AddLiquid. returns how much actually fit, same as stock.
	[HarmonyPatch(nameof(WaterContainerItem.AddLiquid))]
	[HarmonyPrefix]
	static bool Add(WaterContainerItem __instance, string liquidId, float amount, ref float __result)
	{
		float num = UnityEngine.Mathf.Min(__instance.SpaceLeft, amount);
		if (num <= 0f)
		{
			__result = 0f;
			return false;
		}
		LiquidStack found = null;
		var stack = __instance.stack;
		for (int i = 0; i < stack.Count; i++)
		{
			if (stack[i].liquidId == liquidId)
			{
				found = stack[i];
				break;
			}
		}
		if (found != null)
			found.amount += num;
		else
			stack.Add(new LiquidStack(liquidId, num));
		__instance.UpdateCondition();
		__result = num;
		Hits++;
		return false;
	}
}
