using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CasualtiesTogetherUtils;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;

namespace Together;

// The lobby stays alive while the game transport and LES managers change roles.
// Ordinary ShutdownNetwork leaves Steam and must never be used for this step.
internal static class TransferTransport {
    internal static bool CanSendCheckpoint(ulong recipient) {
        if (!Net.TryGetSteamTransport(out var transport) || !transport.connectionMapping.TryGetValue(recipient, out var connection))
            throw new IOException("Checkpoint recipient disconnected.");
        var status = new SteamNetConnectionRealTimeStatus_t();
        var lane = new SteamNetConnectionRealTimeLaneStatus_t();
        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(connection.connection, ref status, 0, ref lane) != EResult.k_EResultOK)
            throw new IOException("Checkpoint connection unavailable.");
        // Native sends otherwise drop a reliable packet when Steam's bounded
        // send queue fills. Leave room for normal lobby/control traffic.
        return (long)status.m_cbPendingReliable + status.m_cbSentUnackedReliable < 256 * 1024;
    }

    internal static void Detach() {
        if (!Util.IsInMainMenu() || !Net.TryGetSteamTransport(out var old))
            throw new InvalidOperationException("Host transfer requires a Steam lobby at the main menu.");
#if FULLGAME
        // Clear old ready-gate selections/timers before player destruction can
        // schedule a new run in the newly handed-over lobby.
        CharacterSelectWait.Reset();
#endif
        old.Shutdown(false);
        Net.Transport = null;
        var rules = Multiplayer.rules;
        var bodies = ScavPlayer.AllPlayers.Where(p => p != null && !p.IsLocal && p.body != null).Select(p => p.body).ToArray();
        try {
            // Player destruction must not replace the real checkpoint with menu bodies.
            var temporary = rules;
            temporary.SavePlayerState = temporary.SavePlayerInventory = temporary.SavePlayerPosition = false;
            Multiplayer.rules = temporary;
            Net.ShutdownReset();
            foreach (var body in bodies) if (body != null) UnityEngine.Object.Destroy(body.gameObject);
        } finally { Multiplayer.rules = rules; }
    }

    internal static void Start(bool host, ulong lobby, ulong owner, string password, string runSettings, bool includesSave) {
        if (!KSteam.IS_IN_LOBBY || SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID != owner)
            throw new InvalidOperationException("Steam ownership changed during handoff.");
        KSteam.CURRENT_LOBBY.lobby_steamID = new CSteamID(lobby);
        KSteam.CURRENT_LOBBY.ownerID = new CSteamID(owner);
        Multiplayer.INPUT_PASSWORD = password;
        if (host) {
            if (includesSave) PlayerCheckpoint.OnHostTransferInstalled();
            if (Net.CurrentServerInfo != null) Net.MyServerInfo = Net.CurrentServerInfo;
            // Apply before opening the listener: reconnecting players must see the
            // transferred rules, not the recipient's previous lobby defaults.
            if (includesSave) {
                var header = JObject.Parse(File.ReadAllText(Path.Combine(SavesystemPatch.mpsavefolder, "mp_rules.json")));
                Multiplayer.rules = header["RULES"].ToObject<MultiplayerGameRules>();
            }
            WorldMP.ApplyRunSettings(in runSettings);
        }
        Net.TransportCreated(host ? Net.NetType.Host : Net.NetType.Client, false);
        var transport = new TransportSteamworks();
        if (!transport.IsInitialized) throw new InvalidOperationException("Steam transport unavailable.");
        Net.Transport = transport;
        if (!(host ? transport.CreateServerSocket() : transport.CreateClientSocket()))
            throw new InvalidOperationException("Could not create the new Steam connection.");
        Net.OnTransportConnected();
        if (host) KSteam.Server_UpdateLobbyData();
        else {
            var intro = Multiplayer.GetClientConnectIntroductionPacket();
            transport.SendThroughSteamSocket(owner, new ArraySegment<byte>(intro.Data, 0, intro.Length),
                Steamworks.Constants.k_nSteamNetworkingSend_Reliable);
        }
    }

    internal static bool SkipTransferredRootBody() {
        if (!LobbyHostTransfer.AwaitingTransferredLoad) return false;
        string root = SavesystemPatch.mpsavefolder;
        ulong local = KSteam.GetLocalUserSteamID().m_SteamID;
        return TransferArchive.NeedsFreshHost(root, local);
    }

    // QoL is optional. Scope its slot override to the save/load call only.
    internal static IDisposable SelectQoLSave() {
        Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("QoL_Unknown.SaveManager")).FirstOrDefault(t => t != null);
        FieldInfo field = type?.GetField("CurrentSaveFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (type != null && field == null) throw new MissingFieldException("QoL save selection changed.");
        return new SlotScope(field);
    }
    private sealed class SlotScope : IDisposable {
        private readonly FieldInfo field; private readonly object previous;
        internal SlotScope(FieldInfo value) { field = value; previous = field?.GetValue(null); field?.SetValue(null, "save.sv"); }
        public void Dispose() { field?.SetValue(null, previous); }
    }
}
