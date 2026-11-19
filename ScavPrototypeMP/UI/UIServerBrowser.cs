using CasualtiesTogetherUtils;
using KrokV5Optimization;
using Newtonsoft.Json.Linq;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.TextCore;
using static Together.KSteam;
using static Together.UIMainMenu;

namespace Together;



public static class UIServerBrowser {

    public static MenuTab menutab_serverbrowser = new MenuTab {
        name = "mmtb_serverbrowser",
        on_open = () => {
            if (KSteam.Loaded) {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                BrowserSteamPing.EnsureCallbacks();
            }
            if (CurServerList.Count == 0) {
                ReloadList();
            }
            ResetPingSamplerFast();
            _manualSortActive = false;
        },
        gui_func = _GUI_RenderServerBrowser,
    };

    public static string _LOBBY_SEARCH_FILTER = "";
    static Vector2 _SERVERBROWSERMENUSCROLL = Vector2.zero;

    public static bool SortHideIncompatible = false;
    public static bool SortHidePasswordProtected = true; // hide locked by default
    public static bool HideDescriptions = false;
    static bool SORT_FLIPBOOL_alpha = false;
    static bool SORT_FLIPBOOL_count = false;
    static bool SORT_FLIPBOOL_layer = false;
    static bool SORT_FLIPBOOL_ping = false;
    static bool _manualSortActive = false;

    const float PingSampleIntervalFast = 0.2f; // 5 real probes / second until first pass done
    const float PingSampleIntervalSlow = 1f;   // then 1 probe / second
    static float _pingSampleAccum;
    static int _pingSampleIndex;
    static bool _pingFirstPassDone;
    static int _pingSortFingerprint = int.MinValue;
    // rows drawn this frame. layout fills these, repaint reuses them.
    // the bools are the bits that add or skip a control.
    struct BrowserRowShape {
        public bool badVersion;
        public bool friendIcon;
        public bool locked;
        public bool steam;
        public bool customMode;
        public bool listPlayers;
        public bool listMods;
        public bool showDesc;
        public bool showLayer;
        public bool showMood;
        public string mood;
    }
    static NetPublicServerInfo[] _browserRows = Array.Empty<NetPublicServerInfo>();
    static BrowserRowShape[] _rowShapes = Array.Empty<BrowserRowShape>();

    static void CaptureBrowserRows() {
        int n = CurFilteredServerList.Count;
        var rows = new List<NetPublicServerInfo>(n);
        var shapes = new List<BrowserRowShape>(n);
        string ver = Multiplayer.FULL_VERSION_TAG;
        bool hideDesc = HideDescriptions;
        for (int i = 0; i < n; i++) {
            NetPublicServerInfo lobby = CurFilteredServerList[i];
            if (lobby == null) {
                continue;
            }
            BrowserRowShape shape = new BrowserRowShape();
#if RELEASE
            shape.badVersion = lobby.version != ver;
#endif
            shape.friendIcon = lobby.friends_tooltip != null;
            shape.locked = lobby.haspassword;
            shape.steam = lobby.steam_lobby_info != null;
            shape.listPlayers = lobby.plr_count > 0 && shape.steam;
            shape.listMods = lobby.modlist != null && lobby.modlist.Length > 0;
            shape.showDesc = !hideDesc && !string.IsNullOrWhiteSpace(lobby.description);
            shape.showLayer = lobby.cur_layer != -1;
            int clayer1 = lobby.cur_layer + 1;
            bool hasTitle = Locale.currentLang != null && Locale.currentLang.other != null && Locale.currentLang.other.TryGetValue("layertitle" + clayer1, out _);
            if (hasTitle && shape.showLayer && lobby.gamemode != "tutorial") {
                shape.customMode = lobby.gamemode != "debug_world" && lobby.gamemode != "loading" && lobby.gamemode != "vanilla";
            }
            if (shape.showLayer && Locale.currentLang != null && Locale.currentLang.other != null) {
                int num = Mathf.RoundToInt(lobby.avg_happiness * 0.1f);
                if (Locale.currentLang.other.TryGetValue("moodrange" + num.ToString(), out var moodrange)) {
                    shape.showMood = true;
                    shape.mood = moodrange;
                }
            }
            rows.Add(lobby);
            shapes.Add(shape);
        }
        _browserRows = rows.ToArray();
        _rowShapes = shapes.ToArray();
    }

    static void ResetPingSamplerFast() {
        _pingSampleIndex = 0;
        _pingSampleAccum = 0f;
        _pingFirstPassDone = false;
        _pingSortFingerprint = int.MinValue;
        BrowserSteamPing.CancelAll();
    }

