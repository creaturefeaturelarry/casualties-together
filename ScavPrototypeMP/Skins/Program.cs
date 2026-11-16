using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using Together;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#pragma warning disable BepInEx001 // together boots this type. a second plugin entry fights the chainloader.
// [BepInPlugin("com.yourname.spritereplacer", "Sprite Replacer", "2.0.0")]
// cUCoreLib is expected beside this mod; no BepInDependency (avoids cycle with CCL->CasualtiesMP).
public class SpriteReplacerPlugin : MonoBehaviour {
    public const int SlotCount = 9;
    public const int DefaultBackupCount = 5;
    public const int MaxBackupCount = 50;

    public static string SpritesPath => Path.Combine(Paths.PluginPath, "CustomSprites");

    internal static readonly string[] SpeciesNames = { "Exp", "Milky", "Dune" };

    // no local body yet counts as experiment. numpad and sync read this every change.
    internal static int ActiveSpecies {
        get {
            int chr = MpBody.LocalCharType;
            if (chr < 0 || chr > 2) {
                return 0;
            }

            return chr;
        }
    }

    internal static string SpeciesRoot(int charType) {
        if (charType < 0 || charType >= SpeciesNames.Length) {
            charType = 0;
        }

        return Path.Combine(SpritesPath, SpeciesNames[charType]);
    }

    internal static string SlotDirectory(string slot) {
        return SlotDirectory(slot, ActiveSpecies);
    }

    internal static string SlotDirectory(string slot, int species) {
        return Path.Combine(SpeciesRoot(species), slot);
    }

    internal static string BackupRoot => BackupRootFor(ActiveSpecies);

    internal static string BackupRootFor(int species) {
        return Path.Combine(SpeciesRoot(species), "backups");
    }

    // saved folder for a species tab. 0 experiment, 1 milky, 2 dune.
    internal static string FolderForSpecies(int species) {
        SkinSettings.EnsureLoaded();
        if (species == 1) {
            return SkinSettings.Folder(SkinSettings.milkySkin);
        }

        if (species == 2) {
            return SkinSettings.Folder(SkinSettings.duneSkin);
        }

        return SkinSettings.Folder(SkinSettings.experimentSkin);
    }
    public static Dictionary<string, Sprite> LoadedSprites = new Dictionary<string, Sprite>();
    internal static readonly HashSet<string> ColoredWoundSprites = new HashSet<string>();
    internal static ManualLogSource Log;

    private static string _activeSlot = SkinSettings.DefaultFolder;
    // folder currently on the body. null means the game sprites are showing.
    private static string _appliedFolder;
    // species that _appliedFolder was loaded from. slot names repeat per species.
    private static int _appliedSpecies = -1;
    // last cached folder that failed to load, so a missing slot is not retried every tick.
    private static string _syncFailed;
    private static int _syncFailedSpecies = -1;
    public static string ActiveSlot => _activeSlot;

    private static ConfigEntry<string> _activeSlotSetting;
    private static ConfigEntry<int> _backupCountSetting;
    private static ConfigEntry<bool> _shareSetting;
    private static ConfigEntry<bool> _downloadSetting;
    private static ConfigEntry<int> _cacheMbSetting;

    internal static int BackupCount =>
        _backupCountSetting == null ? DefaultBackupCount : Math.Max(0, _backupCountSetting.Value);

    internal static bool ShareSkins => _shareSetting == null || _shareSetting.Value;
    internal static bool DownloadSkins => _downloadSetting == null || _downloadSetting.Value;

    internal static int MaxSkinCacheMB =>
        _cacheMbSetting == null ? 64 : Math.Max(4, _cacheMbSetting.Value);

    private static readonly Dictionary<KeyCode, string> KeyMapping = new Dictionary<KeyCode, string>
    {
        // 0 is not a custom slot - it always loads CustomSprites/<species>/default.
        { KeyCode.Keypad0, SkinSettings.DefaultFolder },
        { KeyCode.Keypad1, SlotFolders.SlotId(1) },
        { KeyCode.Keypad2, SlotFolders.SlotId(2) },
        { KeyCode.Keypad3, SlotFolders.SlotId(3) },
        { KeyCode.Keypad4, SlotFolders.SlotId(4) },
        { KeyCode.Keypad5, SlotFolders.SlotId(5) },
        { KeyCode.Keypad6, SlotFolders.SlotId(6) },
        { KeyCode.Keypad7, SlotFolders.SlotId(7) },
        { KeyCode.Keypad8, SlotFolders.SlotId(8) },
        { KeyCode.Keypad9, SlotFolders.SlotId(9) }
    };

    private static bool _booted;

    // together calls this. bepinex does not construct the type.
    internal static void EnsureLoaded() {
        if (_booted || Log != null) {
            return;
        }

        GameObject go = new GameObject("SpriteReplacer");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.GetOrAddComponent<SpriteReplacerPlugin>();
    }

    private void Awake() {
        if (_booted) {
            return;
        }

        _booted = true;

        Log = BepInEx.Logging.Logger.CreateLogSource("Sprite Replacer");

        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) {
            if (string.Equals(loaded.GetName().Name, "body_sprite_replacer", StringComparison.Ordinal)) {
                Log.LogWarning("Standalone body_sprite_replacer.dll is also loaded. Disable that plugin. Together includes this skin replacer.");
                break;
            }
        }

