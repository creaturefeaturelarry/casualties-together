using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Together;

// a player leaving takes their gear out of the session.
// saved meshes stay on SAVESTATE_ with the sync record destroyed.
// everything else that still names that body, or a bag that names it, loses the sync record
// before the body destroy can keep the bag alive. clients do not build a mesh while the owner is gone.
internal static class LeaveGear {
    static readonly HashSet<int> BlockOnce = new HashSet<int>();
    static readonly HashSet<int> LeakOnce = new HashSet<int>();
    static readonly List<SyncInfo> Scratch = new List<SyncInfo>(64);
    static readonly HashSet<SyncInfo> Kill = new HashSet<SyncInfo>();
    static readonly HashSet<ushort> Ids = new HashSet<ushort>();

    static int _blocks;
    static int _leaks;
    static int _released;
    static int _dead;
    static int _loose;
    static string _releaseLine;

    // worn, in a slot, or in a bag, and that owner is not on this machine.
    internal static bool OwnerMissing(SyncInfo si) {
        if (si == null || si.IsDestroyed) {
            return false;
        }

        byte data;
        try {
            data = si.container_data1.Value;
        } catch {
            return false;
        }

        if (data == 0) {
            return false;
        }

        if (data >= 100) {
            return !ContainerLink.TryBody(si, out _);
        }

        if (data == 1) {
            // a version miss is a stale stamp on a live bag, not a bag that left.
            // requiring the match here meant the owner never built the meshes.
            return !ContainerLink.BagAlive(si);
        }

        return false;
    }

    // host. saved gear was already detached. this catches whatever that walk missed,
    // and the case where nothing is saved and the mesh is about to die with the body.
    internal static void Release(ScavPlayer leaving) {
        _releaseLine = null;
        if (!Net.IsServer || leaving == null || leaving.body == null) {
            return;
        }

        Body body = leaving.body;
        ushort net = BodyNet(body);
        if (net == 0) {
            _releaseLine = "release side=host player=" + Name(leaving)
                + " net=0 reason=no-body";
            return;
        }

        Scratch.Clear();
        Scratch.AddRange(NetObjectRegistry.NetIdToSyncInfoDict.Values);
        Kill.Clear();
        Ids.Clear();
        Ids.Add(net);

        bool grew = true;
        int guard = 0;
        while (grew && guard++ < 8) {
            grew = false;
            for (int i = 0; i < Scratch.Count; i++) {
                SyncInfo si = Scratch[i];
                if (si == null || si.IsDestroyed || Kill.Contains(si)) {
                    continue;
                }

                if (!Names(si, Ids) && !OnBody(si, body)) {
                    continue;
                }

                Kill.Add(si);
                grew = true;
                try {
                    ushort id = si.syncId.id;
                    if (id != 0) {
                        Ids.Add(id);
                    }
                } catch {
                    // a half-dead record still needs to leave the set
                }
            }
        }

        int parked = 0;
        int destroyed = 0;
        int failed = 0;
        var sample = new StringBuilder();
        foreach (SyncInfo si in Kill) {
            AppendSample(sample, si);
            try {
                if (si.go != null && OnSaveState(si.go)) {
                    NetObjectRegistry.DetachSyncKeepMesh(si.go);
                    parked++;
                } else {
                    Sever(si);
                    if (!si.IsDestroyed) {
                        si.Destroy();
                    }
                    destroyed++;
                }
            } catch {
                failed++;
            }
        }

        _released += Kill.Count;
        _releaseLine = "release side=host player=" + Name(leaving)
            + " net=" + net
            + " save=" + (Multiplayer.rules.SavePlayerInventory ? 1 : 0)
            + " kill=" + Kill.Count
            + " parked=" + parked
            + " destroyed=" + destroyed
            + " failed=" + failed
            + " sample=" + (sample.Length > 0 ? sample.ToString() : "-");
        Kill.Clear();
        Scratch.Clear();
        Ids.Clear();
    }

    internal static string TakeReleaseLine() {
        string line = _releaseLine;
        _releaseLine = null;
        return line;
    }

    internal static int OrphanCount() {
        int count = 0;
        foreach (SyncInfo si in NetObjectRegistry.NetIdToSyncInfoDict.Values) {
            if (OwnerMissing(si)) {
                count++;
            }
        }
        return count;
    }

    internal static void MarkBlock(SyncInfo si) {
        _blocks++;
        int id = SyncKey(si);
        if (id == 0 || !BlockOnce.Add(id)) {
            return;
        }
        if (BlockOnce.Count > 512) {
            BlockOnce.Clear();
        }
        FindTheBugReports.Say("leavegear", "block " + Describe(si));
    }

