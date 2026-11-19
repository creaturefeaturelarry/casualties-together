using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Together {

    // hold carry while grounded to share an airborne player's vertical speed.
    // the faller keeps one share, the catcher is hit with the other. stock limb impact
    // starts once the hit is faster than jump speed, and anything slower just stops.
    static class FallCatch {
        const float ArmRefresh = 0.2f;
        const float ArmLifetime = 0.45f;
        const float HitReach = 4f;
        const float MaxForce = 250f;

        // rows are the catcher, columns are the faller. exp 0, milky 1, dune 2.
        static readonly float[] FallerKeep = {
            0.50f, 0.35f, 0.50f,
            0.60f, 0.50f, 0.65f,
            0.35f, 0.25f, 0.50f,
        };
        static readonly float[] CatcherTake = {
            0.50f, 0.35f, 0.75f,
            0.75f, 0.50f, 1.00f,
            0.35f, 0.25f, 0.50f,
        };

        static readonly Dictionary<ushort, float> armedUntil = new Dictionary<ushort, float>();
        static readonly HashSet<ushort> latched = new HashSet<ushort>();
        static readonly HashSet<ushort> slammed = new HashSet<ushort>();
        static readonly List<ushort> drop = new List<ushort>();
        static float nextArmSend;
        static float nextLocalHit;
        static bool armSent;

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict["fall_arm"] = (plr, cmd, args) => {
                if (plr == null || args == null || args.Length < 2) {
                    return;
                }
                bool on = args[1] == "1";
                NoteArmed(plr.playerId.id, on);
                BroadcastArm(plr.playerId.id, on);
            };
            ServerMain.ServerCustomCommandsDict["fall_hit"] = (plr, cmd, args) => {
                if (plr == null || args == null || args.Length < 3) {
                    return;
                }
                if (!ushort.TryParse(args[1], out ushort catcherId)) {
                    return;
                }
                if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float force)) {
                    return;
                }
                AcceptHit(plr, catcherId, force);
            };
            ClientMain.ClientCustomCommandsDict["fall_arm"] = (cmd, reader) => {
                reader.Get(out ushort id);
                reader.Get(out byte on);
                NoteArmed(id, on == 1);
            };
            ClientMain.ClientCustomCommandsDict["fall_hit"] = (cmd, reader) => {
                reader.Get(out float force);
                HitLocal(force);
            };
        }

        public static void Tick() {
            if (!Net.IsRunning || !Util.IsWorldGenerated()) {
                return;
            }
            if (!NetBody.TryGetLocalNetBody(out NetBody me) || me.body == null || me.plr == null || !me.body.alive) {
                return;
            }
            ArmSelf(me);
            TryBreak(me);
            Brace(me);
            DropLatch(me.body);
            DropSlam(me.body);
        }

        static void ArmSelf(NetBody me) {
            bool armed = me.body.grounded && me.body.currentClimbable == null && Input.GetKey(KeyBinds.GetBind(CoopKeybinds.carry));
            if (armed) {
                if (Time.time < nextArmSend && armSent) {
                    return;
                }
                nextArmSend = Time.time + ArmRefresh;
                armSent = true;
                if (Net.IsServer) {
                    NoteArmed(me.plr.playerId.id, true);
                    BroadcastArm(me.plr.playerId.id, true);
                } else {
                    SendCommand("fall_arm 1");
                }
                return;
            }
            if (!armSent) {
                return;
            }
            armSent = false;
            nextArmSend = 0f;
            if (Net.IsServer) {
                NoteArmed(me.plr.playerId.id, false);
                BroadcastArm(me.plr.playerId.id, false);
            } else {
                SendCommand("fall_arm 0");
            }
        }

        static void TryBreak(NetBody me) {
            Body body = me.body;
            if (body.grounded || body.currentClimbable != null) {
                return;
            }
            float vy = Vertical(body);
            float gate = body.jumpSpeed;
            if (gate < 1f) {
                gate = 26f;
            }
            // damaging falls are downward. sideways speed does not count.
            if (vy >= -gate) {
                return;
            }
            bool found = false;
            ushort caught = 0;
            float keep = 0.5f;
            float taken = 0f;
            foreach (NetBody other in NetBody.all_instances) {
                if (other == null || other == me || !other.IsPlayer || other.body == null || other.plr == null || !other.body.alive) {
                    continue;
                }
                ushort id = other.plr.playerId.id;
                if (!armedUntil.TryGetValue(id, out float until) || Time.time > until) {
                    continue;
                }
                if (latched.Contains(id) || !Overlaps(body, other.body)) {
                    continue;
                }
                found = true;
                caught = id;
                Share(body.charType, other.body.charType, out keep, out float take);
                taken = Mathf.Abs(vy) * take;
                break;
            }
            if (!found) {
                return;
            }
            latched.Add(caught);
            if (!ScavPlayer.TryGetPlayerFromClientId(caught, out ScavPlayer catcher) || catcher.body == null) {
                ScaleVertical(body, keep);
                return;
            }
            // flop before the fall is slowed. every catch puts the catcher down.
            Slam(catcher.body, Mathf.Min(taken, MaxForce));
            ScaleVertical(body, keep);
            // a real body collision already lands at the slowed speed. only add the hit when the two pass through each other.
            if (taken > catcher.body.jumpSpeed && !SolidContact(body, catcher.body)) {
                SendHit(caught, taken);
            }
        }

        static void Brace(NetBody me) {
            Body body = me.body;
            if (!body.grounded || body.currentClimbable != null) {
                return;
            }
            if (!Input.GetKey(KeyBinds.GetBind(CoopKeybinds.carry))) {
                return;
            }
            foreach (NetBody other in NetBody.all_instances) {
                if (other == null || other == me || !other.IsPlayer || other.body == null || other.plr == null || !other.body.alive) {
                    continue;
                }
                if (other.body.grounded || other.body.currentClimbable != null) {
                    continue;
                }
                ushort id = other.plr.playerId.id;
                if (slammed.Contains(id) || !Overlaps(body, other.body)) {
                    continue;
                }
                float vy = Vertical(other.body);
                float gate = other.body.jumpSpeed;
                if (gate < 1f) {
                    gate = 26f;
                }
                if (vy >= -gate) {
                    continue;
                }
                Share(other.body.charType, body.charType, out _, out float take);
                float taken = Mathf.Abs(vy) * take;
                Slam(body, Mathf.Min(taken, MaxForce));
                slammed.Add(id);
                break;
            }
        }

        static void DropSlam(Body body) {
            if (slammed.Count == 0) {
                return;
            }
            drop.Clear();
            foreach (ushort id in slammed) {
                if (!ScavPlayer.TryGetPlayerFromClientId(id, out ScavPlayer plr) || plr.body == null || plr.body.grounded || !Overlaps(body, plr.body)) {
                    drop.Add(id);
                }
            }
            for (int i = 0; i < drop.Count; i++) {
                slammed.Remove(drop[i]);
            }
        }

        static void DropLatch(Body body) {
            if (latched.Count == 0) {
                return;
            }
            drop.Clear();
            foreach (ushort id in latched) {
                if (body.grounded || !ScavPlayer.TryGetPlayerFromClientId(id, out ScavPlayer plr) || plr.body == null || !Overlaps(body, plr.body)) {
                    drop.Add(id);
                }
            }
            for (int i = 0; i < drop.Count; i++) {
                latched.Remove(drop[i]);
            }
        }

        static void AcceptHit(ScavPlayer faller, ushort catcherId, float force) {
            if (!Net.IsServer || faller.body == null || !Util.IsFinite(force) || force <= 0f || force > MaxForce) {
                return;
            }
            if (faller.playerId.id == catcherId) {
                return;
            }
            if (!armedUntil.TryGetValue(catcherId, out float until) || Time.time > until + 0.2f) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(catcherId, out ScavPlayer catcher) || catcher.body == null) {
                return;
            }
            if (Vector2.Distance(faller.body.transform.position, catcher.body.transform.position) > HitReach) {
                return;
            }
            ApplyCatchImpact(catcher.body, force);
            if (catcher.netBody != null) {
                MedicalSync.Server_QueueSendCharacterHealth(catcher.netBody, true);
            }
            if (catcher.IsLocal) {
                return;
            }
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("fall_hit");
            writer.Put(force);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, catcher.playerId);
        }

        static void HitLocal(float force) {
            if (Time.time < nextLocalHit) {
                return;
            }
            if (!Util.TryGetLocalBody(out Body body) || body == null || !body.alive || !Util.IsFinite(force)) {
                return;
            }
            nextLocalHit = Time.time + 0.4f;
            // the server already applied this hit. Force skips the report so it is not applied twice.
            ApplyCatchImpact(body, force);
        }

        static void ApplyCatchImpact(Body body, float force) {
            if (body == null || !body.alive || !Util.IsFinite(force) || force <= 0f) {
                return;
            }
            force = Mathf.Min(force, MaxForce);
            Slam(body, force);
            if (force <= body.jumpSpeed) {
                return;
            }
            Limb limb = body.LimbByName("DownTorso");
            if (limb == null) {
                return;
            }
            Limb_ImpactDamage_MultiplayerPatch.Force(limb, force, Vector2.down);
        }

        static void Slam(Body body, float force) {
            Vector2 last = body.lastTimeStepVelocity;
            last.y = -force;
            body.lastTimeStepVelocity = last;
            if (body.rb != null) {
                Vector2 v = body.rb.velocity;
                v.y = -force;
                body.rb.velocity = v;
            }
            if (body.standing) {
                body.Ragdoll();
                return;
            }
            if (body.limbs == null) {
                return;
            }
            for (int i = 0; i < body.limbs.Length; i++) {
                Limb limb = body.limbs[i];
                if (limb == null || limb.rb == null) {
                    continue;
                }
                Vector2 lv = limb.rb.velocity;
                lv.y = -force;
                limb.rb.velocity = lv;
            }
        }

        static void Share(int fallerType, int catcherType, out float keep, out float take) {
            int faller = Mathf.Clamp(fallerType, 0, 2);
            int catcher = Mathf.Clamp(catcherType, 0, 2);
            int index = catcher * 3 + faller;
            keep = FallerKeep[index];
            take = CatcherTake[index];
        }

        static void SendHit(ushort catcherId, float force) {
            if (Net.IsServer) {
                if (!NetBody.TryGetLocalNetBody(out NetBody me) || me.plr == null) {
                    return;
                }
                AcceptHit(me.plr, catcherId, force);
                return;
            }
            SendCommand("fall_hit " + catcherId.ToString(CultureInfo.InvariantCulture) + " " + force.ToString("R", CultureInfo.InvariantCulture));
        }

        static void SendCommand(string command) {
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put(command);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        static void BroadcastArm(ushort id, bool on) {
            if (!Net.IsServer) {
                return;
            }
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("fall_arm");
            writer.Put(id);
            writer.Put((byte)(on ? 1 : 0));
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
        }

        internal static void Forget(ushort player) {
            armedUntil.Remove(player);
        }

        static void NoteArmed(ushort id, bool on) {
            if (on) {
                armedUntil[id] = Time.time + ArmLifetime;
            } else {
                armedUntil.Remove(id);
            }
        }

        static float Vertical(Body body) {
            if (body.rb != null) {
                return body.rb.velocity.y;
            }
            return body.lastTimeStepVelocity.y;
        }

        static void ScaleVertical(Body body, float keep) {
            if (body.rb != null) {
                Vector2 v = body.rb.velocity;
                v.y *= keep;
                body.rb.velocity = v;
            }
            Vector2 last = body.lastTimeStepVelocity;
            last.y *= keep;
            body.lastTimeStepVelocity = last;
            if (body.limbs == null) {
                return;
            }
            for (int i = 0; i < body.limbs.Length; i++) {
                Limb limb = body.limbs[i];
                if (limb == null || limb.rb == null) {
                    continue;
                }
                Vector2 lv = limb.rb.velocity;
                lv.y *= keep;
                limb.rb.velocity = lv;
            }
        }

        static bool Overlaps(Body a, Body b) {
            return Touching(a, b, out _);
        }

        static bool SolidContact(Body a, Body b) {
            return Touching(a, b, out bool solid) && solid;
        }

        static bool Touching(Body a, Body b, out bool solid) {
            solid = false;
            Collider2D[] ac = a.GetComponentsInChildren<Collider2D>();
            Collider2D[] bc = b.GetComponentsInChildren<Collider2D>();
            bool hit = false;
            for (int i = 0; i < ac.Length; i++) {
                Collider2D ca = ac[i];
                if (ca == null || !ca.enabled || ca.isTrigger) {
                    continue;
                }
                for (int j = 0; j < bc.Length; j++) {
                    Collider2D cb = bc[j];
                    if (cb == null || !cb.enabled || cb.isTrigger) {
                        continue;
                    }
                    if (!Physics2D.Distance(ca, cb).isOverlapped) {
                        continue;
                    }
                    hit = true;
                    if (!Physics2D.GetIgnoreCollision(ca, cb)) {
                        solid = true;
                        return true;
                    }
                }
            }
            return hit;
        }
    }
}