        // opened here because this type is not a BepInEx plugin entry anymore.
        ConfigFile cfg = null;
        if (cfg == null) {
            cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, "com.yourname.spritereplacer.cfg"), false);
        }

        _activeSlotSetting = cfg.Bind("General", "ActiveSlot", SkinSettings.DefaultFolder,
            "Last folder applied. The Skins tab remembers one folder per species and wins over this. default is the game's Experiment art.");

        _backupCountSetting = cfg.Bind("General", "BackupCount", DefaultBackupCount,
            new ConfigDescription(
                "Number of backup folders to keep for each slot. Older backups are deleted. 0 keeps all backups.",
                new AcceptableValueRange<int>(0, MaxBackupCount)));

        _shareSetting = cfg.Bind("Multiplayer", "ShareSkins", true,
            "Share your selected skin with the other players.");

        _downloadSetting = cfg.Bind("Multiplayer", "DownloadSkins", true,
            "Download and apply the skins that other players share.");

        _cacheMbSetting = cfg.Bind("Multiplayer", "MaxSkinCacheMB", 64,
            new ConfigDescription(
                "Maximum disk space for the remote skin cache.",
                new AcceptableValueRange<int>(4, 512)));

        Directory.CreateDirectory(SpritesPath);
        MigrateLegacyIntoExp();
        EnsureCustomSpritesStructure();

        Harmony harmony = new Harmony("com.yourname.spritereplacer");
        PatchLoader.Apply(harmony);

        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;

        GameObject listenerObj = new GameObject("SpriteReplacerInputListener");
        DontDestroyOnLoad(listenerObj);
        listenerObj.hideFlags = HideFlags.HideAndDontSave;
        listenerObj.AddComponent<InputListener>();

        Log.LogInfo("Sprite Replacer loaded successfully. Numpad 0 = default, 1-9 = slots.");
        // species cache lives in the settings json, not the old slot1 cfg.
        SyncSpeciesCache();
    }

    private void Start() {
        CoreLibIntegration.TryRegister();
        BuildInfo.Log(Log);
        // sprite readback needs a graphics context, so wait one frame.
        StartCoroutine(ExportDefaultsWhenReady());
    }

    private IEnumerator ExportDefaultsWhenReady() {
        yield return null;
        int written = DefaultSpriteExport.ExportPrefabs();
        written += DefaultSpriteExport.ExportLoadedWoundViews();
        if (written > 0) {
            // awake loaded whatever was already on disk. reload if boot wrote new pngs.
            _appliedFolder = null;
            SyncSpeciesCache();
        }
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        SyncSpeciesCache();
        SkinMenuUI.EnsureCanvas();
        SkinMenuUI.SetVisible(false);
        ApplyToAll();
    }

    private static void OnSceneUnloaded(Scene scene) {
    }

    private static void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
    }

    private class InputListener : MonoBehaviour {
        private static bool _mpTickError;

        private void Update() {
            SkinMenuUI.SetVisible(MenuGate.ShouldShowButton());
            SkinMenuUI.AlignMenuButtonBesideMp();
            SkinMenuUI.TickCooldown();

            try {
                MpBody.Tick();
                MpSkinSync.Tick();
            } catch (Exception ex) {
                if (!_mpTickError) {
                    _mpTickError = true;
                    SpriteReplacerPlugin.Log.LogWarning("Multiplayer tick failed: " + ex);
                }
            }

            if (SkinMenuUI.IsFolderBrowserOpen) {
                return;
            }

            if (IsInputFieldFocused()) {
                return;
            }

            // species decides which CustomSprites/<Exp|Milky|Dune> tree the numpad hits.
            // species decides which CustomSprites/<Exp|Milky|Dune> tree the numpad hits.
            foreach (KeyValuePair<KeyCode, string> kvp in KeyMapping) {
                if (UnityEngine.Input.GetKeyDown(kvp.Key)) {
                    SwitchToSlot(kvp.Value);
                    break;
                }
            }
        }

        private static bool IsInputFieldFocused() {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null) {
                return false;
            }

            GameObject selected = eventSystem.currentSelectedGameObject;
            if (selected == null) {
                return false;
            }

            TMP_InputField field = selected.GetComponentInParent<TMP_InputField>();
            return field != null && field.isFocused;
        }
    }

    public static bool SwitchToSlot(string slot, bool forceReload = false, bool remember = true) {
        string normalized = ResolveSlot(slot);
        if (normalized == null) {
            Log.LogWarning("Invalid slot: " + slot);
            return false;
        }
        slot = normalized;

        if (!SkinLock.CanChangeLocal) {
            SkinMenuUI.ShowStatus("Skins locked by host.", GameTheme.WarningColor);
            CoreLibIntegration.StoreActiveSlot(_activeSlot);
            return false;
        }

        SkinSettings.EnsureLoaded();
        if (!SkinSettings.skinsEnable) {
            if (remember) {
                RememberSpeciesSlot(slot);
            }

            ClearApplied();
            return true;
        }

        int species = ActiveSpecies;
        if (!forceReload
            && string.Equals(slot, _appliedFolder, StringComparison.Ordinal)
            && _appliedSpecies == species) {
            if (remember) {
                RememberSpeciesSlot(slot);
            }

            SkinMenuUI.RefreshSelection();
            return true;
        }

        if (!CharacterFolderExists(slot)) {
            Log.LogWarning("Character folder for " + SpeciesNames[species] + "/" + slot + " not found");
            SkinTrace.Problem("switch-fail", "reason=folder-missing slot=" + slot
                + " species=" + species
                + " path=" + SlotDirectory(slot));
            return false;
        }

        string prev = _activeSlot;
        _activeSlot = slot;
        _appliedFolder = slot;
        _appliedSpecies = species;
        SaveActiveSlot(slot);
        CoreLibIntegration.StoreActiveSlot(slot);
        LoadCharacterSprites(slot);
        ApplyToAll();
        MpSkinSync.OnLocalSkinChanged();
        SkinMenuUI.RefreshSelection();
        if (remember) {
            RememberSpeciesSlot(slot);
        }

        SkinTrace.Event("switch", "from=" + (prev ?? "-") + " to=" + slot
            + " force=" + (forceReload ? 1 : 0)
            + " remember=" + (remember ? 1 : 0)
            + " " + SkinTrace.SlotSummary(slot, LoadedSprites, species));
        return true;
    }

    // loads the remembered folder for the local body's species. no body yet uses experiment.
    internal static void SyncSpeciesCache() {
        SkinSettings.EnsureLoaded();
        string want = DesiredSpeciesFolder();
        int species = ActiveSpecies;
        if (string.Equals(want, _appliedFolder, StringComparison.Ordinal) && _appliedSpecies == species) {
            return;
        }

        SkinTrace.Event("species-sync", "want=" + (want ?? "-")
            + " applied=" + (_appliedFolder ?? "-")
            + " appliedSpecies=" + _appliedSpecies
            + " liveSpecies=" + species
            + " active=" + (_activeSlot ?? "-"));

        if (want != null
            && string.Equals(want, _syncFailed, StringComparison.Ordinal)
            && _syncFailedSpecies == species) {
            SkinTrace.Event("species-sync-skip", "reason=failed-before want=" + want);
            return;
        }

        if (want == null) {
            _syncFailed = null;
            _syncFailedSpecies = -1;
            ClearApplied();
            SkinTrace.Event("species-sync-clear", "reason=want-null");
            return;
        }

        if (!SwitchToSlot(want, true, false)) {
            _syncFailed = want;
            _syncFailedSpecies = species;
            SkinTrace.Problem("species-sync-fail", "want=" + want
                + " species=" + species
                + " falling-back-default=1"
                + " path=" + SlotDirectory(want));
            if (!string.Equals(want, SkinSettings.DefaultFolder, StringComparison.Ordinal)) {
                SwitchToSlot(SkinSettings.DefaultFolder, true, false);
            }

            return;
        }

        _syncFailed = null;
        _syncFailedSpecies = -1;
    }

    private static string DesiredSpeciesFolder() {
        if (!SkinSettings.skinsEnable) {
            return null;
        }

        // same species key as RememberSpeciesSlot - do not fall back to experiment while milky/dune
        return FolderForSpecies(ActiveSpecies);
    }

    private static void RememberSpeciesSlot(string slot) {
        SkinSettings.Remember(ActiveSpecies, slot);
    }

    private static void ClearApplied() {
        if (_appliedFolder == null && LoadedSprites.Count == 0) {
            return;
        }

        LoadedSprites.Clear();
        ColoredWoundSprites.Clear();
        _appliedFolder = null;
        _appliedSpecies = -1;
        _activeSlot = SkinSettings.DefaultFolder;
        ApplyToAll();
        MpSkinSync.DropSharedLocal();
        SkinMenuUI.RefreshSelection();
        Log.LogInfo("Skin cleared.");
    }

    private static string ResolveSlot(string slot) {
        if (string.IsNullOrEmpty(slot)) {
            return null;
        }

        if (string.Equals(slot, SkinSettings.DefaultFolder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "slot0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "st0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "0", StringComparison.OrdinalIgnoreCase)) {
            // slot 0 no longer exists - numpad 0 and legacy slot0/st0 all mean default.
            return SkinSettings.DefaultFolder;
        }

        return SlotFolders.Normalize(slot);
    }

    private static void SaveActiveSlot(string slot) {
        if (_activeSlotSetting == null) {
            return;
        }

        if (_activeSlotSetting.Value == slot) {
            return;
        }

        _activeSlotSetting.Value = slot;
        if (_activeSlotSetting.ConfigFile != null) {
            _activeSlotSetting.ConfigFile.Save();
        }
    }

    internal static void SetBackupCount(int value) {
        if (_backupCountSetting == null) {
            return;
        }

        value = Math.Max(0, Math.Min(MaxBackupCount, value));
        if (_backupCountSetting.Value == value) {
            return;
        }

        _backupCountSetting.Value = value;
        if (_backupCountSetting.ConfigFile != null) {
            _backupCountSetting.ConfigFile.Save();
        }
    }

    internal static void SetShareSkins(bool value) {
        if (_shareSetting == null || _shareSetting.Value == value) {
            return;
        }

        _shareSetting.Value = value;
        if (_shareSetting.ConfigFile != null) {
            _shareSetting.ConfigFile.Save();
        }
    }

    internal static void SetDownloadSkins(bool value) {
        if (_downloadSetting == null || _downloadSetting.Value == value) {
            return;
        }

        _downloadSetting.Value = value;
        if (_downloadSetting.ConfigFile != null) {
            _downloadSetting.ConfigFile.Save();
        }
    }

    internal static void SetMaxSkinCacheMB(int value) {
        if (_cacheMbSetting == null) {
            return;
        }

        value = Math.Max(4, Math.Min(512, value));
        if (_cacheMbSetting.Value == value) {
            return;
        }

        _cacheMbSetting.Value = value;
        if (_cacheMbSetting.ConfigFile != null) {
            _cacheMbSetting.ConfigFile.Save();
        }
    }

    internal static bool CharacterFolderExists(string character) {
        return CharacterFolderExists(character, ActiveSpecies);
    }

    internal static bool CharacterFolderExists(string character, int species) {
        string normalized = ResolveSlot(character);
        if (normalized == null) {
            return false;
        }
        // default is the game art. an empty folder still counts so a new game can start there.
        if (normalized == SkinSettings.DefaultFolder) {
            return true;
        }

        string basePath = SlotDirectory(normalized, species);
        string bodyPath = Path.Combine(basePath, "Body");
        string headPath = Path.Combine(basePath, "Head");
        // empty prepared dirs are not a character. need pngs.
        return (Directory.Exists(bodyPath) && Directory.GetFiles(bodyPath, "*.png").Length > 0) ||
               (Directory.Exists(headPath) && Directory.GetFiles(headPath, "*.png").Length > 0);
    }

    // old installs kept slot1..slot9 and default next to CustomSprites itself.
    private static void MigrateLegacyIntoExp() {
        string exp = Path.Combine(SpritesPath, SpeciesNames[0]);
        Directory.CreateDirectory(exp);

        List<string> names = new List<string> { SkinSettings.DefaultFolder, "st0", "slot0" };
        for (int i = 1; i <= SlotCount; i++) {
            names.Add(SlotFolders.SlotId(i));
            names.Add(SlotFolders.LegacySlotId(i));
        }

        foreach (string name in names) {
            string src = Path.Combine(SpritesPath, name);
            string dst = Path.Combine(exp, name);
            if (!Directory.Exists(src)) {
                continue;
            }

            if (Directory.Exists(dst)) {
                continue;
            }

            try {
                Directory.Move(src, dst);
            } catch (Exception ex) {
                Log.LogWarning("Could not move " + src + " to Exp: " + ex.Message);
            }
        }
    }

    private static void EnsureCustomSpritesStructure() {
        for (int species = 0; species < SpeciesNames.Length; species++) {
            string root = SpeciesRoot(species);
            Directory.CreateDirectory(root);
            SlotFolders.Migrate(root, SlotCount);

            string fallback = Path.Combine(root, SkinSettings.DefaultFolder);
            Directory.CreateDirectory(Path.Combine(fallback, "Body"));
            Directory.CreateDirectory(Path.Combine(fallback, "Head"));
            MergeLegacySlotZeroIntoDefault(root, fallback);

            for (int i = 1; i <= SlotCount; i++) {
                string slot = Path.Combine(root, SlotFolders.SlotId(i));
                Directory.CreateDirectory(Path.Combine(slot, "Body"));
                Directory.CreateDirectory(Path.Combine(slot, "Head"));
            }
        }
    }

    // slot0 / st0 were never real custom slots. fold leftover art into default, then remove the dirs.
    private static void MergeLegacySlotZeroIntoDefault(string speciesRoot, string defaultRoot) {
        foreach (string legacyName in new[] { "slot0", "st0" }) {
            string legacy = Path.Combine(speciesRoot, legacyName);
            if (!Directory.Exists(legacy)) {
                continue;
            }

            try {
                foreach (string sub in new[] { "Body", "Head", "WoundView" }) {
                    string src = Path.Combine(legacy, sub);
                    if (!Directory.Exists(src)) {
                        continue;
                    }

                    string dst = Path.Combine(defaultRoot, sub);
                    Directory.CreateDirectory(dst);
                    foreach (string file in Directory.GetFiles(src)) {
                        string target = Path.Combine(dst, Path.GetFileName(file));
                        if (!File.Exists(target)) {
                            File.Copy(file, target, false);
                        }
                    }
                }

                Directory.Delete(legacy, true);
            } catch (Exception ex) {
                Log.LogWarning("Could not merge " + legacy + " into default: " + ex.Message);
            }
        }
    }

    private static void LoadCharacterSprites(string character) {
        LoadedSprites.Clear();
        ColoredWoundSprites.Clear();
        LoadSpritesFromFolder(character, "Body");
        LoadSpritesFromFolder(character, "Head");
        LoadSpritesFromFolder(character, "WoundView");
        // milky/dune slots often keep experiment* filenames. map them onto the live sprite names.
        AddSpeciesAliases(LoadedSprites, ActiveSpecies);
        SkinTrace.Event("load-slot", SkinTrace.SlotSummary(character, LoadedSprites, ActiveSpecies));
    }

    // remote packages carry experiment filenames. the species on the wire picks the live names.
    internal static IReadOnlyDictionary<string, Sprite> WithSpeciesAliases(
        IReadOnlyDictionary<string, Sprite> sprites, int species) {
        if (sprites == null || species < 1 || species > 2) {
            return sprites;
        }

        Dictionary<string, Sprite> copy = new Dictionary<string, Sprite>(sprites.Count + 32);
        foreach (KeyValuePair<string, Sprite> pair in sprites) {
            copy[pair.Key] = pair.Value;
        }

        AddSpeciesAliases(copy, species);
        return copy;
    }

    // game limbs look up by their stock sprite name. fill missing milky/dune keys from experiment art.
    internal static void AddSpeciesAliases(IDictionary<string, Sprite> sprites, int species) {
        if (sprites == null) {
            return;
        }

        if (species == 1) {
            Alias(sprites, "milkycrus", "experimentCrus");
            Alias(sprites, "mildownarm", "experimentDownArm");
            Alias(sprites, "milkydowntorso", "experimentDownTorso");
            Alias(sprites, "milkyfoot", "experimentFoot");
            Alias(sprites, "milkyhandb", "experimentHandB");
            Alias(sprites, "milkyhandf", "experimentHandF");
            Alias(sprites, "milkytail", "experimentTail");
            Alias(sprites, "milkythigh", "experimentThigh");
            Alias(sprites, "milkyuparm", "experimentUpArm");
            Alias(sprites, "milkytorso", "experimentUpTorso");
            Alias(sprites, "milkyeyeclosed", "experimentEyeClosed");
            Alias(sprites, "milkyeyegone", "experimentEyeGone");
            Alias(sprites, "milkyeyegonehealed", "experimentEyeGoneHealed");
            Alias(sprites, "milkyeyehalfclosed", "experimentEyeHalfClosed");
            Alias(sprites, "milkyeyehalfclosedback", "experimentEyeHalfClosedBack");
            Alias(sprites, "milkyeyehappy", "experimentEyeHappy");
            Alias(sprites, "milkyeyehappyback", "experimentEyeLookBack");
            Alias(sprites, "milkyeyelockedin", "experimentEyeLockedIn");
            Alias(sprites, "milkyeyelockedinback", "experimentEyeLockedInBack");
            Alias(sprites, "milkyeyeopen", "experimentEyeOpen");
            Alias(sprites, "milkyeyeopenback", "experimentEyeLookBack");
            Alias(sprites, "milkyeyepanic", "experimentEyePanic");
            Alias(sprites, "milkyeyesad", "experimentEyeSad");
            Alias(sprites, "milkyeyesadback", "experimentEyeSadBack");
            Alias(sprites, "milkyeyescared", "experimentEyeScared");
            Alias(sprites, "milkyeyescaredback", "experimentEyeScaredBack");
            Alias(sprites, "milkyheadback", "experimentHeadBack");
            Alias(sprites, "milkyheadbackmouthopen", "experimentHeadBackMouth");
            Alias(sprites, "milkyheadbackmouthhalfopen", "experimentHeadBackMouthMini");
            Alias(sprites, "milkydisfigure1", "experimentHeadDisfigured1");
            Alias(sprites, "milkydisfigure1healed", "experimentHeadDisfigured1Healed");
            Alias(sprites, "milkydisfigure2", "experimentHeadDisfigured2");
            Alias(sprites, "milkydisfigure2healed", "experimentHeadDisfigured2Healed");
            Alias(sprites, "milkheadmiddle", "headMiddle");
            Alias(sprites, "milkheadouter", "headoutline");
            return;
        }

        if (species != 2) {
            return;
        }

        Alias(sprites, "duneCrus", "experimentCrus");
        Alias(sprites, "duneDownArm", "experimentDownArm");
        Alias(sprites, "duneDownArmAlt", "experimentDownArm");
        Alias(sprites, "duneDownTorso", "experimentDownTorso");
        Alias(sprites, "duneFoot", "experimentFoot");
        Alias(sprites, "duneHand", "experimentHandF");
        Alias(sprites, "duneHandAlt", "experimentHandB");
        Alias(sprites, "duneTail", "experimentTail");
        Alias(sprites, "duneThigh", "experimentThigh");
        Alias(sprites, "duneThighAlt", "experimentThigh");
        Alias(sprites, "duneUpArm", "experimentUpArm");
        Alias(sprites, "duneUpArmAlt", "experimentUpArm");
        Alias(sprites, "duneUpTorso", "experimentUpTorso");
        Alias(sprites, "duneEyesClosed", "experimentEyeClosed");
        Alias(sprites, "duneEyeGone", "experimentEyeGone");
        Alias(sprites, "duneEyeGoneHealed", "experimentEyeGoneHealed");
        Alias(sprites, "duneEyesHalfClosed", "experimentEyeHalfClosed");
        Alias(sprites, "duneEyeHappy", "experimentEyeHappy");
        Alias(sprites, "duneeyelockedin", "experimentEyeLockedIn");
        Alias(sprites, "duneEyes", "experimentEyeOpen");
        Alias(sprites, "duneEyePanic", "experimentEyePanic");
        Alias(sprites, "duneEyeSad", "experimentEyeSad");
        Alias(sprites, "duneEyeScared", "experimentEyeScared");
        Alias(sprites, "duneHeadBack", "experimentHeadBack");
        Alias(sprites, "duneHeadMouth", "experimentHeadBackMouth");
        Alias(sprites, "duneHeadMouthHalf", "experimentHeadBackMouthMini");
        Alias(sprites, "duneDisfigured", "experimentHeadDisfigured1");
        Alias(sprites, "duneDisfiguredHealed", "experimentHeadDisfigured1Healed");
        Alias(sprites, "headMiddleDune", "headMiddle");
        Alias(sprites, "headOuterDune", "headoutline");
    }

    private static void Alias(IDictionary<string, Sprite> sprites, string liveName, string sourceName) {
        if (sprites == null || string.IsNullOrEmpty(liveName) || string.IsNullOrEmpty(sourceName)) {
            return;
        }

        if (sprites.ContainsKey(liveName)) {
            return;
        }

        if (!sprites.TryGetValue(sourceName, out Sprite sprite) || sprite == null) {
            return;
        }

        sprites[liveName] = sprite;
    }

    private static void LoadSpritesFromFolder(string character, string subfolder) {
        string path = Path.Combine(SlotDirectory(character), subfolder);
        if (!Directory.Exists(path)) {
            return;
        }

        foreach (string file in Directory.GetFiles(path, "*.png")) {
            string spriteName = Path.GetFileNameWithoutExtension(file);
            byte[] data = File.ReadAllBytes(file);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            if (!ImageConversion.LoadImage(tex, data)) {
                UnityEngine.Object.Destroy(tex);
                Log.LogWarning("Could not decode sprite: " + file);
                continue;
            }

            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 8f);
            sprite.name = spriteName;
            LoadedSprites[spriteName] = sprite;

            if (subfolder == "WoundView" && HasColorPixels(tex)) {
                ColoredWoundSprites.Add(spriteName);
            }
        }
    }

    private static bool HasColorPixels(Texture2D tex) {
        Color32[] pixels = tex.GetPixels32();
        for (int i = 0; i < pixels.Length; i++) {
            Color32 p = pixels[i];
            if (p.a < 128) {
                continue;
            }

            if (p.r != p.g || p.g != p.b) {
                return true;
            }
        }
        return false;
    }

    internal static Sprite LoadEmbeddedSprite(string resourceName) {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using (Stream stream = assembly.GetManifestResourceStream(resourceName)) {
            if (stream == null) {
                Log.LogWarning($"Embedded resource '{resourceName}' not found");
                return null;
            }
            byte[] data = new byte[stream.Length];
            stream.Read(data, 0, data.Length);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            if (!ImageConversion.LoadImage(tex, data)) {
                UnityEngine.Object.Destroy(tex);
                Log.LogWarning("Could not decode embedded sprite: " + resourceName);
                return null;
            }
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 8f);
            sprite.name = "MenuButtonPlaceholder";
            return sprite;
        }
    }

    internal static void ApplyToAll() {
        if (MpBody.IsActive) {
            MpBody.ApplyLocalSkin(LoadedSprites);
        } else {
            foreach (Limb limb in UnityEngine.Object.FindObjectsOfType<Limb>()) {
                ApplyReplacement(limb);
            }
            foreach (TailScript tail in UnityEngine.Object.FindObjectsOfType<TailScript>()) {
                ApplyReplacementToTail(tail);
            }
            foreach (FacialExpression face in UnityEngine.Object.FindObjectsOfType<FacialExpression>()) {
                ApplyReplacementToFace(face);
            }
        }

        WoundViewIntegration.ApplyAll();
        DefaultSpriteExport.ExportNew();
    }

    public static void ApplyReplacement(Limb limb) {
        if (!MpBody.CanApply(limb.body)) {
            return;
        }

        SpriteRenderer sr = limb.GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null) {
            DefaultSpriteExport.Capture(sr.sprite);

            if (LoadedSprites.TryGetValue(sr.sprite.name, out Sprite replacement)) {
                sr.sprite = replacement;
            }
        }
    }

    public static void ApplyReplacementToTail(TailScript tail) {
        if (!MpBody.CanApply(tail.GetComponentInParent<Body>())) {
            return;
        }

        SpriteRenderer sr = tail.GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null) {
            DefaultSpriteExport.Capture(sr.sprite);

            if (LoadedSprites.TryGetValue(sr.sprite.name, out Sprite replacement)) {
                sr.sprite = replacement;
            }
        }
    }

    public static void ApplyReplacementToFace(FacialExpression face) {
        if (face == null || !MpBody.CanApply(face.body)) {
            return;
        }

        ApplyReplacementToFace(face, LoadedSprites);
    }

    static readonly IReadOnlyDictionary<string, Sprite> EmptySpriteMap =
        new Dictionary<string, Sprite>();

    // slot apply renames dune/milky face fields to experiment* via aliases. empty-map
    // replaceSprite can only recover stock by the *current* name, so default never
    // returns duneHeadBack. keep the pre-skin field sprites and write them back.
    sealed class FaceStock {
        internal Sprite DefaultHead;
        internal Sprite DefaultHeadMouth;
        internal Sprite DefaultHeadMouthHalf;
        internal Sprite EyesGone;
        internal Sprite EyesGoneHealed;
        internal Sprite[] DisfiguredHead;
        internal Sprite[] DisfiguredHeadHeal;
        internal Eye[] Eyes;
    }

    static readonly Dictionary<int, FaceStock> FaceStocks = new Dictionary<int, FaceStock>();

    static void CaptureFaceStock(FacialExpression face) {
        if (face == null) {
            return;
        }

        int id = face.GetInstanceID();
        if (FaceStocks.ContainsKey(id)) {
            return;
        }

        FaceStock stock = new FaceStock {
            DefaultHead = face.defaultHead,
            DefaultHeadMouth = face.defaultHeadMouth,
            DefaultHeadMouthHalf = face.defaultHeadMouthHalf,
            EyesGone = face.eyesGone,
            EyesGoneHealed = face.eyesGoneHealed,
        };

        if (face.disfiguredHead != null) {
            stock.DisfiguredHead = (Sprite[])face.disfiguredHead.Clone();
        }

        if (face.disfiguredHeadHeal != null) {
            stock.DisfiguredHeadHeal = (Sprite[])face.disfiguredHeadHeal.Clone();
        }

        if (face.eyeList != null) {
            stock.Eyes = face.eyeList.ToArray();
        }

        FaceStocks[id] = stock;
        DefaultSpriteExport.Capture(face.defaultHead);
        DefaultSpriteExport.Capture(face.defaultHeadMouth);
        DefaultSpriteExport.Capture(face.defaultHeadMouthHalf);
    }

    // facialExpression caches head/eye sprites in fields - SR Reset alone leaves a custom head.
    public static void RestoreFaceToStock(FacialExpression face) {
        if (face == null) {
            return;
        }

        int id = face.GetInstanceID();
        if (FaceStocks.TryGetValue(id, out FaceStock stock)) {
            face.defaultHead = stock.DefaultHead;
            face.defaultHeadMouth = stock.DefaultHeadMouth;
            face.defaultHeadMouthHalf = stock.DefaultHeadMouthHalf;
            face.eyesGone = stock.EyesGone;
            face.eyesGoneHealed = stock.EyesGoneHealed;

            if (stock.DisfiguredHead != null && face.disfiguredHead != null) {
                int n = Math.Min(stock.DisfiguredHead.Length, face.disfiguredHead.Length);
                for (int i = 0; i < n; i++) {
                    face.disfiguredHead[i] = stock.DisfiguredHead[i];
                }
            }

            if (stock.DisfiguredHeadHeal != null && face.disfiguredHeadHeal != null) {
                int n = Math.Min(stock.DisfiguredHeadHeal.Length, face.disfiguredHeadHeal.Length);
                for (int i = 0; i < n; i++) {
                    face.disfiguredHeadHeal[i] = stock.DisfiguredHeadHeal[i];
                }
            }

            if (stock.Eyes != null && face.eyeList != null) {
                int n = Math.Min(stock.Eyes.Length, face.eyeList.Count);
                for (int i = 0; i < n; i++) {
                    face.eyeList[i] = stock.Eyes[i];
                }
            }

            if (face.nosebleedSprite != null) {
                SkinSpriteReplacer replacer = face.nosebleedSprite.GetComponent<SkinSpriteReplacer>();
                if (replacer != null) {
                    replacer.Restore();
                }
            }

            if (face.head != null && stock.DefaultHead != null) {
                face.head.sprite = stock.DefaultHead;
            }

            SkinTrace.Event("face-restore-stock",
                "head=" + (stock.DefaultHead != null ? stock.DefaultHead.name : "-")
                + " " + SkinTrace.BodyState(face.body));
            return;
        }

        ApplyReplacementToFace(face, EmptySpriteMap);
    }

    public static bool IsDefaultSlotName(string slot) {
        return string.IsNullOrEmpty(slot)
            || string.Equals(slot, SkinSettings.DefaultFolder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "slot0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "st0", StringComparison.OrdinalIgnoreCase);
    }

    // remote MP skins pass their package sprites; local uses LoadedSprites.
    public static void ApplyReplacementToFace(FacialExpression face,
        IReadOnlyDictionary<string, Sprite> sprites) {
        if (face == null || sprites == null) {
            return;
        }

        if (sprites.Count > 0) {
            CaptureFaceStock(face);
        }

        // always start from the pre-skin sprites. the last slot renames duneHeadBack to
        // experimentHeadBack, and the next slot would then restore the experiment head.
        FaceStocks.TryGetValue(face.GetInstanceID(), out FaceStock stock);
        Sprite headBase = stock != null ? stock.DefaultHead : face.defaultHead;
        Sprite mouthBase = stock != null ? stock.DefaultHeadMouth : face.defaultHeadMouth;
        Sprite mouthHalfBase = stock != null ? stock.DefaultHeadMouthHalf : face.defaultHeadMouthHalf;
        Sprite eyesGoneBase = stock != null ? stock.EyesGone : face.eyesGone;
        Sprite eyesGoneHealedBase = stock != null ? stock.EyesGoneHealed : face.eyesGoneHealed;

        string before = headBase != null ? headBase.name : "-";
        SkinTrace.Event("face-apply", SkinTrace.FaceApplySummary(face, sprites)
            + " " + SkinTrace.BodyState(face.body));

        face.defaultHead = ReplaceSprite(headBase, sprites);
        face.defaultHeadMouth = ReplaceSprite(mouthBase, sprites);
        face.defaultHeadMouthHalf = ReplaceSprite(mouthHalfBase, sprites);
        face.eyesGone = ReplaceSprite(eyesGoneBase, sprites);
        face.eyesGoneHealed = ReplaceSprite(eyesGoneHealedBase, sprites);

        if (face.nosebleedSprite != null) {
            DefaultSpriteExport.Capture(face.nosebleedSprite.sprite);

            SkinSpriteReplacer replacer = face.nosebleedSprite.GetComponent<SkinSpriteReplacer>();
            if (replacer == null) {
                replacer = SkinSpriteReplacer.Attach(face.nosebleedSprite, sprites);
                replacer.Apply();
            } else {
                replacer.SetSprites(sprites);
            }
        }

        if (face.disfiguredHead != null) {
            for (int i = 0; i < face.disfiguredHead.Length; i++) {
                Sprite basis = face.disfiguredHead[i];
                if (stock != null && stock.DisfiguredHead != null && i < stock.DisfiguredHead.Length) {
                    basis = stock.DisfiguredHead[i];
                }
                face.disfiguredHead[i] = ReplaceSprite(basis, sprites);
            }
        }

        if (face.disfiguredHeadHeal != null) {
            for (int i = 0; i < face.disfiguredHeadHeal.Length; i++) {
                Sprite basis = face.disfiguredHeadHeal[i];
                if (stock != null && stock.DisfiguredHeadHeal != null && i < stock.DisfiguredHeadHeal.Length) {
                    basis = stock.DisfiguredHeadHeal[i];
                }
                face.disfiguredHeadHeal[i] = ReplaceSprite(basis, sprites);
            }
        }

        if (face.eyeList != null) {
            for (int i = 0; i < face.eyeList.Count; i++) {
                Eye eye = face.eyeList[i];
                Eye basis = eye;
                if (stock != null && stock.Eyes != null && i < stock.Eyes.Length) {
                    basis = stock.Eyes[i];
                }
                eye.front = ReplaceSprite(basis.front, sprites);
                eye.back = ReplaceSprite(basis.back, sprites);
                face.eyeList[i] = eye;
            }
        }

        string after = face.defaultHead != null ? face.defaultHead.name : "-";
        int headSpecies = -1;
        string afterLower = after.ToLowerInvariant();
        if (afterLower.Contains("dune")) {
            headSpecies = 2;
        } else if (afterLower.Contains("milk")) {
            headSpecies = 1;
        } else if (afterLower.StartsWith("experiment") || SkinImporter.HeadNames.Contains(after)) {
            headSpecies = 0;
        }
        int charType = face.body != null ? face.body.charType : -1;
        int clash = headSpecies >= 0 && charType >= 0 && charType <= 2 && headSpecies != charType ? 1 : 0;
        int faceId = face.body != null ? face.body.GetInstanceID() : 0;
        SkinTrace.Problem("face-applied " + faceId, "beforeHead=" + before + " afterHead=" + after
            + " stockMiss=" + (sprites.Count == 0 || !sprites.ContainsKey(before) ? 1 : 0)
            + " headSpecies=" + headSpecies
            + " headClash=" + clash
            + " map=" + sprites.Count
            + " " + SkinTrace.BodyState(face.body));
    }

    private static Sprite ReplaceSprite(Sprite s) {
        return ReplaceSprite(s, LoadedSprites);
    }

    private static Sprite ReplaceSprite(Sprite s, IReadOnlyDictionary<string, Sprite> sprites) {
        DefaultSpriteExport.Capture(s);

        if (s == null) {
            return null;
        }

        if (sprites != null && sprites.TryGetValue(s.name, out Sprite r) && r != null) {
            return r;
        }

        // missing in this slot (empty Head folder, etc.) - restore stock, do not keep the prior skin.
        if (DefaultSpriteExport.TryGetStock(s.name, out Sprite stock)) {
            return stock;
        }

        return s;
    }
}

