using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

/// <summary>
/// the chunk renderer lookup is cached until the item changes chunk.
/// </summary>
[HarmonyPatch]
internal static class ItemTick
{
	static Item _current;
	static Slot _slot;

	sealed class Slot
	{
		internal float X;
		internal float Y;
		internal int ChunkX = int.MinValue;
		internal int ChunkY = int.MinValue;
		internal TilemapRenderer Renderer;
		internal bool Lookup;
	}

	static readonly ConditionalWeakTable<Item, Slot> Slots = new ConditionalWeakTable<Item, Slot>();

	[HarmonyPatch(typeof(Item), "Update")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	static bool Prefix(Item __instance)
	{
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists || !Net.IsRunning)
			return true;

		Slot slot = SlotFor(__instance);
		Vector3 position = __instance.transform.position;
		if (slot.Renderer == null || position.x != slot.X || position.y != slot.Y)
		{
			slot.X = position.x;
			slot.Y = position.y;
			Vector2Int chunk = ChunkStamp.ChunkOf(position);
			if (chunk.x != slot.ChunkX || chunk.y != slot.ChunkY || slot.Renderer == null)
			{
				slot.ChunkX = chunk.x;
				slot.ChunkY = chunk.y;
				slot.Lookup = true;
			}
		}

		_current = __instance;
		_slot = slot;
		return true;
	}

	[HarmonyPatch(typeof(Item), "Update")]
	[HarmonyPostfix]
	static void Postfix()
	{
		HeldSeat.Quiet(_current);
		_current = null;
		_slot = null;
	}

	// stock Item.Update asks for this every item, every frame. a cached renderer is the same object.
	[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GetClosestChunkRenderer))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	static bool RendererPrefix(ref TilemapRenderer __result)
	{
		if (_slot == null || _slot.Lookup || _slot.Renderer == null)
			return true;
		__result = _slot.Renderer;
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GetClosestChunkRenderer))]
	[HarmonyPostfix]
	static void RendererPostfix(TilemapRenderer __result)
	{
		if (_slot == null)
			return;
		_slot.Renderer = __result;
		_slot.Lookup = false;
	}

	static Slot SlotFor(Item item)
	{
		if (!Slots.TryGetValue(item, out Slot slot))
		{
			slot = new Slot();
			Slots.Add(item, slot);
		}
		return slot;
	}
}
