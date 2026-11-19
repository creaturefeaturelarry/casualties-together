using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Together;
using UnityEngine;
using UnityEngine.UI;

internal static class SkinMenuUI {
    private const float WindowWidth = 780f;
    private const float WindowHeight = 636f;
    private const string CommunityUrl = "https://casutogether.com/";
    private const string SkinFileFilter =
        "Skin files (*.json;*.zip;*.casutogether)\0*.json;*.zip;*.casutogether\0All files\0*.*\0";

    private static GameObject _canvas;
    private static GraphicRaycaster _canvasRaycaster;
    private static readonly HashSet<GraphicRaycaster> _disabledRaycasters = new HashSet<GraphicRaycaster>();
    private static readonly HashSet<Behaviour> _disabledButtons = new HashSet<Behaviour>();
    private static Type _adaptiveButtonType;
    private static GameObject _popupWindow;
    private static GameObject _popupBackdrop;
    private static GameObject _grid;
    private static GameObject _browserWindow;
    private static GameObject _browserBackdrop;
    private static TMP_InputField _pathInput;
    private static RectTransform _listContent;
    private static ScrollRect _listScroll;
    private static TextMeshProUGUI _statusText;
    private static ScrollRect _statusScroll;
    private static string _browserPath;
    private static string _pendingSlot;
    private static bool _visible;
    private static Sprite _placeholder;

    private static readonly Dictionary<string, Image> _cardBackgrounds = new Dictionary<string, Image>();
    private static readonly Dictionary<string, Outline> _cardOutlines = new Dictionary<string, Outline>();
    private static readonly Dictionary<string, Button> _cardSelectButtons = new Dictionary<string, Button>();
    private static readonly Dictionary<string, Button> _cardSwitchButtons = new Dictionary<string, Button>();
    private static readonly List<Texture2D> _previewTextures = new List<Texture2D>();
    private static readonly List<Sprite> _previewSprites = new List<Sprite>();
    private static Button _lockButton;
    private static TextMeshProUGUI _lockStatus;
    private static TextMeshProUGUI _rateLimitLabel;
    private static Button _shareButton;
    private static Button _downloadButton;
    private static Button _cacheButton;
    private static readonly Button[] _speciesButtons = new Button[3];
    private static readonly string[] _speciesLabels = { "Expie", "Milky", "Dune" };
    private static int _browseSpecies;
    private static bool _openOnShow;
    private static bool _cooldownShown;
    private static RectTransform _menuButtonRt;
    private static Transform _menuButtonHome;

    internal static bool IsFolderBrowserOpen => _browserWindow != null;

    internal static bool IsPopupOpen => _popupWindow != null && _popupWindow.activeSelf;

