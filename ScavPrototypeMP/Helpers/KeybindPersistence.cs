using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

// VC mode saves properly so putting keybinds with it makes sense. Vanilla settings saves can discard absent mods' entries without deleting these preferences
internal static class KeybindPersistence {
    const string PrefPrefix = "Keybind_";
    static int loadingDepth;
    static int applyingDepth;

    internal static void RestoreSavedDefaults(List<Setting> defaults) {
        // DefaultSettings also serves reset, which must return actual defaults
        if (loadingDepth == 0 || defaults == null) return;
        var prefs = SettingsJsonThing.json;
        foreach (var setting in defaults) {
            if (setting is SettingKeybind bind
                && Settings_DefaultSettings_Patch.custom_binds.ContainsKey(bind.name)) {
                bind.value = (KeyCode)prefs.GetInt(PrefPrefix + bind.name, (int)bind.value);
            }
        }
        // The game applies settings.json values by name
    }

    static void Capture() {
        if (Settings.settings == null) return;
        var prefs = SettingsJsonThing.json;
        bool haveBinds = false;
        foreach (var setting in Settings.settings) {
            if (!(setting is SettingKeybind bind)
                || !Settings_DefaultSettings_Patch.custom_binds.ContainsKey(bind.name)) continue;
            prefs.SetInt(PrefPrefix + bind.name, (int)bind.value);
            haveBinds = true;
        }
        if (haveBinds) prefs.Save();
    }

    [HarmonyPatch(typeof(Settings), nameof(Settings.LoadSettings))]
    static class LoadPatch {
        static void Prefix() { loadingDepth++; }
        static void Finalizer(Exception __exception) {
            loadingDepth--;
            if (__exception == null && loadingDepth == 0) Capture();
        }
    }

    // Rebinding calls Apply, but the game normally waits until the settings menu closes to save. Mod toggles can quit directly from that menu.
    [HarmonyPatch(typeof(SettingKeybind), nameof(SettingKeybind.Apply))]
    static class RebindPatch {
        static void Postfix(SettingKeybind __instance) {
            if (loadingDepth != 0 || applyingDepth != 0 || Settings.settings == null
                || !Settings.settings.Contains(__instance)) return;
            try {
                // I noticed that the vanilla keybinds sometimes changed too, so I put them in here as well just in case
                Settings.SaveSettings();
            } catch (Exception e) {
                log.error("Could not save changed keybind: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(Settings), nameof(Settings.ApplyAll))]
    static class ApplyAllPatch {
        static void Prefix() { applyingDepth++; }
        static void Finalizer() { applyingDepth--; }
    }

    [HarmonyPatch(typeof(Settings), nameof(Settings.SaveSettings))]
    static class SavePatch {
        static void Postfix() { Capture(); }
    }
}
