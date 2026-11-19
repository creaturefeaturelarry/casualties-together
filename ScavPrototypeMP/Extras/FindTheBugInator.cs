using System;
using System.Collections.Generic;
using System.Text;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// Temporary vanish logging. Delete this file to remove it.
// ForceVerbose keeps DebugMenuSettings.is_verbose true so the config cannot turn it back off.
// All FindTheBugInator lines are gated on Multiplayer.verbose (always on while ForceVerbose is true).
internal static class FindTheBugInator {
    const bool ForceVerbose = false;
    const float DumpSeconds = 10f;

    static readonly HashSet<ushort> SeenOnBody = new HashSet<ushort>();
    static readonly HashSet<int> AskSkipLogged = new HashSet<int>();
    static readonly Dictionary<int, int> RampStage = new Dictionary<int, int>();
    static readonly Dictionary<int, int> SwingLogged = new Dictionary<int, int>();
    static float _nextDump;

    internal static void Tick() {
        if (ForceVerbose)
            DebugMenuSettings.is_verbose = true;
        if (!Multiplayer.verbose)
            return;
        if (!Util.IsWorldGenerated())
            return;
        if (Time.unscaledTime < _nextDump)
            return;
        _nextDump = Time.unscaledTime + DumpSeconds;
        try {
            if (Net.IsServer) {
                if (ScavPlayer.BodyToPlayerDict == null)
                    return;
                foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
                    if (plr == null || plr.body == null)
                        continue;
                    Dump(plr.body, "player=" + plr.playerName);
                }
            } else {
                Body local = Util.GetLocalBody();
                if (local != null)
                    Dump(local, "local");
            }
        } catch (Exception ex) {
            Line("inventory", "dump-failed " + ex.Message);
        }
    }

    internal static void Line(string tag, string detail) {
        if (ForceVerbose)
            DebugMenuSettings.is_verbose = true;
        if (!Multiplayer.verbose)
            return;
        Plugin.log.LogInfo("[FindTheBugInator] " + tag + " " + detail);
    }

    internal static string Side() {
        if (Net.IsServer)
            return "host";
        if (Net.IsClient)
            return "client";
        return "offline";
    }

    internal static string SyncLine(SyncInfo si) {
        if (si == null)
            return "sync=null side=" + Side();
        ushort id = 0;
        byte data = 0;
        ushort cnet = 0;
        string itemId = "?";
        string go = "null";
        string parent = "none";
        string body = "body=none";
        try { id = si.syncId; } catch (Exception) { }
        try { data = si.container_data1.Value; } catch (Exception) { }
        try { cnet = si.container_netId.Value.id; } catch (Exception) { }
        try {
            if (si.go != null) {
                go = si.go.name;
                if (si.go.TryGetComponent(out Item item) && item != null && !string.IsNullOrEmpty(item.id))
                    itemId = item.id;
                Transform p = si.go.transform.parent;
                parent = ParentKind(p);
                body = BodyLine(p);
            }
        } catch (Exception) { }
        return "sync=" + id + " item=" + itemId + " go=" + go + " byte=" + data + " cnet=" + cnet + " parent=" + parent + " " + body + " side=" + Side();
    }

    internal static string ItemLine(Item item) {
        if (item == null)
            return "item=null side=" + Side();
        if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
            return SyncLine(si);
        string parent = "none";
        string body = "body=none";
        try {
            parent = ParentKind(item.transform.parent);
            body = BodyLine(item.transform.parent);
        } catch (Exception) { }
        return "sync=none item=" + item.id + " go=" + item.name + " byte=? cnet=? parent=" + parent + " " + body + " side=" + Side();
    }

    internal static void Remember(SyncInfo si) {
        if (si == null)
            return;
        try {
            if (si.syncId != 0)
                SeenOnBody.Add(si.syncId);
        } catch (Exception) { }
    }

    internal static void LocalAttempt(string op, Item item, string extra) {
        try {
            Line("local-attempt", "op=" + op + " " + extra + " " + ItemLine(item));
        } catch (Exception ex) {
            Line("local-attempt", "op=" + op + " log-failed " + ex.Message);
        }
    }

    internal static bool IsLocalAttempt(Item item, Body body) {
        try {
            if (body != null && body.IsBodyLocal())
                return true;
            PlayerCamera cam = PlayerCamera.main;
            if (cam == null || cam.body == null || !cam.body.IsBodyLocal())
                return false;
            if (item != null && cam.dragItem == item)
                return true;
            if (item != null && item.transform.parent != null) {
                Body owner = item.GetComponentInParent<Body>();
                if (owner != null && owner.IsBodyLocal())
                    return true;
            }
        } catch (Exception) { }
        return false;
    }

    internal static ushort PeekId(NetDataReader reader) {
        if (reader == null)
            return 0;
        int pos = reader.Position;
        try {
            reader.Get(out knetid id);
            return id.id;
        } catch (Exception) {
            return 0;
        } finally {
            try { reader.SetPosition(pos); } catch (Exception) { }
        }
    }

    internal static void NoteLayerFree() {
        if (NetObjectRegistry.SyncRegistry == null)
            return;
        foreach (SyncInfo si in NetObjectRegistry.SyncRegistry.Values) {
            if (si == null)
                continue;
            ushort id = 0;
            byte data = 0;
            try { id = si.syncId; } catch (Exception) { continue; }
            try { data = si.container_data1.Value; } catch (Exception) { }
            bool seen = SeenOnBody.Contains(id);
            bool onBody = OnBody(si);
            // byte >= 100 still names a body slot or a worn limb, but the transform is not on a body.
            // a seen id that is loose here is what keep-inventory frees with no per-id destroy line.
            if (onBody)
                continue;
            if (!seen && data < 100)
                continue;
            Line("path4-desync-id-drop", "keep-inventory seen=" + seen + " " + SyncLine(si));
        }
    }

    static void Dump(Body body, string who) {
        var sb = new StringBuilder(512);
        sb.Append("who=").Append(who).Append(" side=").Append(Side()).Append(' ').Append(BodyLine(body.transform));
        if (body.slots != null) {
            for (int i = 0; i < body.slots.Length; i++) {
                InventorySlot slot = body.slots[i];
                Item item = null;
                try { item = body.GetItem(i); } catch (Exception) { }
                string hand = slot != null && slot.isHand ? "hand" : "slot";
                sb.Append(" | ").Append(hand).Append(i).Append('=');
                if (item == null)
                    sb.Append("empty");
                else {
                    sb.Append(ItemLine(item));
                    RememberHeld(item);
                }
            }
        }
        if (body.limbs != null) {
            for (int i = 0; i < body.limbs.Length; i++) {
                Limb limb = body.limbs[i];
                if (limb == null)
                    continue;
                for (int c = 0; c < limb.transform.childCount; c++) {
                    Transform child = limb.transform.GetChild(c);
                    if (!child.TryGetComponent(out Item worn))
                        continue;
                    sb.Append(" | worn=").Append(ItemLine(worn));
                    RememberHeld(worn);
                    if (!worn.TryGetComponent(out Container box))
                        continue;
                    for (int k = 0; k < box.transform.childCount; k++) {
                        if (!box.transform.GetChild(k).TryGetComponent(out Item inside))
                            continue;
                        sb.Append(" | inbag=").Append(ItemLine(inside));
                        RememberHeld(inside);
                    }
                }
            }
        }
        Line("inventory", sb.ToString());
    }

    static void RememberHeld(Item item) {
        if (item != null && ItemSync.TryGetSyncInfo(item, out SyncInfo si))
            Remember(si);
    }

    static bool OnBody(SyncInfo si) {
        try {
            return si != null && si.go != null && si.go.transform.parent != null && si.go.GetComponentInParent<Body>() != null;
        } catch (Exception) {
            return false;
        }
    }

    static string ParentKind(Transform parent) {
        if (parent == null)
            return "none";
        try {
            if (parent.TryGetComponent(out InventorySlot slot))
                return (slot.isHand ? "hand:" : "slot:") + slot.slot;
            if (parent.GetComponent<Container>() != null)
                return "bag";
            if (parent.GetComponent<Limb>() != null)
                return "limb";
            return "other:" + parent.name;
        } catch (Exception) {
            return "other";
        }
    }

    internal static string BodyLine(Transform parent) {
        if (parent == null)
            return "body=none";
        try {
            Body body = parent.GetComponentInParent<Body>();
            if (body == null)
                return "body=none";
            string local = body.IsBodyLocal() ? "local" : "remote";
            if (body.TryGetNetBody(out NetBody nb))
                return "bodyNet=" + nb.netId.id + " body=" + local + " syncBody=" + (nb.syncBody != null);
            return "bodyNet=none body=" + local + " syncBody=false";
        } catch (Exception) {
            return "body=error";
        }
    }

    internal static byte ByteOf(Item item) {
        try {
            if (item != null && ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
                return si.container_data1.Value;
        } catch (Exception) { }
        return 0;
    }

    internal static bool InSlot(Item item, out bool hand, out int slot) {
        hand = false;
        slot = -1;
        try {
            if (item == null || item.transform.parent == null)
                return false;
            if (!item.transform.parent.TryGetComponent(out InventorySlot hold))
                return false;
            hand = hold.isHand;
            slot = hold.slot;
            return true;
        } catch (Exception) {
            return false;
        }
    }

    // verbose only. one line per snapshot ask, drop, apply, and hostility step.
    internal static void Trader(string what, string detail) {
        try {
            Line("trader", what + " " + detail);
        } catch (Exception ex) {
            Line("trader", what + " log-failed " + ex.Message);
        }
    }

    internal static void TraderAsk(TrackerTrader tt) {
        if (!Multiplayer.verbose)
            return;
        try {
            bool ready = (tt != null) && (tt.si != null) && (tt.si.syncId != 0);
            if (!ready) {
                int key = (tt == null) ? 0 : tt.GetInstanceID();
                if (!AskSkipLogged.Add(key))
                    return;
                Trader("ask-skip", (tt == null) ? "tt=null" : TraderState(tt.og));
                return;
            }
            Trader("ask-send", TraderState(tt.og));
        } catch (Exception ex) {
            Line("trader", "ask log-failed " + ex.Message);
        }
    }

    internal static void TraderRamp(TraderScript trader) {
        if (!Multiplayer.verbose || (trader == null))
            return;
        try {
            int stage = -1;
            if (trader.hostile)
                stage = 3;
            else if (trader.hostility >= 55f)
                stage = 2;
            else if (trader.hostility >= 30f)
                stage = 1;
            else if (trader.reputation < 30f)
                stage = 0;
            int key = trader.GetInstanceID();
            if (RampStage.TryGetValue(key, out int prev) && (prev == stage))
                return;
            if ((stage < 0) && !RampStage.ContainsKey(key))
                return;
            RampStage[key] = stage;
            Trader("ramp", "stage=" + stage + " " + TraderState(trader));
        } catch (Exception ex) {
            Line("trader", "ramp log-failed " + ex.Message);
        }
    }

    internal static void TraderSwing(TraderScript trader) {
        if (!Multiplayer.verbose || (trader == null))
            return;
        try {
            int key = trader.GetInstanceID();
            SwingLogged.TryGetValue(key, out int n);
            n++;
            SwingLogged[key] = n;
            if ((n > 4) && ((n % 8) != 0))
                return;
            Trader("swing", "n=" + n + " " + TraderState(trader));
        } catch (Exception ex) {
            Line("trader", "swing log-failed " + ex.Message);
        }
    }

    internal static string RefreshText(TraderScript trader) {
        try {
            PlayerCamera cam = PlayerCamera.main;
            if (cam == null)
                return "refresh=no-cam";
            bool same = cam.currentTrader == trader;
            bool menu = Util.IsInTradingMenu();
            string which = "yes";
            if (!same || !menu)
                which = "no";
            return "refresh=" + which + " same=" + (same ? "1" : "0") + " menu=" + (menu ? "1" : "0");
        } catch (Exception) {
            return "refresh=err";
        }
    }

    internal static string IdList(List<knetid> ids) {
        if (ids == null)
            return "n=0";
        var sb = new StringBuilder(32);
        sb.Append("n=").Append(ids.Count).Append(" to=");
        int shown = ids.Count;
        if (shown > 8)
            shown = 8;
        for (int i = 0; i < shown; i++) {
            if (i > 0)
                sb.Append(',');
            sb.Append(ids[i].id);
        }
        return sb.ToString();
    }

    internal static string TraderState(TraderScript trader) {
        if (trader == null)
            return "trader=null side=" + Side();
        string reg = "none";
        string tracker = "none";
        try {
            if (NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out SyncInfo si) && (si != null))
                reg = si.syncId.ToString();
        } catch (Exception) {
            reg = "err";
        }
        try {
            TrackerTrader tt = trader.GetComponent<TrackerTrader>();
            if (tt == null)
                tracker = "no-tt";
            else if (tt.si == null)
                tracker = "si-null";
            else
                tracker = tt.si.syncId.ToString();
        } catch (Exception) {
            tracker = "err";
        }
        int items = -1;
        string first = "";
        try {
            if (trader.items == null)
                items = -1;
            else
                items = trader.items.Count;
            if ((trader.items != null) && (trader.items.Count > 0) && (trader.items[0] != null))
                first = " first=" + trader.items[0].id;
        } catch (Exception) { }
        float rep = 0f;
        float hostl = 0f;
        int character = 0;
        bool hostile = false;
        bool convo = false;
        Vector2 pos = default;
        Vector2 desired = default;
        try { rep = trader.reputation; } catch (Exception) { }
        try { hostl = trader.hostility; } catch (Exception) { }
        try { character = trader.character; } catch (Exception) { }
        try { hostile = trader.hostile; } catch (Exception) { }
        try { convo = trader.startedConvo; } catch (Exception) { }
        try { pos = trader.transform.position; } catch (Exception) { }
        try { desired = trader.desiredPos; } catch (Exception) { }
        return "side=" + Side()
            + " reg=" + reg
            + " tracker=" + tracker
            + " rep=" + rep.ToString("0.#")
            + " hostl=" + hostl.ToString("0.#")
            + " hostile=" + (hostile ? "1" : "0")
            + " items=" + items
            + first
            + " char=" + character
            + " convo=" + (convo ? "1" : "0")
            + " pos=" + pos.x.ToString("0.#") + "," + pos.y.ToString("0.#")
            + " desired=" + desired.x.ToString("0.#") + "," + desired.y.ToString("0.#");
    }
}

