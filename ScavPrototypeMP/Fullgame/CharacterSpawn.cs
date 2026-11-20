#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;
using UnityEngine.TextCore;
using UnityEngine.Timeline;

namespace Together;

// maps selectedCharacter / SyncBody.characterSelected onto Experiment/Milky/Dune prefabs.
public static class CharacterSpawn {
    static readonly string[] PrefabPaths = {
        "Special/Characters/Experiment",
        "Special/Characters/Milky",
        "Special/Characters/Dune",
    };

    static readonly GameObject[] PrefabCache = new GameObject[3];
    static TMP_FontAsset[] _noteFonts;

    public static int ClampChar(int chr) {
        if (chr < 0) {
            return 0;
        }
        if (chr > 2) {
            return 2;
        }
        return chr;
    }

    public static int ResolveCharForPlayer(ScavPlayer plr, SyncBody sb) {
        if (plr != null && sb != null) {
            int fromPlr = ClampChar(plr.selectedCharacter);
            int fromSb = ClampChar(sb.characterSelected.Value);
            if (fromPlr != fromSb) {
                // server Ready/save pick lives on plr first; clients must trust the sync var
                // (selectedCharacter can lag or get stomped when someone else joins)
                if (Net.IsClient && !Net.IsServer) {
                    plr.selectedCharacter = (byte)fromSb;
                    return fromSb;
                }
                return fromPlr;
            }
            return fromPlr;
        }
        if (plr != null) {
            return ClampChar(plr.selectedCharacter);
        }
        if (sb != null) {
            return ClampChar(sb.characterSelected.Value);
        }
        return ClampChar(PreRunScript.characterSelected);
    }

    public static GameObject InstantiateCharacter(int chr, string name) {
        chr = ClampChar(chr);
        GameObject prefab = GetOrCreatePrefab(chr);
        GameObject chara = Object.Instantiate(prefab);
        chara.name = "Character_" + name;
        chara.SetActive(true);
        return chara;
    }

    public static GameObject GetOrCreatePrefab(int chr) {
        chr = ClampChar(chr);
        if (PrefabCache[chr] != null) {
            // cache can get poisoned across species picks - refuse mismatched charType
            Body cachedBody = PrefabCache[chr].GetComponentInChildren<Body>(true);
            if (cachedBody != null && ClampChar(cachedBody.charType) == chr) {
                return PrefabCache[chr];
            }
            PrefabCache[chr] = null;
        }

        // the scene Experiment is already wearing the local slot. cloning it gives every
        // new experiment that skin, and the shared sprite names stop the strip from undoing it.
        GameObject live = Utils.Create(PrefabPaths[chr], Vector2.zero, 0f);
        if (live == null) {
            return null;
        }

        DefaultSpriteExport.CaptureClean(live);
        CaptureHingeState(live);
        GameObject cached = Object.Instantiate(live);
        cached.SetActive(false);
        MpBody.ResetBodyToStock(cached.GetComponentInChildren<Body>(true));
        PrefabCache[chr] = cached;
        if (chr == 0) {
            ServerMain.CHARACTER_PREFAB = cached;
        }

        // if we spawned a temp via Utils.Create and it isn't the local player body, destroy the live copy
        if (PlayerCamera.main == null || PlayerCamera.main.body == null ||
            (PlayerCamera.main.body.transform.parent != null
                ? PlayerCamera.main.body.transform.parent.gameObject
                : PlayerCamera.main.body.gameObject) != live) {
            Object.Destroy(live);
        }
        return PrefabCache[chr];
    }

    static void CaptureHingeState(GameObject root) {
        HingeJoint2D[] hinges = root.GetComponentsInChildren<HingeJoint2D>();
        for (int i = 0; i < hinges.Length; i++) {
            HingeJoint2D hinge = hinges[i];
            if (hinge.GetComponent<ClientMain.SavedHingeJointState>() != null) {
                continue;
            }
            ClientMain.SavedHingeJointState state = hinge.gameObject.AddComponent<ClientMain.SavedHingeJointState>();
            state.a = hinge.transform.localPosition;
            state.b = hinge.transform.localRotation;
            state.aa = hinge.connectedBody.transform.localPosition;
            state.bb = hinge.connectedBody.transform.localRotation;
            state.useLimits = hinge.useLimits;
            state.limitsmax = hinge.limits.max;
            state.limitsmin = hinge.limits.min;
            state.referenceAngle = hinge.referenceAngle;
            state.hinge = hinge;
            state.connectedBody = hinge.connectedBody;
        }
    }

    public static void ApplySelectedToSyncBody(SyncBody sb, ScavPlayer plr) {
        if (sb == null) {
            return;
        }
        byte chr = (byte)ResolveCharForPlayer(plr, sb);
        if (plr != null) {
            plr.selectedCharacter = chr;
        }
        sb.characterSelected.Value = chr;
    }


    public static string SaveSpeciesName(int chr) {
        return ClampChar(chr) switch {
            1 => "Milky",
            2 => "Dune",
            _ => "Experiment",
        };
    }

