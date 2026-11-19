using System;
using System.Linq;
using CasualtiesTogetherUtils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

// Only presentation and button lifetime live here. Consent, sender authority,
// save policy and reconnection stay in LobbyHostTransfer.
internal sealed class HostTransferUI : CasualtiesTogetherSingleton {
    private enum View { Closed, Players, Offer, Progress, Notice }
    private static HostTransferUI instance;
    private View view;
    private bool atExit, copySave;
    private int page;
    private string offerToken, offerHost, error;
    private string noticeText;
    private LobbyHostTransfer.TransferMode offerMode;
    private GameObject canvas;
    private RectTransform panel;
    private TextMeshProUGUI statusLabel;
    private Button cancelButton, exitButton;
    private WorldGeneration buttonWorld;
    private double closeAt;
    private double nextButtonSearch;
    private int buttonSearches;
    internal static bool SelectingAtExit => instance != null && instance.view == View.Players && instance.atExit;
    internal static bool IsModalOpen => instance != null && instance.canvas != null && instance.canvas.activeInHierarchy;

    private void Awake() { instance = this; }
    private void OnDestroy() { if (canvas != null) Destroy(canvas); if (instance == this) instance = null; }
    private void OnGUI() {
        if (canvas != null && canvas.activeInHierarchy) UIBullshit.CheckCursorOverlap(new Rect(0, 0, Screen.width, Screen.height));
    }

    internal static void DrawLobbyAction() {
        if (!Net.IsRunningSteam || !Net.IsHost || !Util.IsInMainMenu()) return;
        bool oldEnabled = GUI.enabled;
        GUI.enabled &= !LobbyHostTransfer.Busy && ScavPlayer.AllPlayers.Count(p => p != null && !p.IsLocal) > 0;
        if (GUILayout.Button(LobbyHostTransfer.HasRequest ? "Host transfer status" : "Transfer host...")) Open(false);
        GUI.enabled = oldEnabled;
    }

    internal static void Open(bool layerExit) {
        if (instance == null) return;
        instance.atExit = layerExit; instance.page = 0; instance.error = null;
        instance.view = LobbyHostTransfer.HasRequest ? View.Progress : View.Players;
        instance.copySave = layerExit;
        instance.closeAt = Time.realtimeSinceStartupAsDouble + 60;
        instance.Render();
    }

    internal static void ShowOffer(string token, string host, LobbyHostTransfer.TransferMode mode) {
        if (instance == null) return;
        instance.offerToken = token; instance.offerHost = host; instance.offerMode = mode;
        instance.view = View.Offer; instance.error = null; instance.Render();
    }

    internal static void NotifyComplete(string message) {
        if (instance == null) return;
        instance.noticeText = message;
        instance.view = View.Notice; instance.Render();
        instance.closeAt = Time.realtimeSinceStartupAsDouble + 8;
    }

    private void Update() {
        if (view == View.Offer && !LobbyHostTransfer.IsCurrentOffer(offerToken)) Close();
        if (view == View.Players && ((!Net.IsRunningSteam || !Net.IsHost) || Time.realtimeSinceStartupAsDouble > closeAt ||
            (atExit ? !Util.IsInWorld() : !Util.IsInMainMenu()))) Close();
        if (view == View.Progress) {
            if (statusLabel != null) statusLabel.text = LobbyHostTransfer.Status;
            if (cancelButton != null) cancelButton.interactable = LobbyHostTransfer.CanCancel;
            if (!LobbyHostTransfer.HasRequest && !LobbyHostTransfer.Busy) Close();
        }
        if (view == View.Notice && Time.realtimeSinceStartupAsDouble > closeAt) Close();
        EnsureExitButton();
        if (exitButton != null) {
            bool supported = Net.IsRunningSteam && Net.IsHost && !SavesystemPatch.PreserveSave && buttonWorld != null && !buttonWorld.doPod && buttonWorld.biomeDepth != 10;
            // Keep the third slot stable after adding it; unsupported contexts
            // disable the button rather than moving the native controls around.
            exitButton.interactable = supported && !LobbyHostTransfer.Busy && ScavPlayer.AllPlayers.Count(p => p != null && !p.IsLocal) > 0;
        }
    }

    private void Close() { view = View.Closed; if (canvas != null) canvas.SetActive(false); }
    private void Try(Action action) {
        try { action(); } catch (Exception ex) { error = ex.Message; Render(); }
    }

