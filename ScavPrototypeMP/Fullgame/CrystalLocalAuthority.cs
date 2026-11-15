#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

// CS-style local crystal authority for Together:
// host stamps a one-shot props bag; clients apply via SetUpEffects Prefix and mark the GO local.
// custom Structures markers are detected by type name so we coexist without a hard CS dependency.
// legacy CrystalAuthority fill/specials remain for unmarked shells and mutations until fully retired.

public class TogetherCrystalLocalMarker : MonoBehaviour { }

public class TogetherCrystalProps : MonoBehaviour {
    public string EffectsCsv;
    public byte[] EffectIds;
    public float[] EffectForceMults;
    public string ColorR;
    public string ColorG;
    public string ColorB;
    public Color32 SpriteColor = new Color32(255, 255, 255, 255);
    public Color32 LightColor = new Color32(255, 255, 255, 255);
    public float Intensity = 1f;
    public float Volume;
    public float Falloff;
    public float HeaterTemp;
    public float HeaterLerp;
    public float CrystalSize = 1f;
    public float OuterRadius;
    public byte Flags;
    public bool HasAuthoredColor;
    public bool Initialized;
}

public static class CrystalLocalAuthority {
    public const byte FlagFlip = 2;
    public const byte FlagHasHeater = 4;
    public const byte FlagVolumeLight = 8;
    public const byte FlagSpecial = 16;

    const float MatchRadiusSq = 1f;
    const string CsLocalMarkerName = "MoreStructuresLocalObjectMarker";
    const string CsPropsMarkerName = "CrystalPropertiesMarker";

    static readonly Dictionary<knetid, byte[]> _bagBySync = new();
    static readonly HashSet<knetid> _bagApplied = new();
    static readonly Dictionary<knetid, float> _nextBagRequestAt = new();
    static FieldInfo _crystalEffectsField;

    public static void ClearLayerCaches() {
        _bagBySync.Clear();
        _bagApplied.Clear();
        _nextBagRequestAt.Clear();
    }

    public static bool IsLocalAuth(GameObject go) {
        if (go == null) {
            return false;
        }
        if (go.GetComponent<TogetherCrystalLocalMarker>() != null) {
            return true;
        }
        TogetherCrystalProps props = go.GetComponent<TogetherCrystalProps>();
        if (props != null && props.Initialized) {
            return true;
        }
        return HasCustomStructuresCrystalAuth(go);
    }

    public static bool HasCustomStructuresCrystalAuth(GameObject go) {
        return FindComponentByTypeName(go, CsLocalMarkerName) != null
            || FindComponentByTypeName(go, CsPropsMarkerName) != null;
    }

    public static void MarkLocal(GameObject go) {
        if (go == null) {
            return;
        }
        if (go.GetComponent<TogetherCrystalLocalMarker>() == null) {
            go.AddComponent<TogetherCrystalLocalMarker>();
        }
    }

    public static void MarkAuthoritySatisfied(knetid syncId) {
        if (syncId.id == 0) {
            return;
        }
        _bagApplied.Add(syncId);
        CrystalAuthority.MarkLocalAuthSatisfied(syncId);
    }

    public static bool BagApplied(knetid syncId) {
        return syncId.id != 0 && _bagApplied.Contains(syncId);
    }

