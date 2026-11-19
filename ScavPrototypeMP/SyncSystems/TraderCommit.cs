using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using static TraderScript;

namespace Together;

// one host record fills a trader. clients send an op and wait. one attempt per trader at a time.
internal static class TraderCommit {
    internal enum Op : byte {
        Buy = 1,
        Sell = 2,
        Haggle = 3,
        Threaten = 4,
        Hug = 5,
        Move = 6,
        Rest = 7,
        Recruit = 8,
    }

    internal static bool Running { get; private set; }
    internal static Body Actor { get; private set; }

    private sealed class Job {
        public Op op;
        public Body actor;
        public SyncInfo trader;
        public byte index;
        public string itemId;
        public ushort value;
        public knetid sold;
    }

    private static readonly Dictionary<knetid, Queue<Job>> Queues = new();
    private static readonly HashSet<knetid> Pumping = new();
    private static readonly Dictionary<knetid, HashSet<byte>> ReservedOffer = new();
    private static readonly HashSet<knetid> ReservedSell = new();
    private static int sellFrame = -1;

    internal static bool LetStock(TraderScript trader, Op op) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Running) {
            return true;
        }
        PlayerCamera cam = PlayerCamera.main;
        if ((cam == null) || (cam.currentTrader != trader)) {
            return true;
        }
        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        if ((tt.si == null) || (tt.si.syncId == 0)) {
            return false;
        }
        // one hug for the whole trader. the snapshot is what tells the other side it is spent.
        if ((op == Op.Hug) && trader.didHug) {
            cam.PlayUISound(PlayerCamera.UISoundType.Deny);
            return false;
        }
        if (Net.IsServer) {
            Submit(op, cam.body, tt.si);
            return false;
        }
        Multiplayer.Client_SendSimpleMessageToServer(OpMessage(op), tt.si.syncId, true);
        if (op == Op.Rest) {
            cam.ToggleTradeMenu();
        } else if ((op == Op.Hug) || (op == Op.Haggle)) {
            cam.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        return false;
    }

    internal static void Submit(Op op, Body actor, SyncInfo trader) {
        if ((actor == null) || (trader == null) || (trader.trader == null) || trader.IsDestroyed) {
            return;
        }
        Enqueue(new Job {
            op = op,
            actor = actor,
            trader = trader,
        });
    }

    internal static void SubmitBuy(Body actor, SyncInfo trader, byte index, string itemId, ushort value) {
        if ((actor == null) || (trader == null) || (trader.trader == null)) {
            return;
        }
        TraderScript live = trader.trader;
        if ((live.items == null) || (index >= live.items.Count)) {
            FindTheBugInator.Trader("buy-deny", $"sync={trader.syncId} index={index} id={itemId}");
            Publish(live, actor);
            return;
        }
        TraderItem offer = live.items[index];
        if ((offer is null) || (offer.id != itemId) || ((ushort)offer.value != value) || Reserved(trader.syncId, index)) {
            FindTheBugInator.Trader("buy-deny", $"sync={trader.syncId} index={index} id={itemId}");
            Publish(live, actor);
            return;
        }
        Reserve(trader.syncId, index);
        Enqueue(new Job {
            op = Op.Buy,
            actor = actor,
            trader = trader,
            index = index,
            itemId = itemId,
            value = value,
        });
    }

    internal static void SubmitSell(Body actor, SyncInfo trader, knetid sold) {
        if ((actor == null) || (trader is null) || (trader.trader == null) || (sold == 0)) {
            return;
        }
        // a second packet in the same frame used to pay again. destroy has not run yet, so the item still looks real.
        if (Time.frameCount != sellFrame) {
            ReservedSell.Clear();
            sellFrame = Time.frameCount;
        }
        if (ReservedSell.Contains(sold)) {
            FindTheBugInator.Trader("sell-deny", $"sync={trader.syncId} item={sold}");
            Publish(trader.trader, actor);
            return;
        }
        ReservedSell.Add(sold);
        Enqueue(new() {
            op = Op.Sell,
            actor = actor,
            trader = trader,
            sold = sold,
        });
    }

    internal static void Ask(TrackerTrader tt) {
        if (!Net.IsClient || (tt == null)) {
            return;
        }
        SyncInfo si = tt.si;
        if ((si == null) || (si.syncId == 0)) {
            FindTheBugInator.TraderAsk(tt);
            return;
        }
        FindTheBugInator.TraderAsk(tt);
        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestTraderSnapshot, si.syncId, true);
    }

    internal static void Publish(TraderScript trader) {
        Publish(trader, null);
    }

    internal static void Publish(TraderScript trader, Body actor) {
        if ((trader == null) || !Net.IsServer) {
            return;
        }
        RefreshOpen(trader);
        List<knetid> ids = ClientsInRange(trader);
        if ((actor != null) && actor.TryGetNetPlayer(out ScavPlayer acting) && !acting.IsLocal && !ids.Contains(acting.playerId)) {
            ids.Add(acting.playerId);
        }
        if (ids.Count == 0) {
            if (Multiplayer.verbose) {
                FindTheBugInator.Trader("publish-none", FindTheBugInator.TraderState(trader));
            }
            return;
        }
        Send(trader, ids);
    }

    internal static void PublishTo(TraderScript trader, knetid clientId) {
        if ((trader == null) || !Net.IsServer || (clientId == 0)) {
            return;
        }
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) && plr.IsLocal) {
            RefreshOpen(trader);
            return;
        }
        Send(trader, new() { clientId });
    }

    internal static bool InRange(Vector2 traderPos, Vector2 bodyPos) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null) {
            return false;
        }
        Vector2Int traderChunk = world.BlockToChunkPos(world.WorldToBlockPos(traderPos));
        Vector2Int bodyChunk = world.BlockToChunkPos(world.WorldToBlockPos(bodyPos));
        int dx = traderChunk.x - bodyChunk.x;
        int dy = traderChunk.y - bodyChunk.y;
        if (dx < 0) {
            dx = -dx;
        }
        if (dy < 0) {
            dy = -dy;
        }
        return (dx <= 1) && (dy <= 1);
    }

    internal static void Apply(TraderScript trader, NetDataReader reader) {
        if (trader == null) {
            return;
        }
        int character = reader.GetInt();
        float desiredX = reader.GetFloat();
        float desiredY = reader.GetFloat();
        float moveMin = reader.GetFloat();
        float moveMax = reader.GetFloat();
        bool didMove = reader.GetBool();
        float reputation = reader.GetFloat();
        float hostility = reader.GetFloat();
        float haggleAmount = reader.GetFloat();
        bool freeDressing = reader.GetBool();
        bool didHug = reader.GetBool();
        byte freeAmount = reader.GetByte();
        ushort valueGiven = reader.GetUShort();
        ushort totalValueGiven = reader.GetUShort();
        byte count = reader.GetByte();
        List<TraderItem> items = new(count);
        for (int i = 0; i < count; i++) {
            reader.Get(out string id, true);
            reader.Get(out bool bought);
            reader.Get(out ushort value);
            reader.Get(out byte preference);
            items.Add(new TraderItem {
                id = id,
                bought = bought,
                value = value,
                preference = (TraderItemPreference)preference,
            });
        }
        trader.character = character;
        trader.desiredPos = new Vector2(desiredX, desiredY);
        trader.MoveRange.min = moveMin;
        trader.MoveRange.max = moveMax;
        trader.didMove = didMove;
        trader.reputation = reputation;
        trader.hostility = hostility;
        trader.haggleAmount = haggleAmount;
        trader.freeDressing = freeDressing;
        trader.didHug = didHug;
        trader.freeAmount = freeAmount;
        trader.valueGiven = valueGiven;
        trader.totalValueGiven = totalValueGiven;
        if (trader.items == null) {
            trader.items = new List<TraderItem>();
        } else {
            trader.items.Clear();
        }
        trader.items.AddRange(items);
        RefreshOpen(trader);
    }

    private static void RefreshOpen(TraderScript trader) {
        PlayerCamera cam = PlayerCamera.main;
        if ((cam == null) || (cam.currentTrader != trader) || !Util.IsInTradingMenu()) {
            return;
        }
        cam.RefreshTraderInventories();
        cam.UpdateTradeTexts();
    }

    internal static void RelaySound(TraderScript trader, Body actor, string name, Vector2 pos) {
        if (!Net.IsServer || string.IsNullOrEmpty(name) || (trader == null)) {
            return;
        }
        List<knetid> ids = ClientsInRange(trader);
        if ((actor != null) && actor.TryGetNetPlayer(out ScavPlayer acting) && !acting.IsLocal && !ids.Contains(acting.playerId)) {
            ids.Add(acting.playerId);
        }
        if (ids.Count == 0) {
            return;
        }
        ServerMain.Server_AnnounceSound(pos, name, ids);
    }

    // all in-range clients get the slash. victims skip stock Swing; host owns the damage.
    internal static void RelaySwing(TraderScript trader, Vector2 aim, knetid exceptClient = default) {
        if (!Net.IsServer || (trader == null)) {
            return;
        }
        knetid id = IdOf(trader);
        if (id == 0) {
            return;
        }
        List<knetid> ids = ClientsInRange(trader);
        if (exceptClient != 0) {
            ids.Remove(exceptClient);
        }
        if (ids.Count == 0) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_TraderSwing);
        writer.Put(id);
        writer.Put(aim.x);
        writer.Put(aim.y);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ids);
    }

    internal static void PlaySwingFx(TraderScript trader, Vector2 aim) {
        if ((trader == null) || (trader.torso == null) || (trader.attackAnimation == null)) {
            return;
        }
        Vector2 to = (aim - (Vector2)trader.torso.position).normalized;
        if (to.sqrMagnitude < 0.0001f) {
            to = Vector2.right;
        }
        GameObject slash = UnityEngine.Object.Instantiate(trader.attackAnimation);
        slash.transform.eulerAngles = new Vector3(0f, 0f, Vector2.SignedAngle(Vector3.right, to));
        slash.transform.localScale = new Vector3(1f, (to.x > 0f) ? 1f : (-1f), 1f);
        slash.transform.position = trader.torso.position + trader.torso.up * 0.8f;
        UnityEngine.Object.Destroy(slash, 2f);
        Sound.Play("BSSwing1", trader.transform.position);
        Traverse arms = Traverse.Create(trader).Field("bonusArmRot");
        float[] bonusArmRot = arms.GetValue<float[]>();
        if ((bonusArmRot != null) && (bonusArmRot.Length > 1)) {
            bonusArmRot[(to.x > 0f) ? 1 : 0] = (to.x > 0f) ? 90f : (-90f);
            arms.SetValue(bonusArmRot);
        }
        trader.bonusRot += (to.x > 0f) ? (-10f) : 10f;
        Traverse.Create(trader).Field("lastAttackTime").SetValue(Time.time + UnityEngine.Random.Range(-0.5f, 0.5f));
        if ((PlayerCamera.main != null) && (Vector2.Distance(trader.transform.position, PlayerCamera.main.transform.position) < 24f)) {
            PlayerCamera.main.threatMusicTime = 10f;
            PlayerCamera.main.currentThreatTheme = 12;
        }
    }

    // stock Swing steps desiredPos toward the victim. host cosmetic path needs the same nudge.
    internal static void ApplySwingLunge(TraderScript trader, Vector2 aim) {
        if (trader == null) {
            return;
        }
        Vector2 to = (aim - (Vector2)trader.transform.position).normalized;
        if (to.sqrMagnitude < 0.0001f) {
            to = Vector2.right;
        }
        Vector3 position = trader.transform.position;
        position.x += to.x * UnityEngine.Random.Range(0.125f, 0.5f)
            * ((UnityEngine.Random.value < 0.25f) ? (-1.6f) : 1f)
            * ((UnityEngine.Random.value < 0.2f) ? 5f : 1f);
        position.x = Mathf.Clamp(position.x, trader.MoveRange.min, trader.MoveRange.max);
        trader.desiredPos = position;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TraderSwing, false)]
    private static void Client_Swing(knetid _, NetDataReader reader) {
        reader.Get(out knetid traderId);
        float x = reader.GetFloat();
        float y = reader.GetFloat();
        if (!NetObjectRegistry.TryGetSyncInfoLive(traderId, out SyncInfo si) || (si is null) || (si.trader == null)) {
            return;
        }
        // clients skip stock Swing. host damage lands via ForceHealth. this is slash/sound for everyone.
        PlaySwingFx(si.trader, new Vector2(x, y));
    }

    private static List<knetid> ClientsInRange(TraderScript trader) {
        List<knetid> ids = new();
        Vector2 pos = trader.transform.position;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if ((plr == null) || plr.IsLocal || (plr.body == null)) {
                continue;
            }
            if (InRange(pos, plr.body.transform.position)) {
                ids.Add(plr.playerId);
            }
        }
        return ids;
    }

    private static void Send(TraderScript trader, List<knetid> ids) {
        knetid id = IdOf(trader);
        if (id == 0) {
            NetObjectRegistry.NewGO(trader.gameObject);
            id = IdOf(trader);
            if (id == 0) {
                FindTheBugInator.Trader("send-skip", "id=0 " + FindTheBugInator.TraderState(trader));
                return;
            }
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_TraderSnapshot);
        writer.Put(id);
        writer.Put((int)trader.character);
        writer.Put(trader.desiredPos.x);
        writer.Put(trader.desiredPos.y);
        writer.Put((float)trader.MoveRange.min);
        writer.Put((float)trader.MoveRange.max);
        writer.Put(trader.didMove);
        writer.Put(trader.reputation);
        writer.Put(trader.hostility);
        writer.Put(trader.haggleAmount);
        writer.Put(trader.freeDressing);
        writer.Put(trader.didHug);
        writer.Put((byte)trader.freeAmount);
        writer.Put((ushort)trader.valueGiven);
        writer.Put((ushort)trader.totalValueGiven);
        int count = (trader.items == null) ? 0 : trader.items.Count;
        if (count > 255) {
            count = 255;
        }
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            TraderItem item = trader.items[i];
            string itemId = (item is null) ? "" : item.id;
            writer.Put(itemId, true);
            writer.Put((item is not null) && item.bought);
            writer.Put((ushort)(item is null ? 0 : item.value));
            writer.Put((byte)(item is null ? 0 : (byte)item.preference));
        }
        if (Multiplayer.verbose) {
            FindTheBugInator.Trader("send", FindTheBugInator.TraderState(trader) + " " + FindTheBugInator.IdList(ids));
        }
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ids);
    }

    private static knetid IdOf(TraderScript trader) {
        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        if ((tt.si != null) && (tt.si.syncId != 0)) {
            return tt.si.syncId;
        }
        if (NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out SyncInfo si)) {
            return si.syncId;
        }
        return 0;
    }

    private static byte OpMessage(Op op) {
        return op switch {
            Op.Haggle => (byte)NetmsgId.SERVER_TraderTryHaggle,
            Op.Threaten => (byte)NetmsgId.SERVER_TraderThreaten,
            Op.Hug => (byte)NetmsgId.SERVER_TraderTryHug,
            Op.Move => (byte)NetmsgId.SERVER_TraderAskToMove,
            Op.Rest => (byte)NetmsgId.SERVER_TraderDoRest,
            _ => 0,
        };
    }

    private static void Enqueue(Job job) {
        knetid id = job.trader.syncId;
        if (!Queues.TryGetValue(id, out Queue<Job> queue)) {
            queue = new();
            Queues[id] = queue;
        }
        queue.Enqueue(job);
        Pump(id);
    }

    private static void Pump(knetid id) {
        if (!Pumping.Add(id)) {
            return;
        }
        try {
            Queue<Job> queue = Queues[id];
            while (queue.Count > 0) {
                Job job = queue.Dequeue();
                try {
                    Run(job);
                } finally {
                    Release(job);
                }
                if ((job.trader is not null) && !job.trader.IsDestroyed && (job.trader.trader != null)) {
                    Publish(job.trader.trader, job.actor);
                }
            }
        } finally {
            Pumping.Remove(id);
        }
    }

    private static void Run(Job job) {
        TraderScript trader = job.trader.trader;
        Body actor = job.actor;
        if ((trader == null) || (actor == null)) {
            return;
        }
        // first trade must lock first-impression. otherwise the host walking up later MeetPlayer-wipes live rep.
        TraderScript_MeetPlayer_MultiplayerPatch.EnsureFirstMeet(trader, actor);
        trader.startedConvo = true;
        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        Body previous = tt.focused_body;
        tt.focused_body = actor;
        Running = true;
        Actor = actor;
        try {
            switch (job.op) {
                case Op.Buy:
                RunBuy(trader, actor, job);
                break;
                case Op.Sell:
                RunSell(trader, actor, job);
                break;
				case Op.Haggle:
				bool bite = (trader.character == 2) && (trader.totalValueGiven < MAX_VALUE_GIVEN);
				Vector2 biteAt = (actor != null) ? (Vector2)actor.transform.position : (Vector2)trader.transform.position;
				trader.TryHaggle();
				if (bite) {
					RelaySound(trader, actor, "eatFlesh", biteAt);
					// stock wrote the limbs on the host copy. living players upload their own
					// health, so without a confirm the owner never keeps the bite.
					if (actor != null && actor.TryGetNetBody(out NetBody bitten)) {
						bitten.OnDamageEvent(new BodyDamageEvent(trader.build, bitten, 30));
						MedicalSync.ForceHealth(bitten);
					}
				}
				break;
                case Op.Threaten:
                if (trader.reputation >= 30f) {
                    trader.Threaten();
                }
                break;
                case Op.Hug:
                if (!trader.didHug) {
                    float rep = trader.reputation;
                    trader.TryHug();
                    // stock leaves this false on a dune success. the hug is still spent for everyone.
                    trader.didHug = true;
                    string hugSound = (trader.reputation < rep) ? "BSSwing1" : "combine";
                    RelaySound(trader, actor, hugSound, trader.transform.position);
                }
                break;
                case Op.Move:
                trader.AskToMove();
                trader.didMove = true;
                break;
                case Op.Rest:
                RunRest(trader, actor);
                break;
                case Op.Recruit:
                PlayerCamera_ToggleTradeMenu_MultiplayerPatch.PerformRecruit(job.trader);
                break;
            }
        } finally {
            Actor = null;
            Running = false;
            tt.focused_body = previous;
        }
    }

    private static void RunBuy(TraderScript trader, Body actor, Job job) {
        if ((trader.items == null) || (job.index >= trader.items.Count)) {
            FindTheBugInator.Trader("buy-deny", $"sync={job.trader.syncId} index={job.index} id={job.itemId}");
            return;
        }
        TraderItem offer = trader.items[job.index];
        if ((offer is null) || (offer.id != job.itemId) || ((ushort)offer.value != job.value)) {
            FindTheBugInator.Trader("buy-deny", $"sync={job.trader.syncId} index={job.index} id={job.itemId}");
            return;
        }
        Item bought = TraderScript_TryPurchase_MultiplayerPatch.TryPurchase_rewritten(trader, offer, actor);
        if (bought != null) {
            NetObjectRegistry.TryGetSyncInfoOrRegister(bought, out _);
        }
    }

    private static void RunSell(TraderScript trader, Body actor, Job job) {
        if (!ItemSync.TryGetItemSyncInfo(job.sold, out SyncInfo itemsi) || (itemsi.item == null)) {
            FindTheBugInator.Trader("sell-deny", $"sync={job.trader.syncId} item={job.sold}");
            return;
        }
        if (!ItemSync.CheckIfBodyReachThisItem(itemsi, actor) || (trader.reputation < 30f)) {
            FindTheBugInator.Trader("sell-deny", $"sync={job.trader.syncId} item={job.sold}");
            return;
        }
        trader.GiveItem(itemsi.item);
    }

    private static void RunRest(TraderScript trader, Body actor) {
        if (Util.IsBodyLocal(actor)) {
            trader.DoRest();
            return;
        }
        if (!actor.canTakeNap) {
            return;
        }
        actor.sleeping = true;
        actor.consciousness = 30f;
        actor.forcedSleepQuality = Body.SleepQuality.Good;
    }

    private static void Release(Job job) {
        if (job.op == Op.Buy) {
            Unreserve(job.trader.syncId, job.index);
        }
    }

    private static bool Reserved(knetid trader, byte index) {
        return ReservedOffer.TryGetValue(trader, out HashSet<byte> held) && held.Contains(index);
    }

    private static void Reserve(knetid trader, byte index) {
        if (!ReservedOffer.TryGetValue(trader, out HashSet<byte> held)) {
            held = new();
            ReservedOffer[trader] = held;
        }
        held.Add(index);
    }

    private static void Unreserve(knetid trader, byte index) {
        if (!ReservedOffer.TryGetValue(trader, out HashSet<byte> held)) {
            return;
        }
        held.Remove(index);
        if (held.Count == 0) {
            ReservedOffer.Remove(trader);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TraderSnapshot)]
    private static void Client_Snapshot(knetid _, NetDataReader reader) {
        reader.Get(out knetid traderId);
        int left = 0;
        try { left = reader.AvailableBytes; } catch (Exception) { }
        if (!NetObjectRegistry.TryGetSyncInfoLive(traderId, out SyncInfo si) || (si is null)) {
            FindTheBugInator.Trader("drop", "no-sync id=" + traderId.id + " bytes=" + left + " side=" + FindTheBugInator.Side());
            return;
        }
        TraderScript trader;
        try {
            trader = si.trader;
        } catch (Exception ex) {
            FindTheBugInator.Trader("drop", "trader-throw id=" + traderId.id + " bytes=" + left + " " + ex.GetType().Name);
            return;
        }
        if (trader == null) {
            string go = "go=null";
            try {
                if (si.go != null) {
                    go = "go=" + si.go.name;
                }
            } catch (Exception) { }
            FindTheBugInator.Trader("drop", "no-trader id=" + traderId.id + " bytes=" + left + " " + go);
            return;
        }
        try {
            Apply(trader, reader);
        } catch (Exception ex) {
            FindTheBugInator.Trader("apply-fail", "id=" + traderId.id + " bytes=" + left + " " + ex.GetType().Name + " " + ex.Message);
            throw;
        }
        FindTheBugInator.Trader("apply", "bytes=" + left + " " + FindTheBugInator.RefreshText(trader) + " " + FindTheBugInator.TraderState(trader));
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestTraderSnapshot)]
    private static void Server_Request(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid traderId);
        TraderScript live = null;
        string fail = "ok";
        if (!NetObjectRegistry.TryGetSyncInfoLive(traderId, out SyncInfo si) || (si is null)) {
            fail = "no-sync";
        } else {
            try {
                live = si.trader;
            } catch (Exception ex) {
                fail = "throw-" + ex.GetType().Name;
            }
            if ((live == null) && (fail == "ok")) {
                fail = "no-trader";
            }
        }
        string state = (live == null) ? "" : (" " + FindTheBugInator.TraderState(live));
        FindTheBugInator.Trader("request", "from=" + clientId.id + " id=" + traderId.id + " " + fail + state);
        if (live != null) {
            Body body = null;
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out body);
            // client walked up first. roll first impression on the host copy before they paint the ui
            if (TraderScript_MeetPlayer_MultiplayerPatch.EnsureFirstMeet(live, body))
                FindTheBugInator.Trader("request-first-meet", FindTheBugInator.TraderState(live));
            PublishTo(live, clientId);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderDoRest)]
    private static void Server_Rest(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid traderId);
        if (TraderSync.Server_TraderInteractionCheck(clientId, traderId, "Rest", out ScavPlayer plr, out SyncInfo si, out _)) {
            Submit(Op.Rest, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(traderId, out si) && (si.trader != null)) {
            PublishTo(si.trader, clientId);
        }
    }
}