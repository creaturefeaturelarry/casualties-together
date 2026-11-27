using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Together {
    // The native thread is CreateThread. It is not a Unity thread, so a Steam
    // call that blocks there does not stall the frame.
    static class SteamIoNative {
        const string Dll = "TogetherSteamIo.dll";

        [StructLayout(LayoutKind.Sequential)]
        struct NativeConn {
            public ulong SteamId;
            public uint Conn;
            public uint Ready;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr SteamIo_Create();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_Start(IntPtr io);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_Running(IntPtr io);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_Stop(IntPtr io);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_Destroy(IntPtr io);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_Kick(IntPtr io);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_SetMode(IntPtr io, int isClient, int isGameServer);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_SetConns(IntPtr io, IntPtr conns, int count);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_PushOut(IntPtr io, ulong steamId, uint conn, int flag, int epoch, IntPtr data, int size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_PushPaced(IntPtr io, ulong steamId, uint conn, int flag, int epoch, IntPtr data, int size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_PushFlush(IntPtr io, uint conn);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_PopIn(IntPtr io, out ulong steamId, out IntPtr data, out int size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_Free(IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_PopFail(IntPtr io, out int result, out uint conn, out int msg, out int known);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern long SteamIo_TakeLimit(IntPtr io);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamIo_TakeBytes(IntPtr io, out long inBytes, out long inMsgs, out long outBytes, out long outMsgs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int SteamIo_CopyStatus(IntPtr io, IntPtr dst, int cap, out int pollUs);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeStatus {
            internal ulong SteamId;
            internal int Ok;
            internal int State;
            internal int Ping;
            internal float QualityLocal;
            internal float QualityRemote;
            internal float OutPps;
            internal float OutBps;
            internal float InPps;
            internal float InBps;
            internal int SendRate;
            internal int PendingUnreliable;
            internal int PendingReliable;
            internal int SentUnacked;
            internal int Pad;
            internal long QueueTime;
        }

        static IntPtr _module;

        internal static IntPtr TryStart(out string error) {
            error = null;
            if (_module == IntPtr.Zero) {
                string dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(dir, Dll);
                _module = LoadLibraryW(path);
                if (_module == IntPtr.Zero) {
                    error = "LoadLibrary failed for " + path;
                    return IntPtr.Zero;
                }
            }
            IntPtr io = SteamIo_Create();
            if (io == IntPtr.Zero) {
                error = "SteamIo_Create failed";
                return IntPtr.Zero;
            }
            SteamIo_Start(io);
            if (SteamIo_Running(io) == 0) {
                SteamIo_Destroy(io);
                error = "CreateThread failed";
                return IntPtr.Zero;
            }
            return io;
        }

        internal static void Stop(IntPtr io) {
            if (io == IntPtr.Zero) {
                return;
            }
            SteamIo_Stop(io);
            SteamIo_Destroy(io);
        }

        internal static void Kick(IntPtr io) {
            if (io != IntPtr.Zero) {
                SteamIo_Kick(io);
            }
        }

        internal static unsafe void Publish(IntPtr io, TransportSteamworks.PollTarget[] snap, bool client, bool gameServer) {
            if (io == IntPtr.Zero) {
                return;
            }
            SteamIo_SetMode(io, client ? 1 : 0, gameServer ? 1 : 0);
            int n = snap == null ? 0 : snap.Length;
            if (n > 64) {
                n = 64;
            }
            if (n == 0) {
                SteamIo_SetConns(io, IntPtr.Zero, 0);
                return;
            }
            NativeConn* tmp = stackalloc NativeConn[64];
            for (int i = 0; i < n; i++) {
                tmp[i].SteamId = snap[i].SteamId;
                tmp[i].Conn = snap[i].Conn.m_HSteamNetConnection;
                tmp[i].Ready = snap[i].Ready ? 1u : 0u;
            }
            SteamIo_SetConns(io, (IntPtr)tmp, n);
        }

        internal static unsafe bool PushOut(IntPtr io, ulong steamId, uint conn, int flag, int epoch, ReadOnlySpan<byte> data) {
            if (io == IntPtr.Zero) {
                return false;
            }
            if (data.Length == 0) {
                return SteamIo_PushOut(io, steamId, conn, flag, epoch, IntPtr.Zero, 0) != 0;
            }
            fixed (byte* p = data) {
                return SteamIo_PushOut(io, steamId, conn, flag, epoch, (IntPtr)p, data.Length) != 0;
            }
        }

        internal static unsafe bool PushPaced(IntPtr io, ulong steamId, uint conn, int flag, int epoch, ReadOnlySpan<byte> data) {
            if (io == IntPtr.Zero) {
                return false;
            }
            if (data.Length == 0) {
                return SteamIo_PushPaced(io, steamId, conn, flag, epoch, IntPtr.Zero, 0) != 0;
            }
            fixed (byte* p = data) {
                return SteamIo_PushPaced(io, steamId, conn, flag, epoch, (IntPtr)p, data.Length) != 0;
            }
        }

        internal static bool PushFlush(IntPtr io, uint conn) {
            return io != IntPtr.Zero && SteamIo_PushFlush(io, conn) != 0;
        }

        internal static bool PopIn(IntPtr io, out ulong steamId, out byte[] data) {
            steamId = 0;
            data = null;
            if (io == IntPtr.Zero || SteamIo_PopIn(io, out steamId, out IntPtr ptr, out int size) == 0) {
                return false;
            }
            if (size < 0) {
                size = 0;
            }
            data = new byte[size];
            if (size > 0 && ptr != IntPtr.Zero) {
                Marshal.Copy(ptr, data, 0, size);
            }
            if (ptr != IntPtr.Zero) {
                SteamIo_Free(ptr);
            }
            return true;
        }

        internal static bool PopFail(IntPtr io, out int result, out uint conn, out int msg, out int known) {
            result = 0;
            conn = 0;
            msg = 0;
            known = 0;
            if (io == IntPtr.Zero) {
                return false;
            }
            return SteamIo_PopFail(io, out result, out conn, out msg, out known) != 0;
        }

        internal static long TakeLimit(IntPtr io) {
            return io == IntPtr.Zero ? 0 : SteamIo_TakeLimit(io);
        }

        internal static void TakeBytes(IntPtr io) {
            if (io == IntPtr.Zero) {
                return;
            }
            SteamIo_TakeBytes(io, out long inBytes, out long inMsgs, out long outBytes, out long outMsgs);
            FindTheBugInator.NoteSteamIo(inBytes, inMsgs, outBytes, outMsgs);
        }

        internal static int CopyStatus(IntPtr io, NativeStatus[] dst, out int pollUs) {
            pollUs = 0;
            if (io == IntPtr.Zero || dst == null || dst.Length == 0) {
                return 0;
            }
            GCHandle pin = GCHandle.Alloc(dst, GCHandleType.Pinned);
            try {
                return SteamIo_CopyStatus(io, pin.AddrOfPinnedObject(), dst.Length, out pollUs);
            } finally {
                pin.Free();
            }
        }
    }
}
