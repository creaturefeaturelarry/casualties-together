using BepInEx.Logging;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.VisualScripting;
using UnityEngine;
using static Together.KSteam;

namespace Together;



// !!! WARNING !!!  UTTER SCHIZO CODE
//   COPYPASTED BULLSHIT FROM THE DOCS MANGLED TOGETHER 
//   AND GENERAL CODE VOMIT






// not static !! and is not ScavSingleton,  cuz instance is created after CheckSteam runs succesfully
public class KSteam : MonoBehaviour {
    protected static ManualLogSource log => Plugin.log;

    public static bool Loaded { get; private set; }

    public static KSteam singleton = null;


    public static bool IS_IN_LOBBY { get; private set; }
    public static bool IS_IN_STEAMOVERLAY { get; private set; }

    public const uint CONST_STEAMAPPID_SPACEWAR = 480;
    public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO = 4576510;
    public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST = 4584420;


    public static bool APP_IS_PIRATE { get; private set; }// 480
    public static bool APP_IS_DEMO { get; private set; }// 4576510
    public static bool APP_IS_PLAYTEST { get; private set; }// 4584420

    //public static bool USER_OWNS_PLAYTEST { get; private set; }


    public static uint STEAM_APPID { get; private set; }




    public class LobbyMember {
        public CSteamID steamID;
        public double server_last_data_receive_time;
        public string disconnectreason = null;
        public Dictionary<string,string> metadata = new();
        public LobbyMember(ulong steamID) => new LobbyMember((CSteamID)steamID);
        public LobbyMember(CSteamID steamID) {
            server_last_data_receive_time = Time.realtimeSinceStartupAsDouble;
        }
    }
    public class Lobby {
        public bool locked;
        public CSteamID lobby_steamID;
        public CSteamID ownerID;
        public Dictionary<CSteamID, LobbyMember> members  = new(); // THIS IS EMPTY IF USER IS NOT IN THE LOBBY
        public int memberlimit;
        public Dictionary<string, string> metadata = new();

        public string DebugReadableDump() {
            if (this == null) return "NULL_LOBBY";
            var s = $"===\nlobby_steamID: {lobby_steamID}\nownerID: {ownerID}\nlocked: {locked}\nmemberlimit: {memberlimit}\nmembers: {String.Join(", ", members.Keys)}\nmetadata:\n{String.Join("\n", metadata)}\n===";
            return s;
        }
    }

    public static List<Lobby> LOBBIES = new();
    public static Lobby CURRENT_LOBBY = new Lobby();

    internal static Dictionary<ulong, NetPublicServerInfo> CurServerList_ButOnlySteamLobbies => UIServerBrowser.CurServerList_ButOnlySteamLobbies;




    internal static void UpdateLobbyInfo(CSteamID steamIDLobby, ref Lobby outLobby) {
        outLobby.lobby_steamID = steamIDLobby;
        outLobby.ownerID = SteamMatchmaking.GetLobbyOwner(steamIDLobby);
        outLobby.memberlimit = SteamMatchmaking.GetLobbyMemberLimit(steamIDLobby);
        
        int nDataCount = SteamMatchmaking.GetLobbyDataCount(steamIDLobby);
        outLobby.metadata.Clear();
        for (int i = 0; i < nDataCount; ++i) {
            bool lobbyDataRet = SteamMatchmaking.GetLobbyDataByIndex(steamIDLobby, i, out var m_Key, Steamworks.Constants.k_nMaxLobbyKeyLength, out var m_Value, Steamworks.Constants.k_cubChatMetadataMax);
            
            m_Key = m_Key.TrimStart(_OLDLOBBYDATAKEYPREFIX); // Scraping Krok's name from everything 

            outLobby.metadata[m_Key] = m_Value;
            if (!lobbyDataRet) {
                Together.log.error("SteamMatchmaking.GetLobbyDataByIndex returned false.");
                continue;
            }
        }



        {
            var new_member_list = new List<CSteamID>();
            var membercount = SteamMatchmaking.GetNumLobbyMembers(steamIDLobby); // returns 0 if local user is not in that lobby 
            for (int i = 0; i < membercount; ++i) {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(steamIDLobby, i);
                new_member_list.Add(member);
            }

            var newList = new_member_list;
            var oldList = new List<CSteamID>(KSteam.CURRENT_LOBBY.members.Select(x => (CSteamID)x.Key));

            var added = newList.Except(oldList);
            var removed = oldList.Except(newList);

            foreach (var sID in added) {
                KSteam.CURRENT_LOBBY.members.Add(sID, new LobbyMember(sID));
            }
            foreach (var sID in removed) {
                KSteam.CURRENT_LOBBY.members.Remove(sID);   
            }

        }

    }




    internal static void LeaveLobbyAndResetNet(CSteamID lobby_steamID) {
        IS_DOING_HostLobbyRecreatorLoop = false;
        CURRENT_LOBBY.lobby_steamID = lobby_steamID;
        LeaveLobbyAndResetNet(true);
    }
    internal static void LeaveLobbyAndResetNet(bool kill_transport = true) {
        if (Together.log.verbose)
            Together.log.l($"STEAM: LeaveLobby called! lobbyID:{CURRENT_LOBBY.lobby_steamID} - STACK: {new StackTrace()}");
        else
            Together.log.l($"STEAM: LeaveLobby called! lobbyID:{CURRENT_LOBBY.lobby_steamID}");

        SteamMatchmaking.LeaveLobby((CSteamID)CURRENT_LOBBY.lobby_steamID);
        KSteam.IS_IN_LOBBY = false;
        CURRENT_LOBBY.lobby_steamID = CSteamID.Nil;

        if (kill_transport) {
            if (Net.TryGetSteamTransport(out var tsteam)) {

            }

            Net.ShutdownReset();
        }
    }


    public static ELobbyType NumToLobbyType(int num) {
        return num == 2 ? ELobbyType.k_ELobbyTypePublic : (num == 1 ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePrivate);
    }
    public static int LobbyTypeToNum(ELobbyType num) {
        return num == ELobbyType.k_ELobbyTypePublic ? 2 : (num == ELobbyType.k_ELobbyTypeFriendsOnly ? 1 : 0);
    }


    /// <summary>
    /// Determines if the current UTC time is within the typical Steam maintenance window.
    /// Maintenance usually happens on Tuesdays between 20:00 UTC and 02:00 UTC (Wednesday).
    /// </summary>
    /// <returns>True if maintenance is likely occurring; otherwise, false.</returns>
    public static bool IsSteamMaintenanceLikely() {
        return IsSteamMaintenanceLikely(DateTime.UtcNow);
    }

    /// <summary>
    /// Determines if the specified UTC time is within the typical Steam maintenance window.
    /// </summary>
    /// <param name="utcTime">The UTC time to check.</param>
    /// <returns>True if maintenance is likely occurring; otherwise, false.</returns>
    public static bool IsSteamMaintenanceLikely(DateTime utcTime) {
        // Ensure the DateTime is treated as UTC
        if (utcTime.Kind != DateTimeKind.Utc) {
            utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        }

        DayOfWeek day = utcTime.DayOfWeek;
        int hour = utcTime.Hour;

        // Check for Tuesday (Maintenance Day)
        if (day == DayOfWeek.Tuesday) {
            // Window: 20:00 UTC to 23:59 UTC
            return hour >= 20;
        }

        // Check for Wednesday (Early morning spillover from Tuesday night)
        if (day == DayOfWeek.Wednesday) {
            // Window: 00:00 UTC to 02:00 UTC
            return hour < 2;
        }

        return false;
    }


    void Awake() {
        if (singleton != null) {
            Destroy(this);
            throw new Exception("kill yourself!");
        }
        singleton = this;
    }

    void Start() {
        Invoke(nameof(__CheckCommandLineForSteamArgs), 3); // check it in 5 seconds
        InvokeRepeating(nameof(Update10s), 5, 10);

        BindToEventsToCallSteamStuff();
    }

