using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

// V1: a slot's skin.json read as sprites. it wins over the slot's PNG folders.
internal static partial class SkinJsonFile {
    internal const string SlotFileName = "skin.json";
    private const string SaveEntryName = "save.json";

    internal static string SlotPath(string slotRoot) {
        return Path.Combine(slotRoot, SlotFileName);
    }

    internal static bool Exists(string slotRoot) {
        return !string.IsNullOrEmpty(slotRoot) && File.Exists(SlotPath(slotRoot));
    }

    private sealed class PreviewEntry {
        internal string Key;
        internal Sprite Sprite;
    }

    private static readonly Dictionary<string, PreviewEntry> Previews = new Dictionary<string, PreviewEntry>(StringComparer.Ordinal);

    // changes when the file or the species changes.
    private static string VersionKey(string path, int species) {
        FileInfo info = new FileInfo(path);
        return info.Exists ? path + "|" + species + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length : null;
    }

    // the eyes drawn over the first of these head sprites that a slot's skin.json holds, for the menu cards.
    // built once per file version.
    internal static Sprite PreviewSprite(string slotRoot, int species, string[] names, string[] eyeNames) {
        string path = SlotPath(slotRoot);
        string key = VersionKey(path, species);
        if (key == null) {
            return null;
        }

        Previews.TryGetValue(path, out PreviewEntry entry);
        if (entry != null && entry.Key == key) {
            return entry.Sprite;
        }

        Sprite sprite = null;
        if (TryReadText(path, out string text, out string error)
            && SkinJson.TryParseV1(text, species, out SkinJsonSet set, out JObject root, out error)) {
            foreach (string name in names) {
                if (set.Right.TryGetValue(name, out SkinGrid grid)) {
                    SkinGrid eyes = null;
                    foreach (string eyeName in eyeNames) {
                        if (set.Right.TryGetValue(eyeName, out eyes)) {
                            break;
                        }
                    }

                    sprite = ToSprite(name + "_preview", ComposeHead(grid, eyes), null);
                    break;
                }
            }
        }

        if (entry != null && entry.Sprite != null) {
            UnityEngine.Object.Destroy(entry.Sprite.texture);
            UnityEngine.Object.Destroy(entry.Sprite);
        }

        Previews[path] = new PreviewEntry { Key = key, Sprite = sprite };
        return sprite;
    }

    // the game draws the eyes as their own renderer over the head, and both sprites share one canvas.
    // a skin with no eyes, or eyes on another canvas, shows the bare head.
    internal static SkinGrid ComposeHead(SkinGrid head, SkinGrid eyes) {
        if (eyes == null || eyes.Width != head.Width || eyes.Height != head.Height) {
            return head;
        }

        SkinGrid composed = new SkinGrid { Width = head.Width, Height = head.Height, Rgba = (byte[])head.Rgba.Clone() };
        for (int at = 0; at < composed.Rgba.Length; at += 4) {
            float eyeAlpha = eyes.Rgba[at + 3] / 255f;
            float headAlpha = composed.Rgba[at + 3] / 255f;
            float outAlpha = eyeAlpha + headAlpha * (1f - eyeAlpha);
            if (outAlpha <= 0f) {
                continue;
            }

            for (int channel = 0; channel < 3; channel++) {
                float mixed = (eyes.Rgba[at + channel] * eyeAlpha
                    + composed.Rgba[at + channel] * headAlpha * (1f - eyeAlpha)) / outAlpha;
                composed.Rgba[at + channel] = (byte)Mathf.Clamp(Mathf.RoundToInt(mixed), 0, 255);
            }

            composed.Rgba[at + 3] = (byte)Mathf.Clamp(Mathf.RoundToInt(outAlpha * 255f), 0, 255);
        }

        return composed;
    }

