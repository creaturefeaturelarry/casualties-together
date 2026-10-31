using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using KrokV5Optimization;

namespace Together;

public enum CraftReason : byte {
    Ok = 0,
    Missing = 1,
    Unreachable = 2,
    Taken = 3,
    Unconscious = 4,
    BadRecipe = 5,
    SpawnFailed = 6,
    StillSyncing = 7,
}

/// <summary>
/// One click is one attempt. The client reserves ingredients until the receipt.
/// The server spends and spawns, or denies with no writes.
/// </summary>
public static class CraftCommit {
    public struct CraftStep {
        public bool Pending;
        public bool Accepted;
        public CraftReason Reason;
    }

    static bool _stepOpen;
    static bool _stepDone;
    static uint _stepAttempt;
    static CraftStep _stepResult;
    const float ResendSeconds = 2f;
    const float TombstoneSeconds = 2f;
    const int Remember = 64;
    const int GateTicks = 240;
    const int LiquidTrustTicks = 45;

    enum PoolMode { SyncedReachable, AnyReachable, Wide }

    sealed class Claim {
        public ushort syncId;
        public bool exclusive;
        public float condition;
        public string liquidId;
        public float liquid;
    }

    sealed class Attempt {
        public uint session;
        public uint id;
        public ushort recipe;
        public ushort[] ids;
        public List<Claim> claims;
        public float sentAt;
    }

    struct Saved {
        public Item item;
        public float condition;
        public bool liquid;
        public List<string> ids;
        public List<float> amounts;
    }

    struct Snap {
        public Item item;
        public float condition;
        public bool liquid;
        public List<string> ids;
        public List<float> amounts;
    }

    sealed class Liq {
        public byte id;
        public ushort ml;
    }

    sealed class Spend {
        public ushort syncId;
        public byte version;
        public bool destroyed;
        public float condition;
        public List<Liq> liquids;
    }

    sealed class Receipt {
        public uint session;
        public uint attempt;
        public bool accepted;
        public CraftReason reason;
        public ushort tick;
        public ushort resultId;
        public List<Spend> spends;
    }

    sealed class PlayerCrafts {
        public uint session;
        public readonly Dictionary<uint, Receipt> receipts = new Dictionary<uint, Receipt>();
        public readonly Queue<uint> order = new Queue<uint>();
    }

    sealed class Gate {
        public ushort tick;
        public byte version;
        public bool destroyed;
        public bool holdCondition;
        public bool conditionOpen;
        public float condition;
        public bool holdLiquid;
        public bool liquidOpen;
        public List<Liq> liquids;
    }

    static uint _session;
    static uint _next = 1;
    static bool _running;
    static int _depth;
    static bool _collecting;
    static int _poolFrame = -1;
    static List<Item> _pool;

    static readonly Dictionary<uint, Attempt> Attempts = new Dictionary<uint, Attempt>();
    static readonly HashSet<ushort> Exclusive = new HashSet<ushort>();
    static readonly Dictionary<ushort, float> ConditionHold = new Dictionary<ushort, float>();
    static readonly Dictionary<ushort, Dictionary<string, float>> LiquidHold = new Dictionary<ushort, Dictionary<string, float>>();
    static readonly Dictionary<ushort, float> Tombstones = new Dictionary<ushort, float>();
    static readonly List<Saved> SavedItems = new List<Saved>();
    static readonly Dictionary<knetid, PlayerCrafts> ServerCrafts = new Dictionary<knetid, PlayerCrafts>();
    static readonly Dictionary<ushort, Gate> Gates = new Dictionary<ushort, Gate>();
    // ingredient id plus the generation we lifted. a recycled id is the result, and that one still writes.
    static readonly Dictionary<ushort, byte> Silent = new Dictionary<ushort, byte>();
    // instance id to the slot it was pulled from. a newer byte for a different slot clears it.
    static readonly Dictionary<int, int> YieldedFrom = new Dictionary<int, int>();
    static readonly HashSet<uint> Seen = new HashSet<uint>();
    static readonly Queue<uint> SeenOrder = new Queue<uint>();
    static readonly List<byte> SeenLiquidId = new List<byte>();
    static readonly List<ushort> SeenLiquidMl = new List<ushort>();

    internal static void Tick() {
        if (!Net.IsRunning) {
            if (_running)
                ClearAll();
            _running = false;
            return;
        }
        _running = true;
        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);