    void BindToEventsToCallSteamStuff() {
        Multiplayer.OnSceneChangeOrWorldStartGenerate += () => {

            // https://partner.steamgames.com/doc/api/ISteamTimeline 

            if (Util.IsInWorld()) {
                SteamTimeline.SetTimelineTooltip(WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth], 0);

            } else {
                SteamTimeline.ClearTimelineTooltip(0);
            }


            if (Net.TryGetSteamTransport(out var tsteam)) {
                foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
                    SteamFriends.SetPlayedWith((CSteamID)plr.SteamId);
                }
            }

        };

        WorldMP.OnWorldgenFinish += () => {
            SteamTimeline.AddInstantaneousTimelineEvent(WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth], "", "steam_bookmark", 400);

            if (Net.IsRunningSteam && Net.IsServer) {

                Server_UpdateLobbyData();
            }
        };

        WorldGeneration_CreateExplosion_MultiplayerPatch.OnExplosion += (eparam) => {
            if (KM.DistanceCheck(eparam.position, Camera.main.transform.position, 60)) {
                SteamTimeline.AddInstantaneousTimelineEvent("Explosion", "", "steam_explosion", 10);
            }
        };

        NetBody.OnPlayerDeath += (plr) => {

            if (plr == null || plr.body == null) return;

            if (Time.unscaledTimeAsDouble - plr.netBody.timeOfDeath > 1) {
                SteamTimeline.AddInstantaneousTimelineEvent("Death", plr.playerName + " died.", "steam_death", 20);
            }

        };
    }

    static bool IS_DOING_HostLobbyRecreatorLoop = false;
    static IEnumerator HostLobbyRecreatorLoop() {
        var start = Time.realtimeSinceStartupAsDouble;
        while (IS_IN_LOBBY && Net.TryGetSteamTransport(out var tsteam) && CURRENT_LOBBY.lobby_steamID == CSteamID.Nil) {
            yield return null;

            if ((Time.realtimeSinceStartupAsDouble - start) > 2) {
                start = Time.realtimeSinceStartupAsDouble;

                IS_DOING_HostLobbyRecreatorLoop = true;

                tsteam.CreateLobby(KSteam.NumToLobbyType(UIMainMenu._STEAM_CHOSEN_LOBBYTYPE));

                yield return new WaitForSeconds(1);
            }
        }
    }


    void Update10s() {

        if (Net.IsRunningSteam && Net.IsServer) {
            Server_UpdateLobbyData();
        }

        UpdateRichPresence();
    }


    public static string GetAppInstallDir() {
        string Folder;
        var ret = SteamApps.GetAppInstallDir(SteamUtils.GetAppID(), out Folder, 260);
        if (Folder == null) {
            Folder = "";
        }
        return Folder;
    }


    private void __CheckCommandLineForSteamArgs() {
        if (!Loaded) return;

        var args = System.Environment.GetCommandLineArgs();

        {
            SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024);
            if (pszCommandLine == null) {
                pszCommandLine = "";
            } else {
                args = pszCommandLine.Split(new char[]{' '}, options: StringSplitOptions.RemoveEmptyEntries);
            }
        }

        GetAppInstallDir();


        for (int i = 0; i < args.Length; i++) {
            var arg = args[i];

            try {
                if (arg == ("+connect_lobby")) { // https://partner.steamgames.com/doc/api/ISteamFriends#GameLobbyJoinRequested_t
                    i++; if (i == args.Length) break; arg = args[i];
                    if (ulong.TryParse(arg,out var newlobbysteamid)) {
                        if (!Net.IsRunning) {
                            KSteam.steamtestlog($"CMD: Will join to lobby {newlobbysteamid.ToString()}");

                            TransportSteamworks.OnWantToJoinLobby(newlobbysteamid);
                        }
                    }
                }
            } catch (Exception e) {
                Together.log.error($"STEAM: CMD: {e.ToString()}");
            }
        }
    }

    
    // this is called before creating this component, it checks if steamworks dll is present, if steam is working and everything is in order
    public static void CheckSteam() {
        if (Loaded || Plugin.FORCE_NO_STEAM) {
            return;
        }
        

        Loaded = false;


        if (!Packsize.Test()) {
            log.LogError($"Steamworks.NET: Packsize.Test() FAILED !!!");
            return;
        }
        if (!DllCheck.Test()) {
            log.LogError($"Steamworks.NET: DllCheck.Test() FAILED !!!");
            return;
        }

        try {
            var init_resultcode = SteamAPI.InitEx(out var outSteamErrMsg);
            switch (init_resultcode) {
                case ESteamAPIInitResult.k_ESteamAPIInitResult_OK: {
                    break;
                }
                default: {
                    log.LogError($"Steamworks.NET: Init failed:\n\t{outSteamErrMsg}");
                    return;
                }
            }
        } catch (Exception e) {
            Together.log.error(e);
            return;
        }
        STEAM_APPID = SteamUtils.GetAppID().m_AppId;



        APP_IS_DEMO = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO;
        APP_IS_PLAYTEST = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST;
        APP_IS_PIRATE = STEAM_APPID == CONST_STEAMAPPID_SPACEWAR;



        Loaded = true;
        try {
            KSteam.Init();

            Plugin.s.GetOrAddComponent<KSteam>(); 
        } catch (Exception e) {
            Multiplayer.DoMultiplayerStatusMessageError($"FAILED TO INIT STEAM:\n{e.ToString()}");
            Loaded = false;
            return;
        }

        if (Together.log.verbose) Together.log.l($"Steamworks initialized: {KSteam.GetLocalUsername()} - {KSteam.GetLocalUserSteamID()}");
        Multiplayer.DoMultiplayerStatusMessageLog($"Steamworks initialized!");

    }


    public static HashSet<ulong> CoplayUsers = new();
    public static HashSet<ulong> Friends = new();
    public static HashSet<ulong> Following = new();
    static void LoadFriendList() {
        Friends.Clear();
        CoplayUsers.Clear();

        {
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < friendCount; i++) {
                CSteamID friendId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                Friends.Add(friendId.m_SteamID);
            }
        }

        {
            int coplayCount = SteamFriends.GetCoplayFriendCount();
            for (int i = 0; i < coplayCount; i++) {
                CSteamID friendId = SteamFriends.GetCoplayFriend(i);
                CoplayUsers.Add(friendId.m_SteamID);
            }
        }
    }




    private void Update() {
        if (Loaded) {
            SteamAPI.RunCallbacks();
        }
    }
    protected void OnDestroy() {
        if (Loaded) {
            SteamAPI.Shutdown();
            Loaded = false;
        }
    }


    protected void OnEnable() {
        if (Loaded) {
            // You must launch with "-debug_steamapi" in the launch args to receive warnings.
            SteamClient.SetWarningMessageHook(new SteamAPIWarningMessageHook_t(KSteam.SteamAPIDebugTextHook));
        }
    }



    public static string GetLocalUsername() {
        return SteamFriends.GetPersonaName();
    }
   
    public static CSteamID GetLocalUserSteamID() {
        return SteamUser.GetSteamID();
    }





    [AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
    internal static void SteamAPIDebugTextHook(int nSeverity, System.Text.StringBuilder pchDebugText) {
        log.LogWarning(pchDebugText);
    }

    public static string GetSteamUsername(ulong steamuserid, bool force_reload_name = false) {
        if (steamuserid == 0)
            return "[UNKNOWN]";

        if (SteamNamesCache.TryGetValue(steamuserid, out string name)) {
            return name;
        } else {

            if (SteamFriends.RequestUserInformation((CSteamID)steamuserid, true)) {
                // Name not ready yet, wait for callback PersonaStateChange_t 
                name = steamuserid.ToString();
            } else {
                // Already cached  
                name = SteamFriends.GetFriendPersonaName((CSteamID)steamuserid);
                if (name == "" || name == "[unknown]") { // Impossible to trigger this, but just for sure
                    name = steamuserid.ToString();
                } else {
                    SteamNamesCache[steamuserid] = name;
                }
            }
            return name;
        }
    }
    public static Texture2D SteamLoadImage(int imageId) {
        if (imageId == -1)
            return null;

        if (imageId == 0)
            return null;

        if (SteamImagesCache.TryGetValue(imageId, out Texture2D texture)) {
            return texture;
        }

        uint width, height;
        if (!SteamUtils.GetImageSize(imageId, out width, out height))
            return null;

        byte[] imageData = new byte[width * height * 4];

        if (!SteamUtils.GetImageRGBA(imageId, imageData, (int)(width * height * 4)))
            return null;

        Color32[] pixels = new Color32[width * height];

        int rowSize = (int)width * 4;

        for (int y = 0; y < height; y++) {
            int srcRow = (int)(height - 1 - y) * rowSize;
            int dstRow = y * (int)width;

            for (int x = 0; x < width; x++) {
                int i = srcRow + x * 4;

                pixels[dstRow + x] = new Color32(
                    imageData[i],
                    imageData[i + 1],
                    imageData[i + 2],
                    imageData[i + 3]
                );
            }
        }

        texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false, true);
        texture.SetPixels32(pixels);
        texture.Apply();

        SteamImagesCache[imageId] = texture;

        return texture;
    }
    public static void LoadPlayerProfilePics(ScavPlayer plr) {
        var steamid = (CSteamID)plr.SteamId;
        if (steamid.m_SteamID == 0) return;

        if (plr.IconLarge == null) {
            Util.DelayCallLambda(UnityEngine.Random.value,() => {
                if (plr != null && plr.IconLarge == null) {
                    plr.IconLarge = SteamLoadImage(SteamFriends.GetLargeFriendAvatar(steamid));
                    if (plr.IconLarge == null) return;
                    var spr = plr.IconLargeSprite = Util.CreateSprite(plr.IconLarge);
                    if (plr.IconLargeSprite == null) plr.IconLargeSprite = spr;
                    if (plr.IconMediumSprite == null) plr.IconMediumSprite = spr;
                    if (plr.IconSmallSprite == null) plr.IconSmallSprite = spr;
                }
            });
        }
        if (plr.IconMedium == null) {
            Util.DelayCallLambda(UnityEngine.Random.value, () => {
                if (plr != null && plr.IconMedium == null) {
                    plr.IconMedium = SteamLoadImage(SteamFriends.GetMediumFriendAvatar(steamid));
                    if (plr.IconMedium == null) return;
                    var spr = plr.IconMediumSprite = Util.CreateSprite(plr.IconMedium);
                    if (plr.IconLargeSprite == null) plr.IconLargeSprite = spr;
                    if (plr.IconMediumSprite == null) plr.IconMediumSprite = spr;
                    if (plr.IconSmallSprite == null) plr.IconSmallSprite = spr;
                }
            });
        }
        if (plr.IconSmall == null) {
            Util.DelayCallLambda(UnityEngine.Random.value, () => {
                if (plr != null && plr.IconSmall == null) {
                    plr.IconSmall = SteamLoadImage(SteamFriends.GetSmallFriendAvatar(steamid));
                    if (plr.IconSmall == null) return;
                    var spr = plr.IconSmallSprite = Util.CreateSprite(plr.IconSmall);
                    if (plr.IconLargeSprite == null) plr.IconLargeSprite = spr;
                    if (plr.IconMediumSprite == null) plr.IconMediumSprite = spr;
                    if (plr.IconSmallSprite == null) plr.IconSmallSprite = spr;
                }
            });
        }

    }
    static Dictionary<int,Texture2D> SteamImagesCache = new();
    static Dictionary<ulong, string> SteamNamesCache = new();

    public static CSteamID LobbyId => CURRENT_LOBBY.lobby_steamID;







    public const string _OLDLOBBYDATAKEYPREFIX = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_";
    public const string _LOBBYDATAKEYPREFIX = "COOP_MOD_";
    public const string LOBBYDATAKEY_VERSION = _LOBBYDATAKEYPREFIX + "VERSION";
    public const string LOBBYDATAKEY_LOBBYNAME = _LOBBYDATAKEYPREFIX + "LOBBYNAME";
    public const string LOBBYDATAKEY_DESCRIPTION = _LOBBYDATAKEYPREFIX + "DESCRIPTION";
    public const string LOBBYDATAKEY_GAMEMODE = _LOBBYDATAKEYPREFIX + "GAMEMODE";
    public const string LOBBYDATAKEY_HASPASSWORD = _LOBBYDATAKEYPREFIX + "HASPASSWORD";
    public const string LOBBYDATAKEY_ISDEDICATED = _LOBBYDATAKEYPREFIX + "ISDEDICATED";
    public const string LOBBYDATAKEY_MIDJOIN_LOCKED = _LOBBYDATAKEYPREFIX + "LOCKED";
    public const string LOBBYDATAKEY_CURRENTLAYER = _LOBBYDATAKEYPREFIX + "CURRENTLAYER";
    public const string LOBBYDATAKEY_LIVINGCOUNT = _LOBBYDATAKEYPREFIX + "LIVINGCOUNT";
    public const string LOBBYDATAKEY_PLAYERCOUNT = _LOBBYDATAKEYPREFIX + "PLRCOUNT";
    public const string LOBBYDATAKEY_DEPTH = _LOBBYDATAKEYPREFIX + "DEPTH";
    public const string LOBBYDATAKEY_AVERAGEMOOD = _LOBBYDATAKEYPREFIX + "AVGMOOD";
    public const string LOBBYDATAKEY_EXTRADATA = _LOBBYDATAKEYPREFIX + "EXTRADATA";
    public const string LOBBYDATAKEY_RUNSETTINGS = _LOBBYDATAKEYPREFIX + "RUNSETTINGS";
    public const string LOBBYDATAKEY_MODS = _LOBBYDATAKEYPREFIX + "MODS";
    public const string LOBBYDATAKEY_LANGUAGE = _LOBBYDATAKEYPREFIX + "LANGUAGE";

    static string GetMyModListAsString() {
        return String.Join(":", Multiplayer.GetModGUIDs().Select(x => x.Replace(":", "")));
    }

    public static void Server_UpdateLobbyData() {
        Net.MyServerInfo.SetValues();
        CURRENT_LOBBY.locked = Net.MyServerInfo.haspassword;
        if (KSteam.NumToLobbyType(UIMainMenu._STEAM_CHOSEN_LOBBYTYPE) != ELobbyType.k_ELobbyTypePublic) {
            CURRENT_LOBBY.locked = true;
        }

        var bucket = 0;
        if (Net.TryGetSteamTransport(out var tsteam)) {
            bucket = tsteam.hostLobbyBucket;
        }

        SteamMatchmaking.SetLobbyMemberLimit(LobbyId, Multiplayer.rules.PLAYER_COUNT_LIMIT);
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_LOBBYNAME, Net.MyServerInfo.name);
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_DESCRIPTION, Net.MyServerInfo.description);
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_VERSION, Multiplayer.FULL_VERSION_TAG); // its set at the start anyway but idk do it again here
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_GAMEMODE, Net.MyServerInfo.gamemode);
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_CURRENTLAYER, Net.MyServerInfo.cur_layer.ToString());
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_AVERAGEMOOD, Net.MyServerInfo.avg_happiness.ToString());
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_DEPTH, Net.MyServerInfo.cur_depth.ToString());
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_LIVINGCOUNT, Net.MyServerInfo.plr_living_count.ToString());
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_PLAYERCOUNT, Net.MyServerInfo.plr_count.ToString());
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_HASPASSWORD, String.IsNullOrEmpty(Multiplayer.INPUT_PASSWORD) ? "0" : "1");
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_ISDEDICATED, Net.IsDedicatedServer ? "1" : "0");
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_MIDJOIN_LOCKED, (Net.MyServerInfo.midjoin_lock_active) ? "1" : "0");
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_RUNSETTINGS, WorldMP.CompileRunSettings());
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_LANGUAGE, Locale.currentLangName);
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_MODS, GetMyModListAsString());
        
        SteamMatchmaking.SetLobbyData(LobbyId, "bucket", bucket.ToString());

        var aaa = new SteamServerInfoInLobbyExtraData(Net.MyServerInfo);
        var writer = new NetDataWriter();
        writer.PutUnmanaged(aaa);
        writer.PutArray(ScavPlayer.ClientIdToPlayerDict.Values.Select(x => x.SteamId).ToArray());
        writer.Put(Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST);
        


        writer.CompressWriter(0);
        var reader = new NetDataReader(writer);
        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_EXTRADATA, Convert.ToBase64String(reader.GetRemainingBytes()));

    }

    public static NetPublicServerInfo Client_LobbyBrowser_ReadLobbyIntoServerInfos(KSteam.Lobby slobby) {
        try {
            var VERSION = slobby.metadata[LOBBYDATAKEY_VERSION];
            var LOBBYNAME = slobby.metadata[LOBBYDATAKEY_LOBBYNAME];
            var GAMEMODE = slobby.metadata[LOBBYDATAKEY_GAMEMODE];
            var CURRENTLAYER = slobby.metadata[LOBBYDATAKEY_CURRENTLAYER];
            var DEPTH = slobby.metadata[LOBBYDATAKEY_DEPTH];
            var LIVINGCOUNT = slobby.metadata[LOBBYDATAKEY_LIVINGCOUNT];
            var PLRCOUNT = slobby.metadata[LOBBYDATAKEY_PLAYERCOUNT];
            var AVERAGEMOOD = slobby.metadata[LOBBYDATAKEY_AVERAGEMOOD];
            var EXTRADATA = slobby.metadata[LOBBYDATAKEY_EXTRADATA];
            var haspassword = slobby.metadata[LOBBYDATAKEY_HASPASSWORD];
            var ISDEDICATED = slobby.metadata[LOBBYDATAKEY_ISDEDICATED];
            var LOCKED = slobby.metadata[LOBBYDATAKEY_MIDJOIN_LOCKED];
            var MODS = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_MODS, "");
            var lang = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_LANGUAGE, "EN");
            var DESCRIPTION = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_DESCRIPTION, "");

            if (lang == null || Multiplayer.SanitizeTextInputAllowSpaces(lang) != lang || lang.Length > 10) {
                lang = "";
            }


            if (DESCRIPTION.Length > Net.MAX_SERVER_DESC_LENGTH || DESCRIPTION.CountIndices('\n') > Net.MAX_SERVER_DESC_NEWLINES) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"Lobby desc too long: lobbyId:{slobby.lobby_steamID}");
#endif 
                return null;
            }
            if (LOBBYNAME.Length > Net.MAX_SERVER_NAME_LENGTH) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"Lobby name too long: lobbyId:{slobby.lobby_steamID}");
#endif 
                return null;
            }
            if (VERSION.Length > Net.MAX_SERVER_NAME_LENGTH) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"Version string too long: lobbyId:{slobby.lobby_steamID}");
#endif 
                return null;
            }
            if (GAMEMODE.Length > 12) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"Gamemode name too long: lobbyId:{slobby.lobby_steamID}");
#endif 
                GAMEMODE = "";
            }
            if (lang.Length > 8) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"Lang name too long: lobbyId:{slobby.lobby_steamID}");
#endif 
                lang = "";
            }
            if (Multiplayer.SanitizeRichTextAllowSimpleTags(LOBBYNAME) != LOBBYNAME) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"unallowed lobby name: \"{LOBBYNAME}\" lobbyId:{slobby.lobby_steamID}");
