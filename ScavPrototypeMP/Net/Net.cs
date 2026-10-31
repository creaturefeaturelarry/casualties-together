using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using K4os.Compression.LZ4;
using LiteEntitySystem;
using LiteEntitySystem.Transport;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Together;
using Unity.VisualScripting;
using UnityEngine;
using static WorldGeneration;
using KrokV5Optimization;

namespace Together;


public struct knetid : IEquatable<knetid> {
    public ushort id;

    public knetid(ushort id) {
        this.id = id;
    }
    public static implicit operator ushort(knetid id) {
        return id.id;
    }
    public static implicit operator knetid(ushort id) {
        return new knetid (id);
    }
    public override string ToString() {
        return id.ToString();
    }

    // dictionary lookups boxed this. the generic comparer uses these two and does not box.
    public bool Equals(knetid other) => id == other.id;

    public override bool Equals(object other) => other is knetid value && value.id == id;

    public override int GetHashCode() => id;
}




// to store info that isnt really useful for querying searches 
public struct SteamServerInfoInLobbyExtraData {
    public int deathcounter;
    public MultiplayerGameRules rules;

    public SteamServerInfoInLobbyExtraData(NetPublicServerInfo info) {
        rules = info.rules;
    }

    public void Read(NetPublicServerInfo info) {
        info.rules = rules;


        {// read rules 
        
            var defaults = (new MultiplayerGameRules()).ConvertToSerializableDict();
            var thisrules = rules.ConvertToSerializableDict();

            var diffrules = thisrules.Where(x => !defaults[x.Key].Equals(x.Value));

            if (diffrules.Count() > 0) {
                info.rules_as_string = Lang.Get("ruleschanged");
                foreach (var fld in diffrules) {
                    info.rules_as_string += $"\n{fld.Key}: {fld.Value}";
                }
            } else {
                info.rules_as_string = Lang.Get("rulesdefault");
            }

        }
    }
}



public class NetPublicServerInfo : INetSerializable {
    public bool dedicated;
    public bool haspassword;
    public string version = "6.7.6.7";
    public string name = "";
    public string gamemode = "vanilla";
    public string description = "";

    public KSteam.Lobby steam_lobby_info = null; // used by steam transport and server browser
    public MultiplayerGameRules rules;

    public int plr_living_count;
    public int plr_count;
    public int plr_max = 8;

    public int cur_layer = -1; // -1 is mainmenu
    public int cur_depth;
    public string lang;

    public int avg_happiness; // inspire from PlayerCamera.HandleDeathScreen

    public string[] modlist = new string[0];
    public bool latejoinspectate => rules.LateJoinSpectate;

    public NetPublicServerInfo SetValues() {
        dedicated = Net.IsDedicatedServer;
        haspassword = !String.IsNullOrEmpty(Multiplayer.INPUT_PASSWORD);
        plr_max = Multiplayer.rules.PLAYER_COUNT_LIMIT;
        midjoin_lock_active = Net.IsMyServerMidjoinLocked();
        rules = Multiplayer.rules;

        var isinworld = Util.IsInWorld();
        if (isinworld) {
            plr_count = ScavPlayer.ClientIdToPlayerDict.Count;
            plr_living_count = ScavPlayer.AllLivingPlayers.Count;
            cur_layer = WorldGeneration.world.biomeDepth;

            if (ScavPlayer.AllLivingPlayers.Count > 0)
                avg_happiness = (int)ScavPlayer.AllLivingPlayers.Average(x => (int)x.body.happiness);
        } else {
            plr_count = ScavPlayer.ClientIdToPlayerDict.Count;
            plr_living_count = plr_count;
            cur_layer = -1;
            avg_happiness = 100;
        }
        if (Util.IsGeneratingWorld()) {
            gamemode = "loading";
        } else if (Util.IsTutorialWorld()) {
            gamemode = "tutorial";
        } else {
            var gm = GamemodeManager.GetGamemode();
             if (gm == null ) {
                if (!isinworld) {
                    gamemode = "mainmenu";
                } else {
                    gamemode = WorldGeneration.world.biomeOverride == OverrideSceneType.Debug ? "debug_world" : "vanilla";
                }
            } else {
                gamemode = GamemodeManager.GetGamemode().GetType().Name;
            }
        }

        return this;
    }

    // DONT SERIALIZE THIS !!!
    public bool enforcemodlist_locked;
    public bool midjoin_lock_active; // this actually means that the game is mid-game and latejoin is not allowed
    public string friends_tooltip = null;// shown as a tooltip
    public string rules_as_string = null;
    public List<ulong> steamfriends = new();

    public bool IsJoinable() {
        return !enforcemodlist_locked && !midjoin_lock_active && Multiplayer.FULL_VERSION_TAG == this.version;
    }

    public void Deserialize(NetDataReader reader) {
        reader.Get(out version);
        reader.Get(out dedicated);
        reader.Get(out haspassword);
        reader.Get(out name);
        reader.Get(out description);
        reader.Get(out gamemode);
        reader.Get(out plr_living_count);
        reader.Get(out plr_count);
        reader.Get(out plr_max);
        reader.Get(out cur_layer);
        reader.Get(out cur_depth);
        reader.Get(out avg_happiness);
        reader.Get(out rules);
    }

    public void Serialize(NetDataWriter writer) {
        writer.Put(version);
        writer.Put(dedicated);
        writer.Put(haspassword);
        writer.Put(name);
        writer.Put(description);
        writer.Put(gamemode);
        writer.Put(plr_living_count);
        writer.Put(plr_count);
        writer.Put(plr_max);
        writer.Put(cur_layer);
        writer.Put(cur_depth);
        writer.Put(avg_happiness);
        writer.Put(rules);
    }

}



public static class Net {
    public enum NetType {
        Client,
        Host,
        DedicatedServer
    }

    public static bool IsRunning { get; private set; }
    public static bool IsRunningSteam { get; private set; }
    public static bool IsClient => Type == NetType.Client;
    public static bool IsClientOrHost => !IsDedicatedServer; // It's better to check ScavPlayer.LocalPlayer != null
    public static bool IsServer => !IsClient;
    public static bool IsHost => Type == NetType.Host;
    public static bool IsDedicatedServer => Type == NetType.DedicatedServer;
    public static bool IsConnecting => Transport != null && Transport.IsConnecting();
    public static bool IsConnected => IsRunning && !IsConnecting;
    public static NetType Type { get; internal set; }

    public static NetTransportBase Transport { get; internal set; }

    public static NetPublicServerInfo MyServerInfo = new NetPublicServerInfo();
    public static NetPublicServerInfo CurrentServerInfo = new NetPublicServerInfo();

    internal static ClientEntityManager ClientEntityManager { get; private set; }
    internal static ServerEntityManager ServerEntityManager { get; private set; }
    internal static EntityManager EntityManager => ((EntityManager)ClientEntityManager ?? (EntityManager)ServerEntityManager);

    private static readonly HashSet<NetPlayer> removingServerPlayers = new();

