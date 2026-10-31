using HarmonyLib;
using CasualtiesTogetherUtils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using BepInEx;
using KrokV5Optimization;

namespace Together;


// !!! WARNING !!!  UTTER SCHIZO CODE



[AttributeUsage(AttributeTargets.Field)]
public abstract class SettingDeclarerThingyBaseAttribute : Attribute {
    public string n;

    public object default_value;

    public bool allow_saving = true;
    
    public string GetPrefKey(FieldInfo f) {
        return "KrokoshaMultiplayer_Setting_" + f.DeclaringType + "_" + f.Name;
    }
    public virtual void Save(FieldInfo f, object v) { 
        throw new NotImplementedException("SettingDeclarerThingyBaseAttribute.Save: Bro... thats not intended to be used! override ts!");
    }
    public virtual void Get(FieldInfo f) {
        throw new NotImplementedException("SettingDeclarerThingyBaseAttribute.Get:  Bro... thats is not intended to be used! override ts!");
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class SettingDeclarerThingyFloatAttribute : SettingDeclarerThingyBaseAttribute {
    public float mi;
    public float ma;
    public string postfix = "";

    public SettingDeclarerThingyFloatAttribute(float min, float max, string name ) {
        mi = min;
        ma = max;
        n = name;
    }
    public override void Save(FieldInfo f, object v) {
        var newval = (float)v;

        if (allow_saving)
            SettingsJsonThing.json.SetFloat(this.GetPrefKey(f), newval);

        f.SetValue(null, newval);
    }
    public override void Get(FieldInfo f) {
        if (!allow_saving) return;
        f.SetValue(null, SettingsJsonThing.json.GetFloat(this.GetPrefKey(f), (float)this.default_value));
    }
    public string GetDisplayString(FieldInfo f) {
        var attr = f.GetAttribute<SettingDeclarerThingyFloatAttribute>();
        var v = (float)f.GetValue(null);
        return v.ToString("0.0") + attr.postfix;
    }



}
[AttributeUsage(AttributeTargets.Field)]
public class SettingDeclarerThingyBoolAttribute : SettingDeclarerThingyBaseAttribute {
    
    public SettingDeclarerThingyBoolAttribute( string name) {
        n = name;
    }
    public override void Save(FieldInfo f, object v) {
        
        var newval = (bool)v;

        if (allow_saving)
            SettingsJsonThing.json.SetBool(this.GetPrefKey(f), newval);
        
        f.SetValue(null, newval);
    }

    public override void Get(FieldInfo f) {
        if (!allow_saving) return;
        f.SetValue(null, SettingsJsonThing.json.GetBool(this.GetPrefKey(f), (bool)this.default_value));
    }
}
[AttributeUsage(AttributeTargets.Field)]
public class SettingDeclarerThingyChoiceAttribute : SettingDeclarerThingyBaseAttribute {

    public string[] choices;
    public SettingDeclarerThingyChoiceAttribute(string name, string[] choices) {

        n = name;
        this.choices = choices;

    }
    public override void Save(FieldInfo f, object v) {
       
        var newval = Convert.ToInt32(v);

        if (allow_saving)
            SettingsJsonThing.json.SetInt(this.GetPrefKey(f), newval);
        
        f.SetValue(null, Convert.ChangeType(newval,f.FieldType));
    }

    public override void Get(FieldInfo f) {
        if (!allow_saving) return;
        f.SetValue(null, 
            Convert.ChangeType(
                SettingsJsonThing.json.GetInt(this.GetPrefKey(f), Convert.ToInt32(this.default_value))
                , f.FieldType
            )
        );
    }
}



public static class Amogus {
    //What is this
}



public static class UIMainMenu {
    public static bool is_client => Net.IsClient;
    public static bool is_server => Net.IsServer;
    public static bool is_dedicated_server => Net.IsDedicatedServer;

    public static bool SINK_KEYBOARD_INPUT = true;

    public class MenuTab {
        public string name;
        public Action<Rect> gui_func;
        public Action on_open;

        public object data1; 
        public object data2;
        public object data3;
        public object data4;
        public object data5;
    }
    public class SettingsTab : MenuTab {
        internal static List<SettingsTab> tabs = new List<SettingsTab>();
        
        public Type settings_origin;
        public Action on_reset;

        public SettingsTab() {
            tabs.Add(this);
        }
    }

    public static float GetMenuUIScale() {
        return UIBullshit.uiScale;
    }

    public static bool IsOpen() {
        if (UIBullshit.main == null) {
            return false;
        }
        //if (UIBullshit.main.TryGetComponent(out UIMainMenu mm)) {
        //    return mm.mainmenu_open;
        //}
        
        return mainmenu_open;
    }
    public static bool IsInSettings() {
        return IsOpen() && current_tab == menutab_settings;
    }


    public static bool mainmenu_open { get; private set; }
    public static void SetOpen(bool open_or_nah) {
        if (open_or_nah != mainmenu_open) {
            if (open_or_nah ) {
                Application.runInBackground = true;
                RestoreLastRulePreset();
                if (UIManager.runSettingsMenu != null) {
                    UIManager.runSettingsMenu.SetActive(false);
                    if (Con.IsConsoleOpen()) { Con.con.ToggleActiveState(); }
                }

                Util.PlayUISound(PlayerCamera.UISoundType.Click);
            } else {
                Util.PlayUISound(PlayerCamera.UISoundType.Close);
            }
            if (UIManager.MP_MENU_BUTTON != null) {
                var ttttt = UIManager.MP_MENU_BUTTON.GetComponentInChildren<TextMeshProUGUI>();
                if (ttttt != null) {
                    if (open_or_nah) {
                        ttttt.text = Lang.Get("mainmenu_mainbutton_close");
                    } else {
                        ttttt.text = Lang.Get("mainmenu_mainbutton");
                    }
                }
            }
            OnMenuTabChange();
        }
        if (open_or_nah) {
            Util.OpenBrightnessPanel(false);
        }
        mainmenu_open = open_or_nah;
    }

    static void _GUI__PanelInfo() {
        {
            var mode = (Net.IsServer ? (Net.IsDedicatedServer ? Lang.Get("netmode_server") : Lang.Get("netmode_host")) : Lang.Get("netmode_client"));

            GUILayout.Label("   " + Lang.Get("netmode") + mode);
        }
        {
            if (!Net.IsConnecting) {
                if (Net.IsServer) {
                    GUILayout.Label("   " + Lang.Get("netpanel_avgping") + Math.Round(ServerMain.AVERAGE_PING));
                } else {
                    if (ScavPlayer.LocalPlayer != null) {
                        GUILayout.Label("   " + Lang.Get("netpanel_ping") + (ClientMain.LOCAL_PING));
                    } else {
                        GUILayout.Label(" ");
                    }
                }
            } else {
                GUILayout.Label(" ");
            }
        }
    }
    static void _GUI__PlrListDrawPlr(ScavPlayer plr) {
        var maxTagSizeX = PANEL_SIZE_X * GetMenuUIScale() * 0.9f;
        var curXOffset = GetMenuUIScale() * 2f;
        GUI.skin.label.normal.textColor = Color.white;

        GUILayout.BeginHorizontal(GUILayout.Width(maxTagSizeX));
        {

            GUI.skin.label.wordWrap = false;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            var txt = $"{plr.playerId}: {plr.playerName}";
            var txtcontent = FixGuiContent.Content(txt, 11);

            var textsize = GUI.skin.label.CalcSize(txtcontent);

            foreach (var item in plr.KnownUserTagIcons) {
                GUILayout.Label(item.texture, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));
                curXOffset += textsize.y;
            }

            var profilepic = plr.IconAnySmallToLarge;
            if (profilepic != null) {
                GUILayout.Label(profilepic, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));

                if (plr.body != null && !plr.body.alive && Multiplayer.rules.TrackPlayerDeaths) { // draw the death cross 
                    var rect = GUILayoutUtility.GetLastRect().ShrinkBorder(-1 * GetMenuUIScale());
                    GUI.DrawTexture(rect, CoopModAssets.cross.texture, ScaleMode.StretchToFill, true, 1, Color.red, 0, 0);
                }
            } else {
                
            }
            if (Voicechat.VCRULE_enabled && plr.vc_output != null && CoopModAssets.voicechaticon != null) {
                var voicechat_transp_color = plr.vc_output.CalculateVoiceChatIconColor(out var realcurvcvol);
                if (realcurvcvol > 0 || (plr.IsLocal && Voicechat.IS_RECORDING)) {
                    
                    GUI.color = voicechat_transp_color;

                    GUILayout.Label(CoopModAssets.voicechaticon.texture, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));

                    GUI.color = Color.white;
                } else {
                    GUILayout.Space(textsize.y);
                }
                curXOffset += textsize.y;
            } else {
                //GUILayout.Space(textsize.y);
            }

            GUI.skin.label.normal.textColor = plr.playerColor;

            //if (!MainNetwork.server_info.is_dedicated_server &&  plr.clientId == MainNetwork.server_info.host_client_id )   txt += " [HOST]";

            var pingtext = $"ping: " + plr.ping_as_ms.ToString();
            var pingtextsize = GUI.skin.label.CalcSize(FixGuiContent.Content(" " + pingtext, 12));


            GUILayout.Label(txt, GUILayout.MaxWidth(maxTagSizeX * 0.97f - (curXOffset + pingtextsize.x)));
            GUILayout.FlexibleSpace();
            GUI.skin.label.alignment = TextAnchor.UpperRight;
            GUILayout.Label(pingtext);

        }
        GUILayout.EndHorizontal();
    }

    static Vector2 scrollbullshit = Vector2.zero;
    public static void _GUI_DrawSideMenuWithPlayerList() {
        if (!Net.IsRunning) return;
        if (!UIBullshit.IsAnyMenuOpen()) return;
        var ingame = Util.IsInWorld();

        var og_align = GUI.skin.label.alignment;
        GUI.skin.label.alignment = TextAnchor.UpperLeft;

        var mainrect = UIBullshit.ScaleRect(0, 1080 * 0.5f - PANEL_SIZE_Y * 0.5f, PANEL_SIZE_X, PANEL_SIZE_Y, ingame);
        UIBullshit._GUI_9SlicePanel(
            mainrect, ingame ? 1 : 0.7f, 0.6980392f, true, UIBullshit.unscaled_uiBlockNano
        );

        var contentrect = mainrect.ShrinkBorder(UIBullshit.uiBlockNanoBorderSize);

        GUILayout.BeginArea(contentrect);

        var ogfixedHeight = GUI.skin.label.fixedHeight;
        var ogfontsize = GUI.skin.label.fontSize;
        var ogstretchHeight = GUI.skin.label.stretchHeight;
        var ogalign = GUI.skin.label.alignment;
        var ogwordwrap = GUI.skin.label.wordWrap;
        var ogmargin = GUI.skin.label.margin;
        GUI.skin.label.alignment = TextAnchor.UpperCenter;
        GUI.skin.label.wordWrap = false;

        GUI.skin.label.margin = new RectOffset(0, 0, 0, 0);
        GUI.skin.label.fontSize = (int)(ogfontsize * 1.3f);
       
        GUILayout.Label($"{Lang.Get("netpanel_title")} v{Plugin.FULL_MOD_VERSION}", GUILayout.Height(GUI.skin.label.fontSize*1.75f));

        GUI.skin.label.fontSize = ogfontsize;
        GUI.skin.label.alignment = ogalign;

        _GUI__PanelInfo();

        var should_do_buttons_usable = Util.IsInWorld() && !UIMainMenu.IsOpen();

        if (should_do_buttons_usable) {
            if (GUILayout.Button(Lang.Get("netpanel_settings"))) {
                if (mainmenu_open && Util.IsInWorld()) {
                    Util.OpenBrightnessPanel(true);
                    SetOpen(false);
                } else {
                    SetOpen(!mainmenu_open);
                    if (mainmenu_open) {
                        UIMainMenu.current_tab = menutab_settings;
                    }
                }
            }
        } else {
            GUILayout.Label(" ");
        }
            

        

        if (Net.IsConnected ) { // draw player list

            GUILayout.BeginVertical();

            GUI.skin.label.fontSize = (int)(ogfontsize * 0.9f);
            try {
                GUILayout.BeginHorizontal();
                {
                    var connectedplrs = Lang.Get("netpanel_connectedplrs") + ScavPlayer.ClientIdToPlayerDict.Count + "/" + Multiplayer.rules.PLAYER_COUNT_LIMIT;

                    if (should_do_buttons_usable) {
                        if (GUILayout.Button(
                            connectedplrs
                        )) {
                            SetOpen(true);
                            UIMainMenu.current_tab = menutab_directconnect;
                            UIMainMenu._CONNECTIONSECONDPANEL_TOOLBAR = 0;
                        }
                    } else {
                        GUILayout.Label(connectedplrs);
                    }
                }
                GUILayout.EndHorizontal();

                scrollbullshit = GUILayout.BeginScrollView(scrollbullshit, GUILayout.ExpandHeight(true));
                {
                    foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
                        _GUI__PlrListDrawPlr(plr);
                    }
                }
                GUI.skin.label.alignment = og_align;
                GUILayout.EndScrollView();
            } catch (Exception e) {
                log.error("_GUI_DrawSideMenuWithPlayerList PLAYER LIST:\n" + e.ToString());
            }

            GUI.skin.label.normal.textColor = UnityEngine.Color.white;
            GUI.skin.label.fontSize = ogfontsize;

            GUILayout.EndVertical();
        }


        GUI.skin.label.alignment = TextAnchor.UpperLeft;

        GUILayout.FlexibleSpace();


        if ( (Time.realtimeSinceStartupAsDouble - Multiplayer.lastMultiplayerStatusMessageChangeTime < 10.0 )) {

            GUI.skin.label.wordWrap = true;
            GUI.skin.label.stretchHeight = true;
            GUI.skin.label.fontSize = (int)(ogfontsize*0.75f);

            var txt = Lang.Get("netpanel_laststatus") + "\n" + Multiplayer.multiplayerStatusMessage;

            GUILayout.Label(txt);

            GUI.skin.label.wordWrap = ogwordwrap;
            GUI.skin.label.stretchHeight = ogstretchHeight;
        }


        GUILayout.FlexibleSpace();


        try {
            {
                var half = contentrect.width * 0.7f;

                GUI.skin.label.fontSize = ogfontsize;
                GUI.skin.label.wordWrap = false;
                GUI.skin.label.alignment = TextAnchor.UpperLeft;

                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"PACKET STABILITY: {(Net.IsClient ? ClientMain.MY_CONNECTION_QUALITY.ToString() : ServerMain.AVG_CONNECTION_QUALITY.ToString())}%");

                    if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {

                        UIBullshit._GUI_SetTooltip("", UIBullshit.net_debug_msg);
                    }

                    GUILayout.EndHorizontal();
                }

                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("SERVER TPS: " + ClientMain.SERVER_TPS.ToString(), GUILayout.Width(half));
                    GUILayout.Label("FPS: " + ClientMain.SERVER_FPS.ToString());
                    GUILayout.EndHorizontal();
                }

                if (Net.IsClient) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("LOCAL TPS: " + ServerMain.CURRENT_TPS.ToString(), GUILayout.Width(half));
                    GUILayout.Label("FPS: " + ServerMain.CURRENT_FPS.ToString());
                    GUILayout.EndHorizontal();
                }
                GUI.skin.label.wordWrap = ogwordwrap;

            }
        } catch (Exception e) {
            log.error($"_GUI_DrawSideMenuWithPlayerList FOOTER: ({Event.current.ToString()})\n{e.ToString()}" );
        }



        GUILayout.EndArea();

        GUI.skin.label.fontSize = ogfontsize;
        GUI.skin.label.stretchHeight = ogstretchHeight;
        GUI.skin.label.margin = ogmargin;

    }

    public static float PANEL_SIZE_X = 350f;
    public static float PANEL_SIZE_Y = 750f;

    //public static float MENU_SIZE_X = 1148.91f;
    //public static float MENU_SIZE_Y = 611.41f;
    public static float MENU_SIZE_X = 1500.0f;
    public static float MENU_SIZE_Y = 800.0f;

    public static List<string> AllowedMods = new List<string> {
        "RuntimeUnityEditor",
    };

    internal static void _GUI_DrawMPMainMenu() {
        if (!mainmenu_open) return;
        if (Util.IsInWorld()) {
            if (PlayerCamera.main != null) { // TEMP? FOR MOVABLE WINDOWS ALLOW TS?
                PlayerCamera.main.radialOpen = false;
                if (PlayerCamera.main.tradeMenu.activeSelf) {
                    PlayerCamera.main.ToggleTradeMenu();
                }
                if (Util.IsInWoundView()) {
                    PlayerCamera.main.ToggleWoundView();
                }
                PlayerCamera.main.craftingPanel.SetActive(false);
            }
        }

        // x 1148.91   y 611.41
        Rect mainpanelcontentrect;
        Rect mainpanelrect;
        {
            var scaled = new Vector2(MENU_SIZE_X, MENU_SIZE_Y) * GetMenuUIScale();
            if (scaled.x > Screen.width) {
                scaled = scaled * (Screen.width / scaled.x);
            }
            mainpanelrect = new Rect(0,0, scaled.x, scaled.y);
            mainpanelrect.center = UIBullshit.GetHalfScreenSize();

            UIBullshit._GUI_9SlicePanel(mainpanelrect, 1, 0.6980392f, true, UIBullshit.unscaled_uiBlockNano);

            mainpanelcontentrect = mainpanelrect.ShrinkBorder(UIBullshit.uiBlockSmallBorderSize);

        }
        {
            var topbarsizey = GetMenuUIScale() * 56f;
            var topbar = new Rect(mainpanelrect);
            topbar.yMin -= topbarsizey;
            topbar.yMax = mainpanelrect.yMin;// + 3f * GetMenuUIScale();
            UIBullshit._GUI_9SlicePanel(
                topbar, 1, 0.6980392f, true, UIBullshit.unscaled_uiBlockNano
            );
            UIBullshit.CheckCursorOverlap(topbar);

            var temp_label_style = new GUIStyle(GUI.skin.label);
            var temp_button_style = new GUIStyle(GUI.skin.button);

            var ogfixedHeight = GUI.skin.label.fixedHeight;
            var ogfontsize = GUI.skin.label.fontSize;
            var ogstretchHeight = GUI.skin.label.stretchHeight;
            var ogalign = GUI.skin.label.alignment;
            var ogwordwrap = GUI.skin.label.wordWrap;
            var ogmargin = GUI.skin.label.margin;
            temp_label_style.alignment = TextAnchor.MiddleCenter;
            temp_label_style.fontSize = (int)(ogfontsize * 1.5f);
            GUI.Label(topbar, Lang.Get("mainmenu_settings"), temp_label_style);



            var exitbuttonrect = (topbar).ShrinkBorder(UIBullshit.uiBlockSmallBorderSize);
            exitbuttonrect.xMin = exitbuttonrect.xMax - exitbuttonrect.height;
            UIBullshit._GUI_SetButtonSkinTexture(temp_button_style, false);
            if (GUI.Button(exitbuttonrect, "X", temp_button_style)) {
                SetOpen(false);
            }

        }

        {
            var plugs = Multiplayer.GetBepinPlugins();
            var metaNames = plugs.Select(x => x.Metadata.Name).ToList();



            if (metaNames.Contains("Krokosha_MP_CU")) {


                GUILayout.BeginArea(mainpanelcontentrect);

                {
                    var ogwordwrap = GUI.skin.label.wordWrap;
                    var ogfontsize = GUI.skin.label.fontSize;
                    GUI.skin.label.fontSize = (int)(ogfontsize * 1.5f);
                    GUI.skin.label.wordWrap = true;

                    var text1 = "Please delete old KrokMP folder.";
                    var height = GUI.skin.label.CalcSize(new GUIContent(text1));
                    GUILayout.Label(text1, GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.Label("MP mod has been renamed after this update", GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.Label("Go to CasualtiesUnknown/BepInEx/plugins/", GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Take me there and close the game")) {
                        PreRunScript.OpenInFileBrowser(BepInEx.Paths.PluginPath);
                        Util.DelayCallLambda(1, () => {
                            Application.Quit();
                        });
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.FlexibleSpace();

                    GUI.skin.label.fontSize = ogfontsize;
                    GUI.skin.label.wordWrap = ogwordwrap;
                }

                GUILayout.EndArea();
                return;
            }


#if PLAYTEST
            metaNames.RemoveAll(x => !AllowedMods.Contains(x));
            if (metaNames.Count > 0) {

                GUILayout.BeginArea(mainpanelcontentrect);

                {
                    var ogwordwrap = GUI.skin.label.wordWrap;
                    var ogfontsize = GUI.skin.label.fontSize;
                    GUI.skin.label.fontSize = (int)(ogfontsize * 1.5f);
                    GUI.skin.label.wordWrap = true;

                    var text1 = "You're playing a playtest build.";
                    var height = GUI.skin.label.CalcSize(new GUIContent(text1));
                    GUILayout.Label(text1, GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.Label("Please delete all your other mods and restart", GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.Label("(Or if you're a modder add Metadata.Name to Together.UIMainMenu.AllowedMods)", GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.Label("Go to CasualtiesUnknown/BepInEx/plugins/", GUILayout.Height(height.y), GUILayout.ExpandWidth(true));

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Take me there and close the game")) {
                        PreRunScript.OpenInFileBrowser(BepInEx.Paths.PluginPath);
                        Util.DelayCallLambda(1, () => {
                            Application.Quit();
                        });
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.FlexibleSpace();

                    GUI.skin.label.fontSize = ogfontsize;
                    GUI.skin.label.wordWrap = ogwordwrap;
                }

                GUILayout.EndArea();
                return;
            }
#endif

        }

        {
            var tabsY = GetMenuUIScale() * 70f;
            var tabspanel = mainpanelcontentrect.ShrinkBorder(-1 * GetMenuUIScale());
            tabspanel.yMin += mainpanelcontentrect.height - tabsY;

            UIBullshit._GUI_9SlicePanel(tabspanel, 0.5f, 0.6f, true, UIBullshit.unscaled_uiBlockNano);

            mainpanelcontentrect.yMax = tabspanel.yMin - UIBullshit.uiBlockNanoBorderSize;

            {
                var horizontal = tabspanel.ShrinkBorder(GetMenuUIScale() * 8);

                GUILayout.BeginArea(horizontal);
                GUILayout.BeginHorizontal();
                {

                    var buttwidth = horizontal.width * (1f / (ALL_MENU_TABS.Count + 1.5f));

                    var ogfontsize = GUI.skin.label.fontSize;
                    GUI.skin.label.fontSize = (int)(ogfontsize * 1.1f);

                    for (int i = 0; i < ALL_MENU_TABS.Count; i++) {
                        var tab = ALL_MENU_TABS[i];

                        if (current_tab == tab) {
                            GUI.backgroundColor = Color.white;
                        } else {
                            GUI.backgroundColor = Color.white * 0.8f;
                        }
                        if (GUILayout.Button(Lang.Get(tab.name), GUILayout.MaxWidth(buttwidth), GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(true))) {
                            current_tab = tab;
                            if (current_tab.on_open != null)
                                current_tab.on_open();

                            OnMenuTabChange();
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                    }

                    // render about
                    {
                        GUILayout.FlexibleSpace();

                        if (current_tab == menutab_about) {
                            GUI.backgroundColor = Color.white;
                        } else {
                            GUI.backgroundColor = Color.white * 0.8f;
                        }
                        if (GUILayout.Button(Lang.Get(menutab_about.name), GUILayout.MaxWidth(buttwidth), GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(true))) {
                            current_tab = menutab_about;
                            if (current_tab.on_open != null)
                                current_tab.on_open();

                            OnMenuTabChange();
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                    }
                    GUI.skin.label.fontSize = ogfontsize;
                    GUI.backgroundColor = Color.white;

                }
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
            }
        }



        GUILayout.BeginArea(mainpanelcontentrect);

        if (current_tab != null && current_tab.gui_func != null) {
            try {
                current_tab.gui_func(mainpanelcontentrect);
            } catch (Exception ex) {
                log.error($"MENU TAB RENDER ERROR: {current_tab.name}\n{ex.ToString()}");
            } 
        } else {
            var ogfontsize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = ogfontsize + ogfontsize;
            GUILayout.Label("No tab selected.", GUILayout.ExpandHeight(true),GUILayout.ExpandWidth(true));
            GUI.skin.label.fontSize = ogfontsize;
        }

        GUILayout.EndArea();


        {
            if (!String.IsNullOrEmpty(_COLOR_ERRORS)) {
                _COLOR_ERRORS_RECT = GUIUtility.ScreenToGUIRect(_COLOR_ERRORS_RECT);
                GUI.color = Color.red;

                GUI.Label(_COLOR_ERRORS_RECT, _COLOR_ERRORS);
                GUI.color = Color.white;
                _COLOR_ERRORS = null;
            }
        }
    }

    public static MenuTab menutab_directconnect = new MenuTab {
        name = "mmtb_directconnect",
        on_open = () => { 
            _COLOR_EDITING = null; 
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety = false;
            RestoreLastRulePreset();
        },
        gui_func = _GUI_RenderDirectConnectMenu,
    };
    public static MenuTab menutab_settings = new MenuTab {
        name = "mmtb_settings",
        gui_func = _GUI_RenderSettingsMenu,
    };
    public static MenuTab menutab_about = new MenuTab {
        name = "mmtb_about",
        gui_func = _GUI_RenderAboutMenu,
    };
    public static List<MenuTab> ALL_MENU_TABS = new List<MenuTab> {
        UIServerBrowser.menutab_serverbrowser,
        menutab_directconnect,
        menutab_settings,
        //menutab_about, // no, it should be moved separately with the flexible space
    };
    //public static int current_tab = 0;
    public static MenuTab current_tab = menutab_directconnect;


    static bool _UNHIDE_PASSWORD = false;
    static bool _HIDE_IPPORT = false;
    static string _COLOR_ERRORS = "";
    static Rect _COLOR_ERRORS_RECT = default(Rect);
    public static string INPUT_COLORHEX = "#ffffff";
    public static Color24 LAST_VALID_INPUT_COLOR = Color24.black;
    public static void ParseInputColor(out bool is_valid) {
        if (
            INPUT_COLORHEX.Length > 1
        ) {
            
            if (
                Color24.TryParseHex(INPUT_COLORHEX, out var c)
                && ScavPlayer.CheckIfPlrColorIsValid(c)
            ) {
                is_valid = true;
                LAST_VALID_INPUT_COLOR = c;
                //return LAST_VALID_INPUT_COLOR;
                return;
            }
        }
        is_valid = false;
        //return Color24.black;
    }
    public static void SetInputColor(in Color24 c) {
        INPUT_COLORHEX = ColorUtility.ToHtmlStringRGB(c);
        LAST_VALID_INPUT_COLOR = c;
    }



    internal static int _STEAM_CHOSEN_LOBBYTYPE = 2;
    static void _GUI___DirectConnect_DoSteamInfoo(Rect r2, float fieldheight, float fieldnameswidth, float fieldswidth) {
        fieldnameswidth *= 1.1f;

        //GUILayout.Space(20 * GetMenuUIScale());

        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));
        {
            GUILayout.Label(Lang.Get("mmct_steam_mypersona") + KSteam.GetLocalUsername());
            _GUI___DirectConnect_DoColorPickerThingy(Net.IsRunning);
        }
        GUILayout.EndHorizontal();
        GUI.skin.label.alignment = TextAnchor.UpperRight;

        GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));// LOBBY TYPE
        {
            var ipportfieldwidth = r2.width * 0.45f;
            GUILayout.Label(
                Lang.Get("mmct_steam_lobbytype")///, GUILayout.Width(fieldswidth)
            );

            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);

            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            var aaaaaaaaaaaaaaaaa = new string[] {
                Lang.Get("mmct_steam_lobbytype_private"),
                Lang.Get("mmct_steam_lobbytype_friends"),
                Lang.Get("mmct_steam_lobbytype_public" ),
            };
            if (Multiplayer.IsNetworkActiveAndIsClient()) {
                GUILayout.Label("  "); // u cant get the lobby type as a member 
            } else {
                var oldlobbytype = _STEAM_CHOSEN_LOBBYTYPE;
                _STEAM_CHOSEN_LOBBYTYPE = GUILayout_DropdownMenu.Dropdown(_STEAM_CHOSEN_LOBBYTYPE, aaaaaaaaaaaaaaaaa, GUILayout.Width(ipportfieldwidth));

                if (Net.IsRunning) {
                    if (oldlobbytype != _STEAM_CHOSEN_LOBBYTYPE) {
                        var newww = KSteam.NumToLobbyType(_STEAM_CHOSEN_LOBBYTYPE);
#if PLAYTEST
                        newww = ELobbyType.k_ELobbyTypePublic;
#endif
                        if (SteamMatchmaking.SetLobbyType(KSteam.CURRENT_LOBBY.lobby_steamID, newww)) {
                            log.l($"STEAM: CHANGED LOBBY TYPE TO {newww}");
                        } else {
                            _STEAM_CHOSEN_LOBBYTYPE = oldlobbytype;
                        }
                    }
                }
            }
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);

            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    

    static void _GUI___DirectConnect_DoSteamConnectButtons(Rect r2, float fieldheight, float fieldnameswidth, float fieldswidth, float bigheight) {

        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mmct_steam_createlobby"), GUILayout.Height(bigheight))) {

            var type = KSteam.NumToLobbyType(_STEAM_CHOSEN_LOBBYTYPE);

            var nettttt = Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED ? Net.NetType.DedicatedServer : Net.NetType.Host;

            Multiplayer.DoMultiplayerStatusMessageLog($"User pressed Create Lobby! {type} -> {nettttt}");

            TransportSteamworks.OnWantToHostLobby(
                nettttt,
                type
            );

            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mmct_steam_openfriendlist"), GUILayout.Height(bigheight))) {
            Multiplayer.DoMultiplayerStatusMessageLog("Open Friends list!");

            // https://partner.steamgames.com/doc/api/ISteamFriends  
            SteamFriends.ActivateGameOverlay("friends");

            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        GUILayout.FlexibleSpace();

    }


    internal static void _GUI___DirectConnect_DoPasswordField(Rect r2, float fieldheight, float fieldnameswidth, float fieldswidth) {
        GUILayout.BeginHorizontal(GUILayout.Height(fieldheight)); // PASSWORD
        {
            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.Label(Lang.Get("mmct_passwordfield") + ":  ", GUILayout.Width(fieldnameswidth));
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            var can_edit = !Net.IsRunning || Net.IsServer;

            if (_UNHIDE_PASSWORD) {
                GUILayout.Label(Multiplayer.INPUT_PASSWORD, GUILayout.Width(fieldswidth));
            } else {
                if (can_edit) {
                    Multiplayer.INPUT_PASSWORD = GUILayout.PasswordField(Multiplayer.INPUT_PASSWORD, '*', GUILayout.Width(fieldswidth));
                    //Multiplayer.INPUT_PASSWORD = Multiplayer.SanitizeTextInput(Multiplayer.INPUT_PASSWORD);
                } else {
                    GUILayout.Label(new string('*', Multiplayer.INPUT_PASSWORD.Length), GUILayout.Width(fieldswidth));
                }
            }


            GUILayout.Button(Lang.Get("mmct_show"));
            var buttonrect = GUILayoutUtility.GetLastRect();
            if (Input.GetKey(KeyCode.Mouse0)) {
                buttonrect = GUIUtility.GUIToScreenRect(buttonrect);

                if (UIBullshit.IsCursorInGUIRect(buttonrect)) {
                    _UNHIDE_PASSWORD = true;
                }
            } else {
                _UNHIDE_PASSWORD = false;
            }

            GUILayout.FlexibleSpace();


        }
        GUILayout.EndHorizontal();
    }
    
    static void _GUI___DirectConnect_DoColorPickerThingy(bool textfields_are_readonly) {
        var siiiiiiiiiiiiz2 = GUI.skin.label.CalcSize(FixGuiContent.Content("#FFFFFF", 13));
        var siiiiiiiiiiiiz3 = GUI.skin.label.CalcSize(FixGuiContent.Content(INPUT_COLORHEX, 14));
        if (siiiiiiiiiiiiz2.x < siiiiiiiiiiiiz3.x) {
            siiiiiiiiiiiiz2.x = siiiiiiiiiiiiz3.x;
        }

        ParseInputColor(out var is_valid);
        GUI.color = LAST_VALID_INPUT_COLOR;

        if (!textfields_are_readonly) {

            if (GUILayout.Button("", GUILayout.Height(siiiiiiiiiiiiz2.y), GUILayout.Width(siiiiiiiiiiiiz2.y))) {
                var allcolors = ScavPlayer.NAMETAG_DEFAULT_COLORS.ToList();
                if (Multiplayer.rules.Teams) {
                    foreach (var c in ScavPlayer.ClientIdToPlayerDict) {
                        if (!allcolors.Contains(c.Value.playerColor)) {
                            allcolors.Add(c.Value.playerColor);
                        }
                    }
                }

                var curindex = allcolors.IndexOf(LAST_VALID_INPUT_COLOR);
                if (curindex == -1) {
                    curindex = UnityEngine.Random.Range(0, allcolors.Count);
                }
                curindex++;
                if (curindex >= allcolors.Count) {
                    curindex = 0;
                }

                SetInputColor(allcolors[curindex]);

                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            if (!is_valid) {
                GUI.color = Color.white;
            }

            INPUT_COLORHEX = GUILayout.TextField(INPUT_COLORHEX, 7, GUILayout.Width(siiiiiiiiiiiiz2.x));
            if (INPUT_COLORHEX.Length > 6 && !INPUT_COLORHEX.StartsWith('#') ) {
                INPUT_COLORHEX = INPUT_COLORHEX.Substring(0, 6);
            }

            _COLOR_ERRORS_RECT = GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect());
            _COLOR_ERRORS_RECT.x = _COLOR_ERRORS_RECT.xMax;
            _COLOR_ERRORS_RECT.width *= 10;

            if (!is_valid) {

                if (Color24.TryParseHex(INPUT_COLORHEX, out var cccccc) && !ScavPlayer.CheckIfPlrColorIsValid(cccccc)) {
                    _COLOR_ERRORS = Lang.Get("mmct_colortoodark");
                } else {
                    _COLOR_ERRORS = Lang.Get("mmct_colornotvalid");
                }

            }
        } else {
            GUILayout.Space(siiiiiiiiiiiiz2.y);
            GUILayout.Label(ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.playerColor.ToHex(true) : INPUT_COLORHEX, GUILayout.Width(siiiiiiiiiiiiz2.x));
        }

        GUI.color = Color.white;
    }

    public static void DoTooltipOnLastGUIRect(in string lang_key) {
        if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
            UIBullshit._GUI_SetTooltip("", Lang.Get(lang_key));
        }
    }
    public static void DoTooltipOnLastGUIRectNoLang(in string text) {
        if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
            UIBullshit._GUI_SetTooltip("", (text));
        }
    }


    static void _GUI___DoServerConfig(Rect r2) {
        var bigheight = 50 * GetMenuUIScale();
        var oglabelalign = GUI.skin.label.alignment;

        GUI.skin.label.alignment = TextAnchor.UpperCenter;
        UIBullshit._GUI_BiggerLabel(Lang.Get("mmct_serveroptions"), 1.4f);
        GUI.skin.label.alignment = TextAnchor.UpperLeft;

        var ogeneabled = GUI.enabled;
        if (Net.IsRunning) {
            GUI.enabled = false;
        }
        Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED = GUILayout.Toggle(Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED, Lang.Get("mmct_sconfig_dedicated"));
        DoTooltipOnLastGUIRect(GUI.enabled ? "mmct_sconfig_dedicated_tooltip" : "mmct_sconfig_dedicated_disabled");
        
        GUI.enabled = ogeneabled;


        Multiplayer.SERVER_TOGGLE_CHECK_GAME_HASH = GUILayout.Toggle(Multiplayer.SERVER_TOGGLE_CHECK_GAME_HASH, Lang.Get("mmct_sconfig_verifyhash"));
        DoTooltipOnLastGUIRect("mmct_sconfig_verifyhash_tooltip");
        Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST = GUILayout.Toggle(Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST, Lang.Get("mmct_sconfig_enforcemodlist"));
        DoTooltipOnLastGUIRect("mmct_sconfig_enforcemodlist_tooltip");

        GUILayout.BeginHorizontal();
        {
            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.Label(Lang.Get("mmct_sconfig_plrcount"));
            // max players
            {
                var actually_parsed = int.TryParse(GUILayout.TextField((Multiplayer.PLAYER_COUNT_LIMIT).ToString(), GUILayout.MinWidth(bigheight)), out var newvalue);

                if (actually_parsed) {
                    Multiplayer.PLAYER_COUNT_LIMIT = (byte)Mathf.Clamp(newvalue, 1, Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT);
                }
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();


        GUI.skin.label.alignment = TextAnchor.UpperLeft;

        GUILayout.Label(Lang.Get("mmct_sconfig_description") + ": ");

        GUILayout.BeginHorizontal();
        {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            var ogwordwrap = GUI.skin.textField.wordWrap;
            GUI.skin.textField.wordWrap = true;

            var calculatedheight = GUI.skin.textField.CalcHeight(FixGuiContent.Content(Net.MyServerInfo.description, 15), r2.width);

            var newdesc = GUILayout.TextArea(Net.MyServerInfo.description, GUILayout.Height(Mathf.Max(calculatedheight, UIMainMenu.GetMenuUIScale() * 100f)));
            GUI.skin.textField.wordWrap = ogwordwrap;

            if (Net.MyServerInfo.description != newdesc) {
                Multiplayer.SetServerDescription(newdesc);
            }
        }
        GUILayout.EndHorizontal();

        GUI.skin.label.alignment = oglabelalign;

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal(GUILayout.Height(bigheight));
        {
            GUILayout.Space(10 * GetMenuUIScale());
            if (GUILayout.Button(Lang.Get("back"), GUILayout.MinWidth(r2.width * 0.25f))) {
                _DO_SERVER_CONFIG = false;
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
        
        GUILayout.FlexibleSpace();

    }


    internal static bool _DO_SERVER_CONFIG = false;
    internal static bool _USE_STEAM_MENU = true;
    static void _GUI__DirectConnect(Rect r2) {
        var ogfontsize = GUI.skin.label.fontSize;
        var oglabelalign = GUI.skin.label.alignment;
        try {

            if (_DO_SERVER_CONFIG) {
                if (Net.IsConnected && Net.IsClient) {
                    _DO_SERVER_CONFIG = false;
                } else {
                    _GUI___DoServerConfig(r2);

                    return;
                }
            } 


            var fieldnameswidth = r2.width * 0.27f;
            var fieldswidth = r2.width * 0.36f;
            var bigheight = 50 * GetMenuUIScale();
            var fieldheight = 30 * GetMenuUIScale();
            var ipportfieldwidth = r2.width * 0.45f;


            var multiplayer_method_menu = 0;

            if (KSteam.Loaded && (_USE_STEAM_MENU || (Net.Transport != null && Net.Transport is TransportSteamworks))) {
                multiplayer_method_menu = 1;
            }

            GUI.skin.label.alignment = TextAnchor.UpperCenter;

            GUILayout.BeginHorizontal(); //  connection panel right title
            {
                var draw_steamtoggle = !Plugin.FORCE_NO_STEAM && !Net.IsRunning && KSteam.Loaded;

                if (draw_steamtoggle) {
                    GUILayout.FlexibleSpace();
                }

                UIBullshit._GUI_BiggerLabel(_USE_STEAM_MENU ? Lang.Get("mmct_steamlobby") : Lang.Get("mmct_directconnecttitle"), 1.4f);

                GUI.skin.label.fontSize = ogfontsize;
                GUI.skin.label.alignment = TextAnchor.MiddleRight;

                if (draw_steamtoggle) {
                    _USE_STEAM_MENU = GUILayout.Toggle(_USE_STEAM_MENU, Lang.Get("mmct_steammenutoggle"));
                }

            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));// SERVER NAME  field
            {
                GUILayout.Label(Lang.Get("mmct_sconfig_servername"), GUILayout.ExpandWidth(false));

                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                if ((!Net.IsRunning || Net.IsServer)) { // is readonly
                    var newname = GUILayout.TextField(Net.MyServerInfo.name );

                    if (Net.MyServerInfo.name != newname) {
                        Multiplayer.SetServerName(newname);
                    }
                } else {
                    GUILayout.Label(Net.CurrentServerInfo.name);
                }
                GUI.skin.label.alignment = TextAnchor.UpperLeft;

            }
            GUILayout.EndHorizontal();



            var textfields_are_readonly = Net.IsRunning;
            if (multiplayer_method_menu == 0) {


                GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));// IP
                {
                    GUILayout.Label(Lang.Get("mmct_ipportfield") + ":  ", GUILayout.Width(fieldnameswidth));

                    GUI.skin.label.alignment = TextAnchor.UpperLeft;
                    if (textfields_are_readonly) {
                        if (_HIDE_IPPORT) {
                            GUILayout.Label(new string('*', USERINPUT_IPPORT.Length), GUILayout.Width(ipportfieldwidth));
                        } else {
                            GUILayout.Label(USERINPUT_IPPORT, GUILayout.Width(ipportfieldwidth));
                        }
                    } else {
                        if (_HIDE_IPPORT) {
                            USERINPUT_IPPORT = GUILayout.PasswordField(USERINPUT_IPPORT, '*', GUILayout.Width(ipportfieldwidth));
                        } else {
                            USERINPUT_IPPORT = GUILayout.TextField(USERINPUT_IPPORT , GUILayout.Width(ipportfieldwidth));
                        }
                    }
                    GUI.skin.label.alignment = TextAnchor.UpperLeft;

                    _HIDE_IPPORT = GUILayout.Toggle(_HIDE_IPPORT, Lang.Get("mmct_hideip"));
                    
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal(GUILayout.Height(fieldheight)); // NAME and COLOR
                {
                    _GUI___DrawNameAndColorFields(fieldnameswidth, fieldswidth, textfields_are_readonly);
                }
                GUILayout.EndHorizontal();
            } else if (multiplayer_method_menu == 1) {
                _GUI___DirectConnect_DoSteamInfoo(r2, fieldheight, fieldnameswidth, fieldswidth);
            }

            {
                _GUI___DirectConnect_DoPasswordField(r2, fieldheight, fieldnameswidth, fieldswidth); // PASSWORD
            }

            GUILayout.Space(20 * GetMenuUIScale());


            if (Net.IsRunning && Net.IsConnecting) {
                GUILayout.Label(Lang.Get("mmct_connecting"));
            } else {
                GUILayout.BeginHorizontal(GUILayout.Height(bigheight));
                {
                    if (Net.IsServer) {
                        if (GUILayout.Button(Lang.Get("mmct_serveroptions"))) {
                            _DO_SERVER_CONFIG = true;
                        }
                        GUILayout.FlexibleSpace();
                    }

                    if (KSteam.IS_IN_LOBBY && KSteam.CURRENT_LOBBY.lobby_steamID.IsValid()) {
                        if (GUILayout.Button(Lang.Get("mmct_copylobbycode"))) {
                            GUIUtility.systemCopyBuffer = KSteam.CURRENT_LOBBY.lobby_steamID.ToString();
                        }
                        GUILayout.FlexibleSpace();
                    }

                    if (Net.IsServer && Net.IsConnected && Util.IsInMainMenu() && PreRunScript.instance != null) {
                        if (GUILayout.Button(Lang.Get("mmct_gotorunsettings"))) {
                            SetOpen(false);
                            PreRunScript.instance.runSettingsScreen.SetActive(value: true);
                        }
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(15 * GetMenuUIScale());
            }
            
            

            GUILayout.BeginHorizontal(GUILayout.Height(bigheight));
            {
                if (Net.IsRunning) {

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Net.IsClient ?
                        (Net.IsConnecting ?
                            Lang.Get("mmct_disconnect_abort")
                            : Lang.Get("mmct_disconnect"))
                            : Lang.Get("mmct_disconnect_server"
                        ), GUILayout.MinWidth(bigheight * 3), GUILayout.Height(bigheight))
                    ) {
                        Multiplayer.ShutdownNetwork();
                        Chat.LogMessage(Chat.NAMELABEL_LOCALSYSTEM, "Disconnected by the user.");
                        Multiplayer.DoMultiplayerStatusMessageLog("Disconnected by the user.");
                        Util.PlayUISound(PlayerCamera.UISoundType.Close);
                    }

                    if (Net.IsRunningSteam && KSteam.IS_IN_LOBBY && (!(KSteam.CURRENT_LOBBY.locked || Net.CurrentServerInfo.midjoin_lock_active) || Net.IsServer)) {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(Lang.Get("mmct_steam_invitefriends"), GUILayout.Height(bigheight))) {
                            Multiplayer.DoMultiplayerStatusMessageLog("Open Friends Invite dialog!");

                            // https://partner.steamgames.com/doc/api/ISteamFriends  
                            SteamFriends.ActivateGameOverlayInviteDialog(KSteam.CURRENT_LOBBY.lobby_steamID);

                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                    }

                    GUILayout.FlexibleSpace();

                } else {

                    if (Util.IsInWorld()) {

                        GUILayout.Label(Lang.Get("mmct_canthostinworld"));

                    } else {
                        if (multiplayer_method_menu == 0) {

                            if (Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED) {
                                if (GUILayout.Button(Lang.Get("mmct_startserver"), GUILayout.Height(bigheight))) {
                                    Multiplayer.DoMultiplayerStatusMessageLog("Start Server clicked!");
                                    TransportLiteNetLib.OnWantToConnect(USERINPUT_IPPORT, Net.NetType.DedicatedServer);
                                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                                }
                            } else {
                                if (GUILayout.Button(Lang.Get("mmct_starthost"), GUILayout.Height(bigheight))) {
                                    Multiplayer.DoMultiplayerStatusMessageLog("Start Host clicked!");
                                    TransportLiteNetLib.OnWantToConnect(USERINPUT_IPPORT, Net.NetType.Host);
                                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                                }
                            }

                            if (Util.IsInMainMenu()) {
                                if (GUILayout.Button(Lang.Get("mmct_startclient"), GUILayout.Height(bigheight))) {
                                    Multiplayer.DoMultiplayerStatusMessageLog("Client Connect clicked!");
                                    TransportLiteNetLib.OnWantToConnect(USERINPUT_IPPORT, Net.NetType.Client);

                                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                                }
                            }

                        } else if (multiplayer_method_menu == 1) {
                            _GUI___DirectConnect_DoSteamConnectButtons(r2, fieldheight, fieldnameswidth, fieldswidth, bigheight);
                        } else {
                            GUILayout.Label("get tf outta my room im playin minecraft!!");
                        }
                    }

                }
            }
            GUILayout.EndHorizontal();

            {
                var ogwordwrap = GUI.skin.label.wordWrap;
                GUI.skin.label.wordWrap = true;

                var txt = FixGuiContent.Content(Lang.Get("mmct_last_status_msg") + "\n" + Multiplayer.multiplayerStatusMessage, 16);

                var texthiehgt = GUI.skin.label.CalcHeight(txt, r2.width);

                GUILayout.Label(txt, GUILayout.Height(texthiehgt));

                if (
                    (KSteam.Loaded
                    && KSteam.IsSteamMaintenanceLikely())
                ) {
                    GUILayout.Space(10 * GetMenuUIScale());

                    var ogcolor = GUI.color;

                    GUI.color = Color.red;

                    txt = FixGuiContent.Content(Lang.Get("mmct_steammaintenance"), 17);
                    texthiehgt = GUI.skin.label.CalcHeight(txt, r2.width);
                    GUILayout.Label(txt, GUILayout.Height(texthiehgt));

                    GUI.color = ogcolor;
                }

                GUI.skin.label.wordWrap = ogwordwrap;
            }




        } catch (Exception e) {
            log.error(e);
        }
        GUI.skin.label.alignment = oglabelalign;
        GUI.skin.label.fontSize = ogfontsize;
    }

    static void _GUI___DrawNameAndColorFields(float fieldnameswidth, float fieldswidth, bool textfields_are_readonly) {
        GUI.skin.label.alignment = TextAnchor.MiddleRight;
        var lllllllllllllllllll = Lang.Get("mmct_usernamefield") + ":  ";
        GUILayout.Label(lllllllllllllllllll, GUILayout.Width(fieldnameswidth));
        if (textfields_are_readonly) {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.Label(USERINPUT_NAME, GUILayout.Width(fieldswidth));
        } else {
            var text = USERINPUT_NAME;
            if (String.IsNullOrEmpty(text)) {
                text = "";
            }

            var nwww = GUILayout.TextField(text, Multiplayer.MAX_NAME_LENGTH, GUILayout.Width(fieldswidth));

            USERINPUT_NAME = nwww;
        }
        _GUI___DirectConnect_DoColorPickerThingy(textfields_are_readonly);


        GUILayout.FlexibleSpace();
    }


    public static string USERINPUT_IPPORT = Net.IP_LOCALHOST_IPv4 + ":" + Net.DEFAULT_PORT.ToString();
    public static string USERINPUT_NAME = "TEST_NAME";



    static string[] _CONNECTIONSECONDPANEL_TOOLBAR_STRS = new string[] {
        "AAAAAAAAA",
        "BBBBBBBBB"
    };
    static int _CONNECTIONSECONDPANEL_TOOLBAR = 1;
    static void _GUI__ConnectionSecondPanel(Rect r2) {
        var ogfontsize = GUI.skin.label.fontSize;
        var oglabelalign = GUI.skin.label.alignment;
        try {

            GUI.skin.label.fontSize = (int)(ogfontsize * 1.4f);
            GUI.skin.label.alignment = TextAnchor.UpperCenter;

            _CONNECTIONSECONDPANEL_TOOLBAR_STRS[0] = Lang.Get("mmct_plrs" );
            _CONNECTIONSECONDPANEL_TOOLBAR_STRS[1] = Lang.Get("mmct_rules");

            _CONNECTIONSECONDPANEL_TOOLBAR = GUILayout.Toolbar(_CONNECTIONSECONDPANEL_TOOLBAR, _CONNECTIONSECONDPANEL_TOOLBAR_STRS);

            GUI.skin.label.fontSize = ogfontsize;
            //GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            var textfields_are_readonly = Net.IsRunning;


            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
            _GUI__ConnectionSecondPanel_ContentRender(r2);

        } catch (Exception e) {
            log.error(e);
        }
        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);

        GUI.skin.label.alignment = oglabelalign;
        GUI.skin.label.fontSize = ogfontsize;
    }


    static void _GUI___ConnectionSecondPanel_ContentRender_Top(Rect r) {
        GUILayout.BeginHorizontal();
        {
            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben) {
                if (KSteam.IS_IN_LOBBY) {
                    if (GUILayout.Button(Lang.Get("mmct_hoststeamprofile"))) {
                        SteamFriends.ActivateGameOverlayToUser("steamid", (CSteamID)KSteam.CURRENT_LOBBY.ownerID);

                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    }

                    GUILayout.FlexibleSpace();
                }
            }
            if (Con.CanExecuteAdminCommands()) {
                ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety = GUILayout.Toggle(____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety, Lang.Get("mmct_bankicksafety"));
            }

        }
        GUILayout.EndHorizontal();
    }

    public static bool ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety = false; // ban/kick safety
    public static bool ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton = false;
    public static bool ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben = false;
    
    static void _GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList(Rect r) {
        if (!Net.IsRunning) {
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUILayout.Label(Lang.Get("mmct_offline"));
        } else if (ScavPlayer.ClientIdToPlayerDict.Count > 0) {

            var og_enabled = GUI.enabled;
            foreach (var plr in new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values)) {

                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                GUILayout.BeginVertical(GUI.skin.box);
                try {
                    _GUI___DrawPlayerInList(r, plr);
                } catch (Exception e) {
                    log.error($"_GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList PLR {plr} " + e.ToString());
                }
                GUI.enabled = og_enabled;
                GUILayout.EndVertical();

            }
        } else {
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUILayout.Label(Lang.Get("mmct_nobody"));
        }

    }

    static void _GUI___ConnectionSecondPanel_ContentRender_RenderRulesList(Rect r2) {

        var can_edit = Con.CanExecuteAdminCommands();

        GUI.skin.label.alignment = TextAnchor.UpperRight;

        if (can_edit) {

            GUILayout.BeginHorizontal();
            {
                
                GUILayout.Label(Lang.Get("mmct_presetname"));
                _GUI____RenderRuleField_PresetName = GUILayout.TextField(_GUI____RenderRuleField_PresetName, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.23f));

                var og_enabled = GUI.enabled;
                if (GUI.enabled) {
                    GUI.enabled = !String.IsNullOrWhiteSpace(_GUI____RenderRuleField_PresetName);
                }
                if (GUILayout.Button(Lang.Get("mmct_presetsave"))) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);

                    Multiplayer.RulesSaver.SetData(_GUI____RenderRuleField_PresetName, Multiplayer.rules);
                    Multiplayer.RulesSaver.Save();

                    var presets = Multiplayer.RulesSaver.GetPresets().ToList();
                    var foundindex = presets.IndexOf(_GUI____RenderRuleField_PresetName);
                    if (foundindex != -1) {
                        _GUI____RenderRuleField_PresetChosen = foundindex + 1;
                        RememberRulePreset(_GUI____RenderRuleField_PresetName);
                    }

                }
                if (GUILayout.Button(Lang.Get("mmct_presetremove"))) {
                    if (Multiplayer.RulesSaver.HasKey(_GUI____RenderRuleField_PresetName)) {
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        var selectedPreset = Multiplayer.RulesSaver.GetPresets().ElementAtOrDefault(_GUI____RenderRuleField_PresetChosen - 1);
                        Multiplayer.RulesSaver.DeleteKey(_GUI____RenderRuleField_PresetName);
                        Multiplayer.RulesSaver.Save();
                        // Resolve by name so deleting an earlier entry cannot select a different preset.
                        _GUI____RenderRuleField_PresetChosen = Multiplayer.RulesSaver.GetPresets().ToList().IndexOf(selectedPreset) + 1;
                        if (_GUI____RenderRuleField_PresetChosen == 0) {
                            _GUI____RenderRuleField_PresetName = "";
                            RememberRulePreset("");
                        }
                    }
                }
                GUI.enabled = og_enabled;
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(Lang.Get("mmct_presetselect"));

                var arr = new List<string> { "[no preset]" };
                var presets = Multiplayer.RulesSaver.GetPresets();
                arr.AddRange(presets);

                var prev_preset = _GUI____RenderRuleField_PresetChosen;
                _GUI____RenderRuleField_PresetChosen = GUILayout_DropdownMenu.Dropdown(_GUI____RenderRuleField_PresetChosen, arr.ToArray());

                var real_preset = _GUI____RenderRuleField_PresetChosen - 1;
                if (prev_preset != _GUI____RenderRuleField_PresetChosen) {
                    if (real_preset >= 0 && presets.Count() > 0) {
                        _GUI____RenderRuleField_PresetName = presets.ElementAt(real_preset);
                        Multiplayer.rules = Multiplayer.RulesSaver.GetData(_GUI____RenderRuleField_PresetName);
                        Multiplayer.ApplyGameRules();
                    } else {
                        _GUI____RenderRuleField_PresetName = "";
                    }
                    RememberRulePreset(_GUI____RenderRuleField_PresetName);
                }

                GUILayout.FlexibleSpace();


                if (can_edit) {
                    if (GUILayout.Button(Lang.Get("mmct_reset_rules"))) {
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        Con.ExecuteCommandAdminNoLog("resetrules");
                    }
                }
            }
            GUILayout.EndHorizontal();
        }


        GUILayout.BeginHorizontal();
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(Lang.Get("mmct_search"));
            _GUI____RenderRuleField_search = GUILayout.TextField(_GUI____RenderRuleField_search, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.3f));
        }
        GUILayout.EndHorizontal();


        _SCROLL_FULLRULELIST = GUILayout.BeginScrollView(_SCROLL_FULLRULELIST);


        var rule_fields = typeof(MultiplayerGameRules).GetFields(BindingFlags.Instance | BindingFlags.Public).ToList();

        if (!String.IsNullOrWhiteSpace(_GUI____RenderRuleField_search)) {
            rule_fields = rule_fields.Where(f => f.Name.ContainsInsensitive(_GUI____RenderRuleField_search)
                || (Lang.Get("rule_name_" + f.Name, true) ?? f.Name).ContainsInsensitive(_GUI____RenderRuleField_search)).ToList();
        }

        if (rule_fields.Count == 0) {
            GUI.skin.label.alignment = TextAnchor.UpperCenter;
            GUILayout.Label(Lang.Get("filter_found_nothing"));
        } else {
            _GUI____RenderRuleField_cur_max_width = 0;
            foreach (var fld in rule_fields) {
                GUILayout.BeginHorizontal();
                try {
                    _GUI____RenderRuleField(fld, can_edit , r2);
                } catch (Exception e) {
                    log.error($"_GUI___ConnectionSecondPanel_ContentRender_RenderRulesList: \nFLD: {fld} \n{e.ToString()}" );
                }
                GUILayout.EndHorizontal();
            }
            _GUI____RenderRuleField_last_max_width = Mathf.Min(r2.width * 0.5f, _GUI____RenderRuleField_cur_max_width);

        }

        GUILayout.EndScrollView();

    }

    static string _GUI____RenderRuleField_search = "";
    const string LastRulePresetKey = "LastRulePreset";

    static void RememberRulePreset(string presetName) {
        SettingsJsonThing.json.SetString(LastRulePresetKey, presetName);
        SettingsJsonThing.json.Save();
    }

    internal static void RestoreLastRulePreset() {
        // Client/server rules belong to the active lobby, not the local hosting preference.
        if (Net.IsRunning) return;

        var presetName = SettingsJsonThing.json.GetString(LastRulePresetKey);
        var index = Multiplayer.RulesSaver.GetPresets().ToList().IndexOf(presetName);
        _GUI____RenderRuleField_PresetChosen = index + 1;
        _GUI____RenderRuleField_PresetName = index >= 0 ? presetName : "";
        if (index >= 0) {
            Multiplayer.rules = Multiplayer.RulesSaver.GetData(presetName);
            Multiplayer.ApplyGameRules();
        } else if (!String.IsNullOrEmpty(presetName)) {
            // A preset removed outside the menu must not leave a stale selection behind.
            RememberRulePreset("");
        }
    }

    internal static int _GUI____RenderRuleField_PresetChosen = 0;
    internal static string _GUI____RenderRuleField_PresetName = "";
    static float _GUI____RenderRuleField_last_max_width = 300;
    static float _GUI____RenderRuleField_cur_max_width = 0;

    static void _GUI____RenderRuleField(FieldInfo fld, bool can_edit, in Rect r2) {

        bool do_tooltip = false;
        string ruleName = Lang.Get("rule_name_" + fld.Name, true) ?? fld.Name;
        GUI.skin.label.wordWrap = false;

        var sizzzz = GUI.skin.label.CalcSize(FixGuiContent.Content(ruleName, 18));

        if (sizzzz.x > _GUI____RenderRuleField_cur_max_width) {
            _GUI____RenderRuleField_cur_max_width = sizzzz.x;
        }

        string rulevaluestring = null;

        var ogvalue = fld.GetValue(Multiplayer.rules);
        if (!can_edit) {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            var str = $"{ruleName}: ";
            if (fld.FieldType == typeof(float)) {
                str += ($" {Math.Round((float)ogvalue, 1)}");
            } else {
                str += ($" {ogvalue}");
            }

            GUILayout.Label(str);
            do_tooltip = UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));
        } else {
            GUI.skin.label.alignment = TextAnchor.MiddleRight;

            GUILayout.Label(ruleName, GUILayout.Width(_GUI____RenderRuleField_last_max_width));
            do_tooltip = UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));

            TypedReference rulesref = __makeref(Multiplayer.rules);


            bool did_change = false;

            GUI.skin.label.alignment = TextAnchor.MiddleLeft;

            if (fld.FieldType == typeof(bool)) {
                bool newvalue = GUILayout.Toggle((bool)ogvalue, "");
                fld.SetValueDirect(rulesref, newvalue);

                did_change = newvalue != (bool)ogvalue;
                
            } else if (fld.HasAttribute(typeof(MultiplayerRuleByteAttribute))) {
                var attr = fld.GetAttribute<MultiplayerRuleByteAttribute>();
                byte p = (byte)ogvalue;
                float floatified = p;

                float newvalue = (float)Math.Round(GUILayout.HorizontalSlider(floatified, attr.min, attr.limit), 1);
                do_tooltip = do_tooltip || UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));

                fld.SetValueDirect(rulesref, (byte)Mathf.RoundToInt(newvalue));

                rulevaluestring = Math.Round(newvalue).ToString() + attr.postfix;
                GUILayout.Label(rulevaluestring, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.1f));

                did_change = newvalue != floatified;
                
            } else if (fld.FieldType == typeof(float)) {

                var min = 0f;
                var max = 10f;

                var attr = fld.GetAttribute<MultiplayerRuleFloatAttribute>();
                if (attr != null) {
                    min = attr.mi;
                    max = attr.ma;
                }
                var f_ogvalue = (float)ogvalue;

                float newvalue = (float)Math.Round(GUILayout.HorizontalSlider(f_ogvalue,
                        Mathf.Min(f_ogvalue, min),
                        Mathf.Max(f_ogvalue, max)
                ), 1);

                do_tooltip = do_tooltip || UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));
                

                fld.SetValueDirect(rulesref, newvalue);
                if (attr != null) {
                    rulevaluestring = newvalue.ToString("0.0") + attr.postfix;
                } else {
                    rulevaluestring = newvalue.ToString("0.0");
                }
                GUILayout.Label(rulevaluestring, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.1f));

                did_change = newvalue != (float)ogvalue;
               
            } else if (fld.FieldType == typeof(int) || fld.FieldType == typeof(byte) || fld.FieldType == typeof(ushort)) {
                var actually_parsed = int.TryParse(GUILayout.TextField( (ogvalue).ToString()), out var newvalue);
                if (actually_parsed) {
                    fld.SetValueDirect(rulesref, newvalue);
                    did_change = newvalue.ToString() != ogvalue.ToString();
                }
            } else {
                GUILayout.Label($"[{fld.FieldType.Name}]: {ogvalue} ");
            }
            if (did_change) {
                if (Multiplayer.IsNetworkActiveAndIsClient()) {
                    if (Con.client_isadmin) {
                        var newvalue = fld.GetValue(Multiplayer.rules);

                        var com = $"rule {fld.Name} {newvalue.ToStringInvariant()}";

                        Multiplayer.DoMultiplayerStatusMessageLog($"Sending command: \"{com}\"");

                        Con.ExecuteCommandAdminNoLog(com);
                    }
                } else {
                    Multiplayer.ApplyGameRules();
                }

            }
        }


        if (do_tooltip) {
            var key = "rule_desc_" + fld.Name;
            if (Lang.TryGet(key, out var value)) {
            } else {
                value = "";
            }
            if (!String.IsNullOrWhiteSpace(rulevaluestring)) {
                if (String.IsNullOrWhiteSpace(value)) {
                    value = rulevaluestring;
                } else {
                    value = value + "\n" + rulevaluestring;
                }
            }
            if (!String.IsNullOrWhiteSpace(value)) {
                UIBullshit._GUI_SetTooltip(fld.Name, value);
            }
        }

    }


    static Vector2 _SCROLL_FULLPLRLIST = Vector2.zero;
    static Vector2 _SCROLL_FULLRULELIST = Vector2.zero;
    static void _GUI__ConnectionSecondPanel_ContentRender(Rect r2) {

        var ogfontsize = GUI.skin.label.fontSize;
        var ogfontsize_button = GUI.skin.button.fontSize;

        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUI.skin.label.fontSize = (int)(ogfontsize * 0.85f);
        GUI.skin.button.fontSize = GUI.skin.label.fontSize;

        if (_CONNECTIONSECONDPANEL_TOOLBAR == 0) {
            GUI.skin.label.wordWrap = false;

            if (Net.IsRunning) {
                _GUI___ConnectionSecondPanel_ContentRender_Top(r2);
            }

            _SCROLL_FULLPLRLIST = GUILayout.BeginScrollView(_SCROLL_FULLPLRLIST);
            _GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList(r2);

            GUILayout.EndScrollView();

        } else if (_CONNECTIONSECONDPANEL_TOOLBAR == 1) {
            //GUI.skin.label.alignment = TextAnchor.UpperRight;
            //GUILayout.Box();


            _GUI___ConnectionSecondPanel_ContentRender_RenderRulesList(r2);


        }
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUI.skin.label.fontSize = ogfontsize;
        GUI.skin.button.fontSize = ogfontsize_button;
    }

    static ScavPlayer _COLOR_EDITING = null;
    static string _COLOR_EDITING_STR = "FFFFFF";

    static void _GUI___DrawPlayerInList(Rect r, ScavPlayer plr) {
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        var buttonwidth = 60 * GetMenuUIScale();

        var og_enabled = GUI.enabled;

        var textsize = GUI.skin.label.CalcSize(FixGuiContent.Content(plr.playerName, 19));

        GUILayout.BeginHorizontal();
        {
            if (plr.IsHost) {
                GUILayout.Label(CoopModAssets.icon_crown.texture, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));
            }

            foreach (var item in plr.KnownUserTagIcons) {
                GUILayout.Label(item.texture, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));
            }

            var pp = plr.IconAnyLargeToSmall;
            if (pp != null) {
                GUILayout.Label(pp, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));
            }

            GUI.color = plr.playerColor;
            GUILayout.Label( plr.playerName , GUILayout.Width(Mathf.Min(textsize.x, r.width*0.5f)));
            GUI.color = Color.white;

            GUILayout.FlexibleSpace();

            // kick buttons

            if (!plr.IsLocal && Con.CanExecuteAdminCommands()) {
                if (GUI.enabled) {
                    GUI.enabled = ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety;
                }


                if (GUILayout.Button(Lang.Get("mmct_plr_kick"))) {
                    Util.DelayCallLambda(0.001f, () => {
                        Con.ExecuteCommandAdminNoLog("kick id:" + plr.playerId.ToString());
                    });
                }


                // ban buttons 
                {
                    if (GUILayout.Button(Lang.Get("mmct_plr_ban"))) {
                        Util.DelayCallLambda(0.001f, () => {
                            Con.ExecuteCommandAdminNoLog("ban id:" + plr.playerId.ToString());
                        });
                    }
                    if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton) {
                        if (GUILayout.Button(Lang.Get("mmct_plr_ipban"))) {
                            Util.DelayCallLambda(0.001f, () => {
                                Con.ExecuteCommandAdminNoLog("ipban id:" + plr.playerId.ToString());
                            });
                        }
                    }
                }

                GUI.enabled = og_enabled;
            }
        }
        GUILayout.EndHorizontal();


        GUILayout.BeginHorizontal();
        {
            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben) {
                if (GUILayout.Button(Lang.Get("mmct_plr_steamprofile")) ) {
                    SteamFriends.ActivateGameOverlayToUser("steamid", (CSteamID)plr.SteamId);
                }
            }

            GUILayout.Space( 5 * GetMenuUIScale() );


            if (Con.CanExecuteAdminCommands()) {
                if (_COLOR_EDITING == plr) {

                    var siiiiiiiiiiiiz2 = GUI.skin.label.CalcSize(FixGuiContent.Content("#FFFFFF", 20));

                    var c = plr.playerColor;
                    if (Color24.TryParseHex(_COLOR_EDITING_STR, out var cccccc)) {
                        c = cccccc;
                    }
                    GUI.color = c;

                    _COLOR_EDITING_STR = GUILayout.TextField( _COLOR_EDITING_STR, 7, GUILayout.Width(siiiiiiiiiiiiz2.x));


                    if (_COLOR_EDITING_STR.Length > 6 && !_COLOR_EDITING_STR.StartsWith('#')) {
                        _COLOR_EDITING_STR = _COLOR_EDITING_STR.Substring(0, 6);
                    }
                    GUI.color = Color.white;

                    if (GUILayout.Button( Lang.Get("mm_apply"))) {
                        if (plr.playerColor != c) {
                            var com = $"setplrcolor id:{plr.playerId} {c.ToHex(true)}";
#if !RELEASE
                            log.warn(com);
#endif
                            Con.ExecuteCommandAdminNoLog(com);
                        }
                        _COLOR_EDITING = null;
                    }

                } else {
                    GUI.color = plr.playerColor;

                    //GUI.color = plr.plrcolor;
                    if (GUILayout.Button( Lang.Get("mmct_plr_setcolor"))) {
                        _COLOR_EDITING_STR = plr.playerColor.ToHex(false);
                        _COLOR_EDITING = plr;
                    }

                    GUI.color = Color.white;
                }
            }
            GUILayout.FlexibleSpace();

            var mutelabelcontent = FixGuiContent.Content(Lang.Get("mmct_plr_mutelabel"), 21);
            var mutelabelsize = GUI.skin.label.CalcSize(mutelabelcontent);

            if (!plr.IsLocal && Con.CanExecuteAdminCommands()) {
                //if (GUI.enabled)  GUI.enabled = ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety;
                

                // mute buttons
                {
                    GUILayout.Label(mutelabelcontent);
                    if (GUILayout.Button(CoopModAssets.voicechaticon.texture, GUILayout.Width(mutelabelsize.y), GUILayout.Height(mutelabelsize.y))) {
                        Util.DelayCallLambda(0.001f, () => {
                            Con.ExecuteCommandAdminNoLog("servermute vc id:" + plr.playerId.ToString());
                        });
                    }
                    if (plr.server_mute_vc) {
                        var rect = (GUILayoutUtility.GetLastRect());
                        GUI.DrawTexture(rect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1, Color.red, 0,0);
                    }
                    if (GUILayout.Button(CoopModAssets.LoadSprite($"speechbubble3.png").texture, GUILayout.Width(mutelabelsize.y), GUILayout.Height(mutelabelsize.y))) {
                        Util.DelayCallLambda(0.001f, () => {
                            Con.ExecuteCommandAdminNoLog("servermute tc id:" + plr.playerId.ToString());
                        });
                    }
                    if (plr.server_mute_tc) {
                        var rect = (GUILayoutUtility.GetLastRect());
                        GUI.DrawTexture(rect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1, Color.red, 0, 0);
                    }
                }


                GUI.enabled = og_enabled;
            } else if (plr.server_mute_vc || plr.server_mute_tc) {

                GUILayout.FlexibleSpace();
                { // show mutes
                    GUILayout.Label(mutelabelcontent);
                    GUILayout.Label(CoopModAssets.voicechaticon.texture, GUILayout.Width(mutelabelsize.y), GUILayout.Height(mutelabelsize.y));
                    if (plr.server_mute_vc) {
                        var rect = (GUILayoutUtility.GetLastRect());
                        GUI.DrawTexture(rect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1, Color.red, 0, 0);
                    }
                    GUILayout.Label(CoopModAssets.LoadSprite($"speechbubble3.png").texture, GUILayout.Width(mutelabelsize.y), GUILayout.Height(mutelabelsize.y));
                    if (plr.server_mute_tc) {
                        var rect = (GUILayoutUtility.GetLastRect());
                        GUI.DrawTexture(rect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1, Color.red, 0, 0);
                    }
                }
                GUILayout.FlexibleSpace();
            }


            if (!plr.IsLocal) {
                if (plr.vc_output != null) {

                    GUILayout.Space(5 * GetMenuUIScale());

                    GUI.skin.label.alignment = TextAnchor.UpperRight;
                    GUILayout.Label(Lang.Get("mmct_plr_vc_vol"));
                    plr.vc_output.custom_volume_set = GUILayout.HorizontalSlider(plr.vc_output.custom_volume_set, 0, 2, GUILayout.MaxWidth(r.width * 0.4f)
                        );

                }
            }
        }
        GUILayout.EndHorizontal();



        GUILayout.BeginHorizontal();
        {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.Label("ID:" + " " + plr.playerId);
            GUILayout.Space(2 * GetMenuUIScale());
            GUILayout.Label( Lang.Get("mmct_plr_ping") + " " + plr.ping_as_ms.ToString());

            if ( plr.ServerPlayerState != null) {
                GUILayout.Label($" TPS:{plr.ServerPlayerState.tps,3} FPS:{plr.ServerPlayerState.fps,3}");
            }
        }
        GUILayout.EndHorizontal();

    }

    static void _GUI_RenderDirectConnectMenu(Rect r) {
        GUI.color = Color.white;

        var additionalhalfoffset = 200f * GetMenuUIScale();


        var halfBorderSize = UIBullshit.uiBlockSmallBorderSize * 0.5f;

        if (Net.Transport != null) {
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton = Net.Transport is TransportLiteNetLib;
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben = Net.Transport is TransportSteamworks;

            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben) {
                _USE_STEAM_MENU = true;
            }
            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton) {
                _USE_STEAM_MENU = false;
            }
        } else {
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton = false;
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben = false;
        }



        {
            var connectrect = new Rect(r);
            connectrect.xMax = connectrect.center.x + additionalhalfoffset - halfBorderSize;

            connectrect.x = 0;
            connectrect.y = 0;

            UIBullshit._GUI_9SlicePanel(connectrect, 1, 0.5f, false, UIBullshit.unscaled_uiBlockNano);

            connectrect = connectrect.ShrinkBorder(UIBullshit.uiBlockNanoBorderSize);

            GUILayout.BeginArea(connectrect);

            _GUI__ConnectionSecondPanel(connectrect);

            GUILayout.EndArea();
        }
        {
            var secondhalf = new Rect(r);
            secondhalf.x = 0;
            secondhalf.y = 0;

            secondhalf.xMin = secondhalf.center.x + additionalhalfoffset + halfBorderSize;


            UIBullshit._GUI_9SlicePanel(secondhalf, 1, 0.5f, false, UIBullshit.unscaled_uiBlockNano);

            secondhalf = secondhalf.ShrinkBorder(UIBullshit.uiBlockNanoBorderSize);

            GUILayout.BeginArea(secondhalf);

            _GUI__DirectConnect(secondhalf);

            GUILayout.EndArea();
        }


    }
    
    #region ALL_SETTINGS
    
    static void _GUI_RenderSettingsMenu(Rect r) {
        
        var choicerect = new Rect(r);
        {
            choicerect.xMax = Mathf.LerpUnclamped(choicerect.xMax, choicerect.xMin,0.7f) - 50f * GetMenuUIScale();

            choicerect.x = 0;
            choicerect.y = 0;

            UIBullshit._GUI_9SlicePanel(choicerect, 1, 0.5f, false, UIBullshit.unscaled_uiBlockNano);
        }

        GUILayout.BeginHorizontal();
        {
            //GUILayout.Space(170f * GetMenuUIScale());

            var setting_submenu_buttons_rects = choicerect.width - UIBullshit.uiBlockNanoBorderSize * 2;
            GUILayout.BeginVertical(GUILayout.Width(choicerect.width));
            // chooser tabs
            {
                GUILayout.Space(UIBullshit.uiBlockNanoBorderSize);

                var gdgsdsgdds = (SettingsTab tab) => {

                    if (cur_settings_tab == tab) {
                        GUI.backgroundColor = Color.white;
                    } else {
                        GUI.backgroundColor = Color.white * 0.8f;
                    }

                    if (GUILayout.Button(Lang.Get(tab.name), GUILayout.Width(setting_submenu_buttons_rects))) {
                        cur_settings_tab = tab;
                        if (cur_settings_tab.on_open != null) {
                            cur_settings_tab.on_open();
                        }
                        OnMenuTabChange();
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    }
                };

                foreach (var tab in ALL_SETTINGS) {
                    gdgsdsgdds(tab);
                }
#if RELEASE && !PLAYTEST
                if ((Net.IsRunning ? (Con.CanCheat()) : (Multiplayer.rules.sv_cheats)) || log.verbose) 
#endif
                if (_debug_setting_tab != null) {
                    gdgsdsgdds(_debug_setting_tab);
                }
                if (_bugreport_setting_tab != null) {
                    gdgsdsgdds(_bugreport_setting_tab);
                }

                GUI.backgroundColor = Color.white;
            }
            GUILayout.EndVertical();

            var r2 = new Rect(r);
            r2.width = r.width - choicerect.width - UIBullshit.uiBlockSmallBorderSize * 4.05f;

            _SETTINGSMENUSCROLL = GUILayout.BeginScrollView(_SETTINGSMENUSCROLL);
            GUILayout.BeginVertical(GUILayout.MaxWidth(r2.width),GUILayout.ExpandWidth(false), GUILayout.Width(r2.width ));
            {
                var ogfontsize = GUI.skin.label.fontSize;
                var ogalign = GUI.skin.label.alignment;
                GUI.skin.label.wordWrap = false;




                if (cur_settings_tab != null) {
                    try {
                        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
                        GUI.skin.label.alignment = TextAnchor.UpperCenter;
                        UIBullshit._GUI_BiggerLabel(Lang.Get(cur_settings_tab.name), 1.4f);

                        GUI.skin.label.alignment = TextAnchor.UpperLeft;

                        if (cur_settings_tab.settings_origin != null)
                            __GUI____RenderFieldsWithThatAttribute(GetSettingsFromStaticClass(cur_settings_tab.settings_origin), r2);

                        if (cur_settings_tab.gui_func != null)
                            cur_settings_tab.gui_func(r2);
                    } catch (Exception e) {
                        log.error($"SETTINGS RENDER ERROR: {e.ToString()}");
                    }
                    UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
                } else {
                    GUILayout.Label("Empty.");
                }


                GUI.skin.label.fontSize = ogfontsize;
                GUI.skin.label.alignment = ogalign;

            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            //GUILayout.Space(170f * GetMenuUIScale());
        }
        GUILayout.EndHorizontal();

        //GUILayout.EndArea();

        GUILayout.Space(10 * GetMenuUIScale());

        GUILayout.BeginHorizontal();
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.Get("mmst_resettodefault"))) {
                if (cur_settings_tab?.settings_origin != null) {

                    foreach (var item in GetSettingsFromStaticClass(cur_settings_tab.settings_origin)) {
                        var attttt = item.GetAttribute<SettingDeclarerThingyBaseAttribute>();
                        attttt.Save(item, attttt.default_value);
                    }

                }
                if (cur_settings_tab?.on_reset != null) {
                    cur_settings_tab.on_reset();
                }
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            DoTooltipOnLastGUIRect("mmst_resettodefault_desc");

            if (GUILayout.Button(Lang.Get("mmst_apply"))) {
                SettingsJsonThing.json.Save();
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
        }
        GUILayout.EndHorizontal();

    }
    public static void OnMenuTabChange() {
        GUILayout_DropdownMenu.CloseAll();
        Voicechat.ENABLE_LOCAL_LOOPBACK = false;
        ResetSettingsVOIPVisualiser();
    }
    public static void ResetSettingsVOIPVisualiser() {
        if (UIBullshit.main.egg != null) {
            GameObject.Destroy(UIBullshit.main.egg.gameObject);
            UIBullshit.main.egg = null;
        }
    }


    internal static SettingsTab _general_setting_tab = new SettingsTab {
        name = "mmst_general",
        gui_func = _GUI___SettingsGeneral,
        on_open = () => {  },
        on_reset = () => {  },
        settings_origin = typeof(Amogus)
    };
    internal static SettingsTab _gui_setting_tab = new SettingsTab {
        name = "mmst_gui",
        gui_func = _GUI___SettingsGUI,
        settings_origin = typeof(UIBullshit)
    };
    internal static SettingsTab _voip_setting_tab = new SettingsTab {
        name = "mmst_voip",
        gui_func = _GUI___SettingsVOIP,
        on_open = () => { _VOIP_SETTINGS_DROPDOWNTEST = false; },
        on_reset = () => { Voicechat.SetMicrophone(Voicechat.NOMIC); },
        settings_origin = typeof(Voicechat)
    };
    internal static SettingsTab _chat_setting_tab = new SettingsTab {
        name = "mmst_chat",
        gui_func = _GUI___SettingsChat,
        settings_origin = typeof(Chat)
    };
    internal static SettingsTab _debug_setting_tab = new SettingsTab {
        name = "mmst_debug",
        gui_func = _GUI___SettingsDebug,
        settings_origin = typeof(DebugMenuSettings)
    };
    internal static SettingsTab _bugreport_setting_tab = null;
    public static SettingsTab cur_settings_tab = _general_setting_tab;
    public static List<SettingsTab> ALL_SETTINGS = new List<SettingsTab> {
        _general_setting_tab,
        _chat_setting_tab,
        _voip_setting_tab,
        _gui_setting_tab,
    };

    static float MicVolumePreviewPixHeight = 20f;
    static float ASJOUFMIGHDSYDSGFMKFSDKYGJUFSYGJFSDGKVYJSFDKJGYFSDGKVJHBFJKVFDJVMBYFJDYG = 0.38f;
    internal static int last_writeX = 0;
    internal static bool writeX_flipflop = false;

    static bool _VOIP_SETTINGS_DROPDOWNTEST = false;
    static void _GUI___SettingsVOIP(Rect r) {
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        

        {
            var halfwidth = r.width * ASJOUFMIGHDSYDSGFMKFSDKYGJUFSYGJFSDGKVYJSFDKJGYFSDGKVJHBFJKVFDJVMBYFJDYG;
            // DO NOT USE _GUILayout_DropdownMenu HERE CUZ ITS KINDA DIFFERENT HERE


            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(Lang.Get("mmst_mike"), GUILayout.Width(halfwidth));
                if (_VOIP_SETTINGS_DROPDOWNTEST) {
                    GUILayout.Label(Lang.Get("mmst_select_mike"), GUILayout.Width(halfwidth));
                    if (GUILayout.Button(Lang.Get("mmst_mike_osdefault"))) {
                        _VOIP_SETTINGS_DROPDOWNTEST = false;
                        Voicechat.SetMicrophone(Voicechat.NOMIC);
                    }
                } else {
                    if (GUILayout.Button(Voicechat.GetMic() ?? Lang.Get("mmst_mike_osdefault"))) {
                        _VOIP_SETTINGS_DROPDOWNTEST = true;
                    }
                }
            }
            GUILayout.EndHorizontal();


            try {
                

                if (_VOIP_SETTINGS_DROPDOWNTEST) {
                    for (int i = 0; i < Microphone.devices.Length; i++) {
                        var mike = Microphone.devices[i];

                        GUILayout.BeginHorizontal();
                        {
                            GUILayout.Space((halfwidth));

                            if (GUILayout.Button(mike)) {
                                _VOIP_SETTINGS_DROPDOWNTEST = false;
                                Voicechat.SetMicrophone(mike);
                            }

                        }
                        GUILayout.EndHorizontal();
                    }

                }
            } catch (Exception e) {
                log.error("_VOIP_SETTINGS_DROPDOWNTEST\n" + e.ToString());
            }

            if (Voicechat.VoiceChatIsEnabledLocallyOnly()) {
                
                UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);

                GUILayout.BeginHorizontal();
                {
                    try {
                        if (GUILayout.Button(Voicechat.ENABLE_LOCAL_LOOPBACK ? Lang.Get("mmst_mike_testing") : Lang.Get("mmst_mike_test"))) {
                            Voicechat.ENABLE_LOCAL_LOOPBACK = !Voicechat.ENABLE_LOCAL_LOOPBACK;
                        }
                        {
                            var rect = GUILayoutUtility.GetRect(FixGuiContent.Content("uououo typ shi", 22), GUI.skin.button, GUILayout.ExpandWidth(true));
                            //var rect = GUILayoutUtility.GetRect(new GUIContent("uououo typ shi"), GUI.skin.button);

                            if (Event.current.type == EventType.Repaint) {

                                var scaledrectsize_float = ((rect.size / UIBullshit.uiScale));

                                var scaledrectsize = new Vector2Int(
                                    (int)(
                                        (scaledrectsize_float.x / scaledrectsize_float.y) * MicVolumePreviewPixHeight
                                    )
                                    , (int)MicVolumePreviewPixHeight);

                                if (UIBullshit.main.egg == null || UIBullshit.main.egg.textureSize != scaledrectsize) {
                                    ResetSettingsVOIPVisualiser();
                                    
                                    var go = new GameObject("hello vro");
                                    go.AddComponent<RawImage>().enabled = false;
                                    UIBullshit.main.egg = go.AddComponent<ECGVisualizer>();
                                    UIBullshit.main.egg.textureSize = scaledrectsize;
                                    UIBullshit.main.egg.followBody = false;
                                }

                            }
                            if (UIBullshit.main.egg != null && UIBullshit.main.egg.tex != null) {
                                if (UIBullshit.main.egg.writeX != last_writeX) {
                                    last_writeX = UIBullshit.main.egg.writeX;
                                    UIBullshit.main.egg.writeHeight = 0;
                                    writeX_flipflop = !writeX_flipflop;
                                }

                                GUI.DrawTexture(rect, UIBullshit.main.egg.tex);
                            }

                        }
                    } catch (Exception e) {
                        log.error("_VOIP_SETTINGS_LOOPBACKRENDER\n" + e.ToString());
                    }
                    //GUILayout.FlexibleSpace();
                }
                GUILayout.EndHorizontal();
            } else {
                GUILayout.Label(Lang.Get("mmst_mike_local_off"));
            }


        }


        if (Voicechat.IsMicModePushToTalk() || Voicechat.IsMicModeToggleToTalk()) {
            GUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Lang.Get("mmst_vcbutton") + KeyBinds.GetBindName(CoopKeybinds.voicechat).ToString())) {
                    UIMainMenu.SetOpen(false);
                    Util.OpenSettingsPanel(true, Setting.SettingCategory.Input);
                }
                DoTooltipOnLastGUIRect("mmst_gotokeybind");
            }
            GUILayout.EndHorizontal();
        }
        if (!Multiplayer.rules.VoicechatEnabled) {
            GUILayout.Label(Lang.Get("mmst_voip_rule_disabled") + "    ");
        } 

    }
   
    static void _GUI___SettingsGUI(Rect r) {

        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);

        GUI.skin.label.alignment = TextAnchor.UpperRight;
        GUILayout.BeginHorizontal();
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.Get("mmst_forcereloaduitex"))) {
                UIBullshit.ResizeGUITextures();
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);

            }
        }
        GUILayout.EndHorizontal();


    }

    

    static void _GUI___SettingsDebug(Rect r) {
        foreach (var item in NetObjectRegistry.NetIdToSyncInfoDict) {
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"{item.Value.ToString()}");
                GUILayout.FlexibleSpace();
                if (Con.CanCheat() && item.Value.go != null) {
                    if (GUILayout.Button($"Destroy")) {
                        GameObject.Destroy(item.Value.go);
                    }
                    if (GUILayout.Button($"TP: {item.Value.transformPosition}")) {
                        PlayerCamera.main.body.transform.position = item.Value.transformPosition;
                    }
                }
            }
            GUILayout.EndHorizontal();
        }
    }

    static void _GUI___SettingsChat(Rect r) {
        Chat.ShowChat();


        {
            GUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Lang.Get("mmst_chatbutton") + KeyBinds.GetBindName(CoopKeybinds.chat).ToString())) {
                    UIMainMenu.SetOpen(false);
                    Util.OpenSettingsPanel(true, Setting.SettingCategory.Input);
                }
                DoTooltipOnLastGUIRect("mmst_gotokeybind");
            }
            GUILayout.EndHorizontal();
        }
    }




    public static Action DrawUpdateButton = () => { };
    public static Action BuildUpdateButton = () => { };


    static void _GUI___SettingsGeneral(Rect r ) {
        var ogeneabled = GUI.enabled;

        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
        GUILayout.Space(20 * GetMenuUIScale());

        if (DrawUpdateButton != null) {
            try {
                DrawUpdateButton();
            } catch(Exception e) {
                
            }
        }

        GUILayout.Space(20 * GetMenuUIScale());

        GUILayout.BeginHorizontal(); 
        {
            var activatethisbutton = !Net.IsRunning && !Util.IsInWorld();

            GUI.enabled = activatethisbutton;
            if (GUILayout.Button(Lang.Get("mmst_button_deactivate_mp"))) {
                PlayerPrefsExtended.SetBool(Plugin.FORCEDISABLE_PLAYERPREF_KEY, true);
                PlayerPrefs.Save();

                Plugin.RestartGame();
            }
            GUI.enabled = ogeneabled;
            DoTooltipOnLastGUIRect(activatethisbutton ? "mmst_button_deactivate_mp_desc" : "mmst_button_deactivate_mp_desc_disabled");
               
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }



    static void __GUI____RenderFieldsWithThatAttribute(List<FieldInfo> flds, in Rect r) {
        var halfwidth = r.width * ASJOUFMIGHDSYDSGFMKFSDKYGJUFSYGJFSDGKVYJSFDKJGYFSDGKVJHBFJKVFDJVMBYFJDYG;
        foreach (var f in flds) {
            if (f.HasAttribute(typeof(SettingDeclarerThingyBaseAttribute))) {
                var attr = f.GetAttribute<SettingDeclarerThingyBaseAttribute>();

                var langkey = attr.n;
                if (langkey == null) {
                    langkey = "setting_" + f.DeclaringType + "_" + f.Name;
                }

                GUI.skin.label.alignment = TextAnchor.MiddleRight;
                GUILayout.BeginHorizontal();
                {
                    GUILayout.Label(Lang.Get(langkey), GUILayout.Width(halfwidth));

                    if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                        var desckey = langkey + "_desc";

                        if (Lang.TryGet(desckey, out var value)) {
                            UIBullshit._GUI_SetTooltip("", value);
                        }
                    }

                    if (attr is SettingDeclarerThingyFloatAttribute f_attr) {

                        var val = (float)f.GetValue(null);

                        var newval = GUILayout.HorizontalSlider(val,
                            Mathf.Min(val, f_attr.mi),
                            Mathf.Max(val, f_attr.ma)
                        );

                        GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                        GUILayout.Label(newval.ToString("0.0") + f_attr.postfix, GUILayout.Width(r.width * 0.1f));

                        if (val != newval) {
                            f_attr.Save(f, newval);
                        }

                    } else if (attr is SettingDeclarerThingyBoolAttribute b_attr) {

                        var val = (bool)f.GetValue(null);
                        var newval = GUILayout.Toggle(val, "");

                        if (val != newval) {
                            b_attr.Save(f, newval);
                        }

                    } else if (attr is SettingDeclarerThingyChoiceAttribute c_attr) {

                        var val = f.GetValue(null).ConvertTo<int>();

                        var translated_choices = new List<string>(c_attr.choices).Select(x => Lang.Get(x)).ToArray();

                        var newval = GUILayout_DropdownMenu.Dropdown(val, translated_choices);

                        if (val != newval) {
                            c_attr.Save(f, newval);
                        }

                    } else {
                        GUILayout.Toggle(false, "ERROR UNKNOWN TYPE");
                    }

                }
                GUILayout.EndHorizontal();

            }
        }
    }


    internal static Dictionary<Type, List<FieldInfo>> _known_settings = new();
    public static List<FieldInfo> GetSettingsFromStaticClass(Type type) {
        if (!_known_settings.TryGetValue(type, out var fields)) {
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).ToList();
            fields.RemoveAll(f => !f.HasAttribute(typeof(SettingDeclarerThingyBaseAttribute)));

            foreach (var f in fields) {
                var attr = f.GetAttribute<SettingDeclarerThingyBaseAttribute>();
                attr.default_value = f.GetValue(null);

                attr.Get(f);
            }

            if (fields.Count > 0)
                _known_settings[type] = fields;
        }

        return fields; 
    }