    // a PNG as a grid, top row first. null when it is not a readable image.
    internal static SkinGrid GridFromPng(string path) {
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try {
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) {
                return null;
            }

            Color32[] pixels = texture.GetPixels32();
            SkinGrid grid = new SkinGrid { Width = texture.width, Height = texture.height, Rgba = new byte[pixels.Length * 4] };
            for (int y = 0; y < grid.Height; y++) {
                for (int x = 0; x < grid.Width; x++) {
                    Color32 pixel = pixels[(grid.Height - 1 - y) * grid.Width + x];
                    int to = (y * grid.Width + x) * 4;
                    grid.Rgba[to] = pixel.r;
                    grid.Rgba[to + 1] = pixel.g;
                    grid.Rgba[to + 2] = pixel.b;
                    grid.Rgba[to + 3] = pixel.a;
                }
            }

            return grid;
        } finally {
            UnityEngine.Object.Destroy(texture);
        }
    }

    // a picked file is the skin file itself, a folder is searched for one.
    internal static string SourceOf(string path) {
        return File.Exists(path) ? path : FindSource(path);
    }

    internal static bool IsSkinFileName(string path) {
        string extension = Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".casutogether", StringComparison.OrdinalIgnoreCase);
    }

    // skin.json, save.json, *.save.json or *.casutogether.zip in a folder, else null.
    internal static string FindSource(string folder) {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) {
            return null;
        }

        foreach (string name in new[] { SlotFileName, SaveEntryName }) {
            string path = Path.Combine(folder, name);
            if (File.Exists(path)) {
                return path;
            }
        }

        foreach (string pattern in new[] { "*.save.json", "*.casutogether.zip", "*.casutogether" }) {
            string[] matches = Directory.GetFiles(folder, pattern);
            Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
            if (matches.Length > 0) {
                return matches[0];
            }
        }

        return null;
    }

    internal static bool TryReadText(string path, out string text, out string error) {
        text = null;
        error = null;
        try {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".casutogether", StringComparison.OrdinalIgnoreCase)) {
                using ZipArchive zip = ZipFile.OpenRead(path);
                ZipArchiveEntry entry = zip.GetEntry(SaveEntryName);
                if (entry == null) {
                    error = Path.GetFileName(path) + " has no " + SaveEntryName + ".";
                    return false;
                }

                if (entry.Length > SkinJson.MaxJsonChars) {
                    error = SaveEntryName + " is larger than " + SkinJson.MaxJsonChars / (1024 * 1024) + " MB.";
                    return false;
                }

                using StreamReader reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
                text = reader.ReadToEnd();
                return true;
            }

            FileInfo info = new FileInfo(path);
            if (info.Length > SkinJson.MaxJsonChars) {
                error = info.Name + " is larger than " + SkinJson.MaxJsonChars / (1024 * 1024) + " MB.";
                return false;
            }

            text = File.ReadAllText(path, new UTF8Encoding(false));
            return true;
        } catch (Exception ex) {
            error = "Cannot read " + Path.GetFileName(path) + ": " + ex.Message;
            return false;
        }
    }

    internal static bool IsWoundName(string name) {
        return SkinImporter.WoundViewNames.Contains(name);
    }

    internal static Texture2D ToTexture(SkinGrid grid) {
        Texture2D texture = new Texture2D(grid.Width, grid.Height, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point
        };
        Color32[] pixels = new Color32[grid.Width * grid.Height];
        for (int y = 0; y < grid.Height; y++) {
            // unity's first row is the bottom one.
            int destinationRow = grid.Height - 1 - y;
            for (int x = 0; x < grid.Width; x++) {
                int from = (y * grid.Width + x) * 4;
                pixels[destinationRow * grid.Width + x] =
                    new Color32(grid.Rgba[from], grid.Rgba[from + 1], grid.Rgba[from + 2], grid.Rgba[from + 3]);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false);
        return texture;
    }

    internal static Sprite ToSprite(string name, SkinGrid grid, List<Texture2D> textures) {
        Texture2D texture = ToTexture(grid);
        textures?.Add(texture);
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f), 8f);
        sprite.name = name;
        return sprite;
    }

    internal static bool HasColorPixels(SkinGrid grid) {
        for (int at = 0; at < grid.Rgba.Length; at += 4) {
            if (grid.Rgba[at + 3] >= 128 && (grid.Rgba[at] != grid.Rgba[at + 1] || grid.Rgba[at + 1] != grid.Rgba[at + 2])) {
                return true;
            }
        }

        return false;
    }

    // right-facing sprites of a parsed file, and which wound view sprites have color.
    internal static Dictionary<string, Sprite> RightSprites(SkinJsonSet set, List<Texture2D> textures,
        out HashSet<string> coloredWound) {
        Dictionary<string, Sprite> rights = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        coloredWound = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SkinGrid> pair in set.Right) {
            rights[pair.Key] = ToSprite(pair.Key, pair.Value, textures);
            if (IsWoundName(pair.Key) && HasColorPixels(pair.Value)) {
                coloredWound.Add(pair.Key);
            }
        }

        return rights;
    }

    internal static void WarnBadColors(string slotRoot, SkinJsonSet set) {
        if (set.BadColors > 0) {
            SpriteReplacerPlugin.Log.LogWarning("skin.json in " + slotRoot + " has " + set.BadColors
                + " colors that are not #rrggbb or #rrggbbaa. They were left transparent.");
        }
    }

    // the zip entry of a right-facing sprite.
    internal static KeyValuePair<string, byte[]> RightPngEntry(string name, SkinGrid grid) {
        return new KeyValuePair<string, byte[]>(EntryFolder(name) + "/" + name + ".png", Encode(grid));
    }

    private static string EntryFolder(string name) {
        return IsWoundName(name) ? "WoundView" : "Body";
    }

    private static byte[] Encode(SkinGrid grid) {
        Texture2D texture = ToTexture(grid);
        try {
            return ImageConversion.EncodeToPNG(texture);
        } finally {
            UnityEngine.Object.Destroy(texture);
        }
    }
}