    // RemovePlayer destroys the entity before the id returns to the pool. a second call for the same player must not run.
    internal static void RemoveServerPlayer(NetPlayer player) {
        var manager = ServerEntityManager;
        if (manager == null || player == null ||
            !ReferenceEquals(manager.GetPlayer(player.Id), player) ||
            !removingServerPlayers.Add(player)) {
            return;
        }

        try {
            manager.RemovePlayer(player);
        } finally {
            removingServerPlayers.Remove(player);
        }
    }


    public const ushort DEFAULT_PORT = 7790;
    public const string IP_LOCALHOST_IPv4 = "127.0.0.1";
    public const string IP_LOCALHOST_IPv6 = "::1";
    public const int SERVER_CLIENTID = 0;


    public static readonly int MAX_SERVER_NAME_LENGTH = 32;
    public static readonly int MAX_SERVER_DESC_LENGTH = 200;
    public static readonly int MAX_SERVER_DESC_NEWLINES = 3;
    public static bool IsMyServerMidjoinLocked() {
        return Util.IsInWorld() && !Multiplayer.rules.LateJoinAllowed;
    }

    public static bool TryGetSteamTransport(out TransportSteamworks tsteam) {
        if (Transport is TransportSteamworks tsteam2) {
            IsRunningSteam = true;
            tsteam = tsteam2;
            return true;
        }
        IsRunningSteam = false;
        tsteam = null;
        return false;
    }


    public static string ByteMsgIdToName(byte msgid) {
        var n = Enum.GetName(typeof(NetmsgId), msgid);
        return (n != null ? (n + $" ({msgid})") : msgid.ToString());
    }


    public static string GetDebugStatsString() {
        var infostr = "";
        if (Net.Transport == null) return infostr;
        if (Net.TryGetSteamTransport(out var tsteam)) {
            if (Net.IsClient) {
                var status = tsteam.client_status;
                infostr =
                    $"State: {status.m_eState}" +
                    $"\nPing: {status.m_nPing}" +
                    $"\nConnectionQuality:  Local: {Mathf.RoundToInt(status.m_flConnectionQualityLocal * 100f)}%  Remote: {Mathf.RoundToInt(status.m_flConnectionQualityRemote * 100f)}%" +
                    $"\nPacketsPerSec:  Out: {status.m_flOutPacketsPerSec}  In: {status.m_flInPacketsPerSec}" +
                    $"\nBytesPerSec:  Out: {status.m_flOutBytesPerSec}  In: {status.m_flInBytesPerSec}" +
                    $"\nSendRateBytesPerSecond: {status.m_nSendRateBytesPerSecond}" +
                    $"\nPendingUnreliable: {status.m_cbPendingUnreliable}" +
                    $"\nPendingReliable: {status.m_cbPendingReliable}" +
                    $"\nSentUnackedReliable: {status.m_cbSentUnackedReliable}" +
                    $"\nusecQueueTime: {status.m_usecQueueTime}";
            } else {
                SteamNetConnectionRealTimeStatus_t status = new();
                var me = KSteam.GetLocalUserSteamID().m_SteamID;
                int i = 0;
                foreach (var item in tsteam.connectionMapping) {
                    if (item.Key == me) { continue; }
                    i++;
                    status.m_flConnectionQualityLocal += item.Value.connectionStatus.m_flConnectionQualityLocal;
                    status.m_flOutPacketsPerSec += item.Value.connectionStatus.m_flOutPacketsPerSec;
                    status.m_flInPacketsPerSec += item.Value.connectionStatus.m_flInPacketsPerSec;
                    status.m_flOutBytesPerSec += item.Value.connectionStatus.m_flOutBytesPerSec;
                    status.m_flInBytesPerSec += item.Value.connectionStatus.m_flInBytesPerSec;
                    status.m_nSendRateBytesPerSecond += item.Value.connectionStatus.m_nSendRateBytesPerSecond;
                    status.m_cbPendingUnreliable += item.Value.connectionStatus.m_cbPendingUnreliable;
                    status.m_cbPendingReliable += item.Value.connectionStatus.m_cbPendingReliable;
                    status.m_cbSentUnackedReliable += item.Value.connectionStatus.m_cbSentUnackedReliable;
                    status.m_usecQueueTime.m_SteamNetworkingMicroseconds += item.Value.connectionStatus.m_usecQueueTime.m_SteamNetworkingMicroseconds;
                }
                if (i == 0) {
                    infostr = "NO PLAYERS TO MEASURE CONNECTION STATS";
                } else {
                    status.m_flConnectionQualityLocal /= i;
                    status.m_usecQueueTime.m_SteamNetworkingMicroseconds /= i;


                    infostr =
                        $"ConnectionQuality:  {Mathf.RoundToInt(status.m_flConnectionQualityLocal * 100f)}%" +
                        $"\nPacketsPerSec:  Out: {status.m_flOutPacketsPerSec}  In: {status.m_flInPacketsPerSec}" +
                        $"\nBytesPerSec:  Out: {status.m_flOutBytesPerSec}  In: {status.m_flInBytesPerSec}" +
                        $"\nSendRateBytesPerSecond: {status.m_nSendRateBytesPerSecond}" +
                        $"\nPendingUnreliable: {status.m_cbPendingUnreliable}" +
                        $"\nPendingReliable: {status.m_cbPendingReliable}" +
                        $"\nSentUnackedReliable: {status.m_cbSentUnackedReliable}" +
                        $"\nusecQueueTime: {status.m_usecQueueTime}";
                }

            }
        } else {
            if (Net.Transport is TransportLiteNetLib tlnl) {
                var stat = tlnl.NetManager.Statistics;
                infostr =
                    string.Format(
                        "BytesReceived: {0}\nPacketsReceived: {1}\nBytesSent: {2}\nPacketsSent: {3}\nPacketLoss: {4}\nPacketLossPercent: {5}",
                        stat.BytesReceived,
                        stat.PacketsReceived,
                        stat.BytesSent,
                        stat.PacketsSent,
                        stat.PacketLoss,
                        stat.PacketLossPercent);
            }
        }
        return infostr;
    }


    static Dictionary<byte, Multiplayer.HandleMessageDelegate> SERVER_MESSAGE_HANDLERS = new();
    static Dictionary<byte, Multiplayer.HandleMessageDelegate> CLIENT_MESSAGE_HANDLERS = new();

    internal static void RegisterServerReceiver(byte msgid, Multiplayer.HandleMessageDelegate receiver) {
        SERVER_MESSAGE_HANDLERS.Add(msgid, receiver);
    }
    internal static void RegisterClientReceiver(byte msgid, Multiplayer.HandleMessageDelegate receiver) {
        CLIENT_MESSAGE_HANDLERS.Add(msgid, receiver);
    }
    internal static void InvokeServerMessage(knetid callerclientId, NetDataReader reader) {
        byte msgId = 0;
        try {
            reader.Get(out msgId);
            if (SERVER_MESSAGE_HANDLERS.TryGetValue(msgId, out var func)) {
                func( callerclientId, reader);
            } else {
                if (log.verbose) {
                    log.warn($"InvokeServerMessage: Unknown Message Id: {ByteMsgIdToName(msgId)}");
                }
            }
        } catch (Exception e) {
            log.error($"SERVER: Net.InvokeServerMessage: {DebugHelp.GetPlayerFullDebugString(callerclientId)}: msgId:{ByteMsgIdToName(msgId)} :\n{e.ToString()}");
        }
    }
    internal static void InvokeClientMessage(knetid callerClientId, NetDataReader reader) {
        byte msgId = 0;
        try {
            reader.Get(out msgId);
            if (Net.IsDedicatedServer) {
                log.error($"SERVER: Net.InvokeClientMessage: msgId:{msgId} : UNEXPECTED LOOPBACK ON DEDICATED SERVER !!!!");
                return;
            }
            if (CLIENT_MESSAGE_HANDLERS.TryGetValue(msgId, out var func)) {
                if (callerClientId != Net.SERVER_CLIENTID) {
                    // todo maybe log this?
                    return;
                }
                func(callerClientId, reader);
            } else {
                if (log.verbose) {
                    log.warn($"InvokeClientMessage: Unknown Message Id: {ByteMsgIdToName(msgId)}");
                }
            }
        } catch (Exception e) {
            log.error($"CLIENT: Net.InvokeClientMessage: msgId:{ByteMsgIdToName(msgId)} :\n{e.ToString()}");
        }
    }

