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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.VisualScripting;
using UnityEngine;
using static Together.KSteam;

namespace Together {


    // !!! WARNING !!!  UTTER SCHIZO CODE
    //   cOPYPASTED BULLSHIT FROM THE DOCS MANGLED TOGETHER
    //   aND GENERAL CODE VOMIT






    // not static !! and is not ScavSingleton,  cuz instance is created after CheckSteam runs succesfully
    public class KSteam : MonoBehaviour {
        protected static ManualLogSource log => Plugin.log;

        public static bool Loaded { get; private set; }

        public static KSteam singleton = null;


        public static bool IS_IN_LOBBY { get; private set; }
        public static bool IS_IN_STEAMOVERLAY { get; private set; }

        public static readonly string Filename_steam_appid_txt = "steam_appid.txt";

        public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO = 4576510;
        public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST = 4584420;
        public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_FULL = 4576490;



        public static bool APP_IS_PIRATE { get; private set; }//    480
        public static bool APP_IS_DEMO { get; private set; }//      4576510
        public static bool APP_IS_PLAYTEST { get; private set; }//  4584420
        public static bool APP_IS_FULL { get; private set; }//      4576490



        public static uint STEAM_APPID { get; private set; }




        public class LobbyMember {
            public CSteamID steamID;
            public double server_last_data_receive_time;
            public string disconnectreason = null;
            public Dictionary<string, string> metadata = new();
            public LobbyMember(ulong steamID) => new LobbyMember((CSteamID)steamID);
            public LobbyMember(CSteamID steamID) {
                server_last_data_receive_time = Time.realtimeSinceStartupAsDouble;
            }
        }
        public class Lobby {
            public bool locked;
            public CSteamID lobby_steamID;
            public CSteamID ownerID;
            public Dictionary<CSteamID, LobbyMember> members = new(); // THIS IS EMPTY IF USER IS NOT IN THE LOBBY
            public int memberlimit;
            // steam lobby keys are case-insensitive and often come back lowercased (coop_mod_version).
            // case-sensitive lookup was dropping every lobby as missing COOP_MOD_VERSION.
            public Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);

            public string DebugReadableDump() {
                if (this == null) {
                    return "NULL_LOBBY";
                }

                string s = $"===\nlobby_steamID: {lobby_steamID}\nownerID: {ownerID}\nlocked: {locked}\nmemberlimit: {memberlimit}\nmembers: {String.Join(", ", members.Keys)}\nmetadata:\n{String.Join("\n", metadata)}\n===";
                return s;
            }
        }

        public static List<Lobby> LOBBIES = new();
        public static Lobby CURRENT_LOBBY = new Lobby();

        // matchList can return lobby ids before metadata is local - RequestLobbyData fills them
        static readonly HashSet<ulong> _pendingBrowserLobbyData = new HashSet<ulong>();

        internal static Dictionary<ulong, NetPublicServerInfo> CurServerList_ButOnlySteamLobbies => UIServerBrowser.CurServerList_ButOnlySteamLobbies;

        public static void ClearPendingBrowserLobbyData() {
            _pendingBrowserLobbyData.Clear();
        }

        internal static void UpdateLobbyInfo(CSteamID steamIDLobby, ref Lobby outLobby) {
            outLobby.lobby_steamID = steamIDLobby;
            outLobby.ownerID = SteamMatchmaking.GetLobbyOwner(steamIDLobby);
            outLobby.memberlimit = SteamMatchmaking.GetLobbyMemberLimit(steamIDLobby);

            int nDataCount = SteamMatchmaking.GetLobbyDataCount(steamIDLobby);
            outLobby.metadata.Clear();
            for (int i = 0; i < nDataCount; ++i) {
                bool lobbyDataRet = SteamMatchmaking.GetLobbyDataByIndex(steamIDLobby, i, out var m_Key, Steamworks.Constants.k_nMaxLobbyKeyLength, out var m_Value, Steamworks.Constants.k_cubChatMetadataMax);

                // trimStart(string) treats the string as a CHAR SET and mangles COOP_MOD_* into D_* etc.
                // only strip the old Krok prefix when it is an actual prefix - keeps demo (old keys) out of full (COOP_MOD_).
                if (m_Key.StartsWith(_OLDLOBBYDATAKEYPREFIX, StringComparison.Ordinal)) {
                    m_Key = m_Key.Substring(_OLDLOBBYDATAKEYPREFIX.Length);
                }

                outLobby.metadata[m_Key] = m_Value;
                if (!lobbyDataRet) {
                    Together.log.error("SteamMatchmaking.GetLobbyDataByIndex returned false.");
                    continue;
                }
            }

            // oNLY sync members for the lobby we're actually in.
            // browsing MatchList used to wipe CURRENT_LOBBY.members (GetNumLobbyMembers==0 when not inside).
            bool updatingCurrent = IS_IN_LOBBY && CURRENT_LOBBY.lobby_steamID == steamIDLobby;
            if (updatingCurrent || object.ReferenceEquals(outLobby, CURRENT_LOBBY)) {
                List<CSteamID> new_member_list = new List<CSteamID>();
                var membercount = SteamMatchmaking.GetNumLobbyMembers(steamIDLobby); // returns 0 if local user is not in that lobby
                for (int i = 0; i < membercount; ++i) {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex(steamIDLobby, i);
                    new_member_list.Add(member);
                }

                List<CSteamID> newList = new_member_list;
                List<CSteamID> oldList = new List<CSteamID>(CURRENT_LOBBY.members.Select(x => (CSteamID)x.Key));

                IEnumerable<CSteamID> added = newList.Except(oldList);
                IEnumerable<CSteamID> removed = oldList.Except(newList);

                foreach (CSteamID sID in added) {
                    CURRENT_LOBBY.members.Add(sID, new LobbyMember(sID));
                }
                foreach (CSteamID sID in removed) {
                    CURRENT_LOBBY.members.Remove(sID);
                }
            }
        }

        static string _lastLobbyDropReason;

        static bool TryAddLobbyToBrowser(Lobby slobby) {
            _lastLobbyDropReason = null;
            NetPublicServerInfo l = Client_LobbyBrowser_ReadLobbyIntoServerInfos(slobby);
            ulong id = slobby != null ? slobby.lobby_steamID.m_SteamID : 0;
            int keyCount = slobby?.metadata != null ? slobby.metadata.Count : 0;
            if (l != null) {
                Together.log.warn($"[LobbyBrowser] ACCEPT id={id} name=\"{l.name}\" ver=\"{l.version}\" plr={l.plr_count}/{l.plr_max} joinable={l.IsJoinable()} keys={keyCount}");
                UIServerBrowser.AddServerToTheList(l);
                return true;
            }
            Together.log.warn($"[LobbyBrowser] DROP-PARSE id={id} reason={_lastLobbyDropReason ?? "unknown"} keys={keyCount}");
            return false;
        }

        static void RequestBrowserLobbyDataIfNeeded(Lobby slobby) {
            if (slobby == null) {
                return;
            }
            ulong id = slobby.lobby_steamID.m_SteamID;
            if (id == 0) {
                return;
            }
            // caller already failed parse - ask Steam for a full metadata refresh
            if (!_pendingBrowserLobbyData.Add(id)) {
                Together.log.warn($"[LobbyBrowser] DATA-PENDING-ALREADY id={id}");
                return;
            }
            Together.log.warn($"[LobbyBrowser] REQUEST-DATA id={id} (metadata incomplete, waiting LobbyDataUpdate)");
            SteamMatchmaking.RequestLobbyData(slobby.lobby_steamID);
        }




