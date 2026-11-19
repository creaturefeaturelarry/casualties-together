using CUCoreLib.Networking;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Together;
using UnityEngine;

// host stores one zip per client and relays it. a late client can ask for the stored set.
internal static class MpSkinSync {
    private const string FullChannel = "spritereplacer.skin.full";
    private const string RequestChannel = "spritereplacer.skin.request";
    private const string ApplyChannel = "spritereplacer.skin.apply";
    private const string ManifestChannel = "spritereplacer.skin.manifest";
    private const string FetchChannel = "spritereplacer.skin.fetch";

    private static readonly Dictionary<uint, MpSkinPackage> Skins =
        new Dictionary<uint, MpSkinPackage>();
    private static readonly Dictionary<uint, MpSkinPackage> Pending =
        new Dictionary<uint, MpSkinPackage>();
    private static readonly Dictionary<uint, uint> PlayerIds =
        new Dictionary<uint, uint>();
    private static readonly Dictionary<uint, string> LastContent =
        new Dictionary<uint, string>();
    private static readonly HashSet<uint> Waiting = new HashSet<uint>();
    private static readonly MpUploadPolicy UploadPolicy = new MpUploadPolicy();
    private static readonly Dictionary<uint, SendWindow> UploadWindows =
        new Dictionary<uint, SendWindow>();
    private static readonly MpRateLimiter RequestLimiter = new MpRateLimiter(4, 10f);
    private static readonly MpRateLimiter FetchLimiter = new MpRateLimiter(4, 10f);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly MpCooldown Cooldown = new MpCooldown();
    private static MpSkinCache _cache;

    private const int WireLimit = 2;
    private const float WireSeconds = 1f;
    private const float SampleSeconds = 1f;
    // sample signature when the local player is on stock/default (no zip shared)
    private const string ClearedSignature = "\0cleared";

    private static bool _handlers;
    private static bool _requested;
    private static bool _resend;
    private static string _lastSharedSlot;
    private static string _sentSignature;
    private static string _pendingSignature;
    private static bool _awaiting;
    private static float _nextTick;
    private static float _nextSample;
    private static float _nextReapply;
    private static bool _manualChoice;
    private static SendWindow _localWire;

    internal static bool IsCoolingDown =>
        MpBody.IsActive && Cooldown.IsActive(Time.unscaledTime);

    internal static int CooldownSeconds =>
        IsCoolingDown ? Cooldown.RemainingSeconds(Time.unscaledTime) : 0;

    // handlers can run off the main thread. unity time is not safe there.
    private static float ServerTime => (float)Clock.Elapsed.TotalSeconds;

    private static MpSkinCache Cache {
        get {
            if (_cache == null) {
                string root = Path.Combine(SpriteReplacerPlugin.SpritesPath, "mp_cache");
                _cache = new MpSkinCache(root,
                    SpriteReplacerPlugin.MaxSkinCacheMB * 1024L * 1024L);
            }
            return _cache;
        }
    }

    internal static int ClearCache() {
        try {
            return Cache.Clear();
        } catch (Exception ex) {
            Log("cache clear failed: " + ex.Message);
            return 0;
        }
    }

    internal static long CacheBytes() {
        try { return Cache.TotalBytes(); } catch { return 0; }
    }

    internal static void OnLocalSkinChanged() {
        _resend = false;
        if (SkinLock.CanChangeOnce) {
            _manualChoice = true;
        }
        SkinTrace.Event("local-changed",
            "slot=" + (SpriteReplacerPlugin.ActiveSlot ?? "-")
            + " default=" + (IsDefaultSlot(SpriteReplacerPlugin.ActiveSlot) ? 1 : 0)
            + " canChangeOnce=" + (SkinLock.CanChangeOnce ? 1 : 0)
            + " " + SkinTrace.SlotSummary(SpriteReplacerPlugin.ActiveSlot,
                SpriteReplacerPlugin.LoadedSprites, SpriteReplacerPlugin.ActiveSpecies));
        // numpad 0 / default is stock art - drop the shared zip so peers do not keep the old slot
        if (IsDefaultSlot(SpriteReplacerPlugin.ActiveSlot)) {
            DropSharedLocal();
        }
    }

    static bool IsDefaultSlot(string slot) {
        return SpriteReplacerPlugin.IsDefaultSlotName(slot);
    }

    // wound view reads the viewed body's package; local body keeps using LoadedSprites.
    internal static IReadOnlyDictionary<string, Sprite> SpritesForBody(Body body) {
        if (body == null || !MpBody.IsActive || MpBody.CanApply(body)) {
            return null;
        }

        uint network = MpBody.GetNetworkIdForBody(body);
        if (network == 0) {
            return null;
        }

        if (Skins.TryGetValue(network, out MpSkinPackage package) &&
            package != null && package.Sprites != null && package.Sprites.Count > 0
            && PackageMatchesBody(package, body)) {
            return SpeciesAliases.WithAliases(package.Sprites, package.Species);
        }

        if (Pending.TryGetValue(network, out MpSkinPackage pending) &&
            pending != null && pending.Sprites != null && pending.Sprites.Count > 0
            && PackageMatchesBody(pending, body)) {
            return SpeciesAliases.WithAliases(pending.Sprites, pending.Species);
        }

        return null;
    }

    static bool PackageMatchesBody(MpSkinPackage package, Body body) {
        if (package == null || body == null) {
            return false;
        }
        int wire = package.Species;
        int live = body.charType;
        if (wire < 0 || wire > 2 || live < 0 || live > 2) {
            return true;
        }
        return wire == live;
    }

