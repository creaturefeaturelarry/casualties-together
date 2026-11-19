using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// textures from a received zip stay alive until the session ends.
internal sealed partial class MpSkinPackage {
    internal string SetName;
    internal int Species;
    internal string Hash;
    internal string ContentHash;
    internal byte[] Zip;
    internal Dictionary<string, Sprite> Sprites;
    internal readonly HashSet<string> ColoredWoundSprites = new HashSet<string>();
    internal readonly List<Texture2D> Textures = new List<Texture2D>();

    internal static MpSkinPackage FromSlot(string slot) {
        string root = SpriteReplacerPlugin.SlotDirectory(slot);
        if (!Directory.Exists(root)) {
            return null;
        }

        using MemoryStream memory = new MemoryStream();
        using (ZipArchive zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) {
            using SHA256 content = SHA256.Create();
            WriteProtocolMarker(zip);

            if (SkinJsonFile.Exists(root)) {
                // sent as PNGs, so peers need no json reader.
                AddJsonEntries(zip, root, content);
            } else {
                // head files win when the same name sits in both folders, matching the local loader.
                AddFolder(zip, Path.Combine(root, "Head"), "Head", content);
                AddFolder(zip, Path.Combine(root, "Body"), "Body", content);
                AddFolder(zip, Path.Combine(root, "WoundView"), "WoundView", content);
            }
        }

        byte[] bytes = memory.ToArray();
        if (!MpSkinRules.ZipSizeOk(bytes.Length)) {
            return null;
        }

        // decode here so host-stored packages have live Sprites (body-only slots included).
        MpSkinPackage package = FromZip(bytes, HashHex(bytes), SlotFolders.DisplayName(slot));
        if (package != null) {
            package.Species = SpriteReplacerPlugin.ActiveSpecies;
        }

        return package;
    }

