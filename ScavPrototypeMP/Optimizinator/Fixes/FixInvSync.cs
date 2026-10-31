using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// a full snapshot repeats container_data1. a stored 0 empties the bag on other machines while the item is still inside on the host.
/// on FinishedWorldgen, reread the live parent and store that byte again. a slot or bag with a null syncBody keeps the last real byte. a real drop still publishes 0.
/// mark the field by repeating the real parent. do not write 0 to force the mark. the client was applying that 0 and dropping gear that had just landed.
/// on a client, do not parent or unload until the local world is up. then keep trying until the body or bag exists. no timer. skip SafeUnloadItem when it names no container.
/// do not let a bag byte pull an item out of a slot before LoadItem accepts it. if the id lookup misses, walk the registry.
/// </summary>
internal static class FixInvSync
{
	private struct Wait
	{
		internal SyncInfo Info;
	}

	internal static long Hits;
	internal static long Retries;
	static int Warns;

	private static readonly List<SyncInfo> Batch = new List<SyncInfo>(256);
	private static readonly HashSet<SyncInfo> Seen = new HashSet<SyncInfo>();
	private static readonly Dictionary<int, Wait> Pending = new Dictionary<int, Wait>();
	private static readonly List<int> Done = new List<int>();
	private static float _nextSweep;
	private static bool _wasInGame;
	private static bool _sweepQuiet;

	private static MethodInfo _getInfo;
	private static MethodInfo _apply;
	private static MethodInfo _force;
	private static FieldInfo _hasContainer;
	private static FieldInfo _extra;
	private static FieldInfo _knet;
	private static FieldInfo _id;
	private static FieldInfo _ver;
	private static bool _ready;
	private static bool _dead;
	private static bool _ticking;

	// this file's counters on the 10s report.
	internal static string Evidence() => "invSync=" + Hits + " retry=" + Retries;

	// client: skip bag applies that would unslot an item, and skip empty unloads.
	// host republish is Refresh, called from FixRejoinBaseline when worldgen finishes.
	internal static void Arm(Harmony harmony)
	{
		_apply = AccessTools.Method(typeof(SyncInfo), "UpdateItemContainer");
		if (_apply == null)
			throw new MissingMethodException("inv sync apply");
		Type sync = AccessTools.TypeByName("Together.ItemSync");
		_force = AccessTools.Method(sync, "Container_ForceLoadItem", new[] { typeof(Item), typeof(Container) });
		OptimizerMain.Log.LogDebug("[KrokV5Opt] inv sync is in UpdateItemContainer");
	}

