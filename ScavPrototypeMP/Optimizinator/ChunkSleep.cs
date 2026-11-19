using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

// turns off items and buildings that sit outside the 3x3.
// colliders go with the object, so the list and the chunk cache are ours.
public static class ChunkSleep {
    const int Batch = 64;
    // 9.4 ms was one update deactivating the whole shown list. the list length is not fixed here.
    // 32 setactive calls a frame is a slice of that pass, so a few hundred ground items still wake in well under a second.
    const int WakeBudget = 32;
    const float ColdFor = 1f;
    // a chunk that was neither hot nor shown is looked at again after this long, plus a per chunk spread so they do not all fall due in one frame.
    // the lag is the longest an object in a far chunk waits to wake once its chunk turns hot or shown.
    const float FarRecheckSeconds = 0.1f;
    const float FarRecheckSpreadSeconds = 0.1f;
    static int _wakeLeft;

    sealed class Entry {
        public int Id;
        public GameObject Go;
        public bool Placed;
        public long Chunk;
        public float ColdSince = -1f;
        public bool Asleep;
        // true once a pass saw this object awake. WakeFreshChunks skips a chunk whose objects are all settled.
        public bool Settled;
        public Slot Slot;
    }

    // one occupied chunk. Unsettled counts the placed objects that still need a wake try.
    sealed class Slot {
        public readonly List<Entry> List = new List<Entry>(4);
        public int Unsettled;
        // true when the last check found the chunk hot or shown. such a chunk is checked every frame.
        public bool Near;
        public float NextCheck;
        public WorldGeneration World;
        public Vector2 Center;
        public TilemapRenderer Renderer;
    }

    static readonly List<Entry> Entries = new List<Entry>();
    static readonly Dictionary<int, Entry> ById = new Dictionary<int, Entry>();
    static readonly Dictionary<long, Slot> ByChunk = new Dictionary<long, Slot>();
    static readonly HashSet<Type> IgnoredTypes = new HashSet<Type>();
    static readonly HashSet<int> IgnoredIds = new HashSet<int>();
    static readonly HashSet<long> HotNow = new HashSet<long>();
    static readonly HashSet<long> HotPrev = new HashSet<long>();
    static int _cursor;

    // every object with this component stays on, even past the 3x3.
    public static void Ignore(Type componentType) {
        if (componentType != null)
            IgnoredTypes.Add(componentType);
    }

    // this one object stays on.
    public static void Ignore(GameObject go) {
        if (go != null)
            IgnoredIds.Add(go.GetInstanceID());
    }

    internal static void Watch(Component body) {
        if (body == null)
            return;
        GameObject go = body.gameObject;
        if (go == null)
            return;
        int id = go.GetInstanceID();
        if (ById.ContainsKey(id))
            return;
        if (go.GetComponent<ChunkSleepAnchor>() == null)
            go.AddComponent<ChunkSleepAnchor>();
        Entry entry = new Entry { Id = id, Go = go };
        Entries.Add(entry);
        ById[id] = entry;
    }

    internal static void Drop(GameObject go) {
        if (go == null)
            return;
        int id = go.GetInstanceID();
        if (!ById.TryGetValue(id, out Entry entry))
            return;
        ById.Remove(id);
        IgnoredIds.Remove(id);
        Unchunk(entry);
        int index = Entries.IndexOf(entry);
        if (index < 0)
            return;
        int last = Entries.Count - 1;
        Entries[index] = Entries[last];
        Entries.RemoveAt(last);
        if (_cursor > Entries.Count)
            _cursor = 0;
        else if (index < _cursor)
            _cursor--;
    }

    internal static void Tick() {
        if (!SharedMain.LocalWorldIsGenerated || WorldGeneration.world == null || !WorldGeneration.world.worldExists)
            return;
        if (TestOn())
            Report();
        if (Entries.Count == 0)
            return;
        // stock turns the tilemap renderer on from the camera, and only after the camera
        // has moved half a chunk. waking before that runs Update against a renderer that is still off.
        EnsureRenderers();
        _wakeLeft = WakeBudget;
        WakeFreshChunks();
        int steps = Entries.Count < Batch ? Entries.Count : Batch;
        for (int s = 0; s < steps; s++) {
            if (Entries.Count == 0)
                return;
            if (_cursor >= Entries.Count)
                _cursor = 0;
            int index = _cursor;
            Entry entry = Entries[index];
            if (entry.Go == null) {
                DropAt(index);
                continue;
            }
            _cursor = index + 1;
            Visit(entry);
        }
    }