#endif 
                LOBBYNAME = $"Expedition {slobby.lobby_steamID}";
            }
            if (Multiplayer.SanitizeRichTextAllowSimpleTags(DESCRIPTION) != DESCRIPTION) {
#if TESTING 
                if (Together.log.verbose) Together.log.warn($"unallowed lobby desc: \"{DESCRIPTION}\" lobbyId:{slobby.lobby_steamID}");
#endif 
                DESCRIPTION = "";
            }

            var l = new NetPublicServerInfo();
            l.steam_lobby_info = slobby;

            l.name = LOBBYNAME;
            l.description = DESCRIPTION;
            l.version = VERSION;
            l.haspassword = haspassword != "0";
            l.dedicated = ISDEDICATED != "0";
            l.midjoin_lock_active = LOCKED != "0";
            l.gamemode = GAMEMODE;
            l.cur_layer = int.Parse(CURRENTLAYER);
            l.avg_happiness = int.Parse(AVERAGEMOOD);
            l.cur_depth = int.Parse(DEPTH);
            l.lang = lang;

            l.plr_living_count = int.Parse(LIVINGCOUNT);
            l.plr_max = slobby.memberlimit;
            l.plr_count = int.Parse(PLRCOUNT);


            if (l.cur_layer > 4 && STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO && CURRENT_LOBBY.lobby_steamID != slobby.lobby_steamID) {
                return null; 
            }


            try {
                var extradata = Convert.FromBase64String(EXTRADATA);
                var reader = new NetDataReader(extradata);
                reader = reader.DecompressReader();

                reader.GetUnmanaged(out SteamServerInfoInLobbyExtraData ssi);
                
                ssi.Read(l);


                {
                    var steamusers = reader.GetULongArray();
                    foreach (var steamid in steamusers) {
                        if (!slobby.members.ContainsKey((CSteamID)steamid)) {

                            slobby.members[(CSteamID)steamid] = new(steamid);
                        }
                    }
                }

                var modsenforce = reader.GetBool();

                {
                    l.modlist = MODS.Split(':');
                    if (modsenforce) {
                        var mymods = GetMyModListAsString().Split(':');

                        l.enforcemodlist_locked = !(new HashSet<string>(mymods).SetEquals(l.modlist));
                    }
                }

            } catch (Exception eeeee) {
#if !RELEASE 
                if (Together.log.verbose && l.version == Multiplayer.FULL_VERSION_TAG) Together.log.warn("READING LOBBY EXTRA DATA KEY: " + eeeee.ToString());
#endif 
            }

            //TODO call func that generates all localized strings?

            return l;
        } catch (Exception e) {
            // just let it error out

#if !RELEASE 
            Together.log.warn("LOBBY LIST REFRESH DATA RETRIAVAL: (PROBABLY IGNORE CUZ ITS OUTDATED VERSION): " + e.ToString());
#endif 
        }
        return null;
    }



    public static void UpdateRichPresence() {
        //SteamFriends.ClearRichPresence();

        // https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence 
        // https://partner.steamgames.com/doc/features/enhancedrichpresence

        if (Net.IsRunning) {
#if TESTING
            SteamFriends.SetRichPresence("status", "Co-op mod: " + (Util.IsInMainMenu() ? "sigma 1..2..3.. " : WorldGeneration.world.biomeDepth.ToString()));
            //SteamFriends.SetRichPresence("connect", "WHAT IS DISS !? ");

            SteamFriends.SetRichPresence("gamestatus", "fart shot dead in florida ");

            SteamFriends.SetRichPresence("score", "67");
            SteamFriends.SetRichPresence("steam_display", "STEAMDISPLAY TESST "); // this cannot be displayed if its a not valid localization tag
#endif

            SteamFriends.SetRichPresence("steam_player_group", LobbyId.ToString());
            SteamFriends.SetRichPresence("steam_player_group_size", ScavPlayer.ClientIdToPlayerDict.Count.ToString());
        } else {
            SteamFriends.SetRichPresence("steam_player_group", null);
            SteamFriends.SetRichPresence("steam_player_group_size", null);

            SteamFriends.ClearRichPresence();
        }
    }




    public const int MAX_LOBBY_BUCKETS = 20;
    public static int LobbySearch_BucketCounter = 0;
    public static void SearchLobbies(bool do_buckets) {
        if (!Loaded) {
            return;
        }

        if (Net.CurrentServerInfo.cur_layer <= 4 && STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO) {

            SteamMatchmaking.AddRequestLobbyListNumericalFilter(LOBBYDATAKEY_CURRENTLAYER, 4, ELobbyComparison.k_ELobbyComparisonEqualToOrLessThan);
        }

        if (do_buckets) {
            SteamMatchmaking.AddRequestLobbyListStringFilter("bucket", LobbySearch_BucketCounter.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
        } else {
            LobbySearch_BucketCounter = 0;


            if (UIServerBrowser.SortHidePasswordProtected) {
                SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBYDATAKEY_HASPASSWORD, "0", ELobbyComparison.k_ELobbyComparisonEqual);
            }
            if (UIServerBrowser.SortHideIncompatible) {
                SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBYDATAKEY_VERSION, Multiplayer.FULL_VERSION_TAG, ELobbyComparison.k_ELobbyComparisonEqual);
            }
        }

        SteamMatchmaking.AddRequestLobbyListDistanceFilter(userselected_lobbyDistanceFilter);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter((int)userselected_maxLobbies); // it actually doesnt go above 50 wow
        SteamAPICall_t handle = SteamMatchmaking.RequestLobbyList();
        OnLobbyMatchListCallResult.Set(handle);

    }
    public static void SearchFriendLobbies() {
        LoadFriendList();

        foreach (var friendId in Friends) {

            FriendGameInfo_t gameInfo;
            if (SteamFriends.GetFriendGamePlayed((CSteamID)friendId, out gameInfo)) {
                // Check if friend is playing THIS game
                if (gameInfo.m_gameID.AppID().m_AppId == STEAM_APPID) {
                    // Check if they're in a lobby

#if TESTING
                    log.LogInfo($"FOUND FRIEND {SteamFriends.GetFriendPersonaName((CSteamID)friendId)} , LOBBY: {gameInfo.m_steamIDLobby} ");
#endif

                    if (gameInfo.m_steamIDLobby.IsValid()) {
                        //    FriendId = friendId;
                        //    FriendName = SteamFriends.GetFriendPersonaName(friendId);
                        //    LobbyId = gameInfo.m_steamIDLobby;

                        if (UIServerBrowser.CurServerList_ButOnlySteamLobbies.TryGetValue(gameInfo.m_steamIDLobby.m_SteamID, out var l)) {
                        } else {
                            var slobby = new Lobby();
                            UpdateLobbyInfo(gameInfo.m_steamIDLobby, ref slobby);
                            l = Client_LobbyBrowser_ReadLobbyIntoServerInfos(slobby);

                            if (l != null) {
                                UIServerBrowser.AddServerToTheList(l);
                            } else {
#if !RELEASE
                                log.LogWarning($"INCOMPATIBLE LOBBY INFO DUMP:\n{slobby.DebugReadableDump()}");
#endif
                            }
                        }

                    }
                }
            }
        }
    }




    internal static ELobbyDistanceFilter userselected_lobbyDistanceFilter = ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault;
    static ushort userselected_maxLobbies = 100;




    #region ALL_STEAM_CALLBACKS









    // just copying shit from "Steamworks.NET-Test" github repo (its public domain)

    // STEAM MATCHMAKING
    internal static Callback<FavoritesListChanged_t> FavoritesListChanged;
    internal static Callback<LobbyInvite_t> LobbyInvite;
    internal static Callback<LobbyEnter_t> LobbyEnter;
    internal static Callback<LobbyDataUpdate_t> LobbyDataUpdate;
    internal static Callback<LobbyChatUpdate_t> LobbyChatUpdate;
    internal static Callback<LobbyChatMsg_t> LobbyChatMsg;
    internal static Callback<LobbyGameCreated_t> LobbyGameCreated;
    internal static Callback<LobbyKicked_t> LobbyKicked;
    internal static Callback<FavoritesListAccountsUpdated_t> FavoritesListAccountsUpdated;
    //static Callback<SearchForGameProgressCallback_t> SearchForGameProgressCallback;  // this shit got deprecated or some shit, i cant find any documentation about it
    //static Callback<SearchForGameResultCallback_t> SearchForGameResultCallback;
    //static Callback<RequestPlayersForGameProgressCallback_t> RequestPlayersForGameProgressCallback;
    //static Callback<RequestPlayersForGameResultCallback_t> RequestPlayersForGameResultCallback;
    //static Callback<RequestPlayersForGameFinalResultCallback_t> RequestPlayersForGameFinalResultCallback;
    //static Callback<SubmitPlayerResultResultCallback_t> SubmitPlayerResultResultCallback;
    //static Callback<EndGameResultCallback_t> EndGameResultCallback;
    internal static Callback<JoinPartyCallback_t> JoinPartyCallback;
    internal static Callback<CreateBeaconCallback_t> CreateBeaconCallback;
    internal static Callback<ReservationNotificationCallback_t> ReservationNotificationCallback;
    internal static Callback<ChangeNumOpenSlotsCallback_t> ChangeNumOpenSlotsCallback;
    internal static Callback<AvailableBeaconLocationsUpdated_t> AvailableBeaconLocationsUpdated;
    internal static Callback<ActiveBeaconsUpdated_t> ActiveBeaconsUpdated;
    internal static CallResult<LobbyEnter_t> OnLobbyEnterCallResult;
    internal static CallResult<LobbyMatchList_t> OnLobbyMatchListCallResult;
    internal static CallResult<LobbyCreated_t> OnLobbyCreatedCallResult;





    static void OnFavoritesListChanged(FavoritesListChanged_t pCallback) {
        steamtestlog("[" + FavoritesListChanged_t.k_iCallback + " - FavoritesListChanged] - " + pCallback.m_nIP + " -- " + pCallback.m_nQueryPort + " -- " + pCallback.m_nConnPort + " -- " + pCallback.m_nAppID + " -- " + pCallback.m_nFlags + " -- " + pCallback.m_bAdd + " -- " + pCallback.m_unAccountId);
    }

    static void OnLobbyInvite(LobbyInvite_t pCallback) {
        steamtestlog("[" + LobbyInvite_t.k_iCallback + " - LobbyInvite] - " + pCallback.m_ulSteamIDUser + " -- " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_ulGameID);
    
        
    }

    // this gets called both on client and server 
    static void OnLobbyEnter(LobbyEnter_t pCallback) {

        var enterresponse = (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse;

        // https://partner.steamgames.com/doc/api/ISteamMatchmaking#:~:text=RequestLobbyData%2C%20SetLobbyOwner-,LobbyEnter_t,-Received%20upon%20attempting  
        steamtestlog("[" + LobbyEnter_t.k_iCallback + " - LobbyEnter] - " + 
            pCallback.m_ulSteamIDLobby
            + " -- " + pCallback.m_rgfChatPermissions // pCallback.m_rgfChatPermissions is unused bruh
            + " -- " + pCallback.m_bLocked // If true, then only invited users may join.
            + " -- " + enterresponse); // This is actually a EChatRoomEnterResponse value. This will be set to k_EChatRoomEnterResponseSuccess if the lobby was successfully joined, otherwise it will be k_EChatRoomEnterResponseError.


        if (!Net.TryGetSteamTransport(out var tsteam)) {
            LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
            IS_IN_LOBBY = false;
            if (Together.log.verbose && enterresponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist) Together.log.error("STEAM: Received OnLobbyEnter without a transport !!!");

            return;
        }
        if (enterresponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
            var msg = enterresponse.ToString();
            msg = msg.TrimStart("k_EChatRoomEnterResponse");

            Multiplayer.DoMultiplayerStatusMessageLog($"OnLobbyEnter failed! response: {msg}");

            if (!IS_DOING_HostLobbyRecreatorLoop) {
                IS_DOING_HostLobbyRecreatorLoop = false;
                LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                IS_IN_LOBBY = false;
            }
            return;
        }




        CURRENT_LOBBY.locked = Net.CurrentServerInfo.haspassword || pCallback.m_bLocked;
        UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);


        if (Net.IsServer) {
            if (tsteam.CreateServerSocket()) {
                IS_DOING_HostLobbyRecreatorLoop = false;
#if TESTING
                Multiplayer.DoMultiplayerStatusMessageLog("CreateServerSocket Success.");
#endif
                CURRENT_LOBBY.locked = false;
            } else {
                LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);// it flips the IS_IN_LOBBY and calls shutdownreset
                return;
            }
        }

        //foreach (var item in CURRENT_LOBBY.members.Keys) { SteamFriends.RequestUserInformation(item, false); }

    }

    // this gets called clientside!
    static void OnLobbyEnter_APIDispatch(LobbyEnter_t pCallback, bool bIOFailure) {
        try {
            steamtestlog("[" + LobbyEnter_t.k_iCallback + " - LobbyEnterAPIDispatch] - " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_rgfChatPermissions + " -- " + pCallback.m_bLocked + " -- " + (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse);

            if (bIOFailure) {
                ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyEnterCallResult.Handle);
                Multiplayer.DoMultiplayerStatusMessageError("STEAM: OnLobbyEnter IOFailure: " + reason);

                IS_IN_LOBBY = false;
                Net.ShutdownReset();
                
                return;
            }

            var m_LobbyEnterResponse = (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse;
            var responseStr = m_LobbyEnterResponse.ToString().TrimStart("k_EChatRoomEnterResponse");

            if (m_LobbyEnterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
                if (m_LobbyEnterResponse == EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist) {
                    Multiplayer.DoMultiplayerStatusMessageLog("STEAM: Lobby Enter Rejected: Lobby does not exist!");
                } else {
                    Multiplayer.DoMultiplayerStatusMessageError("STEAM: Lobby Enter Rejected: " + responseStr);
                }

                IS_IN_LOBBY = false;
                Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                Net.ShutdownReset();
                
                return;
            }

            if (!Net.TryGetSteamTransport(out var tsteam)) {
                LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                IS_IN_LOBBY = false;
                if (Together.log.verbose ) Together.log.error("STEAM: Received OnLobbyEnter_APIDispatch without a transport !!!");

                return;
            }

            CURRENT_LOBBY.locked = Net.CurrentServerInfo.haspassword || pCallback.m_bLocked;
            UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);



            IS_IN_LOBBY = true;


#if TESTING
            Multiplayer.DoMultiplayerStatusMessageLog($"Steam: OnLobbyEnter  lobby: {LobbyId}   ");
#else
            Multiplayer.DoMultiplayerStatusMessageLog($"Steam: OnLobbyEnter");
#endif
            UpdateRichPresence();



            if (Net.IsClient) {
                if (tsteam.CreateClientSocket()) {
                    Multiplayer.DoMultiplayerStatusMessageLog("CreateClientSocket Success.");

                    Net.OnTransportConnected();

                    var hellopacket = Multiplayer.GetClientConnectIntroductionPacket();
                    tsteam.SendThroughSteamSocket(tsteam.serverUser.connection, new ArraySegment<byte>(hellopacket.Data), Steamworks.Constants.k_nSteamNetworkingSend_Reliable);
                    
                } else {
                    LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                    IS_IN_LOBBY = false;
                    return;
                }
            }

        } catch (Exception e) {
            Together.log.error("STEAM: OnLobbyEnter_APIDispatch: " + e.ToString());
        }
    }

    static void OnLobbyDataUpdate(LobbyDataUpdate_t pCallback) {

#if false  // not necessary to log this
        if (pCallback.m_ulSteamIDLobby == pCallback.m_ulSteamIDMember) {
            steamtestlog("[" + LobbyDataUpdate_t.k_iCallback + " - LobbyDataUpdate] - LOBBY " + pCallback.m_ulSteamIDLobby + " -- " + (pCallback.m_bSuccess != 0));
        } else {
            steamtestlog("[" + LobbyDataUpdate_t.k_iCallback + " - LobbyDataUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDMember) + " -- " + (pCallback.m_bSuccess != 0));
        }
#endif
            
        var ooo = CURRENT_LOBBY.ownerID;
        UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
        CURRENT_LOBBY.ownerID = ooo;

    }



    static void OnLobbyChatUpdate(LobbyChatUpdate_t pCallback) {
        try { 
            var stateChange = (EChatMemberStateChange)pCallback.m_rgfChatMemberStateChange;
            if (pCallback.m_ulSteamIDUserChanged == pCallback.m_ulSteamIDMakingChange) {
                steamtestlog("[" + LobbyChatUpdate_t.k_iCallback + " - LobbyChatUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUserChanged) + " -- " + stateChange,true);
            } else {
                steamtestlog("[" + LobbyChatUpdate_t.k_iCallback + " - LobbyChatUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUserChanged) + " -- " + pCallback.m_ulSteamIDMakingChange + " -- " + stateChange,true);
            }

            if (IS_IN_LOBBY && Net.TryGetSteamTransport(out var tsteam)) {
            } else {
                if (Multiplayer.verbose)
                    Together.log.warn("Received OnLobbyChatUpdate while transport is not active, what?"); // happens on  k_EChatMemberStateChangeLeft!
                UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
                return;
            }


            ulong changedUser = pCallback.m_ulSteamIDUserChanged;
            ulong makingChangeUser = pCallback.m_ulSteamIDMakingChange;

            
            //k_EChatMemberStateChangeEntered	    0x0001	This user has joined or is joining the lobby.
            //k_EChatMemberStateChangeLeft	        0x0002	This user has left or is leaving the lobby.
            //k_EChatMemberStateChangeDisconnected	0x0004	User disconnected without leaving the lobby first.
            //k_EChatMemberStateChangeKicked	    0x0008	The user has been kicked.
            //k_EChatMemberStateChangeBanned	    0x0010	The user has been kicked and banned.
             

            // Convert flags → bools
            bool joined = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            bool left = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0;
            bool disconnected = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0;
            bool kicked = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0;
            bool banned = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0;

            ulong localUser = SteamUser.GetSteamID().m_SteamID;
            bool isLocalUser = changedUser == localUser;

            // ---- IF STATEMENTS FOR EVERYTHING ----

            if (joined) {
                if (isLocalUser) {
                    // local user joined lobby
                } else {
                    if (tsteam.i_am_owner_of_this_lobby && BanList.IsBanned(pCallback.m_ulSteamIDUserChanged)) {
                        tsteam.KickMember((CSteamID)pCallback.m_ulSteamIDUserChanged, "Banned!");
                        tsteam.RemoveSteamUser(pCallback.m_ulSteamIDUserChanged);
                    } else {
                        SteamFriends.RequestUserInformation((CSteamID)pCallback.m_ulSteamIDUserChanged, true);
                    }
                }
            }


            if (left || disconnected || kicked || banned) { // anything related to getting disconnected

                if (isLocalUser && tsteam.i_am_owner_of_this_lobby) {
                    Multiplayer.DoMultiplayerStatusMessageLog($"I got removed from the lobby. But im the owner!\nAttempting to recreate lobby in a sec...");

                    CURRENT_LOBBY.lobby_steamID = CSteamID.Nil;
                    tsteam.Shutdown(false); // clean up the sockets
                    Util.StartCoroutine(HostLobbyRecreatorLoop);
                    
                    return;
                }

                if (isLocalUser) {
                    Multiplayer.DoMultiplayerStatusMessageLog($"I got removed from the lobby.");

                    if (Util.IsInWorld()) {
                        Multiplayer.showMultiplayerMenu = true; // to avoid another log message and re-disconnecting 
                        PlayerCamera.main.ToMainMenu();
                    }
                    UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
                    LeaveLobbyAndResetNet(true);
                    return;
                }
                // log the steam flags and the live socket before RemoveSteamUser closes it
                int rawFlags = (int)stateChange;
                int memberCount = SteamMatchmaking.GetNumLobbyMembers((CSteamID)pCallback.m_ulSteamIDLobby);
                string persona = SteamFriends.GetFriendPersonaName((CSteamID)changedUser);
                bool closeP2p = tsteam.connectionMapping.ContainsKey(changedUser);
                string connSnap = tsteam.DescribeLobbyLeftConnection(changedUser);
                Together.log.l("[lobby-left] steam=" + changedUser
                    + " name=\"" + TransportSteamworks.OneLine(persona) + "\""
                    + " flags=" + stateChange + " (" + rawFlags + ")"
                    + " joined=" + (joined ? 1 : 0)
                    + " left=" + (left ? 1 : 0)
                    + " disconnected=" + (disconnected ? 1 : 0)
                    + " kicked=" + (kicked ? 1 : 0)
                    + " banned=" + (banned ? 1 : 0)
                    + " making=" + makingChangeUser
                    + " self=" + (changedUser == makingChangeUser ? 1 : 0)
                    + " lobby=" + pCallback.m_ulSteamIDLobby
                    + " members=" + memberCount
                    + " closeP2P=" + (closeP2p ? 1 : 0)
                    + " method=TransportSteamworks.RemoveSteamUser "
                    + connSnap);
                tsteam.RemoveSteamUser(pCallback.m_ulSteamIDUserChanged);


                // TODO, THIS IS TEMPORARY SOLUTION TO KILL ALL CLIENTS , BEFORE I FIGURE OUT HOST TRANSFER
                if (Net.IsClient) {
                    if (pCallback.m_ulSteamIDUserChanged == CURRENT_LOBBY.ownerID.m_SteamID) {
                        Multiplayer.DoMultiplayerStatusMessageLog("LOBBY OWNER LEFT, LEAVING");

                        if (Util.IsInWorld()) {
                            Multiplayer.showMultiplayerMenu = true;// to avoid another log message and re-disconnecting 
                            PlayerCamera.main.ToMainMenu();
                        }
                        LeaveLobbyAndResetNet(true);
                    }
                }

                if (isLocalUser) {
                    // local user was banned
                } else {
                    // another user was banned
                }
            }


        } catch (Exception e) {
            Together.log.error("STEAM: LobbyChatUpdate: " + e.ToString());
        }
        UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
    }

    static void OnLobbyChatMsg(LobbyChatMsg_t pCallback) {
        steamtestlog("[" + LobbyChatMsg_t.k_iCallback + " - LobbyChatMsg] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUser) + " -- " + (EChatEntryType)pCallback.m_eChatEntryType + " -- " + pCallback.m_iChatID);

        CSteamID SteamIDUser;
        byte[] data = new byte[4096];
        EChatEntryType ChatEntryType;
        int ret = SteamMatchmaking.GetLobbyChatEntry((CSteamID)pCallback.m_ulSteamIDLobby, (int)pCallback.m_iChatID, out SteamIDUser, data, data.Length, out ChatEntryType);
        steamtestlog("GetLobbyChatEntry(" + (CSteamID)pCallback.m_ulSteamIDLobby + ", " + (int)pCallback.m_iChatID + ", out SteamIDUser, Data, Data.Length, out ChatEntryType) : " + ret + " -- " + SteamIDUser + " -- " + System.Text.Encoding.UTF8.GetString(data,0, ret).Trim() + " -- " + ChatEntryType);


        string msg = System.Text.Encoding.UTF8.GetString(data, 0, ret);
        if (msg.StartsWith("KICK:")) {// STEAM DOES NOT PROVIDE A WAY TO KICK MEMBERS WHAAAAAAAATTTTTTTTT ???????????????????????????????
            if (pCallback.m_ulSteamIDUser == CURRENT_LOBBY.ownerID.m_SteamID ) {
                var reason = "u got kicked for some reson";

                msg = msg.Substring(5); // remove kick: prefix
                var colonloc = msg.IndexOf(':');
                if (colonloc != -1) {
                    reason = msg.Substring(colonloc + 1);
                    msg = msg.Substring(0,colonloc);
                }
                ulong targetId = ulong.Parse(msg);

                if ( targetId == SteamUser.GetSteamID().m_SteamID ) {
                    Multiplayer.DoMultiplayerStatusMessageLog("STEAM: Kicked: " + reason);
                    LeaveLobbyAndResetNet(true);
                }
            }
        }
    }

    static void OnLobbyGameCreated(LobbyGameCreated_t pCallback) {
        steamtestlog("[" + LobbyGameCreated_t.k_iCallback + " - LobbyGameCreated] - " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_ulSteamIDGameServer + " -- " + pCallback.m_unIP + " -- " + pCallback.m_usPort);
    }

    static void OnLobbyMatchList(LobbyMatchList_t pCallback, bool bIOFailure) {
        LOBBIES.Clear();
        if (Together.log.verbose) steamtestlog("[" + LobbyMatchList_t.k_iCallback + " - LobbyMatchList] - " + pCallback.m_nLobbiesMatching + $"  {(LobbySearch_BucketCounter == 0 ? "" : (LobbySearch_BucketCounter - 1).ToString())}");


        if (bIOFailure) {
            ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyMatchListCallResult.Handle);
            Together.log.error("OnLobbyMatchList encountered an IOFailure due to: " + reason);
            return; 
        }
        try {

            if (pCallback.m_nLobbiesMatching == 0) {
                Multiplayer.DoMultiplayerStatusMessageLog("STEAM: No lobbies found, lol, game fell off");
            } else {
                for (int i = 0; i < pCallback.m_nLobbiesMatching; i++) {
                    var slobby = new Lobby();

                    UpdateLobbyInfo(SteamMatchmaking.GetLobbyByIndex(i), ref slobby);

                    LOBBIES.Add(slobby);

                    var l = Client_LobbyBrowser_ReadLobbyIntoServerInfos(slobby);
                    if (l != null) {
                        UIServerBrowser.AddServerToTheList(l);
                    } else {
#if !RELEASE
                        log.LogWarning($"INCOMPATIBLE LOBBY INFO DUMP:\n{slobby.DebugReadableDump()}");
#endif
                    }
                }
            }


            // now check for friends in those lobbies
            if (LobbySearch_BucketCounter == 0) {
                SearchFriendLobbies();
            }

            // Search next buckets
            if (
                UIServerBrowser.CurServerCount > 35 // do it if reaching max lobbies
                && LobbySearch_BucketCounter <= MAX_LOBBY_BUCKETS
            ) {
                Util.DelayCallLambda(0.2f, () => {
                    KSteam.SearchLobbies(true);
                    LobbySearch_BucketCounter++;
                });
            }

        } catch (Exception e) {
            Together.log.error("STEAM: LobbyMatchList: " + e.ToString());
        }
    }

    static void OnLobbyKicked(LobbyKicked_t pCallback) {
        steamtestlog("[" + LobbyKicked_t.k_iCallback + " - LobbyKicked] - " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_ulSteamIDAdmin + " -- " + pCallback.m_bKickedDueToDisconnect);
    }

    static void OnLobbyCreated(LobbyCreated_t pCallback, bool bIOFailure) {
        steamtestlog("[" + LobbyCreated_t.k_iCallback + " - LobbyCreated] - " + pCallback.m_eResult + " -- " + pCallback.m_ulSteamIDLobby);

        if (bIOFailure) {
            ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyCreatedCallResult.Handle);
            Multiplayer.DoMultiplayerStatusMessageError("STEAM: OnLobbyCreated encountered an IOFailure due to: " + reason);

            IS_IN_LOBBY = false;
            Net.ShutdownReset();
            Util.PlayUISound(PlayerCamera.UISoundType.Deny);
            return;
        } else if (pCallback.m_eResult != EResult.k_EResultOK) {
            Util.PlayUISound(PlayerCamera.UISoundType.Deny);
            Net.ShutdownReset();
            IS_IN_LOBBY = false;
            if (pCallback.m_eResult == EResult.k_EResultNoConnection) {
                Multiplayer.DoMultiplayerStatusMessageError($"Steam: Create Lobby FAILED: No internet connection.");
                return;
            }
            Multiplayer.DoMultiplayerStatusMessageError($"Steam: Create Lobby FAILED: {pCallback.m_eResult}");
            return;
        }

        if (Net.Transport == null || (Net.Transport is not TransportSteamworks)) {
            Multiplayer.DoMultiplayerStatusMessageError($"CREATED LOBBY BUT THERES NO STEAMWORKS TRASNPORT WHAAAAT??!?!?!!??");

            IS_IN_LOBBY = false;
            Net.ShutdownReset();
            return;
        }

        UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);

        IS_IN_LOBBY = true;

        var bucket = 0;
        if (Net.TryGetSteamTransport(out var tsteam)) {
            bucket = tsteam.hostLobbyBucket;
        }

        Net.OnTransportConnected();
        
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.SteamId = GetLocalUserSteamID().m_SteamID;
            ScavPlayer.LocalPlayer.ApplyNameAndColor(ScavPlayer.LocalPlayer.SteamId.ToString(), UIMainMenu.LAST_VALID_INPUT_COLOR);
            
            tsteam.SteamIDToNetPlayerDict[ScavPlayer.LocalPlayer.SteamId] = ScavPlayer.LocalPlayer;

            // but client receives their player info from server
        }

        SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_VERSION, Multiplayer.FULL_VERSION_TAG);
        SteamMatchmaking.SetLobbyData(LobbyId, "bucket", bucket.ToString());
        Util.DelayCallLambda(0.5f, () => { 
            if (!IS_IN_LOBBY) {
                return;
            }
            Server_UpdateLobbyData();
        });

        Multiplayer.DoMultiplayerStatusMessageLog($"STEAM: Lobby Created :3");
        UpdateRichPresence();
    }

    static void OnFavoritesListAccountsUpdated(FavoritesListAccountsUpdated_t pCallback) {
        steamtestlog("[" + FavoritesListAccountsUpdated_t.k_iCallback + " - FavoritesListAccountsUpdated] - " + pCallback.m_eResult);
    }

    //static void OnSearchForGameProgressCallback(SearchForGameProgressCallback_t pCallback) {
    //    steamtestlog("[" + SearchForGameProgressCallback_t.k_iCallback + " - SearchForGameProgressCallback] - " + pCallback.m_ullSearchID + " -- " + pCallback.m_eResult + " -- " + pCallback.m_lobbyID + " -- " + pCallback.m_steamIDEndedSearch + " -- " + pCallback.m_nSecondsRemainingEstimate + " -- " + pCallback.m_cPlayersSearching);
    //}
    //
    //static void OnSearchForGameResultCallback(SearchForGameResultCallback_t pCallback) {
    //    steamtestlog("[" + SearchForGameResultCallback_t.k_iCallback + " - SearchForGameResultCallback] - " + pCallback.m_ullSearchID + " -- " + pCallback.m_eResult + " -- " + pCallback.m_nCountPlayersInGame + " -- " + pCallback.m_nCountAcceptedGame + " -- " + pCallback.m_steamIDHost + " -- " + pCallback.m_bFinalCallback);
    //}
    //
    //static void OnRequestPlayersForGameProgressCallback(RequestPlayersForGameProgressCallback_t pCallback) {
    //    steamtestlog("[" + RequestPlayersForGameProgressCallback_t.k_iCallback + " - RequestPlayersForGameProgressCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ullSearchID);
    //}
    //
    //static void OnRequestPlayersForGameResultCallback(RequestPlayersForGameResultCallback_t pCallback) {
    //    steamtestlog("[" + RequestPlayersForGameResultCallback_t.k_iCallback + " - RequestPlayersForGameResultCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ullSearchID + " -- " + pCallback.m_SteamIDPlayerFound + " -- " + pCallback.m_SteamIDLobby + " -- " + pCallback.m_ePlayerAcceptState + " -- " + pCallback.m_nPlayerIndex + " -- " + pCallback.m_nTotalPlayersFound + " -- " + pCallback.m_nTotalPlayersAcceptedGame + " -- " + pCallback.m_nSuggestedTeamIndex + " -- " + pCallback.m_ullUniqueGameID);
    //}
    //
    //static void OnRequestPlayersForGameFinalResultCallback(RequestPlayersForGameFinalResultCallback_t pCallback) {
    //    steamtestlog("[" + RequestPlayersForGameFinalResultCallback_t.k_iCallback + " - RequestPlayersForGameFinalResultCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ullSearchID + " -- " + pCallback.m_ullUniqueGameID);
    //}
    //
    //static void OnSubmitPlayerResultResultCallback(SubmitPlayerResultResultCallback_t pCallback) {
    //    steamtestlog("[" + SubmitPlayerResultResultCallback_t.k_iCallback + " - SubmitPlayerResultResultCallback] - " + pCallback.m_eResult + " -- " + pCallback.ullUniqueGameID + " -- " + pCallback.steamIDPlayer);
    //}
    //
    //static void OnEndGameResultCallback(EndGameResultCallback_t pCallback) {
    //    steamtestlog("[" + EndGameResultCallback_t.k_iCallback + " - EndGameResultCallback] - " + pCallback.m_eResult + " -- " + pCallback.ullUniqueGameID);
    //}

    static void OnJoinPartyCallback(JoinPartyCallback_t pCallback) {
        steamtestlog("[" + JoinPartyCallback_t.k_iCallback + " - JoinPartyCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ulBeaconID + " -- " + pCallback.m_SteamIDBeaconOwner + " -- " + pCallback.m_rgchConnectString);
    }

    static void OnCreateBeaconCallback(CreateBeaconCallback_t pCallback) {
        steamtestlog("[" + CreateBeaconCallback_t.k_iCallback + " - CreateBeaconCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ulBeaconID);
    }

    static void OnReservationNotificationCallback(ReservationNotificationCallback_t pCallback) {
        steamtestlog("[" + ReservationNotificationCallback_t.k_iCallback + " - ReservationNotificationCallback] - " + pCallback.m_ulBeaconID + " -- " + pCallback.m_steamIDJoiner);
    }

    static void OnChangeNumOpenSlotsCallback(ChangeNumOpenSlotsCallback_t pCallback) {
        steamtestlog("[" + ChangeNumOpenSlotsCallback_t.k_iCallback + " - ChangeNumOpenSlotsCallback] - " + pCallback.m_eResult);
    }

    static void OnAvailableBeaconLocationsUpdated(AvailableBeaconLocationsUpdated_t pCallback) {
        steamtestlog("[" + AvailableBeaconLocationsUpdated_t.k_iCallback + " - AvailableBeaconLocationsUpdated]");
    }

    static void OnActiveBeaconsUpdated(ActiveBeaconsUpdated_t pCallback) {
        steamtestlog("[" + ActiveBeaconsUpdated_t.k_iCallback + " - ActiveBeaconsUpdated]");
    }










    // STEAM FRIENDS

    static Callback<PersonaStateChange_t> PersonaStateChange;
    static Callback<GameOverlayActivated_t> GameOverlayActivated;
    static Callback<GameServerChangeRequested_t> GameServerChangeRequested;
    static Callback<GameLobbyJoinRequested_t> GameLobbyJoinRequested;
    static Callback<AvatarImageLoaded_t> AvatarImageLoaded;
    static Callback<FriendRichPresenceUpdate_t> FriendRichPresenceUpdate;
    static Callback<GameRichPresenceJoinRequested_t> GameRichPresenceJoinRequested;
    static Callback<GameConnectedClanChatMsg_t> GameConnectedClanChatMsg;
    static Callback<GameConnectedChatJoin_t> GameConnectedChatJoin;
    static Callback<GameConnectedChatLeave_t> GameConnectedChatLeave;
    static Callback<GameConnectedFriendChatMsg_t> GameConnectedFriendChatMsg;
    static Callback<UnreadChatMessagesChanged_t> UnreadChatMessagesChanged;
    static Callback<OverlayBrowserProtocolNavigation_t> OverlayBrowserProtocolNavigation;
    static Callback<EquippedProfileItemsChanged_t> EquippedProfileItemsChanged;
    static CallResult<ClanOfficerListResponse_t> OnClanOfficerListResponseCallResult;
    static CallResult<DownloadClanActivityCountsResult_t> OnDownloadClanActivityCountsResultCallResult;
    static CallResult<JoinClanChatRoomCompletionResult_t> OnJoinClanChatRoomCompletionResultCallResult;
    static CallResult<FriendsGetFollowerCount_t> OnFriendsGetFollowerCountCallResult;
    static CallResult<FriendsIsFollowing_t> OnFriendsIsFollowingCallResult;
    static CallResult<FriendsEnumerateFollowingList_t> OnFriendsEnumerateFollowingListCallResult;
    static CallResult<EquippedProfileItems_t> OnEquippedProfileItemsCallResult;


    static void OnPersonaStateChange(PersonaStateChange_t pCallback) {
        var changes = (EPersonaChange)pCallback.m_nChangeFlags;

        var steamID = (CSteamID)pCallback.m_ulSteamID;

        //steamtestlog("[" + PersonaStateChange_t.k_iCallback + " - PersonaStateChange] - " + steamID + " -- " + changes.ToString());


        if (Net.TryGetSteamTransport(out var tsteam)) {
            if (changes.HasFlag(EPersonaChange.k_EPersonaChangeName) || changes.HasFlag(EPersonaChange.k_EPersonaChangeNickname) || changes.HasFlag(EPersonaChange.k_EPersonaChangeNameFirstSet)) {

                KSteam.GetSteamUsername(steamID.m_SteamID, true);

                if (tsteam.SteamIDToNetPlayerDict.TryGetValue(pCallback.m_ulSteamID, out var plr)) {
                    if (!plr.nameIsCustom) {
                        plr.ApplyNameAndColor(steamID.ToString(), plr.playerColor);
                    }
                }

            } 
            if (changes.HasFlag(EPersonaChange.k_EPersonaChangeAvatar)) {

            }
            if (changes.HasFlag(EPersonaChange.k_EPersonaChangeRelationshipChanged)) {
                LoadFriendList();
            }
        }
    }

    static void OnGameOverlayActivated(GameOverlayActivated_t pCallback) {
        steamtestlog("[" + GameOverlayActivated_t.k_iCallback + " - GameOverlayActivated] - " + pCallback.m_bActive + " -- " + pCallback.m_bUserInitiated + " -- " + pCallback.m_nAppID + " -- " + pCallback.m_dwOverlayPID);
        IS_IN_STEAMOVERLAY = pCallback.m_bActive != 0;
    }

    static void OnGameServerChangeRequested(GameServerChangeRequested_t pCallback) {
        steamtestlog("[" + GameServerChangeRequested_t.k_iCallback + " - GameServerChangeRequested] - " + pCallback.m_rgchServer + " -- " + pCallback.m_rgchPassword);
    }

    static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t pCallback) {
        steamtestlog("[" + GameLobbyJoinRequested_t.k_iCallback + " - GameLobbyJoinRequested] - " + pCallback.m_steamIDLobby + " -- " + pCallback.m_steamIDFriend);

        if (Multiplayer.IsNetworkActiveOrIsInGame()) {
            Multiplayer.DoMultiplayerStatusMessageLog("GameLobbyJoinRequested: Nah, Already in a game.");
        } else {
            Multiplayer.DoMultiplayerStatusMessageLog($"GameLobbyJoinRequested: lobbyID:{pCallback.m_steamIDLobby}");
            TransportSteamworks.OnWantToJoinLobby(pCallback.m_steamIDLobby.m_SteamID);
        }
    }

    static void OnAvatarImageLoaded(AvatarImageLoaded_t pCallback) {
        
        var steamID = (CSteamID)pCallback.m_steamID;

        steamtestlog("[" + AvatarImageLoaded_t.k_iCallback + " - AvatarImageLoaded] - " + pCallback.m_steamID + " -- " + pCallback.m_iImage + " -- " + pCallback.m_iWide + " -- " + pCallback.m_iTall);


        if (Net.TryGetSteamTransport(out var tsteam)) {
            if (tsteam.SteamIDToNetPlayerDict.TryGetValue((ulong)steamID, out var plr)) {
                LoadPlayerProfilePics(plr);
            }
        }

    }

    static void OnClanOfficerListResponse(ClanOfficerListResponse_t pCallback, bool bIOFailure) {
        steamtestlog("[" + ClanOfficerListResponse_t.k_iCallback + " - ClanOfficerListResponse] - " + pCallback.m_steamIDClan + " -- " + pCallback.m_cOfficers + " -- " + pCallback.m_bSuccess);
    }

    static void OnFriendRichPresenceUpdate(FriendRichPresenceUpdate_t pCallback) {
        // useless log
        //steamtestlog("[" + FriendRichPresenceUpdate_t.k_iCallback + " - FriendRichPresenceUpdate] - " + pCallback.m_steamIDFriend + " -- " + pCallback.m_nAppID);
    }

    static void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t pCallback) {
        steamtestlog("[" + GameRichPresenceJoinRequested_t.k_iCallback + " - GameRichPresenceJoinRequested] - " + pCallback.m_steamIDFriend + " -- " + pCallback.m_rgchConnect);
    }

    static void OnGameConnectedClanChatMsg(GameConnectedClanChatMsg_t pCallback) {
        steamtestlog("[" + GameConnectedClanChatMsg_t.k_iCallback + " - GameConnectedClanChatMsg] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_steamIDUser + " -- " + pCallback.m_iMessageID);

        string Text;
        EChatEntryType ChatEntryType;
        CSteamID Chatter;
        int ret = SteamFriends.GetClanChatMessage(pCallback.m_steamIDClanChat, pCallback.m_iMessageID, out Text, 2048, out ChatEntryType, out Chatter); // Must be called from within OnGameConnectedClanChatMsg
        steamtestlog(ret + " " + Chatter + ": " + Text);
    }

    static void OnGameConnectedChatJoin(GameConnectedChatJoin_t pCallback) {
        steamtestlog("[" + GameConnectedChatJoin_t.k_iCallback + " - GameConnectedChatJoin] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_steamIDUser);
    }

    static void OnGameConnectedChatLeave(GameConnectedChatLeave_t pCallback) {
        steamtestlog("[" + GameConnectedChatLeave_t.k_iCallback + " - GameConnectedChatLeave] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_steamIDUser + " -- " + pCallback.m_bKicked + " -- " + pCallback.m_bDropped);
    }

    static void OnDownloadClanActivityCountsResult(DownloadClanActivityCountsResult_t pCallback, bool bIOFailure) {
        steamtestlog("[" + DownloadClanActivityCountsResult_t.k_iCallback + " - DownloadClanActivityCountsResult] - " + pCallback.m_bSuccess);
    }

    static void OnJoinClanChatRoomCompletionResult(JoinClanChatRoomCompletionResult_t pCallback, bool bIOFailure) {
        steamtestlog("[" + JoinClanChatRoomCompletionResult_t.k_iCallback + " - JoinClanChatRoomCompletionResult] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_eChatRoomEnterResponse);
    }

    static void OnGameConnectedFriendChatMsg(GameConnectedFriendChatMsg_t pCallback) {
        steamtestlog("[" + GameConnectedFriendChatMsg_t.k_iCallback + " - GameConnectedFriendChatMsg] - " + pCallback.m_steamIDUser + " -- " + pCallback.m_iMessageID);

        string Text;
        EChatEntryType ChatEntryType;
        int ret = SteamFriends.GetFriendMessage(pCallback.m_steamIDUser, pCallback.m_iMessageID, out Text, 2048, out ChatEntryType); // Must be called from within OnGameConnectedFriendChatMsg
        steamtestlog(ret + " " + pCallback.m_steamIDUser + ": " + Text);
    }

    static void OnFriendsGetFollowerCount(FriendsGetFollowerCount_t pCallback, bool bIOFailure) {
        steamtestlog("[" + FriendsGetFollowerCount_t.k_iCallback + " - FriendsGetFollowerCount] - " + pCallback.m_eResult + " -- " + pCallback.m_steamID + " -- " + pCallback.m_nCount);
    }

    static void OnFriendsIsFollowing(FriendsIsFollowing_t pCallback, bool bIOFailure) {
        steamtestlog("[" + FriendsIsFollowing_t.k_iCallback + " - FriendsIsFollowing] - " + pCallback.m_eResult + " -- " + pCallback.m_steamID + " -- " + pCallback.m_bIsFollowing);
    
        if (pCallback.m_bIsFollowing) {
            Following.Add(pCallback.m_steamID.m_SteamID);
        }
    }

    static void OnFriendsEnumerateFollowingList(FriendsEnumerateFollowingList_t pCallback, bool bIOFailure) {
        steamtestlog("[" + FriendsEnumerateFollowingList_t.k_iCallback + " - FriendsEnumerateFollowingList] - " + pCallback.m_eResult + " -- " + pCallback.m_rgSteamID + " -- " + pCallback.m_nResultsReturned + " -- " + pCallback.m_nTotalResultCount);
    }

    static void OnUnreadChatMessagesChanged(UnreadChatMessagesChanged_t pCallback) {
        steamtestlog("[" + UnreadChatMessagesChanged_t.k_iCallback + " - UnreadChatMessagesChanged]");
    }

    static void OnOverlayBrowserProtocolNavigation(OverlayBrowserProtocolNavigation_t pCallback) {
        steamtestlog("[" + OverlayBrowserProtocolNavigation_t.k_iCallback + " - OverlayBrowserProtocolNavigation] - " + pCallback.rgchURI);
    }

    static void OnEquippedProfileItemsChanged(EquippedProfileItemsChanged_t pCallback) {
        steamtestlog("[" + EquippedProfileItemsChanged_t.k_iCallback + " - EquippedProfileItemsChanged] - " + pCallback.m_steamID);
    }

    static void OnEquippedProfileItems(EquippedProfileItems_t pCallback, bool bIOFailure) {
        steamtestlog("[" + EquippedProfileItems_t.k_iCallback + " - EquippedProfileItems] - " + pCallback.m_eResult + " -- " + pCallback.m_steamID + " -- " + pCallback.m_bHasAnimatedAvatar + " -- " + pCallback.m_bHasAvatarFrame + " -- " + pCallback.m_bHasProfileModifier + " -- " + pCallback.m_bHasProfileBackground + " -- " + pCallback.m_bHasMiniProfileBackground);
    }




    // called only once after steamworks dll has loaded
    public static void Init() {
        if (!KSteam.Loaded) return;
        

        { // bind all callbacks

            // STEAM MATCHMAKING
            FavoritesListChanged = Callback<FavoritesListChanged_t>.Create(OnFavoritesListChanged);
            LobbyInvite = Callback<LobbyInvite_t>.Create(OnLobbyInvite);
            LobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            LobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
            LobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            LobbyChatMsg = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMsg);
            LobbyGameCreated = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
            LobbyKicked = Callback<LobbyKicked_t>.Create(OnLobbyKicked);
            FavoritesListAccountsUpdated = Callback<FavoritesListAccountsUpdated_t>.Create(OnFavoritesListAccountsUpdated);
            //SearchForGameProgressCallback = Callback<SearchForGameProgressCallback_t>.Create(OnSearchForGameProgressCallback);
            //SearchForGameResultCallback = Callback<SearchForGameResultCallback_t>.Create(OnSearchForGameResultCallback);
            //RequestPlayersForGameProgressCallback = Callback<RequestPlayersForGameProgressCallback_t>.Create(OnRequestPlayersForGameProgressCallback);
            //RequestPlayersForGameResultCallback = Callback<RequestPlayersForGameResultCallback_t>.Create(OnRequestPlayersForGameResultCallback);
            //RequestPlayersForGameFinalResultCallback = Callback<RequestPlayersForGameFinalResultCallback_t>.Create(OnRequestPlayersForGameFinalResultCallback);
            //SubmitPlayerResultResultCallback = Callback<SubmitPlayerResultResultCallback_t>.Create(OnSubmitPlayerResultResultCallback);
            //EndGameResultCallback = Callback<EndGameResultCallback_t>.Create(OnEndGameResultCallback);
            JoinPartyCallback = Callback<JoinPartyCallback_t>.Create(OnJoinPartyCallback);
            CreateBeaconCallback = Callback<CreateBeaconCallback_t>.Create(OnCreateBeaconCallback);
            ReservationNotificationCallback = Callback<ReservationNotificationCallback_t>.Create(OnReservationNotificationCallback);
            ChangeNumOpenSlotsCallback = Callback<ChangeNumOpenSlotsCallback_t>.Create(OnChangeNumOpenSlotsCallback);
            AvailableBeaconLocationsUpdated = Callback<AvailableBeaconLocationsUpdated_t>.Create(OnAvailableBeaconLocationsUpdated);
            ActiveBeaconsUpdated = Callback<ActiveBeaconsUpdated_t>.Create(OnActiveBeaconsUpdated);
            OnLobbyEnterCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEnter_APIDispatch);
            OnLobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);
            OnLobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);

            // STEAM FRIENDS
            PersonaStateChange = Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);
            GameOverlayActivated = Callback<GameOverlayActivated_t>.Create(OnGameOverlayActivated);
            GameServerChangeRequested = Callback<GameServerChangeRequested_t>.Create(OnGameServerChangeRequested);
            GameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            AvatarImageLoaded = Callback<AvatarImageLoaded_t>.Create(OnAvatarImageLoaded);
            FriendRichPresenceUpdate = Callback<FriendRichPresenceUpdate_t>.Create(OnFriendRichPresenceUpdate);
            GameRichPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
            GameConnectedClanChatMsg = Callback<GameConnectedClanChatMsg_t>.Create(OnGameConnectedClanChatMsg);
            GameConnectedChatJoin = Callback<GameConnectedChatJoin_t>.Create(OnGameConnectedChatJoin);
            GameConnectedChatLeave = Callback<GameConnectedChatLeave_t>.Create(OnGameConnectedChatLeave);
            GameConnectedFriendChatMsg = Callback<GameConnectedFriendChatMsg_t>.Create(OnGameConnectedFriendChatMsg);
            UnreadChatMessagesChanged = Callback<UnreadChatMessagesChanged_t>.Create(OnUnreadChatMessagesChanged);
            OverlayBrowserProtocolNavigation = Callback<OverlayBrowserProtocolNavigation_t>.Create(OnOverlayBrowserProtocolNavigation);
            EquippedProfileItemsChanged = Callback<EquippedProfileItemsChanged_t>.Create(OnEquippedProfileItemsChanged);
            OnClanOfficerListResponseCallResult = CallResult<ClanOfficerListResponse_t>.Create(OnClanOfficerListResponse);
            OnDownloadClanActivityCountsResultCallResult = CallResult<DownloadClanActivityCountsResult_t>.Create(OnDownloadClanActivityCountsResult);
            OnJoinClanChatRoomCompletionResultCallResult = CallResult<JoinClanChatRoomCompletionResult_t>.Create(OnJoinClanChatRoomCompletionResult);
            OnFriendsGetFollowerCountCallResult = CallResult<FriendsGetFollowerCount_t>.Create(OnFriendsGetFollowerCount);
            OnFriendsIsFollowingCallResult = CallResult<FriendsIsFollowing_t>.Create(OnFriendsIsFollowing);
            OnFriendsEnumerateFollowingListCallResult = CallResult<FriendsEnumerateFollowingList_t>.Create(OnFriendsEnumerateFollowingList);
            OnEquippedProfileItemsCallResult = CallResult<EquippedProfileItems_t>.Create(OnEquippedProfileItems);

        }




        if (SteamApps.BIsLowViolence()) {

        }

        // SteamApps.GetCurrentGameLanguage() 



        SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024);
        if (pszCommandLine == null) {
            pszCommandLine = "";
        }
        steamtestlog("[SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024)] - " + pszCommandLine);

        LoadFriendList();

        UpdateRichPresence();
    }




