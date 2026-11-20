using BepInEx.Configuration;
using HarmonyLib;
using System;
using Together;

namespace KrokV5Optimization;

// one switch for every evidence logger. off means the hooks are not installed.
// mp verbose 1 turns this on even when the config entry is off.
internal static class FindTheBug {
    internal static ConfigEntry<bool> Enabled;
    static Harmony _harmony;
    static bool _armed;

    internal static bool On => Multiplayer.verbose || (Enabled != null && Enabled.Value);

    internal static void Install(Harmony harmony) {
        _harmony = harmony;
        if (Enabled == null && Plugin.main != null) {
            Enabled = Plugin.main.Config.Bind("Probe", "FindTheBug", true, "Evidence loggers. Off skips their hooks. Also on while mp verbose is on.");
        }
        Arm();
    }

    internal static void Arm() {
        if (_armed || _harmony == null || !On) {
            return;
        }
        _armed = true;
        try {
            EvidenceCache.Arm(_harmony);
        } catch (Exception ex) {
            // a patch that will not compile used to escape as "Command mpverbose failed".
            Write("cache arm failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    internal static void Write(string line) {
        if (!On || line == null) {
            return;
        }
        log.l("[findthebuginator] " + line, false);
    }
}