    static int ComputePingSortFingerprint() {
        int known = 0;
        int sum = 0;
        for (int i = 0; i < CurFilteredServerList.Count; i++) {
            NetPublicServerInfo info = CurFilteredServerList[i];
            if (info == null || !info.HasPing) {
                continue;
            }
            known++;
            sum += info.pingMs;
        }
        return (known * 397) ^ sum;
    }

    static void TickPingSampler() {
        if (!KSteam.Loaded) {
            return;
        }

        BrowserSteamPing.Tick();

        int fingerprint = ComputePingSortFingerprint();
        if (fingerprint != _pingSortFingerprint) {
            _pingSortFingerprint = fingerprint;
            if (!_manualSortActive) {
                SortServerListByJoinability();
            }
        }

        float interval = _pingFirstPassDone ? PingSampleIntervalSlow : PingSampleIntervalFast;
        _pingSampleAccum += Time.unscaledDeltaTime;
        if (_pingSampleAccum < interval) {
            return;
        }
        _pingSampleAccum = 0f;

        int count = CurFilteredServerList.Count;
        if (count <= 0) {
            _pingSampleIndex = 0;
            return;
        }

        // one new ConnectP2P probe per tick; walk the filtered list in order
        for (int attempt = 0; attempt < count; attempt++) {
            if (_pingSampleIndex >= count) {
                _pingSampleIndex = 0;
                if (!_pingFirstPassDone) {
                    _pingFirstPassDone = true;
                    return; // first pass finished - next probes use the slow interval
                }
            }

            NetPublicServerInfo info = CurFilteredServerList[_pingSampleIndex++];
            if (BrowserSteamPing.TryBeginProbe(info)) {
                return;
            }
            if (BrowserSteamPing.InFlightCount >= BrowserSteamPing.MaxConcurrent) {
                // back up so we retry this slot once a probe frees
                _pingSampleIndex = Math.Max(0, _pingSampleIndex - 1);
                return;
            }
        }
    }

