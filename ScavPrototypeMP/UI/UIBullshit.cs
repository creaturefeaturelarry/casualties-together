using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together {



    public class UIBullshit : CasualtiesTogetherSingleton {
        public static UIBullshit main { get; private set; }

        internal ECGVisualizer egg;

        void Awake() {
            if (main != null) {
                throw new Exception("man wtf is this bro");
            }
            main = this;

        }

        public static string net_debug_msg = "";

        void SecondUpdate() {

            {
                net_debug_msg = Net.GetDebugStatsString();
            }

            float n = 0;
            float packetStability = 0;
            if (ScavPlayer.ClientIdToPlayerDict.Count > 0) {
                foreach (ScavPlayer item in ScavPlayer.AllPlayers) {
                    n += (float)(item.ping * 1000);
                }
                n /= ScavPlayer.ClientIdToPlayerDict.Count;

            }
            {
                int totalStatuses = 0;
                if (Net.IsServer) {
                    if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                        var me = KSteam.GetLocalUserSteamID().m_SteamID;
                        foreach (KeyValuePair<ulong, TransportSteamworks.SteamConnectionData> item in tsteam.connectionMapping) {
                            if (item.Key == me) { continue; }
                            var qual = item.Value.connectionStatus.m_flConnectionQualityLocal;
                            packetStability += qual;
                            totalStatuses += 1;
                        }
                    } else {
                        if (Net.Transport is TransportLiteNetLib tlnl) {
                            List<NetPeer> peers = new List<NetPeer>();
                            tlnl.NetManager.GetConnectedPeers(peers);
                            foreach (NetPeer item in peers) {
                                float qual = (float)item.Statistics.PacketLossPercent * 0.01f;
                                packetStability += Mathf.Clamp01(1f - qual);
                                totalStatuses += 1;
                            }
                        }
                    }
                }
                if (totalStatuses > 0) {
                    packetStability /= totalStatuses;
                } else {
                    packetStability = 1;
                }
            }

            ServerMain.AVERAGE_PING = n;
            if (Net.IsServer) {
                ServerMain.AVG_CONNECTION_QUALITY = (byte)Mathf.Clamp(packetStability * 100f, 0, 100);
                if (Net.IsClient) {
                    ServerMain.AVG_CONNECTION_QUALITY = ClientMain.MY_CONNECTION_QUALITY;
                } else {
                    ClientMain.MY_CONNECTION_QUALITY = ServerMain.AVG_CONNECTION_QUALITY;
                }
            } else {
                ServerMain.AVG_CONNECTION_QUALITY = ClientMain.MY_CONNECTION_QUALITY;
            }


            Application.runInBackground = Net.IsRunning || !Util.IsWorldGenerated() || UIMainMenu.IsOpen();

        }

        //nah
        //static string ALERT_STR = "";
        //static double ALERT_TIME = -1000;
        //public static void DoAlert(in string alertstr) {
        //    aLERT_TIME = Time.realtimeSinceStartupAsDouble;
        //    aLERT_STR = alertstr; 
        //}

        static Vector2 last_screensize = Vector2.zero;
        static Dictionary<Texture2D, Dictionary<Vector2, Texture2D>> cached_modified_textures = new();
        static Dictionary<Texture2D, Texture2D> cached_scaled_textures = new();


        static Texture2D TexDarkener(Texture2D t, float m, float alpha) {
            Texture2D ddddd = new Texture2D(
                t.width,
                t.height,
                TextureFormat.RGBA32,//t.format,
                t.mipmapCount > 1
            );
            ddddd.filterMode = FilterMode.Point;

            Color[] pixels = t.GetPixels();
            for (int i = 0; i < pixels.Length; i++) {
                Color c = pixels[i];
                float a = c.a;
                c = c * m;
                c.a = a * alpha;
                pixels[i] = c;
            }

            ddddd.SetPixels(pixels);
            ddddd.Apply();
            return ddddd;
        }
        static Texture2D TexScaler(Texture2D t, float s) {

            Texture2D ddddd = new Texture2D(
                (int)(Mathf.Ceil(t.width * s)),
                (int)(Mathf.Ceil(t.height * s)),
                //(int)(Mathf.Ceil(t.width * s * 0.5f) * 2f),
                //(int)(Mathf.Ceil(t.height * s * 0.5f) * 2f),
                TextureFormat.RGBA32,//t.format,
                t.mipmapCount > 1
            );
            ddddd.filterMode = FilterMode.Point;


            float ratiox = t.width / (float)ddddd.width;
            float ratioy = t.height / (float)ddddd.height;

            Color[] og_pixels = t.GetPixels();
            Color[] pixels = ddddd.GetPixels();
            for (int x = 0; x < ddddd.width; x++) {
                for (int y = 0; y < ddddd.height; y++) {
                    pixels[x + y * ddddd.width] = og_pixels[Mathf.FloorToInt(x * ratiox) + Mathf.FloorToInt(y * ratioy) * t.width];
                }
            }

            ddddd.SetPixels(pixels);
            ddddd.Apply();
            return ddddd;
        }
        public static Vector2 ScaleSize(in float x, in float y) {
            Vector2 v = new Vector2(x, y);
            v *= uiScale;
            return v;
        }
        public static Rect ScaleRect(float x, float y, float width, float height, bool check_overlap = false) {
            x *= uiScale;
            y *= uiScale;
            width *= uiScale;
            height *= uiScale;
            if (check_overlap) {
                return ConstructorCheckCursorOverlap(x, y, width, height);
            } else {
                return new Rect(x, y, width, height);
            }
        }
        public static Rect ScaleRectButNotPos(in float x, in float y, float width, float height, bool check_overlap = false) {
            width *= uiScale;
            height *= uiScale;
            if (check_overlap) {
                return ConstructorCheckCursorOverlap(x, y, width, height);
            } else {
                return new Rect(x, y, width, height);
            }
        }
        public static Vector2 GetScreenSize() {
            return new Vector2(Screen.width, Screen.height);
        }
        public static Vector2 GetHalfScreenSize() {
            return GetScreenSize() * 0.5f;
        }

        public static Texture2D GetChangedTex(Texture2D og, float brightness, float alpha) {
            if (og == null) {
                return null;
            }
            Texture2D t = GetScaledTex(og);
            Vector2 v2 = new Vector2(brightness, alpha);

            if (!cached_modified_textures.ContainsKey(og)) {
                cached_modified_textures[og] = new();
            }
            Texture2D result;
            if (!cached_modified_textures[og].TryGetValue(v2, out result)) {
                result = TexDarkener(t, brightness, alpha);
                cached_modified_textures[og][v2] = result;
            }
            return result;
        }

        public static Texture2D GetScaledTex(Texture2D original) {
            Texture2D result;
            if (!cached_scaled_textures.TryGetValue(original, out result)) {
                result = TexScaler(original, uiScaleForMostUI);
                cached_scaled_textures[original] = result;
            }
            return result;
        }


        // the skin still points at the old textures until it is rebuilt, so they are destroyed after that.
        static readonly List<Texture2D> retired_textures = new();

        public static void ResizeGUITextures() {
            foreach (Dictionary<Vector2, Texture2D> variants in cached_modified_textures.Values) {
                retired_textures.AddRange(variants.Values);
            }
            retired_textures.AddRange(cached_scaled_textures.Values);
            cached_modified_textures.Clear();
            cached_scaled_textures.Clear();

            last_screensize = Util.GetResolution();

            _recalculate_skin = true;
        }
        static bool _recalculate_skin = false;


        public static Canvas canvas {
            get {
                if (Util.IsInMainMenu()) {
                    return PreRunScript.instance.GetComponent<Canvas>();
                } else {
                    return PlayerCamera.main.mainCanvas;
                }
            }
        }

        [SettingDeclarerThingyBool("setting_ui_discordrpc_allowjoin")]
        public static bool DISCORD_ALLOWJOINBUTTON = true;
        [SettingDeclarerThingyBool("setting_ui_discordrpc_onlyask")]
        public static bool DISCORD_ONLYASKTOJOINBUTTON = false;

        [SettingDeclarerThingyBool("setting_ui_talker_species_font")]//TODO Change this, This should only change body talker font, regardless of UNPIXELIZE_TALKER_FONT it will change both original and a copy
        public static bool USE_TALKER_SPECIES_FONT = false;
        // off keeps the species talker fonts. lives on the multiplayer user-interface tab.
        [SettingDeclarerThingyBool("setting_ui_defaulttalkerfont")]
        public static bool USE_DEFAULT_TALKER_FONT = false;
        //[SettingDeclarerThingyBool("setting_ui_unpixelize_talker_font")]//TODO Implement this, This should only create the duplicate body talker text in GUI canvas
        //public static bool UNPIXELIZE_TALKER_FONT = true;

        [SettingDeclarerThingyBool("setting_ui_usegamefont")]
        public static bool USE_GAME_FONT = true;

        //[SettingDeclarerThingyBool("setting_ui_modernui")] //V6
        public static bool USE_NEW_UI = false;

        public static float CUSTOM_UI_SCALE_FOR_UIBULLSHIT = 1;
        public static float DEFAULT_RETROGAME_FONTSIZE = 20;
        public static float THING_FOR_UI_SCALE = 3;// its that  m_PixelsToUnits: 33.3

        public static float uiScale = 1;
        public static float uiScaleMultiplier = 1;
        public static float uiScaleForMostUI = 1;
        public static int uiBlockSmallBorderSize = 3;
        public static int uiBlockNanoBorderSize = 2;
        //referencepixelsperunit

        // 9-sliced images

        void Start() {
            WorldMP.OnWorldgenFinish += WorldgenPatches_OnWorldgenFinish;

            InvokeRepeating(nameof(this.SecondUpdate), 0.5f, 1);
        }

        private void WorldgenPatches_OnWorldgenFinish() {
            last_screensize = Vector2.zero;//to reset ts
        }

        void Update() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Ui);

            if (UIBullshit.main.egg != null) { // i need speed multiplier
                UIBullshit.main.egg.timeToUpdate += Time.unscaledDeltaTime;
            }

            if (IS_CURSOR_OVERLAPPING) {
                GlobalDark.main.SetTooltip(("", ""));
            }

            try {
                uiScale = canvas.scaleFactor;
                uiScaleMultiplier = uiScale * CUSTOM_UI_SCALE_FOR_UIBULLSHIT;
                uiScaleForMostUI = uiScaleMultiplier * THING_FOR_UI_SCALE;
                uiBlockSmallBorderSize = Mathf.CeilToInt(uiScaleForMostUI * 4f); // should be 3 but whatever
                uiBlockNanoBorderSize = Mathf.CeilToInt(uiScaleForMostUI * 2f); // should be 1 but whatever

            } catch (Exception e) {
                log.error("UIBULLSHIT 1 " + e.ToString());
            }

            if (Input.GetKeyDown(KeyBinds.GetBind("pause"))) {
                if (PauseHandler.main != null && PauseHandler.main.disallowPause && !UIMainMenu.IsOpen()) {
                    UIMainMenu.SetOpen(true);
                } else {
                    UIMainMenu.SetOpen(false);
                }
            }

            if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
                return;
            }

            UIInGame ui = PlayerCamera.main.GetOrAddComponent<UIInGame>();

            ScavPlayer localsci = ScavPlayer.LocalPlayer;
            if (localsci != null) {

            }

        }
        void LateUpdate() {
            UIMainMenu.USERINPUT_NAME = Multiplayer.SanitizeTextInput(UIMainMenu.USERINPUT_NAME);


            if (_reset_gui_textures_next_frame) {
                _reset_gui_textures_next_frame = false;
                ResizeGUITextures();
            }
            if (last_screensize != Util.GetResolution()) {
                _reset_gui_textures_next_frame = true;
            }

            IS_CURSOR_OVERLAPPING = (TOTAL_FOCUSED_INTERACTIVE_UI > 0);
            TOTAL_INTERACTIVE_UI = 0;
            TOTAL_FOCUSED_INTERACTIVE_UI = 0;
        }


        static int TOTAL_INTERACTIVE_UI = 0;
        static int TOTAL_FOCUSED_INTERACTIVE_UI = 0;
        public static bool IS_CURSOR_OVERLAPPING = false;

        public static bool CheckCursorOverlap(in Rect rect) {
            TOTAL_INTERACTIVE_UI += 1;
            if (UIBullshit.IsCursorInGUIRect(rect)) {
                IS_CURSOR_OVERLAPPING = true;
                TOTAL_FOCUSED_INTERACTIVE_UI += 1;
                return true;
            }
            return false;
        }
        public static Rect HollowCheckCursorOverlap(in Rect rect) {
            CheckCursorOverlap(rect);
            return rect;
        }
        public static Rect ConstructorCheckCursorOverlap(in float x, in float y, in float width, in float height) {
            Rect rect = new Rect(x, y, width, height);
            CheckCursorOverlap(rect);
            return rect;
        }

        public static Vector2 GetCursorPosInGUI() {
            return new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);
        }
        public static bool IsCursorInGUIRect(in Rect area) {
            return area.Contains(GetCursorPosInGUI());
        }


        public static void _GUI_SetTooltip(in string name, in string desc) {
            _gui_tooltip = true;
            _gui_tooltip_name = name;
            _gui_tooltip_desc = desc;
        }
        static bool _gui_tooltip = false;
        static string _gui_tooltip_name = "";
        static string _gui_tooltip_desc = "";


        public static void _GUI_BiggerLabel(in string txt, float multiplier) {

            int ogfontsize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = (int)(ogfontsize * multiplier);

            GUIContent content = FixGuiContent.Content(txt, 1);
            Vector2 siz = GUI.skin.label.CalcSize(content);

            GUILayout.Label(content, GUILayout.Height(siz.y));

            GUI.skin.label.fontSize = ogfontsize;

        }
        public static bool _GUI_FocusableButton(in Rect rect, in string text, out bool focused, GUIStyle style = null) {


            bool result = style == null ? (GUI.Button(rect, text)) : (GUI.Button(rect, text, style));
            focused = UIBullshit.IsCursorInGUIRect(rect);

            TOTAL_INTERACTIVE_UI += 1;
            if (focused) {
                IS_CURSOR_OVERLAPPING = true;
                TOTAL_FOCUSED_INTERACTIVE_UI += 1;
            }

            return result;
        }
        public static void _GUI_9SlicePanel(in Rect rect, in float brightness, in float alpha, bool check_overlap, Texture2D tex = null) {

            Texture2D og = GUI.skin.box.normal.background;

            tex ??= unscaled_uiBlockSmall;
            if (tex != null) {
                GUI.skin.box.normal.background = GetChangedTex(tex, brightness, alpha);

                if (tex == unscaled_uiBlockSmall) {
                    int gaaaaaaa = uiBlockSmallBorderSize;// + 2;
                    GUI.skin.box.border = new RectOffset(gaaaaaaa, gaaaaaaa, gaaaaaaa, gaaaaaaa);
                } else if (tex == unscaled_uiBlockNano) {
                    int gaaaaaaa = uiBlockNanoBorderSize;// + 2;
                    GUI.skin.box.border = new RectOffset(gaaaaaaa, gaaaaaaa, gaaaaaaa, gaaaaaaa);
                }
            }
            GUI.Box(rect, "");

            GUI.skin.box.normal.background = og;

            if (check_overlap) {
                CheckCursorOverlap(rect);
            }

        }
        public static bool IsAnyMenuOpen() {
            return Util.IsInPauseMenuOrMainMenu() || UIMainMenu.IsOpen();
        }

        public static float dev2222222222348333343gfddgfgdfdfgfdgfdg4398 = 0;
        public static bool _reset_gui_textures_next_frame = false;
        public static bool dev348432843289723498723497824398 = true;
        public static bool dev34843gfddgfgdfdfgfdgfdg4398 = false;
        void OnGUI() {
            using (FindTheBugInator.Time(FindTheBugInator.Ui)) {
                _GUI_DoTheFullGUI();
            }
        }

        void __GUI__AllTheMenus() {
            try {

                if (
                    !Con.IsConsoleOpen() && !HostTransferUI.IsModalOpen
                ) {
                    try {
                        UIMainMenu._GUI_DrawSideMenuWithPlayerList();

                        //this.GetOrAddComponent<UIMainMenu>()._GUI_DrawMPMainMenu();
                        UIMainMenu._GUI_DrawMPMainMenu();
                    } catch (Exception e) {
                        log.error("MAIN MENU UI: " + e.ToString());
                    }
                }

                Chat._GUI_DrawChat();

            } catch (Exception e) {
                log.error("CHAT UI: " + e.ToString());
            }


            if (UIInGame.main != null) {
                UIInGame.main._GUI_DoInGameUI();
            }
        }


        void _GUI_DoTheFullGUI() {

            try {
                // MAINMENU TRANSPARENCY: 0,6980392

                try {
                    bool chrome = WantsMpChrome();
                    if (chrome) {
                        UIBullshit._GUI_CaptureOGUITextures();
                        UIBullshit._GUI_DoTexturesForMPUserInterface();
                    }

                    try {

                        GUI.enabled = !GUILayout_DropdownMenu.isOpen;
                        __GUI__AllTheMenus();
                        GUI.enabled = true;

                        if (UIInGame.main != null) {
                            UIInGame.main._GUI_DoInGameUI();
                        }

                        GUILayout_DropdownMenu.Draw();

                        if (!Con.IsConsoleOpen()) {
                            try {
                                UIChoicePrompt.instance._GUI_RenderUIChoicePrompt();
                            } catch (Exception e) {
                                log.error("_GUI_RenderUIChoicePrompt: " + e.ToString());
                            }
                        }

                        __GUI__DoTooltipBullshit();


                    } catch (Exception e) {
                        log.error("MAIN UI RENDER: " + e.ToString());
                    }

                    if (chrome) {
                        UIBullshit._GUI_ResetToOGUITextures();
                    }
                } catch (Exception e) {
                    log.error("GAME UI: " + e.ToString());
                }



                if (Con.IsConsoleOpen()) {
                    return;
                }


#if !RELEASE


                try {
                    UIBullshit._GUI_CaptureOGUITextures();
                    UIBullshit._GUI_DoTexturesForMPUserInterface();

                    float sc = 1f;
                    if (dev348432843289723498723497824398) {
                        sc = UIBullshit.uiScale;
                    }
                    if (dev34843gfddgfgdfdfgfdgfdg4398) {

                        { //draw button next to cursor

                            Vector2 pos = (Vector2)Util.GetCursorPos();
                            pos = new Vector2(pos.x, Screen.height - pos.y);
                            //GUI.Button(new Rect(pos.x + 5, pos.y + 5, 150 * sc, 40 * sc), "Cursor");

                            _GUI_9SlicePanel(new Rect(pos.x + 5, pos.y + 5, 150 * sc, 40 * sc), 1, 0.6980392f, false);
                        }

                        if (GUI.Button(new Rect(Screen.width - 200 * sc, 0, 200 * sc, 50 * sc), "Make UI relative")) {
                            dev348432843289723498723497824398 = !dev348432843289723498723497824398;
                        }
                        GUI.Label(new Rect(Screen.width - 300 * sc, 0, 100 * sc, 50 * sc), "Lorem");
                        {
                            float oldffff = UIBullshit.THING_FOR_UI_SCALE;
                            UIBullshit.THING_FOR_UI_SCALE = GUI.HorizontalSlider(new Rect(Screen.width - 500 * sc, 90 * sc, 200 * sc, 20 * sc), oldffff, 0.1f, 5f);
                            if (oldffff != UIBullshit.THING_FOR_UI_SCALE) {
                                UIBullshit.ResizeGUITextures();
                            }
                        }
                        if (false) {
                            float oldffff = UIBullshit.DEFAULT_RETROGAME_FONTSIZE;
                            UIBullshit.DEFAULT_RETROGAME_FONTSIZE = GUI.HorizontalSlider(new Rect(Screen.width - 500 * sc, 110 * sc, 200 * sc, 20 * sc), oldffff, 10f, 30f);
                            if (oldffff != UIBullshit.DEFAULT_RETROGAME_FONTSIZE) {
                                UIBullshit.ResizeGUITextures();
                            }
                        }
                        {
                            float oldffff = UIBullshit.dev2222222222348333343gfddgfgdfdfgfdgfdg4398;
                            UIBullshit.dev2222222222348333343gfddgfgdfdfgfdgfdg4398 = GUI.HorizontalSlider(new Rect(Screen.width - 500 * sc, 110 * sc, 200 * sc, 20 * sc), oldffff, 0, 10);
                            if (oldffff != UIBullshit.dev2222222222348333343gfddgfgdfdfgfdgfdg4398) {
                                UIBullshit.ResizeGUITextures();
                            }
                        }

                        GUI.Label(new Rect(Screen.width - 500 * sc, 130 * sc, 500 * sc, 400 * sc), $"scaleFactor: {UIBullshit.canvas.scaleFactor}\nuiScaleMultiplier: {UIBullshit.uiScaleMultiplier}\nuiScaleForMostUI: {UIBullshit.uiScaleForMostUI}\nTHING_FOR_UI_SCALE: {UIBullshit.THING_FOR_UI_SCALE}\nRETROGAME_FONTSIZE: {UIBullshit.DEFAULT_RETROGAME_FONTSIZE}\nOVERLAP: {IS_CURSOR_OVERLAPPING}");

                        if (GUI.Button(new Rect(Screen.width - 500 * sc, 40 * sc, 200 * sc, 50 * sc), "Reset textures")) {
                            UIBullshit.ResizeGUITextures();
                        }

                        _GUI_9SlicePanel(new Rect(Screen.width - 700 * sc, 40 * sc, 200 * sc, 100 * sc), 1, 0.6980392f, false);

                        //GUI.skin.button.richText = true; // not worky 
                        //GUI.Button(new Rect(Screen.width - 500, 50, 200, 50), "<quad material=1 size=20 x=0.1 y=0.1 width=0.5 height=0.5>AAAAAAAAAAAA</quad>");
                        //GUI.skin.button.richText = false;
                    }
                    dev34843gfddgfgdfdfgfdgfdg4398 = GUI.Toggle(UIBullshit.ConstructorCheckCursorOverlap(Screen.width - 500 * sc, 0, 200 * sc, 40 * sc), dev34843gfddgfgdfdfgfdgfdg4398, "RENDER DEBUG UI");


                    UIBullshit._GUI_ResetToOGUITextures();
                } catch (Exception e) {
                    log.error("TEST UI: " + e.ToString());
                }
#endif


            } catch (Exception e) {
                log.error("UIBULLSHIT ONGUI: " + e.ToString());
            }


            if (Con.IsConsoleOpen()) {
                return;
            }

            {
                _GUI_DrawVerboseWoundview();
            }


        }

        static float TOOLTIP_SCALE = 0.78f;
        void __GUI__DoTooltipBullshit() {
            if (_gui_tooltip) {
                _gui_tooltip = false;

                int ogfontsize = GUI.skin.label.fontSize;
                bool ogfrichText = GUI.skin.label.richText;
                bool ogwordWrap = GUI.skin.label.wordWrap;
                GUI.skin.label.fontSize = (int)(ogfontsize * TOOLTIP_SCALE);


                Vector2 cornerpos = GetCursorPosInGUI() + Vector2.one * 10;

                GUI.skin.label.richText = true;
                if (!string.IsNullOrWhiteSpace(_gui_tooltip_name)) {
                    GUI.skin.label.wordWrap = false;

                    GUIContent content = FixGuiContent.Content(_gui_tooltip_name, 2);

                    Vector2 size = GUI.skin.label.CalcSize(content);
                    Rect rect = new Rect(cornerpos.x, cornerpos.y, size.x, size.y);
                    Rect rectwider = new Rect(rect);
                    rectwider.width += size.y * 0.1f;

                    GUI.color = Color.black;
                    GUI.DrawTexture(rectwider, WorldMP.whiteSquare.texture);
                    GUI.color = Color.white;
                    GUI.Label(rect, content);

                    cornerpos.y += size.y;
                }



                if (!string.IsNullOrWhiteSpace(_gui_tooltip_desc)) {
                    GUI.skin.label.wordWrap = true;

                    GUIContent content = FixGuiContent.Content(_gui_tooltip_desc, 3);

                    Vector2 size = GUI.skin.label.CalcSize(content);
                    Rect rect = new Rect(cornerpos.x, cornerpos.y, size.x, size.y);
                    Rect rectwider = new Rect(rect);
                    rectwider.width += size.y * 0.1f;

                    GUI.color = Color.black;
                    GUI.DrawTexture(rectwider, WorldMP.whiteSquare.texture);
                    GUI.color = new Color(0.9f, 0.9f, 0.9f);
                    GUI.Label(rect, content);
                }

                GUI.skin.label.wordWrap = ogwordWrap;
                GUI.skin.label.fontSize = ogfontsize;
                GUI.skin.label.richText = ogfrichText;
                GUI.color = Color.white;
            }
        }

        public static void _GUI_DrawVerboseWoundview() {
            if (Util.IsInWoundView()) {
                if (MinigameBase.main.currentMinigame != null && MinigameBase.main.currentMinigame is CPRMinigame) {
                    return;
                }

                WoundView wv = WoundView.view;

                if (log.verbose) {
                    int ogfont = GUI.skin.label.fontSize;
                    GUI.skin.label.fontSize = 10;

                    for (int k = 0; k < wv.body.limbs.Length; k++) {
                        Limb limb = wv.body.limbs[k];
                        Image limbimage = wv.limbImages[k];
                        Vector3 pos = limbimage.rectTransform.position;
                        pos = new Vector2(pos.x, Screen.height - pos.y);

                        GUI.Label(new Rect(pos.x, pos.y, 200, 20), $"{limb.name} id:{k}");
                        GUI.Label(new Rect(pos.x, pos.y + 12, 200, 20), $"totalForce: {Math.Round(limb.totalForce, 2)}");
                        GUI.Label(new Rect(pos.x, pos.y + 12 * 2, 200, 20), $"shra: {limb.shrapnel}");
                        GUI.Label(new Rect(pos.x, pos.y + 12 * 3, 200, 20), $"totalBleed: {limb.totalBleedAmount}");
                        GUI.Label(new Rect(pos.x, pos.y + 12 * 4, 200, 20), $"inf: {limb.infectionAmount}");

                    }
                    GUI.skin.label.fontSize = ogfont;
                }

                if (!Util.IsBodyLocal(wv.body)) {
                    // close the wound view if too far from the target 
                    if (!UIInGame.SpectatorModeActive &&
                            !KM.DistanceCheck(PlayerCamera.main.body, wv.body, SharedMain.max_player_interaction_distance * 2f)) {
                        PlayerCamera.main.ToggleWoundView();
                        return;
                    }
                }
            }
        }



        public static int GetCurFontSize(float additional_scale = 1) {
            if (USE_GAME_FONT) {
                return Mathf.RoundToInt(DEFAULT_RETROGAME_FONTSIZE * uiScale * CUSTOM_UI_SCALE_FOR_UIBULLSHIT * additional_scale);
            } else {
                return Mathf.RoundToInt(_og_fontsize * 2f * uiScale * CUSTOM_UI_SCALE_FOR_UIBULLSHIT * additional_scale);
            }
        }
        static void DoSliderThings(GUIStyle slider, GUIStyle thumb) {
            thumb.normal.background = GetChangedTex(unscaled_uiBlockNano, 1f, 1f);
            thumb.focused.background = GetChangedTex(unscaled_uiBlockNano, 0.9f, 1f);
            thumb.hover.background = thumb.focused.background;
            thumb.active.background = GetChangedTex(unscaled_uiBlockNano, 0.8f, 1f);
            slider.normal.background = GetChangedTex(unscaled_uiBlockNano, 1f, 1f);
            slider.overflow = new RectOffset(0, 0, 0, 0);
            thumb.overflow = new RectOffset(0, 0, 0, 0);
            slider.padding = new RectOffset(0, 0, 0, 0);

            slider.border = new RectOffset(uiBlockNanoBorderSize, uiBlockNanoBorderSize, uiBlockNanoBorderSize, uiBlockNanoBorderSize);
            thumb.border = slider.border;


            float mcdonanlds = 20f * uiScale;
            if (thumb.fixedHeight != 0) {
                thumb.fixedHeight = mcdonanlds;
            }

            if (thumb.fixedWidth != 0) {
                thumb.fixedWidth = mcdonanlds;
            }
        }
        static void DoFontThings(GUIStyle style) {
            if (USE_GAME_FONT) {
                // put the font on the style. skin.font alone flips back to stock when the text box loses focus
                if (CoopModAssets.gamefont != null) {
                    style.font = CoopModAssets.gamefont;
                }

                style.fontSize = GetCurFontSize();
            } else {
                style.font = null;
                style.fontSize = _og_fontsize;
            }
        }

        // kept in the dll so a later build can be told apart from this one
        internal static readonly string ChatFontPinMarker = "ct-chatfont-pin";

        // the face you get while typing. popup chat and shift names should keep it
        internal static Font FocusedChatFont() {
            if (ChatFontPinMarker == null || !USE_GAME_FONT) {
                return null;
            }

            return CoopModAssets.gamefont;
        }

        internal static bool PushFocusedChatFont(out Font label, out Font box, out Font field) {
            label = null;
            box = null;
            field = null;
            Font font = FocusedChatFont();
            if (font == null || GUI.skin == null) {
                return false;
            }

            label = GUI.skin.label.font;
            box = GUI.skin.box.font;
            field = GUI.skin.textField.font;
            GUI.skin.label.font = font;
            GUI.skin.box.font = font;
            GUI.skin.textField.font = font;
            return true;
        }

        internal static void PopFocusedChatFont(bool pushed, Font label, Font box, Font field) {
            if (!pushed || GUI.skin == null) {
                return;
            }

            GUI.skin.label.font = label;
            GUI.skin.box.font = box;
            GUI.skin.textField.font = field;
        }
        static void UnfuckFonts(GUIStyle style) {
            style.font = null;
            style.fontSize = _og_fontsize;
        }
        public static RectOffset RectOffset(int n) {
            return new RectOffset(n, n, n, n);
        }

        static RectOffset _zeroPadding;
        // one padding instance for the hud. not shared with border or overflow.
        internal static RectOffset ZeroPadding {
            get {
                if (_zeroPadding == null) {
                    _zeroPadding = new RectOffset(0, 0, 0, 0);
                }

                return _zeroPadding;
            }
        }

        // menus, chat, prompts, and the few in-game buttons are the only things that need the mp skin.
        internal static bool WantsMpChrome() {
            // console used to drop the skin before chat was checked, so the font flipped as soon as the text box lost focus
            bool console = Con.IsConsoleOpen();
            if (!console) {
                if (UIMainMenu.mainmenu_open || IsAnyMenuOpen()) {
                    return true;
                }

                if (UIChoicePrompt.prompt_show_progress != 0f) {
                    return true;
                }

                if (UIInGame.interaction_menu_target_body != null) {
                    return true;
                }

                if (UIInGame.SpectatorModeActive || Con._DEV_FREECAM) {
                    return true;
                }
            }
            if (Chat.WantsGui()) {
                return true;
            }

            if (console) {
                return false;
            }

            if (!Multiplayer.IsNetworkActiveAndIsInGame()) {
                return false;
            }

            ScavPlayer me = ScavPlayer.LocalPlayer;
            if (me == null || me.body == null || !me.body.alive || me.body.sleeping) {
                return true;
            }

            return me.netBody != null && (me.netBody.piggybacking_on != null || me.netBody.carrying_person != null);
        }

        public static void _GUI_SetButtonSkinTexture(GUIStyle style, bool small = true) {
            if (small) {
                style.normal.background = tex_button_normal;
                style.active.background = tex_button_active;
                style.focused.background = tex_button_focus;
                style.hover.background = tex_button_hover;
                int if_the_earth_is_round___how_the_ball_isnt_going_nowhere = uiBlockSmallBorderSize;// + 2;
                style.border = RectOffset(if_the_earth_is_round___how_the_ball_isnt_going_nowhere);

                style.onNormal.background = GetChangedTex(unscaled_uiBlockSmall, 1.05f, 1f);
            } else {
                style.normal.background = tex_button_normal_nano;
                style.active.background = tex_button_active_nano;
                style.focused.background = tex_button_focus_nano;
                style.hover.background = tex_button_hover_nano;
                style.border = RectOffset(uiBlockNanoBorderSize);

                style.onNormal.background = GetChangedTex(unscaled_uiBlockNano, 1.05f, 1f);
            }

            style.onActive.background = style.active.background;
            style.onFocused.background = style.focused.background;
            style.onHover.background = style.hover.background;

            style.padding = style.border;
        }

        internal static void _GUI_MPUISetSkinValues() {
            if (!FixSkinSkip.Allow()) {
                return;
            }

            _GUI_SetButtonSkinTexture(GUI.skin.button, true);

            GUI.skin.box.normal.background = tex_button_hover_nano;

            GUI.skin.toggle.normal.background = GetChangedTex(CoopModAssets.checkbox.texture, 1f, 1f);
            if (GUI.skin.toggle.normal.background != null) {
                GUI.skin.toggle.border = new RectOffset(GUI.skin.toggle.normal.background.width, 1, GUI.skin.toggle.normal.background.height, 1);
                GUI.skin.toggle.padding.left = GUI.skin.toggle.normal.background.width;
                GUI.skin.toggle.padding.top = (int)(GUI.skin.toggle.normal.background.height * 0.21f);
            }
            GUI.skin.toggle.focused.background = GetChangedTex(CoopModAssets.checkbox.texture, 0.9f, 1f);
            GUI.skin.toggle.hover.background = GUI.skin.toggle.focused.background;
            GUI.skin.toggle.active.background = GetChangedTex(CoopModAssets.checkbox.texture, 0.8f, 1f);
            GUI.skin.toggle.onNormal.background = GetChangedTex(CoopModAssets.checkbox_ticked.texture, 1f, 1f);
            GUI.skin.toggle.onHover.background = GetChangedTex(CoopModAssets.checkbox_ticked.texture, 0.9f, 1f);
            GUI.skin.toggle.onFocused.background = GUI.skin.toggle.onHover.background;
            GUI.skin.toggle.onActive.background = GetChangedTex(CoopModAssets.checkbox_ticked.texture, 0.8f, 1f);



            DoSliderThings(GUI.skin.horizontalSlider, GUI.skin.horizontalSliderThumb);
            DoSliderThings(GUI.skin.horizontalScrollbar, GUI.skin.horizontalScrollbarThumb);
            DoSliderThings(GUI.skin.verticalSlider, GUI.skin.verticalSliderThumb);
            DoSliderThings(GUI.skin.verticalScrollbar, GUI.skin.verticalScrollbarThumb);

            float mcdonanlds = 20f * uiScale;
            GUI.skin.verticalScrollbar.fixedWidth = mcdonanlds;
            GUI.skin.horizontalScrollbar.fixedHeight = mcdonanlds;
            GUI.skin.verticalSlider.fixedWidth = mcdonanlds;
            GUI.skin.horizontalSlider.fixedHeight = mcdonanlds;


            GUI.skin.textField.normal.background = GetChangedTex(unscaled_uiBlockNano, 0.8f, 0.8f);
            GUI.skin.textField.hover.background = GetChangedTex(unscaled_uiBlockNano, 0.9f, 0.8f);
            GUI.skin.textField.focused.background = GetChangedTex(unscaled_uiBlockNano, 1f, 0.8f);
            GUI.skin.textField.onNormal.background = GetChangedTex(unscaled_uiBlockNano, 0.9f, 0.8f);

            GUI.skin.textArea.normal.background = GUI.skin.textField.normal.background;
            GUI.skin.textArea.hover.background = GUI.skin.textField.hover.background;
            GUI.skin.textArea.focused.background = GUI.skin.textField.focused.background;
            GUI.skin.textArea.onNormal.background = GUI.skin.textField.onNormal.background;
            GUI.skin.textArea.onNormal.textColor = Color.white;
            GUI.skin.textArea.focused.textColor = Color.white;

            if (CoopModAssets.gamefont != null) {
                if (USE_GAME_FONT) {
                    GUI.skin.font = CoopModAssets.gamefont;
                } else {
                    GUI.skin.font = _b_ogfont;
                }
                DoFontThings(GUI.skin.button);
                DoFontThings(GUI.skin.textField);
                DoFontThings(GUI.skin.textArea);
                DoFontThings(GUI.skin.label);
                DoFontThings(GUI.skin.box);
                DoFontThings(GUI.skin.toggle);
            }
        }
        internal static void _GUI_DoTexturesForMPUserInterface() {
            try {
                if (_og_skin_cloned == null) {
                    _og_skin_cloned = UnityEngine.Object.Instantiate(GUI.skin);
                }
                if (new_skin == null) {
                    new_skin = UnityEngine.Object.Instantiate(GUI.skin);
                }
                GUI.skin = new_skin;

                if (_recalculate_skin) {
                    // FixSkinSkip reads the flag to decide whether the rebuild runs, so it is cleared after the rebuild.
                    // cleared before, the rebuild was skipped and the destroy below left the skin on dead textures.
                    _GUI_MPUISetSkinValues();
                    _recalculate_skin = false;
                    foreach (Texture2D retired in retired_textures) {
                        if (retired != null) {
                            UnityEngine.Object.Destroy(retired);
                        }
                    }
                    retired_textures.Clear();
                } else {

                    _GUI_MPUISetSkinValues();//fuck it
                }

            } catch (Exception e) {
#if TESTING
                log.error("_GUI_DoTexturesForMPUserInterface\n"+e.ToString());
#endif
            }
        }

        internal static void _GUI_CaptureOGUITextures() {
            if (_og_skin == null) {
                _og_skin = GUI.skin;

                _og_verticalscrollbar_fixedWidth = GUI.skin.verticalScrollbar.fixedWidth;
                _og_gorizontalscrollbar_fixedHeight = GUI.skin.horizontalScrollbar.fixedHeight;



            }
            if (_b_n_b == null) {

                _og_fontsize = GUI.skin.button.fontSize;
                _b_n_b = GUI.skin.button.normal.background;
                _b_a_b = GUI.skin.button.active.background;
                _b_f_b = GUI.skin.button.focused.background;
                _b_h_b = GUI.skin.button.hover.background;

                if (_og_border == null) {
                    _og_border = GUI.skin.button.border;
                }

                _box_bg = GUI.skin.box.normal.background;
                _box_txcolor = GUI.skin.box.normal.textColor;
                _b_ogfont = GUI.skin.font;
            }
        }


        internal static void _GUI_ResetToOGUITextures() {
            if (_og_skin != null) {
                GUI.skin = _og_skin;
            }

            //GUI.skin.button.normal.background = _b_n_b;
            //GUI.skin.button.active.background = _b_a_b;
            //GUI.skin.button.focused.background = _b_f_b;
            //GUI.skin.button.hover.background = _b_h_b;
            //
            //
            //
            //GUI.skin.button.border = _og_border;
            //GUI.skin.box.normal.background = _box_bg ;
            //GUI.skin.box.normal.textColor = _box_txcolor;
            //GUI.skin.font = _b_ogfont;
            //unfuckFonts(GUI.skin.button);
            //unfuckFonts(GUI.skin.label);
            //unfuckFonts(GUI.skin.box);
            //unfuckFonts(GUI.skin.toggle);
        }


        internal static int _og_fontsize = 13;
        //static Dictionary<GUIStyleState, Texture2D> _og_gui_textures = new();

        static GUISkin new_skin;
        static GUISkin _og_skin;
        static GUISkin _og_skin_cloned;

        static float _og_verticalscrollbar_fixedWidth;
        static float _og_gorizontalscrollbar_fixedHeight;

        static Texture2D _b_n_b;
        static Texture2D _b_a_b;
        static Texture2D _b_f_b;
        static Texture2D _b_h_b;
        static Texture2D _box_bg;
        static Color _box_txcolor;
        static Font _b_ogfont;
        static RectOffset _og_border = new RectOffset(6, 6, 6, 4); //  rectOffset (l:6 r:6 t:6 b:4)

        static GUISkin skin => GUI.skin;

        //public static Texture2D tex_button_normal => KrokoshaMainmenuBackground.uiBlockSmall_transparent ?? _b_n_b;
        //public static Texture2D tex_button_active => KrokoshaMainmenuBackground.uiBlockSmall_darkened2 ?? _b_a_b;
        //public static Texture2D tex_button_focus => KrokoshaMainmenuBackground.uiBlockSmall_darkened2 ?? _b_f_b;
        //public static Texture2D tex_button_hover => KrokoshaMainmenuBackground.uiBlockSmall_darkened ?? _b_h_b;



        public static Texture2D unscaled_uiBlockNano => UIManager.uiBlockNano != null ? UIManager.uiBlockNano.texture : null;
        public static Texture2D unscaled_uiBlockSmall => UIManager.uiBlockSmall != null ? UIManager.uiBlockSmall.texture : null;
        public static Texture2D tex_toggle_normal => (GetChangedTex(unscaled_uiBlockNano, 1f, 0.8f)) ?? _b_n_b;
        public static Texture2D tex_button_normal_nano => (GetChangedTex(unscaled_uiBlockNano, 1f, 0.8f)) ?? _b_n_b;
        public static Texture2D tex_button_active_nano => (GetChangedTex(unscaled_uiBlockNano, 0.4f, 0.7f)) ?? _b_a_b;
        public static Texture2D tex_button_focus_nano => (GetChangedTex(unscaled_uiBlockNano, 0.4f, 0.7f)) ?? _b_f_b;
        public static Texture2D tex_button_hover_nano => (GetChangedTex(unscaled_uiBlockNano, 0.65f, 0.7f)) ?? _b_h_b;
        public static Texture2D tex_button_normal => (GetChangedTex(unscaled_uiBlockSmall, 1f, 0.8f)) ?? _b_n_b;
        public static Texture2D tex_button_active => (GetChangedTex(unscaled_uiBlockSmall, 0.4f, 0.7f)) ?? _b_a_b;
        public static Texture2D tex_button_focus => (GetChangedTex(unscaled_uiBlockSmall, 0.4f, 0.7f)) ?? _b_f_b;
        public static Texture2D tex_button_hover => (GetChangedTex(unscaled_uiBlockSmall, 0.65f, 0.7f)) ?? _b_h_b;


        void OnDestroy() {
            IS_CURSOR_OVERLAPPING = false;

        }

    }




    public class UIInGame : MonoBehaviour {
        public static UIInGame main;

        internal bool antidispersion_active = false;
        internal bool antidispersion_warned = false;
        internal float antidispersion_cur_distance = 0;
        internal float antidispersion_cur_required_count = 0;
        internal int antidispersion_cur_range_count = 0;



        public static NetBody interaction_menu_target_body = null;
        public static Vector2 interaction_menu_target_pos = Vector2.zero;
        public static bool interaction_menu_focused = false;


        public static bool SpectatorModeActive { get; private set; }
        public static Transform SpectatorCurrentTarget { get; private set; }
        public static Transform SpectatorLastTarget { get; private set; }
        internal static bool spectator_only_alive = true;
        static int curspectatefolowing = 0;
        static string curspectatefolowingname = "EROR";

        public static Vector2 _DEV_FREECAM_CURPOS {
            get {
                return ConsoleScript.instance.freecamPos;
            }
            set {
                ConsoleScript.instance.freecamPos = value;
            }
        }

        static float PLRDIRTAG_BARK_VISIBILITY_TIME = 3.0f;
        static bool PLRDIRTAG_SHOULD_NOTIFY_DYING = true;


        static ushort last_action_priority = 0;
        static float last_action_cooldown = 0;
        static float last_action_cooldown_countdown = 0;

        static bool action_spectator_active = true;
        public static void Spectator_OnAction(NetBody star, ushort priority, float cooldown, float max_viewing_distance = 50f) {
            if (SpectatorModeActive && action_spectator_active) {
                float priorityfalloff = Mathf.Clamp(((last_action_cooldown_countdown + last_action_cooldown * 0.5f) / last_action_cooldown), 0.5f, 1f);

                if (priority > last_action_priority || last_action_cooldown_countdown <= 0 || priority > last_action_priority * priorityfalloff) {
                    last_action_priority = priority;
                    last_action_cooldown = cooldown;
                    last_action_cooldown_countdown = cooldown;

                    if (!KM.DistanceCheck(star.pos, PlayerCamera.main.transform.position, max_viewing_distance)) { // if camera is too far, look at this 
                        Spectator_SetTarget(star);
                    }
                }
            }
        }


        static void _GUI__DrawPlayerDirection(NetBody nb, float a, bool shiftHeld) {
            Vector2 plrpos = (Vector2)nb.transform.position;

            if (plrpos == Util.ClampWorldPositionToCameraView(plrpos)) { // is on screen
                if (shiftHeld) {
                    DrawShiftSpeciesMark(nb, a);
                }
                return;
            }

            Vector2 direction = KM.NormalTo(Camera.main.transform.position, plrpos, out float distance);
            direction = new Vector2(direction.x, -direction.y);

            Vector2 halfscreen = (new Vector2(Screen.width, Screen.height) * 0.5f);
            Vector2 arrow_screen_pos = halfscreen * (Vector2.one + direction * 0.6f);

            Color color = nb.color;
            color = color.WithAlpha(a);
            if (PLRDIRTAG_SHOULD_NOTIFY_DYING && nb.body.isDying) {
                float sine = ((float)Math.Sin(nb.lastTimeBark + Time.realtimeSinceStartupAsDouble * 4.0));
                color = Color.Lerp(color, Color.red, sine * 0.5f + 0.5f);
            }

            GUI.skin.label.normal.textColor = color;

            Matrix4x4 oldmat = GUI.matrix;
            string txt = $"{nb.playerName} ( {Math.Floor(Util.TilesToMeters(distance))} M )";

            Vector2 labelsize = GUI.skin.label.CalcSize(FixGuiContent.Content(txt, 4));
            Vector2 labelpos = arrow_screen_pos - labelsize * 0.5f;
            Rect labelrect = new Rect(labelpos.x, labelpos.y, labelsize.x, labelsize.y);
            GUI.Label(new Rect(labelpos.x, labelpos.y, labelsize.x + 200, labelsize.y), txt);

            if (nb.IsPlayer) {
                Texture2D profilepic = nb.plr.IconAnySmallToLarge;
                if (profilepic != null) {
                    GUI.Label(new Rect(labelpos.x - labelsize.y * 1.1f, labelpos.y, labelsize.y, labelsize.y), profilepic);
                }
            }

            // stock wound-view head under the name (never custom skin art)
            float speciesSize = Mathf.Max(labelsize.y * 1.4f, 24f * UIBullshit.uiScale);
            Rect mark = DrawSpeciesMark(labelrect, nb, color, speciesSize, false);
            float xMin = labelrect.xMin < mark.xMin ? labelrect.xMin : mark.xMin;
            float yMin = labelrect.yMin < mark.yMin ? labelrect.yMin : mark.yMin;
            float xMax = labelrect.xMax > mark.xMax ? labelrect.xMax : mark.xMax;
            float yMax = labelrect.yMax > mark.yMax ? labelrect.yMax : mark.yMax;
            Rect tagBounds = Rect.MinMaxRect(xMin, yMin, xMax, yMax);

            // move it farther
            arrow_screen_pos += direction * labelsize.magnitude;
            // put it on the surface of the thing
            arrow_screen_pos = new Vector2(
                Mathf.Clamp(arrow_screen_pos.x, tagBounds.xMin, tagBounds.xMax),
                Mathf.Clamp(arrow_screen_pos.y, tagBounds.yMin, tagBounds.yMax)
            );
            // move it forward again a little
            arrow_screen_pos += direction * 32;

            float arrowsize = 32f * UIBullshit.uiScale;

            // center it
            arrow_screen_pos -= Vector2.one * arrowsize * 0.5f;
            GUIUtility.RotateAroundPivot(Vector2.SignedAngle(Vector2.right, direction), arrow_screen_pos);

            GUI.DrawTexture(new Rect(arrow_screen_pos.x, arrow_screen_pos.y, arrowsize, arrowsize), CoopModAssets.arrowicon.texture, ScaleMode.StretchToFill, true, 0, color, 0f, 0f);

            GUI.matrix = oldmat;
            GUI.skin.label.normal.textColor = UnityEngine.Color.white;
        }

        // shift keeps the species head on players you can already see, above the head
        static void DrawShiftSpeciesMark(NetBody nb, float a) {
            Camera cam = Camera.main;
            if (cam == null || nb == null) {
                return;
            }
            Vector3 world = nb.transform.position;
            if (nb.body != null && nb.body.limbs != null && nb.body.limbs.Length > 0 && nb.body.limbs[0] != null) {
                world = nb.body.limbs[0].transform.position;
            }
            world.y += 0.55f;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) {
                return;
            }
            float size = Mathf.Max(28f * UIBullshit.uiScale, 22f);
            Rect anchor = new Rect(sp.x, Screen.height - sp.y, 0f, 0f);
            Color color = nb.color;
            color.a = a;
            DrawSpeciesMark(anchor, nb, color, size, true);
        }

        // woundView.headCharSprites stays on the prefab stock art; skin swaps only replace Image.sprite.
        // underName centers the mark on a name rect. overhead centers it on a point.
        static Rect DrawSpeciesMark(Rect anchor, NetBody nb, Color color, float size, bool overhead) {
            if (nb == null || size < 1f) {
                return anchor;
            }

            WoundView view = WoundView.view;
            if (view == null || view.headCharSprites == null || view.headCharSprites.Length < 6) {
                return anchor;
            }

            int chr = 0;
            if (nb.body != null) {
                chr = Mathf.Clamp(nb.body.charType, 0, 2);
            } else if (nb.plr != null) {
                chr = Mathf.Clamp(nb.plr.selectedCharacter, 0, 2);
            }

            Sprite outline = view.headCharSprites[chr * 2];
            Sprite middle = view.headCharSprites[chr * 2 + 1];
            if (outline == null) {
                return anchor;
            }

            int digit = 0;
            bool haveDigit = nb.plr != null
                && UI.TeammateList.TeammateListHost.Roster != null
                && UI.TeammateList.TeammateListHost.Roster.TrySlotDigit(nb.plr, out digit);
            string num = haveDigit ? digit.ToString() : "";
            float gap = 2f * UIBullshit.uiScale;
            float numW = 0f;
            if (num.Length > 0) {
                numW = GUI.skin.label.CalcSize(FixGuiContent.Content(num, 37)).x;
            }
            float pair = size + (numW > 0f ? numW + gap : 0f);
            float x;
            float y;
            if (overhead) {
                x = anchor.x - pair * 0.5f;
                y = anchor.y - size * 0.5f;
            } else {
                x = anchor.x + (anchor.width - pair) * 0.5f;
                y = anchor.yMax + 2f * UIBullshit.uiScale;
            }
            if (numW > 0f) {
                Color prevText = GUI.skin.label.normal.textColor;
                TextAnchor prevAlign = GUI.skin.label.alignment;
                GUI.skin.label.normal.textColor = color;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x, y, numW, size), num);
                GUI.skin.label.alignment = prevAlign;
                GUI.skin.label.normal.textColor = prevText;
            }
            Rect icon = new Rect(x + (numW > 0f ? numW + gap : 0f), y, size, size);
            DrawGuiSprite(icon, outline, color);
            if (middle != null) {
                DrawGuiSprite(icon, middle, color);
            }
            return new Rect(x, y, pair, size);
        }

        static void DrawGuiSprite(Rect r, Sprite sprite, Color color) {
            if (sprite == null || sprite.texture == null) {
                return;
            }

            Rect tr = sprite.textureRect;
            Texture2D tex = sprite.texture;
            if (tex.width < 1 || tex.height < 1 || tr.width < 1f || tr.height < 1f) {
                return;
            }

            Rect uv = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height);
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTextureWithTexCoords(r, tex, uv);
            GUI.color = prev;
        }

        // single-purpose: called only from DoMPUserInterface which is called only from OnGUI
        static void _GUI_DoPlayerPointersGui() {
            double t = Time.realtimeSinceStartupAsDouble;

            // shift names use the typing font even when chat is only popped up or the console took focus
            bool pinned = UIBullshit.PushFocusedChatFont(out Font labelfont, out Font boxfont, out Font fieldfont);
            int ognamesize = GUI.skin.label.fontSize;
            if (pinned) {
                GUI.skin.label.fontSize = UIBullshit.GetCurFontSize();
            }

            try {
                foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
                    if (plr.IsLocal || plr.body == null || !plr.body.alive) {
                        continue;
                    }
                    float a = 1;
                    bool shiftHeld = Input.GetKey(KeyBinds.GetBind(CoopKeybinds.showplrs));
                    if (Util.IsInGameAndPauseMenu() || shiftHeld) {
                    } else {
                        float diff = (float)(t - plr.netBody.lastTimeBark);
                        if (diff < PLRDIRTAG_BARK_VISIBILITY_TIME) {
                            a = Mathf.Clamp01(1.0f - diff / PLRDIRTAG_BARK_VISIBILITY_TIME);
                        } else {
                            continue;
                        }
                    }
                    _GUI__DrawPlayerDirection(plr.netBody, a, shiftHeld);
                }
            } finally {
                if (pinned) {
                    GUI.skin.label.fontSize = ognamesize;
                    UIBullshit.PopFocusedChatFont(true, labelfont, boxfont, fieldfont);
                }
            }
        }

        static void _GUI_DoSpectatorGui() {

            StopPlayerInteractionMenu();

            if (Util.IsInWoundView()) { // hide this in woundview
                return;
            }
            if (!Util.IsWorldGenerated()) {
                return;
            }

            int dir = 0;



            float uisc = UIBullshit.uiScale;

            float halfsc = Screen.width * 0.5f;
            float sypos = Screen.height * 0.54f + 70f * uisc;

            float iteratorbutton_wdth = 60 * uisc;
            float iteratorbutton_hght = 50 * uisc;

            float wdth = Mathf.Max(220 * uisc, GUI.skin.label.CalcSize(FixGuiContent.Content(curspectatefolowingname, 5)).x + 16 * uisc);
            float hght = 45 * uisc;

            {
                Rect nametagrect = new Rect(halfsc - wdth / 2, sypos, wdth, iteratorbutton_hght);
                UIBullshit._GUI_9SlicePanel(nametagrect, 1, 0.8f, true, UIBullshit.unscaled_uiBlockNano);

                TextAnchor ogalign = GUI.skin.label.alignment;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                GUI.Label(nametagrect, curspectatefolowingname);
                GUI.skin.label.alignment = ogalign;
            }

            wdth += iteratorbutton_wdth * 2;

            if (GUI.Button(UIBullshit.ConstructorCheckCursorOverlap(halfsc - wdth / 2, sypos, iteratorbutton_wdth, iteratorbutton_hght), "<")) {
                ServerMain._ded_server_switch_counter = -30;
                dir -= 1;
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            }
            if (GUI.Button(UIBullshit.ConstructorCheckCursorOverlap(halfsc + wdth / 2 - iteratorbutton_wdth, sypos, iteratorbutton_wdth, iteratorbutton_hght), ">")) {
                ServerMain._ded_server_switch_counter = -30;
                dir += 1;
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            }

            sypos += iteratorbutton_hght * 1.1f;




            spectator_only_alive = GUI.Toggle(new Rect(halfsc - wdth / 2, sypos, wdth, hght), spectator_only_alive, Lang.Get("spectate_alive"));
            sypos += hght;

            if (Spectator.GetCameraFollow() != null && Spectator.GetCameraFollow().TryGetComponent<Body>(out Body bbbbbbbbbbb) && !Util.IsInWoundView()) {
                if (GUI.Button(new Rect(halfsc - wdth / 2, sypos, wdth, hght), Lang.Get("spectate_openwoundview"))) {
                    PlayerCamera.main.ToggleWoundView();
                    return;
                }
                sypos += hght;
            }
            if (!Con._DEV_FREECAM && Multiplayer.rules.AllowSpectatorFreecam) {
                if (GUI.Button(new Rect(halfsc - wdth / 2, sypos, wdth, hght), Lang.Get("spectate_freecam"))) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);

                    Util.DoAlert(Lang.Get("freecam_keybindalert"));

                    StartFreecamMode(true);

                    return;
                }
                sypos += hght;
            }
            if (GUI.Button(new Rect(halfsc - wdth / 2, sypos, wdth, hght), Lang.Get("spectate_exit"))) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Close);
                StopSpectatorMode();
                return;
            }
            sypos += hght;


            if (dir != 0) {

                IList<ScavPlayer> plrs = ScavPlayer.BodyToPlayerDict.Values.AsReadOnlyList();
                int plramount = plrs.Count();
                for (int i = 0; i < plrs.Count(); i++) {
                    curspectatefolowing += dir;

                    if (curspectatefolowing < 0) {
                        curspectatefolowing = plramount - 1;
                    }
                    if (curspectatefolowing >= plramount) {
                        curspectatefolowing = 0;
                    }

                    ScavPlayer plr = plrs[curspectatefolowing];
                    if (plr.body != null) {
                        if (!spectator_only_alive || plr.body.alive) {
                            Spectator_SetTarget(plr.netBody);

                            break;
                        }
                    }
                }
            }
        }

        static bool _GUI_DoGeneralMPGui() {

            float uisc = UIBullshit.uiScale;
            float wdth2 = 340 * uisc;

            if (Util.TryGetLocalBody(out Body locbody)) {
                if (
                    Spectator.GetCameraFollow() == SpectatorLastTarget
                ) {
                    Spectator.SetCameraFollow(null);
                }



                if (locbody.sleeping && locbody.alive && !Multiplayer.rules.DisableSleep) {
                    if (!Util.IsInPauseMenuOrMainMenu() && !ServerMain.CheckIfEveryoneIsSleeping()) {

                        float wdthpanel3 = wdth2 + 20 * uisc;
                        float wakeupbutton_x = Math.Min(Screen.width * 0.5f + wdth2, Screen.width - wdthpanel3);
                        float wakeupbutton_y = Screen.height * 0.45f - 100 * uisc;

                        int conscoiuscounter = 0;
                        int sleepcounter = 0;
                        foreach (KeyValuePair<Body, ScavPlayer> b in ScavPlayer.BodyToPlayerDict) {
                            if (b.Key.conscious) {
                                conscoiuscounter++;
                            } else {
                                sleepcounter++;
                            }
                        }

                        float fullheight = (100 + 10 + 40 + 10 + 10 + 30 * conscoiuscounter) * uisc;

                        UIBullshit._GUI_9SlicePanel(new Rect(wakeupbutton_x - (wdthpanel3 - wdth2) * 0.5f, wakeupbutton_y, wdthpanel3, fullheight), 0.9f, 0.5f, false, UIBullshit.unscaled_uiBlockNano);
                        wakeupbutton_y += 10 * uisc;

                        int ogfontsize = GUI.skin.label.fontSize;
                        GUI.skin.label.fontSize = (int)(ogfontsize * 1.3f);
                        if (GUI.Button(new Rect(wakeupbutton_x, wakeupbutton_y, wdth2, 100 * uisc), Lang.Get("wake_up"))) {
                            if (locbody.sleeping) {
                                locbody.WakeUp();
                                if (locbody.TryGetNetBody(out NetBody nb)) {
                                    nb.SetNetHealthSyncIgnoreTime(1);
                                }
                            }

                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerWakeTheFuckUpSamurai, true);
                        }
                        GUI.skin.label.fontSize = ogfontsize;

                        wakeupbutton_y += 100 * uisc;
                        wakeupbutton_y += 10 * uisc;
                        {
                            GUI.Label(new Rect(wakeupbutton_x, wakeupbutton_y, wdth2 + 100 * uisc, 40 * uisc), Lang.Get("plr_sleeping_count") + sleepcounter.ToString());
                        }
                        wakeupbutton_y += 40 * uisc;

                        foreach (KeyValuePair<Body, ScavPlayer> b in ScavPlayer.BodyToPlayerDict) {
                            if (b.Key.conscious) {
                                GUI.Label(new Rect(wakeupbutton_x, wakeupbutton_y, wdth2 + 300 * uisc, 40 * uisc), b.Value.playerName + Lang.Get("plr_not_sleeping"));
                                wakeupbutton_y += 30 * uisc;
                            }
                        }
                        wakeupbutton_y += 30 * uisc;
                    }
                    return true;
                } else {


                    if (Net.IsDedicatedServer) { // this is ui shit
                        return true;
                    }


                    ScavPlayer me = ScavPlayer.LocalPlayer;

                    NetBody mine = (me != null && me.body != null) ? me.netBody : null;
                    if (!Util.IsInWoundView() && me != null && me.body != null && me.body.conscious && mine != null) {
                        float pgbkbutton_hght = 80 * uisc;
                        NetBody riding = mine.piggybacking_on;
                        NetBody carried = mine.carrying_person;
                        if (riding != null) {
                            if (UIBullshit._GUI_FocusableButton(new Rect(Screen.width * 0.5f - wdth2 / 2, Screen.height * 0.9f - 10 * uisc, wdth2, pgbkbutton_hght), Lang.Get("piggyback_stop") + "\n" + riding.bodyName, out bool focused)) {
                                mine.StopPiggyback();
                            }
                            interaction_menu_focused = interaction_menu_focused || focused;
                        } else if (carried != null) {
                            if (UIBullshit._GUI_FocusableButton(new Rect(Screen.width * 0.5f - wdth2 / 2, Screen.height * 0.9f - 10 * uisc, wdth2, pgbkbutton_hght), Lang.Get("carry_stop") + "\n" + carried.bodyName, out bool focused)) {
                                carried.SetNetIgnoreTime(0.2f);
                                // host runs the drop before this returns, and that clears the link on mine
                                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestStopCarryPerson);
                                if (carried.piggybacking_on != null) {
                                    carried.StopPiggyback();
                                }
                            }
                            interaction_menu_focused = interaction_menu_focused || focused;
                        }
                    }
                    if (Multiplayer.rules.SpectateWhileUnconscious) {
                        if (locbody.conscious) {
                            return true;
                        }
                    } else {
                        if (locbody.alive) {
                            return true;
                        }
                    }
                }


            }

            return false;
        }



        public static void StartFreecamMode(bool resetpos = true) {
            Con._DEV_FREECAM = true;

            if (resetpos) {
                UIInGame._DEV_FREECAM_CURPOS = PlayerCamera.main.transform.position;
            }

            MoodleManager.main.body = (Util.GetLocalBody());
            PlayerCamera.main.radialOpen = false;
        }

        public static void StartSpectatorMode() {
            Con._DEV_FREECAM = false;

            if (!SpectatorModeActive) {
                SpectatorModeActive = true;

                PlayerCamera.main.endScreen.gameObject.SetActive(false);

                if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null) {
                    ScavPlayer plr = ScavPlayer.LocalPlayer;
                    curspectatefolowingname = plr.playerName;

                    curspectatefolowing = ScavPlayer.BodyToPlayerDict.Values.AsReadOnlyList().IndexOf(plr);

                    if (Spectator.GetCameraFollow() == null) {
                        Spectator_SetTarget(plr.body);
                    }
                } else {
                    curspectatefolowingname = "NOBODY";

                    curspectatefolowing = 0;
                }
            }
        }


        public static void StopSpectatorMode() {
#if TESTING
            log.l("StopSpectatorMode");
#endif

            if (Util.IsInMainMenu()) {
                Con._DEV_FREECAM = false;
                SpectatorModeActive = false;
                return;
            }

            Con._DEV_FREECAM = false;
            if (SpectatorModeActive && !Net.IsDedicatedServer) {
                SpectatorModeActive = false;

                if (PlayerCamera.main.didDeathScreen) {
                    PlayerCamera.main.endScreen.gameObject.SetActive(true);
                }

                Spectator.SetCameraFollow(null);


                MoodleManager.main.body = (Util.GetLocalBody());
            }
        }

        public static void Spectator_SetTarget(Component obj, string custom_name = null) {
            if (SpectatorModeActive) {
                NetBody pb;
                if (custom_name != null) {
                    curspectatefolowingname = custom_name;
                } else if (obj.TryGetComponent(out pb)) {
                    curspectatefolowingname = pb.bodyName;
                } else {
                    curspectatefolowingname = obj.name;
                }

                if (obj.TryGetComponent(out Body booooooooooooooooooooodu)) {
                    MoodleManager.main.body = (booooooooooooooooooooodu);
                }

                Spectator.SetCameraFollow(obj.transform);
                SpectatorLastTarget = obj.transform;

                if (obj.TryGetComponent(out pb)) {
                    if (WoundView.view.gameObject.activeSelf) {
                        PlayerCamera_ToggleWoundView_MultiplayerPatch.WoundView_SetTargetBody(pb.body);
                        WoundView.view.UpdateView();
                        //playerCamera.main.PlayUISound(PlayerCamera.UISoundType.HealthPanelOn);
                    }
                    if (pb.IsPlayer) {
                        curspectatefolowing = ScavPlayer.BodyToPlayerDict.Values.AsReadOnlyList().IndexOf(pb.plr);
                    }
                }


            }
        }








        public static bool DoPlayerInteractionMenuButton(in Rect rect, bool enabled, in string text, ref bool focused, bool continue_drag_item = false, bool accept_drag_item = false) {

            GUIStyle style = FixGuiStyle.Copy(GUI.skin.button, 6);

            GUI.enabled = enabled;
            //if (enabled) {
            //    style.normal.background = UIBullshit.tex_button_normal;
            //    style.focused.background = UIBullshit.tex_button_focus;
            //    style.hover.background = UIBullshit.tex_button_hover;
            //} else {
            //    style.normal.background = UIBullshit.tex_button_active;
            //    style.focused.background = UIBullshit.tex_button_active;
            //    style.hover.background = UIBullshit.tex_button_active;
            //}

            bool result = UIBullshit._GUI_FocusableButton(rect, text, out focused, style) && enabled;

            if (focused) { // show tooltip       krok rule EnableNametags false    krok screenshotmode 
                // force open if have a drag item
                if (enabled) {
                    if (
                        PlayerCamera.main.dragItem != null && !Util.IsInWoundView() //&& !UIUtil.IsPointerOverUIElement()
                    ) {
                        if (continue_drag_item) {
                            return true;
                        }
                        if (accept_drag_item && Input.GetKeyUp(KeyBinds.GetBind("iteminteract"))) {
                            return true;
                        }
                    }
                }
                interaction_menu_focused = true;
            }

            return result;
        }




        static void _GUI_PlayerInteractionGui() {
            if (Net.IsDedicatedServer) { // this is ui shit
                return;
            }

            float uisc = UIBullshit.uiScale;

            float buttonxsize = 100 * uisc;
            float buttonysize = 45 * uisc;

            float menufullsizex = buttonxsize * 4;
            float menufullsizey = buttonysize * 2.5f;


            ScavPlayer me = ScavPlayer.LocalPlayer;


            Vector2 corrected_mouse_pos = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);


            Vector2 newworldpos = (Vector2)interaction_menu_target_body.body.transform.position;
            newworldpos += Vector2.up * 6f;


            Vector2 newscreenpospos = (Vector2)Camera.main.WorldToScreenPoint(newworldpos);
            newscreenpospos = new Vector2(newscreenpospos.x, Screen.height - newscreenpospos.y);

            if (interaction_menu_target_pos == Vector2.zero) {
                interaction_menu_target_pos = newscreenpospos;
            } else {
                //interaction_menu_target_pos = Vector2.Lerp(interaction_menu_target_pos, newscreenpospos, Mathf.Min(0.03f, Time.unscaledDeltaTime) * 0.4f);
            }
            interaction_menu_target_pos = new Vector2(Mathf.Clamp(interaction_menu_target_pos.x, menufullsizex, Screen.width - menufullsizex), Mathf.Clamp(interaction_menu_target_pos.y, menufullsizey, Screen.height - menufullsizey));

            Vector2 pos = interaction_menu_target_pos;



            int ogfontsize = GUI.skin.button.fontSize;
            {
                float fontsize = (UIBullshit.DEFAULT_RETROGAME_FONTSIZE * 0.6f) * UIBullshit.uiScale * UIBullshit.CUSTOM_UI_SCALE_FOR_UIBULLSHIT;
                GUI.skin.button.fontSize = Mathf.RoundToInt(fontsize);
                GUI.skin.label.fontSize = GUI.skin.button.fontSize;
            }


            float curbuttonx = 0;
            float curbuttony = 0;

            bool focused = false;
            string tooltip = null;

            Rect GetNextRect() {
                return new Rect(pos.x + buttonxsize * (-4 * 0.5f + curbuttonx), pos.y + (buttonysize * (-2 * 0.5f + curbuttony)), buttonxsize, buttonysize);
            }

            if (DoPlayerInteractionMenuButton(GetNextRect(), true, Lang.Get("plrint_woundview"), ref focused, true)) {

                if (Util.QuickRaycastInteractionCheck(me.body.transform.position, interaction_menu_target_body.body.transform.position, true)) {
                    PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(interaction_menu_target_body.body);
                } else {
                    PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                }
                StopPlayerInteractionMenu();
                return;
            }
            if (focused) {
                tooltip = ("plrint_woundview_tooltip");
            }
            curbuttonx += 1;

            Item dragitem = PlayerCamera.main.dragItem;

            bool canGiveItem = dragitem != null;
            bool can_access_inv = Multiplayer.rules.NoInventoryLock || !interaction_menu_target_body.body.conscious || canGiveItem;
            if (
                DoPlayerInteractionMenuButton(GetNextRect(), can_access_inv, canGiveItem ? Lang.Get("plrint_give") : Lang.Get("plrint_inventory"), ref focused, false, true
            )) {
                if (canGiveItem) {
                    ClientMain._PLRINT_GiveItem(interaction_menu_target_body, dragitem);
                    PlayerCamera.main.dragItem = null;
                } else {
                    ClientMain._PLRINT_Inventory(interaction_menu_target_body);
                }

                StopPlayerInteractionMenu();
                return;
            }
            if (focused && !canGiveItem) {
                tooltip = can_access_inv ? ("plrint_inv_allowed") : ("plrint_inv_no");
            }
            curbuttonx += 1;

            // dune Feed on replaces Feed when nothing is dragged; dragging keeps normal item Feed/Use
            bool localIsDune = me.body != null && me.body.charType == 2;
            bool showDuneFeedOn = localIsDune && dragitem == null;
            if (showDuneFeedOn) {
                bool can_bite = interaction_menu_target_body.IsPlayer
                    && interaction_menu_target_body.body != null
                    && interaction_menu_target_body.body.alive
                    && interaction_menu_target_body != me.netBody;
                if (DoPlayerInteractionMenuButton(GetNextRect(), can_bite, Lang.Get("plrint_feed_on"), ref focused)) {
                    me.netBody.Bite(interaction_menu_target_body);
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = can_bite ? ("plrint_feed_on_tooltip") : ("plr_dead");
                }
                curbuttonx += 1;
            } else {
                // if have draggedItem and its whitelisted in items that can be used on other players (this excludes tools, allows autopump, also disallow overfeeding, maybe also do a check if the other player is too sad))

                bool can_feed = dragitem != null && (dragitem.Stats.usable || dragitem.Stats.wearable) && ClientMain.ItemCanBeUsedOnThisBody(dragitem, interaction_menu_target_body.body) && interaction_menu_target_body.body.alive;
                if (DoPlayerInteractionMenuButton(
                    GetNextRect()
                    , can_feed
                    , (!can_feed) ? Lang.Get("plrint_useitem") :
                        (dragitem.Stats.wearable ? Lang.Get("plrint_useitem_wearable") :
                        (dragitem.IsEdible() ? Lang.Get("plrint_useitem_food") :
                        (
                            (dragitem.Stats.category == "water"// || dragitem.TryGetComponent(out WaterContainerItem _ )
                        ) ? Lang.Get("plrint_useitem_water")
                        : Lang.Get("plrint_useitem")))
                    )
                    , ref focused
                    , false
                    , true
                )) {

                    Body targetbody = interaction_menu_target_body.body;
                    if (
                        !MedicalSync.IsRefusingHelp(targetbody)// || dragitem.Stats.ignoreDepression // maybe nah?
                    ) {
                        if (ItemSync.TryGetSyncInfo(dragitem, out SyncInfo si)) {
                            si.SetIgnoreTimeForRoundTrip();

                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestUseItem, si.syncId, interaction_menu_target_body.netId, true);
                        }
                        if (Multiplayer.rules.ConsentForFeeding && targetbody.conscious) {
                            PlayerCamera.main.DoAlert(String.Format(Lang.Get("plrint_feeditem_offered"), interaction_menu_target_body.bodyName, PlayerCamera.ItemHoverDescription(dragitem).Item1));
                        } else {
                            if (Net.IsClient) {
                                ItemSync.BetterUseItem(targetbody, dragitem);
                            }
                        }
                    } else {
                        PlayerCamera.main.DoAlert(Lang.Get("deny_refuse"));
                    }

                    PlayerCamera.main.dragItem = null;

                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = !interaction_menu_target_body.body.alive ?
                        ("plr_dead") :
                        (can_feed ?
                            ("plrint_feed_item") :
                            ("plrint_drag_item_here")
                        );
                }
                curbuttonx += 1;
            }

            bool the_guy_is_piggybacking_or_carrying_me = interaction_menu_target_body.piggybacking_on == me.netBody || interaction_menu_target_body.carrying_person == me.netBody;

            {
                bool canHug = !the_guy_is_piggybacking_or_carrying_me
                    && interaction_menu_target_body.IsPlayer
                    && interaction_menu_target_body.body != null
                    && interaction_menu_target_body.body.alive
                    && interaction_menu_target_body.body.conscious;
                if (DoPlayerInteractionMenuButton(GetNextRect(), canHug, Lang.Get("plrint_hug"), ref focused)) {
                    me.netBody.Hug(interaction_menu_target_body);
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = ("plrint_hug_tooltip");
                }
                curbuttonx += 1;
            }

            curbuttonx = 0;
            curbuttony += 1;

            if (Multiplayer.rules.AllowPiggyback) {

                {
                    var can_carry = interaction_menu_target_body.CanBeCarriedBySomeone()
                        && !the_guy_is_piggybacking_or_carrying_me
                        && (me.netBody == null || !MacroFleshRebuilderMp.BlocksCarry(me.netBody));
                    if (DoPlayerInteractionMenuButton(GetNextRect(), can_carry, Lang.Get("plrint_carry"), ref focused)) {

                        ClientMain._PLRINT_Carry(interaction_menu_target_body);

                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = can_carry ?
                            ("plrint_carry_allow") :
                            ("plrint_carry_no")
                        ;
                    }
                    curbuttonx += 1;
                }


                {
                    bool can_piggyback = interaction_menu_target_body.IsPiggybackable() && interaction_menu_target_body.body.overEncumberance < 0.5 && !the_guy_is_piggybacking_or_carrying_me;
                    if (DoPlayerInteractionMenuButton(GetNextRect(), can_piggyback, Lang.Get("plrint_piggyback"), ref focused)) {
                        ClientMain._PLRINT_Piggyback(interaction_menu_target_body);

                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = can_piggyback ?
                            ("plrint_piggyback_allow") :
                            ("plrint_piggyback_no")
                        ;
                    }
                    curbuttonx += 1;
                }
            }


            if (Multiplayer.rules.AllowPush) {
                bool can_push = !the_guy_is_piggybacking_or_carrying_me;
                if (DoPlayerInteractionMenuButton(GetNextRect(), can_push, Lang.Get("plrint_push"), ref focused)) {

                    me.netBody.Push(interaction_menu_target_body);

                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = ("plrint_push_tooltip");
                }
                curbuttonx += 1;
            }

            {
                bool canPat = !the_guy_is_piggybacking_or_carrying_me
                    && interaction_menu_target_body.IsPlayer
                    && interaction_menu_target_body.body != null
                    && interaction_menu_target_body.body.alive
                    && interaction_menu_target_body.body.conscious;
                if (DoPlayerInteractionMenuButton(GetNextRect(), canPat, Lang.Get("plrint_pat"), ref focused)) {
                    me.netBody.Pat(interaction_menu_target_body);
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = ("plrint_pat_tooltip");
                }
                curbuttonx += 1;
            }

            GUI.enabled = true;

            // players name

            Rect nametagrect = new Rect(pos.x - buttonxsize * 2f, pos.y - buttonysize * 1.5f, buttonxsize * 4, buttonysize * 0.5f);
            UIBullshit._GUI_9SlicePanel(nametagrect, 1, 0.8f, true, UIBullshit.unscaled_uiBlockNano);

            Color ogcolr = GUI.skin.label.normal.textColor;
            TextAnchor ogalign = GUI.skin.label.alignment;
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            {
                string nnn;
                if (interaction_menu_target_body.IsPlayer) {
                    ScavPlayer int_plr = interaction_menu_target_body.plr;
                    GUI.skin.label.normal.textColor = int_plr.playerColor;
                    nnn = (interaction_menu_target_body.playerName + "  ID:" + int_plr.playerId.ToString());
                } else {
                    nnn = interaction_menu_target_body.bodyName + "  ID:" + interaction_menu_target_body.netId.ToString();
                    if (log.verbose) {
                        nnn += " (NPC)";
                    }
                }
                GUI.Label(nametagrect, nnn);
            }
            GUI.skin.label.normal.textColor = ogcolr;
            GUI.skin.label.alignment = ogalign;


            if (tooltip != null) {

                UIBullshit._GUI_SetTooltip(Lang.Get(tooltip), null);

            }

            GUI.skin.button.fontSize = ogfontsize;
            GUI.skin.label.fontSize = ogfontsize;

        }



        // single-purpose: called only from OnGUI only when net system is running and is in game 
        static void _GUI_DoMPUserInterface() {



            if (
                Multiplayer.rules.ShowPlayerDirections
            ) {
                _GUI_DoPlayerPointersGui();
            }



            if (Con._DEV_HIDEHUD || Con.IsConsoleOpen()) {
                Util.OpenPauseMenu(false); // close the menu cuz it interferes with console
                StopPlayerInteractionMenu();
                return;
            }


            if (interaction_menu_target_body != null) {
                _GUI_PlayerInteractionGui();
            } else {
                interaction_menu_target_body = null;
                interaction_menu_focused = false;
            }




            if (!Con._DEV_FREECAM) {
                if (SpectatorModeActive) {
                    _GUI_DoSpectatorGui();
                    return;
                } else {
                    if (_GUI_DoGeneralMPGui()) {
                        return;
                    }
                }
            }


            if (!Util.IsInPauseMenuOrMainMenu()) {
                float uisc = UIBullshit.uiScale;
                float wdth2 = 240 * uisc;
                float hght2 = 80 * uisc;
                if (Con._DEV_FREECAM) {
                    if (GUI.Button(new Rect(Screen.width - wdth2, Screen.height - 80 * uisc, wdth2, hght2), Lang.Get("spectate_freecamexit"))) {
                        Util.PlayUISound(PlayerCamera.UISoundType.Close);
                        Con._DEV_FREECAM = false;

                        if (UIInGame.SpectatorModeActive) {
                            (NetBody, float) res = NetBody.GetNearestBody(PlayerCamera.main.transform.position, spectator_only_alive, false);
                            if (res.Item1 != null) {
                                Spectator_SetTarget(res.Item1);
                            }
                        }
                    }
                } else {
                    if (GUI.Button(new Rect(Screen.width - wdth2, Screen.height - 80 * uisc, wdth2, hght2), Lang.Get("spectate"))) {
                        Util.PlayUISound(PlayerCamera.UISoundType.Click);
                        StartSpectatorMode();
                    }
                }
            }

        }









        internal void _GUI_DoInGameUI() {
            if (!FixGuiLayout.Allow()) {
                return;
            }

            interaction_menu_focused = false;

            if (Multiplayer.IsNetworkActiveAndIsInGame()) {
                if (!UIBullshit.WantsMpChrome()) {
                    _GUI_DoMPUserInterface();
                    Together.UI.TeammateList.TeammateListHost.Draw();
                    HeadpatSession.Draw();
                    return;
                }

                RectOffset ogpadding = GUI.skin.button.padding;
                TextClipping ogclipg = GUI.skin.button.clipping;
                try {

                    GUI.skin.button.padding = UIBullshit.ZeroPadding;
                    GUI.skin.button.clipping = TextClipping.Overflow;

                    _GUI_DoMPUserInterface();
                } catch (Exception e) {
                    log.error("ClientMain.OnGUI -> " + e.ToString());
                }
                GUI.skin.button.padding = ogpadding;
                GUI.skin.button.clipping = ogclipg;

                Together.UI.TeammateList.TeammateListHost.Draw();
                HeadpatSession.Draw();
            }
        }


        void UI_Update_Antidispersion() {

            ScavPlayer localsci = ScavPlayer.LocalPlayer;
            if (SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive()) {
                if (localsci != null && localsci.IsAlive() && localsci.levelPlayTime > 3) {

                    antidispersion_active =
                        SharedMain.CheckIfShouldActivateDispersionPunishmentProtocol(localsci, out antidispersion_cur_distance, out int count_plrs_in_range, out int required_count_plrs_in_range);

                    antidispersion_cur_range_count = count_plrs_in_range;
                    antidispersion_cur_required_count = required_count_plrs_in_range;

                    if (antidispersion_warned) {
                        if (!antidispersion_active && antidispersion_cur_distance < 10) {
                            antidispersion_warned = false;
                        }
                    } else if (antidispersion_active) {
                        antidispersion_warned = true;
                        // scare the player - mood drain is host-side; beep stays as the warn cue
                        Util.DoAlert(Lang.Get("antidispersion_activated_alert"));
                        localsci.body.talker.Talk(Locale.GetCharacter("loud"));
                        Sound.Play("beep", localsci.pos, true, false, localsci.body.transform, 0.5f, 2, true, true);
                        PlayerCamera.main.shaker.Shake(5f);
                    }

                } else {
                    antidispersion_active = false;
                    antidispersion_cur_distance = 0;
                }
            }
        }



        bool _UpdateCheckInteractKeybinds_CheckDistNShi(NetBody targetpb) {
            ScavPlayer locplr = ScavPlayer.LocalPlayer;
            Body body_on_cursor = targetpb.body;
            if (Util.QuickRaycastInteractionCheck(locplr.body.transform.position, targetpb.transform.position, true)) {
                if (KM.DistanceCheck(locplr.body, targetpb, SharedMain.max_player_interaction_distance)) {

                    UIInGame.StopPlayerInteractionMenu();
                    return true;
                } else {
                    PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
                }
            } else {
                PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
            }
            return false;
        }

        void _UpdateCheckInteractKeybinds(NetBody targetpb) {
            ScavPlayer locplr = ScavPlayer.LocalPlayer;
            Body body_on_cursor = targetpb.body;
            locplr.TryGetNetBody(out NetBody locpb);
            bool the_guy_is_piggybacking_or_carrying_me = targetpb.piggybacking_on == locpb || targetpb.carrying_person == locpb;

            if (Multiplayer.rules.AllowPush && UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.push))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    locplr.netBody.Push(targetpb);
                }
            } else if (UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.hug))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    locplr.netBody.Hug(targetpb);
                }
            } else if (UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.woundview))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {

                    Util.DoAlert(String.Format(Lang.Get("plrint_req_woundview"), targetpb.bodyName));
                    bool og = PlayerCamera_ToggleWoundView_MultiplayerPatch.IsOpen();

                    PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(targetpb.body);//TODO try commenting this line and see if it still works, and do it only if not IsWoundViewSameAsOG 

                    if (!og) {
                        if (CoopKeybinds.IsWoundViewSameAsOG()) {
                            PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
                        }
                    }

                }
            } else if (UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.inventory))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    ClientMain._PLRINT_Inventory(targetpb);
                }
            } else if (!the_guy_is_piggybacking_or_carrying_me) {
                if (UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.piggyback))) {
                    if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                        ClientMain._PLRINT_Piggyback(targetpb);
                    }
                } else if (UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.carry))) {
                    if (targetpb.CanBeCarriedBySomeone() && (locpb == null || !MacroFleshRebuilderMp.BlocksCarry(locpb))) {
                        if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                            ClientMain._PLRINT_Carry(targetpb);
                        }
                    } else {
                        Util.DoAlert(Lang.Get("alert_cantcarry"));
                    }
                }
            }

        }





        void OGInGameIHandlingBullshit() {

            Vector2 cursorpos = Util.GetCursorWorldPos();

            if (Util.TryGetLocalBody(out Body locbody)) {

                if (ConsoleScript.instance.fullBright && !Con.CanCheat()) {
                    ConsoleScript.instance.fullBright = false;
                }

                if (Con._DEV_FREECAM) {
                    if (
                        !Con.CanFreecam() // cant freecam anymore
                    ) {
                        Con._DEV_FREECAM = false;
                    }
                }



                if (Con._DEV_FREECAM) {

                    PlayerCamera_HandleScreenShaders_MultiplayerPatch.forceDefaultShaderForNextFrame = true;

                } else {

                    if (ConsoleScript.instance.noClip) {
                        //ragdoll while noclipping is glitchy as hell
                        if (!locbody.standing) {
                            locbody.ForceStand();
                        }
                        //prevent fatal falldamage when done
                        locbody.rb.velocity = Vector2.zero;

                        if (!Con.CanCheat()) {
                            Con.UnNoclip();
                        }
                    }

                    _DEV_FREECAM_CURPOS = (Vector2)PlayerCamera.main.body.transform.position;
                }


                if (!Con.CanCheat()) {
                    if (ConsoleScript.instance.fullBright) {
                        ConsoleScript.instance.fullBright = false;
                    }
                } else if (Con._DEV_GODMODE) {

                    locbody.ResetHealth(); // just heal em every frame 

                    //if (MainNetwork.is_server)
                    //    foreach (var hgggg in NetPlayer.ClientIdToPlayerDict.Values)
                    if (Net.IsServer) {
                        foreach (ScavPlayer hgggg in ScavPlayer.AllPlayers) {
                            if (hgggg.body != null) {
                                hgggg.Server_HealCharacter(); // just heal em every frame 
                            }
                        }
                    }
                }
            }



            ScavPlayer locplr = ScavPlayer.LocalPlayer;
            if (Net.IsRunning && locplr != null) {


                if (Util.IsWorldGenerated()) {
                    if (!Con.IsConsoleOpen() && UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.pointfingerat))) {

                        Vector2 pos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);

#if !RELEASE
                        if (log.verbose) {
                            log.l($"Sending PlayerPointFingerAt! {pos}");
                        }
#endif

                        bool isfast = (Time.realtimeSinceStartupAsDouble - locplr.is_pointingfingeratTIME) < 0.2f;
                        byte t = (byte)(isfast ? 1 : 0);


                        locplr.PointFingerAt(pos, t);

                        NetDataWriter writer = Net.CreateWriter(NetmsgId.PlayerPointFingerAt);

                        writer.Put((Vector2)pos);
                        writer.Put(t);

                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

                        // WRITER END;
                    }






                    locbody = locplr.body;
                    if (locbody != null) {

                        {
                            Body body_on_cursor = Util.GetBodyOnPos(cursorpos);

                            if (
                                body_on_cursor != null
                                && !Con.IsConsoleOpen()
                                && !PlayerCamera.main.woundView.activeSelf
                                && locplr.IsConscious()
                                && !body_on_cursor.IsBodyLocal()
                                && body_on_cursor.TryGetComponent(out NetBody targetpb)
                            ) {
                                _UpdateCheckInteractKeybinds(targetpb);

                            }
                        }

                    }
                }
            }
        }


        void Awake() {
            main = this;
        }

        void Update() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Ui);

            last_action_cooldown_countdown -= Time.deltaTime;


            OGInGameIHandlingBullshit();

            if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
                Destroy(this);
                return;
            }


            if (SpectatorModeActive) {

                if (Input.GetMouseButtonDown(0)) {

                    Vector2 cursorpos = Util.GetCursorWorldPos();
                    Body body_on_cursor = Util.GetBodyOnPos(cursorpos);

                    if (
                        body_on_cursor != null
                        && !Con.IsConsoleOpen()
                        && !PlayerCamera.main.woundView.activeSelf
                        && !UIUtil.IsPointerOverUIElement()
                        && body_on_cursor.TryGetComponent(out NetBody targetpb)
                    ) {
                        Spectator_SetTarget(targetpb);
                    }
                }

            }



            ScavPlayer localsci = ScavPlayer.LocalPlayer;
            if (localsci != null) {

                if (Util.TryGetLocalBody(out Body locbody)) {
                    if (interaction_menu_target_body != null) {

                        ScavPlayer me = ScavPlayer.LocalPlayer;

                        if (
                            (interaction_menu_target_body.body == null || me.body == null || !me.body.conscious)
                            || (!KM.DistanceCheck(PlayerCamera.main.body.transform.position, interaction_menu_target_body.body.transform.position, SharedMain.max_player_interaction_distance))
                            || Util.IsInWoundView()
                        ) {
                            StopPlayerInteractionMenu();
                        }

                    }
                }

                UI_Update_Antidispersion();
            }
        }


        void LateUpdate() {

        }



        public static void StartPlayerInteractionMenu(NetBody target) {
            if (target != interaction_menu_target_body && target.body != null && PlayerCamera.main.body != null && PlayerCamera.main.body.conscious && !SpectatorModeActive) {

                if (!KM.DistanceCheck(PlayerCamera.main.body, target.body, SharedMain.max_player_interaction_distance)) {
                    PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
                    return;
                }
                if (!Util.AccurateRaycastInteractionCheckObstruction(PlayerCamera.main.body, target.body, true)) {
                    PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                    return;
                }

                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);

                interaction_menu_target_body = target;
            }
        }
        public static void StopPlayerInteractionMenu() {
            interaction_menu_target_pos = Vector2.zero;
            if (interaction_menu_target_body != null) {
                interaction_menu_target_body = null;
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Close);
            }
            interaction_menu_focused = false;
        }




    }





    [HarmonyPatch(typeof(UIUtil), nameof(UIUtil.IsPointerOverUIElement), new Type[] { typeof(List<RaycastResult>) })]
    static class UIUtil_IsPointerOverUIElement_MultiplayerPatch {
        static void Postfix(UIUtil __instance, ref bool __result) {
            if (Net.IsRunning && !__result) {
                __result =
                    UIInGame.interaction_menu_focused
                    || UIBullshit.IS_CURSOR_OVERLAPPING
                    || Chat.MouseIsInteractingWithScrollbar();
            }
        }
    }





    class LoadingScreenSpecimenDuplicator : MonoBehaviour {
        Vector2 OG_creaturepos = Vector2.zero;
        readonly List<RectTransform> clones = new();
        readonly List<int> cloneSpecies = new();
        string _rosterKey = "";

        void Awake() {
            if (WorldGeneration.world != null && WorldGeneration.world.genRects != null && WorldGeneration.world.genRects.Length > 2) {
                OG_creaturepos = WorldGeneration.world.genRects[2].anchoredPosition;
            }
        }

        void LateUpdate() {
            if (!gameObject.activeInHierarchy || WorldGeneration.world == null) {
                return;
            }
            WorldGeneration world = WorldGeneration.world;
            if (world.genRects == null || world.genRects.Length <= 2) {
                return;
            }

            // vanilla Update paints genRects[2] from local body.charType + Color.white every frame.
            // keep it disabled so only one-per-player clones show (otherwise late-join Dune = 2 Dunes).
            HideOgCreature();

            string key = BuildRosterKey();
            if (key != _rosterKey) {
                RebuildClones();
                _rosterKey = key;
            }

            OG_creaturepos = world.genRects[2].anchoredPosition;

            bool surfacePod = world.biomeDepth == 0 && world.biomeOverride == WorldGeneration.OverrideSceneType.None;
            Sprite[] pods = world.podImages;
            Color tint = Color.white;
            if (!surfacePod && world.layerGenerationCharColors != null && world.layerGenerationCharColors.Length > 0) {
                int di = Mathf.Clamp(world.biomeDepth, 0, world.layerGenerationCharColors.Length - 1);
                tint = world.layerGenerationCharColors[di];
            }

            Vector2 offsetthing = new Vector2(100f, 25f) * PlayerCamera.uiScale;
            for (int i = 0; i < clones.Count; i++) {
                RectTransform rt = clones[i];
                if (rt == null) {
                    continue;
                }
                ApplySpeciesSprite(rt.GetComponent<Image>(), cloneSpecies[i], pods, surfacePod, tint);
                float progress = (((float)i + 0.5f) - ((float)clones.Count * 0.5f));
                rt.anchoredPosition = OG_creaturepos - offsetthing * progress;
            }
        }

        void HideOgCreature() {
            Image img = og_creature_rect != null ? og_creature_rect.GetComponent<Image>() : null;
            if (img == null) {
                return;
            }
            img.enabled = false;
            Color c = img.color;
            c.a = 0f;
            img.color = c;
        }

        static string BuildRosterKey() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                if (plr == null) {
                    continue;
                }
                sb.Append(plr.playerId.id).Append(':').Append(SpeciesOf(plr)).Append(';');
            }
            return sb.ToString();
        }

        static void ApplySpeciesSprite(Image img, int chr, Sprite[] pods, bool surfacePod, Color tint) {
            if (img == null || pods == null || pods.Length < 3) {
                return;
            }
            chr = Mathf.Clamp(chr, 0, 2);
            int half = pods.Length / 2;
            int idx = surfacePod ? chr : chr + half;
            if (idx < 0 || idx >= pods.Length || pods[idx] == null) {
                return;
            }
            img.sprite = pods[idx];
            img.color = tint;
            img.enabled = true;
        }

        static int SpeciesOf(ScavPlayer plr) {
            if (plr == null) {
                return 0;
            }
#if FULLGAME
            if (plr.body != null) {
                return CharacterSpawn.ClampChar(plr.body.charType);
            }
            if (plr.TryGetNetBody(out NetBody nb) && nb.syncBody != null) {
                return CharacterSpawn.ClampChar(nb.syncBody.characterSelected.Value);
            }
            if (plr.IsLocal) {
                return CharacterSpawn.ClampChar(PreRunScript.characterSelected);
            }
            return CharacterSpawn.ClampChar(plr.selectedCharacter);
#else
            return Mathf.Clamp(plr.selectedCharacter, 0, 2);
#endif
        }

        static RectTransform og_creature_rect => WorldGeneration.world.genRects[2];

        void OnEnable() {
            _rosterKey = "";
            RebuildClones();
        }

        void RebuildClones() {
            if (WorldGeneration.world == null || WorldGeneration.world.genRects == null || WorldGeneration.world.genRects.Length <= 2) {
                return;
            }
            GameObject og_creature = og_creature_rect.gameObject;

            for (int i = 0; i < clones.Count; i++) {
                if (clones[i] != null) {
                    Destroy(clones[i].gameObject);
                }
            }
            clones.Clear();
            cloneSpecies.Clear();

            WorldGeneration world = WorldGeneration.world;
            bool surfacePod = world.biomeDepth == 0 && world.biomeOverride == WorldGeneration.OverrideSceneType.None;
            Sprite[] pods = world.podImages;

            foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                if (plr == null) {
                    continue;
                }
                int chr = SpeciesOf(plr);
                GameObject n = Instantiate(og_creature);
                OnAddDuplicated(n, plr, chr);
                ApplySpeciesSprite(n.GetComponent<Image>(), chr, pods, surfacePod, Color.white);
            }
            HideOgCreature();
        }

        void OnAddDuplicated(GameObject obj, ScavPlayer plr, int chr) {
            RectTransform rt = obj.GetComponent<RectTransform>();
            obj.name = "GenCharacter_" + plr.playerName + "_" + chr;
            rt.SetParent(og_creature_rect.parent);
            rt.sizeDelta = Vector2.zero;
            rt.SetSiblingIndex(og_creature_rect.GetSiblingIndex());
            clones.Add(rt);
            cloneSpecies.Add(chr);
            Image img = obj.GetComponent<Image>();
            if (img != null) {
                img.enabled = true;
            }
        }
    }
}
