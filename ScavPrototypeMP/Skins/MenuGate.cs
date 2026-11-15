using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class MenuGate {
    private static bool _resolved;
    private static FieldInfo _didIntroField;
    private static FieldInfo _textObjField;

    internal static bool ShouldShowButton() {
        if (MpMenuButtonVisible()) {
            return true;
        }
        bool mainMenu = SceneManager.GetActiveScene().name == "PreGen" && IsMainMenuVisible();
        bool pauseMenu = PauseMenuProbe.IsOpen()
                      && !GameplayOverlayProbe.IsOpen()
                      && !SettingsMenuProbe.IsOpen();
        return mainMenu || pauseMenu;
    }

    // the lobby keeps the mp button up while the intro text is still alive, which used to hide skins.
    static bool MpMenuButtonVisible() {
        GameObject button = Together.UIManager.MP_MENU_BUTTON;
        return button != null && button.activeInHierarchy;
    }

    // didIntro flips when the intro starts. also require the intro text to be gone.
    internal static bool IsMainMenuVisible() {
        Resolve();
        if (_didIntroField == null) {
            return false;
        }

        if (!(bool)_didIntroField.GetValue(null)) {
            return false;
        }

        if (_textObjField == null) {
            return true;
        }
        // destroyed textObj compares equal to null only through unity's ==.
        object val = _textObjField.GetValue(null);
        if (val is UnityEngine.Object uo) {
            return uo == null;
        }

        return val == null;
    }

    private static void Resolve() {
        if (_resolved) {
            return;
        }

        _resolved = true;

        Type preRunType = typeof(Limb).Assembly.GetType("PreRunScript");
        if (preRunType != null) {
            _didIntroField = preRunType.GetField("didIntro",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (_didIntroField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PreRunScript.didIntro via reflection");
        }

        Type scrollType = typeof(Limb).Assembly.GetType("ScrollableText");
        if (scrollType != null) {
            _textObjField = scrollType.GetField("textObj",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (_textObjField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find ScrollableText.textObj via reflection");
        }
    }
}

internal static class PauseMenuProbe {
    private static bool _resolved;
    private static FieldInfo _mainField;
    private static FieldInfo _pauseContainerField;
    private static PropertyInfo _pausedProperty;
    private static FieldInfo _pausedField;

    internal static bool IsOpen() {
        Resolve();
        if (_mainField == null || _pauseContainerField == null) {
            return false;
        }

        object main = _mainField.GetValue(null);
        if (!IsAlive(main)) {
            return false;
        }

        GameObject container = _pauseContainerField.GetValue(main) as GameObject;
        if (container == null || !container.activeInHierarchy) {
            return false;
        }

        if (_pausedProperty != null) {
            MethodInfo getter = _pausedProperty.GetGetMethod(true);
            object value = getter != null && getter.IsStatic
                ? _pausedProperty.GetValue(null, null)
                : _pausedProperty.GetValue(main, null);
            return value is bool paused && paused;
        }

        if (_pausedField != null) {
            return _pausedField.GetValue(main) is bool pausedField && pausedField;
        }

        return true;
    }

    private static void Resolve() {
        if (_resolved) {
            return;
        }

        _resolved = true;

        Type type = typeof(Limb).Assembly.GetType("PauseHandler");
        if (type == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PauseHandler via reflection");
            return;
        }

        _mainField = type.GetField("main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        _pauseContainerField = type.GetField("pauseContainer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _pausedProperty = type.GetProperty("paused", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        _pausedField = type.GetField("isPaused", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (_mainField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PauseHandler.main via reflection");
        }

        if (_pauseContainerField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PauseHandler.pauseContainer via reflection");
        }

        if (_pausedProperty == null && _pausedField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PauseHandler.paused via reflection");
        }
    }

    private static bool IsAlive(object value) {
        if (value is UnityEngine.Object unityObject) {
            return unityObject != null;
        }

        return value != null;
    }
}

internal static class GameplayOverlayProbe {
    private static bool _resolved;
    private static FieldInfo _mainField;
    private static FieldInfo _woundViewField;
    private static FieldInfo _craftingPanelField;
    private static FieldInfo _tradeMenuField;
    private static FieldInfo _radialOpenField;

    internal static bool IsOpen() {
        Resolve();
        if (_mainField == null) {
            return false;
        }

        object main = _mainField.GetValue(null);
        if (!IsAlive(main)) {
            return false;
        }

        if (IsActive(_woundViewField, main)) {
            return true;
        }

        if (IsActive(_craftingPanelField, main)) {
            return true;
        }

        if (IsActive(_tradeMenuField, main)) {
            return true;
        }

        if (_radialOpenField != null && _radialOpenField.GetValue(main) is bool radial && radial) {
            return true;
        }

        return false;
    }

    private static void Resolve() {
        if (_resolved) {
            return;
        }

        _resolved = true;

        Type type = typeof(Limb).Assembly.GetType("PlayerCamera");
        if (type == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PlayerCamera via reflection");
            return;
        }

        _mainField = type.GetField("main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        _woundViewField = type.GetField("woundView", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _craftingPanelField = type.GetField("craftingPanel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _tradeMenuField = type.GetField("tradeMenu", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _radialOpenField = type.GetField("radialOpen", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (_mainField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find PlayerCamera.main via reflection");
        }
    }

    private static bool IsActive(FieldInfo field, object instance) {
        if (field == null) {
            return false;
        }

        GameObject go = field.GetValue(instance) as GameObject;
        return go != null && go.activeInHierarchy;
    }

    private static bool IsAlive(object value) {
        if (value is UnityEngine.Object unityObject) {
            return unityObject != null;
        }

        return value != null;
    }
}

internal static class SettingsMenuProbe {
    private static bool _resolved;
    private static FieldInfo _instanceField;

    internal static bool IsOpen() {
        Resolve();
        if (_instanceField == null) {
            return false;
        }

        object instance = _instanceField.GetValue(null);
        if (instance is UnityEngine.Object unityObject) {
            return unityObject != null;
        }

        return instance != null;
    }

    private static void Resolve() {
        if (_resolved) {
            return;
        }

        _resolved = true;

        Type type = typeof(Limb).Assembly.GetType("SettingsMenu");
        if (type == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find SettingsMenu via reflection");
            return;
        }

        _instanceField = type.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (_instanceField == null) {
            SpriteReplacerPlugin.Log.LogWarning("Could not find SettingsMenu.instance via reflection");
        }
    }
}