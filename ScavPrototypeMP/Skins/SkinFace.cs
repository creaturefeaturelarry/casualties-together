using System;
using System.Collections.Generic;
using UnityEngine;

// puts a skin's sprites on a face and takes them off again.
internal static class SkinFace {
    public static void ApplyReplacementToFace(FacialExpression face) {
        if (face == null || !MpBody.CanApply(face.body)) {
            return;
        }

        ApplyReplacementToFace(face, SpriteReplacerPlugin.LoadedSprites);
    }

    static readonly IReadOnlyDictionary<string, Sprite> EmptySpriteMap =
        new Dictionary<string, Sprite>();

    // slot apply renames dune/milky face fields to experiment* via aliases. empty-map
    // replaceSprite can only recover stock by the *current* name, so default never
    // returns duneHeadBack. keep the pre-skin field sprites and write them back.
    sealed class FaceStock {
        internal Sprite DefaultHead;
        internal Sprite DefaultHeadMouth;
        internal Sprite DefaultHeadMouthHalf;
        internal Sprite EyesGone;
        internal Sprite EyesGoneHealed;
        internal Sprite[] DisfiguredHead;
        internal Sprite[] DisfiguredHeadHeal;
        internal Eye[] Eyes;
    }

    static readonly Dictionary<int, FaceStock> FaceStocks = new Dictionary<int, FaceStock>();
    static int _faceStockPruneAt = StaleIds.FirstThreshold;

    static void CaptureFaceStock(FacialExpression face) {
        if (face == null) {
            return;
        }

        int id = face.GetInstanceID();
        if (FaceStocks.ContainsKey(id)) {
            return;
        }

        FaceStock stock = new FaceStock {
            // a clone's fields are already the skin. remember the vanilla sprite, not that copy.
            DefaultHead = PreferStock(face.defaultHead),
            DefaultHeadMouth = PreferStock(face.defaultHeadMouth),
            DefaultHeadMouthHalf = PreferStock(face.defaultHeadMouthHalf),
            EyesGone = PreferStock(face.eyesGone),
            EyesGoneHealed = PreferStock(face.eyesGoneHealed),
        };

        if (face.disfiguredHead != null) {
            stock.DisfiguredHead = (Sprite[])face.disfiguredHead.Clone();
        }

        if (face.disfiguredHeadHeal != null) {
            stock.DisfiguredHeadHeal = (Sprite[])face.disfiguredHeadHeal.Clone();
        }

        if (face.eyeList != null) {
            stock.Eyes = face.eyeList.ToArray();
        }

        StaleIds.Prune<FacialExpression, FaceStock>(FaceStocks, ref _faceStockPruneAt);
        FaceStocks[id] = stock;
        DefaultSpriteExport.Capture(face.defaultHead);
        DefaultSpriteExport.Capture(face.defaultHeadMouth);
        DefaultSpriteExport.Capture(face.defaultHeadMouthHalf);
    }

    // stock export is the vanilla sprite. the field may already be the skin.
    static Sprite PreferStock(Sprite current) {
        if (current == null) {
            return null;
        }

        if (DefaultSpriteExport.TryGetStock(current.name, out Sprite stock) && stock != null) {
            return stock;
        }

        // no stock sprite recorded for this name: keep the current one
        return current;
    }

    // facialExpression caches head/eye sprites in fields - SR Reset alone leaves a custom head.
    public static void RestoreFaceToStock(FacialExpression face) {
        if (face == null) {
            return;
        }

        int id = face.GetInstanceID();
        if (FaceStocks.TryGetValue(id, out FaceStock stock)) {
            face.defaultHead = stock.DefaultHead;
            face.defaultHeadMouth = stock.DefaultHeadMouth;
            face.defaultHeadMouthHalf = stock.DefaultHeadMouthHalf;
            face.eyesGone = stock.EyesGone;
            face.eyesGoneHealed = stock.EyesGoneHealed;

            if (stock.DisfiguredHead != null && face.disfiguredHead != null) {
                int n = Math.Min(stock.DisfiguredHead.Length, face.disfiguredHead.Length);
                for (int i = 0; i < n; i++) {
                    face.disfiguredHead[i] = stock.DisfiguredHead[i];
                }
            }

            if (stock.DisfiguredHeadHeal != null && face.disfiguredHeadHeal != null) {
                int n = Math.Min(stock.DisfiguredHeadHeal.Length, face.disfiguredHeadHeal.Length);
                for (int i = 0; i < n; i++) {
                    face.disfiguredHeadHeal[i] = stock.DisfiguredHeadHeal[i];
                }
            }

            if (stock.Eyes != null && face.eyeList != null) {
                int n = Math.Min(stock.Eyes.Length, face.eyeList.Count);
                for (int i = 0; i < n; i++) {
                    face.eyeList[i] = stock.Eyes[i];
                }
            }

            if (face.nosebleedSprite != null) {
                SkinSpriteReplacer replacer = face.nosebleedSprite.GetComponent<SkinSpriteReplacer>();
                if (replacer != null) {
                    replacer.Restore();
                }
            }

            if (face.head != null && stock.DefaultHead != null) {
                face.head.sprite = stock.DefaultHead;
            }

            SkinTrace.Event("face-restore-stock",
                "head=" + (stock.DefaultHead != null ? stock.DefaultHead.name : "-")
                + " " + SkinTrace.BodyState(face.body));
            return;
        }

        ApplyReplacementToFace(face, EmptySpriteMap);
    }

