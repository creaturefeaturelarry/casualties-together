using System;
using System.Collections.Generic;
using System.Reflection;

namespace Multiupdater;

internal static class UpdaterText {

    static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["au_checking"] = "Checking for updates",
        ["au_update_found"] = "Update found! {0} -> {1}",
        ["au_versions"] = "Local version: {0}  Remote version: {1}",
        ["au_github_unreachable"] = "GitHub unreachable",
        ["au_latest"] = "You're running the latest version.",
        ["au_outdated"] = "The multiplayer mod is out of date.",
        ["au_prerelease"] = "You're running the pre-release build.",
        ["au_github_unreachable_version"] = "GitHub unreachable when retrieving the version.",
        ["au_stage_success"] = "Update ready.",
        ["au_downloading"] = "Downloading",
        ["au_github_unreachable_long"] = "GitHub seems to be unreachable. Check your internet connection and GitHub's status.",
        ["au_unpacking"] = "Unpacking",
        ["au_unzip_failed"] = "Failed to unpack the update. The download may be corrupted. Please try again.",
        ["au_staged"] = "Update ready! Restart the game to apply it.",
        ["mmst_button_checkupdate"] = "Check for Updates",
        ["mmst_button_checkupdate_desc"] = "Automatically install a new version of MP mod, if available.",
        ["mmst_button_restart"] = "Restart the Game",
        ["mmst_button_restart_desc"] = "Restart to apply update.",
    };

    // After a failed lookup do not hammer the main mod on every GUI frame (DrawUpdateButton
    // runs from OnGUI). Environment.TickCount keeps this helper free of Unity dependencies.
    const int FailureBackoffMs = 5000;
    static MethodInfo _langGet;
    static int _nextResolveAttempt = Environment.TickCount;
    static bool _failureLogged;

    internal static string Get(string key) {
        if (String.IsNullOrEmpty(key)) {
            return String.Empty;
        }

        if (TryGetFromMainMod(key, out string localized)) {
            return localized;
        }

        if (English.TryGetValue(key, out string fallback)) {
            return fallback;
        }

        // Unknown key: show something readable instead of an exception or an empty label.
        return key;
    }

    internal static string Format(string key, params object[] arguments) {
        string format = Get(key);
        if (arguments == null || arguments.Length == 0) {
            return format;
        }

        try {
            return String.Format(format, arguments);
        } catch (FormatException) {
            // A bad translation must never break the updater.
            return format;
        }
    }

    static bool TryGetFromMainMod(string key, out string text) {
        text = null;
        try {
            // Keep trying until the main assembly is present; the updater must not cache a
            // miss that happened before the main mod finished loading.
            if (_langGet == null) {
                if (IsInFailureBackoff()) {
                    return false;
                }
                _langGet = FindLangGet();
                if (_langGet == null) {
                    _nextResolveAttempt = Environment.TickCount + FailureBackoffMs;
                    return false;
                }
            }

            // A missing key should not spam the log.
            object result = _langGet.Invoke(null, new object[] { key, true });
            string value = result as string;
            if (String.IsNullOrEmpty(value)) {
                return false;
            }
            if (value.StartsWith("ERROR_NO_LOCALE_", StringComparison.Ordinal)) {
                return false;
            }

            text = value;
            return true;
        } catch (Exception e) {
            // Drop the cached method: the main assembly may have been reloaded, and a
            // persistent failure must not be retried on every GUI frame.
            _langGet = null;
            _nextResolveAttempt = Environment.TickCount + FailureBackoffMs;

            if (!_failureLogged) {
                _failureLogged = true;
                Exception cause = (e is TargetInvocationException invocation && invocation.InnerException != null)
                    ? invocation.InnerException
                    : e;
                try {
                    AutoUpdater.LogError($"UpdaterText: falling back to English ({cause.GetType().Name}: {cause.Message})");
                } catch {
                    
                }
            }
            return false;
        }
    }

    static bool IsInFailureBackoff() {
        // Unchecked subtraction also behaves correctly when TickCount wraps around.
        return unchecked(Environment.TickCount - _nextResolveAttempt) < 0;
    }

    static MethodInfo FindLangGet() {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            Type langType;
            try {
                langType = assembly.GetType("Together.Lang", throwOnError: false);
            } catch {
                continue;
            }
            if (langType == null) {
                continue;
            }

            foreach (MethodInfo method in langType.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
                if (IsLangGet(method)) {
                    return method;
                }
            }
        }

        return null;
    }

    // Match only the exact shape we rely on: string Get(in string, in bool).
    static bool IsLangGet(MethodInfo method) {
        if (method.Name != "Get" || method.ReturnType != typeof(string)) {
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 2
            && IsStringParameter(parameters[0].ParameterType)
            && IsBoolParameter(parameters[1].ParameterType);
    }

    static bool IsStringParameter(Type type) {
        return type == typeof(string) || type == typeof(string).MakeByRefType();
    }

    static bool IsBoolParameter(Type type) {
        return type == typeof(bool) || type == typeof(bool).MakeByRefType();
    }
}