        internal static void LeaveLobbyAndResetNet(CSteamID lobby_steamID) {
            IS_DOING_HostLobbyRecreatorLoop = false;
            CURRENT_LOBBY.lobby_steamID = lobby_steamID;
            LeaveLobbyAndResetNet(true);
        }
        internal static void LeaveLobbyAndResetNet(bool kill_transport = true) {
            if (Together.log.verbose) {
                Together.log.l($"STEAM: LeaveLobby called! lobbyID:{CURRENT_LOBBY.lobby_steamID} - STACK: {new StackTrace()}");
            } else {
                Together.log.l($"STEAM: LeaveLobby called! lobbyID:{CURRENT_LOBBY.lobby_steamID}");
            }

            SteamMatchmaking.LeaveLobby((CSteamID)CURRENT_LOBBY.lobby_steamID);
            KSteam.IS_IN_LOBBY = false;
            CURRENT_LOBBY.lobby_steamID = CSteamID.Nil;

            if (kill_transport) {
                if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {

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


        public static bool IsSteamMaintenanceLikely() {
            return IsSteamMaintenanceLikely(DateTime.UtcNow);
        }

        public static bool IsSteamMaintenanceLikely(DateTime utcTime) {
            // ensure the DateTime is treated as UTC
            if (utcTime.Kind != DateTimeKind.Utc) {
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
            }

            DayOfWeek day = utcTime.DayOfWeek;
            int hour = utcTime.Hour;

            // check for Tuesday (Maintenance Day)
            if (day == DayOfWeek.Tuesday) {
                // window: 20:00 UTC to 23:59 UTC
                return hour >= 20;
            }

            // check for Wednesday (Early morning spillover from Tuesday night)
            if (day == DayOfWeek.Wednesday) {
                // window: 00:00 UTC to 02:00 UTC
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


                if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                    foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
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

                if (plr == null || plr.body == null) {
                    return;
                }

                if (Time.unscaledTimeAsDouble - plr.netBody.timeOfDeath > 1) {
                    SteamTimeline.AddInstantaneousTimelineEvent("Death", plr.playerName + " died.", "steam_death", 20);
                }

            };
        }

        static bool IS_DOING_HostLobbyRecreatorLoop = false;
        static IEnumerator HostLobbyRecreatorLoop() {
            double start = Time.realtimeSinceStartupAsDouble;
            while (IS_IN_LOBBY && Net.TryGetSteamTransport(out TransportSteamworks tsteam) && CURRENT_LOBBY.lobby_steamID == CSteamID.Nil) {
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
            if (!Loaded) {
                return;
            }

            string[] args = System.Environment.GetCommandLineArgs();

            {
                SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024);
                if (pszCommandLine == null) {
                    pszCommandLine = "";
                } else {
                    args = pszCommandLine.Split(new char[] { ' ' }, options: StringSplitOptions.RemoveEmptyEntries);
                }
            }

            GetAppInstallDir();


            for (int i = 0; i < args.Length; i++) {
                string arg = args[i];

                try {
                    if (arg == ("+connect_lobby")) { // https://partner.steamgames.com/doc/api/ISteamFriends#GameLobbyJoinRequested_t
                        i++; if (i == args.Length) {
                            break;
                        }

                        arg = args[i];
                        if (ulong.TryParse(arg, out ulong newlobbysteamid)) {
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
    static void InitSteam() {

            try {
                string path = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, Filename_steam_appid_txt);

                if (!File.Exists(path)) {
                    File.WriteAllText(path, (Util.GameIsFull() ?
                        CONST_STEAMAPPID_CASUALTIESUNKNOWN_FULL // kinda useless?
                        : CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO // demo doesn't have steam_appid.txt bundled
                        ).ToString());
                }
            } catch (Exception e) {
#if TESTING
                Together.log.error($"Failed to get or create {Filename_steam_appid_txt} {e}");
#endif
                //don't return yet, let Init crash and say what's wrong
            }

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
                Together.log.error($"Fatally failed to initialize Steam: {e}");
                return;
            }
            STEAM_APPID = SteamUtils.GetAppID().m_AppId;



            APP_IS_DEMO = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO;
            APP_IS_PLAYTEST = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST;
            APP_IS_FULL = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_FULL;
            APP_IS_PIRATE = Util.GameIsFull() && !(APP_IS_PLAYTEST || APP_IS_FULL);



            Loaded = true;
            try {
                KSteam.Init();

                if (Plugin.s != null) {
                    Plugin.s.GetOrAddComponent<KSteam>();
#if FULLGAME
                } else if (SteamManager.instance != null) {
                    SteamManager.instance.GetOrAddComponent<KSteam>();
#endif
                } else {
                    GameObject.DontDestroyOnLoad(new GameObject("KSteam", typeof(KSteam)));
                }
            } catch (Exception e) {
                Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("steam_init_failed"), e.ToString()));
                Loaded = false;
                return;
            }

            if (Together.log.verbose) {
                Together.log.l($"Steamworks initialized: {KSteam.GetLocalUsername()} - {KSteam.GetLocalUserSteamID()}");
            }

            Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_initialized"));

        }

        public static bool CheckSteam() {
            try {
                InitSteam();
            } catch (Exception e) {
                log.LogError(e);
            }
            return Loaded;
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
                using (FindTheBugInator.Time(FindTheBugInator.SteamCb)) {
                    SteamAPI.RunCallbacks();
                }
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
                // you must launch with "-debug_steamapi" in the launch args to receive warnings.
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
            if (steamuserid == 0) {
                return "[UNKNOWN]";
            }

            // ask once, then always read the live persona. a stored name goes stale and a friend nickname is not the account name.
            if (force_reload_name || _personaAsked.Add(steamuserid)) {
                SteamFriends.RequestUserInformation((CSteamID)steamuserid, true);
            }
            string name = SteamFriends.GetFriendPersonaName((CSteamID)steamuserid);
            if (string.IsNullOrEmpty(name) || name == "[unknown]") {
                return steamuserid.ToString();
            }
            return name;
        }

        // the name this machine should be listed under. steam persona, unless the player typed one.
        public static string SessionName() {
            if ((Plugin.plrname_override || Multiplayer.ExplicitSessionName) && !string.IsNullOrEmpty(UIMainMenu.USERINPUT_NAME)) {
                return UIMainMenu.USERINPUT_NAME;
            }
            if (Loaded) {
                string live = GetLocalUsername();
                if (!string.IsNullOrEmpty(live) && live != "[unknown]") {
                    string clean = Multiplayer.SanitizeTextInput(live);
                    if (clean.Length > 0) {
                        UIMainMenu.USERINPUT_NAME = clean;
                        return clean;
                    }
                }
            }
            return UIMainMenu.USERINPUT_NAME;
        }
        public static Texture2D SteamLoadImage(int imageId) {
            if (imageId == -1) {
                return null;
            }

            if (imageId == 0) {
                return null;
            }

            if (SteamImagesCache.TryGetValue(imageId, out Texture2D texture)) {
                return texture;
            }

            uint width, height;
            if (!SteamUtils.GetImageSize(imageId, out width, out height)) {
                return null;
            }

            byte[] imageData = new byte[width * height * 4];

            if (!SteamUtils.GetImageRGBA(imageId, imageData, (int)(width * height * 4))) {
                return null;
            }

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
            if (steamid.m_SteamID == 0) {
                return;
            }

            if (plr.IconLarge == null) {
                Util.DelayCallLambda(UnityEngine.Random.value, () => {
                    if (plr != null && plr.IconLarge == null) {
                        plr.IconLarge = SteamLoadImage(SteamFriends.GetLargeFriendAvatar(steamid));
                        if (plr.IconLarge == null) {
                            return;
                        }

                        Sprite spr = plr.IconLargeSprite = Util.CreateSprite(plr.IconLarge);
                        if (plr.IconLargeSprite == null) {
                            plr.IconLargeSprite = spr;
                        }

                        if (plr.IconMediumSprite == null) {
                            plr.IconMediumSprite = spr;
                        }

                        if (plr.IconSmallSprite == null) {
                            plr.IconSmallSprite = spr;
                        }
                    }
                });
            }
            if (plr.IconMedium == null) {
                Util.DelayCallLambda(UnityEngine.Random.value, () => {
                    if (plr != null && plr.IconMedium == null) {
                        plr.IconMedium = SteamLoadImage(SteamFriends.GetMediumFriendAvatar(steamid));
                        if (plr.IconMedium == null) {
                            return;
                        }

                        Sprite spr = plr.IconMediumSprite = Util.CreateSprite(plr.IconMedium);
                        if (plr.IconLargeSprite == null) {
                            plr.IconLargeSprite = spr;
                        }

                        if (plr.IconMediumSprite == null) {
                            plr.IconMediumSprite = spr;
                        }

                        if (plr.IconSmallSprite == null) {
                            plr.IconSmallSprite = spr;
                        }
                    }
                });
            }
            if (plr.IconSmall == null) {
                Util.DelayCallLambda(UnityEngine.Random.value, () => {
                    if (plr != null && plr.IconSmall == null) {
                        plr.IconSmall = SteamLoadImage(SteamFriends.GetSmallFriendAvatar(steamid));
                        if (plr.IconSmall == null) {
                            return;
                        }

                        Sprite spr = plr.IconSmallSprite = Util.CreateSprite(plr.IconSmall);
                        if (plr.IconLargeSprite == null) {
                            plr.IconLargeSprite = spr;
                        }

                        if (plr.IconMediumSprite == null) {
                            plr.IconMediumSprite = spr;
                        }

                        if (plr.IconSmallSprite == null) {
                            plr.IconSmallSprite = spr;
                        }
                    }
                });
            }

        }
        static Dictionary<int, Texture2D> SteamImagesCache = new();
        static readonly HashSet<ulong> _personaAsked = new HashSet<ulong>();

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

            int bucket = 0;
            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
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

            SteamServerInfoInLobbyExtraData aaa = new SteamServerInfoInLobbyExtraData(Net.MyServerInfo);
            NetDataWriter writer = new NetDataWriter();
            writer.PutUnmanaged(aaa);
            writer.PutArray(ScavPlayer.AllPlayers.Select(x => x.SteamId).ToArray());
            writer.Put(Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST);



            writer.CompressWriter(0);
            NetDataReader reader = new NetDataReader(writer);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_EXTRADATA, Convert.ToBase64String(reader.GetRemainingBytes()));

        }

        static readonly string[] _requiredLobbyMetaKeys = {
            LOBBYDATAKEY_VERSION,
            LOBBYDATAKEY_LOBBYNAME,
            LOBBYDATAKEY_GAMEMODE,
            LOBBYDATAKEY_CURRENTLAYER,
            LOBBYDATAKEY_DEPTH,
            LOBBYDATAKEY_LIVINGCOUNT,
            LOBBYDATAKEY_PLAYERCOUNT,
            LOBBYDATAKEY_AVERAGEMOOD,
            LOBBYDATAKEY_EXTRADATA,
            LOBBYDATAKEY_HASPASSWORD,
            LOBBYDATAKEY_ISDEDICATED,
            LOBBYDATAKEY_MIDJOIN_LOCKED,
        };

        public static NetPublicServerInfo Client_LobbyBrowser_ReadLobbyIntoServerInfos(KSteam.Lobby slobby) {
            try {
                if (slobby?.metadata == null || slobby.metadata.Count == 0) {
                    _lastLobbyDropReason = "no-metadata";
                    return null;
                }

                List<string> missing = new List<string>();
                for (int i = 0; i < _requiredLobbyMetaKeys.Length; i++) {
                    if (!slobby.metadata.ContainsKey(_requiredLobbyMetaKeys[i])) {
                        missing.Add(_requiredLobbyMetaKeys[i]);
                    }
                }
                if (missing.Count > 0) {
                    string have = string.Join(",", slobby.metadata.Keys);
                    _lastLobbyDropReason = "missing-keys:" + string.Join(",", missing) + " have=[" + have + "]";
                    return null;
                }

                string VERSION = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_VERSION, null) ?? slobby.metadata[LOBBYDATAKEY_VERSION.ToLower()];
                string LOBBYNAME = slobby.metadata[LOBBYDATAKEY_LOBBYNAME];
                string GAMEMODE = slobby.metadata[LOBBYDATAKEY_GAMEMODE];
                string CURRENTLAYER = slobby.metadata[LOBBYDATAKEY_CURRENTLAYER];
                string DEPTH = slobby.metadata[LOBBYDATAKEY_DEPTH];
                string LIVINGCOUNT = slobby.metadata[LOBBYDATAKEY_LIVINGCOUNT];
                string PLRCOUNT = slobby.metadata[LOBBYDATAKEY_PLAYERCOUNT];
                string AVERAGEMOOD = slobby.metadata[LOBBYDATAKEY_AVERAGEMOOD];
                string EXTRADATA = slobby.metadata[LOBBYDATAKEY_EXTRADATA];
                string haspassword = slobby.metadata[LOBBYDATAKEY_HASPASSWORD];
                string ISDEDICATED = slobby.metadata[LOBBYDATAKEY_ISDEDICATED];
                string LOCKED = slobby.metadata[LOBBYDATAKEY_MIDJOIN_LOCKED];
                string MODS = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_MODS, "");
                string lang = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_LANGUAGE, "EN");
                string DESCRIPTION = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_DESCRIPTION, "");

