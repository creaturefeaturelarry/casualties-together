
using BepInEx.Configuration;
using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Burst.Intrinsics;
using Unity.VisualScripting;

namespace Together;







public static class Lang {


    internal static Dictionary<string, string> EN = new Dictionary<string, string>{

    };

    internal static Dictionary<string, Dictionary<string, string>> dict = new Dictionary<string, Dictionary<string, string>> {
        ["EN"] = EN,
    };

    static HashSet<string> failed = new();

    static ConfigEntry<string> languageConfig;
    static string selectedLocale = "";
    static string[] availableLocales;

    internal static void InitializeLanguageConfig(ConfigFile config) {
        languageConfig = config.Bind("Localization", "Language", "auto", new ConfigDescription(
            "Multiplayer mod language. auto follows the game's language. The in-game selector only saves when you click Save or Reset to Defaults.",
            new AcceptableValueList<string>(new[] { "auto" }.Concat(AvailableLocales).ToArray())));
        selectedLocale = languageConfig.Value == "auto" ? "" : languageConfig.Value;
    }

    // Keep the menu's live preview separate: BepInEx normally saves ConfigEntry changes immediately.
    internal static string SelectedLocale => selectedLocale;

    internal static bool IsSelectedLocaleSaved {
        get {
            if (languageConfig == null)
                return true;
            var savedLocale = String.IsNullOrEmpty(SelectedLocale) ? "auto" : SelectedLocale;
            return String.Equals(languageConfig.Value, savedLocale, StringComparison.Ordinal);
        }
    }

    internal static string CurrentLocale => String.IsNullOrEmpty(SelectedLocale) ? Locale.currentLangName ?? "EN" : SelectedLocale;

    internal static string[] AvailableLocales => availableLocales ??= dict.Keys
        .OrderBy(locale => locale == "EN" ? 0 : 1)
        .ThenBy(locale => locale, StringComparer.Ordinal)
        .ToArray();

    internal static string GetLocaleDisplayName(string locale) {
        // Read each language's own name, not its translation in the currently selected language.
        if (dict.TryGetValue(locale, out var language) && language.TryGetValue("language_name", out var name) && !String.IsNullOrEmpty(name))
            return name + " (" + locale + ")";
        return locale;
    }

    internal static void SetSelectedLocale(string locale) {
        locale ??= "";
        if (!String.IsNullOrEmpty(locale) && !dict.ContainsKey(locale))
            return;
        selectedLocale = locale;
        failed.Clear();
    }

    internal static bool SaveSelectedLocale() {
        if (languageConfig == null)
            return false;
        try {
            languageConfig.Value = String.IsNullOrEmpty(SelectedLocale) ? "auto" : SelectedLocale;
            languageConfig.ConfigFile.Save();
            return IsSelectedLocaleSaved;
        } catch (Exception e) {
            log.error("Lang.SaveSelectedLocale: " + e.ToString());
            return false;
        }
    }

    internal static string GetEN(string key) {
        if (EN.TryGetValue(key, out var value))
            return value;
        return null;
    }

    internal const string LocalPrefixTag = "casualtiestogether_";

    internal const string RussianLocaleKey = "RU";
    static string _ruCachedLocale;
    static bool _ruCachedResult;
    static string _ruLoggedLocale;

    internal static bool IsRussianLocale(string locale) {
        if (String.IsNullOrEmpty(locale)) return false;
        if (locale == _ruCachedLocale) return _ruCachedResult;

        bool isRu = locale.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                 || locale.IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isRu) {
            foreach (var c in locale) {
                if (c >= '\u0400' && c <= '\u04FF') { isRu = true; break; }
            }
        }

        if (!isRu) {
            var pack = Locale.currentLang;
            if (pack == null || pack.other == null) {
                return false;
            }
            if (pack.other.TryGetValue("layer", out var sample) && !String.IsNullOrEmpty(sample)) {
                foreach (var c in sample) {
                    if (c >= '\u0400' && c <= '\u04FF') { isRu = true; break; }
                }
            }
        }

        _ruCachedLocale = locale;
        _ruCachedResult = isRu;

