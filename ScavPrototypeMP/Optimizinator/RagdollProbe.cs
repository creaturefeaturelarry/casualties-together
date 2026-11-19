using CasualtiesTogetherUtils;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Together;

// player ragdoll pose, physics, and the animator. one line on a change, then a heartbeat while they are down.
internal static class RagdollProbe {
    const float Heartbeat = 0.5f;

    struct Prev {
        internal bool Have;
        internal bool Standing;
        internal bool Reading;
        internal int Kin;
        internal Vector2[] Pos;
        internal float Next;
        internal string LastEvent;
        internal int Pastes;
        internal int Writes;
        internal Vector2 PackVel;
        internal bool HavePack;
    }

    static readonly Dictionary<int, Prev> State = new Dictionary<int, Prev>(16);
    static int _prunePendingAt = StaleIds.FirstThreshold;

    internal static void MarkPaste(Body body) {
        if (body == null) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        prev.Pastes++;
        State[id] = prev;
    }

    internal static void MarkWrite(Body body) {
        if (body == null) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        prev.Writes++;
        State[id] = prev;
    }

    internal static void Pack(Body body, bool standing, Vector2 vel, bool carried) {
        if (body == null) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        prev.PackVel = vel;
        prev.HavePack = true;
        State[id] = prev;
        if (carried && !standing) {
            Event(body, "sync-carried", " packVel=" + vel.x.ToString("0.00") + "," + vel.y.ToString("0.00"));
        }
    }

    internal static void Event(Body body, string why, string extra = null) {
        if (body == null || string.IsNullOrEmpty(why)) {
            return;
        }
        int id = body.GetInstanceID();
        Prev prev = Get(id);
        string key = why + (extra ?? "");
        if (prev.LastEvent == key) {
            State[id] = prev;
            return;
        }
        prev.LastEvent = key;
        State[id] = prev;
        Log(Line(body, why, extra, prev, 0f));
    }

    internal static void Tick(Body body) {
        if (!Net.IsRunning || body == null) {
            return;
        }
        bool player = false;
        try {
            player = body.TryGetNetBody(out NetBody nb) && nb != null && nb.IsPlayer;
        } catch {
            return;
        }
        if (!player) {
            return;
        }

        int id = body.GetInstanceID();
        Prev prev = Get(id);
        bool down = !body.standing || body.shock > 1f;
        if (!down && prev.Standing && prev.Have) {
            return;
        }

        float moved = PoseDelta(body, ref prev);
        float now = Time.unscaledTime;
        bool changed = !prev.Have || prev.Standing != body.standing;
        if (!changed && now < prev.Next) {
            State[id] = prev;
            return;
        }

        string why = !prev.Have ? "see" : (changed ? (body.standing ? "up" : "down") : "beat");
        Log(Line(body, why, null, prev, moved));
        prev.Have = true;
        prev.Standing = body.standing;
        prev.Pastes = 0;
        prev.Writes = 0;
        prev.Next = now + Heartbeat;
        State[id] = prev;
    }

    static Prev Get(int id) {
        if (!State.TryGetValue(id, out Prev prev)) {
            StaleIds.Prune<Body, Prev>(State, ref _prunePendingAt);
            prev = new Prev();
        }
        return prev;
    }