    // THIS DOES NOT CHECK THE BAN LIST  !!! BAN LIST IS CHECKED BY THE TRANSPORT, NEAR THIS VALIDATION
    internal static bool ValidateNewClientHandshake(NetDataReader reader, bool should_validate_name, bool privileged_user, out Multiplayer.IntroductionPlayerInfo userInfo, out string reject_reason) {
        reject_reason = "The server doesn't like you ig.";
        userInfo = new();
        userInfo.userName = "?????????????????????";
        userInfo.color = Color24.black;

        try {

            try {
                if (!Multiplayer.ValidateClientConnectIntroductionPacket(reader, ref userInfo, out string deny_reason, should_validate_name, privileged_user)) {
                    reject_reason = ( deny_reason);
                    return false;
                }
            } catch {
                reject_reason = ( $"Corrupt payload." );
                return false;
            }

            //if (SharedMain.LocalWorldIsGenerating) {
            //    reject_reason = ( $"Server is generating world, please try again.");
            //    return false;
            //}

            if (!privileged_user) {

                if (
                    ScavPlayer.ClientIdToPlayerDict.Count() + 1 > Multiplayer.PLAYER_COUNT_LIMIT
                    //netmgr.ConnectedPeersCount + 1 > Multiplayer.PLAYER_COUNT_LIMIT
                ) {
                    reject_reason = ($"Max players reached ({Multiplayer.PLAYER_COUNT_LIMIT}).");
                    return false;
                }

                if (should_validate_name) {

                    // check name
                    foreach (var item in ScavPlayer.ClientIdToPlayerDict) {
                        if (item.Value.playerName == userInfo.userName) {
                            reject_reason = "Player with this name already exists.";
                            return false;
                        }
                    }
                } else  {
                    if (Net.TryGetSteamTransport(out var tsteam)) {

                    }
                }

                    string reject_reason2 = null;
                if (!Multiplayer.AdditionalApprovalCheck(ref reader, userInfo.userName, ref reject_reason2)) {
                    reject_reason = (reject_reason2);
                    return false;
                }
            }


            return true;

        } catch (Exception e) {
#if !RELEASE
            if (log.verbose) log.error("INetEventListener.OnConnectionRequest: " + e.ToString());
#endif
            reject_reason = ( "You caused an error!! Fuck off!!!");
            return false;
        }
        return true;
    }



    public static void ShutdownReset() {
        try {

            ClientEntityManager = null;
            ServerEntityManager = null;

            RPCManager_Initialize_MultiplayerPatch.QuickReset();

            if (IsClient) {
                MP3Menu.dropdownList = null;
                if (!String.IsNullOrEmpty(_clientRunSettingsBeforeConnect)) {
                    WorldMP.ApplyRunSettings(_clientRunSettingsBeforeConnect); // Apply back
                }
            }
            _clientRunSettingsBeforeConnect = null;

            IsRunningSteam = false;
            Type = NetType.Host;
            ClientMain.LOCAL_PING = 0;

            ClientMain.SERVER_FPS = 60;
            ClientMain.SERVER_TPS = 60;
            ServerMain.CURRENT_TPS = 60;
            ServerMain.CURRENT_FPS = 60;

            ClientMain.MY_CONNECTION_QUALITY = 100;
            ServerMain.AVG_CONNECTION_QUALITY = 100;
            if (IsRunning) {
                log.l("Network Shutdown Reset called.");
            }

            IsRunning = false;
            SERVER_MESSAGE_HANDLERS.Clear();
            CLIENT_MESSAGE_HANDLERS.Clear();

            try {
                var plrs = new HashSet<ScavPlayer>(GameObject.FindObjectsOfType<ScavPlayer>(true));
                plrs.AddRange(ScavPlayer.ClientIdToPlayerDict.Values);

                foreach (var item in plrs) {
                    if (item == null) continue;
                    try {
                        GameObject.DestroyImmediate(item.gameObject);
                    } catch (Exception e) {
                        log.error($"Net.ShutdownReset: Player Deletion: {item}: " + e.ToString());
                    }
                }
            } catch (Exception e) {
                log.error("Net.ShutdownReset: Player Deletion: " + e.ToString());
            }

            if (Transport != null) {
                try {
                    Transport.Shutdown();
                } catch (Exception e) {
                    log.error($"Net.ShutdownReset: Transport Shutdown: {Transport.GetType()}: " + e.ToString());
                }
                Transport = null;
            }
        } catch (Exception e) {
            Multiplayer.DoMultiplayerStatusMessageError("Net.ShutdownReset: !!!! CRITICAL FAILURE !!!!: " + e.ToString());
        }
        try {
            SavesystemPatch.UpdatePersistentDataPathCuzMultiplayer();
        } catch (Exception _) {
        }
    }
    internal static void TransportCreated(NetType ntype, bool save_name = true) {
        Plugin.SkipMainMenuIntro();

        if (IsClient) {
            MP3Menu.dropdownList = null;
            _clientRunSettingsBeforeConnect = WorldMP.CompileRunSettings();
        }

        IsRunning = true;
        Net.Type = ntype;
        Application.runInBackground = true;


        if (save_name) {
#if RELEASE
            PlayerPrefs.SetString("KrokoshaMultiplayer_LastPlayerName", UIMainMenu.USERINPUT_NAME);
            PlayerPrefs.SetString("KrokoshaMultiplayer_LastPlayerColor", UIMainMenu.LAST_VALID_INPUT_COLOR.ToHex());
#endif
        }

        if (IsServer) {
            PlayerPrefs.SetString("KrokoshaMultiplayer_LastMyLobbyName", MyServerInfo.name);

            ServerMain._RegisterServerReceivers();
        }
        if (IsClientOrHost) {
            ClientMain._RegisterClientReceivers();
        }
        if (IsClient) {
            UIMainMenu._GUI____RenderRuleField_PresetChosen = 0;
            UIMainMenu._GUI____RenderRuleField_PresetName = "";
            Multiplayer.rules = new MultiplayerGameRules(); // reset the rules 
        }

        SavesystemPatch.UpdatePersistentDataPathCuzMultiplayer();
    }
    static string _clientRunSettingsBeforeConnect = null;