    public static Component FindComponentByTypeName(GameObject go, string typeName) {
        if (go == null || string.IsNullOrEmpty(typeName)) {
            return null;
        }
        Component[] comps = go.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++) {
            Component c = comps[i];
            if (c != null && c.GetType().Name == typeName) {
                return c;
            }
        }
        return null;
    }

    public static TogetherCrystalProps EnsureProps(GameObject go) {
        if (go == null) {
            return null;
        }
        TogetherCrystalProps props = go.GetComponent<TogetherCrystalProps>();
        if (props == null) {
            props = go.AddComponent<TogetherCrystalProps>();
        }
        return props;
    }

    public static bool TryCaptureBag(GameObject go, knetid syncId, out byte[] bag) {
        bag = null;
        if (go == null) {
            return false;
        }
        CrystalBehaviour crystal = null;
        go.TryGetComponent(out crystal);
        if (crystal == null && !CrystalAuthority.IsGenerationDone(go)) {
            return false;
        }

        List<CrystalEffect> effects = crystal != null ? CrystalAuthority.GetEffects(crystal) : null;
        if (effects == null) {
            effects = new List<CrystalEffect>();
        }
        if (crystal != null && effects.Count == 0 && !CrystalAuthority.IsGenerationDone(go)) {
            return false;
        }

        SpriteRenderer sprite = crystal != null && crystal.sprite != null
            ? crystal.sprite
            : go.GetComponent<SpriteRenderer>();
        Light2D light = crystal != null && crystal.light != null
            ? crystal.light
            : go.GetComponent<Light2D>();

        byte flags = 0;
        if (go.transform.localScale.x < 0f) {
            flags |= FlagFlip;
        }
        if (effects.Count > 0 || CrystalAuthority.IsSpecial(go)) {
            flags |= FlagSpecial;
        }

        Color32 spriteCol = sprite != null ? (Color32)sprite.color : (Color32)Color.white;
        Color32 lightCol = light != null ? (Color32)light.color : spriteCol;
        float intensity = light != null ? light.intensity : 1f;
        float volume = 0f;
        float falloff = 0f;
        if (light != null) {
            if (light.volumeIntensityEnabled) {
                flags |= FlagVolumeLight;
                volume = light.volumeIntensity;
            }
            falloff = light.falloffIntensity;
        }

        float heaterTemp = 0f;
        float heaterLerp = 0f;
        if (go.TryGetComponent(out Heater heater)) {
            flags |= FlagHasHeater;
            heaterTemp = heater.desiredTemp;
            heaterLerp = heater.tempLerpStrength;
        }

        float crystalSize = crystal != null ? crystal.crystalSize : 1f;
        float outerRadius = light != null ? light.pointLightOuterRadius : 0f;

        NetDataWriter writer = new NetDataWriter();
        writer.Put(syncId);
        writer.Put(flags);
        writer.Put((Color24)spriteCol);
        writer.Put(spriteCol.a);
        writer.Put((Color24)lightCol);
        writer.Put(lightCol.a);
        writer.Put(intensity);
        writer.Put(volume);
        writer.Put(falloff);
        writer.Put(heaterTemp);
        writer.Put(heaterLerp);
        writer.Put(crystalSize);
        writer.Put(outerRadius);

        byte count = (byte)Mathf.Clamp(effects.Count, 0, 16);
        writer.Put(count);
        for (int i = 0; i < count; i++) {
            int id = CrystalAuthority.EffectIdOf(effects[i]);
            writer.Put((byte)Mathf.Clamp(id, 0, 255));
        }
        for (int i = 0; i < count; i++) {
            float mult = 0f;
            if (effects[i] is CrystalGravity) {
                float raw = Traverse.Create(effects[i]).Field("forceMult").GetValue<float>();
                mult = raw >= 0f ? 1f : -1f;
            }
            writer.Put(mult);
        }

        bag = writer.CopyData();
        return true;
    }

    public static void HostCaptureAndBroadcastBag(GameObject go, SyncInfo si = null) {
        if (!Net.IsServer || go == null) {
            return;
        }
        if (HasCustomStructuresCrystalAuth(go)) {
            // CS owns structure crystals; don't fight their local props.
            return;
        }
        if (si == null) {
            NetObjectRegistry.TryGetSyncInfo(go, out si);
        }
        knetid syncId = si != null ? si.syncId : default;
        if (!TryCaptureBag(go, syncId, out byte[] bag) || bag == null) {
            return;
        }
        if (syncId.id != 0 && _bagBySync.TryGetValue(syncId, out var prevBag) && CrystalAuthority.SameBytes(prevBag, bag)) {
            return;
        }
        if (syncId.id != 0) {
            _bagBySync[syncId] = bag;
        }
        SendBag(bag, ServerMain.AllClientIdsExceptHost);
    }

    public static void HostPushRegisteredBags() {
        if (!Net.IsServer) {
            return;
        }
        var who = ServerMain.AllClientIdsExceptHost;
        if (who == null || who.Count == 0) {
            return;
        }
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si == null || si.go == null || !CrystalAuthority.LooksLikeCrystal(si.go)) {
                continue;
            }
            if (HasCustomStructuresCrystalAuth(si.go)) {
                continue;
            }
            if (!TryCaptureBag(si.go, si.syncId, out byte[] bag) || bag == null) {
                continue;
            }
            _bagBySync[si.syncId] = bag;
            SendBag(bag, who);
        }
    }

    static void SendBag(byte[] bag, IReadOnlyList<knetid> who) {
        if (bag == null || bag.Length < 4 || who == null || who.Count == 0) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CrystalBag);
        writer.PutBytesWithLength(bag);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, who);
    }

    public static void ServerSendBagTo(knetid syncId, IReadOnlyList<knetid> who) {
        if (!Net.IsServer || who == null || who.Count == 0) {
            return;
        }
        byte[] bag = null;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si.go != null) {
            if (HasCustomStructuresCrystalAuth(si.go)) {
                return;
            }
            if (TryCaptureBag(si.go, syncId, out var fresh)) {
                bag = fresh;
                _bagBySync[syncId] = fresh;
            }
        }
        if (bag == null) {
            _bagBySync.TryGetValue(syncId, out bag);
        }
        if (bag != null) {
            SendBag(bag, who);
        }
    }

    public static void StoreBag(knetid syncId, byte[] bag) {
        if (syncId.id == 0 || bag == null) {
            return;
        }
        _bagBySync[syncId] = bag;
        _bagApplied.Remove(syncId);
    }

    public static bool TryGetBag(knetid syncId, out byte[] bag) {
        return _bagBySync.TryGetValue(syncId, out bag) && bag != null && bag.Length >= 4;
    }

    public static bool TryApplyBagToGo(GameObject go, knetid syncId, byte[] bag) {
        if (go == null || bag == null || bag.Length < 4) {
            return false;
        }
        if (HasCustomStructuresCrystalAuth(go)) {
            MarkAuthoritySatisfied(syncId);
            return true;
        }

        NetDataReader reader = new NetDataReader(bag);
        reader.Get(out knetid payloadSync);
        if (syncId.id == 0) {
            syncId = payloadSync;
        }
        reader.Get(out byte flags);
        reader.Get(out Color24 sprite24);
        reader.Get(out byte spriteA);
        reader.Get(out Color24 light24);
        reader.Get(out byte lightA);
        reader.Get(out float intensity);
        reader.Get(out float volume);
        reader.Get(out float falloff);
        reader.Get(out float heaterTemp);
        reader.Get(out float heaterLerp);
        reader.Get(out float crystalSize);
        reader.Get(out float outerRadius);
        reader.Get(out byte effectCount);
        byte[] effectIds = new byte[effectCount];
        for (int i = 0; i < effectCount; i++) {
            reader.Get(out effectIds[i]);
        }
        float[] forceMults = new float[effectCount];
        if (reader.AvailableBytes >= effectCount * 4) {
            for (int i = 0; i < effectCount; i++) {
                reader.Get(out forceMults[i]);
            }
        }

        TogetherCrystalProps props = EnsureProps(go);
        props.Flags = flags;
        props.SpriteColor = sprite24;
        props.SpriteColor = new Color32(props.SpriteColor.r, props.SpriteColor.g, props.SpriteColor.b, spriteA);
        Color32 lightCol = light24;
        lightCol.a = lightA;
        props.LightColor = lightCol;
        props.Intensity = intensity;
        props.Volume = volume;
        props.Falloff = falloff;
        props.HeaterTemp = heaterTemp;
        props.HeaterLerp = heaterLerp;
        props.CrystalSize = crystalSize;
        props.OuterRadius = outerRadius;
        props.EffectIds = effectIds;
        props.EffectForceMults = forceMults;
        props.HasAuthoredColor = true;
        props.Initialized = false;

        bool special = (flags & FlagSpecial) != 0 && effectCount > 0;
        if (!go.TryGetComponent(out CrystalBehaviour crystal)) {
            if (!special) {
                // inert decorative: color only, no CB
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) {
                    sr.color = props.SpriteColor;
                }
                var lightOnly = go.GetComponent<Light2D>();
                if (lightOnly != null) {
                    lightOnly.color = props.LightColor;
                    lightOnly.intensity = intensity;
                }
                ApplyFlip(go, flags);
                props.Initialized = true;
                MarkLocal(go);
                MarkAuthoritySatisfied(syncId);
                return true;
            }
            crystal = go.AddComponent<CrystalBehaviour>();
            crystal.sprite = go.GetComponent<SpriteRenderer>();
            crystal.light = go.GetComponent<Light2D>();
            crystal.build = go.GetComponent<BuildingEntity>();
            CrystalAuthority.EnsureClientEffectsList(crystal);
            if (!go.TryGetComponent(out UsableObject _)) {
                go.AddComponent<UsableObject>();
            }
        }

        crystal.crystalSize = crystalSize;
        ApplyFlip(go, flags);
        CrystalAuthority.SetSpecial(go, special);

        // setUpEffects Prefix consumes props and builds effects/colors.
        crystal.SetUpEffects();

        if ((flags & FlagHasHeater) != 0) {
            Heater heater = go.GetComponent<Heater>() ?? go.AddComponent<Heater>();
            heater.desiredTemp = heaterTemp;
            heater.tempLerpStrength = heaterLerp;
            go.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }

        if (special) {
            if (go.TryGetComponent(out UsableObject usable) && usable != null) {
                usable.enabled = true;
            }
        } else if (go.TryGetComponent(out CrystalBehaviour leftover) && leftover != null) {
            // inert after SetUpEffects may already have destroyed CB; ignore
        }

        MarkLocal(go);
        MarkAuthoritySatisfied(syncId);
        return props.Initialized || IsLocalAuth(go);
    }

    static void ApplyFlip(GameObject go, byte flags) {
        Vector3 scale = go.transform.localScale;
        float mag = Mathf.Abs(scale.x) < 0.001f ? 1f : Mathf.Abs(scale.x);
        scale.x = ((flags & FlagFlip) != 0) ? -mag : mag;
        go.transform.localScale = scale;
    }

    public static bool TryApplyCachedBag(SyncInfo si) {
        if (si == null || si.go == null) {
            return false;
        }
        if (BagApplied(si.syncId) || IsLocalAuth(si.go)) {
            MarkAuthoritySatisfied(si.syncId);
            return true;
        }
        if (!TryGetBag(si.syncId, out byte[] bag)) {
            return false;
        }
        return TryApplyBagToGo(si.go, si.syncId, bag);
    }

    public static void MaybeRequestBag(knetid syncId) {
        if (!Net.IsClient || syncId.id == 0 || BagApplied(syncId)) {
            return;
        }
        float now = Time.unscaledTime;
        if (_nextBagRequestAt.TryGetValue(syncId, out float next) && now < next) {
            return;
        }
        _nextBagRequestAt[syncId] = now + 2.5f;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_RequestCrystalBag);
        writer.Put(syncId);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static string CrystalResourceKey(GameObject go) {
        if (go == null) {
            return null;
        }
        if (go.TryGetComponent(out BuildingEntity build) && !string.IsNullOrEmpty(build.id)) {
            return build.id;
        }
        string name = go.name;
        if (string.IsNullOrEmpty(name)) {
            return null;
        }
        if (name.EndsWith("(Clone)", StringComparison.Ordinal)) {
            name = name.Substring(0, name.Length - "(Clone)".Length);
        }
        int mark = name.IndexOf("res: ", StringComparison.Ordinal);
        if (mark >= 0) {
            int start = mark + 5;
            int end = name.IndexOf(')', start);
            if (end > start) {
                return name.Substring(start, end - start).Trim();
            }
        }
        return name;
    }

    public static bool ResourceIdsMatch(string wantedRes, GameObject go) {
        if (string.IsNullOrEmpty(wantedRes) || go == null) {
            return false;
        }
        string key = CrystalResourceKey(go);
        if (!string.IsNullOrEmpty(key)) {
            return string.Equals(key, wantedRes, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static bool HasLocalMarkedCrystalAt(Vector2 position, GameObject except = null, string requiredRes = null) {
        CrystalBehaviour[] crystals = UnityEngine.Object.FindObjectsOfType<CrystalBehaviour>();
        for (int i = 0; i < crystals.Length; i++) {
            CrystalBehaviour c = crystals[i];
            if (c == null) {
                continue;
            }
            GameObject go = c.gameObject;
            if (except != null && go == except) {
                continue;
            }
            if (!IsLocalAuth(go) || (requiredRes != null && !ResourceIdsMatch(requiredRes, go))) {
                continue;
            }
            Vector2 delta = (Vector2)go.transform.position - position;
            if (delta.sqrMagnitude <= MatchRadiusSq) {
                return true;
            }
        }
        return false;
    }

    public static GameObject FindLocalMarkedCrystalAt(Vector2 position, GameObject except = null, string requiredRes = null) {
        CrystalBehaviour[] crystals = UnityEngine.Object.FindObjectsOfType<CrystalBehaviour>();
        GameObject best = null;
        float bestSq = MatchRadiusSq;
        for (int i = 0; i < crystals.Length; i++) {
            CrystalBehaviour c = crystals[i];
            if (c == null) {
                continue;
            }
            GameObject go = c.gameObject;
            if (except != null && go == except) {
                continue;
            }
            if (!IsLocalAuth(go) || (requiredRes != null && !ResourceIdsMatch(requiredRes, go))) {
                continue;
            }
            float sq = ((Vector2)go.transform.position - position).sqrMagnitude;
            if (sq <= bestSq) {
                bestSq = sq;
                best = go;
            }
        }
        return best;
    }

    // network shell landed on top of a CS/Together local crystal - rebind SyncInfo and destroy the shell mesh.
    public static bool TryDiscardReplica(SyncInfo si) {
        if (!Net.IsClient || si == null || si.go == null) {
            return false;
        }
        GameObject replica = si.go;
        if (IsLocalAuth(replica) && !HasCustomStructuresCrystalAuth(replica)) {
            // together shell that already received a bag - keep it.
            return false;
        }
        string requiredRes = null;
        try {
            requiredRes = si.resourceId.Value;
        } catch {
            requiredRes = null;
        }
        GameObject local = FindLocalMarkedCrystalAt(replica.transform.position, replica, requiredRes);
        if (local == null) {
            return false;
        }
        Plugin.StartCoroutine(DiscardReplicaNextFrame(si, replica, local));
        return true;
    }

    static IEnumerator DiscardReplicaNextFrame(SyncInfo si, GameObject replica, GameObject local) {
        yield return null;
        if (si == null || local == null) {
            yield break;
        }
        if (replica != null) {
            try {
                if (NetObjectRegistry.SyncRegistry.TryGetValue(replica, out var mapped) && mapped == si) {
                    NetObjectRegistry.SyncRegistry.Remove(replica);
                }
                if (replica.TryGetComponent(out SyncInfoGameObjectTracker repTracker)) {
                    repTracker.syncInfo = null;
                    UnityEngine.Object.Destroy(repTracker);
                }
                UnityEngine.Object.Destroy(replica);
            } catch (Exception ex) {
                Plugin.log.LogWarning($"[CrystalLocalAuth] discard replica failed: {ex.Message}");
            }
        }

        si.go = local;
        NetObjectRegistry.SyncRegistry[local] = si;
        SyncInfoGameObjectTracker tracker = local.GetComponent<SyncInfoGameObjectTracker>()
            ?? local.AddComponent<SyncInfoGameObjectTracker>();
        tracker.syncInfo = si;
        MarkLocal(local);
        MarkAuthoritySatisfied(si.syncId);
    }

    public static List<CrystalEffect> CreateEffectsFromIds(byte[] effectIds) {
        List<CrystalEffect> list = new List<CrystalEffect>();
        if (effectIds == null || effectIds.Length == 0) {
            return list;
        }
        CrystalAuthority.EnsureEffectMapsPublic();
        for (int i = 0; i < effectIds.Length; i++) {
            int id = effectIds[i];
            CrystalEffect effect = CrystalAuthority.CreateEffectById(id);
            if (effect != null) {
                list.Add(effect);
            }
        }
        return list;
    }

    public static List<CrystalEffect> CreateEffectsFromCsv(string raw) {
        List<CrystalEffect> list = new List<CrystalEffect>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] parts = (raw ?? string.Empty).Split(',');
        for (int i = 0; i < parts.Length; i++) {
            string token = parts[i].Trim();
            if (token.Length == 0 || !seen.Add(token)) {
                continue;
            }
            bool matched = false;
            foreach (var possible in CrystalBehaviour.possibleEffects) {
                CrystalEffect effect = possible.factory();
                if (string.Equals(effect.GetType().Name, "Crystal" + token, StringComparison.OrdinalIgnoreCase)) {
                    list.Add(effect);
                    matched = true;
                    break;
                }
            }
            if (!matched) {
                Plugin.log.LogWarning($"[CrystalLocalAuth] Unknown crystal property '{token}'.");
            }
        }
        return list;
    }

    static FieldInfo CrystalEffectsField {
        get {
            if (_crystalEffectsField == null) {
                _crystalEffectsField = AccessTools.Field(typeof(CrystalBehaviour), "effects");
            }
            return _crystalEffectsField;
        }
    }

    public static void AssignEffects(CrystalBehaviour crystal, List<CrystalEffect> effects) {
        if (crystal == null) {
            return;
        }
        crystal.effects = effects ?? new List<CrystalEffect>();
        CrystalEffectsField?.SetValue(crystal, crystal.effects);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalBag)]
    static void Client_ReceiveBag(knetid _, NetDataReader reader) {
        byte[] bag = reader.GetBytesWithLength();
        if (bag == null || bag.Length < 4) {
            return;
        }
        NetDataReader probe = new NetDataReader(bag);
        probe.Get(out knetid syncId);
        StoreBag(syncId, bag);
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si != null && si.go != null) {
            TryApplyBagToGo(si.go, syncId, bag);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCrystalBag)]
    static void Server_RequestBag(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        ServerSendBagTo(syncId, new[] { clientId });
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.SetUpEffects))]
static class CrystalBehaviour_SetUpEffects_LocalAuth_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance) {
        if (!Net.IsRunning || __instance == null) {
            return true;
        }

