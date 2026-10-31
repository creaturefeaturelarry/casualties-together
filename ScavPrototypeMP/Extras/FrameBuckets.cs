using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;

namespace Together;

// 10s line, always on. numbers are ms per frame, then the worst single call.
// net.item and net.body sit inside net. items is the stock item update plus our patches.
static class FrameBuckets {
    internal const int IdNet = 0;
    internal const int IdNetItem = 1;
    internal const int IdNetBody = 2;
    internal const int IdSrv = 3;
    internal const int IdClient = 4;
    internal const int IdWorld = 5;
    internal const int IdFluid = 6;
    internal const int IdMed = 7;
    internal const int IdObj = 8;
    internal const int IdPly = 9;
    internal const int IdBody = 10;
    internal const int IdItems = 11;
    internal const int IdGun = 12;
    internal const int IdVoice = 13;
    internal const int IdSteam = 14;
    internal const int IdOpt = 15;
    internal const int IdTick = 16;
    internal const int IdBldg = 17;
    internal const int IdDrip = 18;
    const int N = 19;

    const float Window = 10f;

    static readonly long[] Sum = new long[N];
    static readonly long[] Peak = new long[N];
    static readonly int[] Hits = new int[N];
    static readonly string[] Names = {
        "net", "net.item", "net.body", "srv", "client", "world", "fluid", "med", "obj",
        "ply", "body", "items", "gun", "voice", "steam", "opt", "tick", "bldg", "drip",
    };

    static readonly StringBuilder Line = new StringBuilder(384);
    static float _windowStart;
    static int _frames;
    static float _frameSum;
    static float _framePeak;

    internal static long Start() {
        return Stopwatch.GetTimestamp();
    }

    internal static void Add(int id, long start) {
        long dt = Stopwatch.GetTimestamp() - start;
        if (dt < 0)
            dt = 0;
        Sum[id] += dt;
        Hits[id]++;
        if (dt > Peak[id])
            Peak[id] = dt;
    }

    internal static void NoteFrame(float dt) {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_windowStart == 0f)
            _windowStart = now;
        if (now - _windowStart >= Window && _frames > 0) {
            Dump(now - _windowStart);
            _windowStart = now;
            _frames = 0;
            _frameSum = 0f;
            _framePeak = 0f;
            Array.Clear(Sum, 0, N);
            Array.Clear(Peak, 0, N);
            Array.Clear(Hits, 0, N);
        }
        if (dt < 0f)
            dt = 0f;
        _frames++;
        _frameSum += dt;
        if (dt > _framePeak)
            _framePeak = dt;
    }

    static void Dump(float span) {
        if (Plugin.log == null || _frames <= 0 || !Together.log.verbose)
            return;
        int fps = span > 0.05f ? (int)(_frames / span) : 0;
        Line.Clear();
        Line.Append("[frames] fps ").Append(fps);
        Line.Append(" frame ").Append((_frameSum / _frames * 1000f).ToString("0.0"));
        Line.Append('/').Append((_framePeak * 1000f).ToString("0.0"));
        double freq = Stopwatch.Frequency;
        for (int i = 0; i < N; i++) {
            if (Hits[i] == 0)
                continue;
            double avg = (Sum[i] / (double)_frames) * 1000.0 / freq;
            double peak = Peak[i] * 1000.0 / freq;
            if (avg < 0.2 && peak < 5.0)
                continue;
            Line.Append(' ').Append(Names[i]).Append(' ');
            Line.Append(avg.ToString("0.0")).Append('/').Append(peak.ToString("0.0"));
            int per = Hits[i] / _frames;
            if (per >= 2)
                Line.Append(" x").Append(per);
        }
        Plugin.log.LogInfo(Line.ToString());
    }

    static void Begin(out long __state) {
        __state = Start();
    }

    [HarmonyPatch(typeof(Net), nameof(Net.DoUpdate))]
    static class WatchNet {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdNet, __state);
    }

    [HarmonyPatch(typeof(SyncInfo), "Update")]
    static class WatchNetItem {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdNetItem, __state);
    }

    [HarmonyPatch(typeof(SyncBody), "Update")]
    static class WatchNetBody {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdNetBody, __state);
    }

    [HarmonyPatch(typeof(ServerMain), "Update")]
    [HarmonyPatch(typeof(ServerMain), "FixedUpdate")]
    static class WatchSrv {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdSrv, __state);
    }

    [HarmonyPatch(typeof(ClientMain), "Update")]
    static class WatchClient {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdClient, __state);
    }

    [HarmonyPatch(typeof(WorldChunkSync), "LateUpdate")]
    static class WatchWorld {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdWorld, __state);
    }

    [HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
    static class WatchFluid {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdFluid, __state);
    }

    [HarmonyPatch(typeof(MedicalSync), "Update")]
    static class WatchMed {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdMed, __state);
    }

    [HarmonyPatch(typeof(NetObjectRegistry), "Update")]
    [HarmonyPatch(typeof(SyncInfoGameObjectTracker), "FixedUpdate")]
    static class WatchObj {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdObj, __state);
    }

    [HarmonyPatch(typeof(ScavPlayer), "Update")]
    [HarmonyPatch(typeof(ScavPlayer), "FixedUpdate")]
    [HarmonyPatch(typeof(ScavPlayer), "LateUpdate")]
    static class WatchPly {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdPly, __state);
    }

    [HarmonyPatch(typeof(NetBody), "Update")]
    [HarmonyPatch(typeof(NetBody), "FixedUpdate")]
    [HarmonyPatch(typeof(NetBody), "LateUpdate")]
    static class WatchBody {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdBody, __state);
    }

    [HarmonyPatch(typeof(Item), "Update")]
    static class WatchItems {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdItems, __state);
    }

    [HarmonyPatch(typeof(TrackerGunScript), "Update")]
    static class WatchGun {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdGun, __state);
    }

    [HarmonyPatch(typeof(Voicechat), "Update")]
    [HarmonyPatch(typeof(StreamedAudioOutput), "Update")]
    static class WatchVoice {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdVoice, __state);
    }

    [HarmonyPatch(typeof(KSteam), "Update")]
    static class WatchSteam {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdSteam, __state);
    }

    [HarmonyPatch(typeof(KrokV5Optimization.OptimizerMain), "Tick")]
    static class WatchOpt {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdOpt, __state);
    }

    [HarmonyPatch(typeof(BuildingEntity), "Update")]
    [HarmonyPatch(typeof(HeaterMultiplayerRewrite), "Update")]
    [HarmonyPatch(typeof(OnWillRenderObject_ForceForMPComponent), "Update")]
    static class WatchBldg {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdBldg, __state);
    }

    [HarmonyPatch(typeof(GeyserScript), "Update")]
    [HarmonyPatch(typeof(OilPipeScript), "Update")]
    static class WatchDrip {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) => Begin(out __state);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) => Add(IdDrip, __state);
    }
}