                if (lang == null || Multiplayer.SanitizeTextInputAllowSpaces(lang) != lang || lang.Length > 10) {
                    lang = "";
                }


                if (DESCRIPTION.Length > Net.MAX_SERVER_DESC_LENGTH || DESCRIPTION.CountIndices('\n') > Net.MAX_SERVER_DESC_NEWLINES) {
                    _lastLobbyDropReason = "desc-too-long";
                    return null;
                }
                if (LOBBYNAME.Length > Net.MAX_SERVER_NAME_LENGTH) {
                    _lastLobbyDropReason = "name-too-long";
                    return null;
                }
                if (VERSION.Length > Net.MAX_SERVER_NAME_LENGTH) {
                    _lastLobbyDropReason = "version-too-long";
                    return null;
                }
                if (GAMEMODE.Length > 12) {
                    GAMEMODE = "";
                }
                if (lang.Length > 8) {
                    lang = "";
                }
                if (Multiplayer.SanitizeRichTextAllowSimpleTags(LOBBYNAME) != LOBBYNAME) {
                    LOBBYNAME = $"Expedition {slobby.lobby_steamID}";
                }
                if (Multiplayer.SanitizeRichTextAllowSimpleTags(DESCRIPTION) != DESCRIPTION) {
                    DESCRIPTION = "";
                }

                NetPublicServerInfo l = new NetPublicServerInfo();
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
                l.pingMs = -1;

                l.plr_living_count = int.Parse(LIVINGCOUNT);
                l.plr_max = slobby.memberlimit;
                l.plr_count = int.Parse(PLRCOUNT);


                if (l.cur_layer > 4 && (Util.GameIsDemo() || (Util.GameIsFull() && !(APP_IS_PLAYTEST || APP_IS_FULL))) && CURRENT_LOBBY.lobby_steamID != slobby.lobby_steamID) {
                    _lastLobbyDropReason = $"demo-layer-filter layer={l.cur_layer}";
                    return null;
                }

                // ascent + end lab stay out of the search browser (stale listings can still slip through)
                if (l.cur_layer >= UIServerBrowser.AscentLayerDepth && CURRENT_LOBBY.lobby_steamID != slobby.lobby_steamID) {
                    _lastLobbyDropReason = $"ascent-lab-layer-blacklist layer={l.cur_layer}";
                    return null;
                }


                try {
                    byte[] extradata = Convert.FromBase64String(EXTRADATA);
                    NetDataReader reader = new NetDataReader(extradata);
                    reader = reader.DecompressReader();

                    reader.GetUnmanaged(out SteamServerInfoInLobbyExtraData ssi);

                    ssi.Read(l);


                    {
                        ulong[] steamusers = reader.GetULongArray();
                        foreach (ulong steamid in steamusers) {
                            if (!slobby.members.ContainsKey((CSteamID)steamid)) {

                                slobby.members[(CSteamID)steamid] = new(steamid);
                            }
                        }
                    }

                    bool modsenforce = reader.GetBool();

                    {
                        l.modlist = MODS.Split(':');
                        if (modsenforce) {
                            string[] mymods = GetMyModListAsString().Split(':');

                            l.enforcemodlist_locked = !(new HashSet<string>(mymods).SetEquals(l.modlist));
                        }
                    }

                } catch (Exception eeeee) {
                    // extradata optional-ish - still show lobby
                    Together.log.warn($"[LobbyBrowser] extradata-parse-fail id={slobby.lobby_steamID} ver=\"{l.version}\": {eeeee.Message}");
                }

                //tODO call func that generates all localized strings?