    // host console: force species for a living player, optionally wipe XP to that species' base skills,
    // sync to their client via characterSelected, then checkpoint-save their player file.
    public static void ServerForceSwapSpecies(ScavPlayer plr, int chr, bool wipeXp, bool dropInventory = true) {
        if (!Net.IsServer || plr == null) {
            return;
        }
        chr = ClampChar(chr);
        int was = plr.body != null ? ClampChar(plr.body.charType) : ClampChar(plr.selectedCharacter);

        int keepSTR = 0, keepRES = 0, keepINT = 0;
        float keepExpSTR = 0f, keepExpRES = 0f, keepExpINT = 0f;
        bool keepSkills = !wipeXp && plr.body != null && plr.body.skills != null;
        if (keepSkills) {
            Skills sk = plr.body.skills;
            keepSTR = sk.STR;
            keepRES = sk.RES;
            keepINT = sk.INT;
            keepExpSTR = sk.expSTR;
            keepExpRES = sk.expRES;
            keepExpINT = sk.expINT;
        }

        plr.selectedCharacter = (byte)chr;
        if (plr.IsLocal) {
            PreRunScript.characterSelected = chr;
        }

        SyncBody sb = plr.netBody != null ? plr.netBody.syncBody : null;
        if (sb != null) {
            sb.characterSelected.Value = (byte)chr;
        }

        if (plr.body != null && plr.body.charType != chr) {
            RebuildBodyMesh(plr, chr, dropInventory);
        } else if (plr.body != null) {
            plr.body.charType = chr;
            ItemSync.RefreshWearableSpeciesSprites(plr.body);
            if (plr.body.talker != null) {
                TalkerSpeciesFont.Apply(plr.body.talker);
            }
        } else {
            NetBody.CreateNewPlayerCharacter(plr, sb);
            if (plr.netBody != null) {
                ApplySelectedToSyncBody(plr.netBody.syncBody ?? sb, plr);
            }
        }

        if (plr.body != null && plr.body.skills != null) {
            if (wipeXp) {
                plr.body.skills.Setup(chr);
            } else if (keepSkills) {
                Skills sk = plr.body.skills;
                sk.STR = keepSTR;
                sk.RES = keepRES;
                sk.INT = keepINT;
                sk.expSTR = keepExpSTR;
                sk.expRES = keepExpRES;
                sk.expINT = keepExpINT;
                sk.UpdateExpBoundaries();
                float[] mult = Skills.BaseMultipliers(chr);
                sk.STRGainMult = mult[0];
                sk.RESGainMult = mult[1];
                sk.INTGainMult = mult[2];
            } else {
                plr.body.skills.Setup(chr);
            }
        }

        RebindLocalCamera(plr);

        if (plr.netBody != null) {
            MedicalSync.ForceHealth(plr.netBody);
        }

        PlayerCheckpoint.RememberSpecies(plr.GetPersistentId(), SaveSpeciesName(chr), 0);
        PlayerCheckpoint.WriteOne(plr, false);

        Together.log.warn($"[Species] force-swap \"{plr.playerName}\" {was}->{chr} wipeXp={wipeXp}");
        plr.Server_DoAlertSingle($"<key:species_forced> {SaveSpeciesName(chr)}");
    }

    public static void ClientRebuildLocalBodyIfNeeded(ScavPlayer plr, SyncBody sb, int want) {
        if (Net.IsServer || plr == null || !plr.IsLocal) {
            return;
        }
        want = ClampChar(want);
        plr.selectedCharacter = (byte)want;
        PreRunScript.characterSelected = want;
        if (plr.body != null && plr.body.charType == want) {
            ItemSync.RefreshWearableSpeciesSprites(plr.body);
            if (plr.body.talker != null) {
                TalkerSpeciesFont.Apply(plr.body.talker);
            }
            return;
        }
        RebuildBodyMesh(plr, want, dropInventory: true);
        RebindLocalCamera(plr);
    }

    static void RebuildBodyMesh(ScavPlayer plr, int want, bool dropInventory) {
        if (plr == null || plr.body == null) {
            return;
        }
        want = ClampChar(want);
        SyncBody sb = plr.netBody != null ? plr.netBody.syncBody : null;
        Vector2 pos = plr.body.transform.position;
        NetBody old = plr.netBody;
        Together.log.warn($"[Species] rebuild \"{plr.playerName}\" {plr.body.charType}->{want}");
        if (dropInventory) {
            try {
                plr.body.Body_DropAllItems();
            } catch (System.Exception e) {
                Together.log.error(e);
            }
        }
        plr.body = null;
        if (old != null) {
            if (sb != null && ReferenceEquals(sb.nb, old)) {
                sb.nb = null;
            }
            ForceDestroyNetBodyMesh(old);
        }
        NetBody.CreateNewPlayerCharacter(plr, sb);
        if (plr.netBody != null) {
            plr.netBody.SetBodyPosition(pos);
            ApplySelectedToSyncBody(plr.netBody.syncBody ?? sb, plr);
            if (plr.body != null) {
                plr.body.charType = want;
                ItemSync.RefreshWearableSpeciesSprites(plr.body);
                if (plr.body.talker != null) {
                    TalkerSpeciesFont.Apply(plr.body.talker);
                }
            }
        }
    }