[HarmonyPatch(typeof(ItemSync), "FixedUpdate")]
static class FindTheBugInatorTick {
    static void Postfix() {
        FindTheBugInator.Tick();
    }
}

// path1: a bag byte force-loads an item that is sitting in a slot. That is the flicker.
[HarmonyPatch(typeof(ItemSync), nameof(ItemSync.Container_ForceLoadItem))]
static class FindTheBugInatorForceLoad {
    static void Prefix(Item item, out byte __state) {
        __state = 0;
        try {
            if (!FindTheBugInator.InSlot(item, out _, out _))
                return;
            __state = FindTheBugInator.ByteOf(item);
        } catch (Exception) { }
    }

    static void Postfix(Item item, Container container, byte __state) {
        try {
            if (__state != 1)
                return;
            string dest = container == null ? "bag=null" : "bag=" + container.name;
            FindTheBugInator.Line("path1-stale-bag-byte-drop", "force-load-from-slot fromByte=1 toByte=" + FindTheBugInator.ByteOf(item) + " " + dest + " " + FindTheBugInator.ItemLine(item));
        } catch (Exception ex) {
            FindTheBugInator.Line("path1-stale-bag-byte-drop", "force-load log-failed " + ex.Message);
        }
    }
}

// path1: the hand diff then sends a drop while the item is still a bag child or still byte 1.
[HarmonyPatch(typeof(ItemSync), "DoClientInventoryUpdate")]
static class FindTheBugInatorClientDrop {
    static void Prefix(out SyncInfo[] __state) {
        __state = null;
        try {
            SyncInfo[] src = ItemSync.Client_last_inventory_state;
            if (src != null)
                __state = (SyncInfo[])src.Clone();
        } catch (Exception) { }
    }

