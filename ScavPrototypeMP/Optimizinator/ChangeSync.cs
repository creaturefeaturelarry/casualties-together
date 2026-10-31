using System;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// v4 called Server_QueueSync when an item was dropped, picked up, worn, or moved
/// in a container. v5 has no force-sync queue. a registered object already has its
/// fields copied on the tracker physics tick, and LiteEntitySystem sends what changed.
/// these hooks cover the other case: the object is not registered yet, so the copy
/// never starts until a spatial pass finds it.
/// </summary>
internal static class ChangeSync
{
	private static int _hooks;

	// the six parent changes. a missing method is skipped so the others still install.
	internal static void Install(Harmony harmony)
	{
		if (harmony == null)
			return;

		_hooks = 0;
		Hook(harmony, AccessTools.Method(typeof(Body), "DropItem", new[] { typeof(Item) }), nameof(AfterItem));
		Hook(harmony, AccessTools.Method(typeof(Body), "PickUpItem", new[] { typeof(Item), typeof(int), typeof(bool) }), nameof(AfterItem));
		Hook(harmony, AccessTools.Method(typeof(Body), "WearWearable", new[] { typeof(Item) }), nameof(AfterItem));
		Hook(harmony, AccessTools.Method(typeof(Body), "DropWearable", new[] { typeof(Item) }), nameof(AfterItem));
		Hook(harmony, AccessTools.Method(typeof(Container), "LoadItem", new[] { typeof(Item) }), nameof(AfterContainer));
		Hook(harmony, AccessTools.Method(typeof(Container), "UnloadItem", new[] { typeof(Item), typeof(Body) }), nameof(AfterContainer));

		OptimizerMain.Log.LogDebug("[KrokV5Optimization] change register hooks=" + _hooks);
	}

	// one postfix. a null method is a signature the game no longer has.
	private static void Hook(Harmony harmony, MethodInfo method, string postfix)
	{
		if (method == null)
		{
			OptimizerMain.Log.LogWarning("[KrokV5Optimization] change register skipped a missing method (" + postfix + ")");
			return;
		}

		harmony.Patch(method, postfix: new HarmonyMethod(typeof(ChangeSync), postfix));
		_hooks++;
	}

	// the item that was picked up, dropped, or worn.
	private static void AfterItem(Item item)
	{
		Note(item != null ? item.gameObject : null);
	}

	// the item, and the bag it moved into or out of.
	private static void AfterContainer(Container __instance, Item item)
	{
		Note(item != null ? item.gameObject : null);
		Note(__instance != null ? __instance.gameObject : null);
	}

	// host only. NewGO starts sync. an object that is already registered is left to LiteEntitySystem.
	internal static void Note(GameObject go)
	{
		if (go == null || !Net.IsServer)
			return;

		try
		{
			if (NetObjectRegistry.IsRegistered(go) || !NetObjectRegistry.ObjectShouldBeSynced(go))
				return;
			NetObjectRegistry.NewGO(go);
		}
		catch (Exception ex)
		{
			OptimizerMain.Log.LogWarning("[KrokV5Optimization] change register: " + ex.Message);
		}
	}
}
