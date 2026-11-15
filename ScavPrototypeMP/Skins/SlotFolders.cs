using System;
using System.Collections.Generic;
using System.IO;

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
}