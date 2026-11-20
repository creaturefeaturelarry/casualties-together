using CUCoreLib.Networking;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Together;
using UnityEngine;

// mp skins only the local body. single-player still skins every body.
internal static class MpBody {
    private static Body _localBody;
    private static float _nextRefresh;
    private static bool _needsApply;
    private static readonly Dictionary<long, IReadOnlyDictionary<string, Sprite>> _rejected =
        new Dictionary<long, IReadOnlyDictionary<string, Sprite>>();

    internal static bool IsActive {
        get {
            if (!CoreAvailable()) {
                return false;
            }

            return ActiveCore();
        }
    }

    internal static bool IsHost {
        get {
            if (!CoreAvailable()) {
                return false;
            }

            return HostCore();
        }
    }

    internal static bool IsClient {
        get {
            if (!CoreAvailable()) {
                return false;
            }

            return ClientCore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HostCore() {
        try {
            return MultiplayerApi.IsHost;
        } catch {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ClientCore() {
        try {
            return MultiplayerApi.IsClient;
        } catch {
            return false;
        }
    }

    internal static bool CanApply(Body body) {
        // MP session up (or coming up): never stamp LoadedSprites onto remotes via solo paths.
        // isActive can lag MultiplayerApi for a frame while FacialExpression.Start already ran.
        if (!IsActive) {
            try {
                if (Together.Net.IsRunning) {
                    return false;
                }
            } catch {
                // net may not be ready in pure SP boot
            }

            return true;
        }

        if (_localBody == null) {
            Refresh();
        }

        if (_localBody == null) {
            return false;
        }

        return body == _localBody;
    }

    internal static void Tick() {
        if (!IsActive) {
            _localBody = null;
            _needsApply = false;
            return;
        }

        if (Time.unscaledTime < _nextRefresh) {
            return;
        }

        _nextRefresh = Time.unscaledTime + 1f;

        Refresh();
        // species cache decides the folder before the binding runs.
        SpriteReplacerPlugin.SyncSpeciesCache();
        ScanRemoteDefaults();
        if (!_needsApply) {
            return;
        }

        // join/species rebuild can leave a torso without FacialExpression for a few frames -
        // keep retrying so we do not stick a body-only bind.
        if (!IsBodyFullyReady(_localBody)) {
            return;
        }

        _needsApply = false;
        ApplyLocalSkin(SpriteReplacerPlugin.LoadedSprites);
    }

    // limbs + FacialExpression.head must exist; otherwise Bind skins the torso and leaves a stock head.
    internal static bool IsBodyFullyReady(Body body) {
        if (body == null) {
            return false;
        }

        try {
            Limb[] limbs = body.limbs;
            if (limbs == null || limbs.Length == 0) {
                return false;
            }

            int headIdx = (int)Body.LimbNum.Head;
            if (headIdx < 0 || headIdx >= limbs.Length) {
                return false;
            }

            Limb headLimb = limbs[headIdx];
            if (headLimb == null) {
                return false;
            }

            FacialExpression face = headLimb.GetComponent<FacialExpression>();
            if (face == null || face.head == null) {
                return false;
            }

            return true;
        } catch {
            return false;
        }
    }

    internal static void ApplyLocalSkin(IReadOnlyDictionary<string, Sprite> sprites) {
        if (!IsActive) {
            return;
        }

        if (_localBody == null) {
            Refresh();
        }

        if (_localBody == null || !IsBodyFullyReady(_localBody)) {
            _needsApply = true;
            SkinTrace.Event("apply-local-defer",
                SkinTrace.SlotSummary(SpriteReplacerPlugin.ActiveSlot, sprites, SpriteReplacerPlugin.ActiveSpecies)
                + " " + SkinTrace.BodyState(_localBody));
            return;
        }

        // numpad 0 / settings default = stock art. Never Bind leftover default-folder or
        // prior-slot body sprites (late join was leaving slot body + stock head).
        if (SpriteReplacerPlugin.IsDefaultSlotName(SpriteReplacerPlugin.ActiveSlot)
            || sprites == null
            || sprites.Count == 0) {
            SkinTrace.Event("apply-local-stock",
                SkinTrace.SlotSummary(SpriteReplacerPlugin.ActiveSlot, sprites, SpriteReplacerPlugin.ActiveSpecies)
                + " " + SkinTrace.BodyState(_localBody));
            ResetBodyToStock(_localBody);
            // the default folder can hold another species' pngs. leave them unloaded so they
            // cannot paint the wound view or the next body that shares those sprite names.
            if (SpriteReplacerPlugin.IsDefaultSlotName(SpriteReplacerPlugin.ActiveSlot)) {
                SpriteReplacerPlugin.LoadedSprites.Clear();
                SpriteReplacerPlugin.ColoredWoundSprites.Clear();
            }
            return;
        }

        SkinTrace.Event("apply-local",
            SkinTrace.SlotSummary(SpriteReplacerPlugin.ActiveSlot, sprites, SpriteReplacerPlugin.ActiveSpecies)
            + " " + SkinTrace.BodyState(_localBody));

        BodySkinBinding binding = _localBody.GetComponent<BodySkinBinding>();
        if (binding == null) {
            binding = _localBody.gameObject.AddComponent<BodySkinBinding>();
        }

        binding.Bind(_localBody, sprites, SlotFolders.DisplayName(SpriteReplacerPlugin.ActiveSlot));
        FacialExpression face = FindFace(_localBody);
        if (face != null) {
            SkinFace.ApplyReplacementToFace(face, sprites);
        }
    }

    // SR Reset + FacialExpression field restore. ClearRemoteSkin used to leave custom heads.
    internal static void ResetBodyToStock(Body body) {
        if (body == null) {
            return;
        }

        SkinTrace.Event("reset-stock", SkinTrace.BodyState(body));

        BodySkinBinding binding = body.GetComponent<BodySkinBinding>();
        if (binding != null) {
            binding.Reset();
        }

        SkinSpriteReplacer[] leftover = body.GetComponentsInChildren<SkinSpriteReplacer>(true);
        for (int i = 0; i < leftover.Length; i++) {
            SkinSpriteReplacer replacer = leftover[i];
            if (replacer == null) {
                continue;
            }

            replacer.Restore();
            UnityEngine.Object.Destroy(replacer);
        }

        FacialExpression face = FindFace(body);
        if (face != null) {
            SkinFace.RestoreFaceToStock(face);
        }

        // cloned from the live body after the slot bind. those renderers already point at the
        // local slot, and the copied replacer has no stock sprite to put back.
        RestoreBakedSprites(body);

        WoundViewIntegration.RefreshForBody(body);
        SkinTrace.NoteDefaultLeak("reset", body);
    }

    internal static void ResetRemoteSkins() {
        try {
            Dictionary<Body, Together.ScavPlayer> map = Together.ScavPlayer.BodyToPlayerDict;
            if (map == null) {
                return;
            }

            List<Body> bodies = new List<Body>(map.Keys);
            for (int i = 0; i < bodies.Count; i++) {
                Body body = bodies[i];
                if (body == null || body == _localBody) {
                    continue;
                }
                ResetBodyToStock(body);
            }
        } catch (Exception ex) {
            LogDebug("remote skin reset failed: " + ex.Message);
        }
    }

    static void RestoreBakedSprites(Body body) {
        SpriteRenderer[] renderers = body.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < renderers.Length; i++) {
            SpriteRenderer renderer = renderers[i];
            if (renderer == null || renderer.sprite == null) {
                continue;
            }

            if (!DefaultSpriteExport.TryGetStock(renderer.sprite.name, out Sprite stock) || stock == null) {
                continue;
            }

            if (ReferenceEquals(renderer.sprite, stock)) {
                continue;
            }

            renderer.sprite = stock;
        }
    }

    static void ScanRemoteDefaults() {
        try {
            Dictionary<Body, Together.ScavPlayer> map = Together.ScavPlayer.BodyToPlayerDict;
            if (map == null) {
                return;
            }

            foreach (KeyValuePair<Body, Together.ScavPlayer> pair in map) {
                Body body = pair.Key;
                if (body == null || body == _localBody) {
                    continue;
                }

                SkinTrace.NoteDefaultLeak("tick", body);
            }
        } catch (Exception ex) {
            LogDebug("default skin scan failed: " + ex.Message);
        }
    }

    static FacialExpression FindFace(Body body) {
        if (body == null || body.limbs == null) {
            return null;
        }

        int headIdx = (int)Body.LimbNum.Head;
        if (headIdx < 0 || headIdx >= body.limbs.Length) {
            return null;
        }

        Limb head = body.limbs[headIdx];
        return head != null ? head.GetComponent<FacialExpression>() : null;
    }

    // -1 when this session has no local body yet. 0 experiment, 1 milky, 2 dune.
    internal static int LocalCharType {
        get {
            if (!IsActive) {
                return -1;
            }

            if (_localBody == null) {
                Refresh();
            }

            if (_localBody == null) {
                return -1;
            }

            return _localBody.charType;
        }
    }

    internal static bool HasLocalBody {
        get {
            if (_localBody == null) {
                Refresh();
            }

            return _localBody != null;
        }
    }

    internal static uint LocalClientId {
        get { return TryGetLocalIds(out uint clientId, out _) ? clientId : 0; }
    }

    internal static uint LocalPlayerId {
        get { return TryGetLocalIds(out _, out uint playerId) ? playerId : 0; }
    }

    internal static bool TryGetLocalIds(out uint networkClientId, out uint playerId) {
        networkClientId = 0;
        playerId = 0;
        if (!IsActive) {
            return false;
        }

        if (_localBody == null) {
            Refresh();
        }

        if (_localBody == null) {
            return false;
        }

        try {
            Type scavType = ResolvePlayerType();
            if (scavType == null) {
                return false;
            }

            MethodInfo method = MultiplayerApi.FindKrokMpMethod(scavType, "GetClientIdFromBody",
                new[] { typeof(Body) });
            if (method != null) {
                object value = method.Invoke(null, new object[] { _localBody });
                networkClientId = KnetToUInt(value);
            }

            MemberInfo localMember = MultiplayerApi.FindKrokMpMember(scavType, "LocalPlayer")
                ?? MultiplayerApi.FindKrokMpMember(scavType, "LOCAL_PLAYER");
            object localPlayer = ReadMember(localMember, null);
            MemberInfo idMember = MultiplayerApi.FindKrokMpMember(scavType, "playerId")
                ?? MultiplayerApi.FindKrokMpMember(scavType, "PlayerId");
            object idValue = ReadMember(idMember, localPlayer);
            playerId = KnetToUInt(idValue);

            return method != null || localPlayer != null;
        } catch (Exception ex) {
            LogDebug("Local ids failed: " + ex.Message);
            return false;
        }
    }

    private static object ReadMember(MemberInfo member, object target) {
        if (member == null) {
            return null;
        }

        try {
            if (member is FieldInfo field) {
                return field.GetValue(target);
            }

            if (member is PropertyInfo property) {
                return property.GetValue(target);
            }
        } catch (Exception ex) {
            LogDebug("Read " + member.Name + " failed: " + ex.Message);
        }

        return null;
    }

    internal static Body GetBodyByPlayerId(uint playerId) {
        if (!IsActive) {
            return null;
        }

        try {
            Type scavType = ResolvePlayerType();
            if (scavType == null) {
                return null;
            }

            Type knetType = Resolve("knetid");
            if (knetType == null) {
                MemberInfo idMember = MultiplayerApi.FindKrokMpMember(scavType, "playerId");
                if (idMember is FieldInfo field) {
                    knetType = field.FieldType;
                } else if (idMember is PropertyInfo property) {
                    knetType = property.PropertyType;
                }
            }
            if (knetType == null) {
                return null;
            }

            MethodInfo method = MultiplayerApi.FindKrokMpMethod(scavType, "GetBodyFromClientId",
                new[] { knetType });
            if (method == null) {
                return null;
            }

            object id = Activator.CreateInstance(knetType, new object[] { (ushort)playerId });
            return method.Invoke(null, new[] { id }) as Body;
        } catch (Exception ex) {
            LogDebug("Body lookup failed for player " + playerId + ": " + ex.Message);
            return null;
        }
    }

    // compare the payload network id with the sender's body. accept the claim if that body is not ready.
    internal static bool PlayerMatchesNetwork(uint networkId, uint playerId) {
        if (networkId == 0 || playerId == 0) {
            return false;
        }

        Body body = GetBodyByPlayerId(playerId);
        if (body == null) {
            LogDebug("Cannot verify network " + networkId + " for player " + playerId + " yet.");
            return true;
        }

        uint actual = GetNetworkIdForBody(body);
        if (actual == 0) {
            LogDebug("Cannot read the network ID for player " + playerId + ".");
            return true;
        }

        return actual == networkId;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint GetNetworkIdForBody(Body body) {
        if (!IsActive || body == null) {
            return 0;
        }

        try {
            Type scavType = ResolvePlayerType();
            if (scavType == null) {
                return 0;
            }

            MethodInfo method = MultiplayerApi.FindKrokMpMethod(scavType, "GetClientIdFromBody",
                new[] { typeof(Body) });
            if (method == null) {
                return 0;
            }

            object value = method.Invoke(null, new object[] { body });
            return KnetToUInt(value);
        } catch (Exception ex) {
            LogDebug("Network ID lookup failed for body: " + ex.Message);
            return 0;
        }
    }

    internal static void ClearRejected() {
        _rejected.Clear();
    }

    internal static bool ApplyRemoteSkin(uint networkClientId, uint playerId,
        IReadOnlyDictionary<string, Sprite> sprites, string setName, int species, string contentHash = null) {
        if (!IsActive || sprites == null) {
            return false;
        }

        if (_localBody == null) {
            Refresh();
        }

        // same package already refused. the timer must not reset the body again.
        long key = ((long)networkClientId << 32) | playerId;
        if (_rejected.TryGetValue(key, out IReadOnlyDictionary<string, Sprite> rejected)
            && ReferenceEquals(rejected, sprites)) {
            return false;
        }

        Body body = ResolveRemoteBody(networkClientId, playerId);
        if (body == null || !IsBodyFullyReady(body)) {
            return false;
        }

        // refuse cross-body paints when the resolved NetBody.netId disagrees with the package key
        uint resolvedNet = GetNetworkIdForBody(body);
        if (networkClientId != 0 && resolvedNet != 0 && resolvedNet != networkClientId) {
            return false;
        }

        int liveSpecies = species;
        if (liveSpecies < 0 || liveSpecies > 2) {
            liveSpecies = 0;
        }
        int bodySpecies = body.charType;
        if (bodySpecies < 0 || bodySpecies > 2) {
            bodySpecies = 0;
        }
        // an experiment pack aliased onto a dune/milky body mixes that skin with the one already there.
        if (liveSpecies != bodySpecies) {
            _rejected[key] = sprites;
            ResetBodyToStock(body);
            return false;
        }

        BodySkinBinding binding = body.GetComponent<BodySkinBinding>();
        if (binding != null && binding.Holds(contentHash)) {
            _rejected.Remove(key);
            return true;
        }

        if (binding != null && binding.Wearing(sprites)) {
            int missing = binding.CountMissingRenderers();
            if (missing <= 0) {
                _rejected.Remove(key);
                binding.Remember(contentHash);
                return true;
            }

            // new limb renderers. the head field is already the skin, the body clip is not.
            SkinTrace.Event("rebind-new-renderers", SkinTrace.Ids(networkClientId, playerId)
                + " missing=" + missing
                + " " + SkinTrace.BodyState(body));
        }

        if (binding == null) {
            binding = body.gameObject.AddComponent<BodySkinBinding>();
        }

        IReadOnlyDictionary<string, Sprite> mapped =
            SpeciesAliases.WithAliases(sprites, liveSpecies);
        _rejected.Remove(key);
        binding.Bind(body, mapped, setName, sprites);

        // facialExpression caches stock head/eye sprites in fields; MP used to skip face
        // replacement and only hope SpriteRenderer hooks caught them after a full Bind.
        FacialExpression face = FindFace(body);
        if (face != null) {
            SkinFace.ApplyReplacementToFace(face, mapped);
        } else {
            SkinTrace.Event("apply-remote-noface", SkinTrace.Ids(networkClientId, playerId)
                + " set=" + (setName ?? "-"));
        }

        WoundViewIntegration.RefreshForBody(body);
        binding.Remember(contentHash);
        return true;
    }

    internal static bool ClearRemoteSkin(uint networkClientId, uint playerId) {
        if (!IsActive) {
            return false;
        }

        if (_localBody == null) {
            Refresh();
        }

        Body body = ResolveRemoteBody(networkClientId, playerId);
        if (body == null) {
            SkinTrace.Event("clear-remote-miss", SkinTrace.Ids(networkClientId, playerId));
            return false;
        }

        SkinTrace.Event("clear-remote", SkinTrace.Ids(networkClientId, playerId)
            + " " + SkinTrace.BodyState(body));
        ResetBodyToStock(body);
        return true;
    }

    // networkClientId is NetBody.netId (GetClientIdFromBody). playerId is ScavPlayer.playerId.
    // those are different id spaces - never cross-match pid == networkClientId or the host
    // package paints onto a joiner when the numbers collide (LeafCider wrong-head).
    private static Body ResolveRemoteBody(uint networkClientId, uint playerId) {
        if (_localBody == null) {
            Refresh();
        }

        Body byNetwork = null;
        Body byPlayer = null;

        try {
            Dictionary<Body, Together.ScavPlayer> map = Together.ScavPlayer.BodyToPlayerDict;
            if (map != null) {
                foreach (KeyValuePair<Body, Together.ScavPlayer> pair in map) {
                    Body body = pair.Key;
                    Together.ScavPlayer player = pair.Value;
                    if (body == null || player == null || body == _localBody) {
                        continue;
                    }

                    uint pid = KnetToUInt(player.playerId);
                    uint nid = GetNetworkIdForBody(body);
                    if (networkClientId != 0 && nid == networkClientId) {
                        byNetwork = body;
                    }

                    if (playerId != 0 && pid == playerId) {
                        byPlayer = body;
                    }
                }
            }
        } catch (Exception ex) {
            LogDebug("Remote body walk failed: " + ex.Message);
        }

        if (byNetwork != null && byPlayer != null && byNetwork != byPlayer) {
            SkinTrace.Event("resolve-ambiguous",
                SkinTrace.Ids(networkClientId, playerId)
                + " netBody=" + byNetwork.GetInstanceID()
                + " playerBody=" + byPlayer.GetInstanceID());
            // prefer netId key - that is what Skins[] is indexed by
            return byNetwork;
        }

        if (byNetwork != null) {
            return byNetwork;
        }

        if (byPlayer != null) {
            return byPlayer;
        }

        if (playerId != 0) {
            Body looked = GetBodyByPlayerId(playerId);
            if (looked != null && looked != _localBody) {
                return looked;
            }
        }

        return null;
    }

    private static Type Resolve(string name) {
        try {
            return MultiplayerApi.ResolveKrokMpType(name);
        } catch {
            return null;
        }
    }

    private static Type ResolvePlayerType() {
        return Resolve("NetPlayer") ?? Resolve("ScavPlayer");
    }

    internal static uint KnetToUInt(object value) {
        if (value == null) {
            return 0;
        }

        FieldInfo id = value.GetType().GetField("id",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return id == null ? 0 : Convert.ToUInt32(id.GetValue(value));
    }

    private static void LogDebug(string message) {
        if (SpriteReplacerPlugin.Log != null) {
            SpriteReplacerPlugin.Log.LogDebug(message);
        }
    }

    private static bool CoreAvailable() {
        // cUCoreLib hard dep. IsAvailable is false until its multiplayer bridge is up.
        try {
            return MultiplayerApi.IsAvailable;
        } catch {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ActiveCore() {
        try {
            return MultiplayerApi.IsAvailable && MultiplayerApi.IsRunning;
        } catch {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Refresh() {
        try {
            Body before = _localBody;
            int beforeChar = before != null ? before.charType : -1;
            if (MultiplayerApi.TryGetLocalBody(out Body body)) {
                _localBody = body;
            }

            int afterChar = _localBody != null ? _localBody.charType : -1;
            bool swapped = _localBody != before && _localBody != null;
            if (swapped || beforeChar != afterChar) {
                _needsApply = true;
                SkinTrace.Event("local-body",
                    "from=" + (before != null ? before.GetInstanceID() : 0)
                    + " to=" + (_localBody != null ? _localBody.GetInstanceID() : 0)
                    + " fromChar=" + beforeChar
                    + " toChar=" + afterChar
                    + " " + SkinTrace.BodyState(_localBody));
            }
            // tutorial (and any regen) builds a new body with a new net id.
            // the last upload is still recorded under the old id, so peers paint stock.
            if (swapped) {
                MpSkinSync.OnLocalBodySwapped();
            }
        } catch (Exception ex) {
            _localBody = null;
            if (SpriteReplacerPlugin.Log != null) {
                SpriteReplacerPlugin.Log.LogDebug("Local body lookup failed: " + ex.Message);
            }
        }
    }
}