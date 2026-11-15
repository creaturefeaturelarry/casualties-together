using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Together {

    // the client runs a use locally and asks the host to confirm it. it used to hold the item
    // for a guessed RTT window; on high or jittery ping the window ran out before the host's
    // answer arrived and the stale snapshot rolled the item back. now the host acks every
    // request it handled (accepted or denied) and the client holds until that ack lands.
    // the byte message enum is full, so this rides the named CustomCommand channel.
    static class UseAck {
        const string Command = "use_ack";

        // host side. call once per handled request, on every exit path.
        internal static void Send(knetid clientId, knetid item) {
            if (!Net.IsServer || item.id == 0) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null || plr.IsLocal) {
                return;
            }
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put(Command);
            writer.Put(item);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
        }

        // `using var _ = UseAck.After(clientId, id);` acks on every return path of a receiver.
        internal static Scope After(knetid clientId, knetid item) => new Scope(clientId, item);

        internal readonly struct Scope : System.IDisposable {
            readonly knetid client;
            readonly knetid item;
            internal Scope(knetid client, knetid item) {
                this.client = client;
                this.item = item;
            }
            public void Dispose() => Send(client, item);
        }

        [CTPostInit]
        static void Init() {
            ClientMain.ClientCustomCommandsDict[Command] = (cmd, reader) => {
                reader.Get(out knetid item);
                if (NetObjectRegistry.TryGetSyncInfo(item, out SyncInfo si) && si != null) {
                    si.OnServerAck();
                }
            };
        }
    }
}
