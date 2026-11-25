using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using Together;
using Unity.VisualScripting;
using UnityEngine;


namespace Together;




public partial struct ClientToServer_NetBodySyncPacket : INetSerializeByMemcpy {
    public ushort Tick;


    public bool crouching;
    public bool offHand;

    public bool standing;
    public bool is_piggyback;
    public knetid piggyback;

    public Vector2 pos;
    public Vector2 targetLookPos;

    public SByteScalarVec2 compressedmoveDir;
    public Vector2 moveDir { get { return compressedmoveDir; } set { compressedmoveDir = (SByteScalarVec2)value; } }

    public Vector2_4byte_200 velocity;


    public void WritePiggyback(NetBody b) {
        is_piggyback = b.piggybacking_on != null;
        if (is_piggyback) {
            piggyback = b.piggybacking_on.netId;
        }
    }
    public bool IncludeRagData() {
        return !this.standing && !this.is_piggyback;
    }

    public ClientToServer_NetBodySyncPacket(NetBody pb) {

        WritePiggyback(pb);

        var body = pb.body;
        standing = body.standing && !is_piggyback;

        pos = pb.GetPosition();
        targetLookPos = body.targetLookPos;

        if (body.conscious) {
            moveDir = body.moveDir;
        } else {
            moveDir = Vector2.zero;
        }
        crouching = body.crouching;
        offHand = body.handSlot != 0;

        velocity = body.GetVelocity();

        if (body.IsBodyLocal()) {
            if (Util.IsInWoundView() && !WoundView.view.body.IsBodyLocal()) {
                targetLookPos = WoundView.view.body.transform.position;
            }
        }

        Tick = Net.ServerTick;
    }



