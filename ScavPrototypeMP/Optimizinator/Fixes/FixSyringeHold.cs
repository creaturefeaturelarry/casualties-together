using System.Collections.Generic;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// Inject changes condition and the liquid list, and the diff then skips container_data1 because that byte did not change.
/// the client still has 0, so UpdateItemContainer drops the copy. resend the current container fields with that packet.
/// </summary>
internal static class FixSyringeHold
{
	internal struct Snap
	{
		internal float Condition;
		internal int Liquid;
		internal bool Seen;
	}

	internal static long Hits;

	private static readonly Dictionary<int, Snap> Last = new Dictionary<int, Snap>();

	// this file's counters on the 10s report.
	internal static string Evidence() => "syringeHold=" + Hits;

	// snapshot condition and liquid before WriteObjectIntoPacket mutates the sync fields.
	internal static void Begin(SyncInfo __instance, out Snap __state)
	{
		__state = default;
		if (!Net.IsServer || __instance == null || __instance.item == null)
			return;
		__state.Seen = true;
		__state.Condition = __instance.item.condition;
		__state.Liquid = LiquidHash(__instance.item);
	}

	// if condition or the liquid stack changed since last packet, force the container fields dirty too.
	internal static void End(SyncInfo __instance, Snap __state)
	{
		if (!__state.Seen || __instance == null || __instance.item == null)
			return;
		Item item = __instance.item;
		int id = item.GetInstanceID();
		Snap now = new Snap
		{
			Seen = true,
			Condition = item.condition,
			Liquid = LiquidHash(item)
		};
		// first time we see the item, remember it. do not punch or the join packet gets a fake dirty.
		if (!Last.TryGetValue(id, out Snap prev))
		{
			if (Last.Count > 20000)
				Last.Clear();
			Last[id] = now;
			return;
		}
		if (prev.Condition == now.Condition && prev.Liquid == now.Liquid)
			return;
		Last[id] = now;
		Punch(__instance);
		Hits++;
	}

	// writing the same value does not mark a SyncVar changed. flip it, then write the real value back.
	static void Punch(SyncInfo info)
	{
		byte data = info.container_data1.Value;
		info.container_data1.Value = (byte)(data == 0 ? 1 : 0);
		info.container_data1.Value = data;
		ushort net = info.container_netId.Value;
		info.container_netId.Value = (ushort)(net == 0 ? 1 : 0);
		info.container_netId.Value = net;
		byte ver = info.container_ver.Value;
		info.container_ver.Value = (byte)(ver == 0 ? 1 : 0);
		info.container_ver.Value = ver;
	}

	// order-sensitive hash of the liquid stack. used only to notice a change, not sent on the wire.
	static int LiquidHash(Item item)
	{
		WaterContainerItem water = item.GetComponent<WaterContainerItem>();
		if (water == null || water.stack == null)
			return 0;
		int hash = water.stack.Count;
		for (int i = 0; i < water.stack.Count; i++)
		{
			LiquidStack part = water.stack[i];
			hash = hash * 31 + (part.liquidId == null ? 0 : part.liquidId.GetHashCode());
			hash = hash * 31 + (int)(part.amount * 10f);
		}
		return hash;
	}
}
