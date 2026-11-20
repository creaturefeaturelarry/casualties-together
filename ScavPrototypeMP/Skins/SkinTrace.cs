using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using Together;
using UnityEngine;

// skin join/swap trace. problems stay in the log until the detail changes.
internal static class SkinTrace {
    internal static void Event(string action, string detail = null) {
        Write(false, "[skin] " + action + " " + (detail ?? ""));
    }

    internal static string SlotSummary(string slot, IReadOnlyDictionary<string, Sprite> sprites, int species) {
        CountParts(sprites, out int body, out int head, out int other, out string sampleHead, out string sampleBody);
        string summary = "slot=" + (slot ?? "-")
            + " species=" + species
            + " sprites=" + (sprites == null ? 0 : sprites.Count)
            + " bodyKeys=" + body
            + " headKeys=" + head
            + " other=" + other
            + " sampleBody=" + sampleBody
            + " sampleHead=" + sampleHead;
        string audit = Audit(sprites, species);
        if (audit != null) {
            summary += " " + audit;
        }
        return summary;
    }

    internal static string PackageSummary(MpSkinPackage package) {
        if (package == null) {
            return "package=null";
        }

        CountParts(package.Sprites, out int body, out int head, out int other, out string sampleHead, out string sampleBody);
        return "set=" + (package.SetName ?? "-")
            + " species=" + package.Species
            + " hash=" + ShortHash(package.Hash)
            + " content=" + ShortHash(package.ContentHash)
            + " sprites=" + (package.Sprites == null ? 0 : package.Sprites.Count)
            + " bodyKeys=" + body
            + " headKeys=" + head
            + " other=" + other
            + " sampleBody=" + sampleBody
            + " sampleHead=" + sampleHead
            + " zip=" + (package.Zip == null ? 0 : package.Zip.Length);
    }

    internal static string BodyState(Body body) {
        if (body == null) {
            return "body=null ready=0 face=0 char=-1";
        }

        bool ready = false;
        bool face = false;
        int limbs = 0;
        try {
            limbs = body.limbs == null ? 0 : body.limbs.Length;
            ready = MpBody.IsBodyFullyReady(body);
            if (body.limbs != null) {
                int headIdx = (int)Body.LimbNum.Head;
                if (headIdx >= 0 && headIdx < body.limbs.Length && body.limbs[headIdx] != null) {
                    FacialExpression fe = body.limbs[headIdx].GetComponent<FacialExpression>();
                    face = fe != null && fe.head != null;
                }
            }
        } catch {
            // ignore
        }

        return "body=" + body.GetInstanceID()
            + " char=" + body.charType
            + " limbs=" + limbs
            + " ready=" + (ready ? 1 : 0)
            + " face=" + (face ? 1 : 0);
    }

    internal static string FaceApplySummary(FacialExpression face, IReadOnlyDictionary<string, Sprite> sprites) {
        if (face == null) {
            return "face=null";
        }

        string headName = face.defaultHead != null ? face.defaultHead.name : "-";
        bool mapped = sprites != null && face.defaultHead != null
            && sprites.ContainsKey(face.defaultHead.name);
        bool hasKey = false;
        if (sprites != null) {
            foreach (string key in SkinImporter.HeadNames) {
                if (sprites.ContainsKey(key)) {
                    hasKey = true;
                    break;
                }
            }
        }

        return "faceHead=" + headName
            + " mappedDefaultHead=" + (mapped ? 1 : 0)
            + " packageHasHeadKey=" + (hasKey ? 1 : 0)
            + " emptyMap=" + (sprites == null || sprites.Count == 0 ? 1 : 0);
    }

    internal static string Ids(uint network, uint player) {
        return "net=" + network + " player=" + player;
    }

    // logged once per distinct detail so a stuck apply does not reprint the same mismatch every frame
    static readonly Dictionary<string, string> LastProblem = new Dictionary<string, string>();

    internal static void Problem(string action, string detail) {
        if (string.IsNullOrEmpty(action)) {
            return;
        }

        string shown = detail ?? "";
        if (LastProblem.TryGetValue(action, out string prev) && prev == shown) {
            return;
        }

        LastProblem[action] = shown;
        bool leak = action.IndexOf("leak", StringComparison.Ordinal) >= 0
            || action.IndexOf("bind-local", StringComparison.Ordinal) >= 0;
        string line = "[skin] " + action + " " + shown;
        if (leak) {
            line += " stack=" + Stack();
        }
        Write(true, line);
    }

    static readonly Dictionary<int, float> NextLeak = new Dictionary<int, float>();
    static int _nextLeakPruneAt = StaleIds.FirstThreshold;

