// by dannad


using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using Together;
using Unity.Burst.CompilerServices;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;


namespace Multiupdater;

[BepInPlugin("dannad.krmultiupdater", "multiupdater", "0.0.1")]
[BepInDependency("CasualtiesMP", BepInDependency.DependencyFlags.HardDependency)]
public class AutoUpdater : BaseUnityPlugin {
    //
    private static Version DEBUG_remote = new Version("10.10.10");
    //
    internal static new ManualLogSource Logger;
    internal static AutoupdaterGUI GUI;
    private static Version local = null;
    private static Version remote = null;
    public static string Status => AutoupdaterGUI.Status;
    internal static (Version, string) latestVersionAndURL = (null, null);
    public static Action OnStageSuccess;
    public static Action OnStageFailure;

    private static Assembly KrokMPAssembly = null;

    private void _LoadKrokMPAssembly() {
        try {
            KrokMPAssembly = Assembly.GetAssembly(typeof(CasualtiesTogetherSingleton));
        } catch (Exception e) {
        }
    }
    private void AddDrawUpdateButtonUI() {
        try {
            UIMainMenu.DrawUpdateButton = TogetherUIOverride.DrawUpdateButton;
        } catch (Exception e) {
        }
    }
    private void AddBuildUpdateButtonUI() {
        try {
            UIMainMenu.BuildUpdateButton = TogetherUIOverride.BuildUpdateButton;
        } catch (Exception e) {
        }
    }


    private async void Awake() {

        _LoadKrokMPAssembly();
        try {
            AddDrawUpdateButtonUI();
        } catch (Exception e) {
#if DEBUG
            Logger.LogError(e.ToString());
#endif
        }
        try {
            AddBuildUpdateButtonUI();
        } catch (Exception e) {
#if DEBUG
            Logger.LogError(e.ToString());
#endif
        }

        Logger = base.Logger;
        bool should_start = true;
        try {
            should_start = ShouldStartImmidiately();
        } catch (Exception e) {
#if DEBUG
            Logger.LogError(e.ToString());
#endif
        }

        if (should_start) {
            InitGUI();
            await StartChecking();
        }
    }
    // separate these functions so that the missing fields can be ignored 
    static bool ShouldStartImmidiately() {
        try {
            return (!Together.Plugin.FORCE_DISABLE_MP_MOD && (!Together.Plugin.SUCCESFULLY_INITIALIZED ||
                Traverse.Create(typeof(Together.Plugin)).Field(nameof(Together.Plugin.TARGET_GAME_VERSION)).GetValue<string>()
                 != Application.version));
        } catch (Exception e) {
            return true;
        }
    }
    static bool MPIsReleaseBuild() {
        try {
            return Traverse.Create(typeof(Together.Plugin)).Field(nameof(Together.Plugin.IS_RELEASE_BUILD)).GetValue<bool>();
        } catch (Exception e) {
            return false;
        }
    }

    static bool Updating = false;
    public static async Task StartChecking() {
        if (Updating) {
            return;
        }
        try {
            Updating = true;
            LogMessage("Checking for updates");
            await ParseVersions();
            if (remote != null) {
                bool UpdatePresent = IsCurrentVersionOutdated();
                if (UpdatePresent) {
                    LogMessage($"Update found! Your version: {local}; New version: {remote}!");
                    AutoupdaterGUI.ShowUpdateButton = true;
                    await AutoUpdater.InitiateUpdate();
                }
            }
            Updating = false;
        } catch (Exception e) {
            Updating = false;
            LogError(e.ToString());
        }
        Updating = false;
    }

    // to wrap it in try-catch, dont trust the game 
    static void LogToGameConsole(in string str) {
        if (ConsoleScript.instance != null) {
            ConsoleScript.instance.LogToConsole(str);
        }
    }

    internal static void LogMessage(in string str) {
        AutoupdaterGUI.Status = str;
        try {
            LogToGameConsole("AutoUpdater: " + str);
        } catch (Exception e) { Logger.LogError($"Failed to log ingame: {e}"); }
        Logger.LogMessage(str);

        if (TogetherUIOverride.RealUpdateStatusText != null) {
            TogetherUIOverride.RealUpdateStatusText.text = Multiupdater.AutoUpdater.Status;
        }
    }
    internal static void LogError(in string str) {
        AutoupdaterGUI.Status = "ERROR: " + str;
        try {
            LogToGameConsole($"<color=red>AutoUpdater ERROR: {str}</color>");
        } catch (Exception e) { Logger.LogError($"Failed to log ingame: {e}"); }
        Logger.LogError(str);

        if (TogetherUIOverride.RealUpdateStatusText != null) {
            TogetherUIOverride.RealUpdateStatusText.text = Multiupdater.AutoUpdater.Status;
        }
    }


