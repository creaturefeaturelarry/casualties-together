using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;


namespace Together;



public struct Bitset8 : INetSerializable {
    public byte bayt;

    public void Deserialize(NetDataReader reader) {
        reader.Get(out bayt);
    }

    public void Serialize(NetDataWriter writer) {
        writer.Put(bayt);
    }



    public Bitset8(byte bayt) { this.bayt = bayt; }

    public Bitset8(params bool[] bits) {
        for (int i = 0; i < bits.Length; ++i) {
            this[i] = bits[i];
        }
    }

    public bool this[int position] {
        get => Test(position);
        set => Set(position, value);
    }

    public static bool operator ==(Bitset8 a, Bitset8 b) {
        return a.bayt == b.bayt;
    }

    public static bool operator !=(Bitset8 a, Bitset8 b) {
        return !(a == b);
    }

    public static Bitset8 operator &(Bitset8 a, Bitset8 b) {
        return new Bitset8((byte)(a.bayt & b.bayt));
    }

    public static Bitset8 operator |(Bitset8 a, Bitset8 b) {
        return new Bitset8((byte)(a.bayt | b.bayt));
    }

    public static Bitset8 operator ^(Bitset8 a, Bitset8 b) {
        return new Bitset8((byte)(a.bayt ^ b.bayt));
    }

    public static Bitset8 operator ~(Bitset8 a) {
        return new Bitset8((byte)(~a.bayt));
    }


    public bool Test(int position) {
        byte mask = (byte)(1 << position);
        return (bayt & mask) == mask;
    }

    public void Set(int position) {
        bayt |= (byte)(1 << position);
    }

    public void Set(int position, bool value) {
        if (value)
            Set(position);
        else
            Reset(position);
    }

    public void Reset(int position) {
        bayt &= (byte)(~(1 << position));
    }

    public void Flip(int position) {
        bayt ^= (byte)(1 << position);
    }

    public bool Equals(Bitset8 s) {
        return this == s;
    }
    public override bool Equals(object obj) {
        return obj is Bitset8 && (Bitset8)obj == this;
    }
    public override int GetHashCode() {
        return bayt;
    }

    public override string ToString() {
        return Convert.ToString(bayt, 2).PadLeft(8, '0');
    }

};







public class KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE : MonoBehaviour {
    void Start() {
        if (TryGetComponent<SyncInfoGameObjectTracker>(out var t)) {
            t.syncInfo.go = null;
            t.syncInfo.Destroy();
            SyncInfoGameObjectTracker.Destroy(t);
        }
    }
}


public class SyncInfoGameObjectTracker : MonoBehaviour {

    public float playerDistanceSqr = 64;
    public bool isNearby = false; // is in 12 tile radius of any player  (but on client is only for local character)
    public bool isVisible = true; // is in 64 tile radius of any player  (but on client is only for local character)
    public bool isInSimulationRange = true; // is in 128 tile radius of any player  (but on client is only for local character)
    public bool isFar => !isInSimulationRange; // !isNearby && !isVisible && !isInSimulationRange;

    public knetid syncId;
    public SyncInfo syncInfo;

    public override string ToString() {
        if (this == null) {
            return "SyncInfo(NULL)";
        }
        if (syncInfo == null && syncId != 0) {
            return $"UnregisteredTracker({(IsBuilding() ? "building" : (IsItem() ? "item" : "UNKNOWN_TYPE"))}, id: {syncId}, res: {SyncInfo.GetItemID(this.gameObject) ?? this.gameObject.ToString()})";
        }
        return syncInfo.ToString();
    }
    public bool IsItem() {
        return GetComponent<Item>() != null;
    }
    public bool IsContainer() {
        return GetComponent<Container>() != null;
    }
    public bool IsBuilding() {
        return GetComponent<BuildingEntity>() != null;
    }

    void Start() {
        if (syncInfo != null) {
            gameObject.name = syncInfo.ToString();
        }
    }

    public void UpdateDistanceChecks() {
        distanceCheckTime = 0;

        Vector2 pos = transform.position;
        // a packed item follows the bag, not the loose position it had when it was picked up.
        isInSimulationRange = Net.IsServer
            ? NetObjectRegistry.ShouldKeepRegistered(gameObject)
            : SharedMain.WorldInSimRange(pos);

        if (!isInSimulationRange) {
            isNearby = false;
            isVisible = false;
            return;
        }

        if (Net.IsServer) {
            var cdist = ScavPlayer.GetDistanceToNearestLivingPlayer(pos);

            playerDistanceSqr = cdist.Item2;
        } else {
            playerDistanceSqr = KM.DistanceSqr(Camera.main.transform.position, pos);
        }
        isNearby = playerDistanceSqr < (12f * 12f);
        isVisible = playerDistanceSqr < (ItemSync.max_plr_view_distance_sqr);
    }

    static int VisualUpdateSlowerWhenNotVisible = 15;
    int visualUpdateTimer = 0;
    Transform _heldParent;
    bool _held;
    bool _wasLoose;
    int _crystal = -1;

    internal bool IsCrystal() {
        if (_crystal < 0) {
            _crystal = GetComponent<CrystalEnemy>() != null ? 1 : 0;
        }
        return _crystal == 1;
    }

