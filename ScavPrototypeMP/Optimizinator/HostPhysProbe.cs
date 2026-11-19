using System.Collections.Generic;
using UnityEngine;

namespace Together;

// crates and stalactites. host logs the real fall. clients log the pose they apply,
// including a step back up, which is the rubberband.
internal static class HostPhysProbe {
    const float Period = 1f;
    const float Far = 40f;
    const float FarSqr = Far * Far;
    const float Radius = 22f;
    const byte Skip = 1;
    const byte Crate = 2;
    const byte Stalact = 3;
    const byte Glass = 4;
    const byte Loose = 5;

    struct ClientSample {
        internal Vector2 Raw0;
        internal Vector2 Vis0;
        internal Vector2 Raw;
        internal Vector2 Vis;
        internal float HostVy;
        internal float RbVy;
        internal float Up;
        internal float Gap;
        internal float Age;
        internal float RawDt;
        internal int Paste;
        internal int Own;
        internal int RawMoves;
        internal int Notes;
        internal byte Kind;
        internal bool Dyn;
        internal bool Sim;
        internal bool Have;
        internal RigidbodyType2D Body;
        internal string Name;
    }

    struct HostSample {
        internal Vector2 Pos;
        internal float Time;
        internal bool Have;
    }

    static readonly Collider2D[] Hits = new Collider2D[64];
    static readonly Dictionary<int, byte> ClientKinds = new Dictionary<int, byte>(64);
    static readonly Dictionary<int, byte> HostKinds = new Dictionary<int, byte>(64);
    static readonly Dictionary<int, ClientSample> Clients = new Dictionary<int, ClientSample>(32);
    static readonly Dictionary<int, HostSample> Hosts = new Dictionary<int, HostSample>(32);
    static readonly HashSet<int> Seen = new HashSet<int>();
    static readonly List<int> Drop = new List<int>(32);
    static float _next = -1f;
    static float _farUntil;
    static bool _far;
    static int _groundMask = int.MinValue;

    internal static void Tick() {
        float now = Time.unscaledTime;
        if (_next < 0f) {
            _next = now + Period;
        }
        if (now < _next) {
            return;
        }
        _next = now + Period;

        if (!Net.IsServer) {
            FlushClient(now);
            return;
        }

        SampleHost(now);
    }

    internal static void NoteClient(SyncInfo si, Vector2 before, Vector2 applied, Rigidbody2D rb, bool owned, bool pasted) {
        if (si == null || si.go == null || Net.IsServer || !IsFar()) {
            return;
        }

        int id = si.syncId.id;
        byte kind = KindOf(ClientKinds, si.go, id);
        if (kind <= Skip) {
            return;
        }

        float now = Time.unscaledTime;
        Vector2 raw = si._position.Value;
        Clients.TryGetValue(id, out ClientSample sample);
        if (!sample.Have) {
            sample.Raw0 = raw;
            sample.Vis0 = before;
            sample.Have = true;
            sample.Kind = kind;
            sample.Name = si.GetItemID();
        } else if ((raw - sample.Raw).sqrMagnitude > 0.0001f) {
            float dt = now - sample.RawDt;
            if (sample.RawMoves == 0 || dt > 0f) {
                sample.RawDt = now;
            }
            sample.RawMoves++;
        }

        float step = applied.y - sample.Vis.y;
        if (sample.Notes > 0 && step > 0.01f) {
            sample.Up += step;
        }

        sample.Raw = raw;
        sample.Vis = applied;
        sample.HostVy = si._velocity.Value.y;
        sample.Gap = Vector2.Distance(raw, applied);
        sample.Age = (float)(Time.realtimeSinceStartupAsDouble - si.lastPositionUpdateTime);
        sample.Notes++;
        if (pasted) {
            sample.Paste++;
        }
        if (owned) {
            sample.Own++;
        }
        if (rb != null) {
            sample.RbVy = rb.velocity.y;
            sample.Dyn = si.rb_dynamic.Value;
            sample.Sim = rb.simulated;
            sample.Body = rb.bodyType;
        }
        sample.Kind = kind;
        Clients[id] = sample;
    }

