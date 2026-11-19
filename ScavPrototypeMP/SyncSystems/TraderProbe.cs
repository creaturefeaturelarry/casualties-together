using CasualtiesTogetherUtils;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Together;

// trader anger, who they are aimed at, and whether the swing actually runs. heartbeat while someone is at the stall.
internal static class TraderProbe {
    const float Heartbeat = 0.5f;
    const float Near = 18f;

    struct Prev {
        internal float Next;
        internal float Rep;
        internal float Hostility;
        internal bool Hostile;
        internal bool Have;
        internal string SwingWhy;
        internal int Swings;
        internal int Allowed;
    }

    static readonly Dictionary<int, Prev> State = new Dictionary<int, Prev>(8);
    static int _prunePendingAt = StaleIds.FirstThreshold;

    internal static void Tick(TraderScript trader, string site) {
        if (!Net.IsRunning || trader == null) {
            return;
        }
        int id = trader.GetInstanceID();
        Prev prev = Get(id);
        bool changed = !prev.Have
            || prev.Hostile != trader.hostile
            || Mathf.Abs(prev.Rep - trader.reputation) > 0.4f
            || Mathf.Abs(prev.Hostility - trader.hostility) > 0.4f;
        float now = Time.unscaledTime;
        if (!changed && now < prev.Next) {
            State[id] = prev;
            return;
        }
        string line = Describe(trader, changed ? "event" : "beat", site, prev);
        if (line == null) {
            prev.Have = true;
            prev.Rep = trader.reputation;
            prev.Hostility = trader.hostility;
            prev.Hostile = trader.hostile;
            prev.Next = now + Heartbeat;
            State[id] = prev;
            return;
        }
        Log(line);
        prev.Have = true;
        prev.Rep = trader.reputation;
        prev.Hostility = trader.hostility;
        prev.Hostile = trader.hostile;
        prev.Swings = 0;
        prev.Allowed = 0;
        prev.Next = now + Heartbeat;
        State[id] = prev;
    }

    internal static void Swing(TraderScript trader, bool allow, string why) {
        if (!Net.IsRunning || trader == null) {
            return;
        }
        int id = trader.GetInstanceID();
        Prev prev = Get(id);
        prev.Swings++;
        if (allow) {
            prev.Allowed++;
        }
        string key = (allow ? "1:" : "0:") + (why ?? "");
        bool fresh = prev.SwingWhy != key;
        prev.SwingWhy = key;
        State[id] = prev;
        if (!fresh) {
            return;
        }
        Log(Describe(trader, "swing", why, prev) ?? ("[trader] swing " + why));
    }

    static Prev Get(int id) {
        if (!State.TryGetValue(id, out Prev prev)) {
            StaleIds.Prune<TraderScript, Prev>(State, ref _prunePendingAt);
            prev = new Prev();
        }
        return prev;
    }

    static string Describe(TraderScript trader, string kind, string site, Prev prev) {
        TrackerTrader tt = trader.GetComponent<TrackerTrader>();
        Body focus = tt != null ? tt.focused_body : null;
        Body near = null;
        try {
            near = TrackerTrader.ClosestLiving(trader.transform.position);
        } catch {
            near = null;
        }
        float nearDist = near != null ? Vector2.Distance(trader.transform.position, near.transform.position) : -1f;
        bool hot = trader.hostile || trader.hostility > 5f || trader.reputation < 90f || (nearDist >= 0f && nearDist < Near);
        if (!hot && kind != "swing") {
            return null;
        }

        Body cam = Util.GetLocalBody();
        float camDist = cam != null ? Vector2.Distance(trader.transform.position, cam.transform.position) : -1f;
        string sync = "0";
        float hp = -1f;
        if (tt != null && tt.si != null) {
            sync = tt.si.syncId.ToString();
        }
        if (tt != null && tt.build != null) {
            try {
                hp = tt.build.GetHealth();
            } catch {
                hp = -1f;
            }
        }

        bool visible = false;
        Renderer[] renderers = trader.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++) {
            if (renderers[i] != null && renderers[i].isVisible) {
                visible = true;
                break;
            }
        }

        string anim = AttackText(trader);
        var sb = new StringBuilder(280);
        sb.Append("[trader] ").Append(kind);
        sb.Append(" site=").Append(site ?? "-");
        sb.Append(" sync=").Append(sync);
        sb.Append(" side=").Append(Net.IsServer ? "host" : "client");
        sb.Append(" rep=").Append(trader.reputation.ToString("0.0"));
        sb.Append(" hostil=").Append(trader.hostility.ToString("0.0"));
        sb.Append(" hostile=").Append(trader.hostile ? 1 : 0);
        sb.Append(" hp=").Append(hp < 0f ? "-" : hp.ToString("0"));
        sb.Append(" active=").Append(trader.gameObject.activeInHierarchy ? 1 : 0);
        sb.Append(" visible=").Append(visible ? 1 : 0);
        sb.Append(" focus=").Append(BodyName(focus));
        sb.Append(" focusDist=").Append(focus == null ? "-" : Vector2.Distance(trader.transform.position, focus.transform.position).ToString("0.0"));
        sb.Append(" focusLocal=").Append(focus != null && focus.IsBodyLocal() ? 1 : 0);
        sb.Append(" near=").Append(BodyName(near));
        sb.Append(" nearDist=").Append(nearDist < 0f ? "-" : nearDist.ToString("0.0"));
        sb.Append(" nearLocal=").Append(near != null && near.IsBodyLocal() ? 1 : 0);
        sb.Append(" camDist=").Append(camDist < 0f ? "-" : camDist.ToString("0.0"));
        sb.Append(" swings=").Append(prev.Swings);
        sb.Append(" allowed=").Append(prev.Allowed);
        sb.Append(' ').Append(anim);
        sb.Append(" pos=").Append(trader.transform.position.x.ToString("0.0")).Append(',').Append(trader.transform.position.y.ToString("0.0"));
        return sb.ToString();
    }

    static string AttackText(TraderScript trader) {
        GameObject attack = trader.attackAnimation;
        int on = attack != null && attack.activeSelf ? 1 : 0;
        Animator anim = attack != null ? attack.GetComponentInChildren<Animator>(true) : null;
        if (anim == null) {
            anim = trader.GetComponentInChildren<Animator>(true);
        }
        if (anim == null) {
            return "atk=" + on + " anim=none";
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
        return "atk=" + on
            + " anim=" + clip
            + " animOn=" + (anim.enabled ? 1 : 0)
            + " animSpd=" + anim.speed.ToString("0.00")
            + " stateSpd=" + stateSpeed.ToString("0.00")
            + " cull=" + anim.cullingMode;
    }

    static string BodyName(Body body) {
        if (body == null) {
            return "-";
        }
        try {
            if (ScavPlayer.BodyToPlayerDict != null && ScavPlayer.BodyToPlayerDict.TryGetValue(body, out ScavPlayer plr) && plr != null) {
                return plr.playerName;
            }
        } catch {
            // ignore
        }
        return "body";
    }

    static void Log(string line) {
        if (Plugin.log == null || string.IsNullOrEmpty(line)) {
            return;
        }
        Plugin.log.LogInfo(line);
    }
}
