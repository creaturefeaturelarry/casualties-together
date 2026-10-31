using System.Collections.Generic;
using System.Globalization;
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// per frame position of a throw or a drop, on every machine that has the copy.
// delete this file to remove it. lines are [evidencebag].
internal static class FlightEvidence {
    private const float Window = 8f;
    private const int MaxWatches = 8;
    private const float HaltStep = 0.02f;
    private const float MoveStep = 0.05f;
    private const float HaltSpeed = 0.4f;

    private sealed class Watch {
        internal int Id;
        internal Item Item;
        internal string How;
        internal float Until;
        internal int N;
        internal bool Have;
        internal Vector2 Prev;
        internal Vector2 PktPrev;
        internal bool HavePkt;
        internal float PrevFixed;
        internal Vector2 Start;
        internal float Path;
        internal float PktPath;
        internal float MaxStep;
        internal int SimOff;
        internal int Sleeps;
        internal int Same;
        internal int FrozenRun;
        internal int FrozenBest;
        internal bool Halted;
    }

    private static readonly List<Watch> Live = new(MaxWatches);
    private static readonly Dictionary<int, bool> SeenLoose = new();

    internal static void Arm(Item item, string how) {
        if (!Net.IsRunning || item == null) {
            return;
        }
        int id = item.GetInstanceID();
        for (int i = 0; i < Live.Count; i++) {
            if (Live[i].Id == id) {
                return;
            }
        }
        if (Live.Count >= MaxWatches) {
            Line("full how=" + how + " " + Who(item));
            return;
        }
        Watch watch = new();
        watch.Id = id;
        watch.Item = item;
        watch.How = how;
        watch.Until = Time.unscaledTime + Window;
        Live.Add(watch);
        Line("arm side=" + Side() + " how=" + how + " " + Who(item) + " " + Place(item));
    }

    internal static void Tick() {
        if (Live.Count == 0) {
            return;
        }
        float now = Time.unscaledTime;
        for (int i = Live.Count - 1; i >= 0; i--) {
            Watch watch = Live[i];
            if (watch.Item == null) {
                Live.RemoveAt(i);
                Line("stop side=" + Side() + " how=" + watch.How + " why=dead n=" + watch.N);
                continue;
            }
            bool held = watch.Item.transform.parent != null;
            Sample(watch);
            if (held && watch.N > 1) {
                Live.RemoveAt(i);
                Line("stop " + Summary(watch, "held"));
                continue;
            }
            if (now >= watch.Until) {
                Live.RemoveAt(i);
                Line("stop " + Summary(watch, "window"));
            }
        }
    }

    // the frame a copy leaves its parent. covers a throw or drop this machine did not run itself.
    internal static void NoteLoose(Item item) {
        if (item == null) {
            return;
        }
        int id = item.GetInstanceID();
        bool loose = item.transform.parent == null;
        if (!SeenLoose.TryGetValue(id, out bool was)) {
            if (SeenLoose.Count > 4000) {
                SeenLoose.Clear();
            }
            SeenLoose[id] = loose;
            return;
        }
        SeenLoose[id] = loose;
        if (!was && loose) {
            Arm(item, "loose");
        }
    }

