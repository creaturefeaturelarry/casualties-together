using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Together;








[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.ResetToDefault))]
static class SettingsMenu_ResetToDefault_MultiplayerPatch {
    static bool Prefix(SettingsMenu __instance) {
        if ((int)Util.GetSettingsPanelOpenedCategory() == SettingsMenu_Start_MultiplayerPatch.multiplayerSettingsMenuTabIndex) {
            foreach (var settingsTab in UIMainMenu.SettingsTab.tabs) {
                if (settingsTab?.settings_origin != null) {
                    foreach (var item in UIMainMenu.GetSettingsFromStaticClass(settingsTab.settings_origin)) {
                        var attttt = item.GetAttribute<SettingDeclarerThingyBaseAttribute>();
                        attttt.Save(item, attttt.default_value);
                    }
                }
                if (settingsTab.on_reset != null)
                    settingsTab.on_reset();

                SettingsMenu_Start_MultiplayerPatch.OnMultiplayerTabClick();
            }
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.Start))]
public static class SettingsMenu_Start_MultiplayerPatch {
    public static List<GameObject> spawnedSettings => SettingsMenu.instance.spawnedSettings;
    public static RectTransform content => SettingsMenu.instance.content;

    internal static int multiplayerSettingsMenuTabIndex = 100;
    static Button multiplayerTabButton = null;
    static RectTransform VCVizRectTransform = null;
    internal static Delegate bugreportFunctionCreator = null;
    internal static GameObject DeactivateButton = null;
    public static GameObject UpdateButton = null;
    public static TextMeshProUGUI UpdateStatusText = null;

    static void Postfix(SettingsMenu __instance) {
        // rebuild

        { // Add buttons
            var sm = __instance;
            var template = sm.buttons.LastOrDefault();
            if (template == null) return;
            var rt = template.transform as RectTransform;
            var lastPos = rt.anchoredPosition;
            var sizereduce = (
                rt.sizeDelta.x
                + 3 // Little gap width
                ) / (sm.buttons.Count() + 1);
            var asV2 = new Vector2(sizereduce, 0);
            for (int i = 0; i < sm.buttons.Count(); i++) {
                var button = sm.buttons[i];
                var brt = button.transform as RectTransform;
                brt.sizeDelta -= asV2;
                brt.anchoredPosition -= asV2 * (i + 0.5f);
            }

            var inst = GameObject.Instantiate(template, template.transform.parent, false);
            inst.name = $"{MyPluginInfo.PLUGIN_GUID}_SettingsTab";
            multiplayerTabButton = inst;

            var newbutton = inst.GetComponent<Button>();
            var buttonrect = newbutton.transform as RectTransform;
            sm.buttons.Add(inst);
            var index = sm.buttons.Count();
            multiplayerSettingsMenuTabIndex = index;
            newbutton.onClick.RemoveAllListeners();

            buttonrect.anchoredPosition = lastPos + asV2 * 0.5f;

            var label = newbutton.GetComponentInChildren<TextMeshProUGUI>(true);
            var uilocalizer = newbutton.GetComponentInChildren<UILocalizer>(true);
            if (label != null) {
                label.text = Lang.Get("multiplayer");
            }
            if (uilocalizer != null) {
                UILocalizer.Destroy(uilocalizer);
            }

            newbutton.onClick.AddListener(OnMultiplayerTabClick);
        }
    }

    public static void OnMultiplayerTabClick() {
        SettingsMenu.instance.SelectTab(multiplayerSettingsMenuTabIndex);

        BuildMPSettingsUI();

        if (multiplayerTabButton != null)
            multiplayerTabButton.GetComponent<Image>().sprite = SettingsMenu.instance.buttonOpen;
    }
    public static float contentHeight = 0;