    static void RebindLocalCamera(ScavPlayer plr) {
        if (plr == null || !plr.IsLocal || plr.body == null || PlayerCamera.main == null) {
            return;
        }
        PlayerCamera.main.body = plr.body;
        if (MoodleManager.main != null) {
            MoodleManager.main.body = plr.body;
        }
    }

    // host BindOnChange often does not rebuild meshes when characterSelected is set locally.
    // force destroy+respawn so host/peers see Dune/Milky instead of a stuck Experiment prefab.
    public static void ServerRebuildBodyIfSpeciesMismatch(ScavPlayer plr) {
        if (!Net.IsServer || plr == null || plr.body == null) {
            return;
        }
        int want = ClampChar(plr.selectedCharacter);
        if (plr.body.charType == want) {
            return;
        }
        SyncBody sb = plr.netBody != null ? plr.netBody.syncBody : null;
        Vector2 pos = plr.body.transform.position;
        NetBody old = plr.netBody;
        Together.log.warn($"[Species] rebuild \"{plr.playerName}\" {plr.body.charType}->{want}");
        plr.body = null;
        if (old != null) {
            if (sb != null && ReferenceEquals(sb.nb, old)) {
                sb.nb = null;
            }
            // destroyNPC skips local bodies - species swap must kill the mesh anyway.
            ForceDestroyNetBodyMesh(old);
        }
        NetBody.CreateNewPlayerCharacter(plr, sb);
        if (plr.netBody != null) {
            plr.netBody.SetBodyPosition(pos);
            ApplySelectedToSyncBody(plr.netBody.syncBody ?? sb, plr);
            EnsureBodySpecies(plr.body);
        }
    }

    static void ForceDestroyNetBodyMesh(NetBody npc) {
        if (npc == null) {
            return;
        }
        if (npc.IsPlayer && npc.plr != null) {
            ScavPlayer.AllLivingPlayers.Remove(npc.plr);
            ScavPlayer.AllDeadPlayers.Remove(npc.plr);
        }
        GameObject root = npc.transform.parent != null
            ? npc.transform.parent.gameObject
            : npc.gameObject;
        UnityEngine.Object.Destroy(root);
    }

    // any stock quirk that reads body.charType (diet, WakeUp/milky sleep, dune sickness clear,
    // insulation, …) breaks if the host copy is still Experiment after Ready. Pin it.
    public static void EnsureBodySpecies(Body body) {
        if (body == null || !Net.IsRunning) {
            return;
        }
        if (!body.TryGetNetBody(out NetBody nb) || nb == null) {
            return;
        }
        if (nb.plr == null && nb.syncBody == null) {
            return;
        }
        int want = ResolveCharForPlayer(nb.plr, nb.syncBody);
        if (body.charType == want) {
            return;
        }
        int was = body.charType;
        Together.log.warn($"[Species] body.charType {was}->{want} on \"{body.name}\"");
        // server rebuilds the mesh. setting charType alone left Milky's model speaking Dune.
        if (Net.IsServer && nb.plr != null && nb.plr.body == body) {
            ServerRebuildBodyIfSpeciesMismatch(nb.plr);
            return;
        }
        body.charType = want;
        ItemSync.RefreshWearableSpeciesSprites(body);
        // setup() resets STR/RES/INT to species base - only safe if still at the old species' stock stats
        if (body.skills != null) {
            int[] oldBase = Skills.BaseSkills(ClampChar(was));
            Skills sk = body.skills;
            if (sk.STR == oldBase[0] && sk.RES == oldBase[1] && sk.INT == oldBase[2]) {
                sk.Setup(want);
            }
        }
        // charType drives Talker note font - re-pin when species is corrected mid-session
        if (body.talker != null) {
            TalkerSpeciesFont.Apply(body.talker);
        }
    }

    public static TMP_FontAsset GetNoteFont(int chr) {
        EnsureNoteFonts();
        chr = ClampChar(chr);
        if (_noteFonts != null && chr < _noteFonts.Length) {
            return _noteFonts[chr];
        }
        return null;
    }

    public static TMP_FontAsset GetLocalNoteFont() {
        int chr = PreRunScript.characterSelected;
        if (Util.TryGetLocalBody(out Body body)) {
            chr = body.charType;
        } else if (ScavPlayer.LocalPlayer != null) {
            chr = ScavPlayer.LocalPlayer.selectedCharacter;
        }
        return GetNoteFont(chr);
    }

    public static void CacheNoteFonts(TMP_FontAsset[] fonts) {
        if (fonts == null || fonts.Length == 0 || _noteFonts != null) {
            return;
        }
        _noteFonts = fonts;
        string[] names = new string[fonts.Length];
        for (int i = 0; i < fonts.Length; i++) {
            names[i] = fonts[i] != null ? fonts[i].name : "null";
        }
        Together.log.warn($"[TalkerFont] cached note fonts [{string.Join(", ", names)}]");
    }