    internal static void EnsureCanvas() {
        if (_canvas != null) {
            return;
        }

        GameTheme.Resolve();
        _placeholder = SpriteReplacerPlugin.LoadEmbeddedSprite("body_sprite_replacer.resources.spritereplacer1.png");

        GameObject canvasObj = new GameObject("SpriteReplacerMenuCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9999;

        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = GameTheme.ReferenceResolution;
        scaler.matchWidthOrHeight = GameTheme.MatchWidthOrHeight;

        _canvasRaycaster = canvasObj.AddComponent<GraphicRaycaster>();
        UnityEngine.Object.DontDestroyOnLoad(canvasObj);
        _canvas = canvasObj;

        CreateTopButton(canvasObj.transform);
    }

    internal static void SetVisible(bool visible) {
        if (_canvas == null) {
            return;
        }

        if (_visible == visible && _canvas.activeSelf == visible) {
            return;
        }

        _visible = visible;

        if (!visible) {
            CloseFolderBrowser();
            ClosePopup();
        }

        _canvas.SetActive(visible);

        if (visible) {
            AlignMenuButtonBesideMp();
        }

        if (visible && (_openOnShow || SkinLock.CanChangeOnce)) {
            _openOnShow = false;
            OpenPopup();
        }
    }

    // park the square skin icon flush left of the mp menu button
    internal static void AlignMenuButtonBesideMp() {
        if (_menuButtonRt == null) {
            return;
        }

        GameObject mpObj = UIManager.MP_MENU_BUTTON;
        if (mpObj == null) {
            if (_menuButtonHome != null && _menuButtonRt.parent != _menuButtonHome) {
                _menuButtonRt.SetParent(_menuButtonHome, false);
                SetRect(_menuButtonRt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(20f, -20f), new Vector2(64f, 64f));
            }
            return;
        }

        RectTransform mp = mpObj.GetComponent<RectTransform>();
        if (mp == null || mp.parent == null) {
            return;
        }

        if (_menuButtonRt.parent != mp.parent) {
            _menuButtonRt.SetParent(mp.parent, false);
        }

        // match mp button vertical stretch; park a square flush on its left edge.
        const float gap = 8f;
        _menuButtonRt.pivot = new Vector2(1f, 0.5f);
        _menuButtonRt.anchorMin = new Vector2(mp.anchorMin.x, mp.anchorMin.y);
        _menuButtonRt.anchorMax = new Vector2(mp.anchorMin.x, mp.anchorMax.y);
        float height = mp.rect.height;
        if (height < 1f) {
            RectTransform parentRt = mp.parent as RectTransform;
            if (parentRt != null) {
                height = (mp.anchorMax.y - mp.anchorMin.y) * parentRt.rect.height;
            }
        }
        if (height < 1f) {
            height = 64f;
        }
        _menuButtonRt.offsetMin = new Vector2(-height - gap, 0f);
        _menuButtonRt.offsetMax = new Vector2(-gap, 0f);
        _menuButtonRt.localScale = Vector3.one;
        _menuButtonRt.SetSiblingIndex(mp.GetSiblingIndex());
    }

    internal static void RefreshSelection() {
        bool live = _browseSpecies == SpriteReplacerPlugin.ActiveSpecies;
        string marked = live
            ? SpriteReplacerPlugin.ActiveSlot
            : SpriteReplacerPlugin.FolderForSpecies(_browseSpecies);
        foreach (KeyValuePair<string, Image> pair in _cardBackgrounds) {
            bool active = pair.Key == marked;

            if (pair.Value != null) {
                pair.Value.color = active ? GameTheme.ActiveCardColor : GameTheme.CardColor;
            }

            if (_cardOutlines.TryGetValue(pair.Key, out Outline outline) && outline != null) {
                outline.effectColor = active ? GameTheme.AccentColor : GameTheme.BorderColor;
            }
        }
    }

    internal static void RefreshLockUi() {
        bool active = MpBody.IsActive;
        bool host = active && MpBody.IsHost;

        if (_lockButton != null) {
            _lockButton.gameObject.SetActive(host);
            TMP_Text label = _lockButton.GetComponentInChildren<TMP_Text>();
            if (label != null) {
                label.text = SkinLock.IsLocked ? "Release lock" : "Lock skins";
            }
        }

        if (_lockStatus != null) {
            if (MpSkinSync.IsCoolingDown) {
                _lockStatus.gameObject.SetActive(true);
                _lockStatus.text = "Skin change paused: " + MpSkinSync.CooldownSeconds + "s";
                _lockStatus.color = GameTheme.DangerColor;
                _cooldownShown = true;
            } else {
                _cooldownShown = false;
                _lockStatus.gameObject.SetActive(active && !host);

                if (SkinLock.CanChangeOnce) {
                    _lockStatus.text = "Locked. Choose one skin.";
                    _lockStatus.color = GameTheme.WarningColor;
                } else {
                    _lockStatus.text = SkinLock.IsLocked ? "Skins locked by host" : "Skins unlocked";
                    _lockStatus.color = SkinLock.IsLocked ? GameTheme.WarningColor : GameTheme.MutedColor;
                }
            }
        }

        bool canChange = SkinLock.CanChangeLocal;
        foreach (KeyValuePair<string, Button> pair in _cardSwitchButtons) {
            if (pair.Value != null) {
                pair.Value.interactable = canChange;
            }
        }

        foreach (KeyValuePair<string, Button> pair in _cardSelectButtons) {
            if (pair.Value != null) {
                pair.Value.interactable = canChange && SpriteReplacerPlugin.CharacterFolderExists(pair.Key, _browseSpecies);
            }
        }

        UpdateCooldownLabel();
    }

    private static void UpdateCooldownLabel() {
        if (_rateLimitLabel == null) {
            return;
        }

        if (!MpSkinSync.IsCoolingDown) {
            if (_rateLimitLabel.gameObject.activeSelf) {
                _rateLimitLabel.gameObject.SetActive(false);
            }

            return;
        }

        if (!_rateLimitLabel.gameObject.activeSelf) {
            _rateLimitLabel.gameObject.SetActive(true);
        }

        string text = "Skin change paused: " + MpSkinSync.CooldownSeconds + "s";
        if (_rateLimitLabel.text != text) {
            _rateLimitLabel.text = text;
        }
    }

    private static void RefreshSettingsUi() {
        SetToggleLabel(_shareButton, "Visible to others", SpriteReplacerPlugin.ShareSkins);
        SetToggleLabel(_downloadButton, "View skins of others", SpriteReplacerPlugin.DownloadSkins);
    }

    private static void SetToggleLabel(Button button, string name, bool value) {
        if (button == null) {
            return;
        }

        TMP_Text label = button.GetComponentInChildren<TMP_Text>();
        if (label == null) {
            return;
        }

        label.text = name + ": " + (value ? "ON" : "OFF");
        label.color = value ? GameTheme.SuccessColor : GameTheme.MutedColor;
    }

    private static void ToggleShare() {
        SpriteReplacerPlugin.SetShareSkins(!SpriteReplacerPlugin.ShareSkins);
        MpSkinSync.OnShareChanged();
        RefreshSettingsUi();
    }

    private static void ToggleDownload() {
        SpriteReplacerPlugin.SetDownloadSkins(!SpriteReplacerPlugin.DownloadSkins);
        MpSkinSync.OnDownloadChanged();
        RefreshSettingsUi();
    }

    private static void ClearCache() {
        int count = MpSkinSync.ClearCache();
        SkinLock.AnnounceCacheCleared(count);
    }

    internal static void TickCooldown() {
        if (IsPopupOpen) {
            // if the overlay canvas died while open, restore menu clicks instead of softlocking
            if (_canvas == null || !_canvas.activeInHierarchy) {
                ClosePopup();
            } else {
                SetGameRaycasters(false);
                SetGameButtons(false);
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) {
                    ClosePopup();
                }
            }
        }

        UpdateCooldownLabel();

        if (!MpSkinSync.IsCoolingDown) {
            if (_cooldownShown) {
                _cooldownShown = false;
                RefreshLockUi();
            }
            return;
        }

        if (_lockStatus == null) {
            return;
        }

        string text = "Skin change paused: " + MpSkinSync.CooldownSeconds + "s";
        if (_lockStatus.text != text) {
            _lockStatus.text = text;
            _lockStatus.color = GameTheme.DangerColor;
        }
        _cooldownShown = true;
    }

    private static void CreateTopButton(Transform parent) {
        GameObject buttonObj = new GameObject("MenuButton");
        buttonObj.transform.SetParent(parent, false);
        _menuButtonHome = parent;

        RectTransform rect = buttonObj.AddComponent<RectTransform>();
        _menuButtonRt = rect;
        // fallback top-left until the mp menu button exists; AlignMenuButtonBesideMp reparents it.
        SetRect(rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -20f), new Vector2(64f, 64f));