    public void Apply(NetBody nb) {
        var body = nb.body;

        bool alreadyCarried = nb.piggybacking_on != null;
        // the crate hit already put them down. this packet was built while they were still standing.
        bool keepCrateRagdoll = this.standing && PlayerFallCrate.RecentlyHurt(body);
        {
            // a packet still in flight from the ground must not stand them up or clear the carry
            if (!alreadyCarried && !keepCrateRagdoll) {
                if (this.standing) {
                    is_piggyback = false;
                    body.shock = 0; // ensure that he will not get ragdolled later imidietly
                } else {
                    body.shock = 100;
                }
                if (body.standing != this.standing) {
                    RagdollProbe.Event(body, this.standing ? "sync-stand" : "sync-down",
                        " packVel=" + ((Vector2)this.velocity).x.ToString("0.00") + "," + ((Vector2)this.velocity).y.ToString("0.00"));
                    if (this.standing) {
                        body.ForceStand(); // force Stand
                    } else {
                        body.Ragdoll(); //ragdoll is guranteed to work
                    }
                }
            }
        }


        if (Net.IsClient) {
            if (is_piggyback) {
                if (NetBody.TryGetNetBodyFromId(piggyback, out var target_nb) && target_nb != nb) {
                    nb.StartPiggyback(target_nb, false, true);
                }
            } else if (nb.piggybacking_on != null && Util.IsWorldGenerated()) {
#if DEBUG
                if (log.verbose) log.l($"Stopping piggybacking");
#endif
                nb.StopPiggyback();
            }
        } else {
            if ((nb.piggybacking_on != null) != is_piggyback) {
                if (nb.piggybacking_on != null) {
                    // the carried client keeps reporting the ground spot until the pickup arrives.
                    // interact range is 9 and the server accepts up to 1.75x that, so 5 was inside a normal pickup.
                    bool fresh = Time.unscaledTime - nb.piggybackStartedUnscaled < 2f;
                    if (!is_piggyback && !fresh && !KM.DistanceCheck(this.pos, nb.piggybacking_on.pos, 18f)) {
                        if (log.verbose) log.warn($"{nb} is piggybacking but pos is too far, missed unpiggyback request?");// it does happen
                        nb.StopPiggyback();
                    }
                }
            }
            WritePiggyback(nb);
        }

        if (nb.piggybacking_on == null && !keepCrateRagdoll) {
            nb.SetBodyPosition(this.pos);

            if (body.IsBodyLocal() && body.alive) {
                ConsoleScript.instance.noClipPos = this.pos;
                //consoleScript.instance.freecamPos = pack.pos;
                if (Util.IsGeneratingWorld() && !Con._DEV_FREECAM) {
                    PlayerCamera.main.transform.position = body.transform.position;
                }
            }
        }

        body.targetLookPos = this.targetLookPos;
        body.crouching = this.crouching;
        // same tick or older than the hand toggle. that packet was built on the main hand.
        if (!nb.handSwitched || LiteEntitySystem.Utils.SequenceDiff(Tick, nb.handSwitchTick) > 0)
            body.handSlot = offHand ? 1 : 0;

        nb.moveDir = compressedmoveDir;
        if (body.conscious) {
            body.moveDir = moveDir;
        }

        Vector2 targetvel = (Vector2)this.velocity;

        // setVelocity writes every limb. doing that while carried cancels the ragdoll flop and yanks them off the chest.
        // a standing packet's velocity is ~0 and would cancel the fall that just ragdolled them.
        if (nb.piggybacking_on == null && !keepCrateRagdoll) {
            if (body.standing) {
                body.rb.velocity = targetvel;
            } else {
                body.SetVelocity(targetvel);
            }
        }

        // fix the randomass jitter when on a rope
        if (body.currentClimbable != null && !body.grounded) {
            var gi = body.currentClimbable.GetGrabInfo(body.transform.position);
            if (gi.distanceToPlayer < 1) {
                var gggggggggggggggggggggg = targetvel.y * 0.1f;
                var neeeeeeeeeeeee = body.climbableProgress + gggggggggggggggggggggg;
                // idk why im doing this, i thought i could maybe make the rope jitter less noticable but idk if everything here is necessary 
                if (neeeeeeeeeeeee >= body.currentClimbable.totalLength) {
                    body.climbableProgress = body.currentClimbable.totalLength - 0.0001f;
                    body.climbVelocity = 0f;
                } else if (neeeeeeeeeeeee <= 0f) {
                    body.climbVelocity = 0f;
                } else {
                    body.climbVelocity = targetvel.y;
                    body.climbableProgress = Mathf.Clamp01(gi.pathDistance);
                }
            } else if (gi.distanceToPlayer > 2) {
                body.StopClimbing();
            }
        }

        if (!body.IsBodyLocal() && body.sleeping && body.moveDir != Vector2.zero) { // if theyre sleeping and moving, they probably woke up
            body.sleeping = false;
        }


        RagdollProbe.Pack(body, this.standing, (Vector2)this.velocity, nb.piggybacking_on != null);

        if (Net.IsServer) {
            nb.syncBody.WriteBodyData();
        }

    }

}

// remote player on the ground. stock FixedUpdate spins the head and crawls with a 6000 shove.
// that pose is what a viewer pastes, so the limbs fly. the get-up check still runs.
[HarmonyPatch(typeof(Body), "FixedUpdate")]
public static class Body_FixedUpdate_RemoteRagdoll {
    static bool Prefix(Body __instance) {
        RagdollProbe.Tick(__instance);
        if (!Net.IsRunning || __instance.standing || !__instance.conscious || __instance.IsBodyLocal()) {
            return true;
        }
        if (!__instance.TryGetNetBody(out NetBody nb) || !nb.IsPlayer) {
            return true;
        }
        if (__instance.rb != null) {
            __instance.rb.gravityScale = 1f;
            __instance.lastTimeStepVelocity = __instance.rb.velocity;
            if ((__instance.grounded || __instance.bodyAffect.wasWater) && __instance.shock < 10f && __instance.rb.velocity.magnitude < __instance.maxSpeed * 0.1f) {
                RagdollProbe.Event(__instance, "getup",
                    " grounded=" + (__instance.grounded ? 1 : 0)
                    + " water=" + (__instance.bodyAffect.wasWater ? 1 : 0)
                    + " vel=" + __instance.rb.velocity.magnitude.ToString("0.00"));
                __instance.Stand();
            }
        }
        return false;
    }
}