    internal static void LiteEntitySystemRegisterFieldTypes() {
        EntityManager.RegisterFieldType<knetid>();
        EntityManager.RegisterFieldType<SByteScalarVec2>();
        EntityManager.RegisterFieldType<ShortScalarVec2>((a, b, t) => {
            return new ShortScalarVec2((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
        });
        EntityManager.RegisterFieldType<Vector2Int16>((a, b, t) => {
            return new Vector2Int16((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
        });
        EntityManager.RegisterFieldType<Vector2UInt8>();
        EntityManager.RegisterFieldType<AnyObjectNetId>();
        EntityManager.RegisterFieldType<LimbNetId>();
        EntityManager.RegisterFieldType<Color>();
        EntityManager.RegisterFieldType<Color24>();
        EntityManager.RegisterFieldType<Color32>();
        EntityManager.RegisterFieldType<LiquidStackPart>();
        //EntityManager.RegisterFieldType<Vector2_4byte_512>(Vector2_4byte_512.Lerp);
        EntityManager.RegisterFieldType<Vector2_4byte_512>((a, b, t) => {
            return new Vector2_4byte_512((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
        });
        EntityManager.RegisterFieldType<Vector2_4byte_200>((a, b, t) => {
            return new Vector2_4byte_200((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
        });
        EntityManager.RegisterFieldType<Vector2>(Vector2.LerpUnclamped);
        EntityManager.RegisterFieldType<Vector3>(Vector3.LerpUnclamped);
        EntityManager.RegisterFieldType<Quaternion>(Quaternion.LerpUnclamped);
    }

    internal static void LiteEntitySystemInit() {
        if (ClientEntityManager != null || ServerEntityManager != null) {
            throw new Exception("EntityManager already exists!");
        }

        var typesMap = ScavEntityTypesMap.CreateTypesMap();
        LiteEntitySystemRegisterFieldTypes();

#if TESTING || true
        LiteEntitySystem.Logger.LoggerImpl = new LiteEntitySystemLogger();
#endif

        if (IsClient) {
            ClientEntityManager = new ClientEntityManager(
                typesMap,
                new LESKrokNetClientPeer(),
                (byte)NetmsgId.LiteEntitySystem
            );
        } else if (IsServer) {
            ServerEntityManager = new ServerEntityManager(
                typesMap,
                (byte)NetmsgId.LiteEntitySystem,
                ServerEntitySendRate,
                ServerSendRate.EqualToFPS
            );
            // distant objects skip three sends out of four. worldgen queues one more full baseline.
            ServerEntityManager.ShouldSendEntityDiff = SendBudget.Allow;
            ServerEntityManager.BeforeLogicTick = FixRejoinBaseline.BeforeTick;

            ServerEntityManager.AddSingleton<ServerWorldStateSingleton>();
        }
    }

    internal static void OnTransportConnected() {

        LiteEntitySystemInit();

        if (Net.Type == NetType.Host && ScavPlayer.LocalPlayer == null) {

            ulong steamId = 0;
            var aplayername = UIMainMenu.USERINPUT_NAME;
            if (Net.TryGetSteamTransport(out var tsteam)) {
                steamId = KSteam.GetLocalUserSteamID().m_SteamID;
                aplayername = steamId.ToString();
            }
            var playerEntity = BasePlayer.ServerCreatePlayer(aplayername, steamId, UIMainMenu.LAST_VALID_INPUT_COLOR, true);

        }

    }

    public static byte ServerEntitySendRate = 30;
    public static ushort ServerTick = 0;

    internal static void DoUpdate() {
        try {
            Transport?.Update();
            ClientEntityManager?.Update();
            ServerEntityManager?.Update();
        } catch (Exception e) {
            log.error("Net.DoUpdate: " + e.ToString());
        }

        if (Net.ServerEntityManager != null) {
            ServerTick = Net.ServerEntityManager.Tick;
        } else if (Net.ClientEntityManager != null) {
            ServerTick = Net.ClientEntityManager.ServerTick;
        }
    }

    public static void Server_Kick(knetid clientId, string message ) {
        Con.ConFailIfNetworkIsRunningAndIsClient();
        Transport.Kick(clientId, message);
    }


    // for LiteNetLib i dont need to resolve domains
    public static bool TryParseIPPORT(string IPPORT, out string ip, out ushort port) {
        ip = IP_LOCALHOST_IPv4;
        port = DEFAULT_PORT;
        var is_ipv4 = true; // if false its ipv6


        // example input_ipport_text that this should support:
        // 127.0.0.1:7777
        // somedomain.com:7777
        // 127.0.0.1        //port should be optional 
        // somedomain.com  
        // ::   // and now it supports ipv6
        // [2001:db8:4006:812::200e]:7790
        // [2001:db8:4006:812::200e]
        // 2001:db8:4006:812::200e
        // ::ffff:192.0.2.128
        // [::ffff:192.0.2.128]:7790

        IPPORT = IPPORT.Trim();
        string split1 = IPPORT;
        string split2 = "";

        if (!String.IsNullOrEmpty(IPPORT)) {
            try {
                // https://en.wikipedia.org/wiki/IPv6

                var indexof_sqbracketend = IPPORT.IndexOf(']');
                var lastcolon = IPPORT.LastIndexOf(':');
                if (IPPORT.StartsWith('[') && indexof_sqbracketend != -1) { // guaranteed ipv6 like "[2001:db8:4006:812::200e]:8080" or  "[2001:db8:4006:812::200e]"

                    split1 = IPPORT.Substring(1, indexof_sqbracketend - 1);
                    if (lastcolon > indexof_sqbracketend) { // try to find the ":8080" part of ip address
                        split2 = IPPORT.Substring(lastcolon + 1);
                    }

                } else {
                    {
                        var firstdot = IPPORT.IndexOf('.');
                        var firstcolon = IPPORT.IndexOf(':');
                        if (firstcolon == -1) { // so its ipv4 without port aka "127.0.0.1" or a domain like "example.com"
                            is_ipv4 = true;
                        } else {
                            if (firstdot == -1) { // guaranteed not ipv4 cuz theres no dots in "2001:db8:4006:812::200e"
                                is_ipv4 = false;
                            } else {
                                if (firstdot < firstcolon) { // very likely basicass ipv4 like "127.0.0.1:7790" or a domain like "example.com:7790"
                                    is_ipv4 = true;
                                } else { // apparently theres ipv4 compatibility bullshit? like "::ffff:192.0.2.128"
                                    is_ipv4 = false;
                                    if (lastcolon > firstdot) {// probably port is possible???? as if "::ffff:192.0.2.128:7777" but nah ppl should use the square brackets for ts
                                        //split2 = input_ipport_text.Substring(lastcolon + 1);
                                    }
                                }
                            }
                        }
                    }

                    if (is_ipv4) {
                        if (lastcolon != -1) {
                            split1 = IPPORT.Substring(0, lastcolon);
                            split2 = IPPORT.Substring(lastcolon + 1);
                        }
                    } else {
                        split1 = IPPORT;
                    }
                }


            } catch (Exception e) {
                log.error("IPv4 or IPv6 parse: " + e.Message);

                return false;
            }
        }
        split1 = split1.Trim();
        split2 = split2.Trim();

        ip = split1;
        port = DEFAULT_PORT;
        if (!String.IsNullOrEmpty(split2)) {
            if (!ushort.TryParse(split2.Trim(), out port)) {
                return false;
            }
        }


        return true;
    }

    public static bool VerifyMyServerName() {
        var n = MyServerInfo.name;
        if (n.Length < 3) {
            return false;
        }
        if (n.Length > Net.MAX_SERVER_NAME_LENGTH || Multiplayer.SanitizeTextInputAllowSpaces(n) != n)  {
            return false;
        }
        
        return true;

    }

    public static NetDataWriter CreateWriter(in Enum msgid) {
        return CreateWriter((byte)(object)msgid);
    }
    public static NetDataWriter CreateWriter(in Enum msgid, int size, bool autoresize = false) {
        return CreateWriter((byte)(object)msgid,size, autoresize);
    }
    public static NetDataWriter CreateWriter(byte msgid) {
        var ndw = new NetDataWriter();
        ndw.Put(msgid);
        return ndw;
    }
    public static NetDataWriter CreateWriter(byte msgid, int size, bool autoresize = false) {
        var ndw = new NetDataWriter(autoresize,size);
        ndw.Put(msgid);
        return ndw;
    }



    public static void Server_SendTo(in DeliveryMethod delivery, in NetDataWriter writer, in ScavPlayer plr) => Server_SendTo(delivery, writer, plr.playerId);
    public static void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid) { 
#if !RELEASE
        {
            var writer = new NetDataWriter();
            writer.Put(data);
            Net.CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(writer);
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
        }
#endif
        if (clientid == Net.SERVER_CLIENTID) { // loopback to myself (the server)
            Net.InvokeClientMessage(Net.SERVER_CLIENTID, new NetDataReader(data.ToArray()));
            return;
        }
        Transport.Server_SendTo(delivery, data, clientid);
    }
    public static void Server_SendTo(in DeliveryMethod delivery, in NetDataWriter writer, in knetid clientid) {
        Server_SendTo(delivery, writer.AsReadOnlySpan(), clientid);
    }

    public static void Server_SendToClientsVeryReliable(in NetDataWriter writer, in IEnumerable<knetid> clientIds){
        if (Net.TryGetSteamTransport(out var tsteam)) {
            tsteam.Server_SendToClients(Steamworks.Constants.k_nSteamNetworkingSend_Reliable, writer, clientIds.ToList());
        } else {
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds);
        }
    }

    public static void Server_SendToClients(in DeliveryMethod delivery, in NetDataWriter writer, in knetid clientIds) => Server_SendTo(delivery, writer, clientIds);  
    public static void Server_SendToClients(in DeliveryMethod delivery, in NetDataWriter writer, in IReadOnlyList<ScavPlayer> clients) => Server_SendToClients(delivery, writer, clients.Select(x => x.playerId).ToList());
    public static void Server_SendToClients(in DeliveryMethod delivery, in NetDataWriter writer, in IEnumerable<knetid> clientIds) {
#if !RELEASE
        Net.CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(writer);
        if (Net.IsClient) {
            throw new Exception("CLIENT CANT DO THAT!!!!");
        }
#endif
        if (clientIds.Count() == 0) {
            return;
        }

        var aaaa = new List<knetid>(clientIds);
        if (aaaa.Remove(SERVER_CLIENTID)) { // loopback to myself (the server)
            Net.InvokeClientMessage(Net.SERVER_CLIENTID, new NetDataReader(writer));

            if (aaaa.Count == 0) {
                return;
            }
        }

        Transport.Server_SendToClients(delivery, writer.AsReadOnlySpan(), aaaa);
    }

    public static void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data) { // TODO THIS IS TEMPORARY TO TEST, FOR OPTIMIZATION I'LL HAVE TO MAKE SPAN A DEFAULT!!
#if !RELEASE
        Con.ConFailIfNotConnected();
        {
            var writer = new NetDataWriter();
            writer.Put(data);
            Net.CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(writer);
            if (Net.IsDedicatedServer) {
                throw new Exception("DEDICATED SERVER CANT DO THAT!!!!");
            }
        }
#endif
        if (Net.IsServer) { // loopback to myself (the server)
            Net.InvokeServerMessage(Net.SERVER_CLIENTID, new NetDataReader(data.ToArray()));
            return;
        }
        Transport.Client_Send(delivery, data);
    }
    public static void Client_Send(in DeliveryMethod delivery, in NetDataWriter writer) {
        Client_Send(delivery, writer.AsReadOnlySpan());
    }

#if !RELEASE
    public static void CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(in NetDataWriter writer) {
        var reader = new NetDataReader(writer);

        reader.Get(out byte msgId);
        var n = Enum.GetName(typeof(NetmsgId), msgId);
        if (n == null) {
            log.warn($"MESSAGE ID BROKE?????: msgid:{msgId}  FROM\n{new StackTrace()}");
        }
    }
#endif

}


public interface NetTransportBase {

    public bool IsConnecting();
    public void Shutdown();
    public void Update();

    public void Kick(knetid clinetId, in string msg);

    public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid);
    public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in IReadOnlyList<knetid> clientIds);
    public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data);
}

public class TransportLiteNetLib : NetTransportBase, INetEventListener {

    public readonly string ip = Net.IP_LOCALHOST_IPv4;
    public readonly ushort port = Net.DEFAULT_PORT;

    public NetManager NetManager;
    public NetPeer CLIENT_PEER { get; private set; }

    public TransportLiteNetLib(string IP, ushort PORT) {
        ip = IP;
        port = PORT;
        
        NetManager = new NetManager(this,null
           // ,false
        ) {
            PingInterval = 500,
            DisconnectTimeout = 10000,
#if RELEASE
            MaxConnectAttempts = 30,
#else
            MaxConnectAttempts = 9999999,
#endif
            EnableStatistics = true,
        };
    }



   
    public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> writer, in knetid clientid) {
        if (ClientIdToPeerDict.TryGet(clientid,out var peer)) {
            peer.Send(writer,delivery);
        } else {
            if (log.verbose)
                log.error($"SERVER: TransportLiteNetLib.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
        }
    }
    public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> writer, in IReadOnlyList<knetid> clientIds) {
        if (Net.IsClient) {
            throw new Exception("CLIENT CANT DO THAT!!!!");
        }
        foreach (var cID in clientIds) {
            Server_SendTo(delivery, writer, cID);
        }
    }
    public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> writer) {
        CLIENT_PEER.Send(writer,delivery);
    }


