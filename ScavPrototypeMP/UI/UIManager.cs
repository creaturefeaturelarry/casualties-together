


using HarmonyLib;
using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using System.Diagnostics;



namespace Together;



[HarmonyPatch(typeof(AdaptiveButton), nameof(AdaptiveButton.overlayActive), MethodType.Getter)]
public static class AdaptiveButton_overlayActive_MultiplayerPatch {

#if !RELEASE 
    static bool _getter_working_announced = false;
    static void Prefix() {
        if (!_getter_working_announced) {
            _getter_working_announced = true;
            Plugin.log.LogInfo($"MethodType.Getter  is working: {new StackTrace()}");
        }
    }
#endif 


    public static void Postfix(AdaptiveButton __instance, ref bool __result) {
        if (Con.IsConsoleOpen()) {
            __result = true;
        }
        if (UIMainMenu.IsOpen()) {
            __result = true;
        }
    }
}




public static class UIUtility {

    public static void SetAnchors(this RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2? pivot = null) {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        if (pivot.HasValue) rt.pivot = pivot.Value;
    }

    public static void SetStretch(this RectTransform rt, float left = 0, float right = 0, float top = 0, float bottom = 0) {
        rt.anchorMin = new Vector2(0, 0);
        rt.anchorMax = new Vector2(1, 1);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    public static void SetSizeAndPos(this RectTransform rt, Vector2 sizeDelta, Vector2 anchoredPosition) {
        rt.sizeDelta = sizeDelta;
        rt.anchoredPosition = anchoredPosition;
    }
}



public class UIManager : CasualtiesTogetherSingleton {
    static UIManager main;
    public UIManager() { main = this; }

    static string[] sprites_to_find = new string[] {
        "uiBlock", // exists
        "uiBlockMini",
        "uiBlockNano", // exists
        "uiBlockSmall",//"uiBlockSmall", // exists
        "UICheckMark",
        "uicursor",
        "uicursorpressed",
        "UIElement8px",
        "UIFoldoutClosed",
        "UIFoldoutOpened",
        "uigradientblack",
        "uiPoint",
        "userbarfill",
        "whitesquare",
    };

    public static Dictionary<string, Sprite> ui_sprites = new();
    public static Sprite uiBlockSmall;
    public static Sprite uiBlockNano;


    public static GameObject template_button;
    public static GameObject template_label;

    public static GameObject runSettingsMenu;

    public static GameObject MP_MENU_BUTTON;
    public static GameObject MP_LINK_BUTTON;

    public static GameObject modPauseTextObj;
    public static GameObject modCanvasObj;
    public static Canvas SeparateCanvas;
    public static Vector2 ReferenceResolution = new Vector2(1920,1080);

    public static PreRunScript PreRunScript => PreRunScript.instance;
    public static Canvas GameCanvas;



    public static VerticalLayoutGroup AddVerticalLayout(in GameObject go, int spacing = 4, RectOffset padding = null, bool controlWidth = true, bool controlHeight = false) {
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = spacing;
        vlg.padding = padding ?? new RectOffset(4, 4, 4, 4);
        vlg.childControlWidth = controlWidth;
        vlg.childControlHeight = controlHeight;
        vlg.childForceExpandWidth = controlWidth;
        vlg.childForceExpandHeight = false;
        return vlg;
    }

    public static HorizontalLayoutGroup AddHorizontalLayout(in GameObject go, int spacing = 4, RectOffset padding = null, bool controlWidth = false, bool controlHeight = true) {
        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = spacing;
        hlg.padding = padding ?? new RectOffset(4, 4, 4, 4);
        hlg.childControlWidth = controlWidth;
        hlg.childControlHeight = controlHeight;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = controlHeight;
        return hlg;
    }

    public static LayoutElement SetPreferredSize(in GameObject go, float width = -1, float height = -1) {
        var le = go.GetOrAddComponent<LayoutElement>();
        if (width >= 0) le.preferredWidth = width;
        if (height >= 0) le.preferredHeight = height;
        return le;
    }


    public static void CreateBlock(in Component parent, in string name, out GameObject go, out RectTransform rt, out Image image) {
        go = new GameObject("UI_BLOCK_" + name);
        if (parent != null) {
            go.transform.SetParent(parent.transform, false);
        }
        go.layer = template_button.layer;

        rt = go.AddComponent<RectTransform>();
        image = go.AddComponent<Image>();
        image.type = Image.Type.Sliced;

        image.sprite = uiBlockNano;
    }
    public static GameObject CreateBlock(in Component parent, in string name, Vector2 size, float alpha = 1f) {
        CreateBlock(parent, name, out var go, out var rt, out var image);
        rt.sizeDelta = size;
        image.color = image.color.WithAlpha(alpha);
        return go;
    }