        Image image = buttonObj.AddComponent<Image>();
        image.sprite = _placeholder;
        // fill the square next to the MP button (same height); don't letterbox inside the rect
        image.preserveAspect = false;
        image.type = Image.Type.Simple;

        Button button = buttonObj.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(TogglePopup);

        _rateLimitLabel = CreateText(parent, "RateLimit", "", 16, GameTheme.DangerColor,
            TextAlignmentOptions.TopLeft);
        SetRect(_rateLimitLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 1f), new Vector2(20f, -88f), new Vector2(260f, 24f));
        _rateLimitLabel.gameObject.SetActive(false);

        AlignMenuButtonBesideMp();
    }

    private static void TogglePopup() {
        try {
            if (_popupWindow == null) {
                CreatePopup();
                SetPopupVisible(true);
            } else {
                SetPopupVisible(!_popupWindow.activeSelf);
            }
            RefreshLockUi();
        } catch (Exception e) {
            SpriteReplacerPlugin.Log.LogError("Skin popup toggle failed: " + e);
            ForceRestoreMenuInput();
        }
    }

    internal static void OpenPopup() {
        if (_canvas == null) {
            return;
        }

        try {
            if (_popupWindow == null) {
                CreatePopup();
                SetPopupVisible(true);
                return;
            }

            SetPopupVisible(true);
            RefreshLockUi();
        } catch (Exception e) {
            SpriteReplacerPlugin.Log.LogError("Skin popup open failed: " + e);
            ForceRestoreMenuInput();
        }
    }

    private static void ForceRestoreMenuInput() {
        try {
            if (_popupWindow != null) {
                _popupWindow.SetActive(false);
            }
            if (_popupBackdrop != null) {
                _popupBackdrop.SetActive(false);
            }
        } catch {
            // ignore cleanup failures - restoring raycasters matters more
        }
        SetGameRaycasters(true);
        SetGameButtons(true);
    }

    internal static void OpenPopupForChoice() {
        if (_canvas == null) {
            return;
        }

        if (!_canvas.activeInHierarchy) {
            _openOnShow = true;
            return;
        }

        OpenPopup();
    }

    private static void CreatePopup() {
        if (_canvas == null) {
            return;
        }

        CreateBackdrop();

        GameObject window = new GameObject("SkinReplacerPopup");
        window.transform.SetParent(_canvas.transform, false);

        RectTransform rect = window.AddComponent<RectTransform>();
        SetRect(rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(WindowWidth, WindowHeight));

        Image background = window.AddComponent<Image>();
        GameTheme.StylePanel(background);

        GameObject bar = new GameObject("HeaderBar");
        bar.transform.SetParent(window.transform, false);
        RectTransform barRect = bar.AddComponent<RectTransform>();
        SetRect(barRect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            Vector2.zero, new Vector2(0f, 96f));
        bar.AddComponent<Image>().color = GameTheme.HeaderColor;

        TextMeshProUGUI title = CreateText(bar.transform, "Title", "Skin Replacer", 30,
            GameTheme.TextColor, TextAlignmentOptions.BottomLeft);
        SetRect(title.rectTransform, new Vector2(0f, 0.62f), new Vector2(0f, 1f), new Vector2(0f, 0f),
            new Vector2(24f, 2f), new Vector2(420f, 0f));

        TextMeshProUGUI community = CreateText(bar.transform, "Community",
            "Visit <link=\"" + CommunityUrl + "\"><color=#" + ColorUtility.ToHtmlStringRGB(GameTheme.AccentColor)
            + "><u>casutogether.com</u></color></link> to browse community made skins or make your own!",
            13, GameTheme.MutedColor, TextAlignmentOptions.MidlineLeft);
        community.raycastTarget = true;
        community.gameObject.AddComponent<LinkClickHandler>().Text = community;
        SetRect(community.rectTransform, new Vector2(0f, 0.32f), new Vector2(0f, 0.62f), new Vector2(0f, 0.5f),
            new Vector2(24f, 0f), new Vector2(520f, 0f));

        TextMeshProUGUI hint = CreateText(bar.transform, "Hint",
            "Numpad 0 = default, 1-9 = slots.",
            13, GameTheme.MutedColor, TextAlignmentOptions.TopLeft);
        SetRect(hint.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0.32f), new Vector2(0f, 1f),
            new Vector2(24f, -2f), new Vector2(420f, 0f));

        Button close = CreateButton(bar.transform, "Close", "X", GameTheme.DangerColor, 20);
        SetRect(close.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(-14f, 0f), new Vector2(40f, 40f));
        close.onClick.AddListener(ClosePopup);

        _lockButton = CreateButton(bar.transform, "Lock", "Lock skins", GameTheme.WarningColor, 18);
        SetRect(_lockButton.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f), new Vector2(-70f, 0f), new Vector2(170f, 32f));
        _lockButton.onClick.AddListener(SkinLock.Toggle);

        _lockStatus = CreateText(bar.transform, "LockStatus", "", 18, GameTheme.WarningColor,
            TextAlignmentOptions.MidlineRight);
        SetRect(_lockStatus.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f), new Vector2(-70f, 0f), new Vector2(300f, 32f));

        // toggles get their own row under the header
        _shareButton = CreateButton(window.transform, "Share", "Visible to others: ON",
            GameTheme.SuccessColor, 14);
        SetRect(_shareButton.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 1f), new Vector2(24f, -102f), new Vector2(190f, 28f));
        _shareButton.onClick.AddListener(ToggleShare);

        _downloadButton = CreateButton(window.transform, "Download", "View skins of others: ON",
            GameTheme.SuccessColor, 14);
        SetRect(_downloadButton.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 1f), new Vector2(222f, -102f), new Vector2(220f, 28f));
        _downloadButton.onClick.AddListener(ToggleDownload);

        _cacheButton = CreateButton(window.transform, "Cache", "Clear cache",
            GameTheme.MutedColor, 14);
        SetRect(_cacheButton.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 1f), new Vector2(450f, -102f), new Vector2(110f, 28f));
        _cacheButton.onClick.AddListener(ClearCache);

        // species tabs on their own row, same buttons as the toggles above
        for (int s = 0; s < _speciesLabels.Length; s++) {
            int species = s;
            Button tab = CreateButton(window.transform, "Species" + s, _speciesLabels[s], GameTheme.TextColor, 14);
            SetRect(tab.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(24f + s * 148f, -138f), new Vector2(140f, 28f));
            tab.onClick.AddListener(() => ShowSpecies(species));
            _speciesButtons[s] = tab;
        }
        _browseSpecies = SpriteReplacerPlugin.ActiveSpecies;
        RefreshSpeciesTabs();

        _popupWindow = window;
        BuildGrid();
        RefreshLockUi();
        RefreshSettingsUi();
    }

    private static void BuildGrid() {
        if (_popupWindow == null) {
            return;
        }

        if (_grid != null) {
            _grid.SetActive(false);
            UnityEngine.Object.Destroy(_grid);
        }

        ClearPreviewImages();
        _cardBackgrounds.Clear();
        _cardOutlines.Clear();
        _cardSelectButtons.Clear();
        _cardSwitchButtons.Clear();

        GameObject gridObj = new GameObject("CharacterGrid");
        gridObj.transform.SetParent(_popupWindow.transform, false);

        RectTransform gridRect = gridObj.AddComponent<RectTransform>();
        SetRect(gridRect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -174f), new Vector2(740f, 434f));
        _grid = gridObj;

        const int columns = 3;
        const float cellWidth = 236f;
        // default + slots 1-9 = 10 cards / 4 rows; keep the grid height under the window.
        const float cellHeight = 112f;
        const float gap = 10f;
        float startX = -(columns * cellWidth + (columns - 1) * gap) / 2f + cellWidth / 2f;

        // card 0 is default (not slot0). Cards 1-9 are custom slots.
        for (int i = 0; i <= SpriteReplacerPlugin.SlotCount; i++) {
            int row = i / columns;
            int column = i % columns;
            float x = startX + column * (cellWidth + gap);
            float y = -(row * (cellHeight + gap) + cellHeight / 2f);
            string folder = i == 0 ? SkinSettings.DefaultFolder : SlotFolders.SlotId(i);
            CreateCard(gridObj.transform, folder, x, y, cellWidth, cellHeight);
        }

        RefreshSelection();
        RefreshLockUi();
    }

    private static void CreateCard(Transform parent, string slot, float x, float y, float width, float height) {
        bool hasSkin = SpriteReplacerPlugin.CharacterFolderExists(slot, _browseSpecies);

        GameObject card = new GameObject("Card_" + slot);
        card.transform.SetParent(parent, false);

        RectTransform rect = card.AddComponent<RectTransform>();
        SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
            new Vector2(x, y), new Vector2(width, height));

        bool live = _browseSpecies == SpriteReplacerPlugin.ActiveSpecies;
        string marked = live
            ? SpriteReplacerPlugin.ActiveSlot
            : SpriteReplacerPlugin.FolderForSpecies(_browseSpecies);
        bool active = slot == marked;

        Image background = card.AddComponent<Image>();
        background.color = active ? GameTheme.ActiveCardColor : GameTheme.CardColor;
        _cardBackgrounds[slot] = background;

        Outline outline = card.AddComponent<Outline>();
        outline.effectColor = active ? GameTheme.AccentColor : GameTheme.BorderColor;
        outline.effectDistance = new Vector2(1.5f, -1.5f);
        outline.useGraphicAlpha = false;
        _cardOutlines[slot] = outline;

        GameObject previewObj = new GameObject("Preview");
        previewObj.transform.SetParent(card.transform, false);
        RectTransform previewRect = previewObj.AddComponent<RectTransform>();
        SetRect(previewRect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -4f), new Vector2(216f, 44f));

        Image preview = previewObj.AddComponent<Image>();
        preview.sprite = LoadHeadPreview(slot);
        preview.preserveAspect = true;
        preview.color = hasSkin ? Color.white : new Color(1f, 1f, 1f, 0.35f);

        string title = SlotFolders.DisplayName(slot);
        if (string.Equals(slot, SkinSettings.DefaultFolder, StringComparison.OrdinalIgnoreCase)) {
            title = hasSkin ? "Default (0)" : "Default (0, empty)";
        } else if (!hasSkin) {
            title += " (empty)";
        }

        TextMeshProUGUI label = CreateText(card.transform, "SlotLabel",
            title,
            16, hasSkin ? GameTheme.TextColor : GameTheme.MutedColor, TextAlignmentOptions.Center);
        SetRect(label.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -50f), new Vector2(216f, 20f));

        Button select = CreateButton(card.transform, "Select", "Select", GameTheme.TextColor, 13);
        SetRect(select.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(-74f, 6f), new Vector2(68f, 26f));
        select.interactable = hasSkin && SkinLock.CanChangeLocal;
        select.onClick.AddListener(() => SelectBrowsedSlot(slot));
        _cardSelectButtons[slot] = select;

        Button folderButton = CreateButton(card.transform, "ImportFolder", "Folder", GameTheme.SuccessColor, 13);
        SetRect(folderButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 6f), new Vector2(68f, 26f));
        folderButton.interactable = SkinLock.CanChangeLocal;
        folderButton.onClick.AddListener(() => ImportFromDialog(slot, false));
        _cardSwitchButtons[slot] = folderButton;

        Button fileButton = CreateButton(card.transform, "ImportFile", "File", GameTheme.SuccessColor, 13);
        SetRect(fileButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(74f, 6f), new Vector2(68f, 26f));
        fileButton.interactable = SkinLock.CanChangeLocal;
        fileButton.onClick.AddListener(() => ImportFromDialog(slot, true));
        _cardSwitchButtons[slot + ":file"] = fileButton;
    }

    private static void ShowSpecies(int species) {
        if (species < 0 || species > 2) {
            species = 0;
        }

        _browseSpecies = species;
        RefreshSpeciesTabs();
        BuildGrid();
    }

    private static void RefreshSpeciesTabs() {
        for (int i = 0; i < _speciesButtons.Length; i++) {
            Button button = _speciesButtons[i];
            if (button == null) {
                continue;
            }

            TMP_Text label = button.GetComponentInChildren<TMP_Text>();
            if (label != null) {
                label.color = i == _browseSpecies ? GameTheme.AccentColor : GameTheme.TextColor;
            }
        }
    }

    private static void SelectBrowsedSlot(string slot) {
        if (_browseSpecies == SpriteReplacerPlugin.ActiveSpecies) {
            SpriteReplacerPlugin.SwitchToSlot(slot);
            return;
        }

        if (!SkinLock.CanChangeLocal) {
            return;
        }

        SkinSettings.Remember(_browseSpecies, slot);
        RefreshSelection();
    }

    // the head a card shows, by species. old slots keep the experiment names, so they are the later choices.
    private static readonly string[][] PreviewNames = {
        new[] { "experimentHeadBack", "experimentHead" },
        new[] { "milkyheadback", "experimentHeadBack" },
        new[] { "duneHeadBack", "experimentHeadBack" }
    };

    // the open eyes drawn over that head, by species, with the experiment name as the second choice like the head.
    private static readonly string[][] PreviewEyeNames = {
        new[] { "experimentEyeOpen" },
        new[] { "milkyeyeopen", "experimentEyeOpen" },
        new[] { "duneEyes", "experimentEyeOpen" }
    };

    private static string FindHeadFile(string root, string[] names) {
        foreach (string name in names) {
            foreach (string folder in new[] { "Head", "Body" }) {
                string path = Path.Combine(root, folder, name + ".png");
                if (File.Exists(path)) {
                    return path;
                }
            }
        }

        return null;
    }

    private static Sprite LoadHeadPreview(string slot) {
        string root = SpriteReplacerPlugin.SlotDirectory(slot, _browseSpecies);
        int species = Mathf.Clamp(_browseSpecies, 0, PreviewNames.Length - 1);
        string[] names = PreviewNames[species];
        string[] eyeNames = PreviewEyeNames[species];
        if (SkinJsonFile.Exists(root)) {
            return SkinJsonFile.PreviewSprite(root, _browseSpecies, names, eyeNames) ?? _placeholder;
        }

        string path = FindHeadFile(root, names);
        if (path == null) {
            return _placeholder;
        }

        try {
            SkinGrid head = SkinJsonFile.GridFromPng(path);
            if (head == null) {
                return _placeholder;
            }

            string eyePath = FindHeadFile(root, eyeNames);
            SkinGrid eyes = eyePath != null ? SkinJsonFile.GridFromPng(eyePath) : null;
            Sprite sprite = SkinJsonFile.ToSprite(slot + "_preview", SkinJsonFile.ComposeHead(head, eyes), _previewTextures);
            _previewSprites.Add(sprite);
            return sprite;
        } catch (Exception ex) {
            SpriteReplacerPlugin.Log.LogWarning("Could not load preview for " + slot + ": " + ex.Message);
        }

        return _placeholder;
    }

    private static void ClearPreviewImages() {
        foreach (Sprite sprite in _previewSprites) {
            if (sprite != null) {
                UnityEngine.Object.Destroy(sprite);
            }
        }
        _previewSprites.Clear();

        foreach (Texture2D texture in _previewTextures) {
            if (texture != null) {
                UnityEngine.Object.Destroy(texture);
            }
        }
        _previewTextures.Clear();
    }

    private static void ClosePopup() {
        SetPopupVisible(false);
    }

    private static void SetPopupVisible(bool visible) {
        try {
            if (_popupWindow != null) {
                _popupWindow.SetActive(visible);
            }

            if (_popupBackdrop != null) {
                _popupBackdrop.SetActive(visible);
            }

            if (visible) {
                int live = SpriteReplacerPlugin.ActiveSpecies;
                if (_browseSpecies != live) {
                    _browseSpecies = live;
                    RefreshSpeciesTabs();
                    BuildGrid();
                }
            }

            SetGameRaycasters(!visible);
            SetGameButtons(!visible);
        } catch (Exception e) {
            SpriteReplacerPlugin.Log.LogError("Skin SetPopupVisible failed: " + e);
            ForceRestoreMenuInput();
        }
    }

    private static void SetGameButtons(bool enabled) {
        if (enabled) {
            foreach (Behaviour button in _disabledButtons) {
                if (button != null) {
                    button.enabled = true;
                }
            }
            _disabledButtons.Clear();
            return;
        }

        if (_adaptiveButtonType == null) {
            _adaptiveButtonType = typeof(Limb).Assembly.GetType("AdaptiveButton", false);
        }

        if (_adaptiveButtonType == null) {
            return;
        }

        foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(_adaptiveButtonType)) {
            if (!(obj is Behaviour button) || !button.enabled) {
                continue;
            }

            button.enabled = false;
            _disabledButtons.Add(button);
        }
    }

    private static void SetGameRaycasters(bool enabled) {
        if (enabled) {
            foreach (GraphicRaycaster raycaster in _disabledRaycasters) {
                if (raycaster != null) {
                    raycaster.enabled = true;
                }
            }
            _disabledRaycasters.Clear();
            return;
        }

        foreach (GraphicRaycaster raycaster in UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>()) {
            if (raycaster == null || raycaster == _canvasRaycaster) {
                continue;
            }

            if (!raycaster.enabled) {
                continue;
            }

            raycaster.enabled = false;
            _disabledRaycasters.Add(raycaster);
        }
    }

    private static void CreateBackdrop() {
        if (_popupBackdrop != null) {
            return;
        }

        GameObject backdrop = new GameObject("SkinReplacerBackdrop");
        backdrop.transform.SetParent(_canvas.transform, false);
        RectTransform rect = backdrop.AddComponent<RectTransform>();
        SetRect(rect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero);

        Image image = backdrop.AddComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.35f);
        image.raycastTarget = true;

        Button button = backdrop.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(CloseFromBackdrop);

        backdrop.transform.SetAsFirstSibling();
        _popupBackdrop = backdrop;
    }

    private static void CloseFromBackdrop() {
        CloseFolderBrowser();
        SetPopupVisible(false);
    }

    // the operating system's dialog picks the folder or the skin file. without one the in-game browser is used.
    private static void ImportFromDialog(string slot, bool pickFile) {
        CloseFolderBrowser();
        _pendingSlot = slot;
        string start = GetStartDirectory();
        string path;
        bool shown = pickFile
            ? NativeDialogs.TryPickFile("Choose a skin file", start, SkinFileFilter, out path)
            : NativeDialogs.TryPickFolder("Choose a skin folder", start, out path);
        if (!shown) {
            OpenFolderBrowser(slot);
            return;
        }

        if (string.IsNullOrEmpty(path)) {
            return;
        }

        _browserPath = path;
        UseSelectedFolder();
    }

    private static void OpenFolderBrowser(string slot) {
        CloseFolderBrowser();
        _pendingSlot = slot;
        _browserPath = GetStartDirectory();
        CreateBrowserWindow();
        Navigate(_browserPath);
    }

    private static string GetStartDirectory() {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) {
            return desktop;
        }

        return SpriteReplacerPlugin.SpritesPath;
    }

    private static void CreateBrowserWindow() {
        if (_canvas == null) {
            return;
        }

        GameObject backdrop = new GameObject("SkinBrowserBackdrop");
        backdrop.transform.SetParent(_canvas.transform, false);
        RectTransform backdropRect = backdrop.AddComponent<RectTransform>();
        SetRect(backdropRect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Image backdropImage = backdrop.AddComponent<Image>();
        backdropImage.color = new Color(0f, 0f, 0f, 0.7f);
        _browserBackdrop = backdrop;

        GameObject window = new GameObject("SkinBrowserWindow");
        window.transform.SetParent(_canvas.transform, false);
        RectTransform rect = window.AddComponent<RectTransform>();
        SetRect(rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(760f, 560f));

        Image background = window.AddComponent<Image>();
        GameTheme.StylePanel(background);
        _browserWindow = window;

        GameObject bar = new GameObject("HeaderBar");
        bar.transform.SetParent(window.transform, false);
        RectTransform barRect = bar.AddComponent<RectTransform>();
        SetRect(barRect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            Vector2.zero, new Vector2(0f, 48f));
        bar.AddComponent<Image>().color = GameTheme.HeaderColor;

        TextMeshProUGUI header = CreateText(bar.transform, "Header",
            "Switch " + SlotFolders.DisplayName(_pendingSlot) + ": choose a skin folder",
            24, GameTheme.TextColor, TextAlignmentOptions.MidlineLeft);
        SetRect(header.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(-48f, 0f));

        Button up = CreateButton(window.transform, "Up", "Up", GameTheme.TextColor, 16);
        SetRect(up.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -56f), new Vector2(56f, 32f));
        up.onClick.AddListener(NavigateUp);

        _pathInput = CreateInputField(window.transform, "PathInput");
        SetRect(_pathInput.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(88f, -56f), new Vector2(500f, 32f));
        _pathInput.onEndEdit.AddListener(Navigate);

        Button go = CreateButton(window.transform, "Go", "Go", GameTheme.TextColor, 16);
        SetRect(go.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(596f, -56f), new Vector2(60f, 32f));
        go.onClick.AddListener(() => Navigate(_pathInput.text));

        Button drives = CreateButton(window.transform, "Drives", "Drives", GameTheme.TextColor, 16);
        SetRect(drives.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(664f, -56f), new Vector2(72f, 32f));
        drives.onClick.AddListener(ShowDrives);

        GameObject statusBox = new GameObject("StatusBox");
        statusBox.transform.SetParent(window.transform, false);
        RectTransform statusBoxRect = statusBox.AddComponent<RectTransform>();
        SetRect(statusBoxRect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -96f), new Vector2(712f, 100f));
        _statusScroll = statusBox.AddComponent<ScrollRect>();

        GameObject statusViewport = new GameObject("Viewport");
        statusViewport.transform.SetParent(statusBox.transform, false);
        RectTransform statusViewportRect = statusViewport.AddComponent<RectTransform>();
        SetRect(statusViewportRect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero);
        statusViewport.AddComponent<RectMask2D>();
        Image statusViewportImage = statusViewport.AddComponent<Image>();
        statusViewportImage.color = new Color(0f, 0f, 0f, 0.01f);

        _statusText = CreateText(statusViewport.transform, "Status",
            "Choose the folder that contains Body and Head.",
            14, GameTheme.MutedColor, TextAlignmentOptions.TopLeft);
        _statusText.richText = false;
        _statusText.overflowMode = TextOverflowModes.Overflow;
        RectTransform statusTextRect = _statusText.rectTransform;
        statusTextRect.anchorMin = new Vector2(0f, 1f);
        statusTextRect.anchorMax = new Vector2(1f, 1f);
        statusTextRect.pivot = new Vector2(0.5f, 1f);
        statusTextRect.anchoredPosition = Vector2.zero;
        statusTextRect.sizeDelta = Vector2.zero;
        ContentSizeFitter statusFitter = _statusText.gameObject.AddComponent<ContentSizeFitter>();
        statusFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _statusScroll.viewport = statusViewportRect;
        _statusScroll.content = statusTextRect;
        _statusScroll.horizontal = false;
        _statusScroll.vertical = true;
        _statusScroll.movementType = ScrollRect.MovementType.Clamped;
        _statusScroll.scrollSensitivity = 20f;

        GameObject list = new GameObject("FolderList");
        list.transform.SetParent(window.transform, false);
        RectTransform listRect = list.AddComponent<RectTransform>();
        SetRect(listRect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -200f), new Vector2(712f, 296f));
        _listScroll = list.AddComponent<ScrollRect>();

        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(list.transform, false);
        RectTransform viewportRect = viewport.AddComponent<RectTransform>();
        SetRect(viewportRect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        viewport.AddComponent<RectMask2D>();
        Image viewportImage = viewport.AddComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
        _listScroll.viewport = viewportRect;

        GameObject content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        _listContent = content.AddComponent<RectTransform>();
        _listContent.anchorMin = new Vector2(0f, 1f);
        _listContent.anchorMax = new Vector2(1f, 1f);
        _listContent.pivot = new Vector2(0.5f, 1f);
        _listContent.anchoredPosition = Vector2.zero;
        _listContent.sizeDelta = Vector2.zero;

        VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 2f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _listScroll.content = _listContent;
        _listScroll.horizontal = false;
        _listScroll.vertical = true;
        _listScroll.movementType = ScrollRect.MovementType.Clamped;
        _listScroll.scrollSensitivity = 30f;

        Button use = CreateButton(window.transform, "Use", "Use this folder", GameTheme.SuccessColor, 16);
        SetRect(use.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -516f), new Vector2(220f, 32f));
        use.onClick.AddListener(UseSelectedFolder);

        Button close = CreateButton(window.transform, "Close", "Close", GameTheme.DangerColor, 16);
        SetRect(close.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-24f, -516f), new Vector2(96f, 32f));
        close.onClick.AddListener(CloseFolderBrowser);
    }

    private static TMP_InputField CreateInputField(Transform parent, string name) {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.AddComponent<RectTransform>();
        obj.SetActive(false);

        Image background = obj.AddComponent<Image>();
        background.color = GameTheme.InputColor;

        TMP_InputField field = obj.AddComponent<TMP_InputField>();
        field.targetGraphic = background;
        field.lineType = TMP_InputField.LineType.SingleLine;

        GameObject viewport = new GameObject("TextArea");
        viewport.transform.SetParent(obj.transform, false);
        RectTransform viewportRect = viewport.AddComponent<RectTransform>();
        SetRect(viewportRect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            new Vector2(8f, 0f), new Vector2(-16f, 0f));
        viewport.AddComponent<RectMask2D>();

        TextMeshProUGUI text = CreateText(viewport.transform, "Text", "", 16,
            GameTheme.TextColor, TextAlignmentOptions.MidlineLeft);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero);
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;

        TextMeshProUGUI placeholder = CreateText(viewport.transform, "Placeholder", "Path", 16,
            GameTheme.MutedColor, TextAlignmentOptions.MidlineLeft);
        SetRect(placeholder.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero);
        placeholder.enableWordWrapping = false;

        field.textViewport = viewportRect;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.caretWidth = 2;

        obj.SetActive(true);
        return field;
    }

    private static void NavigateUp() {
        if (string.IsNullOrEmpty(_browserPath)) {
            ShowDrives();
            return;
        }

        DirectoryInfo parent = Directory.GetParent(_browserPath);
        if (parent == null) {
            ShowDrives();
            return;
        }

        Navigate(parent.FullName);
    }

    private static void ShowDrives() {
        ClearList();
        _browserPath = string.Empty;
        if (_pathInput != null) {
            _pathInput.text = string.Empty;
        }

        SetStatus("Select a drive.", GameTheme.MutedColor);

        DriveInfo[] drives;
        try {
            drives = DriveInfo.GetDrives();
        } catch (Exception ex) {
            SetStatus("Cannot list drives: " + ex.Message, GameTheme.DangerColor);
            return;
        }

        foreach (DriveInfo drive in drives) {
            if (!drive.IsReady) {
                continue;
            }

            AddFolderButton(drive.Name, drive.RootDirectory.FullName);
        }

        if (_listScroll != null) {
            _listScroll.verticalNormalizedPosition = 1f;
        }
    }

    private static void Navigate(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return;
        }

        path = path.Trim().Trim('"');

        try {
            if (File.Exists(path)) {
                path = Path.GetDirectoryName(path);
            }

            path = Path.GetFullPath(path);
        } catch (Exception ex) {
            SetStatus("Invalid path: " + ex.Message, GameTheme.DangerColor);
            return;
        }

        if (!Directory.Exists(path)) {
            SetStatus("Folder not found: " + path, GameTheme.DangerColor);
            return;
        }

        _browserPath = path;
        if (_pathInput != null) {
            _pathInput.text = path;
        }

        SetStatus("Open a subfolder, or use this folder.", GameTheme.MutedColor);
        PopulateList();
    }

    private static void PopulateList() {
        ClearList();

        string[] directories;
        try {
            directories = Directory.GetDirectories(_browserPath);
        } catch (Exception ex) {
            SetStatus("Cannot read this folder: " + ex.Message, GameTheme.DangerColor);
            return;
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);

        int count = 0;
        foreach (string directory in directories) {
            if (count >= 300) {
                break;
            }

            string name = Path.GetFileName(directory);
            bool isSkin = Directory.Exists(Path.Combine(directory, "Body")) ||
                          Directory.Exists(Path.Combine(directory, "Head")) ||
                          Directory.Exists(Path.Combine(directory, "WoundView"));

            AddFolderButton(name + (isSkin ? "  [skin]" : string.Empty), directory);
            count++;
        }

        if (count == 0) {
            TextMeshProUGUI empty = CreateText(_listContent, "Empty", "No subfolders.", 16,
                GameTheme.MutedColor, TextAlignmentOptions.MidlineLeft);
            LayoutElement element = empty.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 28f;
        }

        if (_listScroll != null) {
            _listScroll.verticalNormalizedPosition = 1f;
        }
    }

    private static void AddFolderButton(string label, string path) {
        Button button = CreateButton(_listContent, "Folder", label, GameTheme.TextColor, 16);

        LayoutElement element = button.gameObject.AddComponent<LayoutElement>();
        element.minHeight = 28f;
        element.preferredHeight = 28f;

        TMP_Text text = button.GetComponentInChildren<TMP_Text>();
        if (text != null) {
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.rectTransform.offsetMin = new Vector2(8f, 0f);
            text.rectTransform.offsetMax = new Vector2(-8f, 0f);
        }

        button.onClick.AddListener(() => Navigate(path));
    }

    private static void ClearList() {
        if (_listContent == null) {
            return;
        }

        for (int i = _listContent.childCount - 1; i >= 0; i--) {
            GameObject child = _listContent.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
    }

    private static void UseSelectedFolder() {
        if (string.IsNullOrEmpty(_pendingSlot)) {
            return;
        }

        if (!SkinLock.CanChangeLocal) {
            SetStatus("Skins locked by host.", GameTheme.WarningColor);
            return;
        }

        if (string.IsNullOrEmpty(_browserPath)) {
            SetStatus("Select a folder first.", GameTheme.DangerColor);
            return;
        }

        int movedHead;
        string prepareError;
        if (!SkinImporter.PrepareSkinFolder(_browserPath, _pendingSlot, out movedHead, out prepareError, _browseSpecies)) {
            SetStatus(prepareError, GameTheme.DangerColor);
            return;
        }

        SkinCheckResult check = SkinImporter.Check(_browserPath, _pendingSlot, _browseSpecies);
        if (!check.Ok) {
            SetStatus(string.Join("\n", check.Errors.Take(3)), GameTheme.DangerColor);
            return;
        }

        string backup;
        string error;
        if (!SkinImporter.Import(_browserPath, _pendingSlot, out backup, out error, _browseSpecies)) {
            SetStatus("Import failed: " + error, GameTheme.DangerColor);
            return;
        }

        string message = "";
        if (movedHead > 0) {
            message += "Moved " + movedHead + " Head sprites into Head.\n";
        }

        message += "Imported " + check.TotalCount + " sprites into " + SlotFolders.DisplayName(_pendingSlot) + ".";
        Color messageColor = GameTheme.SuccessColor;

        if (check.Warnings.Count > 0) {
            List<string> lines = new List<string>();
            lines.Add("Warnings (" + check.Warnings.Count + "):");
            foreach (string warning in check.Warnings) {
                lines.Add("Warning: " + warning);
            }

            message += "\n" + string.Join("\n", lines);
            messageColor = GameTheme.WarningColor;
        }

        SetStatus(message, messageColor);
        if (_browseSpecies == SpriteReplacerPlugin.ActiveSpecies) {
            SpriteReplacerPlugin.SwitchToSlot(_pendingSlot, true);
        } else if (SkinLock.CanChangeLocal) {
            SkinSettings.Remember(_browseSpecies, _pendingSlot);
        }
        BuildGrid();
    }

    private static void SetStatus(string text, Color color) {
        if (_statusText == null) {
            return;
        }

        _statusText.text = text;
        _statusText.color = color;

        if (_statusScroll != null) {
            _statusText.rectTransform.anchoredPosition = Vector2.zero;
            _statusScroll.verticalNormalizedPosition = 1f;
        }
    }

    internal static void ShowStatus(string text, Color color) {
        SetStatus(text, color);
    }

    internal static void CloseFolderBrowser() {
        if (_browserWindow != null) {
            UnityEngine.Object.Destroy(_browserWindow);
        }

        if (_browserBackdrop != null) {
            UnityEngine.Object.Destroy(_browserBackdrop);
        }

        _browserWindow = null;
        _browserBackdrop = null;
        _browserPath = null;
        _pendingSlot = null;
        _pathInput = null;
        _listContent = null;
        _listScroll = null;
        _statusText = null;
        _statusScroll = null;
    }

    private static TextMeshProUGUI CreateText(Transform parent, string name, string text, int size, Color color,
        TextAlignmentOptions alignment) {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.AddComponent<RectTransform>();

        TextMeshProUGUI label = obj.AddComponent<TextMeshProUGUI>();
        label.font = GameTheme.Font;
        label.text = text;
        label.fontSize = size;
        label.color = color;
        label.alignment = alignment;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        return label;
    }

    private static Button CreateButton(Transform parent, string name, string label, Color labelColor, int fontSize) {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.AddComponent<RectTransform>();

        Image image = obj.AddComponent<Image>();
        GameTheme.StyleButton(image);

        Button button = obj.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = GameTheme.ButtonColors;

        TextMeshProUGUI text = CreateText(obj.transform, "Label", label, fontSize, labelColor,
            TextAlignmentOptions.Center);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero);
        text.rectTransform.offsetMin = new Vector2(4f, 0f);
        text.rectTransform.offsetMax = new Vector2(-4f, 0f);
        text.enableWordWrapping = false;

        return button;
    }

    private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchoredPosition, Vector2 sizeDelta) {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }
}

// opens the link under the pointer in a TextMeshPro text. only https links.
internal sealed class LinkClickHandler : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler {
    internal TextMeshProUGUI Text;

    public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData) {
        if (Text == null) {
            return;
        }

        int index = TMP_TextUtilities.FindIntersectingLink(Text, eventData.position, eventData.pressEventCamera);
        if (index < 0) {
            return;
        }

        string url = Text.textInfo.linkInfo[index].GetLinkID();
        if (url.StartsWith("https://", StringComparison.Ordinal)) {
            Application.OpenURL(url);
        }
    }
}
