using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// Temporary Item.Update evidence logger. Delete this file to remove it.
// Collects unique Item.Update ticks into a set; dumps and clears every 10s when verbose is on.
// With Item.enabled scheduler, Unity only calls Update on hot (enabled) Items — that is expected;
// the dump still notes hot vs cold counts from Item.allItems without waking cold Items.
// ForceVerbose keeps DebugMenuSettings.is_verbose true so the config cannot turn logging back off.
internal static class ItemUpdateInator
{
    const bool ForceVerbose = false;
    const float DumpSeconds = 10f;

    static readonly object Gate = new object();
    static readonly Dictionary<int, string> Seen = new Dictionary<int, string>();
    static float _nextDump;

    internal static void Note(Item item)
    {
        if (ForceVerbose)
            DebugMenuSettings.is_verbose = true;
        if (!Multiplayer.verbose || item == null)
            return;
        try
        {
            int key = item.GetInstanceID();
            string line = Describe(item);
            lock (Gate)
            {
                Seen[key] = line;
            }
        }
        catch (Exception ex)
        {
            Line("note-failed " + ex.Message);
        }
    }

    internal static void Tick()
    {
        if (ForceVerbose)
            DebugMenuSettings.is_verbose = true;
        if (!Multiplayer.verbose)
            return;
        if (!Util.IsWorldGenerated())
            return;
        if (Time.unscaledTime < _nextDump)
            return;
        _nextDump = Time.unscaledTime + DumpSeconds;

        List<string> lines;
        lock (Gate)
        {
            if (Seen.Count == 0)
            {
                Line("dump empty side=" + Side() + " window=" + DumpSeconds + "s " + SchedulerSummary());
                return;
            }
            lines = new List<string>(Seen.Values);
            Seen.Clear();
        }

        lines.Sort(StringComparer.Ordinal);
        Line("dump begin count=" + lines.Count + " side=" + Side() + " window=" + DumpSeconds + "s " + SchedulerSummary());
        for (int i = 0; i < lines.Count; i++)
            Line("tick " + lines[i]);
        Line("dump end count=" + lines.Count + " side=" + Side());
    }