    public static void CreateButton(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject button_go, out TextMeshProUGUI text, out RectTransform rt, out Button button) => CreateButton(parent, label, tooltip, tooltip_desc, out  button_go, out text, out rt, out button, out var _);
    public static void CreateButton(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject button_go, out TextMeshProUGUI text, out RectTransform rt, out Button button, out Image image) {
        button_go = GameObject.Instantiate(template_button.gameObject, parent != null ? parent.transform : null, false);
        button_go.SetActive(true);
        button_go.name = "UI_BUTTON_" + label;

        text = button_go.GetComponentInChildren<TextMeshProUGUI>();
        rt = button_go.GetComponent<RectTransform>();
        button = button_go.GetComponent<UnityEngine.UI.Button>();
        image = button_go.GetComponent<UnityEngine.UI.Image>();

        SetUITooltip(text, label, tooltip, tooltip_desc);
    }
    public static Button CreateButton(in Component parent, in string label, Action onClick, in string tooltip = null, in string tooltip_desc = null) {
        CreateButton(parent, label, tooltip, tooltip_desc, out _, out _, out _, out var button);
        if (onClick != null) button.onClick.AddListener(() => onClick());
        return button;
    }

    public static void CreateLabelWithBackground(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject go, out TextMeshProUGUI text, out RectTransform rt) => CreateLabelWithBackground(parent, label, tooltip, tooltip_desc, out  go, out text, out rt, out var _);
    public static void CreateLabelWithBackground(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject go, out TextMeshProUGUI text, out RectTransform rt, out Image image) {
        go = GameObject.Instantiate(template_button.gameObject, parent != null ? parent.transform : null, false);
        go.SetActive(true);
        go.name = "UI_LABELBG_" + label;

        text = go.GetComponentInChildren<TextMeshProUGUI>();
        rt = go.GetComponent<RectTransform>();
        image = go.GetComponent<UnityEngine.UI.Image>();
        var button = go.GetComponent<UnityEngine.UI.Button>();
        GameObject.Destroy(button);

        SetUITooltip(text, label, tooltip, tooltip_desc);
    }
    public static void CreateLabel(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject go, out TextMeshProUGUI text, out RectTransform rt) {
        go = GameObject.Instantiate(template_label.gameObject, parent != null ? parent.transform : null, false);
        go.SetActive(true);
        go.name = "UI_LABEL_" + label;

        text = go.GetComponent<TextMeshProUGUI>();
        rt = go.GetComponent<RectTransform>();

        SetUITooltip(text, label, tooltip, tooltip_desc);
    }
    public static TextMeshProUGUI CreateLabel(in Component parent, in string label, in string tooltip = null, in string tooltip_desc = null) {
        CreateLabel(parent, label, tooltip, tooltip_desc, out _, out var text, out _);
        return text;
    }

    public static void SetUITooltip(in TextMeshProUGUI text, in string label, in string tooltip, in string tooltip_desc, bool translate = true) {
        text.text = translate ? Lang.GetNoError(label) : label;

        if (string.IsNullOrEmpty(tooltip) && string.IsNullOrEmpty(tooltip_desc)) {
            if (text.TryGetComponent(out UITooltip aaa)) {
                UITooltip.Destroy(aaa);
            }
        } else {
            var tt = text.GetOrAddComponent<UITooltip>();
            tt.skipLocale = true;
            if (string.IsNullOrEmpty(tooltip)) {
                tt.tipName = "";
                tt.localeName = "";
            } else {
                tt.tipName = translate ? Lang.GetNoError(tooltip) : tooltip;
                tt.localeName = Lang.LocalPrefixTag + tooltip;
            }
            if (string.IsNullOrEmpty(tooltip_desc)) {
                tt.tipDesc = "";
                tt.localeDesc = "";
            } else {
                tt.tipDesc = translate ? Lang.GetNoError(tooltip_desc) : tooltip_desc;
                tt.localeDesc = Lang.LocalPrefixTag + tooltip_desc;
            }
        }
        if (text.TryGetComponent(out UILocalizer uil)) {
            Component.Destroy(uil);
        }
    }


    public static RectTransform CreateRect(in Component parent, in string name = "rect") {
        var go = new GameObject(name, typeof(RectTransform));

        go.transform.SetParent(parent.transform, false);
        return go.GetComponent<RectTransform>();
    }








