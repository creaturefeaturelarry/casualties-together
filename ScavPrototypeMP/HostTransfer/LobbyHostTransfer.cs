using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using UnityEngine;

namespace Together
{
    internal sealed class LobbyHostTransfer : CasualtiesTogetherSingleton
    {
        internal enum TransferMode : byte { NextLayer, LobbyWithSave, LobbyOnly }
        // The transfer code stays compiled. The singleton loader and the menu button both check this.
        internal const bool Enabled = true;
        private enum Phase { Idle, Offered, Accepted, Preparing, Ready, CommitApproved, Switching, Reconnecting, Resuming }
        private enum Message : byte { Offer, Answer, Prepare, Begin, Chunk, Ready, Abort, Arrived, Complete, Commit, CommitAck }
        private const string MessageName = "together.host-transfer.v2";
        private const int Protocol = 2, ChunkSize = 12288;
        private static LobbyHostTransfer instance;
        private static string id, targetName, stage, password, runSettings;
        private static ulong lobby, originalHost, target;
        private static Phase phase;
        private const string ReadyLobbyKey = "together_transfer_ready";
        private const string CompleteLobbyKey = "together_transfer_complete", ReturnersLobbyKey = "together_transfer_returners";
        private const double ReconnectGraceSeconds = 5;
        private static double reconnectDeadline;
        private static ulong returnLobby, returnHost;
        private static readonly HashSet<ulong> returners = new HashSet<ulong>();
        private static bool oldConnectionClosed;
        private static double nextArrival;
        private static double deadline;
        private static int depth, expectedBytes, received;
        private static string expectedHash;
        private static byte[] incoming;
        private static HashSet<ulong> members = new HashSet<ulong>(), ready = new HashSet<ulong>(), arrived = new HashSet<ulong>();
        private static readonly HashSet<ulong> commitAcks = new HashSet<ulong>();
        private static WorldGeneration queuedWorld;
        private static bool boundaryArmed;
        private static bool pendingTransferredLoad;
        private static ulong pendingLoadLobby;
        private static TransferMode mode;
        private static bool continueWhenAccepted;
        private static double autoResumeDeadline;
        private static bool holdLobbyAutoStart;
        private static ulong heldLobby;
        private static ulong Local => KSteam.GetLocalUserSteamID().m_SteamID;
        private static bool Accepted => phase >= Phase.Accepted && phase <= Phase.Reconnecting;
        private static bool Preparing => phase >= Phase.Preparing && phase <= Phase.CommitApproved;
        private static bool Switching => phase == Phase.Switching || phase == Phase.Reconnecting;
        internal static bool HasRequest => id != null;
        internal static bool CanCancel => id != null && Local == originalHost && !Switching;
        internal static bool IncludesSave => mode != TransferMode.LobbyOnly;
        internal static bool PauseAutoContinue => HostTransferUI.SelectingAtExit || (id != null && mode == TransferMode.NextLayer && !Accepted);
        internal static string Status => id == null ? (AwaitingTransferredLoad ? "You are hosting the transferred expedition. Select Resume Expedition." : "No transfer queued.")
            : targetName + ": " + (phase == Phase.Reconnecting && Local == target ? "reconnecting players (" + Math.Ceiling(Math.Max(0, reconnectDeadline - Time.realtimeSinceStartupAsDouble)) + "s remaining)"
                : Switching ? "reconnecting players" : Preparing ? "preparing transfer" : Accepted ? "accepted; waiting to continue" : "waiting for acceptance");
        internal static bool IsCurrentOffer(string token) => id == token && Local == target && phase == Phase.Offered;
        internal static bool Busy => phase >= Phase.Preparing;
        internal static bool AwaitingTransferredLoad => pendingTransferredLoad && Net.IsRunning && Net.IsHost && KSteam.LobbyId.m_SteamID == pendingLoadLobby;
        internal static bool HoldLobbyAutoStart => holdLobbyAutoStart && Net.IsRunning && Net.IsHost && KSteam.LobbyId.m_SteamID == heldLobby;