#endregion // ALL_STEAM_CALLBACKS






    public static bool _DEV_ENABLE_STEAM_LOG => DebugMenuSettings._DEV_ENABLE_STEAM_LOG;
    internal static void steamtestlog(string what, bool forcelog = false) {
        if (!forcelog) {
            if (!_DEV_ENABLE_STEAM_LOG) return;
        }
        try {
            what = "STEAM: " + what;
            Plugin.Logger.LogInfo( Together.log.do_timestamp(what) );
            
            if (Con.con != null) {
                Con.con.LogToConsole($"<color=#00ffffff>{Together.log.prefix1} {what}</color>");
            }
        } catch (Exception e) {
            Plugin.Logger.LogError("steamtestlog: " + e.ToString());
        }
    }





}




























public class TransportSteamworks : NetTransportBase {

    public TransportSteamworks() {
        hostLobbyBucket = (byte)UnityEngine.Random.Range(0, KSteam.MAX_LOBBY_BUCKETS);

        // https://partner.steamgames.com/doc/api/steamnetworkingtypes#:~:text=k_ESteamNetworkingConfig_SendBufferSize
        const int defaultBufferSizeLimit = 524288;

        int sendBufferSizeBytes = defaultBufferSizeLimit * 2;

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(sendBufferSizeBytes, System.Runtime.InteropServices.GCHandleType.Pinned);

        SteamNetworkingUtils.SetConfigValue(
            Steamworks.ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
            Steamworks.ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
            IntPtr.Zero,
            Steamworks.ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            handle.AddrOfPinnedObject()
        );

    }

