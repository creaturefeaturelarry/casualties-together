using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteEntitySystem;
using KrokV5Optimization;

namespace Together;

/// <summary>
/// A hitch queues every server snapshot. Each one has to be applied or a spawn
/// and a position that changed only in that snapshot are gone.
/// Prediction replay is the expensive part of walking them, so the extras are
/// written here in order and the blend to the newest one stays a single tick.
/// </summary>
internal static class StateCatchUp
{
	const int KeepReady = 2;

	static FieldInfo _ready;
	static FieldInfo _pool;
	static FieldInfo _stateA;
	static FieldInfo _stateB;
	static FieldInfo _timer;
	static FieldInfo _total;
	static FieldInfo _lerp;
	static FieldInfo _serverTick;
	static FieldInfo _changed;
	static FieldInfo _entities;
	static MethodInfo _count;
	static MethodInfo _extract;
	static MethodInfo _enqueue;
	static MethodInfo _readDiff;
	static MethodInfo _syncCalls;
	static MethodInfo _clearChanged;
	static MethodInfo _preload;
	static MethodInfo _rpcs;
	static object _onNext;
	static bool _missed;
	static float _loggedAt;

	internal static void Arm(Harmony harmony)
	{
		MethodInfo update = AccessTools.Method(typeof(ClientEntityManager), "Update");
		if (update == null)
		{
			OptimizerMain.Log.LogWarning("state catch-up missed ClientEntityManager.Update");
			return;
		}
		harmony.Patch(update, prefix: new HarmonyMethod(typeof(StateCatchUp), nameof(Before)));
		OptimizerMain.Log.LogDebug("state catch-up applies extra snapshots");
	}

	static void Before(ClientEntityManager __instance)
	{
		if (__instance == null || Net.IsServer)
			return;
		try
		{
			Trim(__instance);
		}
		catch (Exception e)
		{
			if (!_missed)
			{
				_missed = true;
				OptimizerMain.Log.LogWarning("state catch-up " + e.GetBaseException().Message);
			}
		}
	}

	static void Trim(ClientEntityManager mgr)
	{
		if (!Ready(mgr))
			return;
		object heap = _ready.GetValue(mgr);
		if (heap == null)
			return;
		int count = (int)_count.Invoke(heap, null);
		if (count <= KeepReady)
			return;

		var pending = new List<object>(count);
		for (int i = 0; i < count; i++)
			pending.Add(_extract.Invoke(heap, null));

		int applied = 0;
		object stateB = _stateB.GetValue(mgr);
		if (stateB != null)
		{
			Commit(mgr, stateB, false);
			applied++;
		}

		int last = pending.Count - 1;
		for (int i = 0; i < last; i++)
		{
			if (pending[i] == null)
				continue;
			Commit(mgr, pending[i], true);
			applied++;
		}

		object newest = last >= 0 ? pending[last] : null;
		if (newest != null)
		{
			Preload(mgr, newest);
			_stateB.SetValue(mgr, newest);
		}

		float step = StepSeconds(mgr);
		_total.SetValue(mgr, step);
		_timer.SetValue(mgr, 0f);
		if (_lerp != null)
			_lerp.SetValue(mgr, 0f);

		float now = UnityEngine.Time.realtimeSinceStartup;
		if (applied > 0 && now - _loggedAt > 2f)
		{
			_loggedAt = now;
			OptimizerMain.Log.LogDebug("state catch-up applied " + applied);
		}
	}

	static void Commit(ClientEntityManager mgr, object state, bool preload)
	{
		if (preload)
			Preload(mgr, state);
		object prev = _stateA.GetValue(mgr);
		ushort prevTick = prev != null ? TickOf(prev) : (ushort)0;
		if (prev != null && !ReferenceEquals(prev, state))
			Pool(mgr, prev);
		_stateA.SetValue(mgr, state);
		_stateB.SetValue(mgr, null);
		if (_serverTick != null)
			_serverTick.SetValue(mgr, TickOf(state));
		if (_clearChanged != null)
		{
			object changed = _changed.GetValue(mgr);
			if (changed != null)
				_clearChanged.Invoke(changed, null);
		}
		ExecuteRpcs(state, prevTick);
		_readDiff.Invoke(mgr, null);
		_syncCalls.Invoke(mgr, new object[] { state });
	}

