using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static UnityEngine.GraphicsBuffer;

namespace Together;

// flash / sting / shake only for the body that touched it. world edits stay on the host.

// pit of doom ahead
// if you're reading this, turn back now! save yourself!
// you have been warned
// fixme: sync is unreliable. i dont know why. i tried for hours.
// currently: spawns shell crystal (color/size, etc, + "special" flag.)
// special flag requests crystal special effects until they're successfully applied, infinitely.
// why only work sometimes? only god knows and he's not telling.
public static class CrystalLocalFx {
    public static Body TriggerBody;
    public static CrystalBehaviour Crystal;

    static readonly HashSet<string> TriggerFeedbackClips = new HashSet<string> {
        "observerlaugh",
        "crystalheal",
        "crystalemp",
        "crystalenemylaugh",
        "crystaltick",
        // teleport / metamorphic also use these as first-person stings
        "Scald",
        "zap",
    };

    public static bool IsTriggerFeedbackClip(string clip) {
        return clip != null && TriggerFeedbackClips.Contains(clip);
    }

    internal static int AllowFlashFrame = -1;
    internal static int BlockFlashFrame = -1;

    public static void Begin(Body body, CrystalBehaviour crystal) {
        TriggerBody = body;
        Crystal = crystal;
        if (crystal != null && body != null) {
            crystal.GetOrAddComponent<BodyGetterOverrider>().body = body;
        }
        // flashBrief is a coroutine. it starts a frame after Touched, after End() has cleared the trigger.
        if (Net.IsRunning && body != null) {
            int until = Time.frameCount + 2;
            if (PlayerCamera.main != null && body == PlayerCamera.main.body) {
                AllowFlashFrame = until;
            } else {
                BlockFlashFrame = until;
            }
        }
    }

    public static void End() {
        TriggerBody = null;
        Crystal = null;
    }

    public static bool IsLocalTrigger() {
        return TriggerBody != null && Util.IsBodyLocal(TriggerBody);
    }

    public static bool SuppressNonLocalFeedback() {
        if (!Net.IsRunning) {
            return false;
        }
        if (TriggerBody != null) {
            return !Util.IsBodyLocal(TriggerBody);
        }
        if (Crystal != null && Crystal.TryGetComponent(out BodyGetterOverrider bgo) && bgo.body != null) {
            return !Util.IsBodyLocal(bgo.body);
        }
        // no active Touched/Hit context (e.g. CrystalEnemy AI) - leave alone.
        return false;
    }
}

#if FULLGAME

// host rolls GenerationFinished once; clients spawn mute shells.
// fill (color/size/flip/heater + special flag) always syncs.
// special effect data is a second payload, requested only when special==true.
public static class CrystalAuthority {
    const byte FlagInert = 1;
    const byte FlagFlip = 2;
    const byte FlagHasHeater = 4;
    const byte FlagVolumeLight = 8;
    const byte FlagSpecial = 16;
    // effect-state byte, not the fill flags byte (FlagHasHeater is also 4 there).
    const byte FlagGravityForce = 4;

    static void Bug(string msg) {
    }

    const string SpecialKey = "CrystalSpecial";
    const string GenDoneKey = "CrystalGenDone";

    struct PosEntry {
        public knetid syncId;
        public byte[] fill;
        public byte[] specials;
    }
    static readonly Dictionary<long, List<PosEntry>> _byPos = new();
    static readonly Dictionary<knetid, byte[]> _fillBySync = new();
    static readonly Dictionary<knetid, byte[]> _specialsBySync = new();
    static readonly HashSet<knetid> _fillApplied = new();
    static readonly HashSet<knetid> _specialsApplied = new();
    static readonly Dictionary<knetid, float> _nextFillAt = new();
    static readonly Dictionary<knetid, float> _nextSpecialAt = new();
    static readonly HashSet<GameObject> _pendingCapture = new();
    public static bool ApplyingAuthority;

    static float _nextBlankScanAt;
    const float BlankScanInterval = 2.5f;
    const int BlankScanBudget = 12;

    public static long PosKey(Vector2 p) {
        int qx = Mathf.RoundToInt(p.x * 10f);
        int qy = Mathf.RoundToInt(p.y * 10f);
        return ((long)qx << 32) ^ (uint)qy;
    }

    public static long PosKeyOf(GameObject go) {
        if (go == null) {
            return 0;
        }
        return PosKey(go.transform.position);
    }

    static int PosEntryCount() {
        int n = 0;
        foreach (var kv in _byPos) {
            if (kv.Value != null) {
                n += kv.Value.Count;
            }
        }
        return n;
    }

    static void ClearLayerCaches() {
        _byPos.Clear();
        _fillBySync.Clear();
        _specialsBySync.Clear();
        _fillApplied.Clear();
        _specialsApplied.Clear();
        _nextFillAt.Clear();
        _nextSpecialAt.Clear();
        _pendingCapture.Clear();
        _liveCrystals.Clear();
        _nextBlankScanAt = 0f;
        CrystalDelayedFx.ClearArmBroadcasts();
        CrystalLocalAuthority.ClearLayerCaches();
    }

    public static void MarkLocalAuthSatisfied(knetid syncId) {
        if (syncId.id == 0) {
            return;
        }
        _fillApplied.Add(syncId);
        _specialsApplied.Add(syncId);
        _nextFillAt.Remove(syncId);
        _nextSpecialAt.Remove(syncId);
    }

    public static void EnsureEffectMapsPublic() {
        EnsureEffectMaps();
    }

    public static CrystalEffect CreateEffectById(int id) {
        EnsureEffectMaps();
        if (id < 0 || _factories == null || id >= _factories.Length || _factories[id] == null) {
            return null;
        }
        return _factories[id]();
    }

    public static void SetSpecial(GameObject go, bool special) {
        if (go == null) {
            return;
        }
        go.GetOrAddComponent<AnyDataContainerComponent>().Set(SpecialKey, special);
    }

    public static bool IsSpecial(GameObject go) {
        if (go == null) {
            return false;
        }
        if (go.TryGetComponent(out AnyDataContainerComponent data) && data.dict.TryGetValue(SpecialKey, out object v)) {
            if (v is bool b) {
                return b;
            }
        }
        return false;
    }

    public static void MarkGenerationDone(GameObject go) {
        if (go == null) {
            return;
        }
        go.GetOrAddComponent<AnyDataContainerComponent>().Set(GenDoneKey, true);
    }

    public static bool IsGenerationDone(GameObject go) {
        if (go == null) {
            return SharedMain.LocalWorldIsGenerated;
        }
        if (go.TryGetComponent(out AnyDataContainerComponent data) && data.dict.ContainsKey(GenDoneKey)) {
            return true;
        }
        return SharedMain.LocalWorldIsGenerated && !go.TryGetComponent(out CrystalBehaviour _);
    }

    static byte[] RemapSyncId(byte[] payload, knetid syncId) {
        if (payload == null || payload.Length < 3) {
            return payload;
        }
        NetDataReader reader = new NetDataReader(payload);
        reader.Get(out knetid _);
        int avail = reader.AvailableBytes;
        NetDataWriter headWriter = new NetDataWriter();
        headWriter.Put(syncId);
        byte[] head = headWriter.CopyData();
        if (avail <= 0) {
            return head;
        }
        byte[] result = new byte[head.Length + avail];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(reader.RawData, reader.Position, result, head.Length, avail);
        return result;
    }

    static bool PayloadIsSpecial(byte[] fill) {
        if (fill == null || fill.Length < 4) {
            return false;
        }
        try {
            NetDataReader reader = new NetDataReader(fill);
            reader.Get(out knetid _);
            reader.Get(out byte flags);
            return (flags & FlagSpecial) != 0;
        } catch {
            return false;
        }
    }

    static void Store(GameObject go, knetid syncId, byte[] fill, byte[] specials) {
        if (go == null) {
            return;
        }
        long key = PosKeyOf(go);
        if (syncId.id != 0) {
            if (fill != null) {
                fill = RemapSyncId(fill, syncId);
                _fillBySync[syncId] = fill;
            }
            if (specials != null) {
                specials = RemapSyncId(specials, syncId);
                _specialsBySync[syncId] = specials;
            }
        }
        if (!_byPos.TryGetValue(key, out var list) || list == null) {
            list = new List<PosEntry>(2);
            _byPos[key] = list;
        }
        int idx = -1;
        if (syncId.id != 0) {
            for (int i = 0; i < list.Count; i++) {
                if (list[i].syncId.id == syncId.id) {
                    idx = i;
                    break;
                }
            }
        }
        if (idx < 0 && list.Count == 1 && syncId.id != 0) {
            idx = 0;
        }
        if (idx >= 0) {
            var prev = list[idx];
            list[idx] = new PosEntry {
                syncId = syncId.id != 0 ? syncId : prev.syncId,
                fill = fill ?? prev.fill,
                specials = specials ?? prev.specials,
            };
            if (list[idx].fill != null && syncId.id != 0) {
                _fillBySync[syncId] = list[idx].fill;
            }
            if (list[idx].specials != null && syncId.id != 0) {
                _specialsBySync[syncId] = list[idx].specials;
            }
            return;
        }
        list.Add(new PosEntry { syncId = syncId, fill = fill, specials = specials });
    }

    static bool TryFill(knetid syncId, long posKey, out byte[] fill) {
        if (_fillBySync.TryGetValue(syncId, out fill) && fill != null && fill.Length >= 3) {
            return true;
        }
        fill = null;
        if (!_byPos.TryGetValue(posKey, out var list) || list == null) {
            return false;
        }
        for (int i = 0; i < list.Count; i++) {
            if (list[i].syncId.id == syncId.id && list[i].fill != null && list[i].fill.Length >= 3) {
                fill = RemapSyncId(list[i].fill, syncId);
                return true;
            }
        }
        if (list.Count == 1 && list[0].fill != null && list[0].fill.Length >= 3) {
            fill = RemapSyncId(list[0].fill, syncId);
            return true;
        }
        return false;
    }

    static bool TrySpecials(knetid syncId, long posKey, out byte[] specials) {
        if (_specialsBySync.TryGetValue(syncId, out specials) && specials != null && specials.Length >= 3) {
            return true;
        }
        specials = null;
        if (!_byPos.TryGetValue(posKey, out var list) || list == null) {
            return false;
        }
        for (int i = 0; i < list.Count; i++) {
            if (list[i].syncId.id == syncId.id && list[i].specials != null && list[i].specials.Length >= 3) {
                specials = RemapSyncId(list[i].specials, syncId);
                return true;
            }
        }
        if (list.Count == 1 && list[0].specials != null && list[0].specials.Length >= 3) {
            specials = RemapSyncId(list[0].specials, syncId);
            return true;
        }
        return false;
    }

    static Func<CrystalEffect>[] _factories;
    static Type[] _effectTypes;

    static void EnsureEffectMaps() {
        if (_factories != null) {
            return;
        }
        var list = CrystalBehaviour.possibleEffects;
        _factories = new Func<CrystalEffect>[list.Count];
        _effectTypes = new Type[list.Count];
        for (int i = 0; i < list.Count; i++) {
            _factories[i] = list[i].factory;
            _effectTypes[i] = list[i].factory().GetType();
        }
    }

    public static int EffectIdOf(CrystalEffect effect) {
        if (effect == null) {
            return -1;
        }
        EnsureEffectMaps();
        Type t = effect.GetType();
        for (int i = 0; i < _effectTypes.Length; i++) {
            if (_effectTypes[i] == t) {
                return i;
            }
        }
        return -1;
    }

    public static List<CrystalEffect> GetEffects(CrystalBehaviour crystal) {
        if (crystal == null) {
            return null;
        }
        return crystal.effects;
    }