    public static async Task ParseVersions() {
        try {
            local = GetLocalVersion();
            latestVersionAndURL = await GetLatestTagAndDownloadURL();
            if (remote != null) {
                LogMessage($"Local version: {local}  Remote version: {remote}");
            }
        } catch (Exception e) {
            LogError($"Something went wrong while parsing versions! {e}");
        }
    }

    public static async Task InitiateUpdate() {
        if (latestVersionAndURL.Item1 == null) {
            LogError("Tried to call InitiateUpdate() with null version! Did you run ParseVersions() beforehand?");

            return;
        }
        if (latestVersionAndURL.Item2 == null) {
            LogError("Tried to call InitiateUpdate() with null download URL! Did you run ParseVersions() beforehand?");
            return;
        }
        try {
            await StageVersion(latestVersionAndURL.Item2);
        } catch (Exception e) {
            LogError($"Exception while initiating update! {e}");
        }
    }
    public static async Task<(Version, string)> GetLatestTagAndDownloadURL() {
        try {
            (Version, string) TagAndURL = await GetRemoteVersion();
            remote = TagAndURL.Item1;
            //remote=DEBUG_remote;
            return TagAndURL;
        } catch (TimeoutException) {
            LogMessage("Github unreachable");
            return (null, null);
        } catch (Exception ex) {
            LogMessage($"Something went wrong while trying to parse versions! {ex}");
            return (null, null);
        }
    }

    public static bool IsCurrentVersionOutdated() {
        return IsVersionOutdated(latestVersionAndURL);
    }
    public static bool IsVersionOutdated((Version, string) TagAndURL) {
        bool is_release = false;
        try {
            is_release = MPIsReleaseBuild();
        } catch (Exception e) {
            is_release = false;
        }

        if (remote >= local) {
            if (remote == local && is_release) {
                LogMessage($"You're running the latest version.");
                return false;
            } else {
                LogMessage("Multiplayer version is outdated");
                return true;
            }
        } else {
            LogMessage("You're running the pre-release build.");
            return false;
        }
    }

    private static Version GetLocalVersion() {
        if (Chainloader.PluginInfos.TryGetValue(Together.MyPluginInfo.PLUGIN_GUID, out PluginInfo info)) {
            return info.Metadata.Version;
        } else {
            throw new Exception("Could not locate mod by guid somehow!!");
        }
    }

    public static async Task<(Version, string)> GetRemoteVersion() {
        try {
            (Version, string) TagAndURL = await HTTPHandler.GetLatestRelease();
            return TagAndURL;
        } catch (TimeoutException e) {
            LogMessage("Github unreachable when retrieving version.");
            throw new TimeoutException("Github unreachable when retrieving version", e);
        } catch (InvalidDataException e) {
            throw new Exception("No suitable version has been found in the release list, meaning that it's either empty, or there are somehow only blacklisted versions now.", e);
        } catch (Exception e) {
            throw new Exception($"Mystery Tag Parsing Exception!", e);
        }
    }

    public static async Task<bool> StageVersion(string ArchiveURL) {
        if (await Updater.InitiateUpdate(ArchiveURL)) {
            OnStageSuccess?.Invoke();
            return true;
        } else {
            OnStageFailure?.Invoke();
            return false;
        }

    }
    private void InitGUI() {
        AutoUpdater.GUI = gameObject.GetOrAddComponent<AutoupdaterGUI>();
    }

    private void OnLoad() {

    }
    private void OnApplicationQuit() {

    }
}




internal static class TogetherUIOverride {
    static bool _CHECKFORUPDATEBUTTON_IS_RESTART = false;
    static bool _CHECKFORUPDATEBUTTON_OnStageSuccess_isBound = false;

    public static GameObject RealUpdateButton = null;
    public static TextMeshProUGUI RealUpdateStatusText = null;

