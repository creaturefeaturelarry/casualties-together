using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

// name swap on the sprite setter, so a later expression keeps the skin.
public sealed class SkinSpriteReplacer : MonoBehaviour {
    public SpriteRenderer Renderer { get; private set; }
    public Sprite Original { get; private set; }
    public bool Active { get; private set; }
    public IReadOnlyDictionary<string, Sprite> Sprites { get; private set; }

    public static SkinSpriteReplacer Attach(SpriteRenderer renderer,
        IReadOnlyDictionary<string, Sprite> sprites) {
        SkinSpriteReplacer replacer = renderer.GetComponent<SkinSpriteReplacer>();
        if (replacer == null) {
            replacer = renderer.gameObject.AddComponent<SkinSpriteReplacer>();
        }

        // keep the first stock sprite. a later skin names the renderer experiment*, and
        // looking that name up again would stick an experiment limb on a dune or milky.
        Sprite current = renderer != null ? renderer.sprite : null;
        if (current != null) {
            DefaultSpriteExport.Capture(current);
        }
        if (replacer.Original == null || IsDestroyed(replacer.Original)) {
            if (current != null && DefaultSpriteExport.TryGetStock(current.name, out Sprite stock)) {
                replacer.Original = stock;
            } else {
                replacer.Original = current;
            }
        }

        if (replacer.Original != null) {
            DefaultSpriteExport.Capture(replacer.Original);
        }

        replacer.Renderer = renderer;
        replacer.Sprites = sprites;
        return replacer;
    }

    public void SetSprites(IReadOnlyDictionary<string, Sprite> sprites) {
        Sprites = sprites;
        Apply();
    }

    public void Apply() {
        Active = true;
        if (Renderer == null) {
            return;
        }

        Sprite stock = ResolveStock();
        if (stock != null) {
            Renderer.sprite = stock;
        }

        if (Renderer.sprite == null || Sprites == null) {
            return;
        }

        if (Sprites.TryGetValue(Renderer.sprite.name, out Sprite replacement) &&
            !IsDestroyed(replacement)) {
            Renderer.sprite = replacement;
        }
    }

    public void Restore() {
        Active = false;
        // instantiate keeps the sprite ref and drops this pointer, so a cloned body cannot restore.
        if (Renderer == null) {
            Renderer = GetComponent<SpriteRenderer>();
        }
        if (Renderer == null) {
            return;
        }

        Sprite stock = ResolveStock();
        if (stock != null) {
            Renderer.sprite = stock;
        }
    }

    private Sprite ResolveStock() {
        if (Original != null && !IsDestroyed(Original)) {
            if (DefaultSpriteExport.TryGetStock(Original.name, out Sprite stock)) {
                Original = stock;
                return stock;
            }

            return Original;
        }

        if (Renderer != null && Renderer.sprite != null &&
            DefaultSpriteExport.TryGetStock(Renderer.sprite.name, out Sprite captured)) {
            Original = captured;
            return captured;
        }

        return null;
    }

    private static bool IsDestroyed(Sprite sprite) {
        // unity fake-null for destroyed objects.
        return sprite == null;
    }

    private void OnDestroy() => Restore();
}

[HarmonyPatch(typeof(SpriteRenderer), nameof(SpriteRenderer.sprite), MethodType.Setter)]
internal static class SkinSpritePatch {
    private static void Prefix(SpriteRenderer __instance, ref Sprite value) {
        DefaultSpriteExport.Capture(value);

        SkinSpriteReplacer replacer = __instance.GetComponent<SkinSpriteReplacer>();
        if (replacer != null && replacer.Active && value != null && replacer.Sprites != null
            && replacer.Sprites.TryGetValue(value.name, out Sprite replacement) && replacement != null) {
            value = replacement;
            Body owner = __instance.GetComponentInParent<Body>();
            if (owner != null && !MpBody.CanApply(owner)
                && (ReferenceEquals(replacer.Sprites, SpriteReplacerPlugin.LoadedSprites)
                    || SkinTrace.UsesLocalSprites(replacer.Sprites))) {
                SkinTrace.NoteDefaultLeak("sprite-set", owner);
            }
        }

        // limbs have no replacer, so this runs for every renderer.
        value = SkinFacing.Choose(__instance, value);
    }
}