    public static void HostCapture(GameObject go) {
        if (!Net.IsServer || go == null) {
            return;
        }
        knetid syncId = default;
        if (NetObjectRegistry.TryGetSyncInfo(go, out var si) && si != null) {
            syncId = si.syncId;
        }
        if (!CapturePair(go, syncId, out var fill, out var specials)) {
            _pendingCapture.Add(go);
            return;
        }
        Store(go, syncId, fill, specials);
        _pendingCapture.Remove(go);
        Bug($"capture sync={syncId} special={PayloadIsSpecial(fill)} fillLen={(fill == null ? -1 : fill.Length)} specLen={(specials == null ? -1 : specials.Length)} pos={go.transform.position}");
    }

    // mutations (EMP/Healing/etc): update cache and push fill + specials if flagged.
    internal static bool SameBytes(byte[] a, byte[] b) {
        if (ReferenceEquals(a, b)) {
            return true;
        }
        if (a == null || b == null || a.Length != b.Length) {
            return false;
        }
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }
        return true;
    }

    public static void HostCaptureAndBroadcast(GameObject go) {
        if (!Net.IsServer || go == null) {
            return;
        }
        HostCapture(go);
        if (!NetObjectRegistry.TryGetSyncInfo(go, out var si) || si == null) {
            return;
        }
        HostCaptureAndBroadcast(go, si);
    }

    public static void HostCaptureAndBroadcast(GameObject go, SyncInfo si) {
        if (!Net.IsServer || go == null || si == null) {
            return;
        }
        if (CrystalLocalAuthority.HasCustomStructuresCrystalAuth(go)) {
            return;
        }
        // primary path: one-shot local-auth bag (CS replication).
        CrystalLocalAuthority.HostCaptureAndBroadcastBag(go, si);
        if (!CapturePair(go, si.syncId, out var fill, out var specials)) {
            if (TryFill(si.syncId, PosKeyOf(go), out var cached)) {
                fill = cached;
                TrySpecials(si.syncId, PosKeyOf(go), out specials);
            } else {
                _pendingCapture.Add(go);
                return;
            }
        }
        if (_fillBySync.TryGetValue(si.syncId, out var prevFill) && SameBytes(prevFill, fill)
            && (!PayloadIsSpecial(fill) || (_specialsBySync.TryGetValue(si.syncId, out var prevSpec) && SameBytes(prevSpec, specials)))) {
            _pendingCapture.Remove(go);
            return;
        }
        Store(go, si.syncId, fill, specials);
        // keep legacy fill/specials during hybrid cutover for clients that miss bags.
        SendFill(fill, ServerMain.AllClientIdsExceptHost);
        if (PayloadIsSpecial(fill) && specials != null) {
            SendSpecials(specials, ServerMain.AllClientIdsExceptHost);
        }
        _pendingCapture.Remove(go);
    }

    public static void HostOnCrystalRegistered(GameObject go, SyncInfo si = null) {
        if (!Net.IsServer || go == null) {
            return;
        }
        if (!go.TryGetComponent(out CrystalBehaviour _) && !LooksLikeCrystal(go)) {
            return;
        }
        HostCapture(go);
        if (si == null) {
            NetObjectRegistry.TryGetSyncInfo(go, out si);
        }
        CrystalLocalAuthority.HostCaptureAndBroadcastBag(go, si);
    }

    public static void HostFlushPendingCaptures() {
        if (!Net.IsServer || _pendingCapture.Count == 0) {
            return;
        }
        List<GameObject> pending = new List<GameObject>(_pendingCapture);
        _pendingCapture.Clear();
        for (int i = 0; i < pending.Count; i++) {
            var go = pending[i];
            if (go == null) {
                continue;
            }
            HostCapture(go);
        }
    }

    public static void ClientForceReapply(knetid syncId) {
        _fillApplied.Remove(syncId);
        _specialsApplied.Remove(syncId);
        _nextFillAt.Remove(syncId);
        _nextSpecialAt.Remove(syncId);
    }

    public static void ClientResyncAllCrystals() {
        if (!Net.IsClient) {
            return;
        }
        _fillApplied.Clear();
        _specialsApplied.Clear();
        _nextFillAt.Clear();
        _nextSpecialAt.Clear();
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si != null) {
                ClientTryApplyOrRequest(si);
            }
        }
    }

    // returns false if gen isn't done yet (caller should pending-retry).
    static bool CapturePair(GameObject go, knetid syncId, out byte[] fill, out byte[] specials) {
        fill = null;
        specials = null;
        if (go == null) {
            return false;
        }
        CrystalBehaviour crystal = null;
        go.TryGetComponent(out crystal);
        if (crystal == null && !IsGenerationDone(go)) {
            return false;
        }
        var effects = crystal != null ? GetEffects(crystal) : null;
        if (effects == null) {
            effects = new List<CrystalEffect>();
        }
        bool genDone = IsGenerationDone(go);
        if (crystal != null && effects.Count == 0 && !genDone) {
            return false;
        }

        bool special = effects.Count > 0;
        // sticky: once special, stay special even if effects get spent/cleared mid-run.
        if (IsSpecial(go)) {
            special = true;
        }
        if (special) {
            SetSpecial(go, true);
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
        if (special) {
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
        fill = writer.CopyData();

        if (special && effects.Count > 0) {
            NetDataWriter sw = new NetDataWriter();
            sw.Put(syncId);
            byte count = (byte)Mathf.Clamp(effects.Count, 0, 16);
            sw.Put(count);
            for (int i = 0; i < count; i++) {
                int id = EffectIdOf(effects[i]);
                sw.Put((byte)Mathf.Clamp(id, 0, 255));
                WriteEffectState(sw, effects[i]);
            }
            specials = sw.CopyData();
        }
        return true;
    }

    static void WriteEffectState(NetDataWriter writer, CrystalEffect effect) {
        if (effect == null) {
            writer.Put(0f);
            writer.Put((byte)0);
            return;
        }
        var t = Traverse.Create(effect);
        float cooldownOrTimer = 0f;
        byte flags = 0;
        if (effect is CrystalGravity) {
            var force = t.Field("forceMult");
            float mult = force.FieldExists() ? force.GetValue<float>() : 1f;
            cooldownOrTimer = mult >= 0f ? 1f : -1f;
            flags |= FlagGravityForce;
        } else {
            var cooldown = t.Field("cooldown");
            if (cooldown.FieldExists()) {
                cooldownOrTimer = cooldown.GetValue<float>();
            } else {
                var timer = t.Field("timer");
                if (timer.FieldExists()) {
                    cooldownOrTimer = timer.GetValue<float>();
                }
            }
        }
        var activated = t.Field("activated");
        if (activated.FieldExists() && activated.GetValue<bool>()) {
            flags |= 1;
        }
        var timerStarted = t.Field("timerStarted");
        if (timerStarted.FieldExists() && timerStarted.GetValue<bool>()) {
            flags |= 2;
        }
        writer.Put(cooldownOrTimer);
        writer.Put(flags);
    }

    static void SendFill(byte[] fill, IReadOnlyList<knetid> who) {
        if (fill == null || fill.Length < 3 || who == null || who.Count == 0) {
            return;
        }
        NetDataReader probe = new NetDataReader(fill);
        probe.Get(out knetid syncId);
        probe.Get(out byte flags);
        Bug($"send FILL sync={syncId} special={(flags & FlagSpecial) != 0} len={fill.Length} to={who.Count}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CrystalState);
        writer.PutBytesWithLength(fill);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, who);
    }

    static void SendSpecials(byte[] specials, IReadOnlyList<knetid> who) {
        if (specials == null || specials.Length < 3 || who == null || who.Count == 0) {
            return;
        }
        NetDataReader probe = new NetDataReader(specials);
        probe.Get(out knetid syncId);
        probe.Get(out byte count);
        Bug($"send SPECIALS sync={syncId} effects={count} len={specials.Length} to={who.Count}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CrystalSpecials);
        writer.PutBytesWithLength(specials);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, who);
    }

    public static void ServerSendTo(knetid syncId, IReadOnlyList<knetid> who) {
        byte[] fill = null;
        byte[] specials = null;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si.go != null) {
            bool hasCrystal = si.go.TryGetComponent(out CrystalBehaviour _);
            if (!hasCrystal && !LooksLikeCrystal(si.go)) {
                Bug($"send skip sync={syncId} not a crystal");
                return;
            }
            if (CapturePair(si.go, syncId, out var freshFill, out var freshSpec)) {
                Store(si.go, syncId, freshFill, freshSpec);
                fill = freshFill;
                specials = freshSpec;
            } else {
                TryFill(syncId, PosKeyOf(si.go), out fill);
                TrySpecials(syncId, PosKeyOf(si.go), out specials);
                if (hasCrystal && fill == null) {
                    _pendingCapture.Add(si.go);
                }
            }
        } else {
            _fillBySync.TryGetValue(syncId, out fill);
            _specialsBySync.TryGetValue(syncId, out specials);
        }
        CrystalLocalAuthority.ServerSendBagTo(syncId, who);
        if (fill == null) {
            Bug($"send miss sync={syncId} no fill");
            return;
        }
        SendFill(fill, who);
        if (PayloadIsSpecial(fill) && specials != null) {
            SendSpecials(specials, who);
        }
    }

    public static bool LooksLikeCrystal(GameObject go) {
        if (go == null) {
            return false;
        }
        if (go.TryGetComponent(out CrystalBehaviour _)) {
            return true;
        }
        if (go.TryGetComponent(out BuildingEntity be) && be != null && be.id != null) {
            string id = be.id;
            return id.IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        return false;
    }

    public static void ClientTryApplyOrRequest(SyncInfo si) {
        NoteCrystal(si);
        if (!Net.IsClient || si == null || si.go == null) {
            return;
        }
        // local-auth / CS-authored crystals: prefer bag, never enter fill/specials request loop
        if (CrystalLocalAuthority.IsLocalAuth(si.go) || CrystalLocalAuthority.BagApplied(si.syncId)) {
            MarkLocalAuthSatisfied(si.syncId);
            return;
        }
        if (CrystalLocalAuthority.TryApplyCachedBag(si)) {
            return;
        }
        if (_fillApplied.Contains(si.syncId) && (!NeedsSpecials(si) || _specialsApplied.Contains(si.syncId))) {
            return;
        }
        if (!si.go.TryGetComponent(out CrystalBehaviour _)
            && !_fillBySync.ContainsKey(si.syncId)
            && !LooksLikeCrystal(si.go)) {
            return;
        }
        if (!SharedMain.LocalWorldIsGenerated) {
            return;
        }
        // prefer one-shot bag before legacy fill/specials traffic.
        if (!CrystalLocalAuthority.TryGetBag(si.syncId, out _)) {
            CrystalLocalAuthority.MaybeRequestBag(si.syncId);
        }
        if (Net.IsServer) {
            if (si.go.TryGetComponent(out CrystalBehaviour hostCrystal)) {
                var hostEffects = GetEffects(hostCrystal);
                if (hostEffects != null && hostEffects.Count > 0) {
                    if (si.go.TryGetComponent(out UsableObject hostUsable) && hostUsable != null) {
                        hostUsable.enabled = true;
                    }
                    _fillApplied.Add(si.syncId);
                    _specialsApplied.Add(si.syncId);
                    return;
                }
            } else if (IsGenerationDone(si.go)) {
                _fillApplied.Add(si.syncId);
                return;
            }
        }

        if (si.go.TryGetComponent(out CrystalBehaviour crystal)) {
            EnsureClientEffectsList(crystal);
            if (!IsSpecial(si.go) && si.go.TryGetComponent(out UsableObject usable)) {
                usable.enabled = false;
            }
        }

        long posKey = PosKeyOf(si.go);
        if (!_fillApplied.Contains(si.syncId)) {
            if (TryFill(si.syncId, posKey, out var fill)) {
                ApplyFill(fill);
            } else {
                MaybeRequestFill(si.syncId);
                return;
            }
        }
        if (NeedsSpecials(si) && !_specialsApplied.Contains(si.syncId)) {
            if (TrySpecials(si.syncId, posKey, out var specials) && SpecialsHaveEffects(specials)) {
                ApplySpecials(specials);
            }
            // empty or failed apply does not count as filled - keep asking.
            if (!_specialsApplied.Contains(si.syncId)) {
                MaybeRequestSpecials(si.syncId);
            }
        }
    }

    static bool SpecialsHaveEffects(byte[] specials) {
        if (specials == null || specials.Length < 4) {
            return false;
        }
        try {
            NetDataReader reader = new NetDataReader(specials);
            reader.Get(out knetid _);
            reader.Get(out byte count);
            return count > 0;
        } catch {
            return false;
        }
    }

    static bool NeedsSpecials(SyncInfo si) {
        if (si == null || si.go == null) {
            return false;
        }
        if (IsSpecial(si.go)) {
            if (si.go.TryGetComponent(out CrystalBehaviour crystal)) {
                var fx = GetEffects(crystal);
                return fx == null || fx.Count == 0;
            }
            return true;
        }
        if (_fillBySync.TryGetValue(si.syncId, out var fill) && PayloadIsSpecial(fill)) {
            return true;
        }
        return false;
    }

    static void MaybeRequestFill(knetid syncId) {
        float now = Time.unscaledTime;
        if (_nextFillAt.TryGetValue(syncId, out float next) && now < next) {
            return;
        }
        _nextFillAt[syncId] = now + 2.5f;
        Bug($"req FILL sync={syncId}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_RequestCrystalState);
        writer.Put(syncId);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static void MaybeRequestSpecials(knetid syncId) {
        float now = Time.unscaledTime;
        if (_nextSpecialAt.TryGetValue(syncId, out float next) && now < next) {
            return;
        }
        _nextSpecialAt[syncId] = now + 1f;
        Bug($"req SPECIALS sync={syncId}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_RequestCrystalSpecials);
        writer.Put(syncId);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static bool IsNearWhite(Color32 c) {
        return c.r >= 245 && c.g >= 245 && c.b >= 245;
    }

    // specials keep requesting every 1s until an effect payload actually applies. No attempt cap.
    static readonly List<SyncInfo> _liveCrystals = new List<SyncInfo>();

    static void NoteCrystal(SyncInfo si) {
        if (si == null) {
            return;
        }
        for (int i = 0; i < _liveCrystals.Count; i++) {
            if (_liveCrystals[i] == si) {
                return;
            }
        }
        _liveCrystals.Add(si);
    }

    static void TickSpecialRetries() {
        if (Net.IsServer) {
            return;
        }
        for (int i = _liveCrystals.Count - 1; i >= 0; i--) {
            SyncInfo si = _liveCrystals[i];
            if (si == null || si.go == null || _specialsApplied.Contains(si.syncId)) {
                continue;
            }
            if (!NeedsSpecials(si)) {
                continue;
            }
            ClientTryApplyOrRequest(si);
        }
    }

    public static void ClientTickBlankResync() {
        if (!Net.IsClient || !Net.IsRunning || !SharedMain.LocalWorldIsGenerated) {
            return;
        }
        if (_liveCrystals.Count == 0) {
            return;
        }
        TickSpecialRetries();
        float now = Time.unscaledTime;
        if (now < _nextBlankScanAt) {
            return;
        }
        _nextBlankScanAt = now + BlankScanInterval;

        int sent = 0;
        for (int i = _liveCrystals.Count - 1; i >= 0 && sent < BlankScanBudget; i--) {
            SyncInfo si = _liveCrystals[i];
            if (si == null || si.go == null) {
                continue;
            }
            if (!si.go.TryGetComponent(out CrystalBehaviour _) && !LooksLikeCrystal(si.go)) {
                continue;
            }
            bool needFill = !_fillApplied.Contains(si.syncId);
            if (!needFill && si.go.TryGetComponent(out CrystalBehaviour blank)) {
                var sr = blank.sprite != null ? blank.sprite : si.go.GetComponent<SpriteRenderer>();
                if (sr != null && IsNearWhite((Color32)sr.color)) {
                    needFill = true;
                }
            }
            bool needSpec = NeedsSpecials(si) && !_specialsApplied.Contains(si.syncId);
            if (!needFill && !needSpec) {
                continue;
            }
            if (needFill) {
                _fillApplied.Remove(si.syncId);
                _nextFillAt.Remove(si.syncId);
            }
            if (needSpec) {
                _nextSpecialAt.Remove(si.syncId);
            }
            ClientTryApplyOrRequest(si);
            sent++;
        }
        if (sent > 0) {
            Bug($"blank-resync touched={sent}");
        }
    }

    public static void EnsureClientEffectsList(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        if (crystal.effects == null) {
            crystal.effects = new List<CrystalEffect>();
        }
    }

    public static void ReceiveState(NetDataReader reader) {
        byte[] payload = reader.GetBytesWithLength();
        if (payload == null || payload.Length < 3) {
            Bug($"recv FILL bad len={(payload == null ? -1 : payload.Length)}");
            return;
        }
        NetDataReader probe = new NetDataReader(payload);
        probe.Get(out knetid syncId);
        probe.Get(out byte flags);
        Bug($"recv FILL sync={syncId} special={(flags & FlagSpecial) != 0} len={payload.Length}");
        _fillBySync[syncId] = payload;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si != null && si.go != null) {
            Store(si.go, syncId, payload, null);
        }
        _nextFillAt.Remove(syncId);
        _fillApplied.Remove(syncId);
        ApplyFill(payload);
    }

    public static void ReceiveSpecials(NetDataReader reader) {
        byte[] payload = reader.GetBytesWithLength();
        if (payload == null || payload.Length < 3) {
            Bug($"recv SPECIALS bad len={(payload == null ? -1 : payload.Length)}");
            return;
        }
        NetDataReader probe = new NetDataReader(payload);
        probe.Get(out knetid syncId);
        probe.Get(out byte count);
        Bug($"recv SPECIALS sync={syncId} effects={count} len={payload.Length}");
        _specialsBySync[syncId] = payload;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si != null && si.go != null) {
            SetSpecial(si.go, true);
            Store(si.go, syncId, null, payload);
        }
        _nextSpecialAt.Remove(syncId);
        _specialsApplied.Remove(syncId);
        ApplySpecials(payload);
    }

    static void ApplyFill(byte[] payload) {
        NetDataReader reader = new NetDataReader(payload);
        reader.Get(out knetid syncId);
        if (!NetObjectRegistry.TryGetSyncInfo(syncId, out var si) || si.go == null) {
            _fillApplied.Remove(syncId);
            Bug($"apply FILL deferred sync={syncId} (no SyncInfo yet)");
            return;
        }
        reader.Get(out byte flags);
        GameObject go = si.go;

        if ((flags & FlagInert) != 0 && reader.AvailableBytes < 8) {
            Bug($"apply FILL inert-stub ignored sync={syncId}");
            return;
        }

        bool special = (flags & FlagSpecial) != 0;
        if (special) {
            SetSpecial(go, true);
        }

        if (!go.TryGetComponent(out CrystalBehaviour crystal)) {
            if (!special) {
                // decorative: color the building sprite/light without resurrecting CB.
                reader.Get(out Color24 sprite24b);
                reader.Get(out byte spriteAb);
                reader.Get(out Color24 light24b);
                reader.Get(out byte lightAb);
                reader.Get(out float intensityB);
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) {
                    Color32 c = sprite24b;
                    c.a = spriteAb;
                    sr.color = c;
                }
                var lightB = go.GetComponent<Light2D>();
                if (lightB != null) {
                    Color32 lc = light24b;
                    lc.a = lightAb;
                    lightB.color = lc;
                    lightB.intensity = intensityB;
                }
                Vector3 scaleB = go.transform.localScale;
                float magB = Mathf.Abs(scaleB.x) < 0.001f ? 1f : Mathf.Abs(scaleB.x);
                scaleB.x = ((flags & FlagFlip) != 0) ? -magB : magB;
                go.transform.localScale = scaleB;
                _fillApplied.Add(syncId);
                Bug($"apply FILL decorative sync={syncId} no CB");
                return;
            }
            crystal = go.AddComponent<CrystalBehaviour>();
            if (crystal == null) {
                return;
            }
            if (crystal.sprite == null) {
                crystal.sprite = go.GetComponent<SpriteRenderer>();
            }
            if (crystal.light == null) {
                crystal.light = go.GetComponent<Light2D>();
            }
            if (crystal.build == null) {
                crystal.build = go.GetComponent<BuildingEntity>();
            }
            EnsureClientEffectsList(crystal);
            if (!go.TryGetComponent(out UsableObject recoveredUsable)) {
                go.AddComponent<UsableObject>();
            }
        }

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

        crystal.crystalSize = crystalSize;
        Vector3 scale = go.transform.localScale;
        float magX = Mathf.Abs(scale.x) < 0.001f ? 1f : Mathf.Abs(scale.x);
        scale.x = ((flags & FlagFlip) != 0) ? -magX : magX;
        go.transform.localScale = scale;

        ApplyingAuthority = true;
        try {
            Color32 sprite32 = sprite24;
            sprite32.a = spriteA;
            Color32 light32 = light24;
            light32.a = lightA;
            ApplyExactColor(crystal, sprite32, light32, intensity, (flags & FlagVolumeLight) != 0, volume, falloff, outerRadius);
            if ((flags & FlagHasHeater) != 0) {
                Heater heater = go.GetComponent<Heater>();
                if (heater == null) {
                    heater = go.AddComponent<Heater>();
                }
                heater.desiredTemp = heaterTemp;
                heater.tempLerpStrength = heaterLerp;
                if (go.GetComponent<HeaterMultiplayerRewrite>() == null) {
                    go.GetOrAddComponent<HeaterMultiplayerRewrite>();
                }
            }
        } finally {
            ApplyingAuthority = false;
        }

        if (!special) {
            if (go.TryGetComponent(out UsableObject bareUsable)) {
                UnityEngine.Object.Destroy(bareUsable);
            }
            UnityEngine.Object.Destroy(crystal);
        } else if (go.TryGetComponent(out UsableObject usable) && usable != null) {
            usable.enabled = true;
        }

        Store(go, syncId, payload, null);
        _fillApplied.Add(syncId);
        Bug($"apply FILL ok sync={syncId} special={special} size={crystalSize}");

        if (!special) {
            CrystalLocalAuthority.MarkLocal(go);
            CrystalLocalAuthority.MarkAuthoritySatisfied(syncId);
        }

        if (special && !_specialsApplied.Contains(syncId)) {
            if (TrySpecials(syncId, PosKeyOf(go), out var spec)) {
                ApplySpecials(spec);
            } else if (!Net.IsServer) {
                MaybeRequestSpecials(syncId);
            }
        }
    }

    static void ApplySpecials(byte[] payload) {
        NetDataReader reader = new NetDataReader(payload);
        reader.Get(out knetid syncId);
        if (!NetObjectRegistry.TryGetSyncInfo(syncId, out var si) || si.go == null) {
            _specialsApplied.Remove(syncId);
            Bug($"apply SPECIALS deferred sync={syncId}");
            return;
        }
        reader.Get(out byte effectCount);
        byte[] effectIds = new byte[effectCount];
        (float cooldown, byte flags)[] effectStates = new (float cooldown, byte flags)[effectCount];
        for (int i = 0; i < effectCount; i++) {
            reader.Get(out effectIds[i]);
            reader.Get(out effectStates[i].cooldown);
            reader.Get(out effectStates[i].flags);
        }

        GameObject go = si.go;
        SetSpecial(go, true);
        if (!go.TryGetComponent(out CrystalBehaviour crystal)) {
            crystal = go.AddComponent<CrystalBehaviour>();
            if (crystal.sprite == null) {
                crystal.sprite = go.GetComponent<SpriteRenderer>();
            }
            if (crystal.light == null) {
                crystal.light = go.GetComponent<Light2D>();
            }
            if (crystal.build == null) {
                crystal.build = go.GetComponent<BuildingEntity>();
            }
            EnsureClientEffectsList(crystal);
            if (!go.TryGetComponent(out UsableObject _)) {
                go.AddComponent<UsableObject>();
            }
        }

        ApplyingAuthority = true;
        try {
            RebuildEffects(crystal, effectIds, effectStates);
        } finally {
            ApplyingAuthority = false;
        }

        var live = GetEffects(crystal);
        if (effectCount <= 0 || live == null || live.Count == 0) {
            _specialsApplied.Remove(syncId);
            Bug($"apply SPECIALS empty sync={syncId} wanted={effectCount} — retry in 1s");
            return;
        }
        if (go.TryGetComponent(out UsableObject usable) && usable != null) {
            usable.enabled = true;
        }
        TryRegisterChunk(crystal);
        Store(go, syncId, null, payload);
        _specialsApplied.Add(syncId);
        CrystalLocalAuthority.MarkLocal(go);
        CrystalLocalAuthority.MarkAuthoritySatisfied(syncId);
        Bug($"apply SPECIALS ok sync={syncId} effects={(live == null ? 0 : live.Count)}");
    }


    static void TryRegisterChunk(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        var effects = GetEffects(crystal);
        if (effects == null || effects.Count == 0 || WorldGeneration.world == null || !WorldGeneration.world.worldExists) {
            return;
        }
        crystal.CheckCurrentChunk(WorldGeneration.world.GetClosestChunkScript(crystal.transform.position));
    }

    static void RebuildEffects(CrystalBehaviour crystal, byte[] effectIds, (float cooldown, byte flags)[] effectStates = null) {
        var existing = GetEffects(crystal);
        EnsureEffectMaps();
        if (!NeedsEffectRebuild(existing, effectIds)) {
            IgnoreEffectChildren(crystal);
            ApplyEffectStates(existing, effectStates);
            return;
        }

        // tear down prior effect visuals (fire / distort / heatwave) before rebuild
        ClearEffectChildren(crystal);

        List<CrystalEffect> effects = new List<CrystalEffect>();
        crystal.effects = effects;

        for (int i = 0; i < effectIds.Length; i++) {
            int id = effectIds[i];
            if (id < 0 || id >= _factories.Length) {
                continue;
            }
            CrystalEffect effect = _factories[id]();
            effect.crystal = crystal;
            effects.Add(effect);
            effect.Start();
            if (effectStates != null && i < effectStates.Length) {
                ApplyOneEffectState(effect, effectStates[i].cooldown, effectStates[i].flags);
            }
        }

        if (effects.Count > 1) {
            bool hasDistort = false;
            for (int c = 0; c < crystal.transform.childCount; c++) {
                if (crystal.transform.GetChild(c).name.Contains("CrystalDistort")) {
                    hasDistort = true;
                    break;
                }
            }
            if (!hasDistort) {
                GameObject obj = Utils.Create("Special/CrystalDistort", crystal.transform);
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localScale = Vector3.one;
                obj.transform.rotation = Quaternion.identity;
                var sr = obj.GetComponent<SpriteRenderer>();
                if (sr != null && sr.material != null) {
                    sr.material.SetFloat("_DistortStrength", effects.Count * 0.015f);
                }
            }
        }

        IgnoreEffectChildren(crystal);

        // match OG description flavor so examine text is sensible
        if (crystal.build != null && effects.Count > 0) {
            string suffix = effects.Count switch {
                1 => " " + Locale.GetOther("crystaleffect1"),
                2 => " " + Locale.GetOther("crystaleffect2"),
                3 => " " + Locale.GetOther("crystaleffect3"),
                4 => " " + Locale.GetOther("crystaleffect4"),
                _ => " " + Locale.GetOther("crystaleffectmany"),
            };
            if (crystal.build.description == null || !crystal.build.description.Contains(suffix.Trim())) {
                crystal.build.description += suffix;
            }
        }
    }

    // crystalFire / CrystalDistort / heatwave are local visual children - client world-clear
    // and far-sync destroy unregistered BuildingEntities, which ate campfires.
    static void IgnoreEffectChildren(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        for (int i = 0; i < crystal.transform.childCount; i++) {
            Transform child = crystal.transform.GetChild(i);
            if (child == null) {
                continue;
            }
            child.gameObject.GetOrAddComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>();
        }
    }

    static void ClearEffectChildren(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        for (int i = crystal.transform.childCount - 1; i >= 0; i--) {
            Transform child = crystal.transform.GetChild(i);
            if (child == null) {
                continue;
            }
            string n = child.name;
            if (n.Contains("CrystalFire") || n.Contains("CrystalDistort") || n.Contains("heatwave")) {
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }
        // stock CrystalTemperature/Burning also add looping AudioSources on the crystal itself
        AudioSource[] audios = crystal.GetComponents<AudioSource>();
        for (int i = 0; i < audios.Length; i++) {
            if (audios[i] != null && audios[i].loop) {
                UnityEngine.Object.Destroy(audios[i]);
            }
        }
        Heater oldHeater = crystal.GetComponent<Heater>();
        if (oldHeater != null) {
            UnityEngine.Object.Destroy(oldHeater);
        }
    }

    static bool NeedsEffectRebuild(List<CrystalEffect> existing, byte[] effectIds) {
        if (effectIds == null || effectIds.Length == 0) {
            return false;
        }
        if (existing == null || existing.Count == 0) {
            return true;
        }
        if (existing.Count != effectIds.Length) {
            return true;
        }
        for (int i = 0; i < effectIds.Length; i++) {
            int id = effectIds[i];
            if (id < 0 || id >= _effectTypes.Length) {
                return true;
            }
            if (existing[i] == null || existing[i].GetType() != _effectTypes[id]) {
                return true;
            }
        }
        return false;
    }

    static void ApplyEffectStates(List<CrystalEffect> effects, (float cooldown, byte flags)[] effectStates) {
        if (effects == null || effectStates == null) {
            return;
        }
        int n = Mathf.Min(effects.Count, effectStates.Length);
        for (int i = 0; i < n; i++) {
            ApplyOneEffectState(effects[i], effectStates[i].cooldown, effectStates[i].flags);
        }
    }

    static void ApplyOneEffectState(CrystalEffect effect, float cooldownOrTimer, byte flags) {
        if (effect == null) {
            return;
        }
        var t = Traverse.Create(effect);
        if (effect is CrystalGravity || (flags & FlagGravityForce) != 0) {
            var force = t.Field("forceMult");
            if (force.FieldExists()) {
                force.SetValue(cooldownOrTimer >= 0f ? 1f : -1f);
            }
        } else {
            var cooldown = t.Field("cooldown");
            if (cooldown.FieldExists()) {
                cooldown.SetValue(cooldownOrTimer);
            } else {
                var timer = t.Field("timer");
                if (timer.FieldExists()) {
                    timer.SetValue(cooldownOrTimer);
                }
            }
        }
        var activated = t.Field("activated");
        if (activated.FieldExists()) {
            activated.SetValue((flags & 1) != 0);
        }
        var timerStarted = t.Field("timerStarted");
        bool wasStarted = timerStarted.FieldExists() && timerStarted.GetValue<bool>();
        if (timerStarted.FieldExists()) {
            bool started = (flags & 2) != 0;
            timerStarted.SetValue(started);
            if (started && !wasStarted && effect is CrystalUnstable unstable) {
                CrystalDelayedFx.OnUnstableArmed(unstable, playTick: true);
            }
        }
        // EMP hides TeslaMini when spent
        if ((flags & 1) != 0 && effect is CrystalEMP) {
            var part = t.Field("part");
            if (part.FieldExists()) {
                var go = part.GetValue<GameObject>();
                if (go != null) {
                    go.SetActive(false);
                }
            }
        }
    }

    static void ApplyExactColor(CrystalBehaviour crystal, Color32 spriteCol, Color32 lightCol, float intensity, bool volumeEnabled, float volume, float falloff, float outerRadius = -1f) {
        if (crystal.sprite != null) {
            crystal.sprite.color = spriteCol;
        }
        if (crystal.light != null) {
            crystal.light.color = lightCol;
            crystal.light.intensity = intensity;
            crystal.light.volumeIntensityEnabled = volumeEnabled;
            if (volumeEnabled) {
                crystal.light.volumeIntensity = volume;
            }
            crystal.light.falloffIntensity = falloff;
            if (outerRadius > 0f) {
                crystal.light.pointLightOuterRadius = outerRadius;
            }
        }
    }

    // push filled states for crystals already in the sync registry (spawn bubble / interest).
    public static void HostPushRegisteredCrystalStates() {
        if (!Net.IsServer) {
            return;
        }
        var who = ServerMain.AllClientIdsExceptHost;
        if (who == null || who.Count == 0) {
            return;
        }
        int n = 0;
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si == null || si.go == null || !LooksLikeCrystal(si.go)) {
                continue;
            }
            HostCapture(si.go);
            ServerSendTo(si.syncId, who);
            n++;
        }
        if (n > 0) {
            Bug($"push registered sent={n}");
        }
    }

    [CTPostInit]
    static void Init() {
        WorldMP.OnWorldgenFinish += () => {
            ClearLayerCaches();
            if (Net.IsServer) {
                foreach (var be in UnityEngine.Object.FindObjectsOfType<BuildingEntity>()) {
                    if (be == null || !LooksLikeCrystal(be.gameObject)) {
                        continue;
                    }
                    MarkGenerationDone(be.gameObject);
                    HostCapture(be.gameObject);
                    if (NetObjectRegistry.TryGetSyncInfo(be.gameObject, out SyncInfo crystalSi)) {
                        NoteCrystal(crystalSi);
                    }
                }
                HostFlushPendingCaptures();
                HostPushRegisteredCrystalStates();
                CrystalLocalAuthority.HostPushRegisteredBags();
                Bug($"worldgen capture cells={_byPos.Count} entries={PosEntryCount()} pending={_pendingCapture.Count}");
            }
            if (Net.IsClient) {
                ClientResyncAllCrystals();
            }
        };
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCrystalState)]
    static void Server_RequestFill(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        Bug($"host recv req FILL sync={syncId} from={clientId}");
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si.go != null
            && !si.go.TryGetComponent(out CrystalBehaviour _)
            && !LooksLikeCrystal(si.go)) {
            return;
        }
        ServerSendTo(syncId, new[] { clientId });
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCrystalSpecials)]
    static void Server_RequestSpecials(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        Bug($"host recv req SPECIALS sync={syncId} from={clientId}");
        if (!NetObjectRegistry.TryGetSyncInfo(syncId, out var si) || si.go == null) {
            if (_specialsBySync.TryGetValue(syncId, out var cached) && cached != null) {
                SendSpecials(cached, new[] { clientId });
            } else {
                Bug($"host SPECIALS miss sync={syncId}");
            }
            return;
        }
        if (!IsSpecial(si.go) && !(si.go.TryGetComponent(out CrystalBehaviour cb) && GetEffects(cb) != null && GetEffects(cb).Count > 0)) {
            Bug($"host SPECIALS refuse sync={syncId} not special");
            return;
        }
        if (CapturePair(si.go, syncId, out _, out var specials) && specials != null) {
            Store(si.go, syncId, null, specials);
            SendSpecials(specials, new[] { clientId });
        } else if (TrySpecials(syncId, PosKeyOf(si.go), out var cached)) {
            SendSpecials(cached, new[] { clientId });
        } else {
            Bug($"host SPECIALS empty sync={syncId}");
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalState)]
    static void Client_ReceiveFill(knetid _, NetDataReader reader) {
        ReceiveState(reader);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalSpecials)]
    static void Client_ReceiveSpecials(knetid _, NetDataReader reader) {
        ReceiveSpecials(reader);
    }

    public static bool LooksLikeCrystalId(string id) {
        if (!string.IsNullOrEmpty(id)) {
            return id.IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        return false;
    }

    static void CollectPendingSyncInfos(List<SyncInfo> needCreate, List<SyncInfo> needCrystalApply) {
        needCreate.Clear();
        needCrystalApply.Clear();
        foreach (var kv in NetObjectRegistry.NetIdToSyncInfoDict) {
            SyncInfo si = kv.Value;
            if (si == null) {
                continue;
            }
            string res = null;
            try {
                res = si.resourceId.Value;
            } catch {
                continue;
            }
            if (string.IsNullOrEmpty(res)) {
                continue;
            }
            if (si.go == null) {
                needCreate.Add(si);
            } else if (LooksLikeCrystalId(res)) {
                needCrystalApply.Add(si);
            }
        }
    }

    static string FormatSyncLoadText(string label, int index, int total) {
        float t = Mathf.Clamp01((float)index / Math.Max(1, total));
        int percent = Mathf.RoundToInt(t * 100f);
        int width = 22;
        int filled = Mathf.Clamp(Mathf.RoundToInt(t * width), 0, width);
        string bar = "[" + new string('#', filled) + new string('-', width - filled) + "]";
        return $"Syncing {label}..\n ({index}/{total})\n{bar} {percent}%\n\n \n ";
    }

    public static IEnumerator ClientLoadingScreenStep(WorldGeneration world) {
        if (!Net.IsClient || world == null) {
            yield break;
        }
        List<SyncInfo> needCreate = new List<SyncInfo>(512);
        List<SyncInfo> needCrystalApply = new List<SyncInfo>(256);
        CollectPendingSyncInfos(needCreate, needCrystalApply);
        int total = needCreate.Count + needCrystalApply.Count;
        if (total == 0 || world.loadingObject == null || !world.loadingObject.activeSelf) {
            yield break;
        }
        _fillApplied.Clear();
        _specialsApplied.Clear();
        _nextFillAt.Clear();
        _nextSpecialAt.Clear();
        LoadStep.Begin();
        try {
            int done = 0;
            for (int i = 0; i < needCreate.Count; i++) {
                SyncInfo si = needCreate[i];
                if (si == null || si.go != null) {
                    done++;
                    continue;
                }
                world.SetLoadingTextNoLocale(FormatSyncLoadText("world objects", done + 1, total));
                LoadStep.GrantOne();
                try {
                    si.CreateObject();
                } catch {
                }
                if (si.go != null && LooksLikeCrystal(si.go)) {
                    ClientTryApplyOrRequest(si);
                }
                done++;
                yield return null;
            }
            for (int i = 0; i < needCrystalApply.Count; i++) {
                SyncInfo si = needCrystalApply[i];
                if (si == null || si.go == null) {
                    done++;
                    continue;
                }
                world.SetLoadingTextNoLocale(FormatSyncLoadText("crystals", done + 1, total));
                ClientTryApplyOrRequest(si);
                done++;
                yield return null;
            }
        } finally {
            LoadStep.End();
        }
    }

    // client hollow WorldGenerateTerrain Instantiates hundreds of crystal prefabs that
    // finishWorldGen_ClientClearWorldObjects then wipes - host SyncInfos remint them in LoadStep.
    public static class LoadStep {
        static bool _active;
        static int _grantFrame = -1;

        public static bool Active => _active;

        public static void Begin() {
            _active = true;
            _grantFrame = -1;
        }

        public static void End() {
            _active = false;
            _grantFrame = -1;
        }

        public static void GrantOne() {
            _grantFrame = Time.frameCount;
        }

        public static bool TryAllowCreate() {
            if (!_active || _grantFrame != Time.frameCount) {
                return false;
            }
            _grantFrame = -1;
            return true;
        }
    }

    public static class SyncBudget {
        public const int HostRegisterPerStamp = 8;
        public const int ClientCreatePerFrame = 4;
        public const int HostPushPerFrame = 8;
        static int _createFrame = -1;
        static int _createsThisFrame;

        public static bool TryConsumeClientCreate() {
            int frame = Time.frameCount;
            if (frame != _createFrame) {
                _createFrame = frame;
                _createsThisFrame = 0;
            }
            if (_createsThisFrame >= ClientCreatePerFrame) {
                return false;
            }
            _createsThisFrame++;
            return true;
        }
    }
    public static class ClientGenThrottle {
        public const string CrystalStubName = "~mp-crystal-skip";
        // true only while WorldGenerateTerrain MoveNext runs - WorldPlacePlayer / Finish wait need LES.
        public static bool TerrainGenActive;

        static GameObject _crystalStub;
        static CrystalBehaviour _crystalStubBehaviour;
        static int _crystalSkips;

        public static bool IsCrystalStub(CrystalBehaviour c) {
            return c != null && c.gameObject != null && c.gameObject.name == CrystalStubName;
        }

        public static bool TrySkipCrystalCreate(string id, ref GameObject result) {
            if (!Net.IsRunning || !Net.IsClient) {
                return false;
            }
            if (!SharedMain.LocalWorldIsGenerating && !Util.IsGeneratingWorld()) {
                return false;
            }
            if (!LooksLikeCrystalId(id) || string.Equals(id, "Special/CrystalDistort", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            if (_crystalStub == null) {
                _crystalStub = new GameObject(CrystalStubName);
                _crystalStub.hideFlags = HideFlags.HideAndDontSave;
                _crystalStub.SetActive(false);
                _crystalStubBehaviour = _crystalStub.AddComponent<CrystalBehaviour>();
            }
            result = _crystalStub;
            _crystalSkips++;
            return true;
        }

        public static void NoteTerrainBegin(int depth) {
            TerrainGenActive = true;
            _crystalSkips = 0;
        }

        public static void NoteTerrainEnd(int depth) {
            TerrainGenActive = false;
            _crystalSkips = 0;
        }
    }
}

// vanilla SetColor multiplies RGB by Random(0.9..1) and randomizes intensity.
// on clients that re-rolls every effect.Start / trigger and fights authority colors.
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.SetColor))]
static class CrystalBehaviour_SetColor_Exact_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, Color clr) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient || CrystalAuthority.ApplyingAuthority) {
            if (__instance.sprite != null) {
                __instance.sprite.color = clr;
            }
            if (__instance.light != null) {
                __instance.light.color = clr;
                // keep existing intensity on client; authority ApplyExactColor owns the final value.
            }
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.GenerationFinished))]
static class CrystalBehaviour_GenerationFinished_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, ref GameObject __state) {
        __state = __instance != null ? __instance.gameObject : null;
        if (!Net.IsRunning || !Net.IsClient) {
            return true;
        }
        // shared stub from ClientGenThrottle - no GetBlock / Destroy work
        if (CrystalAuthority.ClientGenThrottle.IsCrystalStub(__instance)) {
            return false;
        }

