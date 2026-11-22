using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

// The game fills the KeyBinds cache from Settings.ApplyAll. If that step
// stops before the input settings, KeyBinds.GetBind returns KeyCode.None for
// every action: movement, Escape, wound view, and mouse actions fail, while
// raw Input checks still work. Cache the binds again after each scene load.
internal static class KeybindRecovery
{
    private static bool _loggedState;

    internal static void Apply()
    {
        KeyCode beforeUp = KeyBinds.GetBind("up");
        KeyCode beforePause = KeyBinds.GetBind("pause");

        try
        {
            Settings.EnsureLoaded();
        }
        catch (Exception ex)
        {
            SpriteReplacerPlugin.Log.LogWarning("Settings load failed: " + ex.Message);
        }

        KeyCode afterLoadUp = KeyBinds.GetBind("up");
        int inSettings = CountInSettings();
        int count = CacheFromSettings();
        if (count == 0) count = CacheFromFile();
        if (count == 0) count = CacheFromDefaults();

        if (!_loggedState)
        {
            _loggedState = true;
            SpriteReplacerPlugin.Log.LogInfo(
                "Keybind cache: before up=" + beforeUp + " pause=" + beforePause
                + ", after load up=" + afterLoadUp + ", " + inSettings
                + " in settings, restored " + count + " binds, now up="
                + KeyBinds.GetBind("up") + " pause=" + KeyBinds.GetBind("pause") + ".");
        }
    }

    private static int CountInSettings()
    {
        List<Setting> settings = Settings.settings;
        if (settings == null) return 0;

        int count = 0;
        foreach (Setting setting in settings)
        {
            if (setting is SettingKeybind) count++;
        }
        return count;
    }

    private static int CacheFromSettings()
    {
        List<Setting> settings = Settings.settings;
        return settings == null ? 0 : Cache(settings);
    }

    // Fallback for a settings load that stopped before the keybind entries.
    private static int CacheFromFile()
    {
        try
        {
            string path = Path.Combine(Application.persistentDataPath, "settings.json");
            if (!File.Exists(path)) return 0;

            List<SettingSaveData> saved =
                JsonConvert.DeserializeObject<List<SettingSaveData>>(File.ReadAllText(path));
            if (saved == null) return 0;

            int count = 0;
            foreach (SettingSaveData entry in saved)
            {
                if (entry == null || string.IsNullOrEmpty(entry.name)) continue;

                int code;
                try
                {
                    code = Convert.ToInt32(entry.value);
                }
                catch
                {
                    continue;
                }

                KeyBinds.CacheBind(new SettingKeybind
                {
                    name = entry.name,
                    value = (KeyCode)code
                });
                count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            SpriteReplacerPlugin.Log.LogWarning(
                "Could not read keybinds from settings.json: " + ex.Message);
            return 0;
        }
    }

    private static int CacheFromDefaults()
    {
        try
        {
            return Cache(Settings.DefaultSettings());
        }
        catch (Exception ex)
        {
            SpriteReplacerPlugin.Log.LogWarning(
                "Could not read the default keybinds: " + ex.Message);
            return 0;
        }
    }

    private static int Cache(List<Setting> settings)
    {
        int count = 0;
        foreach (Setting setting in settings)
        {
            if (!(setting is SettingKeybind keybind)) continue;
            KeyBinds.CacheBind(keybind);
            count++;
        }
        return count;
    }
}