    // parent walk is only when the parent changes. a ground item stays on the chunk stamp.
    bool HeldByBody() {
        Transform parent = transform.parent;
        if (parent == _heldParent)
            return _held;
        _heldParent = parent;
        _held = parent != null && parent.GetComponentInParent<Body>() != null;
        return _held;
    }

    // bag rows skip the packet when nothing changed. the loose edge still has to see the parent.
    internal void NoteCarry() {
        HeldByBody();
        _wasLoose = transform.parent == null;
    }

    // a thrown or dropped body is awake before its chunk comes due.
    // sleeping piles stay on the stamp.
    bool LooseAwake() {
        if (transform.parent != null) {
            return false;
        }
        Rigidbody2D rb;
        bool has;
        if (syncInfo != null && syncInfo.go == gameObject) {
            has = syncInfo.TryCachedRigidbody(out rb);
        } else {
            has = TryGetComponent(out rb);
        }
        if (!has || rb == null) {
            return false;
        }
        if (!rb.simulated || rb.IsSleeping()) {
            return false;
        }
        return rb.bodyType == RigidbodyType2D.Dynamic;
    }

    void FixedUpdate() {
        TickSync();
    }

    // bag sleep and chunk sleep turn the mesh off. this tick is the host snapshot
    // and the client pose, so a closed bag still has to run it or the rot never leaves.
    internal void TickSync() {
        if (syncInfo == null)
            return;

        // clients still ease off-screen copies. the chunk stamp is the host's write.
        if (!Net.IsServer) {
            // a manifestation is posed every step. the 15-step skip left the lunge a chunk behind.
            if (IsCrystal()) {
                visualUpdateTimer = 0;
                syncInfo.ClientFixedUpdate();
                KrokV5Optimization.EvidenceCrystal.Client(syncInfo);
                return;
            }
            visualUpdateTimer++;
            if (!isVisible && visualUpdateTimer < VisualUpdateSlowerWhenNotVisible)
                return;
            visualUpdateTimer = 0;
            syncInfo.ClientFixedUpdate();
            return;
        }

        // host physics writes this every step. the entity tick then ships that sample, not the chunk sweep.
        if (IsCrystal()) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            KrokV5Optimization.EvidenceCrystal.Host(syncInfo);
            return;
        }

        // the level tool is dragging this. a chunk stamp would leave the turn a step behind.
        if (syncInfo.placeGhost.Value) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            return;
        }

        // worn and held items follow the body every physics step.
        if (HeldByBody()) {
            _wasLoose = false;
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            return;
        }

        // the loose byte goes out on its own. this step has to carry the pose with it,
        // and an awake body keeps sending or the next client copy plants the old spot.
        bool loose = transform.parent == null;
        bool released = loose && !_wasLoose;
        _wasLoose = loose;
        if (released || LooseAwake()) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            return;
        }

        // chunks due this physics step. the rest of the union waits its turn.
        if (ChunkStamp.Due(transform.position, out bool inUnion)) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            return;
        }
        if (inUnion)
            return;

        visualUpdateTimer++;
        if (visualUpdateTimer < VisualUpdateSlowerWhenNotVisible)
            return;
        visualUpdateTimer = 0;
        syncInfo.WriteObjectIntoPacket();
    }

    void OnDestroy() {

        NetObjectRegistry.SyncRegistry.Remove(gameObject); // for sure 

        // null syncInfo means DestroyTrackedGameObject is already killing this go. don't free the id twice.
        if (syncInfo != null) {
            if (Net.IsServer) {
                // the go is going away but a bag id has to stay taken while any item still names it.
                // parked SAVESTATE_ stays too, mesh and id.
                if (NetObjectRegistry.KeepFarMesh(syncInfo, gameObject)) {
                    syncInfo.tracker = null;
                    syncInfo.go = null;
                    syncInfo = null;
                    return;
                }
                // go is still alive. nulling it made DestroyTrackedGameObject return
                // and the next NewGO stamped a new id on the leftover mesh.
                syncInfo.Destroy();
                syncInfo = null;
            }
        }

    }


    float distanceCheckTime = 0;
    void LateUpdate() {
        if (Net.IsServer || syncInfo == null || !IsCrystal())
            return;
        // fixed step only lands on the sample. this is the frames between packets.
        syncInfo.ClientCrystalPose();
    }
    void Update() {
        if (syncInfo == null) return;

        distanceCheckTime += Time.unscaledDeltaTime;

        if (distanceCheckTime > 0.1f) {
            UpdateDistanceChecks();
        }
    }

    public void AnimalHit() {
#if DEBUG
        log.l($"i detected AnimalHit on the tracker. what now?");
#endif

        // i think this functino will get forgotten, ive made the attack system a bit differently 

    }

}





public class NetObjectRegistry : CasualtiesTogetherSingleton {

    public static bool verbose => Multiplayer.verbose;

    const float _FastSyncFrequency = 0.043f;
    const float _DefaultSyncFrequency = 0.211f;
    const float _RareSyncFrequency = 1.894512f;

    const byte _MaxObjectsToSyncAtOnce = 25; // TODO maybe set it to 20 ?


    public static Dictionary<GameObject, SyncInfo> SyncRegistry = new Dictionary<GameObject, SyncInfo>();
    public static Dictionary<knetid, SyncInfo> NetIdToSyncInfoDict = new Dictionary<knetid, SyncInfo>();

    // CreateObject is inside Instantiate. Item.Start runs before the tracker is registered,
    // and NewGO used to destroy that copy. depth covers a prefab that instantiates another.
    internal static int ClientSpawnDepth;