    public static void ScrapeAssets() {
        // find assets
        try {
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            foreach (var spritename in sprites_to_find) {
                var spr = sprites.FirstOrDefault((Sprite f) => f.name == spritename);
                if (spr == null) {
                    spr = sprites.FirstOrDefault((Sprite f) => f.name.Contains(spritename));

                    if (spr == null) {
                        if (log.verbose) Plugin.Logger.LogWarning($"\"{spritename}\" wasnt found !!!!!!!!!!!!");
                        continue;
                    } else {
                        if (log.verbose) Plugin.Logger.LogWarning($"\"{spritename}\" was found through Contains: \"{spr.name}\"");
                    }
                } else {
                    if (log.verbose) Plugin.Logger.LogInfo($"SPRITE SEARCH: \"{spritename}\" was found.");
                }
                ui_sprites[spritename] = spr;
            }
        } catch (System.Exception e) {
            log.error("ASSET SEARCH 1 " + e.ToString());
        }

        if (ui_sprites.TryGetValue("uiBlockSmall", out var uiBlockSmall_sprite) || ui_sprites.TryGetValue("uiBlock", out uiBlockSmall_sprite)) {
            uiBlockSmall = uiBlockSmall_sprite;
        }
        if (ui_sprites.TryGetValue("uiBlockNano", out var uiBlockNano_sprite)) {
            uiBlockNano = uiBlockNano_sprite;
        }

        GetPrerunScriptAndCanvas();
        if (PreRunScript != null) {
            var menubg = PreRunScript.transform.Find("RunSettings");
            if (menubg != null) {
                runSettingsMenu = menubg.gameObject;
            }

            if (template_button == null) {
                try {
                    var cancel = PreRunScript.transform.Find("RunSettings/Presets/Cancel");
                    if (cancel != null) {
                        template_button = GameObject.Instantiate(cancel.gameObject, modCanvasObj.transform, false);
                        template_button.name = "MAIN_BUTTON";
                        var button = template_button.GetComponent<Button>();
                        var text = button.GetComponentInChildren<TMP_Text>(true);
                        text.color = Color.white;
                        template_label = text.gameObject;
                        button.onClick.RemoveAllListeners();
                        button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                        var rt = button.GetComponent<RectTransform>();
                        rt.anchoredPosition = new Vector2(0f, 0f);
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.one;
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                        template_button.SetActive(false);
                    }
                } catch (System.Exception e) {
                    log.error("RUNSETTINGS SEARCH 1 " + e.ToString());
                }
            }
        }
        
    }

    public static void GetPrerunScriptAndCanvas() {
        if (PreRunScript != null) { // PrerunScript is on the main menu canvas
            GameCanvas = PreRunScript.GetComponent<Canvas>();
        }
    }
    static void CreateModCanvas() {
        if (modCanvasObj == null) {
            modCanvasObj = new GameObject("CasualtiesTogetherUICanvas");
            modCanvasObj.hideFlags = HideFlags.HideAndDontSave;
            modCanvasObj.layer = GameCanvas.gameObject.layer;

            SeparateCanvas = modCanvasObj.GetOrAddComponent<Canvas>();
            modCanvasObj.GetOrAddComponent<GraphicRaycaster>();
            var rt = modCanvasObj.GetOrAddComponent<RectTransform>();
            var scaler = modCanvasObj.GetOrAddComponent<CanvasScaler>();
            {
                var ogScaler = GameCanvas.GetComponent<CanvasScaler>();
                scaler.referenceResolution = ogScaler.referenceResolution;
                ReferenceResolution = ogScaler.referenceResolution;
                scaler.fallbackScreenDPI = ogScaler.fallbackScreenDPI;
                scaler.dynamicPixelsPerUnit = ogScaler.dynamicPixelsPerUnit;
                scaler.referencePixelsPerUnit = ogScaler.referencePixelsPerUnit;
                scaler.physicalUnit = ogScaler.physicalUnit;
                scaler.scaleFactor = ogScaler.scaleFactor;
                scaler.screenMatchMode = ogScaler.screenMatchMode;
                scaler.uiScaleMode = ogScaler.uiScaleMode;
            }
            {
                var ogRt = GameCanvas.GetComponent<RectTransform>();
                rt.anchoredPosition = ogRt.anchoredPosition;
                rt.anchorMax = ogRt.anchorMax;
                rt.anchorMin = ogRt.anchorMin;
                rt.offsetMin = ogRt.offsetMin;
                rt.offsetMax = ogRt.offsetMax;
                rt.localScale = ogRt.localScale;
            }
            {
                SeparateCanvas.sortingOrder = GameCanvas.sortingOrder + 10;
                SeparateCanvas.renderMode = GameCanvas.renderMode;
                SeparateCanvas.scaleFactor = GameCanvas.scaleFactor;
                SeparateCanvas.additionalShaderChannels = GameCanvas.additionalShaderChannels;
            }

            modCanvasObj.transform.SetSiblingIndex(Mathf.Max(0, GameCanvas.transform.GetSiblingIndex()-2));

            GameObject.DontDestroyOnLoad(modCanvasObj);
        }
    }

    static bool CreateLinkButton = false;