        // custom Structures owns its CrystalPropertiesMarker path - do not double-apply.
        if (CrystalLocalAuthority.FindComponentByTypeName(__instance.gameObject, "CrystalPropertiesMarker") != null) {
            return true;
        }

        TogetherCrystalProps props = __instance.GetComponent<TogetherCrystalProps>();
        if (props == null) {
            return true;
        }
        if (props.Initialized) {
            return false;
        }
        props.Initialized = true;

        List<CrystalEffect> effects;
        if (props.EffectIds != null && props.EffectIds.Length > 0) {
            effects = CrystalLocalAuthority.CreateEffectsFromIds(props.EffectIds);
        } else {
            effects = CrystalLocalAuthority.CreateEffectsFromCsv(props.EffectsCsv);
        }

        CrystalLocalAuthority.AssignEffects(__instance, effects);
        for (int i = 0; i < effects.Count; i++) {
            effects[i].crystal = __instance;
            effects[i].Start();
            if (props.EffectForceMults != null && i < props.EffectForceMults.Length
                && effects[i] is CrystalGravity
                && Mathf.Abs(props.EffectForceMults[i]) > 0.1f) {
                Traverse.Create(effects[i]).Field("forceMult").SetValue(props.EffectForceMults[i] >= 0f ? 1f : -1f);
            }
        }

