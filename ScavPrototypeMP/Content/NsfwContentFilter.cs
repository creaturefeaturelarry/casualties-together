using CasualtiesTogetherUtils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Together;

// hide/block NSFW server names + descriptions locally
public static class NsfwContentFilter {
    public const string DefaultSafeServerName = "Expedition Redacted";

    private const string ResourceSuffix = "nsfw-server-filter.json";

    private static string[] _terms;
    private static bool _loaded;
    public static bool LastCreateWasRejected { get; private set; }

    public static bool ContainsBlocked(string text) {
        EnsureLoaded();
        if (_terms == null || _terms.Length == 0 || string.IsNullOrEmpty(text)) {
            return false;
        }
        string hay = Normalize(text);
        if (hay.Length == 0) {
            return false;
        }
        for (int i = 0; i < _terms.Length; i++) {
            string term = _terms[i];
            if (term.Length > 0 && hay.IndexOf(term, StringComparison.Ordinal) >= 0) {
                return true;
            }
        }
        return false;
    }

    public static bool IsListingBlocked(string name, string description) {
        return ContainsBlocked(name) || ContainsBlocked(description);
    }
    public static bool RejectCreateIfBlocked() {
        LastCreateWasRejected = false;
        NetPublicServerInfo info = Net.MyServerInfo;
        if (info == null) {
            return false;
        }
        if (!IsListingBlocked(info.name, info.description)) {
            return false;
        }

        ResetToSafeDefaults();
        ShowBlockedAlert();
        LastCreateWasRejected = true;
        return true;
    }

    public static void ResetToSafeDefaults() {
        Multiplayer.rules = new MultiplayerGameRules();
        Multiplayer.SetServerName(DefaultSafeServerName);
        Multiplayer.SetServerDescription("");
#if RELEASE
        try {
            PlayerPrefs.SetString("KrokoshaMultiplayer_LastMyLobbyName", DefaultSafeServerName);
            PlayerPrefs.Save();
        } catch { /* ignore */ }
#endif
    }

    public static void ShowBlockedAlert() {
        string msg = Lang.Get("nsfw_filter_blocked_alert");
        Multiplayer.DoMultiplayerStatusMessageLog(msg);

        try {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.DoAlert(msg, important: true);
            }
        } catch { /* menu may lack PlayerCamera */ }

        try {
            if (UIChoicePrompt.instance != null) {
                UIChoicePrompt.ShowPrompt(new UIChoicePrompt.Prompt {
                    name = Lang.Get("nsfw_filter_blocked_title"),
                    text = msg,
                    timeout = 14f,
                    defaultcanignore = true,
                    pos = new Vector2(0.5f, 0.55f),
                    size = new Vector2(0.55f, 0.22f),
                    choices = new[] {
                        new UIChoicePrompt.Prompt.PromptChoice {
                            text = Lang.Get("ok"),
                            action = () => { }
                        }
                    }
                });
            }
        } catch (Exception e) {
            Plugin.log?.LogWarning("NsfwContentFilter prompt: " + e.Message);
        }
    }

    internal static string Normalize(string input) {
        if (string.IsNullOrEmpty(input)) {
            return "";
        }
        StringBuilder sb = new StringBuilder(input.Length);
        foreach (char c in input) {
            char mapped = MapLeet(char.ToLowerInvariant(c));
            if (char.IsLetterOrDigit(mapped)) {
                sb.Append(mapped);
            }
        }
        return sb.ToString();
    }

    private static char MapLeet(char c) {
        switch (c) {
            case '0': return 'o';
            case '1': return 'i';
            case '!': return 'i';
            case '|': return 'i';
            case '3': return 'e';
            case '4': return 'a';
            case '@': return 'a';
            case '5': return 's';
            case '$': return 's';
            case '7': return 't';
            case '+': return 't';
            default: return c;
        }
    }

    private static void EnsureLoaded() {
        if (_loaded) {
            return;
        }
        _loaded = true;
        try {
            LoadFromEmbedded();
        } catch (Exception e) {
            Plugin.log?.LogError("NsfwContentFilter load failed: " + e);
            _terms = Array.Empty<string>();
        }
    }

    private static void LoadFromEmbedded() {
        Assembly asm = typeof(NsfwContentFilter).Assembly;
        string resourceName = null;
        foreach (string name in asm.GetManifestResourceNames()) {
            if (name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) {
                resourceName = name;
                break;
            }
        }
        if (resourceName == null) {
            Plugin.log?.LogError("NsfwContentFilter: embedded " + ResourceSuffix + " not found");
            _terms = Array.Empty<string>();
            return;
        }

        using Stream stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) {
            _terms = Array.Empty<string>();
            return;
        }
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        string json = reader.ReadToEnd();
        JObject root = JObject.Parse(json);
        HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);

        void AddRaw(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) {
                return;
            }
            string n = Normalize(raw);
            if (n.Length > 0) {
                set.Add(n);
            }
        }

        if (root["blocked"] is JArray blocked) {
            foreach (JToken t in blocked) {
                AddRaw(t?.ToString());
            }
        }
        if (root["blocked_phrases"] is JArray phrases) {
            foreach (JToken t in phrases) {
                AddRaw(t?.ToString());
            }
        }

        _terms = new string[set.Count];
        set.CopyTo(_terms);
        Array.Sort(_terms, (a, b) => b.Length.CompareTo(a.Length));
        Plugin.log?.LogInfo($"NsfwContentFilter loaded {_terms.Length} terms from {resourceName}");
    }
}