    public static void UnregisterAllObjects() {
#if !RELEASE
        if (log.verbose) Plugin.log.LogWarning($"Called UnregisterAllObjects ");
#endif

        foreach (var si in new List<SyncInfo>(NetObjectRegistry.SyncRegistry.Values)) {
            si.Destroy();
        }
        NetObjectRegistry.NetIdToSyncInfoDict.Clear();
        NetObjectRegistry.SyncRegistry.Clear();
        foreach (var item in GameObject.FindObjectsOfType<SyncInfoGameObjectTracker>()) {
            item.syncInfo = null;
            SyncInfoGameObjectTracker.Destroy(item);// remove tracker
        }
    }

    // tutorial. drop leftover body and item ids so they dont stick onto the new bodies.
    // the player objects stay. a continue still reads mp_save.
    public static void WipeSessionEntities() {
        if (!Net.IsServer || Net.ServerEntityManager == null)
            return;

        int items = 0;
        var infos = new List<SyncInfo>(Net.ServerEntityManager.GetEntities<SyncInfo>());
        for (int i = 0; i < infos.Count; i++) {
            SyncInfo si = infos[i];
            if (si == null || si.IsDestroyed)
                continue;
            si.Destroy();
            items++;
        }

        int bodies = 0;
        var bodiesList = new List<SyncBody>(Net.ServerEntityManager.GetEntities<SyncBody>());
        for (int i = 0; i < bodiesList.Count; i++) {
            SyncBody sb = bodiesList[i];
            if (sb == null || sb.IsDestroyed)
                continue;
            sb.Destroy();
            bodies++;
        }

        SyncRegistry.Clear();
        NetIdToSyncInfoDict.Clear();
        foreach (var item in GameObject.FindObjectsOfType<SyncInfoGameObjectTracker>()) {
            item.syncInfo = null;
            SyncInfoGameObjectTracker.Destroy(item);
        }


        if (Together.log.verbose) Plugin.log.LogInfo("[tutorial] wiped items " + items + " bodies " + bodies);
    }

    public static void UnregisterAllObjectsButKeepInventory() {
#if !RELEASE
        if (log.verbose) Plugin.log.LogWarning($"Called UnregisterAllObjectsButKeepInventory");
#endif

        // a bag on a body stays. so does any id a kept item still stores, or the next craft inherits that bag.
        var all = new List<SyncInfo>(SyncRegistry.Values);
        var stay = new HashSet<SyncInfo>();
        HashSet<int> named = new();
        for (int i = 0; i < all.Count; i++) {
            SyncInfo si = all[i];
            if (!OnABody(si))
                continue;
            stay.Add(si);
            if (TryNet(si, out ushort net, out byte namedVer) && net != 0)
                named.Add(NameKey(net, namedVer));
        }
        for (int i = 0; i < all.Count; i++) {
            SyncInfo si = all[i];
            if (si == null || stay.Contains(si))
                continue;
            if (TryId(si, out ushort id) && named.Contains(NameKey(id, si.Version))) {
                stay.Add(si);
                continue;
            }
            if (si.go != null && IsSaveStateItem(si.go)) {
                ReleaseSaveStateItem(si);
                continue;
            }
            si.Destroy();
        }
        if (Net.IsServer)
            RetieAfterKeep();
    }

    // true when some other item's container id is this entity and this generation. freeing it would hand the bag to the next spawn.
    internal static bool ContainerStillNamed(SyncInfo si) {
        if (!TryId(si, out ushort id) || id == 0 || SyncRegistry == null)
            return false;
        byte ver = si.Version;
        foreach (SyncInfo other in SyncRegistry.Values) {
            if (other == null || other == si)
                continue;
            if (TryNet(other, out ushort net, out byte namedVer) && net == id && namedVer == ver)
                return true;
        }
        return false;
    }

    // bag something still names, or a pouch parked on SAVESTATE_. far drop keeps the mesh and the id.
    internal static bool KeepFarMesh(SyncInfo si, GameObject go) {
        if (ContainerStillNamed(si))
            return true;
        return go != null && IsSaveStateItem(go);
    }

    static bool OnABody(SyncInfo si) {
        if (si == null)
            return false;
        return InPlayerInventory(si.go);
    }

    // worn, bagged, or held. a dead body still counts, so a corpse's pack is not far-removed.
    static bool InPlayerInventory(GameObject go) {
        if (go == null)
            return false;
        try {
            if (go.GetComponent<Item>() == null || go.transform.parent == null)
                return false;
            return go.GetComponentInParent<Body>() != null;
        } catch (Exception) {
            return false;
        }
    }

    // far pass. free the id, leave the mesh. the next visit registers this same object.
    static void ReleaseFarSync(SyncInfo si) {
        if (si == null || si.IsDestroyed)
            return;
        GameObject go = si.go;
        if (go == null) {
            si.Destroy();
            return;
        }
        SyncInfoGameObjectTracker tracker = si.tracker;
        if (tracker == null)
            go.TryGetComponent(out tracker);
        if (tracker != null)
            tracker.syncInfo = null;
        SyncRegistry.Remove(go);
        si.go = null;
        si.Destroy();
    }

    static bool TryId(SyncInfo si, out ushort id) {
        id = 0;
        if (si == null)
            return false;
        try {
            id = si.syncId;
            return id != 0;
        } catch (Exception) {
            return false;
        }
    }

