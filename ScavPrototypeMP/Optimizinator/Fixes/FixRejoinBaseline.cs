using System;
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using LiteEntitySystem;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

// the accept-time full snapshot is before FinishedWorldgen, so saved gear is not on the body yet.
// queue one more full send when they report worldgen finished, after worn gear and bag contents have a real parent id. an object they already have is updated. a missing one is created.
internal static class FixRejoinBaseline
{
	static readonly List<byte> Pending = new List<byte>();
	static readonly Dictionary<byte, float> Since = new Dictionary<byte, float>();
	static readonly List<byte> GearWatch = new List<byte>();
	static readonly List<byte> Sent = new List<byte>();
	static float _nextPass;
	static float _nextWatch;
	const float BaselineWait = 4f;
	const float WatchLimit = 30f;

	// true only for the packet that flips finished_worldgen from false to true. a repeat does not queue another snapshot.
	internal static void Mark(knetid clientId, out bool __state)
	{
		__state = false;
		if (!Util.IsInWorld())
			return;
		if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null)
			return;
		if (plr.ServerPlayerState == null || plr.ServerPlayerState.finished_worldgen)
			return;
		__state = true;
	}

	// stock has set finished_worldgen. if the body is not spawned yet, wait. the gear is on the body.
	internal static void After(knetid clientId, bool __state)
	{
		if (!__state)
			return;
		if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null)
			return;
		if (plr.body == null)
		{
			((MonoBehaviour)plr).StartCoroutine(WaitForBody(plr));
			return;
		}
		Queue(plr);
	}

	// one extra frame after the body appears, so saved items have been loaded onto it.
	static IEnumerator WaitForBody(ScavPlayer plr)
	{
		float until = Time.realtimeSinceStartup + 30f;
		while (plr != null && plr.body == null && Time.realtimeSinceStartup < until)
			yield return null;
		if (plr == null || plr.body == null)
			yield break;
		yield return null;
		Queue(plr);
	}

	// player ids fit in a byte. 0 is not a player.
	static void Queue(ScavPlayer plr)
	{
		ushort id = plr.playerId;
		if (id == 0 || id > 255)
			return;
		byte key = (byte)id;
		if (!Pending.Contains(key))
		{
			Pending.Add(key);
			Since[key] = Time.realtimeSinceStartup;
		}
	}

	// republish container bytes, then send the full baseline once gear has real parent ids.
	// a few seconds of waiting is fine. after that the baseline goes out anyway so enemies are not stuck,
	// and GearWatch keeps rewriting bytes until the bags and slots are nameable.
	// les calls this at the start of the server tick, before the send.
	internal static void BeforeTick()
	{
		float now = Time.realtimeSinceStartup;
		if (Pending.Count > 0 && now >= _nextPass)
		{
			_nextPass = now + 0.25f;
			// the picture he seats from is this refresh. gear has to be on the body before it.
			for (int i = 0; i < Pending.Count; i++)
				PlayerCheckpoint.ReclaimGear(Pending[i]);
			FixInvSync.Refresh();
			bool settled = FixInvSync.GearSettled();
			ServerEntityManager manager = Net.ServerEntityManager;
			Sent.Clear();
			if (manager != null)
			{
				for (int i = 0; i < Pending.Count; i++)
				{
					byte key = Pending[i];
					if (!Since.TryGetValue(key, out float since))
						since = now;
					bool waited = now - since >= BaselineWait;
					if (!settled && !waited)
						continue;
					SendBaseline(manager, key);
					Sent.Add(key);
					if (!settled && !GearWatch.Contains(key))
					{
						GearWatch.Add(key);
						Since[key] = now;
					}
				}
			}
			for (int i = 0; i < Sent.Count; i++)
			{
				Pending.Remove(Sent[i]);
				// he can drop things after this. do not pull those back into the bag.
				PlayerCheckpoint.ForgetGear(Sent[i]);
				if (!GearWatch.Contains(Sent[i]))
					Since.Remove(Sent[i]);
			}
		}
		if (GearWatch.Count == 0 || now < _nextWatch)
			return;
		_nextWatch = now + 0.5f;
		FixInvSync.Refresh();
		if (FixInvSync.GearSettled())
		{
			for (int i = 0; i < GearWatch.Count; i++)
				Since.Remove(GearWatch[i]);
			GearWatch.Clear();
			return;
		}
		for (int i = GearWatch.Count - 1; i >= 0; i--)
		{
			if (!Since.TryGetValue(GearWatch[i], out float since) || now - since < WatchLimit)
				continue;
			Since.Remove(GearWatch[i]);
			GearWatch.RemoveAt(i);
		}
	}

	// RequestFullBaseline is what the server treats as "send everything".
	static void SendBaseline(ServerEntityManager manager, byte key)
	{
		NetPlayer player = manager.GetPlayer(key);
		if (player == null)
			return;
		manager.RequestFullBaseline(player);
		OptimizerMain.Log?.LogInfo("[KrokV5Opt] rejoin baseline player=" + player.Id);
	}
}