        // clients wipe unregistered worldgen buildings anyway; skip local rolls
        if (WorldGeneration.world != null
            && WorldGeneration.world.GetBlock(__instance.transform.position - __instance.transform.up * 2f) == 0) {
            UnityEngine.Object.Destroy(__instance.gameObject);
            Collider2D col = __instance.GetComponent<Collider2D>();
            if (col != null) {
                col.enabled = false;
            }
        }
        return false;
    }

    static void Postfix(CrystalBehaviour __instance, GameObject __state) {
        if (Net.IsRunning && Net.IsClient
            && __instance != null
            && !CrystalAuthority.ClientGenThrottle.IsCrystalStub(__instance)) {
            CrystalAuthority.EnsureClientEffectsList(__instance);
        }
        if (!Net.IsRunning || !Net.IsServer || __state == null) {
            return;
        }
        CrystalAuthority.MarkGenerationDone(__state);
        // store host roll in pos cache; clients pull on shell spawn (mutations still broadcast).
        CrystalAuthority.HostCapture(__state);
        CrystalLocalAuthority.HostCaptureAndBroadcastBag(__state);
    }
}

// hollow terrain: Utils.Create(crystal*) on clients -> stub (host remints via SyncInfo LoadStep)
[HarmonyPatch(typeof(Utils), nameof(Utils.Create), new Type[] { typeof(string), typeof(Vector2), typeof(float) })]
static class Utils_Create_SkipClientCrystalDuringGen_MultiplayerPatch {
    static bool Prefix(string id, ref GameObject __result) {
        return !CrystalAuthority.ClientGenThrottle.TrySkipCrystalCreate(id, ref __result);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleBuildingHover))]
