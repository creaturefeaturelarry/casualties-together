using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// host belt drag picked the item up before TryGetSyncInfoOrRegister, so a later sweep still saw it loose.
/// register it immediately after PickUpItem.
/// </summary>
[HarmonyPatch(typeof(Body), "PickUpItem")]
internal static class FixHostPickup
{
	// after PickUpItem, if the body is actually holding it, register the SyncInfo now.
	static void Postfix(Body __instance, Item item)
	{
		if (!Net.IsServer || item == null || __instance == null)
			return;
		if (!__instance.HoldingItem(item))
			return;
		NetObjectRegistry.TryGetSyncInfoOrRegister((Component)item, out _);
	}
}
