using System.Collections.Generic;
using HarmonyLib;
using KrokV5Optimization;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

// contents of a closed bag stay off. the bag itself stays on.
// decay still runs here, because Item.Update does not run while the object is off.
public static class BagSleep
{
	struct Row
	{
		public int Id;
		public Item Item;
	}

	// 9.4 ms was the full deactivate pass. the row count is not fixed here.
	// 32 a frame spreads that pass, and a few hundred closed-bag items still finish in a fraction of a second.
	const int SleepBudget = 32;

	static readonly List<Row> Rows = new List<Row>();
	static readonly HashSet<int> Ids = new HashSet<int>();
	static readonly HashSet<int> Holds = new HashSet<int>();
	static readonly Dictionary<int, bool> Lights = new Dictionary<int, bool>();
	static int _sleepLeft;

	internal static void Note(Item item)
	{
		if (item == null)
			return;
		if (item.TryGetComponent(out Container box))
		{
			int count = box.transform.childCount;
			for (int i = 0; i < count; i++)
			{
				Item child = box.transform.GetChild(i).GetComponent<Item>();
				if (child != null)
					Track(child);
			}
		}
		if (InBag(item, out _))
			Track(item);
	}

	// the mesh is off, so the tracker physics tick does not run. the host still
	// publishes the same fields, but only on a step where one of them changed.
	internal static void WriteHost()
	{
		if (!Net.IsServer || Rows.Count == 0)
			return;
		for (int i = 0; i < Rows.Count; i++)
		{
			Item item = Rows[i].Item;
			if (item == null || item.gameObject.activeSelf)
				continue;
			if (!item.TryGetComponent(out SyncInfoGameObjectTracker tracker))
				continue;
			if (tracker.syncInfo is null)
				continue;
			tracker.NoteCarry();
			if (!tracker.syncInfo.InactiveChanged())
				continue;
			tracker.syncInfo.WriteObjectIntoPacket();
		}
	}

	internal static void Tick()
	{
		if (!SharedMain.LocalWorldIsGenerated || WorldGeneration.world == null || !WorldGeneration.world.worldExists)
			return;
		_sleepLeft = SleepBudget;
		for (int i = Rows.Count - 1; i >= 0; i--)
		{
			Item item = Rows[i].Item;
			if (item == null)
			{
				DropAt(i);
				continue;
			}
			if (Ghost(item.gameObject))
			{
				DropAt(i);
				UnityEngine.Object.Destroy(item.gameObject);
				continue;
			}
			if (!InBag(item, out Container box))
			{
				Wake(item);
				DropAt(i);
				continue;
			}
			if (HeldOpen(box) || Breaking(box) || MoveCommit.Busy(item) || KeepLit(item, box))
			{
				Wake(item);
				continue;
			}
			Hold(item);
			GameObject go = item.gameObject;
			if (go.activeSelf)
			{
				if (_sleepLeft <= 0)
					continue;
				go.SetActive(false);
				_sleepLeft--;
			}
			item.HandleDecay();
			if (Net.IsServer)
				ItemSync.SpentDestroy(item);
		}
	}

	internal static void WakeContents(Container box)
	{
		if (box == null)
			return;
		// opening used to turn the prefab's leftover bags on. drop those before the wake.
		FixContainerBroke.Strip(box.gameObject, true);
		int count = box.transform.childCount;
		for (int i = 0; i < count; i++)
		{
			Transform child = box.transform.GetChild(i);
			if (child == null)
				continue;
			if (Ghost(child.gameObject))
				continue;
			Item childItem = child.GetComponent<Item>();
			if (childItem != null)
				Wake(childItem);
			else
				child.gameObject.SetActive(true);
		}
	}

	internal static void SleepContents(Container box)
	{
		if (box == null || HeldOpen(box) || Breaking(box))
			return;
		int count = box.transform.childCount;
		int left = SleepBudget;
		for (int i = 0; i < count; i++)
		{
			Item child = box.transform.GetChild(i).GetComponent<Item>();
			if (child == null)
				continue;
			Track(child);
			// a move still in flight has to stay on. sleeping it here dropped the seat and left the bag icon behind.
			if (MoveCommit.Busy(child) || KeepLit(child, box))
			{
				Wake(child);
				continue;
			}
			Hold(child);
			if (!child.gameObject.activeSelf)
				continue;
			if (left <= 0)
				continue;
			child.gameObject.SetActive(false);
			left--;
		}
	}

	static void Track(Item item)
	{
		int id = item.gameObject.GetInstanceID();
		if (!Ids.Add(id))
			return;
		Rows.Add(new Row { Id = id, Item = item });
	}

