using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

internal static class WoundViewIntegration {
    private static readonly Dictionary<int, Sprite> Originals = new Dictionary<int, Sprite>();
    // vanilla slot size, taken while the stock sprite is still on the image
    private static readonly Dictionary<int, Vector2> StockSizes = new Dictionary<int, Vector2>();

    internal static void ApplyAll() {
        foreach (WoundView view in Resources.FindObjectsOfTypeAll<WoundView>()) {
            if (view == null) {
                continue;
            }

            if (!view.gameObject.scene.IsValid()) {
                continue;
            }

            Apply(view);
        }
    }

    // species retarget swaps stock head sprites; keep the lookup key on the live stock art.
    internal static void RememberStock(Image image) {
        if (image == null || image.sprite == null) {
            return;
        }

        int id = image.GetInstanceID();
        Originals[id] = image.sprite;
        StockSizes[id] = image.rectTransform.sizeDelta;
    }

    // Replacement art loads at 8 pixels per unit, so scale the native size by the ratio to the stock sprite.
    private static void SizeLikeStock(Image image, Sprite stock, Sprite replacement) {
        image.SetNativeSize();
        if (stock != null && stock.pixelsPerUnit > 0f && replacement.pixelsPerUnit > 0f) {
            image.rectTransform.sizeDelta *= replacement.pixelsPerUnit / stock.pixelsPerUnit;
        }
    }

    internal static void Apply(WoundView view) {
        if (view == null) {
            return;
        }

        IReadOnlyDictionary<string, Sprite> sprites = SpritesForView(view);
        HashSet<string> colored = ColoredForView(view);
        HashSet<string> replaced = new HashSet<string>();
        foreach (Image image in view.GetComponentsInChildren<Image>(true)) {
            if (image == null || image.sprite == null) {
                continue;
            }

            int id = image.GetInstanceID();
            Sprite original;
            if (!Originals.TryGetValue(id, out original) || original == null) {
                original = image.sprite;
                Originals[id] = original;
            }
            // only sample the box while stock art is showing. a custom sprite's size is the one we just wrote.
            if (image.sprite == original) {
                StockSizes[id] = image.rectTransform.sizeDelta;
            }
            DefaultSpriteExport.Capture(original);

            Sprite replacement;
            if (sprites != null && sprites.TryGetValue(original.name, out replacement) && replacement != null) {
                if (image.sprite != replacement) {
                    image.sprite = replacement;
                    // custom wound art is often larger/square vs stock; match vanilla Start() sizing.
                    image.SetNativeSize();
                }
                // vanilla slot times how the new png compares to the stock sprite. gunsaw does this.
                // setNativeSize was ignoring that slot, and our sprites are 8 pixels per unit, so they blew up.
                FitToStock(image, id, original, replacement);
                replaced.Add(replacement.name);
            } else if (image.sprite != original) {
                image.sprite = original;
                if (StockSizes.TryGetValue(id, out Vector2 stockSize)) {
                    image.rectTransform.sizeDelta = stockSize;
                }
            }
        }

        foreach (SpriteRenderer renderer in view.GetComponentsInChildren<SpriteRenderer>(true)) {
            if (renderer == null || renderer.sprite == null) {
                continue;
            }

            Sprite original;
            if (!Originals.TryGetValue(renderer.GetInstanceID(), out original) || original == null) {
                original = renderer.sprite;
                Originals[renderer.GetInstanceID()] = original;
            }
            DefaultSpriteExport.Capture(original);

            Sprite replacement;
            if (sprites != null && sprites.TryGetValue(original.name, out replacement) && replacement != null) {
                renderer.sprite = replacement;
                replaced.Add(replacement.name);
            } else {
                renderer.sprite = original;
            }
        }

        ShowCustomColors(view, colored);
        DefaultSpriteExport.ExportWoundSpecies(view);
        DefaultSpriteExport.ExportNew();
    }

    // gunsaw keeps the vanilla box, then grows it only by how the new drawing compares to the stock sprite.
    private static void FitToStock(Image image, int id, Sprite stock, Sprite custom) {
        if (!StockSizes.TryGetValue(id, out Vector2 box)) {
            box = image.rectTransform.sizeDelta;
            StockSizes[id] = box;
        }

        float stockW = stock != null ? stock.rect.width : 0f;
        float stockH = stock != null ? stock.rect.height : 0f;
        float scaleX = stockW > 0.5f ? custom.rect.width / stockW : 1f;
        float scaleY = stockH > 0.5f ? custom.rect.height / stockH : 1f;
        image.rectTransform.sizeDelta = new Vector2(box.x * scaleX, box.y * scaleY);
    }

    // the game tints the inner wound near black. keep a custom color visible.
    internal static void ShowCustomColors(WoundView view) {
        ShowCustomColors(view, ColoredForView(view));
    }

    private static void ShowCustomColors(WoundView view, HashSet<string> colored) {
        if (view == null || view.limbImages == null || colored == null) {
            return;
        }

        foreach (Image image in view.limbImages) {
            if (image == null || image.transform.childCount == 0) {
                continue;
            }

            Image middle = image.transform.GetChild(0).GetComponent<Image>();
            if (middle == null || middle.sprite == null) {
                continue;
            }

            if (!colored.Contains(middle.sprite.name)) {
                continue;
            }

            Color color = middle.color;
            middle.color = new Color(1f, 1f, 1f, color.a);
        }
    }

    internal static void RefreshForBody(Body body) {
        if (body == null) {
            return;
        }

        foreach (WoundView view in Resources.FindObjectsOfTypeAll<WoundView>()) {
            if (view == null || view.body != body || !view.gameObject.scene.IsValid()) {
                continue;
            }

            Apply(view);
        }
    }

    private static IReadOnlyDictionary<string, Sprite> SpritesForView(WoundView view) {
        Body body = view != null ? view.body : null;
        // a remote panel with no package stays stock. falling back to LoadedSprites painted the local wound set onto them.
        if (body != null && !MpBody.CanApply(body)) {
            return MpSkinSync.SpritesForBody(body);
        }
        return SpriteReplacerPlugin.LoadedSprites;
    }

    private static HashSet<string> ColoredForView(WoundView view) {
        Body body = view != null ? view.body : null;
        if (body != null && !MpBody.CanApply(body)) {
            return MpSkinSync.ColoredWoundSpritesForBody(body);
        }
        return SpriteReplacerPlugin.ColoredWoundSprites;
    }
}