using BepInEx;
using HarmonyLib;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using KrokV5Optimization;

namespace Together;







public class Chat : CasualtiesTogetherSingleton {

    public static event Action<ScavPlayer,string> OnPlayerChatMessage;



    [SettingDeclarerThingyFloat(0, 1, "setting_chatposx")]
    public static float SETTING_posX  = 1;
    [SettingDeclarerThingyFloat(0, 1, "setting_chatposy")]
    public static float SETTING_posY  = 1;
    [SettingDeclarerThingyFloat(0, 3, "setting_chatsizex")]
    public static float SETTING_sizeX = 1;
    [SettingDeclarerThingyFloat(0, 3, "setting_chatsizey")]
    public static float SETTING_sizeY = 1;
    [SettingDeclarerThingyFloat(0, 2, "setting_chatscale")]
    public static float SETTING_scale = 1;

    [SettingDeclarerThingyBool("setting_chatpreview")]
    public static bool _DEV_ENABLE_PREVIEW = true;
    [SettingDeclarerThingyBool("setting_chatpreviewonside")]
    public static bool _DEV_PREVIEW_ON_SIDE = false;

    public static bool ALLOW_ME = false;



    public static class DefaultChatColors {
        public static Color24 Server = Color24.FromHex("#FFC90E");
        public static Color24 Kill = Color24.FromHex("#F15058");
    }

    public static string AddColorTags(Color24 color, string text) {
        return $"<color={color.ToHex()}>{text}</color>";
    }


    static void DoNothing() {

    }

    public static void ShowChat() {
        focustime = Time.realtimeSinceStartupAsDouble;
    }

    void Start() {

        ShowChat();
    }

