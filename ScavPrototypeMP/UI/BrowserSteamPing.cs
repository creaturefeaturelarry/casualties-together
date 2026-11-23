using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

// real RTT via short-lived SteamNetworkingSockets ConnectP2P on a dedicated virtual port.
// hosts listen on VirtualPort and accept+close; browsers connect, read m_nPing, then close.
internal static class BrowserSteamPing {
    public const int VirtualPort = 13;
    public const int MaxConcurrent = 6;
    const float ProbeTimeoutSeconds = 4f;

    public static HSteamListenSocket HostListen = HSteamListenSocket.Invalid;

    static Callback<SteamNetConnectionStatusChangedCallback_t> _callback;
    static readonly SteamNetworkingConfigValue_t[] EmptyOptions = Array.Empty<SteamNetworkingConfigValue_t>();
    static readonly Dictionary<HSteamNetConnection, Probe> Probes = new Dictionary<HSteamNetConnection, Probe>();
    static readonly HashSet<ulong> InFlightOwners = new HashSet<ulong>();

    sealed class Probe {
        public NetPublicServerInfo Info;
        public ulong OwnerId;
        public HSteamNetConnection Conn;
        public float StartedAt;
        public bool GotPing;
    }

    public static int InFlightCount => Probes.Count;

    public static void EnsureCallbacks() {
        if (!KSteam.Loaded || _callback != null) {
            return;
        }
        _callback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
    }

