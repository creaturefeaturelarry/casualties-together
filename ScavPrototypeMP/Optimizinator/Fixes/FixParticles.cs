using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// FluidManager.RenderFluids built a new List and a new Particle[] every draw.
/// keep one Particle[] per liquid and SetParticles(buffer, count).
/// </summary>
[HarmonyPatch(typeof(FluidManager), "RenderFluids")]
internal static class FixParticles
{
	private static readonly AccessTools.FieldRef<FluidManager, byte[,]> FluidRef =
		AccessTools.FieldRefAccess<FluidManager, byte[,]>("fluid");
	private static readonly AccessTools.FieldRef<FluidManager, List<ParticleSystem>> ParticlesRef =
		AccessTools.FieldRefAccess<FluidManager, List<ParticleSystem>>("liquidParticles");

	private static ParticleSystem.Particle[][] _buffers;
	private static int[] _counts;
	private static bool _warned;

	// false skips stock RenderFluids. true means we failed and stock should draw.
	static bool Prefix(FluidManager __instance)
	{
		try
		{
			if (!Render(__instance))
				return true;
			return false;
		}
		catch (Exception ex)
		{
			if (!_warned)
			{
				_warned = true;
				OptimizerMain.Log.LogWarning("[KrokV5Opt] fluid particles fell back: " + ex.Message);
			}
			return true;
		}
	}

	// walk the simulated fluid grid once. each liquid id has its own particle system and its own buffer.
	private static bool Render(FluidManager manager)
	{
		if (manager.LiquidParticlePrefabs == null)
			return false;
		int kinds = manager.LiquidParticlePrefabs.Count;
		List<ParticleSystem> systems = ParticlesRef(manager);
		byte[,] fluid = FluidRef(manager);
		if (kinds <= 0 || systems == null || systems.Count < kinds || fluid == null)
			return false;

		if (_buffers == null || _buffers.Length != kinds)
		{
			_buffers = new ParticleSystem.Particle[kinds][];
			_counts = new int[kinds];
		}
		else
		{
			Array.Clear(_counts, 0, _counts.Length);
		}

		(RangeI, RangeI) range = manager.SimulationRange();
		int x0 = range.Item1.min;
		int x1 = range.Item1.max;
		int y0 = range.Item2.min;
		int y1 = range.Item2.max;
		for (int x = x0; x < x1; x++)
		{
			for (int y = y0; y < y1; y++)
			{
				// id 0 is empty. surface tiles are shorter and sit lower, matching stock.
				int id = fluid[x, y];
				if (id == 0)
					continue;
				int bucket = id - 1;
				bool surface = fluid[x, y + 1] == 0 && (fluid[x + 1, y] == 0 || fluid[x - 1, y] == 0);
				var particle = new ParticleSystem.Particle
				{
					position = WorldGeneration.world.BlockToWorldPos(new Vector2Int(x, y))
					           + (surface ? new Vector2(0f, -0.3125f) : Vector2.zero),
					startLifetime = 999f,
					remainingLifetime = 999f,
					startColor = Color.white,
					startSize3D = new Vector2(1.25f, surface ? 0.625f : 1.25f)
				};
				Add(bucket, particle);
			}
		}

		for (int i = 0; i < kinds; i++)
		{
			int count = _counts[i];
			ParticleSystem.Particle[] buffer = _buffers[i] ?? (_buffers[i] = new ParticleSystem.Particle[1]);
			// count, not buffer.Length. the spare slots are leftover from a bigger frame.
			systems[i].SetParticles(buffer, count);
		}
		return true;
	}

	// grow that liquid's buffer by doubling. the array lives for the session.
	private static void Add(int bucket, ParticleSystem.Particle particle)
	{
		int count = _counts[bucket];
		ParticleSystem.Particle[] buffer = _buffers[bucket];
		if (buffer == null || count == buffer.Length)
		{
			int cap = buffer == null ? 64 : buffer.Length * 2;
			var grown = new ParticleSystem.Particle[cap];
			if (buffer != null)
				Array.Copy(buffer, grown, count);
			_buffers[bucket] = buffer = grown;
		}
		buffer[count] = particle;
		_counts[bucket] = count + 1;
	}
}