    static void Postfix(SyncInfo[] __state) {
        try {
            if (__state == null || !Net.IsClient)
                return;
            Body body = Util.GetLocalBody();
            if (body == null || body.slots == null)
                return;
            var now = new HashSet<SyncInfo>();
            for (int i = 0; i < body.slots.Length; i++) {
                Item item = body.GetItem(i);
                if (item != null && ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
                    now.Add(si);
            }
            for (int i = 0; i < __state.Length; i++) {
                SyncInfo old = __state[i];
                if (old == null || now.Contains(old))
                    continue;
                byte data = 0;
                try { data = old.container_data1.Value; } catch (Exception) { }
                bool bagParent = false;
                try {
                    Transform parent = old.go != null ? old.go.transform.parent : null;
                    bagParent = parent != null && parent.GetComponent<Container>() != null;
                } catch (Exception) { }
                if (data != 1 && !bagParent)
                    continue;
                FindTheBugInator.Line("path1-stale-bag-byte-drop", "client-sent-drop fromSlot=" + i + " " + FindTheBugInator.SyncLine(old));
            }
        } catch (Exception ex) {
            FindTheBugInator.Line("path1-stale-bag-byte-drop", "client-drop log-failed " + ex.Message);
        }
    }
}

// path1 on the host: a drop packet unloads an item that is still inside a bag.
[HarmonyPatch(typeof(ItemSync), "ServerReceiver__ItemDropWearable")]
static class FindTheBugInatorHostDrop {
    static void Prefix(NetDataReader reader) {
        try {
            ushort id = FindTheBugInator.PeekId(reader);
            if (id == 0 || !ItemSync.TryGetItem(id, out SyncInfo si, out Item item) || item == null)
                return;
            byte data = 0;
            try { data = si.container_data1.Value; } catch (Exception) { }
            bool bagParent = item.transform.parent != null && item.transform.parent.GetComponent<Container>() != null;
            if (data != 1 && !bagParent)
                return;
            FindTheBugInator.Line("path1-stale-bag-byte-drop", "host-drop-while-bagged fromByte=" + data + " " + FindTheBugInator.SyncLine(si));
        } catch (Exception ex) {
            FindTheBugInator.Line("path1-stale-bag-byte-drop", "host-drop log-failed " + ex.Message);
        }
    }
}

// path2: after the container write, the item is in a slot and the published byte is still 0.
[HarmonyPatch(typeof(SyncInfo), "WriteContainerInfo")]
static class FindTheBugInatorPublishZero {
    static void Prefix(SyncInfo __instance, out byte __state) {
        __state = 0;
        try {
            if (__instance != null)
                __state = __instance.container_data1.Value;
        } catch (Exception) { }
    }