    public static void EnsureHostListen() {
        if (!KSteam.Loaded) {
            return;
        }
        EnsureCallbacks();
        SteamNetworkingUtils.InitRelayNetworkAccess();
        if (HostListen != HSteamListenSocket.Invalid) {
            return;
        }
        HostListen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, EmptyOptions);
        if (HostListen == HSteamListenSocket.Invalid) {
            Together.log.warn("[BrowserPing] failed to create host ping listen socket");
        }
    }

    public static void CloseHostListen() {
        if (HostListen == HSteamListenSocket.Invalid) {
            return;
        }
        SteamNetworkingSockets.CloseListenSocket(HostListen);
        HostListen = HSteamListenSocket.Invalid;
    }

    public static bool IsHostPingListen(HSteamListenSocket listen) {
        return HostListen != HSteamListenSocket.Invalid && listen == HostListen;
    }

    public static bool IsProbeConnection(HSteamNetConnection conn) {
        return Probes.ContainsKey(conn);
    }

    // transportSteamworks must ignore these so ping probes are not treated as players.
    public static bool HandleTransportCallback(SteamNetConnectionStatusChangedCallback_t cb) {
        if (IsHostPingListen(cb.m_info.m_hListenSocket)) {
            HandleHostSide(cb);
            return true;
        }
        if (IsProbeConnection(cb.m_hConn)) {
            HandleProbeSide(cb);
            return true;
        }
        return false;
    }

    // the probe is removed when the ping is read. the host's later "browser_ping" close
    // then arrives with the same steam id as the game host, and used to kick the client.
    public static bool IsStrayPing(SteamNetConnectionStatusChangedCallback_t cb) {
        if (IsHostPingListen(cb.m_info.m_hListenSocket) || IsProbeConnection(cb.m_hConn)) {
            return true;
        }
        string debug = cb.m_info.m_szEndDebug ?? "";
        if (debug.IndexOf("browser_ping", StringComparison.Ordinal) >= 0) {
            return true;
        }
        string desc = cb.m_info.m_szConnectionDescription ?? "";
        return desc.IndexOf("vport " + VirtualPort, StringComparison.Ordinal) >= 0;
    }

    public static bool TryBeginProbe(NetPublicServerInfo info) {
        if (info == null || info.steam_lobby_info == null || !KSteam.Loaded) {
            return false;
        }
        if (Probes.Count >= MaxConcurrent) {
            return false;
        }

        CSteamID owner = info.steam_lobby_info.ownerID;
        if (!owner.IsValid() || owner.GetEAccountType() != EAccountType.k_EAccountTypeIndividual) {
            return false;
        }
        ulong ownerId = owner.m_SteamID;
        if (ownerId == 0 || InFlightOwners.Contains(ownerId)) {
            return false;
        }
        if (KSteam.GetLocalUserSteamID().m_SteamID == ownerId) {
            info.pingMs = 0;
            return true;
        }

        EnsureCallbacks();
        SteamNetworkingUtils.InitRelayNetworkAccess();

        SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
        identity.SetSteamID(owner);
        HSteamNetConnection conn = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, EmptyOptions);
        if (conn == HSteamNetConnection.Invalid) {
            return false;
        }

        Probe probe = new Probe {
            Info = info,
            OwnerId = ownerId,
            Conn = conn,
            StartedAt = Time.realtimeSinceStartup,
        };
        Probes[conn] = probe;
        InFlightOwners.Add(ownerId);
        return true;
    }

    public static void Tick() {
        if (Probes.Count == 0) {
            return;
        }

        float now = Time.realtimeSinceStartup;
        List<HSteamNetConnection> done = null;
        foreach (KeyValuePair<HSteamNetConnection, Probe> kv in Probes) {
            Probe probe = kv.Value;
            TryReadPing(probe);
            if (probe.GotPing) {
                if (done == null) {
                    done = new List<HSteamNetConnection>();
                }
                done.Add(kv.Key);
                continue;
            }
            if (now - probe.StartedAt >= ProbeTimeoutSeconds) {
                if (done == null) {
                    done = new List<HSteamNetConnection>();
                }
                done.Add(kv.Key);
            }
        }

        if (done == null) {
            return;
        }
        for (int i = 0; i < done.Count; i++) {
            FinishProbe(done[i], keepPing: Probes.TryGetValue(done[i], out Probe p) && p.GotPing);
        }
    }

    public static void CancelAll() {
        List<HSteamNetConnection> keys = new List<HSteamNetConnection>(Probes.Keys);
        for (int i = 0; i < keys.Count; i++) {
            FinishProbe(keys[i], keepPing: false);
        }
    }

    static void TryReadPing(Probe probe) {
        if (probe == null || probe.GotPing) {
            return;
        }
        SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
        SteamNetConnectionRealTimeLaneStatus_t lane = new SteamNetConnectionRealTimeLaneStatus_t();
        EResult result = SteamNetworkingSockets.GetConnectionRealTimeStatus(probe.Conn, ref status, 0, ref lane);
        if (result != EResult.k_EResultOK) {
            return;
        }
        int ms = status.m_nPing;
        if (ms < 0) {
            return;
        }
        if (probe.Info != null) {
            probe.Info.pingMs = ms;
        }
        probe.GotPing = true;
    }

    static void HandleHostSide(SteamNetConnectionStatusChangedCallback_t cb) {
        ESteamNetworkingConnectionState state = cb.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) {
            SteamNetworkingSockets.AcceptConnection(cb.m_hConn);
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) {
            // real RTT is available on the probe side; drop the acceptor immediately.
            SteamNetworkingSockets.CloseConnection(cb.m_hConn, 0, "browser_ping", false);
        }
    }

    static void HandleProbeSide(SteamNetConnectionStatusChangedCallback_t cb) {
        if (!Probes.TryGetValue(cb.m_hConn, out Probe probe)) {
            return;
        }
        ESteamNetworkingConnectionState state = cb.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
            || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute) {
            TryReadPing(probe);
            if (probe.GotPing) {
                FinishProbe(cb.m_hConn, keepPing: true);
            }
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
            || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
            || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None) {
            TryReadPing(probe);
            FinishProbe(cb.m_hConn, keepPing: probe.GotPing);
        }
    }

    static void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb) {
        // when transport is up it also gets this callback; HandleTransportCallback covers that path.
        // this standalone callback covers browser-open-without-net.
        if (Net.IsRunning && Net.TryGetSteamTransport(out _)) {
            return;
        }
        HandleTransportCallback(cb);
    }

    static void FinishProbe(HSteamNetConnection conn, bool keepPing) {
        if (!Probes.TryGetValue(conn, out Probe probe)) {
            return;
        }
        Probes.Remove(conn);
        InFlightOwners.Remove(probe.OwnerId);
        if (!keepPing && probe.Info != null && probe.Info.pingMs < 0) {
            // leave as unknown; do not invent a value
        }
        SteamNetworkingSockets.CloseConnection(conn, 0, "browser_ping_done", false);
    }
}