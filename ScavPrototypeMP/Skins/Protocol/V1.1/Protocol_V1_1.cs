using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
#if !SKIN_JSON_TESTS
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
#endif

// V1.1: the left-facing twins, the zip protocol marker and the local skin's twins.

// V1.1: the game mirrors a body that faces left, so a left sprite is stored mirrored.
internal sealed partial class SkinGrid {
    internal SkinGrid Mirrored() {
        SkinGrid flipped = new SkinGrid { Width = Width, Height = Height, Rgba = new byte[Rgba.Length] };
        for (int y = 0; y < Height; y++) {
            for (int x = 0; x < Width; x++) {
                int from = (y * Width + x) * 4;
                int to = (y * Width + (Width - 1 - x)) * 4;
                Array.Copy(Rgba, from, flipped.Rgba, to, 4);
            }
        }

        return flipped;
    }
}

// V1.1: the optional "left" section, shaped like "skins". a sprite without one uses its right sprite.
internal sealed partial class SkinJsonSet {
    internal readonly Dictionary<string, SkinGrid> Left = new Dictionary<string, SkinGrid>(StringComparer.Ordinal);
}

internal static partial class SkinJson {
    internal const string LeftSuffix = "_left";

    internal static bool IsLeftName(string name) {
        return name != null && name.Length > LeftSuffix.Length && name.EndsWith(LeftSuffix, StringComparison.Ordinal);
    }

    internal static string RightNameOf(string leftName) {
        return IsLeftName(leftName) ? leftName.Substring(0, leftName.Length - LeftSuffix.Length) : leftName;
    }

    // V1, and the left section when the file says skinProtocol 1.1 or later.
    internal static bool TryParse(string json, int species, out SkinJsonSet set, out string error) {
        if (!TryParseV1(json, species, out set, out JObject root, out error)) {
            return false;
        }

        if (!MpSkinProtocol.TryReadVersion(root, out string version, out bool supportsLeft)) {
            set = null;
            error = "Skin file has protocol '" + root[MpSkinProtocol.VersionField] + "' this build cannot read.";
            return false;
        }

        if (supportsLeft) {
            ReadSection(root["left"] as JObject, SpeciesId(species), species, set.Left, set);
        }

        return true;
    }
}

#if !SKIN_JSON_TESTS
// V1.1: the sprites and zip PNGs of a skin.json, built once per file version.
internal static partial class SkinJsonFile {
    private sealed class BuiltSkin {
        internal string Key;
        internal Dictionary<string, Sprite> Rights;
        internal Dictionary<string, Sprite> Lefts;
        internal HashSet<string> ColoredWound;
        internal readonly List<Texture2D> Textures = new List<Texture2D>();
    }

    // how often the sprites and the PNGs were built, read by the live checks.
    internal static int SpriteBuildCount;
    internal static int PngBuildCount;

    private static BuiltSkin _built;
    private static string _entriesKey;
    private static List<KeyValuePair<string, byte[]>> _entries;

    // the cached sprites of a slot's skin.json, left twins mirrored. callers copy from them.
    internal static bool TryLoad(string slotRoot, int species, out Dictionary<string, Sprite> rights,
        out Dictionary<string, Sprite> lefts, out HashSet<string> coloredWound, out string error) {
        error = null;
        string key = VersionKey(SlotPath(slotRoot), species);
        if (key != null && _built != null && _built.Key == key) {
            rights = _built.Rights;
            lefts = _built.Lefts;
            coloredWound = _built.ColoredWound;
            return true;
        }

        rights = null;
        lefts = null;
        coloredWound = null;
        if (!TryReadText(SlotPath(slotRoot), out string text, out error)
            || !SkinJson.TryParse(text, species, out SkinJsonSet set, out error)) {
            return false;
        }

        SpriteBuildCount++;
        BuiltSkin built = new BuiltSkin { Key = key };
        built.Rights = RightSprites(set, built.Textures, out built.ColoredWound);
        built.Lefts = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SkinGrid> pair in set.Left) {
            // no wound view twins, a twin needs its right sprite.
            if (IsWoundName(pair.Key) || !built.Rights.ContainsKey(pair.Key)) {
                continue;
            }

            built.Lefts[pair.Key] = ToSprite(pair.Key + SkinJson.LeftSuffix, pair.Value.Mirrored(), built.Textures);
        }