    static void Postfix(SyncInfo __instance, byte __state) {
        try {
            if (__instance == null || __instance.item == null)
                return;
            if (!FindTheBugInator.InSlot(__instance.item, out _, out _))
                return;
            byte now = __instance.container_data1.Value;
            if (now != 0)
                return;
            FindTheBugInator.Line("path2-publish-zero-while-seated", "fromByte=" + __state + " toByte=0 " + FindTheBugInator.SyncLine(__instance));
        } catch (Exception ex) {
            FindTheBugInator.Line("path2-publish-zero-while-seated", "log-failed " + ex.Message);
        }
    }
}

// path3: host accepted the unload, then PickUpItem did not leave the item in the slot.
[HarmonyPatch(typeof(ItemSync), "SeatPickup")]
static class FindTheBugInatorSeatFail {
    static void Postfix(Body body, Item item, byte slot, Container bag) {
        try {
            if (item == null || body == null)
                return;
            if (body.HoldingItem(item))
                return;
            string bagName = bag == null ? "bag=null" : "bag=" + bag.name;
            string parent = "none";
            try { parent = item.transform.parent == null ? "none" : item.transform.parent.name; } catch (Exception) { }
            FindTheBugInator.Line("path3-seat-failed-no-restore", "slot=" + slot + " " + bagName + " parentAfter=" + parent + " " + FindTheBugInator.ItemLine(item) + " " + FindTheBugInator.BodyLine(body.transform));
        } catch (Exception ex) {
            FindTheBugInator.Line("path3-seat-failed-no-restore", "log-failed " + ex.Message);
        }
    }
}

// path4: a stored 0 unloads an item that is still in a slot. path5: a mismatched byte unloads and does not end on that parent.
[HarmonyPatch(typeof(SyncInfo), "UpdateItemContainer")]
static class FindTheBugInatorApply {
    sealed class Snap {
        internal bool inSlot;
        internal byte data;
        internal ushort net;
        internal byte live;
        internal ushort liveNet;
    }

