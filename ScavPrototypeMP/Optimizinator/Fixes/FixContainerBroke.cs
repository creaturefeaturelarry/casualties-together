using System.Collections.Generic;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// a container prefab is instantiated whole on the client, baked loot included.
/// the host's real contents arrive as their own synced items and get parented in.
/// unregistered children are the prefab copies. strip those, leave the synced ones.
/// </summary>
[HarmonyPatch(typeof(Container), "ContainerBroke")]
internal static class FixContainerBroke
{
	// client only. host spill is the real item. unregistered children are local ghosts.
	static void Prefix(Container __instance)
	{
		if (__instance == null)
			return;
		Strip(__instance.gameObject, false);
	}

	// client. items sitting under a container with no sync id are the prefab's loot.
	// immediate drops them before Start, so they never simulate or get woken with the bag.
	internal static void Strip(GameObject root, bool immediate)
	{
		if (root == null || !Net.IsRunning || Net.IsServer)
			return;
		if (root.GetComponent<Container>() == null && root.GetComponentInChildren<Container>(true) == null)
			return;

		Item[] found = root.GetComponentsInChildren<Item>(true);
		var local = new List<Item>(found.Length);
		for (int i = 0; i < found.Length; i++)
		{
			Item item = found[i];
			if (item == null || item.gameObject == root)
				continue;
			if (NetObjectRegistry.IsRegistered(item.gameObject))
				continue;
			if (!UnderContainer(item.transform, root.transform))
				continue;
			local.Add(item);
		}
		local.Sort((a, b) => Depth(b.transform, root.transform).CompareTo(Depth(a.transform, root.transform)));
		for (int i = 0; i < local.Count; i++)
		{
			if (local[i] == null)
				continue;
			GameObject go = local[i].gameObject;
			if (go == null)
				continue;
			Transform parent = go.transform.parent;
			if (parent != null)
				go.transform.SetParent(null);
			if (immediate)
				Object.DestroyImmediate(go);
			else
				Object.Destroy(go);
		}
	}

	static bool UnderContainer(Transform item, Transform root)
	{
		Transform parent = item.parent;
		while (parent != null)
		{
			if (parent.GetComponent<Container>() != null)
				return true;
			if (parent == root)
				break;
			parent = parent.parent;
		}
		return false;
	}

	static int Depth(Transform item, Transform root)
	{
		int depth = 0;
		Transform cursor = item;
		while (cursor != null && cursor != root)
		{
			depth++;
			cursor = cursor.parent;
		}
		return depth;
	}
}
