using System;
using HarmonyLib;
using UnityEngine;

namespace Together;

// The skiplayer layer stays in generatingWorld when a spawn throws. Record the id and the exception.
[HarmonyPatch(typeof(Utils), nameof(Utils.Create), typeof(string), typeof(Vector2), typeof(float))]
internal static class GenWatchCreate
{
	private static void Postfix(string id, GameObject __result)
	{
		if (__result != null || Plugin.Logger == null)
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.generatingWorld)
			return;
		UnityEngine.Object loaded = Resources.Load(id);
		string kind = loaded == null ? "null" : loaded.GetType().Name;
		string active = "-";
		GameObject go = loaded as GameObject;
		if (go != null)
			active = go.activeSelf ? "1" : "0";
		Plugin.Logger.LogError("gen create null id=" + id
			+ " loaded=" + kind
			+ " active=" + active
			+ " depth=" + world.biomeDepth);
	}
}

[HarmonyPatch(typeof(WorldGeneration), "GenerateCollapsedPods")]
internal static class GenWatchPods
{
	private static void Finalizer(Exception __exception, float amt)
	{
		if (__exception == null || Plugin.Logger == null)
			return;
		WorldGeneration world = WorldGeneration.world;
		int depth = world != null ? world.biomeDepth : -1;
		Plugin.Logger.LogError("gen pods threw " + __exception.GetType().Name
			+ " depth=" + depth
			+ " amt=" + amt.ToString("0.000")
			+ " " + __exception.Message);
	}
}