    static void Prefix(SyncInfo __instance, out Snap __state) {
        __state = null;
        try {
            if (__instance == null || __instance.item == null)
                return;
            var snap = new Snap();
            snap.inSlot = FindTheBugInator.InSlot(__instance.item, out _, out _);
            snap.data = __instance.container_data1.Value;
            snap.net = __instance.container_netId.Value.id;
            ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(__instance.item);
            if (ici != null) {
                snap.live = ici.containerExtraData1;
                snap.liveNet = ici.knetid.id;
            }
            __state = snap;
        } catch (Exception) { }
    }

    static void Postfix(SyncInfo __instance, Snap __state) {
        try {
            if (__state == null || __instance == null || __instance.item == null)
                return;
            bool loose = __instance.item.transform.parent == null;
            if (__state.inSlot && __state.data == 0 && loose)
                FindTheBugInator.Line("path4-desync-id-drop", "byte0-unloaded-seated-item fromByte=0 toParent=none " + FindTheBugInator.SyncLine(__instance));
            bool mismatch = __state.data != 0 && (__state.data != __state.live || __state.net != __state.liveNet);
            if (!mismatch || !loose)
                return;
            FindTheBugInator.Line("path5-apply-reject-rollback", "apply-reverted fromByte=" + __state.live + " storedByte=" + __state.data + " fromNet=" + __state.liveNet + " storedNet=" + __state.net + " " + FindTheBugInator.SyncLine(__instance));
        } catch (Exception ex) {
            FindTheBugInator.Line("path4-desync-id-drop", "apply log-failed " + ex.Message);
        }
    }
}

// path4: layer change frees a sync id that was on a body, or whose byte still names a body, with no per-id destroy line.
[HarmonyPatch(typeof(NetObjectRegistry), nameof(NetObjectRegistry.UnregisterAllObjectsButKeepInventory))]
static class FindTheBugInatorKeepInventory {
    static void Prefix() {
        try {
            FindTheBugInator.NoteLayerFree();
        } catch (Exception ex) {
            FindTheBugInator.Line("path4-desync-id-drop", "keep-inventory log-failed " + ex.Message);
        }
    }
}

// path4: the server GameObject died, which frees the sync id from the tracker with no item-id line.
[HarmonyPatch(typeof(SyncInfoGameObjectTracker), "OnDestroy")]
static class FindTheBugInatorTrackerDestroy {
    static void Prefix(SyncInfoGameObjectTracker __instance) {
        try {
            if (!Net.IsServer || __instance == null || __instance.syncInfo == null)
                return;
            SyncInfo si = __instance.syncInfo;
            ushort id = 0;
            try { id = si.syncId; } catch (Exception) { return; }
            if (id == 0)
                return;
            bool item = false;
            try { item = si.go != null && si.go.GetComponent<Item>() != null; } catch (Exception) { }
            if (!item)
                return;
            FindTheBugInator.Line("path4-desync-id-drop", "tracker-ondestroy " + FindTheBugInator.SyncLine(si));
        } catch (Exception ex) {
            FindTheBugInator.Line("path4-desync-id-drop", "tracker log-failed " + ex.Message);
        }
    }
}

// path5: an unload announce for an item the slot diff still has. That is a second apply beside the pickup.
[HarmonyPatch(typeof(ItemSync), nameof(ItemSync.Client_AnnounceContainerChange))]
static class FindTheBugInatorAnnounce {
    static void Prefix(SyncInfo si) {
        try {
            if (si == null || si.item == null || Net.IsServer)
                return;
            ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(si.item);
            if (ici == null)
                return;
            bool wearingLocal = ici.is_wearing && ici.limb != null && ici.limb.IsBodyLocal();
            if (ici.is_in_locplr_surface_inv || wearingLocal)
                return;
            bool unload = !(ici.has_container && ici.container_si != null);
            if (!unload)
                return;
            SyncInfo[] last = ItemSync.Client_last_inventory_state;
            bool inSlots = false;
            if (last != null) {
                for (int i = 0; i < last.Length; i++) {
                    if (last[i] == si)
                        inSlots = true;
                }
            }
            if (!inSlots)
                return;
            FindTheBugInator.Line("path5-apply-reject-rollback", "unload-announce-while-slotted " + FindTheBugInator.SyncLine(si));
        } catch (Exception ex) {
            FindTheBugInator.Line("path5-apply-reject-rollback", "announce log-failed " + ex.Message);
        }
    }
}

// path5: a denied pickup that does not put the item back leaves the local copy loose.
[HarmonyPatch(typeof(ItemSync), "ClientReceiver_ItemPickupDENY")]
static class FindTheBugInatorDeny {
    static void Prefix(NetDataReader reader, out ushort __state) {
        __state = 0;
        try { __state = FindTheBugInator.PeekId(reader); } catch (Exception) { }
    }

