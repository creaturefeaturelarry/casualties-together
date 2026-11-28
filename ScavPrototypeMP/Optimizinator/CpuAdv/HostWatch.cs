using System.Collections.Generic;
using UnityEngine;

namespace Together;

// Hosting keeps a wake around every living player.
internal static class HostWatch
{
	internal static bool IsHost()
	{
		return Net.IsRunning && Net.IsServer;
	}

	internal static bool IsDedicated()
	{
		return Net.IsRunning && Net.IsDedicatedServer;
	}

	internal struct Watch
	{
		public int Id;
		public Vector2 Pos;
		public float HalfW;
		public float HalfH;
	}

	internal static int CopyOthers(List<Watch> into, Body local)
	{
		into.Clear();
		if (!IsHost())
			return 0;
		List<ScavPlayer> living = ScavPlayer.AllLivingPlayers;
		for (int i = 0; i < living.Count; i++)
		{
			ScavPlayer plr = living[i];
			if (plr == null || plr.body == null)
				continue;
			if (local != null && plr.body == local)
				continue;
			SyncStep.HalfExtents((byte)plr.playerId.id, false, out float halfW, out float halfH);
			into.Add(new Watch
			{
				Id = plr.playerId.id,
				Pos = plr.body.transform.position,
				HalfW = halfW,
				HalfH = halfH
			});
		}
		return into.Count;
	}
}