    static void BuildMPSettingsUI() {

        contentHeight = 0;
        ref float num = ref contentHeight;
        {

            var handler = new GameObject("MpSetHandler", typeof(MultiplayerSettingsHandler));
            handler.transform.SetParent(content.transform);
            spawnedSettings.Add(handler);

            const string gap = "     ";

            foreach (var settingsTab in UIMainMenu.SettingsTab.tabs) {
                var fields = UIMainMenu.GetSettingsFromStaticClass(settingsTab.settings_origin);

                // Big label with  settingsTab.name
                {
                    CreateSettingButton(gap + Lang.Get(settingsTab.name), ref num, out var g, out var button, out var text);
                    if (text != null) {
                        text.fontSize += 10;
                        text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                        text.color = Color.cyan;
                        button.GetComponent<Image>().color = Color.cyan;
                    }
                    if (g != null) {
                        g.name = settingsTab.name;
                    }
                    if (button != null) {
                        Button.Destroy(button);
                    }
                }

                CreateSettingFields(fields, ref num);

                //Additional buttons
                {
                    if (settingsTab == UIMainMenu._chat_setting_tab) {
                        Chat.ShowChat();

                        CreateKeybindSetting(CoopKeybinds.chat, "mmst_chatkeybind", ref num);

                    } else if (settingsTab == UIMainMenu._voip_setting_tab) {

                        { // Mic selector
                            var curMic = Voicechat.GetMic();
                            var val = Array.IndexOf(Microphone.devices, curMic);
                            if (val == -1) {
                                val = 0;
                            }

                            var g = Utils.Create("Special/GameSettingDropdown", content);
                            List<TMP_Dropdown.OptionData> list = new List<TMP_Dropdown.OptionData>();
                            if (Microphone.devices.Length == 0) {
                                list.Add(new TMP_Dropdown.OptionData(Lang.Get("mmst_mike_osdefault")));
                            } else {
                                foreach (string text in Microphone.devices) {
                                    list.Add(new TMP_Dropdown.OptionData(text));
                                }
                            }
                            var dropDown = g.transform.GetChild(1).GetComponent<TMP_Dropdown>();
                            dropDown.AddOptions(list);
                            dropDown.SetValueWithoutNotify(val);
                            dropDown.onValueChanged.AddListener(delegate (int i) {
                                var mike = Microphone.devices.ElementAtOrDefault(i);
                                if (mike != null) {
                                    Voicechat.SetMicrophone(mike);
                                }
                            });
                            var dropDownRect = dropDown.GetComponent<RectTransform>();
                            dropDownRect.offsetMin = new Vector2(0, dropDownRect.offsetMin.y);
                            dropDownRect.anchorMin = new Vector2(0.5f, dropDownRect.anchorMin.y);
                            SetupSetting(g, Lang.Get("mmst_mike"), null, ref num);

                            var scrollRect = dropDownRect.GetComponentInChildren<ScrollRect>(true);
                            if (scrollRect != null) {
                                scrollRect.vertical = true;
                                if (scrollRect.scrollSensitivity == 1) {
                                    scrollRect.scrollSensitivity += 20;
                                }
                                if (scrollRect.viewport != null) {
                                    scrollRect.viewport.sizeDelta += new Vector2(0, 400);
                                }
                            }
                        }

                        CreateKeybindSetting(CoopKeybinds.voicechat, "mmst_vckeybind", ref num);

                        { // Loopback button
                            CreateSettingButton(gap + Lang.Get("mmst_mike_test"), ref num, out var g, out var button, out var text);
                            RawImage rawImage = null;
                            if (text != null) {
                                text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                            }
                            if (button != null) {
                                button.onClick.AddListener(() => {
                                    Voicechat.ENABLE_LOCAL_LOOPBACK = !Voicechat.ENABLE_LOCAL_LOOPBACK;
                                    text.text = gap + (Voicechat.ENABLE_LOCAL_LOOPBACK ? Lang.Get("mmst_mike_testing") : Lang.Get("mmst_mike_test"));
                                    if (rawImage != null) {
                                        rawImage.gameObject.SetActive(Voicechat.ENABLE_LOCAL_LOOPBACK);
                                    }
                                    CasualtiesTogetherUtils.Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                                });
                            }
                            g.name = "mmst_mike_test";

                            {
                                UIMainMenu.ResetSettingsVOIPVisualiser();

                                var go = new GameObject("hello vro");
                                VCVizRectTransform = go.AddComponent<RectTransform>();
                                VCVizRectTransform.SetParent(g.transform);
                                VCVizRectTransform.SetStretch();
                                VCVizRectTransform.anchorMin = new Vector2(0.5f, 0);
                                rawImage = go.AddComponent<RawImage>();
                                rawImage.gameObject.SetActive(Voicechat.ENABLE_LOCAL_LOOPBACK);
                                UIBullshit.main.egg = go.AddComponent<ECGVisualizer>();
                                Vector2 pixelSize = RectTransformUtility.PixelAdjustRect(VCVizRectTransform, VCVizRectTransform.GetComponentInParent<Canvas>()).size;
                                UIBullshit.main.egg.textureSize = (pixelSize * 0.5f).ToInt();
                                UIBullshit.main.egg.followBody = false;
                            }
                        }
                    } else if (settingsTab == UIMainMenu._general_setting_tab) {

                        {
                            CreateSettingButton(Lang.Get("mmst_button_deactivate_mp"), ref num, out DeactivateButton, out var button, out var text);

                            button.onClick.AddListener(() => {
                                PlayerPrefsExtended.SetBool(Plugin.FORCEDISABLE_PLAYERPREF_KEY, true);
                                PlayerPrefs.Save();

                                Plugin.RestartGame();
                            });

                            var rect = DeactivateButton.GetComponent<RectTransform>();
                            rect.offsetMin += Vector2.one * 10f;
                            rect.offsetMax -= Vector2.one * 10f;
                        }

                        if (UIMainMenu.BuildUpdateButton != null) {
                            UIMainMenu.BuildUpdateButton();
                        }
                    } else if (settingsTab == UIMainMenu._bugreport_setting_tab && bugreportFunctionCreator != null) {

                        bugreportFunctionCreator.DynamicInvoke();


                    } else if (settingsTab == UIMainMenu._debug_setting_tab) { }
                }

                num += 60;
            }

        }
        SettingsMenu.instance.content.sizeDelta = new Vector2(SettingsMenu.instance.content.sizeDelta.x, num);

    }
    static void CreateKeybindSetting(in string setting, in string text, ref float num) {
        var keybind = Settings.Get<SettingKeybind>(setting);
        var g = Utils.Create("Special/GameSettingInput", content);
        Button button = g.transform.GetChild(1).GetComponent<Button>();
        TextMeshProUGUI txt = button.GetComponentInChildren<TextMeshProUGUI>();
        txt.text = keybind.GetDisplayString();
        button.onClick.AddListener(delegate {
            button.StartCoroutine(SettingsMenu.instance.WaitForKeyPress(keybind, txt));
            CasualtiesTogetherUtils.Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        });
        SetupSetting(g, Lang.Get(text), null, ref num);
    }
    public static void CreateSettingButton(in string str, ref float num, out GameObject g, out Button button, out TextMeshProUGUI text) {
        g = Utils.Create("Special/GameSettingLanguage", content);
        if (g.TryGetComponent(out button)) {
            button.onClick.RemoveAllListeners();
        }
        text = g.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
        SetupSetting(g, str, null, ref num);
    }
    static void CreateSettingFields(List<FieldInfo> fields, ref float num) {

        //OG: SettingsMenu.SelectTab V7.0.1
        foreach (var f in fields) {

            var attr = f.GetAttribute<SettingDeclarerThingyBaseAttribute>();
            if (attr == null) {
                continue;
            }

            var langkey = attr.n;
            if (langkey == null) {
                langkey = "setting_" + f.DeclaringType + "_" + f.Name;
            }
            var desckey = langkey + "_desc";

            var setName = Lang.Get(langkey);
            bool hasDesc = Lang.TryGet(desckey, out var setDescription);


            GameObject g = null;
            if (attr is SettingDeclarerThingyFloatAttribute setfloat) {
                var val = (float)f.GetValue(null);

                g = Utils.Create("Special/GameSettingFloat", content);
                Slider component = g.transform.GetChild(1).GetComponent<Slider>();
                component.minValue = setfloat.mi;
                component.maxValue = setfloat.ma;
                component.SetValueWithoutNotify(val);
                g.transform.GetChild(2).GetComponent<TextMeshProUGUI>().text = setfloat.GetDisplayString(f);
                component.onValueChanged.AddListener(delegate (float x) {
                    setfloat.Save(f, x);
                    g.transform.GetChild(2).GetComponent<TextMeshProUGUI>().text = setfloat.GetDisplayString(f);
                });
            } else if (attr is SettingDeclarerThingyChoiceAttribute c_attr) {

                var val = f.GetValue(null).ConvertTo<int>();

                var translated_choices = new List<string>(c_attr.choices).Select(x => Lang.Get(x));

                g = Utils.Create("Special/GameSettingDropdown", content);
                List<TMP_Dropdown.OptionData> list = new List<TMP_Dropdown.OptionData>();
                foreach (string text in translated_choices) {
                    list.Add(new TMP_Dropdown.OptionData(text));
                }
                g.transform.GetChild(1).GetComponent<TMP_Dropdown>().AddOptions(list);
                g.transform.GetChild(1).GetComponent<TMP_Dropdown>().SetValueWithoutNotify(val);
                g.transform.GetChild(1).GetComponent<TMP_Dropdown>().onValueChanged.AddListener(delegate (int x) {
                    c_attr.Save(f, x);
                });
            } else if (attr is SettingDeclarerThingyBoolAttribute b_attr) {
                var val = (bool)f.GetValue(null);

                g = Utils.Create("Special/GameSettingBool", content);
                g.transform.GetChild(1).GetComponent<Toggle>().SetIsOnWithoutNotify(val);
                g.transform.GetChild(1).GetComponent<Toggle>().onValueChanged.AddListener(delegate (bool x) {
                    b_attr.Save(f, x);
                });
            } else {
                log.warn($"Unknown setting: {langkey}");
                continue;
            }
            g.name = langkey;
            SetupSetting(g, setName, setDescription, ref num);
        }

    }
    public static void SetSettingNameAndTooltip(GameObject g, in string setName, in string setDesc) {
        g.transform.GetChild(0).GetComponent<TextMeshProUGUI>().text = setName;
        UITooltip uITooltip = g.transform.GetChild(0).gameObject.GetOrAddComponent<UITooltip>();
        uITooltip.skipLocale = true;
        uITooltip.tipName = setName;
        if (setDesc != null) {
            uITooltip.tipDesc = setDesc;
        }
    }
    public static void SetupSetting(GameObject g, in string setName, in string setDesc, ref float num) {
        SetSettingNameAndTooltip(g, setName, setDesc);
        g.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, 0f - num - g.GetComponent<RectTransform>().sizeDelta.y * 0.5f);
        num += g.GetComponent<RectTransform>().sizeDelta.y;
        spawnedSettings.Add(g);
    }
}