        if (!Net.IsServer) {
            float now = Time.realtimeSinceStartup;
            foreach (var pair in Attempts) {
                Attempt attempt = pair.Value;
                if (now - attempt.sentAt >= ResendSeconds)
                    SendAttempt(attempt);
            }
        }
        ExpireTombstones();
        ExpireGates();
    }

    internal static void TryClick(PlayerCamera cam) {
        if (cam == null)
            return;
        Body body = Util.GetLocalBody();
        if (body == null || cam.selectedRecipe < 0 || cam.selectedRecipe >= Recipes.recipes.Count) {
            FailLocal(cam, CraftReason.BadRecipe);
            return;
        }
        if (!body.conscious) {
            FailLocal(cam, CraftReason.Unconscious);
            return;
        }

        Recipe recipe = Recipes.recipes[cam.selectedRecipe];
        if (!TryClaim(body, recipe, out List<Claim> claims, out ushort[] ids, out CraftReason reason)) {
            FailLocal(cam, reason);
            return;
        }

        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint id = _next++;
        if (_next == 0)
            _next = 1;

        var attempt = new Attempt {
            session = _session,
            id = id,
            recipe = (ushort)recipe.index,
            ids = ids,
            claims = claims,
            sentAt = Time.realtimeSinceStartup,
        };
        Hold(attempt);

        if (Net.IsServer) {
            if (!body.TryGetNetBody(out NetBody nb)) {
                Release(id);
                FailLocal(cam, CraftReason.BadRecipe);
                return;
            }
            Receipt receipt = Commit(nb, _session, id, recipe, ids);
            FinishLocal(cam, recipe, receipt);
            return;
        }

        SendAttempt(attempt);
        cam.RefreshRecipeList();
    }

    // one craft for the local body. the host finishes before this returns.
    // a client returns pending, and a later call with any recipe polls that same step until the receipt lands.
    public static CraftStep Request(PlayerCamera cam, Recipe recipe) {
        if (_stepOpen) {
            if (!_stepDone)
                return new CraftStep { Pending = true, Accepted = false, Reason = CraftReason.Ok };
            CraftStep done = _stepResult;
            _stepOpen = false;
            _stepDone = false;
            return done;
        }

        if (cam == null || recipe == null || !Net.IsRunning)
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };

        int index = recipe.index;
        if (Recipes.recipes == null || index < 0 || index >= Recipes.recipes.Count) {
            FailLocal(cam, CraftReason.BadRecipe);
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };
        }

        recipe = Recipes.recipes[index];
        cam.selectedRecipe = index;
        Body body = Util.GetLocalBody();
        if (body == null) {
            FailLocal(cam, CraftReason.BadRecipe);
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };
        }
        if (!body.conscious) {
            FailLocal(cam, CraftReason.Unconscious);
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.Unconscious };
        }
        if (!TryClaim(body, recipe, out List<Claim> claims, out ushort[] ids, out CraftReason reason)) {
            FailLocal(cam, reason);
            return new CraftStep { Pending = false, Accepted = false, Reason = reason };
        }

        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint id = _next++;
        if (_next == 0)
            _next = 1;

        var attempt = new Attempt {
            session = _session,
            id = id,
            recipe = (ushort)recipe.index,
            ids = ids,
            claims = claims,
            sentAt = Time.realtimeSinceStartup,
        };
        Hold(attempt);

        if (Net.IsServer) {
            if (!body.TryGetNetBody(out NetBody nb)) {
                Release(id);
                FailLocal(cam, CraftReason.BadRecipe);
                return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };
            }
            Receipt receipt = Commit(nb, _session, id, recipe, ids);
            FinishLocal(cam, recipe, receipt);
            return new CraftStep { Pending = false, Accepted = receipt.accepted, Reason = receipt.reason };
        }

        SendAttempt(attempt);
        cam.RefreshRecipeList();
        _stepOpen = true;
        _stepDone = false;
        _stepAttempt = id;
        return new CraftStep { Pending = true, Accepted = false, Reason = CraftReason.Ok };
    }

    internal static void HandleServer(knetid clientId, uint session, uint attempt, ushort recipeIndex, ushort[] ids) {
        if (ids == null)
            ids = Array.Empty<ushort>();
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb) || plr == null || pb == null)
            return;

        PlayerCrafts crafts = CraftsFor(clientId, session);
        if (crafts.receipts.TryGetValue(attempt, out Receipt cached)) {
            SendReceipt(plr.playerId, cached);
            return;
        }

        if (plr.body == null || !plr.body.conscious) {
            DenyAndSend(plr, pb, crafts, session, attempt, null, CraftReason.Unconscious, ids);
            return;
        }
        if (recipeIndex >= Recipes.recipes.Count) {
            DenyAndSend(plr, pb, crafts, session, attempt, null, CraftReason.BadRecipe, ids);
            return;
        }

        Recipe recipe = Recipes.recipes[recipeIndex];
        Receipt receipt = Commit(pb, session, attempt, recipe, ids);
        SendReceipt(plr.playerId, receipt);
    }

    internal static void OnReceipt(NetDataReader reader) {
        Receipt receipt = ReadReceipt(reader);
        bool mine = receipt.session == _session;
        if (mine && !Seen.Add(receipt.attempt))
            return;
        if (mine) {
            SeenOrder.Enqueue(receipt.attempt);
            while (SeenOrder.Count > Remember)
                Seen.Remove(SeenOrder.Dequeue());
        }

        // another player's craft. spend the ingredients anyway. their session is not ours, and the ui stays theirs.
        if (receipt.accepted)
            ApplySpend(receipt);
        if (!mine)
            return;

        if (receipt.accepted)
            NoteMade(receipt);
        else if (receipt.reason == CraftReason.Taken)
            Tombstone(receipt);

        Release(receipt.attempt);
        if (_stepOpen && receipt.attempt == _stepAttempt) {
            _stepDone = true;
            _stepResult = new CraftStep { Pending = false, Accepted = receipt.accepted, Reason = receipt.reason };
        }
        if (!receipt.accepted)
            Util.DoAlert(ReasonText(receipt.reason));
        else
            NoteMade(receipt);
        RefreshIfOpen();
    }

    internal static List<Item> MatchList(Recipe recipe, bool thorough) {
        if (_depth > 0)
            return thorough ? new List<Item>() : null;
        _depth++;
        try {
            List<Item> pool = Pool(PoolMode.SyncedReachable);
            return Evaluate(recipe, pool, reserve: true, thorough: thorough, capture: false, out _);
        } finally {
            _depth--;
        }
    }

    internal static void OpenPool() {
        _poolFrame = Time.frameCount;
        _pool = null;
    }

    internal static void ClosePool() {
        _poolFrame = -1;
        _pool = null;
    }

    internal static bool EatDestroyed(SyncInfo si) {
        if (si == null || si.item == null)
            return false;
        ushort id = (ushort)si.syncId;
        if (!Gates.TryGetValue(id, out Gate gate) || !gate.destroyed)
            return false;
        // les bumps version when it reuses the id. this construct is the next item, seat it.
        if (si.Version != gate.version)
            return false;
        Consume(si.item);
        return true;
    }

    // off the body without dropitem. dropitem tells the host this id hit the ground, and the id may already be the result.
    internal static void StepAside(Item item) {
        if (item == null)
            return;
        int inst = item.GetInstanceID();
        int from = -1;
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
        if (ici.inv_slot != null)
            from = ici.inv_slot.slot;
        if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
            Silent[(ushort)si.syncId] = si.Version;
        MoveCommit.PushApply();
        ItemSync.SuppressContainerWrite++;
        try {
            ItemSync.SafeUnloadItem(item);
            Freeze(item);
        } finally {
            ItemSync.SuppressContainerWrite--;
            MoveCommit.PopApply();
        }
        YieldedFrom[inst] = from;
    }

    internal static void FinishAside(Item item) {
        if (item == null)
            return;
        int inst = item.GetInstanceID();
        bool was = YieldedFrom.ContainsKey(inst);
        ushort sid = 0;
        bool had = ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null;
        if (had)
            sid = (ushort)si.syncId;
        YieldedFrom.Remove(inst);
        if (had)
            Silent.Remove(sid);
        if (was && item.rb != null && item.transform.parent == null)
            item.rb.simulated = true;
    }

    // a byte for a different slot is a real move. the old craft hold should let go.
    internal static void ReleaseIfLeaving(Item item, int slot) {
        if (item == null || !YieldedFrom.TryGetValue(item.GetInstanceID(), out int from))
            return;
        if (from >= 0 && from != slot)
            FinishAside(item);
    }

    internal static void Consume(Item item) {
        if (item == null)
            return;
        int inst = item.GetInstanceID();
        ushort sid = 0;
        bool had = ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null;
        if (had)
            sid = (ushort)si.syncId;
        StepAside(item);
        if (item != null)
            UnityEngine.Object.Destroy(item.gameObject);
        YieldedFrom.Remove(inst);
        if (had)
            Silent.Remove(sid);
    }

    internal static bool Yielded(Item item) {
        return item != null && YieldedFrom.ContainsKey(item.GetInstanceID());
    }

    internal static bool Silenced(SyncInfo si) {
        if (si == null)
            return false;
        return Silent.TryGetValue((ushort)si.syncId, out byte ver) && ver == si.Version;
    }

    static void Freeze(Item item) {
        if (item == null || item.rb == null)
            return;
        item.rb.velocity = Vector2.zero;
        item.rb.angularVelocity = 0f;
        item.rb.simulated = false;
    }

    internal static bool BlockCondition(knetid syncId, float incoming) {
        ushort id = (ushort)syncId;
        if (!Gates.TryGetValue(id, out Gate gate) || gate.destroyed || !gate.holdCondition || gate.conditionOpen)
            return false;
        if (OtherGen(id, gate))
            return false;
        if (incoming <= gate.condition + 0.0001f) {
            gate.conditionOpen = true;
            Relax(id, gate);
            return false;
        }
        return true;
    }

    internal static void ClearSeenLiquids() {
        SeenLiquidId.Clear();
        SeenLiquidMl.Clear();
    }

    internal static void SeeLiquid(byte id, ushort ml) {
        SeenLiquidId.Add(id);
        SeenLiquidMl.Add(ml);
    }

    internal static bool SkipSeenLiquids(knetid syncId) {
        ushort id = (ushort)syncId;
        if (!Gates.TryGetValue(id, out Gate gate) || gate.destroyed || !gate.holdLiquid || gate.liquidOpen)
            return false;
        if (OtherGen(id, gate))
            return false;
        if (LiquidsLookStale(gate))
            return true;
        if (!LiquidsCaughtUp(gate) && LiteEntitySystem.Utils.SequenceDiff(Net.ServerTick, gate.tick) < LiquidTrustTicks)
            return true;
        gate.liquidOpen = true;
        Relax(id, gate);
        return false;
    }

    static void FailLocal(PlayerCamera cam, CraftReason reason) {
        Util.DoAlert(ReasonText(reason));
        cam.RefreshRecipeList();
    }

    static void FinishLocal(PlayerCamera cam, Recipe recipe, Receipt receipt) {
        Release(receipt.attempt);
        if (!Seen.Add(receipt.attempt))
            return;
        SeenOrder.Enqueue(receipt.attempt);
        if (!receipt.accepted) {
            if (receipt.reason == CraftReason.Taken)
                Tombstone(receipt);
            Util.DoAlert(ReasonText(receipt.reason));
        } else if (recipe != null) {
            recipe.hasMadeBefore = true;
        }
        cam.RefreshRecipeList();
    }

    static bool TryClaim(Body body, Recipe recipe, out List<Claim> claims, out ushort[] ids, out CraftReason reason) {
        claims = null;
        ids = null;
        reason = CraftReason.Missing;
        List<Item> synced = Collect(body, PoolMode.SyncedReachable);
        List<Item> matched = Evaluate(recipe, synced, reserve: true, thorough: false, capture: true, out claims);
        if (matched != null && claims != null) {
            ids = UniqueIds(claims);
            reason = CraftReason.Ok;
            return true;
        }
        if (Evaluate(recipe, synced, reserve: false, thorough: false, capture: false, out _) != null) {
            reason = CraftReason.Missing;
            return false;
        }
        if (Evaluate(recipe, Collect(body, PoolMode.AnyReachable), reserve: false, thorough: false, capture: false, out _) != null) {
            reason = CraftReason.StillSyncing;
            return false;
        }
        if (Evaluate(recipe, Collect(body, PoolMode.Wide), reserve: false, thorough: false, capture: false, out _) != null) {
            reason = CraftReason.Unreachable;
            return false;
        }
        reason = CraftReason.Missing;
        return false;
    }

    static List<Item> Pool(PoolMode mode) {
        if (mode == PoolMode.SyncedReachable && _poolFrame == Time.frameCount && _pool != null)
            return _pool;
        Body body = Util.GetLocalBody();
        List<Item> list = Collect(body, mode);
        if (mode == PoolMode.SyncedReachable && _poolFrame == Time.frameCount)
            _pool = list;
        return list;
    }

    static List<Item> Collect(Body body, PoolMode mode) {
        var list = new List<Item>();
        if (body == null || _collecting)
            return list;
        _collecting = true;
        try {
            List<Item> inventory = body.GetAllItemsThorough();
            for (int i = 0; i < inventory.Count; i++)
                AddItem(list, inventory[i], mode, reachable: true);
            Collider2D[] hits = Physics2D.OverlapCircleAll(body.transform.position, 10f, LayerMask.GetMask("Item"));
            for (int i = 0; i < hits.Length; i++) {
                if (!hits[i].TryGetComponent(out Item item))
                    continue;
                if (!body.DoPickupCheck(item, true))
                    continue;
                bool reach = ItemSync.CheckIfBodyReachThisItem(item, body);
                if (mode != PoolMode.Wide && !reach)
                    continue;
                AddItem(list, item, mode, reach);
            }
            return list;
        } finally {
            _collecting = false;
        }
    }

    static void AddItem(List<Item> list, Item item, PoolMode mode, bool reachable) {
        if (item == null)
            return;
        if (mode == PoolMode.Wide && !reachable) {
            list.Add(item);
            return;
        }
        bool synced = ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null;
        if (mode == PoolMode.SyncedReachable && !synced)
            return;
        list.Add(item);
    }

    static List<Item> Evaluate(Recipe recipe, List<Item> pool, bool reserve, bool thorough, bool capture, out List<Claim> claims) {
        claims = null;
        if (recipe == null || recipe.items == null)
            return thorough ? new List<Item>() : null;
        List<Item> working = pool;
        bool restore = false;
        try {
            if (reserve || Tombstones.Count > 0) {
                working = Filter(pool, reserve);
                restore = true;
            }
            var result = new List<Item>();
            var used = new List<Item>();
            for (int i = 0; i < recipe.items.Count; i++) {
                RecipeItem ingredient = recipe.items[i];
                Item found = ingredient.GetMatchingItem(working, used);
                if (found != null) {
                    result.Add(found);
                    if (ingredient.destroyItem && !ingredient.isLiquid)
                        used.Add(found);
                } else if (thorough) {
                    result.Add(null);
                } else {
                    return null;
                }
            }
            if (capture)
                claims = BuildClaims(recipe, result);
            if (capture && claims == null)
                return null;
            return result;
        } finally {
            if (restore)
                Restore();
        }
    }

    static List<Item> Filter(List<Item> pool, bool reserve) {
        var working = new List<Item>();
        SavedItems.Clear();
        for (int i = 0; i < pool.Count; i++) {
            Item item = pool[i];
            if (item == null)
                continue;
            if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null) {
                ushort id = (ushort)si.syncId;
                if (Tombstoned(id))
                    continue;
                if (reserve && Exclusive.Contains(id))
                    continue;
                if (reserve)
                    SaveAndReduce(item, id);
            }
            working.Add(item);
        }
        return working;
    }

    static void SaveAndReduce(Item item, ushort id) {
        for (int i = 0; i < SavedItems.Count; i++) {
            if (SavedItems[i].item == item)
                return;
        }
        float condition = item.condition;
        List<string> ids = null;
        List<float> amounts = null;
        bool touch = false;
        if (ConditionHold.TryGetValue(id, out float hold) && hold > 0.0001f) {
            item.condition = Mathf.Max(0f, item.condition - hold);
            touch = true;
        }
        if (LiquidHold.TryGetValue(id, out Dictionary<string, float> byLiquid) && item.TryGetComponent(out WaterContainerItem wci)) {
            ids = new List<string>();
            amounts = new List<float>();
            for (int i = 0; i < wci.stack.Count; i++) {
                ids.Add(wci.stack[i].liquidId);
                amounts.Add(wci.stack[i].amount);
                if (byLiquid.TryGetValue(wci.stack[i].liquidId, out float take) && take > 0f) {
                    LiquidStack entry = wci.stack[i];
                    entry.amount = Mathf.Max(0f, entry.amount - take);
                    wci.stack[i] = entry;
                }
            }
            touch = true;
        }
        if (!touch)
            return;
        SavedItems.Add(new Saved {
            item = item,
            condition = condition,
            liquid = ids != null,
            ids = ids,
            amounts = amounts,
        });
    }

    static void Restore() {
        for (int i = 0; i < SavedItems.Count; i++) {
            Saved saved = SavedItems[i];
            if (saved.item == null)
                continue;
            saved.item.condition = saved.condition;
            if (!saved.liquid || !saved.item.TryGetComponent(out WaterContainerItem wci))
                continue;
            wci.stack.Clear();
            for (int j = 0; j < saved.ids.Count; j++)
                wci.stack.Add(new LiquidStack(saved.ids[j], saved.amounts[j]));
        }
        SavedItems.Clear();
    }

    static List<Claim> BuildClaims(Recipe recipe, List<Item> matched) {
        var claims = new List<Claim>();
        for (int i = 0; i < matched.Count; i++) {
            Item item = matched[i];
            if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
                return null;
            var claim = new Claim { syncId = (ushort)si.syncId };
            RecipeItem ingredient = recipe.items[i];
            if (ingredient.destroyItem && !ingredient.isLiquid)
                claim.exclusive = true;
            else if (ingredient.isLiquid)
                FillLiquid(ingredient, item, claim);
            else
                FillCondition(ingredient, item, claim);
            Merge(claims, claim);
        }
        return claims;
    }

    static void Merge(List<Claim> claims, Claim claim) {
        for (int i = 0; i < claims.Count; i++) {
            Claim have = claims[i];
            if (have.syncId != claim.syncId)
                continue;
            if (claim.exclusive)
                have.exclusive = true;
            have.condition += claim.condition;
            if (claim.liquid > 0f && claim.liquidId != null) {
                if (have.liquidId == null || have.liquidId == claim.liquidId) {
                    have.liquidId = claim.liquidId;
                    have.liquid += claim.liquid;
                } else {
                    have.exclusive = true;
                }
            }
            return;
        }
        claims.Add(claim);
    }

    static void FillCondition(RecipeItem ingredient, Item item, Claim claim) {
        try {
            if (ingredient.specific) {
                claim.condition = ingredient.minimumCondition;
                return;
            }
            CraftingQuality met = Item.GetQualityThatMeetsCriteria(ingredient.quality, item.Stats.qualities);
            if (met == null || Mathf.Abs(met.amount) < 0.0001f) {
                claim.exclusive = true;
                return;
            }
            claim.condition = ingredient.quality.amount / met.amount;
        } catch (Exception) {
            claim.exclusive = true;
        }
    }

    static void FillLiquid(RecipeItem ingredient, Item item, Claim claim) {
        try {
            if (!item.TryGetComponent(out WaterContainerItem wci)) {
                claim.exclusive = true;
                return;
            }
            if (ingredient.specific) {
                List<float> drains = wci.CalculateDrainSingleLiquid(ingredient.specificId, ingredient.minimumCondition);
                float take = 0f;
                for (int i = 0; i < drains.Count; i++)
                    take += drains[i];
                claim.liquidId = ingredient.specificId;
                claim.liquid = take;
                return;
            }
            for (int i = 0; i < wci.stack.Count; i++) {
                LiquidStack stack = wci.stack[i];
                if (!Liquids.Registry.TryGetValue(stack.liquidId, out var value) || value == null)
                    continue;
                CraftingQuality met = Item.GetQualityThatMeetsCriteria(ingredient.quality, value.GetScaledQualities(stack.amount));
                if (met == null || Mathf.Abs(met.amount) < 0.0001f)
                    continue;
                float max = Mathf.Lerp(0f, stack.amount, ingredient.quality.amount / met.amount);
                claim.liquidId = stack.liquidId;
                claim.liquid = Mathf.Min(stack.amount, max);
                return;
            }
            claim.exclusive = true;
        } catch (Exception) {
            claim.exclusive = true;
        }
    }

    static ushort[] UniqueIds(List<Claim> claims) {
        var ids = new List<ushort>();
        for (int i = 0; i < claims.Count; i++) {
            ushort id = claims[i].syncId;
            if (!ids.Contains(id))
                ids.Add(id);
        }
        return ids.ToArray();
    }

    static void Hold(Attempt attempt) {
        Attempts[attempt.id] = attempt;
        for (int i = 0; i < attempt.claims.Count; i++)
            Fold(attempt.claims[i], 1f);
    }

    static void Release(uint id) {
        if (!Attempts.TryGetValue(id, out Attempt attempt))
            return;
        for (int i = 0; i < attempt.claims.Count; i++)
            Fold(attempt.claims[i], -1f);
        Attempts.Remove(id);
    }

    static void Fold(Claim claim, float sign) {
        if (claim.exclusive) {
            if (sign > 0f)
                Exclusive.Add(claim.syncId);
            else
                Exclusive.Remove(claim.syncId);
            return;
        }
        if (claim.condition > 0f) {
            float next = (ConditionHold.TryGetValue(claim.syncId, out float have) ? have : 0f) + sign * claim.condition;
            if (next <= 0.0001f)
                ConditionHold.Remove(claim.syncId);
            else
                ConditionHold[claim.syncId] = next;
        }
        if (claim.liquid <= 0f || claim.liquidId == null)
            return;
        if (!LiquidHold.TryGetValue(claim.syncId, out Dictionary<string, float> byLiquid)) {
            if (sign < 0f)
                return;
            byLiquid = new Dictionary<string, float>();
            LiquidHold[claim.syncId] = byLiquid;
        }
        float left = (byLiquid.TryGetValue(claim.liquidId, out float held) ? held : 0f) + sign * claim.liquid;
        if (left <= 0.0001f) {
            byLiquid.Remove(claim.liquidId);
            if (byLiquid.Count == 0)
                LiquidHold.Remove(claim.syncId);
        } else {
            byLiquid[claim.liquidId] = left;
        }
    }

    static bool Tombstoned(ushort id) {
        if (!Tombstones.TryGetValue(id, out float until))
            return false;
        knetid key = id;
        if (Time.time >= until || !ItemSync.TryGetItemSyncInfo(key, out _)) {
            Tombstones.Remove(id);
            return false;
        }
        return true;
    }

    static void Tombstone(Receipt receipt) {
        float until = Time.time + TombstoneSeconds;
        if (receipt.spends == null)
            return;
        for (int i = 0; i < receipt.spends.Count; i++)
            Tombstones[receipt.spends[i].syncId] = until;
    }

    static void ExpireTombstones() {
        if (Tombstones.Count == 0)
            return;
        _drop.Clear();
        foreach (var pair in Tombstones) {
            knetid key = pair.Key;
            if (Time.time >= pair.Value || !ItemSync.TryGetItemSyncInfo(key, out _))
                _drop.Add(pair.Key);
        }
        for (int i = 0; i < _drop.Count; i++)
            Tombstones.Remove(_drop[i]);
    }

    static readonly List<ushort> _drop = new List<ushort>();

    static Receipt Commit(NetBody pb, uint session, uint attempt, Recipe recipe, ushort[] ids) {
        knetid playerId = pb.plr != null ? pb.plr.playerId : default;
        PlayerCrafts crafts = CraftsFor(playerId, session);
        if (crafts.receipts.TryGetValue(attempt, out Receipt cached))
            return cached;

        var resolved = new List<Item>();
        for (int i = 0; i < ids.Length; i++) {
            knetid syncId = ids[i];
            if (!ItemSync.TryGetItemSyncInfo(syncId, out SyncInfo si) || si == null || !si.IsItem() || si.item == null)
                return Store(crafts, Deny(session, attempt, CraftReason.Taken, ids, pb, recipe));
            if (!ItemSync.CheckIfBodyReachThisItem(si, pb.body))
                return Store(crafts, Deny(session, attempt, CraftReason.Unreachable, ids, pb, recipe));
            resolved.Add(si.item);
        }

        var favourites = new List<Item>();
        var favouriteBits = new List<bool>();
        List<Item> matched = null;
        try {
            for (int i = 0; i < resolved.Count; i++) {
                favourites.Add(resolved[i]);
                favouriteBits.Add(resolved[i].favourited);
                resolved[i].favourited = false;
            }
            matched = PlayerCamera_TryCraft_MultiplayerPatch.CheckIfTheseItemsAreEnoughForTheRecipe(recipe, resolved);
        } finally {
            for (int i = 0; i < favourites.Count; i++) {
                if (favourites[i] != null)
                    favourites[i].favourited = favouriteBits[i];
            }
        }

        if (matched == null)
            return Store(crafts, Deny(session, attempt, CraftReason.Missing, ids, pb, recipe));

        // take consumed items off their slots before the result is created.
        // autopickup only sees empty slots, so a full inventory would leave the result on the ground.
        // this is not a drop. a drop announcement reuses the ingredient id for the result and throws that on the ground.
        List<Held> lifted = LiftIngredients(matched, recipe);
        CraftSpawn spawn;
        try {
            spawn = PlayerCamera_TryCraft_MultiplayerPatch.RecipeResult_SpawnResult(recipe.INT, recipe.result, pb.body);
        } catch (Exception e) {
            Plugin.log.LogWarning("craft spawn " + e.Message);
            spawn = CraftSpawn.Fail(null);
        }
        if (spawn.failed) {
            spawn.Discard();
            PutBack(lifted);
            return Store(crafts, Deny(session, attempt, CraftReason.SpawnFailed, ids, pb, recipe));
        }

        ushort resultId = 0;
        if (spawn.item != null && ItemSync.TryGetSyncInfo(spawn.item, out SyncInfo spawned) && spawned != null)
            resultId = (ushort)spawned.syncId;
        // grab the generation before useitem destroys the ingredient. the id gets recycled after that.
        ushort[] spendIds = new ushort[matched.Count];
        byte[] spendVers = new byte[matched.Count];
        for (int n = 0; n < matched.Count; n++) {
            if (matched[n] != null && ItemSync.TryGetSyncInfo(matched[n], out SyncInfo ingredient) && ingredient != null) {
                spendIds[n] = (ushort)ingredient.syncId;
                spendVers[n] = ingredient.Version;
            }
        }

        var snaps = new List<Snap>();
        var exclusive = new List<int>();
        try {
            for (int i = 0; i < matched.Count; i++) {
                RecipeItem ingredient = recipe.items[i];
                if (ingredient.destroyItem && !ingredient.isLiquid) {
                    exclusive.Add(i);
                    continue;
                }
                snaps.Add(TakeSnap(matched[i]));
                ingredient.UseItem(matched[i]);
            }
        } catch (Exception e) {
            Plugin.log.LogWarning("craft spend " + e.Message);
            RestoreSnaps(snaps);
            spawn.Discard();
            PutBack(lifted);
            return Store(crafts, Deny(session, attempt, CraftReason.SpawnFailed, ids, pb, recipe));
        }

        for (int i = 0; i < exclusive.Count; i++) {
            int index = exclusive[i];
            Item item = matched[index];
            if (item == null)
                continue;
            int inst = item.GetInstanceID();
            ushort sid = 0;
            bool had = ItemSync.TryGetSyncInfo(item, out SyncInfo spent) && spent != null;
            if (had)
                sid = (ushort)spent.syncId;
            try {
                // already off the slot from the lift. useitem destroys it. unloading again would drop the recycled id.
                recipe.items[index].UseItem(item);
            } catch (Exception e) {
                Plugin.log.LogWarning("craft destroy " + e.Message);
            }
            // useitem can destroy the object, so the id was captured above.
            YieldedFrom.Remove(inst);
            if (had)
                Silent.Remove(sid);
        }

        if (spawn.item != null && pb.body != null && !Carried(pb.body, spawn.item))
            pb.body.AutoPickUpItem(spawn.item);

        AcceptRewards(pb, recipe);
        var receipt = new Receipt {
            session = session,
            attempt = attempt,
            accepted = true,
            reason = CraftReason.Ok,
            tick = Net.ServerTick,
            resultId = resultId,
            spends = Describe(matched, recipe, spendIds, spendVers),
        };
        FixCraftOrder.LogResult(pb, recipe, true, CraftReason.Ok.ToString());
        receipt = Store(crafts, receipt);
        // the entity destroy does not clear the copy other machines already seated from the mouth byte.
        SendSpend(receipt);
        return receipt;
    }

    struct Held {
        public Item item;
        public InventorySlot slot;
        public Container container;
        public bool wearing;
        public Body body;
    }

    static List<Held> LiftIngredients(List<Item> matched, Recipe recipe) {
        var lifted = new List<Held>();
        for (int i = 0; i < matched.Count; i++) {
            RecipeItem ingredient = recipe.items[i];
            if (!ingredient.destroyItem || ingredient.isLiquid)
                continue;
            Item item = matched[i];
            if (item == null)
                continue;
            ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
            if (!ici.has_container)
                continue;
            var held = new Held {
                item = item,
                slot = ici.inv_slot,
                container = ici.container,
                wearing = ici.is_wearing,
                body = ici.inv_slot != null ? ici.inv_slot.body : (ici.bodynpc != null ? ici.bodynpc.body : null),
            };
            try {
                StepAside(item);
            } catch (Exception e) {
                Plugin.log.LogWarning("craft lift " + e.Message);
                continue;
            }
            lifted.Add(held);
        }
        return lifted;
    }

    static void PutBack(List<Held> lifted) {
        for (int i = 0; i < lifted.Count; i++) {
            Held held = lifted[i];
            if (held.item == null)
                continue;
            try {
                FinishAside(held.item);
                if (held.slot != null && held.body != null)
                    held.body.PickUpItem(held.item, held.slot.slot, true);
                else if (held.container != null)
                    held.container.LoadItem(held.item);
                else if (held.wearing && held.body != null)
                    held.body.WearWearable(held.item);
            } catch (Exception e) {
                Plugin.log.LogWarning("craft restore " + e.Message);
            }
        }
    }

    static bool Carried(Body body, Item item) {
        Transform t = item.transform.parent;
        while (t != null) {
            if (t == body.transform)
                return true;
            if (t.TryGetComponent(out InventorySlot slot) && slot.body == body)
                return true;
            if (t.TryGetComponent(out Limb limb) && limb.body == body)
                return true;
            t = t.parent;
        }
        return false;
    }

    static Snap TakeSnap(Item item) {
        var snap = new Snap { item = item, condition = item != null ? item.condition : 0f };
        if (item != null && item.TryGetComponent(out WaterContainerItem wci)) {
            snap.liquid = true;
            snap.ids = new List<string>();
            snap.amounts = new List<float>();
            for (int i = 0; i < wci.stack.Count; i++) {
                snap.ids.Add(wci.stack[i].liquidId);
                snap.amounts.Add(wci.stack[i].amount);
            }
        }
        return snap;
    }

    static void RestoreSnaps(List<Snap> snaps) {
        for (int i = 0; i < snaps.Count; i++) {
            Snap snap = snaps[i];
            if (snap.item == null)
                continue;
            snap.item.condition = snap.condition;
            if (!snap.liquid || !snap.item.TryGetComponent(out WaterContainerItem wci))
                continue;
            wci.stack.Clear();
            for (int j = 0; j < snap.ids.Count; j++)
                wci.stack.Add(new LiquidStack(snap.ids[j], snap.amounts[j]));
        }
    }

    static List<Spend> Describe(List<Item> matched, Recipe recipe, ushort[] spendIds, byte[] versions) {
        var spends = new List<Spend>();
        for (int i = 0; i < matched.Count; i++) {
            Item item = matched[i];
            bool gone = item == null;
            var spend = new Spend {
                syncId = spendIds[i],
                version = versions[i],
                destroyed = gone || (recipe.items[i].destroyItem && !recipe.items[i].isLiquid),
                condition = gone ? 0f : item.condition,
                liquids = ReadLiquids(item),
            };
            spends.Add(spend);
        }
        return spends;
    }

    static List<Liq> ReadLiquids(Item item) {
        if (item == null || !item.TryGetComponent(out WaterContainerItem wci) || Item_SetupItems_Listener.LiquidIdRegistry == null)
            return null;
        var list = new List<Liq>();
        for (int i = 0; i < wci.stack.Count; i++) {
            if (!Item_SetupItems_Listener.LiquidIdRegistry.TryGetValue(wci.stack[i].liquidId, out byte netId))
                continue;
            list.Add(new Liq { id = netId, ml = LiquidStackPart.Pack(wci.stack[i].amount) });
        }
        return list.Count == 0 ? null : list;
    }

    static void AcceptRewards(NetBody pb, Recipe recipe) {
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
            DebugHelp.OnNetEvent(pb.body.transform.position, "S: TryCraft id:" + recipe.index + " - " + recipe.fullName);
        ServerMain.Server_AnnounceSound(pb.pos, "combine", ServerMain.GetListOfClientIdsExceptThis(pb));
        if (pb.IsLocal && pb.body != null)
            Sound.Play("combine", pb.body.transform.position);
        if (pb.body != null && pb.body.TryGetNetBody(out NetBody known)) {
            if (!known.RecipesHasCrafterBefore.Contains(recipe.index)) {
                known.RecipesHasCrafterBefore.Add(recipe.index);
                known.body.happiness += 1f;
                known.body.skills.AddExp(2, 10f);
            }
            PlayerCheckpoint.SendCrafted(known);
        }
    }

    static Receipt Deny(uint session, uint attempt, CraftReason reason, ushort[] ids, NetBody pb, Recipe recipe) {
        var spends = new List<Spend>();
        if (ids != null) {
            for (int i = 0; i < ids.Length; i++)
                spends.Add(new Spend { syncId = ids[i] });
        }
        FixCraftOrder.LogResult(pb, recipe, false, reason.ToString());
        return new Receipt {
            session = session,
            attempt = attempt,
            accepted = false,
            reason = reason,
            tick = Net.ServerTick,
            spends = spends,
        };
    }

    static void DenyAndSend(ScavPlayer plr, NetBody pb, PlayerCrafts crafts, uint session, uint attempt, Recipe recipe, CraftReason reason, ushort[] ids) {
        Receipt receipt = Store(crafts, Deny(session, attempt, reason, ids, pb, recipe));
        SendReceipt(plr.playerId, receipt);
    }

    static PlayerCrafts CraftsFor(knetid playerId, uint session) {
        if (!ServerCrafts.TryGetValue(playerId, out PlayerCrafts crafts)) {
            crafts = new PlayerCrafts { session = session };
            ServerCrafts[playerId] = crafts;
            return crafts;
        }
        if (crafts.session != session) {
            crafts.session = session;
            crafts.receipts.Clear();
            crafts.order.Clear();
        }
        return crafts;
    }

    static Receipt Store(PlayerCrafts crafts, Receipt receipt) {
        if (crafts.receipts.ContainsKey(receipt.attempt))
            return crafts.receipts[receipt.attempt];
        crafts.receipts[receipt.attempt] = receipt;
        crafts.order.Enqueue(receipt.attempt);
        while (crafts.order.Count > Remember)
            crafts.receipts.Remove(crafts.order.Dequeue());
        return receipt;
    }

    static void SendAttempt(Attempt attempt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_PlayerCamera_TryCraft);
        writer.Put(attempt.session);
        writer.Put(attempt.id);
        writer.Put(attempt.recipe);
        writer.PutArray(attempt.ids);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        attempt.sentAt = Time.realtimeSinceStartup;
    }

    static void SendReceipt(knetid playerId, Receipt receipt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CraftReceipt);
        WriteReceipt(writer, receipt);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, playerId);
    }

    // every other machine. the host already destroyed its copy. the crafter's own receipt still carries the ui.
    static void SendSpend(Receipt receipt) {
        if (receipt == null || !receipt.accepted)
            return;
        IReadOnlyList<knetid> ids = ServerMain.AllClientIdsExceptHost;
        if (ids == null || ids.Count == 0)
            return;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CraftReceipt);
        WriteReceipt(writer, receipt);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ids);
    }

    static void WriteReceipt(NetDataWriter writer, Receipt receipt) {
        writer.Put(receipt.session);
        writer.Put(receipt.attempt);
        writer.Put(receipt.accepted ? (byte)1 : (byte)0);
        writer.Put((byte)receipt.reason);
        writer.Put(receipt.tick);
        writer.Put(receipt.resultId);
        int count = receipt.spends != null ? receipt.spends.Count : 0;
        if (count > 255)
            count = 255;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            Spend spend = receipt.spends[i];
            writer.Put(spend.syncId);
            writer.Put(spend.version);
            byte flags = 0;
            if (spend.destroyed)
                flags |= 1;
            bool liquids = spend.liquids != null && spend.liquids.Count > 0;
            if (liquids)
                flags |= 2;
            writer.Put(flags);
            writer.Put(spend.condition);
            if (!liquids)
                continue;
            int n = spend.liquids.Count;
            if (n > 255)
                n = 255;
            writer.Put((byte)n);
            for (int j = 0; j < n; j++) {
                writer.Put(spend.liquids[j].id);
                writer.Put(spend.liquids[j].ml);
            }
        }
    }

    static Receipt ReadReceipt(NetDataReader reader) {
        var receipt = new Receipt();
        receipt.session = reader.GetUInt();
        receipt.attempt = reader.GetUInt();
        receipt.accepted = reader.GetByte() != 0;
        receipt.reason = (CraftReason)reader.GetByte();
        receipt.tick = reader.GetUShort();
        receipt.resultId = reader.GetUShort();
        int count = reader.GetByte();
        receipt.spends = new List<Spend>(count);
        for (int i = 0; i < count; i++) {
            var spend = new Spend();
            spend.syncId = reader.GetUShort();
            spend.version = reader.GetByte();
            byte flags = reader.GetByte();
            spend.destroyed = (flags & 1) != 0;
            spend.condition = reader.GetFloat();
            if ((flags & 2) != 0) {
                int n = reader.GetByte();
                spend.liquids = new List<Liq>(n);
                for (int j = 0; j < n; j++)
                    spend.liquids.Add(new Liq { id = reader.GetByte(), ml = reader.GetUShort() });
            }
            receipt.spends.Add(spend);
        }
        return receipt;
    }

    static void ApplySpend(Receipt receipt) {
        if (receipt.spends == null)
            return;
        for (int i = 0; i < receipt.spends.Count; i++) {
            Spend spend = receipt.spends[i];
            var gate = new Gate {
                tick = receipt.tick,
                version = spend.version,
                destroyed = spend.destroyed,
                holdCondition = !spend.destroyed,
                condition = spend.condition,
                holdLiquid = spend.liquids != null && spend.liquids.Count > 0,
                liquids = spend.liquids,
            };
            Gates[spend.syncId] = gate;
            knetid key = spend.syncId;
            if (!ItemSync.TryGetItemSyncInfo(key, out SyncInfo si) || si == null || si.item == null)
                continue;
            Item item = si.item;
            // receipt showed up after this id was already the next item. leave that one alone.
            if (si.Version != spend.version)
                continue;
            if (spend.destroyed) {
                Consume(item);
                continue;
            }
            item.condition = spend.condition;
            ApplyLiquids(item, spend.liquids);
        }
    }

    static void ApplyLiquids(Item item, List<Liq> liquids) {
        if (liquids == null || !item.TryGetComponent(out WaterContainerItem wci))
            return;
        string[] names = Item_SetupItems_Listener.LiquidNetIdToId;
        if (names == null)
            return;
        wci.stack.Clear();
        for (int i = 0; i < liquids.Count; i++) {
            if (liquids[i].id >= names.Length)
                continue;
            string liquidId = names[liquids[i].id];
            if (string.IsNullOrEmpty(liquidId))
                continue;
            wci.stack.Add(new LiquidStack(liquidId, liquids[i].ml));
        }
    }

    static void NoteMade(Receipt receipt) {
        Recipe recipe = null;
        if (Attempts.TryGetValue(receipt.attempt, out Attempt attempt)) {
            if (attempt.recipe < Recipes.recipes.Count)
                recipe = Recipes.recipes[attempt.recipe];
        }
        if (recipe != null)
            recipe.hasMadeBefore = true;
        Body body = Util.GetLocalBody();
        if (body != null)
            Sound.Play("combine", body.transform.position);
    }

    static void RefreshIfOpen() {
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null && cam.craftingPanel != null && cam.craftingPanel.activeSelf)
            cam.RefreshRecipeList();
    }

    // item.start, after newgo. the craft refresh already ran, before this object had a sync id.
    // a world item starting does nothing here. only a carried item that matching can actually use.
    internal static void NoteCarriedSync(Item item) {
        if (item == null || !Net.IsRunning)
            return;
        PlayerCamera cam = PlayerCamera.main;
        if (cam == null || cam.craftingPanel == null || !cam.craftingPanel.activeSelf)
            return;
        Body body = Util.GetLocalBody();
        if (body == null || !Carried(body, item))
            return;
        if (!ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return;
        cam.RefreshRecipeList();
    }

    static bool LiquidsLookStale(Gate gate) {
        if (gate.liquids == null)
            return false;
        for (int i = 0; i < SeenLiquidId.Count; i++) {
            ushort allowed = 0;
            for (int j = 0; j < gate.liquids.Count; j++) {
                if (gate.liquids[j].id == SeenLiquidId[i])
                    allowed = gate.liquids[j].ml;
            }
            if (SeenLiquidMl[i] > allowed)
                return true;
        }
        return false;
    }

    static bool LiquidsCaughtUp(Gate gate) {
        if (gate.liquids == null)
            return true;
        for (int i = 0; i < gate.liquids.Count; i++) {
            bool found = false;
            for (int j = 0; j < SeenLiquidId.Count; j++) {
                if (SeenLiquidId[j] != gate.liquids[i].id)
                    continue;
                found = true;
                if (SeenLiquidMl[j] > gate.liquids[i].ml)
                    return false;
            }
            if (!found && gate.liquids[i].ml > 0)
                return false;
        }
        return true;
    }

    // live entity on this id, but not the generation we spent
    static bool OtherGen(ushort id, Gate gate) {
        knetid key = id;
        if (!ItemSync.TryGetItemSyncInfo(key, out SyncInfo si) || si == null)
            return false;
        return si.Version != gate.version;
    }

    static void Relax(ushort id, Gate gate) {
        if (gate.destroyed)
            return;
        if ((!gate.holdCondition || gate.conditionOpen) && (!gate.holdLiquid || gate.liquidOpen))
            Gates.Remove(id);
    }

    static void ExpireGates() {
        if (Gates.Count == 0)
            return;
        _drop.Clear();
        foreach (var pair in Gates) {
            if (LiteEntitySystem.Utils.SequenceDiff(Net.ServerTick, pair.Value.tick) > GateTicks)
                _drop.Add(pair.Key);
        }
        for (int i = 0; i < _drop.Count; i++)
            Gates.Remove(_drop[i]);
    }

    static void ClearAll() {
        _session = 0;
        _next = 1;
        _stepOpen = false;
        _stepDone = false;
        _stepAttempt = 0;
        _pool = null;
        _poolFrame = -1;
        Attempts.Clear();
        Exclusive.Clear();
        ConditionHold.Clear();
        LiquidHold.Clear();
        Tombstones.Clear();
        SavedItems.Clear();
        ServerCrafts.Clear();
        Gates.Clear();
        Silent.Clear();
        YieldedFrom.Clear();
        Seen.Clear();
        SeenOrder.Clear();
    }

    static string ReasonText(CraftReason reason) {
        switch (reason) {
            case CraftReason.Missing: return "Can't craft: missing ingredients";
            case CraftReason.Unreachable: return "Can't craft: out of reach";
            case CraftReason.Taken: return "Can't craft: that item was just used";
            case CraftReason.Unconscious: return "Can't craft: unconscious";
            case CraftReason.BadRecipe: return "Can't craft: unknown recipe";
            case CraftReason.SpawnFailed: return "Can't craft: the result could not be made";
            case CraftReason.StillSyncing: return "Can't craft: ingredient still syncing";
            default: return "Can't craft";
        }
    }
}