    // Read-only scan of Item.allItems; never sets enabled.
    static string SchedulerSummary()
    {
        try
        {
            List<Item> items = Item.allItems;
            if (items == null)
                return "scheduler=off hot=0 cold=0 total=0";
            int hot = 0;
            int cold = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null)
                    continue;
                if (item.enabled)
                    hot++;
                else
                    cold++;
            }
            return "scheduler=off hot=" + hot + " cold=" + cold + " total=" + (hot + cold);
        }
        catch (Exception ex)
        {
            return "scheduler=error " + ex.Message;
        }
    }

    // one line per id, right when the entity is born or freed. not folded into the 10s dump.
    internal static void SyncCreate(SyncInfo si)
    {
        SyncLife("create", si);
    }

    internal static void SyncDestroy(SyncInfo si)
    {
        SyncLife("destroy", si);
    }

    internal static void BodyCreate(SyncBody body)
    {
        EntityLife("create", "body", body, BodyDetail(body));
    }

    internal static void BodyDestroy(SyncBody body)
    {
        EntityLife("destroy", "body", body, BodyDetail(body));
    }

    internal static void PlayerCreate(BasePlayer player)
    {
        EntityLife("create", "player", player, PlayerDetail(player));
    }

    internal static void PlayerDestroy(BasePlayer player)
    {
        EntityLife("destroy", "player", player, PlayerDetail(player));
    }

    static void SyncLife(string what, SyncInfo si)
    {
        if (ForceVerbose)
            DebugMenuSettings.is_verbose = true;
        if (si == null)
            return;
        try
        {
            Line("sync-" + what + " " + DescribeSync(si) + Callers());
        }
        catch (Exception ex)
        {
            Line("sync-" + what + " log-failed " + ex.Message);
        }
    }

    static void EntityLife(string what, string kind, LiteEntitySystem.Internal.InternalEntity entity, string detail)
    {
        if (ForceVerbose)
            DebugMenuSettings.is_verbose = true;
        if (entity == null)
            return;
        try
        {
            Line("sync-" + what + " kind=" + kind + " id=" + entity.Id + " " + detail + " side=" + Side() + Callers());
        }
        catch (Exception ex)
        {
            Line("sync-" + what + " kind=" + kind + " log-failed " + ex.Message);
        }
    }

    static string DescribeSync(SyncInfo si)
    {
        var sb = new StringBuilder(256);
        sb.Append("kind=sync id=").Append(si.Id);
        string res = "?";
        try
        {
            if (!string.IsNullOrEmpty(si.resourceId.Value))
                res = si.resourceId.Value;
        }
        catch (Exception) { }
        sb.Append(" res=").Append(res);

        byte containerByte = 0;
        ushort containerNet = 0;
        try { containerByte = si.container_data1.Value; } catch (Exception) { }
        try { containerNet = si.container_netId.Value.id; } catch (Exception) { }
        sb.Append(" byte=").Append(containerByte).Append(" cnet=").Append(containerNet);

        GameObject go = null;
        try { go = si.go; } catch (Exception) { }
        if (go == null)
        {
            sb.Append(" go=null parent=none body=none side=").Append(Side());
            return sb.ToString();
        }

        sb.Append(" go=").Append(go.name);
        Item item = null;
        try { go.TryGetComponent(out item); } catch (Exception) { }
        if (item != null)
        {
            string itemId = string.IsNullOrEmpty(item.id) ? "?" : item.id;
            sb.Append(" item=").Append(itemId);
        }
        else if (go.GetComponent<BuildingEntity>() != null)
        {
            sb.Append(" building=yes");
        }

        Transform parent = null;
        try { parent = go.transform.parent; } catch (Exception) { }
        sb.Append(" parent=").Append(ParentKind(parent));
        sb.Append(' ').Append(BodyLine(parent));
        try
        {
            Vector3 p = go.transform.position;
            sb.Append(" pos=").Append(p.x.ToString("0.0")).Append(',').Append(p.y.ToString("0.0"));
        }
        catch (Exception) { }
        sb.Append(" side=").Append(Side());
        return sb.ToString();
    }

    static string BodyDetail(SyncBody body)
    {
        if (body == null)
            return "body=null";
        string who = "none";
        try
        {
            if (body.nb != null)
                who = body.nb.ToString();
        }
        catch (Exception) { }
        return "who=" + who + " owner=" + body.OwnerId;
    }

    static string PlayerDetail(BasePlayer player)
    {
        if (player == null)
            return "name=?";
        string name = "?";
        try
        {
            if (!string.IsNullOrEmpty(player.playerName.Value))
                name = player.playerName.Value;
        }
        catch (Exception) { }
        return "name=" + name + " owner=" + player.OwnerId;
    }

    // a few callers, so a destroy line says which path freed the id
    static string Callers() {
        try
        {
            var trace = new StackTrace(2, false);
            var frames = trace.GetFrames();
            if (frames == null)
                return "";
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < frames.Length && n < 6; i++)
            {
                var method = frames[i].GetMethod();
                if (method == null)
                    continue;
                string type = method.DeclaringType != null ? method.DeclaringType.Name : "?";
                if (type.IndexOf("ItemUpdateInator", StringComparison.Ordinal) >= 0)
                    continue;
                if (type.IndexOf("Harmony", StringComparison.Ordinal) >= 0)
                    continue;
                sb.Append(n == 0 ? " via " : " <- ");
                sb.Append(type).Append('.').Append(method.Name);
                n++;
            }
            return sb.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }

    static void Line(string detail) {
        if (log.verbose) Plugin.log.LogInfo("[ItemUpdateInator] " + detail);
    }

    static string Side()
    {
        if (Net.IsServer)
            return "host";
        if (Net.IsClient)
            return "client";
        return "offline";
    }

    static string Describe(Item item)
    {
        var sb = new StringBuilder(256);
        string itemId = "?";
        string go = "null";
        string name = "?";
        try
        {
            if (!string.IsNullOrEmpty(item.id))
                itemId = item.id;
            if (item.gameObject != null)
                go = item.gameObject.name;
            try
            {
                if (item.Stats != null && !string.IsNullOrEmpty(item.Stats.fullName))
                    name = item.Stats.fullName;
            }
            catch (Exception)
            {
                name = itemId;
            }
        }
        catch (Exception) { }

        sb.Append("item=").Append(itemId);
        sb.Append(" name=").Append(name);
        sb.Append(" go=").Append(go);
        sb.Append(" iid=").Append(item.GetInstanceID());

        ushort syncId = 0;
        byte containerByte = 0;
        ushort containerNet = 0;
        bool hasSync = false;
        try
        {
            if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
            {
                hasSync = true;
                try { syncId = si.syncId; } catch (Exception) { }
                try { containerByte = si.container_data1.Value; } catch (Exception) { }
                try { containerNet = si.container_netId.Value.id; } catch (Exception) { }
            }
        }
        catch (Exception) { }

        if (hasSync)
            sb.Append(" sync=").Append(syncId).Append(" byte=").Append(containerByte).Append(" cnet=").Append(containerNet);
        else
            sb.Append(" sync=none");

        Transform parent = null;
        try { parent = item.transform.parent; } catch (Exception) { }
        sb.Append(" parent=").Append(ParentKind(parent));
        sb.Append(' ').Append(BodyLine(parent));
        sb.Append(" side=").Append(Side());
        return sb.ToString();
    }

    static string ParentKind(Transform parent)
    {
        if (parent == null)
            return "world";
        try
        {
            if (parent.TryGetComponent(out InventorySlot slot))
                return (slot.isHand ? "hand:" : "belt:") + slot.slot;

            Container bag = parent.GetComponent<Container>();
            if (bag != null)
            {
                Body body = parent.GetComponentInParent<Body>();
                if (body != null)
                {
                    string dead = body.alive ? "alive" : "corpse";
                    Item bagItem = parent.GetComponent<Item>();
                    string bagId = bagItem != null && !string.IsNullOrEmpty(bagItem.id) ? bagItem.id : parent.name;
                    return "bag:" + bagId + ":" + dead;
                }
                Item host = parent.GetComponent<Item>();
                if (host != null)
                    return "chest:" + (string.IsNullOrEmpty(host.id) ? parent.name : host.id);
                return "container:" + parent.name;
            }

            if (parent.GetComponent<Limb>() != null)
            {
                Body body = parent.GetComponentInParent<Body>();
                string dead = body != null && !body.alive ? "corpse" : "worn";
                return dead + ":limb:" + parent.name;
            }

            if (parent.GetComponent<Body>() != null)
            {
                Body body = parent.GetComponent<Body>();
                return (body != null && !body.alive ? "corpse" : "body") + ":" + parent.name;
            }

            if (parent.GetComponent<BuildingEntity>() != null)
                return "building:" + parent.name;

            return "other:" + parent.name;
        }
        catch (Exception)
        {
            return "other";
        }
    }

    static string BodyLine(Transform parent)
    {
        if (parent == null)
            return "body=none";
        try
        {
            Body body = parent.GetComponentInParent<Body>();
            if (body == null)
                return "body=none";
            string local = body.IsBodyLocal() ? "local" : "remote";
            string alive = body.alive ? "alive" : "dead";
            if (body.TryGetNetBody(out NetBody nb))
                return "bodyNet=" + nb.netId.id + " body=" + local + " " + alive;
            return "bodyNet=none body=" + local + " " + alive;
        }
        catch (Exception)
        {
            return "body=error";
        }
    }
}