    public static void OnMainMenuLoad() {

        GetPrerunScriptAndCanvas();

        if (GameCanvas == null) {
            return;
        }

        CreateModCanvas();

        ScrapeAssets();

        if (CreateLinkButton) {
            if (MP_LINK_BUTTON == null) {
                // create mp button
                try {
                    var credits = PreRunScript.transform.Find("Credits");
                    var trans = PreRunScript.transform.Find("Credits/TranslationRepo");
                    if (trans != null) {
                        MP_LINK_BUTTON = GameObject.Instantiate(trans.gameObject, PreRunScript.transform, false).gameObject;
                        MP_LINK_BUTTON.name = "MAIN_BUTTON_LINK";

                        var rt = MP_LINK_BUTTON.GetComponent<RectTransform>();
                        rt.anchoredPosition = new Vector2(50, rt.anchoredPosition.y);
                        rt.SetSiblingIndex(credits.GetSiblingIndex());

                        var tt = MP_LINK_BUTTON.GetComponent<UITooltip>();
                        tt.localeName = Lang.LocalPrefixTag + "mainmenu_mp_button_tooltip";
                        tt.skipLocale = true;

                        tt.tipName = Lang.Get("mainmenu_mp_button_tooltip");

                        var img = MP_LINK_BUTTON.GetComponent<Image>();
                        img.sprite = CoopModAssets.mpmod_icon;
                        img.color = Color.green;

                        var button = MP_LINK_BUTTON.GetComponent<UnityEngine.UI.Button>();
                        button.onClick.RemoveAllListeners();
                        button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                        button.onClick.AddListener(() => {
                            if (UIBullshit.main == null || (UIMainMenu.IsOpen() && UIMainMenu.current_tab == UIMainMenu.menutab_about) || Con.IsConsoleOpen()) {
                                PreRunScript.OpenURL(Constants.OG_DOWNLOAD_URL);
                            } else {
                                UIMainMenu.SetOpen(true);
                                UIMainMenu.current_tab = UIMainMenu.menutab_about;
                            }
                        });
                    } else {
                        log.error("BUTTON SEARCH MAIN_BUTTON_LINK not found");
                    }

                } catch (System.Exception e) {
                    log.error("BUTTON SEARCH 1 " + e.ToString());
                }
            }
        }
        
        if (MP_MENU_BUTTON == null) {
            try {
                if (PreRunScript != null) {
                    var credits = PreRunScript.transform.Find("Credits");

                    var rt = PreRunScript.GetComponent<RectTransform>();
                    

                    if (template_button != null) {
                        
                        CreateButton(PreRunScript, "mainmenu_mainbutton", null, null, out MP_MENU_BUTTON, out var ttttt, out var rt2, out var button2, out var mpButtonBg);
                        
#if !RELEASE 
                        ttttt.fontMaterial = new Material(ttttt.fontMaterial);
                        ttttt.fontMaterial.name = "CHANGED TO FANCY MAT !!!";
                        ttttt.fontMaterial.shader = CoopModAssets.coolshader;
#endif       

#if RELEASE
                        if (credits != null) {
                            rt2.SetSiblingIndex(credits.GetSiblingIndex());
                        } else
#endif
                        {
                            rt2.SetSiblingIndex(rt.childCount); // move it to the front so i can click it even before the lore dump finishes
                        }
                        rt2.pivot = new Vector2(0.5f, 1f);
                        var wdth = 0.27f * 0.5f;
                        rt2.anchorMin = new Vector2(0.5f - wdth, 0.005f);
                        rt2.anchorMax = new Vector2(0.5f + wdth, 0.09f);
                        rt2.offsetMin = Vector2.zero;
                        rt2.offsetMax = Vector2.zero;

                        button2.onClick.AddListener(() => {
#if !RELEASE 
                            log.l("OPENING THE MENU");
#endif 
                            UIMainMenu.SetOpen(!UIMainMenu.mainmenu_open);
                            if (runSettingsMenu != null) {
                                runSettingsMenu.SetActive(false);
                            }
                        });

                        mpButtonBg.raycastPadding = Vector2.one * 4f;
                        mpButtonBg.sprite = uiBlockSmall;
                        
                    }
                }

            } catch (System.Exception e) {
                log.error("CREATING MENU BUTTONS " + e.ToString());
            }
        }


        main.SendMessage("UIManager_OnMainMenuLoad",SendMessageOptions.DontRequireReceiver);

    }




    public static bool IsServerGameplayPaused => (!Util.IsInPauseMenuOrMainMenu() && (Mathf.Abs(Time.timeScale) < 0.00501f));
    void Update() {

        if (modPauseTextObj != null) {
            var shouldDoPause = IsServerGameplayPaused;
            modPauseTextObj.SetActive(shouldDoPause);
        }

    }

}








