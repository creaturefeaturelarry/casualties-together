using System.Collections.Generic;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

public enum MoveKind : byte {
    World = 0,
    Bag = 1,
    Slot = 2,
    Worn = 3,
}

public enum MoveReason : byte {
    Ok = 0,
    Missing = 1,
    Unreachable = 2,
    Taken = 3,
    Unconscious = 4,
    BadSlot = 5,
    BrokenBag = 6,
    StillSyncing = 7,
}

// one gesture, one try. host keeps the parent it has, or seats the batch and writes once.
// a no still says where it is, so we move that same object and dont make another.
public static class MoveCommit {
    public struct Move {
        public ushort item;
        public MoveKind kind;
        public ushort destNet;
        public byte destSlot;
    }

    struct Row {
        public ushort item;
        public byte data;
        public ushort net;
    }

    sealed class Attempt {
        public uint session;
        public uint id;
        public List<Move> moves;
        public float sentAt;
    }

    sealed class Receipt {
        public uint session;
        public uint attempt;
        public bool accepted;
        public MoveReason reason;
        public List<Row> rows;
    }

    sealed class Book {
        public uint session;
        public readonly Dictionary<uint, Receipt> receipts = new Dictionary<uint, Receipt>();
        public readonly Queue<uint> order = new Queue<uint>();
    }

    const float ResendSeconds = 2f;
    const int Remember = 64;

    static uint _session;
    static uint _next = 1;
    static bool _running;
    static int _depth;
    static int _throwing;
    static int _commit;
    static bool _applying;
    static int _fromNet;
    static int _guardFrame = -1;

    static readonly Dictionary<uint, Attempt> Attempts = new Dictionary<uint, Attempt>();
    static readonly HashSet<ushort> Exclusive = new HashSet<ushort>();
    static readonly HashSet<ushort> Guard = new HashSet<ushort>();
    static readonly Dictionary<ushort, uint> Newest = new Dictionary<ushort, uint>();
    static readonly Dictionary<ushort, Latch> Latches = new Dictionary<ushort, Latch>();
    static readonly List<Item> Gesture = new List<Item>();
    static readonly Dictionary<knetid, Book> Books = new Dictionary<knetid, Book>();
    static readonly List<uint> DropIds = new List<uint>();

    // the reply named this parent. ignore container bytes until they say the same thing, or this runs out.
    struct Latch {
        public byte data;
        public ushort net;
        public float until;
    }

    internal static bool Held(ushort id) {
        return Exclusive.Contains(id);
    }

	internal static bool Held(Item item) {
        if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return false;
        ushort id = si.syncId;
        return id != 0 && Exclusive.Contains(id);
    }

    // true until the host byte has caught the parent. bag sleep leaves these on.
    internal static bool Busy(Item item) {
        if (Held(item))
            return true;
        if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return false;
        ushort id = si.syncId;
        return id != 0 && Latches.ContainsKey(id);
    }

    internal static void Tick() {
        if (!Net.IsRunning) {
            if (_running)
                Clear();
            _running = false;
            return;
        }
        _running = true;
        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        if (Net.IsServer)
            return;
        float now = Time.realtimeSinceStartup;
        foreach (var pair in Attempts) {
            Attempt attempt = pair.Value;
            if (now - attempt.sentAt >= ResendSeconds)
                Send(attempt);
        }
    }

    internal static void Push() {
        _depth++;
    }

    internal static void Pop() {
        if (_depth > 0)
            _depth--;
    }

    internal static void BeginThrow() {
        _throwing++;
    }

    internal static void EndThrow() {
        if (_throwing > 0)
            _throwing--;
    }

    // q. both hands are one attempt, read after the local exchange.
    internal static void EndHands(Body body) {
        Pop();
        if (body != null) {
            Keep(body.GetItem(0));
            Keep(body.GetItem(1));
        }
        FinishGesture();
    }

    // hotbar swap of two slots. same batch, those two results only.
    internal static void EndSwap(Body body, int slot1, int slot2) {
        Pop();
        if (body != null && body.slots != null) {
            if (slot1 >= 0 && slot1 < body.slots.Length)
                Keep(body.GetItem(slot1));
            if (slot2 >= 0 && slot2 < body.slots.Length)
                Keep(body.GetItem(slot2));
        }
        FinishGesture();
    }

    internal static void EndDrag(Item item) {
        Pop();
        Keep(item);
        FinishGesture();
    }