static class Crystal_HandleBuildingHover_ExamineGate_MultiplayerPatch {
    static void Postfix(BuildingEntity build, ref UsableObject usableObject) {
        if (!Net.IsRunning || !build.TryGetComponent(out CrystalBehaviour crystal)) {
            return;
        }
        var effects = CrystalAuthority.GetEffects(crystal);
        if (effects == null || effects.Count == 0) {
            // local-auth crystals may still be mid-apply; don't soft-lock examine forever on marked GOs with pending bag
            if (!CrystalLocalAuthority.IsLocalAuth(build.gameObject)) {
                usableObject = null;
                if (PlayerCamera.main != null) {
                    var cursorField = Traverse.Create(PlayerCamera.main).Field("cursor");
                    if (cursorField.GetValue<int>() == 4) {
                        cursorField.SetValue(2);
                    }
                }
                return;
            }
            return;
        }
        // effects known - keep examine available (listen-host path used to leave UsableObject disabled)
        if (build.TryGetComponent(out UsableObject uo) && uo != null) {
            uo.enabled = true;
            usableObject = uo;
        }
    }
}

// intentionally no UpdateBuilding hook - LES calls that every sync tick for every building
// and crystal LooksLike/GetComponent work tanked client FPS while moving.

[HarmonyPatch(typeof(SyncInfo), nameof(SyncInfo.CreateObject))]
static class SyncInfo_CreateObject_CrystalAuthority_MultiplayerPatch {
    static bool _meshAlreadyThere;

