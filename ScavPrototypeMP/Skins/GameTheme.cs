using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// colors and font come from the game settings menu. a miss uses the fallback.
internal static class GameTheme {
    private static bool _resolved;
    private static TMP_FontAsset _font;
    private static Sprite _panelSprite;
    private static Sprite _buttonSprite;
    private static Vector2 _referenceResolution = new Vector2(1920f, 1080f);
    private static float _matchWidthOrHeight = 0.5f;

    // a font missing any of these glyphs gets rejected, or the ui shows empty boxes.
    private const string UiCharacters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:;-_/\\()[]!?'\"+*&%#@=<>|~^`{}";

    private static Color _panel = Hex("#0E0E0E", 0.97f);
    private static Color _header = Hex("#151515", 1f);
    private static Color _card = Hex("#121212", 1f);
    private static Color _activeCard = Hex("#17222B", 1f);
    private static Color _text = Hex("#E6E6E6", 1f);
    private static Color _muted = Hex("#A3A3A3", 1f);
    private static Color _accent = Hex("#91D1FF", 1f);
    private static Color _success = Hex("#A2E8AF", 1f);
    private static Color _warning = Hex("#FFC814", 1f);
    private static Color _danger = Hex("#FF8787", 1f);
    private static Color _border = Hex("#3A3A3A", 1f);
    private static Color _button = Hex("#1A1A1A", 1f);
    private static Color _input = Hex("#141414", 1f);