    // CreateObject on a sync the host already destroyed. the pending list was still holding it.
    internal static void MarkDeadSpawn(SyncInfo si) {
        _dead++;
        int id = SyncKey(si);
        if (id == 0 || !LeakOnce.Add(id ^ 0x40000000)) {
            return;
        }
        FindTheBugReports.Say("leavegear", "deadspawn " + Describe(si));
    }

    // pending wanted a mesh for a loose byte. seating is over, so the optimizer must not build one.
    internal static void MarkLooseSkip(SyncInfo si) {
        _loose++;
        int id = SyncKey(si);
        if (id == 0 || !BlockOnce.Add(id ^ 0x20000000)) {
            return;
        }
        FindTheBugReports.Say("leavegear", "looseskip " + Describe(si));
    }

    internal static void MarkLeak(SyncInfo si) {
        _leaks++;
        int id = SyncKey(si);
        if (id != 0 && !LeakOnce.Add(id)) {
            return;
        }
        if (LeakOnce.Count > 512) {
            LeakOnce.Clear();
        }
        FindTheBugReports.Say("leavegear", "LEAK " + Describe(si));
    }

    // one line on the 10s frame report. leaks staying above 0 means a mesh was built with no owner.
    internal static void AppendStatus(StringBuilder sb) {
        if (sb == null) {
            return;
        }
        sb.Append('\n');
        sb.Append("leavegear blocks=");
        sb.Append(_blocks);
        sb.Append(" leaks=");
        sb.Append(_leaks);
        sb.Append(" orphans=");
        sb.Append(OrphanCount());
        sb.Append(" released=");
        sb.Append(_released);
        sb.Append(" deadspawn=");
        sb.Append(_dead);
        sb.Append(" looseskip=");
        sb.Append(_loose);
        _blocks = 0;
        _leaks = 0;
        _released = 0;
        _dead = 0;
        _loose = 0;
    }

    static ushort BodyNet(Body body) {
        try {
            if (body != null && body.TryGetNetBody(out NetBody nb) && nb != null && nb.syncBody != null) {
                return nb.syncBody.Id;
            }
        } catch {
            // the body is already dropping its sync link
        }
        return 0;
    }

    static bool Names(SyncInfo si, HashSet<ushort> ids) {
        try {
            if (si.container_data1.Value == 0) {
                return false;
            }
            ushort id = si.container_netId.Value.id;
            return id != 0 && ids.Contains(id);
        } catch {
            return false;
        }
    }

    static bool OnBody(SyncInfo si, Body body) {
        if (si.go == null || body == null) {
            return false;
        }
        Transform walk = si.go.transform;
        while (walk != null) {
            if (walk.TryGetComponent(out Body found) && found == body) {
                return true;
            }
            walk = walk.parent;
        }
        return false;
    }

    static bool OnSaveState(GameObject go) {
        Transform walk = go.transform.parent;
        while (walk != null) {
            if (walk.name != null && walk.name.StartsWith("SAVESTATE_")) {
                return true;
            }
            walk = walk.parent;
        }
        return false;
    }

    // the mesh stays. the record must not, or the client builds another copy.
    static void Sever(SyncInfo si) {
        GameObject go = si.go;
        if (si.tracker != null) {
            si.tracker.syncInfo = null;
            si.tracker = null;
        }
        if (go != null && go.TryGetComponent(out SyncInfoGameObjectTracker tracker)) {
            tracker.syncInfo = null;
        }
        if (go != null) {
            NetObjectRegistry.SyncRegistry.Remove(go);
        }
        si.go = null;
    }

    static void AppendSample(StringBuilder sample, SyncInfo si) {
        if (sample.Length > 180) {
            return;
        }
        if (sample.Length > 0) {
            sample.Append(',');
        }
        sample.Append(Describe(si));
    }

    static string Describe(SyncInfo si) {
        if (si == null) {
            return "null";
        }
        string res = "-";
        string data = "-";
        string cnet = "-";
        string id = "-";
        string parent = "none";
        try {
            id = si.syncId.id.ToString();
        } catch {
            // ignore
        }
        try {
            res = string.IsNullOrEmpty(si.resourceId.Value) ? "-" : si.resourceId.Value;
        } catch {
            // ignore
        }
        try {
            data = si.container_data1.Value.ToString();
            cnet = si.container_netId.Value.id.ToString();
        } catch {
            // ignore
        }
        if (si.go != null && si.go.transform.parent != null) {
            parent = si.go.transform.parent.name;
        }
        return res + "#" + id + " data=" + data + " cnet=" + cnet + " parent=" + parent
            + " side=" + (Net.IsServer ? "host" : "client");
    }

    static string Name(ScavPlayer leaving) {
        return string.IsNullOrEmpty(leaving.playerName) ? "-" : leaving.playerName;
    }

    static int SyncKey(SyncInfo si) {
        try {
            return si != null ? si.syncId.id : 0;
        } catch {
            return 0;
        }
    }
}