    static void _GUI_RenderServerBrowser(Rect r) {
        // ping sort and steam updates change the list between layout and repaint.
        // imgui then asks for a control the group never recorded.
        if (Event.current == null || Event.current.type == EventType.Layout) {
            TickPingSampler();
            CaptureBrowserRows();
        }

        GUI.skin.label.alignment = TextAnchor.UpperCenter;
        //uIBullshit._GUI_BiggerLabel(Lang.Get("mmtb_serverbrowser"), 1.4f);
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
#if RELEASE
        if( !KSteam.Loaded ) {
            UIBullshit._GUI_BiggerLabel("Steam not loaded", 1.4f);
            return;
        }
#endif

        var fieldnameswidth = r.width * 0.12f;
        var fieldswidth = r.width * 0.26f;
        var bigheight = 50 * GetMenuUIScale();
        var fieldheight = 30 * GetMenuUIScale();

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        {
            {
                UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);

                GUILayout.BeginHorizontal();
                {
                    GUI.skin.label.alignment = TextAnchor.MiddleRight;

                    GUILayout.Label(Lang.Get("mmsb_sort"));

                    GUI.skin.label.alignment = TextAnchor.MiddleLeft;

                    if (GUILayout.Button(Lang.Get("mmsb_sort_ping"))) {
                        var mul = SORT_FLIPBOOL_ping ? -1 : 1;
                        CurFilteredServerList.Sort((a, b) => ComparePing(a, b) * mul);
                        SORT_FLIPBOOL_ping = !SORT_FLIPBOOL_ping;
                        _manualSortActive = true;
                    }
                    if (GUILayout.Button(Lang.Get("mmsb_sort_plrcount"))) {
                        var mul = SORT_FLIPBOOL_count ? 1 : -1;
                        CurFilteredServerList.Sort((a, b) => {
                            return a.plr_count.CompareTo(b.plr_count) * mul;
                        });
                        SORT_FLIPBOOL_count = !SORT_FLIPBOOL_count;
                        _manualSortActive = true;
                    }
                    if (GUILayout.Button(Lang.Get("mmsb_sort_layer"))) {
                        var mul = SORT_FLIPBOOL_layer ? -1 : 1;
                        CurFilteredServerList.Sort((a, b) => {
                            return a.cur_layer.CompareTo(b.cur_layer) * mul;
                        });
                        SORT_FLIPBOOL_layer = !SORT_FLIPBOOL_layer;
                        _manualSortActive = true;
                    }
                    if (GUILayout.Button(Lang.Get("mmsb_sort_name"))) {
                        var mul = SORT_FLIPBOOL_alpha ? -1 : 1;
                        CurFilteredServerList.Sort((a, b) => {
                            return a.name.CompareTo(b.name) * mul;
                        });
                        SORT_FLIPBOOL_alpha = !SORT_FLIPBOOL_alpha;
                        _manualSortActive = true;
                    }




                    GUILayout.FlexibleSpace();

                    GUILayout.Label(String.Format(Lang.Get("mmsb_lobbyfounds"), CurServerList.Count, CurFilteredServerList.Count, TotalPlayerCount));


                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10 * GetMenuUIScale());



            _SERVERBROWSERMENUSCROLL = GUILayout.BeginScrollView(_SERVERBROWSERMENUSCROLL, false, true);


            var ogfontsize = GUI.skin.label.fontSize;

            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            var ogalign = GUI.skin.label.alignment;
            GUI.skin.label.wordWrap = false;



            var i_am_privileged_user_idc_bout_passwords =
#if DEBUG
            true;
#else
            KSteam.Loaded && KnownPersons.IsSteamUserPrivileged(KSteam.GetLocalUserSteamID().m_SteamID);
#endif

            GUI.skin.label.alignment = TextAnchor.MiddleCenter;



            if (_browserRows.Length == 0) {
                GUILayout.Label(CurServerList.Count == 0 ? Lang.Get("mmsb_empty") : Lang.Get("filter_found_nothing"));
            } else {
                try {
                    GUI.skin.label.fontSize = ogfontsize;
                    var lobbyfontsize = (int)(ogfontsize);
                    var plrtabheight = _LOBBY_ENTRY_HEIGHT * GetMenuUIScale();
                    var og_enabled = GUI.enabled;
                    for (int ri = 0; ri < _browserRows.Length; ri++) {
                        var lobby = _browserRows[ri];
                        var shape = _rowShapes[ri];
                        GUI.skin.label.alignment = TextAnchor.UpperLeft;
                        GUILayout.BeginVertical(GUI.skin.box);
                        int rowH = 0;
                        try {
                            var is_a_steam_lobby = shape.steam;

                            GUI.skin.label.fontSize = lobbyfontsize;
#if RELEASE
                            if (shape.badVersion) {
                                GUI.enabled = false;
                                GUILayout.BeginHorizontal();
                                rowH++;
                                {
                                    GUILayout.Space(10 * GetMenuUIScale());

                                    GUILayout.Label(lobby.name);

                                    GUI.skin.label.alignment = TextAnchor.UpperCenter;
                                    GUILayout.Label(lobby.plr_count + "/" + lobby.plr_max);
                                }
                                GUILayout.EndHorizontal();
                                rowH--;

                                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                                GUI.skin.label.fontSize = (int)(GUI.skin.label.fontSize * 0.9f);
                                GUILayout.Label(Lang.Get("mmsb_lobbybadver") + " - " + lobby.version);
                                GUI.enabled = og_enabled;
                            } else
#endif
                            {
                                var clayer1 = lobby.cur_layer + 1;
                                var spr = GetResourceSprite("BiomeIcon/" + clayer1);



                                GUIContent gameStateContent;
                                bool shouldDrawLayerIcon = false;
                                {
                                    if (
                                        !Locale.currentLang.other.TryGetValue("layertitle" + clayer1, out var lobby_layer_localed) //OG: WorldGeneration.Start V6.0DEMO
                                        || lobby.cur_layer == -1
                                    ) {
                                        gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_inmenu"), 27);
                                    } else if (lobby.gamemode == "tutorial") {
                                        gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_loading"), 28);
                                    } else {
                                        shouldDrawLayerIcon = true;
                                        if (lobby.gamemode == "debug_world") {
                                            gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_debug_world"), 29);
                                        } else if (lobby.gamemode == "loading") {
                                            gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_loading"), 30);
                                        } else if (lobby.gamemode == "vanilla") {
                                            gameStateContent = FixGuiContent.Content(lobby_layer_localed, 31);
                                        } else if (true) {
                                            gameStateContent = FixGuiContent.Content(lobby.gamemode, 32);
                                        }
                                    }
                                }



                                GUILayout.BeginHorizontal();
                                rowH++;
                                {
                                    var conttt = FixGuiContent.Content(lobby.name, 33);
                                    var sizzzz = GUI.skin.label.CalcSize(conttt);

                                    GUILayout.Space(10 * GetMenuUIScale());

                                    GUI.skin.label.richText = true;
                                    GUILayout.Label(conttt);
                                    GUI.skin.label.richText = false;
                                    GUILayout.Space(10 * GetMenuUIScale());

                                    if (shape.friendIcon) {
                                        GUILayout.Label(CoopModAssets.happy.texture, GUILayout.Width(sizzzz.y), GUILayout.Height(sizzzz.y));

                                        if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                                            UIBullshit._GUI_SetTooltip(lobby.friends_tooltip, "");
                                        }
                                    }

                                    if (shape.locked) {
                                        GUILayout.Label(CoopModAssets.@lock.texture, GUILayout.Width(sizzzz.y), GUILayout.Height(sizzzz.y));

                                        if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                                            UIBullshit._GUI_SetTooltip(Lang.Get("mmsb_lobby_islocked"), "");
                                        }
                                    }

                                    if (is_a_steam_lobby) {
                                        GUILayout.Label(CoopModAssets.icon_steam.texture, GUILayout.Width(sizzzz.y), GUILayout.Height(sizzzz.y));
                                    }



                                    GUI.skin.label.alignment = TextAnchor.UpperCenter;
                                    GUILayout.Label(lobby.plr_count + "/" + lobby.plr_max);
                                    GUILayout.Label(FormatPing(lobby), GUILayout.Width(70 * GetMenuUIScale()));

                                    GUILayout.FlexibleSpace();

                                    if (shape.customMode) {
                                        GUILayout.Label(lobby.gamemode);
                                    }

                                    GUILayout.FlexibleSpace();

                                    if (true) {
                                        if (GUILayout.Button(Lang.Get("mmsb_lobby_listrules"))) {

                                        }
                                        DoTooltipOnLastGUIRectNoLang(lobby.rules_as_string);
                                    }
                                    if (shape.listPlayers) {
                                        if (GUILayout.Button(Lang.Get("mmsb_lobby_listplrs"))) {

                                        }
                                        if (lobby.steam_lobby_info != null) {
                                            DoTooltipOnLastGUIRectNoLang(String.Join("\n", lobby.steam_lobby_info.members.Select(x => KSteam.GetSteamUsername(x.Key.m_SteamID))));
                                        }
                                    }
                                    if (shape.listMods) {
                                        if (GUILayout.Button(Lang.Get("mmsb_lobby_listmod"))) {

                                        }
                                        if (lobby.modlist != null) {
                                            DoTooltipOnLastGUIRectNoLang(String.Join("\n", lobby.modlist));
                                        }
                                    }

                                }
                                GUILayout.EndHorizontal();
                                rowH--;


                                if (shape.showDesc) {
                                    GUILayout.BeginHorizontal();
                                    rowH++;
                                    {
                                        GUI.skin.label.richText = true;
                                        GUI.skin.label.wordWrap = true;
                                        GUI.skin.label.alignment = TextAnchor.UpperLeft;
                                        var gggogfontsize = GUI.skin.label.fontSize;
                                        var col = GUI.backgroundColor;
                                        GUI.color = Color.white * 0.9f;
                                        GUI.skin.label.fontSize = (int)(gggogfontsize * 0.8f);

                                        var content = FixGuiContent.Content(lobby.description ?? "", 34);

                                        var realwidth = 800 * GetMenuUIScale();
                                        var calculatedheight = GUI.skin.label.CalcHeight(content, realwidth);

                                        GUILayout.Label(content, GUILayout.Width(realwidth), GUILayout.Height(calculatedheight));

                                        GUI.skin.label.fontSize = gggogfontsize;
                                        GUI.color = col;

                                        GUILayout.Space(10 * GetMenuUIScale());

                                        GUI.skin.label.richText = false;
                                        GUI.skin.label.wordWrap = false;
                                    }
                                    GUILayout.EndHorizontal();
                                    rowH--;
                                }





                                GUILayout.BeginHorizontal();
                                rowH++;
                                {
                                    {
                                        GUI.enabled = og_enabled && (!Multiplayer.IsNetworkActiveOrIsInGame()) && (lobby.IsJoinable() || i_am_privileged_user_idc_bout_passwords);
                                        if (
                                            GUILayout.Button(
                                                Lang.Get(lobby.latejoinspectate ? "mmsb_lobbyjoinspectate" : "mmsb_lobbyjoin")
                                            , GUILayout.Width(r.width * 0.2f))
                                        ) {
                                            if (is_a_steam_lobby && lobby.steam_lobby_info != null) {
                                                Multiplayer.DoMultiplayerStatusMessageLog($"STEAM: Join Lobby {lobby.steam_lobby_info.lobby_steamID}");
                                                TransportSteamworks.OnWantToJoinLobby(lobby.steam_lobby_info.lobby_steamID.m_SteamID);

                                                UIMainMenu.current_tab = UIMainMenu.menutab_directconnect;
                                                UIMainMenu._DO_SERVER_CONFIG = false;
                                            } else {
                                                log.error("TODO: JOIN TO IP");

                                                //tODO
                                                //tODO
                                                //tODO
                                                //tODO
                                                //tODO
                                                //tODO
                                                //tODO
                                                //tODO
                                                //tODO
                                            }
                                        }
                                        if (Net.IsRunning) {
                                            DoTooltipOnLastGUIRect("mmsb_lobbyjoin_alreadyjoined");
                                        } else if (lobby.midjoin_lock_active) {
                                            DoTooltipOnLastGUIRect("mmsb_lobbyjoin_nomidjoin");
                                        } else if (lobby.enforcemodlist_locked) {
                                            DoTooltipOnLastGUIRect("mmsb_lobbyjoin_wrongmods");
                                        }
                                        GUI.enabled = og_enabled;
                                    }

                                    var uiscale = 10 * GetMenuUIScale();

                                    // draw cur layer info
                                    {
                                        GUI.skin.label.alignment = TextAnchor.UpperLeft;
                                        GUILayout.Space(uiscale);


                                        GUILayout.Label(gameStateContent);

                                        if (shouldDrawLayerIcon) {
                                            var rect = (GUILayoutUtility.GetLastRect());

                                            if (spr != null) {
                                                var size = GUI.skin.label.CalcSize(gameStateContent);

                                                GUI.DrawTexture(rect, spr.texture);
                                            }
                                            GUI.Label(rect, gameStateContent);
                                        }
                                    }
                                    GUI.skin.label.fontSize = (int)(GUI.skin.label.fontSize * 0.9f);

                                    if (shape.showLayer) {
                                        GUI.skin.label.alignment = TextAnchor.MiddleCenter;

                                        if (shape.showMood) {
                                            GUILayout.Label(Lang.Get("mmsb_lobby_avgmood") + shape.mood);
                                        }

                                        GUILayout.Label(Lang.Get("mmsb_lobby_alivecount") + (lobby.plr_living_count + "/" + lobby.plr_count));
                                    }

                                    GUILayout.Space(uiscale);

                                    GUI.skin.label.alignment = TextAnchor.LowerRight;
                                    GUILayout.Label("V" + lobby.version);

                                    GUILayout.Space(uiscale);

                                }
                                GUILayout.EndHorizontal();
                                rowH--;



                            }

                        } catch (Exception e) {
                            while (rowH > 0) {
                                GUILayout.EndHorizontal();
                                rowH--;
                            }
                            log.error($"_GUI_RenderServerBrowser: {lobby}\n{e.ToString()}");
                        }
                        GUI.enabled = og_enabled;
                        GUILayout.EndVertical();
                    }
                } catch (Exception e) {
                    log.error($"_GUI_RenderServerBrowser: RENDER: \n{e.ToString()}");
                }
            }
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);

            GUI.skin.label.fontSize = ogfontsize;
            GUI.skin.label.alignment = ogalign;

            GUILayout.Label("");
            GUILayout.EndScrollView();
        }
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(190 * GetMenuUIScale()));
        {

            GUILayout.BeginHorizontal();
            {
                if (KSteam.Loaded) {
                    GUILayout.Label(Lang.Get("mmsb_steam_distfilter"));
                    UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
                    KSteam.userselected_lobbyDistanceFilter = (ELobbyDistanceFilter)GUILayout_DropdownMenu.Dropdown((int)KSteam.userselected_lobbyDistanceFilter, new string[] {
                        Lang.Get("mmsb_steam_dist0"),
                        Lang.Get("mmsb_steam_dist1"),
                        Lang.Get("mmsb_steam_dist2"),
                        Lang.Get("mmsb_steam_dist3"),
                    });
                    UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
                }

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();


            GUILayout.BeginHorizontal();
            {
                GUI.skin.label.alignment = TextAnchor.MiddleRight;
                GUILayout.Label(Lang.Get("mmct_search"));
                // max players
                {
                    var newnamefilter = GUILayout.TextField(_LOBBY_SEARCH_FILTER, GUILayout.Width(fieldswidth));
                    if (newnamefilter != _LOBBY_SEARCH_FILTER) {
                        _LOBBY_SEARCH_FILTER = newnamefilter;
                        RefilterList();
                    }
                }
            }
            GUILayout.EndHorizontal();


            UIMainMenu._GUI___DirectConnect_DoPasswordField(r, fieldheight, fieldnameswidth * 0.9f, fieldswidth * 0.5f);


            {
                var oldPass = SortHidePasswordProtected;
                SortHidePasswordProtected = GUILayout.Toggle(SortHidePasswordProtected, Lang.Get("mmsb_lobbyhidepass"));
                if (oldPass != SortHidePasswordProtected) {
                    RefilterList();
                }
            }

            {
                var oldImcomp = SortHideIncompatible;
                SortHideIncompatible = GUILayout.Toggle(SortHideIncompatible, Lang.Get("mmsb_lobbyhidever"));
                if (oldImcomp != SortHideIncompatible) {
                    RefilterList();
                }
            }

            {
                var old = HideDescriptions;
                HideDescriptions = GUILayout.Toggle(HideDescriptions, Lang.Get("mmsb_lobbyhidedesc"));
                if (old != HideDescriptions) {

                }
            }


            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            {
                GUILayout.Space(20);
                {
                    if (GUILayout.Button("  " + Lang.Get("mmsb_refresh") + "  ")) {
                        Util.PlayUISound(PlayerCamera.UISoundType.Click);
                        ReloadList();
                    }
                }

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
        }

        GUILayout.EndVertical();
        GUILayout.EndHorizontal();


    }
    public static int _LOBBY_ENTRY_HEIGHT = 90;
    public static int _LOBBY_ERROR_HEIGHT = 30;


    static readonly Dictionary<string, Sprite> ResourceSprites = new();
    public static Sprite GetResourceSprite(in string str) {
        try {
            ResourceSprites[str] = Resources.Load<Sprite>(str);
        } catch (Exception ex) {
#if !RELEASE
            log.error("GetResourceSprite: " + ex.ToString());
#endif
            ResourceSprites[str] = null;
        }

        return ResourceSprites[str];
    }




    internal static readonly Dictionary<ulong, NetPublicServerInfo> CurServerList_ButOnlySteamLobbies = new();
    internal static List<NetPublicServerInfo> CurServerList = new();
    internal static List<NetPublicServerInfo> CurFilteredServerList = new();
    public static int CurServerCount => CurServerList.Count;
    static int TotalPlayerCount = 0;

    public static void AddServerToTheList(NetPublicServerInfo info) {
        ulong id = info.steam_lobby_info != null ? info.steam_lobby_info.lobby_steamID.m_SteamID : 0;

        if (info.steam_lobby_info != null) {

            if (CurServerList_ButOnlySteamLobbies.TryGetValue(info.steam_lobby_info.lobby_steamID.m_SteamID, out var l)) {
                Together.log.warn($"[LobbyBrowser] DROP-DUP id={id} name=\"{info.name}\"");
                return; //  lobby already exists
            } else {
                CurServerList_ButOnlySteamLobbies[info.steam_lobby_info.lobby_steamID.m_SteamID] = info;
            }

            foreach (var friendId in KSteam.Friends) {
                if (info.steam_lobby_info.members.ContainsKey((CSteamID)friendId) && !info.steamfriends.Contains(friendId)) {
                    info.steamfriends.Add(friendId);
                    if (info.friends_tooltip == null) {
                        info.friends_tooltip = Lang.Get("mmsb_steam_lobbyfriend");
                    }
                    info.friends_tooltip += '\n' + SteamFriends.GetFriendPersonaName((CSteamID)friendId);
                }
            }

        }

        TotalPlayerCount += info.plr_count;
        CurServerList.Add(info);

        string filterDrop;
        if (TestLobbySearchFilters(info, out filterDrop)) {
            var is_joinable = info.IsJoinable();
            if (is_joinable) {
                CurFilteredServerList.Insert(0, info);
            } else {
                CurFilteredServerList.Add(info);
            }
            Together.log.warn($"[LobbyBrowser] UI-SHOW id={id} name=\"{info.name}\" ver=\"{info.version}\" joinable={is_joinable} filtered={CurFilteredServerList.Count}/{CurServerList.Count}");
        } else {
            Together.log.warn($"[LobbyBrowser] DROP-FILTER id={id} name=\"{info.name}\" ver=\"{info.version}\" reason={filterDrop}");
        }
    }
    public static bool TestLobbySearchFilters(NetPublicServerInfo info) {
        return TestLobbySearchFilters(info, out _);
    }
    // crimson ascent (10) and end lab (11). keep out of the public browser when a run is there.
    public const int AscentLayerDepth = 10;
    public const int LabLayerDepth = 11;

    public static bool TestLobbySearchFilters(NetPublicServerInfo info, out string dropReason) {
        dropReason = null;
        if (info != null && info.cur_layer >= AscentLayerDepth) {
            dropReason = $"ascent-lab-layer-blacklist layer={info.cur_layer}";
            return false;
        }
        if (NsfwContentFilter.IsListingBlocked(info.name, info.description)) {
            dropReason = "nsfw-filter";
            return false;
        }
        if (!String.IsNullOrEmpty(_LOBBY_SEARCH_FILTER)) {
            if (info.name.ContainsInsensitive(_LOBBY_SEARCH_FILTER)
                || (SearchPatternIsLobbyId && info.steam_lobby_info != null && info.steam_lobby_info.lobby_steamID.m_SteamID == SearchPatternLobbyId)
            ) {
                // passes the test
            } else {
                dropReason = $"search-filter \"{_LOBBY_SEARCH_FILTER}\"";
                return false;
            }
        }
        if (SortHidePasswordProtected) {
            if (info.haspassword) {
                dropReason = "hide-password";
                return false;
            }
        }
        if (SortHideIncompatible) {
            if (info.version != Multiplayer.FULL_VERSION_TAG) {
                dropReason = $"hide-incompatible lobbyVer=\"{info.version}\" myVer=\"{Multiplayer.FULL_VERSION_TAG}\"";
                return false;
            }
        }
        return true;
    }


    static bool SearchPatternIsLobbyId;
    static ulong SearchPatternLobbyId;

    public static void RefilterList() {

        var listclone = new List<NetPublicServerInfo>(CurServerList);

        ClearLists();

        foreach (var item in listclone) {
            AddServerToTheList(item);
        }

        _pingSampleIndex = 0;
        _manualSortActive = false;
        _pingSortFingerprint = int.MinValue;
        SortServerListByJoinability();
    }
    static int ComparePing(NetPublicServerInfo a, NetPublicServerInfo b) {
        int ap = a != null && a.HasPing ? a.pingMs : int.MaxValue;
        int bp = b != null && b.HasPing ? b.pingMs : int.MaxValue;
        return ap.CompareTo(bp);
    }

    static string FormatPing(NetPublicServerInfo lobby) {
        if (lobby == null || !lobby.HasPing) {
            return "— ms";
        }
        return lobby.pingMs + " ms";
    }

    public static void SortServerListByJoinability() {
        CurFilteredServerList.Sort((a, b) => {
            {
                var afriend = (a.friends_tooltip != null);
                var bfriend = (b.friends_tooltip != null);

                var comp = bfriend.CompareTo(afriend); // friends first

                if (comp != 0) return comp;
            }
            {
                // fastest ping first; unknown ping sorts last
                var comp = ComparePing(a, b);
                if (comp != 0) return comp;
            }
            {
                var aver = (a.version != Multiplayer.FULL_VERSION_TAG);
                var bver = (b.version != Multiplayer.FULL_VERSION_TAG);

                var comp = aver.CompareTo(bver);

                if (comp != 0) return comp;
            }
            {
                var ajoinable = !(a.IsJoinable());
                var bjoinable = !(b.IsJoinable());

                var comp = ajoinable.CompareTo(bjoinable);

                if (comp != 0) return comp;
            }
            {
                var apass = !(a.haspassword);
                var bpass = !(b.haspassword);

                var comp = apass.CompareTo(bpass);

                if (comp != 0) return comp;
            }

            return 0;
        });
    }

    public static void ClearLists() {

        CurServerList_ButOnlySteamLobbies.Clear();
        CurServerList.Clear();
        CurFilteredServerList.Clear();
        TotalPlayerCount = 0;

        SearchPatternIsLobbyId = ulong.TryParse(_LOBBY_SEARCH_FILTER, out SearchPatternLobbyId);
        if (SearchPatternIsLobbyId) {
            SearchPatternIsLobbyId = ((CSteamID)SearchPatternLobbyId).IsLobby();
        }

    }


    public static void ReloadList() {
        SORT_FLIPBOOL_alpha = false;
        SORT_FLIPBOOL_count = false;
        SORT_FLIPBOOL_layer = false;
        SORT_FLIPBOOL_ping = false;
        _manualSortActive = false;
        ResetPingSamplerFast();

        ClearLists();
        KSteam.ClearPendingBrowserLobbyData();

        if (KSteam.Loaded) {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            KSteam.SearchLobbies(false);
        }

        //tODO litenetlib LAN scan



#if !RELEASE
        AddServerToTheList(new NetPublicServerInfo {
            name = "TEST DONT CLICK",
            description = "TEST DESCRIPTION ",
            plr_count = 67,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby()
        });
        AddServerToTheList(new NetPublicServerInfo {
            name = "1TEST <color=red>DONT</color> CLICK",
            description = Multiplayer.SanitizeRichTextAllowSimpleTags("Hello <b>bold</b> <i>italic</i> <u>underline</u> <s>strike</s> <color=red>red</color> <color=#FF0000>hex</color> <color=#FF0000FF>rgba</color> <size=20>size</size> <size=999999>huge</size> <size=-999>negative</size> <size=abc>invalid</size> <font=ComicSans>font</font> <material=999>material</material> <sprite=999999>sprite</sprite> <link=\"https://evil.example\">link</link> <style=\"evil\">style</style> <mark=#FF0000>mark</mark> <noparse><b>nested noparse</b></noparse> <br> <align=center>alignment</align> <voffset=999>offset</voffset> <space=999>space</space> <width=999>width</width> <indent=999>indent</indent> <line-height=999>line</line-height> <cspace=999>spacing</cspace> <mspace=999>mono</mspace> <rotate=999>rotate</rotate> <pos=999>position</pos> <gradient=\"evil\">gradient</gradient> <unknown>KEEP THIS TEXT</unknown> <unknown><b>KEEP <i>ALL</i> TEXT</b></unknown> Hello bold italic underline strike red hex rgba size huge negative invalid font material sprite link style mark nested noparse alignment offset space width indent line spacing mono rotate position gradient KEEP THIS TEXT KEEP ALL TEXT "),
            plr_count = 13,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby { lobby_steamID = (CSteamID)1 }
        });
        AddServerToTheList(new NetPublicServerInfo {
            name = "2TEST DONT CLICK",
            description = Multiplayer.SanitizeRichTextAllowSimpleTags("TEST DESCRIPTION <size=500>WAY TOO BIG</size> <someDangerousTag>text</someDangerousTag>"),
            plr_count = 583,
            haspassword = true,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby { lobby_steamID = (CSteamID)2 }
        });
        AddServerToTheList(new NetPublicServerInfo {
            name = "3TEST DONT CLICK",
            description = Multiplayer.SanitizeRichTextAllowSimpleTags("PLAIN <b>BOLD</b> <i>ITALIC</i> <u>UNDERLINE</u> <s>STRIKETHROUGH</s> <b><i><u><s>NESTED</s></u></i></b> <color=red>RED</color> <color=#00FF00>GREEN</color> <color=#FF00FFFF>RGBA</color> <size=50>BIG TEXT</size> <size=9999>HUGE TEXT</size> <align=center>ALIGN CENTER</align> <align=right>ALIGN RIGHT</align> <font=Arial>FONT</font> <font-weight=900>WEIGHT</font-weight> <material=1>MATERIAL</material> <mark=#FFFF00>HIGHLIGHT</mark> <noparse><b>NOT PARSED</b></noparse> <nobr>NO BREAK</nobr> <link=\"https://example.com\">LINK</link> <sprite=0>SPRITE</sprite> <sprite name=\"Icon\">SPRITE NAME</sprite> <style=\"H1\">STYLE</style> <br>LINE BREAK <voffset=10>OFFSET</voffset> <space=20>SPACE</space> <width=100>WIDTH</width> <indent=20>INDENT</indent> <line-height=50>LINE HEIGHT</line-height> <cspace=10>CHAR SPACE</cspace> <mspace=10>MONO SPACE</mspace> <uppercase>UPPERCASE</uppercase> <lowercase>LOWERCASE</lowercase> <smallcaps>SMALL CAPS</smallcaps> <rotate=45>ROTATE</rotate> <pos=50>POSITION</pos> <gradient=\"MyGradient\">GRADIENT</gradient> <someUnknownTag>UNKNOWN TAG</someUnknownTag> <script>alert('test')</script> <dangerous=whatever>SHOULD STAY</dangerous=whatever> END dsadfdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            plr_count = 0,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby { lobby_steamID = (CSteamID)3 }
        });
#endif

    }







}