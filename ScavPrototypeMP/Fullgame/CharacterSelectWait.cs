#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

// flow:
//   run presets: Next stays stock -> species select -> Ready up [n/max] -> host starts
//   continue / LoadRun: skip species select, seed from saves, host loads immediately
// mid-run late join (no save): CLIENT_LateJoinCharSelect -> solo Join -> spawn -> StartGameAnnouncement
public static class CharacterSelectWait {
    public static bool Waiting { get; private set; }
    public static bool LocalConfirmed { get; private set; }
    public static int ReadyCount { get; private set; }
    public static int MaxCount { get; private set; }
    // client mid-run pick before WaitLoad
    public static bool LateJoinPicking { get; private set; }

    const float ProceedDelaySeconds = 1f;

    static readonly Dictionary<knetid, byte> _selections = new Dictionary<knetid, byte>();
    static readonly HashSet<knetid> _pendingLateJoin = new HashSet<knetid>();
    static bool _pendingLoadSave;
    static bool _pendingTutorial;
    // title Tutorial button opens run settings. Back must close it again.
    static bool _openedRunSettings;
    // leftover protocol bit for CLIENT_CharSelectWait (new-run gate never locks)
    static bool _speciesLocked;
    static byte _lockedSpecies;
    static readonly List<ReadyButton> _readyButtons = new List<ReadyButton>();
    static readonly List<GameObject> _hiddenRunRulePanels = new List<GameObject>();
    static int _proceedToken;
    static bool _netHooks;
    static bool _uiHooks;
    static Button _charSelectStart;
    static Button _presetsNext;
    static Button _charSelectBack;

    public static bool SpeciesLocked => Waiting && _speciesLocked;

    struct ReadyButton {
        public Button button;
        public TextMeshProUGUI label;
        public string originalText;
    }

    static void EnsureNetHooks() {
        if (_netHooks) {
            return;
        }
        _netHooks = true;
        ScavPlayer.OnPlayerJoined += OnLobbyPlayerJoined;
        ScavPlayer.OnPlayerLeft += OnLobbyPlayerLeft;
    }

    public static void EnsureUiHooks(PreRunScript prs) {
        if (prs == null || _uiHooks) {
            return;
        }
        _uiHooks = true;

        Transform root = prs.runSettingsScreen != null ? prs.runSettingsScreen.transform : prs.transform;
        Transform presets = FindDeepChild(root, "Presets");
        Transform charSelect = FindCharSelectRoot(prs) ?? FindDeepChild(root, "CharSelect");

        if (presets != null) {
            Transform nextTf = FindDeepChild(presets, "Start");
            if (nextTf != null) {
                _presetsNext = nextTf.GetComponent<Button>();
                if (_presetsNext != null) {
                    _presetsNext.onClick.AddListener(OnPresetsNextClicked);
                }
            }
        }

        if (charSelect != null) {
            Transform startTf = FindDeepChild(charSelect, "Start");
            // prefer Bottom/Start over any other Start under CharSelect
            Transform bottom = FindDeepChild(charSelect, "Bottom");
            if (bottom != null) {
                Transform bottomStart = FindDeepChild(bottom, "Start");
                if (bottomStart != null) {
                    startTf = bottomStart;
                }
            }
            if (startTf != null) {
                // cache only - StartRun Prefix owns ready toggles (avoid double-fire with AddListener)
                _charSelectStart = startTf.GetComponent<Button>();
            }
            Transform backTf = FindDeepChild(charSelect, "Back");
            if (backTf != null) {
                _charSelectBack = backTf.GetComponent<Button>();
                if (_charSelectBack != null) {
                    _charSelectBack.onClick.AddListener(OnCharSelectBackClicked);
                }
            }
        }

        log.warn($"CharacterSelectWait UI hooks: next={_presetsNext != null} start={_charSelectStart != null} back={_charSelectBack != null}");
    }

    static void OnLobbyPlayerJoined(ScavPlayer plr) {
        if (!Net.IsServer || !Waiting || plr == null) {
            return;
        }
        RefreshCountsFromLobby();
        if (!plr.IsHost) {
            SendWaitBeginTo(plr);
        }
        BroadcastProgress();
        CancelProceed();
    }

    static void OnLobbyPlayerLeft(ScavPlayer plr) {
        if (plr != null) {
            _pendingLateJoin.Remove(plr.playerId);
        }
        if (!Net.IsServer || !Waiting) {
            return;
        }
        if (plr != null) {
            _selections.Remove(plr.playerId);
        }
        RefreshCountsFromLobby();
        BroadcastProgress();
        if (ReadyCount >= MaxCount && MaxCount > 0 && ReadyCount > 0) {
            ScheduleProceed();
        } else {
            CancelProceed();
        }
    }

