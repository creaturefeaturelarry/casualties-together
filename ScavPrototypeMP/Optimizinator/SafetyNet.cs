using System.Collections.Generic;
using CasualtiesTogetherUtils;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// host near-stamp. one quadrant of one living player per frame, players in order.
/// after the last player, wait 1.9s before the next loop.
/// an 80 unit circle, a 10 unit always-register band, and 16 farther objects.
/// </summary>
internal static class SafetyNet
{
	private const float Radius = 80f;
	private const float Close = 10f;
	private const int FarBudget = 16;

	private static readonly Vector2[] Quadrant =
	{
		new Vector2(1f, 1f),
		new Vector2(-1f, 1f),
		new Vector2(-1f, -1f),
		new Vector2(1f, -1f),
	};

	private static readonly Collider2D[] Hits = new Collider2D[256];
	private static readonly List<NetBody> Bodies = new List<NetBody>(16);

	private static bool _waiting;
	private static bool _logged;
	private static bool _bufferFullLogged;
	private static float _wait;
	private static int _player;
	private static int _quadrant;
	private static int _farLeft = FarBudget;

	// host only. one quadrant, then the next, then the next living player, then the wait.
	internal static void Tick()
	{
		if (!Net.IsServer)
			return;

		if (_waiting)
		{
			_wait -= Time.deltaTime;
			if (_wait > 0f)
				return;
			_waiting = false;
			_player = 0;
			_quadrant = 0;
			_farLeft = FarBudget;
		}

		IList<ScavPlayer> living;
		try
		{
			living = ScavPlayer.AllLivingPlayers;
		}
		catch (System.Exception ex)
		{
			OptimizerMain.Log.LogWarning("[KrokV5Optimization] living players: " + ex.Message);
			BeginWait();
			return;
		}

		if (living == null || living.Count == 0)
		{
			BeginWait();
			return;
		}

		if (_player >= living.Count)
			_player = 0;

		ScavPlayer plr = living[_player];
		if (plr != null)
			StampQuadrant(plr, _quadrant);

		if (!_logged)
		{
			_logged = true;
			OptimizerMain.Log.LogDebug("[KrokV5Optimization] safety net is one quadrant per frame, then wait " + Delay().ToString("0.0") + "s");
		}

		_quadrant++;
		if (_quadrant < Quadrant.Length)
			return;

		_quadrant = 0;
		_farLeft = FarBudget;
		_player++;
		if (_player >= living.Count)
			BeginWait();
	}

	// rest after the last living player. the next loop starts at the first player again.
	private static void BeginWait()
	{
		_waiting = true;
		_wait = Delay();
		_player = 0;
		_quadrant = 0;
		_farLeft = FarBudget;
	}

	private const float WaitSeconds = 1.9f;

	private static float Delay()
	{
		return WaitSeconds;
	}

	// box over this quarter of the 80 circle. the first quadrant also walks bags, backgrounds, and other bodies.
	private static void StampQuadrant(ScavPlayer plr, int quadrant)
	{
		Vector2 pos = plr.pos;
		Vector2 center = pos + Quadrant[quadrant] * (Radius * 0.5f);
		int count;
		try
		{
			count = Physics2D.OverlapBoxNonAlloc(center, new Vector2(Radius, Radius), 0f, Hits);
		}
		catch (System.Exception ex)
		{
			OptimizerMain.Log.LogWarning("[KrokV5Optimization] quadrant: " + ex.Message);
			return;
		}

		if (count >= Hits.Length && !_bufferFullLogged)
		{
			_bufferFullLogged = true;
			OptimizerMain.Log.LogWarning("[KrokV5Optimization] quadrant hit buffer filled at " + Hits.Length);
		}

		float radiusSqr = Radius * Radius;
		for (int i = 0; i < count; i++)
		{
			Collider2D hit = Hits[i];
			Hits[i] = null;
			if (hit == null)
				continue;
			if (hit.GetComponent<Item>() == null && hit.GetComponent<BuildingEntity>() == null)
				continue;
			GameObject go = hit.gameObject;
			if (go == null)
				continue;
			if (((Vector2)go.transform.position - pos).sqrMagnitude > radiusSqr)
				continue;

			Note(go, pos);
			if (go.TryGetComponent<Container>(out Container container))
			{
				foreach (Item item in container.GetAllItems())
				{
					if (item != null)
						Note(item.gameObject, pos);
				}
			}
		}

		// the other three quadrants are only the box query.
		if (quadrant != 0)
			return;

		try
		{
			foreach (GameObject go in NetObjectRegistry.GetIdentifiedBackgroundsInRadius(pos, Radius))
				Note(go, pos);

			if (plr.body != null)
			{
				foreach (Item item in plr.body.GetAllItemsThorough())
				{
					if (item != null)
						Note(item.gameObject, pos);
				}
			}

			Bodies.Clear();
			NetBody.GetBodiesInRadius(pos, Radius, Bodies);
			if (plr.body != null && plr.body.TryGetNetBody(out NetBody self))
				Bodies.Remove(self);
			for (int i = 0; i < Bodies.Count; i++)
			{
				NetBody other = Bodies[i];
				if (other == null || other.body == null)
					continue;
				foreach (Item item in other.body.GetAllItemsThorough())
				{
					if (item != null)
						Note(item.gameObject, pos);
				}
			}
		}
		catch (System.Exception ex)
		{
			OptimizerMain.Log.LogWarning("[KrokV5Optimization] quadrant extras: " + ex.Message);
		}
	}

	// register an unregistered object the game should sync. within 10 units always. farther objects share a budget of 16.
	private static void Note(GameObject go, Vector2 pos)
	{
		if (go == null || NetObjectRegistry.IsRegistered(go) || !NetObjectRegistry.ObjectShouldBeSynced(go))
			return;
		// outside the 3x3 the far pass drops the id and leaves the mesh. registering it out here just repeats that.
		if (!NetObjectRegistry.ShouldKeepRegistered(go))
			return;

		bool close = ((Vector2)go.transform.position - pos).sqrMagnitude <= Close * Close;
		if (!close)
		{
			if (_farLeft <= 0)
				return;
			_farLeft--;
		}

		NetObjectRegistry.NewGO(go);
	}
}