    static bool Prefix(SyncInfo __instance, ref bool __result) {
        _meshAlreadyThere = __instance != null && __instance.go != null;
        if (!Net.IsRunning || !Net.IsClient || __instance == null) {
            return true;
        }
        if (_meshAlreadyThere) {
            return true;
        }
        if (SharedMain.LocalWorldIsGenerating || Util.IsGeneratingWorld()) {
            if (!CrystalAuthority.LoadStep.TryAllowCreate()) {
                __result = false;
                return false;
            }
            return true;
        }
        string res = null;
        try {
            res = __instance.resourceId.Value;
        } catch {
            return true;
        }
        if (CrystalAuthority.LooksLikeCrystalId(res) && !CrystalAuthority.SyncBudget.TryConsumeClientCreate()) {
            __result = false;
            return false;
        }
        return true;
    }

    static void Postfix(SyncInfo __instance, bool __result) {
        if (_meshAlreadyThere) {
            return;
        }
        if (!__result || !Net.IsRunning || !Net.IsClient || __instance == null || __instance.go == null) {
            return;
        }
        // LES calls CreateObject every client Update once go exists - never ForceReapply here
        // or _applied/_request throttles reset every frame (spawn-bubble white + FPS death).
        if (__instance.go.TryGetComponent(out CrystalBehaviour _)
            || CrystalAuthority.LooksLikeCrystal(__instance.go)) {
            // CS/Together local crystal already at this pos -> discard network shell, rebind syncId.
            if (CrystalLocalAuthority.TryDiscardReplica(__instance)) {
                return;
            }
            if (CrystalLocalAuthority.TryApplyCachedBag(__instance)) {
                return;
            }
            CrystalAuthority.ClientTryApplyOrRequest(__instance);
        }
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.OnCollisionEnter2D))]
static class CrystalBehaviour_OnCollisionEnter2D_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, Collision2D collision) {
        if (!Net.IsRunning) return true;

        if (SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(__instance.transform.position)) {
            BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
            AnyDataContainerComponent data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
            bool is_body_hit = Util.TryGetBodyFromObject(collision.gameObject, out Body body);
            if (is_body_hit) {
                bgo.body = body;
            } else {
                body = null;
                bgo.body = null;
            }
            data["CrystalCollision"] = collision;
            data["CrystalCollisionIsBody"] = is_body_hit;
            CrystalLocalFx.Begin(body, __instance);
            try {
            foreach (CrystalEffect effect in __instance.effects) {
                effect.Touched(collision.gameObject);
                }
            } finally {
                CrystalLocalFx.End();
            }
        }

        return false;
    }
}
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.BuildingHit))]
static class CrystalBehaviour_BuildingHit_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, AttackInfo atk) {
        if (!Net.IsRunning) return true;