	// host only. walk the registry a few times because parenting one item can register another mid-loop.
	internal static void Refresh()
	{
		if (!Net.IsServer || !Ready() || NetObjectRegistry.SyncRegistry == null)
			return;
		int wrote = 0;
		int skipped = 0;
		Seen.Clear();
		// Seen stops a pass from publishing the same SyncInfo twice. a later pass picks up ones created during the write.
		for (int pass = 0; pass < 4; pass++)
		{
			Batch.Clear();
			foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values)
			{
				if (info != null)
					Batch.Add(info);
			}
			int fresh = 0;
			for (int i = 0; i < Batch.Count; i++)
			{
				SyncInfo info = Batch[i];
				if (!Seen.Add(info))
					continue;
				fresh++;
				int result = Publish(info);
				if (result > 0)
					wrote++;
				else if (result < 0)
					skipped++;
			}
		if (fresh == 0)
			break;
		}
		Seen.Clear();
		Batch.Clear();
		Hits += wrote;
		OptimizerMain.Log?.LogInfo("[KrokV5Opt] inv sync items=" + wrote + " skip=" + skipped);
	}

	// host. true when every worn item, slot item, and bag child has a real parent id.
	// a loose item stays 0. an item whose body or bag has no sync id yet is not ready, so the baseline waits.
	internal static bool GearSettled()
	{
		if (!Net.IsServer || !Ready() || NetObjectRegistry.SyncRegistry == null)
			return true;
		foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values)
		{
			if (info == null || info.go == null)
				continue;
			Item item;
			try
			{
				item = info.item;
			}
			catch (Exception)
			{
				return false;
			}
			if (item == null || item.transform == null || item.transform.parent == null)
				continue;
			if (!OnCharacter(item.transform.parent))
				continue;
			if (FixContainerInfo.ParentLinkMissing(item))
				return false;
			if (!ReadLive(item, out byte data, out ushort net, out byte ver))
				return false;
			if (data == 0 || net == 0)
				return false;
			if (info.container_data1.Value != data || info.container_netId.Value != net || info.container_ver.Value != ver)
				return false;
		}
		return true;
	}

	// slot, worn limb, or container. world clutter with some other parent is not character gear.
	static bool OnCharacter(Transform parent)
	{
		if (parent.GetComponent<InventorySlot>() != null)
			return true;
		if (parent.GetComponent<Limb>() != null)
			return true;
		return parent.GetComponent<Container>() != null;
	}

	// true once the local world finished generating. gear and health wait for this.
	internal static bool InGame()
	{
		return Util.IsWorldGenerated();
	}

	// client only. nothing is parented during the load. after that, a missing body or bag just stays queued.
	internal static void Tick()
	{
		if (Net.IsServer || _apply == null)
			return;
		if (!InGame())
		{
			if (_wasInGame)
			{
				_wasInGame = false;
				_sweepQuiet = false;
				Pending.Clear();
			}
			return;
		}
		MedicalSync.FlushHeldHealth();
		if (!_wasInGame)
		{
			_wasInGame = true;
			_sweepQuiet = false;
			_nextSweep = 0f;
			OptimizerMain.Log?.LogInfo("[KrokV5Opt] inv apply now");
		}
		Sweep();
		if (Pending.Count == 0)
			return;
		Done.Clear();
		// _ticking lets KeepHeld run stock during this reapply. without it, data==1 would skip the apply we just asked for.
		_ticking = true;
		try
		{
			foreach (KeyValuePair<int, Wait> pair in Pending)
			{
				SyncInfo info = pair.Value.Info;
				if (info == null)
				{
					Done.Add(pair.Key);
					continue;
				}
				if (info.go == null)
					info.CreateObject();
				if (info.go == null)
					continue;
				if (info.item == null)
				{
					Done.Add(pair.Key);
					continue;
				}
				if (info.go == null)
					continue;
				if (info.container_data1.Value == 0 || Placed(info))
				{
					Done.Add(pair.Key);
					continue;
				}
				if (TargetMissing(info))
					continue;
				try
				{
					if (!Parent(info))
						continue;
					Retries++;
					Done.Add(pair.Key);
				}
				catch (Exception ex)
				{
					if (Warns < 3)
					{
						Warns++;
						OptimizerMain.Log?.LogWarning("[KrokV5Opt] inv retry failed: " + ex.Message);
					}
				}
			}
		}
		finally
		{
			_ticking = false;
		}
		for (int i = 0; i < Done.Count; i++)
			Pending.Remove(Done[i]);
	}

	// once we're in game, pick up anything the baseline says is on a body or in a bag.
	// stops after a quiet pass. a later field change comes back through AfterApply.
	static void Sweep()
	{
		if (_sweepQuiet)
			return;
		float now = Time.realtimeSinceStartup;
		if (now < _nextSweep || NetObjectRegistry.SyncRegistry == null)
			return;
		_nextSweep = now + 0.5f;
		Batch.Clear();
		foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values)
		{
			if (info != null)
				Batch.Add(info);
		}
		int added = 0;
		for (int i = 0; i < Batch.Count; i++)
		{
			SyncInfo info = Batch[i];
			if (info.container_data1.Value == 0)
				continue;
			if (info.go == null)
				info.CreateObject();
			if (info.go == null || info.item == null || Placed(info))
				continue;
			int id = info.item.GetInstanceID();
			if (Pending.ContainsKey(id) || Pending.Count > 4000)
				continue;
			Pending[id] = new Wait { Info = info };
			added++;
		}
		Batch.Clear();
		if (added == 0 && Pending.Count == 0)
			_sweepQuiet = true;
	}

	// prefix on UpdateItemContainer. false skips it.
	// during load, skip every container byte. 0 is not a drop yet, and the body may not exist.
	// data 1 is "in a bag". stock unloads the slot before the bag load, and a refused load leaves the item on the ground.
	// server and the retry tick still run stock.
	internal static bool KeepHeld(SyncInfo __instance)
	{
		if (_ticking || Net.IsServer || __instance == null || __instance.go == null)
			return true;
		if (!InGame())
			return false;
		Item item;
		try
		{
			item = __instance.item;
		}
		catch (Exception)
		{
			return true;
		}
		// anything except a bag byte runs stock. a bag byte waits for Parent/ForceLoad.
		return item == null || __instance.container_data1.Value != 1;
	}

	// after stock UpdateItemContainer. if the item is not where the byte says, queue a retry.
	// a missing target is not a failure. it stays queued with no deadline.
	internal static void AfterApply(SyncInfo __instance)
	{
		if (_ticking || Net.IsServer || __instance == null || __instance.go == null || __instance.item == null || _apply == null)
			return;
		int id = __instance.item.GetInstanceID();
		if (InGame() && (__instance.container_data1.Value == 0 || Placed(__instance)))
		{
			Pending.Remove(id);
			return;
		}
		if (__instance.container_data1.Value == 0)
			return;
		_sweepQuiet = false;
		if (Pending.ContainsKey(id) || Pending.Count > 4000)
			return;
		Pending[id] = new Wait { Info = __instance };
	}

	// prefix on SafeUnloadItem. false skips it when the info names no container. that call returns before it moves anything, but it still allocates.
	internal static bool SkipLoose(object ici)
	{
		if (ici == null || HasContainer(ici))
			return true;
		return false;
	}

	// host. read the live parent and store it. 1 = wrote and marked dirty, 0 = nothing to say, -1 = leave the stored byte alone.
	static int Publish(SyncInfo info)
	{
		Item item = info.item;
		if (item == null)
			return 0;
		if (MoveCommit.Held(info.syncId))
			return 0;
		if (ItemSync.SuppressContainerWrite > 0) {
			if (ItemSync.ParentTestSwap)
				ItemSync.ParentTest("swap block publish " + ItemSync.ParentTestItem(item));
			return 0;
		}
		byte storedData = info.container_data1.Value;
		ushort storedNet = info.container_netId.Value;
		byte storedVer = info.container_ver.Value;
		if (FixContainerInfo.ParentLinkMissing(item))
			return -1;
		if (!ReadLive(item, out byte data, out ushort net, out byte ver))
			return -1;
		// parent exists but its sync id is still 0. storing that would tell clients the item is loose.
		if (net == 0 && item.transform.parent != null)
			return -1;
		if (net == 0)
			ver = 0;
		bool changed = storedData != data || storedNet != net || storedVer != ver;
		info.container_data1.Value = data;
		info.container_netId.Value = net;
		info.container_ver.Value = ver;
		FixContainerInfo.Remember(item, net, data, ver);
		// already 0 and still loose. do not Touch, or every loose item dirties a field on each refresh.
		if (data == 0 && !changed)
			return 0;
		Touch(info, "container_data1");
		Touch(info, "container_netId");
		Touch(info, "container_ver");
		return 1;
	}

	// MarkDirty flags the stored byte without writing a different value. assigning the same Value does not.
	static void Touch(SyncInfo info, string fieldName)
	{
		if (info == null)
			return;
		if (fieldName == "container_data1")
			info.container_data1.MarkDirty();
		else if (fieldName == "container_netId")
			info.container_netId.MarkDirty();
		else if (fieldName == "container_ver")
			info.container_ver.MarkDirty();
	}

	// client retry. data >= 100 is a body slot or worn limb. data 1 is a bag.
	static bool Parent(SyncInfo info)
	{
		byte data = info.container_data1.Value;
		ushort net = info.container_netId.Value;
		byte ver = info.container_ver.Value;
		if (data == 0 || net == 0 || info.go == null || info.item == null)
			return false;
		// slot or worn gear. the body has to exist, then stock UpdateItemContainer can parent it.
		if (data >= 100)
		{
			if (!ContainerLink.TryBody(info, out _))
				return false;
			_apply.Invoke(info, null);
			return Placed(info);
		}
		SyncInfo bag = FindBag(net, ver, out Container box);
		if (bag == null || bag.go == null || box == null)
			return false;
		// chest already broke. sticking it on the bag turns physics off too.
		if (ItemSync.BrokenBag(bag, box)) {
			ItemSync.ParentTestChest("chest skip retry", info.item, bag.go);
			return false;
		}
		// ForceLoad skips the "unload slot first" path that KeepHeld is avoiding.
		if (_force == null)
			return false;
		_force.Invoke(null, new object[] { info.item, box });
		return Placed(info);
	}

	// true when the transform parent matches the stored byte. data 0 counts as placed (it is loose on purpose).
	static bool Placed(SyncInfo info)
	{
		byte data = info.container_data1.Value;
		if (data == 0 || info.go == null || info.item == null)
			return data == 0;
		Transform parent = info.item.transform.parent;
		if (parent == null)
			return false;
		if (data >= 100)
			return parent.GetComponent<InventorySlot>() != null || parent.GetComponent<Limb>() != null;
		ushort net = info.container_netId.Value;
		byte ver = info.container_ver.Value;
		if (net == 0)
			return false;
		if (ParentBagId(info.item) == net && parent.gameObject != null && NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo liveBag) && liveBag != null && liveBag.Version == ver)
			return true;
		SyncInfo bag = FindBag(net, ver, out _);
		if (bag == null || bag.go == null)
			return false;
		return parent == bag.go.transform || parent.IsChildOf(bag.go.transform);
	}

	// the bag or body named by the byte is not on this machine yet. keep waiting, do not parent to nothing.
	static bool TargetMissing(SyncInfo info)
	{
		byte data = info.container_data1.Value;
		if (data == 0)
			return false;
		ushort net = info.container_netId.Value;
		byte ver = info.container_ver.Value;
		if (net == 0)
			return true;
		if (data >= 100)
			return !ContainerLink.TryBody(info, out _);
		SyncInfo bag = FindBag(net, ver, out _);
		return bag == null || bag.go == null;
	}

	// id lookup first. a miss walks both registries, because the bag can be registered under the other map.
	static SyncInfo FindBag(ushort net, byte ver, out Container box)
	{
		box = null;
		SyncInfo found = null;
		if (net != 0 && NetObjectRegistry.TryGetSyncInfo(new knetid(net), out SyncInfo direct) && direct != null)
			found = direct;
		if (found == null)
			found = ScanBag(net);
		if (found != null && found.Version != ver)
			found = null;
		if (found == null)
			return null;
		box = Box(found);
		return found;
	}

	// linear scan. TryGetSyncInfo misses some bags whose syncId is set but whose key is the unity object.
	static SyncInfo ScanBag(ushort net)
	{
		if (NetObjectRegistry.NetIdToSyncInfoDict != null)
		{
			foreach (SyncInfo info in NetObjectRegistry.NetIdToSyncInfoDict.Values)
			{
				if (SameBag(info, net))
					return info;
			}
		}
		if (NetObjectRegistry.SyncRegistry == null)
			return null;
		foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values)
		{
			if (SameBag(info, net))
				return info;
		}
		return null;
	}

	// syncId throws if the entity has no id yet. that is a miss, not a failure.
	static bool SameBag(SyncInfo info, ushort net)
	{
		if (info == null)
			return false;
		try
		{
			return (ushort)info.syncId == net;
		}
		catch (Exception)
		{
			return false;
		}
	}

	// the Container is sometimes on the bag object, sometimes on a child.
	static Container Box(SyncInfo bag)
	{
		if (bag == null || bag.go == null)
			return null;
		Container own = bag.container;
		if (own != null)
			return own;
		Transform root = bag.go.transform;
		for (int i = 0; i < root.childCount; i++)
		{
			Container child = root.GetChild(i).GetComponent<Container>();
			if (child != null)
				return child;
		}
		return null;
	}

	// sync id of the bag this item is parented under right now. 0 if the parent is not a container.
	static ushort ParentBagId(Item item)
	{
		Transform parent = item.transform.parent;
		if (parent == null || parent.GetComponent<Container>() == null)
			return 0;
		if (!NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) || bag == null)
			return 0;
		try
		{
			return bag.syncId;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	// ItemsContainerInfo.has_container. false means the unload names nothing.
	static bool HasContainer(object ici)
	{
		if (_hasContainer == null)
			_hasContainer = ici.GetType().GetField("has_container", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return _hasContainer != null && _hasContainer.GetValue(ici) is bool on && on;
	}

	// ItemGetContainerInfo is the same read stock uses. containerExtraData1 is the parent kind, knetid is who.
	static bool ReadLive(Item item, out byte data, out ushort net, out byte ver)
	{
		data = 0;
		net = 0;
		ver = 0;
		object info = _getInfo.Invoke(null, new object[] { item });
		if (info == null)
			return false;
		object extra = _extra.GetValue(info);
		object knet = _knet.GetValue(info);
		if (!(extra is byte live) || knet == null)
			return false;
		object id = _id.GetValue(knet);
		if (!(id is ushort liveNet))
			return false;
		data = live;
		net = liveNet;
		if (_ver != null && _ver.GetValue(info) is byte liveVer)
			ver = liveVer;
		return true;
	}

	// one-time reflection. a miss turns the host republish off. the client hooks from Arm stay.
	static bool Ready()
	{
		if (_ready)
			return true;
		if (_dead)
			return false;
		try
		{
			Type sync = AccessTools.TypeByName("Together.ItemSync");
			_getInfo = AccessTools.Method(sync, "ItemGetContainerInfo", new[] { typeof(Item) });
			_apply = AccessTools.Method(typeof(SyncInfo), "UpdateItemContainer");
			if (_getInfo == null || _apply == null)
				throw new MissingMethodException("inv sync read");
			_extra = AccessTools.Field(_getInfo.ReturnType, "containerExtraData1");
			_knet = AccessTools.Field(_getInfo.ReturnType, "knetid");
			_id = AccessTools.Field(typeof(knetid), "id");
			_ver = AccessTools.Field(_getInfo.ReturnType, "containerVer");
			if (_extra == null || _knet == null || _id == null || _ver == null)
				throw new MissingFieldException("inv sync fields");
			_ready = true;
			return true;
		}
		catch (Exception ex)
		{
			_dead = true;
			OptimizerMain.Log.LogWarning("[KrokV5Opt] inv sync off: " + ex.Message);
			return false;
		}
	}
}
