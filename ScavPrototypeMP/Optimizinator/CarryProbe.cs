using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Together;

// carried body. one line per pin, plus a line when a limb hits something.
internal static class CarryProbe {
    struct Prev {
        internal bool Have;
        internal Vector2[] Pos;
        internal float Last;
        internal int Skips;
    }

    static readonly Dictionary<int, Prev> State = new Dictionary<int, Prev>(8);
    static readonly Dictionary<int, float> HitNext = new Dictionary<int, float>(16);
    static int _statePruneAt = StaleIds.FirstThreshold;
    static int _hitPruneAt = StaleIds.FirstThreshold;

    internal static float Measure(Body body) {
        if (body == null || body.limbs == null) {
            return 0f;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        float max = 0f;
        Limb[] limbs = body.limbs;
        if (prev.Have && prev.Pos != null) {
            int n = limbs.Length < prev.Pos.Length ? limbs.Length : prev.Pos.Length;
            for (int i = 0; i < n; i++) {
                Limb limb = limbs[i];
                if (limb == null) {
                    continue;
                }
                float d = Vector2.Distance(limb.transform.position, prev.Pos[i]);
                if (d > max) {
                    max = d;
                }
            }
        }
        State[id] = prev;
        return max;
    }

    internal static void Save(Body body) {
        if (body == null || body.limbs == null) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        Limb[] limbs = body.limbs;
        if (prev.Pos == null || prev.Pos.Length != limbs.Length) {
            prev.Pos = new Vector2[limbs.Length];
        }
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            prev.Pos[i] = limb != null ? (Vector2)limb.transform.position : Vector2.zero;
        }
        prev.Have = true;
        State[id] = prev;
    }

    internal static void MarkSkip(Body body) {
        if (body == null) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        prev.Skips++;
        State[id] = prev;
    }

    internal static void Rel(Body body, Vector2 carrierVel, out float rel, out float ang, out int hot) {
        rel = 0f;
        ang = 0f;
        hot = -1;
        if (body == null || body.limbs == null) {
            return;
        }
        Limb[] limbs = body.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null) {
                continue;
            }
            float r = (limb.rb.velocity - carrierVel).magnitude;
            if (r > rel) {
                rel = r;
                hot = i;
            }
            float a = Mathf.Abs(limb.rb.angularVelocity);
            if (a > ang) {
                ang = a;
            }
        }
    }

    internal static void Write(Body body, string phase, float step, float chestErr, float relIn, float relOut, float ang, int hot) {
        if (body == null || Plugin.log == null) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        float now = Time.unscaledTime;
        float dt = prev.Last > 0f ? now - prev.Last : 0f;
        prev.Last = now;
        int skips = prev.Skips;
        prev.Skips = 0;
        State[id] = prev;

        Rigidbody2D hotRb = null;
        if (hot >= 0 && body.limbs != null && hot < body.limbs.Length && body.limbs[hot] != null) {
            hotRb = body.limbs[hot].rb;
        }
        string name = "?";
        string on = "-";
        try {
            if (ScavPlayer.BodyToPlayerDict != null && ScavPlayer.BodyToPlayerDict.TryGetValue(body, out ScavPlayer plr) && plr != null) {
                name = plr.playerName;
            }
            if (body.TryGetNetBody(out NetBody nb) && nb != null && nb.piggybacking_on != null && nb.piggybacking_on.plr != null) {
                on = nb.piggybacking_on.plr.playerName;
            }
        } catch {
            // mid destroy
        }

        var sb = new StringBuilder(240);
        sb.Append("[carry] ").Append(phase);
        sb.Append(" name=").Append(name);
        sb.Append(" side=").Append(Net.IsServer ? "host" : "client");
        sb.Append(" local=").Append(body.IsBodyLocal() ? 1 : 0);
        sb.Append(" alive=").Append(body.alive ? 1 : 0);
        sb.Append(" stand=").Append(body.standing ? 1 : 0);
        sb.Append(" on=").Append(on);
        sb.Append(" dt=").Append(dt.ToString("0.000"));
        sb.Append(" scale=").Append(Time.timeScale.ToString("0.00"));
        sb.Append(" fdt=").Append(Time.fixedDeltaTime.ToString("0.000"));
        sb.Append(" step=").Append(step.ToString("0.000"));
        sb.Append(" chestErr=").Append(chestErr.ToString("0.000"));
        sb.Append(" relIn=").Append(relIn.ToString("0.00"));
        sb.Append(" relOut=").Append(relOut.ToString("0.00"));
        sb.Append(" ang=").Append(ang.ToString("0.0"));
        sb.Append(" hot=").Append(hot);
        sb.Append(" skip=").Append(skips);
        if (hotRb != null) {
            sb.Append(" kin=").Append(hotRb.isKinematic ? 1 : 0);
            sb.Append(" sim=").Append(hotRb.simulated ? 1 : 0);
            sb.Append(" interp=").Append(hotRb.interpolation);
            sb.Append(" mass=").Append(hotRb.mass.ToString("0.00"));
            sb.Append(" drag=").Append(hotRb.drag.ToString("0.00"));
            sb.Append(" grav=").Append(hotRb.gravityScale.ToString("0.00"));
        }
        if (body.rb != null) {
            sb.Append(" rootMass=").Append(body.rb.mass.ToString("0.00"));
            sb.Append(" rootGrav=").Append(body.rb.gravityScale.ToString("0.00"));
            sb.Append(" rootDrag=").Append(body.rb.drag.ToString("0.00"));
        }
        Plugin.log.LogInfo(sb.ToString());
    }

    internal static void Hit(Limb limb, Collision2D col, string kind) {
        if (limb == null || limb.body == null || col == null || Plugin.log == null) {
            return;
        }
        if (!limb.body.TryGetNetBody(out NetBody nb) || nb == null || nb.piggybacking_on == null) {
            return;
        }
        int id = limb.GetInstanceID();
        float now = Time.unscaledTime;
        if (kind == "stay" && HitNext.TryGetValue(id, out float next) && now < next) {
            return;
        }
        if (kind == "stay") {
            StaleIds.Prune<Limb, float>(HitNext, ref _hitPruneAt);
            HitNext[id] = now + 0.25f;
        }
        int index = -1;
        Limb[] limbs = limb.body.limbs;
        if (limbs != null) {
            for (int i = 0; i < limbs.Length; i++) {
                if (limbs[i] == limb) {
                    index = i;
                    break;
                }
            }
        }
        string other = col.collider != null ? col.collider.name : "-";
        string name = "?";
        try {
            if (ScavPlayer.BodyToPlayerDict != null && ScavPlayer.BodyToPlayerDict.TryGetValue(limb.body, out ScavPlayer plr) && plr != null) {
                name = plr.playerName;
            }
        } catch {
            // mid destroy
        }
        Plugin.log.LogInfo("[carry] " + kind
            + " name=" + name
            + " side=" + (Net.IsServer ? "host" : "client")
            + " limb=" + index
            + " other=" + other
            + " rel=" + col.relativeVelocity.magnitude.ToString("0.00")
            + " n=" + col.contactCount);
    }

    static Prev Get(int id) {
        if (!State.TryGetValue(id, out Prev prev)) {
            StaleIds.Prune<Body, Prev>(State, ref _statePruneAt);
            prev = new Prev();
        }
        return prev;
    }
}

[HarmonyPatch(typeof(Limb), "OnCollisionEnter2D")]
static class Limb_OnCollisionEnter2D_CarryProbe {
    static void Postfix(Limb __instance, Collision2D col) {
        CarryProbe.Hit(__instance, col, "hit");
    }
}

