using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// the row goes on after the game fills the tab, and comes off when another tab opens.
internal static class BugReportSettings {
    private const string GitHubUrl =
        "https://github.com/dogederp/CasultiesUnknown_Player-sprite-replacer/issues";
    private const string NexusUrl =
        "https://www.nexusmods.com/scavprototype/mods/15?tab=bugs";

    private static GameObject _row;
    private static int _category = int.MinValue;

    internal static void Remove() {
        if (_row != null) {
            UnityEngine.Object.Destroy(_row);
        }

        _row = null;
    }

    internal static void AddIfOurCategory(int category) {
        Remove();
        if (category != CategoryIndex()) {
            return;
        }

        SettingsMenu menu = SettingsMenu.instance;
        if (menu == null || menu.content == null) {
            return;
        }

        RectTransform content = menu.content;
        float oldHeight = Mathf.Abs(content.sizeDelta.y);
        float width = content.rect.width;
        if (width <= 1f) {
            width = content.sizeDelta.x;
        }

        if (width <= 1f) {
            width = 700f;
        }

        const float height = 96f;
        GameObject row = new GameObject("SpriteReplacerBugReport");
        row.transform.SetParent(content, false);
        RectTransform rect = row.AddComponent<RectTransform>();
        SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f), new Vector2(0f, -(oldHeight + 12f)),
            new Vector2(width, height));

        Image background = row.AddComponent<Image>();
        background.color = GameTheme.CardColor;

        Outline outline = row.AddComponent<Outline>();
        outline.effectColor = GameTheme.BorderColor;
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = false;

        TextMeshProUGUI label = MakeText(row.transform,
            "Found a bug? Please report it.", 16, GameTheme.TextColor,
            TextAlignmentOptions.MidlineLeft);
        SetRect(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 1f), new Vector2(14f, -8f),
            new Vector2(width - 28f, 24f));

        Button github = MakeButton(row.transform, "GitHub issues", 14f, -58f);
        github.onClick.AddListener(() => Application.OpenURL(GitHubUrl));

        Button nexus = MakeButton(row.transform, "Nexus bugs", 246f, -58f);
        nexus.onClick.AddListener(() => Application.OpenURL(NexusUrl));

        content.sizeDelta = new Vector2(content.sizeDelta.x, oldHeight + height + 20f);
        _row = row;
    }

    private static int CategoryIndex() {
        if (_category != int.MinValue) {
            return _category;
        }

        // set once CUCoreLib accepts the Skin Replacer category.
        int index = CoreLibIntegration.BugReportCategoryIndex;
        if (index < 0) {
            return -1;
        }

        _category = index;
        return _category;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string value, int size,
        Color color, TextAlignmentOptions alignment) {
        GameObject obj = new GameObject("Label");
        obj.transform.SetParent(parent, false);
        obj.AddComponent<RectTransform>();

        TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
        text.font = GameTheme.Font;
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private static Button MakeButton(Transform parent, string label, float x, float y) {
        GameObject obj = new GameObject("Button");
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.AddComponent<RectTransform>();
        SetRect(rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 1f), new Vector2(x, y), new Vector2(220f, 30f));

        Image image = obj.AddComponent<Image>();
        GameTheme.StyleButton(image);

        Button button = obj.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = GameTheme.ButtonColors;

        TextMeshProUGUI text = MakeText(obj.transform, label, 14, GameTheme.TextColor,
            TextAlignmentOptions.Center);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one,
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        return button;
    }

    private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 pivot, Vector2 position, Vector2 size) {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }
}