    public bool is_steamserver = false;

    public int DefaultTimeoutMS = 30000;

    public bool i_am_owner_of_this_lobby => KSteam.CURRENT_LOBBY.ownerID == KSteam.GetLocalUserSteamID();

    public bool IsInitialized {
        get {
            try {
                if (is_steamserver)
                    InteropHelp.TestIfAvailableGameServer();
                else
                    InteropHelp.TestIfAvailableClient();

                return true;
            } catch {
                return false;
            }
        }
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t pCallback) {
        try {


#if !RELEASE
            KSteam.steamtestlog($"{this.GetType().Name}.OnConnectionStatusChanged [" + SteamNetConnectionStatusChangedCallback_t.k_iCallback + " - SteamNetConnectionStatusChangedCallback] - " + pCallback.m_hConn + " -- " + pCallback.m_info + " -- " + pCallback.m_eOldState);
#endif


            if (pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) {
                //This happens when someone asked to connect to us, in the case of NetCode for GameObject this should only happen if we are a server/host
                //the current standard is to blindly accept ... NetCode for GO should really consider a validation model for connections
                if (log.verbose)
                    KSteam.steamtestlog($"OnConnectionStatusChanged: - connection request from " + pCallback.m_info.m_identityRemote.GetSteamID64());

                if (Net.IsServer) {

                    if (connectionMapping.Count > Multiplayer.PLAYER_COUNT_LIMIT) {
#if !RELEASE
                        if (log.verbose)
                            KSteam.steamtestlog($"OnConnectionStatusChanged: Rejecting connection {pCallback.m_info.m_identityRemote.GetSteamID64()}");
#endif

                        bool res2;
                        if (is_steamserver) {
                            res2 = SteamGameServerNetworkingSockets.CloseConnection(pCallback.m_hConn, 0, "MP max players", false);
                        } else {
                            res2 = SteamNetworkingSockets.CloseConnection(pCallback.m_hConn, 0, "MP max players", false);
                        }


                        return;
                    }


                    EResult res;
                    if (is_steamserver) {
                        res = SteamGameServerNetworkingSockets.AcceptConnection(pCallback.m_hConn);
                    } else {
                        res = SteamNetworkingSockets.AcceptConnection(pCallback.m_hConn);
                    }

                    if (res == EResult.k_EResultOK) {
                        if (Net.IsServer) {
                            if (log.verbose)
                                KSteam.steamtestlog($"OnConnectionStatusChanged: Accepting connection {pCallback.m_info.m_identityRemote.GetSteamID64()}");


                            var clientId = pCallback.m_info.m_identityRemote.GetSteamID64();

                            //This should be a new connection, record it
                            if (connectionMapping.ContainsKey(clientId) == false) {
                                var nCon = new SteamConnectionData(pCallback.m_info.m_identityRemote.GetSteamID());
                                nCon.connection = pCallback.m_hConn;
                                connectionMapping.Add(clientId, nCon);
                            }
                        } else {
                            if (log.verbose)
                                KSteam.steamtestlog($"OnConnectionStatusChanged: Connection {pCallback.m_info.m_identityRemote.GetSteamID64()} could not be accepted: this is not a server");
                        }
                    } else {
                        if (log.verbose)
                            KSteam.steamtestlog($"OnConnectionStatusChanged: Connection {pCallback.m_info.m_identityRemote.GetSteamID64()} could not be accepted: {res}");
                    }
                }
            } else if (pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) {
                if (log.verbose)
                    KSteam.steamtestlog($"OnConnectionStatusChanged: connection request to " + pCallback.m_info.m_identityRemote.GetSteamID64() + " was accepted!");

                var clientId = pCallback.m_info.m_identityRemote.GetSteamID64();


                //We should already have it but if not record the server connection that was just accepted
                if (connectionMapping.ContainsKey(clientId) == false) {
                    var nCon = new SteamConnectionData(pCallback.m_info.m_identityRemote.GetSteamID());
                    nCon.connection = pCallback.m_hConn;
                    connectionMapping.Add(clientId, nCon);
                } else {
                    //Should be redundent but update the conneciton handle anyway
                    connectionMapping[clientId].connection = pCallback.m_hConn;
                }

                //return NetworkEvent.Connect;
                return;
            } else if (
                pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                || pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
            ) {

                var client_steamId = pCallback.m_info.m_identityRemote.GetSteamID64();

                //The connection is no more
                if (log.verbose)
                    KSteam.steamtestlog($"OnConnectionStatusChanged: Connection closed for {BetterToStringFromSteamID(client_steamId)} state changed: {pCallback.m_info.m_eState}    lobby owner:{BetterToStringFromSteamID(KSteam.CURRENT_LOBBY.ownerID.m_SteamID)}");

                if (client_steamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID) {
                    Multiplayer.ShutdownNetwork();
                    Multiplayer.DoMultiplayerStatusMessageLog($"Steam: Connection closed: {pCallback.m_info.m_eState.ToString().TrimStart("k_ESteamNetworkingConnectionState_")}");
                } else {
                    // socket died on its own. not a lobby callback. log it before we stop sending.
                    ESteamNetConnectionEnd endReason = (ESteamNetConnectionEnd)pCallback.m_info.m_eEndReason;
                    string endDebug = OneLine(pCallback.m_info.m_szEndDebug);
                    string persona = SteamFriends.GetFriendPersonaName((CSteamID)client_steamId);
                    bool closeP2p = connectionMapping.ContainsKey(client_steamId);
                    ulong lobbyId = KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID;
                    int memberCount = SteamMatchmaking.GetNumLobbyMembers(KSteam.CURRENT_LOBBY.lobby_steamID);
                    log.l("[lobby-left] trigger=OnConnectionStatusChanged steam=" + client_steamId
                        + " name=\"" + OneLine(persona) + "\""
                        + " state=" + pCallback.m_info.m_eState + " (" + ((int)pCallback.m_info.m_eState) + ")"
                        + " old=" + pCallback.m_eOldState + " (" + ((int)pCallback.m_eOldState) + ")"
                        + " end=" + endReason + " (" + pCallback.m_info.m_eEndReason + ")"
                        + " debug=\"" + endDebug + "\""
                        + " lobby=" + lobbyId
                        + " members=" + memberCount
                        + " closeP2P=" + (closeP2p ? 1 : 0)
                        + " method=TransportSteamworks.RemoveSteamUser");
                    //Remove the mapped connection info
                    RemoveSteamUser(client_steamId);
                }

                //return NetworkEvent.Disconnect;
                return;
            } else {
                var client_steamId = pCallback.m_info.m_identityRemote.GetSteamID64();
                if (log.verbose)
                    KSteam.steamtestlog($"OnConnectionStatusChanged: Connection closed for {BetterToStringFromSteamID(client_steamId)} state changed: {pCallback.m_info.m_eState}    lobby owner:{BetterToStringFromSteamID(KSteam.CURRENT_LOBBY.ownerID.m_SteamID)}");

            }

        } catch (Exception e) {
            Plugin.Logger.LogError("OnConnectionStatusChanged: " + e.ToString());
        }
    }

