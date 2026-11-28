using HarmonyLib;
using LiteEntitySystem;
using LiteEntitySystem.Internal;
using System.Collections.Generic;
using System.Reflection;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

// SyncInfo is updated on the client for every copy the client has ever been sent.
// a far one that already has a mesh does not need that call. a diff puts it back.
static class SyncInfoPark
{
	static readonly HashSet<SyncInfo> Pending = new HashSet<SyncInfo>();
	static readonly HashSet<SyncInfo> Parked = new HashSet<SyncInfo>();
	static object _alive;
	static MethodInfo _add;
	static MethodInfo _remove;

	internal static void Install(Harmony harmony)
	{
		MethodInfo update = AccessTools.DeclaredMethod(typeof(SyncInfo), "Update");
		MethodInfo tick = AccessTools.DeclaredMethod(typeof(ClientEntityManager), "Update");
		MethodInfo delta = AccessTools.DeclaredMethod(typeof(ClientEntityManager), "ApplyEntityDelta");
		if (update == null || tick == null || delta == null)
		{
			OptimizerMain.Log?.LogWarning("[KrokV5Opt] syncinfo park skipped, method missing");
			return;
		}
		harmony.Patch(update, postfix: new HarmonyMethod(typeof(SyncInfoPark), nameof(AfterUpdate)));
		harmony.Patch(tick, postfix: new HarmonyMethod(typeof(SyncInfoPark), nameof(AfterTick)));
		harmony.Patch(delta, postfix: new HarmonyMethod(typeof(SyncInfoPark), nameof(AfterDelta)));
	}

	static void AfterUpdate(SyncInfo __instance)
	{
		if (__instance == null || Net.IsServer || !Net.IsClient)
			return;
		if (__instance.go == null || __instance.tracker == null || !ReferenceEquals(__instance.tracker.syncInfo, __instance))
			return;
		if (SyncStep.NearAny(__instance.go.transform.position, 0f))
			return;
		Pending.Add(__instance);
	}

	static void AfterTick()
	{
		if (Pending.Count == 0)
			return;
		if (!Ready())
		{
			Pending.Clear();
			return;
		}
		foreach (SyncInfo si in Pending)
		{
			if (si == null)
				continue;
			_remove.Invoke(_alive, new object[] { si });
			Parked.Add(si);
		}
		Pending.Clear();
	}

	static void AfterDelta(InternalEntity entity)
	{
		if (entity is not SyncInfo si || !Parked.Remove(si))
			return;
		Pending.Remove(si);
		if (!Ready())
			return;
		_add.Invoke(_alive, new object[] { si });
	}

	static bool Ready()
	{
		if (_remove != null)
			return _alive != null || Bind();
		return Bind();
	}

	static bool Bind()
	{
		ClientEntityManager manager = Net.ClientEntityManager;
		if (manager == null)
			return false;
		FieldInfo field = AccessTools.Field(typeof(EntityManager), "AliveEntities");
		_alive = field?.GetValue(manager);
		if (_alive == null)
			return false;
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		_add = _alive.GetType().GetMethod("Add", flags);
		_remove = _alive.GetType().GetMethod("Remove", flags);
		return _add != null && _remove != null;
	}
}
