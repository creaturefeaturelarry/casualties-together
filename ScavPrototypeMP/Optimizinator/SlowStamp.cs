using System;
using System.Collections.Generic;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// the host rare pass queues every dead player, or every living player 80 units down.
/// one of those circles runs every 0.1s. the rest of the queue waits.
/// </summary>
internal static class SlowStamp
{
	private const float Gap = 0.1f;

	private struct Job
	{
		internal Vector2 Pos;
		internal Body Body;
	}

	private static readonly Queue<Job> Jobs = new Queue<Job>(16);
	private static float _wait;
	private static int _warns;

	internal static void Enqueue(Vector2 pos, Body body)
	{
		Jobs.Enqueue(new Job { Pos = pos, Body = body });
	}

	internal static void Tick()
	{
		if (Jobs.Count == 0 || !Net.IsServer)
			return;
		if (_wait > 0f)
		{
			_wait -= Time.unscaledDeltaTime;
			if (_wait > 0f)
				return;
		}

		Job job = Jobs.Dequeue();
		try
		{
			NetObjectRegistry._ImmidiatelyRegisterCloseObjects(job.Pos, job.Body);
		}
		catch (Exception ex)
		{
			if (_warns < 3)
			{
				_warns++;
				string message = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
				OptimizerMain.Log.LogWarning("[KrokV5Optimization] slow stamp: " + message);
			}
		}

		if (Jobs.Count > 0)
			_wait = Gap;
	}
}