        WarnBadColors(slotRoot, set);
        DisposeBuilt();
        _built = built;
        rights = built.Rights;
        lefts = built.Lefts;
        coloredWound = built.ColoredWound;
        return true;
    }

    // the sprites of the replaced skin are dropped. the caller applies the new ones right after.
    private static void DisposeBuilt() {
        if (_built == null) {
            return;
        }

        foreach (Sprite sprite in _built.Rights.Values) {
            UnityEngine.Object.Destroy(sprite);
        }

        foreach (Sprite sprite in _built.Lefts.Values) {
            UnityEngine.Object.Destroy(sprite);
        }

        foreach (Texture2D texture in _built.Textures) {
            UnityEngine.Object.Destroy(texture);
        }

        _built = null;
    }

    // the PNG entries of a skin.json for the zip sent to other players.
    internal static List<KeyValuePair<string, byte[]>> ToPngEntries(string slotRoot, int species) {
        string key = VersionKey(SlotPath(slotRoot), species);
        if (key != null && _entriesKey == key) {
            return _entries;
        }

        PngBuildCount++;
        List<KeyValuePair<string, byte[]>> entries = new List<KeyValuePair<string, byte[]>>();
        if (!TryReadText(SlotPath(slotRoot), out string text, out string error)
            || !SkinJson.TryParse(text, species, out SkinJsonSet set, out error)) {
            SpriteReplacerPlugin.Log.LogWarning("skin.json in " + slotRoot + " was not shared: " + error);
            return entries;
        }

        List<string> rightNames = new List<string>(set.Right.Keys);
        rightNames.Sort(StringComparer.Ordinal);
        foreach (string name in rightNames) {
            entries.Add(RightPngEntry(name, set.Right[name]));
            if (!IsWoundName(name) && set.Left.TryGetValue(name, out SkinGrid left)) {
                // as drawn, the receiver mirrors it.
                entries.Add(new KeyValuePair<string, byte[]>("Body/" + name + SkinJson.LeftSuffix + ".png", Encode(left)));
            }
        }

        _entriesKey = key;
        _entries = entries;
        return entries;
    }
}

// V1.1: the protocol marker and the left-facing twins of a skin zip.
internal sealed partial class MpSkinPackage {
    // "1.0" when the zip has no marker entry.
    internal string Protocol = MpSkinProtocol.Legacy;
    internal bool SupportsLeft;
    // left twins by right sprite name, already mirrored.
    internal readonly Dictionary<string, Sprite> LeftSprites = new Dictionary<string, Sprite>();
    private readonly List<Sprite> _rightsWithLeft = new List<Sprite>();

    // old clients skip protocol.json and still read the right-facing sprites.
    private static void WriteProtocolMarker(ZipArchive zip) {
        byte[] markerBytes = Encoding.UTF8.GetBytes(MpSkinProtocol.MarkerJson);
        using (Stream markerStream = zip.CreateEntry(MpSkinProtocol.MarkerEntry).Open()) {
            markerStream.Write(markerBytes, 0, markerBytes.Length);
        }
    }

    // false: a version this build cannot read. no protocol.json is the original protocol.
    private bool ReadProtocolMarker(ZipArchive zip) {
        ZipArchiveEntry marker = zip.GetEntry(MpSkinProtocol.MarkerEntry);
        if (marker == null) {
            return true;
        }

        if (marker.Length > MpSkinProtocol.MaxMarkerLength) {
            return false;
        }

        JObject root;
        try {
            using (StreamReader reader = new StreamReader(marker.Open(), Encoding.UTF8)) {
                root = JObject.Parse(reader.ReadToEnd());
            }
        } catch (Exception) {
            return false;
        }

        if (!MpSkinProtocol.TryReadVersion(root, out string version, out bool supportsLeft)) {
            SpriteReplacerPlugin.Log.LogWarning("Skin package has protocol '" + root[MpSkinProtocol.VersionField] + "' this build cannot read.");
            return false;
        }

        Protocol = version;
        SupportsLeft = supportsLeft;
        return true;
    }

    // the wound view has no left twins, and a 1.0 zip has none at all.
    private bool AcceptsLeftEntry(string folder) {
        return SupportsLeft && folder != "WoundView";
    }

    private bool HasSprite(string name, bool isLeft) {
        return isLeft ? LeftSprites.ContainsKey(SkinJson.RightNameOf(name)) : Sprites.ContainsKey(name);
    }

    private void AddLeftSprite(string name, Texture2D texture) {
        SkinFacing.MirrorTexture(texture);
        Sprite sprite = Sprite.Create(texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f), 8f);
        sprite.name = name;
        Textures.Add(texture);
        LeftSprites[SkinJson.RightNameOf(name)] = sprite;
    }

    // a twin without its right sprite is dropped.
    private void LinkLeftSprites() {
        foreach (KeyValuePair<string, Sprite> pair in LeftSprites) {
            if (Sprites.TryGetValue(pair.Key, out Sprite right)) {
                SkinFacing.Link(right, pair.Value);
                _rightsWithLeft.Add(right);
            }
        }
    }

    private void DisposeLeft() {
        foreach (Sprite right in _rightsWithLeft) {
            SkinFacing.Unlink(right);
        }

        _rightsWithLeft.Clear();
        foreach (Sprite left in LeftSprites.Values) {
            if (left != null) {
                UnityEngine.Object.Destroy(left);
            }
        }

        LeftSprites.Clear();
    }
}

// V1.1: the left twins of the local skin while it loads. a reload unlinks the old ones first.
internal static class LocalSkinLeft {
    private static readonly List<Sprite> LinkedRights = new List<Sprite>();
    private static readonly HashSet<Sprite> LinkedTwins = new HashSet<Sprite>();
    private static readonly Dictionary<string, Sprite> Pending = new Dictionary<string, Sprite>(StringComparer.Ordinal);