    // container apply is not the player letting go. a drop from that used to tell the host to drop it too.
	// true while a packet or a receipt is seating. a hand drag is not this, and that drag already updates its own button.
    internal static bool Applying => _applying || _fromNet > 0;

    internal static void PushApply() {
        _fromNet++;
    }

    internal static void PopApply() {
        if (_fromNet > 0)
            _fromNet--;
    }

    internal static void SubmitLive(Item item) {
        if (item == null || Busy() || _fromNet > 0)
            return;
        // still inside the drag or the swap. describe it once, at the end, wherever it landed.
        if (_depth > 0) {
            Keep(item);
            return;
        }
        if (!TryDescribe(item, out Move move))
            return;
        // same frame as the gesture that just sent. a later frame replaces that send.
        if (Guarded(move.item))
            return;
        // already where the last reply put it. the slot scan must not send that again.
        if (Latched(move))
            return;
        var moves = new List<Move>(1);
        moves.Add(move);
        Submit(moves);
    }

    static void Keep(Item item) {
        if (item == null)
            return;
        for (int i = 0; i < Gesture.Count; i++) {
            if (Gesture[i] == item)
                return;
        }
        Gesture.Add(item);
    }

    // depth back to 0. one packet for every item the gesture touched, at the parent it has now.
    static void FinishGesture() {
        if (_depth > 0)
            return;
        if (Busy()) {
            Gesture.Clear();
            return;
        }
        var moves = new List<Move>(Gesture.Count);
        for (int i = 0; i < Gesture.Count; i++)
            TryAdd(Gesture[i], moves);
        Gesture.Clear();
        Submit(moves);
    }

    static bool Busy() {
        return _throwing > 0 || _applying || _commit > 0;
    }

    // a later swap still counts. the pickup reply is old by then.
    static void TryAdd(Item item, List<Move> moves) {
        if (!TryDescribe(item, out Move move))
            return;
        for (int i = 0; i < moves.Count; i++) {
            if (moves[i].item == move.item)
                return;
        }
        moves.Add(move);
    }

    static bool TryDescribe(Item item, out Move move) {
        move = default;
        if (item == null || !Net.IsRunning)
            return false;
        SyncInfo si;
        bool found = Net.IsServer
            ? NetObjectRegistry.TryGetSyncInfoOrRegister(item.gameObject, out si)
            : ItemSync.TryGetSyncInfo(item, out si);
        if (!found || si == null || si.syncId.id == 0)
            return false;
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
        move.item = si.syncId;
        if (ici.is_wearing) {
            move.kind = MoveKind.Worn;
            move.destNet = ici.body_netid;
        } else if (ici.is_in_surface_inv) {
            move.kind = MoveKind.Slot;
            move.destNet = ici.body_netid;
            move.destSlot = ici.inv_slot_number;
        } else if (ici.has_container && ici.container_si != null) {
            move.kind = MoveKind.Bag;
            move.destNet = ici.container_si.syncId;
        } else {
            move.kind = MoveKind.World;
        }
        return true;
    }

    static void Submit(List<Move> moves) {
        if (moves == null || moves.Count == 0 || !Net.IsRunning)
            return;
        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint id = _next++;
        if (_next == 0)
            _next = 1;
        var attempt = new Attempt {
            session = _session,
            id = id,
            moves = moves,
            sentAt = Time.realtimeSinceStartup,
        };
        Hold(attempt);
        Attempts[attempt.id] = attempt;
        DropOlder(attempt);
        if (Net.IsServer)
            FinishLocal(attempt);
        else
            Send(attempt);
    }

    static void FinishLocal(Attempt attempt) {
        Body body = Util.GetLocalBody();
        if (body == null || !body.TryGetNetBody(out NetBody nb) || nb.plr == null) {
            Release(attempt);
            return;
        }
        Receipt receipt = Commit(body, attempt.session, attempt.id, attempt.moves, out bool terminal);
        if (terminal)
            Release(attempt);
        else
            attempt.sentAt = Time.realtimeSinceStartup;
        if (receipt != null && terminal)
            Apply(receipt);
    }

    static void Hold(Attempt attempt) {
        int frame = Time.frameCount;
        if (frame != _guardFrame) {
            _guardFrame = frame;
            Guard.Clear();
        }
        for (int i = 0; i < attempt.moves.Count; i++) {
            ushort id = attempt.moves[i].item;
            Exclusive.Add(id);
            Guard.Add(id);
            Newest[id] = attempt.id;
            Latches.Remove(id);
        }
    }

