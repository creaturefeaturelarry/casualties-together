using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Together {

    // a client use used to ignore host snapshots for a guessed round trip.
    // on a slow or lossy link that window ran out first and the old snapshot rolled the use back.
    // the host acks every request it finished, accepted or denied. the byte enum is full, so this rides CustomCommand.
    static class UseAck {
        const string Command = "use_ack";

        internal static void Send(knetid clientId, knetid item) {
            if (!Net.IsServer || item.id == 0) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null || plr.IsLocal) {
                return;
            }
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put(Command);
            writer.Put(item.id);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
        }

        // using (UseAck.Scope ack = UseAck.After(clientId, id)) acks on every return, including a deny.
        internal static Scope After(knetid clientId, knetid item) {
            return new Scope(clientId, item);
        }

        internal readonly struct Scope : System.IDisposable {
            readonly knetid client;
            readonly knetid item;

            internal Scope(knetid client, knetid item) {
                this.client = client;
                this.item = item;
            }

            public void Dispose() {
                Send(client, item);
            }
        }

        [CTPostInit]
        static void Init() {
            ClientMain.ClientCustomCommandsDict[Command] = (cmd, reader) => {
                reader.Get(out ushort id);
                if (id == 0) {
                    return;
                }
                if (NetObjectRegistry.TryGetSyncInfo((knetid)id, out SyncInfo si) && si != null) {
                    si.OnServerAck();
                }
            };
        }
    }
}
