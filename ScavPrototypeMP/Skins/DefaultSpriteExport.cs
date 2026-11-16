using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

// game sprites land in CustomSprites/<species>/default. an existing png is left alone.
internal static class DefaultSpriteExport {
    private const string FolderName = "default";

    private static readonly string[] PrefabPaths =
    {
        "Special/Characters/Experiment",
        "Special/Characters/Milky",
        "Special/Characters/Dune"
    };

    private static readonly Dictionary<string, Sprite> Captured =
        new Dictionary<string, Sprite>(StringComparer.Ordinal);

    internal static void Capture(Sprite sprite) {
        if (sprite == null) {
            return;
        }

        string name = sprite.name;
        if (string.IsNullOrEmpty(name) || Captured.ContainsKey(name)) {
            return;
        }

        if (FolderFor(name) == null) {
            return;
        }

        // loaded replacements keep the stock name. never record them as vanilla.
        if (SpriteReplacerPlugin.LoadedSprites != null &&
            SpriteReplacerPlugin.LoadedSprites.TryGetValue(name, out Sprite loaded) &&
            ReferenceEquals(loaded, sprite)) {
            return;
        }

        Captured[name] = sprite;
    }

    internal static bool TryGetStock(string name, out Sprite sprite) {
        if (string.IsNullOrEmpty(name)) {
            sprite = null;
            return false;
        }

        return Captured.TryGetValue(name, out sprite) && sprite != null;
    }

    private static string FolderFor(string name) {
        if (SkinImporter.BodyNames.Contains(name)) {
            return "Body";
        }

        if (SkinImporter.HeadNames.Contains(name)) {
            return "Head";
        }

        if (SkinImporter.WoundViewNames.Contains(name)) {
            return "WoundView";
        }

        return null;
    }

    // an edited file is never overwritten. delete one png to get a fresh copy.
    internal static int ExportNew() {
        if (Captured.Count == 0) {
            return 0;
        }

        int written = 0;

        foreach (KeyValuePair<string, Sprite> pair in Captured) {
            string folder = FolderFor(pair.Key);
            if (folder == null) {
                continue;
            }

            // milky and dune head masks are not the exp panel sprites.
            string root = Path.Combine(SpriteReplacerPlugin.SpeciesRoot(SpeciesFolder(pair.Key)), FolderName);
            string path = Path.Combine(root, folder, pair.Key + ".png");
            if (File.Exists(path)) {
                continue;
            }

            byte[] png = Encode(pair.Value);
            if (png == null) {
                continue;
            }

            try {
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.WriteAllBytes(path, png);
                written++;
            } catch (Exception ex) {
                SpriteReplacerPlugin.Log.LogWarning(
                    "Could not export default sprite " + path + ": " + ex.Message);
            }
        }

        if (written > 0) {
            SpriteReplacerPlugin.Log.LogInfo("Exported " + written + " default sprites.");
        }

        return written;
    }

    internal static int ExportLoadedWoundViews() {
        int written = 0;
        WoundView[] views = Resources.FindObjectsOfTypeAll<WoundView>();
        for (int i = 0; i < views.Length; i++) {
            WoundView view = views[i];
            if (view == null || !view.gameObject.scene.IsValid()) {
                continue;
            }
            written += ExportWoundSpecies(view);
        }
        return written;
    }

    // headCharSprites is exp [0,1], milky [2,3], dune [4,5]. other wound images are shared.
    internal static int ExportWoundSpecies(WoundView view) {
        if (view == null || view.headCharSprites == null || view.headCharSprites.Length < 6) {
            return 0;
        }

        int written = 0;
        for (int species = 0; species < 3; species++) {
            Sprite outline = view.headCharSprites[species * 2];
            Sprite middle = view.headCharSprites[species * 2 + 1];
            if (WriteNew(species, "WoundView", outline)) {
                written++;
            }
            if (WriteNew(species, "WoundView", middle)) {
                written++;
            }
        }

        Image[] images = view.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < images.Length; i++) {
            Image image = images[i];
            if (image == null || image.sprite == null) {
                continue;
            }
            if (!SkinImporter.WoundViewNames.Contains(image.sprite.name) || IsHeadSprite(view, image.sprite)) {
                continue;
            }
            for (int species = 0; species < 3; species++) {
                if (WriteNew(species, "WoundView", image.sprite)) {
                    written++;
                }
            }
        }

