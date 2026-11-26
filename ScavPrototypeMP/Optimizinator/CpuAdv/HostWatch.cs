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

	internal static int CopyOthers(List<Vector2> into, Body local)
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
			into.Add(plr.body.transform.position);
		}
		return into.Count;
	}
}