    static bool _DoOnWantToConnect(string ipport_input, Net.NetType type) {
        try {
            if (UIMainMenu.USERINPUT_NAME.Length < 3) {
                Multiplayer.DoMultiplayerStatusMessageLog("Name too short.");
                return false;
            }
            if (UIMainMenu.USERINPUT_NAME != Multiplayer.SanitizeTextInput(UIMainMenu.USERINPUT_NAME)) {
                Multiplayer.DoMultiplayerStatusMessageLog("Invalid Name.");
                return false;
            }
            if (!Net.TryParseIPPORT(ipport_input, out var ip, out var port)) {
                Multiplayer.DoMultiplayerStatusMessageLog("Invalid IP:PORT format.");
                return false;
            }
            if (type != Net.NetType.Client && !Net.VerifyMyServerName()) {
                Multiplayer.DoMultiplayerStatusMessageLog("Invalid Server name.");
                return false;
            }


            Net.TransportCreated(type);
            var transport = new TransportLiteNetLib(ip, port);
            Net.Transport = transport;

            if (type == Net.NetType.Client) {

                var hellopacket = Multiplayer.GetClientConnectIntroductionPacket();

                bool success = transport.NetManager.Start();
                if (!success) {
                    Multiplayer.DoMultiplayerStatusMessageLog("LiteNetLib: CLIENT: NetManager.Start() failed.");
                    return false;
                }

                transport.CLIENT_PEER = transport.NetManager.Connect(ip, port, hellopacket);

                return true;
            } else {
                if (type == Net.NetType.Host || type == Net.NetType.DedicatedServer) {

                    bool success = transport.NetManager.Start(port);
                    if (!success) {
                        Multiplayer.DoMultiplayerStatusMessageLog("LiteNetLib: SERVER: NetManager.Start() failed.");
                        return false;
                    }

                    Net.OnTransportConnected();

                    return true;
                } else {
                    throw new NotImplementedException();
                }
            }


            Multiplayer.DoMultiplayerStatusMessageError("TransportLiteNetLib.OnWantToConnect: What the fuck?");

        } catch (Exception e) {
            Multiplayer.DoMultiplayerStatusMessageError("TransportLiteNetLib.OnWantToConnect:\n" + e.ToString());
        } 
        return false;
    }