        if (SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(__instance.transform.position)) {
            BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
            AnyDataContainerComponent data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
            Body attacker = Body_Attack_MultiplayerPatch.LastAttacker;
            if (attacker != null) {
                bgo.body = attacker;
                data["CrystalBuildingHitAttacker"] = attacker;
            } else {
                bgo.body = null;
            }
            data["CrystalBuildingHitAtk"] = atk;
            CrystalLocalFx.Begin(attacker, __instance);
            try {
            foreach (CrystalEffect effect in __instance.effects) {
                effect.Hit(atk);
                }
            } finally {
                CrystalLocalFx.End();
            }
        }

        return false;
    }
}

// fullgame examine appends effect names onto build.description and destroys UsableObject.
// keep UsableObject until examined (or confirmed inert) so peers can still examine specials.
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.OnUse))]
static class CrystalBehaviour_OnUse_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        List<CrystalEffect> effects = CrystalAuthority.GetEffects(__instance);
        if (effects == null || effects.Count == 0) {
            // not synced yet - keep UsableObject so examine works once authority arrives
            return false;
        }

        AnyDataContainerComponent data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
        if (!data.WasntTouched("CrystalExaminedLocal")) {
            return false;
        }

        BuildingEntity buildingEntity = __instance.build;
        buildingEntity.description = buildingEntity.description + "\n\n" + ((effects.Count == 1) ? Locale.GetBuilding("crystalproperty") : Locale.GetBuilding("crystalproperties"));
        List<string> names = new List<string>();
        for (int i = 0; i < effects.Count; i++) {
            names.Add(Locale.GetBuilding(effects[i].GetType().Name.ToLower()));
        }
        __instance.build.description += string.Join(", ", names);
        if (__instance.TryGetComponent(out UsableObject uo)) {
            UnityEngine.Object.Destroy(uo);
        }
        return false;
    }
}

static class CrystalMpGates {
    internal static bool HostSimOnly(CrystalEffect effect) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            return false;
        }
        if (effect == null || effect.crystal == null) {
            return false;
        }
        return SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(effect.crystal.transform.position);
    }

    internal static bool IsActivated(CrystalEffect effect) {
        if (effect == null) {
            return false;
        }
        var activated = Traverse.Create(effect).Field("activated");
        if (activated.FieldExists()) {
            return activated.GetValue<bool>();
        }
        return false;
    }

    internal static void MarkActivated(CrystalEffect effect) {
        if (effect == null) {
            return;
        }
        var activated = Traverse.Create(effect).Field("activated");
        if (activated.FieldExists()) {
            activated.SetValue(true);
        }
    }

    internal static bool SkipOneShotFx(CrystalEffect effect) {
        return IsActivated(effect);
    }
}

internal static class CrystalDelayedFx {
    internal static void OnUnstableArmed(CrystalUnstable unstable, bool playTick) {
        if (unstable == null || unstable.crystal == null) {
            return;
        }
        Traverse t = Traverse.Create(unstable);
        var origPos = t.Field("origPos");
        if (origPos.FieldExists()) {
            Vector2 value = origPos.GetValue<Vector2>();
            if (value.sqrMagnitude < 0.0001f) {
                origPos.SetValue((Vector2)unstable.crystal.transform.position);
            }
        }
        var timerStarted = t.Field("timerStarted");
        if (timerStarted.FieldExists()) {
            timerStarted.SetValue(true);
        }
        if (playTick) {
            Sound.Play("crystaltick", unstable.crystal.transform.position, twoDimensional: true, pitchShift: false, unstable.crystal.transform, 1f, 1f, false, false);
        }
    }

    static readonly HashSet<int> _armBroadcast = new HashSet<int>();

    internal static void ClearArmBroadcasts() {
        _armBroadcast.Clear();
    }

    internal static void HostBroadcastUnstableArm(CrystalUnstable unstable) {
        if (!Net.IsServer || unstable == null || unstable.crystal == null) {
            return;
        }
        var timerStarted = Traverse.Create(unstable).Field("timerStarted");
        if (timerStarted.FieldExists() && timerStarted.GetValue<bool>() && _armBroadcast.Add(unstable.crystal.GetInstanceID())) {
            CrystalAuthority.HostCaptureAndBroadcast(unstable.crystal.gameObject);
        }
    }
}

[HarmonyPatch(typeof(CrystalBurning), nameof(CrystalBurning.Burn))]
internal static class CrystalBurning_Burn_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }
    static bool Prefix(CrystalBurning __instance, ref Body body) {
        if (!Net.IsRunning || __instance.crystal == null) return true;

        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body != null) {
            body = bgo.body;
        }

        return true;
    }
}

[HarmonyPatch(typeof(CrystalEMP), nameof(CrystalEMP.TryEMP))]
internal static class CrystalEMP_TryEMP_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalEMP __instance) {
        if (!Net.IsRunning || __instance.crystal == null) return true;
        if (CrystalMpGates.SkipOneShotFx(__instance)) return false;

        // battery drain / shake / 2D emp sting must target the trigger body only.
        // without an explicit trigger, skip so we never drain PlayerCamera.main via GetBody fallback.
        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        Body trigger = CrystalLocalFx.TriggerBody ?? bgo.body;
        if (trigger == null) {
            return false;
        }
        bgo.body = trigger;
        return true;
    }

    static void Postfix(CrystalEMP __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.crystal == null) {
            return;
        }
        // setColor(white) + activated + hidden Tesla must rebroadcast or clients keep old colors.
        CrystalAuthority.HostCaptureAndBroadcast(__instance.crystal.gameObject);
    }
}

[HarmonyPatch(typeof(CrystalElectric), nameof(CrystalElectric.Shock))]
internal static class CrystalElectric_Shock_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalElectric __instance, ref Body body) {
        if (!Net.IsRunning || __instance.crystal == null) return true;

        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body != null) {
            body = bgo.body;
        }

        return true;
    }
}



[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.body), MethodType.Getter)]
static class CrystalEnemy_get_body_MultiplayerPatch {

    static void Postfix(CrystalEnemy __instance, ref Body __result) {
        if (!Net.IsRunning) return;

        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body == null) {
            bgo.SetNearestBody();
            if (bgo.body == null) {
                bgo.body = PlayerCamera.main.body;
            }
        }
        __result = bgo.body;

    }
}


[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Update))]
internal static class CrystalEnemy_Update_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalEnemy __instance) {
        if (!Net.IsRunning) return true;
        // host owns AI + pose (LES). Clients keep threat music only when the enemy is near them.
        if (Net.IsClient) {
            if (PlayerCamera.main != null
                && Util.TryGetLocalBody(out Body local)
                && Vector2.Distance(__instance.transform.position, local.transform.position) < 64f) {
                PlayerCamera.main.currentThreatTheme = 18;
                if (WorldGeneration.world != null
                    && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial) {
                    PlayerCamera.main.currentThreatTheme = 17;
                }
                PlayerCamera.main.threatMusicTime = 2f;
            }
            ClientHum(__instance);
            ClientStreak(__instance);
            return false;
        }

        var bgu = __instance.GetComponent<BodyGetterUpdater>();
        if (bgu == null) {
            bgu = __instance.AddComponent<BodyGetterUpdater>();
            bgu.cooldown = 5f;
            bgu.condition = (g) => { 
                if (g.TryGetComponent(out CrystalEnemy ce)) {
                    if (ce.close) {
                        List<RaycastHit2D> rh = new();
                        var result = Physics2D.Linecast(ce.body.GetLowerTorso().transform.position, ce.transform.position, new ContactFilter2D() { layerMask = LayerMask.GetMask("Ground") }, rh);
                        if (result > 0) {
                            rh.RemoveAll((x) => {
                                return x.transform == null || Util.TryGetBodyFromObject(x.transform, out var _) || ce.transform == x.transform;
                            });
                            result = rh.Count;
                        }
                        return result > 0;// something is blocking the view, pick another player
                    }
                    return true;
                }
                return true; 
            };
        }

        return true;
    }

    // vanilla sets threat theme when manifestation is close to its AI body; BodyGetterOverrider retargets that
    // body per-peer so remote targets must not drive local fight music.
    static void Postfix(CrystalEnemy __instance) {
        if (!Net.IsRunning || !Net.IsServer || PlayerCamera.main == null) {
            return;
        }
        if (!Util.TryGetLocalBody(out Body local)) {
            return;
        }
        if (Vector2.Distance(__instance.transform.position, local.transform.position) < 64f) {
            return;
        }
        var bgo = __instance.GetComponent<BodyGetterOverrider>();
        Body target = bgo != null ? bgo.body : null;
        if (target == null || Util.IsBodyLocal(target)) {
            return;
        }
        if (Vector2.Distance(__instance.transform.position, target.transform.position) >= 64f) {
            return;
        }
        if (PlayerCamera.main.currentThreatTheme == 17 || PlayerCamera.main.currentThreatTheme == 18) {
            PlayerCamera.main.currentThreatTheme = 0;
            PlayerCamera.main.threatMusicTime = 0f;
        }
    }

    // the looping source only changes pitch and volume inside Update. clients skip the AI, so this is the hum.
    static void ClientHum(CrystalEnemy enemy) {
        Traverse fields = Traverse.Create(enemy);
        AudioSource source = fields.Field("source").GetValue<AudioSource>();
        Rigidbody2D rb = fields.Field("rb").GetValue<Rigidbody2D>();
        BuildingEntity build = fields.Field("build").GetValue<BuildingEntity>();
        if (source == null || rb == null) {
            return;
        }
        bool stuck = fields.Field("stuck").GetValue<bool>();
        float timeBeforeAttack = fields.Field("timeBeforeAttack").GetValue<float>();
        float hurt = build != null ? 1f - build.GetHealth() / 450f : 0f;
        float wind = Mathf.Clamp01(-timeBeforeAttack) * (stuck ? 0f : 1f);
        float speed = rb.velocity.magnitude * 0.005f;
        float pitch = 1f + speed + wind + hurt;
        float volume = Mathf.Clamp01(0.3f + speed) + wind;
        source.pitch = Mathf.Lerp(source.pitch, pitch, Time.deltaTime * 8f);
        source.volume = Mathf.Lerp(source.volume, volume, Time.deltaTime * 8f);
        if (!source.isPlaying) {
            source.loop = true;
            source.Play();
        }
    }

    struct Streak {
        public bool stuck;
        public bool aimed;
        public Vector3 from;
        public Vector3 to;
    }

    static readonly Dictionary<int, Streak> streaks = new Dictionary<int, Streak>();

    // host draws this inside Update, which clients skip. windup aims the line, the lunge leaves it behind.
    static void ClientStreak(CrystalEnemy enemy) {
        Traverse fields = Traverse.Create(enemy);
        LineRenderer rend = fields.Field("rend").GetValue<LineRenderer>();
        SpriteRenderer sprite = fields.Field("sprite").GetValue<SpriteRenderer>();
        if (rend == null || sprite == null) {
            return;
        }
        bool stuck = fields.Field("stuck").GetValue<bool>();
        float timeBeforeAttack = fields.Field("timeBeforeAttack").GetValue<float>();
        int id = enemy.GetInstanceID();
        if (!streaks.TryGetValue(id, out Streak aim)) {
            aim = new Streak();
        }
        if (!stuck) {
            aim.from = enemy.transform.position;
            if (timeBeforeAttack < 0f) {
                Vector3 from = enemy.transform.position;
                RaycastHit2D hit = Physics2D.Raycast(from, enemy.transform.up, 999f, LayerMask.GetMask("Ground"));
                Vector3 to = hit.collider != null ? (Vector3)hit.point : from + enemy.transform.up * 999f;
                rend.SetPosition(0, from);
                rend.SetPosition(1, to);
                rend.startColor = Color.Lerp(Color.clear, sprite.color, -timeBeforeAttack * 2f);
                rend.widthMultiplier = timeBeforeAttack * 2f;
                aim.aimed = true;
                aim.to = to;
            } else {
                rend.endColor = Color.clear;
                rend.startColor = Color.clear;
                aim.aimed = false;
            }
        } else {
            if (!aim.stuck) {
                if (!aim.aimed) {
                    Vector3 from = aim.from != Vector3.zero ? aim.from : enemy.transform.position;
                    RaycastHit2D hit = Physics2D.Raycast(from, enemy.transform.up, 999f, LayerMask.GetMask("Ground"));
                    aim.from = from;
                    aim.to = hit.collider != null ? (Vector3)hit.point : enemy.transform.position;
                }
                rend.SetPosition(0, aim.from);
                rend.SetPosition(1, aim.to);
                rend.endColor = sprite.color;
                rend.startColor = Color.clear;
            }
            rend.endColor = Color.Lerp(sprite.color, Color.clear, -timeBeforeAttack);
        }
        aim.stuck = stuck;
        streaks[id] = aim;
        if (streaks.Count > 64) {
            streaks.Clear();
            streaks[id] = aim;
        }
    }
}

