using BepInEx.Logging;
using System;
using System.Reflection;

// cucorelib flags are optional. a miss stays a log line.
internal static class BuildInfo {
    internal static bool IsFullGame { get; private set; }
    internal static bool Known { get; private set; }

    internal static void Log(ManualLogSource log) {
        if (log == null) {
            return;
        }

        try {
            Type build = FindType("CUCoreLib.Helpers.GameBuildInfo");
            if (build != null) {
                PropertyInfo full = build.GetProperty("IsFullGame",
                    BindingFlags.Public | BindingFlags.Static);
                if (full != null && full.PropertyType == typeof(bool)) {
                    IsFullGame = (bool)full.GetValue(null);
                    Known = true;
                }
            }

            if (!Known) {
                Type utils = FindType("CUCoreLib.Helpers.CUCoreUtils");
                MethodInfo isDemo = utils?.GetMethod("IsDemo",
                    BindingFlags.Public | BindingFlags.Static);
                if (isDemo != null) {
                    IsFullGame = !(bool)isDemo.Invoke(null, null);
                    Known = true;
                }
            }
        } catch (Exception ex) {
            log.LogDebug("Build detection failed: " + ex.Message);
        }

        string name = Known ? (IsFullGame ? "full game" : "demo") : "unknown";
        log.LogInfo("Game build: " + name + ".");
    }

    private static Type FindType(string name) {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            Type type = assembly.GetType(name, false);
            if (type != null) {
                return type;
            }
        }

        return null;
    }
}