    static void EnsureNoteFonts() {
        if (_noteFonts != null) {
            return;
        }
        SurvivorNote note = Object.FindObjectOfType<SurvivorNote>(true);
        if (note == null || note.fonts == null || note.fonts.Length == 0) {
            SurvivorNote[] loaded = Resources.FindObjectsOfTypeAll<SurvivorNote>();
            for (int i = 0; i < loaded.Length; i++) {
                if (loaded[i] != null && loaded[i].fonts != null && loaded[i].fonts.Length > 0) {
                    note = loaded[i];
                    break;
                }
            }
        }
        if (note == null) {
            // resources name is LoreNote. tutorial often never spawns one, and Talker.Start runs first.
            string[] paths = { "LoreNote", "Special/LoreNote", "Special/survivornote", "survivornote" };
            for (int i = 0; i < paths.Length; i++) {
                GameObject probe = Resources.Load<GameObject>(paths[i]);
                if (probe == null) {
                    continue;
                }
                note = probe.GetComponent<SurvivorNote>();
                if (note == null) {
                    note = probe.GetComponentInChildren<SurvivorNote>(true);
                }
                if (note != null && note.fonts != null && note.fonts.Length > 0) {
                    break;
                }
                note = null;
            }
        }
        if (note != null && note.fonts != null && note.fonts.Length > 0) {
            CacheNoteFonts(note.fonts);
        }
    }
}

[HarmonyPatch(typeof(SurvivorNote), "Start")]
static class SurvivorNote_Start_CacheFonts_MultiplayerPatch {
    static void Postfix(SurvivorNote __instance) {
        if (!Net.IsRunning || __instance == null) {
            return;
        }
        // client world-clear destroys the generated notes. the font assets have to be kept first.
        CharacterSpawn.CacheNoteFonts(__instance.fonts);
    }
}

[HarmonyPatch(typeof(PlayerCamera), "Awake")]
static class PlayerCamera_Awake_CharacterSelect_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return;
        }
        int chr = PreRunScript.characterSelected;
        if (ScavPlayer.LocalPlayer != null) {
            chr = CharacterSpawn.ClampChar(ScavPlayer.LocalPlayer.selectedCharacter);
        }
        PreRunScript.characterSelected = chr;
        PlayerPrefs.SetInt("lastchar", chr);
    }
}

// when MP code (or stock with null font) opens scrollable text, prefer the local character's note font
[HarmonyPatch(typeof(ScrollableText), nameof(ScrollableText.CreateText))]
static class ScrollableText_CreateText_CharFont_MultiplayerPatch {
    static void Prefix(bool cover, ref TMP_FontAsset font) {
        if (!Net.IsRunning || font != null || cover || SharedMain.IsInCutscene) {
            return;
        }
        TMP_FontAsset localFont = CharacterSpawn.GetLocalNoteFont();
        if (localFont != null) {
            font = localFont;
        }
    }
}

// overhead Talker: species note fonts. Missing glyphs (e.g. Dune !) use TMP per-char
// fallback: Experiment note -> Milky note -> ExpTalkText default. Never TryAddCharacters.
public static class TalkerSpeciesFont {
    static float _experimentTalkSize = -1f;
    static TMP_FontAsset _expTalkFont;
    static readonly HashSet<int> _fontsLogged = new HashSet<int>();
    static readonly HashSet<int> _fontsFallbackReady = new HashSet<int>();
    static bool _missingFontLogged;

    const string ProbePunctuation = ".,!?;:'\"-";


    public static bool IsPlayerTalker(Talker talker) {
        if (talker == null) {
            return false;
        }
        Body body = talker.body != null ? talker.body : talker.GetComponentInParent<Body>();
        if (body == null) {
            return false;
        }
        if (body.TryGetNetBody(out NetBody nb)) {
            return nb.IsPlayer;
        }
        return ScavPlayer.BodyToPlayerDict.ContainsKey(body);
    }

    public static bool UseDefaultTalkerFonts() {
        return UIBullshit.USE_DEFAULT_TALKER_FONT;
    }

    public static bool CustomFontsOn() {
        return UIBullshit.USE_TALKER_SPECIES_FONT && !UseDefaultTalkerFonts();
    }

    public static void RefreshAll() {
        if (!CustomFontsOn()) {
            TalkerPixelExempt.Teardown();
            return;
        }
        if (!Net.IsRunning) {
            return;
        }
        Talker[] talkers = Object.FindObjectsOfType<Talker>();
        for (int i = 0; i < talkers.Length; i++) {
            Apply(talkers[i]);
        }
    }

    public static void Apply(Talker talker) {
        if (!Net.IsRunning || talker == null || talker.text == null) {
            return;
        }
        Body body = talker.body != null ? talker.body : talker.GetComponent<Body>();
        if (body == null) {
            return;
        }
        
        if (!CustomFontsOn()) {
            TalkerPixelExempt.Teardown();
            return;
        }

        // keep charType honest before picking a font (join can leave plr/sync disagreeing)
        CharacterSpawn.EnsureBodySpecies(body);

        EnsureExperimentTalkSize();

        int chr = CharacterSpawn.ClampChar(body.charType);
        TMP_FontAsset font = CharacterSpawn.GetNoteFont(chr);
        if (font == null) {
            if (!_missingFontLogged) {
                _missingFontLogged = true;
                Together.log.warn("[TalkerFont] note fonts missing on this peer; talker is on the default font");
            }
        } else {
            WireNoteFontFallbacks(font, chr);
            // stash prefab font and size before the swap. teardown puts them back.
            AnyDataContainerComponent data = talker.GetOrAddComponent<AnyDataContainerComponent>();
            if (talker.text.font != null && talker.text.font != font) {
                data.GetOrAdd("DefaultTalkerFont", talker.text.font);
                data.GetOrAdd("DefaultTalkerFontSize", talker.text.fontSize);
            }
            if (talker.text.font != font) {
                talker.text.font = font;
            }
        }

        talker.text.richText = true;
        talker.text.enableAutoSizing = false;
        talker.text.extraPadding = true;

        TalkerPixelExempt.Exempt(talker);
    }

