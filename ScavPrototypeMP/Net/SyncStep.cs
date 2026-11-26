using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteEntitySystem.Internal;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Together {
    // one log prefix. grep [syncstep] to find the line, then delete this file and its call sites.
    internal static class SyncStep {
        const float SnapAt = 0.5f;
        const float StandH = 40f;
        const float StandAspect = 21f / 9f;

        static readonly float[] HalfH = new float[256];
        static readonly float[] Aspect = new float[256];
        static readonly bool[] Known = new bool[256];

        static int _condSkip, _condWrite;
        static int _rotSkip, _rotWrite;
        static int _scaleSkip, _scaleWrite;
        static int _spiderEdge, _spiderSnap, _spiderSkip;
        static int _stunEdge, _stunSnap, _stunSkip;
        static int _containerSkip, _containerRebuild;
        static int _distSkip, _distSend;
        static int _resendSkip, _resendSend;
        static float _sentH = -1f;
        static float _sentA = -1f;
        static bool _harmony;
        static long _windowStart;
        static bool _armed;
        static ushort _armedId;
        static byte _armedPlayer;
        static byte _armedVer;
        static ushort _armedTick;
        static ushort _realAck;
        static NetPlayer _armedWho;
        static FieldInfo _ack;
        static readonly Dictionary<long, ushort> Sent = new Dictionary<long, ushort>();

        internal static void Install() {
            if (_harmony) {
                return;
            }
            _harmony = true;
            _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Multiplayer.RegisterCustomServerReceiver("syncstep.view", OnView);
            try {
                var type = AccessTools.TypeByName("LiteEntitySystem.Internal.StateSerializer");
                MethodInfo method = null;
                foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (candidate.Name == "MakeDiff") {
                        method = candidate;
                    }
                }
                _ack = AccessTools.Field(typeof(NetPlayer), "LatestServerTick");
                var harmony = new HarmonyLib.Harmony("together.syncstep");
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(SyncStep).GetMethod(nameof(BeforeDiff), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(SyncStep).GetMethod(nameof(AfterDiff), BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(SyncStep).GetMethod(nameof(FinalDiff), BindingFlags.Static | BindingFlags.NonPublic)));
            } catch (Exception ex) {
                Plugin.Logger.LogInfo("[syncstep] send-once hook " + ex.GetType().Name + " " + ex.Message);
            }
        }

        internal static void Arm(InternalEntity entity, NetPlayer player) {
            if (entity == null || player == null) {
                return;
            }
            _armed = true;
            _armedId = entity.Id;
            _armedPlayer = player.Id;
            _armedWho = player;
            _armedTick = entity.EntityManager != null ? entity.EntityManager.Tick : (ushort)0;
            _armedVer = entity.Version;
            _realAck = _ack != null ? (ushort)_ack.GetValue(player) : (ushort)0;
        }

        internal static void Pulse() {
            Install();
            ReportView();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _windowStart < System.Diagnostics.Stopwatch.Frequency * 10L) {
                return;
            }
            _windowStart = now;
            if (!Net.IsRunning || !Net.IsServer) {
                return;
            }
            int any = _condSkip + _condWrite + _rotSkip + _rotWrite + _scaleSkip + _scaleWrite
                + _spiderEdge + _spiderSnap + _spiderSkip + _stunEdge + _stunSnap + _stunSkip
                + _containerSkip + _containerRebuild + _distSkip + _distSend + _resendSkip + _resendSend;
            if (any == 0) {
                return;
            }
            var line = new StringBuilder(256);
            line.Append("[syncstep] 10s cond=").Append(_condSkip).Append('/').Append(_condWrite);
            line.Append(" rot=").Append(_rotSkip).Append('/').Append(_rotWrite);
            line.Append(" scale=").Append(_scaleSkip).Append('/').Append(_scaleWrite);
            line.Append(" spider=").Append(_spiderEdge).Append('/').Append(_spiderSnap).Append('/').Append(_spiderSkip);
            line.Append(" stun=").Append(_stunEdge).Append('/').Append(_stunSnap).Append('/').Append(_stunSkip);
            line.Append(" container=").Append(_containerSkip).Append('/').Append(_containerRebuild);
            line.Append(" resend=").Append(_resendSkip).Append('/').Append(_resendSend);
            line.Append(" dist=").Append(_distSkip).Append('/').Append(_distSend);
            Plugin.Logger.LogInfo(line.ToString());
            _condSkip = _condWrite = _rotSkip = _rotWrite = _scaleSkip = _scaleWrite = 0;
            _spiderEdge = _spiderSnap = _spiderSkip = _stunEdge = _stunSnap = _stunSkip = 0;
            _containerSkip = _containerRebuild = _distSkip = _distSend = _resendSkip = _resendSend = 0;
        }

        static bool Newer(ushort a, ushort b) {
            const int max = 65536;
            const int half = 32768;
            return ((a - b + max + half) % max - half) > 0;
        }
        static void BeforeDiff(NetPlayer player, bool includeUnchanged) {
            if (!_armed || _ack == null || player == null || player.Id != _armedPlayer || includeUnchanged) {
                return;
            }
            long key = ((long)_armedId << 16) | _armedPlayer | ((long)_armedVer << 32);
            if (Sent.TryGetValue(key, out ushort sent) && Newer(sent, _realAck)) {
                _ack.SetValue(player, sent);
            }
        }

        static void AfterDiff(NetPlayer player, bool includeUnchanged, bool __result) {
            if (!_armed || player == null || player.Id != _armedPlayer) {
                return;
            }
            long key = ((long)_armedId << 16) | player.Id | ((long)_armedVer << 32);
            if (!includeUnchanged) {
                if (__result) {
                    Sent[key] = _armedTick;
                    _resendSend++;
                } else if (Sent.ContainsKey(key)) {
                    _resendSkip++;
                }
            }
            if (_ack != null) {
                _ack.SetValue(player, _realAck);
            }
            _armed = false;
        }

        static void FinalDiff() {
            if (!_armed || _armedWho == null || _ack == null) {
                _armed = false;
                return;
            }
            _ack.SetValue(_armedWho, _realAck);
            _armed = false;
        }

        internal static float StepItem(float value) {
            return Mathf.Round(value * 100f) * 0.01f;
        }

        internal static float StepBuilding(ref float peak, float health) {
            if (health > peak) {
                peak = health;
            }
            float scale = peak > 1.5f ? peak : 1f;
            float step = scale * 0.01f;
            if (step < 0.0001f) {
                step = 0.01f;
            }
            return Mathf.Round(health / step) * step;
        }

        internal static void Cond(bool wrote) {
            if (wrote) _condWrite++;
            else _condSkip++;
        }

        internal static void Rot(bool wrote) {
            if (wrote) _rotWrite++;
            else _rotSkip++;
        }

        internal static void Scale(bool wrote) {
            if (wrote) _scaleWrite++;
            else _scaleSkip++;
        }

        // kind 0 edge, 1 snap, 2 skip
        internal static void Timer(bool stun, int kind) {
            if (stun) {
                if (kind == 0) _stunEdge++;
                else if (kind == 1) _stunSnap++;
                else _stunSkip++;
            } else if (kind == 0) {
                _spiderEdge++;
            } else if (kind == 1) {
                _spiderSnap++;
            } else {
                _spiderSkip++;
            }
        }

        internal static void Container(bool rebuild) {
            if (rebuild) _containerRebuild++;
            else _containerSkip++;
        }

        internal static void Dist(bool send) {
            if (send) _distSend++;
            else _distSkip++;
        }

        internal static bool InView(SyncInfo info, NetPlayer player) {
            if (info == null || info.go == null || player == null) {
                return true;
            }
            return Hot((Vector2)info.go.transform.position, player);
        }

        internal static bool Hot(Vector2 point, NetPlayer player) {
            if (player == null) {
                return true;
            }
            knetid id = (ushort)player.Id;
            if (!ScavPlayer.ClientIdToPlayerDict.TryGetValue(id, out ScavPlayer who) || who == null) {
                return true;
            }
            bool local = ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.playerId.id == player.Id;
            float radius = RadiusOf(player.Id, local);
            return (point - who.pos).sqrMagnitude <= radius * radius;
        }

        // host: any living player. client: the local player only.
        internal static bool NearAny(Vector2 point, float extra) {
            if (!Net.IsRunning || !Net.IsServer) {
                return NearLocal(point, extra);
            }
            bool any = false;
            List<ScavPlayer> living = ScavPlayer.AllLivingPlayers;
            for (int i = 0; i < living.Count; i++) {
                ScavPlayer plr = living[i];
                if (plr == null) {
                    continue;
                }
                any = true;
                bool local = ScavPlayer.LocalPlayer != null && plr == ScavPlayer.LocalPlayer;
                float radius = RadiusOf((byte)plr.playerId.id, local) + extra;
                if ((point - plr.pos).sqrMagnitude <= radius * radius) {
                    return true;
                }
            }
            if (!any) {
                return NearLocal(point, extra);
            }
            return false;
        }

        static bool NearLocal(Vector2 point, float extra) {
            Vector2 from;
            if (Util.TryGetLocalBody(out Body body) && body != null) {
                from = body.transform.position;
            } else if (Camera.main != null) {
                from = Camera.main.transform.position;
            } else {
                return true;
            }
            byte id = 0;
            if (ScavPlayer.LocalPlayer != null) {
                id = (byte)ScavPlayer.LocalPlayer.playerId.id;
            }
            float radius = RadiusOf(id, true) + extra;
            return (point - from).sqrMagnitude <= radius * radius;
        }

        static float RadiusOf(byte id, bool localCamera) {
            float halfH;
            float aspect;
            if (localCamera && Camera.main != null) {
                halfH = Camera.main.orthographicSize;
                aspect = Camera.main.aspect;
            } else if (id < Known.Length && Known[id]) {
                halfH = HalfH[id];
                aspect = Aspect[id];
            } else {
                halfH = StandH;
                aspect = StandAspect;
            }
            if (halfH < 1f) {
                halfH = StandH;
            }
            if (aspect < 0.5f) {
                aspect = StandAspect;
            }
            float halfW = halfH * aspect;
            return Mathf.Sqrt(halfW * halfW + halfH * halfH) * 1.5f;
        }

        internal static bool LocalCircle(out Vector2 center, out float radius) {
            center = default;
            radius = 0f;
            if (Util.TryGetLocalBody(out Body body) && body != null) {
                center = body.transform.position;
            } else if (Camera.main != null) {
                center = Camera.main.transform.position;
            } else {
                return false;
            }
            byte id = 0;
            if (ScavPlayer.LocalPlayer != null) {
                id = (byte)ScavPlayer.LocalPlayer.playerId.id;
            }
            radius = RadiusOf(id, true);
            return true;
        }

        static void ReportView() {
            if (!Net.IsRunning || !Net.IsClient || Net.IsServer) {
                return;
            }
            Camera cam = Camera.main;
            if (cam == null) {
                return;
            }
            float h = cam.orthographicSize;
            float a = cam.aspect;
            if (_sentH > 0f && Mathf.Abs(h - _sentH) < 0.05f && Mathf.Abs(a - _sentA) < 0.01f) {
                return;
            }
            _sentH = h;
            _sentA = a;
            var writer = Multiplayer.CreateNamedWriter("syncstep.view");
            writer.Put(h);
            writer.Put(a);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        static void OnView(ScavPlayer plr, LiteNetLib.Utils.NetDataReader reader) {
            if (plr == null) {
                return;
            }
            float h = reader.GetFloat();
            float a = reader.GetFloat();
            int id = plr.playerId.id;
            if (id < 0 || id >= Known.Length) {
                return;
            }
            HalfH[id] = h;
            Aspect[id] = a;
            Known[id] = true;
        }
    }
}