    internal static void Reset() {
        foreach (Sprite right in LinkedRights) {
            SkinFacing.Unlink(right);
        }

        LinkedRights.Clear();
        LinkedTwins.Clear();
        Pending.Clear();
    }

    // the local skin's twins are local art too, for the default leak check.
    internal static bool IsLocalTwin(Sprite sprite) {
        return sprite != null && LinkedTwins.Contains(sprite);
    }

    internal static void Add(string rightName, Sprite leftMirrored) {
        Pending[rightName] = leftMirrored;
    }

    // a <name>_left.png file, stored mirrored because the game mirrors the body.
    internal static void LoadFile(string file, string leftName) {
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(file))) {
            UnityEngine.Object.Destroy(tex);
            SpriteReplacerPlugin.Log.LogWarning("Could not decode sprite: " + file);
            return;
        }

        SkinFacing.MirrorTexture(tex);
        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 8f);
        sprite.name = leftName;
        Pending[SkinJson.RightNameOf(leftName)] = sprite;
    }

    // a twin needs its right sprite.
    internal static void Link(IReadOnlyDictionary<string, Sprite> rights) {
        foreach (KeyValuePair<string, Sprite> pair in Pending) {
            if (rights.TryGetValue(pair.Key, out Sprite right)) {
                SkinFacing.Link(right, pair.Value);
                LinkedRights.Add(right);
                LinkedTwins.Add(pair.Value);
            } else {
                SpriteReplacerPlugin.Log.LogWarning("Left-facing sprite " + pair.Value.name + " has no right-facing " + pair.Key + ". Skipped.");
                UnityEngine.Object.Destroy(pair.Value);
            }
        }

        Pending.Clear();
    }
}

// left twins of skin sprites, stored mirrored because the game mirrors a body that faces left.
internal static class SkinFacing {
    private static readonly Dictionary<Sprite, Sprite> LeftOfRight = new Dictionary<Sprite, Sprite>();
    private static readonly Dictionary<Sprite, Sprite> RightOfLeft = new Dictionary<Sprite, Sprite>();

    internal static int LinkCount => LeftOfRight.Count;

    internal static void Link(Sprite right, Sprite leftMirrored) {
        if (ReferenceEquals(right, null) || ReferenceEquals(leftMirrored, null)) {
            return;
        }

        Unlink(right);
        LeftOfRight[right] = leftMirrored;
        RightOfLeft[leftMirrored] = right;
    }

    internal static void Unlink(Sprite right) {
        if (ReferenceEquals(right, null)) {
            return;
        }

        if (LeftOfRight.TryGetValue(right, out Sprite left)) {
            LeftOfRight.Remove(right);
            if (!ReferenceEquals(left, null)) {
                RightOfLeft.Remove(left);
            }
        }
    }

    // runs in the sprite setter, so the body is only looked up for a sprite with a twin.
    internal static Sprite Choose(SpriteRenderer renderer, Sprite value) {
        if (LeftOfRight.Count == 0 || renderer == null || value == null) {
            return value;
        }

        bool isRight = LeftOfRight.TryGetValue(value, out Sprite left);
        Sprite right = null;
        bool isLeft = !isRight && RightOfLeft.TryGetValue(value, out right);
        if (!isRight && !isLeft) {
            return value;
        }

        Body body = renderer.GetComponentInParent<Body>();
        if (body == null) {
            return value;
        }

        if (!body.isRight) {
            return isRight && left != null ? left : value;
        }

        return isLeft && right != null ? right : value;
    }

    // setting each sprite again runs Choose. the renderers of a body are collected once.
    internal static void Refresh(Body body) {
        if (LeftOfRight.Count == 0 || body == null) {
            return;
        }

        BodyRenderers cache = body.GetComponent<BodyRenderers>();
        if (cache == null) {
            cache = body.gameObject.AddComponent<BodyRenderers>();
            cache.Renderers = body.GetComponentsInChildren<SpriteRenderer>(true);
        }

        foreach (SpriteRenderer renderer in cache.Renderers) {
            if (renderer == null) {
                continue;
            }

            Sprite current = renderer.sprite;
            if (current != null && (LeftOfRight.ContainsKey(current) || RightOfLeft.ContainsKey(current))) {
                renderer.sprite = current;
            }
        }
    }

    private sealed class BodyRenderers : MonoBehaviour {
        internal SpriteRenderer[] Renderers;
    }

    internal static void MirrorTexture(Texture2D texture) {
        int width = texture.width;
        Color32[] pixels = texture.GetPixels32();
        for (int row = 0; row < texture.height; row++) {
            System.Array.Reverse(pixels, row * width, width);
        }

        texture.SetPixels32(pixels);
        texture.Apply(false);
    }
}

internal static class BodySwitchDirPatch {
    private static void Postfix(Body __instance) {
        SkinFacing.Refresh(__instance);
    }
}
#endif