    // remote with no synced skin, body sprites are the local slot, head is not.
    internal static void NoteDefaultLeak(string where, Body body) {
        if (body == null || !MpBody.IsActive || MpBody.CanApply(body)) {
            return;
        }

        Dictionary<string, Sprite> loaded = SpriteReplacerPlugin.LoadedSprites;
        if (loaded == null || loaded.Count == 0) {
            return;
        }

        if (MpSkinSync.SpritesForBody(body) != null) {
            return;
        }

        int id = body.GetInstanceID();
        float now = Time.unscaledTime;
        if (NextLeak.TryGetValue(id, out float next) && now < next) {
            return;
        }
        StaleIds.Prune<Body, float>(NextLeak, ref _nextLeakPruneAt);
        NextLeak[id] = now + 1f;

        int bodyHit = 0;
        int bodySeen = 0;
        int replacerLocal = 0;
        string sample = "-";
        Limb[] limbs = body.limbs;
        int headIdx = (int)Body.LimbNum.Head;
        if (limbs != null) {
            for (int i = 0; i < limbs.Length; i++) {
                if (i == headIdx || limbs[i] == null) {
                    continue;
                }

                SpriteRenderer renderer = limbs[i].GetComponent<SpriteRenderer>();
                if (renderer == null || renderer.sprite == null) {
                    continue;
                }

                bodySeen++;
                if (IsLocalArt(renderer.sprite)) {
                    bodyHit++;
                    if (sample == "-") {
                        sample = renderer.sprite.name;
                    }
                }

                SkinSpriteReplacer replacer = renderer.GetComponent<SkinSpriteReplacer>();
                if (replacer != null && replacer.Active && ReferenceEquals(replacer.Sprites, loaded)) {
                    replacerLocal++;
                }
            }
        }

        int headHit = 0;
        string headName = "-";
        if (limbs != null && headIdx >= 0 && headIdx < limbs.Length && limbs[headIdx] != null) {
            FacialExpression face = limbs[headIdx].GetComponent<FacialExpression>();
            if (face != null) {
                if (face.head != null && face.head.sprite != null) {
                    headName = face.head.sprite.name;
                    if (IsLocalArt(face.head.sprite)) {
                        headHit++;
                    }
                }
                if (face.defaultHead != null && IsLocalArt(face.defaultHead)) {
                    headHit++;
                }
            }
        }

        if (bodyHit <= 0) {
            return;
        }

        string kind = headHit == 0 ? "default-body-leak" : "default-full-leak";
        Problem(kind + " " + id,
            "where=" + where
            + " net=" + MpBody.GetNetworkIdForBody(body)
            + " bodyHit=" + bodyHit + "/" + bodySeen
            + " sample=" + sample
            + " headHit=" + headHit
            + " head=" + headName
            + " replacerLocal=" + replacerLocal
            + " " + BodyState(body));
    }

    internal static bool UsesLocalSprites(IReadOnlyDictionary<string, Sprite> sprites) {
        if (sprites == null) {
            return false;
        }

        if (ReferenceEquals(sprites, SpriteReplacerPlugin.LoadedSprites)) {
            return true;
        }

        foreach (Sprite sprite in sprites.Values) {
            if (IsLocalArt(sprite)) {
                return true;
            }
        }

        return false;
    }

    static bool IsLocalArt(Sprite sprite) {
        if (sprite == null) {
            return false;
        }

        if (LocalSkinLeft.IsLocalTwin(sprite)) {
            return true;
        }

        Dictionary<string, Sprite> loaded = SpriteReplacerPlugin.LoadedSprites;
        if (loaded == null) {
            return false;
        }

        foreach (Sprite loadedSprite in loaded.Values) {
            if (ReferenceEquals(loadedSprite, sprite)) {
                return true;
            }
        }

        return false;
    }

    static string Stack() {
        string raw = Environment.StackTrace;
        if (string.IsNullOrEmpty(raw)) {
            return "-";
        }

        raw = raw.Replace("\r", " ").Replace("\n", " | ");
        if (raw.Length > 700) {
            raw = raw.Substring(0, 700);
        }
        return raw;
    }

    static void Write(bool problem, string line) {
        try {
            ManualLogSource source = SpriteReplacerPlugin.Log;
            if (source != null) {
                if (problem) {
                    source.LogWarning(line);
                } else {
                    source.LogInfo(line);
                }
            }
        } catch {
            // log source can be missing during early boot
        }

        if (!problem) {
            return;
        }

        if (line.IndexOf("default-body-leak", StringComparison.Ordinal) < 0
            && line.IndexOf("bind-local-onto-remote", StringComparison.Ordinal) < 0) {
            return;
        }

        try {
            log.warn(line, false);
        } catch {
            // together log follows the plugin
        }
    }

    internal static void ClearProblems() {
        LastProblem.Clear();
    }