struct CraftSpawn {
    public bool failed;
    public bool noItem;
    public Item item;
    public List<GameObject> made;

    public static CraftSpawn Made(Item item) {
        return new CraftSpawn { item = item };
    }

    public static CraftSpawn None() {
        return new CraftSpawn { noItem = true };
    }

    public static CraftSpawn Fail(List<GameObject> made) {
        var spawn = new CraftSpawn { failed = true, made = made };
        spawn.Discard();
        return spawn;
    }

    public void Discard() {
        if (made == null)
            return;
        for (int i = 0; i < made.Count; i++) {
            if (made[i] != null)
                UnityEngine.Object.Destroy(made[i]);
        }
        made = null;
        item = null;
    }
}

[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetItemsForRecipe))]
[HarmonyPriority(Priority.First)]
static class CraftCommit_GetItems {
    static bool Prefix(Recipe __instance, ref List<Item> __result) {
        if (!Net.IsRunning)
            return true;
        __result = CraftCommit.MatchList(__instance, thorough: false);
        return false;
    }
}

[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetItemsForRecipeThorough))]
[HarmonyPriority(Priority.First)]
static class CraftCommit_GetItemsThorough {
    static bool Prefix(Recipe __instance, ref List<Item> __result) {
        if (!Net.IsRunning)
            return true;
        __result = CraftCommit.MatchList(__instance, thorough: true);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.RefreshRecipeList))]
static class CraftCommit_Refresh {
    static void Prefix() {
        if (Net.IsRunning)
            CraftCommit.OpenPool();
    }

    static void Postfix() {
        CraftCommit.ClosePool();
    }
}