	static void Preload(ClientEntityManager mgr, object state)
	{
		if (_preload == null)
			_preload = state.GetType().GetMethod("Preload");
		object dict = _entities.GetValue(mgr);
		_preload.Invoke(state, new object[] { dict });
	}

	static void ExecuteRpcs(object state, ushort prevTick)
	{
		if (_rpcs == null)
		{
			_rpcs = state.GetType().GetMethod("ExecuteRpcs");
			Type mode = _rpcs.GetParameters()[1].ParameterType;
			_onNext = Enum.Parse(mode, "OnNextState");
		}
		_rpcs.Invoke(state, new object[] { prevTick, _onNext });
	}

	static void Pool(ClientEntityManager mgr, object state)
	{
		object pool = _pool.GetValue(mgr);
		if (pool != null && _enqueue != null)
			_enqueue.Invoke(pool, new object[] { state });
	}

	static float StepSeconds(ClientEntityManager mgr)
	{
		PropertyInfo delta = AccessTools.Property(typeof(EntityManager), "DeltaTimeF");
		if (delta != null)
		{
			object value = delta.GetValue(mgr, null);
			if (value is float step && step > 0f)
				return step;
		}
		float rate = Net.ServerEntitySendRate;
		if (rate < 1f)
			rate = 30f;
		return 1f / rate;
	}

	static ushort TickOf(object state)
	{
		FieldInfo tick = state.GetType().GetField("Tick");
		return tick != null ? (ushort)tick.GetValue(state) : (ushort)0;
	}

	static bool Ready(ClientEntityManager mgr)
	{
		if (_ready != null)
			return _extract != null && _readDiff != null && _syncCalls != null;
		_ready = AccessTools.Field(typeof(ClientEntityManager), "_readyStates");
		_pool = AccessTools.Field(typeof(ClientEntityManager), "_statesPool");
		_stateA = AccessTools.Field(typeof(ClientEntityManager), "_stateA");
		_stateB = AccessTools.Field(typeof(ClientEntityManager), "_stateB");
		_timer = AccessTools.Field(typeof(ClientEntityManager), "_remoteInterpolationTimer");
		_total = AccessTools.Field(typeof(ClientEntityManager), "_remoteInterpolationTotalTime");
		_lerp = AccessTools.Field(typeof(ClientEntityManager), "_remoteLerpFactor");
		_serverTick = AccessTools.Field(typeof(ClientEntityManager), "<ServerTick>k__BackingField");
		_changed = AccessTools.Field(typeof(ClientEntityManager), "_changedEntities");
		_entities = AccessTools.Field(typeof(EntityManager), "EntitiesDict");
		_readDiff = AccessTools.Method(typeof(ClientEntityManager), "ReadDiff");
		_syncCalls = AccessTools.Method(typeof(ClientEntityManager), "ExecuteSyncCalls");
		if (_ready == null || _stateA == null || _stateB == null || _timer == null || _total == null
			|| _entities == null || _readDiff == null || _syncCalls == null)
		{
			Miss("state catch-up missed the snapshot fields");
			return false;
		}
		Type heapType = _ready.FieldType;
		_count = AccessTools.PropertyGetter(heapType, "Count");
		_extract = AccessTools.Method(heapType, "ExtractMin");
		if (_pool != null)
			_enqueue = AccessTools.Method(_pool.FieldType, "Enqueue");
		if (_changed != null)
			_clearChanged = AccessTools.Method(_changed.FieldType, "Clear");
		if (_count == null || _extract == null)
		{
			Miss("state catch-up missed the snapshot heap");
			return false;
		}
		return true;
	}

	static void Miss(string message)
	{
		if (_missed)
			return;
		_missed = true;
		OptimizerMain.Log.LogWarning(message);
	}
}