    static void Visit(Entry entry) {
        if (Ignored(entry)) {
            entry.ColdSince = -1f;
            Wake(entry);
            return;
        }
        Vector2 pos = entry.Go.transform.position;
        long key = ChunkStamp.Pack(ChunkStamp.ChunkOf(pos));
        Place(entry, key);
        // something else switched it off. the chunk has to try waking it again.
        if (entry.Settled && !entry.Go.activeSelf)
            MarkSettled(entry, false);
        // tiles already drawing. do not count a cold second and turn it back off.
        if (Hot(pos) || Shown(pos)) {
            entry.ColdSince = -1f;
            Wake(entry);
            return;
        }
        float now = Time.unscaledTime;
        if (entry.ColdSince < 0f)
            entry.ColdSince = now;
        else if (!entry.Asleep && now - entry.ColdSince >= ColdFor)
            Sleep(entry);
    }

    // host keeps every player's 3x3. everyone else only their own, so a far mesh can be off here and still live on the host.
    static bool Hot(Vector2 pos) {
        // a kept throw holds its 3x3 on every machine. the host stamp alone does not cover a client.
        if (SimKeep.Covers(pos))
            return true;
        if (Net.IsRunning && Net.IsServer)
            return SharedMain.PositionIsInSimulatedChunk(pos);
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !LocalChunk(out Vector2Int mine))
            return true;
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int dx = chunk.x - mine.x;
        int dy = chunk.y - mine.y;
        if (dx < 0)
            dx = -dx;
        if (dy < 0)
            dy = -dy;
        return dx <= 1 && dy <= 1;
    }

    static bool LocalChunk(out Vector2Int chunk) {
        chunk = default;
        WorldGeneration world = WorldGeneration.world;
        if (world == null)
            return false;
        Vector2 pos;
        if (Util.TryGetLocalBody(out Body body) && body != null)
            pos = body.transform.position;
        else if (Camera.main != null)
            pos = Camera.main.transform.position;
        else
            return false;
        chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        return true;
    }

    static bool Ignored(Entry entry) {
        if (IgnoredIds.Contains(entry.Id))
            return true;
        if (IgnoredTypes.Count == 0)
            return false;
        GameObject go = entry.Go;
        foreach (Type type in IgnoredTypes) {
            if (go.GetComponentInChildren(type, true) != null)
                return true;
        }
        return false;
    }

    static void Sleep(Entry entry) {
        if (entry.Asleep || entry.Go == null)
            return;
        entry.Go.SetActive(false);
        entry.Asleep = true;
        MarkSettled(entry, false);
    }

    static void Wake(Entry entry) {
        if (entry.Go == null)
            return;
        // closed bag rows stay off. waking them here is what bag sleep turns off again the same update.
        if (BagSleep.LeaveAsleep(entry.Go))
            return;
        if (!entry.Asleep && entry.Go.activeSelf) {
            MarkSettled(entry, true);
            return;
        }
        if (!RendererReady(entry.Go.transform.position))
            return;
        if (!entry.Go.activeSelf) {
            if (_wakeLeft <= 0)
                return;
            _wakeLeft--;
            entry.Go.SetActive(true);
        }
        entry.Asleep = false;
        entry.ColdSince = -1f;
        MarkSettled(entry, true);
    }

    static void MarkSettled(Entry entry, bool settled) {
        if (entry.Settled == settled)
            return;
        entry.Settled = settled;
        if (entry.Slot != null)
            entry.Slot.Unsettled += settled ? -1 : 1;
    }

    // stock enables the camera window. a union chunk wakes even when that window leaves it dark.
    static void EnsureRenderers() {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld || !world.worldExists || world.mainCam == null)
            return;
        world.UpdateChunkVisibility();
    }

    static bool RendererReady(Vector2 pos) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists)
            return true;
        TilemapRenderer renderer = world.GetClosestChunkRenderer(world.WorldToBlockPos(pos));
        if (renderer != null && renderer.enabled)
            return true;
        // stock leaves the ring three chunks out unrendered. a player standing there is still in the union, so the props have to wake or the scan never sees them.
        if (Net.IsRunning && Net.IsServer && SharedMain.PositionIsInSimulatedChunk(pos))
            return true;
        return false;
    }

    // a shown chunk wakes every pass. the first hot frame used to be the only try, and a renderer that was still off then left the plants asleep.
    // a chunk whose objects are all awake has nothing to wake, so the scan only pays for chunks with an unsettled object.
    // a chunk that is not near is only checked when its timer is due, so the hot and shown lookups run for the few chunks around the players.
    static void WakeFreshChunks() {
        HotNow.Clear();
        WorldGeneration world = WorldGeneration.world;
        float now = Time.unscaledTime;
        foreach (KeyValuePair<long, Slot> pair in ByChunk) {
            Slot slot = pair.Value;
            if (slot.Unsettled <= 0)
                continue;
            if (!slot.Near && now < slot.NextCheck)
                continue;
            long key = pair.Key;
            bool hot = ChunkIsHot(world, slot, key);
            bool shown = ChunkShown(world, slot, key);
            slot.Near = hot || shown;
            if (!slot.Near) {
                slot.NextCheck = now + FarRecheckSeconds + FarRecheckSpreadSeconds * SpreadOf(key);
                continue;
            }
            if (hot)
                HotNow.Add(key);
            if (shown || !HotPrev.Contains(key))
                WakeSlot(slot);
        }
        HotPrev.Clear();
        foreach (long key in HotNow)
            HotPrev.Add(key);
    }

    // a fixed fraction in [0, 1) for a chunk key.
    static float SpreadOf(long key) {
        ulong mixed = (ulong)key * 2654435761UL;
        return (int)((mixed >> 16) & 0xFFFFUL) / 65536f;
    }

    static void WakeSlot(Slot slot) {
        List<Entry> list = slot.List;
        for (int i = 0; i < list.Count; i++) {
            Entry entry = list[i];
            entry.ColdSince = -1f;
            Wake(entry);
        }
    }

    static bool ChunkIsHot(WorldGeneration world, Slot slot, long key) {
        if (world == null)
            return true;
        if (WorldGeneration.CHUNKSIZE < 1)
            return true;
        FillSlot(world, slot, key);
        return Hot(slot.Center);
    }

    // the tilemap the player can already see. this is not the hot flag.
    static bool ChunkShown(WorldGeneration world, Slot slot, long key) {
        if (world == null || !world.worldExists)
            return false;
        FillSlot(world, slot, key);
        return slot.Renderer != null && slot.Renderer.enabled;
    }

    // the centre and the renderer of a chunk do not change while the world lives, so the lookup runs once per world.
    static void FillSlot(WorldGeneration world, Slot slot, long key) {
        if (slot.World != world) {
            slot.World = world;
            slot.Center = CenterOf(key);
            slot.Renderer = null;
        }
        if (slot.Renderer == null)
            slot.Renderer = world.GetClosestChunkRenderer(world.WorldToBlockPos(slot.Center));
    }

    static bool Shown(Vector2 pos) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists)
            return false;
        TilemapRenderer renderer = world.GetClosestChunkRenderer(world.WorldToBlockPos(pos));
        return renderer != null && renderer.enabled;
    }

    static Vector2 CenterOf(long key) {
        WorldGeneration world = WorldGeneration.world;
        int size = WorldGeneration.CHUNKSIZE;
        if (world == null || size < 1)
            return default;
        int x = (int)(key >> 32);
        int y = (int)key;
        int bx = x * size + size / 2;
        int by = y * size + size / 2;
        return new Vector2(bx - (float)world.halfWidth, by - (float)world.halfHeight);
    }

    static void Place(Entry entry, long key) {
        if (entry.Placed && entry.Chunk == key)
            return;
        Unchunk(entry);
        entry.Placed = true;
        entry.Chunk = key;
        if (!ByChunk.TryGetValue(key, out Slot slot))
            ByChunk[key] = slot = new Slot();
        slot.List.Add(entry);
        entry.Slot = slot;
        if (!entry.Settled)
            slot.Unsettled++;
    }

    static void Unchunk(Entry entry) {
        if (!entry.Placed)
            return;
        Slot slot = entry.Slot;
        if (slot != null) {
            slot.List.Remove(entry);
            if (!entry.Settled)
                slot.Unsettled--;
            if (slot.List.Count == 0)
                ByChunk.Remove(entry.Chunk);
            entry.Slot = null;
        }
        entry.Placed = false;
    }

    static void DropAt(int index) {
        Entry entry = Entries[index];
        ById.Remove(entry.Id);
        Unchunk(entry);
        int last = Entries.Count - 1;
        Entries[index] = Entries[last];
        Entries.RemoveAt(last);
        if (_cursor > Entries.Count)
            _cursor = 0;
    }

    // temporary. CASU_SLEEPTEST=1 flies out and back and writes sleep-test-<pid>.txt
    static bool TestOn() {
        string v = Environment.GetEnvironmentVariable("CASU_SLEEPTEST");
        return v == "1";
    }

    static float _reportAt;
    static int _fly;
    static float _flyAt;
    static float _sawAt;
    static bool _sawBody;
    static bool _runAsked;
    static int _awayAsleep;
    static int _homeAsleep;
    static int _awayBad;

    internal static void LateFly() {
        if (!TestOn())
            return;
        MaybeStartRun();
        if (!SharedMain.LocalWorldIsGenerated)
            return;
        if (!Util.TryGetLocalBody(out Body body) || body == null)
            return;
        float now = Time.unscaledTime;
        if (!_sawBody) {
            _sawBody = true;
            _sawAt = now;
            _flyAt = now;
        }
        if (_fly == 0) {
            if (now - _sawAt < 12f)
                return;
            _fly = 1;
            _flyAt = now;
            Note("fly away");
        }
        float dur = now - _flyAt;
        if (_fly == 1) {
            Nudge(body, Vector2.right);
            if (dur > 5f) {
                _fly = 2;
                _flyAt = now;
            }
        } else if (_fly == 2) {
            Nudge(body, Vector2.zero);
            if (dur > 4f) {
                Count(out _awayAsleep, out _awayBad, out int n, out int badHot, out int badCold, out int ignSleep);
                _fly = 3;
                _flyAt = now;
                Note("away n=" + n + " asleep=" + _awayAsleep + " bad=" + _awayBad + " hot=" + badHot + " cold=" + badCold + " ignSleep=" + ignSleep + " pos=" + (Vector2)body.transform.position);
            }
        } else if (_fly == 3) {
            Nudge(body, Vector2.left);
            if (dur > 5f) {
                _fly = 4;
                _flyAt = now;
            }
        } else if (_fly == 4) {
            Nudge(body, Vector2.zero);
            if (dur > 4f) {
                Count(out _homeAsleep, out int bad, out int n, out int badHot, out int badCold, out int ignSleep);
                _fly = 5;
                bool ok = n > 20 && _awayAsleep > 10 && _awayBad == 0 && bad == 0 && ignSleep == 0 && _homeAsleep < _awayAsleep;
                Note("home n=" + n + " asleep=" + _homeAsleep + " bad=" + bad + " hot=" + badHot + " cold=" + badCold + " ignSleep=" + ignSleep + " " + (ok ? "SLEEP_OK" : "SLEEP_BAD"));
            }
        } else {
            Nudge(body, Vector2.zero);
        }
    }

    static void MaybeStartRun() {
        if (_runAsked || !Net.IsServer)
            return;
        string role = Environment.GetEnvironmentVariable("CASU_ROLE") ?? "";
        if (role != "host")
            return;
        if (SharedMain.LocalWorldIsGenerated) {
            _runAsked = true;
            return;
        }
        if (ServerMain.AllClientIdsExceptHost.Count < 1)
            return;
        if (PreRunScript.instance == null)
            return;
        _runAsked = true;
        Note("start run, client is in");
        PreRunScript.instance.StartRun();
    }

    static void Nudge(Body body, Vector2 dir) {
        ConsoleScript con = ConsoleScript.instance;
        if (con == null)
            return;
        con.noClip = true;
        if (body.rb != null)
            body.rb.simulated = false;
        if (dir.sqrMagnitude > 0f)
            body.transform.position += (Vector3)(dir * Time.unscaledDeltaTime * 100f);
        con.noClipPos = body.transform.position;
        body.moveDir = Vector2.zero;
    }

    static void Report() {
        float now = Time.unscaledTime;
        if (now < _reportAt)
            return;
        _reportAt = now + 2f;
        Count(out int asleep, out int bad, out int n, out int badHot, out int badCold, out int ignSleep);
        string side = !Net.IsRunning ? "off" : (Net.IsServer ? "host" : "client");
        Note(side + " n=" + n + " asleep=" + asleep + " bad=" + bad + " hot=" + badHot + " cold=" + badCold + " ignSleep=" + ignSleep + " fly=" + _fly);
    }

    static void Count(out int asleep, out int bad, out int n, out int badHot, out int badCold, out int ignSleep) {
        asleep = 0;
        bad = 0;
        n = 0;
        badHot = 0;
        badCold = 0;
        ignSleep = 0;
        float now = Time.unscaledTime;
        for (int i = 0; i < Entries.Count; i++) {
            Entry entry = Entries[i];
            if (entry.Go == null)
                continue;
            n++;
            bool ign = Ignored(entry);
            if (entry.Asleep)
                asleep++;
            if (ign && entry.Asleep)
                ignSleep++;
            bool hot = Hot(entry.Go.transform.position);
            if (entry.Asleep && hot)
                badHot++;
            if (!entry.Asleep && !ign && entry.ColdSince >= 0f && now - entry.ColdSince > 1.5f)
                badCold++;
        }
        bad = badHot + badCold + ignSleep;
    }

    static void Note(string line) {
        if (!Together.log.verbose) return;
        string text = DateTime.UtcNow.ToString("HH:mm:ss") + " " + line;
        try { Plugin.log.LogInfo("[sleep] " + line); } catch { }
        try {
            string dir = Path.GetDirectoryName(typeof(ChunkSleep).Assembly.Location);
            string path = Path.Combine(dir, "sleep-test-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".txt");
            File.AppendAllText(path, text + Environment.NewLine);
        } catch { }
    }
}

// destroyed with the object. disable does not remove it, or a slept mesh would be forgotten.
sealed class ChunkSleepAnchor : MonoBehaviour {
    void OnDestroy() {
        ChunkSleep.Drop(gameObject);
    }
}