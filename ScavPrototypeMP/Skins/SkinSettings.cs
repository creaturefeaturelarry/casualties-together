using System;
using System.Reflection;

namespace Together;

// 0 is CustomSprites/<species>/default.
// only experiment is applied. milky and dune are stored until their names are wired.
public static class SkinSettings {
    public const string DefaultFolder = "default";

    [SettingDeclarerThingyBool("setting_skins_enable")]
    public static bool skinsEnable = true;

    [SettingDeclarerThingyChoice("setting_skins_experiment", new string[] {
        "setting_skins_choice_default",
        "setting_skins_choice_slot1",
        "setting_skins_choice_slot2",
        "setting_skins_choice_slot3",
        "setting_skins_choice_slot4",
        "setting_skins_choice_slot5",
        "setting_skins_choice_slot6",
        "setting_skins_choice_slot7",
        "setting_skins_choice_slot8",
        "setting_skins_choice_slot9",
    })]
    public static int experimentSkin = 0;

    [SettingDeclarerThingyChoice("setting_skins_milky", new string[] {
        "setting_skins_choice_default",
        "setting_skins_choice_slot1",
        "setting_skins_choice_slot2",
        "setting_skins_choice_slot3",
        "setting_skins_choice_slot4",
        "setting_skins_choice_slot5",
        "setting_skins_choice_slot6",
        "setting_skins_choice_slot7",
        "setting_skins_choice_slot8",
        "setting_skins_choice_slot9",
    })]
    public static int milkySkin = 0;

    [SettingDeclarerThingyChoice("setting_skins_dune", new string[] {
        "setting_skins_choice_default",
        "setting_skins_choice_slot1",
        "setting_skins_choice_slot2",
        "setting_skins_choice_slot3",
        "setting_skins_choice_slot4",
        "setting_skins_choice_slot5",
        "setting_skins_choice_slot6",
        "setting_skins_choice_slot7",
        "setting_skins_choice_slot8",
        "setting_skins_choice_slot9",
    })]
    public static int duneSkin = 0;

    public static void EnsureLoaded() {
        UIMainMenu.GetSettingsFromStaticClass(typeof(SkinSettings));
    }

    public static string Folder(int index) {
        if (index <= 0 || index > 9) {
            return DefaultFolder;
        }

        return SlotFolders.SlotId(index);
    }

    public static int IndexOf(string folder) {
        if (string.IsNullOrEmpty(folder)) {
            return 0;
        }

        if (string.Equals(folder, DefaultFolder, StringComparison.OrdinalIgnoreCase)) {
            return 0;
        }

        int slotIndex = SlotFolders.Index(folder);
        if (slotIndex < 0) {
            return -1;
        }

        return slotIndex + 1;
    }

    // charType 0 experiment, 1 milky, 2 dune. no body counts as experiment, the only wired species.
    public static void Remember(int charType, string folder) {
        int index = IndexOf(folder);
        if (index < 0) {
            return;
        }

        EnsureLoaded();
        FieldInfo field = SpeciesField(charType);
        if (field == null) {
            return;
        }

        SettingDeclarerThingyChoiceAttribute attr = field.GetCustomAttribute<SettingDeclarerThingyChoiceAttribute>();
        if (attr == null) {
            return;
        }

        attr.Save(field, index);
        SettingsJsonThing.json.Save();
    }

    static FieldInfo SpeciesField(int charType) {
        string name = null;
        if (charType == 0) {
            name = nameof(experimentSkin);
        } else if (charType == 1) {
            name = nameof(milkySkin);
        } else if (charType == 2) {
            name = nameof(duneSkin);
        }

        if (name == null) {
            return null;
        }

        return typeof(SkinSettings).GetField(name, BindingFlags.Static | BindingFlags.Public);
    }
}