    static void FlushClient(float now) {
        if (Plugin.log == null) {
            return;
        }
        if (!IsFar()) {
            Clients.Clear();
            return;
        }

        int logged = 0;
        int moving = 0;
        Drop.Clear();
        foreach (KeyValuePair<int, ClientSample> pair in Clients) {
            ClientSample sample = pair.Value;
            if (!sample.Have || sample.Notes == 0) {
                Drop.Add(pair.Key);
                continue;
            }
            float rawDy = sample.Raw.y - sample.Raw0.y;
            float visDy = sample.Vis.y - sample.Vis0.y;
            bool live = Mathf.Abs(rawDy) > 0.04f || Mathf.Abs(visDy) > 0.04f || sample.Up > 0.02f || sample.HostVy < -0.3f || sample.RbVy < -0.3f;
            if (!live) {
                continue;
            }
            moving++;
            if (sample.Kind == Loose && logged >= 4) {
                continue;
            }
            if (logged >= 8) {
                continue;
            }
            logged++;
            bool slow = rawDy < -0.35f && visDy > rawDy + 0.25f;
            bool rubber = sample.Up > 0.04f;
            string line = "[fall] client id=" + pair.Key
                + " " + KindName(sample.Kind)
                + " " + sample.Name
                + (slow ? " slow" : "")
                + (rubber ? " rubber" : "")
                + " hostDist=" + HostDist(sample.Vis).ToString("0")
                + " rawDy=" + rawDy.ToString("0.00")
                + " visDy=" + visDy.ToString("0.00")
                + " up=" + sample.Up.ToString("0.00")
                + " hostVy=" + sample.HostVy.ToString("0.00")
                + " rbVy=" + sample.RbVy.ToString("0.00")
                + " paste=" + sample.Paste
                + " own=" + sample.Own
                + " rawN=" + sample.RawMoves
                + " gap=" + sample.Gap.ToString("0.00")
                + " age=" + sample.Age.ToString("0.00")
                + " dyn=" + (sample.Dyn ? 1 : 0)
                + " sim=" + (sample.Sim ? 1 : 0)
                + " body=" + sample.Body
                + " at=" + sample.Vis.x.ToString("0") + "," + sample.Vis.y.ToString("0");
            if (slow || rubber) {
                Plugin.log.LogWarning(line);
            } else {
                Plugin.log.LogInfo(line);
            }
        }
        for (int i = 0; i < Drop.Count; i++) {
            Clients.Remove(Drop[i]);
            ClientKinds.Remove(Drop[i]);
        }
        Clients.Clear();
        if (logged == 0) {
            Plugin.log.LogInfo("[fall] client idle far=1 moving=" + moving);
        }
        _ = now;
    }