[HarmonyPatch(typeof(CrystalEnemy), "FixedUpdate")]
static class CrystalEnemy_FixedUpdate_HostSim_MultiplayerPatch {
    static bool Prefix() => !Net.IsRunning || Net.IsServer;
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Lunge))]
static class CrystalEnemy_Lunge_VictimAuth_MultiplayerPatch {
    static bool Prefix(CrystalEnemy __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        // host executes lunge motion; damage is victim-authoritative via TrapClaim.
        if (!Net.IsServer) {
            return false;
        }

        var stuck = Traverse.Create(__instance).Field("stuck");
        var didAttackWarning = Traverse.Create(__instance).Field("didAttackWarning");
        var rend = Traverse.Create(__instance).Field("rend").GetValue<LineRenderer>();
        var rb = Traverse.Create(__instance).Field("rb").GetValue<Rigidbody2D>();
        var build = Traverse.Create(__instance).Field("build").GetValue<BuildingEntity>();

        stuck.SetValue(true);
        didAttackWarning.SetValue(false);
        if (rend != null) {
            rend.endColor = rend.startColor;
            rend.startColor = Color.clear;
        }

        bool hitSomeone = false;
        knetid victim = default;
        RaycastHit2D[] hits = Physics2D.RaycastAll(__instance.transform.position, __instance.transform.up);
        for (int i = 0; i < hits.Length; i++) {
            RaycastHit2D hit = hits[i];
            if (hit.transform == null) {
                continue;
            }
            if (Utils.GetBody(hit.transform.gameObject, out Body body) && !hitSomeone) {
                hitSomeone = true;
                if (body.TryGetNetBody(out NetBody nb)) {
                    victim = nb.netId;
                }
                if (TrapClaim.ManifestMayHurt(__instance, body)) {
                    TrapClaim.ManifestHurtLocal(__instance, body);
                }
            }
            if (hit.transform.gameObject.layer == 6) {
                __instance.transform.position = hit.point;
                if (rb != null) {
                    rb.MovePosition(hit.point);
                    rb.velocity = Vector2.zero;
                    rb.constraints = RigidbodyConstraints2D.FreezeAll;
                }
                Sound.Play("crystalenemylodge", __instance.transform.position, twoDimensional: false, pitchShift: false);
                break;
            }
        }

        TrapClaim.HostManifestLunged(__instance, victim);
        return false;
    }
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Awake))]
static class CrystalEnemy_Awake_MultiplayerPatch {
    static void Postfix(CrystalEnemy __instance) {
        if (!Net.IsRunning) return;
        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody(); // just pick a nearest body
    }
}



[HarmonyPatch(typeof(PlayerCamera), "FlashBrief")]
static class PlayerCamera_FlashBrief_CrystalLocal_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) {
            return true;
        }
        // a remote touch still runs the crystal's own flash on whatever machine simulated the hit.
        // the coroutine often starts the frame after Touched, so the block has to outlive End().
        if (Time.frameCount <= CrystalLocalFx.AllowFlashFrame) {
            return true;
        }
        if (Time.frameCount <= CrystalLocalFx.BlockFlashFrame) {
            return false;
        }
        return !CrystalLocalFx.SuppressNonLocalFeedback();
    }
}

[HarmonyPatch(typeof(CrystalHealing), nameof(CrystalHealing.Touched))]
internal static class CrystalHealing_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        instructions = TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null);

        return instructions;
    }

    static bool Prefix(CrystalHealing __instance, GameObject touched) {
        if (!Net.IsRunning) return true;
        // vanilla FlashBrief always hits PlayerCamera.main. running the original on the host
        // white-screens the host when a client is the one who touched it.
        if (CrystalMpGates.SkipOneShotFx(__instance) || CrystalLooksSpent(__instance)) return false;
        if (__instance.crystal == null || !Utils.GetBody(touched, out Body body) || body == null || !body.alive) {
            return false;
        }
        // a spent crystal stays spent on every machine. only the camera body that touched a live one gets the flash.
        CrystalMpGates.MarkActivated(__instance);
        bool mine = PlayerCamera.main != null && body == PlayerCamera.main.body;
        if (mine) {
            PlayerCamera.main.StartCoroutine("FlashBrief");
            Sound.Play("crystalheal", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false);
            if (!Net.IsServer && NetObjectRegistry.TryGetSyncInfo(__instance.crystal.gameObject, out SyncInfo si) && si != null) {
                NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
                writer.Put("healing_touch " + si.syncId.id.ToString());
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }
        if (Net.IsServer) {
            ApplyHeal(body);
            __instance.crystal.SetColor(Color.black);
            if (body.TryGetNetBody(out NetBody nb) && nb != null) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
            CrystalAuthority.HostCaptureAndBroadcast(__instance.crystal.gameObject);
        }
        return false;
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict["healing_touch"] = (plr, cmd, args) => {
            if (!Net.IsServer || plr == null || plr.body == null || args == null || args.Length < 2) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort syncId)) {
                return;
            }
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
                return;
            }
            if (!si.go.TryGetComponent(out CrystalBehaviour crystal) || crystal.effects == null) {
                return;
            }
            if (Vector2.Distance(plr.body.transform.position, crystal.transform.position) > 8f) {
                return;
            }
            for (int i = 0; i < crystal.effects.Count; i++) {
                if (crystal.effects[i] is not CrystalHealing healing) {
                    continue;
                }
                if (CrystalMpGates.SkipOneShotFx(healing) || CrystalLooksSpent(healing)) {
                    return;
                }
                if (!plr.body.alive) {
                    return;
                }
                ApplyHeal(plr.body);
                if (healing.crystal != null) {
                    healing.crystal.SetColor(Color.black);
                }
                CrystalMpGates.MarkActivated(healing);
                if (plr.body.TryGetNetBody(out NetBody nb) && nb != null) {
                    MedicalSync.Server_QueueSendCharacterHealth(nb, true);
                }
                CrystalAuthority.HostCaptureAndBroadcast(crystal.gameObject);
                return;
            }
        };
    }

    static bool CrystalLooksSpent(CrystalHealing healing) {
        if (healing == null || healing.crystal == null || healing.crystal.sprite == null) {
            return false;
        }
        Color c = healing.crystal.sprite.color;
        return c.r < 0.08f && c.g < 0.08f && c.b < 0.08f;
    }

    static void ApplyHeal(Body body) {
        if (body.talker != null) {
            body.talker.Talk(Locale.GetCharacter("magicheal"));
        }
        body.brainHealth = Mathf.Lerp(body.brainHealth, 100f, 0.1f);
        if (body.limbs != null) {
            for (int i = 0; i < body.limbs.Length; i++) {
                Limb limb = body.limbs[i];
                if (limb == null) {
                    continue;
                }
                limb.bleedAmount -= 10f;
                limb.muscleHealth += 25f;
                limb.skinHealth += 25f;
                limb.infectionAmount -= 40f;
                limb.pain = 0f;
            }
        }
        body.happiness += 2f;
        body.internalBleeding *= 0.5f;
        body.septicShock *= 0.5f;
        body.sicknessAmount *= 0.66f;
        body.strokeAmount *= 0.1f;
        body.temperature = 37f;
        body.thirst = Mathf.Lerp(body.thirst, 125f, 0.25f);
        body.hunger = Mathf.Lerp(body.hunger, 125f, 0.25f);
        body.radiationSickness *= 0.75f;
        body.fibrillationProgress = 0f;
        body.bloodVolume = Mathf.Lerp(body.bloodVolume, 100f, 0.1f);
    }
}

// playtest ask: dead players within 10u of a healing crystal get a forced last stand; crystal is consumed.
[HarmonyPatch(typeof(CrystalHealing), nameof(CrystalHealing.Update))]
internal static class CrystalHealing_Update_DeadLastStand_MultiplayerPatch {
    const float ReviveRange = 10f;

    static void Postfix(CrystalHealing __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.crystal == null) {
            return;
        }
        if (Traverse.Create(__instance).Field("activated").GetValue<bool>()) {
            return;
        }
        if (!Multiplayer.rules.LastStandAllowed) {
            return;
        }

        Vector2 crystalPos = __instance.crystal.transform.position;
        Body revive = null;
        float best = ReviveRange;
        foreach (var plr in ScavPlayer.AllDeadPlayers) {
            if (plr == null || plr.body == null) {
                continue;
            }
            Body body = plr.body;
            if (body.alive || body.succesfullyRolledLastStand || body.triedRollingLastStand) {
                continue;
            }
            float dist = Vector2.Distance(body.transform.position, crystalPos);
            if (dist <= best) {
                best = dist;
                revive = body;
            }
        }
        if (revive == null) {
            return;
        }

        Traverse.Create(__instance).Field("activated").SetValue(true);
        __instance.crystal.SetColor(Color.black);
        Sound.Play("crystalheal", __instance.crystal.transform.position, twoDimensional: false, pitchShift: false);

        CrystalLocalFx.Begin(revive, __instance.crystal);
        try {
            revive.ForceLastStand();
        } finally {
            CrystalLocalFx.End();
        }

        if (__instance.crystal.build != null) {
            __instance.crystal.build.SetHealth(0f);
        }
        CrystalAuthority.HostCaptureAndBroadcast(__instance.crystal.gameObject);
    }
}


[HarmonyPatch(typeof(CrystalKinetic), nameof(CrystalKinetic.Touched))]
internal static class CrystalKinetic_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalKinetic __instance, GameObject touched) {
        var crystal = __instance.crystal;
        if (!Net.IsRunning || crystal == null) return true;
        // velocity kick is host/sim for the touched body; shake is already local-gated via PatchShakers.
        // still skip remote peers re-running SetVelocity on a body they don't own.
        if (Net.IsServer) return true;
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body)) {
            // client-owned body: allow original so local prediction feels responsive.
        return true;
        }
        return false;
    }
}