    private static void Sample(Watch watch) {
        Item item = watch.Item;
        Transform trans = item.transform;
        Vector2 pos = trans.position;
        float fixedNow = Time.fixedTime;
        bool phys = !watch.Have || fixedNow != watch.PrevFixed;
        float step = 0f;
        if (watch.Have) {
            step = Vector2.Distance(pos, watch.Prev);
        }
        if (!watch.Have) {
            watch.Start = pos;
        }
        watch.Have = true;
        watch.Prev = pos;
        watch.PrevFixed = fixedNow;
        watch.N++;
        watch.Path += step;
        if (step > watch.MaxStep) {
            watch.MaxStep = step;
        }

        Rigidbody2D rb = item.rb;
        bool sim = rb != null && rb.simulated;
        bool sleep = rb != null && rb.IsSleeping();
        float speed = rb != null ? rb.velocity.magnitude : 0f;
        if (!sim) {
            watch.SimOff++;
        }
        if (sleep) {
            watch.Sleeps++;
        }

        string chunk = "na";
        if (TryChunk(pos, out int dx, out int dy)) {
            chunk = dx + "," + dy;
            if (dx == 0 && dy == 0) {
                watch.Same++;
            }
        }

        if (phys && step < HaltStep && speed > HaltSpeed && sim) {
            watch.FrozenRun++;
            if (watch.FrozenRun > watch.FrozenBest) {
                watch.FrozenBest = watch.FrozenRun;
            }
            if (watch.FrozenRun == 3 && !watch.Halted) {
                watch.Halted = true;
                Line("halt side=" + Side() + " how=" + watch.How + " " + Who(item) + " " + Place(item) + " n=" + watch.N);
            }
        } else if (phys && step > MoveStep) {
            watch.FrozenRun = 0;
            watch.Halted = false;
        }

        string pkt = "na";
        string pvel = "na";
        int ignore = 0;
        byte data = 255;
        ushort cnet = 0;
        if (item.TryGetComponent(out SyncInfoGameObjectTracker tracker) && tracker.syncInfo != null) {
            SyncInfo si = tracker.syncInfo;
            data = si.container_data1.Value;
            cnet = si.container_netId.Value.id;
            Vector2 raw = si._position.Value;
            Vector2 pv = si._velocity.Value;
            pkt = P(raw);
            pvel = P(pv);
            ignore = si.IsIgnored() ? 1 : 0;
            if (watch.HavePkt) {
                watch.PktPath += Vector2.Distance(raw, watch.PktPrev);
            }
            watch.PktPrev = raw;
            watch.HavePkt = true;
        }

        string body = "none";
        string vel = "0.00,0.00";
        string ang = F(trans.eulerAngles.z);
        string avel = "0.00";
        if (rb != null) {
            body = rb.bodyType.ToString();
            vel = P(rb.velocity);
            avel = F(rb.angularVelocity);
        }

        bool simrange = false;
        bool in3 = false;
        try {
            if (WorldGeneration.world != null) {
                simrange = SharedMain.WorldInSimRange(pos);
                in3 = SharedMain.PositionIsInSimulatedChunk(pos);
            }
        } catch (System.Exception) {
        }

        Line("side=" + Side()
            + " how=" + watch.How
            + " " + Who(item)
            + " n=" + watch.N
            + " age=" + F(Window - (watch.Until - Time.unscaledTime))
            + " dt=" + F(Time.unscaledDeltaTime)
            + " phys=" + (phys ? 1 : 0)
            + " pos=" + P(pos)
            + " d=" + F(step)
            + " vel=" + vel
            + " ang=" + ang
            + " avel=" + avel
            + " sim=" + (sim ? 1 : 0)
            + " sleep=" + (sleep ? 1 : 0)
            + " rb=" + body
            + " parent=" + Parent(trans)
            + " byte=" + data
            + " cnet=" + cnet
            + " pkt=" + pkt
            + " pvel=" + pvel
            + " dist=" + Dist(pos)
            + " chunk=" + chunk
            + " simrange=" + (simrange ? 1 : 0)
            + " in3x3=" + (in3 ? 1 : 0)
            + " act=" + (item.gameObject.activeInHierarchy ? 1 : 0)
            + " en=" + (item.enabled ? 1 : 0)
            + " ignore=" + ignore);
    }

    private static string Summary(Watch watch, string why) {
        Vector2 end = watch.Item != null ? (Vector2)watch.Item.transform.position : watch.Prev;
        return "side=" + Side()
            + " how=" + watch.How
            + " why=" + why
            + " " + Who(watch.Item)
            + " n=" + watch.N
            + " path=" + F(watch.Path)
            + " pktpath=" + F(watch.PktPath)
            + " maxstep=" + F(watch.MaxStep)
            + " frozen=" + watch.FrozenBest
            + " simoff=" + watch.SimOff
            + " sleep=" + watch.Sleeps
            + " same=" + watch.Same
            + " start=" + P(watch.Start)
            + " end=" + P(end);
    }