    public static void Server_MarkPendingLateJoin(knetid clientId) {
        if (!Net.IsServer) {
            return;
        }
        _pendingLateJoin.Add(clientId);
    }

    public static bool IsPendingLateJoin(knetid clientId) {
        return _pendingLateJoin.Contains(clientId);
    }

    // checkpoint file, else in-memory leave state
    public static bool TryResolveSavedSpecies(ScavPlayer plr, out byte chr) {
        if (TryReadSavedCharacter(plr, out chr)) {
            return true;
        }
        if (plr == null) {
            return false;
        }
        if (ServerMain.Server_LastPlayerStates.TryGetValue(plr.GetPersistentId(), out PlayerSavedState st)) {
            chr = (byte)CharacterSpawn.ClampChar(st.charType);
            return true;
        }
        chr = 0;
        return false;
    }

    public static void Server_CompleteLateJoin(knetid clientId, byte character) {
        if (!Net.IsServer) {
            return;
        }
        if (!_pendingLateJoin.Remove(clientId)) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null) {
            return;
        }
        plr.selectedCharacter = (byte)CharacterSpawn.ClampChar(character);
        plr.CreateCharacter();

        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.StartGameAnnouncement);
        writer.Put(ServerMain.LastStartGameAnnouncementPacket);
        writer.Put(WorldMP.CompileRunSettings(), true);
        AchievementBreakSync.WriteAnnouncementFlag(writer);
        Net.Server_SendToClientsVeryReliable(writer, new List<knetid> { clientId });
        ServerMain.Server_AnnounceSeed(new List<knetid> { clientId });
        Multiplayer.DoMultiplayerStatusMessageLog($"{plr.playerName} joining as species {plr.selectedCharacter}...");
    }

    // pack every player's Ready/join species so late joiners' loading sprites aren't all local.
    public static void WriteSpeciesRoster(LiteNetLib.Utils.NetDataWriter writer) {
        if (writer == null) {
            return;
        }
        // selectedCharacter only. body/sync var can be unset in the lobby and must not abort announce.
        List<knetid> ids = new List<knetid>();
        List<byte> chrs = new List<byte>();
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null) {
                continue;
            }
            ids.Add(plr.playerId);
            chrs.Add((byte)CharacterSpawn.ClampChar(plr.selectedCharacter));
        }
        int n = Mathf.Min(ids.Count, 255);
        writer.Put((byte)n);
        for (int i = 0; i < n; i++) {
            writer.Put(ids[i]);
            writer.Put(chrs[i]);
        }
    }

    public static void ReadSpeciesRoster(LiteNetLib.Utils.NetDataReader reader) {
        if (reader == null || reader.EndOfData) {
            return;
        }
        reader.Get(out byte count);
        for (int i = 0; i < count; i++) {
            if (reader.EndOfData) {
                break;
            }
            reader.Get(out knetid id);
            reader.Get(out byte chr);
            chr = (byte)CharacterSpawn.ClampChar(chr);
            if (ScavPlayer.TryGetPlayerFromClientId(id, out ScavPlayer plr) && plr != null) {
                plr.selectedCharacter = chr;
            }
        }
    }

    public static void Client_EnterLateJoinPick() {
        EnsureNetHooks();
        CancelProceed();
        LateJoinPicking = true;
        Waiting = true;
        LocalConfirmed = false;
        ReadyCount = 0;
        MaxCount = 1;
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
            EnsureUiHooks(PreRunScript.instance);
        }
        RestoreReadyButtons();
        ApplyLocalWaitUi();
        ForceCharSelectReadyVisual(PreRunScript.instance);
        Multiplayer.DoMultiplayerStatusMessageLog("Pick a species, then Join...");
    }

    public static bool ShouldBlockStartGameAnnouncement() {
        // still on pick UI - ignore stray announcement until Join completes
        return LateJoinPicking && !LocalConfirmed;
    }

    // next on presets -> enter species-select ready gate (new run)
    static void OnPresetsNextClicked() {
        if (!Net.IsRunning) {
            return;
        }
        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("Waiting for host to pick a run...");
            return;
        }
        if (!Net.IsServer) {
            return;
        }
        if (!Waiting) {
            OpenLobbyGate();
        }
    }

    // tutorial uses the species screen. only the host has to confirm.
    public static void HostBeginTutorialPick() {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        if (Waiting && _pendingTutorial) {
            return;
        }
        OpenLobbyGate(tutorial: true);
        if (UIMainMenu.IsOpen()) {
            UIMainMenu.SetOpen(false);
        }
    }

    static void OnCharSelectBackClicked() {
        if (LateJoinPicking) {
            // mid-run join: stuck on species until Join (no presets to return to)
            return;
        }
        if (!Waiting) {
            return;
        }
        // back to presets, or back to the title if Tutorial opened this screen.
        // host has to tell the lobby. a local Reset left them in the gate with no bodies.
        if (Net.IsServer) {
            AbortLobbyGate();
            return;
        }
        Reset();
        ReleaseMenuDim();
    }

    // tutorial / new-run gate was still open. drop it before a world load or a Back,
    // or CreateCharacter skips every lobby player and the fade stays up.
    public static void AbortLobbyGate() {
        if (!Net.IsServer || (!Waiting && !LateJoinPicking)) {
            return;
        }
        Reset();
        ReleaseMenuDim();
        BroadcastAbort();
        Multiplayer.DoMultiplayerStatusMessageLog("Left character select.");
    }

    public static void EnsureHostLobbyGate() {
        EnsureNetHooks();
        if (!Net.IsRunning || !Net.IsServer || !Waiting || !Util.IsInMainMenu()) {
            return;
        }
        int max = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        if (max != MaxCount) {
            RefreshCountsFromLobby();
            BroadcastProgress();
        }
    }

    // new-run only - Continue never opens this gate
    static void OpenLobbyGate(bool tutorial = false) {
        EnsureNetHooks();
        CancelProceed();
        RestoreReadyButtons();
        _pendingLoadSave = false;
        _pendingTutorial = tutorial;
        _speciesLocked = false;
        _selections.Clear();
        LocalConfirmed = false;
        Waiting = true;
        ReadyCount = 0;
        MaxCount = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        ApplyLocalWaitUi();
        BroadcastWaitBegin();
        BroadcastProgress();
        Multiplayer.DoMultiplayerStatusMessageLog(tutorial ? "Pick a character, then start the tutorial." : "Ready up to start...");
    }

    public static void EnforceLockedSpecies() {
        if (!_speciesLocked) {
            return;
        }
        PreRunScript.characterSelected = _lockedSpecies;
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = _lockedSpecies;
        }
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
        }
    }

    // continue: seed species from each player's save and load immediately (no char select).
    static void HostContinueDirectly() {
        EnsureNetHooks();
        CancelProceed();
        RestoreReadyButtons();
        RestoreRunRulePanels();
        SetCharBrowseInteractable(PreRunScript.instance, true);
        LateJoinPicking = false;
        LocalConfirmed = false;
        _speciesLocked = false;
        _pendingLoadSave = true;
        Waiting = true;
        SeedSavedCharacters();
        _selections.Clear();
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null) {
                continue;
            }
            byte chr = (byte)CharacterSpawn.ClampChar(plr.selectedCharacter);
            plr.selectedCharacter = chr;
            _selections[plr.playerId] = chr;
        }
        ReadyCount = _selections.Count;
        MaxCount = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        HostProceed();
    }

    public static void HostBeginNewRun(bool loadSave) {
        if (!LobbyHostTransfer.AllowRun(loadSave)) return;
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        if (loadSave) {
            HostContinueDirectly();
            return;
        }
        if (Waiting) {
            return;
        }
        OpenLobbyGate();
    }

    public static void HostBeginLoadRun() {
        HostContinueDirectly();
    }

    static void SeedSavedCharacters() {
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (TryReadSavedCharacter(plr, out byte chr)) {
                plr.selectedCharacter = chr;
                if (plr.IsLocal) {
                    PreRunScript.characterSelected = chr;
                }
            }
        }
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
        }
    }

    public static bool TryReadSavedCharacter(ScavPlayer plr, out byte chr) {
        chr = 0;
        if (plr == null || SavesystemPatch.PreserveSave) {
            return false;
        }
        try {
            string folder = Path.Combine(SavesystemPatch.mpsavefolder, plr.GetPersistentId());
            string path = Path.Combine(folder, "save.sv");
            if (!File.Exists(path)) {
                return false;
            }
            JObject jObject = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            if (jObject["character"] != null) {
                chr = (byte)CharacterSpawn.ClampChar((int)jObject["character"]);
                return true;
            }
            if (jObject["body"] != null && jObject["body"]["charType"] != null) {
                chr = (byte)CharacterSpawn.ClampChar((int)jObject["body"]["charType"]);
                return true;
            }
        } catch (System.Exception e) {
            log.warn($"TryReadSavedCharacter failed for {plr}: {e.Message}");
        }
        return false;
    }

    public static void Reset() {
        CancelProceed();
        Waiting = false;
        LateJoinPicking = false;
        LocalConfirmed = false;
        ReadyCount = 0;
        MaxCount = 0;
        _pendingLoadSave = false;
        _pendingTutorial = false;
        _speciesLocked = false;
        _selections.Clear();
        RestoreReadyButtons();
        RestoreRunRulePanels();
        HideCharSelect();
        if (_openedRunSettings) {
            PreRunScript prs = PreRunScript.instance;
            if (prs != null && prs.runSettingsScreen != null) {
                prs.runSettingsScreen.SetActive(false);
            }
            _openedRunSettings = false;
        }
        SetCharBrowseInteractable(PreRunScript.instance, true);
    }

    // startRun -> ready gate; LoadRun / Continue -> direct load
    public static void OnStartOrLoadClicked(bool loadSave) {
        if (!LobbyHostTransfer.AllowRun(loadSave)) return;
        if (!Net.IsRunning) {
            return;
        }

        if (loadSave) {
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog("Waiting for host to continue...");
                return;
            }
            if (!Net.IsServer) {
                return;
            }
            HostContinueDirectly();
            return;
        }

        if (Waiting) {
            if (LateJoinPicking) {
                // mid-run: Start = Join (confirm species once)
                SetLocalReady(true);
                return;
            }
            // charSelect Bottom/Start is always StartRun - ready toggle only
            ToggleLocalReady();
            return;
        }

        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("Waiting for host to pick a run...");
            return;
        }
        if (!Net.IsServer) {
            return;
        }
        OpenLobbyGate();
    }

    public static void OnLocalCharacterBrowsed(int character) {
        if (!Net.IsRunning || !Waiting) {
            return;
        }
        if (_speciesLocked) {
            EnforceLockedSpecies();
            return;
        }
        byte ch = (byte)CharacterSpawn.ClampChar(character);
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = ch;
        }
        PreRunScript.characterSelected = ch;
        // always push the pick - waiting until Ready left host on Expie when the body
        // already existed (early spawn / direct-IP) while the client UI showed Dune.
        SendOrRecordReady(LocalConfirmed, ch);
    }

    public static void ToggleLocalReady() {
        if (!Net.IsRunning || !Waiting) {
            return;
        }
        SetLocalReady(!LocalConfirmed);
    }

    public static void SetLocalReady(bool ready) {
        if (!Net.IsRunning || !Waiting) {
            return;
        }
        if (_speciesLocked) {
            EnforceLockedSpecies();
        }
        byte ch = (byte)CharacterSpawn.ClampChar(PreRunScript.characterSelected);
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = ch;
        }
        PreRunScript.characterSelected = ch;
        // record first so ReadyCount updates before the label paints
        SendOrRecordReady(ready, ch);
        LocalConfirmed = ready;
        ForceCharSelectReadyVisual(PreRunScript.instance);
    }

    static void SendOrRecordReady(bool ready, byte ch) {
        if (Net.IsServer) {
            ScavPlayer host = ScavPlayer.LocalPlayer;
            if (host == null || !ScavPlayer.ClientIdToPlayerDict.ContainsKey(host.playerId)) {
                Together.log.warn($"[CharSelect] host ready failed: local={(host != null)} id={(host != null ? host.playerId.ToString() : "null")} lobby={ScavPlayer.ClientIdToPlayerDict.Count}");
                return;
            }
            Server_SetReady(host.playerId, ready, ch);
        } else {
            ClientMain.Client_SendCharacterReady(ready, ch);
        }
    }

    public static void Client_EnterWait(int ready, int max, int suggestedChar, bool speciesLocked = false) {
        EnsureNetHooks();
        CancelProceed();
        LateJoinPicking = false;
        Waiting = true;
        LocalConfirmed = false;
        ReadyCount = ready;
        MaxCount = Mathf.Max(1, max);
        int chr = CharacterSpawn.ClampChar(suggestedChar);
        _pendingLoadSave = speciesLocked;
        _speciesLocked = speciesLocked;
        _lockedSpecies = (byte)chr;
        PreRunScript.characterSelected = chr;
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = (byte)chr;
        }
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
            EnsureUiHooks(PreRunScript.instance);
        }
        RestoreReadyButtons();
        ApplyLocalWaitUi();
    }

    public static void Client_UpdateProgress(int ready, int max) {
        // host backed out of the gate. a normal progress packet is never negative.
        if (ready < 0) {
            Reset();
            ReleaseMenuDim();
            return;
        }
        ReadyCount = ready;
        MaxCount = Mathf.Max(1, max);
        ForceCharSelectReadyVisual(PreRunScript.instance);
    }

    public static void TickUi() {
        if (!Net.IsRunning || !Util.IsInMainMenu()) {
            return;
        }
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        EnsureUiHooks(prs);

        if (LateJoinPicking) {
            ApplyLocalWaitUi();
            ForceCharSelectReadyVisual(prs);
            return;
        }

        // stock Back can hide the page without our listener. Waiting would stay set
        // and the next world would spawn nobody.
        if (Net.IsServer && Waiting && Util.IsInMainMenu() && !IsSpeciesSelectPageVisible(prs)) {
            AbortLobbyGate();
            return;
        }

        // stock Next may show species select without calling StartRun - catch that page
        if (Net.IsServer && !Waiting && IsSpeciesSelectPageVisible(prs)) {
            OpenLobbyGate();
            return;
        }

        if (!Waiting) {
            RestoreCharSelectStartStock(prs);
            return;
        }

        EnsureHostLobbyGate();
        SetCharBrowseInteractable(prs, !_speciesLocked);
        if (_speciesLocked) {
            EnforceLockedSpecies();
        }
        ForceCharSelectReadyVisual(prs);
    }

    static bool IsSpeciesSelectPageVisible(PreRunScript prs) {
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect == null || !charSelect.gameObject.activeInHierarchy) {
            return false;
        }
        Transform presets = null;
        if (prs.runSettingsScreen != null) {
            presets = FindDeepChild(prs.runSettingsScreen.transform, "Presets");
        }
        // species page = char select up and presets put away
        if (presets != null && presets.gameObject.activeInHierarchy) {
            return false;
        }
        Transform bottom = FindDeepChild(charSelect, "Bottom");
        return bottom != null && bottom.gameObject.activeInHierarchy;
    }

    public static void Server_SetReady(knetid clientId, bool ready, byte character) {
        if (!Net.IsServer || !Waiting) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)) {
            return;
        }
        character = (byte)CharacterSpawn.ClampChar(character);
        // continue: ignore portrait picks - keep the save species
        if (_pendingLoadSave && TryReadSavedCharacter(plr, out byte savedChr)) {
            character = savedChr;
        }
        // always stamp species from browse/ready so host mesh tracks the portrait.
        plr.selectedCharacter = character;
        if (plr.body != null) {
            NetBody nb = plr.netBody;
            if (nb != null && nb.syncBody != null) {
                CharacterSpawn.ApplySelectedToSyncBody(nb.syncBody, plr);
            }
            CharacterSpawn.ServerRebuildBodyIfSpeciesMismatch(plr);
        }
        if (ready) {
            _selections[clientId] = character;
        } else {
            _selections.Remove(clientId);
        }
        RefreshCountsFromLobby();
        BroadcastProgress();
        // host confirm loads the tutorial. other players do not ready.
        if (_pendingTutorial && plr.IsHost && ready) {
            HostProceed();
            return;
        }
        if (ReadyCount >= MaxCount && MaxCount > 0) {
            ScheduleProceed();
        } else {
            CancelProceed();
        }
    }

    static void RefreshCountsFromLobby() {
        MaxCount = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        List<knetid> stale = null;
        foreach (KeyValuePair<knetid, byte> kv in _selections) {
            if (!ScavPlayer.ClientIdToPlayerDict.ContainsKey(kv.Key)) {
                if (stale == null) {
                    stale = new List<knetid>();
                }
                stale.Add(kv.Key);
            }
        }
        if (stale != null) {
            for (int i = 0; i < stale.Count; i++) {
                _selections.Remove(stale[i]);
            }
        }
        ReadyCount = _selections.Count;
    }

    static void ScheduleProceed() {
        CancelProceed();
        int token = _proceedToken;
        Util.StartCoroutine(ProceedAfterDelay(token));
        Multiplayer.DoMultiplayerStatusMessageLog("All ready - starting...");
    }

    static void CancelProceed() {
        _proceedToken++;
    }

    static IEnumerator ProceedAfterDelay(int token) {
        float t = 0f;
        while (t < ProceedDelaySeconds) {
            if (token != _proceedToken || !Waiting || !Net.IsServer) {
                yield break;
            }
            if (ReadyCount < MaxCount || MaxCount <= 0) {
                yield break;
            }
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (token != _proceedToken || !Waiting || !Net.IsServer) {
            yield break;
        }
        if (ReadyCount < MaxCount || MaxCount <= 0) {
            yield break;
        }
        HostProceed();
    }

    static void HostProceed() {
        if (LobbyHostTransfer.Busy) return;
        if (!Net.IsServer) {
            return;
        }
        bool loadSave = _pendingLoadSave;
        bool tutorial = _pendingTutorial;
        CancelProceed();
        Waiting = false;
        RestoreReadyButtons();
        RestoreRunRulePanels();
        Multiplayer.DoMultiplayerStatusMessageLog(tutorial ? "Starting tutorial..." : (loadSave ? "Continuing saved run..." : "Starting new run..."));

        // stamp ready picks onto every player BEFORE bodies spawn in the world
        foreach (KeyValuePair<knetid, byte> kv in _selections) {
            if (!ScavPlayer.TryGetPlayerFromClientId(kv.Key, out ScavPlayer plr) || plr == null) {
                continue;
            }
            byte chr = (byte)CharacterSpawn.ClampChar(kv.Value);
            plr.selectedCharacter = chr;
            if (plr.IsLocal) {
                PreRunScript.characterSelected = chr;
            }
            if (plr.body != null) {
                NetBody nb = plr.netBody;
                if (nb != null && nb.syncBody != null) {
                    CharacterSpawn.ApplySelectedToSyncBody(nb.syncBody, plr);
                }
                CharacterSpawn.ServerRebuildBodyIfSpeciesMismatch(plr);
            }
        }

        if (tutorial) {
            SaveSystem.loadedRun = false;
            SavesystemPatch.PendingLoadMpSave = false;
            WorldMP.LoadVanillaTutorialWorld();
        } else if (loadSave) {
            SaveSystem.loadedRun = true;
            SavesystemPatch.PendingLoadMpSave = true;
            WorldMP.LoadVanillaGeneratedWorld(true);
        } else {
            SaveSystem.loadedRun = false;
            SavesystemPatch.PendingLoadMpSave = false;
            SavesystemPatch.DeleteMPSave();
            if (PreRunScript.instance != null) {
                WorldGeneration.runSettings = PreRunScript.instance.runSettings;
            }
            WorldMP.LoadVanillaGeneratedWorld(false);
        }
        _selections.Clear();
        LocalConfirmed = false;
        ReadyCount = 0;
        MaxCount = 0;
        _pendingLoadSave = false;
        _pendingTutorial = false;
        _speciesLocked = false;
    }

    static void ReleaseMenuDim() {
        WorldMP.ReleaseTitleFade();
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        if (prs.cover != null) {
            Color c = prs.cover.color;
            c.a = 0f;
            prs.cover.color = c;
            prs.cover.raycastTarget = false;
        }
        try {
            Traverse.Create(prs).Field("startedLoading").SetValue(false);
        } catch {
        }
    }

    static void BroadcastAbort() {
        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CharSelectProgress);
        writer.Put(-1);
        writer.Put(0);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    static void BroadcastWaitBegin() {
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null || plr.IsHost) {
                continue;
            }
            SendWaitBeginTo(plr);
        }
    }

    static void SendWaitBeginTo(ScavPlayer plr) {
        if (plr == null) {
            return;
        }
        byte saved = 0;
        bool lockSpecies = _pendingLoadSave && TryReadSavedCharacter(plr, out saved);
        int suggested = lockSpecies ? saved : plr.selectedCharacter;
        if (lockSpecies) {
            plr.selectedCharacter = saved;
        }
        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CharSelectWait);
        writer.Put(ReadyCount);
        writer.Put(MaxCount);
        writer.Put(suggested);
        writer.Put(lockSpecies);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: new knetid[] { plr.playerId });
    }

    static void BroadcastProgress() {
        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CharSelectProgress);
        writer.Put(ReadyCount);
        writer.Put(MaxCount);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        ForceCharSelectReadyVisual(PreRunScript.instance);
    }

    static void ApplyLocalWaitUi() {
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        EnsureUiHooks(prs);
        ShowCharSelectHideRunRules(prs);
        if (prs.tutorialWarning != null) {
            prs.tutorialWarning.SetActive(false);
        }
        SetCharBrowseInteractable(prs, !_speciesLocked);
        if (_speciesLocked) {
            EnforceLockedSpecies();
        }
        ForceCharSelectReadyVisual(prs);
    }

    static void SetCharBrowseInteractable(PreRunScript prs, bool interactable) {
        if (prs == null || prs.charImages == null) {
            return;
        }
        for (int i = 0; i < prs.charImages.Count; i++) {
            if (prs.charImages[i] == null) {
                continue;
            }
            Button btn = prs.charImages[i].GetComponent<Button>();
            if (btn == null) {
                continue;
            }
            if (!interactable) {
                btn.interactable = false;
                continue;
            }
            // restore stock unlock gate
            if (i != 0 && PlayerPrefs.GetInt("wonchar" + (i - 1)) == 0) {
                btn.interactable = false;
            } else {
                btn.interactable = true;
            }
        }
    }

    static Transform FindDeepChild(Transform parent, string name) {
        if (parent == null) {
            return null;
        }
        if (parent.name == name) {
            return parent;
        }
        for (int i = 0; i < parent.childCount; i++) {
            Transform found = FindDeepChild(parent.GetChild(i), name);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    static Transform FindCharSelectRoot(PreRunScript prs) {
        if (prs == null) {
            return null;
        }
        if (prs.charImages != null) {
            for (int i = 0; i < prs.charImages.Count; i++) {
                if (prs.charImages[i] == null) {
                    continue;
                }
                Transform t = prs.charImages[i].transform;
                while (t != null) {
                    if (t.name == "CharSelect") {
                        return t;
                    }
                    if (prs.runSettingsScreen != null && t == prs.runSettingsScreen.transform) {
                        break;
                    }
                    t = t.parent;
                }
            }
        }
        if (prs.charSelectName != null) {
            Transform t = prs.charSelectName.transform;
            while (t != null) {
                if (t.name == "CharSelect") {
                    return t;
                }
                t = t.parent;
            }
        }
        if (prs.runSettingsScreen != null) {
            return FindDeepChild(prs.runSettingsScreen.transform, "CharSelect");
        }
        return null;
    }

    static void HideCharSelect() {
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect != null) {
            charSelect.gameObject.SetActive(false);
        }
    }

    static void ShowCharSelectHideRunRules(PreRunScript prs) {
        if (prs.runSettingsScreen != null) {
            if (!prs.runSettingsScreen.activeSelf) {
                _openedRunSettings = true;
            }
            prs.runSettingsScreen.SetActive(true);
        }
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect != null) {
            charSelect.gameObject.SetActive(true);
            Transform bottom = FindDeepChild(charSelect, "Bottom");
            if (bottom != null) {
                bottom.gameObject.SetActive(true);
            }
        }
        if (prs.runSettingsScreen == null) {
            return;
        }
        Transform root = prs.runSettingsScreen.transform;
        for (int i = 0; i < root.childCount; i++) {
            Transform child = root.GetChild(i);
            if (child == null) {
                continue;
            }
            if (charSelect != null && child == charSelect) {
                continue;
            }
            // presets + custom settings + outline (outline can eat raycasts over Start)
            if (child.name != "Presets" && child.name != "CustomSettings" && child.name != "SelectOutline") {
                continue;
            }
            if (child.gameObject.activeSelf) {
                child.gameObject.SetActive(false);
                if (!_hiddenRunRulePanels.Contains(child.gameObject)) {
                    _hiddenRunRulePanels.Add(child.gameObject);
                }
            }
        }
    }

    static void RestoreRunRulePanels() {
        for (int i = 0; i < _hiddenRunRulePanels.Count; i++) {
            if (_hiddenRunRulePanels[i] != null) {
                _hiddenRunRulePanels[i].SetActive(true);
            }
        }
        _hiddenRunRulePanels.Clear();
    }

    static Button FindCharSelectStartButton(PreRunScript prs) {
        if (_charSelectStart != null) {
            return _charSelectStart;
        }
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect == null) {
            return null;
        }
        Transform bottom = FindDeepChild(charSelect, "Bottom");
        if (bottom != null) {
            Transform startTf = FindDeepChild(bottom, "Start");
            if (startTf != null) {
                return startTf.GetComponent<Button>();
            }
        }
        Transform anyStart = FindDeepChild(charSelect, "Start");
        return anyStart != null ? anyStart.GetComponent<Button>() : null;
    }

    static void ForceCharSelectReadyVisual(PreRunScript prs) {
        if (prs == null) {
            return;
        }
        Button start = FindCharSelectStartButton(prs);
        if (start == null) {
            return;
        }
        if (!start.gameObject.activeSelf) {
            start.gameObject.SetActive(true);
        }
        if (start.transform.parent != null && !start.transform.parent.gameObject.activeSelf) {
            start.transform.parent.gameObject.SetActive(true);
        }

        RememberButton(start);
        start.enabled = true;
        start.interactable = true;

        // clear any canvas-group blocks on the chain
        Transform t = start.transform;
        while (t != null) {
            CanvasGroup cg = t.GetComponent<CanvasGroup>();
            if (cg != null) {
                cg.interactable = true;
                cg.blocksRaycasts = true;
                if (cg.alpha < 0.01f) {
                    cg.alpha = 1f;
                }
            }
            if (prs.runSettingsScreen != null && t == prs.runSettingsScreen.transform) {
                break;
            }
            t = t.parent;
        }

        string text = ReadyButtonText();
        TextMeshProUGUI[] labels = start.GetComponentsInChildren<TextMeshProUGUI>(true);
        for (int i = 0; i < labels.Length; i++) {
            if (labels[i] != null) {
                labels[i].text = text;
                labels[i].raycastTarget = false;
            }
        }
    }

    static void RestoreCharSelectStartStock(PreRunScript prs) {
        if (_readyButtons.Count > 0) {
            RestoreReadyButtons();
        }
        Button start = FindCharSelectStartButton(prs);
        if (start == null) {
            return;
        }
        start.enabled = true;
        start.interactable = true;
    }

    static void RememberButton(Button button) {
        if (button == null) {
            return;
        }
        for (int i = 0; i < _readyButtons.Count; i++) {
            if (_readyButtons[i].button == button) {
                return;
            }
        }
        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        _readyButtons.Add(new ReadyButton {
            button = button,
            label = label,
            originalText = label != null ? label.text : "Start",
        });
    }

    static string ReadyButtonText() {
        if (LateJoinPicking) {
            return LocalConfirmed ? "Joining..." : "Join";
        }
        if (_pendingTutorial) {
            return "Start";
        }
        if (LocalConfirmed) {
            return $"Ready [{ReadyCount}/{MaxCount}]";
        }
        return $"Ready up [{ReadyCount}/{MaxCount}]";
    }

    static void RestoreReadyButtons() {
        for (int i = 0; i < _readyButtons.Count; i++) {
            ReadyButton rb = _readyButtons[i];
            if (rb.label != null && rb.originalText != null) {
                rb.label.text = rb.originalText;
            }
            if (rb.button != null) {
                rb.button.enabled = true;
                rb.button.interactable = true;
            }
        }
        _readyButtons.Clear();
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SelectChar)]
    static void Server_SelectChar(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        bool ready = true;
        int ch;
        if (reader.AvailableBytes >= 5) {
            reader.Get(out ready);
            reader.Get(out ch);
        } else {
            reader.Get(out ch);
        }
        byte chr = (byte)Mathf.Clamp(ch, 0, 255);
        if (_pendingLateJoin.Contains(clientId)) {
            if (ready) {
                Server_CompleteLateJoin(clientId, chr);
            }
            return;
        }
        Server_SetReady(clientId, ready, chr);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CharSelectWait)]
    static void Client_CharSelectWait(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out int ready);
        reader.Get(out int max);
        int suggested = 0;
        if (reader.AvailableBytes >= 4) {
            reader.Get(out suggested);
        }
        bool speciesLocked = false;
        if (reader.AvailableBytes >= 1) {
            reader.Get(out speciesLocked);
        }
        Client_EnterWait(ready, max, suggested, speciesLocked);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CharSelectProgress)]
    static void Client_CharSelectProgress(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out int ready);
        reader.Get(out int max);
        Client_UpdateProgress(ready, max);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_LateJoinCharSelect)]
    static void Client_LateJoinCharSelect(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        Client_EnterLateJoinPick();
    }
}

[HarmonyPatch(typeof(PreRunScript), "Start")]
static class PreRunScript_Start_CharSelectWaitHooksPatch {
    static void Postfix(PreRunScript __instance) {
        if (!Net.IsRunning) {
            // still install; TickUi no-ops until net is up, hooks ready when lobby starts
        }
        CharacterSelectWait.EnsureUiHooks(__instance);
    }
}

[HarmonyPatch(typeof(PreRunScript), "Update")]
static class PreRunScript_Update_CharSelectWaitPatch {
    static void Postfix(PreRunScript __instance) {
        CharacterSelectWait.EnsureUiHooks(__instance);
        CharacterSelectWait.TickUi();
    }
}
#endif