    public static bool OnWantToConnect(string ipport_input, Net.NetType type) {
        Net.ShutdownReset();

        if (_DoOnWantToConnect(ipport_input, type)) {
            Multiplayer.DoMultiplayerStatusMessageLog("Succesfully started: " + type.ToString());
            return true;
        } else {
            Net.ShutdownReset();
            // all paths already announce the error
        }

        return false;
    }

    void INetEventListener.OnConnectionRequest(ConnectionRequest request) {
        var reader = request.Data;

        var is_rejected = !Net.ValidateNewClientHandshake(reader, true, false, out var userInfo, out string reject_reason);
        if (!is_rejected) {
            if (BanList.IsBannedIPName(request.RemoteEndPoint.Address.ToString(), userInfo.userName)) {
                is_rejected = true;
                reject_reason = "Banned";
            }
        }

        if (is_rejected) {
            var writer = new NetDataWriter();
            writer.Put(reject_reason);

            request.Reject(writer.Data, 0, writer.Length, false);
            return;
        }



        var newpeer = request.Accept();
#if !RELEASE
        if (log.verbose) log.l($"{this.GetType().Name}: Accepted connection from: {newpeer}  name:{userInfo.userName}  color:{userInfo.color}");
#endif

        var playerEntity = BasePlayer.ServerCreatePlayer(userInfo.userName, 0, userInfo.color, false);

        ClientIdToPeerDict[playerEntity.OwnerId] = newpeer;

        newpeer.Tag = new TagPlayerInfo {
            intro = userInfo,
            plrId = playerEntity.OwnerId,
            netPlayer = playerEntity.serverNetPlayer,
            plr = playerEntity.player,
        };

    }

    void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) {
        log.error($"{this.GetType().Name}.INetEventListener.OnNetworkError :IPEndPoint: {endPoint}, SocketError: {socketError}");
    }

    void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) {
        if (peer == CLIENT_PEER) {
            ClientMain.LOCAL_PING = latency;
            ClientMain.MY_CONNECTION_QUALITY = (byte)(Mathf.Clamp(100 - (int)peer.Statistics.PacketLossPercent, 0, 100));
        }

        if (peer.Tag != null && peer.Tag is TagPlayerInfo klpe && klpe.plr != null) {
            klpe.plr.ping = (double)latency * 0.001;
        }
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) {
        
        if (Net.IsServer) {
            if (ClientIdToPeerDict.TryGet(peer, out var plrId)) {
                Net.InvokeServerMessage(plrId, reader);
            }
        } else {
            Net.InvokeClientMessage(Net.SERVER_CLIENTID, reader);
        }
    }

    void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) {
#if !RELEASE
        log.l($"{this.GetType().Name}.INetEventListener.OnNetworkReceiveUnconnected :IPEndPoint: {remoteEndPoint}, NetPacketReader: {reader}, UnconnectedMessageType: {messageType}");
#endif
        if (Net.IsServer) {
            if (messageType == UnconnectedMessageType.Broadcast) {

            }
        } 
    }

    void INetEventListener.OnPeerConnected(NetPeer peer) {
#if !RELEASE
        if (log.verbose) log.l($"{this.GetType().Name}.INetEventListener.OnPeerConnected :peer: {peer}"); // it prints something like:  TransportLiteNetLib.INetEventListener.OnPeerConnected :peer: 127.0.0.1:51728
#endif
        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("Connected!  :3");

            Net.OnTransportConnected();
        } else {
            
        }
    }


    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) {
#if !RELEASE
        if (log.verbose) log.l($"{this.GetType().Name}.INetEventListener.OnPeerDisconnected :peer: {peer}, DisconnectInfo: Reason: {disconnectInfo.Reason} SocketErrorCode: {disconnectInfo.SocketErrorCode} AdditionalData: {disconnectInfo.AdditionalData?.RawData?.ToHexString()}");
#endif
        var tttttt = Net.Type;

        try {
            if (peer == CLIENT_PEER) {
                Multiplayer.ShutdownNetwork(); // to go back to main menu and shutdown

                if (disconnectInfo.Reason == DisconnectReason.UnknownHost) {
                    Multiplayer.DoMultiplayerStatusMessageLog("Unknown Host");
                } else if (disconnectInfo.Reason == DisconnectReason.Timeout) {
                    Multiplayer.DoMultiplayerStatusMessageLog("Timed Out");
                } else if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected) {
                    disconnectInfo.AdditionalData.TryGetString(out var str);
                    Multiplayer.DoMultiplayerStatusMessageLog($"Connection Rejected: {str}");
                } else if (disconnectInfo.Reason == DisconnectReason.DisconnectPeerCalled) {
                    disconnectInfo.AdditionalData.TryGetString(out var str);
                    Multiplayer.DoMultiplayerStatusMessageLog($"Disconnected: {str}");
                } else if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose) {
                    disconnectInfo.AdditionalData.TryGetString(out var str);
                    Multiplayer.DoMultiplayerStatusMessageLog($"Disconnected: {str}");
                } else {
                    disconnectInfo.AdditionalData.TryGetString(out var str);
                    Multiplayer.DoMultiplayerStatusMessageLog($"Disconnected: {disconnectInfo.Reason} : {disconnectInfo.SocketErrorCode} : {str}");
                }
            }

            if (peer != null && peer.Tag != null && peer.Tag is TagPlayerInfo klpe) {
                if (klpe.plrId != 0 && ClientIdToPeerDict.TryGet(klpe.plrId, out var currentPeer) &&
                    ReferenceEquals(currentPeer, peer)) {
                    Net.RemoveServerPlayer(klpe.netPlayer);
                }

                if (klpe.plr != null) {
                    ScavPlayer.Destroy(klpe.plr.gameObject);
                }
            }
        } catch (Exception e) {
            log.error(e);
        }
        if (peer != null) {
            ClientIdToPeerDict.Remove(peer);
        }
    }

    

    public void OnMessageDelivered(NetPeer peer, object userData) {
        if (log.verbose)
            log.l($"{this.GetType().Name}.IDeliveryEventListener.OnMessageDelivered :peer: {peer}, userData: {userData}");
    }

    public void OnNtpResponse(NtpPacket packet) {
        if (log.verbose)
            log.l($"{this.GetType().Name}.INtpEventListener.OnNtpResponse :NtpPacket: {packet}");
    }

    public void OnPeerAddressChanged(NetPeer peer, IPEndPoint previousAddress) {
        if (log.verbose)
            log.l($"{this.GetType().Name}.IPeerAddressChangedListener.OnPeerAddressChanged :peer: {peer}  previousAddress: {previousAddress}");
    }

    bool NetTransportBase.IsConnecting() {
        if (CLIENT_PEER != null) {
            return CLIENT_PEER.ConnectionState != ConnectionState.Connected;
        }
        return false;
    }


    void NetTransportBase.Shutdown() {
        NetManager.Stop();
        
    }


    internal TwoWayDictionary<knetid, NetPeer> ClientIdToPeerDict = new();

    void NetTransportBase.Update() {

        //ClientIdToPeerDict.Clear();
        if (Net.IsServer) {
            var peers = new List<NetPeer>();
            NetManager.GetConnectedPeers(peers);
            foreach (var peer in peers) {
                try {
                    if (peer.Tag != null && peer.Tag is TagPlayerInfo klpe) {
                        //ClientIdToPeerDict[klpe.plrId] = peer;
                        klpe.plr.ping = peer.Ping * 0.001f;
                    } 
                } catch (Exception e) {
                    log.error($"{nameof(TransportLiteNetLib)}: Player NetPeer bullshit happened {peer}\n{e.ToString()}");
                }
            }
        }

        NetManager.PollEvents();

    }

    void NetTransportBase.Kick(knetid clientid, in string msg) {
        if (ClientIdToPeerDict.TryGet(clientid, out var peer)) {
            var writer = new NetDataWriter();
            writer.Put(msg);

            peer.Disconnect(writer);
        } else {
            log.warn($"Tried to kick a non-existant player, clientid:{clientid}  reason:{msg}");
        }
    }

}