    static void EnsureExperimentTalkSize() {
        if (_experimentTalkSize > 0f && _expTalkFont != null) {
            return;
        }

        GameObject prefab = Resources.Load<GameObject>("Special/ExpTalkText");
        if (prefab == null) {
            prefab = Resources.Load<GameObject>("ExpTalkText");
        }
        if (prefab != null) {
            TextMeshPro tmp = prefab.GetComponent<TextMeshPro>();
            if (tmp == null) {
                tmp = prefab.GetComponentInChildren<TextMeshPro>(true);
            }
            if (tmp != null) {
                if (_expTalkFont == null) {
                    _expTalkFont = tmp.font;
                }
                if (_experimentTalkSize <= 0f && tmp.fontSize > 0f) {
                    _experimentTalkSize = tmp.fontSize;
                }
            }
        }

        if (_experimentTalkSize <= 0f) {
            _experimentTalkSize = 3f;
        }
    }

    // TMP only consults fallbacks for chars missing from the primary atlas - so Experiment/Milky
    // punctuation is used only when the active note font lacks that exact character.
    static void WireNoteFontFallbacks(TMP_FontAsset noteFont, int charType) {
        if (noteFont == null) {
            return;
        }
        int id = noteFont.GetInstanceID();

        if (!_fontsLogged.Contains(id)) {
            _fontsLogged.Add(id);
            List<string> missing = new List<string>();
            List<string> present = new List<string>();
            for (int i = 0; i < ProbePunctuation.Length; i++) {
                char c = ProbePunctuation[i];
                string label = c == '"' ? "quot" : c.ToString();
                // native atlas only - don't searchFallbacks / tryAdd (those lied and synthesized)
                if (noteFont.HasCharacter(c, false, false)) {
                    present.Add(label);
                } else {
                    missing.Add(label);
                }
            }
            Together.log.warn($"[TalkerFont] charType={charType} font=\"{noteFont.name}\" native=[{string.Join(" ", present)}] missing=[{string.Join(" ", missing)}]");
        }

        if (_fontsFallbackReady.Contains(id)) {
            return;
        }
        _fontsFallbackReady.Add(id);

        // keep the font's authored fallbacks (those hold the actual letterforms).
        // only append other note fonts and ExpTalk for glyphs this font still lacks.
        List<TMP_FontAsset> chain = new List<TMP_FontAsset>();
        if (noteFont.fallbackFontAssetTable != null) {
            for (int i = 0; i < noteFont.fallbackFontAssetTable.Count; i++) {
                TMP_FontAsset existing = noteFont.fallbackFontAssetTable[i];
                if (existing != null && existing != noteFont && !chain.Contains(existing)) {
                    chain.Add(existing);
                }
            }
        }
        TMP_FontAsset expNote = CharacterSpawn.GetNoteFont(0);
        TMP_FontAsset milkyNote = CharacterSpawn.GetNoteFont(1);
        if (expNote != null && expNote != noteFont && !chain.Contains(expNote)) {
            chain.Add(expNote);
        }
        if (milkyNote != null && milkyNote != noteFont && !chain.Contains(milkyNote)) {
            chain.Add(milkyNote);
        }
        if (_expTalkFont != null && _expTalkFont != noteFont && !chain.Contains(_expTalkFont)) {
            chain.Add(_expTalkFont);
        }
        noteFont.fallbackFontAssetTable = chain;
    }

    public static float ExperimentTalkSize => _experimentTalkSize > 0f ? _experimentTalkSize : 3f;
    public static TMP_FontAsset ExperimentTalkFont => _expTalkFont;

    // letters only; spaces/symbols ok. strips rich-text tags.
    public static bool IsYelling(string msg) {
        if (string.IsNullOrEmpty(msg)) {
            return false;
        }
        bool anyLetter = false;
        bool inTag = false;
        for (int i = 0; i < msg.Length; i++) {
            char c = msg[i];
            if (c == '<') {
                inTag = true;
                continue;
            }
            if (inTag) {
                if (c == '>') {
                    inTag = false;
                }
                continue;
            }
            if (!char.IsLetter(c)) {
                continue;
            }
            anyLetter = true;
            if (!char.IsUpper(c)) {
                return false;
            }
        }
        return anyLetter;
    }
}

// screen-space mirror - no second Camera (that blacks out with PixelPerfect).
public static class TalkerPixelExempt {
    class Mirror {
        public Talker talker;
        public RectTransform root;
        public TextMeshProUGUI ui;
        public TextMeshProUGUI[] outline;
        public bool yelling;
    }

