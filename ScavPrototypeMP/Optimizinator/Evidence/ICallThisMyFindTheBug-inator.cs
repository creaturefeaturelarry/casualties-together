using BepInEx.Configuration;
using HarmonyLib;

namespace KrokV5Optimization;

// one switch for every evidence logger. off means the hooks are not installed.
internal static class FindTheBug {
    internal static ConfigEntry<bool> Enabled;

    internal static bool On => Enabled != null && Enabled.Value;

    internal static void Install(Harmony harmony) {
    }

    internal static void Write(string line) {
    }
}