    private static string Who(Item item) {
        if (item == null) {
            return "sync=0 item=null";
        }
        ushort sync = 0;
        if (item.TryGetComponent(out SyncInfoGameObjectTracker tracker) && tracker.syncInfo != null) {
            sync = tracker.syncInfo.syncId.id;
        }
        string id = string.IsNullOrEmpty(item.id) ? item.name : item.id;
        return "sync=" + sync + " item=" + id;
    }

    private static string Place(Item item) {
        if (item == null) {
            return "pos=0.00,0.00";
        }
        return "pos=" + P(item.transform.position) + " parent=" + Parent(item.transform);
    }

    private static string Parent(Transform trans) {
        if (trans == null || trans.parent == null) {
            return "none";
        }
        string name = trans.parent.name;
        if (string.IsNullOrEmpty(name)) {
            return "held";
        }
        return name.Replace(' ', '_');
    }

    private static string Dist(Vector2 pos) {
        if (!Util.TryGetLocalBody(out Body body) || body == null) {
            return "na";
        }
        return F(Vector2.Distance(pos, body.transform.position));
    }

    // dx,dy are this item's chunk minus the local body chunk. 0,0 is the same chunk.
    private static bool TryChunk(Vector2 pos, out int dx, out int dy) {
        dx = 0;
        dy = 0;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !Util.TryGetLocalBody(out Body body) || body == null) {
            return false;
        }
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        Vector2Int mine = world.BlockToChunkPos(world.WorldToBlockPos(body.transform.position));
        dx = chunk.x - mine.x;
        dy = chunk.y - mine.y;
        return true;
    }

    private static string Side() {
        return Net.IsServer ? "host" : "client";
    }

    private static string F(float v) {
        return v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string P(Vector2 v) {
        return F(v.x) + "," + F(v.y);
    }

    private static void Line(string message) {
        if (!log.verbose) return;
        try {
            Plugin.log.LogInfo("[evidencebag] " + message);
        } catch (System.Exception) {
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.ThrowItem))]
internal static class EvidenceBagThrow {
    private static Item _item;

    private static void Prefix(Body __instance) {
        _item = null;
        try {
            if (__instance != null) {
                _item = __instance.GetItem(__instance.handSlot);
            }
        } catch (System.Exception) {
        }
    }

    private static void Postfix() {
        Item item = _item;
        _item = null;
        if (item != null && item.transform.parent == null) {
            FlightEvidence.Arm(item, "throw");
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DropItem), typeof(Item))]
internal static class EvidenceBagDrop {
    private static void Postfix(Item item) {
        if (item != null && item.transform.parent == null) {
            FlightEvidence.Arm(item, "drop");
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DropItem), typeof(int))]
internal static class EvidenceBagDropSlot {
    private static void Prefix(Body __instance, int slot, out Item __state) {
        __state = null;
        try {
            if (__instance != null) {
                __state = __instance.GetItem(slot);
            }
        } catch (System.Exception) {
        }
    }

    private static void Postfix(Item __state) {
        if (__state != null && __state.transform.parent == null) {
            FlightEvidence.Arm(__state, "drop");
        }
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
internal static class EvidenceBagUnload {
    private static void Postfix(Item item) {
        if (item != null && item.transform.parent == null) {
            FlightEvidence.Arm(item, "unload");
        }
    }
}

[HarmonyPatch(typeof(SyncInfo), "UpdateItemContainer")]
internal static class EvidenceBagLoose {
    private static void Postfix(SyncInfo __instance) {
        if (__instance != null) {
            FlightEvidence.NoteLoose(__instance.item);
        }
    }
}

[HarmonyPatch(typeof(Plugin), "LateUpdate")]
internal static class EvidenceBagTick {
    private static void Postfix() {
        FlightEvidence.Tick();
    }
}