    internal static MpSkinPackage FromZip(byte[] zipBytes, string expectedHash, string setName) {
        if (!MpSkinRules.ZipSizeOk(zipBytes == null ? 0 : zipBytes.Length)) {
            return null;
        }

        if (!string.Equals(HashHex(zipBytes), expectedHash, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        MpSkinPackage package = new MpSkinPackage {
            SetName = Sanitize(setName),
            Hash = expectedHash.ToLowerInvariant(),
            Zip = zipBytes,
            Sprites = new Dictionary<string, Sprite>()
        };

        using SHA256 content = SHA256.Create();
        try {
            using ZipArchive zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            int entries = 0;
            int total = 0;
            long pixels = 0;

            if (!package.ReadProtocolMarker(zip)) {
                return Reject(package);
            }

            foreach (ZipArchiveEntry entry in zip.Entries) {
                if (!MpSkinRules.EntryCountOk(++entries, package.SupportsLeft)) {
                    return Reject(package);
                }

                string path = entry.FullName.Replace('\\', '/');
                if (path.Contains("..") || path.StartsWith("/")) {
                    return Reject(package);
                }

                string[] parts = path.Split('/');
                if (parts.Length != 2) {
                    continue;
                }

                string folder = parts[0];
                if (folder != "Body" && folder != "Head" && folder != "WoundView") {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(parts[1]);
                // older saves put head pngs in the Body folder. the sprite name is what the body looks up.
                if (!SkinImporter.IsPackableSpriteName(name)) {
                    continue;
                }

                bool isLeft = SkinJson.IsLeftName(name);
                if (isLeft && !package.AcceptsLeftEntry(folder)) {
                    continue;
                }

                if (package.HasSprite(name, isLeft)) {
                    continue;
                }

                if (!MpSkinRules.EntrySizeOk(entry.Length)) {
                    return Reject(package);
                }

                total += (int)entry.Length;
                if (!MpSkinRules.TotalSizeOk(total)) {
                    return Reject(package);
                }

                if (!MpSkinRules.CompressionRatioOk(total, zipBytes.Length)) {
                    return Reject(package);
                }

                byte[] data;
                using (Stream stream = entry.Open()) {
                    if (!TryReadLimited(stream, MpSkinRules.MaxEntryBytes, out data)) {
                        return Reject(package);
                    }
                }

                HashPart(content, path, data);

                if (!MpSkinRules.TryReadPngSize(data, out int pngWidth, out int pngHeight) ||
                    !MpSkinRules.TextureSizeOk(pngWidth, pngHeight)) {
                    continue;
                }

                pixels += (long)pngWidth * pngHeight;
                if (!MpSkinRules.TexturePixelsOk(pixels)) {
                    return Reject(package);
                }

                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) {
                    filterMode = FilterMode.Point
                };

                if (!ImageConversion.LoadImage(texture, data) ||
                    !MpSkinRules.TextureSizeOk(texture.width, texture.height)) {
                    UnityEngine.Object.Destroy(texture);
                    continue;
                }

                if (isLeft) {
                    package.AddLeftSprite(name, texture);
                    continue;
                }

                Sprite sprite = Sprite.Create(texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 8f);
                sprite.name = name;
                package.Textures.Add(texture);
                package.Sprites[name] = sprite;

                if (folder == "WoundView" && HasColorPixels(texture)) {
                    package.ColoredWoundSprites.Add(name);
                }
            }
        } catch (Exception ex) {
            SpriteReplacerPlugin.Log.LogWarning("Skin package failed: " + ex.Message);
            return Reject(package);
        }

        package.LinkLeftSprites();
        content.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        package.ContentHash = Hex(content.Hash);
        return package.Sprites.Count == 0 ? Reject(package) : package;
    }

    internal void Dispose() {
        DisposeLeft();

        if (Sprites != null) {
            foreach (Sprite sprite in Sprites.Values) {
                if (sprite != null) {
                    UnityEngine.Object.Destroy(sprite);
                }
            }
            Sprites.Clear();
        }

        ColoredWoundSprites.Clear();

        foreach (Texture2D texture in Textures) {
            if (texture != null) {
                UnityEngine.Object.Destroy(texture);
            }
        }
        Textures.Clear();
    }

    private static MpSkinPackage Reject(MpSkinPackage package) {
        package.Dispose();
        return null;
    }

    private static void AddJsonEntries(ZipArchive zip, string slotRoot, SHA256 content) {
        foreach (KeyValuePair<string, byte[]> pair in SkinJsonFile.ToPngEntries(slotRoot, SpriteReplacerPlugin.ActiveSpecies)) {
            HashPart(content, pair.Key, pair.Value);
            ZipArchiveEntry entry = zip.CreateEntry(pair.Key);
            using Stream target = entry.Open();
            target.Write(pair.Value, 0, pair.Value.Length);
        }
    }

    private static void HashPart(SHA256 sha, string name, byte[] data) {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] lengthBytes = BitConverter.GetBytes(data.Length);
        sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
        sha.TransformBlock(lengthBytes, 0, lengthBytes.Length, null, 0);
        sha.TransformBlock(data, 0, data.Length, null, 0);
    }

    private static bool TryReadLimited(Stream stream, int limit, out byte[] data) {
        using MemoryStream memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) {
            if (memory.Length + read > limit) {
                data = null;
                return false;
            }
            memory.Write(buffer, 0, read);
        }

        data = memory.ToArray();
        return true;
    }

    private static string Sanitize(string value) {
        if (string.IsNullOrEmpty(value)) {
            return "remote";
        }

        value = value.Trim();
        return value.Length > 64 ? value.Substring(0, 64) : value;
    }

    private static string HashHex(byte[] data) {
        using SHA256 sha = SHA256.Create();
        return Hex(sha.ComputeHash(data));
    }

    private static string Hex(byte[] hash) {
        StringBuilder text = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash) {
            text.Append(value.ToString("x2"));
        }

        return text.ToString();
    }
}