[HarmonyPatch(typeof(CrystalMetamorphic), nameof(CrystalMetamorphic.Touched))]
internal static class CrystalMetamorphic_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        instructions = TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null);

        return instructions;
    }

    static bool Prefix(CrystalMetamorphic __instance, GameObject touched) {
        if (!Net.IsRunning) return true;
        // getRandomItem drops + health kill must be host-authored or clients forge duplicate loot.
        // host runs the original (FlashBrief / laugh gated to local trigger). Clients only play local FX.
        if (CrystalMpGates.HostSimOnly(__instance)) return true;
        if (CrystalMpGates.SkipOneShotFx(__instance)) return false;
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body) && __instance.crystal != null) {
            PlayerCamera.main.StartCoroutine("FlashBrief");
            Sound.Play("crystalenemylaugh", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

// mimic spawns CrystalEnemy buildings; only the host may Create them so Start net-registers once.
// local triggerer still gets the observerlaugh sting on their own client.
[HarmonyPatch(typeof(CrystalMimic), nameof(CrystalMimic.Touched))]
internal static class CrystalMimic_Touched_MultiplayerPatch {
    static bool Prefix(CrystalMimic __instance, GameObject touched) {
        if (!Net.IsRunning) return true;
        if (CrystalMpGates.HostSimOnly(__instance)) return true;
        if (CrystalMpGates.SkipOneShotFx(__instance)) return false;
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body)) {
            Sound.Play("observerlaugh", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalMimic), nameof(CrystalMimic.Hit))]
internal static class CrystalMimic_Hit_MultiplayerPatch {
    static bool Prefix(CrystalMimic __instance, AttackInfo atk) {
        if (!Net.IsRunning) return true;
        if (CrystalMpGates.HostSimOnly(__instance)) return true;
        if (CrystalMpGates.SkipOneShotFx(__instance)) return false;
        Body attacker = CrystalLocalFx.TriggerBody ?? Body_Attack_MultiplayerPatch.LastAttacker;
        if (attacker != null && Util.IsBodyLocal(attacker)) {
            Sound.Play("observerlaugh", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

// shy swaps world positions with a nearby crystal; host-only so building transforms stay authoritative.
[HarmonyPatch(typeof(CrystalShy), nameof(CrystalShy.Touched))]
internal static class CrystalShy_Touched_MultiplayerPatch {
    static bool Prefix(CrystalShy __instance, GameObject touched) {
        if (!Net.IsRunning) return true;
        if (CrystalMpGates.HostSimOnly(__instance)) return true;
        if (CrystalMpGates.SkipOneShotFx(__instance)) return false;
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body) && __instance.crystal != null) {
            Sound.Play("observerlaugh", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalFragile), nameof(CrystalFragile.Touched))]
internal static class CrystalFragile_Touched_MultiplayerPatch {
    static readonly HashSet<int> broke = new HashSet<int>();
    static bool ran;

    // health=0 goes through the fullgame property setter -> CheckDamage. host destroys; client gate is elsewhere.
    // the glass sting used to stay on the host. clients hear the announce.
    static bool Prefix(CrystalFragile __instance, GameObject touched) {
        ran = false;
        if (!Net.IsRunning)
            return true;
        if (__instance == null || __instance.crystal == null || !Breaker(touched))
            return false;
        if (Net.IsServer) {
            if (!broke.Add(__instance.crystal.gameObject.GetInstanceID()))
                return false;
            if (broke.Count > 256)
                broke.Clear();
            ran = true;
            return true;
        }
        if (!LocalBreaker(touched))
            return false;
        if (NetObjectRegistry.TryGetSyncInfo(__instance.crystal.gameObject, out SyncInfo si) && si != null) {
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("fragile_break " + si.syncId.id.ToString());
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }
        return false;
    }

    static void Postfix(CrystalFragile __instance) {
        if (!ran || !Net.IsServer || __instance == null || __instance.crystal == null)
            return;
        ServerMain.Server_AnnounceSound(__instance.crystal.transform.position, "glass", ServerMain.AllClientIdsExceptHost);
    }

    static bool Breaker(GameObject touched) {
        if (touched == null)
            return false;
        return touched.GetComponent<Item>() != null || touched.GetComponent<Body>() != null;
    }

    static bool LocalBreaker(GameObject touched) {
        if (touched == null)
            return false;
        if (touched.GetComponent<Item>() != null)
            return true;
        Body body = touched.GetComponent<Body>();
        return body != null && Util.IsBodyLocal(body);
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict["fragile_break"] = (plr, cmd, args) => {
            if (!Net.IsServer || args == null || args.Length < 2)
                return;
            if (!ushort.TryParse(args[1], out ushort syncId))
                return;
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null)
                return;
            if (!si.go.TryGetComponent(out CrystalBehaviour crystal) || crystal.effects == null)
                return;
            for (int i = 0; i < crystal.effects.Count; i++) {
                if (crystal.effects[i] is not CrystalFragile fragile)
                    continue;
                if (!broke.Add(crystal.gameObject.GetInstanceID()))
                    return;
                Sound.Play("glass", crystal.transform.position);
                ServerMain.Server_AnnounceSound(crystal.transform.position, "glass", ServerMain.AllClientIdsExceptHost);
                crystal.build.health = 0f;
                return;
            }
        };
    }
}





[HarmonyPatch(typeof(CrystalSeptic), nameof(CrystalSeptic.Update))]
internal static class CrystalSeptic_Update_MultiplayerPatch {
    static readonly Dictionary<ushort, float> nextHealthSend = new Dictionary<ushort, float>();

    static bool Prefix(CrystalSeptic __instance) {
        if (!Net.IsRunning) return true;
        if (__instance.crystal == null) return false;
        // septicShock is host health. a client-only add was wiped by the next health packet, and the host only ticked its own body.
        if (!Net.IsServer) return false;

        float size = __instance.crystal.crystalSize;
        if (size < 0.05f) {
            size = 1f;
        }
        float range = 12f * size;
        float add = Time.deltaTime * 0.5f;
        float now = Time.unscaledTime;
        Vector2 pos = __instance.crystal.transform.position;
        foreach (NetBody nb in NetBody.GetBodiesInRadius(pos, range)) {
            Body body = nb != null ? nb.body : null;
            if (body == null || !body.alive) {
                continue;
            }
            body.septicShock += add;
            ushort id = nb.netId.id;
            if (nextHealthSend.TryGetValue(id, out float at) && now < at) {
                continue;
            }
            nextHealthSend[id] = now + 0.5f;
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
        return false;
    }
}



[HarmonyPatch(typeof(CrystalTeleport), nameof(CrystalTeleport.Touched))]
internal static class CrystalTeleport_Touched_MultiplayerPatch {
    static bool Prefix(CrystalTeleport __instance, GameObject touched) {
        if (!Net.IsRunning || __instance.crystal == null) return true;
        if (!Utils.GetBody(touched, out Body body) || body == null) {
            return false;
        }
        if (Net.IsServer) {
            HostTeleportBody(__instance.crystal, body);
        }
        if (Util.IsBodyLocal(body)) {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.StartCoroutine("FlashBrief");
            }
            Sound.Play("observerlaugh", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        }
        return false;
    }

    static void HostTeleportBody(CrystalBehaviour crystal, Body body) {
        if (crystal == null || body == null || WorldGeneration.world == null) {
            return;
        }
        int halfWidth = (int)WorldGeneration.world.halfWidth;
        int mask = LayerMask.GetMask("Ground");
        for (int i = 0; i < 1000; i++) {
            Vector2 pos = (Vector2)crystal.transform.position + UnityEngine.Random.insideUnitCircle * 64f;
            if (pos.x < -halfWidth + 10f) {
                pos.x = -halfWidth + 10f;
            }
            if (pos.x > halfWidth - 10f) {
                pos.x = halfWidth - 10f;
            }
            if (Physics2D.OverlapPoint(pos, mask)) {
                continue;
            }
            RaycastHit2D hit = Physics2D.Raycast(pos, Vector2.down, 999f, mask);
            if (!hit) {
                continue;
            }
            Vector2 dest = hit.point + Vector2.up * 3f;
            body.consciousness = 100f;
            body.shock = 0f;
            if (body.rb != null) {
                body.rb.velocity = Vector2.zero;
            }
            body.Stand(false);
            if (body.TryGetNetBody(out var nb) && nb != null) {
                nb.SetBodyPosition(dest);
                if (body.rb != null) {
                    body.rb.velocity = Vector2.zero;
                }
                if (!nb.IsBodyLocal()) {
                    nb.Server_RemindPlayersCurrentState();
                }
            } else {
                body.transform.position = dest;
            }
            break;
        }
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Touched))]
internal static class CrystalUnstable_Touched_HostSim_MultiplayerPatch {
    static bool Prefix(CrystalUnstable __instance, GameObject touched) {
        if (!Net.IsRunning) return true;
        if (Net.IsServer) return true;
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body) && __instance.crystal != null) {
            body.eyeScareTime = 3f;
            if (body.talker != null) {
                body.talker.Talk("..!");
            }
            CrystalDelayedFx.OnUnstableArmed(__instance, playTick: true);
            // the host owns the fuse and the blast. a client-only collision never reached it, so the blast never happened.
            if (NetObjectRegistry.TryGetSyncInfo(__instance.crystal.gameObject, out SyncInfo si) && si != null) {
                LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
                writer.Put("unstable_arm " + si.syncId.id.ToString());
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }
        return false;
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict["unstable_arm"] = (plr, cmd, args) => {
            if (!Net.IsServer || args == null || args.Length < 2 || plr == null) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort syncId)) {
                return;
            }
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
                return;
            }
            if (!si.go.TryGetComponent(out CrystalBehaviour crystal) || crystal.effects == null) {
                return;
            }
            for (int i = 0; i < crystal.effects.Count; i++) {
                if (crystal.effects[i] is not CrystalUnstable unstable) {
                    continue;
                }
                if (Traverse.Create(unstable).Field("timerStarted").GetValue<bool>()) {
                    return;
                }
                CrystalDelayedFx.OnUnstableArmed(unstable, playTick: false);
                CrystalDelayedFx.HostBroadcastUnstableArm(unstable);
                return;
            }
        };
    }

    static void Postfix(CrystalUnstable __instance) {
        if (Net.IsRunning && Net.IsServer) {
            CrystalDelayedFx.HostBroadcastUnstableArm(__instance);
        }
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Hit))]
internal static class CrystalUnstable_Hit_HostSim_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;
        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
        codes = TranspilerHelpers.PatchCoroutines(codes, __originalMethod, null);
        return codes;
    }

    static bool Prefix(CrystalUnstable __instance) {
        if (!Net.IsRunning) return true;
        if (CrystalMpGates.HostSimOnly(__instance)) return true;
        Body attacker = CrystalLocalFx.TriggerBody ?? Body_Attack_MultiplayerPatch.LastAttacker;
        if (attacker != null && Util.IsBodyLocal(attacker) && __instance.crystal != null) {
            Sound.Play("crystaltick", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false, __instance.crystal.transform);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Update))]
internal static class CrystalUnstable_Update_HostSim_MultiplayerPatch {
    static bool Prefix(CrystalUnstable __instance) {
        // a size of 0 makes the blast deal 0 muscle damage. the generated crystal can lose it.
        if (Net.IsRunning && Net.IsServer && __instance != null && __instance.crystal != null && __instance.crystal.crystalSize < 0.2f) {
            __instance.crystal.crystalSize = 1f;
        }
        // explosion + light ramp is host-authored; clients get building destroy via normal sync.
        return !Net.IsRunning || Net.IsServer;
    }
}

[HarmonyPatch(typeof(CrystalDripping), nameof(CrystalDripping.Update))]
internal static class CrystalDripping_Update_HostSim_MultiplayerPatch {
    static bool Prefix() {
        // fluidManager writes must be host-only or every peer floods liquids.
        return !Net.IsRunning || Net.IsServer;
    }
}

[HarmonyPatch(typeof(CrystalTemperature), "Start")]
public static class CrystalTemperature_Start_MultiplayerPatch {
    static void Postfix(CrystalTemperature __instance) {
        if (Net.IsRunning && __instance != null && __instance.crystal != null) {
            __instance.crystal.gameObject.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }
    }
}

// stock ChunkScript.Update returns while the tile renderer is off, and that renderer only follows the host camera.
// septic, gravity, drip, burn cooldown, and the unstable fuse all live in CrystalBehaviour.ChunkUpdate.
[HarmonyPatch(typeof(ChunkScript), "Update")]
static class ChunkScript_Update_CrystalSim_MultiplayerPatch {
    static void Postfix(ChunkScript __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null) {
            return;
        }
        if (__instance.renderer != null && __instance.renderer.enabled) {
            return;
        }
        List<ISimEntity> list = __instance.containedEntities;
        if (list == null || list.Count == 0) {
            return;
        }
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists || world.generatingWorld) {
            return;
        }
        int size = WorldGeneration.CHUNKSIZE;
        if (size < 1) {
            return;
        }
        Vector2Int block = new Vector2Int(__instance.pos.x * size + size / 2, __instance.pos.y * size + size / 2);
        if (!SharedMain.PositionIsInSimulatedChunk_BlockPos(block)) {
            return;
        }
        for (int i = list.Count - 1; i >= 0; i--) {
            if (list[i] is CrystalBehaviour crystal && crystal != null) {
                crystal.ChunkUpdate();
            }
        }
    }
}

#endif