                return l;
            } catch (Exception e) {
                _lastLobbyDropReason = "exception:" + e.GetType().Name + " " + e.Message;
                Together.log.warn($"[LobbyBrowser] DROP-EXCEPTION id={slobby?.lobby_steamID}: {e}");
            }
            return null;
        }



        public static void UpdateRichPresence() {
            //steamFriends.ClearRichPresence();

            // https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence
            // https://partner.steamgames.com/doc/features/enhancedrichpresence

            if (Net.IsRunning) {
#if TESTING
                SteamFriends.SetRichPresence("status", "Co-op mod: " + (Util.IsInMainMenu() ? "sigma 1..2..3.. " : WorldGeneration.world.biomeDepth.ToString()));
                //steamFriends.SetRichPresence("connect", "WHAT IS DISS !? ");

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

            foreach (ulong friendId in Friends) {

                FriendGameInfo_t gameInfo;
                if (SteamFriends.GetFriendGamePlayed((CSteamID)friendId, out gameInfo)) {
                    // check if friend is playing THIS game
                    if (gameInfo.m_gameID.AppID().m_AppId == STEAM_APPID) {
                        // check if they're in a lobby

#if TESTING
                        log.LogInfo($"FOUND FRIEND {SteamFriends.GetFriendPersonaName((CSteamID)friendId)} , LOBBY: {gameInfo.m_steamIDLobby} ");
#endif

                        if (gameInfo.m_steamIDLobby.IsValid()) {
                            //    friendId = friendId;
                            //    friendName = SteamFriends.GetFriendPersonaName(friendId);
                            //    lobbyId = gameInfo.m_steamIDLobby;

                            if (UIServerBrowser.CurServerList_ButOnlySteamLobbies.TryGetValue(gameInfo.m_steamIDLobby.m_SteamID, out NetPublicServerInfo l)) {
                            } else {
                                Lobby slobby = new Lobby();
                                UpdateLobbyInfo(gameInfo.m_steamIDLobby, ref slobby);
                                if (!TryAddLobbyToBrowser(slobby)) {
                                    RequestBrowserLobbyDataIfNeeded(slobby);
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

        // sTEAM MATCHMAKING
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
                + " -- " + pCallback.m_bLocked // if true, then only invited users may join.
                + " -- " + enterresponse); // this is actually a EChatRoomEnterResponse value. This will be set to k_EChatRoomEnterResponseSuccess if the lobby was successfully joined, otherwise it will be k_EChatRoomEnterResponseError.


            if (!Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                IS_IN_LOBBY = false;
                if (Together.log.verbose && enterresponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist) {
                    Together.log.error("STEAM: Received OnLobbyEnter without a transport !!!");
                }

                return;
            }
            if (enterresponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
                var msg = enterresponse.ToString();
                msg = msg.TrimStart("k_EChatRoomEnterResponse");

                Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("steam_lobbyenter_failed"), msg));

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
                    Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("steam_lobbyenter_iofailure"), reason));

                    IS_IN_LOBBY = false;
                    Net.ShutdownReset();

                    return;
                }

                var m_LobbyEnterResponse = (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse;
                var responseStr = m_LobbyEnterResponse.ToString().TrimStart("k_EChatRoomEnterResponse");

                if (m_LobbyEnterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
                    if (m_LobbyEnterResponse == EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_lobbyenter_rejected_noexists"));
                    } else {
                        Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("steam_lobbyenter_rejected"), responseStr));
                    }

                    IS_IN_LOBBY = false;
                    Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                    Net.ShutdownReset();

                    return;
                }

                if (!Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                    LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                    IS_IN_LOBBY = false;
                    if (Together.log.verbose) {
                        Together.log.error("STEAM: Received OnLobbyEnter_APIDispatch without a transport !!!");
                    }

                    return;
                }

                CURRENT_LOBBY.locked = Net.CurrentServerInfo.haspassword || pCallback.m_bLocked;
                UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);



                IS_IN_LOBBY = true;


#if TESTING
                Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("steam_onlobbyenter_id"), LobbyId));
