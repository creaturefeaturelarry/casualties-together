using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Together {

    // while a player is ragdolled, each limb is a falling crate. physics does the shove.
    // the crate script is what deals the damage. your own limbs are not a crate to you,
    // and holding carry makes you immune so a catch is not a smash.
    sealed class PlayerFallCrate : MonoBehaviour {
        const int FallingCrate = 1;
        const float Again = 0.35f;
        // stock walk caps at Body.maxSpeed. a step past that is a real fall or a shove, not a crawl.
        const float DefaultWalk = 25f;
        const float AboveWalk = 2f;
        const float Hold = 1f;
        const float HitSpeed = 6f;

        static bool hadAny;
        static readonly Dictionary<int, float> nextHit = new Dictionary<int, float>();
        static readonly Dictionary<int, float> holdUntil = new Dictionary<int, float>();
        static readonly Dictionary<int, float> hurtAt = new Dictionary<int, float>();
        static readonly List<Body> players = new List<Body>();
        static readonly List<Body> armed = new List<Body>();

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict["player_crate"] = (plr, cmd, args) => {
                if (!Net.IsServer || plr == null || args == null || args.Length < 3) {
                    return;
                }
                if (!Multiplayer.rules.RagdollImpact) {
                    return;
                }
                if (!byte.TryParse(args[1], out byte limbId)) {
                    return;
                }
                if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed)) {
                    return;
                }
                int species = 0;
                if (args.Length >= 4) {
                    int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out species);
                }
                if (!MedicalSync.TryGetPlayerAndLimb(plr.playerId, limbId, out NetBody nb, out Limb limb) || nb == null || limb == null) {
                    return;
                }
                if (nb.plr != null && FallCatch.IsArmed(nb.plr.playerId.id)) {
                    return;
                }
                Hurt(limb, speed, species);
            };
        }

        public static void Tick() {
            if (!Net.IsRunning || !Util.IsWorldGenerated()) {
                return;
            }
            bool rule = Multiplayer.rules.RagdollImpact;
            bool any = false;
            armed.Clear();
            List<NetBody> bodies = NetBody.all_instances;
            for (int i = 0; i < bodies.Count; i++) {
                NetBody nb = bodies[i];
                if (!IsPlayer(nb)) {
                    continue;
                }
                // a carried body is ragdolled on purpose. crates there ram whoever the carrier runs into.
                bool eligible = rule && nb.body.alive && !nb.body.standing && !Carried(nb);
                if (!eligible) {
                    holdUntil.Remove(nb.body.GetInstanceID());
                }
                bool want = eligible && CollidersLive(nb.body);
                if (want) {
                    any = true;
                    armed.Add(nb.body);
                }
                SetCrates(nb.body, want);
            }
            if (any || hadAny) {
                SetContacts();
            }
            hadAny = any;
        }

        static bool IsPlayer(NetBody nb) {
            return nb != null && nb.IsPlayer && nb.body != null && nb.body.limbs != null;
        }

        static void SetCrates(Body body, bool want) {
            Limb[] limbs = body.limbs;
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                if (limb == null) {
                    continue;
                }
                bool on = want && !limb.dismembered && limb.gameObject.activeInHierarchy;
                PlayerFallCrate mark = limb.GetComponent<PlayerFallCrate>();
                if (on) {
                    if (mark == null) {
                        limb.gameObject.AddComponent<PlayerFallCrate>();
                    }
                    DamagingCrate crate = limb.GetComponent<DamagingCrate>();
                    if (crate == null) {
                        crate = limb.gameObject.AddComponent<DamagingCrate>();
                    }
                    crate.type = FallingCrate;
                } else if (mark != null) {
                    DamagingCrate crate = limb.GetComponent<DamagingCrate>();
                    if (crate != null) {
                        Object.Destroy(crate);
                    }
                    Object.Destroy(mark);
                }
            }
        }

        static void SetContacts() {
            players.Clear();
            List<NetBody> bodies = NetBody.all_instances;
            for (int i = 0; i < bodies.Count; i++) {
                if (IsPlayer(bodies[i])) {
                    players.Add(bodies[i].body);
                }
            }
            for (int i = 0; i < players.Count; i++) {
                for (int j = i + 1; j < players.Count; j++) {
                    bool touch = armed.Contains(players[i]) || armed.Contains(players[j]);
                    IgnoreBetween(players[i], players[j], !touch);
                }
            }
        }

        internal static bool RecentlyHurt(Body body) {
            if (body == null) {
                return false;
            }
            return hurtAt.TryGetValue(body.GetInstanceID(), out float at) && Time.time - at < 2f;
        }

        static void KnockDown(Body body) {
            if (body == null) {
                return;
            }
            body.Ragdoll();
            if (body.shock < 40f) {
                body.shock = 40f;
            }
        }

        static bool Carried(NetBody nb) {
            return nb != null && nb.piggybacking_on != null;
        }

        static float WalkGate(Body attacker) {
            float walk = attacker != null && attacker.maxSpeed > 1f ? attacker.maxSpeed : DefaultWalk;
            return (walk + AboveWalk) * 2f;
        }

        // above the floor, colliders are on. after they slow down, the colliders stay for a second
        // so the contact itself does not turn the hit off.
        static bool CollidersLive(Body body) {
            int id = body.GetInstanceID();
            if (Travel(body) > WalkGate(body)) {
                holdUntil[id] = Time.time + Hold;
                return true;
            }
            if (holdUntil.TryGetValue(id, out float until) && Time.time < until) {
                return true;
            }
            holdUntil.Remove(id);
            return false;
        }

        static float Travel(Body body) {
            if (body == null) {
                return 0f;
            }
            Limb torso = body.GetUpperTorso();
            if (torso != null && torso.rb != null && !torso.dismembered) {
                return torso.rb.velocity.magnitude;
            }
            if (body.rb != null) {
                return body.rb.velocity.magnitude;
            }
            return body.lastTimeStepVelocity.magnitude;
        }

        // expie is the crate as written. milky is lighter, dune is heavier.
        static float SpeciesScale(int charType) {
            if (charType == 1) {
                return 0.7f;
            }
            if (charType == 2) {
                return 1.2f;
            }
            return 1f;
        }

        static void IgnoreBetween(Body a, Body b, bool ignore) {
            Collider2D[] ac = a.GetComponentsInChildren<Collider2D>(true);
            Collider2D[] bc = b.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < ac.Length; i++) {
                Collider2D ca = ac[i];
                if (ca == null || ca.isTrigger) {
                    continue;
                }
                for (int j = 0; j < bc.Length; j++) {
                    Collider2D cb = bc[j];
                    if (cb == null || cb.isTrigger || cb == ca) {
                        continue;
                    }
                    if (Physics2D.GetIgnoreCollision(ca, cb) != ignore) {
                        Physics2D.IgnoreCollision(ca, cb, ignore);
                    }
                }
            }
        }

        static void Hit(DamagingCrate crate, Collision2D collision) {
            if (crate == null || collision == null || collision.contactCount < 1) {
                return;
            }
            Limb self = crate.GetComponent<Limb>();
            if (self == null || self.body == null) {
                return;
            }
            Limb hit = Body.LimbFromObject(collision.gameObject, collision.GetContact(0).point);
            if (hit == null || hit.body == null || hit.body == self.body) {
                return;
            }
            if (!Multiplayer.rules.RagdollImpact) {
                return;
            }
            float speed = collision.relativeVelocity.magnitude;
            if (speed <= HitSpeed) {
                return;
            }
            if (self.body.TryGetNetBody(out NetBody attacker) && Carried(attacker)) {
                return;
            }
            if (hit.body.TryGetNetBody(out NetBody victim) && victim.plr != null && FallCatch.IsArmed(victim.plr.playerId.id)) {
                return;
            }
            if (Net.IsServer) {
                Hurt(hit, speed, self.body.charType);
                return;
            }
            if (!Util.IsBodyLocal(hit.body)) {
                return;
            }
            // the host ragdolls its copy. this machine is the one on screen, and it never applied that.
            KnockDown(hit.body);
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("player_crate " + Util.GetLimbIndex(hit).ToString(CultureInfo.InvariantCulture) + " " + speed.ToString("R", CultureInfo.InvariantCulture) + " " + self.body.charType.ToString(CultureInfo.InvariantCulture));
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        static void Hurt(Limb limb, float speed, int attackerSpecies) {
            if (limb == null || limb.body == null || !Util.IsFinite(speed) || speed <= HitSpeed || speed > 250f) {
                return;
            }
            int id = limb.GetInstanceID();
            if (nextHit.TryGetValue(id, out float until) && Time.time < until) {
                return;
            }
            nextHit[id] = Time.time + Again;
            float armor = limb.GetArmorReduction();
            if (armor < 0.05f) {
                armor = 0.05f;
            }
            float scale = SpeciesScale(attackerSpecies) * 0.5f;
            if (limb.isHead) {
                float target = 20f / armor;
                float drop = (limb.body.consciousness - target) * scale;
                if (drop > 0f) {
                    limb.body.consciousness -= drop;
                }
            }
            limb.muscleHealth -= speed * 0.75f / armor * scale;
            limb.skinHealth -= speed / armor * scale;
            limb.bleedAmount += speed * 0.1f / armor * scale;
            limb.DamageWearables(0.1f * scale);
            Sound.Play("stalactitehit", limb.transform.position, twoDimensional: limb.body.IsBodyLocal());
            // half damage no longer drops consciousness into a knockout, so the client
            // keeps reporting standing and stands them back up. the hit itself is the ragdoll.
            KnockDown(limb.body);
            hurtAt[limb.body.GetInstanceID()] = Time.time;
            if (limb.TryGetNetBody(out NetBody nb)) {
                nb.SetNetIgnoreTime(0.75f);
                if (nb.syncBody != null) {
                    nb.syncBody.WriteBodyData();
                }
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }

        [HarmonyPatch(typeof(DamagingCrate), nameof(DamagingCrate.OnCollisionEnter2D))]
        static class Gate {
            static bool Prefix(DamagingCrate __instance, Collision2D collision) {
                if (__instance == null || __instance.GetComponent<PlayerFallCrate>() == null) {
                    return true;
                }
                Hit(__instance, collision);
                return false;
            }
        }
    }
}