[HarmonyPatch(typeof(Limb), "Awake")]
public class LimbAwakePatch {
    static void Postfix(Limb __instance) {
        // never solo-stamp LoadedSprites while a session is up - remotes would get the local slot.
        if (MpBody.IsActive || Net.IsRunning) {
            return;
        }

        SpriteReplacerPlugin.ApplyReplacement(__instance);
    }
}

[HarmonyPatch(typeof(FacialExpression), "Start")]
public class FacialExpressionStartPatch {
    static void Postfix(FacialExpression __instance) {
        if (MpBody.IsActive) {
            // head/face finishes after torso - kick pending remote (or local) skin apply now.
            MpSkinSync.OnBodyPartReady(__instance != null ? __instance.body : null);
            return;
        }

        if (Net.IsRunning) {
            // mpBody bridge not ready yet - do not paint local slot onto joiners.
            return;
        }

        SpriteReplacerPlugin.ApplyReplacementToFace(__instance);
    }
}

[HarmonyPatch(typeof(TailScript), "Start")]
public class TailScriptStartPatch {
    static void Postfix(TailScript __instance) {
        if (MpBody.IsActive || Net.IsRunning) {
            return;
        }

        SpriteReplacerPlugin.ApplyReplacementToTail(__instance);
    }
}

[HarmonyPatch(typeof(Settings), "ApplyAll")]
public class SettingsApplyAllPatch {
    // the game writes settings after us. put the slot back first so a stale value cannot win.
    static void Prefix() {
        CoreLibIntegration.StoreActiveSlot(SpriteReplacerPlugin.ActiveSlot);
        CoreLibIntegration.StoreBackupCount(SpriteReplacerPlugin.BackupCount);
    }
}

[HarmonyPatch(typeof(WoundView), "Awake")]
public class WoundViewAwakePatch {
    static void Postfix(WoundView __instance) {
        WoundViewIntegration.Apply(__instance);
    }
}

[HarmonyPatch(typeof(WoundView), "Start")]
public class WoundViewStartPatch {
    static void Postfix(WoundView __instance) {
        if (__instance == null || __instance.limbImages == null || __instance.limbImages.Length == 0) {
            WoundViewIntegration.Apply(__instance);
            return;
        }

        Image head = __instance.limbImages[0];
        WoundViewIntegration.RememberStock(head);
        if (head != null && head.transform.childCount > 0) {
            WoundViewIntegration.RememberStock(head.transform.GetChild(0).GetComponent<Image>());
        }

        WoundViewIntegration.Apply(__instance);
    }
}

[HarmonyPatch(typeof(WoundView), "UpdateView")]
public class WoundViewUpdatePatch {
    static void Postfix(WoundView __instance) {
        WoundViewIntegration.ShowCustomColors(__instance);
    }
}

[HarmonyPatch(typeof(PlayerCamera), "Awake")]
public class PlayerCameraAwakePatch {
    static void Postfix() {
        WoundViewIntegration.ApplyAll();
    }
}