        if (props.HasAuthoredColor) {
            CrystalAuthority.ApplyingAuthority = true;
            try {
                if (__instance.sprite != null) {
                    __instance.sprite.color = props.SpriteColor;
                }
                if (__instance.light != null) {
                    __instance.light.color = props.LightColor;
                    __instance.light.intensity = props.Intensity;
                    __instance.light.volumeIntensityEnabled = (props.Flags & CrystalLocalAuthority.FlagVolumeLight) != 0;
                    if (__instance.light.volumeIntensityEnabled) {
                        __instance.light.volumeIntensity = props.Volume;
                    }
                    __instance.light.falloffIntensity = props.Falloff;
                    if (props.OuterRadius > 0f) {
                        __instance.light.pointLightOuterRadius = props.OuterRadius;
                    }
                }
            } finally {
                CrystalAuthority.ApplyingAuthority = false;
            }
        } else if (!string.IsNullOrEmpty(props.ColorR) || !string.IsNullOrEmpty(props.ColorG) || !string.IsNullOrEmpty(props.ColorB)) {
            if (TryParseRgb(props, out Color color)) {
                if (__instance.sprite != null) {
                    __instance.sprite.color = color;
                }
                if (__instance.light != null) {
                    __instance.light.color = color;
                }
            }
        }

