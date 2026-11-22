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
    private const string WordsResourceSuffix = "name-filter-words.txt";

    private static NameFilterMatcher _matcher;
    private static bool _loaded;
    public static bool LastCreateWasRejected { get; private set; }

    public static bool ContainsBlocked(string text) {
        EnsureLoaded();
        if (_matcher == null || string.IsNullOrEmpty(text)) {
            return false;
        }
        return _matcher.ContainsBlocked(text);
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

    internal static string Normalize(string input) => NameFilterMatcher.Normalize(input);

    private static void EnsureLoaded() {
        if (_loaded) {
            return;
        }
        _loaded = true;
        try {
            LoadFromEmbedded();
        } catch (Exception e) {
            Plugin.log?.LogError("NsfwContentFilter load failed: " + e);
            _matcher = null;
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
            return;
        }

        string json = ReadResource(asm, resourceName);
        if (json == null) {
            return;
        }
        JObject root = JObject.Parse(json);
        var terms = new List<string>();
        var phrases = new List<string>();
        if (root["blocked"] is JArray blocked) {
            foreach (JToken t in blocked) {
                terms.Add(t?.ToString());
            }
        }
        if (root["blocked_phrases"] is JArray blockedPhrases) {
            foreach (JToken t in blockedPhrases) {
                phrases.Add(t?.ToString());
            }
        }

        // without the word list every term is strong so it acts like the old substring filter
        var words = new List<string>();
        string wordsResource = null;
        foreach (string name in asm.GetManifestResourceNames()) {
            if (name.EndsWith(WordsResourceSuffix, StringComparison.OrdinalIgnoreCase)) {
                wordsResource = name;
                break;
            }
        }
        string wordsText = wordsResource == null ? null : ReadResource(asm, wordsResource);
        if (wordsText == null) {
            Plugin.log?.LogError("NsfwContentFilter: embedded " + WordsResourceSuffix + " not found");
        } else {
            foreach (string line in wordsText.Split('\n')) {
                string w = line.Trim();
                if (w.Length == 0 || w[0] == '#') {
                    continue;
                }
                if (w[0] == '!') {
                    terms.Add(w.Substring(1));
                } else {
                    words.Add(w);
                }
            }
        }

        _matcher = new NameFilterMatcher(terms, phrases, words);
        Plugin.log?.LogInfo($"NsfwContentFilter loaded {_matcher.StrongCount} strong + {_matcher.WeakCount} weak terms and {_matcher.WordCount} words");
    }

    private static string ReadResource(Assembly asm, string resourceName) {
        using Stream stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) {
            return null;
        }
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}