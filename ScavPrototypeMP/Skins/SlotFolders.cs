using System;
using System.Collections.Generic;
using System.IO;
using Together;

// st1..st9 still work and get renamed to slotN.
internal static class SlotFolders {
    // custom slots are 1..9 only. 0 is reserved for SkinSettings.DefaultFolder via numpad 0.
    internal static string SlotId(int index) {
        if (index < 1 || index > 9) {
            return null;
        }

        return "slot" + index;
    }

    internal static string LegacySlotId(int index) {
        if (index < 1 || index > 9) {
            return null;
        }

        return "st" + index;
    }

    internal static int Index(string slot) {
        if (string.IsNullOrEmpty(slot)) {
            return -1;
        }

        // slot0 / st0 are not custom slots.
        if (string.Equals(slot, "slot0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slot, "st0", StringComparison.OrdinalIgnoreCase)) {
            return -1;
        }

        int number = ParseNumber(slot, "slot");
        if (number < 0) {
            number = ParseNumber(slot, "st");
        }

        return number < 0 ? -1 : number - 1;
    }

    internal static string Normalize(string slot) {
        int index = Index(slot);
        return index < 0 ? null : SlotId(index + 1);
    }

    internal static string DisplayName(string slot) {
        if (string.Equals(slot, "default", StringComparison.OrdinalIgnoreCase)) {
            return "Default";
        }

        int index = Index(slot);
        return index < 0 ? slot : "Slot " + (index + 1);
    }

    internal static List<string> Migrate(string root, int count) {
        List<string> messages = new List<string>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) {
            return messages;
        }

        for (int i = 1; i <= count; i++) {
            string legacy = Path.Combine(root, LegacySlotId(i));
            string current = Path.Combine(root, SlotId(i));
            if (!Directory.Exists(legacy)) {
                continue;
            }

            if (Directory.Exists(current)) {
                messages.Add("Both " + legacy + " and " + current + " exist. The mod uses " + current + ".");
                continue;
            }

            try {
                Directory.Move(legacy, current);
                messages.Add("Renamed " + legacy + " to " + current + ".");
            } catch (Exception ex) {
                messages.Add("Could not rename " + legacy + " to " + current + ": " + ex.Message);
            }
        }

        return messages;
    }

    private static int ParseNumber(string slot, string prefix) {
        if (slot.Length != prefix.Length + 1) {
            return -1;
        }

        if (!slot.StartsWith(prefix, StringComparison.Ordinal)) {
            return -1;
        }

        char digit = slot[prefix.Length];
        if (digit < '1' || digit > '9') {
            return -1;
        }

        return digit - '0';
    }

    // old installs kept slot1..slot9 and default next to CustomSprites itself.
    internal static void MigrateLegacyIntoExp() {
        string exp = Path.Combine(SpriteReplacerPlugin.SpritesPath, SpriteReplacerPlugin.SpeciesNames[0]);
        Directory.CreateDirectory(exp);

        List<string> names = new List<string> { SkinSettings.DefaultFolder, "st0", "slot0" };
        for (int i = 1; i <= SpriteReplacerPlugin.SlotCount; i++) {
            names.Add(SlotId(i));
            names.Add(LegacySlotId(i));
        }

        foreach (string name in names) {
            string src = Path.Combine(SpriteReplacerPlugin.SpritesPath, name);
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
                SpriteReplacerPlugin.Log.LogWarning("Could not move " + src + " to Exp: " + ex.Message);
            }
        }
    }

    internal static void EnsureCustomSpritesStructure() {
        for (int species = 0; species < SpriteReplacerPlugin.SpeciesNames.Length; species++) {
            string root = SpriteReplacerPlugin.SpeciesRoot(species);
            Directory.CreateDirectory(root);
            Migrate(root, SpriteReplacerPlugin.SlotCount);

            string fallback = Path.Combine(root, SkinSettings.DefaultFolder);
            Directory.CreateDirectory(Path.Combine(fallback, "Body"));
            Directory.CreateDirectory(Path.Combine(fallback, "Head"));
            MergeLegacySlotZeroIntoDefault(root, fallback);

            for (int i = 1; i <= SpriteReplacerPlugin.SlotCount; i++) {
                string slot = Path.Combine(root, SlotId(i));
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
                SpriteReplacerPlugin.Log.LogWarning("Could not merge " + legacy + " into default: " + ex.Message);
            }
        }
    }
}