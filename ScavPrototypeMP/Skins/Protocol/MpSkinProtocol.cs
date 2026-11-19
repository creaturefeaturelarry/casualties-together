using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

// the "skinProtocol" string of a skin file or of the zip's protocol.json. none is 1.0, the original protocol.
// 1.1 adds _left twins.
internal static class MpSkinProtocol {
    internal const string MarkerEntry = "protocol.json";
    internal const string VersionField = "skinProtocol";
    internal const string Current = "1.1";
    internal const string Legacy = "1.0";
    private const int SupportedMajor = 1;
    private const int FirstMinorWithLeft = 1;
    private const int MaxMarkerBytes = 256;

    internal static int MaxMarkerLength => MaxMarkerBytes;

    internal static string MarkerJson => "{\"" + VersionField + "\":\"" + Current + "\"}";

    // true when the version is absent (1.0) or readable. false: a version this build cannot read.
    internal static bool TryReadVersion(JObject root, out string version, out bool supportsLeft) {
        version = Legacy;
        supportsLeft = false;
        JToken token = root[VersionField];
        if (token == null || token.Type == JTokenType.Null) {
            return true;
        }

        if (token.Type != JTokenType.String) {
            return false;
        }

        version = token.Value<string>().Trim();
        return TryParse(version, out supportsLeft);
    }

    internal static bool TryParse(string text, out bool supportsLeft) {
        supportsLeft = false;
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        string[] parts = text.Trim().Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)) {
            return false;
        }

        if (major != SupportedMajor) {
            return false;
        }

        supportsLeft = minor >= FirstMinorWithLeft;
        return true;
    }
}
