using System.Collections.Generic;
using System.Text;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

// plant and box breaks that leave no loot. quiet unless findthebug is on.
internal static class EvidenceBag
{
	static double _hotUntil;
	static int _lines;
	static readonly Dictionary<int, byte> _logged = new Dictionary<int, byte>();

	internal static bool On => FindTheBug.On;

	internal static string Evidence() => "bagTrace=" + _lines;

	internal static void Arm(Harmony harmony)
	{
		var method = AccessTools.Method(typeof(BuildingEntity), nameof(BuildingEntity.Update));
		if (method == null)
		{
			FindTheBug.Write("evidencebag missed BuildingEntity.Update");
			return;
		}
		harmony.Patch(method, prefix: new HarmonyMethod(typeof(EvidenceBag), nameof(OnBuildingUpdate)) { priority = Priority.First });
	}

	// a break or open just happened. item starts in the next few seconds get a line.
	internal static void Arm()
	{
		if (!On)
			return;
		double until = Time.unscaledTimeAsDouble + 3.0;
		if (until > _hotUntil)
			_hotUntil = until;
	}

	internal static void Line(string message)
	{
		if (!On)
			return;
		_lines++;
		FindTheBug.Write("evidencebag " + message);
	}

	internal static string Side()
	{
		if (!Net.IsRunning)
			return "offline";
		if (Net.IsHost)
			return "host";
		if (Net.IsServer)
			return "server";
		return "client";
	}

	internal static string Building(BuildingEntity be)
	{
		if (!On)
			return "";
		if (be == null)
			return "building=null";
		string sync = "none";
		if (be.gameObject != null && NetObjectRegistry.TryGetSyncInfo(be.gameObject, out SyncInfo si))
			sync = si.syncId.ToString();
		Vector2 pos = be.transform.position;
		var near = ScavPlayer.GetDistanceToNearestLivingPlayer(pos);
		return "res=" + be.id
			+ " health=" + be.GetHealth().ToString("0.###")
			+ " sync=" + sync
			+ " pos=" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0")
			+ " living=" + ScavPlayer.AllLivingPlayers.Count
			+ " nearest2=" + near.Item2.ToString("0")
			+ " " + Drops(be);
	}

	static string Drops(BuildingEntity be)
	{
		var text = new StringBuilder();
		ItemDrop[] roll = be.itemsDropOnDestroy;
		int rollCount = roll != null ? roll.Length : 0;
		text.Append("roll=").Append(rollCount);
		if (rollCount > 0)
		{
			text.Append('[');
			int shown = rollCount < 8 ? rollCount : 8;
			for (int i = 0; i < shown; i++)
			{
				if (i > 0)
					text.Append(',');
				ItemDrop drop = roll[i];
				if (drop == null)
					text.Append("null");
				else
					text.Append(drop.id).Append('@').Append(drop.chance.ToString("0.##"));
			}
			text.Append(']');
		}
		ItemDrop[] always = be.alwaysDrop;
		int alwaysCount = always != null ? always.Length : 0;
		text.Append(" always=").Append(alwaysCount);
		if (alwaysCount > 0)
		{
			text.Append('[');
			int shown = alwaysCount < 8 ? alwaysCount : 8;
			for (int i = 0; i < shown; i++)
			{
				if (i > 0)
					text.Append(',');
				ItemDrop drop = always[i];
				text.Append(drop != null ? drop.id : "null");
			}
			text.Append(']');
		}
		text.Append(" guaranteed=").Append(be.guaranteedDropAmount);
		return text.ToString();
	}

	// loose items only, and only while a break or open is still warm, or the drop is marked fresh
	internal static bool WatchingItem(Item item)
	{
		if (!On || item == null || !Net.IsRunning || !Util.IsWorldGenerated())
			return false;
		if (item.transform.parent != null)
			return false;
		if (Time.unscaledTimeAsDouble <= _hotUntil)
			return true;
		return item.GetComponent<FreshItemDrop>() != null;
	}

	internal static void AfterItemStart(Item item)
	{
		if (!On)
			return;
		if (item == null)
		{
			Line("item start " + Side() + " destroyed during register");
			return;
		}
		bool registered = NetObjectRegistry.IsRegistered(item.gameObject);
		string sync = "none";
		if (registered && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si))
			sync = si.syncId.ToString();
		var near = ScavPlayer.GetDistanceToNearestLivingPlayer(item.transform.position);
		bool fresh = item.GetComponent<FreshItemDrop>() != null;
		Line("item start " + Side()
			+ " res=" + item.id
			+ " registered=" + registered
			+ " sync=" + sync
			+ " fresh=" + fresh
			+ " cond=" + item.condition.ToString("0.###")
			+ " living=" + ScavPlayer.AllLivingPlayers.Count
			+ " nearest2=" + near.Item2.ToString("0")
			+ " parent=" + (item.transform.parent != null));
	}

	// same test as the real prefix. host is not IsClient, so the host still runs the spawn.
	static void OnBuildingUpdate(BuildingEntity __instance)
	{
		if (!On || !Net.IsRunning || __instance == null)
			return;
		int id = __instance.GetInstanceID();
		if (__instance.GetHealth() >= 0.5f)
		{
			_logged.Remove(id);
			return;
		}
		if (_logged.ContainsKey(id))
			return;
		if (_logged.Count > 300)
			_logged.Clear();
		_logged[id] = 1;
		Arm();
		bool skip = Net.IsClient;
		Line((skip ? "update skip " : "update run ") + Side() + " " + Building(__instance));
	}
}