	static void DropAt(int index)
	{
		int id = Rows[index].Id;
		Ids.Remove(id);
		Holds.Remove(id);
		int last = Rows.Count - 1;
		Rows[index] = Rows[last];
		Rows.RemoveAt(last);
	}

	static void Hold(Item item)
	{
		int id = item.gameObject.GetInstanceID();
		Holds.Add(id);
		ChunkSleep.Drop(item.gameObject);
	}

	static void Release(Item item)
	{
		Holds.Remove(item.gameObject.GetInstanceID());
		ChunkSleep.Watch(item);
	}

	// chunk wake asks this before setactive. a closed bag stays off, an open one does not.
	internal static bool LeaveAsleep(GameObject go)
	{
		if (go == null)
			return false;
		if (Holds.Contains(go.GetInstanceID()))
			return true;
		Transform parent = go.transform.parent;
		if (parent == null)
			return false;
		if (!parent.TryGetComponent(out Container box))
			return false;
		Item item = go.GetComponent<Item>();
		if (item == null)
			return false;
		if (HeldOpen(box) || Breaking(box) || MoveCommit.Busy(item) || KeepLit(item, box))
			return false;
		return true;
	}

	internal static void Wake(Item item)
	{
		if (item == null)
			return;
		Release(item);
		if (!item.gameObject.activeSelf)
			item.gameObject.SetActive(true);
	}

	// the weight bar already reads live children. the icons do not, until this.
	internal static void Refresh(Container box)
	{
		PlayerCamera cam = PlayerCamera.main;
		if (cam == null || box == null || cam.currentContainer != box)
			return;
		cam.RepopulateContainer();
	}

	// prefab loot on the client. it has a container parent and no sync id.
	static bool Ghost(GameObject go)
	{
		if (go == null || !Net.IsRunning || Net.IsServer)
			return false;
		if (NetObjectRegistry.IsRegistered(go))
			return false;
		Transform parent = go.transform.parent;
		return parent != null && parent.GetComponent<Container>() != null;
	}

	static bool InBag(Item item, out Container box)
	{
		box = null;
		Transform parent = item.transform.parent;
		if (parent == null)
			return false;
		return parent.TryGetComponent(out box);
	}

	static bool HeldOpen(Container box)
	{
		PlayerCamera cam = PlayerCamera.main;
		return cam != null && cam.currentContainer == box;
	}

	// a glowstick on a belt is still in a closed bag. turning the object off kills the light.
	static bool KeepLit(Item item, Container box)
	{
		if (item == null || box == null || !Emits(item))
			return false;
		return box.GetComponentInParent<Body>() != null;
	}

	static bool Emits(Item item)
	{
		int id = item.gameObject.GetInstanceID();
		if (Lights.TryGetValue(id, out bool on))
			return on;
		on = item.GetComponentInChildren<LightItem>(true) != null
			|| item.GetComponentInChildren<Light2D>(true) != null;
		if (Lights.Count > 4000)
			Lights.Clear();
		Lights[id] = on;
		return on;
	}

	static bool Breaking(Container box)
	{
		if (box == null || !box.TryGetComponent(out Item bag))
			return false;
		if (ItemSync.DyingContainer(bag))
			return true;
		return GravBagBoom.Armed(bag, bag.condition);
	}
}

// stock turns the light off for anything sitting in a container. a worn belt or rig is still inventory.
[HarmonyPatch(typeof(LightItem), "Update")]
static class LightItem_Update_KeepWorn
{
	static void Postfix(LightItem __instance)
	{
		if (__instance == null || !__instance.inContainer || __instance.light == null)
			return;
		if (__instance.transform.parent == null || __instance.transform.parent.GetComponentInParent<Body>() == null)
			return;
		__instance.inContainer = false;
		if (__instance.shouldEnable)
			__instance.light.enabled = true;
	}
}

[HarmonyPatch(typeof(Container), nameof(Container.UnloadAllItems))]
static class Container_UnloadAllItems_WakeBag
{
	// spill turns physics back on. an inactive child would stay invisible.
	static void Prefix(Container __instance)
	{
		BagSleep.WakeContents(__instance);
	}
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OpenContainer))]
static class PlayerCamera_OpenContainer_WakeBag
{
	static void Postfix(Container cont)
	{
		BagSleep.WakeContents(cont);
	}
}

[HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
static class Container_UnloadItem_RefreshOpen
{
	static void Prefix(Item item)
	{
		BagSleep.Wake(item);
	}

	static void Postfix(Container __instance)
	{
		if (MoveCommit.Applying)
			BagSleep.Refresh(__instance);
	}
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.CloseContainer))]
static class PlayerCamera_CloseContainer_SleepBag
{
	static void Prefix(PlayerCamera __instance)
	{
		if (__instance != null)
			BagSleep.SleepContents(__instance.currentContainer);
	}
}