    // remote MP skins pass their package sprites; local uses SpriteReplacerPlugin.LoadedSprites.
    public static void ApplyReplacementToFace(FacialExpression face,
        IReadOnlyDictionary<string, Sprite> sprites) {
        if (face == null || sprites == null) {
            return;
        }

        if (sprites.Count > 0) {
            CaptureFaceStock(face);
        }

        // always start from the pre-skin sprites. the last slot renames duneHeadBack to
        // experimentHeadBack, and the next slot would then restore the experiment head.
        FaceStocks.TryGetValue(face.GetInstanceID(), out FaceStock stock);
        Sprite headBase = stock != null ? stock.DefaultHead : face.defaultHead;
        Sprite mouthBase = stock != null ? stock.DefaultHeadMouth : face.defaultHeadMouth;
        Sprite mouthHalfBase = stock != null ? stock.DefaultHeadMouthHalf : face.defaultHeadMouthHalf;
        Sprite eyesGoneBase = stock != null ? stock.EyesGone : face.eyesGone;
        Sprite eyesGoneHealedBase = stock != null ? stock.EyesGoneHealed : face.eyesGoneHealed;

        string before = headBase != null ? headBase.name : "-";
        SkinTrace.Event("face-apply", SkinTrace.FaceApplySummary(face, sprites)
            + " " + SkinTrace.BodyState(face.body));

        face.defaultHead = ReplaceSprite(headBase, sprites);
        face.defaultHeadMouth = ReplaceSprite(mouthBase, sprites);
        face.defaultHeadMouthHalf = ReplaceSprite(mouthHalfBase, sprites);
        face.eyesGone = ReplaceSprite(eyesGoneBase, sprites);
        face.eyesGoneHealed = ReplaceSprite(eyesGoneHealedBase, sprites);

        if (face.nosebleedSprite != null) {
            DefaultSpriteExport.Capture(face.nosebleedSprite.sprite);

            SkinSpriteReplacer replacer = face.nosebleedSprite.GetComponent<SkinSpriteReplacer>();
            if (replacer == null) {
                replacer = SkinSpriteReplacer.Attach(face.nosebleedSprite, sprites);
                replacer.Apply();
            } else {
                replacer.SetSprites(sprites);
            }
        }

        if (face.disfiguredHead != null) {
            for (int i = 0; i < face.disfiguredHead.Length; i++) {
                Sprite basis = face.disfiguredHead[i];
                if (stock != null && stock.DisfiguredHead != null && i < stock.DisfiguredHead.Length) {
                    basis = stock.DisfiguredHead[i];
                }
                face.disfiguredHead[i] = ReplaceSprite(basis, sprites);
            }
        }

        if (face.disfiguredHeadHeal != null) {
            for (int i = 0; i < face.disfiguredHeadHeal.Length; i++) {
                Sprite basis = face.disfiguredHeadHeal[i];
                if (stock != null && stock.DisfiguredHeadHeal != null && i < stock.DisfiguredHeadHeal.Length) {
                    basis = stock.DisfiguredHeadHeal[i];
                }
                face.disfiguredHeadHeal[i] = ReplaceSprite(basis, sprites);
            }
        }

        if (face.eyeList != null) {
            for (int i = 0; i < face.eyeList.Count; i++) {
                Eye eye = face.eyeList[i];
                Eye basis = eye;
                if (stock != null && stock.Eyes != null && i < stock.Eyes.Length) {
                    basis = stock.Eyes[i];
                }
                eye.front = ReplaceSprite(basis.front, sprites);
                eye.back = ReplaceSprite(basis.back, sprites);
                face.eyeList[i] = eye;
            }
        }

        string after = face.defaultHead != null ? face.defaultHead.name : "-";
        int headSpecies = -1;
        string afterLower = after.ToLowerInvariant();
        if (afterLower.Contains("dune")) {
            headSpecies = 2;
        } else if (afterLower.Contains("milk")) {
            headSpecies = 1;
        } else if (afterLower.StartsWith("experiment") || SkinImporter.HeadNames.Contains(after)) {
            headSpecies = 0;
        }
        int charType = face.body != null ? face.body.charType : -1;
        int clash = headSpecies >= 0 && charType >= 0 && charType <= 2 && headSpecies != charType ? 1 : 0;
        int faceId = face.body != null ? face.body.GetInstanceID() : 0;
        SkinTrace.Problem("face-applied " + faceId, "beforeHead=" + before + " afterHead=" + after
            + " stockMiss=" + (sprites.Count == 0 || !sprites.ContainsKey(before) ? 1 : 0)
            + " headSpecies=" + headSpecies
            + " headClash=" + clash
            + " map=" + sprites.Count
            + " " + SkinTrace.BodyState(face.body));
    }

    private static Sprite ReplaceSprite(Sprite s) {
        return ReplaceSprite(s, SpriteReplacerPlugin.LoadedSprites);
    }

    private static Sprite ReplaceSprite(Sprite s, IReadOnlyDictionary<string, Sprite> sprites) {
        DefaultSpriteExport.Capture(s);

        if (s == null) {
            return null;
        }

        if (sprites != null && sprites.TryGetValue(s.name, out Sprite r) && r != null) {
            return r;
        }

        // missing in this slot (empty Head folder, etc.) - restore stock, do not keep the prior skin.
        if (DefaultSpriteExport.TryGetStock(s.name, out Sprite stock)) {
            return stock;
        }

        return s;
    }
}