    // null when the set matches the species. otherwise the reason a body/head would come out wrong.
    internal static string Audit(IReadOnlyDictionary<string, Sprite> sprites, int species) {
        if (sprites == null || sprites.Count == 0) {
            return "audit=empty";
        }

        int expBody = 0;
        int expHead = 0;
        int liveBody = 0;
        int liveHead = 0;
        int foreign = 0;
        string foreignSample = "-";
        foreach (string name in sprites.Keys) {
            int owner = SpeciesOfName(name);
            bool bodyName = IsBodyName(name);
            bool headName = IsHeadName(name);
            if (owner == 0 && bodyName) {
                expBody++;
            } else if (owner == 0 && headName) {
                expHead++;
            }
            if (owner == species && bodyName) {
                liveBody++;
            } else if (owner == species && headName) {
                liveHead++;
            }
            if (owner >= 0 && owner != species && owner != 0) {
                foreign++;
                if (foreignSample == "-") {
                    foreignSample = name;
                }
            }
        }

        // experiment filenames are the source art. species 1 and 2 need the live names filled in.
        if (species >= 1 && liveBody == 0 && expBody > 0) {
            return "audit=alias-missing expBody=" + expBody + " expHead=" + expHead
                + " foreign=" + foreign + " foreignSample=" + foreignSample;
        }
        if (species >= 1 && liveBody == 0 && expBody == 0 && liveHead == 0 && expHead == 0) {
            return "audit=no-body-or-head foreign=" + foreign + " foreignSample=" + foreignSample;
        }
        if (foreign > 0 && liveBody == 0) {
            return "audit=foreign-only foreign=" + foreign + " foreignSample=" + foreignSample
                + " expBody=" + expBody + " liveHead=" + liveHead;
        }
        return null;
    }

    internal static string RendererAudit(Body body, int replaced, int missed, string missedNames) {
        string head = "-";
        int charType = body != null ? body.charType : -1;
        if (body != null && body.limbs != null) {
            int headIdx = (int)Body.LimbNum.Head;
            if (headIdx >= 0 && headIdx < body.limbs.Length && body.limbs[headIdx] != null) {
                FacialExpression face = body.limbs[headIdx].GetComponent<FacialExpression>();
                if (face != null && face.defaultHead != null) {
                    head = face.defaultHead.name;
                }
            }
        }
        int headSpecies = SpeciesOfName(head);
        int clash = headSpecies >= 0 && charType >= 0 && charType <= 2 && headSpecies != charType ? 1 : 0;
        return "replaced=" + replaced
            + " missed=" + missed
            + " missedNames=" + (string.IsNullOrEmpty(missedNames) ? "-" : missedNames)
            + " headNow=" + head
            + " headSpecies=" + headSpecies
            + " headClash=" + clash
            + " char=" + charType;
    }

    static bool IsBodyName(string name) {
        if (SkinImporter.BodyNames.Contains(name)) {
            return true;
        }
        return name != null && (
            name.IndexOf("Crus", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Torso", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Thigh", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Arm", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Tail", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static bool IsHeadName(string name) {
        if (SkinImporter.HeadNames.Contains(name)) {
            return true;
        }
        if (string.IsNullOrEmpty(name)) {
            return false;
        }
        string lower = name.ToLowerInvariant();
        return lower.Contains("head") || lower.Contains("eye") || lower.Contains("mouth")
            || lower.Contains("nose") || lower.Contains("disfig");
    }

    static int SpeciesOfName(string name) {
        if (string.IsNullOrEmpty(name) || name == "-") {
            return -1;
        }
        if (SkinImporter.BodyNames.Contains(name) || SkinImporter.HeadNames.Contains(name)) {
            return 0;
        }
        string lower = name.ToLowerInvariant();
        if (lower.StartsWith("dune") || lower.Contains("dune")) {
            return 2;
        }
        if (lower.StartsWith("milk") || lower.Contains("milky")) {
            return 1;
        }
        if (lower.StartsWith("experiment")) {
            return 0;
        }
        return -1;
    }

    static string ShortHash(string hash) {
        if (string.IsNullOrEmpty(hash)) {
            return "-";
        }

        return hash.Length <= 8 ? hash : hash.Substring(0, 8);
    }

    static void CountParts(IReadOnlyDictionary<string, Sprite> sprites,
        out int body, out int head, out int other, out string sampleHead, out string sampleBody) {
        body = 0;
        head = 0;
        other = 0;
        sampleHead = "-";
        sampleBody = "-";
        if (sprites == null) {
            return;
        }

        foreach (KeyValuePair<string, Sprite> pair in sprites) {
            string name = pair.Key;
            if (SkinImporter.HeadNames.Contains(name)) {
                head++;
                if (sampleHead == "-") {
                    sampleHead = name;
                }
            } else if (SkinImporter.BodyNames.Contains(name)) {
                body++;
                if (sampleBody == "-") {
                    sampleBody = name;
                }
            } else {
                other++;
            }
        }
    }
}