    // the last ask is the one that still matters. earlier sends for those items are not resent.
    static void DropOlder(Attempt newer) {
        DropIds.Clear();
        foreach (var pair in Attempts) {
            if (pair.Key == newer.id)
                continue;
            Attempt old = pair.Value;
            if (old.moves == null)
                continue;
            for (int i = old.moves.Count - 1; i >= 0; i--) {
                ushort id = old.moves[i].item;
                if (Newest.TryGetValue(id, out uint cur) && cur != old.id)
                    old.moves.RemoveAt(i);
            }
            if (old.moves.Count == 0)
                DropIds.Add(pair.Key);
        }
        for (int i = 0; i < DropIds.Count; i++)
            Attempts.Remove(DropIds[i]);
        DropIds.Clear();
    }

    static void Release(Attempt attempt) {
        Attempts.Remove(attempt.id);
        if (attempt.moves == null)
            return;
        for (int i = 0; i < attempt.moves.Count; i++) {
            ushort id = attempt.moves[i].item;
            if (Newest.TryGetValue(id, out uint cur) && cur != attempt.id)
                continue;
            Exclusive.Remove(id);
        }
    }

    // true when this reply is the last place we asked for. an older one is dropped.
    static bool Fresh(ushort item, uint attempt) {
        if (!Newest.TryGetValue(item, out uint cur))
            return true;
        return cur == attempt;
    }

    static bool Guarded(ushort id) {
        return _guardFrame == Time.frameCount && Guard.Contains(id);
    }