public static class MyLiteNetLibExtensions {

    

    public static void ResetKeepMsgId(this NetDataWriter w2) {
        w2.SetPosition(sizeof(NetmsgId));
    }
    /*
    public static void CompressWriter(this NetDataWriter w2, int start_to_ignore = sizeof(NetmsgId)) {
        var og = CopyData(w2, start_to_ignore);
        var compressed = Util.Compress(og);
        w2.SetPosition(start_to_ignore); // dont compress the first two bytes cuz its the msg id 
        w2.PutBytesWithLength(compressed);
    }
    public static NetDataReader DecompressReader(this NetDataReader r2) {
        // it already passed the ushort
        var og = Util.Decompress(r2.GetBytesWithLength());
        return new NetDataReader(og);
    }
*/

    public static void CompressWriter(this NetDataWriter w2, int start_to_ignore = sizeof(NetmsgId)) {
        if (FixCompressWriter.Apply(w2, start_to_ignore))
            return;
        var og = CopyData(w2, start_to_ignore);
        //TODO try using LZ4Codec
        /*
        int encodedLength = LZ4Codec.Encode(
            packetBuffer,
            originalLength,
            compressionBuffer + sizeof(BaselineDataHeader),
            _compressionBuffer.Length - sizeof(BaselineDataHeader),
        );

        int maxCompressedSize = LZ4Codec.MaximumOutputSize(_packetBuffer.Length);
        if (_compressionBuffer.Length < maxCompressedSize)
            _compressionBuffer = new byte[maxCompressedSize];
        */

        var compressed = Util.Compress(og);
        w2.SetPosition(start_to_ignore); // dont compress the first two bytes cuz its the msg id 
        w2.PutBytesWithLength(compressed);
    }
    public static NetDataReader DecompressReader(this NetDataReader r2) {
        var og = Util.Decompress(r2.GetBytesWithLength());
        return new NetDataReader(og);
    }


    public static byte[] CopyData(this NetDataWriter w2, int start_index) {
        return CopyData(w2, start_index, w2.Length - start_index);
    }
    public static byte[] CopyData(this NetDataWriter w2, int start_index, int length) {
        byte[] resultData = new byte[length];
        Buffer.BlockCopy(w2.Data, start_index, resultData, 0, length);
        return resultData;
    }

    public static void Put<T>(this NetDataWriter writer, T value) where T : unmanaged, INetSerializeByMemcpy {
        writer.PutUnmanaged(value);
    }
    public static void Get<T>(this NetDataReader reader, out T result) where T : unmanaged, INetSerializeByMemcpy {
        result = reader.GetUnmanaged<T>();
    }
    public static void Get(this NetDataReader reader, out byte[] result) {
        result = reader.GetBytesWithLength();
    }

    public static void Put(this NetDataWriter writer, float[] value) {
        writer.PutArray(value);
    }
    public static void Get(this NetDataReader reader, out float[] result) {
        result = reader.GetFloatArray();
    }

    public static void Put(this NetDataWriter writer, Vector2[] value) {
        writer.Put(value.Length);
        for (int i = 0; i < value.Length; i++) {
            writer.Put(value[i].x);
            writer.Put(value[i].y);
        }
    }
    public static void Get(this NetDataReader reader, out Vector2[] result) {
        int length = reader.GetInt();
        result = new Vector2[length];

        for (int i = 0; i < length; i++) {
            float x = reader.GetFloat();
            float y = reader.GetFloat();
            result[i] = new Vector2(x, y);
        }
    }

    public static void Put(this NetDataWriter writer, Vector2 value) {
        writer.Put(value.x);
        writer.Put(value.y);
    }
    public static void Get(this NetDataReader reader, out Vector2 result) {
        result = new Vector2(reader.GetFloat(), reader.GetFloat());
    }
    public static Vector2 GetVector2(this NetDataReader reader) {
        Vector2 v;
        v.x = reader.GetFloat();
        v.y = reader.GetFloat();
        return v;
    }

    public static void Put(this NetDataWriter writer, Vector2Int value) {
        writer.Put(value.x);
        writer.Put(value.y);
    }
    public static void Get(this NetDataReader reader, out Vector2Int result) {
        result = new Vector2Int(reader.GetInt(), reader.GetInt());
    }
    