    static float PoseDelta(Body body, ref Prev prev) {
        Limb[] limbs = body.limbs;
        if (limbs == null) {
            return 0f;
        }
        if (prev.Pos == null || prev.Pos.Length != limbs.Length) {
            prev.Pos = new Vector2[limbs.Length];
        }
        float max = 0f;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null) {
                continue;
            }
            Vector2 at = limb.transform.position;
            if (prev.Have) {
                float d = Vector2.Distance(at, prev.Pos[i]);
                if (d > max) {
                    max = d;
                }
            }
            prev.Pos[i] = at;
        }
        return max;
    }

    static string Line(Body body, string why, string extra, Prev prev, float moved) {
        int kin = 0;
        int limbsN = 0;
        int asleep = 0;
        float maxSpeed = 0f;
        Limb[] limbs = body.limbs;
        if (limbs != null) {
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                if (limb == null || limb.rb == null) {
                    continue;
                }
                limbsN++;
                if (limb.rb.isKinematic) {
                    kin++;
                }
                if (!limb.rb.simulated) {
                    asleep++;
                }
                float spd = limb.rb.velocity.magnitude;
                if (spd > maxSpeed) {
                    maxSpeed = spd;
                }
            }
        }

        Rigidbody2D root = body.rb;
        bool reading = false;
        int net = 0;
        string name = "?";
        bool carried = false;
        try {
            if (body.TryGetNetBody(out NetBody nb) && nb != null) {
                carried = nb.IsPiggybacking();
                if (nb.syncBody != null) {
                    net = nb.syncBody.Id;
                    reading = nb.syncBody.readingRagdollData;
                }
            }
            if (ScavPlayer.BodyToPlayerDict != null && ScavPlayer.BodyToPlayerDict.TryGetValue(body, out ScavPlayer plr) && plr != null) {
                name = plr.playerName;
            }
        } catch {
            // body can be mid-destroy
        }

        float camDist = -1f;
        Body cam = Util.GetLocalBody();
        if (cam != null) {
            camDist = Vector2.Distance(body.transform.position, cam.transform.position);
        }
        bool sim = false;
        try {
            sim = SharedMain.PositionIsInSimulatedChunk(body.transform.position);
        } catch {
            sim = false;
        }

        string anim = AnimText(body);
        var sb = new StringBuilder(256);
        sb.Append("[ragdoll] ");
        sb.Append(why);
        sb.Append(" name=").Append(name);
        sb.Append(" net=").Append(net);
        sb.Append(" side=").Append(Net.IsServer ? "host" : "client");
        sb.Append(" local=").Append(body.IsBodyLocal() ? 1 : 0);
        sb.Append(" stand=").Append(body.standing ? 1 : 0);
        sb.Append(" shock=").Append(body.shock.ToString("0.0"));
        sb.Append(" conscious=").Append(body.conscious ? 1 : 0);
        sb.Append(" alive=").Append(body.alive ? 1 : 0);
        sb.Append(" sleep=").Append(body.sleeping ? 1 : 0);
        sb.Append(" piggy=").Append(carried ? 1 : 0);
        sb.Append(" sim=").Append(sim ? 1 : 0);
        sb.Append(" read=").Append(reading ? 1 : 0);
        sb.Append(" kin=").Append(kin).Append('/').Append(limbsN);
        sb.Append(" unsim=").Append(asleep);
        sb.Append(" maxSpd=").Append(maxSpeed.ToString("0.00"));
        sb.Append(" poseDt=").Append(moved.ToString("0.000"));
        sb.Append(" paste=").Append(prev.Pastes);
        sb.Append(" write=").Append(prev.Writes);
        if (root != null) {
            sb.Append(" rootKin=").Append(root.isKinematic ? 1 : 0);
            sb.Append(" rootSim=").Append(root.simulated ? 1 : 0);
            sb.Append(" grav=").Append(root.gravityScale.ToString("0.00"));
            sb.Append(" rootVel=").Append(root.velocity.x.ToString("0.00")).Append(',').Append(root.velocity.y.ToString("0.00"));
            sb.Append(" body=").Append(root.bodyType);
        }
        if (prev.HavePack) {
            sb.Append(" packVel=").Append(prev.PackVel.x.ToString("0.00")).Append(',').Append(prev.PackVel.y.ToString("0.00"));
        }
        sb.Append(" camDist=").Append(camDist < 0f ? "-" : camDist.ToString("0.0"));
        sb.Append(' ').Append(anim);
        if (!string.IsNullOrEmpty(extra)) {
            sb.Append(extra);
        }
        return sb.ToString();
    }

    static string AnimText(Body body) {
        Animator anim = body.bodyAnimator;
        if (anim == null) {
            return "anim=none";
        }
        string clip = "-";
        float stateSpeed = 0f;
        try {
            if (anim.runtimeAnimatorController != null && anim.isActiveAndEnabled) {
                AnimatorClipInfo[] clips = anim.GetCurrentAnimatorClipInfo(0);
                if (clips != null && clips.Length > 0 && clips[0].clip != null) {
                    clip = clips[0].clip.name;
                }
                stateSpeed = anim.GetCurrentAnimatorStateInfo(0).speed;
            }
        } catch {
            clip = "err";
        }
        float arms = 0f;
        if (body.armsAnimator != null) {
            arms = body.armsAnimator.speed;
        }
        return "anim=" + clip
            + " animOn=" + (anim.enabled ? 1 : 0)
            + " animSpd=" + anim.speed.ToString("0.00")
            + " stateSpd=" + stateSpeed.ToString("0.00")
            + " add=" + body.bodyAnimationSpeedAdd.ToString("0.00")
            + " arms=" + arms.ToString("0.00")
            + " cull=" + anim.cullingMode;
    }

    static void Log(string line) {
        if (Plugin.log == null || string.IsNullOrEmpty(line)) {
            return;
        }
        Plugin.log.LogInfo(line);
    }
}