class MultiplayerSettingsHandler : MonoBehaviour {
    void Update() {
        if (UIBullshit.main.egg != null && UIBullshit.main.egg.tex != null && UIBullshit.main.egg.gameObject.activeInHierarchy) {
            if (UIBullshit.main.egg.writeX != UIMainMenu.last_writeX) {
                UIMainMenu.last_writeX = UIBullshit.main.egg.writeX;
                UIBullshit.main.egg.writeHeight = 0;
                UIMainMenu.writeX_flipflop = !UIMainMenu.writeX_flipflop;
            }
        }
        if (SettingsMenu_Start_MultiplayerPatch.DeactivateButton != null && SettingsMenu_Start_MultiplayerPatch.DeactivateButton.activeInHierarchy) {
            var activatethisbutton = !Net.IsRunning && !Util.IsInWorld();

            var button = SettingsMenu_Start_MultiplayerPatch.DeactivateButton.GetComponent<Button>();
            if (button != null) {
                button.interactable = activatethisbutton;
            }

            var uiTooltip = SettingsMenu_Start_MultiplayerPatch.DeactivateButton.GetComponentInChildren<UITooltip>();

            if (uiTooltip != null) {
                uiTooltip.tipDesc = (activatethisbutton ? "mmst_button_deactivate_mp_desc" : "mmst_button_deactivate_mp_desc_disabled");
            }
        }

    }
}









