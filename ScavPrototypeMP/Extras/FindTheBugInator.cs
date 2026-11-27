using CasualtiesTogetherUtils;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Together {
    // call sites stay. nothing here writes a log or opens a file.
    internal static class FindTheBugInator {
        internal static void Mark(string evt) {
        }

        internal static void Mark(string evt, string detail) {
        }

        internal static void NoteSyncCreate(SyncInfo si) {
        }

        internal static void NoteSyncDestroy(SyncInfo si) {
        }

        internal static void NoteBodyCreate(SyncBody body) {
        }

        internal static void NoteBodyDestroy(SyncBody body) {
        }

        internal static void NotePlayerCreate(BasePlayer player) {
        }

        internal static void NotePlayerDestroy(BasePlayer player) {
        }

        internal static void NoteCreateObject(SyncInfo si, bool ok) {
        }

        internal static void NoteNewGO(GameObject obj) {
        }

        internal static void NoteMove(string why, NetBody nb, Vector2 pos, string detail) {
        }

        internal static void Tick() {
            FindTheBugReports.Tick();
            SyncStep.Pulse();
        }

        // 10s frame timers. each line is one part of the mod. the last line is the rest of the frame, the game.
        // bands are how many frames landed in that millisecond range. avg/max are ms.
        // phase lines are the unity player-loop systems inside that game time. only the slow ones are written.
        internal const int SteamCb = 0;
        internal const int Transport = 1;
        internal const int Les = 2;
        internal const int Plugin = 3;
        internal const int Chunks = 4;
        internal const int FixedStep = 5;
        internal const int Late = 6;
        internal const int Opt = 7;
        internal const int Roster = 8;
        internal const int Crystals = 9;
        internal const int Players = 10;
        internal const int Icons = 11;
        internal const int Ui = 12;
        internal const int Shared = 13;
        internal const int Game = 14;
        const int Count = 15;
        const int Bands = 6;

        static readonly string[] Names = {
            "steamcb", "transport", "les", "plugin", "chunks", "fixed", "late",
            "opt", "roster", "crystals", "players", "icons", "ui", "shared", "game",
        };

        static readonly long[] Frame = new long[Count];
        static readonly long[] Total = new long[Count];
        static readonly long[] Max = new long[Count];
        static readonly int[,] Hist = new int[Count, Bands];

        static long _frameStart;
        static long _frameMod;
        static bool _open;
        static bool _installed;
        static int _frames;
        static long _windowStart;
        static long _inBytes;
        static long _outBytes;
        static long _inMsgs;
        static long _outMsgs;
        static readonly StringBuilder _sb = new StringBuilder(2048);

        const int PhaseCap = 128;
        static readonly string[] PhaseNames = new string[PhaseCap];
        static readonly long[] PhaseStart = new long[PhaseCap];
        static readonly long[] PhaseFrame = new long[PhaseCap];
        static readonly long[] PhaseTotal = new long[PhaseCap];
        static readonly long[] PhaseMax = new long[PhaseCap];
        static readonly int[] PhaseRank = new int[PhaseCap];
        static int _phaseCount;

        internal readonly struct Scope : IDisposable {
            readonly int _id;
            readonly long _t;

            internal Scope(int id) {
                _id = id;
                _t = Stopwatch.GetTimestamp();
            }

            public void Dispose() {
                Add(_id, Stopwatch.GetTimestamp() - _t);
            }
        }

        internal static Scope Time(int id) {
            return new Scope(id);
        }

        internal static void NoteSteamIn(int bytes) {
            if (bytes <= 0) {
                return;
            }
            Interlocked.Add(ref _inBytes, bytes);
            Interlocked.Increment(ref _inMsgs);
        }

        internal static void NoteSteamOut(int bytes) {
            if (bytes <= 0) {
                return;
            }
            Interlocked.Add(ref _outBytes, bytes);
            Interlocked.Increment(ref _outMsgs);
        }

        internal static void NoteSteamIo(long inBytes, long inMsgs, long outBytes, long outMsgs) {
            if (inBytes > 0) {
                Interlocked.Add(ref _inBytes, inBytes);
            }
            if (inMsgs > 0) {
                Interlocked.Add(ref _inMsgs, inMsgs);
            }
            if (outBytes > 0) {
                Interlocked.Add(ref _outBytes, outBytes);
            }
            if (outMsgs > 0) {
                Interlocked.Add(ref _outMsgs, outMsgs);
            }
        }

        internal static void Install() {
            if (_installed) {
                return;
            }
            try {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                if (loop.subSystemList == null || loop.subSystemList.Length == 0) {
                    return;
                }
                PlayerLoopSystem[] list = new PlayerLoopSystem[loop.subSystemList.Length + 2];
                list[0] = new PlayerLoopSystem {
                    type = typeof(FindTheBugInator),
                    updateDelegate = BeginFrame
                };
                Array.Copy(loop.subSystemList, 0, list, 1, loop.subSystemList.Length);
                for (int i = 1; i < list.Length - 1; i++) {
                    TimeChildren(ref list[i]);
                }
                list[list.Length - 1] = new PlayerLoopSystem {
                    type = typeof(FrameEnd),
                    updateDelegate = EndFrame
                };
                loop.subSystemList = list;
                PlayerLoop.SetPlayerLoop(loop);
                _installed = true;
                _windowStart = Stopwatch.GetTimestamp();
                SyncStep.Install();
            } catch (Exception e) {
                log.l("[findthebuginator] install failed " + e.Message, false);
            }
        }

        static void Add(int id, long ticks) {
            if (!_open || (uint)id >= (uint)Game || ticks <= 0) {
                return;
            }
            Frame[id] += ticks;
            _frameMod += ticks;
        }

        static void BeginFrame() {
            _frameStart = Stopwatch.GetTimestamp();
            _frameMod = 0;
            Array.Clear(Frame, 0, Frame.Length);
            if (_phaseCount > 0) {
                Array.Clear(PhaseFrame, 0, _phaseCount);
                Array.Clear(PhaseStart, 0, _phaseCount);
            }
            _open = true;
        }

        static void EndFrame() {
            if (!_open) {
                return;
            }
            _open = false;
            long wall = Stopwatch.GetTimestamp() - _frameStart;
            if (wall < 0) {
                wall = 0;
            }
            long game = wall - _frameMod;
            if (game < 0) {
                game = 0;
            }
            Frame[Game] = game;
            for (int i = 0; i < Count; i++) {
                long ticks = Frame[i];
                Total[i] += ticks;
                if (ticks > Max[i]) {
                    Max[i] = ticks;
                }
                Hist[i, Band(ticks)]++;
            }
            for (int p = 0; p < _phaseCount; p++) {
                long ticks = PhaseFrame[p];
                PhaseTotal[p] += ticks;
                if (ticks > PhaseMax[p]) {
                    PhaseMax[p] = ticks;
                }
            }
            _frames++;
            long now = Stopwatch.GetTimestamp();
            if (now - _windowStart < Stopwatch.Frequency * 10L) {
                return;
            }
            Report(now);
        }

        static int Band(long ticks) {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            if (ms < 1.0) {
                return 0;
            }
            if (ms < 4.0) {
                return 1;
            }
            if (ms < 8.0) {
                return 2;
            }
            if (ms < 16.0) {
                return 3;
            }
            if (ms < 33.0) {
                return 4;
            }
            return 5;
        }

        static void Report(long now) {
            double sec = (now - _windowStart) / (double)Stopwatch.Frequency;
            if (sec < 0.1) {
                sec = 0.1;
            }
            int frames = _frames;
            if (frames < 1) {
                frames = 1;
            }
            long inBytes = Interlocked.Exchange(ref _inBytes, 0);
            long outBytes = Interlocked.Exchange(ref _outBytes, 0);
            long inMsgs = Interlocked.Exchange(ref _inMsgs, 0);
            long outMsgs = Interlocked.Exchange(ref _outMsgs, 0);

            _sb.Clear();
            _sb.Append("[findthebuginator] ");
            _sb.Append(sec.ToString("0.0"));
            _sb.Append("s frames=");
            _sb.Append(_frames);
            _sb.Append(" bands <1 1-4 4-8 8-16 16-33 33+");
            _sb.Append(" steamOut=");
            _sb.Append((outBytes / 1024.0 / sec).ToString("0.0"));
            _sb.Append("KB/s in=");
            _sb.Append((inBytes / 1024.0 / sec).ToString("0.0"));
            _sb.Append("KB/s msgs ");
            _sb.Append((outMsgs / sec).ToString("0"));
            _sb.Append("/s out ");
            _sb.Append((inMsgs / sec).ToString("0"));
            _sb.Append("/s in");
            for (int i = 0; i < Count; i++) {
                _sb.Append('\n');
                _sb.Append(Names[i]);
                _sb.Append(' ');
                _sb.Append((Total[i] * 1000.0 / Stopwatch.Frequency / frames).ToString("0.00"));
                _sb.Append('/');
                _sb.Append((Max[i] * 1000.0 / Stopwatch.Frequency).ToString("0.00"));
                _sb.Append("ms");
                for (int b = 0; b < Bands; b++) {
                    _sb.Append(' ');
                    _sb.Append(Hist[i, b]);
                }
            }
            AppendPhases(frames);
            LeaveGear.AppendStatus(_sb);
            log.l(_sb.ToString(), false);

            Array.Clear(Total, 0, Total.Length);
            Array.Clear(Max, 0, Max.Length);
            Array.Clear(Hist, 0, Hist.Length);
            if (_phaseCount > 0) {
                Array.Clear(PhaseTotal, 0, _phaseCount);
                Array.Clear(PhaseMax, 0, _phaseCount);
            }
            _frames = 0;
            _windowStart = now;
        }

        static int AddPhase(string name) {
            if (_phaseCount >= PhaseCap) {
                return -1;
            }
            int id = _phaseCount++;
            PhaseNames[id] = name;
            return id;
        }

        static void TimeChildren(ref PlayerLoopSystem parent) {
            PlayerLoopSystem[] kids = parent.subSystemList;
            if (kids == null || kids.Length == 0) {
                return;
            }
            string parentName = parent.type != null ? parent.type.Name : "loop";
            PlayerLoopSystem[] next = new PlayerLoopSystem[kids.Length * 3];
            int n = 0;
            for (int i = 0; i < kids.Length; i++) {
                PlayerLoopSystem kid = kids[i];
                string kidName = kid.type != null ? kid.type.Name : "sys";
                int id = AddPhase(parentName + "." + kidName);
                if (id >= 0) {
                    next[n++] = Bookend(id, true);
                }
                next[n++] = kid;
                if (id >= 0) {
                    next[n++] = Bookend(id, false);
                }
            }
            if (n < next.Length) {
                Array.Resize(ref next, n);
            }
            parent.subSystemList = next;
        }

        static PlayerLoopSystem Bookend(int id, bool begin) {
            int captured = id;
            if (begin) {
                return new PlayerLoopSystem {
                    type = typeof(PhaseBegin),
                    updateDelegate = () => BeginPhase(captured)
                };
            }
            return new PlayerLoopSystem {
                type = typeof(PhaseEnd),
                updateDelegate = () => EndPhase(captured)
            };
        }

        static void BeginPhase(int id) {
            PhaseStart[id] = Stopwatch.GetTimestamp();
        }

        static void EndPhase(int id) {
            long start = PhaseStart[id];
            PhaseStart[id] = 0;
            if (start <= 0 || !_open) {
                return;
            }
            long ticks = Stopwatch.GetTimestamp() - start;
            if (ticks > 0) {
                PhaseFrame[id] += ticks;
            }
        }

        static void AppendPhases(int frames) {
            int keep = 0;
            for (int i = 0; i < _phaseCount; i++) {
                double avg = PhaseTotal[i] * 1000.0 / Stopwatch.Frequency / frames;
                double max = PhaseMax[i] * 1000.0 / Stopwatch.Frequency;
                if (avg < 1.0 && max < 16.0) {
                    continue;
                }
                PhaseRank[keep++] = i;
            }
            for (int a = 0; a < keep; a++) {
                int best = a;
                for (int b = a + 1; b < keep; b++) {
                    if (PhaseTotal[PhaseRank[b]] > PhaseTotal[PhaseRank[best]]) {
                        best = b;
                    }
                }
                int swap = PhaseRank[a];
                PhaseRank[a] = PhaseRank[best];
                PhaseRank[best] = swap;
            }
            int limit = keep < 8 ? keep : 8;
            for (int i = 0; i < limit; i++) {
                int id = PhaseRank[i];
                _sb.Append('\n');
                _sb.Append("phase ");
                _sb.Append(PhaseNames[id]);
                _sb.Append(' ');
                _sb.Append((PhaseTotal[id] * 1000.0 / Stopwatch.Frequency / frames).ToString("0.00"));
                _sb.Append('/');
                _sb.Append((PhaseMax[id] * 1000.0 / Stopwatch.Frequency).ToString("0.00"));
                _sb.Append("ms");
            }
        }

        sealed class FrameEnd { }
        sealed class PhaseBegin { }
        sealed class PhaseEnd { }
    }
}