#endregion

    #region ABOUT_MENU_BULLSHIT


    static void _GUI_RenderAboutMenu(Rect r) {

        //r = new Rect(r);
        //r.width = Mathf.Min(r.width, GetMenuUIScale() * 600);

        //GUILayout.BeginArea(r);
        _ABOUTMENUSCROLL = GUILayout.BeginScrollView(_ABOUTMENUSCROLL);

        var ogfontsize = GUI.skin.label.fontSize;
        //GUI.skin.label.fontSize = (int)(ogfontsize * 1.3f);

        var ogalign = GUI.skin.label.alignment;
        GUI.skin.label.wordWrap = false;

        GUILayout.Space(10 * GetMenuUIScale());

        //TODO steam guide link 
        _GUI__AboutMenuLinkButton(Lang.Get("mmta_og_download"), Constants.OG_DOWNLOAD_URL, r);
        _GUI__AboutMenuLinkButton(Lang.Get("mmta_discord"), Constants.OG_MOD_DISCORD_URL, r);


        GUI.skin.label.alignment = TextAnchor.UpperCenter;
        GUILayout.Label("");
        UIBullshit._GUI_BiggerLabel(Lang.Get("mmta_credits"), 1.2f);
        
        try {
            _GUI__ABOUTMENU_FULL_CREDITS(r);

            //TODO mention previous failed mp mod creators? (Dathusss, Pookie? i forgor)
            //GUI.skin.label.alignment = TextAnchor.UpperCenter;
            //GUILayout.Label("Honorable mentions:" + );

        }
        catch (Exception e) {
            log.error($"RENDER: \n{e.ToString()}");
        }

        GUILayout.Label("");

        UIBullshit._GUI_BiggerLabel(Lang.Get("mmta_libraries"), 1.2f);

        _GUI__AboutMenuLinkButton("LiteNetLib", "https://github.com/RevenantX/LiteNetLib", r);
        _GUI__AboutMenuLinkButton("LiteEntitySystem", "https://github.com/RevenantX/LiteEntitySystem", r);
        _GUI__AboutMenuLinkButton("Steamworks.NET", "https://github.com/rlabrecque/Steamworks.NET", r);
        _GUI__AboutMenuLinkButton("OpusSharp", "https://github.com/AvionBlock/OpusSharp", r);
        _GUI__AboutMenuLinkButton("BepInEx", "https://github.com/BepInEx/BepInEx", r);

        GUI.skin.label.fontSize = ogfontsize;
        GUI.skin.label.alignment = ogalign;

        GUILayout.Label("");
        GUILayout.EndScrollView();
        //GUILayout.EndArea();

    }

    static string LastCopiedLink = "E";

    static void _GUI__AboutMenuLinkButton(in string name, in string link, in Rect r) {

        GUI.skin.label.alignment = TextAnchor.MiddleRight;

        GUILayout.BeginHorizontal();
        {
            GUILayout.Label(name, GUILayout.Width(r.width * 0.5f));
            if (GUILayout.Button(Lang.Get("mmta_link_open"))) {
                //OG: PreRunScript.OpenURL  V6.0DEMO

                Application.OpenURL(link);
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }

            if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                UIBullshit._GUI_SetTooltip("", link);
            }

            if (GUILayout.Button(Lang.Get("mmta_link_copy"))) {
                GUIUtility.systemCopyBuffer = link;
                LastCopiedLink = link;
                //UIBullshit.DoAlert(Lang.Get("mmta_link_copied") + "\n" + link);
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }

            if (LastCopiedLink == link) {
                GUILayout.Label(Lang.Get("mmta_link_copied"));
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    internal static (Texture2D, string)[] Credits = new (Texture2D, string)[] {
        (null, "Creature - MP Mod Developer"),
        (null, "JyanSei - Optimization assistance"),
        (null, "dannad - Auto-updater module, Installer tool, C:T ds Staff"),
        (null, "Gary the cat - playtester, Bug report module, past servers, C:T ds Staff"), //  , past mod \"Cat-Patch\"
        (null, "Todd - external mod \"ChangeSkin\", C:T ds Staff"),
        (null, "ItsVoidSK - playtester, some sprites"),
        (null, "dawn - TimeManipulation patch"),
        (null, "VtLoader - Bug fixes"),
        (null, "Jilu-tin - playtester"),
        (null, "NyaruQwQ - playtester"),
        (null, "X0men0X - playtester"),
        (null, "[Cider] - playtester"),
        (null, "avr - playtester"),
        (null, "gordocats - self-heal sprite"),
        (null, "notamoron - playtester"),
        (null, "Devactor - playtester"),
        (null, "Dictator - playtester"),
        (null, "Мрак 2.0 - playtester"),
        (null, "Dmonya - playtester"),
        (null, "Shiro - playtester"),
        (null, "Charu - playtester"),
        (null, "kipish - playtester"),
        (null, "Zero Ares IV - Supporter"),
        (null, "EthanTheProtogen - Supporter"),
        (null, "RoyMartini - Supporter"),
        (null, "Jazun - Supporter"),
        (null, "Alexx__ - Supporter"),
        (null, "vega - Supporter"),
        (null, "Zmechol1337 - Supporter"),
        (null, "plasterjello - Supporter"),
        (null, "Doctor Callico - Supporter"),
        (null, "vivikat - Supporter"),
        (null, "Ariyah - Supporter"),
        (null, "therealbudget - Supporter"),
        (null, "_stuck_ - Supporter"),
        (null, "ShaoAZ - Supporter"),
        (null, "JameisonAus - Supporter"),
        (null, "Kidi - Supporter"),
        (null, "BluBerryToaster - Supporter"),
        (null, "Katomou - Supporter"),
        (null, "Nemean_Lion732 - Supporter"),
        (null, "Locko2265 - Supporter"),
        (null, "Trenchburner - Supporter"),
        (null, "TheBoathead - Supporter"),
        (null, "Diesel - Supporter"),
        (null, "Mitirich - Supporter"),
        (null, "Krokosha666 - OG MP Mod Developer, Retired"),
        (null, "Orsoniks - Developer of Casualties: Unknown"),
        (null, "...and the rest of bug reporters, suggestions and regular players"),
    };

    static void _GUI__ABOUTMENU_FULL_CREDITS(Rect r) {

        foreach (var item in Credits) {
            GUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace();
                var textsize = GUI.skin.label.CalcSize(FixGuiContent.Content(item.Item2, 23));
                if (item.Item1 != null) {
                    GUILayout.Label(item.Item1, GUILayout.Width(textsize.y), GUILayout.Height(textsize.y));
                    GUILayout.Space(textsize.y * 0.1f);
                }
                GUILayout.Label(item.Item2);
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
        }

    }
    #endregion



    static Vector2 _ABOUTMENUSCROLL = Vector2.zero;
    static Vector2 _SETTINGSMENUSCROLL = Vector2.zero;






}




// based on ai code
public static class GUILayout_DropdownMenu {
    public static bool isOpen => activeID != -1;
    private static int activeID = -1;

    private static Rect activeRect;
    private static string[] activeOptions;
    static int clickedID = -1;
    static int clickedNumToReturn = 0;

    static int id_counter = 0;

    public static void CloseAll() {
        clickedID = -1;
        activeID = -1;
    }

    /// <summary>
    /// Call this anywhere in OnGUI
    /// </summary>
    public static int Dropdown(int selectedIndex, string[] options, params GUILayoutOption[] uILayoutOptions) {
        id_counter++;
        if (selectedIndex > 0 && selectedIndex >= options.Length) {
            selectedIndex = options.Length - 1;
        }
        GUIContent content;
        float maxwidth = 0;
        if (options.Length == 0) {
            selectedIndex = 0;
            content = FixGuiContent.Content("[NONE]", 24);
        } else {
            content = FixGuiContent.Content(options[selectedIndex], 25);
            foreach (var item in options) {
                var gggg = GUI.skin.button.CalcSize(FixGuiContent.Content(item, 26));
                maxwidth = Mathf.Max(maxwidth, gggg.x);
            }
        }
        {
            var gggg = GUI.skin.button.CalcSize(content);
            maxwidth = Mathf.Max(maxwidth, gggg.x);
        }
        if (uILayoutOptions.Length == 0) {
            uILayoutOptions = uILayoutOptions.AddToArray( GUILayout.MinWidth(maxwidth));
        }

        var buttonrect = GUILayoutUtility.GetRect(content, GUI.skin.button, uILayoutOptions);

        var result = selectedIndex;

        var aaaaaaaaaaa = clickedID == id_counter;
        if (aaaaaaaaaaa || activeID == id_counter) {
            if (aaaaaaaaaaa) {
                clickedID = -1;
                result = clickedNumToReturn;
            }

            GUI.color = new Color(0, 0, 0, 0);
            var oldenabled = GUI.enabled;
            GUI.enabled = false;
            GUI.Button(buttonrect, content);
            GUI.enabled = oldenabled;
            GUI.color = Color.white;
            return result;
        }
        if (
            GUI.Button(buttonrect, content)
        ) {
            clickedID = -1;

            if ( activeID == id_counter) {
                activeID = -1;
            } else {
                activeID = id_counter;
                activeRect = GUIUtility.GUIToScreenRect(buttonrect);
                activeOptions = options;
            }
            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        return result;
    }

    static float dropdownitemheight = 35;
    /// <summary>
    /// MUST be called at the END of OnGUI
    /// </summary>
    public static void Draw() {
        id_counter = 0;
        if (!isOpen || activeOptions == null || activeOptions.Count() == 0) return;
        
        try {
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);

            float itemHeight = dropdownitemheight * UIBullshit.uiScale;

            Rect dropdownRect = new Rect(
                activeRect.x,
                activeRect.y,
                activeRect.width,
                activeOptions.Length * itemHeight
            );


            for (int i = 0; i < activeOptions.Length; i++) {
                Rect r = new Rect(
                    dropdownRect.x,
                    dropdownRect.y + i * itemHeight,
                    dropdownRect.width,
                    itemHeight
                );

                if (GUI.Button(r, activeOptions[i])) {
                    clickedID = activeID;
                    clickedNumToReturn = i;
                    activeID = -1;
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                }
            }

            // Close when clicking outside
            if (Event.current.type == EventType.MouseDown) {
                if (
                    !dropdownRect.Contains(Event.current.mousePosition)
                    && !activeRect.Contains(Event.current.mousePosition)
                ) {
                    activeID = -1;
                }
            }
        }
        catch (Exception e) {
            log.error(e);   
        }
        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
    }
}






