// Boundary fakes for UI action/protocol tests. Production archive/controller source
// and the real LiteNetLib reader/writer are linked into this executable.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;

namespace HarmonyLib {
    public class HarmonyPatch : Attribute { public HarmonyPatch() {} public HarmonyPatch(Type t, string method) {} }
    public class HarmonyTargetMethods : Attribute {}
    public class HarmonyPrefix : Attribute {}
    public class HarmonyPostfix : Attribute {}
    public class HarmonyFinalizer : Attribute {}
    public class HarmonyPriority : Attribute { public HarmonyPriority(int n) {} }
    public static class Priority { public const int First = 800; }
}
namespace UnityEngine {
    public class MonoBehaviour {
        public static readonly List<IEnumerator> Work = new();
        public void StartCoroutine(IEnumerator work) { Work.Add(work); }
    }
    public static class Time { public static double realtimeSinceStartupAsDouble; }
}
namespace Steamworks {
    public struct CSteamID {
        public ulong m_SteamID;
        public CSteamID(ulong n) { m_SteamID = n; }
    }
    public static class SteamMatchmaking {
        public static ulong Owner = 1;
        public static bool AllowOwnerChange = true;
        public static readonly Dictionary<string, string> Data = new();
        public static CSteamID GetLobbyOwner(CSteamID lobby) => new(Owner);
        public static bool SetLobbyOwner(CSteamID lobby, CSteamID owner) { if (!AllowOwnerChange) return false; Owner = owner.m_SteamID; return true; }
        public static string GetLobbyData(CSteamID lobby, string key) => Data.GetValueOrDefault(key, "");
        public static bool SetLobbyData(CSteamID lobby, string key, string value) { Data[key] = value; return true; }
    }
}
namespace CasualtiesTogetherUtils {}
public class WorldGeneration {
    public static WorldGeneration world;
    public bool generatingWorld, doPod;
    public int biomeDepth = 3;
    public int ContinueCalls, ClearCalls;
    public void ContinueRun() { ContinueCalls++; }
    public IEnumerator Clear() { ClearCalls++; yield break; }
}
public class PreRunScript {
    public static PreRunScript instance = new();
    public int Loads;
    public bool WasBusy;
    public void LoadRun() { Loads++; WasBusy = Together.LobbyHostTransfer.Busy; }
}
public static class SaveSystem {
    public static Action SaveAction = () => {};
    public static void SaveGame() => SaveAction();
    public static void TryLoadGame() {}
    public static bool HasSave() => true;
    public static byte[] Zip(string text) {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true))
        using (var writer = new StreamWriter(zip, Encoding.ASCII)) writer.Write(text);
        return output.ToArray();
    }
}
public class PlayerCamera {
    public static PlayerCamera main = new();
    public void ToMainMenu() { Together.Util.Menu = true; }
}
public class ConsoleScript {
    public static ConsoleScript instance = new();
    public void LogToConsole(string text) {}
}
namespace Together {
    public class CasualtiesTogetherSingleton : UnityEngine.MonoBehaviour {}
    public class Log { public void LogInfo(string s) {} public void LogWarning(string s) {} }
    public static class Plugin { public static Log Logger = new(); }
    public class ScavPlayer {
        public static List<ScavPlayer> AllPlayers = new(); public static ScavPlayer LocalPlayer;
        public ulong SteamId; public string playerName; public bool IsLocal; public ushort playerId;
    }
    public static class Net {
        public static bool IsRunning = true, IsRunningSteam = true, IsHost = true;
        public static bool IsClient => !IsHost;
        public static bool IsConnected => IsRunning;
        public static List<byte[]> Outbox = new();
        public static void Server_SendToClients(DeliveryMethod delivery, NetDataWriter w, object ids) => Outbox.Add(w.CopyData());
        public static void Server_SendTo(DeliveryMethod delivery, NetDataWriter w, ushort id) => Outbox.Add(w.CopyData());
        public static void Client_Send(DeliveryMethod delivery, NetDataWriter w) => Outbox.Add(w.CopyData());
    }
    public static class ServerMain {
        public static object AllClientIdsExceptHost = new();
        public static bool CheckIfEnoughPeopleAreAtLayerFinish() => true;
    }
    public static class Multiplayer {
        public static bool showMultiplayerMenu;
        public static string INPUT_PASSWORD = "";
        public static Action<NetDataReader> Client;
        public static Action<ScavPlayer, NetDataReader> Server;
        public static List<string> Logs = new();
        public static void DoMultiplayerStatusMessageLog(string s) => Logs.Add(s);
        public static NetDataWriter CreateNamedWriter(string name) => new();
        public static void RegisterCustomClientReceiver(string n, Action<NetDataReader> a) => Client = a;
        public static void RegisterCustomServerReceiver(string n, Action<ScavPlayer, NetDataReader> a) => Server = a;
    }
    public static class Util {
        public static bool Menu;
        public static bool IsInWorld() => !Menu;
        public static bool IsInMainMenu() => Menu;
        public static bool IsWorldGenerated() => !Menu && !WorldGeneration.world.generatingWorld;
    }
    public static class SavesystemPatch { public static string mpsavefolder; public static bool PreserveSave; }
    public static class WorldMP {
        public static event Action OnWorldgenFinish;
        public static void Finish() => OnWorldgenFinish?.Invoke();
        public static string CompileRunSettings() => "settings";
    }
    public static class FullgameWorldCompat { public static bool IsAscentLayer() => WorldGeneration.world.biomeDepth == 10; }
    public static class KSteam {
        public static bool IS_IN_LOBBY = true;
        public static ulong Local = 1;
        public static Steamworks.CSteamID LobbyId = new(100);
        public class Lobby { public Steamworks.CSteamID ownerID = new(1); }
        public static Lobby CURRENT_LOBBY = new();
        public static Steamworks.CSteamID GetLocalUserSteamID() => new(Local);
        public static void LeaveLobbyAndResetNet(bool b) { Net.IsRunning = Net.IsRunningSteam = false; }
    }
    public static class TransferTransport {
        public static bool CanSendCheckpoint(ulong recipient) => true;
        public static int Starts, Detaches;
        public static void Detach() { Detaches++; Net.IsRunning = false; }
        public static bool InstalledSave;
        public static void Start(bool host, ulong lobby, ulong owner, string pw, string settings, bool includesSave) {
            Starts++; Net.IsRunning = Net.IsRunningSteam = true; Net.IsHost = host;
            InstalledSave = includesSave;
            KSteam.CURRENT_LOBBY.ownerID = new(owner);
        }
        public static IDisposable SelectQoLSave() => null;
    }
    public static class HostTransferUI {
        public static bool SelectingAtExit;
        public static string OfferText;
        internal static void ShowOffer(string token, string host, LobbyHostTransfer.TransferMode mode) => OfferText = LobbyHostTransfer.OfferWarning(mode);
        public static void NotifyComplete(string message) {}
    }
    public static class PlayerCheckpoint {
        public static bool TryReadSpecies(string id, out string name, out int slot) { name = null; slot = 0; return false; }
        public static void RememberSpecies(string id, string name, int slot) {}
    }
}