        // This exception only admits the authenticated Steam identities from the
        // transferred party. Password, version, capacity and ban checks still apply.
        internal static bool CanReturnAfterTransfer(ulong steam) => steam != 0 && Net.IsRunningSteam && Net.IsHost
            && KSteam.LobbyId.m_SteamID == returnLobby && Local == returnHost
            && SteamMatchmaking.GetLobbyOwner(new CSteamID(returnLobby)).m_SteamID == returnHost && returners.Contains(steam);

        internal static bool CanRejoinLobby(CSteamID value)
        {
            if (!KSteam.Loaded) return false;
            string entry = SteamMatchmaking.GetLobbyData(value, ReturnersLobbyKey);
            int split = entry.IndexOf(':');
            return split == 32 && Guid.TryParseExact(entry.Substring(0, split), "N", out _)
                && entry.Substring(split + 1).Split(',').Contains(Local.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private void Awake()
        {
            instance = this;
            Multiplayer.RegisterCustomClientReceiver(MessageName, ClientMessage);
            Multiplayer.RegisterCustomServerReceiver(MessageName, ServerMessage);
            WorldMP.OnWorldgenFinish += OnWorldFinished;
        }
        private static void Say(string text)
        {
            Plugin.Logger?.LogInfo("[LobbyTransfer] " + text);
            ConsoleScript.instance?.LogToConsole("[LobbyTransfer] " + text);
            Multiplayer.DoMultiplayerStatusMessageLog("[LobbyTransfer] " + text);
        }
        private static string ReadText(NetDataReader r) => r.GetString(32768);
        private static NetDataWriter Packet(Message kind)
        {
            var w = Multiplayer.CreateNamedWriter(MessageName);
            w.Put(Protocol); w.Put((byte)kind); w.Put(id ?? ""); return w;
        }
        private static NetDataWriter RequestPacket(Message kind)
        {
            var w = Packet(kind);
            w.Put(lobby); w.Put(originalHost); w.Put(target); w.Put(targetName); w.Put((byte)mode);
            return w;
        }
        private static void EnterPhase(Phase next, double timeout)
        {
            phase = next;
            deadline = Time.realtimeSinceStartupAsDouble + timeout;
        }
        private static void SendAll(NetDataWriter w) => Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, ServerMain.AllClientIdsExceptHost);
        private static void Reply(Message kind, bool yes = true)
        {
            var w = Packet(kind); w.Put(yes); Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
        }
        private static ScavPlayer Find(ulong steam) => ScavPlayer.AllPlayers.FirstOrDefault(p => p != null && p.SteamId == steam);
        private static HashSet<ulong> CurrentMembers() => new HashSet<ulong>(ScavPlayer.AllPlayers.Where(p => p != null).Select(p => p.SteamId));

        internal static void Request(ScavPlayer recipient, TransferMode requestedMode)
        {
            if (!Net.IsRunningSteam || !Net.IsHost || !Net.IsRunning) throw new InvalidOperationException("Only the Steam lobby host can transfer hosting.");
            if (HasRequest || Busy) throw new InvalidOperationException("Wait for or cancel the existing transfer first.");
            if (recipient == null || recipient.IsLocal || recipient.SteamId == 0 || Find(recipient.SteamId) != recipient)
                throw new ArgumentException("Choose a victim to host.");
            if (requestedMode == TransferMode.NextLayer)
            {
                if (!Util.IsInWorld() || WorldGeneration.world.generatingWorld || WorldGeneration.world.doPod || WorldGeneration.world.biomeDepth == 10 || SavesystemPatch.PreserveSave || SavesystemPatch.InDebugMap)
                    throw new InvalidOperationException("Transfer from an ordinary expedition layer.");
            }
            else if (!Util.IsInMainMenu()) throw new InvalidOperationException("Lobby transfer requires the main menu.");
            if (requestedMode == TransferMode.LobbyWithSave && !HasSavedExpedition)
                throw new InvalidOperationException("No mp save is available to transfer.");
            mode = requestedMode; continueWhenAccepted = mode == TransferMode.NextLayer;
            id = Guid.NewGuid().ToString("N"); targetName = recipient.playerName; target = recipient.SteamId;
            originalHost = Local; lobby = KSteam.LobbyId.m_SteamID; queuedWorld = WorldGeneration.world;
            EnterPhase(Phase.Offered, 60);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, RequestPacket(Message.Offer), recipient.playerId);
            Say("Waiting for " + targetName + " to accept hosting. Keep your game open until the transfer completes.");
        }

