using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// ScrapEaterScript.Update rewrote build.description every frame.
/// keep the sentence until the rounded percent changes.
/// </summary>
[HarmonyPatch(typeof(ScrapEaterScript), "Update")]
internal static class FixScrapDesc
{
	internal static long Skipped;

	static readonly Dictionary<int, int> Percent = new Dictionary<int, int>();

	// this file's counters on the 10s report.
	internal static string Evidence() => "scrapSkip=" + Skipped;

	// false skips ScrapEaterScript.Update for this frame, so build.description is left as it is.
	static bool Prefix(ScrapEaterScript __instance)
	{
		if (__instance.build == null || ScrapEaterScript.target == 0f)
			return true;
		int pct = Mathf.RoundToInt(__instance.scrapAmount / ScrapEaterScript.target * 100f);
		int id = __instance.GetInstanceID();
		if (Percent.TryGetValue(id, out int prev)
			&& prev == pct
			&& !string.IsNullOrEmpty(__instance.build.description))
		{
			Skipped++;
			return false;
		}
		Percent[id] = pct;
		if (Percent.Count > 2000)
			Percent.Clear();
		return true;
	}
}
