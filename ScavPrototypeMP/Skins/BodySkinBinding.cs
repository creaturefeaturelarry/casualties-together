using System.Collections.Generic;
using UnityEngine;

// the local body gets the active slot. remotes wait for a synced skin.
public sealed class BodySkinBinding : MonoBehaviour {
    private readonly List<SkinSpriteReplacer> _replacers = new List<SkinSpriteReplacer>();
    private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
    private IReadOnlyDictionary<string, Sprite> _sprites;

    internal string SetName { get; private set; }

    internal void Bind(Body body, IReadOnlyDictionary<string, Sprite> sprites, string setName) {
        if (body == null) {
            return;
        }

        SetName = setName ?? "";

        _renderers.Clear();
        foreach (SpriteRenderer renderer in body.GetComponentsInChildren<SpriteRenderer>(true)) {
            _renderers.Add(renderer);
        }

        _sprites = sprites;
        SkinTrace.Event("bind", "set=" + SetName
            + " renderers=" + _renderers.Count
            + " " + SkinTrace.BodyState(body)
            + " " + SkinTrace.SlotSummary(SetName, sprites, body.charType));
        Apply();
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
    }

    private void OnDestroy() => Reset();
}