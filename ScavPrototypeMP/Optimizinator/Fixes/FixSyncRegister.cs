using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// TryGetSyncInfoOrRegister created the SyncInfo and then returned false, so the container id was stored as 0.
/// return the SyncInfo it just created.
/// </summary>
internal static class FixSyncRegister
{
	// only when stock returned false. if the registry already has this object, that create succeeded and the false was a lie.
	internal static void Recover(GameObject obj, ref SyncInfo si, ref bool result)
	{
		if (result || obj == null || !Net.IsServer)
			return;
		if (!NetObjectRegistry.SyncRegistry.TryGetValue(obj, out SyncInfo created) || created == null)
			return;
		si = created;
		result = true;
	}
}