        if (isRu && _ruLoggedLocale != locale) {
            _ruLoggedLocale = locale;
            try { Plugin.log.LogInfo($"Lang: language '{locale}' is Russian, using embedded RU translation."); } catch { }
        }
        return isRu;
    }
    public static string Get(in string key, in bool allow_null_result = false) {

        string result = null;
        var locale = CurrentLocale;

        if (IsRussianLocale(locale)
            && dict.TryGetValue(RussianLocaleKey, out var ruDict)
            && ruDict.TryGetValue(key, out var ruText)
            && !String.IsNullOrEmpty(ruText)) {
            return ruText;
        }

        if (dict.TryGetValue(locale, out var lang))
            if (lang.TryGetValue(key, out result) && String.IsNullOrEmpty(result))
                result = null;

        var nkey = LocalPrefixTag + key;
        // Game-provided mod translations must not override an independently selected language.
        if ((String.IsNullOrEmpty(SelectedLocale) || locale == Locale.currentLangName) && Locale.currentLang?.other != null) {
            if (Locale.currentLang.other.TryGetValue(nkey, out var v) && !String.IsNullOrEmpty(v)) {
                return v;
            } else {
                if (result != null) {
                    return result;
                }
                if (!failed.Contains(nkey)) {
                    failed.Add(nkey);
                    var en = GetEN(key);
                    if (String.IsNullOrEmpty(en)) {
                        if (!allow_null_result)
                            log.error($"Lang: Failed to get {locale} translation for \"{nkey}\"");
                    } else {
                        log.error($"Lang: Failed to get {locale} translation for \"{nkey}\" -> OG: \"{en}\"");
                    }
                }
            }
        }


        if (result == null) {
            if (EN.TryGetValue(key, out result) && !String.IsNullOrEmpty(result))
                return result;
            if (!allow_null_result) {
                result = "ERROR_NO_LOCALE_" + locale + "_" + nkey; 
            }
        }

        return result;
    }
    public static bool TryGet(in string key, out string text) {
        text = Get(key,true);
        return text != null;
    }
    public static string GetNoError(in string key) {
        return Get(key, true) ?? key;
    }

    public const string MSG_TYPE_DEF_SEPARATOR = "$\r";
    public const string TRANSLATION_KEY_MSG_MARK = "T" + MSG_TYPE_DEF_SEPARATOR;
    const string FORMATTED_MSG_MARK = "F" + MSG_TYPE_DEF_SEPARATOR;

    public readonly struct MessageArgument {
        internal readonly string Text;
        internal readonly bool IsMessage;

        internal MessageArgument(string text, bool isMessage) {
            Text = text;
            IsMessage = isMessage;
        }

        public static implicit operator MessageArgument(string text) => new MessageArgument(text, false);
    }

    // Only explicitly marked arguments may be interpreted as nested localization messages.
    public static MessageArgument NestedMsg(string message) => new MessageArgument(message, true);

    // Ordinary string arguments always remain literal, including any protocol prefixes or tags.
    public static string FormatMsg(string key, params MessageArgument[] arguments) {
        var parts = new JArray(key);
        foreach (var argument in arguments) {
            if (argument.IsMessage)
                parts.Add(new JObject { ["message"] = argument.Text ?? "" });
            else
                parts.Add(argument.Text ?? "");
        }
        return FORMATTED_MSG_MARK + JsonConvert.SerializeObject(parts);
    }

    static string ParseFormattedMessage(string message, int depth = 0) {
        if (depth >= 8)
            throw new InvalidDataException("Localized message nesting is too deep.");
        if (message.StartsWith(TRANSLATION_KEY_MSG_MARK))
            return Get(message.Substring(TRANSLATION_KEY_MSG_MARK.Length));
        if (!message.StartsWith(FORMATTED_MSG_MARK))
            return message;

        var parts = JArray.Parse(message.Substring(FORMATTED_MSG_MARK.Length));
        if (parts.Count == 0 || parts[0].Type != JTokenType.String || String.IsNullOrEmpty((string)parts[0]))
            throw new InvalidDataException("Localized message has no translation key.");

        var arguments = new object[parts.Count - 1];
        for (int i = 1; i < parts.Count; i++) {
            if (parts[i].Type == JTokenType.String) {
                arguments[i - 1] = (string)parts[i];
            } else if (parts[i] is JObject nested && nested.Count == 1 && nested["message"]?.Type == JTokenType.String) {
                arguments[i - 1] = ParseFormattedMessage((string)nested["message"], depth + 1);
            } else {
                throw new InvalidDataException("Invalid localized message argument.");
            }
        }
        return String.Format(Get((string)parts[0]), arguments);
    }

    public static string ParseDisconnectReason(string message) {
        // These errors can reach clients too old to understand our localization messages.
        switch (message) {
            case "Version mismatch.": return Get("deny_version_mismatch");
            case "Corrupt payload.": return Get("deny_corrupt_payload");
            case "Server error.": return Get("deny_server_broke");
            default: return ParseTaggedString(message);
        }
    }
   
    public static string MarkMsgAsLocaleKey(in string message) {
        return TRANSLATION_KEY_MSG_MARK + message;
    }
    public static void MsgTryTranslateIfItsLocaleKey(ref string message) {
        if (message.StartsWith(TRANSLATION_KEY_MSG_MARK)) {
            message = Lang.Get(message.Substring(TRANSLATION_KEY_MSG_MARK.Length));
        }
    }
    public static string ParseTaggedString(string message) {
        MsgTryParse(ref message);
        return message;
    }
    static bool TryParseIndividualTag(ref string fullTag) {
        if (fullTag.TryTrimStart("key:", out var langKey)) {
            fullTag = Lang.Get(langKey, true) ?? langKey;
            return true;
        } else if (fullTag.TryTrimStart("item:", out var itemId)) {
            if (ItemSync.TryGetItem(ushort.Parse(itemId), out var si, out var item)) {
                // fullName already wraps condition in <color>. an extra unclosed <color=#FFFFFF>
                // makes the prompt print that code instead of tinting the name.
                fullTag = PlayerCamera.ItemHoverDescription(item).Item1;
            }
            return true;
        } else if (fullTag.TryTrimStart("itemfull:", out itemId)) {
            if (ItemSync.TryGetItem(ushort.Parse(itemId), out var si, out var item)) {
                fullTag = item.fullName;
            }
            return true;
        } else if (fullTag.TryTrimStart("obj:", out var syncId)) {
            if (NetObjectRegistry.TryGetSyncInfo(ushort.Parse(syncId), out var si)) {
                if (si.go == null) {
                    fullTag = si.ToString();
                } else {
                    if (si.IsBuilding()) {
                        fullTag = si.building.fullName;
                    } else if (si.IsItem()) {
                        fullTag = si.item.Stats.fullName;
                    } else {
                        fullTag = si.ToString();
                    }
                }
            }
            return true;
        } else if (fullTag.TryTrimStart("body:", out var bodyId)) {
            if (NetBody.TryGetNetBodyFromId(ushort.Parse(bodyId), out var nb)) {
                fullTag = nb.bodyName;
            }
            return true;
        } else if (fullTag.TryTrimStart("plr:", out var plrId)) {
            if (ScavPlayer.TryGetPlayerFromClientId(ushort.Parse(plrId), out var plr)) {
                fullTag = plr.playerName;
            }
            return true;
        } else if (fullTag.TryTrimStart("bodyc:", out bodyId)) {
            if (NetBody.TryGetNetBodyFromId(ushort.Parse(bodyId), out var nb)) {
                fullTag = $"<color=#{nb.color.ToHexString()}>{nb.bodyName}</color>";
            }
            return true;
        } else if (fullTag.TryTrimStart("plrc:", out plrId)) {
            if (ScavPlayer.TryGetPlayerFromClientId(ushort.Parse(plrId), out var plr)) {
                fullTag = $"<color=#{plr.playerColor.ToHexString()}>{plr.playerName}</color>";
            }
            return true;
        }
        return false;
    }
    public static void MsgTryParse(ref string message) {
        if (String.IsNullOrEmpty(message))
            return;
        if (message.StartsWith(FORMATTED_MSG_MARK)) {
            try {
                message = ParseFormattedMessage(message);
            } catch (Exception e) {
                log.error("Lang Message Formatting: " + e.ToString());
            }
            return;
        }
        if (message.StartsWith(TRANSLATION_KEY_MSG_MARK)) {
            message = Lang.Get(message.Substring(TRANSLATION_KEY_MSG_MARK.Length));
        } else {
            try {
                int bracketEnd = -1;
                bool shouldBreak = false;
                for (int i = (message.Length - 1); i >= 0; i--) {
                    var c = message[i];

                    if (shouldBreak) {
                        break;
                    }

                    if (bracketEnd == -1) {
                        if (c == '>') {
                            bracketEnd = i;
                            continue;
                        }
                    } else {
                        string fullTag = null;
                        if (c == '<') {
                            try {
                                fullTag = message.Substring(i + 1, bracketEnd - i - 1);

                                if (fullTag == "parsebreak") {
                                    fullTag = "";
                                    shouldBreak = true;
                                } else if (TryParseIndividualTag(ref fullTag)) {
                                    // It's good
                                } else {
                                    fullTag = '<' + fullTag + '>';
                                }

                                message = message.Substring(0, i) + fullTag + message.Substring(bracketEnd + 1);
                            } catch (Exception e) {
                                if (log.verbose) log.error($"Lang Applying tag: {fullTag}\n{e.ToString()}");
                            }
                            bracketEnd = -1;
                        }
                    }
                }
            } catch (Exception e) {
                log.error("Lang Tag Parsing: " + e.ToString());
            }

            try { // A tag without end breaks it for some reason
                const string colorTagStart = "<color=";
                const string colorTagEnd = "</color>";

                var startCount = message.CountOccurences(colorTagStart);
                var endCount = message.CountOccurences(colorTagEnd);

                if (startCount != endCount) {

                    if (startCount > endCount) {
                        var foundBegin = message.LastIndexOf(colorTagStart);
                        var foundEnd = message.LastIndexOf(colorTagEnd);
                        if (foundBegin > foundEnd) {
                            foundEnd = message.IndexOf(">", foundBegin) + 1;
                            message = message.Remove(foundBegin, foundEnd - foundBegin);
                        }
                    } else {

                    }

                }

            } catch (Exception e) {
                log.error("Lang Color Tag Correction: " + e.ToString());
            }
        }
    }

    static bool LoadedLocale = false;
    internal static void InternalLoadLocale() {
        if (LoadedLocale) return;

        try {
            var prefix = CoopModAssets.AssetsPrefix + "locale.";
            var postfix = ".json";
            var asm = Assembly.GetExecutingAssembly();
            var assetNames = asm.GetManifestResourceNames()
                                 .OrderBy(n => n)
                                 .ToList();

            foreach (var asset in assetNames) {
                if (asset.TryTrimStart(prefix, out var trimmed) && trimmed.TryTrimEnd(postfix, out var localeName, true)) {
                    try {
                        using var stream = asm.GetManifestResourceStream(asset);
                        using var reader = new StreamReader(stream); 
                        var jsonData = reader.ReadToEnd();

                        var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonData).ToDictionary(a => a.Key.TrimStart(LocalPrefixTag), b => b.Value);


                        Lang.dict[localeName] = parsed;
                        if ((EN == null || EN.Count == 0) && localeName == "EN") {
                            EN = parsed;
                        }

#if !RELEASE
                        Plugin.log.LogInfo($"Loaded locale: '{localeName}'");
#endif       
                    } catch (Exception e) {
                        log.error($"Failed to load embedded locale: {trimmed} - {e.ToString()}");
                    }
                }
            }

            availableLocales = null;
            LoadedLocale = true;
        } catch (Exception e) { log.error("Lang.InternalLoadLocale: " + e.ToString()); }

    }


#if DEBUG
    [CTPostInit]
    static void RegisterDebugCommands() {

        Con.RegisterCommandModern("testspooflang", "DEBUG MP - Spoof Locale.currentLangName", (split, comm) => {
            Locale.currentLangName = split[1];
            log.l("testspooflang", Locale.currentLangName);
        }, ("string", "Lang name", "EN", null));
    }
#endif
}




