    private void Render() {
        if (UIManager.template_button == null || UIManager.template_label == null) return;
        if (canvas != null) Destroy(canvas);
        canvas = new GameObject("Together Host Transfer", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvas.transform.SetParent(transform, false);
        var uiCanvas = canvas.GetComponent<Canvas>(); uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay; uiCanvas.sortingOrder = 32000;
        var scaler = canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
        var shade = new GameObject("Input shield", typeof(RectTransform), typeof(Image)); shade.transform.SetParent(canvas.transform, false);
        var shadeRect = (RectTransform)shade.transform; shadeRect.anchorMin = Vector2.zero; shadeRect.anchorMax = Vector2.one; shadeRect.offsetMin = shadeRect.offsetMax = Vector2.zero;
        shade.GetComponent<Image>().color = new Color(0, 0, 0, 0.78f);
        var block = UIManager.CreateBlock(canvas.transform, "Transfer host dialog", new Vector2(760, view == View.Offer ? 790 : 710));
        panel = (RectTransform)block.transform; panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f); panel.anchoredPosition = Vector2.zero;
        string title = view == View.Offer ? "Host transfer request" : view == View.Players ? (atExit ? "Transfer host and continue" : "Transfer lobby host") : "Host transfer";
        Label(title, 22, 52, 30);
        if (view == View.Players) RenderPlayers();
        else if (view == View.Offer) {
            Label(offerHost + " would like you to become the host.", 87, 74, 25);
            Label(LobbyHostTransfer.OfferWarning(offerMode), 166, 350, 20);
            AddButton(offerMode == LobbyHostTransfer.TransferMode.LobbyOnly ? "Accept hosting" : "Accept hosting and replace active save", 533, () => Try(() => { LobbyHostTransfer.AnswerOffer(offerToken, true); Close(); }));
            AddButton("Decline", 599, () => Try(() => { LobbyHostTransfer.AnswerOffer(offerToken, false); Close(); }));
        }
        else if (view == View.Progress) {
            statusLabel = Label(LobbyHostTransfer.Status, 100, 180, 27);
            Label("Keep your game open until the transfer completes. Every player must reconnect before the expedition can resume.", 302, 120, 23);
            cancelButton = AddButton("Cancel transfer", 443, () => Try(() => { LobbyHostTransfer.Cancel(); Close(); }));
            AddButton("Close this window", 509, Close);
        }
        else {
            Label(noticeText, 125, 300, 28);
            AddButton("OK", 507, Close);
        }
        if (!string.IsNullOrEmpty(error)) Label(error, view == View.Offer ? 675 : 628, 58, 21);
    }

    private void RenderPlayers() {
        if (atExit) Label("Choose the next host. Once they accept, this layer will continue and the expedition will resume automatically.", 80, 90, 23);
        else {
            AddButton((copySave ? "[X] " : "[ ] ") + "Send your MP save file to them?", 82, () => { copySave = !copySave; Render(); }).interactable = LobbyHostTransfer.HasSavedExpedition;
            Label(copySave ? "The recipient must accept replacing their active multiplayer save. A backup is kept."
                : "Transfer hosting only. The new host keeps their own saved expedition.", 150, 70, 22);
        }
        var players = ScavPlayer.AllPlayers.Where(p => p != null && !p.IsLocal && p.SteamId != 0).OrderBy(p => p.playerName).ToArray();
        int pages = Math.Max(1, (players.Length + 3) / 4); page = Math.Min(page, pages - 1);
        for (int i = 0; i < 4 && page * 4 + i < players.Length; i++) {
            ulong steam = players[page * 4 + i].SteamId;
            string name = players[page * 4 + i].playerName;
            AddButton("Transfer to " + name, 232 + i * 61, () => Try(() => {
                var player = ScavPlayer.AllPlayers.FirstOrDefault(p => p != null && p.SteamId == steam);
                var mode = atExit ? LobbyHostTransfer.TransferMode.NextLayer : copySave ? LobbyHostTransfer.TransferMode.LobbyWithSave : LobbyHostTransfer.TransferMode.LobbyOnly;
                LobbyHostTransfer.Request(player, mode); view = View.Progress; Render();
            }));
        }
        if (players.Length == 0) Label("No other players are connected.", 250, 80, 25);
        if (pages > 1) AddButton($"Next players ({page + 1}/{pages})", 480, () => { page = (page + 1) % pages; Render(); });
        AddButton("Back", 548, Close);
    }