[HarmonyPatch(typeof(Item), "Update")]
static class ItemUpdateInatorCollect
{
    static void Postfix(Item __instance)
    {
        ItemUpdateInator.Note(__instance);
    }
}

[HarmonyPatch(typeof(ItemSync), "FixedUpdate")]
static class ItemUpdateInatorTick
{
    static void Postfix()
    {
        ItemUpdateInator.Tick();
    }
}

[HarmonyPatch(typeof(SyncInfo), "OnConstructed")]
static class ItemUpdateInatorSyncCreate
{
    static void Postfix(SyncInfo __instance)
    {
        ItemUpdateInator.SyncCreate(__instance);
    }
}

[HarmonyPatch(typeof(SyncInfo), "OnDestroy")]
static class ItemUpdateInatorSyncDestroy
{
    static void Prefix(SyncInfo __instance)
    {
        ItemUpdateInator.SyncDestroy(__instance);
    }
}

[HarmonyPatch(typeof(SyncBody), "OnConstructed")]
static class ItemUpdateInatorBodyCreate
{
    static void Postfix(SyncBody __instance)
    {
        ItemUpdateInator.BodyCreate(__instance);
    }
}

[HarmonyPatch(typeof(SyncBody), "OnDestroy")]
static class ItemUpdateInatorBodyDestroy
{
    static void Prefix(SyncBody __instance)
    {
        ItemUpdateInator.BodyDestroy(__instance);
    }
}

[HarmonyPatch(typeof(BasePlayer), "OnConstructed")]
static class ItemUpdateInatorPlayerCreate
{
    static void Postfix(BasePlayer __instance)
    {
        ItemUpdateInator.PlayerCreate(__instance);
    }
}

[HarmonyPatch(typeof(BasePlayer), "OnDestroy")]
static class ItemUpdateInatorPlayerDestroy
{
    static void Prefix(BasePlayer __instance)
    {
        ItemUpdateInator.PlayerDestroy(__instance);
    }
}