    public static void Put(this NetDataWriter writer, Color value) {
        writer.Put(value.r);
        writer.Put(value.g);
        writer.Put(value.b);
        writer.Put(value.a);
    }
    public static void Get(this NetDataReader reader, out Color result) {
        result = new Color(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
    }


    public static void Put(this NetDataWriter writer, knetid value) {
        writer.Put(value.id);
    }
    public static void Get(this NetDataReader reader, out knetid result) {
        result = reader.GetUShort();
    }


    public static void Put(this NetDataWriter writer, string value, bool oneByteChars) {
        if (oneByteChars) {
            writer.PutBytesWithLength(value.ToByteArray());
        } else {
            writer.Put(value);
        }
    }
    public static void Get(this NetDataReader reader, out string result, bool oneByteChars) {
        if (oneByteChars) {
            result = reader.GetBytesWithLength().ToStringFromCharBytes();
        } else {
            result = reader.GetString();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void GetUnmanaged<T>(this NetDataReader reader, out T result) where T : unmanaged {
        result = reader.GetUnmanaged<T>();
    }


    public static void Put(this NetDataWriter writer, Dictionary<string, string> value) {
        writer.Put((ushort)value.Count);
        writer.PutArray(value.Keys.ToArray());
        writer.PutArray(value.Values.ToArray());
    }
    public static void Get(this NetDataReader reader, out Dictionary<string, string> result) {
        reader.Get(out ushort Count);
        var Keys = reader.GetStringArray();
        var Values = reader.GetStringArray();
        result = new();
        for (int i = 0; i < Count; i++) {
            result[Keys[i]] = Values[i];
        }
    }


    // Half AI generated
    #region CARELESS_SERIALIZERS 


    public static void PutAny<T>(this NetDataWriter writer, T value) {
        //if (value == null)  throw new ArgumentNullException(nameof(value));

        var type = typeof(T);

        // bool
        if (type == typeof(bool))
            writer.Put((bool)(object)value);

        // byte/sbyte
        else if (type == typeof(byte))
            writer.Put((byte)(object)value);
        else if (type == typeof(sbyte))
            writer.Put((sbyte)(object)value);

        // short/ushort
        else if (type == typeof(short))
            writer.Put((short)(object)value);
        else if (type == typeof(ushort))
            writer.Put((ushort)(object)value);

        // int/uint
        else if (type == typeof(int))
            writer.Put((int)(object)value);
        else if (type == typeof(uint))
            writer.Put((uint)(object)value);

        // long/ulong
        else if (type == typeof(long))
            writer.Put((long)(object)value);
        else if (type == typeof(ulong))
            writer.Put((ulong)(object)value);

        // float/double
        else if (type == typeof(float))
            writer.Put((float)(object)value);
        else if (type == typeof(double))
            writer.Put((double)(object)value);

        // char/string
        else if (type == typeof(char))
            writer.Put((char)(object)value);
        else if (type == typeof(string))
            writer.Put((string)(object)value);

        // arrays
        else if (type == typeof(byte[]))
            writer.Put((byte[])(object)value);

        // Guid
        else if (type == typeof(Guid))
            writer.Put((Guid)(object)value);

        else
            throw new NotSupportedException($"Type {type} is not supported.");
    }

    public static bool GetAny<T>(this NetDataReader reader, out T value) {
        var type = typeof(T);
        object result;

        try {
            if (type == typeof(bool))
                result = reader.GetBool();

            else if (type == typeof(byte))
                result = reader.GetByte();
            else if (type == typeof(sbyte))
                result = reader.GetSByte();

            else if (type == typeof(short))
                result = reader.GetShort();
            else if (type == typeof(ushort))
                result = reader.GetUShort();

            else if (type == typeof(int))
                result = reader.GetInt();
            else if (type == typeof(uint))
                result = reader.GetUInt();

            else if (type == typeof(long))
                result = reader.GetLong();
            else if (type == typeof(ulong))
                result = reader.GetULong();

            else if (type == typeof(float))
                result = reader.GetFloat();
            else if (type == typeof(double))
                result = reader.GetDouble();

            else if (type == typeof(char))
                result = reader.GetChar();
            else if (type == typeof(string))
                result = reader.GetString();

            else if (type == typeof(byte[]))
                result = reader.GetRemainingBytes();

            else if (type == typeof(Guid))
                result = reader.GetGuid();

            else
                throw new NotSupportedException($"Type {type} is not supported.");

            value = (T)result;
            return true;
        } catch {
            value = default!;
            return false;
        }
    }


    public enum NetValueType : byte {
        Null = 0,

        Bool,
        Byte,
        SByte,

        Short,
        UShort,

        Int,
        UInt,

        Long,
        ULong,

        Float,
        Double,

        Char,
        String,

        Guid,
        ByteArray
    }

    public static void PutObject(this NetDataWriter writer, object value) {
        // null
        if (value == null) {
            writer.Put((byte)NetValueType.Null);
            return;
        }

        switch (value) {
            case bool v:
            writer.Put((byte)NetValueType.Bool);
            writer.Put(v);
            break;

            case byte v:
            writer.Put((byte)NetValueType.Byte);
            writer.Put(v);
            break;

            case sbyte v:
            writer.Put((byte)NetValueType.SByte);
            writer.Put(v);
            break;

            case short v:
            writer.Put((byte)NetValueType.Short);
            writer.Put(v);
            break;

            case ushort v:
            writer.Put((byte)NetValueType.UShort);
            writer.Put(v);
            break;

            case int v:
            writer.Put((byte)NetValueType.Int);
            writer.Put(v);
            break;

            case uint v:
            writer.Put((byte)NetValueType.UInt);
            writer.Put(v);
            break;

            case long v:
            writer.Put((byte)NetValueType.Long);
            writer.Put(v);
            break;

            case ulong v:
            writer.Put((byte)NetValueType.ULong);
            writer.Put(v);
            break;

            case float v:
            writer.Put((byte)NetValueType.Float);
            writer.Put(v);
            break;

            case double v:
            writer.Put((byte)NetValueType.Double);
            writer.Put(v);
            break;

            case char v:
            writer.Put((byte)NetValueType.Char);
            writer.Put(v);
            break;

            case string v:
            writer.Put((byte)NetValueType.String);
            writer.Put(v);
            break;

            case Guid v:
            writer.Put((byte)NetValueType.Guid);
            writer.Put(v);
            break;

            case byte[] v:
            writer.Put((byte)NetValueType.ByteArray);
            writer.PutBytesWithLength(v);
            break;

            default:
            throw new NotSupportedException(
                $"Unsupported object type: {value.GetType().FullName}");
        }
    }

    public static object GetObject(this NetDataReader reader) {
        var type = (NetValueType)reader.GetByte();

        return type switch {
            NetValueType.Null => null,

            NetValueType.Bool => reader.GetBool(),

            NetValueType.Byte => reader.GetByte(),
            NetValueType.SByte => reader.GetSByte(),

            NetValueType.Short => reader.GetShort(),
            NetValueType.UShort => reader.GetUShort(),

            NetValueType.Int => reader.GetInt(),
            NetValueType.UInt => reader.GetUInt(),

            NetValueType.Long => reader.GetLong(),
            NetValueType.ULong => reader.GetULong(),

            NetValueType.Float => reader.GetFloat(),
            NetValueType.Double => reader.GetDouble(),

            NetValueType.Char => reader.GetChar(),
            NetValueType.String => reader.GetString(),

            NetValueType.Guid => reader.GetGuid(),

            NetValueType.ByteArray => reader.GetBytesWithLength(),

            _ => throw new InvalidOperationException(
                $"Unknown NetValueType: {type}")
        };
    }

    #endregion
}







public interface INetSerializeByMemcpy {
}