#else
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_onlobbyenter"));
#endif
                UpdateRichPresence();



                if (Net.IsClient) {
                    if (tsteam.CreateClientSocket()) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_socket_success"));

                        Net.OnTransportConnected();

                        NetDataWriter hellopacket = Multiplayer.GetClientConnectIntroductionPacket();
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
            var lobbyId = (CSteamID)pCallback.m_ulSteamIDLobby;

            // browser RequestLobbyData result - don't overwrite CURRENT_LOBBY
            if (_pendingBrowserLobbyData.Remove(pCallback.m_ulSteamIDLobby)) {
                if (pCallback.m_bSuccess == 0) {
                    Together.log.warn($"[LobbyBrowser] DATA-FAIL id={pCallback.m_ulSteamIDLobby}");
                    return;
                }
                Lobby slobby = new Lobby();
                UpdateLobbyInfo(lobbyId, ref slobby);
                Together.log.warn($"[LobbyBrowser] DATA-OK id={pCallback.m_ulSteamIDLobby} keys={slobby.metadata.Count}");
                TryAddLobbyToBrowser(slobby);
                return;
            }

            if (!IS_IN_LOBBY || CURRENT_LOBBY.lobby_steamID != lobbyId) {
                return;
            }

            var ooo = CURRENT_LOBBY.ownerID;
            UpdateLobbyInfo(lobbyId, ref CURRENT_LOBBY);
            CURRENT_LOBBY.ownerID = ooo;

        }



        static void OnLobbyChatUpdate(LobbyChatUpdate_t pCallback) {
            try {
                var stateChange = (EChatMemberStateChange)pCallback.m_rgfChatMemberStateChange;
                if (pCallback.m_ulSteamIDUserChanged == pCallback.m_ulSteamIDMakingChange) {
                    steamtestlog("[" + LobbyChatUpdate_t.k_iCallback + " - LobbyChatUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUserChanged) + " -- " + stateChange, true);
                } else {
                    steamtestlog("[" + LobbyChatUpdate_t.k_iCallback + " - LobbyChatUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUserChanged) + " -- " + pCallback.m_ulSteamIDMakingChange + " -- " + stateChange, true);
                }

                if (IS_IN_LOBBY && Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                } else {
                    if (Multiplayer.verbose) {
                        Together.log.warn("Received OnLobbyChatUpdate while transport is not active, what?"); // happens on  k_EChatMemberStateChangeLeft!
                    }

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


                // convert flags -> bools
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
                            tsteam.KickMember((CSteamID)pCallback.m_ulSteamIDUserChanged, "<key:steam_banned>");
                            tsteam.RemoveSteamUser(pCallback.m_ulSteamIDUserChanged);
                        } else {
                            SteamFriends.RequestUserInformation((CSteamID)pCallback.m_ulSteamIDUserChanged, true);
                        }
                    }
                }


                if (left || disconnected || kicked || banned) { // anything related to getting disconnected

                    if (isLocalUser && tsteam.i_am_owner_of_this_lobby) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_removed_owner"));

                        CURRENT_LOBBY.lobby_steamID = CSteamID.Nil;
                        tsteam.Shutdown(false); // clean up the sockets
                        Util.StartCoroutine(HostLobbyRecreatorLoop);

                        return;
                    }

                    if (isLocalUser) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_removed"));

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


                    // tODO, THIS IS TEMPORARY SOLUTION TO KILL ALL CLIENTS , BEFORE I FIGURE OUT HOST TRANSFER
                    if (Net.IsClient) {
                        if (pCallback.m_ulSteamIDUserChanged == CURRENT_LOBBY.ownerID.m_SteamID) {
                            Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_owner_left"));

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
            steamtestlog("GetLobbyChatEntry(" + (CSteamID)pCallback.m_ulSteamIDLobby + ", " + (int)pCallback.m_iChatID + ", out SteamIDUser, Data, Data.Length, out ChatEntryType) : " + ret + " -- " + SteamIDUser + " -- " + System.Text.Encoding.UTF8.GetString(data, 0, ret).Trim() + " -- " + ChatEntryType);


            string msg = System.Text.Encoding.UTF8.GetString(data, 0, ret);
            if (msg.StartsWith("KICK:")) {// STEAM DOES NOT PROVIDE A WAY TO KICK MEMBERS WHAAAAAAAATTTTTTTTT ???????????????????????????????
                if (pCallback.m_ulSteamIDUser == CURRENT_LOBBY.ownerID.m_SteamID) {
                    var reason = "<key:steam_kick_default>";

                    msg = msg.Substring(5); // remove kick: prefix
                    int colonloc = msg.IndexOf(':');
                    if (colonloc != -1) {
                        reason = msg.Substring(colonloc + 1);
                        msg = msg.Substring(0, colonloc);
                    }
                    ulong targetId = ulong.Parse(msg);

                    if (targetId == SteamUser.GetSteamID().m_SteamID) {
                        Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("steam_kicked"), Lang.ParseDisconnectReason(reason)));
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
            if (Together.log.verbose) {
                steamtestlog("[" + LobbyMatchList_t.k_iCallback + " - LobbyMatchList] - " + pCallback.m_nLobbiesMatching + $"  {(LobbySearch_BucketCounter == 0 ? "" : (LobbySearch_BucketCounter - 1).ToString())}");
            }

            if (bIOFailure) {
                ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyMatchListCallResult.Handle);
                Together.log.error("OnLobbyMatchList encountered an IOFailure due to: " + reason);
                return;
            }
            try {

                if (pCallback.m_nLobbiesMatching == 0) {
                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_no_lobbies"));
                } else {
                    int added = 0;
                    int pendingData = 0;
                    for (int i = 0; i < pCallback.m_nLobbiesMatching; i++) {
                        Lobby slobby = new Lobby();

                        UpdateLobbyInfo(SteamMatchmaking.GetLobbyByIndex(i), ref slobby);

                        LOBBIES.Add(slobby);

                        if (TryAddLobbyToBrowser(slobby)) {
                            added++;
                        } else {
                            // metadata not local yet (common) - ask Steam, LobbyDataUpdate will add
                            int before = _pendingBrowserLobbyData.Count;
                            RequestBrowserLobbyDataIfNeeded(slobby);
                            if (_pendingBrowserLobbyData.Count > before) {
                                pendingData++;
                            } else {
#if !RELEASE
                                log.LogWarning($"INCOMPATIBLE LOBBY INFO DUMP:\n{slobby.DebugReadableDump()}");
#endif
                            }
                        }
                    }
                    Together.log.warn($"[LobbyBrowser] MatchList steam={pCallback.m_nLobbiesMatching} accepted={added} request-data={pendingData} ui={UIServerBrowser.CurServerCount}/{UIServerBrowser.CurFilteredServerList.Count} myVer=\"{Multiplayer.FULL_VERSION_TAG}\"");
                }


                // now check for friends in those lobbies
                if (LobbySearch_BucketCounter == 0) {
                    SearchFriendLobbies();
                }

                // search next buckets
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
                Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("steam_onlobbycreated_iofailure"), reason));

                IS_IN_LOBBY = false;
                Net.ShutdownReset();
                Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                return;
            } else if (pCallback.m_eResult != EResult.k_EResultOK) {
                Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                Net.ShutdownReset();
                IS_IN_LOBBY = false;
                if (pCallback.m_eResult == EResult.k_EResultNoConnection) {
                    Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("steam_create_lobby_failed_nonet"));
                    return;
                }
                Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("steam_create_lobby_failed"), pCallback.m_eResult));
                return;
            }

            if (Net.Transport == null || (Net.Transport is not TransportSteamworks)) {
                Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("steam_created_no_transport"));

                IS_IN_LOBBY = false;
                Net.ShutdownReset();
                return;
            }

            UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);

            IS_IN_LOBBY = true;

            int bucket = 0;
            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
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

            Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_lobby_created"));
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










        // sTEAM FRIENDS

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


            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                if (changes.HasFlag(EPersonaChange.k_EPersonaChangeName) || changes.HasFlag(EPersonaChange.k_EPersonaChangeNickname) || changes.HasFlag(EPersonaChange.k_EPersonaChangeNameFirstSet)) {

                    KSteam.GetSteamUsername(steamID.m_SteamID, true);

                    if (tsteam.SteamIDToNetPlayerDict.TryGetValue(pCallback.m_ulSteamID, out ScavPlayer plr)) {
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
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_joinrequest_in_game"));
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("steam_joinrequest"), pCallback.m_steamIDLobby));
                TransportSteamworks.OnWantToJoinLobby(pCallback.m_steamIDLobby.m_SteamID);
            }
        }

        static void OnAvatarImageLoaded(AvatarImageLoaded_t pCallback) {

            var steamID = (CSteamID)pCallback.m_steamID;

            steamtestlog("[" + AvatarImageLoaded_t.k_iCallback + " - AvatarImageLoaded] - " + pCallback.m_steamID + " -- " + pCallback.m_iImage + " -- " + pCallback.m_iWide + " -- " + pCallback.m_iTall);


            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                if (tsteam.SteamIDToNetPlayerDict.TryGetValue((ulong)steamID, out ScavPlayer plr)) {
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
            int ret = SteamFriends.GetClanChatMessage(pCallback.m_steamIDClanChat, pCallback.m_iMessageID, out Text, 2048, out ChatEntryType, out Chatter); // must be called from within OnGameConnectedClanChatMsg
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
            int ret = SteamFriends.GetFriendMessage(pCallback.m_steamIDUser, pCallback.m_iMessageID, out Text, 2048, out ChatEntryType); // must be called from within OnGameConnectedFriendChatMsg
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
            if (!KSteam.Loaded) {
                return;
            }

            { // bind all callbacks

                // sTEAM MATCHMAKING
                FavoritesListChanged = Callback<FavoritesListChanged_t>.Create(OnFavoritesListChanged);
                LobbyInvite = Callback<LobbyInvite_t>.Create(OnLobbyInvite);
                LobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                LobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
                LobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                LobbyChatMsg = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMsg);
                LobbyGameCreated = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
                LobbyKicked = Callback<LobbyKicked_t>.Create(OnLobbyKicked);
                FavoritesListAccountsUpdated = Callback<FavoritesListAccountsUpdated_t>.Create(OnFavoritesListAccountsUpdated);
                //searchForGameProgressCallback = Callback<SearchForGameProgressCallback_t>.Create(OnSearchForGameProgressCallback);
                //searchForGameResultCallback = Callback<SearchForGameResultCallback_t>.Create(OnSearchForGameResultCallback);
                //requestPlayersForGameProgressCallback = Callback<RequestPlayersForGameProgressCallback_t>.Create(OnRequestPlayersForGameProgressCallback);
                //requestPlayersForGameResultCallback = Callback<RequestPlayersForGameResultCallback_t>.Create(OnRequestPlayersForGameResultCallback);
                //requestPlayersForGameFinalResultCallback = Callback<RequestPlayersForGameFinalResultCallback_t>.Create(OnRequestPlayersForGameFinalResultCallback);
                //submitPlayerResultResultCallback = Callback<SubmitPlayerResultResultCallback_t>.Create(OnSubmitPlayerResultResultCallback);
                //endGameResultCallback = Callback<EndGameResultCallback_t>.Create(OnEndGameResultCallback);
                JoinPartyCallback = Callback<JoinPartyCallback_t>.Create(OnJoinPartyCallback);
                CreateBeaconCallback = Callback<CreateBeaconCallback_t>.Create(OnCreateBeaconCallback);
                ReservationNotificationCallback = Callback<ReservationNotificationCallback_t>.Create(OnReservationNotificationCallback);
                ChangeNumOpenSlotsCallback = Callback<ChangeNumOpenSlotsCallback_t>.Create(OnChangeNumOpenSlotsCallback);
                AvailableBeaconLocationsUpdated = Callback<AvailableBeaconLocationsUpdated_t>.Create(OnAvailableBeaconLocationsUpdated);
                ActiveBeaconsUpdated = Callback<ActiveBeaconsUpdated_t>.Create(OnActiveBeaconsUpdated);
                OnLobbyEnterCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEnter_APIDispatch);
                OnLobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);
                OnLobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);

                // sTEAM FRIENDS
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

            // steamApps.GetCurrentGameLanguage()



            SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024);
            if (pszCommandLine == null) {
                pszCommandLine = "";
            }
            steamtestlog("[SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024)] - " + pszCommandLine);

            LoadFriendList();

            UpdateRichPresence();
        }




        #endregion // aLL_STEAM_CALLBACKS






        public static bool _DEV_ENABLE_STEAM_LOG => DebugMenuSettings._DEV_ENABLE_STEAM_LOG;
        internal static void steamtestlog(string what, bool forcelog = false) {
            if (!forcelog) {
                if (!_DEV_ENABLE_STEAM_LOG) {
                    return;
                }
            }
            try {
                what = "STEAM: " + what;
                Plugin.Logger.LogInfo(Together.log.do_timestamp(what));

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

            GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(sendBufferSizeBytes, System.Runtime.InteropServices.GCHandleType.Pinned);

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
                    if (is_steamserver) {
                        InteropHelp.TestIfAvailableGameServer();
                    } else {
                        InteropHelp.TestIfAvailableClient();
                    }

                    return true;
                } catch {
                    return false;
                }
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t pCallback) {
            try {
                // Disposed transports can still have callbacks queued by Steam.
                if (!ReferenceEquals(Net.Transport, this)) return;
                // dedicated browser-ping virtual port - never treat as a game client
                if (BrowserSteamPing.HandleTransportCallback(pCallback)) {
                    return;
                }
                if (BrowserSteamPing.IsStrayPing(pCallback)) {
                    log.l("[steam] ignore ping conn=" + pCallback.m_hConn.m_HSteamNetConnection
                        + " state=" + pCallback.m_info.m_eState
                        + " debug=\"" + OneLine(pCallback.m_info.m_szEndDebug) + "\""
                        + " desc=\"" + OneLine(pCallback.m_info.m_szConnectionDescription) + "\"");
                    return;
                }
                LogSteamState(pCallback);


#if !RELEASE
                KSteam.steamtestlog($"{this.GetType().Name}.OnConnectionStatusChanged [" + SteamNetConnectionStatusChangedCallback_t.k_iCallback + " - SteamNetConnectionStatusChangedCallback] - " + pCallback.m_hConn + " -- " + pCallback.m_info + " -- " + pCallback.m_eOldState);
#endif


                if (pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) {
                    //this happens when someone asked to connect to us, in the case of NetCode for GameObject this should only happen if we are a server/host
                    //the current standard is to blindly accept ... NetCode for GO should really consider a validation model for connections
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: - connection request from " + pCallback.m_info.m_identityRemote.GetSteamID64());
                    }

                    if (Net.IsServer) {

                        if (connectionMapping.Count > Multiplayer.PLAYER_COUNT_LIMIT) {
#if !RELEASE
                            if (log.verbose) {
                                KSteam.steamtestlog($"OnConnectionStatusChanged: Rejecting connection {pCallback.m_info.m_identityRemote.GetSteamID64()}");
                            }
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
                                if (log.verbose) {
                                    KSteam.steamtestlog($"OnConnectionStatusChanged: Accepting connection {pCallback.m_info.m_identityRemote.GetSteamID64()}");
                                }

                                var clientId = pCallback.m_info.m_identityRemote.GetSteamID64();

                                //this should be a new connection, record it
                                if (connectionMapping.ContainsKey(clientId) == false) {
                                    SteamConnectionData nCon = new SteamConnectionData(pCallback.m_info.m_identityRemote.GetSteamID());
                                    nCon.connection = pCallback.m_hConn;
                                    connectionMapping.Add(clientId, nCon);
                                }
                            } else {
                                if (log.verbose) {
                                    KSteam.steamtestlog($"OnConnectionStatusChanged: Connection {pCallback.m_info.m_identityRemote.GetSteamID64()} could not be accepted: this is not a server");
                                }
                            }
                        } else {
                            if (log.verbose) {
                                KSteam.steamtestlog($"OnConnectionStatusChanged: Connection {pCallback.m_info.m_identityRemote.GetSteamID64()} could not be accepted: {res}");
                            }
                        }
                    }
                } else if (pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) {
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: connection request to " + pCallback.m_info.m_identityRemote.GetSteamID64() + " was accepted!");
                    }

                    var clientId = pCallback.m_info.m_identityRemote.GetSteamID64();


                    //we should already have it but if not record the server connection that was just accepted
                    if (connectionMapping.ContainsKey(clientId) == false) {
                        SteamConnectionData nCon = new SteamConnectionData(pCallback.m_info.m_identityRemote.GetSteamID());
                        nCon.connection = pCallback.m_hConn;
                        connectionMapping.Add(clientId, nCon);
                    } else {
                        //should be redundent but update the conneciton handle anyway
                        connectionMapping[clientId].connection = pCallback.m_hConn;
                    }

                    //return NetworkEvent.Connect;
                    return;
                } else if (
                    pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                    || pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                ) {

                    var client_steamId = pCallback.m_info.m_identityRemote.GetSteamID64();

                    //the connection is no more
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: Connection closed for {BetterToStringFromSteamID(client_steamId)} state changed: {pCallback.m_info.m_eState}    lobby owner:{BetterToStringFromSteamID(KSteam.CURRENT_LOBBY.ownerID.m_SteamID)}");
                    }

                    if (LobbyHostTransfer.HandlePlannedDisconnect(client_steamId)) return;
                    bool gameLink = serverUser != null && pCallback.m_hConn == serverUser.connection;
                    if (client_steamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID && gameLink) {
                        ESteamNetConnectionEnd ownerEnd = (ESteamNetConnectionEnd)pCallback.m_info.m_eEndReason;
                        log.l("[steam] owner-down end=" + ownerEnd
                            + " (" + pCallback.m_info.m_eEndReason + ")"
                            + " debug=\"" + OneLine(pCallback.m_info.m_szEndDebug) + "\""
                            + " state=" + pCallback.m_info.m_eState);
                        Multiplayer.ShutdownNetwork();
                        Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("steam_connection_closed"), pCallback.m_info.m_eState.ToString().TrimStart("k_ESteamNetworkingConnectionState_")));
                    } else if (client_steamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID) {
                        log.l("[steam] ignore other owner conn=" + pCallback.m_hConn.m_HSteamNetConnection
                            + " game=" + (serverUser != null ? serverUser.connection.m_HSteamNetConnection.ToString() : "none")
                            + " state=" + pCallback.m_info.m_eState
                            + " debug=\"" + OneLine(pCallback.m_info.m_szEndDebug) + "\"");
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
                        //remove the mapped connection info
                        RemoveSteamUser(client_steamId);
                    }

                    //return NetworkEvent.Disconnect;
                    return;
                } else {
                    var client_steamId = pCallback.m_info.m_identityRemote.GetSteamID64();
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: Connection closed for {BetterToStringFromSteamID(client_steamId)} state changed: {pCallback.m_info.m_eState}    lobby owner:{BetterToStringFromSteamID(KSteam.CURRENT_LOBBY.ownerID.m_SteamID)}");
                    }
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
        // one buffer for the life of the transport. A client used to allocate 65535 slots every frame.
        const int ClientPollCap = 256;
        const int HostPollCap = 255;
        IntPtr[] _pollPtrs;
        readonly List<SteamConnectionData> _pollConnections = new List<SteamConnectionData>(4);

        public byte hostLobbyBucket;

        //cALLED ONLY FROM  OnWantToHostLobby !!!!
        internal bool CreateServerSocket() {
            if (listenSocket != HSteamListenSocket.Invalid) {
                log.error($"{this.GetType().Name}.CreateServerSocket : Socket already exists !!!!!!!!! {listenSocket}");
                return false;
            }

            if (onConnectionChange == null) {
                if (is_steamserver) {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
                } else {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                }
            }

            if (options == null) {
                options = new SteamNetworkingConfigValue_t[0];
            }

            if (is_steamserver) {
                listenSocket = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
            } else {
                listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
            }

            // real browser RTT probes connect here; accept+close without joining the game
            if (!is_steamserver) {
                BrowserSteamPing.EnsureHostListen();
            }

            log.l("[steam] listen role=host socket=" + listenSocket.m_HSteamListenSocket
                + " ok=" + (listenSocket != HSteamListenSocket.Invalid ? 1 : 0)
                + " " + RelayBits()
                + " " + LobbyBits());

            return listenSocket != HSteamListenSocket.Invalid;
        }
        internal bool CreateClientSocket() {
            if (listenSocket != HSteamListenSocket.Invalid) {
                log.error($"{this.GetType().Name}.CreateClientSocket : Socket already exists! {listenSocket}");
                return false;
            }

            if (onConnectionChange == null) {
                if (is_steamserver) {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
                } else {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                }
            }

            var ConnectToSteamID = KSteam.CURRENT_LOBBY.ownerID;

            serverUser = new SteamConnectionData((CSteamID)(ConnectToSteamID));

            try {
                if (is_steamserver) {
                    SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
                } else {
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                }

                SteamNetworkingIdentity smi = new SteamNetworkingIdentity();
                smi.SetSteamID(serverUser.id);
                if (is_steamserver) {
                    serverUser.connection = SteamGameServerNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
                } else {
                    serverUser.connection = SteamNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
                }

                connectionMapping.Add((ulong)ConnectToSteamID, serverUser);

                bool ok = serverUser.connection != HSteamNetConnection.Invalid;
                log.l("[steam] connect role=client target=" + (ulong)ConnectToSteamID
                    + " conn=" + serverUser.connection.m_HSteamNetConnection
                    + " ok=" + (ok ? 1 : 0)
                    + " " + RelayBits()
                    + " " + RouteBits(serverUser.connection)
                    + " " + LobbyBits());
                return ok;
            } catch (Exception ex) {
                log.error($"{this.GetType().Name}.CreateClientSocket : Client could not be started !! {ex.Message}");
                log.l("[steam] connect role=client target=" + (ulong)ConnectToSteamID + " fail=\"" + OneLine(ex.Message) + "\"");
                return false;
            }
        }

        // one line per steam state change. release builds were only printing the final "problem detected locally".
        void LogSteamState(SteamNetConnectionStatusChangedCallback_t cb) {
            ulong remote = 0;
            try {
                remote = cb.m_info.m_identityRemote.GetSteamID64();
            } catch (Exception) {
            }
            ESteamNetConnectionEnd end = (ESteamNetConnectionEnd)cb.m_info.m_eEndReason;
            string name = "";
            if (remote != 0) {
                try {
                    name = SteamFriends.GetFriendPersonaName((CSteamID)remote);
                } catch (Exception) {
                }
            }
            bool owner = false;
            try {
                owner = remote != 0 && remote == KSteam.CURRENT_LOBBY.ownerID.m_SteamID;
            } catch (Exception) {
            }
            log.l("[steam] state role=" + (Net.IsServer ? "host" : "client")
                + " conn=" + cb.m_hConn.m_HSteamNetConnection
                + " remote=" + remote
                + " name=\"" + OneLine(name) + "\""
                + " owner=" + (owner ? 1 : 0)
                + " old=" + cb.m_eOldState + "(" + (int)cb.m_eOldState + ")"
                + " state=" + cb.m_info.m_eState + "(" + (int)cb.m_info.m_eState + ")"
                + " end=" + end + "(" + cb.m_info.m_eEndReason + ")"
                + " debug=\"" + OneLine(cb.m_info.m_szEndDebug) + "\""
                + " desc=\"" + OneLine(cb.m_info.m_szConnectionDescription) + "\""
                + " map=" + connectionMapping.Count
                + " " + RelayBits()
                + " " + RouteBits(cb.m_hConn)
                + " " + LobbyBits());
        }

        static string AvailShort(ESteamNetworkingAvailability avail) {
            string name = avail.ToString();
            const string prefix = "k_ESteamNetworkingAvailability_";
            if (name.StartsWith(prefix)) {
                name = name.Substring(prefix.Length);
            }
            return name + "(" + (int)avail + ")";
        }

        // relay readiness at this instant. a slow join that is already Current is not waiting on startup.
        string RelayBits() {
            try {
                SteamRelayNetworkStatus_t status;
                ESteamNetworkingAvailability avail = is_steamserver
                    ? SteamGameServerNetworkingUtils.GetRelayNetworkStatus(out status)
                    : SteamNetworkingUtils.GetRelayNetworkStatus(out status);
                return "relay=" + AvailShort(avail)
                    + " config=" + AvailShort(status.m_eAvailNetworkConfig)
                    + " any=" + AvailShort(status.m_eAvailAnyRelay)
                    + " pinging=" + status.m_bPingMeasurementInProgress
                    + " msg=\"" + OneLine(status.m_debugMsg) + "\"";
            } catch (Exception e) {
                return "relayErr=" + e.GetType().Name;
            }
        }

        // steam's own route text: selected transport, relay pop, and why it is still searching.
        string RouteBits(HSteamNetConnection conn) {
            try {
                string detail;
                int n = is_steamserver
                    ? SteamGameServerNetworkingSockets.GetDetailedConnectionStatus(conn, out detail, 2048)
                    : SteamNetworkingSockets.GetDetailedConnectionStatus(conn, out detail, 2048);
                if (n < 0 || string.IsNullOrEmpty(detail)) {
                    return "route=none";
                }
                string line = OneLine(detail);
                if (line.Length > 800) {
                    line = line.Substring(0, 800);
                }
                return "route=\"" + line + "\"";
            } catch (Exception e) {
                return "routeErr=" + e.GetType().Name;
            }
        }

        string LobbyBits() {
            ulong lobby = 0;
            ulong owner = 0;
            int members = -1;
            try {
                lobby = KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID;
                owner = KSteam.CURRENT_LOBBY.ownerID.m_SteamID;
                if (lobby != 0) {
                    members = SteamMatchmaking.GetNumLobbyMembers(KSteam.CURRENT_LOBBY.lobby_steamID);
                }
            } catch (Exception) {
            }
            return "lobby=" + lobby + " lobbyOwner=" + owner + " members=" + members;
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

            Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_creating_lobby"));

            if (log.verbose) {
                log.l(this.GetType().Name + ".CreateLobby");
            }

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

            if (!Net.VerifyMyServerName()) {
                if (!NsfwContentFilter.LastCreateWasRejected) {
                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("net_invalid_server_name"));
                }

                return false;
            }

            try {

                UIMainMenu._STEAM_CHOSEN_LOBBYTYPE = KSteam.LobbyTypeToNum(lobbyType);
                Net.TransportCreated(type);
                TransportSteamworks t = new TransportSteamworks();
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
            Net.NetType type = Net.NetType.Client;

            try {

                Net.TransportCreated(type);
                TransportSteamworks t = new TransportSteamworks();
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
            if (!KSteam.Loaded && KSteam.CURRENT_LOBBY.lobby_steamID != CSteamID.Nil) {
                return;
            }

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

                foreach (SteamConnectionData connectionData in connectionMapping.Values) {
                    var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connectionData.connection, ref status, 0, ref laneStatus);
                    if (result == EResult.k_EResultOK) {
                        int ping_ms = status.m_nPing;

                        if (SteamIDToNetPlayerDict.TryGetValue(connectionData.id.m_SteamID, out ScavPlayer plr)) {
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

                        float ping_s = ((float)ping_ms) * 0.001f;

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

            float DefaultTimeoutInSeconds = (float)DefaultTimeoutMS * 0.001f;

            double curtime = Time.realtimeSinceStartupAsDouble;
            if (i_am_owner_of_this_lobby) {
                foreach (KeyValuePair<CSteamID, LobbyMember> item in KSteam.CURRENT_LOBBY.members) {
                    if (KSteam.GetLocalUserSteamID() == item.Key) {
                        item.Value.server_last_data_receive_time = curtime;
                        continue;
                    }

                    double lastdatareceived_time = curtime - item.Value.server_last_data_receive_time;

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
                        KickMember(item.Key, "Timed out!");
                        RemoveSteamUser(item.Key.m_SteamID);

                    }
                }
            } else {
                foreach (KeyValuePair<CSteamID, LobbyMember> item in KSteam.CURRENT_LOBBY.members) {
                    item.Value.server_last_data_receive_time = curtime;
                }
            }

        }
        void UpdateTheIDDicts() {
            SteamIDToNetPlayerDict.Clear();
            SteamIDToClientIDDict.Clear();

            foreach (KeyValuePair<knetid, ScavPlayer> item in ScavPlayer.ClientIdToPlayerDict) {
                SteamIDToNetPlayerDict[item.Value.SteamId] = item.Value;
                SteamIDToClientIDDict[item.Value.SteamId] = item.Value.playerId;
                item.Value.IsHost = item.Value.SteamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID;
            }
        }
        void _Update_PollMessages() {
            // receive only what this frame will dispatch. Steam leaves the rest queued.
            int cap = Net.IsClient ? ClientPollCap : HostPollCap;
            if (_pollPtrs == null || _pollPtrs.Length < cap) {
                _pollPtrs = new IntPtr[cap];
            }

            _pollConnections.Clear();
            foreach (SteamConnectionData connectionData in connectionMapping.Values) {
                _pollConnections.Add(connectionData);
            }

            int remaining = cap;
            for (int c = 0; c < _pollConnections.Count; c++) {
                if (Net.IsClient && remaining <= 0) {
                    break;
                }

                SteamConnectionData connectionData = _pollConnections[c];
                int take = Net.IsClient ? remaining : cap;
                // if the connection handle is invalid, -1 is returned. If no data is available, 0 is returned.
                int messageCount = is_steamserver
                    ? SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, _pollPtrs, take)
                    : SteamNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, _pollPtrs, take);
                if (messageCount <= 0) {
                    continue;
                }

                if (messageCount > take) {
                    messageCount = take;
                }

                for (int i = 0; i < messageCount; i++) {
                    SteamNetworkingMessage_t data = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_pollPtrs[i]);
                    FindTheBugInator.NoteSteamIn(data.m_cbSize);
                    byte[] buffer = new byte[data.m_cbSize];
                    Marshal.Copy(data.m_pData, buffer, 0, data.m_cbSize);
                    SteamNetworkingMessage_t.Release(_pollPtrs[i]);

                    try {
                        _K_OnReceiveNetworkingMessage(connectionData, buffer);
                    } catch (Exception e) {
                        Plugin.Logger.LogError($"{this.GetType().Name}._K_OnReceiveNetworkingMessage\n ERROR:\n{e.ToString()}\nCURRENT LOBBY:{(KSteam.CURRENT_LOBBY == null ? "NULL_LOBBY" : KSteam.CURRENT_LOBBY.DebugReadableDump())}\nconnectionData: {connectionData}");
                    }
                }

                if (Net.IsClient) {
                    remaining -= messageCount;
                }
            }
        }

        void _Server_NewLobbyMember(NetDataReader reader, ulong steamID) {
            bool privileged_user = false;
            {
                if (KnownPersons.IsSteamUserPrivileged(steamID)) {
                    privileged_user = true;
                }
            }

            bool validated = Net.ValidateNewClientHandshake(reader, false, privileged_user, out Multiplayer.IntroductionPlayerInfo userInfo, out string reject_reason);
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

            string joined = userInfo.userName;
            if (string.IsNullOrEmpty(joined) || joined == "EMPTY_STRING") {
                joined = steamID.ToString();
            }
            BasePlayer playerEntity = BasePlayer.ServerCreatePlayer(joined, steamID, userInfo.color, false);

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

            NetDataReader reader = new NetDataReader(data);

            if (i_am_owner_of_this_lobby) {

                if (!KSteam.CURRENT_LOBBY.members.TryGetValue(connection.id, out LobbyMember _member)) {
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


        public readonly Dictionary<ulong, ScavPlayer> SteamIDToNetPlayerDict = new();
        public readonly TwoWayDictionary<ulong, knetid> SteamIDToClientIDDict = new();


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


            if (SteamIDToNetPlayerDict.TryGetValue(steamID, out ScavPlayer player) && player != null) {
                try {
                    GameObject.DestroyImmediate(player.gameObject);
                } catch (Exception e) {
                    Plugin.Logger.LogError(e.ToString());
                }
            }

            SteamIDToNetPlayerDict.Remove(steamID);
            SteamIDToClientIDDict.Remove(steamID);


            if (connectionMapping.TryGetValue(steamID, out SteamConnectionData conndata)) {

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

            if (SteamIDToClientIDDict.TryGet(clientid, out ulong steamid)) {
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
                if (KSteam.CURRENT_LOBBY.members.TryGetValue(target, out LobbyMember member)) {
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
                if (is_steamserver) {
                    SteamGameServerNetworkingSockets.CloseListenSocket(listenSocket);
                } else {
                    SteamNetworkingSockets.CloseListenSocket(listenSocket);
                }

                listenSocket = HSteamListenSocket.Invalid;
            }
            BrowserSteamPing.CloseHostListen();
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
                if (connectionMapping.ContainsKey(serverUser.id.m_SteamID)) {
                    connectionMapping.Remove(serverUser.id.m_SteamID);
                }

                if (is_steamserver) {
                    SteamGameServerNetworkingSockets.CloseConnection(serverUser.connection, 0, "Disconnected", false);
                } else {
                    SteamNetworkingSockets.CloseConnection(serverUser.connection, 0, "Disconnected", false);
                }
            }
            serverUser = null;
        }
        private void CloseP2PSessions() {
            CloseConnectionOnServerUser();

            foreach (SteamConnectionData user in connectionMapping.Values) {
                if (is_steamserver) {
                    SteamGameServerNetworkingSockets.CloseConnection(user.connection, 0, "Disconnected", false);
                } else {
                    SteamNetworkingSockets.CloseConnection(user.connection, 0, "Disconnected", false);
                }
            }

            connectionMapping.Clear();
            if (log.verbose) {
                KSteam.steamtestlog($"{this.GetType().Name}.CloseP2PSessions - has Closed P2P Sessions With all Users");
            }

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
                if (serverUser == null) {
                    return;
                }

                steamID = serverUser.id.m_SteamID;
            }

            //check if we have a mapped user for this ID
            if (connectionMapping.TryGetValue(steamID, out SteamConnectionData mappedconnection)) {
                //grab a pointer to the user's connection

                SendThroughSteamSocket(mappedconnection.connection, data, sendFlag);
            } else {
                //no client found so report that
#if RELEASE && !PLAYTEST
                if (log.verbose)
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Trying to send to an unknown connection  {BetterToStringFromSteamID(steamID)}");
#else
                if (log.verbose) // DO NOT CHANGE THESE TO log.error !!! it causes stack overflow!!!
    {
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Trying to send to an unknown connection  {BetterToStringFromSteamID(steamID)}\n{new StackTrace()}");
                }
#endif
            }
        }
        double lastExceedWarn = 0;
        public void SendThroughSteamSocket(in HSteamNetConnection connection, ReadOnlySpan<byte> data, int sendFlag) {

            byte[] data_copied = data.ToArray();

            //create an unmanaged array and get a pointer to it
            GCHandle pinnedArray = GCHandle.Alloc(data_copied, GCHandleType.Pinned);
            IntPtr pData = pinnedArray.AddrOfPinnedObject();

            //send to the target
            EResult response;
            if (is_steamserver) {
                response = SteamGameServerNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data_copied.Length, sendFlag, out long _);
            } else {
                response = SteamNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data_copied.Length, sendFlag, out long _);
            }

            //free the unmanaged array
            pinnedArray.Free();


            if (response == EResult.k_EResultOK) {
                FindTheBugInator.NoteSteamOut(data_copied.Length);
            }

            if (response != EResult.k_EResultOK) {

                if (response == EResult.k_EResultNoConnection && !log.verbose) {
                    return;
                }


                var gfgfddfgdfg = connection.m_HSteamNetConnection;
                SteamConnectionData foundsteamid = connectionMapping.Values.FirstOrDefault(x => x.connection.m_HSteamNetConnection == (gfgfddfgdfg));

                string msgid_as_str = "";
#if !(RELEASE && !PLAYTEST)
                try {
                    NetDataReader read = new NetDataReader(data_copied);
                    byte msgid = read.GetByte();
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
            string plrid = "";
            if (Net.TryGetSteamTransport(out TransportSteamworks ttttt) && ttttt.SteamIDToClientIDDict.TryGet(steamid, out knetid plrdknetid)) {
                plrid = $", plrid:{plrdknetid}";
            }
            string tagstr = "";
            {
                List<string> tags = new List<string>();
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
            if (SteamIDToClientIDDict.TryGet(clientid, out ulong user_steamid)) {
                SendThroughSteamSocket(user_steamid, writer.AsReadOnlySpan(), steam_sendFlags);
            } else {
                if (log.verbose) {
                    Plugin.Logger.LogError($"SERVER: TransportSteamworks.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
                }
            }
        }
        public void Server_SendToClients(in int steam_sendFlags, in NetDataWriter writer, in IReadOnlyList<knetid> clientIds) {
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
            foreach (knetid cID in clientIds) {
                Server_SendTo(steam_sendFlags, writer, cID);
            }
        }



        public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid) {
            if (SteamIDToClientIDDict.TryGet(clientid, out ulong user_steamid)) {
                SendThroughSteamSocket(user_steamid, data, delivery);
            } else {
                if (log.verbose) {
                    Plugin.Logger.LogError($"SERVER: TransportSteamworks.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
                }
            }
        }
        public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in IReadOnlyList<knetid> clientIds) {
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
            foreach (knetid cID in clientIds) {
                Server_SendTo(delivery, data, cID);
            }
        }
        public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data) {
            SendThroughSteamSocket(KSteam.CURRENT_LOBBY.ownerID.m_SteamID, data, delivery);
        }





        public void FlushNagle() {
            SteamNetworkingSockets.FlushMessagesOnConnection(serverUser.connection);
            foreach (KeyValuePair<ulong, SteamConnectionData> item in connectionMapping) {
                SteamNetworkingSockets.FlushMessagesOnConnection(item.Value.connection);
            }
        }
        public void FlushNagle(knetid plrId) {
            if (SteamIDToClientIDDict.TryGet(plrId, out ulong steamId) && connectionMapping.TryGetValue(steamId, out SteamConnectionData connection)) {
                SteamNetworkingSockets.FlushMessagesOnConnection(connection.connection);
            }
        }


    }
}
