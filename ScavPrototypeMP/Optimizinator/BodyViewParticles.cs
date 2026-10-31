using System.Collections.Generic;
using UnityEngine;

namespace Together;

/// <summary>
/// drip and blood systems are local. nothing in the sync packet carries them.
/// one body per frame. a playing system with a zero rate still sits in ParticleSystem.Update,
/// so an idle system is stopped, and it plays again only while it is emitting or particles are alive.
/// a body outside the local player's 3x3 is stopped outright.
/// </summary>
internal static class BodyViewParticles
{
	sealed class Slot
	{
		internal ParticleSystem[] Systems = System.Array.Empty<ParticleSystem>();
		internal float Refresh = -1f;
	}

	static readonly Dictionary<int, Slot> Slots = new Dictionary<int, Slot>(8);
	static readonly List<Body> Order = new List<Body>(8);
	static int _index;

	internal static void Tick()
	{
		if (!Net.IsRunning)
			return;
		if (_index >= Order.Count)
			Refill();
		if (Order.Count == 0)
			return;

		Body body = Order[_index];
		_index++;
		if (body == null)
			return;

		Body local = LocalBody();
		if (local == null || WorldGeneration.world == null || !WorldGeneration.world.worldExists)
			return;
		Vector2Int origin = ChunkStamp.ChunkOf(local.transform.position);
		bool keep = body == local || Near(origin, ChunkStamp.ChunkOf(body.transform.position));
		Apply(body, keep);
	}

	static void Refill()
	{
		Order.Clear();
		foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict)
		{
			if (pair.Key != null)
				Order.Add(pair.Key);
		}
		_index = 0;
	}

	static bool Near(Vector2Int origin, Vector2Int chunk)
	{
		int dx = origin.x - chunk.x;
		int dy = origin.y - chunk.y;
		if (dx < 0)
			dx = -dx;
		if (dy < 0)
			dy = -dy;
		return dx <= 1 && dy <= 1;
	}

	static void Apply(Body body, bool keep)
	{
		int id = body.GetInstanceID();
		if (!Slots.TryGetValue(id, out Slot slot))
		{
			slot = new Slot();
			Slots[id] = slot;
		}
		if (Time.unscaledTime >= slot.Refresh)
		{
			slot.Systems = body.GetComponentsInChildren<ParticleSystem>(true);
			slot.Refresh = Time.unscaledTime + 2f;
		}
		ParticleSystem[] systems = slot.Systems;
		for (int i = 0; i < systems.Length; i++)
		{
			ParticleSystem system = systems[i];
			if (system == null)
				continue;
			if (!keep)
			{
				if (!system.isStopped)
					system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
				continue;
			}
			// rate is 0 on a dry limb. leave it playing only while particles are still in the air.
			if (EmissionOn(system) || system.particleCount > 0)
			{
				if (!system.isPlaying)
					system.Play(false);
			}
			else if (!system.isStopped)
			{
				system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
			}
		}
	}

	static bool EmissionOn(ParticleSystem system)
	{
		ParticleSystem.EmissionModule emission = system.emission;
		if (!emission.enabled)
			return false;
		if (RateOn(emission.rateOverTime))
			return true;
		return RateOn(emission.rateOverDistance);
	}

	static bool RateOn(ParticleSystem.MinMaxCurve curve)
	{
		switch (curve.mode)
		{
			case ParticleSystemCurveMode.Constant:
				return curve.constant > 0.001f;
			case ParticleSystemCurveMode.TwoConstants:
				return curve.constantMax > 0.001f;
			default:
				return curve.constant > 0.001f || curve.constantMax > 0.001f;
		}
	}

	static Body LocalBody()
	{
		if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null)
			return ScavPlayer.LocalPlayer.body;
		if (PlayerCamera.main != null)
			return PlayerCamera.main.body;
		return null;
	}
}