    static void Send(Attempt attempt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemMove);
        writer.Put(attempt.session);
        writer.Put(attempt.id);
        int count = attempt.moves.Count;
        if (count > 16)
            count = 16;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            Move move = attempt.moves[i];
            writer.Put(move.item);
            writer.Put((byte)move.kind);
            writer.Put(move.destNet);
            writer.Put(move.destSlot);
        }
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        attempt.sentAt = Time.realtimeSinceStartup;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemMove)]
    static void Server_ItemMove(knetid clientId, NetDataReader reader) {
        uint session = reader.GetUInt();
        uint attempt = reader.GetUInt();
        int count = reader.GetByte();
        var moves = new List<Move>(count);
        for (int i = 0; i < count && !reader.EndOfData; i++) {
            var move = new Move {
                item = reader.GetUShort(),
                kind = (MoveKind)reader.GetByte(),
                destNet = reader.GetUShort(),
                destSlot = reader.GetByte(),
            };
            moves.Add(move);
        }
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody nb) || plr.body == null)
            return;
        Book book = BookFor(plr.playerId, session);
        if (book.receipts.TryGetValue(attempt, out Receipt prior)) {
            SendReceipt(plr.playerId, prior);
            return;
        }
        Receipt receipt = Commit(nb.body, session, attempt, moves, out bool terminal);
        if (receipt == null)
            return;
        if (terminal)
            Store(book, receipt);
        SendReceipt(plr.playerId, receipt);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemMoveReceipt)]
    static void Client_ItemMoveReceipt(knetid _, NetDataReader reader) {
        Apply(ReadReceipt(reader));
    }

    static Receipt Commit(Body body, uint session, uint attempt, List<Move> moves, out bool terminal) {
        terminal = true;
        if (moves == null || moves.Count == 0)
            return null;
        if (body == null || !body.conscious)
            return Refuse(session, attempt, MoveReason.Unconscious, moves);

        for (int i = 0; i < moves.Count; i++) {
            MoveReason reason = Check(body, moves[i]);
            if (reason == MoveReason.StillSyncing) {
                terminal = false;
                return new Receipt {
                    session = session,
                    attempt = attempt,
                    accepted = false,
                    reason = MoveReason.StillSyncing,
                    rows = new List<Row>(),
                };
            }
            if (reason != MoveReason.Ok)
                return Refuse(session, attempt, reason, moves);
        }

        var touched = new List<ushort>(moves.Count + 2);
        ItemSync.SuppressContainerWrite++;
        _commit++;
        _applying = true;
        try {
            for (int i = 0; i < moves.Count; i++)
                ApplyMove(body, moves[i], touched);
        } finally {
            _applying = false;
            _commit--;
            ItemSync.SuppressContainerWrite--;
        }

        for (int i = 0; i < touched.Count; i++) {
            if (!ItemSync.TryGetItemSyncInfo((knetid)touched[i], out SyncInfo si) || si == null)
                continue;
            si.WriteContainerInfo();
        }

        var rows = new List<Row>(touched.Count);
        for (int i = 0; i < touched.Count; i++)
            rows.Add(Current(touched[i]));
        for (int i = 0; i < moves.Count; i++) {
            bool listed = false;
            for (int j = 0; j < rows.Count; j++) {
                if (rows[j].item == moves[i].item)
                    listed = true;
            }
            if (!listed)
                rows.Add(Current(moves[i].item));
        }
        return new Receipt {
            session = session,
            attempt = attempt,
            accepted = Landed(moves, rows),
            reason = MoveReason.Ok,
            rows = rows,
        };
    }

    static MoveReason Check(Body body, Move move) {
        if (!ItemSync.NetIdToSyncInfoDict.TryGetValue(move.item, out SyncInfo si) || si == null)
            return MoveReason.StillSyncing;
        // item's getter throws once the object is gone, so the null check never gets to return missing.
        if (si.IsDestroyed || si.go == null)
            return MoveReason.Missing;
        Item item = si.item;
        if (item == null)
            return MoveReason.Missing;
        if (!ItemSync.CheckIfBodyCanStealThisItem(item, body))
            return MoveReason.Taken;
        if (!ItemSync.CheckIfBodyReachThisItem(item, body))
            return MoveReason.Unreachable;
        // owner drop stays. the host still has it in the slot until this lands, and that is the ask to let go.
        // the rubberband was a packet apply sending the same move. that one does not send.
        if (move.kind == MoveKind.World)
            return MoveReason.Ok;
        if (move.kind == MoveKind.Bag) {
            if (move.destNet == 0 || !NetObjectRegistry.TryGetSyncInfo(move.destNet, out SyncInfo box) || box == null || box.go == null)
                return MoveReason.StillSyncing;
            Container cont = box.container;
            if (cont == null && !box.go.TryGetComponent(out cont))
                return MoveReason.BrokenBag;
            if (ItemSync.BrokenBag(box, cont))
                return MoveReason.BrokenBag;
            return MoveReason.Ok;
        }
        if (!NetBody.TryGetNetBodyFromId(move.destNet, out NetBody owner) || owner.body == null)
            return MoveReason.Unconscious;
        if (move.kind == MoveKind.Worn) {
            if (item.Stats == null || !item.Stats.wearable)
                return MoveReason.BadSlot;
            Limb limb = owner.body.LimbByName(item.Stats.desiredWearLimb);
            if (limb == null || limb.dismembered)
                return MoveReason.BadSlot;
            return MoveReason.Ok;
        }
        if (owner.body.slots == null || move.destSlot >= owner.body.slots.Length)
            return MoveReason.BadSlot;
        // one item already in the slot is the swap. HostCanSeat lets that through.
        if (!ItemSync.HostCanSeat(owner.body, item, move.destSlot, out _))
            return MoveReason.BadSlot;
        return MoveReason.Ok;
    }

    static void ApplyMove(Body asker, Move move, List<ushort> touched) {
		if (!ItemSync.TryGetItem(move.item, out _, out Item item) || item == null)
            return;
        BagSleep.Wake(item);
        if (move.kind == MoveKind.World) {
            ItemSync.SafeUnloadItem(item);
            HeldSeat.Release(item);
            Note(item, touched);
            return;
        }
        if (move.kind == MoveKind.Bag) {
            if (!NetObjectRegistry.TryGetSyncInfo(move.destNet, out SyncInfo box) || box == null || box.go == null)
                return;
            Container cont = box.container;
            if (cont == null)
                box.go.TryGetComponent(out cont);
            if (cont == null)
                return;
            ItemSync.SafeUnloadItem(item);
            ItemSync.Container_ForceLoadItem(item, cont);
            Note(item, touched);
            return;
        }
        if (!NetBody.TryGetNetBodyFromId(move.destNet, out NetBody owner) || owner.body == null)
            return;
        if (move.kind == MoveKind.Worn) {
            ItemSync.SafeUnloadItem(item);
            ItemSync.Body_ForceWearWearable(owner.body, item);
            Note(item, touched);
            return;
        }
        Item occupant = owner.body.GetItem(move.destSlot);
        if (occupant != null && occupant != item) {
            owner.body.DropItem(move.destSlot);
            Note(occupant, touched);
        }
        ItemSync.SafeUnloadItem(item);
        owner.body.PickUpItem(item, move.destSlot, true);
        if (!owner.body.HoldingItem(item))
            HeldSeat.Seat(owner.body, item, move.destSlot);
        Note(item, touched);
    }

    static void Note(Item item, List<ushort> touched) {
        if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return;
        ushort id = si.syncId;
        if (id == 0 || touched.Contains(id))
            return;
        touched.Add(id);
    }

    static bool Landed(List<Move> moves, List<Row> rows) {
        for (int i = 0; i < moves.Count; i++) {
            bool found = false;
            for (int j = 0; j < rows.Count; j++) {
                if (rows[j].item != moves[i].item)
                    continue;
                found = true;
                if (!Matches(moves[i], rows[j]))
                    return false;
            }
            if (!found)
                return false;
        }
        return true;
    }

    static bool Matches(Move move, Row row) {
        if (move.kind == MoveKind.World)
            return row.data == 0;
        if (move.kind == MoveKind.Bag)
            return row.data == 1 && row.net == move.destNet;
        if (move.kind == MoveKind.Worn)
            return row.data == 100 && row.net == move.destNet;
        return row.data == (byte)(200 + move.destSlot) && row.net == move.destNet;
    }

    static Receipt Refuse(uint session, uint attempt, MoveReason reason, List<Move> moves) {
        var rows = new List<Row>(moves.Count);
        for (int i = 0; i < moves.Count; i++)
            rows.Add(Current(moves[i].item));

        if (log.verbose) Plugin.log.LogInfo("[move] " + reason + " attempt " + attempt);
        return new Receipt {
            session = session,
            attempt = attempt,
            accepted = false,
            reason = reason,
            rows = rows,
        };
    }

    static Row Current(ushort id) {
        var row = new Row { item = id };
        if (!ItemSync.TryGetItemSyncInfo((knetid)id, out SyncInfo si) || si == null || si.item == null)
            return row;
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(si.item);
        row.data = ici.containerExtraData1;
        row.net = ici.knetid;
        return row;
    }

    static void Apply(Receipt receipt) {
        if (receipt == null)
            return;
        if (receipt.reason == MoveReason.StillSyncing)
            return;
        if (Attempts.TryGetValue(receipt.attempt, out Attempt attempt))
            Release(attempt);
        if (receipt.reason == MoveReason.Missing) {
            DestroyGone(receipt);
            return;
        }
        if (receipt.rows == null)
            return;
        _applying = true;
        try {
            for (int i = 0; i < receipt.rows.Count; i++) {
                if (!Fresh(receipt.rows[i].item, receipt.attempt))
                    continue;
                // hold this parent. a container byte from before the reply must not move it back.
                NoteLatch(receipt.rows[i]);
                ApplyRow(receipt.rows[i]);
            }
        } finally {
            _applying = false;
        }
    }

    static void NoteLatch(Row row) {
        Latches[row.item] = new Latch {
            data = row.data,
            net = row.net,
            until = Time.realtimeSinceStartup + 2f,
        };
    }

    // true while the stored byte is still an older place. a match, or the wait running out, lets it through.
    internal static bool StaleContainer(SyncInfo si) {
        if (si == null)
            return false;
        ushort id = si.syncId;
        if (id == 0 || !Latches.TryGetValue(id, out Latch latch))
            return false;
        if (si.container_data1.Value == latch.data && si.container_netId.Value.id == latch.net) {
            Latches.Remove(id);
            return false;
        }
        if (Time.realtimeSinceStartup >= latch.until) {
            Latches.Remove(id);
            return false;
        }
        return true;
    }

    static bool Latched(Move move) {
        if (!Latches.TryGetValue(move.item, out Latch latch))
            return false;
        if (Time.realtimeSinceStartup >= latch.until)
            return false;
        return SamePlace(move, latch.data, latch.net);
    }

    static bool SamePlace(Move move, byte data, ushort net) {
        if (move.kind == MoveKind.World)
            return data == 0;
        if (move.kind == MoveKind.Bag)
            return data == 1 && net == move.destNet;
        if (move.kind == MoveKind.Worn)
            return data == 100 && net == move.destNet;
        return data == (byte)(200 + move.destSlot) && net == move.destNet;
    }

    // host lost this id. delete the local one, dont drop a copy on the ground.
    static void DestroyGone(Receipt receipt) {
        if (receipt.rows == null)
            return;
        _applying = true;
        try {
            for (int i = 0; i < receipt.rows.Count; i++) {
                if (!Fresh(receipt.rows[i].item, receipt.attempt))
                    continue;
                if (!ItemSync.TryGetItem(receipt.rows[i].item, out _, out Item item) || item == null)
                    continue;
                UnityEngine.Object.Destroy(item.gameObject);
            }
        } finally {
            _applying = false;
        }
    }

    static void ApplyRow(Row row) {
		if (!ItemSync.TryGetItem(row.item, out _, out Item item) || item == null)
            return;
        BagSleep.Wake(item);
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
        if (ici.containerExtraData1 == row.data && ici.knetid.id == row.net)
        {
            if (row.data == 0)
                HeldSeat.Release(item);
            return;
        }
        if (row.data == 0) {
            ItemSync.SafeUnloadItem(item);
            HeldSeat.Release(item);
            return;
        }
        if (row.data == 1) {
            if (!NetObjectRegistry.TryGetSyncInfo(row.net, out SyncInfo box) || box == null || box.go == null)
                return;
            Container cont = box.container;
            if (cont == null)
                box.go.TryGetComponent(out cont);
            if (cont == null || ItemSync.BrokenBag(box, cont))
                return;
            ItemSync.SafeUnloadItem(item);
            ItemSync.Container_ForceLoadItem(item, cont);
            return;
        }
        if (!NetBody.TryGetNetBodyFromId(row.net, out NetBody owner) || owner.body == null)
            return;
        if (row.data == 100) {
            ItemSync.SafeUnloadItem(item);
            ItemSync.Body_ForceWearWearable(owner.body, item);
            return;
        }
        if (row.data < 200)
            return;
        int slot = row.data - 200;
        if (owner.body.slots == null || slot < 0 || slot >= owner.body.slots.Length)
            return;
        Item occupant = owner.body.GetItem(slot);
        if (occupant != null && occupant != item)
            owner.body.DropItem(slot);
        ItemSync.SafeUnloadItem(item);
        owner.body.PickUpItem(item, slot, true);
        if (!owner.body.HoldingItem(item))
            HeldSeat.Seat(owner.body, item, slot);
        if (owner.IsBodyLocal())
            ItemSync.RecordAndApplyInvState(owner.body);
    }

    static Book BookFor(knetid playerId, uint session) {
        if (!Books.TryGetValue(playerId, out Book book)) {
            book = new Book { session = session };
            Books[playerId] = book;
            return book;
        }
        if (book.session != session) {
            book.session = session;
            book.receipts.Clear();
            book.order.Clear();
        }
        return book;
    }

    static void Store(Book book, Receipt receipt) {
        if (book.receipts.ContainsKey(receipt.attempt))
            return;
        book.receipts[receipt.attempt] = receipt;
        book.order.Enqueue(receipt.attempt);
        while (book.order.Count > Remember)
            book.receipts.Remove(book.order.Dequeue());
    }

    static void SendReceipt(knetid playerId, Receipt receipt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ItemMoveReceipt);
        WriteReceipt(writer, receipt);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, playerId);
    }

    static void WriteReceipt(NetDataWriter writer, Receipt receipt) {
        writer.Put(receipt.session);
        writer.Put(receipt.attempt);
        writer.Put(receipt.accepted ? (byte)1 : (byte)0);
        writer.Put((byte)receipt.reason);
        int count = receipt.rows != null ? receipt.rows.Count : 0;
        if (count > 16)
            count = 16;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            Row row = receipt.rows[i];
            writer.Put(row.item);
            writer.Put(row.data);
            writer.Put(row.net);
        }
    }

    static Receipt ReadReceipt(NetDataReader reader) {
        var receipt = new Receipt();
        receipt.session = reader.GetUInt();
        receipt.attempt = reader.GetUInt();
        receipt.accepted = reader.GetByte() != 0;
        receipt.reason = (MoveReason)reader.GetByte();
        int count = reader.GetByte();
        receipt.rows = new List<Row>(count);
        for (int i = 0; i < count && !reader.EndOfData; i++) {
            receipt.rows.Add(new Row {
                item = reader.GetUShort(),
                data = reader.GetByte(),
                net = reader.GetUShort(),
            });
        }
        return receipt;
    }

    static void Clear() {
        Attempts.Clear();
        Exclusive.Clear();
        Guard.Clear();
        Newest.Clear();
        Latches.Clear();
        Gesture.Clear();
        Books.Clear();
        _fromNet = 0;
        _depth = 0;
        _throwing = 0;
        _commit = 0;
        _applying = false;
        _session = 0;
    }
}