    void Update() {


        if (!Net.IsRunning) { return; }

        if (scrollbar_area != null) {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Mouse0) && UIBullshit.IsCursorInGUIRect((Rect)Chat.scrollbar_area)) {
                scrollbar_is_being_interacted = true;
            }
        }
        if (UnityEngine.Input.GetKeyUp(KeyCode.Mouse0)) {
            scrollbar_is_being_interacted = false;
        }

        if (Con.IsConsoleOpen()) {
            CHATforceinputfocus = false;
            CHAT_textbox_input_focused = false;
        } else {
            if (!CHAT_textbox_input_focused) {
                _preview_distorted_message = "";

                if (UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.chat))) {
                    if (PlayerCamera.main && ConsoleScript.instance.active) {
                        return;
                    }

                    focustime = Time.realtimeSinceStartupAsDouble;

                    UpdateMessageDistortPreview();
                    CHAT_textbox_input_focused = true;
                    CHATforceinputfocus = true;
                    _chatinput_timer = 2;
                }
                return;
            } else {




            }
        }


        if (Util.IsLocalBodyAlive() && CHAT_textbox_input_focused && Multiplayer.rules.SpeechImpairedChat && !String.IsNullOrWhiteSpace(CHAT_current_input)) {

            _chatinput_timer += Time.deltaTime;

            if (_chatinput_changed || _chatinput_timer > 1f) {
                UpdateMessageDistortPreview();
            }

        }

    }
    public static void UpdateMessageDistortPreview() {
        _chatinput_timer = 0;
        _chatinput_changed = false;

        if (Util.TryGetLocalBody(out var locbody)) {

            _preview_distorted_message = locbody.talker.DistortString(CHAT_current_input);

            if (locbody.TryGetComponent(out NetBody nb)) {
                string ggggg = "test";
                nb.HearinglossDistortMessage(ScavPlayer.LocalPlayer.netBody, ref _preview_distorted_message, ref ggggg);
            }

        } else {
            _preview_distorted_message = "";
        }

    }




    public static bool MouseIsInteractingWithScrollbar() {
        if (scrollbar_area != null) {
            return scrollbar_is_being_interacted; 
        }
        return false;
    }

    public static bool _DEV_LOG_CHAT = false;

    public static bool SHOULD_LOG_CHAT => (_DEV_LOG_CHAT || log.verbose || Con._DEV_CHATSPY);


    public static float DEV_CHAT_FULL_WIDTH = 500;
    public static float DEV_CHAT_HISTORY_HEIGHT = 250;
    public static float DEV_CHAT_TEXTFIELD_HEIGHT = 40;


    const string CHAT_TEXTFIELD_CONTROLNAME = "ChatField";

    static bool scrollbar_is_being_interacted = false;
    static Nullable<Rect> scrollbar_area = null;

    static bool mouse_is_in_chat_area = false;
    static double focustime = 0;

    static bool _chatinput_changed = false;
    static float _chatinput_timer = 0;
    static string _preview_distorted_message = "";

    // called from the main component

    // icons sit in the column before the text. the label width is whatever is left.
    static float ChatIconStrip(ScavPlayer plr) {
        if (plr == null)
            return 0f;
        float step = _GUI_RealChatFontsize + (int)(8f * UIBullshit.uiScale);
        float w = 0f;
        foreach (var tag in plr.KnownUserTagIcons)
            w += step;
        if (plr.IconAnySmallToLarge != null)
            w += step;
        return w;
    }

    // same width _GUI_ChatDrawMessage hands the label. a wider guess treats a short second line as one line, and it paints over the next message.
    static void MeasureChatLine(ChatMsgContainer text, float columnWidth) {
        text.namesize = GUI.skin.label.CalcSize(FixGuiContent.Content(text.result_nametag, 9));
        float icons = ChatIconStrip(text.plr);
        float drawColumn = Mathf.Max(1f, columnWidth - icons);
        float nametag = text.namesize.x + 1f + icons;
        int spaces = Mathf.Max(0, Mathf.CeilToInt((nametag - drawColumn * 0.1f) / _GUI_SizeOfSpace.x));
        text.msg2 = new string(' ', spaces) + text.msg;
        text.height = GUI.skin.label.CalcHeight(FixGuiContent.Content(text.msg2, 10), drawColumn * 0.91f);
    }

    static float MeasureHistory(float columnWidth) {
        float total = 0f;
        foreach (var text in CHAT_LOG.JustGiveTheQueue()) {
            MeasureChatLine(text, columnWidth);
            total += text.height;
        }
        return total;
    }

    static void _GUI_ChatDrawMessage(ChatMsgContainer textinfo, float real_pos_X, ref float this_y, float chathistory_area_width, float texthiehgt) {

        float space_used = 0;
        chathistory_area_width -= ChatIconStrip(textinfo.plr);

        if (textinfo.plr != null ) {

            var pic_size_on_axis = _GUI_RealChatFontsize;
            Rect rect = new Rect(real_pos_X + space_used, this_y + 1f, pic_size_on_axis, pic_size_on_axis);
            var spacing_gap = (int)(8f * UIBullshit.uiScale);
            var full_tag_size_x = pic_size_on_axis + spacing_gap;

            foreach (var tagimage in textinfo.plr.KnownUserTagIcons) {
                rect.x = real_pos_X + space_used;

                GUI.DrawTexture(rect, tagimage.texture);

                space_used += full_tag_size_x;
            }

            var profilepic = textinfo.plr.IconAnySmallToLarge;
            if (profilepic != null) {
                rect.x = real_pos_X + space_used;

                GUI.DrawTexture(rect, profilepic);

                space_used += full_tag_size_x;
            }
        }
        
        GUI.skin.label.richText = true;
        GUI.Label(new Rect(real_pos_X + space_used, this_y, textinfo.namesize.x, textinfo.namesize.y), textinfo.result_nametag);
        space_used += textinfo.namesize.x;

        GUI.skin.label.richText = textinfo.rich;
        GUI.skin.label.wordWrap = true;
        GUI.Label(new Rect(real_pos_X + chathistory_area_width * 0.1f, this_y, chathistory_area_width * 0.91f, texthiehgt + textinfo.namesize.y*2), textinfo.msg2);
    }
    static Vector2 _GUI_SizeOfSpace = Vector2.one;
    public static int _GUI_RealChatFontsize => Mathf.RoundToInt(UIBullshit.GetCurFontSize() * 0.82f * SETTING_scale);
    // true when the box, history, or input will paint. does not touch GUI.skin.
    internal static bool WantsGui()
    {
        bool forceSettings = !Net.IsRunning && UIMainMenu.IsInSettings() && UIMainMenu.cur_settings_tab == UIMainMenu._chat_setting_tab;
        if ((!forceSettings && (!Net.IsRunning || Con._DEV_HIDEHUD))
            || SETTING_scale <= 0 || SETTING_sizeY <= 0 || SETTING_sizeX <= 0)
            return false;

        if (CHATforceinputfocus || CHAT_textbox_input_focused)
            return true;
        if (Time.realtimeSinceStartupAsDouble - focustime < 5.0)
            return true;

        float finalScale = SETTING_scale * UIBullshit.uiScale;
        float areaWidth = DEV_CHAT_FULL_WIDTH * finalScale * SETTING_sizeX;
        float areaHeight = Mathf.Max(0f, DEV_CHAT_HISTORY_HEIGHT * finalScale * SETTING_sizeY);
        float textHeight = DEV_CHAT_TEXTFIELD_HEIGHT * finalScale;
        float x = (Screen.width - areaWidth) * SETTING_posX;
        float y = (Screen.height - areaHeight - textHeight) * SETTING_posY;
        var window = new Rect(x, y, areaWidth, areaHeight + textHeight);
        window.xMin -= 50f;
        window.xMax += 50f;
        window.yMin -= 50f;
        window.yMax += 50f;
        mouse_is_in_chat_area = UIBullshit.IsCursorInGUIRect(window);
        return mouse_is_in_chat_area;
    }

    internal static void _GUI_DrawChat()
    {
        var is_forceshow_in_settings = (!Net.IsRunning && UIMainMenu.IsInSettings() && UIMainMenu.cur_settings_tab == UIMainMenu._chat_setting_tab);

        if ((
                !is_forceshow_in_settings
                && (!Net.IsRunning || Con._DEV_HIDEHUD) 
            ) 
            || (SETTING_scale <= 0 || SETTING_sizeY <= 0 || SETTING_sizeX <= 0)
        ) {
            CHATforceinputfocus = false;
            CHAT_textbox_input_focused = false;
            return; 
        }

        // hidden chat used to rewrite label and text-field styles on every repaint.
        if (!WantsGui())
            return;

        var ogwordwrap = GUI.skin.label.wordWrap;
        var ogfontsize = GUI.skin.label.fontSize;
        var ogboxfontsize = GUI.skin.box.fontSize;
        var ogtextfieldfontsize = GUI.skin.textField.fontSize;
        // keep the typing face when the box is only popped up, or the console has taken focus
        bool pinnedfont = UIBullshit.PushFocusedChatFont(out var oglabelfont, out var ogboxfont, out var ogfieldfont);
        try {
            var unchipped_hide_chat_history = Util.IsWorldGenerated() && (!(
                Con._DEV_CHATSPY //&& KrokoshaScavMultiConsoleCommands.CanCheat()
                )) && Util.IsLocalBodyAlive() && (//(WorldGeneration.unchipped && Multiplayer.rules.DisableChatboxIfUnchipped) || 
                !Multiplayer.rules.EnableChatbox);

            var real_chat_fontsize = _GUI_RealChatFontsize;

            var final_scale = SETTING_scale * UIBullshit.uiScale;

            GUI.skin.label.fontSize = (real_chat_fontsize);
            GUI.skin.box.fontSize = (real_chat_fontsize);
            GUI.skin.textField.fontSize = (real_chat_fontsize);
            // mp-chatbox-pos 100 100 100 100 70
            
            var areawidth = DEV_CHAT_FULL_WIDTH * final_scale * SETTING_sizeX;
            var areaheight = Mathf.Max(0, DEV_CHAT_HISTORY_HEIGHT * final_scale * SETTING_sizeY);

            var textareaheight = DEV_CHAT_TEXTFIELD_HEIGHT * final_scale;

            var real_pos_X = (Screen.width - areawidth) * SETTING_posX;
            var real_pos_Y = (Screen.height - areaheight - textareaheight) * SETTING_posY;

            var chathistory_area = new Rect(real_pos_X, real_pos_Y, areawidth, areaheight);
            var chatinputrect = new Rect(real_pos_X, real_pos_Y + areaheight, areawidth, textareaheight);


            var chatareawidth = areawidth - UIBullshit.uiBlockNanoBorderSize;


            bool should_show_it_all = false;
            if (Time.realtimeSinceStartupAsDouble - focustime < 5f) {
                should_show_it_all = true;
            }


            var chatinputrectfocustest = new Rect(chatinputrect);
            chatinputrectfocustest.xMin -= 50;
            chatinputrectfocustest.xMax += 50;
            chatinputrectfocustest.yMin -= 50;
            chatinputrectfocustest.yMax += 50;

            if (should_show_it_all || CHATforceinputfocus || CHAT_textbox_input_focused || mouse_is_in_chat_area || UIBullshit.IsCursorInGUIRect(chatinputrectfocustest)) {

                if (CHATforceinputfocus) {
                    GUI.FocusControl(CHAT_TEXTFIELD_CONTROLNAME);
                    CHAT_textbox_input_focused = true;
                    CHATforceinputfocus = false;
                };

                if (CHAT_textbox_input_focused) {

                    {
                        var curkeycode = Event.current.keyCode;
                        if (Event.current.type == EventType.KeyDown && (curkeycode == KeyCode.UpArrow || curkeycode == KeyCode.DownArrow)) {

                            int historytraverse = 0;
                            if (curkeycode == (KeyCode.UpArrow)) {
                                historytraverse = (-1);
                            }
                            if (curkeycode == (KeyCode.DownArrow)) {
                                historytraverse = (1);
                            }
                            if (historytraverse != 0) {
                                if (MyMessageLog.Count != 0) {
                                    MyMessageLog_Index += historytraverse;
                                    if (MyMessageLog_Index < 0) {
                                        MyMessageLog_Index = 0;
                                    } else {
                                        if (MyMessageLog_Index >= MyMessageLog.Count) {
                                            MyMessageLog_Index = MyMessageLog.Count - 1;
                                        } else {
                                            if (!String.IsNullOrWhiteSpace(CHAT_current_input) && !MyMessageLog.Contains(CHAT_current_input)) {
                                                MyMessageLog.Enqueue(CHAT_current_input);
                                            }
                                            CHAT_current_input = MyMessageLog.ElementAt(MyMessageLog_Index);
                                            _chatinput_changed = true;
                                            CHATforceinputfocus = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    GUI.SetNextControlName(CHAT_TEXTFIELD_CONTROLNAME);
                    var old = CHAT_current_input;
                    CHAT_current_input = GUI.TextField(chatinputrect, CHAT_current_input);
                    if (old != CHAT_current_input) {
                        _chatinput_changed = true;
                        CHATforceinputfocus = true;
                    }
                    GUI.SetNextControlName("NONE");
                } else {

                    UIBullshit._GUI_9SlicePanel(chatinputrect, 1, 0.65f, false, UIBullshit.unscaled_uiBlockNano);
                    var smoler = new Rect(chatinputrect);
                    smoler.xMin += UIBullshit.uiBlockNanoBorderSize;
                    //smoler.min += Vector2.one * UIBullshit.uiBlockNanoBorderSize ;
                    //smoler.max -= Vector2.one * UIBullshit.uiBlockNanoBorderSize;

                    GUI.skin.label.alignment = TextAnchor.MiddleCenter;

                    GUI.Label(smoler,
                        String.IsNullOrEmpty(CHAT_current_input) ? String.Format(Lang.Get("chat_keybind"), KeyBinds.GetBindName(CoopKeybinds.chat)) : CHAT_current_input
                    );

                    GUI.skin.label.alignment = TextAnchor.UpperLeft;

                    //if (String.IsNullOrEmpty(CHAT_current_input)) {
                    //    GUI.Box(chatinputrect, String.Format(Lang.Get("chat_keybind"), KeyBinds.GetBindName(CoopKeybinds.chat)));
                    //} else {
                    //    GUI.Box(chatinputrect, CHAT_current_input);
                    //}
                }


                if (CHAT_textbox_input_focused) {
                    if (Event.current.keyCode == KeyCode.Escape) {

                        CHATforceinputfocus = false;
                        CHAT_textbox_input_focused = false;
                        GUI.FocusControl(""); // unfocus the text box

                        Event.current.keyCode = KeyCode.None;

                    } else if (Event.current.keyCode == KeyCode.Return) {

                        CHATforceinputfocus = false;
                        CHAT_textbox_input_focused = false;
                        GUI.FocusControl(""); // unfocus the text box
                        _chatinput_changed = true;

                        if (!string.IsNullOrEmpty(CHAT_current_input)) {
                            OnEnteredUserMessage();
                        }
                        Event.current.keyCode = KeyCode.None;

                    } else {


                        focustime = Time.realtimeSinceStartupAsDouble;
                    }


                    if (CHAT_current_input.Count() > MAX_MESSAGE_LENGTH) {
                        CHAT_current_input = CHAT_current_input.Substring(0, MAX_MESSAGE_LENGTH);
                    }
                }

                CHAT_textbox_input_focused = GUI.GetNameOfFocusedControl() == CHAT_TEXTFIELD_CONTROLNAME;

            }






            GUI.SetNextControlName("NONE");
            if (_DEV_ENABLE_PREVIEW) {
                try {
                    if (
                        (
                            Util.IsLocalBodyAlive() 
                            && CHAT_textbox_input_focused 
                            && Multiplayer.rules.SpeechImpairedChat 
                            && !String.IsNullOrWhiteSpace(CHAT_current_input) 
                            && !CHAT_current_input.StartsWith("/")
                        ) &&
                        (!String.IsNullOrWhiteSpace(_preview_distorted_message) && _preview_distorted_message != CHAT_current_input)
                    ) {

                        var torender = Lang.Get("chat_preview") + _preview_distorted_message;

                        float previewareaheight = Mathf.Max(textareaheight, GUI.skin.label.CalcHeight(FixGuiContent.Content(torender, 7), areawidth));

                        var chatpreviewrect = new Rect(chatinputrect);
                        if (!_DEV_PREVIEW_ON_SIDE) { 
                            chatpreviewrect.y -= previewareaheight;
                            chatpreviewrect.height = previewareaheight;
                            chathistory_area.height -= previewareaheight;
                        } else {
                            chatpreviewrect.y = chatinputrect.yMax - previewareaheight;

                            if (SETTING_posX > 0.5f) {
                                chatpreviewrect.x -= chatpreviewrect.width;
                            } else {
                                chatpreviewrect.x += chatpreviewrect.width;
                            }

                            chatpreviewrect.height = previewareaheight;
                        }

                        GUI.skin.label.wordWrap = true;
                       
                        UIBullshit._GUI_9SlicePanel(chatpreviewrect, 1, 0.65f, false, UIBullshit.unscaled_uiBlockNano);
                        //GUI.Box(chatpreviewrect, "");

                        var chatpreviewrect_label = new Rect(chatpreviewrect);
                        chatpreviewrect_label.xMin += UIBullshit.uiBlockNanoBorderSize;
                        chatpreviewrect_label.xMax -= UIBullshit.uiBlockNanoBorderSize;
                        //chatpreviewrect_label.min += Vector2.one * UIBullshit.uiBlockNanoBorderSize;
                        //chatpreviewrect_label.max -= Vector2.one * UIBullshit.uiBlockNanoBorderSize;

                        GUI.Label(chatpreviewrect_label, torender);
                        GUI.skin.label.wordWrap = ogwordwrap;

                        
                    } 
                } catch (Exception e) {
                    log.error("CHAT PREVIEW RENDER ERROR:\n" + e.ToString());
                }
            }
            
           

            var full_rect_of_entire_window = new Rect(real_pos_X, chathistory_area.y, areawidth, chatinputrect.yMax - chathistory_area.y);

            // check if cursor is in this area to add focustime
            mouse_is_in_chat_area = UIBullshit.IsCursorInGUIRect(full_rect_of_entire_window);
            if (mouse_is_in_chat_area) {
                focustime = Time.realtimeSinceStartupAsDouble;
            }

            if (areaheight > 0 && areawidth > 0) {
                if (Time.realtimeSinceStartupAsDouble - focustime < 2f && !unchipped_hide_chat_history) {
                    //GUI.Box(chathistory_area, "");
                    UIBullshit._GUI_9SlicePanel(chathistory_area, 1, 0.65f, false, UIBullshit.unscaled_uiBlockNano);
                    
                }
            }








            if (should_show_it_all && !unchipped_hide_chat_history && areaheight > 0) {
                GUI.SetNextControlName("NONE");

                GUI.skin.label.richText = true;
                GUI.skin.label.wordWrap = true;

                var textpos_x = real_pos_X + UIBullshit.uiBlockNanoBorderSize;

                _GUI_SizeOfSpace = GUI.skin.label.CalcSize(FixGuiContent.Content(" ", 8));
                if (_GUI_SizeOfSpace.x <= 0) {
                    _GUI_SizeOfSpace = Vector2.one;
                }

                GUI.skin.label.alignment = TextAnchor.UpperLeft;

                float full_text_height = MeasureHistory(chatareawidth);

                if (full_text_height > areaheight) {
                    var ogwidth = GUI.skin.verticalSlider.fixedWidth;
                    GUI.skin.verticalSlider.fixedWidth = GUI.skin.verticalSlider.fixedWidth / UIBullshit.uiScale * final_scale;
                    var scrollbarwidth = GUI.skin.verticalSlider.fixedWidth;
                    chatareawidth = Mathf.Max(1f, chatareawidth - scrollbarwidth * 1.05f);
                    full_text_height = MeasureHistory(chatareawidth);

                    var scrollbarrect = new Rect(chathistory_area.xMax - scrollbarwidth, chathistory_area.y, scrollbarwidth, chathistory_area.height);
                    scrollbar_area = scrollbarrect;

                    CHATScrollPosition = GUI.VerticalScrollbar(scrollbarrect, CHATScrollPosition, areaheight, full_text_height, 0f);

                    GUI.skin.verticalSlider.fixedWidth = ogwidth;
                } else {
                    CHATScrollPosition = 0;
                    scrollbar_area = null;
                }


                GUI.skin.label.alignment = TextAnchor.UpperLeft;

                var this_y = (chathistory_area.yMax) + CHATScrollPosition;

                for (int i = CHAT_LOG.Count - 1; i >= 0; i--) {
                    var textinfo = CHAT_LOG.ElementAt(i);
                    var texthiehgt = textinfo.height;

                    this_y -= texthiehgt;

                    if (this_y <= chatinputrect.y - texthiehgt * 0.8f) {
                        if (this_y < chathistory_area.y - texthiehgt) {
                            break;
                        }
                        _GUI_ChatDrawMessage(textinfo, textpos_x, ref this_y, chatareawidth, texthiehgt);
                    }

                }
                GUI.skin.label.richText = false;
            }





        } catch (Exception e) {
            log.error("CHAT RENDER ERROR:\n"+ e.ToString());
        }

        GUI.skin.label.wordWrap = ogwordwrap;
        GUI.skin.label.fontSize = ogfontsize;
        GUI.skin.box.fontSize = ogboxfontsize;
        GUI.skin.textField.fontSize = ogtextfieldfontsize;
        UIBullshit.PopFocusedChatFont(pinnedfont, oglabelfont, ogboxfont, ogfieldfont);
    }


    public class ChatMsgContainer {
        public ScavPlayer plr;
        public string name;
        public string tag;
        public string msg;
        public string msg2;
        public string result_nametag;
        public string result_nametag_sanitized;
        public bool rich;
        //public string result;
        //public string result_sanitized;
        public Vector2 namesize;
        public float height;
        public ChatMsgContainer(in ScavPlayer plr, in string tag, string s, in bool rich = false) {
            this.plr = plr;

            this.rich = rich;
            //if (!rich) s = Multiplayer.SanitizeRichText(s);
            
            msg = s;
            this.tag = tag;
            this.name = plr.playerName;
            Compile();
        }
        public ChatMsgContainer(in string name, in string tag, string s, in bool rich = false) {
            this.rich = rich;
            //if (!rich) s = Multiplayer.SanitizeRichText(s);
            msg = s;
            this.tag = tag;
            this.name = name;
            Compile();
        }
        public void Compile() {
            string nnnnnn = "";
            if (plr != null) {
                nnnnnn = TagName($"<color=#{ColorUtility.ToHtmlStringRGB(plr.playerColor)}>{name}</color>", tag, true);
            } else {
                nnnnnn = TagName(name, tag, true);
            }
            result_nametag = $"[{nnnnnn}]: ";
            result_nametag_sanitized = Multiplayer.SanitizeRichText(result_nametag); ;
            //result = $"[{nnnnnn}]: {msg}";
            //result_sanitized = Multiplayer.SanitizeRichText(result);
        }
    }


    internal static MaxCapacityQueue<string> MyMessageLog = new(20);
    internal static int MyMessageLog_Index = 0;

    internal const int MAX_MESSAGES_HISTORY_COUNT = 100;
    private static MaxCapacityQueue<ChatMsgContainer> CHAT_LOG = new(MAX_MESSAGES_HISTORY_COUNT);
    private static string CHAT_current_input = "";
    private static float CHATScrollPosition;
    public static bool CHAT_textbox_input_focused = false;
    public static bool CHATforceinputfocus = false;


    public static void OnLogMessage() {
        focustime = Math.Max(focustime, Time.realtimeSinceStartupAsDouble - 2.1f);
        CHATScrollPosition = 0;
    }
    public static void LogMessage(string plrname, string msg, in bool richtext = false) {
        var msgContainer = new ChatMsgContainer(plrname, null, msg, richtext);
        CHAT_LOG.Enqueue(msgContainer); 
        OnLogMessage();
    }

    public const string NAMELABEL_LOCALSYSTEM = "*SYSTEM*";
    public const string NAMELABEL_SERVER = "*SERVER*";


    static private bool CheckForCommandInMessageInput() {

        var splited = new List<string>(CHAT_current_input.Trim().TrimStart("/").Split(' '));
        if (splited[0] == ("?") || splited[0] == ("help")) {

            LogMessage(NAMELABEL_LOCALSYSTEM, "Main commands:" +
                "\n/[console command] // run a console command" +
                "\n/clear // clear chat history" +
                "\n/team [message] // team message (If teams are enabled)" +
                "\n/whisper [plr] [message] // whisper" +
                $"\n// Use console for console commands. ({KeyBinds.GetBindName("console")})" 
            );

            return true;
        } else {

            var cmdname = splited[0];
            if (cmdname == "clear") {
                CHAT_LOG.Clear();
            } else if(cmdname == "team" || cmdname == "t") {

                var msg = String.Join(" ", splited.Skip(1));
                InputVerifyChatMessage(msg);

                var teammates = new List<ScavPlayer>();
                if (ScavPlayer.LocalPlayer != null) {
                    teammates = ScavPlayer.LocalPlayer.GetTeamPlayers();
                }
                if (ScavPlayer.LocalPlayer == null || !Multiplayer.rules.Teams || (teammates.Count <= 1)) {
                    LogMessage(NAMELABEL_LOCALSYSTEM, $"You don't have a team.");
                } else {
                    Client_SendChatMessage(msg, true);
                }
            } else if (cmdname == "whisper" || cmdname == "w") {

                var msg = String.Join(" ", splited.Skip(2));
                InputVerifyChatMessage(msg);

                if (ScavPlayer.LocalPlayer != null) {
                    var targetplr = Con.GetPlayersForCommand(splited[1], ScavPlayer.LocalPlayer, true, true);
                    if (targetplr != null && targetplr.Count > 0) {
                        Client_SendChatMessage(msg, false, true, targetplr[0].playerId);
                    }
                } else {
                    var targetplr = Con.GetPlayersForCommand(splited[1], ScavPlayer.LocalPlayer, true, true);
                    if (targetplr != null && targetplr.Count > 0) {
                        Server_ChatAnnouncement("SERVER", Lang.MarkMsgAsLocaleKey("plr_chattag_whisper"), msg, new List<knetid> { targetplr[0].playerId });
                    }
                }
            } else if (cmdname == "me") {
                return false;
            } else {
                var cmd = ConsoleScript.SearchExact(cmdname);
                if (cmd != null && false) {
                    LogMessage(NAMELABEL_LOCALSYSTEM, cmdname);
                } else {
                    LogMessage(NAMELABEL_LOCALSYSTEM, $"Unknown command: {cmdname}");
                }
            }


            return true;
        }

        return false;
    }



    public static void OnEnteredUserMessage(string text) {
        CHAT_current_input = text;
        OnEnteredUserMessage();
    }
    static void OnEnteredUserMessage() {
        bool ran_a_command = false;



        if (CHAT_current_input.StartsWith("/")) {
            ran_a_command = CheckForCommandInMessageInput();
        }

        if (!ran_a_command) { 
            SendChatMessage(CHAT_current_input);
        }

        _chatinput_changed = true;

        if (!MyMessageLog.Contains(CHAT_current_input)) {
            MyMessageLog.Enqueue(CHAT_current_input);
        }
        MyMessageLog_Index = MyMessageLog.Count;

        CHAT_current_input = "";
    }



    const ushort MAX_MESSAGE_LENGTH = 150;

    public static bool ValidateChatMessage(in string msg) {
        if (msg.Contains('\r')) {
            return false;
        }
        if (msg.Length > MAX_MESSAGE_LENGTH) {
            return false;
        }
        return true;
    }
    public static bool CharCanBeReplacedWithBlank(in char c) {
        return !(char.IsWhiteSpace(c) || c == '?' || c == '!' || c == '.' || c == '-');
    }

    enum MessageType :byte {
        PlayerMessage,
        ServerAnnouncement,
        ServerAnnouncementCustomName
    }


    public static string TagName(string name, string tag, bool rich = false) {
        if (!String.IsNullOrWhiteSpace(tag)) {
            Lang.MsgTryParse(ref tag);
            if (!rich) {
                tag = Multiplayer.SanitizeRichText(tag);
            }
            name = $"*{tag}* {name}";
        }
        return name;
    }
    public static string TagNameMask(string name, string tag, bool showName = false) {
        if (!String.IsNullOrWhiteSpace(tag)) {
            Lang.MsgTryParse(ref tag);
            if (showName) {
                tag = Multiplayer.SanitizeRichText(tag);
                name = $"<color=#00000000>*{tag}* </color>{name}";
            } else {
                name = $"*{tag}* <color=#00000000>{name}</color>";
            }
        }
        return name;
    }


    [ClientReceiver((byte)NetmsgId.CLIENT_ChatMessage, false)]
    static void Client_ChatMessageReceive(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        
        // Read the data
        reader.Get(out byte msgType______________);
        MessageType msgType = (MessageType)msgType______________;


        switch (msgType) {
            case MessageType.PlayerMessage: {

                reader.Get(out knetid speakerId);
                reader.Get(out string tag);
                reader.Get(out string msg);

                string plrname = speakerId.ToString();

                
                if (ScavPlayer.TryGetPlayerFromClientId(speakerId, out var plr)) {
                    plrname = plr.playerName;

                    if (plr.IsAlive()) {
                        Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(plr.body.talker, msg, true);
                    }
                }

                if (Net.IsClient) {
                    try {
                        OnPlayerChatMessage?.Invoke(plr, msg);
                    } catch (Exception e) {
                        log.error("CLIENT: OnPlayerChatMessage: " + e.ToString());
                    }
                }



                if (Net.IsClient && SHOULD_LOG_CHAT) Plugin.log.LogInfo(log.do_timestamp($"CLIENT: CHAT PLAYER MESSAGE: {plrname}: {msg}"));

                var will_show_this_in_history = true;

                if (
                    will_show_this_in_history
                ) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick,0.95f,0.3f);
                    
                    CHAT_LOG.Enqueue(
                        plr != null ?
                        new ChatMsgContainer(plr, tag, msg, false) :
                        new ChatMsgContainer(plrname, tag, msg, false)
                    );
                    OnLogMessage();
                }

                
                break;
            }
            case MessageType.ServerAnnouncement: {

                reader.Get(out string msg);

                Lang.MsgTryParse(ref msg);

                if (Net.IsClient && SHOULD_LOG_CHAT) Plugin.log.LogInfo($"CLIENT: SERVER ANNOUNCEMENT MESSAGE: {msg}");

                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.86f, 0.9f);
                LogMessage(NAMELABEL_SERVER, msg,true);
                
                break;
            }
            case MessageType.ServerAnnouncementCustomName: {

                reader.Get(out string name);
                reader.Get(out string tag);
                reader.Get(out string msg);


                if (Net.IsClient && SHOULD_LOG_CHAT) Plugin.log.LogInfo($"CLIENT: SERVER NAMED MESSAGE: {name}: {msg}");

                Lang.MsgTryParse(ref msg);

                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.95f, 0.7f);
                LogMessage(TagName(name,tag), msg, true);

                break;
            }
            
            default: {

                //TODO error msg
                break;
            }
        }
        

    }





    [ServerReceiver((byte)NetmsgId.SERVER_PlayerChatMessageSend)]
    static void Server_PlayerChatMessageSend(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out bool only_for_team);
        reader.Get(out bool whisper);
        reader.Get(out knetid whisperTargetPlrId);
        reader.Get(out string message);

        if (String.IsNullOrWhiteSpace(message)) { // they said nothing, do nothing
            return;
        }

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var yapper) ) {
            
            if (yapper.server_mute_tc) {

                var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);
                writer.Put((byte)MessageType.ServerAnnouncement);
                writer.Put("You're muted by the server!");
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, yapper.playerId);

                return;
            }

            if (!ValidateChatMessage(message)) {
                yapper.Server_DoAlertSingle("SERVER: Invalid chat message.",false);
                return;
            }
            if (  SHOULD_LOG_CHAT) Plugin.log.LogInfo($"SERVER: \"{yapper}\" CHAT MESSAGE: {message}");

            try {
                OnPlayerChatMessage?.Invoke(yapper, message);
            } catch (Exception e) {
                log.error("SERVER: OnPlayerChatMessage: " + e.ToString());
            }


            var chatspy = Con._DEV_CHATSPY;
            var forceseelocally = chatspy || Net.IsDedicatedServer;


            var yappertag = "";
            if (yapper.IsDead()) {
                yappertag = Lang.MarkMsgAsLocaleKey("plr_chattag_dead");
            }


            // if spy is enabled, always route unfiltered message to myself
            if (forceseelocally) {
                LogMessage(TagName(yapper.playerName,yappertag), message);
            }

            // make speech impairment effect on the message
            if (yapper.IsAlive() && Multiplayer.rules.SpeechImpairedChat) {
                message = yapper.body.talker.DistortString(message);
            }

            // still visualise the message for myself
            if (forceseelocally) {
                yapper.body?.talker?.ForceNoSpeechImpairment(message, true);
            }

            var targetlisteners = ScavPlayer.ClientIdToPlayerDict.Values.ToList();
            if (only_for_team) {
                targetlisteners = yapper.GetTeamPlayers();
                yappertag = $"<color={yapper.playerColor.ToHex()}><key:plr_chattag_team></color>"; 
            }
            ScavPlayer whisperTargetPlr = null;
            if (whisper) {
                if (!ScavPlayer.TryGetPlayerFromClientId(whisperTargetPlrId, out whisperTargetPlr)) {
                    yapper.Server_DoAlertSingle("SERVER: Invalid chat message. (Whisper player not found?)", false);
                    return;
                }
                targetlisteners = new List<ScavPlayer> { whisperTargetPlr, yapper };
                yappertag = "<key:plr_chattag_whisper>";// Lang.MarkMsgAsLocaleKey("plr_chattag_whisper");
            }
            

            foreach (var plr in targetlisteners) {

                if (chatspy && plr.IsLocal) {
                    continue;
                }

                var individualmessage = message;
                var individualtag = yappertag;

                if (plr == yapper) {
                    if (whisper) {
                        individualtag = $"{individualtag} <key:plr_chat_to> <plr:{whisperTargetPlr.playerId}>";
                    }
                } else {
                    if (Util.IsWorldGenerated()) {

                        if (!yapper.CanCommunicateWith_TextChat(plr)) {
                            continue;
                        }


                        // distort the message even more
                        if (plr.IsAlive()) {
                            plr.netBody.HearinglossDistortMessage(yapper.netBody, ref individualmessage, ref individualtag);
                        }

                        if (String.IsNullOrWhiteSpace(individualmessage)) {
                            continue;
                        }

                    }
                }

                var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);

                writer.Put((byte)MessageType.PlayerMessage);
                writer.Put(clientId);
                writer.Put(individualtag);
                writer.Put(individualmessage);

                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);

                // WRITER END;

            }


        }
    }

    public static bool InputVerifyChatMessage(in string message) {

        if (string.IsNullOrEmpty(message)) {
            return false;
        }

        if (!ValidateChatMessage(message)) {
            LogMessage(NAMELABEL_LOCALSYSTEM, "Message is invalid.");
            return false;
        }

        if (!Net.IsRunning) {
            LogMessage("*OFFLINE*", message);
            return false;
        }
        return true;
    }

    public static void SendChatMessage(in string message, bool force_server_if_server = false) {

        InputVerifyChatMessage(message);

        if (
            Net.IsDedicatedServer || (force_server_if_server && Net.IsServer)
        ) {
            Server_ChatAnnouncement(message);
        } else {
            Client_SendChatMessage(message);
        }
    }

    public static void Client_SendChatMessage(in string message, bool team = false, bool whisper = false, ushort whisperTargetPlrId = 0) {
        var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerChatMessageSend);

        writer.Put(team);
        writer.Put(whisper);
        writer.Put(whisperTargetPlrId);
        writer.Put(message);

        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

        // WRITER END;
    }
    public static void Server_ChatAnnouncement(in string message) {

        if (Net.IsDedicatedServer) {
            LogMessage(NAMELABEL_SERVER, message);
        }
        if (SHOULD_LOG_CHAT) log.l($"SERVER: SENDING CHAT ANNOUNCEMENT: {message.Replace("\r","")}");

        var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);
        writer.Put((byte)MessageType.ServerAnnouncement);
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIds);

        // WRITER END;
    }
    public static void Server_ChatAnnouncement(string name, string tag, string message, IReadOnlyList<knetid> targets = null) {
        if (targets == null) {
            targets = ServerMain.AllClientIds;
        }
        if (Net.IsDedicatedServer) {
            LogMessage(TagName(name,tag), message);
        }
        if (SHOULD_LOG_CHAT) log.l($"SERVER: SENDING NAMED CHAT ANNOUNCEMENT: NAME:\"{name}\" TAG:\"{tag}\": {message}");


        var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);

        writer.Put((byte)MessageType.ServerAnnouncementCustomName);
        writer.Put(name);
        writer.Put(tag);
        writer.Put(message);

        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, targets);

        // WRITER END;
    }
    

}