        if (effects.Count > 1) {
            GameObject distort = Utils.Create("Special/CrystalDistort", __instance.transform);
            if (distort != null) {
                distort.transform.localPosition = Vector3.zero;
                distort.transform.localScale = Vector3.one;
                distort.transform.rotation = Quaternion.identity;
                SpriteRenderer sr = distort.GetComponent<SpriteRenderer>();
                if (sr != null && sr.material != null) {
                    sr.material.SetFloat("_DistortStrength", effects.Count * 0.015f);
                }
            }
        }

        if (effects.Count > 0 && WorldGeneration.world != null) {
            __instance.CheckCurrentChunk(WorldGeneration.world.GetClosestChunkScript(__instance.transform.position));
        }

        if (__instance.build != null && effects.Count > 0) {
            __instance.build.description = Locale.GetBuilding(__instance.build.id + "dsc");
            string key = effects.Count switch {
                1 => "crystaleffect1",
                2 => "crystaleffect2",
                3 => "crystaleffect3",
                4 => "crystaleffect4",
                _ => "crystaleffectmany",
            };
            __instance.build.description += " " + Locale.GetOther(key);
            __instance.build.skipDescriptionSet = true;
        }

        if (effects.Count == 0) {
            UsableObject usable = __instance.GetComponent<UsableObject>();
            if (usable != null) {
                UnityEngine.Object.Destroy(usable);
            }
            UnityEngine.Object.Destroy(__instance);
        }

        CrystalLocalAuthority.MarkLocal(__instance != null ? __instance.gameObject : props.gameObject);
        return false;
    }

    static bool TryParseRgb(TogetherCrystalProps props, out Color color) {
        color = Color.white;
        if (!float.TryParse(props.ColorR, out float r)) {
            r = 1f;
        }
        if (!float.TryParse(props.ColorG, out float g)) {
            g = 1f;
        }
        if (!float.TryParse(props.ColorB, out float b)) {
            b = 1f;
        }
        color = new Color(r, g, b, 1f);
        return true;
    }
}

#endif