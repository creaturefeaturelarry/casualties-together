using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// inventory redraw calls GetHoldingWeight per bag slot, and that walk boxed an enumerator. a drag then built a list just to compare tags.
/// count the children for weight. compare tags without the list.
/// </summary>
[HarmonyPatch(typeof(Container))]
internal static class FixInvDrag
{
	internal static long Walks;
	internal static long Tints;

	// this file's counters on the 10s report.
	internal static string Evidence() => "holdWalk=" + Walks + " bagTint=" + Tints;

	// false skips stock GetHoldingWeight. the stock walk boxed a foreach enumerator.
	[HarmonyPatch(nameof(Container.GetHoldingWeight))]
	[HarmonyPrefix]
	static bool Weight(Container __instance, ref float __result)
	{
		__result = Sum(__instance);
		Walks++;
		return false;
	}

	// false skips stock CanHoldItem, which allocated a list to test tags.
	[HarmonyPatch(nameof(Container.CanHoldItem))]
	[HarmonyPrefix]
	static bool Hold(Container __instance, Item item, ref bool __result)
	{
		Tints++;
		if (item.totalWeight > __instance.maxWeightPerItem
			|| Sum(__instance) + item.totalWeight > __instance.maxWeight)
		{
			__result = false;
			return false;
		}
		// empty restriction means any item. otherwise one matching tag is enough.
		string[] restrict = __instance.tagRestriction;
		if (restrict == null || restrict.Length == 0)
		{
			__result = true;
			return false;
		}
		string[] tags = item.Stats.GetTags();
		for (int i = 0; i < restrict.Length; i++)
		{
			string need = restrict[i];
			for (int j = 0; j < tags.Length; j++)
			{
				if (need == tags[j])
				{
					__result = true;
					return false;
				}
			}
		}
		__result = false;
		return false;
	}

	// weight of item children only. stock summed the same way, via foreach.
	static float Sum(Container box)
	{
		float sum = 0f;
		Transform root = box.transform;
		int count = root.childCount;
		for (int i = 0; i < count; i++)
		{
			if (root.GetChild(i).TryGetComponent(out Item held))
				sum += held.totalWeight;
		}
		return sum;
	}
}
