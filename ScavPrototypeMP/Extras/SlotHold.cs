using HarmonyLib;
using UnityEngine;

namespace Together;

// a slot we just sat an item in. the mesh can be loose for a moment and still own it.
// the next craft, and a second item with the same byte, have to pick somewhere else.
static class SlotHold
{
	struct Row
	{
		public int Item;
		public float Until;
	}

	const float Seconds = 0.2f;

	static readonly System.Collections.Generic.Dictionary<long, Row> Rows = new System.Collections.Generic.Dictionary<long, Row>();

	internal static void Keep(Body body, int slot, Item item)
	{
		if (body == null || item == null || body.slots == null)
			return;
		if (slot < 0 || slot >= body.slots.Length)
			return;
		Rows[Key(body, slot)] = new Row
		{
			Item = item.GetInstanceID(),
			Until = Time.realtimeSinceStartup + Seconds,
		};
	}

	internal static void Release(Body body, Item item)
	{
		if (body == null || item == null)
			return;
		int id = item.GetInstanceID();
		long drop = 0;
		bool found = false;
		foreach (var pair in Rows)
		{
			if (pair.Value.Item != id)
				continue;
			if ((int)(pair.Key >> 32) != body.GetInstanceID())
				continue;
			drop = pair.Key;
			found = true;
			break;
		}
		if (found)
			Rows.Remove(drop);
	}

	// true when this slot is held for a different item.
	internal static bool Other(Body body, int slot, Item item)
	{
		if (body == null || !Live(body, slot, out Row row))
			return false;
		if (item != null && item.GetInstanceID() == row.Item)
			return false;
		return true;
	}

	internal static bool Owns(Body body, int slot, Item item)
	{
		if (body == null || item == null || !Live(body, slot, out Row row))
			return false;
		return row.Item == item.GetInstanceID();
	}

	internal static int? NextOpen(Body body)
	{
		if (body == null || body.slots == null)
			return null;
		for (int i = 0; i < body.slots.Length; i++)
		{
			if (body.HoldingItem(i))
				continue;
			if (!body.slots[i].canPickUp)
				continue;
			if (Other(body, i, null))
				continue;
			return i;
		}
		return null;
	}

	static bool Live(Body body, int slot, out Row row)
	{
		row = default;
		if (body.slots == null || slot < 0 || slot >= body.slots.Length)
			return false;
		long key = Key(body, slot);
		if (!Rows.TryGetValue(key, out row))
			return false;
		if (row.Until >= Time.realtimeSinceStartup)
			return true;
		Rows.Remove(key);
		return false;
	}

	static long Key(Body body, int slot)
	{
		return ((long)body.GetInstanceID() << 32) | (uint)slot;
	}
}

[HarmonyPatch(typeof(Body), nameof(Body.FirstEmptySlot))]
static class Body_FirstEmptySlot_Hold
{
	static void Postfix(Body __instance, ref int? __result)
	{
		if (__instance == null || __instance.slots == null)
			return;
		if (__result.HasValue && !SlotHold.Other(__instance, __result.Value, null))
			return;
		__result = SlotHold.NextOpen(__instance);
	}
}

[HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
static class Body_PickUpItem_Hold
{
	static void Postfix(Body __instance, Item item, int slot)
	{
		if (__instance != null && item != null && __instance.HoldingItem(item))
			SlotHold.Keep(__instance, slot, item);
	}
}