    internal static void BuildUpdateButton() {
        var content = SettingsMenu_Start_MultiplayerPatch.content;

        ref float num = ref SettingsMenu_Start_MultiplayerPatch.contentHeight;

        { // Send button
            SettingsMenu_Start_MultiplayerPatch.CreateSettingButton(Lang.Get("mmst_button_checkupdate"), ref num, out var g, out var button, out var text);
            SettingsMenu_Start_MultiplayerPatch.UpdateButton = g;
            RealUpdateButton = g;
            if (_CHECKFORUPDATEBUTTON_IS_RESTART) {
                SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(g, Lang.Get("mmst_button_restart"), Lang.Get("mmst_button_restart_desc"));
            } else {
                SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(g, Lang.Get("mmst_button_checkupdate"), Lang.Get("mmst_button_checkupdate_desc"));
            }
            button.onClick.AddListener(() => {
                button.interactable = false;

                if (_CHECKFORUPDATEBUTTON_IS_RESTART) {
                    CasualtiesTogetherUtils.Util.DelayCallLambda(0.01f, () => {
                        Plugin.RestartGame();
                    });
                } else {
                    if (!_CHECKFORUPDATEBUTTON_OnStageSuccess_isBound) {
                        _CHECKFORUPDATEBUTTON_OnStageSuccess_isBound = true;
                        Multiupdater.AutoUpdater.OnStageSuccess += () => {
                            AutoUpdater.LogMessage("Update staging succesful.");
                            if (Plugin.IsExeSameAsInSteam()) {
                                _CHECKFORUPDATEBUTTON_IS_RESTART = true;
                                button.interactable = true;
                                SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(g, Lang.Get("mmst_button_restart"), Lang.Get("mmst_button_restart_desc"));
                            }
                        };
                    }
                    Multiupdater.AutoUpdater.StartChecking();
                    CasualtiesTogetherUtils.Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        
                    SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(g, Lang.Get("mmst_button_checkupdate"), Lang.Get("mmst_button_checkupdate_desc"));
                }
            });
            var rect = g.GetComponent<RectTransform>();
            rect.anchorMax -= Vector2.right * 0.5f;
            rect.offsetMin += Vector2.one * 10f;
            rect.offsetMax -= Vector2.one * 10f;
        }

        { // Notice text
            var g = Utils.Create("Special/GameSettingDropdown", content);
            var dropDown = g.transform.GetChild(1).GetComponent<TMP_Dropdown>();
            if (dropDown != null) {
                GameObject.Destroy(dropDown.gameObject);
            }

            var rect = g.GetComponent<RectTransform>();
            rect.sizeDelta += rect.sizeDelta * Vector2.up * 1f;

            RealUpdateStatusText = g.GetComponentInChildren<TextMeshProUGUI>();
            RealUpdateStatusText.verticalAlignment = VerticalAlignmentOptions.Top;
            RealUpdateStatusText.alignment = TextAlignmentOptions.TopLeft;
            RealUpdateStatusText.color *= 0.8f;
            SettingsMenu_Start_MultiplayerPatch.UpdateStatusText = RealUpdateStatusText;

            SettingsMenu_Start_MultiplayerPatch.SetupSetting(g, Multiupdater.AutoUpdater.Status, null, ref num);
        }

    }
    internal static void DrawUpdateButton() {
        var resuuuu = Multiupdater.AutoUpdater.Status; // KEEP THIS HERE, SO THIS FUNC CAN CRASH EARLIER
        UnityEngine.GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUILayout.BeginHorizontal();
        {
            if (_CHECKFORUPDATEBUTTON_IS_RESTART) {
                if (GUILayout.Button(Lang.Get("mmst_button_restart"))) {
                    CasualtiesTogetherUtils.Util.DelayCallLambda(0.01f, () => {
                        Plugin.RestartGame();
                    });
                }
                UIMainMenu.DoTooltipOnLastGUIRect("mmst_button_restart_desc");
            } else {
                if (GUILayout.Button(Lang.Get("mmst_button_checkupdate"))) {
                    if (!_CHECKFORUPDATEBUTTON_OnStageSuccess_isBound) {
                        _CHECKFORUPDATEBUTTON_OnStageSuccess_isBound = true;
                        Multiupdater.AutoUpdater.OnStageSuccess += () => {
                            AutoUpdater.LogMessage("Update staging succesful.");
                            if (Plugin.IsExeSameAsInSteam()) {
                                _CHECKFORUPDATEBUTTON_IS_RESTART = true;
                            }
                        };
                    }
                    Multiupdater.AutoUpdater.StartChecking();
                    CasualtiesTogetherUtils.Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                }
                UIMainMenu.DoTooltipOnLastGUIRect("mmst_button_checkupdate_desc");
            }

            GUILayout.FlexibleSpace();

        }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        {
            var ogwordwrap = UnityEngine.GUI.skin.label.wordWrap;
            UnityEngine.GUI.skin.label.wordWrap = true;

            GUILayout.Label(resuuuu);

            UnityEngine.GUI.skin.label.wordWrap = ogwordwrap;
        }
        GUILayout.EndHorizontal();
    }
}