    // ts is stolen from unity NGO steamworks.net transport
    internal class SteamConnectionData {
        internal SteamConnectionData(CSteamID steamId) {
            id = steamId;
        }

        internal SteamNetConnectionRealTimeStatus_t connectionStatus = new();
        internal CSteamID id;
        internal HSteamNetConnection connection;



        public override string ToString() {
            return $"SteamConnectionData(ID:{id}, connection:{connection})";
        }
    }

    private Callback<SteamNetConnectionStatusChangedCallback_t> onConnectionChange = null;
    public SteamNetworkingConfigValue_t[] options = new SteamNetworkingConfigValue_t[0];
    public HSteamListenSocket listenSocket { get; private set; }
    internal SteamConnectionData serverUser { get; private set; }
    internal readonly Dictionary<ulong, SteamConnectionData> connectionMapping = new Dictionary<ulong, SteamConnectionData>();
    // One buffer for the life of the transport. A client used to allocate 65535 slots every frame.
    const int ClientPollCap = 256;
    const int HostPollCap = 255;
    IntPtr[] _pollPtrs;
    readonly List<SteamConnectionData> _pollConnections = new List<SteamConnectionData>(4);

    public byte hostLobbyBucket;

    //CALLED ONLY FROM  OnWantToHostLobby !!!!
    internal bool CreateServerSocket() {
        if (listenSocket != HSteamListenSocket.Invalid) {
            log.error($"{this.GetType().Name}.CreateServerSocket : Socket already exists !!!!!!!!! {listenSocket}");
            return false;
        }
        
        if (onConnectionChange == null)
            if (is_steamserver)
                onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
            else
                onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);