    // black copies behind the face. note fonts ignore tmp outline.
    static readonly Vector2[] OutlineOffsets = {
        new Vector2(-1f, 0f), new Vector2(1f, 0f), new Vector2(0f, -1f), new Vector2(0f, 1f),
        new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f),
    };

    static RectTransform _canvas;
    static readonly List<Mirror> _mirrors = new List<Mirror>();
    static bool _sceneHooked;

    public static void Exempt(Talker talker) {
        if (talker == null || talker.text == null) {
            return;
        }
        if (!TalkerSpeciesFont.CustomFontsOn()) {
            Teardown();
            return;
        }
        EnsureCanvas();
        Mirror existing = _mirrors.FirstOrDefault(x => x.talker == talker);
        if (existing != null) {
            return;
        }

        GameObject rootGo = new GameObject("MP_TalkMirror", typeof(RectTransform));
        rootGo.transform.SetParent(_canvas.transform, false);
        RectTransform root = rootGo.GetComponent<RectTransform>();

        // earlier siblings draw under the face
        TextMeshProUGUI[] outline = new TextMeshProUGUI[OutlineOffsets.Length];
        for (int i = 0; i < OutlineOffsets.Length; i++) {
            outline[i] = CreateMirrorText(root, "outline", Color.black);
        }

        TextMeshProUGUI ui = CreateMirrorText(root, "face", Color.white);
        rootGo.SetActive(false);

        existing = new Mirror { talker = talker, root = root, ui = ui, outline = outline, yelling = false };
        _mirrors.Add(existing);
    }

    static TextMeshProUGUI CreateMirrorText(RectTransform parent, string name, Color color) {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        TextMeshProUGUI ui = go.AddComponent<TextMeshProUGUI>();
        ui.raycastTarget = false;
        ui.enableWordWrapping = true;
        ui.overflowMode = TextOverflowModes.Overflow;
        ui.alignment = TextAlignmentOptions.Center;
        ui.color = color;
        ui.extraPadding = true;
        return ui;
    }

    // call when a new line starts (chat / Talk) so caps is known before progressive reveal
    public static void SetYelling(Talker talker, string fullLine) {
        if (talker == null) {
            return;
        }
        Exempt(talker);
        Mirror m = Find(talker);
        if (m != null) {
            m.yelling = TalkerSpeciesFont.IsYelling(fullLine);
        }
    }

    public static void Remove(Talker talker) {
        for (int i = _mirrors.Count - 1; i >= 0; i--) {
            Mirror m = _mirrors[i];
            if (m.talker != null && m.talker != talker) {
                continue;
            }
            DestroyMirror(m);
            _mirrors.RemoveAt(i);
        }
        if (_mirrors.Count == 0 && _canvas != null) {
            Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }
    }

    public static void SyncOne(Talker talker) {
        if (talker == null || talker.text == null) {
            return;
        }
        // menu / leave-run: Talker LateUpdate dies but DDOL UI would stick - nuke it
        if (!Net.IsRunning || Util.IsInMainMenu() || PlayerCamera.main == null || !TalkerSpeciesFont.CustomFontsOn()) {
            Teardown();
            return;
        }

        Mirror mirror = Find(talker);
        if (mirror == null || mirror.ui == null) {
            return;
        }

        Camera cam = ResolveMainCamera();
        if (cam == null) {
            HideMirror(mirror);
            return;
        }

        // always hide world mesh while mirrored - world note fonts project huge on screen
        Renderer worldRend = talker.text.renderer;
        if (worldRend != null) {
            worldRend.enabled = false;
        }

        string msg = talker.text.text;
        if (string.IsNullOrEmpty(msg)) {
            HideMirror(mirror);
            return;
        }

        Vector3 screen = cam.WorldToScreenPoint(talker.text.rectTransform.position);
        if (screen.z < 0f) {
            HideMirror(mirror);
            return;
        }

        AnyDataContainerComponent data = talker.GetOrAddComponent<AnyDataContainerComponent>();

        if (mirror.root != null) {
            mirror.root.gameObject.SetActive(true);
        } else {
            mirror.ui.gameObject.SetActive(true);
        }

        // re-pin species note font every frame - chat path skips Talk(), and a late join
        // can leave remotes on Experiment until the next Talk Apply
        Body bodyForFont = talker.body != null ? talker.body : talker.GetComponent<Body>();
        TMP_FontAsset wantFont = null;
        if (bodyForFont != null) {
            wantFont = CharacterSpawn.GetNoteFont(bodyForFont.charType);
        }
        if (wantFont == null) {
            wantFont = talker.text.font;
        }

        if (wantFont == null || talker.text.font != wantFont) {
            data.GetOrAdd("DefaultTalkerFont", talker.text.font);
            data.GetOrAdd("DefaultTalkerFontSize", talker.text.fontSize);
        }

        if (wantFont != null) {
            if (talker.text.font != wantFont) {
                talker.text.font = wantFont;
            }
            if (mirror.ui.font != wantFont) {
                mirror.ui.font = wantFont;
            }
            // duneMode randomly stomps TextMeshProUGUI.material - restore font atlas material
            if (wantFont.material != null && mirror.ui.fontSharedMaterial != wantFont.material) {
                mirror.ui.fontSharedMaterial = wantFont.material;
            }
        }

        mirror.ui.text = msg;
        mirror.ui.color = talker.text.color;
        mirror.ui.alignment = talker.text.alignment;
        mirror.ui.enableAutoSizing = false;
        mirror.ui.enableWordWrapping = true;
        mirror.ui.overflowMode = TextOverflowModes.Overflow;
        if (mirror.root != null) {
            mirror.root.position = screen;
        } else {
            mirror.ui.rectTransform.position = screen;
        }

        // match the stock talker's on-screen cap. yell scales after.
        float stockSize = data.GetOrDefault("DefaultTalkerFontSize", talker.text.fontSize);
        if (stockSize <= 0f) {
            stockSize = talker.text.fontSize;
        }
        TMP_FontAsset stockFont = data.GetOrDefault<TMP_FontAsset>("DefaultTalkerFont", null);
        float stockCap = CapFactor(stockFont);
        if (stockCap < 0.01f) {
            stockCap = CapFactor(TalkerSpeciesFont.ExperimentTalkFont);
        }
        if (stockCap < 0.01f) {
            stockCap = 1f;
        }
        float worldCap = stockSize * Mathf.Abs(talker.text.transform.lossyScale.y) * stockCap;
        // non-orthographic tmp draws at 0.1x. that is the mesh the setting-on path shows.
        if (!talker.text.isOrthographic) {
            worldCap *= 0.1f;
        }
        float screenCap;
        if (cam.orthographic) {
            screenCap = worldCap * cam.pixelHeight / (2f * Mathf.Max(0.01f, cam.orthographicSize));
        } else {
            Vector3 origin = talker.text.transform.position;
            Vector3 above = origin + Vector3.up * worldCap;
            screenCap = Mathf.Abs(cam.WorldToScreenPoint(above).y - cam.WorldToScreenPoint(origin).y);
        }
        float canvasScale = 1f;
        if (mirror.root != null) {
            Canvas canvas = mirror.root.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.scaleFactor > 0.01f) {
                canvasScale = canvas.scaleFactor;
            }
        }
        float noteCap = CapFactor(wantFont);
        if (noteCap < 0.01f) {
            noteCap = 1f;
        }
        // normal special text is the old non-dune yell size. yell still multiplies from there.
        const float nonDuneYell = 1.64f;
        const float duneYell = 2.5f;
        float basePx = screenCap / canvasScale / noteCap * nonDuneYell;

        // refresh yell from full line each frame (covers Talk + chat)
        string yellSrc = talker.currentString;
        if (string.IsNullOrEmpty(yellSrc)) {
            yellSrc = msg;
        }
        mirror.yelling = TalkerSpeciesFont.IsYelling(yellSrc);
        float yellMult = 1f;
        if (mirror.yelling) {
            Body body = talker.body != null ? talker.body : talker.GetComponent<Body>();
            int chr = body != null ? CharacterSpawn.ClampChar(body.charType) : 0;
            yellMult = chr == 2 ? duneYell : nonDuneYell;
        }
        float fontPx = basePx * yellMult;
        fontPx = Mathf.Clamp(fontPx, 1f, 512f);

        float refW = DesignResolutionWidth();
        float wrapW = Mathf.Clamp(refW * 0.28f, 120f, 420f) * nonDuneYell * 2f;
        if (mirror.yelling) {
            wrapW *= yellMult;
        }
        RectTransform sized = mirror.root != null ? mirror.root : mirror.ui.rectTransform;
        sized.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, wrapW);
        mirror.ui.fontSize = fontPx;
        mirror.ui.ForceMeshUpdate(true);
        float prefH = Mathf.Max(fontPx, mirror.ui.preferredHeight);
        sized.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, prefH + 6f);
        CopyOutline(mirror, fontPx);
    }

    static float CapFactor(TMP_FontAsset font) {
        if (font == null) {
            return 0f;
        }
        FaceInfo face = font.faceInfo;
        float point = face.pointSize;
        if (point < 1f) {
            return 0f;
        }
        float cap = face.capLine - face.baseline;
        if (cap < 1f) {
            cap = face.ascentLine - face.baseline;
        }
        if (cap < 1f) {
            return 0f;
        }
        float scale = face.scale <= 0f ? 1f : face.scale;
        return cap / point * scale;
    }

    static void CopyOutline(Mirror mirror, float fontPx) {
        if (mirror.outline == null || mirror.root == null) {
            return;
        }
        float px = 2f;
        Canvas canvas = mirror.root.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.scaleFactor > 0.01f) {
            px = 2f / canvas.scaleFactor;
        }
        for (int i = 0; i < mirror.outline.Length; i++) {
            TextMeshProUGUI o = mirror.outline[i];
            if (o == null) {
                continue;
            }
            Vector2 off = OutlineOffsets[i] * px;
            o.rectTransform.offsetMin = off;
            o.rectTransform.offsetMax = off;
            if (o.font != mirror.ui.font) {
                o.font = mirror.ui.font;
            }
            if (mirror.ui.fontSharedMaterial != null && o.fontSharedMaterial != mirror.ui.fontSharedMaterial) {
                o.fontSharedMaterial = mirror.ui.fontSharedMaterial;
            }
            if (o.fontSize != fontPx) {
                o.fontSize = fontPx;
            }
            if (o.text != mirror.ui.text) {
                o.text = mirror.ui.text;
            }
            o.alignment = mirror.ui.alignment;
            o.color = Color.black;
        }
    }

    static float DesignResolutionHeight() {
        if (PlayerCamera.main != null) {
            PixelPerfectCamera ppc = PlayerCamera.main.GetComponent<PixelPerfectCamera>();
            if (ppc != null && ppc.refResolutionY > 0) {
                return ppc.refResolutionY;
            }
        }
        return 1080f;
    }

    static float DesignResolutionWidth() {
        if (PlayerCamera.main != null) {
            PixelPerfectCamera ppc = PlayerCamera.main.GetComponent<PixelPerfectCamera>();
            if (ppc != null && ppc.refResolutionX > 0) {
                return ppc.refResolutionX;
            }
        }
        return 1920f;
    }

    static void HideMirror(Mirror mirror) {
        if (mirror == null || mirror.ui == null) {
            return;
        }
        mirror.ui.text = "";
        if (mirror.root != null) {
            mirror.root.gameObject.SetActive(false);
        } else {
            mirror.ui.gameObject.SetActive(false);
        }
    }

    static Mirror Find(Talker talker) {
        SweepDead();
        return _mirrors.FirstOrDefault(x => x.talker == talker);
    }

    static void SweepDead() {
        for (int i = _mirrors.Count - 1; i >= 0; i--) {
            Mirror m = _mirrors[i];
            if (m.talker != null && m.ui != null) {
                continue;
            }
            DestroyMirror(m);
            _mirrors.RemoveAt(i);
        }
    }

    static void DestroyMirror(Mirror m) {
        if (m == null) {
            return;
        }
        if (m.ui != null) {
            m.ui.text = "";
        }
        GameObject go = m.root != null ? m.root.gameObject : (m.ui != null ? m.ui.gameObject : null);
        if (go != null) {
            Object.Destroy(go);
        }
    }

    static void EnsureCanvas() {
        HookScene();
        if (_canvas != null || PlayerCamera.main == null || PlayerCamera.main.mainCanvas == null) {
            return;
        }
        GameObject go = new GameObject("MP_TalkOverlayCanvas", typeof(RectTransform));
        // NOT DontDestroyOnLoad - leaving to menu must kill stuck speech
        _canvas = go.GetComponent<RectTransform>();
        _canvas.SetStretch();
        go.AddComponent<CanvasGroup>().blocksRaycasts = false;
        go.transform.SetParent(PlayerCamera.main.mainCanvas);
        go.transform.SetAsFirstSibling();
    }

    static void HookScene() {
        if (_sceneHooked) {
            return;
        }
        _sceneHooked = true;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, __) => Teardown();
    }

    static Camera ResolveMainCamera() {
        if (PlayerCamera.main != null) {
            Camera c = PlayerCamera.main.GetComponent<Camera>();
            if (c != null) {
                return c;
            }
        }
        return Camera.main;
    }

    public static void Teardown() {
        for (int i = 0; i < _mirrors.Count; i++) {
            Mirror m = _mirrors[i];

            Talker talker = m.talker;
            if (talker != null && talker.text != null) {
                AnyDataContainerComponent data = talker.GetOrAddComponent<AnyDataContainerComponent>();
                talker.text.font = data.GetOrDefault("DefaultTalkerFont", talker.text.font);
                float savedSize = data.GetOrDefault("DefaultTalkerFontSize", 0f);
                if (savedSize > 0f) {
                    talker.text.fontSize = savedSize;
                }

                if (talker.text.renderer != null) {
                    talker.text.renderer.enabled = true;
                }
            }

            DestroyMirror(m);
        }
        _mirrors.Clear();
        if (_canvas != null) {
            Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }
    }
}

[HarmonyPatch(typeof(Talker), "Start")]
static class Talker_Start_SpeciesFont_MultiplayerPatch {
    static void Postfix(Talker __instance) {
        TalkerSpeciesFont.Apply(__instance);
    }
}

[HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) })]
static class Talker_TalkList_SpeciesFont_MultiplayerPatch {
    static void Prefix(Talker __instance) {
        TalkerSpeciesFont.Apply(__instance);
    }
}

[HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new[] { typeof(string), typeof(Limb), typeof(bool), typeof(bool) })]
static class Talker_TalkString_SpeciesFont_MultiplayerPatch {
    static void Prefix(Talker __instance) {
        TalkerSpeciesFont.Apply(__instance);
    }
}

[HarmonyPatch(typeof(Talker), "LateUpdate")]
static class Talker_LateUpdate_PixelExempt_MultiplayerPatch {
    static void Postfix(Talker __instance) {
        TalkerPixelExempt.SyncOne(__instance);
    }
}

[HarmonyPatch(typeof(Talker), "OnDestroy")]
static class Talker_OnDestroy_PixelExempt_MultiplayerPatch {
    static void Prefix(Talker __instance) {
        TalkerPixelExempt.Remove(__instance);
    }
}
#endif