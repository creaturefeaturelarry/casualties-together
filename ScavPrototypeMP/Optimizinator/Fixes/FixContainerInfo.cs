using Together;
using System.Collections.Generic;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// WriteContainerInfo built a new ItemsContainerInfo even when the parent had not changed.
/// reuse the last answer when the parent is unchanged and container_netId is not 0.
/// a slot or bag with a null syncBody used to store container_data1 0 without unparenting, and the full snapshot repeated that 0.
/// while the item is still in that slot or bag, keep the last real parent. a real drop has no parent, so it still publishes 0. worn gear is left alone.
/// </summary>
internal static class FixContainerInfo
{
	private struct Hold
	{
		internal int Parent;
		internal ushort Net;
		internal byte Data;
		internal byte Ver;
		internal bool Wait;
		internal bool Known;
	}

	internal struct Shot
	{
		internal bool Ok;
		internal byte Data;
		internal ushort Net;
		internal string Via;
	}

	internal static long Hits;
	internal static long ParentHolds;

	private static readonly Dictionary<int, Hold> Cache = new Dictionary<int, Hold>();

	// this file's counters on the 10s report.
	internal static string Evidence() => "containerHold=" + Hits + " parentHold=" + ParentHolds;

	// false skips WriteContainerInfo. true lets stock build a fresh ItemsContainerInfo.
	internal static bool Begin(SyncInfo __instance, out Shot __state)
	{
		__state = default;
		if (__instance.item == null)
			return true;
		// reading .Value copies the struct. FieldInfo.GetValue boxed the whole SyncVar.
		__state.Ok = true;
		__state.Data = __instance.container_data1.Value;
		__state.Net = __instance.container_netId.Value;
		// null syncBody would make stock store 0. skip the write and leave the last real parent.
		if (ParentLinkMissing(__instance.item))
		{
			__state.Via = "link";
			ParentHolds++;
			return false;
		}
		Item item = __instance.item;
		int id = item.GetInstanceID();
		int parent = ParentId(item);
		// parent moved, or the last net id was 0. stock has to read the container again.
		if (!Cache.TryGetValue(id, out Hold hold) || !hold.Known || hold.Wait || hold.Parent != parent)
		{
			__state.Via = "original";
			return true;
		}
		// same transform, new entity life. the cached id now belongs to whatever spawned next.
		if (LiveVer(item, out byte live) && live != hold.Ver)
		{
			__state.Via = "generation";
			return true;
		}
		__instance.container_netId.Value = hold.Net;
		__instance.container_ver.Value = hold.Ver;
		__instance.container_data1.Value = hold.Data;
		Hits++;
		__state.Via = "cache";
		return false;
	}

	// stock already wrote the fields. remember them so the next unchanged parent can skip the alloc.
	internal static void End(SyncInfo __instance, Shot __state)
	{
		if (__instance.item == null)
			return;
		Item item = __instance.item;
		ushort net = __instance.container_netId.Value;
		byte data = __instance.container_data1.Value;
		byte ver = __instance.container_ver.Value;
		int parent = ParentId(item);
		// container_netId 0 with a parent means the container was not registered yet. caching that leaves the client unable to parent.
		bool wait = net == 0 && parent != 0;
		if (Cache.Count > 20000)
			Cache.Clear();
		Cache[item.GetInstanceID()] = new Hold { Parent = parent, Net = net, Data = data, Ver = ver, Wait = wait, Known = true };
	}

	// FixInvSync calls this after it stores a parent, so the next WriteContainerInfo does not rebuild the note.
	internal static void Remember(Item item, ushort net, byte data, byte ver)
	{
		if (item == null)
			return;
		int parent = ParentId(item);
		if (Cache.Count > 20000)
			Cache.Clear();
		Cache[item.GetInstanceID()] = new Hold
		{
			Parent = parent,
			Net = net,
			Data = data,
			Ver = ver,
			Wait = net == 0 && parent != 0,
			Known = true
		};
	}

	// version of the entity on this parent. false when that entity is not registered yet.
	static bool LiveVer(Item item, out byte ver)
	{
		ver = 0;
		Transform parent = item.transform.parent;
		if (parent == null)
			return false;
		InventorySlot slot = parent.GetComponent<InventorySlot>();
		if (slot != null && slot.body != null && slot.body.TryGetNetBody(out NetBody nb) && nb.syncBody != null)
		{
			ver = nb.syncBody.Version;
			return true;
		}
		Limb limb = parent.GetComponent<Limb>();
		if (limb != null && limb.body != null && limb.body.TryGetNetBody(out NetBody worn) && worn.syncBody != null)
		{
			ver = worn.syncBody.Version;
			return true;
		}
		if (parent.GetComponent<Container>() != null && NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) && bag != null)
		{
			ver = bag.Version;
			return true;
		}
		return false;
	}

	// slot or bag, body exists, syncBody is null. worn gear and a real drop do not match.
	internal static bool ParentLinkMissing(Item item)
	{
		Transform parent = item.transform.parent;
		if (parent == null)
			return false;
		InventorySlot slot = parent.GetComponent<InventorySlot>();
		if (slot != null)
			return slot.body != null && slot.body.TryGetNetBody(out NetBody body) && body.syncBody == null;
		if (parent.GetComponent<Container>() == null)
			return false;
		NetBody owner = item.GetComponentInParent<NetBody>();
		return owner != null && owner.syncBody == null;
	}

	// 0 means no parent, which is a real drop. an instance id is enough; we only compare it to last time.
	private static int ParentId(Item item)
	{
		Transform parent = item.transform.parent;
		return parent == null ? 0 : parent.GetInstanceID();
	}
}
