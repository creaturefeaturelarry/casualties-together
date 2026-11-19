using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using UnityEngine;

// Legacy: reading the PNG folders of a slot and zipping them for other players.

// Legacy: one PNG per sprite in a slot's Body, Head and WoundView folders, named like the sprite.
internal static class LegacySkinFolder {
    // names ending in _left are V1.1 twins and go to onLeftFile, except in the wound view, which has none.
    internal static void Load(string folder, bool isWoundView, IDictionary<string, Sprite> into,
        ISet<string> coloredWound, Action<string, string> onLeftFile) {
        if (!Directory.Exists(folder)) {
            return;
        }

        foreach (string file in Directory.GetFiles(folder, "*.png")) {
            string spriteName = Path.GetFileNameWithoutExtension(file);
            if (SkinJson.IsLeftName(spriteName)) {
                if (!isWoundView) {
                    onLeftFile(file, spriteName);
                }

                continue;
            }

            byte[] data = File.ReadAllBytes(file);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            if (!ImageConversion.LoadImage(tex, data)) {
                UnityEngine.Object.Destroy(tex);
                SpriteReplacerPlugin.Log.LogWarning("Could not decode sprite: " + file);
                continue;
            }

            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 8f);
            sprite.name = spriteName;
            into[spriteName] = sprite;

            if (isWoundView && HasColorPixels(tex)) {
                coloredWound.Add(spriteName);
            }
        }
    }

    private static bool HasColorPixels(Texture2D tex) {
        Color32[] pixels = tex.GetPixels32();
        for (int i = 0; i < pixels.Length; i++) {
            Color32 p = pixels[i];
            if (p.a < 128) {
                continue;
            }

            if (p.r != p.g || p.g != p.b) {
                return true;
            }
        }

        return false;
    }
}

// Legacy: a skin zip made from the PNG folders of a slot.
internal sealed partial class MpSkinPackage {
    private static void AddFolder(ZipArchive zip, string folder, string entryFolder, SHA256 content) {
        if (!Directory.Exists(folder)) {
            return;
        }

        string[] files = Directory.GetFiles(folder, "*.png");
        Array.Sort(files, StringComparer.Ordinal);

        int dropped = 0;
        System.Text.StringBuilder droppedNames = new System.Text.StringBuilder();
        foreach (string file in files) {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!SkinImporter.IsPackableSpriteName(name)) {
                dropped++;
                if (dropped <= 8) {
                    if (droppedNames.Length > 0) {
                        droppedNames.Append(',');
                    }
                    droppedNames.Append(name);
                }
                continue;
            }

            byte[] data = File.ReadAllBytes(file);
            string entryName = entryFolder + "/" + Path.GetFileName(file);
            HashPart(content, entryName, data);

            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using Stream target = entry.Open();
            target.Write(data, 0, data.Length);
        }

        if (dropped > 0) {
            SkinTrace.Problem("zip-dropped", "folder=" + entryFolder
                + " dropped=" + dropped
                + " names=" + droppedNames);
        }
    }

    private static bool HasColorPixels(Texture2D tex) {
        Color32[] pixels = tex.GetPixels32();
        for (int i = 0; i < pixels.Length; i++) {
            Color32 p = pixels[i];
            if (p.a < 128) {
                continue;
            }

            if (p.r != p.g || p.g != p.b) {
                return true;
            }
        }

        return false;
    }
}