    private static ColorBlock _buttonColors = new ColorBlock {
        normalColor = Color.white,
        highlightedColor = new Color(0.82f, 0.82f, 0.82f, 1f),
        pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f),
        selectedColor = new Color(0.9f, 0.9f, 0.9f, 1f),
        disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f),
        colorMultiplier = 1f,
        fadeDuration = 0.1f
    };

    internal static TMP_FontAsset Font { get { Resolve(); return _font; } }
    internal static Sprite PanelSprite { get { Resolve(); return _panelSprite; } }
    internal static Sprite ButtonSprite { get { Resolve(); return _buttonSprite; } }
    internal static Vector2 ReferenceResolution { get { Resolve(); return _referenceResolution; } }
    internal static float MatchWidthOrHeight { get { Resolve(); return _matchWidthOrHeight; } }
    internal static Color PanelColor { get { Resolve(); return _panel; } }
    internal static Color HeaderColor { get { Resolve(); return _header; } }
    internal static Color CardColor { get { Resolve(); return _card; } }
    internal static Color ActiveCardColor { get { Resolve(); return _activeCard; } }
    internal static Color TextColor { get { Resolve(); return _text; } }
    internal static Color MutedColor { get { Resolve(); return _muted; } }
    internal static Color AccentColor { get { Resolve(); return _accent; } }
    internal static Color SuccessColor { get { Resolve(); return _success; } }
    internal static Color WarningColor { get { Resolve(); return _warning; } }
    internal static Color DangerColor { get { Resolve(); return _danger; } }
    internal static Color BorderColor { get { Resolve(); return _border; } }
    internal static Color ButtonColor { get { Resolve(); return _button; } }
    internal static Color InputColor { get { Resolve(); return _input; } }
    internal static ColorBlock ButtonColors { get { Resolve(); return _buttonColors; } }

    internal static void StylePanel(Image image) {
        Resolve();
        image.color = _panel;
        if (_panelSprite != null) {
            image.sprite = _panelSprite;
            image.type = _panelSprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
        }
    }

    internal static void StyleButton(Image image) {
        Resolve();
        image.color = _button;
        if (_buttonSprite != null) {
            image.sprite = _buttonSprite;
            image.type = _buttonSprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
        }
    }

    internal static void Resolve() {
        if (_resolved) {
            return;
        }

        _resolved = true;

        try {
            ProbeFromGame();
        } catch (Exception ex) {
            Warn("Theme probe failed: " + ex.Message);
        }

        if (_font == null) {
            try { _font = TMP_Settings.defaultFontAsset; } catch (Exception ex) { Warn("No TMP default font: " + ex.Message); }
        }

        if (_font == null) {
            _font = CreateArialFont();
        } else if (!SupportsUiText(_font)) {
            TMP_FontAsset fallback = CreateArialFont();
            if (fallback != null) {
                Warn("The game font misses UI characters. Using a fallback font.");
                _font = fallback;
            } else {
                Warn("The game font misses UI characters. No fallback font is available.");
            }
        }

        if (_panelSprite == null && IsNeutralWhite(_panel)) {
            _panel = Hex("#0E0E0E", 0.97f);
        }

        _header = IsNeutralWhite(_panel) ? Hex("#151515", 1f) : Lighten(_panel, 0.05f);

        Note("Theme: font=" + (_font == null ? "none" : _font.name)
            + " panel=" + HexOf(_panel)
            + " text=" + HexOf(_text)
            + " accent=" + HexOf(_accent)
            + " button=" + HexOf(_button)
            + " scale=" + _referenceResolution.x + "x" + _referenceResolution.y
            + " match=" + _matchWidthOrHeight);
    }

    private static void ProbeFromGame() {
        ProbeCanvasScale();

        GameObject prefab = Resources.Load<GameObject>("Special/SettingsMenu");
        if (prefab == null) {
            Warn("Settings prefab not found. The menu uses the fallback theme.");
            return;
        }

        ReadTextStyle(prefab);
        ReadPanelStyle(prefab);

        Button button = prefab.GetComponentInChildren<Button>(true);
        if (button == null) {
            return;
        }

        _buttonColors = button.colors;

        Image target = button.targetGraphic as Image;
        if (target == null) {
            return;
        }

        _buttonSprite = target.sprite;
        if (target.sprite != null) {
            _button = IsNeutralWhite(target.color) ? Color.white : target.color;
        } else if (!IsNeutralWhite(target.color)) {
            _button = target.color;
        }
    }

    private static void ReadTextStyle(GameObject prefab) {
        Dictionary<TMP_FontAsset, int> fonts = new Dictionary<TMP_FontAsset, int>();
        Dictionary<Color, int> colors = new Dictionary<Color, int>();

        foreach (TMP_Text text in prefab.GetComponentsInChildren<TMP_Text>(true)) {
            if (text.font == null) {
                continue;
            }

            Increment(fonts, text.font);
            if (text.color.a >= 0.5f) {
                Increment(colors, text.color);
            }
        }

        if (fonts.Count == 0) {
            return;
        }

        _font = MostCommon(fonts, _font);
        if (colors.Count > 0) {
            _text = MostCommon(colors, _text);
        }
    }

    private static void ReadPanelStyle(GameObject prefab) {
        Image rootImage = prefab.GetComponent<Image>();
        if (rootImage != null && ReadPanel(rootImage)) {
            return;
        }

        foreach (Image image in prefab.GetComponentsInChildren<Image>(true)) {
            string name = image.gameObject.name.ToLowerInvariant();
            if (!name.Contains("back") && !name.Contains("panel") &&
                !name.Contains("bg") && !name.Contains("window") && !name.Contains("frame")) {
                continue;
            }

            if (ReadPanel(image)) {
                return;
            }
        }
    }

    private static bool ReadPanel(Image image) {
        _panelSprite = image.sprite;

        if (image.sprite != null) {
            _panel = IsNeutralWhite(image.color) ? Color.white : image.color;
            return true;
        }

        if (!IsNeutralWhite(image.color)) {
            _panel = image.color;
            return true;
        }

        return false;
    }

    private static void ProbeCanvasScale() {
        CanvasScaler[] scalers = Resources.FindObjectsOfTypeAll<CanvasScaler>();
        foreach (CanvasScaler scaler in scalers) {
            if (scaler == null) {
                continue;
            }

            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) {
                continue;
            }

            if (scaler.referenceResolution.x < 100f) {
                continue;
            }

            if (scaler.GetComponent<Canvas>() == null) {
                continue;
            }

            if (!scaler.gameObject.scene.IsValid()) {
                continue;
            }

            _referenceResolution = scaler.referenceResolution;
            _matchWidthOrHeight = scaler.matchWidthOrHeight;
            return;
        }
    }

    private static TMP_FontAsset CreateArialFont() {
        Font font = LoadBuiltinFont("Arial.ttf");
        if (font == null) {
            font = LoadBuiltinFont("LegacyRuntime.ttf");
        }

        if (font == null) {
            return null;
        }

        try {
            return TMP_FontAsset.CreateFontAsset(font);
        } catch (Exception ex) {
            Warn("Could not create a fallback font: " + ex.Message);
            return null;
        }
    }

    private static Font LoadBuiltinFont(string name) {
        try {
            return Resources.GetBuiltinResource<Font>(name);
        } catch (Exception) {
            return null;
        }
    }

    private static bool SupportsUiText(TMP_FontAsset font) {
        if (font == null) {
            return false;
        }

        try {
            // a dynamic font adds the character on first use, so the probe cannot trust it.
            if (font.atlasPopulationMode == AtlasPopulationMode.Dynamic) {
                return true;
            }

            for (int i = 0; i < UiCharacters.Length; i++) {
                if (!font.HasCharacter(UiCharacters[i], true)) {
                    return false;
                }
            }
            return true;
        } catch (Exception ex) {
            Warn("Could not read the font glyphs: " + ex.Message);
            return false;
        }
    }

    private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key) {
        counts.TryGetValue(key, out int count);
        counts[key] = count + 1;
    }

    private static TKey MostCommon<TKey>(Dictionary<TKey, int> counts, TKey fallback) {
        TKey best = fallback;
        int bestCount = 0;

        foreach (KeyValuePair<TKey, int> pair in counts) {
            if (pair.Value <= bestCount) {
                continue;
            }

            best = pair.Key;
            bestCount = pair.Value;
        }

        return best;
    }

    private static bool IsNeutralWhite(Color color) {
        return color.r >= 0.95f && color.g >= 0.95f && color.b >= 0.95f;
    }

    private static Color Lighten(Color color, float amount) {
        return new Color(
            Mathf.Clamp01(color.r + amount),
            Mathf.Clamp01(color.g + amount),
            Mathf.Clamp01(color.b + amount),
            Mathf.Clamp01(color.a + amount));
    }

    private static Color Hex(string value, float alpha) {
        if (ColorUtility.TryParseHtmlString(value, out Color color)) {
            color.a = alpha;
            return color;
        }
        return Color.white;
    }

    private static string HexOf(Color color) {
        return "#" + ColorUtility.ToHtmlStringRGB(color);
    }

    private static void Warn(string message) {
        if (SpriteReplacerPlugin.Log != null) {
            SpriteReplacerPlugin.Log.LogWarning(message);
        }
    }

    private static void Note(string message) {
        if (SpriteReplacerPlugin.Log != null) {
            SpriteReplacerPlugin.Log.LogInfo(message);
        }
    }
}