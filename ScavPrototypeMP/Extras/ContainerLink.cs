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
        if (body.syncBody.Version != item.container_ver.Value) {
            body = null;
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
