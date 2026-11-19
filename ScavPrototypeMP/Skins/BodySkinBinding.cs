using System.Collections.Generic;
using UnityEngine;

// the local body gets the active slot. remotes wait for a synced skin.
public sealed class BodySkinBinding : MonoBehaviour {
    private readonly List<SkinSpriteReplacer> _replacers = new List<SkinSpriteReplacer>();
    private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
    private IReadOnlyDictionary<string, Sprite> _sprites;
    private IReadOnlyDictionary<string, Sprite> _package;

    internal string SetName { get; private set; }

    // package is the synced dictionary. mapped sprites are what the body actually looks up.
    internal bool Wearing(IReadOnlyDictionary<string, Sprite> package) {
        return package != null && ReferenceEquals(_package, package);
    }

    // a limb rebuilt after the bind has a renderer and no replacer. the face field still shows the skin.
    internal int CountMissingRenderers() {
        int missing = 0;
        SpriteRenderer[] renderers = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < renderers.Length; i++) {
            SpriteRenderer renderer = renderers[i];
            if (renderer == null || renderer.sprite == null) {
                continue;
            }

            SkinSpriteReplacer replacer = renderer.GetComponent<SkinSpriteReplacer>();
            if (replacer == null || !replacer.Active) {
                missing++;
            }
        }

        return missing;
    }

    internal void Bind(Body body, IReadOnlyDictionary<string, Sprite> sprites, string setName) {
        Bind(body, sprites, setName, sprites);
    }

    internal void Bind(Body body, IReadOnlyDictionary<string, Sprite> sprites, string setName,
        IReadOnlyDictionary<string, Sprite> package) {
        if (body == null) {
            return;
        }

        _package = package ?? sprites;
        SetName = setName ?? "";

        _renderers.Clear();
        foreach (SpriteRenderer renderer in body.GetComponentsInChildren<SpriteRenderer>(true)) {
            _renderers.Add(renderer);
        }

        _sprites = sprites;
        bool localDict = SkinTrace.UsesLocalSprites(sprites);
        bool localBody = MpBody.CanApply(body);
        SkinTrace.Event("bind", "set=" + SetName
            + " renderers=" + _renderers.Count
            + " localDict=" + (localDict ? 1 : 0)
            + " localBody=" + (localBody ? 1 : 0)
            + " " + SkinTrace.BodyState(body)
            + " " + SkinTrace.SlotSummary(SetName, sprites, body.charType));
        if (!localBody && localDict) {
            SkinTrace.Problem("bind-local-onto-remote " + body.GetInstanceID(),
                "set=" + SetName
                + " net=" + MpBody.GetNetworkIdForBody(body)
                + " " + SkinTrace.BodyState(body));
        }
        Apply();
        SkinTrace.NoteDefaultLeak("bind", body);
    }

    internal void Apply() {
        foreach (SkinSpriteReplacer replacer in _replacers) {
            if (replacer != null) {
                replacer.Restore();
            }
        }
        _replacers.Clear();

        if (_sprites == null) {
            return;
        }

        int replaced = 0;
        int missed = 0;
        System.Text.StringBuilder missedNames = new System.Text.StringBuilder();
        Body body = GetComponent<Body>();
        foreach (SpriteRenderer renderer in _renderers) {
            if (renderer == null || renderer.sprite == null) {
                continue;
            }

            string before = renderer.sprite.name;
            SkinSpriteReplacer replacer = SkinSpriteReplacer.Attach(renderer, _sprites);
            replacer.Apply();
            _replacers.Add(replacer);
            string after = renderer.sprite != null ? renderer.sprite.name : "-";
            bool hit = _sprites.ContainsKey(before) || after != before;
            if (hit) {
                replaced++;
            } else if (missed < 8) {
                missed++;
                if (missedNames.Length > 0) {
                    missedNames.Append(',');
                }
                missedNames.Append(before);
            } else {
                missed++;
            }
        }

        string audit = SkinTrace.RendererAudit(body, replaced, missed, missedNames.ToString());
        int bodyId = body != null ? body.GetInstanceID() : 0;
        SkinTrace.Problem("bind-result " + bodyId, "set=" + SetName + " " + audit
            + " " + SkinTrace.BodyState(body));
    }

    internal void Reset() {
        foreach (SkinSpriteReplacer replacer in _replacers) {
            if (replacer == null) {
                continue;
            }

            replacer.Restore();
            Destroy(replacer);
        }
        _replacers.Clear();
        _sprites = null;
        _package = null;
    }

    private void OnDestroy() => Reset();
}