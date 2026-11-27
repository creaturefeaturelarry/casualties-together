namespace Together;

// container_netId is the entity id only. the next spawn of that id is a different object.
// container_ver is the Version taken when the id was stored. a miss means leave the item alone.
internal static class ContainerLink {
    internal static byte VersionOf(NetBody body) {
        if (body == null || body.syncBody == null) {
            return 0;
        }
        return body.syncBody.Version;
    }

    internal static bool TryBody(SyncInfo item, out NetBody body) {
        body = null;
        if (item == null) {
            return false;
        }
        ushort id = item.container_netId.Value.id;
        if (id == 0) {
            return false;
        }
        if (!NetBody.TryGetNetBodyFromId(new knetid(id), out body) || body == null || body.syncBody == null) {
            return false;
        }
        // the body with this id is the one in the session. a version miss used to
        // hide the hotbar, so the item never got an object and could not be taken out.
        return true;
    }

    // the bag record is still in the session. go and version can lag a fresh join.
    internal static bool BagAlive(SyncInfo item) {
        if (item == null) {
            return false;
        }
        ushort id;
        try {
            id = item.container_netId.Value.id;
        } catch {
            return false;
        }
        if (id == 0) {
            return false;
        }
        // the id map misses bags that are only in the mesh map. the worn bag still shows, and its contents never spawn.
        if (!NetObjectRegistry.TryGetSyncInfoLive(new knetid(id), out SyncInfo bag) || bag == null || bag.IsDestroyed) {
            return false;
        }
        return true;
    }

    // live bag at this id, ignoring the stored version. seating uses this when the
    // stamp is stale so a rejoiner still puts the items in the bag he left with.
    internal static bool TryLiveBag(SyncInfo item, out SyncInfo bag) {
        bag = null;
        if (!BagAlive(item)) {
            return false;
        }
        ushort id = item.container_netId.Value.id;
        if (!NetObjectRegistry.TryGetSyncInfoLive(new knetid(id), out bag) || bag == null || bag.go == null || bag.container == null) {
            bag = null;
            return false;
        }
        return true;
    }

    internal static bool TryBag(SyncInfo item, out SyncInfo bag) {
        bag = null;
        if (item == null) {
            return false;
        }
        ushort id = item.container_netId.Value.id;
        if (id == 0) {
            return false;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(new knetid(id), out bag) || bag == null || bag.go == null) {
            return false;
        }
        if (bag.Version != item.container_ver.Value) {
            bag = null;
            return false;
        }
        return true;
    }
}