    internal static HashSet<string> ColoredWoundSpritesForBody(Body body) {
        if (body == null || !MpBody.IsActive || MpBody.CanApply(body)) {
            return null;
        }

        uint network = MpBody.GetNetworkIdForBody(body);
        if (network == 0) {
            return null;
        }

        if (Skins.TryGetValue(network, out MpSkinPackage package) && package != null
            && PackageMatchesBody(package, body)) {
            return package.ColoredWoundSprites;
        }

        if (Pending.TryGetValue(network, out MpSkinPackage pending) && pending != null
            && PackageMatchesBody(pending, body)) {
            return pending.ColoredWoundSprites;
        }

        return null;
    }

    // peers drop the skin once. cleared signature stops SampleLocalSkin from re-zipping /default.
    internal static void DropSharedLocal() {
        SkinTrace.Event("drop-shared-local",
            "prevShared=" + (_lastSharedSlot ?? "-")
            + " sentSig=" + (_sentSignature ?? "-"));
        _sentSignature = ClearedSignature;
        _pendingSignature = ClearedSignature;
        _awaiting = false;
        _resend = false;
        _lastSharedSlot = SkinSettings.DefaultFolder;
        ClearLocalSkin();
        SkinLock.HasLocalSkin = false;
    }

    internal static void OnLockChanged() {
        UploadPolicy.Clear();
        foreach (uint playerId in LastContent.Keys) {
            UploadPolicy.MarkSeen(playerId);
        }
    }