        if (written > 0) {
            SpriteReplacerPlugin.Log.LogInfo("Exported " + written + " species wound view sprites.");
        }
        return written;
    }

    private static int SpeciesFolder(string name) {
        if (name == "milkheadouter" || name == "milkheadmiddle") {
            return 1;
        }
        if (name == "headOuterDune" || name == "headMiddleDune") {
            return 2;
        }
        return 0;
    }

    private static bool IsHeadSprite(WoundView view, Sprite sprite) {
        for (int i = 0; i < view.headCharSprites.Length; i++) {
            Sprite head = view.headCharSprites[i];
            if (head != null && (head == sprite || head.name == sprite.name)) {
                return true;
            }
        }
        return false;
    }

    internal static int ExportPrefabs() {
        int written = 0;

        for (int species = 0; species < PrefabPaths.Length; species++) {
            GameObject prefab = Resources.Load<GameObject>(PrefabPaths[species]);
            if (prefab == null) {
                SpriteReplacerPlugin.Log.LogWarning(
                    "Could not load " + PrefabPaths[species] + " for the default skin.");
                continue;
            }

            int body = 0;
            int head = 0;
            int bodyWrote = 0;
            int headWrote = 0;

            Limb[] limbs = prefab.GetComponentsInChildren<Limb>(true);
            for (int i = 0; i < limbs.Length; i++) {
                SpriteRenderer renderer = limbs[i].GetComponent<SpriteRenderer>();
                if (renderer == null || renderer.sprite == null) {
                    continue;
                }

                CountPart(species, renderer.sprite, "Body", ref body, ref head, ref bodyWrote, ref headWrote);
            }

            TailScript[] tails = prefab.GetComponentsInChildren<TailScript>(true);
            for (int i = 0; i < tails.Length; i++) {
                SpriteRenderer renderer = tails[i].GetComponent<SpriteRenderer>();
                if (renderer == null || renderer.sprite == null) {
                    continue;
                }

                CountPart(species, renderer.sprite, "Body", ref body, ref head, ref bodyWrote, ref headWrote);
            }

            FacialExpression[] faces = prefab.GetComponentsInChildren<FacialExpression>(true);
            for (int i = 0; i < faces.Length; i++) {
                CollectFace(species, faces[i], ref head, ref headWrote);
            }

            written += bodyWrote + headWrote;
        }

        return written;
    }

    private static void CollectFace(int species, FacialExpression face, ref int found, ref int wrote) {
        wrote += WriteHead(species, face.defaultHead, ref found);
        wrote += WriteHead(species, face.defaultHeadMouth, ref found);
        wrote += WriteHead(species, face.defaultHeadMouthHalf, ref found);
        wrote += WriteHead(species, face.eyesGone, ref found);
        wrote += WriteHead(species, face.eyesGoneHealed, ref found);

        if (face.head != null) {
            wrote += WriteHead(species, face.head.sprite, ref found);
        }

        if (face.eyes != null) {
            wrote += WriteHead(species, face.eyes.sprite, ref found);
        }

        if (face.nosebleedSprite != null) {
            wrote += WriteHead(species, face.nosebleedSprite.sprite, ref found);
        }

        if (face.disfiguredHead != null) {
            for (int i = 0; i < face.disfiguredHead.Length; i++) {
                wrote += WriteHead(species, face.disfiguredHead[i], ref found);
            }
        }
        if (face.disfiguredHeadHeal != null) {
            for (int i = 0; i < face.disfiguredHeadHeal.Length; i++) {
                wrote += WriteHead(species, face.disfiguredHeadHeal[i], ref found);
            }
        }
        if (face.eyeList != null) {
            for (int i = 0; i < face.eyeList.Count; i++) {
                Eye eye = face.eyeList[i];
                wrote += WriteHead(species, eye.front, ref found);
                wrote += WriteHead(species, eye.back, ref found);
            }
        }
    }

    // the head limb's renderer uses the head sprite, so the name picks the folder.
    private static void CountPart(int species, Sprite sprite, string fallback,
        ref int body, ref int head, ref int bodyWrote, ref int headWrote) {
        if (sprite == null) {
            return;
        }

        string folder = Bucket(sprite.name, fallback);
        if (folder == "Head") {
            head++;
            if (WriteNew(species, folder, sprite)) {
                headWrote++;
            }

            return;
        }
        body++;
        if (WriteNew(species, folder, sprite)) {
            bodyWrote++;
        }
    }

    private static string Bucket(string name, string fallback) {
        if (SkinImporter.HeadNames.Contains(name)) {
            return "Head";
        }

        if (SkinImporter.BodyNames.Contains(name)) {
            return "Body";
        }

        if (SkinImporter.WoundViewNames.Contains(name)) {
            return "WoundView";
        }

        string lower = name.ToLowerInvariant();
        if (lower.Contains("head") || lower.Contains("eye") || lower.Contains("mouth")
            || lower.Contains("nose") || lower.Contains("disfig")) {
            return "Head";
        }

        return fallback;
    }

    private static int WriteHead(int species, Sprite sprite, ref int found) {
        if (sprite == null) {
            return 0;
        }

        found++;
        return WriteNew(species, "Head", sprite) ? 1 : 0;
    }

    private static bool WriteNew(int species, string subfolder, Sprite sprite) {
        if (sprite == null || string.IsNullOrEmpty(sprite.name)) {
            return false;
        }

        // experimentHead is the hidden anim head. The visible head is experimentHeadBack.
        if (sprite.name == "experimentHead") {
            return false;
        }

        string path = Path.Combine(
            SpriteReplacerPlugin.SpeciesRoot(species), FolderName, subfolder, sprite.name + ".png");
        if (File.Exists(path)) {
            return false;
        }

        byte[] png = Encode(sprite);
        if (png == null) {
            return false;
        }

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
            return true;
        } catch (Exception ex) {
            SpriteReplacerPlugin.Log.LogWarning(
                "Could not export default sprite " + path + ": " + ex.Message);
            return false;
        }
    }

    private static byte[] Encode(Sprite sprite) {
        Texture2D source = null;

        RenderTexture rt = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D full = null;
        Texture2D cropped = null;

        try {
            source = sprite.texture;
            if (source == null) {
                return null;
            }

            // blit through a render texture. packed sprites are not readable.
            rt = RenderTexture.GetTemporary(source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            full = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
            full.Apply();
            RenderTexture.active = previous;

            Rect rect = sprite.packed ? sprite.textureRect : sprite.rect;
            int x = Mathf.RoundToInt(rect.x);
            int y = Mathf.RoundToInt(rect.y);
            int w = Mathf.RoundToInt(rect.width);
            int h = Mathf.RoundToInt(rect.height);

            if (w <= 0 || h <= 0 || x < 0 || y < 0 ||
                x + w > source.width || y + h > source.height) {
                x = 0;
                y = 0;
                w = source.width;
                h = source.height;
            }

            Texture2D image = full;
            if (w != source.width || h != source.height) {
                cropped = new Texture2D(w, h, TextureFormat.RGBA32, false);
                cropped.SetPixels(full.GetPixels(x, y, w, h));
                cropped.Apply();
                image = cropped;
            }

            return ImageConversion.EncodeToPNG(image);
        } catch (Exception ex) {
            SpriteReplacerPlugin.Log.LogWarning(
                "Could not read sprite '" + sprite.name + "': " + ex.Message);
            return null;
        } finally {
            RenderTexture.active = previous;
            if (rt != null) {
                RenderTexture.ReleaseTemporary(rt);
            }

            if (cropped != null) {
                UnityEngine.Object.Destroy(cropped);
            }

            if (full != null) {
                UnityEngine.Object.Destroy(full);
            }
        }
    }
}