    static int NameKey(ushort id, byte ver) {
        return id | (ver << 16);
    }

    static bool TryNet(SyncInfo si, out ushort net, out byte ver) {
        net = 0;
        ver = 0;
        if (si == null)
            return false;
        try {
            net = si.container_netId.Value;
            ver = si.container_ver.Value;
            return true;
        } catch (Exception) {
            return false;
        }
    }

    // host. bags that lost their entity get one, then every carried item stores that live parent.
    // this is before the new layer registers containers, so a fresh bag cannot inherit the old id.
    static void RetieAfterKeep() {
        foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
            if (plr != null && plr.body != null)
                PlayerCheckpoint.RegisterBody(plr.body);
        }
        FixInvSync.Refresh();
        var all = new List<SyncInfo>(SyncRegistry.Values);
        HashSet<int> named = new();
        for (int i = 0; i < all.Count; i++) {
            if (!OnABody(all[i]))
                continue;
            if (TryNet(all[i], out ushort net, out byte namedVer) && net != 0)
                named.Add(NameKey(net, namedVer));
        }
        for (int i = 0; i < all.Count; i++) {
            SyncInfo si = all[i];
            if (si == null || OnABody(si))
                continue;
            if (si.go != null && IsSaveStateItem(si.go))
                continue;
            if (!TryId(si, out ushort id) || named.Contains(NameKey(id, si.Version)))
                continue;
            if (si.IsDestroyed)
                continue;
            si.Destroy();
        }
    }

    static bool IsSaveStateItem(GameObject go) {
        Transform walk = go.transform.parent;
        while (walk != null) {
            if (walk.name != null && walk.name.StartsWith("SAVESTATE_"))
                return true;
            walk = walk.parent;
        }
        return false;
    }

    // parked on SAVESTATE_. keep the id on this object. dropping it made the
    // rejoin register the same pouch again, and everyone else spawned a clone.
    static void ReleaseSaveStateItem(SyncInfo si) {
        if (si != null && si.go == null)
            si.Destroy();
    }

    // disconnect save parks gear under SAVESTATE_. kill the SyncInfo so clients drop
    // their copy, but leave the host mesh for PlayerSavedState.Apply.
    // a bag destroy also destroys the sync records nested under it. those still
    // pointed at the real items, so the bag came back empty. unhook every mesh first.
    public static void DetachSyncKeepMesh(GameObject go) {
        if (!Net.IsServer || go == null) {
            return;
        }
        Item[] items = go.GetComponentsInChildren<Item>(true);
        var doomed = new List<SyncInfo>(items != null ? items.Length + 1 : 1);
        if (items != null) {
            for (int i = 0; i < items.Length; i++) {
                if (items[i] == null) {
                    continue;
                }
                SyncInfo si = UnhookMesh(items[i].gameObject);
                if (si != null) {
                    doomed.Add(si);
                }
            }
        }
        SyncInfo root = UnhookMesh(go);
        if (root != null) {
            doomed.Add(root);
        }
        for (int i = 0; i < doomed.Count; i++) {
            SyncInfo si = doomed[i];
            if (si != null && !si.IsDestroyed) {
                si.Destroy();
            }
        }
    }

    // drop the sync link and keep the GameObject. safe to call twice.
    static SyncInfo UnhookMesh(GameObject go) {
        if (go == null) {
            return null;
        }
        SyncInfo si = null;
        if (SyncRegistry.TryGetValue(go, out SyncInfo listed) && listed != null && !listed.IsDestroyed) {
            si = listed;
        } else if (go.TryGetComponent(out SyncInfoGameObjectTracker existing) && existing.syncInfo != null && !existing.syncInfo.IsDestroyed) {
            si = existing.syncInfo;
        }
        if (si == null) {
            return null;
        }
        if (go.TryGetComponent(out SyncInfoGameObjectTracker tracker)) {
            tracker.syncInfo = null;
        }
        SyncRegistry.Remove(go);
        si.tracker = null;
        si.go = null;
        return si;
    }




    // call this in Start of any entity that needs to be synced
    public static void NewGO(GameObject obj) {
#if TESTING
		if (ObjectShouldNotBeSynced(obj)) {
			throw new Exception($"Attempting to register an ignored object: {DebugStringGameObject(obj)}");
		}
#endif

        if (Net.IsServer) {
            //safeNewGO(obj); // its done by the system now
            Server_EnsureItemIsNetworkRegistered(obj);
        } else if (ClientSpawnDepth > 0) {
            // this Start belongs to the replica CreateObject is still building.
        } else {
            if (!NetObjectRegistry.SyncRegistry.TryGetValue(obj, out var isi)) {
                SafeDestroyObject(obj);
            }
        }
    }
    public static void SafeNewGO(GameObject obj) {
        if (!ObjectShouldNotBeSynced(obj)) {
            NewGO(obj);
        }
    }



    public static HashSet<GameObject> GetIdentifiedBackgroundsInRadius(Vector2 pos, float radius) {
        HashSet<GameObject> r = new();

        radius *= radius;

        foreach (var item in TrackerBuildingEntityBackgroundified.all_instances) {
            if ((item.is_a_background_tilemap // || item.is_weird_nocollision_building
                ) // && item.TryGetComponent<BuildingEntity>(out var be) 
                && KM.DistanceCheck_PreSqr(item.transform.position, pos, radius)) {
                r.Add(item.gameObject);
            }
        }

        return r;
    }

    public static HashSet<GameObject> GatherClosestObjectsToSync(Vector2 pos, Body body, float radius) {
        var objects = GetIdentifiedBackgroundsInRadius(pos, radius);
        {
            // get shit to sync that has a collider 
            var phys_in_radius = Physics2D.OverlapCircleAll(pos, radius);
            var only_items_and_buildings = phys_in_radius.Where(x => x.GetComponent<Item>() != null || x.GetComponent<BuildingEntity>() != null).Select(x => x.gameObject);

            // also dump all the items of containers
            foreach (var item in only_items_and_buildings) {
                if (item.TryGetComponent<Container>(out var ci)) {
                    objects.AddRange(ci.GetAllItems().Select(x => x.gameObject));
                }
            }

            objects.AddRange(only_items_and_buildings);

            if (body != null) {
                objects.AddRange(body.GetAllItemsThorough().Select(x => x.gameObject));
            }

            { // add players inventories
                var rad = NetBody.GetBodiesInRadius(pos, radius);
                if (body != null && body.TryGetNetBody(out var locnb)) {
                    rad.Remove(locnb);
                }
                foreach (var item in rad) {
                    foreach (var eeeeeeeeeeeee in item.body.GetAllItemsThorough()) {
                        objects.Add(eeeeeeeeeeeee.gameObject);
                    }
                }
            }

        }
        objects.RemoveWhere(x => (x == null || ObjectShouldNotBeSynced(x))); // i only need stuff that should be synced
        return objects;
    }

    // container children use the bag's position. the item's own transform can sit at the old drop point.
    internal static bool ShouldKeepRegistered(GameObject obj) {
        if (obj == null)
            return false;
        // the cargo heart is part of the layer. joiners get it with the baseline, not when they walk up.
        if (obj.GetComponent<KeepForJoiners>() != null)
            return true;
        var simulationTransform = obj.transform;
        while (simulationTransform.parent != null &&
            simulationTransform.parent.TryGetComponent<Container>(out _)) {
            simulationTransform = simulationTransform.parent;
        }
        return SharedMain.WorldInSimRange(simulationTransform.position);
    }

    const float immitiate_registration_search_radius = 80f;

    internal static void _ImmidiatelyRegisterCloseObjects(Vector2 pos, Body body) {
        var touching = GatherClosestObjectsToSync(pos, body, immitiate_registration_search_radius);
        touching.RemoveWhere(x => IsRegistered(x) || (Net.IsServer && !ShouldKeepRegistered(x)));

        int counter = 0;
        int crystalCounter = 0;
        foreach (var thing in touching) {
#if FULLGAME
            bool crystal = CrystalAuthority.LooksLikeCrystal(thing);
            if (crystal && crystalCounter >= CrystalAuthority.SyncBudget.HostRegisterPerStamp) {
                continue;
            }
#endif
            NewGO(thing);
#if FULLGAME
            if (crystal) {
                crystalCounter++;
                counter++;
            } else
#endif
            if (!KM.DistanceCheck(thing.transform.position, pos, 10)) {
                counter++;
            }
            if (counter >= MAX_REGISTER_BATCH) {
                break;
            }
        }
    }

    // check if its movable
    public static bool IsObjectDynamic(GameObject go) {
        if (go.TryGetComponent<Item>(out var item)) {
            return go.transform.parent == null;
        }
        if (go.TryGetComponent<GrabberPlant>(out var gb)) {
            return true;
        }
        if (!go.TryGetComponent<Rigidbody2D>(out var rb)) {
            return false;
        }
        return rb.bodyType == RigidbodyType2D.Dynamic;
    }
    public static bool IsObjectPhysicsActive(GameObject go) {
        if (go.TryGetComponent<Item>(out var item)) {
            if (go.transform.parent != null) { // if its in a container, container will be synced eventually
                return false;
            }
        }
        if (!go.TryGetComponent<Rigidbody2D>(out var rb) || rb.IsSleeping()) {
            return false;
        }
        return true;
    }


    public static void AlertObjectNotRegistered(bool popup = false) {
        if (popup) {
            PlayerCamera.main.DoAlert(Lang.Get("obj_not_net_registered"));
        }
        var msg = "Attempted to interact with unregistered object";
        if (log.verbose) {
            msg += ":\n" + (new System.Diagnostics.StackTrace()).ToString();
        }
        log.warn(msg);

        PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
    }

    static int MAX_REGISTER_BATCH = 16;
    static int server_slowmode_type_counter = 0;
    internal static void _ObjectSyncUpdateLoopUniversal(bool do_slow_mode) {
        if (Net.IsClient) {
            var locplr = ScavPlayer.LocalPlayer;
            if (locplr.body != null) { // is in world
                if (!locplr.body.alive == do_slow_mode) {
                    _ImmidiatelyRegisterCloseObjects(PlayerCamera.main.transform.position, locplr.body);
                }
                if (locplr.body.alive && do_slow_mode) { // and a lil bit down
                    _ImmidiatelyRegisterCloseObjects((Vector2)PlayerCamera.main.transform.position - new Vector2(0, 60), null);
                }
            }
        } else {
            // host near-stamp is SafetyNet: one quadrant of one living player per frame.
            // the rare pass still drops far trackers, then queues dead players or the
            // living-player downward circle. SlowStamp runs one of those circles every 0.1s.
            if (do_slow_mode) {
#if FULLGAME
                LayerBuildingStream.HostRegister();
#endif

                { // do unregistering
                    int autounregistercount = 0;
                    List<SyncInfo> dropWhole = null;

                    // unregister all items that are out of range of all players
                    foreach (var item in SyncRegistry) {
                        // parked gear. dropping the tracker cleared go, and the rejoin minted a second id on the same mesh.
                        if (IsSaveStateItem(item.Key))
                            continue;
                        item.Value.tracker.UpdateDistanceChecks();

                        if (item.Value.tracker.isFar) {
                            // pack, hotbar, worn. alive or a corpse. the id stays on the mesh.
                            if (InPlayerInventory(item.Key))
                                continue;
                            // far pass destroys the mesh. a trader near a client then comes back as a new id
                            // and the click never lands. they are few, so the id stays.
                            if (item.Key.TryGetComponent(out TraderScript _))
                                continue;
                            // crystals far-drop; clients refill from host via position cache on re-enter.
#if !RELEASE
                            log.l($"AUTO UNREGISTERING: {item.Value}");
#endif
                            if (item.Value != null && !item.Value.IsDestroyed) {
                                if (item.Value.go == null)
                                    item.Value.go = item.Key;
                                if (dropWhole == null)
                                    dropWhole = new List<SyncInfo>();
                                dropWhole.Add(item.Value);
                                autounregistercount++;
                                if (autounregistercount > MAX_REGISTER_BATCH * 3) {
                                    break;
                                }
                            }
                        }
                    }

                    if (dropWhole != null) {
                        for (int i = 0; i < dropWhole.Count; i++) {
                            SyncInfo si = dropWhole[i];
                            // a bag something still names keeps this id. freeing it hands the contents to the next spawn.
                            if (si != null && !si.IsDestroyed && !KeepFarMesh(si, si.go))
                                ReleaseFarSync(si);
                        }
                    }
                }

                if (server_slowmode_type_counter == 0) {
                    foreach (var plr in ScavPlayer.AllDeadPlayers) {
                        SlowStamp.Enqueue(plr.pos, plr.body);
                    }
                    server_slowmode_type_counter++;
                } else if (server_slowmode_type_counter == 1) {
                    foreach (var plr in ScavPlayer.AllLivingPlayers) {
                        SlowStamp.Enqueue(plr.pos - new Vector2(0, 80), plr.body);
                    }
                    server_slowmode_type_counter++;
                } else {
                    server_slowmode_type_counter = 0;
                }
            }
        }
    }










    public static bool IsRegistered(Component obj) {
        return SyncRegistry.ContainsKey(obj.gameObject);
    }
    public static bool IsRegistered(GameObject obj) {
        return SyncRegistry.ContainsKey(obj);
    }
    public static SyncInfo GetSyncInfo(Component obj) {
        if (obj != null && SyncRegistry.TryGetValue(obj.gameObject, out var si)) {
            return si;
        }
        return null;
    }
    public static bool TryGetSyncInfo(Component obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj.gameObject, out si)) {
            return true;
        }
        si = null;
        return false;
    }
    public static bool TryGetSyncInfo(GameObject obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj, out si)) {
            return true;
        }
        si = null;
        return false;
    }
    public static bool TryGetSyncInfo(knetid syncid, out SyncInfo si) {
        if (NetIdToSyncInfoDict.TryGetValue(syncid, out si)) {
            return true;
        }
        si = null;
        return false;
    }
    // the id map can miss after a reused id is destroyed late. the mesh map still has the live object.
    public static bool TryGetSyncInfoLive(knetid syncid, out SyncInfo si) {
        if (TryGetSyncInfo(syncid, out si) && (si is not null) && !si.IsDestroyed) {
            return true;
        }
        si = null;
        if (SyncRegistry == null) {
            return false;
        }
        foreach (SyncInfo cur in SyncRegistry.Values) {
            if ((cur is null) || cur.IsDestroyed) {
                continue;
            }
            try {
                if (cur.syncId != syncid) {
                    continue;
                }
            } catch (Exception) {
                continue;
            }
            NetIdToSyncInfoDict[syncid] = cur;
            si = cur;
            return true;
        }
        return false;
    }
    public static bool TryGetSyncInfoOrRegister(Component obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj.gameObject, out si)) {
            return true;
        }
        NewGO(obj.gameObject);
        si = null;
        bool created = false;
        FixSyncRegister.Recover(obj.gameObject, ref si, ref created);
        return created;
    }
    public static bool TryGetSyncInfoOrRegister(GameObject obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj, out si)) {
            return true;
        }
        NewGO(obj);
        si = null;
        bool created = false;
        FixSyncRegister.Recover(obj, ref si, ref created);
        return created;
    }


    internal static HashSet<string> lowpriority_objects_resourceids = new HashSet<string> {
        "casing",
        "droppings",
    };
    static HashSet<string> networkignored_buildings_maingame = new HashSet<string> {

        "KSMUTLTI_IGN",

        // i am renaming backrounds into my own ids, theyre never separate and always unique even when they have same id
        "background",

        // dont sync lifepod shit (theire children of "background" thingy that is a part of lifepod prefab,  theyre all referring to their parent background for their syncid so its alright)
        "lifepodbuttonheat",
        "lifepodbuttonshower",
        "lifepodheater",
        "lifepodshower",
        "lifepodpump", 

        // these are the decorations of sandvines, remove em
        "sandvine",
        // fullgame veinloop endcaps (climbable mid-section is Special/veinloop)
        "veintop",
        "veinbottom",
        // post V5t5 ropes are synced separately (actually i will instead check for climbable component
        //krokosha_BuildingEntity_Rope_TrackerComponent.sandvine_new_id,
        //krokosha_BuildingEntity_Rope_TrackerComponent.climbingropeextended_id,
        //krokosha_BuildingEntity_Rope_TrackerComponent.mushroomrope_new_id,

        // no gameplay significance
        "marbleBackground",

        // idk what is this
        "mushroomdropperpayload",

        // baked into the end lab prefab. there is no Resources entry, so a net spawn is null and the client clear deletes the real one.
        "macrofleshrebuilder",
    };

    public static bool ObjectShouldBeSynced(GameObject obj) => !ObjectShouldNotBeSynced(obj);
    public static bool ObjectShouldNotBeSynced(GameObject obj) {
        {
            if (obj.TryGetComponent(out BuildingEntity b)) {
                if (networkignored_buildings_maingame.Contains(b.id)) {
                    return true;
                }
                if (obj.TryGetComponent<Climbable>(out var c)) {
                    // spicevine is a climbable you can also smash. the rope tracker would leave the break on one machine.
                    bool spice = b.id == TrackerClimbableEntity.spicevine_id
                        || b.id == TrackerClimbableEntity.spicevineceiling_id
                        || (c.name != null && c.name.StartsWith("spicevineceiling"));
                    if (spice) {
                        if (c.name != null && c.name.StartsWith("spicevineceiling") && b.id != TrackerClimbableEntity.spicevineceiling_id)
                            b.id = TrackerClimbableEntity.spicevineceiling_id;
                        return false;
                    }
                    if (!obj.TryGetComponent<TrackerClimbableEntity>(out var _)) {
                        var r = obj.AddComponent<TrackerClimbableEntity>();
                        // late-bind fullgame climbables if prefab init had not rewritten this instance yet
                        if (b.id == "veinloop" || b.id == TrackerClimbableEntity.veinloop_new_id) {
                            if (string.IsNullOrEmpty(r.og_id)) {
                                r.og_id = "veinloop";
                            }
                            b.id = TrackerClimbableEntity.veinloop_new_id;
                        } else if (b.id == TrackerClimbableEntity.spicevine_id
                                   || b.id == TrackerClimbableEntity.spicevineceiling_id
                                   || c.name.StartsWith("spicevineceiling")) {
                            // ceiling prefab ships with BuildingEntity.id "spicevine" - rewrite so clients Load the right Resources path
                            if (c.name.StartsWith("spicevineceiling") && b.id != TrackerClimbableEntity.spicevineceiling_id) {
                                if (string.IsNullOrEmpty(r.og_id)) {
                                    r.og_id = b.id;
                                }
                                b.id = TrackerClimbableEntity.spicevineceiling_id;
                            }
                        }
                        if (!r.is_known_climbable) {
                            log.error($"Unknown Climbable: {c.name} -> id: {b.id}");
                        }
                    }
                    return true;
                }
            }
        }

        if (obj.TryGetComponent(out KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE ignorerrrrrrrrrrrrrrrrrrrrrrrrrrrrr)) {
            return true;
        }

#if FULLGAME
        // CS-style local-only crystal (no SyncInfo tracker): never treat as a Together-owned sync shell.
        if (obj.TryGetComponent(out TogetherCrystalLocalMarker _)
            && !obj.TryGetComponent(out SyncInfoGameObjectTracker _)) {
            return true;
        }
#endif

        return false;
    }


    public static string DebugStringGameObject(GameObject go) {
        // $"SyncInfo({(IsBuilding() ? "building" : (IsItem() ? "item" : "UNKNOWN_TYPE"))}, id: {syncId}, res: {this.GetItemID() ?? go.ToString()})"
        var s = $"{(go.TryGetComponent<BuildingEntity>(out var b) ? "BUILDING" : (go.TryGetComponent<Item>(out var _) ? "ITEM" : "UNKNOWN_OBJECT"))}(res: {SyncInfo.GetItemID(go)}, name: {go.name}";
        if (go.TryGetTracker(out var tracker)) {
            s += $"{tracker}";
        }
        s += $")";
        return s;
    }


    public static SyncInfo Server_EnsureItemIsNetworkRegistered(GameObject go) {
        try {
            if (SyncRegistry.TryGetValue(go, out var v) && v != null && !v.IsDestroyed) {
                return v;
            }
            // registry missed, but this object already has a live id. reuse it.
            if (go.TryGetComponent(out SyncInfoGameObjectTracker existing) && existing.syncInfo != null && !existing.syncInfo.IsDestroyed) {
                SyncRegistry[go] = existing.syncInfo;
                if (existing.syncInfo.go == null)
                    existing.syncInfo.go = go;
#if FULLGAME
                // include inert crystal buildings (CB already destroyed) so FlagInert can wipe client FX.
                if (go.TryGetComponent(out CrystalBehaviour _) || CrystalAuthority.LooksLikeCrystal(go)) {
                    CrystalAuthority.HostOnCrystalRegistered(go, existing.syncInfo);
                }
#endif
                return existing.syncInfo;
            }

#if TESTING
            if (ObjectShouldNotBeSynced(go)) {
                throw new Exception($"Attempting to register an ignored object: {DebugStringGameObject(go)}");
            }
#endif

            var si = Net.ServerEntityManager.AddEntity<SyncInfo>((e) => {
                e.go = go;
                e.GetItemID();
            });

#if FULLGAME
            // crystals roll GenerationFinished before interest-range register - capture now that syncId exists
            // also push inert for crystal-like buildings whose CB was destroyed on the host roll.
            if (go != null && si != null
                && (go.TryGetComponent(out CrystalBehaviour _) || CrystalAuthority.LooksLikeCrystal(go))) {
                SyncRegistry[go] = si;
                if (si.go == null) {
                    si.go = go;
                }
                CrystalAuthority.HostOnCrystalRegistered(go, si);
            }
#endif
            return si;
        } catch (Exception e) {
            Plugin.Logger.LogError($"Server_EnsureItemIsNetworkRegistered Error: {go}\n{e.ToString()}\n{new StackTrace()}");
            return null;
        }
    }



    public static bool Client_DeleteUnregisteredSyncableObject(GameObject obj) {
        if (!ObjectShouldNotBeSynced(obj)) {
            SafeDestroyObject(obj);
            return true;
        }
        return false;
    }












    public static void SafeDestroyObject(GameObject go) {

        if (go.TryGetComponent(out Container container)) {
            container.UnloadAllItems();
        }

        if (NetObjectRegistry.SyncRegistry.TryGetValue(go, out var si)) {
            NetObjectRegistry.SyncRegistry.Remove(si.go);
            si.go = null;
            si.Destroy();
        }

        Destroy(go.gameObject);
    }






    static float timer_fastsync = 0;
    static float timer_objsync = 0;
    static float timer_raresync = 0;
    void Update() {
        if (!Net.IsRunning) return;
        if (!Util.IsWorldGenerated()) return;

        timer_fastsync += ClientMain.AdaptiveSyncTimerDelta;
        timer_objsync += ClientMain.AdaptiveSyncTimerDelta;
        timer_raresync += ClientMain.AdaptiveSyncTimerDelta;
        if (timer_fastsync > _FastSyncFrequency) {
            //doFastSyncUpdate();
            timer_fastsync = 0;
        }
        if (timer_objsync > _DefaultSyncFrequency) {
            _ObjectSyncUpdateLoopUniversal(false);
            timer_objsync = 0;
        }
        if (timer_raresync > _RareSyncFrequency) {
            _ObjectSyncUpdateLoopUniversal(true);
            timer_raresync = 0;
        }


    }



    public static bool _DEV_ENABLE_SYNCINFO_SNITCHING = false;

    void OnGUI() {

        var ogfont = GUI.skin.label.fontSize;

        if (Net.IsRunning && _DEV_ENABLE_SYNCINFO_SNITCHING) {
            foreach (var item in SyncRegistry) {
                var go = item.Key;
                var isi = item.Value;
                try {

                    var pos = (Vector2)Camera.main.WorldToScreenPoint(go.transform.position);
                    pos = new Vector2(pos.x, Screen.height - pos.y);

                    if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {

                        const float bsize = 17;
                        const float elsize = 13;

                        float y = 0;

                        GUI.skin.label.normal.textColor = UnityEngine.Color.white;

                        GUI.skin.label.fontSize = 10;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "syncid: " + isi.syncId);
                        y += elsize;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "combat_relax: " + isi.relaxed_combat_sync);
                        y += elsize;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "lastUpdateTick: " + (isi.lastUpdateTick));
                        y += elsize;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "pos: " + (Vector2)go.transform.position);
                        y += elsize;
                        int o = 0;
                        bool yes = false;
                        if (isi.IsItem()) {
                            o = ItemSync.cur_items_check.IndexOf(isi.item);
                            yes = o >= ItemSync.cur_items_check_index || o < ItemSync.cur_items_check_index + ItemSync.max_items_to_check_per_iteration;

                            GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "is contained: " + ItemSync.ItemIsInContainer(isi.item));
                            y += elsize;
                        } else if (isi.IsBuilding()) {
                            o = ScavMPBuildingSynchronizer.cur_buildings_check.IndexOf(isi.building);
                            yes = o >= ScavMPBuildingSynchronizer.cur_buildings_check_index || o < ItemSync.cur_items_check_index + ScavMPBuildingSynchronizer.cur_buildings_check_index;

                            GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "physics: " + ScavMPBuildingSynchronizer.BuildingHasActivePhysics(isi.building));
                            y += elsize;

                            if (isi.IsTrader()) {
                                var ttracker = go.GetComponent<TrackerTrader>();

                                if (ttracker.focused_body != null) {

                                    GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "trader_body: " + ttracker.focused_body.GetNetBody().plr);
                                    y += elsize;
                                }

                            }

                        }
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "system index: " + o + (yes ? " NOW" : ""));
                        y += elsize;
                        var t = isi.tracker;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "see12: " + t.isNearby);
                        y += elsize;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "see64: " + t.isVisible);
                        y += elsize;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "see200: " + t.isInSimulationRange);
                        y += elsize;



                    }
                } catch { }
            }

        }

        GUI.skin.label.normal.textColor = UnityEngine.Color.white;
        GUI.skin.label.fontSize = ogfont;
    }









}