    internal static void OnShareChanged() {
        if (!MpBody.IsActive) {
            return;
        }

        ShareChangedCore();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShareChangedCore() {
        if (SpriteReplacerPlugin.ShareSkins) {
            _sentSignature = null;
            _resend = !string.IsNullOrEmpty(_lastSharedSlot) &&
                      string.Equals(_lastSharedSlot, SpriteReplacerPlugin.ActiveSlot,
                          StringComparison.Ordinal);
            return;
        }

        _sentSignature = CurrentSignature();
        _resend = false;
        ClearLocalSkin();
    }

    internal static void OnDownloadChanged() {
        if (!MpBody.IsActive) {
            return;
        }

        if (!SpriteReplacerPlugin.DownloadSkins) {
            ClearRemoteViews();
            return;
        }

        _requested = false;
        ApplyPending();
    }

    private static void ClearLocalSkin() {
        if (!MpBody.TryGetLocalIds(out uint network, out uint player)) {
            return;
        }

        if (MpBody.IsHost) {
            Remove(network, player);
            BroadcastClear(network, player);
        } else {
            SendClear(network, player);
        }

        SkinLock.HasLocalSkin = false;
    }

    private static void SendClear(uint network, uint player) {
        try {
            JObject payload = new JObject {
                ["clientId"] = network,
                ["playerId"] = player,
                ["clear"] = true
            };
            MultiplayerApi.RequestServer(FullChannel, payload, OnUploadResponse, true);
        } catch (Exception ex) {
            Log("clear request failed: " + ex.Message);
        }
    }

    private static void BroadcastClear(uint network, uint player) {
        try {
            JObject payload = new JObject {
                ["clientId"] = network,
                ["playerId"] = player,
                ["clear"] = true
            };
            MultiplayerApi.Broadcast(ApplyChannel, payload, false, true);
        } catch (Exception ex) {
            Log("clear broadcast failed: " + ex.Message);
        }
    }

    internal static void Tick() {
        if (!MpBody.IsActive) {
            Reset();
            return;
        }

        if (Time.unscaledTime < _nextTick) {
            return;
        }

        _nextTick = Time.unscaledTime + 0.5f;

        EnsureHandlers();

        if (!MpBody.TryGetLocalIds(out uint localNetwork, out uint localPlayer)) {
            return;
        }

        if (Time.unscaledTime >= _nextSample) {
            _nextSample = Time.unscaledTime + SampleSeconds;
            SampleLocalSkin(localNetwork, localPlayer);
        }

        // wait until local body + world are up before fetching remotes - early apply was
        // binding torso-only bodies (stock head) and clearing Pending.
        bool joinReady = MpBody.HasLocalBody
            && MpBody.IsBodyFullyReady(ResolveLocalBodySafe())
            && SharedMain.LocalWorldIsGenerated;

        if (!_requested && joinReady && !IsCoolingDown && SpriteReplacerPlugin.DownloadSkins
            && MpBody.IsClient && !MpBody.IsHost) {
            _requested = true;
            SkinTrace.Event("request-existing", SkinTrace.Ids(localNetwork, localPlayer)
                + " " + SkinTrace.BodyState(ResolveLocalBodySafe())
                + " skinsStored=" + Skins.Count
                + " pending=" + Pending.Count);
            RequestExistingSkins(localNetwork);
        }

        if (joinReady) {
            ApplyPending();
        }

        // failsafe for a skin that never landed. already-worn packages return immediately.
        if (joinReady && Time.unscaledTime >= _nextReapply) {
            _nextReapply = Time.unscaledTime + 3f;
            if (SpriteReplacerPlugin.DownloadSkins) {
                foreach (KeyValuePair<uint, MpSkinPackage> pair in Skins) {
                    if (pair.Key == localNetwork) {
                        continue;
                    }

                    uint player = PlayerIds.TryGetValue(pair.Key, out uint id) ? id : 0;
                    // keep incomplete bodies in Pending until head/face exists
                    if (!MpBody.ApplyRemoteSkin(pair.Key, player, pair.Value.Sprites, pair.Value.SetName,
                        pair.Value.Species)) {
                        Pending[pair.Key] = pair.Value;
                    }
                }
            }
        }
    }

    static Body ResolveLocalBodySafe() {
        try {
            if (MultiplayerApi.TryGetLocalBody(out Body body)) {
                return body;
            }
        } catch {
            // ignore
        }

        return null;
    }

    // facialExpression.Start / limb ready - retry any pending skin for this body now.
    internal static void OnBodyPartReady(Body body) {
        if (body == null || !MpBody.IsActive || !SpriteReplacerPlugin.DownloadSkins) {
            return;
        }

        bool local = MpBody.CanApply(body);
        uint network = MpBody.GetNetworkIdForBody(body);
        SkinTrace.Event("body-part-ready",
            "local=" + (local ? 1 : 0)
            + " net=" + network
            + " " + SkinTrace.BodyState(body)
            + " pending=" + Pending.Count
            + " hasPackage=" + (network != 0 && Skins.ContainsKey(network) ? 1 : 0));

        if (local) {
            // species/settings may still be settling on late join - sync before bind
            SpriteReplacerPlugin.SyncSpeciesCache();
            MpBody.ApplyLocalSkin(SpriteReplacerPlugin.LoadedSprites);
            return;
        }

        if (network == 0) {
            // id is not on the body yet. resetting here puts the limbs back on the
            // clip and can write a cloned custom head back over them.
            SkinTrace.Event("body-part-ready-noid", SkinTrace.BodyState(body));
            return;
        }

        if (!Skins.TryGetValue(network, out MpSkinPackage package) || package == null) {
            // known id, no package. drop a bind that does not belong to this body.
            MpBody.ResetBodyToStock(body);
            SkinTrace.Event("body-part-ready-stock",
                "net=" + network + " " + SkinTrace.BodyState(body));
            return;
        }

        Pending[network] = package;
        uint player = PlayerIds.TryGetValue(network, out uint id) ? id : 0;
        if (MpBody.ApplyRemoteSkin(network, player, package.Sprites, package.SetName, package.Species)) {
            Pending.Remove(network);
            Waiting.Remove(network);
        }
        SkinTrace.NoteDefaultLeak("body-part-ready", body);
    }

    // a limb spawned after the bind. reattach the package to the new renderer.
    internal static void OnLimbRebuilt(Body body) {
        if (body == null || !MpBody.IsActive || !SpriteReplacerPlugin.DownloadSkins) {
            return;
        }

        if (MpBody.CanApply(body)) {
            BodySkinBinding binding = body.GetComponent<BodySkinBinding>();
            int missing = binding != null ? binding.CountMissingRenderers() : 0;
            if (missing <= 0) {
                return;
            }

            SkinTrace.Event("rebind-local-limb", "missing=" + missing + " " + SkinTrace.BodyState(body));
            MpBody.ApplyLocalSkin(SpriteReplacerPlugin.LoadedSprites);
            return;
        }

        uint network = MpBody.GetNetworkIdForBody(body);
        if (network == 0) {
            SkinTrace.Problem("limb-rebuilt-noid " + body.GetInstanceID(), SkinTrace.BodyState(body));
            return;
        }

        if (!Skins.TryGetValue(network, out MpSkinPackage package) || package == null) {
            SkinTrace.Problem("limb-rebuilt-nopackage " + body.GetInstanceID(),
                "net=" + network + " " + SkinTrace.BodyState(body));
            return;
        }

        uint player = PlayerIds.TryGetValue(network, out uint id) ? id : 0;
        SkinTrace.Event("limb-rebuilt", SkinTrace.Ids(network, player) + " " + SkinTrace.BodyState(body));
        if (MpBody.ApplyRemoteSkin(network, player, package.Sprites, package.SetName, package.Species)) {
            Pending.Remove(network);
            Waiting.Remove(network);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequestExistingSkins(uint localNetwork) {
        try {
            JObject request = new JObject { ["clientId"] = localNetwork };
            MultiplayerApi.RequestServer(RequestChannel, request, OnSyncResponse, true);
        } catch (Exception ex) {
            Log("request failed: " + ex.Message);
        }
    }

    private static void EnsureHandlers() {
        if (_handlers) {
            return;
        }

        try {
            RegisterSenderHandler(FullChannel, OnServerFull);
            RegisterSenderHandler(RequestChannel, OnServerRequest);
            RegisterSenderHandler(FetchChannel, OnServerFetch);
            MultiplayerApi.RegisterClientHandler(ApplyChannel, OnClientApply);
            MultiplayerApi.RegisterClientHandler(ManifestChannel, OnClientManifest);
            MultiplayerApi.RegisterClientHandler(SkinLock.LockChannel, SkinLock.ApplyRemote);
            ScavPlayer.OnPlayerLeft += OnPlayerLeft;
            Log("handlers registered.");
        } catch (Exception ex) {
            Log("handler registration failed: " + ex);
        }

        _handlers = true;
    }

    // sender-aware handler lets the host check the real sender. older cores fall back to the payload id.
    private static void RegisterSenderHandler(string channel,
        Func<uint, JToken, JToken> handler) {
        try {
            MethodInfo method = typeof(MultiplayerBridge).GetMethod(
                "RegisterServerHandler",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(Func<uint, JToken, JToken>) },
                null);
            if (method != null) {
                method.Invoke(null, new object[] { channel, handler });
                Log("sender-aware handler registered for " + channel + ".");
                return;
            }
        } catch (Exception ex) {
            Log("sender handler failed for " + channel + ": " + ex.Message);
        }

        MultiplayerApi.RegisterServerHandler(channel, token => handler(0, token));
        Log("sender-aware handler missing for " + channel + ". Using the payload client ID.");
    }

    private static JToken OnServerFull(uint sender, JToken token) {
        try {
            JObject payload = token as JObject;
            if (payload == null) {
                return new JValue("bad");
            }

            uint claimedPlayer = ReadUInt(payload, "playerId");
            if (sender != 0 && claimedPlayer != sender) {
                Log("rejected skin: claimed player " + claimedPlayer
                    + ", sender " + sender + ".");
                return new JValue("bad owner");
            }

            uint ownerNetwork = ReadUInt(payload, "clientId");
            uint ownerPlayer = sender != 0 ? sender : claimedPlayer;
            if (ownerNetwork == 0 || ownerPlayer == 0) {
                return new JValue("bad owner");
            }

            if (sender != 0 && !MpBody.PlayerMatchesNetwork(ownerNetwork, ownerPlayer)) {
                Log("rejected skin: network " + ownerNetwork
                    + " does not match player " + ownerPlayer + ".");
                return new JValue("bad owner");
            }

            payload["clientId"] = ownerNetwork;

            if (payload.Value<bool?>("clear") == true) {
                Remove(ownerNetwork, ownerPlayer);
                MultiplayerApi.Broadcast(ApplyChannel, payload, false, true);
                Log("cleared skin from client " + ownerNetwork + ".");
                return SkinLock.Payload(false);
            }

            if (!TakeUploadSlot(ownerPlayer, ServerTime)) {
                Log("dropped skin from player " + ownerPlayer + ". wire limit is "
                    + WireLimit + " per second.");
                return new JValue("busy");
            }

            MpSkinPackage package = Parse(payload);
            if (package == null) {
                return new JValue("reject");
            }

            bool sameSkin = !string.IsNullOrEmpty(package.ContentHash) &&
                LastContent.TryGetValue(ownerPlayer, out string lastContent) &&
                string.Equals(lastContent, package.ContentHash, StringComparison.OrdinalIgnoreCase);

            MpUploadPolicy.Decision decision = sameSkin
                ? MpUploadPolicy.Decision.Accept
                : UploadPolicy.Check(ownerPlayer, SkinLock.IsLocked, HasStoredSkin(ownerPlayer));
            if (decision == MpUploadPolicy.Decision.Reject) {
                package.Dispose();
                Log("rejected locked skin from player " + ownerPlayer + ".");
                return SkinLock.Payload(false);
            }

            bool allow = SkinLock.IsLocked && !sameSkin &&
                decision == MpUploadPolicy.Decision.Accept;

            if (SpriteReplacerPlugin.DownloadSkins) {
                Cache.Store(package.Hash, package.Zip);
            }

            Store(ownerNetwork, ownerPlayer, package);
            SkinTrace.Event("host-store", SkinTrace.Ids(ownerNetwork, ownerPlayer)
                + " allow=" + (allow ? 1 : 0)
                + " same=" + (sameSkin ? 1 : 0)
                + " " + SkinTrace.PackageSummary(package));
            if (SpriteReplacerPlugin.DownloadSkins) {
                MpBody.ApplyRemoteSkin(ownerNetwork, ownerPlayer, package.Sprites, package.SetName,
                    package.Species);
            }

            MultiplayerApi.Broadcast(ApplyChannel, payload, false, true);

            return SkinLock.Payload(allow);
        } catch (Exception ex) {
            Log("server full failed: " + ex);
            return new JValue("error");
        }
    }

    private static JToken OnServerRequest(uint sender, JToken token) {
        try {
            JObject payload = token as JObject;
            uint requester = RequesterId(sender, payload);
            if (requester == 0) {
                return new JValue("bad");
            }

            MpRateLimiter.Result limit = RequestLimiter.Check(requester, ServerTime);
            if (!limit.Allowed && !limit.AcceptOnce) {
                Log("rate limited request from player " + requester + " for "
                    + limit.RetryIn.ToString("0") + "s, strike " + limit.Strikes + ".");
                return SkinLock.Payload(false, limit.RetryIn, limit.Strikes);
            }

            EnsureLocalPackage();

            JArray entries = new JArray();
            foreach (KeyValuePair<uint, MpSkinPackage> pair in Skins) {
                uint player = PlayerIds.TryGetValue(pair.Key, out uint id) ? id : 0;
                entries.Add(new JObject {
                    ["clientId"] = pair.Key,
                    ["playerId"] = player,
                    ["hash"] = pair.Value.Hash,
                    ["setName"] = pair.Value.SetName,
                    // a client that already has the zip cached builds the package from it and needs the species from here
                    ["species"] = pair.Value.Species
                });
            }

            JObject manifest = new JObject { ["skins"] = entries };
            MultiplayerApi.Broadcast(ManifestChannel, manifest, false, true);

            bool allow = SkinLock.IsLocked && UploadPolicy.HasAllowance(requester);
            SkinTrace.Event("host-manifest", "requester=" + requester
                + " entries=" + entries.Count
                + " allow=" + (allow ? 1 : 0));

            if (limit.AcceptOnce) {
                return SkinLock.Payload(allow, limit.RetryIn, limit.Strikes, true);
            }

            return SkinLock.Payload(allow);
        } catch (Exception ex) {
            Log("server request failed: " + ex);
            return new JValue("error");
        }
    }

    private static JToken OnServerFetch(uint sender, JToken token) {
        try {
            JObject payload = token as JObject;
            uint requester = RequesterId(sender, payload);
            if (requester == 0) {
                return new JValue("bad");
            }

            MpRateLimiter.Result limit = FetchLimiter.Check(requester, ServerTime);
            if (!limit.Allowed && !limit.AcceptOnce) {
                Log("rate limited fetch from player " + requester + " for "
                    + limit.RetryIn.ToString("0") + "s, strike " + limit.Strikes + ".");
                return SkinLock.Payload(false, limit.RetryIn, limit.Strikes);
            }

            JArray hashes = payload["hashes"] as JArray;
            HashSet<string> seen = new HashSet<string>();
            int sent = 0;

            if (hashes != null) {
                foreach (JToken item in hashes) {
                    string hash = item == null ? null : item.Value<string>();
                    if (!MpSkinCache.IsHash(hash)) {
                        continue;
                    }

                    string key = hash.ToLowerInvariant();
                    if (!seen.Add(key)) {
                        continue;
                    }

                    foreach (KeyValuePair<uint, MpSkinPackage> pair in Skins) {
                        if (!string.Equals(pair.Value.Hash, hash, StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }

                        uint player = PlayerIds.TryGetValue(pair.Key, out uint id) ? id : 0;
                        MultiplayerApi.Broadcast(ApplyChannel,
                            ToPayload(pair.Value, pair.Key, player), false, true);
                        sent++;
                    }
                }
            }

            Log("sent " + sent + " cached skins to player " + requester + ".");
            if (limit.AcceptOnce) {
                return SkinLock.Payload(false, limit.RetryIn, limit.Strikes, true);
            }

            return SkinLock.Payload(false);
        } catch (Exception ex) {
            Log("fetch failed: " + ex);
            return new JValue("error");
        }
    }

    private static void EnsureLocalPackage() {
        if (!MultiplayerApi.IsHost) {
            return;
        }

        if (!SpriteReplacerPlugin.ShareSkins) {
            return;
        }

        if (!MpBody.TryGetLocalIds(out uint network, out uint player)) {
            return;
        }

        if (!SharedMain.LocalWorldIsGenerated || !MpBody.HasLocalBody) {
            return;
        }

        if (Skins.ContainsKey(network)) {
            return;
        }

        if (IsDefaultSlot(SpriteReplacerPlugin.ActiveSlot)) {
            return;
        }

        MpSkinPackage package = MpSkinPackage.FromSlot(SpriteReplacerPlugin.ActiveSlot);
        if (package == null) {
            SkinTrace.Event("ensure-local-fail", SkinTrace.Ids(network, player)
                + " slot=" + (SpriteReplacerPlugin.ActiveSlot ?? "-"));
            return;
        }

        Skins[network] = package;
        PlayerIds[network] = player;
        LastContent[player] = package.ContentHash;
        SkinTrace.Event("ensure-local", SkinTrace.Ids(network, player)
            + " " + SkinTrace.PackageSummary(package));
    }

    private static void OnClientApply(JToken token) {
        try {
            JObject payload = token as JObject;
            if (payload == null) {
                return;
            }

            if (!SpriteReplacerPlugin.DownloadSkins) {
                return;
            }

            uint ownerNetwork = ReadUInt(payload, "clientId");
            uint ownerPlayer = ReadUInt(payload, "playerId");
            if (ownerNetwork == MpBody.LocalClientId) {
                return;
            }

            if (payload.Value<bool?>("clear") == true) {
                SkinTrace.Event("recv-clear", SkinTrace.Ids(ownerNetwork, ownerPlayer));
                Remove(ownerNetwork, ownerPlayer);
                return;
            }

            string hash = payload.Value<string>("hash") ?? string.Empty;
            if (MpSkinCache.IsHash(hash) &&
                Skins.TryGetValue(ownerNetwork, out MpSkinPackage existing) &&
                string.Equals(existing.Hash, hash, StringComparison.OrdinalIgnoreCase)) {
                SkinTrace.Event("recv-same", SkinTrace.Ids(ownerNetwork, ownerPlayer)
                    + " hash=" + (hash.Length <= 8 ? hash : hash.Substring(0, 8)));
                return;
            }

            MpSkinPackage package = Parse(payload);
            if (package == null) {
                SkinTrace.Event("recv-parse-fail", SkinTrace.Ids(ownerNetwork, ownerPlayer)
                    + " hash=" + (hash ?? "-"));
                return;
            }

            Cache.Store(package.Hash, package.Zip);
            Store(ownerNetwork, ownerPlayer, package);
            SkinTrace.Event("recv-apply", SkinTrace.Ids(ownerNetwork, ownerPlayer)
                + " " + SkinTrace.PackageSummary(package));
            ApplyPending();
        } catch (Exception ex) {
            Log("client apply failed: " + ex);
        }
    }

    private static void OnClientManifest(JToken token) {
        try {
            if (!SpriteReplacerPlugin.DownloadSkins) {
                return;
            }

            JObject payload = token as JObject;
            JArray skins = payload == null ? null : payload["skins"] as JArray;
            if (skins == null) {
                return;
            }

            if (!MpBody.TryGetLocalIds(out uint localNetwork, out _)) {
                return;
            }

            JArray missing = new JArray();
            HashSet<string> seen = new HashSet<string>();
            int cached = 0;

            foreach (JToken entry in skins) {
                JObject item = entry as JObject;
                if (item == null) {
                    continue;
                }

                uint owner = ReadUInt(item, "clientId");
                uint player = ReadUInt(item, "playerId");
                string hash = item.Value<string>("hash") ?? string.Empty;
                string setName = item.Value<string>("setName") ?? string.Empty;
                if (owner == 0 || owner == localNetwork || !MpSkinCache.IsHash(hash)) {
                    continue;
                }

                if (Skins.TryGetValue(owner, out MpSkinPackage existing) &&
                    string.Equals(existing.Hash, hash, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (Cache.TryRead(hash, out byte[] zip)) {
                    MpSkinPackage package = MpSkinPackage.FromZip(zip, hash, setName);
                    if (package != null) {
                        package.Species = ClampSpecies(item.Value<int?>("species") ?? 0);
                        Store(owner, player, package);
                        cached++;
                        continue;
                    }
                    Cache.Remove(hash);
                }

                string key = hash.ToLowerInvariant();
                if (seen.Add(key) && missing.Count < MpSkinRules.MaxEntries) {
                    missing.Add(hash);
                }
            }

            ApplyPending();

            if (missing.Count > 0) {
                RequestMissing(missing, localNetwork);
            }

            SkinTrace.Event("manifest", "entries=" + skins.Count
                + " cached=" + cached
                + " missing=" + missing.Count
                + " stored=" + Skins.Count
                + " pending=" + Pending.Count);
        } catch (Exception ex) {
            Log("client manifest failed: " + ex);
        }
    }

    private static void RequestMissing(JArray hashes, uint localNetwork) {
        try {
            JObject request = new JObject {
                ["clientId"] = localNetwork,
                ["hashes"] = hashes
            };
            MultiplayerApi.RequestServer(FetchChannel, request, OnFetchResponse, true);
        } catch (Exception ex) {
            Log("fetch request failed: " + ex.Message);
        }
    }

    private static void OnFetchResponse(JToken token) {
        if (token == null) {
            return;
        }

        Log("fetch response: " + token);
    }

    private static void OnSyncResponse(JToken token) {
        SkinLock.ApplyRemote(token);
        HandleRateLimit(token, true);
        Log("sync response: " + (token == null ? "null" : token.ToString()));
    }

    private static void OnUploadResponse(JToken token) {
        _awaiting = false;
        if (token != null && token.Type == JTokenType.String) {
            string text = token.Value<string>();
            Log("upload response: " + text);
            if (text != "busy") {
                _sentSignature = _pendingSignature;
            }
            return;
        }

        _sentSignature = _pendingSignature;
        SkinLock.ApplyRemote(token);
        HandleRateLimit(token, false);
    }

    private static void HandleRateLimit(JToken token, bool retryRequest) {
        JObject payload = token as JObject;
        if (payload == null) {
            return;
        }

        JToken retryToken = payload["retryIn"];
        if (retryToken == null) {
            return;
        }

        float retry = retryToken.Value<float>();
        if (retry <= 0f) {
            return;
        }

        int strikes = payload["strikes"] == null ? 1 : payload["strikes"].Value<int>();
        bool accepted = payload["accepted"] != null && payload["accepted"].Value<bool>();
        SetCooldown(retry, strikes, retryRequest, accepted);
    }

    private static void SetCooldown(float seconds, int strikes, bool retryRequest,
        bool accepted) {
        Cooldown.Set(Time.unscaledTime, seconds);
        if (retryRequest && !accepted) {
            _requested = false;
        }

        int whole = Cooldown.RemainingSeconds(Time.unscaledTime);
        SkinLock.AnnounceRateLimit(whole, strikes, accepted && !retryRequest);
        SkinMenuUI.RefreshLockUi();
        Log("rate limited for " + whole + "s (strike " + strikes
            + ", accepted=" + accepted + ").");
    }

    private static MpSkinPackage Parse(JObject payload) {
        string hash = payload.Value<string>("hash") ?? string.Empty;
        string setName = payload.Value<string>("setName") ?? string.Empty;
        string encoded = payload.Value<string>("zip") ?? string.Empty;

        byte[] zip;
        try {
            zip = Convert.FromBase64String(encoded);
        } catch (FormatException) {
            return null;
        }

        MpSkinPackage package = MpSkinPackage.FromZip(zip, hash, setName);
        if (package == null) {
            return null;
        }

        package.Species = ClampSpecies(payload.Value<int?>("species") ?? 0);
        return package;
    }

    private static int ClampSpecies(int species) {
        return species >= 0 && species <= 2 ? species : 0;
    }

    private static bool SendToServer(MpSkinPackage package, uint localNetwork, uint localPlayer) {
        bool sent = MultiplayerApi.RequestServer(FullChannel,
            ToPayload(package, localNetwork, localPlayer), OnUploadResponse, true);
        Log("sent local skin (" + package.SetName + ") network=" + localNetwork
            + " player=" + localPlayer + " result=" + sent + ".");
        return sent;
    }

    private static void Broadcast(MpSkinPackage package, uint localNetwork, uint localPlayer) {
        MultiplayerApi.Broadcast(ApplyChannel,
            ToPayload(package, localNetwork, localPlayer), false, true);
        Log("broadcast local skin (" + package.SetName + ") network=" + localNetwork
            + " player=" + localPlayer + ".");
    }

    private static void ClearRemoteViews() {
        foreach (KeyValuePair<uint, MpSkinPackage> pair in Skins) {
            if (pair.Key == MpBody.LocalClientId) {
                continue;
            }

            uint player = PlayerIds.TryGetValue(pair.Key, out uint id) ? id : 0;
            MpBody.ClearRemoteSkin(pair.Key, player);
        }

        if (MpBody.IsHost) {
            return;
        }

        HashSet<MpSkinPackage> packages = new HashSet<MpSkinPackage>(Skins.Values);
        foreach (MpSkinPackage pending in Pending.Values) {
            packages.Add(pending);
        }

        foreach (MpSkinPackage package in packages) {
            package.Dispose();
        }

        Skins.Clear();
        Pending.Clear();
        PlayerIds.Clear();
        Waiting.Clear();
    }

    // a player who leaves sends no clear, and a rejoin gets a new network id, so the old package would stay.
    private static void OnPlayerLeft(ScavPlayer leaving) {
        uint player = MpBody.KnetToUInt(leaving.playerId);
        if (player == 0 || player == MpBody.LocalPlayerId) {
            return;
        }

        List<uint> networks = new List<uint>();
        foreach (KeyValuePair<uint, uint> owner in PlayerIds) {
            if (owner.Value == player) {
                networks.Add(owner.Key);
            }
        }

        foreach (uint network in networks) {
            Remove(network, player);
        }
    }

    private static void Remove(uint ownerNetwork, uint ownerPlayer) {
        // host clearing *own* package: local body already ResetBodyToStock via ApplyLocalSkin.
        // clearRemoteSkin resolves remotes only and used to hit another player when
        // netBody.netId collided with their playerId (net=3 -> Sauce plrid 3).
        if (ownerNetwork != MpBody.LocalClientId && ownerPlayer != MpBody.LocalPlayerId) {
            MpBody.ClearRemoteSkin(ownerNetwork, ownerPlayer);
        }

        if (Skins.TryGetValue(ownerNetwork, out MpSkinPackage package)) {
            package.Dispose();
            Skins.Remove(ownerNetwork);
        }

        if (Pending.TryGetValue(ownerNetwork, out MpSkinPackage pending) &&
            !ReferenceEquals(pending, package)) {
            pending.Dispose();
        }

        Pending.Remove(ownerNetwork);
        PlayerIds.Remove(ownerNetwork);
        Waiting.Remove(ownerNetwork);
        if (ownerPlayer != 0) {
            LastContent.Remove(ownerPlayer);
        }
    }

    private static bool HasStoredSkin(uint playerId) {
        foreach (KeyValuePair<uint, uint> pair in PlayerIds) {
            if (pair.Value == playerId) {
                return true;
            }
        }

        return false;
    }

    private static void Store(uint ownerNetwork, uint ownerPlayer, MpSkinPackage package) {
        MpSkinPackage old = null;
        if (Skins.TryGetValue(ownerNetwork, out MpSkinPackage existing) &&
            !ReferenceEquals(existing, package)) {
            old = existing;
            // drop the old bind first; empty-Head slots must not keep prior head textures alive mid-frame.
            uint player = PlayerIds.TryGetValue(ownerNetwork, out uint id) ? id : ownerPlayer;
            SkinTrace.Event("store-replace", SkinTrace.Ids(ownerNetwork, ownerPlayer)
                + " old=" + SkinTrace.PackageSummary(old)
                + " new=" + SkinTrace.PackageSummary(package));
            MpBody.ClearRemoteSkin(ownerNetwork, player);
        } else {
            SkinTrace.Event("store", SkinTrace.Ids(ownerNetwork, ownerPlayer)
                + " " + SkinTrace.PackageSummary(package));
        }

        Skins[ownerNetwork] = package;
        PlayerIds[ownerNetwork] = ownerPlayer;
        LastContent[ownerPlayer] = package.ContentHash;
        Pending[ownerNetwork] = package;

        if (old != null) {
            old.Dispose();
        }
    }

    private static void ApplyPending() {
        if (Pending.Count == 0 || !SpriteReplacerPlugin.DownloadSkins) {
            return;
        }

        List<uint> owners = new List<uint>(Pending.Keys);
        foreach (uint owner in owners) {
            if (owner == MpBody.LocalClientId) {
                Pending.Remove(owner);
                continue;
            }

            MpSkinPackage package = Pending[owner];
            uint player = PlayerIds.TryGetValue(owner, out uint id) ? id : 0;
            if (MpBody.ApplyRemoteSkin(owner, player, package.Sprites, package.SetName, package.Species)) {
                Pending.Remove(owner);
                Waiting.Remove(owner);
                SkinTrace.Event("pending-ok", SkinTrace.Ids(owner, player)
                    + " " + SkinTrace.PackageSummary(package));
            } else if (Waiting.Add(owner)) {
                SkinTrace.Event("pending-wait", SkinTrace.Ids(owner, player)
                    + " " + SkinTrace.PackageSummary(package));
            }
        }
    }

    private static JObject ToPayload(MpSkinPackage package, uint ownerNetwork, uint ownerPlayer) {
        return new JObject {
            ["clientId"] = ownerNetwork,
            ["playerId"] = ownerPlayer,
            ["setName"] = package.SetName,
            ["species"] = package.Species,
            ["hash"] = package.Hash,
            // informational, the zip's marker entry decides.
            ["skinProtocol"] = package.Protocol,
            ["zip"] = Convert.ToBase64String(package.Zip)
        };
    }

    private static uint RequesterId(uint sender, JObject payload) {
        return sender != 0 ? sender : (payload == null ? 0 : ReadUInt(payload, "clientId"));
    }

    private static uint ReadUInt(JObject payload, string key) {
        JToken token = payload[key];
        return token == null ? 0 : token.Value<uint>();
    }

    private static void Reset() {
        _handlers = false;
        _requested = false;
        _resend = false;
        _lastSharedSlot = null;
        _sentSignature = null;
        _pendingSignature = null;
        _awaiting = false;
        _nextSample = 0f;
        _manualChoice = false;
        _localWire = new SendWindow();
        UploadWindows.Clear();
        HashSet<MpSkinPackage> packages = new HashSet<MpSkinPackage>(Skins.Values);
        foreach (MpSkinPackage package in Pending.Values) {
            packages.Add(package);
        }

        foreach (MpSkinPackage package in packages) {
            package.Dispose();
        }

        Skins.Clear();
        Pending.Clear();
        PlayerIds.Clear();
        LastContent.Clear();
        Waiting.Clear();
        UploadPolicy.Clear();
        RequestLimiter.Clear();
        FetchLimiter.Clear();
        Cooldown.Clear();
        SkinLock.Reset();
        MpBody.ClearRejected();
    }

    private static string CurrentSignature() {
        return SpriteReplacerPlugin.ActiveSlot + ":" + SpriteReplacerPlugin.LoadedSprites.Count;
    }

    // once a second, send the slot that is selected now. keypresses do not send.
    // default / numpad 0 clears the shared zip - never upload CustomSprites/.../default.
    private static void SampleLocalSkin(uint localNetwork, uint localPlayer) {
        if (!SpriteReplacerPlugin.ShareSkins) {
            return;
        }

        if (IsDefaultSlot(SpriteReplacerPlugin.ActiveSlot)
            || SpriteReplacerPlugin.LoadedSprites.Count == 0) {
            if (!string.Equals(_sentSignature, ClearedSignature, StringComparison.Ordinal)) {
                SkinTrace.Event("sample-clear", SkinTrace.Ids(localNetwork, localPlayer)
                    + " slot=" + (SpriteReplacerPlugin.ActiveSlot ?? "-")
                    + " sprites=" + SpriteReplacerPlugin.LoadedSprites.Count);
                ClearLocalSkin();
                _sentSignature = ClearedSignature;
                _pendingSignature = ClearedSignature;
                _lastSharedSlot = SkinSettings.DefaultFolder;
                _resend = false;
                _awaiting = false;
                SkinLock.HasLocalSkin = false;
            }
            return;
        }

        if (_resend && !string.Equals(_lastSharedSlot, SpriteReplacerPlugin.ActiveSlot,
                StringComparison.Ordinal)) {
            _resend = false;
        }

        if (!SkinLock.CanChangeLocal && !_resend) {
            return;
        }

        string signature = CurrentSignature();
        bool changed = !string.Equals(signature, _sentSignature, StringComparison.Ordinal);
        if (!changed && !_resend) {
            return;
        }

        if (_awaiting) {
            return;
        }

        MpSkinPackage package = MpSkinPackage.FromSlot(SpriteReplacerPlugin.ActiveSlot);
        if (package == null) {
            SkinTrace.Event("sample-pack-fail", SkinTrace.Ids(localNetwork, localPlayer)
                + " slot=" + (SpriteReplacerPlugin.ActiveSlot ?? "-"));
            return;
        }

        if (!TakeSendSlot(ref _localWire, Time.unscaledTime)) {
            package.Dispose();
            SkinTrace.Event("sample-wire-limit", SkinTrace.Ids(localNetwork, localPlayer));
            return;
        }

        SkinTrace.Event("sample-send", SkinTrace.Ids(localNetwork, localPlayer)
            + " host=" + (MpBody.IsHost ? 1 : 0)
            + " resend=" + (_resend ? 1 : 0)
            + " sig=" + signature
            + " " + SkinTrace.PackageSummary(package));

        if (MpBody.IsHost) {
            Store(localNetwork, localPlayer, package);
            Broadcast(package, localNetwork, localPlayer);
            SkinLock.HasLocalSkin = true;
            _sentSignature = signature;
            _resend = false;
            _lastSharedSlot = SpriteReplacerPlugin.ActiveSlot;
            SkinLock.HadLocalSkin = true;
            return;
        }

        _pendingSignature = signature;
        bool sent = SendToServer(package, localNetwork, localPlayer);
        package.Dispose();
        if (!sent) {
            SkinTrace.Event("sample-send-fail", SkinTrace.Ids(localNetwork, localPlayer));
            return;
        }

        _awaiting = true;
        _resend = false;
        _lastSharedSlot = SpriteReplacerPlugin.ActiveSlot;
        SkinLock.HasLocalSkin = true;
        SkinLock.HadLocalSkin = true;
        if (_manualChoice) {
            _manualChoice = false;
            SkinLock.ConsumeChoice();
        }
    }
    private struct SendWindow {
        internal float Start;
        internal int Count;
    }

    private static bool TakeSendSlot(ref SendWindow window, float now) {
        if (window.Start <= 0f || now - window.Start >= WireSeconds) {
            window.Start = now;
            window.Count = 0;
        }

        if (window.Count >= WireLimit) {
            return false;
        }

        window.Count++;
        return true;
    }

    private static bool TakeUploadSlot(uint player, float now) {
        UploadWindows.TryGetValue(player, out SendWindow window);
        bool taken = TakeSendSlot(ref window, now);
        UploadWindows[player] = window;
        return taken;
    }

    private static void Log(string message) {
        // quiet unless something failed or got rejected
        if (SpriteReplacerPlugin.Log == null || string.IsNullOrEmpty(message)) {
            return;
        }
        string lower = message.ToLowerInvariant();
        if (lower.Contains("fail") || lower.Contains("reject") || lower.Contains("dropped")
            || lower.Contains("rate limited")) {
            SpriteReplacerPlugin.Log.LogWarning("[MP skin] " + message);
        }
    }
}