public class NewNewMultiplayerUI : CasualtiesTogetherSingleton {
    public static NewNewMultiplayerUI main { get; private set; }


    void Awake() {
        if (main != null) {
            throw new Exception("man wtf is this bro");
        }
        main = this;
    }


    void UIManager_OnMainMenuLoad() {

#if !RELEASE
        {
            // Canvas/MenuBackground/Experiment
            try {
                var exp = PreRunScript.instance.transform.Find("MenuBackground/Experiment");
                if (exp != null) {

                    var img = exp.GetComponent<Image>();
                    img.color = img.color.WithAlpha(0);

                } else {
                    log.error("exp not found");
                }
            } catch (System.Exception e) {
                log.error("AAAAA " + e.ToString());
            }
        }
#endif

        if (UIManager.modCanvasObj != null && mainUI == null) {
            CreateMainUI();
        }
    }





    static GameObject mainUI;
    public static void CreateMainUI() {
        if (mainUI == null) {


            CreateTestUI();

            BuildPauseTextUI();

        }
    }
    static void BuildPauseTextUI() {
        if (UIManager.modPauseTextObj == null) {

            UIManager.CreateLabelWithBackground(UIManager.modCanvasObj.transform, "server_pause", null, null, out UIManager.modPauseTextObj, out var text, out var mainRT, out var image);
            mainRT.pivot = Vector2.one * 0.5f;
            mainRT.anchorMin = new Vector2(-0.1f, -0.1f);
            mainRT.anchorMax = new Vector2(1.1f, 1.1f);
            text.raycastTarget = false;
            image.color = Color.black * 0.6f;

        }
    }

    static GameObject testUI;
    static void CreateTestUI() {
#if !RELEASE && false
        if (testUI == null) {
            {
                CreateBlock(modCanvas, "yes", out testUI, out var mainRT, out var mainImage);
                mainRT.anchoredPosition += Vector2.left * 0 + Vector2.up * 500;
                mainImage.raycastTarget = false;
            }
            {
                CreateBlock(modCanvas, "aaaaaa", out var aaaa, out var mainRT, out var mainImage);
                mainImage.sprite = uiBlockSmall;
                mainRT.anchoredPosition += Vector2.left * 100 + Vector2.up * 500;
                mainImage.raycastTarget = false;
            }
            {
                CreateButton(modCanvas, "discord_joinrequest", "no", "sleep_is_disabled", out var go, out var text, out var rt, out var button);
                rt.anchoredPosition += Vector2.left * 200 + Vector2.up * 500;
            }
            {
                CreateLabelWithBackground(modCanvas, "mmsb_refresh", "yes", "no", out var go, out var text, out var mainRT, out var image);
                mainRT.anchoredPosition += Vector2.left * 300 + Vector2.up * 500;
                image.raycastTarget = false;
                text.raycastTarget = false;
            }
        }
#endif
    }




}





