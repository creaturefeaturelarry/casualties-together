using CasualtiesTogetherUtils;
using System;
using UnityEngine;

namespace Together;

// title/main menu uncapped framerate burns CPU for no reason (menu particles/UI).
// only intervene when the Video "framerate" setting is 0 (no cap -> targetFrameRate -1).
// any explicit cap (including 60) is left alone.
internal static class TitleScreenFpsCap {
    const int MenuCap = 60;

    static bool _menuCapping;

    internal static void Tick() {
        bool onTitle = Util.IsInMainMenu();
        int setting = ReadFramerateSetting();

        if (onTitle && setting == 0) {
            if (Application.targetFrameRate != MenuCap) {
                Application.targetFrameRate = MenuCap;
            }
            _menuCapping = true;
            return;
        }

        if (!_menuCapping) {
            return;
        }

        _menuCapping = false;
        // leaving the title screen (or user set an explicit cap): restore the setting's apply semantics.
        Application.targetFrameRate = setting == 0 ? -1 : Math.Max(10, setting);
    }

    static int ReadFramerateSetting() {
        try {
            Settings.EnsureLoaded();
            SettingInt framerate = Settings.Get<SettingInt>("framerate");
            return framerate != null ? framerate.value : 0;
        } catch {
            return 0;
        }
    }
}