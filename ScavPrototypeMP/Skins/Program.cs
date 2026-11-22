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

    internal static bool ShareSkins {
        get {
            SkinSettings.EnsureLoaded();
            return SkinSettings.shareSkins;
        }
    }

    internal static bool DownloadSkins {
        get {
            SkinSettings.EnsureLoaded();
            return SkinSettings.downloadSkins;
        }
    }

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
        SlotFolders.MigrateLegacyIntoExp();
        SlotFolders.EnsureCustomSpritesStructure();

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
        AdoptCfgToggle(nameof(SkinSettings.shareSkins), _shareSetting);
        AdoptCfgToggle(nameof(SkinSettings.downloadSkins), _downloadSetting);
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
        KeybindRecovery.Apply();
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

        if (MpSkinSync.IsCoolingDown) {
            SkinMenuUI.ShowStatus("Skin changes paused. Try again in "
                + MpSkinSync.CooldownSeconds + "s.", GameTheme.WarningColor);
            CoreLibIntegration.StoreActiveSlot(_activeSlot);
            Log.LogInfo("Skin change blocked: rate limited.");
            return false;
        }

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
        WriteSkinToggle(nameof(SkinSettings.shareSkins), value);
        WriteCfgBool(_shareSetting, value);
    }

    internal static void SetDownloadSkins(bool value) {
        bool was = DownloadSkins;
        WriteSkinToggle(nameof(SkinSettings.downloadSkins), value);
        WriteCfgBool(_downloadSetting, value);
        if (was == DownloadSkins) {
            return;
        }
        if (!DownloadSkins) {
            MpSkinSync.ReleaseShownRemoteSkins();
        } else {
            MpSkinSync.ShowStoredRemoteSkins();
        }
    }

    // the cfg file is the old store. a missing settings-json key takes that value once.
    static void AdoptCfgToggle(string fieldName, ConfigEntry<bool> entry) {
        if (entry == null) {
            return;
        }

        SkinSettings.EnsureLoaded();
        FieldInfo field = typeof(SkinSettings).GetField(fieldName, BindingFlags.Static | BindingFlags.Public);
        SettingDeclarerThingyBoolAttribute attr = field == null ? null : field.GetCustomAttribute<SettingDeclarerThingyBoolAttribute>();
        if (attr == null || SettingsJsonThing.json.HasKey(attr.GetPrefKey(field))) {
            WriteCfgBool(entry, (bool)field.GetValue(null));
            return;
        }

        attr.Save(field, entry.Value);
        SettingsJsonThing.json.Save();
    }

    static void WriteSkinToggle(string fieldName, bool value) {
        SkinSettings.EnsureLoaded();
        FieldInfo field = typeof(SkinSettings).GetField(fieldName, BindingFlags.Static | BindingFlags.Public);
        if (field == null || (bool)field.GetValue(null) == value) {
            return;
        }

        SettingDeclarerThingyBoolAttribute attr = field.GetCustomAttribute<SettingDeclarerThingyBoolAttribute>();
        if (attr == null) {
            field.SetValue(null, value);
            return;
        }

        attr.Save(field, value);
        SettingsJsonThing.json.Save();
    }

    static void WriteCfgBool(ConfigEntry<bool> entry, bool value) {
        if (entry == null || entry.Value == value) {
            return;
        }

        entry.Value = value;
        if (entry.ConfigFile != null) {
            entry.ConfigFile.Save();
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
        if (SkinJsonFile.Exists(basePath)) {
            return true;
        }

        string bodyPath = Path.Combine(basePath, "Body");
        string headPath = Path.Combine(basePath, "Head");
        // empty prepared dirs are not a character. need pngs.
        return (Directory.Exists(bodyPath) && Directory.GetFiles(bodyPath, "*.png").Length > 0) ||
               (Directory.Exists(headPath) && Directory.GetFiles(headPath, "*.png").Length > 0);
    }


    private static void LoadCharacterSprites(string character) {
        LocalSkinLeft.Reset();
        LoadedSprites.Clear();
        ColoredWoundSprites.Clear();

        // skin.json is the whole skin, PNG folders are not read.
        if (SkinJsonFile.Exists(SlotDirectory(character))) {
            LoadSpritesFromJson(character);
        } else {
            LoadSpritesFromFolder(character, "Body");
            LoadSpritesFromFolder(character, "Head");
            LoadSpritesFromFolder(character, "WoundView");
        }

        LocalSkinLeft.Link(LoadedSprites);
        // milky/dune slots often keep experiment* filenames. map them onto the live sprite names.
        SpeciesAliases.Add(LoadedSprites, ActiveSpecies);
        SkinTrace.Event("load-slot", SkinTrace.SlotSummary(character, LoadedSprites, ActiveSpecies));
    }

    private static void LoadSpritesFromFolder(string character, string subfolder) {
        LegacySkinFolder.Load(Path.Combine(SlotDirectory(character), subfolder), subfolder == "WoundView",
            LoadedSprites, ColoredWoundSprites, LocalSkinLeft.LoadFile);
    }

    private static void LoadSpritesFromJson(string character) {
        string slotRoot = SlotDirectory(character);
        if (!SkinJsonFile.TryLoad(slotRoot, ActiveSpecies, out Dictionary<string, Sprite> rights,
                out Dictionary<string, Sprite> lefts, out HashSet<string> coloredWound, out string error)) {
            Log.LogWarning("Could not load " + SkinJsonFile.SlotPath(slotRoot) + ": " + error);
            return;
        }

        foreach (KeyValuePair<string, Sprite> pair in rights) {
            LoadedSprites[pair.Key] = pair.Value;
        }

        foreach (string name in coloredWound) {
            ColoredWoundSprites.Add(name);
        }

        foreach (KeyValuePair<string, Sprite> pair in lefts) {
            LocalSkinLeft.Add(pair.Key, pair.Value);
        }
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
                SkinFace.ApplyReplacementToFace(face);
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

    public static bool IsDefaultSlotName(string slot) {
        return string.IsNullOrEmpty(slot)
            || string.Equals(slot, SkinSettings.DefaultFolder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "slot0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "st0", StringComparison.OrdinalIgnoreCase);
    }

}
