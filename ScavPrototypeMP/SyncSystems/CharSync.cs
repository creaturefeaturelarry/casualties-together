using Together;
using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;


namespace Together;




public partial struct ClientToServer_NetBodySyncPacket : INetSerializeByMemcpy {
    public ushort Tick;


    public bool crouching;

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

        {
            if (this.standing) {
                is_piggyback = false;
                body.shock = 0; // ensure that he will not get ragdolled later imidietly
            } else {
                body.shock = 100;
            }
            if (body.standing != this.standing) {
                if (this.standing) {
                    body.ForceStand(); // force Stand
                } else {
                    body.Ragdoll(); //ragdoll is guranteed to work
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
                    // on server, account for possibly missed unpiggyback packet
                    if (!is_piggyback && !KM.DistanceCheck(this.pos, nb.piggybacking_on.pos, 5)) {
                        if (log.verbose) log.warn($"{nb} is piggybacking but pos is too far, missed unpiggyback request?");// It does happen
                        nb.Server_RemindPlayersCurrentState( false);
                    }
                }
            }
            WritePiggyback(nb);
        }

        if (nb.piggybacking_on == null) {
            nb.SetBodyPosition(this.pos);

            if (body.IsBodyLocal() && body.alive) {
                ConsoleScript.instance.noClipPos = this.pos;
                //ConsoleScript.instance.freecamPos = pack.pos;
                if (Util.IsGeneratingWorld() && !Con._DEV_FREECAM) {
                    PlayerCamera.main.transform.position = body.transform.position;
                }
            }
        }

        body.targetLookPos = this.targetLookPos;
        body.crouching = this.crouching;

        nb.moveDir = compressedmoveDir;
        if (body.conscious) {
            body.moveDir = moveDir;
        } 

        Vector2 targetvel = (Vector2)this.velocity;
        
        if (body.standing) {
            body.rb.velocity = targetvel;
        } else {
            body.SetVelocity(targetvel);
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


        if (Net.IsServer) {
            nb.syncBody.WriteBodyData();
        }

    }

}