        internal static bool HasSavedExpedition => File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, "save.sv"))
            && File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, "mp_rules.json"));

        internal static string OfferWarning(TransferMode value) => value == TransferMode.LobbyOnly
            ? "You will become the lobby host. Your mp save will not be replaced. Everyone will stay in the lobby."
            : "The host's save will be stored in a separate host-transfer slot. Your normal multiplayer save will be kept. Any previous host-transfer save will be backed up. Open the console and enter openfolder saves to access your save files.\n\n"
                + "Your character is restored if it exists in this save, otherwise you join with a fresh character.\n\n"
                + (value == TransferMode.NextLayer ? "After this layer is continued, the new host waits for all player connections, then resumes automatically." : "The handoff waits up to five seconds for reconnections. Everyone stays in the lobby.");

        internal static void AnswerOffer(string token, bool yes)
        {
            if (!IsCurrentOffer(token)) throw new InvalidOperationException("This offer is no longer available.");
            if (yes) EnterPhase(Phase.Accepted, double.PositiveInfinity);
            Reply(Message.Answer, yes);
            Say(yes ? "Hosting accepted. " + (mode == TransferMode.NextLayer ? "Waiting for the layer to continue." : "Preparing lobby transfer.") : "Hosting declined.");
            if (!yes) Reset();
        }

        internal static void Cancel() { if (!CanCancel) throw new InvalidOperationException("Transfer can no longer be cancelled."); Abort("Cancelled by host."); }

        private static void ClientMessage(NetDataReader r)
        {
            try
            {
                if (r.AvailableBytes > 100000) return;
                if (r.GetInt() != Protocol) return;
                var kind = (Message)r.GetByte(); string token = ReadText(r);
                if (!Guid.TryParseExact(token, "N", out _)) return;
                if (kind == Message.Offer || kind == Message.Prepare)
                {
                    ulong l = r.GetULong(), host = r.GetULong(), t = r.GetULong(); string n = ReadText(r);
                    var requestedMode = (TransferMode)r.GetByte();
                    if (requestedMode > TransferMode.LobbyOnly) throw new InvalidDataException("Unknown host transfer mode.");
                    if (!Net.IsClient || l != KSteam.LobbyId.m_SteamID || host != SteamMatchmaking.GetLobbyOwner(new CSteamID(l)).m_SteamID) return;
                    if (id != null && id != token) return;
                    if (kind == Message.Offer && t != Local) return;
                    if (kind == Message.Prepare && t == Local && (id != token || !Accepted)) return;
                    if (kind == Message.Prepare && t == Local && mode != requestedMode) throw new InvalidDataException("Save option changed after acceptance.");
                    mode = requestedMode;
                    if (id == null) phase = Phase.Offered;
                    id = token; lobby = l; originalHost = host; target = t; targetName = n;
                    deadline = Time.realtimeSinceStartupAsDouble + (kind == Message.Offer ? 60 : 120);
                    if (kind == Message.Offer)
                    {
                        Say(OfferWarning(mode));
                        HostTransferUI.ShowOffer(token, Find(host)?.playerName ?? "The host", mode);
                        return;
                    }
                    
                    password = ReadText(r); runSettings = ReadText(r); depth = r.GetInt();
                    int count = r.GetInt(); if (count < 2 || count > 200) throw new InvalidDataException("Invalid member count.");
                    members = new HashSet<ulong>(); for (int i = 0; i < count; i++) members.Add(r.GetULong());
                    if (!members.Contains(Local) || !members.Contains(target) || !members.Contains(originalHost) || members.Count != count) throw new InvalidDataException("Invalid members.");
                    if (phase < Phase.Preparing) phase = Phase.Preparing;
                    Say(mode == TransferMode.NextLayer ? "Returning to the lobby to transfer hosting." : "Transferring lobby hosting. Please wait..."); return;
                }
                if (id != token) return;
                if (kind == Message.Abort && !Switching) { Say("Transfer cancelled; the original host remains in control."); Reset(); return; }
                if (kind == Message.Commit && (phase == Phase.Ready || phase == Phase.CommitApproved))
                { phase = Phase.CommitApproved; Reply(Message.CommitAck); return; }
                if (kind == Message.Complete && Switching && Net.IsClient && KSteam.CURRENT_LOBBY.ownerID.m_SteamID == target
                    && SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID == target)
                {
                    CompleteClient(); return;
                }
                if (!Preparing || Local != target) return;
                if (kind == Message.Begin)
                {
                    if (incoming != null || stage != null) throw new InvalidDataException("Duplicate checkpoint header.");
                    expectedBytes = r.GetInt(); expectedHash = ReadText(r);
                    if (expectedBytes < 1 || expectedBytes > TransferArchive.MaxBytes || expectedHash.Length != 64) throw new InvalidDataException("Invalid checkpoint size/hash.");
                    incoming = new byte[expectedBytes]; received = 0;
                }
                else if (kind == Message.Chunk)
                {
                    int offset = r.GetInt(), length = r.GetInt();
                    if (incoming == null || offset != received || length < 1 || length > ChunkSize || length > expectedBytes - received || length > r.AvailableBytes)
                        throw new InvalidDataException("Invalid checkpoint chunk.");
                    byte[] bytes = new byte[length]; r.GetBytes(bytes, length);
                    Buffer.BlockCopy(bytes, 0, incoming, received, bytes.Length); received += bytes.Length;
                    if (received == expectedBytes)
                    {
                        if (TransferArchive.Hash(incoming) != expectedHash) throw new InvalidDataException("Checkpoint hash mismatch.");
                        stage = TransferArchive.Stage(incoming, SavesystemPatch.HostSaveFolder, id);
                        if (mode == TransferMode.NextLayer) TransferArchive.ValidateCheckpoint(stage, target, depth, members);
                        else TransferArchive.ValidateSaved(stage, target);
                        incoming = null; Say("Checkpoint verified and staged; existing saves untouched.");
                    }
                }
            }
            catch (Exception ex)
            {
                Say("Transfer packet rejected: " + ex.Message);
                if (id != null && !Switching) { try { Reply(Message.Ready, false); } catch { } }
                Abort("Transfer packet could not be processed.");
            }
        }
        private static void ServerMessage(ScavPlayer p, NetDataReader r)
        {
            try
            {
                if (r.GetInt() != Protocol) return;
                var kind = (Message)r.GetByte(); string token = ReadText(r); bool yes = r.GetBool();
                if (token != id || id == null || p == null) return;
                if (kind == Message.Answer && p.SteamId == target && (phase == Phase.Offered || phase == Phase.Accepted))
                {
                    if (!yes) { Abort("Recipient declined."); return; }
                    EnterPhase(Phase.Accepted, double.PositiveInfinity);
                    Say(targetName + " accepted. " + (mode == TransferMode.NextLayer ? "Transfer will run when the layer continues." : "Preparing the lobby transfer."));
                    if (mode != TransferMode.NextLayer)
                    {
                        if (!Util.IsInMainMenu()) { Abort("The host left the lobby before acceptance. Asshole."); return; }
                        EnterPhase(Phase.Preparing, 120);
                        instance.StartCoroutine(Safe(Prepare()));
                    }
                }
                else if (kind == Message.Ready && Preparing && members.Contains(p.SteamId))
                { if (!yes) { Abort("A peer could not prepare."); return; } ready.Add(p.SteamId); }
                else if (kind == Message.CommitAck && phase == Phase.CommitApproved && members.Contains(p.SteamId) && yes) commitAcks.Add(p.SteamId);
                else if (kind == Message.Arrived && Switching && Local == target && members.Contains(p.SteamId) && yes) arrived.Add(p.SteamId);
            }
            catch (Exception ex) { Plugin.Logger?.LogWarning("[LobbyTransfer] Rejected reply: " + ex.Message); }
        }

        private void Update()
        {
            if (returnLobby != 0 && (!Net.IsRunningSteam || !Net.IsHost || KSteam.LobbyId.m_SteamID != returnLobby))
            { returners.Clear(); returnLobby = returnHost = 0; }
            if (phase == Phase.Resuming)
            {
                if (!AwaitingTransferredLoad) { phase = Phase.Idle; return; }
                if (Util.IsInMainMenu() && PreRunScript.instance != null)
                {
                    // Use the normal saved-run path only after all lobby arrivals.
                    // Release the start gate before invoking the game's load hooks.
                    phase = Phase.Idle;
                    try { PreRunScript.instance.LoadRun(); }
                    catch (Exception ex) { Say("Automatic resume failed: " + ex.Message + ". Your transferred save is preserved. Select Resume Expedition."); }
                }
                else if (Time.realtimeSinceStartupAsDouble > autoResumeDeadline)
                { phase = Phase.Idle; Say("The expedition is ready. Select Resume Expedition to continue."); }
                return;
            }
            if (pendingTransferredLoad && (!Net.IsRunning || KSteam.LobbyId.m_SteamID != pendingLoadLobby)) pendingTransferredLoad = false;
            if (id == null) return;
            if (!Switching && (!Net.IsRunningSteam || KSteam.LobbyId.m_SteamID != lobby)) { Reset(); return; }
            if (Switching && (!KSteam.IS_IN_LOBBY || KSteam.LobbyId.m_SteamID != lobby || (phase == Phase.Reconnecting && !Net.IsRunning)))
            { Abort("The lobby disconnected during handoff."); return; }
            if (phase == Phase.Reconnecting && SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID != target)
            { Abort("The new host left before handoff completed."); return; }
            // A slow peer may miss the completion packet and connect after the
            // host has resumed. The lobby token releases its transfer gate too.
            if (phase == Phase.Reconnecting && Net.IsClient && Net.IsConnected
                && KSteam.CURRENT_LOBBY.ownerID.m_SteamID == target
                && SteamMatchmaking.GetLobbyData(new CSteamID(lobby), CompleteLobbyKey) == id)
            { CompleteClient(); return; }
            if (Time.realtimeSinceStartupAsDouble > deadline) { Abort(Switching ? "Reconnect timed out. Save/backups preserved; do not assume completion." : "Preparation timed out; no transfer confirmed."); return; }
            if (!Switching && Local == originalHost && (!Net.IsHost || Find(target) == null)) { Abort("Host or target disconnected."); return; }
            if (!Busy && Local == originalHost && queuedWorld != WorldGeneration.world) { Abort("Run changed before transfer."); return; }
            if (phase == Phase.Accepted && continueWhenAccepted && Local == originalHost && Util.IsInWorld()
                && !WorldGeneration.world.generatingWorld && ServerMain.CheckIfEnoughPeopleAreAtLayerFinish())
            { continueWhenAccepted = false; WorldGeneration.world.ContinueRun(); return; }
            if (Preparing)
            {
                if (SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID == target)
                {
                    if (Local != originalHost && phase != Phase.CommitApproved)
                    { phase = Phase.Switching; Abort("Steam ownership changed before preparation was acknowledged."); return; }
                    BeginSwitch(); return;
                }
                if (Local != originalHost && phase == Phase.Preparing && Util.IsInMainMenu() && (Local != target || !IncludesSave || stage != null)) { Reply(Message.Ready); phase = Phase.Ready; }
                if (Local == originalHost && Util.IsInMainMenu())
                {
                    // New arrivals are not part of this checkpoint handshake;
                    // departed clients can return after the host is ready.
                    ulong[] peers = members.Where(s => s != originalHost && Find(s) != null).ToArray();
                    if (!peers.Contains(target) || !peers.All(ready.Contains)) return;
                    // Every peer must know that the old connection is expected to
                    // close before we touch Steam ownership. Steam's lobby-owner
                    // callback can arrive later than a P2P socket-close callback.
                    if (phase != Phase.CommitApproved) { SendAll(Packet(Message.Commit)); phase = Phase.CommitApproved; return; }
                    if (!peers.All(commitAcks.Contains)) return;
                    if (!SteamMatchmaking.SetLobbyOwner(new CSteamID(lobby), new CSteamID(target))) { Abort("Steam declined ownership change."); return; }
                    BeginSwitch();
                }
            }
            else if (phase == Phase.Reconnecting && Net.IsConnected && Util.IsInMainMenu() && ScavPlayer.LocalPlayer != null && KSteam.CURRENT_LOBBY.ownerID.m_SteamID == target)
            {
                if (Local != target && Time.realtimeSinceStartupAsDouble >= nextArrival) { Reply(Message.Arrived); nextArrival = Time.realtimeSinceStartupAsDouble + 2; }
                if (Local == target && (members.All(s => s == target || (Find(s) != null && arrived.Contains(s)))
                    || Time.realtimeSinceStartupAsDouble >= reconnectDeadline))
                {
                    int missing = members.Count(s => s != target && (Find(s) == null || !arrived.Contains(s)));
                    if (!SteamMatchmaking.SetLobbyData(new CSteamID(lobby), CompleteLobbyKey, id))
                    { Abort("Could not publish handoff completion."); return; }
                    SendAll(Packet(Message.Complete));
                    bool resume = mode == TransferMode.NextLayer;
                    string message = resume ? "Host transfer complete. You are hosting. Resuming the expedition automatically."
                        : IncludesSave ? "Host transfer complete. You are hosting the transferred save file. Select Resume Expedition when ready."
                        : "Host transfer complete. You are hosting this lobby.";
                    if (missing > 0) message += " " + missing + (missing == 1 ? " player can reconnect later." : " players can reconnect later.");
                    Say(message); HostTransferUI.NotifyComplete(message); Reset();
                    if (resume) { phase = Phase.Resuming; autoResumeDeadline = Time.realtimeSinceStartupAsDouble + 30; }
                }
            }
        }

        private static void CompleteClient()
        {
            string message = "Host transfer complete. " + targetName + " is now hosting. "
                + (mode == TransferMode.NextLayer ? "The expedition is resuming. " : "")
                + (Local == originalHost ? "You can safely quit." : "The former host can safely quit.");
            Say(message); HostTransferUI.NotifyComplete(message); Reset();
        }

        private static void BeginSwitch()
        {
            if (Switching) return;
            EnterPhase(Phase.Switching, 90);
            instance.StartCoroutine(Safe(Switch()));
        }

        // Acknowledged commit permits waiting for delayed Steam ownership data.
        // Role switching itself still requires Steam to report the expected owner.
        internal static bool HandlePlannedDisconnect(ulong peer)
        {
            if (id == null || Local == originalHost || peer != originalHost || (phase != Phase.CommitApproved && phase != Phase.Switching) ||
                !Util.IsInMainMenu() || KSteam.LobbyId.m_SteamID != lobby) return false;
            oldConnectionClosed = true;
            if (SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID == target) BeginSwitch();
            return true;
        }

        private static IEnumerator Safe(IEnumerator work)
        {
            try
            {
                while (true)
                {
                    object next;
                    try { if (!work.MoveNext()) yield break; next = work.Current; }
                    catch (Exception ex) { Abort("Transfer failed: " + ex.Message + ". Recovery files were preserved."); yield break; }
                    yield return next;
                }
            }
            finally { (work as IDisposable)?.Dispose(); }
        }
        private static IEnumerator Prepare()
        {
            string transfer = id;
            members = CurrentMembers();
            if (members.Contains(0) || members.Count < 2 || members.Count > 200) throw new InvalidOperationException("Unsupported lobby members.");
            string root = SavesystemPatch.mpsavefolder;
            byte[] archive = mode == TransferMode.NextLayer ? CaptureCheckpoint(root)
                : mode == TransferMode.LobbyWithSave ? TransferArchive.BuildSaved(root, target) : null;
            if (archive != null)
            {
                string recovery = TransferArchive.RecoveryFolder(root, id);
                Directory.CreateDirectory(recovery);
                File.WriteAllBytes(Path.Combine(recovery, "checkpoint.zip"), archive);
            }
            password = Multiplayer.INPUT_PASSWORD ?? "";
            runSettings = mode == TransferMode.LobbyWithSave ? TransferArchive.SavedRunSettings(root) ?? WorldMP.CompileRunSettings() : WorldMP.CompileRunSettings();
            var w = RequestPacket(Message.Prepare);
            w.Put(password); w.Put(runSettings); w.Put(depth); w.Put(members.Count); foreach (ulong s in members) w.Put(s);
            SendAll(w);
            // Together's own return-to-menu announcement keeps everyone in the lobby.
            if (mode == TransferMode.NextLayer)
            {
                Multiplayer.showMultiplayerMenu = true; PlayerCamera.main.ToMainMenu();
                Say("Expedition saved. Returning to the lobby before switching host.");
            }
            yield return null;
            if (id != transfer || archive == null) yield break;
            var begin = Packet(Message.Begin); begin.Put(archive.Length); begin.Put(TransferArchive.Hash(archive));
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, begin, Find(target).playerId);
            for (int offset = 0; offset < archive.Length; offset += ChunkSize)
            {
                while (id == transfer && !TransferTransport.CanSendCheckpoint(target)) yield return null;
                if (id != transfer) yield break;
                byte[] part = new byte[Math.Min(ChunkSize, archive.Length - offset)]; Buffer.BlockCopy(archive, offset, part, 0, part.Length);
                var chunk = Packet(Message.Chunk); chunk.Put(offset); chunk.Put(part.Length); chunk.Put(part);
                Net.Server_SendTo(DeliveryMethod.ReliableOrdered, chunk, Find(target).playerId);
                if ((offset / ChunkSize) % 4 == 3) yield return null;
            }
        }
        private static byte[] CaptureCheckpoint(string root)
        {
            if (Directory.Exists(root)) TransferArchive.CopyTree(root, Path.Combine(TransferArchive.RecoveryFolder(root, id), "source-before-checkpoint"));
            // Cache custom species before vanilla SaveGame replaces each player's
            // file. Their saved character must survive becoming host or reconnecting.
            var species = new Dictionary<ulong, (string name, int slot)>();
            foreach (ulong s in members)
                if (PlayerCheckpoint.TryReadSpecies("STEAM_" + s, out string name, out int slot)) species[s] = (name, slot);
            var stamps = members.Select(s => Path.Combine(root, "STEAM_" + s, "save.sv"))
                .ToDictionary(path => path, path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue);
            SaveSystem.SaveGame();
            foreach (var stamp in stamps)
                if (!File.Exists(stamp.Key) || File.GetLastWriteTimeUtc(stamp.Key) == stamp.Value)
                    throw new IOException("A fresh player save was not written: " + stamp.Key);
            foreach (var entry in species) PlayerCheckpoint.RememberSpecies("STEAM_" + entry.Key, entry.Value.name, entry.Value.slot);
            TransferArchive.NormalizeSource(root, originalHost, depth, members, id);
            return TransferArchive.Build(root, target, depth, members);
        }
        private static IEnumerator Switch()
        {
            string transfer = id;
            bool host = Local == target;
            if (!Util.IsInMainMenu()) throw new InvalidOperationException("Refusing transport switch outside lobby.");
            yield return null;
            if (id != transfer) yield break;
            TransferTransport.Detach();
            if (host)
            {
                if (IncludesSave)
                {
                    TransferArchive.Install(stage, SavesystemPatch.HostSaveFolder, transfer);
                    SavesystemPatch.SelectHostSave();
                }
            }
            else
            {
                // The new owner publishes this token only after opening its listener.
                // Waiting on readiness avoids racing a guessed reconnect delay.
                while (id == transfer && SteamMatchmaking.GetLobbyData(new CSteamID(lobby), ReadyLobbyKey) != transfer)
                    yield return null;
                if (id != transfer) yield break;
            }
            TransferTransport.Start(host, lobby, target, password, runSettings, IncludesSave);
            phase = Phase.Reconnecting;
            if (host)
            {
                pendingTransferredLoad = IncludesSave;
                pendingLoadLobby = lobby;
                holdLobbyAutoStart = mode != TransferMode.NextLayer;
                heldLobby = lobby;
                returners.Clear(); returners.UnionWith(members);
                returnLobby = lobby; returnHost = target;
                if (!SteamMatchmaking.SetLobbyData(new CSteamID(lobby), ReturnersLobbyKey,
                    transfer + ":" + string.Join(",", members.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
                    throw new IOException("Could not publish returning-player access.");
                if (!SteamMatchmaking.SetLobbyData(new CSteamID(lobby), ReadyLobbyKey, transfer))
                    throw new IOException("Could not announce the new host listener.");
                reconnectDeadline = Time.realtimeSinceStartupAsDouble + ReconnectGraceSeconds;
            }
            Say(host ? "New host listening up to five seconds for reconnections." : "Reconnecting to the new host's lobby.");
        }
        private static void Abort(string reason)
        {
            if (id == null) return;
            if (Net.IsHost && !Switching) { try { SendAll(Packet(Message.Abort)); } catch { } }
            Say(reason);
            if (Switching || oldConnectionClosed)
            {
                Say("Handoff incomplete. Rehost from the preserved save; no completion was confirmed.");
                // Detach may have already removed Net.Transport while retaining
                // Steam membership, so explicitly leave through the Steam lifecycle.
                KSteam.LeaveLobbyAndResetNet(true);
            }
            Reset();
        }
        private static void Reset()
        {
            id = targetName = stage = password = runSettings = null; incoming = null;
            lobby = originalHost = target = 0; phase = Phase.Idle; boundaryArmed = false;
            queuedWorld = null; ready.Clear(); arrived.Clear(); members.Clear();
            nextArrival = reconnectDeadline = 0;
            oldConnectionClosed = false; commitAcks.Clear();
            continueWhenAccepted = false;
            // pendingTransferredLoad survives success until the user's Load Run.
        }
        private static void OnWorldFinished() { if (AwaitingTransferredLoad && Util.IsInWorld()) pendingTransferredLoad = false; }
        private void OnDestroy() { if (instance == this) { WorldMP.OnWorldgenFinish -= OnWorldFinished; Reset(); returners.Clear(); returnLobby = returnHost = 0; instance = null; } }

        internal static bool AllowRun(bool load) {
            if (Busy || (HasRequest && mode != TransferMode.NextLayer)) { Say("Wait for host transfer to finish."); return false; }
            holdLobbyAutoStart = false;
            if (!load)
            {
                pendingTransferredLoad = false;
                if (Net.IsHost && !SavesystemPatch.InDebugMap && !SavesystemPatch.PreserveSave) SavesystemPatch.SelectNormalSave();
            }
            return true;
        }

        internal static void ArmBoundary(WorldGeneration world, bool twice) {
            if (id == null || Local != originalHost || Busy) return;
            if (!Accepted || twice || world.doPod || world.biomeDepth == 10 || SavesystemPatch.PreserveSave) {
                Abort("Transfer requires acceptance and an ordinary single-layer descent. Continuing normally."); return;
            }
            boundaryArmed = true;
        }
        [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Clear))]
        private static class AtClear
        {
            [HarmonyPostfix] private static void Postfix(WorldGeneration __instance, ref IEnumerator __result)
            {
                if (!boundaryArmed || __instance != queuedWorld || !Net.IsHost) return;
                boundaryArmed = false; depth = __instance.biomeDepth;
                EnterPhase(Phase.Preparing, 120);
                __result = Hold(__result);
            }
            private static IEnumerator Hold(IEnumerator original)
            {
                string transfer = id;
                instance.StartCoroutine(Safe(Prepare()));
                while (id == transfer && (Preparing || Switching)) yield return null;
                // Failure before returning to menu falls back to the normal transition.
                if (Util.IsInWorld()) while (original.MoveNext()) yield return original.Current;
                (original as IDisposable)?.Dispose();
            }
        }
    }
}