        if (options == null)
            options = new SteamNetworkingConfigValue_t[0];

        if (is_steamserver)
            listenSocket = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
        else
            listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);


        if (log.verbose)
            log.l(this.GetType().Name + "CreateServerSocket!");

        return true;
    }
    internal bool CreateClientSocket() {
        if (listenSocket != HSteamListenSocket.Invalid) {
            log.error($"{this.GetType().Name}.CreateClientSocket : Socket already exists! {listenSocket}");
            return false;
        }

        if (onConnectionChange == null)
            if (is_steamserver)
                onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
            else
                onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);

        var ConnectToSteamID = KSteam.CURRENT_LOBBY.ownerID;

        serverUser = new SteamConnectionData( (CSteamID)(ConnectToSteamID));

        try {
            if (is_steamserver)
                SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
            else
                SteamNetworkingUtils.InitRelayNetworkAccess();

            SteamNetworkingIdentity smi = new SteamNetworkingIdentity();
            smi.SetSteamID(serverUser.id);
            if (is_steamserver)
                serverUser.connection = SteamGameServerNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
            else
                serverUser.connection = SteamNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);

            connectionMapping.Add((ulong)ConnectToSteamID, serverUser);

            return true;
        } catch (Exception ex) {
            log.error($"{this.GetType().Name}.CreateClientSocket : Client could not be started !! {ex.Message}");
            return false;
        }
    }


    internal bool CreateLobby(ELobbyType lobbyType) {

#if PLAYTEST 
        lobbyType = ELobbyType.k_ELobbyTypePublic; 
#endif  

        if (Multiplayer.PLAYER_COUNT_LIMIT > Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT) {
            Multiplayer.PLAYER_COUNT_LIMIT = Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT;
        }

        KSteam.CURRENT_LOBBY.locked = false;
        SteamAPICall_t handle = SteamMatchmaking.CreateLobby(lobbyType, Multiplayer.PLAYER_COUNT_LIMIT);
        KSteam.OnLobbyCreatedCallResult.Set(handle);

        Multiplayer.DoMultiplayerStatusMessageLog("STEAM: Creating Lobby...");

        if (log.verbose)  log.l(this.GetType().Name + ".CreateLobby");

        return true;
    }


    public static bool OnWantToHostLobby(Net.NetType type, ELobbyType lobbyType) {
        Net.ShutdownReset();

        if (!KSteam.Loaded) {
            log.error($"Steam is not loaded!");
            return false;
        }

        if (type == Net.NetType.Client) {
            log.error($"what  {new StackTrace()}");
            return false;
        }

        if ( !Net.VerifyMyServerName()) {
            Multiplayer.DoMultiplayerStatusMessageLog("Invalid Server name.");
            return false;
        }

        try {

            UIMainMenu._STEAM_CHOSEN_LOBBYTYPE = KSteam.LobbyTypeToNum(lobbyType);
            Net.TransportCreated(type);
            var t = new TransportSteamworks();
            Net.Transport = t;

            if (!t.IsInitialized) {
                log.error($"STEAM: IsInitialized is false!");
                Net.ShutdownReset();
                return false;
            }

            if (t.CreateLobby(lobbyType)) {
                // it waits for steam answer
                return true;
            } else {
                // never errors
            }

        } catch (Exception e) {
            log.error("TransportSteamworks.OnWantToHostLobby: " + e.ToString());
            Net.ShutdownReset();
        }

        return false;
    }

    public static bool OnWantToJoinLobby(ulong lobby_steamID) {
        Net.ShutdownReset();

        KSteam.CURRENT_LOBBY.locked = true;

        if (!KSteam.Loaded) {
            log.error($"Steam is not loaded!");
            return false;
        }
        var type = Net.NetType.Client;

        try {

            Net.TransportCreated(type);
            var t = new TransportSteamworks();
            Net.Transport = t;

            if (!t.IsInitialized) {
                log.error($"STEAM: IsInitialized is false!");
                Net.ShutdownReset();
                return false;
            }

            KSteam.OnLobbyEnterCallResult.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby_steamID)));

            return true;

        } catch (Exception e) {
            log.error("TransportSteamworks.OnWantToConnect: " + e.ToString());
            Net.ShutdownReset();
        }

        return false;
    }

    

    
    public void Update() {
        if (!KSteam.Loaded && KSteam.CURRENT_LOBBY.lobby_steamID != CSteamID.Nil) return;

        // callbacks are run by the singleton

        UpdateTheIDDicts();
        _Update_PollMessages();

        one_second_timer += Time.unscaledDeltaTime;
        if (one_second_timer > 1) {
            one_second_timer = 0;

            UpdateOneSecond();
        }
    }
    float one_second_timer = -1;

    public SteamNetConnectionRealTimeStatus_t client_status = new();
    void UpdateOneSecond() {
        CheckLobbyMembersList();

        SteamNetConnectionRealTimeStatus_t status = new();
        SteamNetConnectionRealTimeLaneStatus_t laneStatus = new();
        if (Net.IsServer) {

            foreach (var connectionData in connectionMapping.Values) {
                var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connectionData.connection, ref status, 0, ref laneStatus );
                if (result == EResult.k_EResultOK) {
                    int ping_ms = status.m_nPing;

                    if (SteamIDToNetPlayerDict.TryGetValue(connectionData.id.m_SteamID, out var plr)) {
                        plr.ping = ((float)ping_ms) * 0.001f;
                    }

                    connectionData.connectionStatus = status;
                }
            }
                
        } else {
            if (serverUser != null && serverUser.connection != HSteamNetConnection.Invalid) {
                var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(serverUser.connection, ref status, 0, ref laneStatus);
                if (result == EResult.k_EResultOK) {
                    int ping_ms = status.m_nPing;

                    var ping_s = ((float)ping_ms) * 0.001f;

                    ClientMain.LOCAL_PING = ping_ms;
                    ClientMain.MY_CONNECTION_QUALITY = (byte)(Mathf.Clamp01(status.m_flConnectionQualityLocal) * 100);
                    client_status = status;
                    return;
                }
            }
            client_status = new();
        }

    }



    void CheckLobbyMembersList() {
        if (!KSteam.IS_IN_LOBBY) {
            return;
        }

        var DefaultTimeoutInSeconds = (float)DefaultTimeoutMS * 0.001f;

        var curtime = Time.realtimeSinceStartupAsDouble;
        if (i_am_owner_of_this_lobby) {
            foreach ( var item in KSteam.CURRENT_LOBBY.members) {
                if (KSteam.GetLocalUserSteamID() == item.Key) {
                    item.Value.server_last_data_receive_time = curtime;
                    continue;
                }

                var lastdatareceived_time = curtime - item.Value.server_last_data_receive_time;

                if (lastdatareceived_time > DefaultTimeoutInSeconds) {
                    // local 1s poll, not a steam callback. DefaultTimeoutMS since their last packet.
                    ulong idleSteam = item.Key.m_SteamID;
                    string idleName = SteamFriends.GetFriendPersonaName(item.Key);
                    bool closeP2p = connectionMapping.ContainsKey(idleSteam);
                    int memberCount = SteamMatchmaking.GetNumLobbyMembers(KSteam.CURRENT_LOBBY.lobby_steamID);
                    string connSnap = DescribeLobbyLeftConnection(idleSteam);
                    log.l("[lobby-left] trigger=CheckLobbyMembersList timer=DefaultTimeoutMS"
                        + " limitMs=" + DefaultTimeoutMS
                        + " idleSec=" + lastdatareceived_time.ToString("0.0")
                        + " steam=" + idleSteam
                        + " name=\"" + OneLine(idleName) + "\""
                        + " lobby=" + KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID
                        + " members=" + memberCount
                        + " closeP2P=" + (closeP2p ? 1 : 0)
                        + " method=TransportSteamworks.RemoveSteamUser alsoKickChat=1 "
                        + connSnap);
                    item.Value.server_last_data_receive_time = curtime;

#if RELEASE
                    log.l($"Lobby member {BetterToStringFromSteamID(item.Key)} timed out!");
#else
                    log.l($"Lobby member {BetterToStringFromSteamID(item.Key)} timed out!  time:{lastdatareceived_time}");
#endif
                    KickMember(item.Key,"Timed out!");
                    RemoveSteamUser(item.Key.m_SteamID);

                }
            }
        } else {
            foreach (var item in KSteam.CURRENT_LOBBY.members) {
                item.Value.server_last_data_receive_time = curtime;
            }
        }

    }
    void UpdateTheIDDicts() {
        SteamIDToNetPlayerDict.Clear();
        SteamIDToClientIDDict.Clear();

        foreach (var item in ScavPlayer.ClientIdToPlayerDict) {
            SteamIDToNetPlayerDict[item.Value.SteamId] = item.Value;
            SteamIDToClientIDDict[item.Value.SteamId] = item.Value.playerId;
            item.Value.IsHost = item.Value.SteamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID;
        }
    }
    void _Update_PollMessages() {
        // Receive only what this frame will dispatch. Steam leaves the rest queued.
        int cap = Net.IsClient ? ClientPollCap : HostPollCap;
        if (_pollPtrs == null || _pollPtrs.Length < cap)
            _pollPtrs = new IntPtr[cap];

        _pollConnections.Clear();
        foreach (var connectionData in connectionMapping.Values)
            _pollConnections.Add(connectionData);

        int remaining = cap;
        for (int c = 0; c < _pollConnections.Count; c++) {
            if (Net.IsClient && remaining <= 0)
                break;

            var connectionData = _pollConnections[c];
            int take = Net.IsClient ? remaining : cap;
            // If the connection handle is invalid, -1 is returned. If no data is available, 0 is returned.
            int messageCount = is_steamserver
                ? SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, _pollPtrs, take)
                : SteamNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, _pollPtrs, take);
            if (messageCount <= 0)
                continue;
            if (messageCount > take)
                messageCount = take;

            for (int i = 0; i < messageCount; i++) {
                SteamNetworkingMessage_t data = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_pollPtrs[i]);
                var buffer = new byte[data.m_cbSize];
                Marshal.Copy(data.m_pData, buffer, 0, data.m_cbSize);
                SteamNetworkingMessage_t.Release(_pollPtrs[i]);

                try {
                    _K_OnReceiveNetworkingMessage(connectionData, buffer);
                } catch (Exception e) {
                    Plugin.Logger.LogError($"{this.GetType().Name}._K_OnReceiveNetworkingMessage\n ERROR:\n{e.ToString()}\nCURRENT LOBBY:{(KSteam.CURRENT_LOBBY == null ? "NULL_LOBBY" : KSteam.CURRENT_LOBBY.DebugReadableDump())}\nconnectionData: {connectionData}");
                }
            }

            if (Net.IsClient)
                remaining -= messageCount;
        }
    }

    void _Server_NewLobbyMember(NetDataReader reader, ulong steamID) {
        bool privileged_user = false;
        {
            if (KnownPersons.IsSteamUserPrivileged(steamID)) {
                privileged_user = true;
            }
        }

        var validated = Net.ValidateNewClientHandshake(reader, false, privileged_user, out var userInfo, out string reject_reason);
        if (validated) {
            if (KnownPersons.IsSteamUserPrivileged(steamID)) { // privileged users 
            } else if (BanList.IsBanned(steamID)) {
                validated = false;
                reject_reason = "Banned";
            }
        }
        if (!validated) {
#if TESTING
            steamtestlog($"Rejected player {BetterToStringFromSteamID(steamID)} for {reject_reason}\ndata:\n{reader.RawData.ToHexString()}", true);
#else
            steamtestlog($"Rejected player {BetterToStringFromSteamID(steamID)} for {reject_reason}");
#endif

            KickMember((CSteamID)steamID, reject_reason);

            Util.DelayCallLambda(0.4f, () => {
                RemoveSteamUser(steamID);
            });
            return;
        }

        var playerEntity = BasePlayer.ServerCreatePlayer(steamID.ToString(), steamID, userInfo.color, false);

        try {
            SteamIDToNetPlayerDict[steamID] = playerEntity.player;
            SteamIDToClientIDDict[steamID] = playerEntity.player.playerId;
        } catch (Exception e) {
            log.error($"Steam: Player creation: {e}");
        }

        log.l($"{this.GetType().Name}: Accepted connection for: {playerEntity.player}  color:{userInfo.color}");
    }

    void _K_OnReceiveNetworkingMessage(SteamConnectionData connection, byte[] data) {
        var steamID = connection.id.m_SteamID;

        var reader = new NetDataReader(data);

        if (i_am_owner_of_this_lobby) {

            if (!KSteam.CURRENT_LOBBY.members.TryGetValue(connection.id,out var _member)) {
                _member = new(connection.id);
                KSteam.CURRENT_LOBBY.members[connection.id] = _member;
            }
            _member.server_last_data_receive_time = Time.realtimeSinceStartupAsDouble;

            if (!SteamIDToClientIDDict.TryGet(steamID, out var clientID)) { // new player !
                _Server_NewLobbyMember(reader, steamID);
                return;
            }
            Net.InvokeServerMessage(clientID, reader);

        } else {
            if (connection.id != KSteam.CURRENT_LOBBY.ownerID) {
#if !RELEASE 
                log.error($"RECEIVING MESSAGES NOT FROM A LOBBY HOST !!!!!! connection.id:{connection.id}");
#endif
                return;
            }
            Net.InvokeClientMessage(Net.SERVER_CLIENTID, reader);
        }

    }


    public readonly Dictionary<ulong, ScavPlayer> SteamIDToNetPlayerDict = new ();
    public readonly TwoWayDictionary<ulong, knetid> SteamIDToClientIDDict = new ();

    
    internal static string OneLine(string value) {
        if (value is null) {
            return "";
        }
        return value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
    }

    // socket state at the moment steam says they left, before we close it
    internal string DescribeLobbyLeftConnection(ulong steamID) {
        if (!connectionMapping.TryGetValue(steamID, out SteamConnectionData conndata)) {
            return "conn=none";
        }

        HSteamNetConnection conn = conndata.connection;
        string text = "conn=" + conn.m_HSteamNetConnection;

        try {
            SteamNetConnectionInfo_t info;
            bool gotInfo;
            if (is_steamserver) {
                gotInfo = SteamGameServerNetworkingSockets.GetConnectionInfo(conn, out info);
            } else {
                gotInfo = SteamNetworkingSockets.GetConnectionInfo(conn, out info);
            }

            if (gotInfo) {
                ESteamNetConnectionEnd endReason = (ESteamNetConnectionEnd)info.m_eEndReason;
                string debug = OneLine(info.m_szEndDebug);
                text += " state=" + info.m_eState + " (" + ((int)info.m_eState) + ")"
                    + " end=" + endReason + " (" + info.m_eEndReason + ")"
                    + " debug=\"" + debug + "\"";
            } else {
                text += " info=false";
            }
        } catch (Exception e) {
            text += " infoErr=" + e.GetType().Name;
        }

        try {
            SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = new SteamNetConnectionRealTimeLaneStatus_t();
            EResult result;
            if (is_steamserver) {
                result = SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);
            } else {
                result = SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);
            }

            if (result == EResult.k_EResultOK) {
                text += " rtState=" + status.m_eState
                    + " ping=" + status.m_nPing
                    + " pendingReliable=" + status.m_cbPendingReliable
                    + " pendingUnreliable=" + status.m_cbPendingUnreliable;
            } else {
                text += " rt=" + result;
            }
        } catch (Exception e) {
            text += " rtErr=" + e.GetType().Name;
        }

        return text;
    }

    internal void RemoveSteamUser(ulong steamID) {
#if TESTING
        KSteam.steamtestlog($"RemoveSteamUser:  {BetterToStringFromSteamID(steamID)} \n{new StackTrace()}");
#endif
        if (steamID == KSteam.GetLocalUserSteamID().m_SteamID) {
            return; // cant delete myself
        }


        if (SteamIDToNetPlayerDict.TryGetValue(steamID, out var player) && player != null) {
            try {
                GameObject.DestroyImmediate(player.gameObject);
            } catch (Exception e) {
                Plugin.Logger.LogError(e.ToString());
            }
        } 

        SteamIDToNetPlayerDict.Remove(steamID);
        SteamIDToClientIDDict.Remove(steamID);


        if (connectionMapping.TryGetValue(steamID, out var conndata)) {

            bool res2;
            if (is_steamserver) {
                res2 = SteamGameServerNetworkingSockets.CloseConnection(conndata.connection, 0, "disconnected", false);
            } else {
                res2 = SteamNetworkingSockets.CloseConnection(conndata.connection, 0, "disconnected", false);
            }


            connectionMapping.Remove(steamID);
#if TESTING
            log.warn($"OnLobbyMemberListChanged: removing {steamID} from connectionMapping ");
#endif
        }

    }


    public void Kick(knetid clientid, in string msg) {
        if (!Net.IsServer) {
            log.error($"Tried to kick {DebugHelp.GetPlayerFullDebugString(clientid)} as a non-host????");
            return;
        }

        if (SteamIDToClientIDDict.TryGet(clientid, out var steamid)) {
            KickMember((CSteamID)steamid, msg);
        }
        RemoveSteamUser(steamid);
    }

    // stupid ass VALVE, STPUID GABEN << DOESNT ALLOW TO KICK LOBBY MEMBERS,
    // //AND CHATGPT SUGGESTED THIS BITCHASS CLIEANT-AUTHORITATIBVE BULLSHTI,  AND APPARENTLY EVERYONE DOES IT THIS WAY AND NOBODY SPEAKS UP?? neither will i  lol
    internal void KickMember(CSteamID target, string reason = null) {
        if (SteamMatchmaking.GetLobbyOwner(LobbyId) != SteamUser.GetSteamID()) {
            log.error($"Tried to kick {BetterToStringFromSteamID(target)} as a non-host????");
            return;
        }

        if (reason == null) {
            if (KSteam.CURRENT_LOBBY.members.TryGetValue(target, out var member)) {
                reason = member.disconnectreason;
            }
        }

        string msg = "KICK:" + target.m_SteamID.ToString();
        if (!String.IsNullOrEmpty(reason)) {
            msg += ":" + reason;
        }
        byte[] data = System.Text.Encoding.UTF8.GetBytes(msg);

        SteamMatchmaking.SendLobbyChatMsg(LobbyId, data, data.Length);
    }

    public void Shutdown() {
        Shutdown(true);
    }
    internal void CloseListenSocket() {
        if (listenSocket != HSteamListenSocket.Invalid) {
            if (is_steamserver)
                SteamGameServerNetworkingSockets.CloseListenSocket(listenSocket);
            else
                SteamNetworkingSockets.CloseListenSocket(listenSocket);
            listenSocket = HSteamListenSocket.Invalid;
        }
    }
    internal void Shutdown(bool reset_net) {

        CloseListenSocket();

        CloseP2PSessions();

        if (reset_net) {
            if (CURRENT_LOBBY.lobby_steamID != CSteamID.Nil || KSteam.IS_IN_LOBBY) {
                KSteam.LeaveLobbyAndResetNet(false);
            }
        }

    }


    private void CloseConnectionOnServerUser() {
        if (serverUser != null) {
            if (connectionMapping.ContainsKey(serverUser.id.m_SteamID))
                connectionMapping.Remove(serverUser.id.m_SteamID);
            if (is_steamserver)
                SteamGameServerNetworkingSockets.CloseConnection(serverUser.connection, 0, "Disconnected", false);
            else
                SteamNetworkingSockets.CloseConnection(serverUser.connection, 0, "Disconnected", false);
        }
        serverUser = null;
    }
    private void CloseP2PSessions() {
        CloseConnectionOnServerUser();

        foreach (SteamConnectionData user in connectionMapping.Values) {
            if (is_steamserver)
                SteamGameServerNetworkingSockets.CloseConnection(user.connection, 0, "Disconnected", false);
            else
                SteamNetworkingSockets.CloseConnection(user.connection, 0, "Disconnected", false);
        }

        connectionMapping.Clear();
        if (log.verbose)
            KSteam.steamtestlog($"{this.GetType().Name}.CloseP2PSessions - has Closed P2P Sessions With all Users");


        if (onConnectionChange != null) {
            onConnectionChange.Dispose();
            onConnectionChange = null;
        }
    }



    bool NetTransportBase.IsConnecting() {
        if (Net.IsClient) {
            if (ScavPlayer.LocalPlayer == null) {
                return true;
            }
        }
        return !KSteam.IS_IN_LOBBY;
    }


    public int ConvertDeliveryMethodToSteamNetworkingSendFlag(in DeliveryMethod delivery) {
        
        // maybe this will improve latency 
        int sendFlag = Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
        switch (delivery) {
            case DeliveryMethod.ReliableUnordered:
            case DeliveryMethod.ReliableSequenced:
            case DeliveryMethod.ReliableOrdered:
            sendFlag = Steamworks.Constants.k_nSteamNetworkingSend_ReliableNoNagle;
            break;
            case DeliveryMethod.Sequenced: // its unused 
            sendFlag = Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
            break;
        }

        return sendFlag;
    }



    public void SendThroughSteamSocket(ulong steamID, ReadOnlySpan<byte> data, DeliveryMethod delivery) => SendThroughSteamSocket(steamID, data, ConvertDeliveryMethodToSteamNetworkingSendFlag(delivery));

    // stolen from Unity NGO  Steamworks.NET transport
    public void SendThroughSteamSocket(ulong steamID, ReadOnlySpan<byte> data, int sendFlag) {
        if (!KSteam.CURRENT_LOBBY.lobby_steamID.IsValid()) {
#if !RELEASE || PRERELASE
            Plugin.Logger.LogWarning("SendThroughSteamSocket: Failed cuz lobby is non-existant (program is assuming lobby will get recreated soon)");
#endif
            return;
        }

        if (steamID == 0) {
            if (serverUser == null) return;
            steamID = serverUser.id.m_SteamID;
        }

        //Check if we have a mapped user for this ID
        if (connectionMapping.TryGetValue(steamID,out var mappedconnection)) {
            //Grab a pointer to the user's connection
            
            SendThroughSteamSocket(mappedconnection.connection, data, sendFlag);
        } else {
            //No client found so report that
#if RELEASE && !PLAYTEST
            if (log.verbose)
                Plugin.Logger.LogError($"{this.GetType().Name}.Send: Trying to send to an unknown connection  {BetterToStringFromSteamID(steamID)}");
#else
            if (log.verbose) // DO NOT CHANGE THESE TO log.error !!! it causes stack overflow!!!
                Plugin.Logger.LogError($"{this.GetType().Name}.Send: Trying to send to an unknown connection  {BetterToStringFromSteamID(steamID)}\n{new StackTrace()}");
#endif
        }
    }
    double lastExceedWarn = 0;
    public void SendThroughSteamSocket(in HSteamNetConnection connection, ReadOnlySpan<byte> data, int sendFlag) {

        byte[] data_copied = data.ToArray();

        //Create an unmanaged array and get a pointer to it
        GCHandle pinnedArray = GCHandle.Alloc(data_copied, GCHandleType.Pinned);
        IntPtr pData = pinnedArray.AddrOfPinnedObject();

        //Send to the target
        EResult response;
        if (is_steamserver)
            response = SteamGameServerNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data_copied.Length, sendFlag, out long _);
        else
            response = SteamNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data_copied.Length, sendFlag, out long _);

        //Free the unmanaged array
        pinnedArray.Free();


        if (response != EResult.k_EResultOK) {

            if (response == EResult.k_EResultNoConnection && !log.verbose) {
                return;
            }


            var gfgfddfgdfg = connection.m_HSteamNetConnection;
            var foundsteamid = connectionMapping.Values.FirstOrDefault(x=> x.connection.m_HSteamNetConnection == (gfgfddfgdfg));

            string msgid_as_str = "";
#if !(RELEASE && !PLAYTEST)
            try {
                var read = new NetDataReader(data_copied);
                var msgid = read.GetByte();
                msgid_as_str = Net.ByteMsgIdToName(msgid) + " :";
            } catch (Exception e) { }
#endif

            if (foundsteamid != null) {
                if (response == EResult.k_EResultLimitExceeded) {
                    if (Time.realtimeSinceStartupAsDouble - lastExceedWarn > 30) {
                        lastExceedWarn = Time.realtimeSinceStartupAsDouble;
                        Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response} {BetterToStringFromSteamID(foundsteamid.id)}");
                    }
                } else {
#if TESTING
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response} {BetterToStringFromSteamID(foundsteamid.id)} {new StackTrace()}");
#else
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response} {BetterToStringFromSteamID(foundsteamid.id)}");
#endif
                }
            } else {
                Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response}  UNKNOWN CONNECTION {connection}");
            }

        }

    }



    public static string BetterToStringFromSteamID(CSteamID steamid) => BetterToStringFromSteamID(steamid.m_SteamID);
    public static string BetterToStringFromSteamID(ulong steamid) {
        var plrid = "";
        if (Net.TryGetSteamTransport(out var ttttt) && ttttt.SteamIDToClientIDDict.TryGet(steamid, out var plrdknetid)) {
            plrid = $", plrid:{plrdknetid}";
        }
        var tagstr = "";
        {
            var tags = new List<string>();
            if (KSteam.IS_IN_LOBBY && steamid == KSteam.CURRENT_LOBBY.ownerID.m_SteamID) {
                tags.Add("OWNER");
            }
            if (steamid == KSteam.GetLocalUserSteamID().m_SteamID) {
                tags.Add("LOCAL");
            }
            if (tags.Count > 0) {
                tagstr = String.Join(", ", tags) + ", ";
            }
        }

        return $"MEMBER({tagstr}steamID:{steamid}, name:{SteamFriends.GetFriendPersonaName((CSteamID)steamid)}{plrid})";
    }






    public void Server_SendTo(in int steam_sendFlags, in NetDataWriter writer, in knetid clientid) {
        if (SteamIDToClientIDDict.TryGet(clientid, out var user_steamid)) {
            SendThroughSteamSocket(user_steamid, writer.AsReadOnlySpan(), steam_sendFlags);
        } else {
            if (log.verbose)
                Plugin.Logger.LogError($"SERVER: TransportSteamworks.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
        }
    }
    public void Server_SendToClients(in int steam_sendFlags, in NetDataWriter writer, in IReadOnlyList<knetid> clientIds) {
        if (Net.IsClient) {
            throw new Exception("CLIENT CANT DO THAT!!!!");
        }
        foreach (var cID in clientIds) {
            Server_SendTo(steam_sendFlags, writer, cID);
        }
    }



    public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid) {
        if (SteamIDToClientIDDict.TryGet(clientid, out var user_steamid) ) {
            SendThroughSteamSocket(user_steamid, data, delivery);
        } else {
            if (log.verbose)
                Plugin.Logger.LogError($"SERVER: TransportSteamworks.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
        }
    }
    public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in IReadOnlyList<knetid> clientIds) {
        if (Net.IsClient) {
            throw new Exception("CLIENT CANT DO THAT!!!!");
        }
        foreach (var cID in clientIds) {
            Server_SendTo(delivery, data, cID);
        }
    }
    public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data) {
        SendThroughSteamSocket(KSteam.CURRENT_LOBBY.ownerID.m_SteamID, data, delivery);
    }





    public void FlushNagle() {
        SteamNetworkingSockets.FlushMessagesOnConnection(serverUser.connection);
        foreach (var item in connectionMapping) {
            SteamNetworkingSockets.FlushMessagesOnConnection(item.Value.connection);
        }
    }
    public void FlushNagle(knetid plrId) {
        if (SteamIDToClientIDDict.TryGet(plrId, out var steamId) && connectionMapping.TryGetValue(steamId, out var connection)) {
            SteamNetworkingSockets.FlushMessagesOnConnection(connection.connection);
        }
    }


}



















