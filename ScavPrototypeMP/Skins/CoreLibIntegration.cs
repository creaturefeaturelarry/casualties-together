using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CUCoreLib.Data;
using CUCoreLib.Registries;

// CUCoreLib only shows a tab for a category that owns an option. Register one
// anchor option to create the "Skin Replacer" tab, take both the option and its
// settings row out again, and keep the tab for the bug report buttons.
internal static class CoreLibIntegration {
    private const string CategoryName = "Skin Replacer";
    private const string AnchorOptionId = "spritereplacer.tab";

    private static bool _tried;
    private static int _categoryIndex = -1;

    // -1 until the CUCoreLib category exists. the bug report row reads this.
    internal static int BugReportCategoryIndex => _categoryIndex;

    internal static void TryRegister() {
        if (_tried) {
            return;
        }

        _tried = true;

        if (!IsAvailable()) {
            if (SpriteReplacerPlugin.Log != null) {
                SpriteReplacerPlugin.Log.LogInfo(
                    "CUCoreLib not found. The bug report tab is disabled.");
            }
            return;
        }

        try {
            RegisterCategory();
        } catch (Exception ex) {
            if (SpriteReplacerPlugin.Log != null) {
                SpriteReplacerPlugin.Log.LogWarning(
                    "Could not register the Skin Replacer settings tab: " + ex);
            }
        }
    }

    private static bool IsAvailable() {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (string.Equals(assembly.GetName().Name, "CUCoreLib", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterCategory() {
        ModOptionDefinition anchor = ModOptionDefinition.Int(
            AnchorOptionId,
            CategoryName,
            "Bug reports and links for the sprite replacer.",
            CategoryName,
            0,
            0,
            0,
            null);

        if (!ModOptionsRegistry.Register(anchor)) {
            if (SpriteReplacerPlugin.Log != null) {
                SpriteReplacerPlugin.Log.LogWarning(
                    "CUCoreLib rejected the Skin Replacer category.");
            }
            return;
        }

        _categoryIndex = (int)anchor.Category;
        RemoveAnchor();

        if (SpriteReplacerPlugin.Log != null) {
            SpriteReplacerPlugin.Log.LogInfo(
                "Skin Replacer bug report tab registered at category "
                + _categoryIndex + ".");
        }
    }

    // the anchor exists only to create the tab. remove its settings row so the
    // tab shows the bug report buttons and nothing else.
    private static void RemoveAnchor() {
        List<ModOptionDefinition> options = GetRegisteredOptions();
        if (options != null) {
            options.RemoveAll(option => option != null && option.Id == AnchorOptionId);
        }

        if (Settings.settings != null) {
            Settings.settings.RemoveAll(setting =>
                setting != null && setting.name == AnchorOptionId);
        }
    }

    private static List<ModOptionDefinition> GetRegisteredOptions() {
        FieldInfo field = typeof(ModOptionsRegistry).GetField(
            "RegisteredOptions", BindingFlags.Static | BindingFlags.NonPublic);
        return field == null ? null : field.GetValue(null) as List<ModOptionDefinition>;
    }

    // the MP build keeps the slot and backup count in the Together settings tab.
    internal static void StoreActiveSlot(string slot) {
    }

    internal static void StoreBackupCount(int count) {
    }
}