    private TextMeshProUGUI Label(string text, float top, float height, float size) {
        var label = UIManager.CreateLabel(panel, text); label.richText = false; label.fontSize = size;
        label.enableAutoSizing = false; label.enableWordWrapping = true; label.alignment = TextAlignmentOptions.TopLeft;
        Place(label.rectTransform, top, height); return label;
    }
    private Button AddButton(string text, float top, Action action) {
        var button = UIManager.CreateButton(panel, text, null);
        button.onClick = new Button.ButtonClickedEvent(); button.onClick.AddListener(() => action());
        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) { label.text = text; label.richText = false; label.fontSize = 23; label.enableAutoSizing = false; }
        foreach (var localizer in button.GetComponentsInChildren<UILocalizer>(true)) Destroy(localizer);
        Place((RectTransform)button.transform, top, 51); return button;
    }
    private static void Place(RectTransform rect, float top, float height) {
        rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 1);
        rect.sizeDelta = new Vector2(-48, height); rect.anchoredPosition = new Vector2(0, -top);
    }

    private void EnsureExitButton() {
        var world = WorldGeneration.world;
        if (world == null) { buttonWorld = null; exitButton = null; buttonSearches = 0; return; }
        if (buttonWorld != world) { buttonWorld = world; exitButton = null; buttonSearches = 0; nextButtonSearch = 0; }
        if (buttonWorld == world && exitButton != null) return;
        if (world.savePanel == null || !Net.IsHost || !Net.IsRunningSteam) return;
        if (Time.realtimeSinceStartupAsDouble < nextButtonSearch || buttonSearches >= 20) return;
        nextButtonSearch = Time.realtimeSinceStartupAsDouble + 0.5; buttonSearches++;
        exitButton = AttachExitButton(world.savePanel);
        if (exitButton == null && buttonSearches == 20) Plugin.Logger.LogWarning("[LobbyTransfer] Could not locate native save/continue buttons; layer-exit host transfer is unavailable.");
    }

    internal static Button AttachExitButton(GameObject savePanel) {
        var existing = savePanel.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "Together_TransferHostAtExit");
        if (existing != null) return existing;
        var buttons = savePanel.GetComponentsInChildren<Button>(true);
        Button continuation = buttons.FirstOrDefault(b => HasAction(b, "ContinueRun"));
        Button save = buttons.FirstOrDefault(b => HasAction(b, "SaveAndExit"));
        if (continuation == null || save == null) return null;
        var added = UIManager.CreateButton(continuation.transform.parent, "Transfer host...", null);
        added.name = "Together_TransferHostAtExit";
        added.onClick = new Button.ButtonClickedEvent(); added.onClick.AddListener(() => Open(true));
        var rt = (RectTransform)added.transform;
        var native = (RectTransform)continuation.transform;
        rt.anchorMin = native.anchorMin; rt.anchorMax = native.anchorMax; rt.pivot = native.pivot; rt.sizeDelta = native.sizeDelta;
        rt.anchoredPosition = native.anchoredPosition; rt.localScale = native.localScale;
        // Native save/continue controls share a panel. Respect a layout group if
        // present; otherwise split their existing two slots into three equal slots.
        if (continuation.transform.parent == save.transform.parent && continuation.transform.parent.GetComponent<LayoutGroup>() == null)
            ArrangeExitButtons((RectTransform)save.transform, native, rt);
        return added;
    }

    private static bool HasAction(Button button, string method) {
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            if (button.onClick.GetPersistentMethodName(i) == method) return true;
        return false;
    }

    internal static void ArrangeExitButtons(RectTransform save, RectTransform continuation, RectTransform transfer) {
        bool horizontal = Mathf.Abs(save.anchoredPosition.x - continuation.anchoredPosition.x) >= Mathf.Abs(save.anchoredPosition.y - continuation.anchoredPosition.y);
        var ordered = new[] { save, continuation }.OrderBy(r => horizontal ? r.anchoredPosition.x : -r.anchoredPosition.y).ToArray();
        float extent = horizontal ? Mathf.Abs(ordered[1].anchoredPosition.x - ordered[0].anchoredPosition.x) + ordered[0].rect.width
            : Mathf.Abs(ordered[1].anchoredPosition.y - ordered[0].anchoredPosition.y) + ordered[0].rect.height;
        float size = (extent - 16) / 3f;
        float start = horizontal ? ordered[0].anchoredPosition.x - ordered[0].rect.width * ordered[0].pivot.x
            : ordered[0].anchoredPosition.y + ordered[0].rect.height * (1 - ordered[0].pivot.y);
        var all = new[] { ordered[0], ordered[1], transfer };
        for (int i = 0; i < all.Length; i++) {
            all[i].SetSizeWithCurrentAnchors(horizontal ? RectTransform.Axis.Horizontal : RectTransform.Axis.Vertical, size);
            var pos = all[i].anchoredPosition;
            if (horizontal) pos.x = start + i * (size + 8) + size * all[i].pivot.x;
            else pos.y = start - i * (size + 8) - size * (1 - all[i].pivot.y);
            all[i].anchoredPosition = pos;
        }
    }
}