    static void SampleHost(float now) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists || Plugin.log == null) {
            return;
        }

        Vector2 hostPos = Vector2.zero;
        bool haveHost = false;
        ScavPlayer host = ScavPlayer.LocalPlayer;
        if (host != null && host.body != null) {
            hostPos = host.body.transform.position;
            haveHost = true;
        }

        int farPlayers = 0;
        int logged = 0;
        Seen.Clear();
        Dictionary<Body, ScavPlayer> map = ScavPlayer.BodyToPlayerDict;
        if (map == null) {
            return;
        }

        foreach (KeyValuePair<Body, ScavPlayer> pair in map) {
            ScavPlayer plr = pair.Value;
            Body body = pair.Key;
            if (plr == null || body == null) {
                continue;
            }
            if (haveHost && plr.IsLocal) {
                continue;
            }
            Vector2 at = body.transform.position;
            if (haveHost && (at - hostPos).sqrMagnitude < FarSqr) {
                continue;
            }
            farPlayers++;
            int n = Physics2D.OverlapCircleNonAlloc(at, Radius, Hits, Physics2D.AllLayers);
            for (int i = 0; i < n; i++) {
                Collider2D col = Hits[i];
                if (col == null) {
                    continue;
                }
                Rigidbody2D rb = col.attachedRigidbody;
                if (rb == null || rb.GetComponentInParent<Body>() != null) {
                    continue;
                }
                int key = rb.GetInstanceID();
                byte kind = KindOf(HostKinds, rb.gameObject, key);
                if (kind <= Skip) {
                    continue;
                }
                if (!Seen.Add(key)) {
                    continue;
                }
                if (kind == Loose && rb.bodyType != RigidbodyType2D.Dynamic && rb.velocity.sqrMagnitude < 0.04f) {
                    continue;
                }

                Vector2 pos = rb.position;
                float dy = 0f;
                if (Hosts.TryGetValue(key, out HostSample prev) && prev.Have) {
                    dy = pos.y - prev.Pos.y;
                }
                HostSample next = prev;
                next.Pos = pos;
                next.Time = now;
                next.Have = true;
                Hosts[key] = next;

                bool moving = Mathf.Abs(dy) > 0.04f || rb.velocity.y < -0.3f;
                bool frozen = rb.bodyType != RigidbodyType2D.Dynamic;
                if (!moving && !frozen) {
                    continue;
                }
                if (!moving && logged >= 3) {
                    continue;
                }
                if (logged >= 8) {
                    continue;
                }
                if (kind == Loose && logged >= 4) {
                    continue;
                }
                logged++;
                int sync = 0;
                string name = NameOf(rb.gameObject);
                if (NetObjectRegistry.TryGetSyncInfo(rb.gameObject, out SyncInfo si) && si != null) {
                    sync = si.syncId.id;
                    string got = si.GetItemID();
                    if (!string.IsNullOrEmpty(got)) {
                        name = got;
                    }
                }
                bool sim = SharedMain.WorldInSimRange(pos);
                bool slow = rb.bodyType == RigidbodyType2D.Dynamic && rb.gravityScale > 0.2f && Air(pos) && dy > -0.35f && rb.velocity.y > -1f && prev.Have;
                string line = "[fall] host id=" + sync
                    + " " + KindName(kind)
                    + " " + name
                    + (slow ? " slow" : "")
                    + " hostDist=" + (haveHost ? Vector2.Distance(pos, hostPos).ToString("0") : "-1")
                    + " plrDist=" + Vector2.Distance(pos, at).ToString("0.0")
                    + " dy=" + dy.ToString("0.00")
                    + " vy=" + rb.velocity.y.ToString("0.00")
                    + " grav=" + rb.gravityScale.ToString("0.00")
                    + " inSim=" + (sim ? 1 : 0)
                    + " stamp=" + (SharedMain.PositionIsInSimulatedChunk(pos) ? 1 : 0)
                    + " sim=" + (rb.simulated ? 1 : 0)
                    + " sleep=" + (rb.IsSleeping() ? 1 : 0)
                    + " body=" + rb.bodyType
                    + " at=" + pos.x.ToString("0") + "," + pos.y.ToString("0");
                if (slow || !sim || rb.bodyType != RigidbodyType2D.Dynamic) {
                    Plugin.log.LogWarning(line);
                } else {
                    Plugin.log.LogInfo(line);
                }
            }
        }

        Drop.Clear();
        foreach (KeyValuePair<int, HostSample> pair in Hosts) {
            if (!Seen.Contains(pair.Key)) {
                Drop.Add(pair.Key);
            }
        }
        for (int i = 0; i < Drop.Count; i++) {
            Hosts.Remove(Drop[i]);
            HostKinds.Remove(Drop[i]);
        }

        if (farPlayers > 0 && logged == 0) {
            Plugin.log.LogInfo("[fall] host idle far=" + farPlayers);
        }
    }

    static bool Air(Vector2 pos) {
        if (_groundMask == int.MinValue) {
            _groundMask = LayerMask.GetMask("Ground");
        }
        return Physics2D.Raycast(pos, Vector2.down, 1.2f, _groundMask).collider == null;
    }

    static float HostDist(Vector2 pos) {
        Vector2 host;
        Vector2 local;
        if (!Players(out host, out local)) {
            return -1f;
        }
        return Vector2.Distance(pos, host);
    }

    static bool IsFar() {
        float now = Time.unscaledTime;
        if (now < _farUntil) {
            return _far;
        }
        _farUntil = now + 0.5f;
        Vector2 host;
        Vector2 local;
        _far = Players(out host, out local) && (local - host).sqrMagnitude > FarSqr;
        return _far;
    }

    static bool Players(out Vector2 host, out Vector2 local) {
        host = Vector2.zero;
        local = Vector2.zero;
        bool haveHost = false;
        bool haveLocal = false;
        Dictionary<Body, ScavPlayer> map = ScavPlayer.BodyToPlayerDict;
        if (map == null) {
            return false;
        }
        foreach (KeyValuePair<Body, ScavPlayer> pair in map) {
            if (pair.Key == null || pair.Value == null) {
                continue;
            }
            if (pair.Value.IsHost) {
                host = pair.Key.transform.position;
                haveHost = true;
            }
            if (pair.Value.IsLocal) {
                local = pair.Key.transform.position;
                haveLocal = true;
            }
        }
        return haveHost && haveLocal;
    }

    static byte KindOf(Dictionary<int, byte> cache, GameObject go, int id) {
        if (cache.TryGetValue(id, out byte have)) {
            return have;
        }
        byte kind = Classify(go);
        cache[id] = kind;
        return kind;
    }

    static byte Classify(GameObject go) {
        if (go.GetComponent<GroundGlass>() != null) {
            return Glass;
        }
        BuildingEntity building = go.GetComponent<BuildingEntity>();
        string id = building != null ? building.id : null;
        if (string.IsNullOrEmpty(id)) {
            id = go.name;
        }
        if (!string.IsNullOrEmpty(id)) {
            if (id.IndexOf("stalac", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                return Stalact;
            }
            if (id.IndexOf("crate", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                return Crate;
            }
            if (id.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                return Glass;
            }
        }
        if (go.GetComponent<DamagingCrate>() != null) {
            return Stalact;
        }
        if (building != null && go.TryGetComponent(out Rigidbody2D rb) && rb.bodyType == RigidbodyType2D.Dynamic) {
            return Loose;
        }
        return Skip;
    }

    static string NameOf(GameObject go) {
        string id = SyncInfo.GetItemID(go);
        if (!string.IsNullOrEmpty(id)) {
            return id;
        }
        return go.name;
    }

    static string KindName(byte kind) {
        if (kind == Crate) {
            return "crate";
        }
        if (kind == Stalact) {
            return "stalact";
        }
        if (kind == Glass) {
            return "glass";
        }
        return "loose";
    }
}