    static void Postfix(ushort __state) {
        try {
            if (__state == 0 || !ItemSync.TryGetItem(__state, out SyncInfo si, out Item item))
                return;
            if (item != null && item.transform.parent != null)
                return;
            FindTheBugInator.Line("path5-apply-reject-rollback", "deny-left-loose " + FindTheBugInator.SyncLine(si));
        } catch (Exception ex) {
            FindTheBugInator.Line("path5-apply-reject-rollback", "deny log-failed " + ex.Message);
        }
    }
}

// path5: the open inventory belongs to another body, but PickUpItem still runs on the local body.
[HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
static class FindTheBugInatorPickup {
    static void Prefix(Body __instance, Item item, int slot, bool force) {
        try {
            if (FindTheBugInator.IsLocalAttempt(item, __instance))
                FindTheBugInator.LocalAttempt("pickup", item, "slot=" + slot + " force=" + force + " " + FindTheBugInator.BodyLine(__instance.transform));
            Body focused = InvButton_get_body_MultiplayerPatch.focused_body;
            if (focused == null || __instance == null || !__instance.IsBodyLocal() || focused == __instance)
                return;
            string focusedNet = "focusedNet=none";
            try {
                if (focused.TryGetNetBody(out NetBody nb))
                    focusedNet = "focusedNet=" + nb.netId.id;
            } catch (Exception) { }
            FindTheBugInator.Line("path5-apply-reject-rollback", "focused-body-hand slot=" + slot + " " + focusedNet + " " + FindTheBugInator.ItemLine(item) + " " + FindTheBugInator.BodyLine(__instance.transform));
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "pickup log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DropItem), typeof(Item))]
static class FindTheBugInatorDrop {
    static void Prefix(Body __instance, Item item) {
        try {
            if (FindTheBugInator.IsLocalAttempt(item, __instance))
                FindTheBugInator.LocalAttempt("drop", item, FindTheBugInator.BodyLine(__instance.transform));
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "drop log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.WearWearable))]
static class FindTheBugInatorWear {
    static void Prefix(Body __instance, Item item) {
        try {
            if (FindTheBugInator.IsLocalAttempt(item, __instance))
                FindTheBugInator.LocalAttempt("wear", item, FindTheBugInator.BodyLine(__instance.transform));
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "wear log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DropWearable))]
static class FindTheBugInatorDropWear {
    static void Prefix(Body __instance, Item item) {
        try {
            if (FindTheBugInator.IsLocalAttempt(item, __instance))
                FindTheBugInator.LocalAttempt("drop-wear", item, FindTheBugInator.BodyLine(__instance.transform));
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "drop-wear log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.SwapSlots))]
static class FindTheBugInatorSwap {
    static void Prefix(Body __instance, int slot1, int slot2) {
        try {
            if (__instance == null || !__instance.IsBodyLocal())
                return;
            Item a = __instance.GetItem(slot1);
            Item b = __instance.GetItem(slot2);
            FindTheBugInator.LocalAttempt("swap", a, "slot=" + slot1 + " otherSlot=" + slot2 + " " + FindTheBugInator.ItemLine(b));
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "swap log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.LoadItem))]
static class FindTheBugInatorLoad {
    static void Prefix(Container __instance, Item item) {
        try {
            if (!FindTheBugInator.IsLocalAttempt(item, null))
                return;
            string dest = __instance == null ? "dest=null" : "dest=" + __instance.name;
            FindTheBugInator.LocalAttempt("load", item, dest);
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "load log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
static class FindTheBugInatorUnload {
    static void Prefix(Item item) {
        try {
            if (!FindTheBugInator.IsLocalAttempt(item, null))
                return;
            FindTheBugInator.LocalAttempt("unload", item, "");
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "unload log-failed " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleReleaseDragging))]
static class FindTheBugInatorRelease {
    static void Prefix(PlayerCamera __instance) {
        try {
            if (__instance == null || __instance.dragItem == null)
                return;
            FindTheBugInator.LocalAttempt("release-drag", __instance.dragItem, FindTheBugInator.BodyLine(__instance.body != null ? __instance.body.transform : null));
        } catch (Exception ex) {
            FindTheBugInator.Line("local-attempt", "release-drag log-failed " + ex.Message);
        }
    }
}
