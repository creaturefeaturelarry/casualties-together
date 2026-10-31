using BepInEx;
using CasualtiesTogetherUtils;
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;
using static GunScript;
using KrokV5Optimization;

namespace Together;



public class LiteEntitySystemLogger : LiteEntitySystem.ILogger {
    //public const string name = "LiteEntitySystem";
    public const string name = "LES";
    public const string prefix1 = (name + ": ");
    public BepInEx.Logging.ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(name);

    public void Log(string log) {
        if (Together.log.verbose) {
            Logger.LogInfo(log);

            if (Together.log.con) {
                Together.log.con.LogToConsole(prefix1 + log);
            }
        }
    }

    public void LogError(string log) {
        Logger.LogError(log);

        if (Together.log.con) {
            Together.log.con.LogToConsole($"<color=red>{name} ERROR: {log}</color>");
        }
    }

    public void LogWarning(string log) {
        if (Together.log.verbose) {
            Logger.LogWarning(log);

            if (Together.log.con) {
                Together.log.con.LogToConsole($"<color=yellow>{name} WARN: {log}</color>");
            }
        }
    }
}




public sealed class ScavEntityTypesMap : EntityTypesMap {
    public ScavEntityTypesMap Register<TEntity>(ushort id, EntityConstructor<TEntity> constructor) where TEntity : InternalEntity {
        if (id == 0) {
            throw new ArgumentNullException("id");
        }
        EntityClassInfo<TEntity>.ClassId = id;
        RegisteredTypes.Add(typeof(TEntity), new RegisteredTypeInfo(id, id.ToString(), constructor));
        MaxId = Math.Max(MaxId, id);
        return this;
    }

    public static EntityTypesMap CreateTypesMap() {
        ushort entityId = 2;
        return new ScavEntityTypesMap()
            .Register(entityId++, e => new BasePlayer(e))
            .Register(entityId++, e => new BasePlayerController(e))
            .Register(entityId++, e => new SyncBody(e))
            .Register(entityId++, e => new SyncInfo(e))
            .Register(entityId++, e => new ServerWorldStateSingleton(e))
            ;
    }



    [ServerReceiver((byte)NetmsgId.LiteEntitySystem)]
    static void ServerReceiver__LiteEntitySystem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        var plr = Net.ServerEntityManager.GetPlayer((byte)clientId);
        if (plr != null) {
            reader.SkipBytes(-sizeof(byte));
            var bytes = reader.GetRemainingBytes();
            var result = Net.ServerEntityManager.Deserialize(plr, bytes) ;
        }
    }

    [ClientReceiver((byte)NetmsgId.LiteEntitySystem)]
    static void ClientReceiver__LiteEntitySystem(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.SkipBytes(-sizeof(byte));
        var bytes = reader.GetRemainingBytes();
        var result = Net.ClientEntityManager.Deserialize(bytes);
    }



}

public struct BodyKnockbackPacket : INetSerializeByMemcpy {
    public Vector2 Velocity;
    public bool CloseUI;
}

public struct BodyTeleportPacket : INetSerializeByMemcpy {
    public Vector2 Position;
    public Vector2 Velocity;
    public float RagdollTime;
    public bool IsRight;
    public bool IsPiggyback;
    public knetid Piggyback;

    public BodyTeleportPacket(NetBody nb) {
        Position = nb.GetPosition();
        Velocity = nb.body.GetVelocity();
        RagdollTime = nb.body.shock;
        IsRight = nb.body.isRight;
        IsPiggyback = nb.IsPiggybacking();
        if (IsPiggyback) {
            Piggyback = nb.piggybacking_on.netId;
        }
    }
}

//public struct BodyInputPacket {
//    public Vector2 Move;
//    public Vector2 LookPos;
//}

[EntityFlags(EntityFlags.UpdateOnClient)]
public class SyncBody : PredictableEntityLogic {
    public NetBody nb;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated | SyncFlags.AlwaysRollback)]
    internal SyncVar<Vector2> _position;
    private SyncVar<Vector2_4byte_200> _velocity;

    private SyncVar<SByteScalarVec2> _moveDir;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated)]
    private SyncVar<Vector2> _targetLookPos;
    private SyncVar<bool> _isRight;
    private SyncVar<bool> _crouching;

    internal SyncVar<bool  > _standing;
    internal SyncVar<bool  > _isPiggyback;
    private SyncVar<knetid> _piggyback;

    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<int> drng_attackCounter; // Melee, Guns
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<int> drng_useCounter; // UseItem
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<int> drng_damageCounter; // Limb Impacts and Spider bites

    public SyncVar<bool> Unchipped;

    #region RagdollThing

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb0_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb0_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb0_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb0_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb1_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb1_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb1_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb1_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb2_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb2_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb2_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb2_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb3_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb3_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb3_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb3_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb4_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb4_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb4_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb4_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb5_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb5_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb5_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb5_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb6_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb6_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb6_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb6_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb7_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb7_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb7_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb7_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb8_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb8_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb8_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb8_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb9_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb9_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb9_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb9_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb10_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb10_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb10_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb10_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb11_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb11_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb11_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb11_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb12_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb12_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb12_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb12_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb13_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb13_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb13_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb13_angvel;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_512> _ragdoll_limb14_pos;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<FloatAngle> _ragdoll_limb14_rot;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<Vector2_4byte_200> _ragdoll_limb14_vel;
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
    private SyncVar<short> _ragdoll_limb14_angvel;

    static float _RAGDOLL_DIST_MARGIN = 0.05f * 0.05f;
    static float _RAGDOLL_ANGVEL_QUANTIZE = 100;
    internal void WriteRagdollData() {
        var limbs = nb.body.limbs;
        Limb limb;



        limb = nb.body.limbs[0];
        _ragdoll_limb0_pos.Value = limb.GetPosition();
        _ragdoll_limb0_rot.Value = limb.rb.rotation;
        _ragdoll_limb0_vel.Value = limb.rb.velocity;
        _ragdoll_limb0_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[1];
        _ragdoll_limb1_pos.Value = limb.GetPosition();
        _ragdoll_limb1_rot.Value = limb.rb.rotation;
        _ragdoll_limb1_vel.Value = limb.rb.velocity;
        _ragdoll_limb1_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[2];
        _ragdoll_limb2_pos.Value = limb.GetPosition();
        _ragdoll_limb2_rot.Value = limb.rb.rotation;
        _ragdoll_limb2_vel.Value = limb.rb.velocity;
        _ragdoll_limb2_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[3];
        _ragdoll_limb3_pos.Value = limb.GetPosition();
        _ragdoll_limb3_rot.Value = limb.rb.rotation;
        _ragdoll_limb3_vel.Value = limb.rb.velocity;
        _ragdoll_limb3_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[4];
        _ragdoll_limb4_pos.Value = limb.GetPosition();
        _ragdoll_limb4_rot.Value = limb.rb.rotation;
        _ragdoll_limb4_vel.Value = limb.rb.velocity;
        _ragdoll_limb4_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[5];
        _ragdoll_limb5_pos.Value = limb.GetPosition();
        _ragdoll_limb5_rot.Value = limb.rb.rotation;
        _ragdoll_limb5_vel.Value = limb.rb.velocity;
        _ragdoll_limb5_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[6];
        _ragdoll_limb6_pos.Value = limb.GetPosition();
        _ragdoll_limb6_rot.Value = limb.rb.rotation;
        _ragdoll_limb6_vel.Value = limb.rb.velocity;
        _ragdoll_limb6_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[7];
        _ragdoll_limb7_pos.Value = limb.GetPosition();
        _ragdoll_limb7_rot.Value = limb.rb.rotation;
        _ragdoll_limb7_vel.Value = limb.rb.velocity;
        _ragdoll_limb7_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[8];
        _ragdoll_limb8_pos.Value = limb.GetPosition();
        _ragdoll_limb8_rot.Value = limb.rb.rotation;
        _ragdoll_limb8_vel.Value = limb.rb.velocity;
        _ragdoll_limb8_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[9];
        _ragdoll_limb9_pos.Value = limb.GetPosition();
        _ragdoll_limb9_rot.Value = limb.rb.rotation;
        _ragdoll_limb9_vel.Value = limb.rb.velocity;
        _ragdoll_limb9_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[10];
        _ragdoll_limb10_pos.Value = limb.GetPosition();
        _ragdoll_limb10_rot.Value = limb.rb.rotation;
        _ragdoll_limb10_vel.Value = limb.rb.velocity;
        _ragdoll_limb10_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[11];
        _ragdoll_limb11_pos.Value = limb.GetPosition();
        _ragdoll_limb11_rot.Value = limb.rb.rotation;
        _ragdoll_limb11_vel.Value = limb.rb.velocity;
        _ragdoll_limb11_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[12];
        _ragdoll_limb12_pos.Value = limb.GetPosition();
        _ragdoll_limb12_rot.Value = limb.rb.rotation;
        _ragdoll_limb12_vel.Value = limb.rb.velocity;
        _ragdoll_limb12_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[13];
        _ragdoll_limb13_pos.Value = limb.GetPosition();
        _ragdoll_limb13_rot.Value = limb.rb.rotation;
        _ragdoll_limb13_vel.Value = limb.rb.velocity;
        _ragdoll_limb13_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


        limb = nb.body.limbs[14];
        _ragdoll_limb14_pos.Value = limb.GetPosition();
        _ragdoll_limb14_rot.Value = limb.rb.rotation;
        _ragdoll_limb14_vel.Value = limb.rb.velocity;
        _ragdoll_limb14_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);


    }
    bool _RagdollReadIndividualLimb(Limb limb, ref SyncVar<Vector2_4byte_512> syncpos, ref SyncVar<FloatAngle> syncrot, ref SyncVar<Vector2_4byte_200> syncvel, ref SyncVar<short> syncangvel) {
        var limb_rb_position = syncpos.InterpolatedValue;
        if (!limb.dismembered) {
            if (Net.IsServer) {
                if ((Vector2.SqrMagnitude(limb_rb_position - nb.GetPosition()) < 100)) {
                } else {
#if !RELEASE
                    log.sus($"{DebugHelp.GetPlayerFullDebugString(nb.body)} something is sus about {DebugHelp.GetGameObjectFullDebugString(limb)} ");
#endif
                    return false;
                }
            }
            limb.transform.position = limb_rb_position;
            limb.rb.rotation = syncrot.InterpolatedValue;
            limb.rb.velocity = syncvel.InterpolatedValue;
            limb.rb.angularVelocity = Util_MPExtensions.UnquantizeFloatFromShort(syncangvel.InterpolatedValue, _RAGDOLL_ANGVEL_QUANTIZE);
        }
        return true;
    }

    internal void ReadRagdollData() {


        if (!_RagdollReadIndividualLimb(nb.body.limbs[0], ref _ragdoll_limb0_pos, ref _ragdoll_limb0_rot, ref _ragdoll_limb0_vel, ref _ragdoll_limb0_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[1], ref _ragdoll_limb1_pos, ref _ragdoll_limb1_rot, ref _ragdoll_limb1_vel, ref _ragdoll_limb1_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[2], ref _ragdoll_limb2_pos, ref _ragdoll_limb2_rot, ref _ragdoll_limb2_vel, ref _ragdoll_limb2_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[3], ref _ragdoll_limb3_pos, ref _ragdoll_limb3_rot, ref _ragdoll_limb3_vel, ref _ragdoll_limb3_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[4], ref _ragdoll_limb4_pos, ref _ragdoll_limb4_rot, ref _ragdoll_limb4_vel, ref _ragdoll_limb4_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[5], ref _ragdoll_limb5_pos, ref _ragdoll_limb5_rot, ref _ragdoll_limb5_vel, ref _ragdoll_limb5_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[6], ref _ragdoll_limb6_pos, ref _ragdoll_limb6_rot, ref _ragdoll_limb6_vel, ref _ragdoll_limb6_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[7], ref _ragdoll_limb7_pos, ref _ragdoll_limb7_rot, ref _ragdoll_limb7_vel, ref _ragdoll_limb7_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[8], ref _ragdoll_limb8_pos, ref _ragdoll_limb8_rot, ref _ragdoll_limb8_vel, ref _ragdoll_limb8_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[9], ref _ragdoll_limb9_pos, ref _ragdoll_limb9_rot, ref _ragdoll_limb9_vel, ref _ragdoll_limb9_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[10], ref _ragdoll_limb10_pos, ref _ragdoll_limb10_rot, ref _ragdoll_limb10_vel, ref _ragdoll_limb10_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[11], ref _ragdoll_limb11_pos, ref _ragdoll_limb11_rot, ref _ragdoll_limb11_vel, ref _ragdoll_limb11_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[12], ref _ragdoll_limb12_pos, ref _ragdoll_limb12_rot, ref _ragdoll_limb12_vel, ref _ragdoll_limb12_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[13], ref _ragdoll_limb13_pos, ref _ragdoll_limb13_rot, ref _ragdoll_limb13_vel, ref _ragdoll_limb13_angvel)) return;

        if (!_RagdollReadIndividualLimb(nb.body.limbs[14], ref _ragdoll_limb14_pos, ref _ragdoll_limb14_rot, ref _ragdoll_limb14_vel, ref _ragdoll_limb14_angvel)) return;

    }

    #endregion


    //public static RemoteCall<BodyTeleportPacket> _teleportRPC;


    public SyncBody(EntityParams entityParams) : base(entityParams) { _standing.Value = true; }

    protected override void OnConstructed() {
        CreateCharacter();
#if !RELEASE 
        log.l($"CREATED SyncBody: {nb} owner:{OwnerId}");
#endif 
    }

    private bool CreateCharacter() {
        if (nb == null) {

            if (Util.IsInMainMenu()) {
                return false;
            }
            if (nb == null) {
                var parent = GetParent<BasePlayer>();
                if (parent != null && parent.player != null) {
                    if (parent.player.IsLocal) {
                        ScavPlayer.TryGetLocalNetBody(out nb);
                        var sb = this;
                        NetBody._Internal_RegisterLiteNetLib(nb, parent.player, ref sb);
                    } else {
                        nb = NetBody.CreateNewPlayerCharacter(parent.player, this);
                    }
                } else {
                    nb = NetBody.CreateNewNPC(_position.Value, null, this);
                }
            }

            if (nb != null) {
                nb.syncBody = this;
                return true;
            } else {
                return false;
            }
        }
        return true;
    }

    protected override void RegisterRPC(ref RPCRegistrator r) {
        base.RegisterRPC(ref r);

        r.BindOnChange(this, ref _velocity, (e) => { OnVelocityUpdate(); }, BindOnChangeFlags.ExecuteOnSync);
        r.BindOnChange(this, ref _moveDir, (e) => { OnMoveDirUpdate(); }, BindOnChangeFlags.ExecuteOnSync);
        r.BindOnChange(this, ref Unchipped, (e) => { OnUnchippedChange(); }, BindOnChangeFlags.ExecuteOnSync);
        
    }

    void OnUnchippedChange() {
        if (nb != null) {
            nb.SetUnchipped(Unchipped.Value);
        }
    }

    public void ApplyBodyTeleportPacket(BodyTeleportPacket data) {
        if (CreateCharacter()) {
#if DEBUG
            if (log.verbose) log.devevent($"BodyTeleportPacket SB {nb}", data.Position);
#endif

            _position.Value = (data.Position);
            _velocity.Value = (data.Velocity);
            _isRight.Value = (data.IsRight);
            _isPiggyback.Value = (data.IsPiggyback);
            _piggyback.Value = (data.Piggyback);

            ApplyBodyTeleportPacket(nb, data);
        }
    }
    public static void ApplyBodyTeleportPacket(NetBody nb, BodyTeleportPacket data) {
        log.devevent($"BodyTeleportPacket NB {nb}", data.Position);

        if (data.RagdollTime <= 0) {
            nb.body.ForceStand();
        }

        nb.SetBodyPosition(data.Position);
        nb.body.SetVelocity(data.Velocity);
        nb.body.shock = data.RagdollTime;

        if (nb.body.standing) {
            nb.body.ForceSwitchDir(data.IsRight);
        }

        if (data.IsPiggyback) {
            if (NetBody.TryGetNetBodyFromId(data.Piggyback, out var target_nb)) {
                nb.StartPiggyback(target_nb, false, true);
            }
        } else if (nb.piggybacking_on != null) {
            nb.StopPiggyback();
        }
    }

    private void OnVelocityUpdate() {
        if (Util.IsWorldGenerated() && CreateCharacter()) {
            if (!nb.IsLocal) {
                var newVelocity = Vector2.Lerp(nb.body.GetVelocity(), _velocity.Value, 0.7f);
                if (nb.body.standing) {
                    nb.body.rb.velocity = newVelocity;
                } else {
                    //nb.body.SetVelocity(newVelocity); // Done by ragdoll sync 
                }
            }
        }
    }
    private void OnMoveDirUpdate() {
        //if (CreateCharacter()) {
        //    if (!nb.is_local) {
        //        nb.last_sync_packet.compressedmoveDir = _moveDir.Value;
        //        nb.body.moveDir = (Vector2)_moveDir.Value;
        //        if (Time.realtimeSinceStartupAsDouble > nb._last_sync_packet_receive_time) {
        //            nb._last_sync_packet_receive_time = Time.realtimeSinceStartupAsDouble;
        //        }
        //    }
        //}
    }

    protected override void OnDestroy() {
#if !RELEASE 
        log.l($"OnDestroy SyncBody: {nb?.ToString()} owner:{OwnerId}");
#endif
        NetBody.DestroyNPC(nb);
    }


    protected override void Update() {
        base.Update();

        if (IsServer) {
            if (CreateCharacter() && nb.IsLocal) {
                WriteBodyData();
            }
        }
    }

    protected override void VisualUpdate() {
        if (CreateCharacter()) {
            if (!nb.IsBodyLocal()) {
                ApplyDataToBody();
            }
        }
    }

    internal void WriteBodyData() {
        var p = nb.GetPosition();
        _position.Value = p;
        _velocity.Value = nb.rb.velocity;

        if (nb.IsBodyLocal()) {
            _moveDir.Value = (SByteScalarVec2)nb.moveDir;
        }
        _targetLookPos.Value = nb.body.targetLookPos;
        _isRight.Value = nb.body.isRight;
        _crouching.Value = nb.body.crouching;
        _standing.Value = nb.body.standing;
        // Ragdoll syncgroup is handled in Player.SlowButFastUpdate 

        //if (!_standing.Value) 
        { // Write the ragdoll data
            WriteRagdollData();
        }

        _isPiggyback.Value = nb.IsPiggybacking();
        if (_isPiggyback.Value) {
            _piggyback.Value = nb.piggybacking_on.netId;
        }
    }

    public override string ToString() {
        if (nb != null) {
            return nb.ToString();
        }
        return $"SyncBody(Id: {Id})";
    }
    internal void ApplyDataToBody() {
        var body = nb.body;

        body.targetLookPos = _targetLookPos.InterpolatedValue;
        body.moveDir = (Vector2)_moveDir.Value;
        body.crouching = _crouching.Value;


        if (_isPiggyback.Value) {
            if (NetBody.TryGetNetBodyFromId(_piggyback.Value, out var target_nb)) {
                nb.StartPiggyback(target_nb, false, true);
            }
        } else {
            if (nb.IsPiggybacking()) {
                nb.StopPiggyback();
            }

            if (Net.IsServer || SharedMain.PositionIsInSimulatedChunk(nb.position)) {
                if (nb.body.standing) {
                    Vector2 interp = _position.InterpolatedValue;
                    nb.SetBodyPosition(interp);
                    EnemyMoveProbe.Note(this, EnemyMoveProbe.Interp, interp);
                    body.ForceSwitchDir(_isRight.Value);
                } else {
                    if (!KM.DistanceCheck(body.transform.position, _position.Value, 10)) {
                        Vector2 raw = _position.Value;
                        nb.SetBodyPosition(raw);
                        EnemyMoveProbe.Note(this, EnemyMoveProbe.RagdollSnap, raw);
                        //if (log.verbose) log.devevent($"BODY RAGDOLL POS CORRECTION {this.ToString()}", _position.Value);
                    } else {
                        EnemyMoveProbe.Note(this, EnemyMoveProbe.RagdollHold, body.transform.position);
                    }
                }

                if (body.IsBodyLocal() && body.alive) {
                    ConsoleScript.instance.noClipPos = body.transform.position;
                    //ConsoleScript.instance.freecamPos = pack.pos;
                    if (Util.IsGeneratingWorld() && !Con._DEV_FREECAM) {
                        PlayerCamera.main.transform.position = body.transform.position;
                    }
                }

                {
                    if (_standing.Value) {
                        body.shock = 0; // ensure that he will not get ragdolled later imidietly
                    } else {
                        body.shock = 100;
                    }
                    if (body.standing != _standing.Value) {
                        if (_standing.Value) {
                            body.ForceStand(); // force Stand
                        } else {
                            body.Ragdoll(); // ragdoll is guranteed to work
                        }
                    }
                }

                // fix the randomass jitter when on a rope
                if (body.currentClimbable != null && !body.grounded) {
                    var grabInfo = body.currentClimbable.GetGrabInfo(body.transform.position);
                    if (grabInfo.distanceToPlayer < 1) {
                        var predictedProgress = body.climbableProgress + (_velocity.Value.y * 0.1f);
                        // idk why im doing this, i thought i could maybe make the rope jitter less noticable but idk if everything here is necessary 
                        if (predictedProgress >= body.currentClimbable.totalLength) {
                            body.climbableProgress = body.currentClimbable.totalLength - 0.0001f;
                            body.climbVelocity = 0f;
                        } else if (predictedProgress <= 0f) {
                            body.climbVelocity = 0f;
                        } else {
                            body.climbVelocity = _velocity.Value.y;
                            body.climbableProgress = Mathf.Clamp01(grabInfo.pathDistance);
                        }
                    } else if (grabInfo.distanceToPlayer > 2) {
                        body.StopClimbing();
                    }
                }

            } else {
                Vector2 raw = _position.Value;
                nb.body.ForceStand();
                nb.SetBodyPosition(raw);
                EnemyMoveProbe.Note(this, EnemyMoveProbe.SimOut, raw);
            }

        }

        readingRagdollData = IsSyncGroupEnabled(SyncGroup.SyncGroup3) && !nb.body.standing;
        if (readingRagdollData) {
            ReadRagdollData();
        }


        if (!body.IsBodyLocal() && body.sleeping && body.moveDir != Vector2.zero) { // if theyre sleeping and moving, they probably woke up
            body.sleeping = false;
        }
    }

    internal bool readingRagdollData = false;


}





public struct LiquidStackPart {
    public byte LiquidId;
    public float LiquidMl;

    // craft receipts still travel as whole millilitres. the live stack keeps the float.
    public static ushort Pack(float amount) {
        if (amount <= 0f) {
            return 0;
        }
        if (amount < 1f) {
            return 1;
        }
        int ml = Mathf.RoundToInt(amount);
        if (ml < 1) {
            ml = 1;
        }
        if (ml > ushort.MaxValue) {
            ml = ushort.MaxValue;
        }
        return (ushort)ml;
    }

    public override bool Equals(object obj) {
        var o = (LiquidStackPart)obj;
        return o.LiquidId == LiquidId && o.LiquidMl == LiquidMl;
    }
    public override int GetHashCode() {
        return System.HashCode.Combine(LiquidId, LiquidMl);
    }
}


[EntityFlags(EntityFlags.UpdateOnClient)]
public class SyncInfo : PredictableEntityLogic {
    public SyncInfo(EntityParams entityParams) : base(entityParams) {
        _scale.Value = Vector2.zero;
        condition.Value = 1;
    }



    public double lastPositionUpdateTime = 0;
    // set when a position sample arrives. cleared by the pose tick that sees it.
    bool poseFresh;
    // the loose byte can land while _position is still the pre-release sample.
    bool poseHold;
    bool poseSeen;
    byte posePrevData;

    void NotePositionSync() {
        lastPositionUpdateTime = Time.realtimeSinceStartupAsDouble;
        poseFresh = true;
        poseHold = false;
    }

    // client pose glide. one server state is a single tick, so a slower write
    // used to finish in ~33ms and then hold. the span below is the ticks since
    // the previous sample, divided by the server rate.
    bool _poseGlide;
    ushort _poseTick;
    Vector2 _poseFrom;
    Vector2 _poseTo;
    float _poseFromAng;
    float _poseToAng;
    float _poseStart;
    float _poseDur;


    public readonly SyncString resourceId = new();

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.AlwaysRollback)]
    public SyncVar<Vector2_4byte_512> _position;
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<FloatAngle> _rotation;

    public SyncVar<Vector2_4byte_200> _velocity;

    private readonly SyncList<LiquidStackPart> _liquidStack = new();

    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<byte> container_data1; // is 0 when no container
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<knetid> container_netId;
    // LES reuses the id and bumps Version. this is the Version from the write, so a later occupant is not this container.
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<byte> container_ver;
    public SyncVar<bool> rb_dynamic;
    public SyncVar<bool> itemLock;

    public SyncVar<Vector2> _scale; // default is 1,1

    // RB2DSyncPacket
    private const float angvel_to_short = 1f / 200f * (float)short.MaxValue;
    public SyncVar<short> angvel_quantized; // make it max 200
                                   // both Item and building
    public float angvel {
        get { return (float)angvel_quantized / angvel_to_short; }
        set { angvel_quantized.Value = (short)Mathf.Clamp(value * angvel_to_short, short.MinValue, short.MaxValue); }
    }

    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<float> condition; // aka health for buildings

    // Item stuff
    public SyncVar<byte> compressedboughtitemtime;
    public float boughtitemtime { get { return compressedboughtitemtime.Value / 0.85f; } set { compressedboughtitemtime.Value = (byte)(Mathf.Clamp(value, 0, 300f) * 0.85f); } }  // cuz its 0 to 300 

    public SyncVar<byte> compressedwetTime;
    public float wetTime { get { return compressedwetTime.Value / 255f * 125f; } set { compressedwetTime.Value = (byte)(Mathf.Clamp(value, 0, 125f) / 125f * 255f); } }  // cuz its 0 to 120 

    public SyncVar<bool> freshitemdrop;
    // the gold slot. save already writes item.favourited. this is what other machines see.
    public SyncVar<bool> favourited;

    public enum CurrentBattery : byte {
        None,
        smallbattery,
        mediumbattery,
        largebattery
    }
    public SyncVar<byte> current_battery;

    //  Plush index or the building special_value
    public SyncVar<byte> specialdata1;
    public SyncVar<float> specialdata2; // data0 for customitembehavior thing
    public SyncVar<byte> rounds;


    //SpiderSyncPacket  
    public SyncVar<Vector2_4byte_512> spider_target;
    public SyncVar<float> spider_biteCooldown;
    public SyncVar<float> spider_stunTime;

    //Trader  
    public SyncVar<Vector2_4byte_512> trader_desiredpos;

    // GunSyncPacket  
    public SyncVar<bool> gun_racked;
    public SyncVar<bool> gun_safe;
    public SyncVar<bool> gun_hasMag;
    public SyncVar<bool> gun_roundInChamber;
    public SyncVar<bool> gun_roundInChamber_is_casing;

    // BuildingSyncPacket:  
    public SyncVar<bool> building_backgroundified;

    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<Vector2_4byte_512> grabberplant_tipPos;

    //public SyncVar<Color24> lightColor;





    protected override void OnConstructed() {
        
        CreateObject();
#if !RELEASE
        log.l($"CREATED SyncInfo: resourceId: {resourceId.Value} \tId:{Id} Owner:{OwnerId}");
#endif
        NetObjectRegistry.NetIdToSyncInfoDict[Id] = this;

        if (go == null) {
            if (String.IsNullOrEmpty(resourceId.Value)) {
                if (Net.IsServer) {
                    Destroy();
                }
            }
            // construct already tried. next try waits, so we don't hammer CreateObject this frame.
            if (Net.IsClient)
                nullGoRetryAt = Time.unscaledTime + NullGoRetryDelay;
        } else {
            if (Net.IsServer) {
                WriteObjectIntoPacket();
            }
        }
        lastUpdateTick = (ushort)(Net.ServerTick - 1);
        useIgnoreArmed = false;
    }

    public bool CreateObject() {
        // the slot mesh can still be registered after go was cleared. returning here
        // used to leave go null, so the container byte and the destroy never touched it.
        if (go == null)
            RebindMesh();
        if (tracker != null && go == null && tracker.gameObject != null && ReferenceEquals(tracker.syncInfo, this))
            go = tracker.gameObject;
        if (tracker != null && go != null) {
            tracker.syncId = Id;
            objectWasSpawned = true;
            if (!NetObjectRegistry.SyncRegistry.TryGetValue(go, out SyncInfo listed) || !ReferenceEquals(listed, this))
                NetObjectRegistry.SyncRegistry[go] = this;
            return true;
        }
        // far drop unhooked this tracker and kept the mesh. spawning another would duplicate it.
        if (tracker != null && tracker.gameObject != null)
            return false;
        if (go == null) {
            if (!Util.IsWorldInstantiated() || String.IsNullOrEmpty(resourceId.Value)) {
                return false;
            }
            if (objectWasSpawned && Util.IsGeneratingWorld()) {
                return false;
            }

            go = InstantiateResource(resourceId.Value, SpawnNearOwner());
        }
        if (go != null) {
            objectWasSpawned = true;
            if (tracker == null) {
                tracker = go.GetOrAddComponent<SyncInfoGameObjectTracker>();
                tracker.syncInfo = this;
                tracker.syncId = Id;
                NetObjectRegistry.SyncRegistry[go] = this;
            }
            if (!Net.IsServer) {
                ApplyPacketToObject();
            }

            return true;
        }
        return false;
    }

    // worn and bagged gear does not write a pose while it is on a body.
    // a late joiner used to build it at that old loose spot. spawn it on the owner instead.
    Vector2 SpawnNearOwner() {
        Vector2 pos = _position.Value;
        if (Net.IsServer || container_data1.Value == 0)
            return pos;
        try {
            byte data = container_data1.Value;
            // a body slot only sits on that body. the id may already belong to a plant.
            if (data >= 100 && ContainerLink.TryBody(this, out NetBody nb) && nb != null)
                return nb.transform.position;
            if (data == 1 && ContainerLink.TryBag(this, out SyncInfo bag) && bag.container != null)
                return bag.go.transform.position;
        } catch (Exception) {
        }
        return pos;
    }



    bool CheckIgnore() {
        prev_was_ignored = last_was_ignored;
        last_was_ignored = IsIgnored();
        if (last_was_ignored) { 
            return false;
        }
        // the round trip is over. a later use may arm again.
        useIgnoreArmed = false;
        lastUpdateTick = (ushort)(Net.ServerTick - 1);
        return true;
    }


    static HashSet<string> UnfoundResourceIds = new HashSet<string>();
    GameObject InstantiateResource(string resourceId, in Vector2 pos) {
        bool isBackground = false;
        GameObject resource = Resources.Load<GameObject>(resourceId);

        if (resource == null) {
            if (condition.Value <= 0) { // Don't create it
                return null;
            }
            if (resourceId.StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix)) {
                if (ScavMPBuildingSynchronizer.known_entities_with_nonunique_id.TryGetValue(resourceId, out var bgstuff)) {
                    resource = bgstuff;
                    if (resource.TryGetComponent(out Tilemap tl)) {
                        isBackground = true;
                    }
                } else {
                    // It results in green backgrounds
                    if (Util.IsGeneratingWorld()) {
#if TESTING
                        log.warn($"Special resource was tried to get created too early! {resourceId}");
#endif
                        return null;
                    }
                    resource = Resources.Load<GameObject>(resourceId.Substring(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix.Count()));
                    if (resource) {
#if TESTING
                        log.warn($"Special resource wasn't known. But Resources.Load found something! {resourceId}");
#endif
                    } else {
                        if (UnfoundResourceIds.Add(resourceId)) {
                            log.error($"Special resource wasn't identified! {resourceId}");
                        }
                        return resource;
                    }
                }
            } else {
                if (UnfoundResourceIds.Add(resourceId)) {
                    log.error($"Resource does not exist: {resourceId} ");
                }
                return resource;
            }
        }
        if (resource == null) {
            // unknown resource
            return null;
        }

        // create the object 
		var instantiatedObject = ((GameObject)UnityEngine.Object.Instantiate(resource, (Vector3)pos, Quaternion.identity));
        if (isBackground) {
            instantiatedObject.transform.SetParent(WorldGeneration.world.worldGrid.transform);
        }
        // client prefab brings the crate's baked loot. the host sends those items on their own.
        if (!Net.IsServer)
            FixContainerBroke.Strip(instantiatedObject, true);

        return instantiatedObject;
    }






    protected override void RegisterRPC(ref RPCRegistrator r) {
        base.RegisterRPC(ref r);

        r.BindOnChange(this, ref _position, (e) => { NotePositionSync(); }, BindOnChangeFlags.ExecuteOnSync);
        r.BindOnChange(this, ref _rotation, (e) => { OnTransformUpdate(); }, BindOnChangeFlags.ExecuteOnSync);
        r.BindOnChange(this, ref _scale, (e) => { OnTransformUpdate(); }, BindOnChangeFlags.ExecuteOnSync);
    }

    private void OnTransformUpdate() {
        
    }


    // go was cleared but the tracker is still on the fruit in the slot.
    void RebindMesh() {
        if (go != null)
            return;
        if (tracker != null && tracker.gameObject != null && ReferenceEquals(tracker.syncInfo, this)) {
            go = tracker.gameObject;
            NetObjectRegistry.SyncRegistry[go] = this;
            return;
        }
        foreach (KeyValuePair<GameObject, SyncInfo> pair in NetObjectRegistry.SyncRegistry) {
            if (!ReferenceEquals(pair.Value, this) || pair.Key == null)
                continue;
            go = pair.Key;
            if (tracker == null && go.TryGetComponent(out SyncInfoGameObjectTracker found))
                tracker = found;
            return;
        }
    }

    void DestroyTrackedGameObject() {
        // a null go used to return here. the closed fruit stayed in the slot, and the
        // container byte never got applied because Update takes the same null path.
        if (go == null)
            RebindMesh();
        if (go == null)
            return;

        GameObject mesh = go;
        // null the tracker first. its OnDestroy would otherwise free this id again.
        if (tracker != null)
            tracker.syncInfo = null;
        else if (mesh.TryGetComponent(out SyncInfoGameObjectTracker onGo))
            onGo.syncInfo = null;
        NetObjectRegistry.SyncRegistry.Remove(mesh);
        try {
            if (!Net.IsServer)
                ApplyPacketToObject();
            // a dead grav bag. the owner may not have run the item update before this destroy arrived.
            if (mesh.TryGetComponent(out Item boom))
                GravBagBoom.Play(boom, Mathf.Min(boom.condition, condition.Value));
        } catch (Exception e) {
            log.error($"SyncInfo destroy mesh {this} - {e}");
        }
        // the client already destroyed its copy. leaving the host object here
        // made the next register mint a new id onto the leftover.
        if (mesh != null)
            GameObject.Destroy(mesh);
    }

    protected override void OnDestroy() {
        // a reused id already belongs to the new entity. removing it here wiped the live trader on rejoin.
        if (NetObjectRegistry.NetIdToSyncInfoDict.TryGetValue(Id, out SyncInfo listed) && ReferenceEquals(listed, this)) {
            NetObjectRegistry.NetIdToSyncInfoDict.Remove(Id);
        }
        DestroyTrackedGameObject();
    }

    // scene might not be up yet. wait a bit, then try the mesh again. stops once go is set, and dies with the entity.
    const float NullGoRetryDelay = 0.4f;
    float nullGoRetryAt;

    void RetryNullObject() {
        float now = Time.unscaledTime;
        if (now < nullGoRetryAt)
            return;
        nullGoRetryAt = now + NullGoRetryDelay;
        CreateObject();
    }

    protected override void Update() {
        base.Update();

        try {
            if (IsServer) {
                //if (tracker != null) {
                //    WriteObjectIntoPacket();
                //}
            } else if (IsClient) {
                // a null mesh is not finished. the ignore window used to skip the only retry, so this one ignores that gate.
                // a few hundred ms, not every tick. once go is set we stop.
                if (go == null && tracker != null)
                    RebindMesh();
                if (go == null) {
                    RetryNullObject();
                }
                if (go != null && CheckIgnore()) {
                    if (CreateObject()) {
                        if (!tracker.isVisible) {
                            updateTimer++;
                            if (updateTimer < 10) {
                                return;
                            }
                        }
                        ApplyPacketToObject();
                    }
                }
            }
        } catch (Exception e) {
            log.error($"SyncInfo.Update {this} - {e.ToString()}");
        }
    }

    int updateTimer = 0;
    protected override void VisualUpdate() {
        
    }
    // Called from trackers, so it means that the object exists 
    internal void ClientFixedUpdate() {
         UpdateTransform();
    }

    internal void WriteContainerInfo() {
        // q writes the container once, after both hands land.
        // a lifted ingredient keeps its old byte. writing 0 here lands on the id after it is reused for the result.
        if (CraftCommit.Silenced(this))
            return;
        if (ItemSync.SuppressContainerWrite > 0) {
            if (ItemSync.ParentTestSwap)
                ItemSync.ParentTest("swap block write " + ItemSync.ParentTestItem(item));
            return;
        }
        bool write = FixContainerInfo.Begin(this, out var shot);
        if (write) {
            var ici = ItemSync.ItemGetContainerInfo(item);
            container_netId.Value = ici.knetid;
            container_ver.Value = ici.knetid.id == 0 ? (byte)0 : ici.containerVer;
            container_data1.Value = ici.containerExtraData1;
        }
        // a cached 0 must not win over a live hotbar slot. clients only announce, they do not own the byte.
        if (Net.IsServer)
            HeldSeat.KeepSlot(this);
        FixContainerInfo.End(this, shot);
    }

    // item, rigidbody, and building, plus the optional bits this write already reads.
    // the count moves when a component is added or removed. a destroyed one fails the null check, so the next pass looks again.
    private int _partGo;
    private int _partCount = -1;
    private bool _itemLive;
    private bool _rbLive;
    private bool _buildLive;
    private bool _gunLive;
    private bool _ammoLive;
    private bool _waterLive;
    private bool _customLive;
    private bool _plushLive;
    private bool _freshLive;
    private bool _boughtLive;
    private bool _lockLive;
    private Item _item;
    private Rigidbody2D _rb;
    private BuildingEntity _build;
    private GunScript _gun;
    private AmmoScript _ammo;
    private WaterContainerItem _water;
    private CustomItemBehaviour _custom;
    private PlushScript _plush;
    private FreshItemDrop _fresh;
    private BoughtItem _bought;
    private ItemLock _lock;
    private Transform _linkParent;
    private int _linkCount = -2;
    private Container _linkBox;
    private InventorySlot _linkSlot;
    private Limb _linkLimb;

    private void ClearParts() {
        _partGo = 0;
        _partCount = -1;
        _itemLive = false;
        _rbLive = false;
        _buildLive = false;
        _gunLive = false;
        _ammoLive = false;
        _waterLive = false;
        _customLive = false;
        _plushLive = false;
        _freshLive = false;
        _boughtLive = false;
        _lockLive = false;
        _item = null;
        _rb = null;
        _build = null;
        _gun = null;
        _ammo = null;
        _water = null;
        _custom = null;
        _plush = null;
        _fresh = null;
        _bought = null;
        _lock = null;
    }

    private void EnsureParts() {
        if (go == null) {
            ClearParts();
            return;
        }
        int id = go.GetInstanceID();
        int count = go.GetComponentCount();
        bool destroyed = (_itemLive && _item == null)
            || (_rbLive && _rb == null)
            || (_buildLive && _build == null)
            || (_gunLive && _gun == null)
            || (_ammoLive && _ammo == null)
            || (_waterLive && _water == null)
            || (_customLive && _custom == null)
            || (_plushLive && _plush == null)
            || (_freshLive && _fresh == null)
            || (_boughtLive && _bought == null)
            || (_lockLive && _lock == null);
        if (id == _partGo && count == _partCount && !destroyed) {
            return;
        }
        _partGo = id;
        _partCount = count;
        _itemLive = go.TryGetComponent(out _item);
        _rbLive = go.TryGetComponent(out _rb);
        _buildLive = go.TryGetComponent(out _build);
        _gunLive = go.TryGetComponent(out _gun);
        _ammoLive = go.TryGetComponent(out _ammo);
        _waterLive = go.TryGetComponent(out _water);
        _customLive = go.TryGetComponent(out _custom);
        _plushLive = go.TryGetComponent(out _plush);
        _freshLive = go.TryGetComponent(out _fresh);
        _boughtLive = go.TryGetComponent(out _bought);
        if (_rb != null) {
            _lockLive = _rb.TryGetComponent(out _lock);
        } else {
            _lockLive = false;
            _lock = null;
        }
    }

    internal bool TryCachedRigidbody(out Rigidbody2D rb) {
        if (go == null) {
            rb = null;
            return false;
        }
        EnsureParts();
        rb = _rb;
        return _rbLive && rb != null;
    }

    private void LinkParent(Transform parent) {
        int count = parent == null ? -1 : parent.gameObject.GetComponentCount();
        if (parent == _linkParent && count == _linkCount) {
            return;
        }
        _linkParent = parent;
        _linkCount = count;
        _linkBox = null;
        _linkSlot = null;
        _linkLimb = null;
        if (parent == null) {
            return;
        }
        parent.TryGetComponent(out _linkBox);
        parent.TryGetComponent(out _linkSlot);
        parent.TryGetComponent(out _linkLimb);
    }

    // true when WriteObjectIntoPacket would change a field an inactive bag row sends, or the parent is still unregistered.
    internal bool InactiveChanged() {
        if (go == null) {
            return false;
        }
        EnsureParts();
        if (!_itemLive || _item == null) {
            return true;
        }
        Item item = _item;
        if (item.condition < 0f) {
            item.condition = 0f;
        }
        if (condition.Value != item.condition) {
            return true;
        }
        if (favourited.Value != item.favourited) {
            return true;
        }
        byte wet = (byte)(Mathf.Clamp(Time.time - item.wetTime, 0f, 125f) / 125f * 255f);
        if (compressedwetTime.Value != wet) {
            return true;
        }
        if (ContainerDirty(item)) {
            return true;
        }
        bool fresh = _freshLive && _fresh != null && _fresh.timeLeft > 5f;
        if (freshitemdrop.Value != fresh) {
            return true;
        }
        if (_boughtLive && _bought != null) {
            byte bought = (byte)(Mathf.Clamp(_bought.time, 0f, 300f) * 0.85f);
            if (compressedboughtitemtime.Value != bought) {
                return true;
            }
        } else if (compressedboughtitemtime.Value != 0) {
            return true;
        }
        if (_customLive && _custom != null && ItemSync.CustomItemBehaviourStateCanBeJustByte(item)) {
            if (specialdata1.Value != (byte)_custom.state) {
                return true;
            }
            if (_custom.data != null && _custom.data.Length > 0 && _custom.data[0] is float data0 && specialdata2.Value != data0) {
                return true;
            }
        } else if (_plushLive && _plush != null && _plush.index >= 0) {
            if (specialdata1.Value != (byte)_plush.index) {
                return true;
            }
        }
        if (item.battery != null && item.battery.hasBattery) {
            CurrentBattery parsed;
            if (!Enum.TryParse(item.battery.batteryType, out parsed)) {
                return true;
            }
            if (current_battery.Value != (byte)parsed) {
                return true;
            }
        } else if (current_battery.Value != 0) {
            return true;
        }
        if (_gunLive && _gun != null) {
            if (gun_racked.Value != _gun.racked || gun_safe.Value != _gun.safe || gun_hasMag.Value != _gun.hasMag) {
                return true;
            }
            bool chamber = _gun.roundInChamber != RoundInChamber.None;
            bool casing = _gun.roundInChamber == RoundInChamber.Casing;
            if (gun_roundInChamber.Value != chamber || gun_roundInChamber_is_casing.Value != casing) {
                return true;
            }
            if (rounds.Value != (byte)_gun.roundsInMag) {
                return true;
            }
        } else if (_ammoLive && _ammo != null) {
            if (rounds.Value != (byte)_ammo.rounds) {
                return true;
            }
        }
        if (LiquidDirty()) {
            return true;
        }
        bool hasRb = _rbLive && _rb != null;
        bool onBody = go.transform.parent != null && go.transform.parent.GetComponentInParent<Body>() != null;
        if (PoseDirty(onBody, hasRb, _rb)) {
            return true;
        }
        return false;
    }

    private bool ContainerDirty(Item item) {
        if (CraftCommit.Silenced(this) || ItemSync.SuppressContainerWrite > 0) {
            return false;
        }
        if (FixContainerInfo.ParentLinkMissing(item)) {
            return false;
        }
        Transform parent = item.transform.parent;
        LinkParent(parent);
        if (parent == null) {
            return container_data1.Value != 0 || container_netId.Value.id != 0 || container_ver.Value != 0;
        }
        if (item.Stats != null && item.Stats.wearable && _linkLimb != null && _linkLimb.body != null && _linkLimb.body.TryGetNetBody(out NetBody worn) && worn.syncBody != null) {
            byte ver = ContainerLink.VersionOf(worn);
            return container_data1.Value != 100 || container_netId.Value.id != worn.netId.id || container_ver.Value != ver;
        }
        if (_linkSlot != null && _linkSlot.body != null && _linkSlot.body.TryGetNetBody(out NetBody owner) && owner.syncBody != null) {
            byte data = (byte)(200 + _linkSlot.slot);
            byte ver = ContainerLink.VersionOf(owner);
            return container_data1.Value != data || container_netId.Value.id != owner.netId.id || container_ver.Value != ver;
        }
        if (_linkBox != null) {
            if (!NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) || bag is null) {
                return true;
            }
            ushort id = bag.syncId.id;
            byte ver = id == 0 ? (byte)0 : bag.Version;
            return container_data1.Value != 1 || container_netId.Value.id != id || container_ver.Value != ver;
        }
        return container_data1.Value != 0 || container_netId.Value.id != 0 || container_ver.Value != 0;
    }

    private bool LiquidDirty() {
        if (!_waterLive || _water == null) {
            return false;
        }
        if (_water.stack == null) {
            return true;
        }
        System.Collections.Generic.IList<LiquidStack> stack = _water.stack;
        System.Collections.Generic.Dictionary<string, byte> reg = Item_SetupItems_Listener.LiquidIdRegistry;
        if (reg == null) {
            return true;
        }
        for (int i = 0; i < _liquidStack.Count; i++) {
            byte id = _liquidStack[i].LiquidId;
            if (!LastMl(stack, reg, id, out float ml) || ml != _liquidStack[i].LiquidMl) {
                return true;
            }
        }
        for (int i = 0; i < stack.Count; i++) {
            string key = stack[i].liquidId;
            if (key == null || !reg.TryGetValue(key, out byte id) || !LiquidListed(id)) {
                return true;
            }
        }
        return false;
    }

    private static bool LastMl(System.Collections.Generic.IList<LiquidStack> stack, System.Collections.Generic.Dictionary<string, byte> reg, byte id, out float ml) {
        ml = 0f;
        bool found = false;
        for (int i = 0; i < stack.Count; i++) {
            string key = stack[i].liquidId;
            if (key == null || !reg.TryGetValue(key, out byte cur) || cur != id) {
                continue;
            }
            ml = stack[i].amount;
            if (ml < 0f) {
                ml = 0f;
            }
            found = true;
        }
        return found;
    }

    private bool PoseDirty(bool onBody, bool hasRb, Rigidbody2D rb) {
        if (!onBody && (!hasRb || rb == null || rb.transform.parent == null || rb.simulated)) {
            Vector2_4byte_512 pos = go.transform.position;
            if (pos != _position.Value) {
                return true;
            }
            FloatAngle rot = go.transform.rotation.eulerAngles.z;
            rot.Normalize();
            if (!rot.Equals(_rotation.Value)) {
                return true;
            }
            Vector2 scale = go.transform.localScale;
            Vector2 sent = _scale.Value;
            if (scale.x != sent.x || scale.y != sent.y) {
                return true;
            }
        }
        if (hasRb && rb != null && !onBody) {
            Vector2_4byte_200 vel = rb.velocity;
            if (vel != _velocity.Value) {
                return true;
            }
            short ang = (short)Mathf.Clamp(rb.angularVelocity * angvel_to_short, short.MinValue, short.MaxValue);
            if (angvel_quantized.Value != ang) {
                return true;
            }
            bool dyn = rb.bodyType == RigidbodyType2D.Dynamic;
            if (rb_dynamic.Value != dyn) {
                return true;
            }
            bool locked = _lockLive && _lock != null;
            if (itemLock.Value != locked) {
                return true;
            }
        }
        return false;
    }

    internal void WriteObjectIntoPacket() {
        EnsureParts();
        FixSyringeHold.Begin(this, out var syringe);

        {
            bool has_rb = _rbLive && _rb != null;
            Rigidbody2D rb = _rb;
            // a slot or a limb already moves with the body. writing the world pose
            // makes other machines drag a loose copy along behind the player.
            bool onBody = go.transform.parent != null && go.transform.parent.GetComponentInParent<Body>() != null;

            if (!onBody && (!has_rb || rb.transform.parent == null || rb.simulated)) { // dont change those cuz item is probably in container now
                //if (has_rb) {
                //    _position.Value = rb.position;
                //    _rotation.Value = rb.rotation;
                //} else 
                {
                    _position.Value = go.transform.position;

                    FloatAngle newRot = go.transform.rotation.eulerAngles.z;
                    newRot.Normalize();
                    _rotation.Value = newRot;
                }

                _scale.Value = go.transform.localScale;
            }

            if (has_rb && !onBody) {
                _velocity.Value = rb.velocity;
                angvel = rb.angularVelocity;
                rb_dynamic.Value = rb.bodyType == RigidbodyType2D.Dynamic;

                itemLock.Value = _lockLive && _lock != null;
            }
        }

        if (_itemLive && _item != null) {
            Item item = _item;
            // stock use lambdas subtract past 0 (dressing roll, mushtail, suture). buildings keep negative health.
            if (item.condition < 0f)
                item.condition = 0f;
            condition.Value = item.condition;
            favourited.Value = item.favourited;

            wetTime = Time.time - item.wetTime;

            WriteContainerInfo();

            {
                if (_freshLive && _fresh != null) {
                    if (_fresh.timeLeft > 5.0f) {
                        freshitemdrop.Value = true;
                    }
                } else {
                    freshitemdrop.Value = false;
                }
            }

            if (_boughtLive && _bought != null) {
                boughtitemtime = _bought.time;
            } else {
                compressedboughtitemtime.Value = 0;
            }

            if (_customLive && _custom != null && ItemSync.CustomItemBehaviourStateCanBeJustByte(item)) {
                specialdata1.Value = (byte)_custom.state;
                if (_custom.data != null && _custom.data.Length > 0 && _custom.data[0] is float f) {
                    specialdata2.Value = f;
                }
            } else if (_plushLive && _plush != null && _plush.index >= 0) {
                specialdata1.Value = (byte)_plush.index;
            }  


            if (item.battery != null && item.battery.hasBattery) {
                CurrentBattery aaaa;
                if (!Enum.TryParse(item.battery.batteryType, out aaaa)) {
                    Plugin.log.LogError($"Unknown battery type: {item.battery.batteryType} ");
                }
                current_battery.Value = (byte)aaaa;
            } else {
                current_battery.Value = 0;
            }


            if (_gunLive && _gun != null) {
                gun_racked.Value = _gun.racked;
                gun_safe.Value = _gun.safe;
                gun_hasMag.Value = _gun.hasMag;
                gun_roundInChamber.Value = _gun.roundInChamber != RoundInChamber.None;
                gun_roundInChamber_is_casing.Value = _gun.roundInChamber == RoundInChamber.Casing;
                rounds.Value = (byte)_gun.roundsInMag;
            } else if (_ammoLive && _ammo != null) {
                rounds.Value = (byte)_ammo.rounds;
            }


            {
                if (_waterLive && _water != null)
                    SyncLiquidStack(_water);
            }

        } else if (_buildLive && _build != null) {
            BuildingEntity b = _build;
            condition.Value = b.GetHealth();

            if (b.TryGetComponent(out TrackerBuildingEntityBackgroundified tr)) {
                building_backgroundified.Value = tr.is_backgroundified;
            }

            if (b.TryGetComponent(out CorpseScript corpse)) {
                specialdata1.Value = corpse.GetOrAddComponent<CorpseScriptMultiplayerRewrite>().corpseSpriteIndex;
            } else if (b.TryGetComponent(out SpikeStabberScript spike)) {
                specialdata1.Value = spike.activated.ToByte();
            } else if (b.TryGetComponent(out BearTrap bear)) {
                specialdata1.Value = bear.activated.ToByte();
            } else if (b.TryGetComponent(out JumpPadScript pad)) {
                specialdata1.Value = (byte)Mathf.Clamp(Mathf.CeilToInt(pad.cooldown), 0, 255);
            } else if (b.TryGetComponent(out CoilScript coil)) {
                specialdata1.Value = (byte)Mathf.Clamp(Mathf.CeilToInt(coil.cooldown), 0, 255);
            } else if (b.TryGetComponent(out GunmineScript gun)) {
                specialdata1.Value = (byte)Mathf.Clamp(Mathf.CeilToInt(gun.cooldown), 0, 255);
            } else if (b.TryGetComponent(out DrillPod_Update_MultiplayerPatch.DrillPodMultiplayerRewrite drel)) {
                specialdata1.Value = drel.working.ToByte();
            } else if (go.TryGetComponent(out SpiderHandler spider)) {
                spider_target.Value = spider.target;
                spider_biteCooldown.Value = spider.biteCooldown;
                spider_stunTime.Value = spider.stunTime;
            } else if (go.TryGetComponent(out GrabberPlant grabber)) {
                // tip and the bone scale, only while a player is in this chunk. the next write after they walk in carries the host tip.
                if (GrabberPlant_Update_MultiplayerPatch.Live(go.transform.position)) {
                    grabberplant_tipPos.Value = grabber.tipPos;
                    GrabberPlant_Update_MultiplayerPatch.WriteScale(grabber, this);
                }
            } else if (go.TryGetComponent(out TraderScript trader)) {
                trader_desiredpos.Value = trader.desiredPos;
            } else if (b.TryGetComponent(out TrackerSoundCannon cannon)) {
                // spent flag plus the barrel angle it froze on. late join reads the same fields.
                specialdata1.Value = cannon.spent.ToByte();
                if (cannon.spent)
                    specialdata2.Value = cannon.endAngle;
            } else  {
                if (go.transform.childCount == 1 && go.transform.GetChild(0) is var e && e != null && e.TryGetComponent(out GeyserScript geyser)) {
                    specialdata1.Value = geyser.liquidType;
                }
            }
        }

        FixSyringeHold.End(this, syringe);
    }

    // a water stack is a few liquids. the old dictionaries, Except, and Intersect were discarded every packet.
    // remove then add is what marks the synclist. writing the millilitres in place would not.
    private void SyncLiquidStack(WaterContainerItem wci) {
        var stack = wci.stack;
        var reg = Item_SetupItems_Listener.LiquidIdRegistry;
        for (int i = _liquidStack.Count - 1; i >= 0; i--) {
            byte id = _liquidStack[i].LiquidId;
            if (!LastLiquidMl(stack, reg, id, out float ml) || (ml != _liquidStack[i].LiquidMl)) {
                _liquidStack.RemoveAt(i);
            }
        }
        for (int i = stack.Count - 1; i >= 0; i--) {
            byte id = (byte)reg[stack[i].liquidId];
            if (LiquidListed(id))
                continue;
            float amount = stack[i].amount;
            if (amount < 0f) {
                amount = 0f;
            }
            _liquidStack.Add(new LiquidStackPart {
                LiquidId = id,
                LiquidMl = amount
            });
        }
    }

    // the dictionary kept the last row when one liquid was listed twice.
    private static bool LastLiquidMl(System.Collections.Generic.IList<LiquidStack> stack, System.Collections.Generic.Dictionary<string, byte> reg, byte id, out float ml) {
        ml = 0f;
        bool found = false;
        for (int i = 0; i < stack.Count; i++) {
            if ((byte)reg[stack[i].liquidId] != id) {
                continue;
            }
            ml = stack[i].amount;
            if (ml < 0f) {
                ml = 0f;
            }
            found = true;
        }
        return found;
    }

    bool LiquidListed(byte id) {
        for (int i = 0; i < _liquidStack.Count; i++) {
            if (_liquidStack[i].LiquidId == id)
                return true;
        }
        return false;
    }


    void ApplyPacketToObject() {
        updateTimer = 0;

        if (go.TryGetComponent(out Item item)) {
            UpdateItem();
            UpdateLiquids();
        } else if (go.TryGetComponent(out BuildingEntity b)) {
            UpdateBuilding();
        }

    }

    void UpdateLiquids() {
        if (go.TryGetComponent(out WaterContainerItem wci)) {
            CraftCommit.ClearSeenLiquids();
            for (int n = 0; n < _liquidStack.Count; n++) {
                CraftCommit.SeeLiquid(_liquidStack[n].LiquidId, LiquidStackPart.Pack(_liquidStack[n].LiquidMl));
            }
            if (CraftCommit.SkipSeenLiquids(syncId))
                return;
            wci.stack.Clear();
            for (int i = 0; i < _liquidStack.Count; i++) {
                var liquid = _liquidStack[i];

                var liquidid = Item_SetupItems_Listener.LiquidNetIdToId[liquid.LiquidId];
                wci.stack.Add(new LiquidStack(liquidid, liquid.LiquidMl));
            }
        }
    }

    void UpdateTransform() {
        byte data = container_data1.Value;
        // first sight is a spawn, not a release. a held byte clears the hold.
        bool released = data == 0 && poseSeen && posePrevData != 0;
        if (data != 0) {
            poseHold = false;
        } else if (released && !poseFresh) {
            poseHold = true;
        }
        poseSeen = true;
        posePrevData = data;
        poseFresh = false;

        if (data != 0) {
            return;
        }
        if (IsItem() && go.transform.parent != null) {
            return;
        }
        // the round trip window. CheckIgnore clears it, and Update already owns that call.
        if (IsIgnored() || poseHold) {
            return;
        }

        ApplyTransform();
    }

    public bool shouldApplyPos;
    public bool shouldApplyAng;
    RaycastHit2D[] raycastHitBuffer = new RaycastHit2D[8];

    // true when this sample was a relocate and the pose snapped.
    bool GlidePose(out Vector2 pos, out float angle) {
        Vector2 raw = _position.Value;
        float rawAng = _rotation.Value.Degrees;
        if (Net.IsServer) {
            pos = raw;
            angle = rawAng;
            return false;
        }

        float now = Time.fixedTime;
        ushort tick = Net.ServerTick;
        bool moved = _poseGlide
            && ((raw - _poseTo).sqrMagnitude > 0.0001f || Mathf.Abs(Mathf.DeltaAngle(_poseToAng, rawAng)) > 0.5f);
        if (!_poseGlide) {
            _poseGlide = true;
            _poseFrom = _poseTo = raw;
            _poseFromAng = _poseToAng = rawAng;
            _poseTick = tick;
            _poseStart = now;
            _poseDur = 0f;
        } else if (moved) {
            float prevT = _poseDur > 0f ? Mathf.Clamp01((now - _poseStart) / _poseDur) : 1f;
            _poseFrom = Vector2.Lerp(_poseFrom, _poseTo, prevT);
            _poseFromAng = Mathf.LerpAngle(_poseFromAng, _poseToAng, prevT);
            _poseTo = raw;
            _poseToAng = rawAng;
            int span = LiteEntitySystem.Utils.SequenceDiff(tick, _poseTick);
            if (span < 1)
                span = 1;
            float rate = Net.ServerEntitySendRate;
            if (rate < 1f)
                rate = 1f;
            float dur = span / rate;
            // chunk rotation never goes past 12 physics steps. a slower server
            // tick is longer than that, so the glide still covers one tick.
            float cap = Time.fixedDeltaTime * 12f;
            float tickDur = 1f / rate;
            if (cap < tickDur)
                cap = tickDur;
            if (dur > cap)
                dur = cap;
            bool snap = (_poseTo - _poseFrom).sqrMagnitude > 48f * 48f;
            if (snap) {
                _poseFrom = _poseTo;
                _poseFromAng = _poseToAng;
                dur = 0f;
            }
            _poseStart = now;
            _poseDur = dur;
            _poseTick = tick;
            if (snap) {
                pos = raw;
                angle = rawAng;
                return true;
            }
        }

        float t = _poseDur > 0f ? Mathf.Clamp01((now - _poseStart) / _poseDur) : 1f;
        pos = Vector2.Lerp(_poseFrom, _poseTo, t);
        angle = Mathf.LerpAngle(_poseFromAng, _poseToAng, t);
        return false;
    }

    void ApplyTransform() {

        var trans = go.transform;
        Vector2 currentPos = trans.position;
        var currentAngle = go.transform.rotation.eulerAngles.z;
        var scale = _scale.Value;
        var isRigid = go.TryGetComponent(out Rigidbody2D rb);

        if (scale.x != trans.localScale.x || scale.y != trans.localScale.y) {
            var newscale = new Vector3(scale.x, scale.y, trans.localScale.z);
            trans.localScale = newscale;
        }

        Vector2 pos;
        float angle;
        bool poseSnap = GlidePose(out pos, out angle);

        // repeat 100 0 spawn_bandage
        shouldApplyPos = true;
        shouldApplyAng = true;

        var prevRelax = relaxed_combat_sync;
        if (tracker.isVisible) {
            if (isRigid) {

                {
                    var newRigidType = rb_dynamic.Value ? RigidbodyType2D.Dynamic : RigidbodyType2D.Static;
                    if (rb.bodyType != newRigidType) {
                        rb.bodyType = newRigidType;
                    }
                    if (rb_dynamic.Value) {
                        if (itemLock.Value) {
                            rb.GetOrAddComponent<ItemLock>();
                        } else { // It will get removed when parented so hopefully it's not necessary
                            //if (rb.TryGetComponent(out ItemLock itemLockComponent)) {
                            //    Item.Destroy(itemLockComponent);
                            //}
                        }
                    }
                }



                if ((IsContactDangerous()) && Util.TryGetLocalBody(out var body)) {
                    if (shouldApplyPos) {
                        var direction = KM.ClampLength(pos - currentPos, 10);

                        var hitCount = rb.Cast(direction, raycastHitBuffer, 10);

                        bool didHitLocalBody = false;
                        for (int i = 0; i < hitCount; i++) {
                            var hit = raycastHitBuffer[i];
                            var hitTrans = hit.transform;
                            if (hitTrans != null) {
                                if (Util.TryGetBodyFromObject(hitTrans.gameObject, out var hitBody) && hitBody == body) {
                                    didHitLocalBody = true;
                                    break;
                                }
                            }
                        }

                        if (didHitLocalBody) {
                            relaxed_combat_sync = true;
                        } else {
                            relaxed_combat_sync = false;
                        }
                    } else {
                        if (shouldApplyAng) {
                            if (relaxed_combat_sync) {
                                angle = (Mathf.LerpAngle(currentAngle, angle, 0.1f));
                            }
                        }
                    }
                } else {
                    //int actualHits = 0;
                    //var hitCount = rb.GetContacts(contactBuffer);
                    //for (int i = 0; i < hitCount; i++) {
                    //    var hit = contactBuffer[i];
                    //    var hitRb = hit.otherRigidbody;
                    //    if (hitRb != null) {
                    //        actualHits++;
                    //    }
                    //}
                    //if (actualHits > 1) {
                    //    shouldApplyPos = true;
                    //}
                }
            }

        }
        if (relaxed_combat_sync || prevRelax) {
            pos = Vector2.Lerp(currentPos, pos, 0.2f);
            angle = Mathf.LerpAngle(currentAngle, angle, 0.2f);
        } else if (isRigid && rb.bodyType == RigidbodyType2D.Dynamic && rb.simulated) {
            rb.velocity = _velocity.Value;
            rb.angularVelocity = angvel;
        }

        EnemyMoveProbe.NoteObject(this, pos, currentPos, poseSnap);
        {
            if (shouldApplyPos) {
                trans.position = pos;
                shouldApplyPos = false;
            }
            if (shouldApplyAng) {
                trans.rotation = Quaternion.Euler(0, 0, angle);
                shouldApplyAng = false;
            }
        }
    }





    void UpdateItemContainer() {
        if (go == null || item == null)
            return;
        ItemSync.ParentTestHand(item);
        // still waiting on the move. a stale ground byte shouldnt dump it.
        if (MoveCommit.Held(syncId))
            return;
        // the move reply already sat it. this byte is from before that. leave it until the byte agrees.
        if (MoveCommit.StaleContainer(this))
            return;
        // announce is still out. do not reseat onto an older host byte. condition and pose stay in Update.
        if (ItemSync.HoldContainerReseat(this))
            return;
		// seating and unloading in here is the packet, not the player. do not tell the host we moved it.
        Item parked = null;
        MoveCommit.PushApply();
        try {
            bool run = FixInvSync.KeepHeld(this)
                && BagPlace.BeforeApply(this);
            if (container_data1.Value < 200)
                CraftCommit.FinishAside(item);
            if (run && container_data1.Value == 0) {
                // still on us. a ground byte there is the rubberband. a real drop already let go. theirs still comes off.
                // a dismembered arm is not a hold. the host drop published 0 and this copy has to let go of the machete.
                bool deadHold = OnDismemberedLimb(go.transform.parent);
                if (go.transform.parent != null && (!Ours(go.transform.parent) || deadHold)) {
                    var ici = ItemSync.ItemGetContainerInfo(item);
                    ItemSync.SafeUnloadItem(ici);
                }
                // dropitem can leave it in their slot. sim stays off and the overlap never sees it, so ctrl and a hover miss a visible item.
                // a loose copy with the quiet already gone is the same: the rigidbody is still asleep.
                if (item != null && item.transform.parent != null && (!Ours(item.transform.parent) || OnDismemberedLimb(item.transform.parent)))
                    HeldSeat.Detach(item);
                else if (item != null && item.transform.parent == null)
                    HeldSeat.Release(item);
            } else if (run) {
            // still loose on this machine. sit on the owner while the slot or bag parent lands.
            if (!Net.IsServer && go.transform.parent == null)
                go.transform.position = SpawnNearOwner();
            var ici = ItemSync.ItemGetContainerInfo(item);

            if (CheckIfContainerIsWrong(ici)) {
                if (container_data1.Value >= 100) { // above 100 is a body 
                    if (ContainerLink.TryBody(this, out NetBody nb)) { // move the item to that player cuz its gonna get synced eventually
                        if (container_data1.Value == 100) { // wearing
                            go.transform.position = nb.transform.position;
                            if (!item.Stats.wearable) {
                                nb.body.AutoPickUpItem(item);
                            } else {
                                ItemSync.Body_ForceWearWearable(nb.body, item);
                            }
                        } else {
							var slot = container_data1.Value - 200;
                            CraftCommit.ReleaseIfLeaving(item, slot);
                            bool slotOk = nb.body.slots != null && slot >= 0 && slot < nb.body.slots.Length;
                            bool stole = false;
                            if (slotOk && nb.body.slots[slot].limb != null && nb.body.slots[slot].limb.dismembered) {
                                HeldSeat.Detach(item);
                                slotOk = false;
                            }
                            if (slotOk && nb.body.HoldingItem(slot)) {
                                Item occupant = nb.body.slots[slot].transform.childCount > 0
                                    ? nb.body.slots[slot].transform.GetChild(0).GetComponent<Item>()
                                    : null;
                                if (occupant == item)
                                    HeldSeat.Show(item);
                                else if (occupant != null && occupant != item && CraftCommit.Yielded(item)) {
                                    // this copy was the ingredient. the result is in the slot now. do not take it back.
                                    stole = true;
                                } else if (occupant != null && occupant != item && SlotHold.Other(nb.body, slot, item)) {
                                    // this slot is already spoken for. sit in the next open one instead of dropping it.
                                    // no open slot used to leave it loose, and the retry logged that forever.
                                    int? open = SlotHold.NextOpen(nb.body);
                                    if (open.HasValue) {
                                        nb.body.PickUpItem(item, open.Value, true);
                                        if (!nb.body.HoldingItem(item))
                                            HeldSeat.Seat(nb.body, item, open.Value);
                                        if (nb.IsBodyLocal())
                                            parked = item;
                                    } else
                                        HeldSeat.SeatBehind(nb.body, item, slot);
                                    stole = true;
                                } else if (occupant != null && occupant != item)
                                    CraftCommit.StepAside(occupant);
                            }
                            if (slotOk && !stole)
                                nb.body.PickUpItem(item, slot, true);
                            // the stock seat no-ops when the slot has a non-item child or the item is hands-only.
                            // parking the copy on the body root left it loose for alt view.
                            if (slotOk && !stole && !nb.body.HoldingItem(item))
                                HeldSeat.Seat(nb.body, item, slot);
                        }
                        if (nb.IsBodyLocal()) {
                            ItemSync.RecordAndApplyInvState(nb.body);
                        }
                        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(nb.transform.position, $"OBJSYNC: Item pickup {this} -> {nb}  data1: {container_data1}");
                    }
                } else {
                    //  move it to that container,  and if container is not found, make send a request to server of all the missing ids
                    if (ContainerLink.TryBag(this, out SyncInfo container_si) && container_si.container != null) {
                        // chest already broke. putting the rag back turns physics off.
                        if (ItemSync.BrokenBag(container_si, container_si.container))
                            ItemSync.ParentTestChest("chest skip apply", item, container_si.go);
                        else
                            ItemSync.Container_ForceLoadItem(item, container_si.container);
                    } 
                }
            } else {
                //Override the position
                if (ici.has_container) {
                    _position.Value = go.transform.position;
                }
            }
            
        }
        FixInvSync.AfterApply(this);
        BagPlace.AfterApply(this);
        if (item != null && container_data1.Value >= 100)
            HeldSeat.Quiet(item);
        } finally {
            MoveCommit.PopApply();
            if (parked != null)
                MoveCommit.SubmitLive(parked);
        }
    }

    // the arm is already gone. a slot on it is not a hand anymore.
    static bool OnDismemberedLimb(Transform parent) {
        if (parent == null)
            return false;
        if (parent.TryGetComponent(out InventorySlot slot))
            return slot.limb != null && slot.limb.dismembered;
        if (parent.TryGetComponent(out Limb limb))
            return limb.dismembered;
        return false;
    }

    // slot or worn on this body. a ground byte must not peel that off.
    static bool Ours(Transform parent) {
        if (parent.TryGetComponent(out InventorySlot slot))
            return slot.body != null && slot.body.IsBodyLocal();
        if (parent.TryGetComponent(out Limb limb))
            return limb.body != null && limb.body.IsBodyLocal();
        return false;
    }

    void UpdateItem() {
        var si = this;
        if (CraftCommit.EatDestroyed(si))
            return;
        if (item == null)
            return;
        // a negative float in the packet used to stick. 0 is empty, not a wrapped ushort.
        float cond = condition.Value;
        if (cond < 0f) {
            cond = 0f;
        }
        // the click already wrote 0. the host field stays ahead of its own sync var until that spend is written.
        // a client keeps that 0 for the use round trip. once the window ends, the host condition is applied.
        bool predictedZero = (item.Stats != null) && item.Stats.destroyAtZeroCondition && (item.condition <= 0f) && (cond > item.condition);
        bool keepZero = predictedZero && (Net.IsServer || IsIgnored());
        if (!keepZero && !CraftCommit.BlockCondition(syncId, cond)) {
            if (predictedZero && log.verbose) {
                log.l($"[spent] sync={syncId} id={item.id} local={item.condition:0.###} host={cond:0.###}");
            }
            item.condition = cond;
        }
        ItemSync.ApplyFavourite(item, syncId, favourited.Value);
        item.wetTime = Time.time - wetTime;

        UpdateItemContainer();

        if (freshitemdrop) {
            item.GetOrAddComponent<FreshItemDrop>();
        }
        if (compressedboughtitemtime > 0) {
            var bi = item.GetOrAddComponent<BoughtItem>();
            bi.time = boughtitemtime;
        }

        {
            if (item.TryGetComponent(out CustomItemBehaviour cib) && ItemSync.CustomItemBehaviourStateCanBeJustByte(item)) {
                // the click already set this. an older off byte should not put the light out.
                if (!Body_UseItemInHand_MultiplayerPatch.StaleState(syncId, specialdata1))
                    cib.state = specialdata1;
                if (cib.data != null && cib.data.Length > 0 && cib.data[0] is float f) {
                    cib.data[0] = specialdata2;
                }
            } else if (item.TryGetComponent(out PlushScript plush)) {
                if (plush.index != specialdata1) {
                    plush.index = specialdata1;
                    j.Prefix(plush);
                    plush.GetComponent<SpriteRenderer>().sprite = plush.possibleSprites[specialdata1];
                    plush.selectedSound = plush.possibleSounds[specialdata1];
                }
            }
        }

        if (item.battery != null) { // item has battery component,  check the battery status
            DoBattery(item.battery);
        }

        {
            if (item.TryGetComponent(out GunScript gun)) {
                gun.racked = gun_racked;
                gun.safe = gun_safe;
                gun.hasMag = gun_hasMag;
                gun.roundInChamber = gun_roundInChamber ? (gun_roundInChamber_is_casing ? RoundInChamber.Casing : RoundInChamber.Round) : RoundInChamber.None;
                gun.roundsInMag = rounds;
            } else if (item.TryGetComponent(out AmmoScript ammo)) {
                ammo.rounds = rounds;
            }
        }
    }

    void UpdateBuilding() {
        var b = go.GetComponent<BuildingEntity>();
        b.SetHealth(condition, false);
        // death fx lives in Update, and the client skips that Update so the loot is not rolled twice.
        if (Net.IsClient && b.GetHealth() < 0.5f)
            BuildingEntity_Update_MultiplayerPatch.PlayDeathOnce(b);

        if (building_backgroundified.Value) {
            var tr = b.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
            if (!tr.is_backgroundified) {
                b.Backgroundify();
            }
            tr.is_backgroundified = building_backgroundified.Value;
        }

        if (b.TryGetComponent(out CorpseScript corpse)) {
            corpse.GetComponent<SpriteRenderer>().sprite = corpse.startSprites[specialdata1];
        } else if (b.TryGetComponent(out SpikeStabberScript spike)) {
            var isActivated = specialdata1.Value.ToBool();
            if (isActivated != spike.activated) {
                if (isActivated) {
                    spike.Stab();
                } else {

                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(spike.transform.position, "RESET SPIKE", Color.cyan);

                    var anim = spike.GetComponent<Animator>();
                    anim.Play("SpikeStab", -1, 0);
                    anim.speed = 0;

                    spike.activated = false;

                }
            }
        } else if (b.TryGetComponent(out BearTrap bear)) {
            if (specialdata1.Value > 0)
                TrapClaim.SpendBear(bear);
        } else if (b.TryGetComponent(out JumpPadScript pad)) {
            TrapClaim.RaiseIfIdle(pad, specialdata1.Value);
        } else if (b.TryGetComponent(out CoilScript coil)) {
            TrapClaim.RaiseIfIdle(coil, specialdata1.Value);
        } else if (b.TryGetComponent(out GunmineScript gun)) {
            TrapClaim.RaiseGun(gun, specialdata1.Value);
        } else if (b.TryGetComponent(out DrillPod_Update_MultiplayerPatch.DrillPodMultiplayerRewrite drel)) {
            if (specialdata1 == 1 && !drel.working) {
                DrillPod_OnUse_MultiplayerPatch.ForceRepairDrill(drel.drill);
            }
        } else if (go.TryGetComponent(out GrabberPlant grabber)) {
            if (GrabberPlant_Update_MultiplayerPatch.Live(go.transform.position))
                GrabberPlant_Update_MultiplayerPatch.Follow(grabber, this);
        } else if (go.TryGetComponent(out TraderScript trader)) {
            trader.desiredPos = trader_desiredpos.Value;
        } else if (b.TryGetComponent(out SoundCannon _)) {
            if (specialdata1.Value != 0)
                b.GetOrAddComponent<TrackerSoundCannon>().soundcannon_spent(specialdata2.Value);
        } else {
            if (go.transform.childCount == 1 && go.transform.GetChild(0) is var e && e != null && e.TryGetComponent(out GeyserScript geyser)) {
                geyser.liquidType = specialdata1.Value;
            }
        }
    }


    void DoBattery(BatteryItem battery) {
        if (current_battery == 0) {
            battery.UnloadBattery(true);
        } else {
            var batid = ((CurrentBattery)current_battery.Value).ToString();
            BatteryInfo batteryInfo = Item.GlobalItems[batid] as BatteryInfo;
            battery.batteryType = batid;
            battery.maxCharge = batteryInfo.maxCharge;
        }
    }

    // returns true if item should be put in the new container
    bool CheckIfContainerIsWrong(ItemSync.ItemsContainerInfo ici) { // called only when container should be a container and NOT an inventory
        if (ici.containerExtraData1 != container_data1.Value || ici.knetid != container_netId.Value) {
            ItemSync.SafeUnloadItem(ici);
            return true;
        } else {
            return false;
        }
        return true;
    }


    public bool IsContactDangerous() {
        return go.TryGetComponent(out SpiderHandler _) || go.TryGetComponent(out DamagingCrate _);
    }



    #region KROK_OLD_SYNCINFO

    public string GetItemID() {
        if (go != null) {
            var itemId = SyncInfo.GetItemID(go);
            if (!String.IsNullOrEmpty(itemId)) {
                resourceId.Value = itemId;
            }
        }
        return resourceId.Value;
    }
    public static string GetItemID(GameObject go) {
        if ( go.TryGetComponent(out Item item) ) {
            return item.id;
        }
        if ( go.TryGetComponent(out BuildingEntity b) ) {
            return b.id;
        }
        return null;
    }



    public bool objectWasSpawned = false;
    public bool prev_was_ignored = false;
    public bool last_was_ignored = false;
    public ushort lastUpdateTick = 0;
    // set by a predicted use. CheckIgnore clears it once the deadline is actually past.
    private bool useIgnoreArmed;
    public knetid syncId => Id;
    public GameObject go;
    public SyncInfoGameObjectTracker tracker;

    public bool relaxed_combat_sync = false;

    public Vector2 transformPosition => go.transform.position;

    // a destroyed object still has this sync entry. getcomponent on it throws, and the move then retries forever.
    public Item item {
        get {
            if (go == null) {
                return null;
            }
            EnsureParts();
            if (_itemLive && _item != null) {
                return _item;
            }
            return null;
        }
    }
    public Container container => go.GetComponent<Container>();
    public BuildingEntity building => go.GetComponent<BuildingEntity>();
    public TraderScript trader => go.GetComponent<TraderScript>();
    public SpiderHandler spider => go.GetComponent<SpiderHandler>();
    public GunScript gun => go.GetComponent<GunScript>();
    public AmmoScript ammo => go.GetComponent<AmmoScript>();
    public WaterContainerItem liquidcontainer => go.GetComponent<WaterContainerItem>();

#if DEBUG
    public double deleted_time;
    public bool is_deleted = false;
#endif

    public bool IsItem() {
        return go.GetComponent<Item>() != null;
    }
    public bool CanBeUsedToAmputate() {
        return IsItem() && Item_DoAmputate_MultiplayerPatch.ItemsThatCanAmputate.Contains(item.id);
    } 
    public bool IsContainer() {
        return container != null;
    }
    public bool IsBuilding() {
        return go.GetComponent<BuildingEntity>() != null;
    }
    public bool IsTrader() {
        return go.GetComponent<BuildingEntity>() && trader != null;
    }
    public bool IsGun() {
        return gun != null;
    }
    public bool IsAmmo() {
        return ammo != null;
    }
    public bool IsLiquidContainer() {
        return liquidcontainer != null;
    }
    public bool IsSpider() {
        return spider != null;
    }
    public bool IsAED() {
        return IsItem() && item.IsAED();
    }
    public bool IsManualDefibrillator() {
        return IsItem() && item.IsManualDefibrillator();
    }

    // half the ushort ring still compares as ahead. further than this is a stale tick, not a round trip.
    private const int MaxIgnoreLead = 16384;

    // one predicted use. the postfix also runs when stamina no-ops, so a repeat must not slide the deadline.
    public void ArmPredictedUse() {
        if (useIgnoreArmed) {
            return;
        }
        useIgnoreArmed = true;
        SetIgnoreTimeForRoundTrip();
    }

    /// <summary>
    /// CLIENT ONLY
    /// </summary>
    public void SetIgnoreTimeForRoundTrip(float additional_time_seconds = 0.3f) {
        float extra = ClientMain.RTT_IN_SECONDS_with_margin;
        if (extra < 0f) {
            extra = 0f;
        }
        // a bad ping used to add minutes of ticks. the ushort cast then sat half a cycle ahead.
        if (extra > 2f) {
            extra = 2f;
        }
        additional_time_seconds += extra;
        if (additional_time_seconds < 0f) {
            additional_time_seconds = 0f;
        }
        int span = (int)(additional_time_seconds * Net.ServerEntitySendRate);
        if (span < 1) {
            span = 1;
        }
        if (span > MaxIgnoreLead) {
            span = MaxIgnoreLead;
        }
        ushort newTick = (ushort)(Net.ServerTick + span);
        int seqDiff = LiteEntitySystem.Utils.SequenceDiff(newTick, lastUpdateTick);
        if (seqDiff > 0) {
            lastUpdateTick = newTick;
        }
    }
    public bool IsIgnored() {
        // positive lead means the deadline is still ahead of the server, wrap included.
        // equal is still this tick, so it stays stale until the server moves past it.
        int lead = LiteEntitySystem.Utils.SequenceDiff(lastUpdateTick, Net.ServerTick);
        return lead >= 0 && lead <= MaxIgnoreLead;
    }

    public override string ToString() {
        if (this == null) {
            return "SyncInfo(NULL)";
        }
        if (this.go == null) {
            var res = this.GetItemID();
            if (string.IsNullOrEmpty(res)) {
                return $"SyncInfo(NULL_OBJ, id: {syncId})";
            } else {
                return $"SyncInfo(NULL_OBJ, id: {syncId}, res: {res})";
            }
        }
        return $"SyncInfo({(IsBuilding() ? "building" : (IsItem() ? "item" : "UNKNOWN_TYPE"))}, id: {syncId}, res: {this.GetItemID() ?? go.ToString()})";
    }
    #endregion
}







public struct PlayerInputPacket {
    public SByteScalarVec2 Move;
    public Vector2 LookPos;
    public Vector2_4byte_512 CameraPos;
    public ShortScalarVec2 CursorPos;
    public bool IsInPauseMenu;
    public bool IsInConsole;
    public bool IsWindowFocused;
    public bool IsChatFocused;
    public bool IsInCraftingMenu;
    public bool IsInTradingMenu;

    public bool ButtonAttack;
    public bool ButtonIteminteract;

    public knetid WoundviewTargetNetBodyId;
    public knetid DragItemId;

    public void CollectInput() {

        if (ScavPlayer.TryGetLocalNetBody(out var nb)) {
            Move = (SByteScalarVec2)nb.body.moveDir;
        }
        LookPos = Util.GetCursorWorldPos(); // on server limit it to 50x30 from the body

        CameraPos = ((Vector2)Camera.main.transform.position);

        {
            var mouseRelativeToCenter = Util.GetCursorPos();
            var res = Util.GetResolution();
            mouseRelativeToCenter -= res * 0.5f;
            mouseRelativeToCenter /= res.y;
            mouseRelativeToCenter *= 2f;

            CursorPos = (ShortScalarVec2)mouseRelativeToCenter;

        }

        IsInConsole = Con.IsConsoleOpen();
        IsWindowFocused = Application.isFocused;
        IsChatFocused = Chat.CHAT_textbox_input_focused;
        IsInCraftingMenu = Util.IsInCraftingMenu();
        IsInTradingMenu = Util.IsInTradingMenu();
        IsInPauseMenu = Util.IsInPauseMenu() || UIMainMenu.IsOpen();

        ButtonAttack = Input.GetKey(KeyBinds.GetBind("attack"));
        ButtonIteminteract = Input.GetKey(KeyBinds.GetBind("iteminteract"));

        if (ScavPlayer.LocalPlayer != null) {
            if (PlayerCamera.main != null) {
                // net id throws once that sync body is gone. the panel can still be open for a frame.
                if (Util.IsInWoundView() && WoundView.view != null && WoundView.view.body != null && WoundView.view.body.TryGetNetBody(out var pacient) && pacient.syncBody != null) {
                    WoundviewTargetNetBodyId = pacient.netId;
                } else {
                    WoundviewTargetNetBodyId = 0;
                }
                DragItemId = AnyObjectNetId.GetNetId(PlayerCamera.main.dragItem).netId;
            } else {
                DragItemId = 0;
                WoundviewTargetNetBodyId = 0;
            }
        }
    }
    public void ValidateInput(ScavPlayer plr) {

        //if (CursorPos.IsFinite()) {
        //    CursorPos = CursorPos.Clamp(-1, 1);
        //} else {
        //    CursorPos = Vector2.zero;
        //}
        if (!LookPos.IsFinite()) {
            LookPos = Vector2.zero;
        }
        //if (!CameraPos.IsFinite()) {
        //    CameraPos = Vector2.zero;
        //}

        if (plr.body != null) {
            if (plr.body.moveDir != Vector2.zero) {
                IsWindowFocused = true;
                IsChatFocused = false;
                IsInTradingMenu = false;
            }
        }

        if (plr.IsConscious() ) {
            {
                var pos = plr.netBody.position;
                var theAspect = new Vector2(50, 30);
                LookPos = LookPos.Clamp(pos - theAspect, pos + theAspect);
            }

            if (IsInTradingMenu) {
                //TODO get closest trader

            }



            if (DragItemId != 0) {
                if (ItemSync.TryGetItem(DragItemId, out var si, out var item) && plr.IsConscious() && ItemSync.CheckIfBodyReachThisItem(item, plr.body)) {
                } else {
                    DragItemId = 0;
                }
            }
            if (WoundviewTargetNetBodyId != 0) {
                if (plr.body != null && NetBody.TryGetNetBodyFromId(WoundviewTargetNetBodyId, out var nb) && KM.DistanceCheck(nb, plr.body, 30)) {
                } else {
                    WoundviewTargetNetBodyId = 0;
                }
            }
        } else {
            IsInTradingMenu = false;
            DragItemId = 0;
            WoundviewTargetNetBodyId = 0;
        }
    }

    public void SetInput(BasePlayer plr) {
        plr.Move.Value = Move;
        plr.LookPos.Value = LookPos;
        plr.CameraPos.Value = CameraPos;
        plr.CursorPos.Value = CursorPos;
        plr.IsInPauseMenu.Value = IsInPauseMenu;
        plr.IsInConsole.Value = IsInConsole;
        plr.IsWindowFocused.Value = IsWindowFocused;
        plr.IsChatFocused.Value = IsChatFocused;
        plr.IsInCraftingMenu.Value = IsInCraftingMenu;
        plr.IsInTradingMenu.Value = IsInTradingMenu;
        plr.ButtonAttack.Value = ButtonAttack;
        plr.ButtonIteminteract.Value = ButtonIteminteract;
        plr.WoundviewTargetNetBodyId.Value = WoundviewTargetNetBodyId;
        plr.DragItemId.Value = DragItemId;
    }
}

public class BasePlayerController : HumanControllerLogic<PlayerInputPacket, BasePlayer> {

    public BasePlayerController(EntityParams entityParams) : base(entityParams) {

    }

    protected override void Update() {
        base.Update();
        

    }

    protected override void VisualUpdate() {
        if (ControlledEntity == null)
            return;

        ref var nextCommand = ref ModifyPendingInput();

        nextCommand.CollectInput();

    }

    protected override void BeforeControlledUpdate() {
        base.BeforeControlledUpdate();

        if (ControlledEntity == null)
            return;

        if (ControlledEntity.player != null) {
            if (Net.IsServer) {
                CurrentInput.ValidateInput(ControlledEntity.player);
                CurrentInput.SetInput(ControlledEntity);
            }
            ControlledEntity.ApplyInput();
        }

    }



}




[EntityFlags(EntityFlags.UpdateOnClient)]
public class BasePlayer : PawnLogic {

    public SyncVar<ulong> playerSteamId;
    public readonly SyncString playerName = new();
    public SyncVar<Color24> playerColor;

    public SyncVar<SByteScalarVec2> Move;
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<Vector2> LookPos;
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<Vector2_4byte_512> CameraPos;
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<ShortScalarVec2> CursorPos;
    public SyncVar<bool> IsInPauseMenu;
    public SyncVar<bool> IsInConsole;
    public SyncVar<bool> IsWindowFocused;
    public SyncVar<bool> IsChatFocused;
    public SyncVar<bool> IsInCraftingMenu;
    public SyncVar<bool> IsInTradingMenu;
    public SyncVar<bool> ButtonAttack;
    public SyncVar<bool> ButtonIteminteract;
    public SyncVar<knetid> WoundviewTargetNetBodyId;
    public SyncVar<knetid> DragItemId;

    public SyncVar<knetid> MinigameCurrentItemId;
   //[SyncVarFlags(SyncFlags.)]
    public SyncVar<AnyObjectNetId> MinigameTargetObjectId;
    public SyncVar<byte> MinigameTypeId;
    public SyncVar<ushort> MinigameSessionId;

    public SyncVar<bool> MuteVC;
    public SyncVar<bool> MuteTC;

    //public SyncVar<int> testCounter;


    public void ApplyInput() {

        player.IsInPauseMenu = IsInPauseMenu.Value;
        player.IsInConsole = IsInConsole.Value;
        player.IsAltTab = !IsWindowFocused.Value;
        player.IsChatFocused = IsChatFocused.Value;
        player.IsInCraftingMenu = IsInCraftingMenu.Value;
        player.ButtonAttack = ButtonAttack.Value;
        player.ButtonIteminteract = ButtonIteminteract.Value;

        player.IsInTradingMenu = IsInTradingMenu.Value;
        player.WoundviewTargetNetBodyId = WoundviewTargetNetBodyId.Value;
        player.DragItemId = DragItemId.Value;

    }





    protected override void RegisterRPC(ref RPCRegistrator r) {
        base.RegisterRPC(ref r);

        r.BindOnChange(this, ref playerColor, (e) => { OnNameChange(); }, BindOnChangeFlags.ExecuteOnSync);
        r.BindOnChange(this, ref CursorPos, (e) => { OnCursorMove(); }, BindOnChangeFlags.ExecuteOnSync);
    }
    void OnCursorMove() {
        if (player != null && player.ServerPlayerState != null) {
            player.ServerPlayerState.last_cursormove = Time.realtimeSinceStartupAsDouble;
        }
    }
    void OnNameChange() {
#if !RELEASE
        log.l($"BasePlayer.OnNameChange  {playerName.Value} - {playerColor.Value}");
#endif
        if (player != null) {
            player.ApplyNameAndColor(playerName.Value, playerColor.Value);
        }
    }



    public ScavPlayer player { get; private set; }

    // the session instance, so an old entity cannot remove a player id that was reused.
    internal NetPlayer serverNetPlayer;

    static ScavPlayer CreatePlayerPrefab(BasePlayer playerEntity, string playerName, Color24 playerColor) {

        if (Net.TryGetSteamTransport(out var tsteam)) {
            var steamId = playerEntity.playerSteamId.Value;
            if (!KSteam.CURRENT_LOBBY.members.ContainsKey((Steamworks.CSteamID)steamId)) {
                // Patch potential exploit that will allow to spoof steamid of users
                // So if userid is not in the lobby, the player doesn't get created
                if (log.verbose) log.warn($"Player SteamID is not in member list: {steamId}");

                KSteam.UpdateLobbyInfo((Steamworks.CSteamID)KSteam.CURRENT_LOBBY.lobby_steamID, ref KSteam.CURRENT_LOBBY);

                return null;
            }
        }


        var _PLRPREFAB = new GameObject("Player");
        _PLRPREFAB.hideFlags = HideFlags.HideAndDontSave;
        GameObject.DontDestroyOnLoad(_PLRPREFAB);
        var player = _PLRPREFAB.AddComponent<ScavPlayer>();
        playerEntity.player = player;


        if (playerEntity.OwnerId == 0) {
            if (Net.IsServer) {
                player.IsLocal = true;
            }
            player.IsHost = true;
        } else {
            if (playerEntity.IsLocalControlled) {
                player.IsLocal = true;
                ScavPlayer.LocalPlayer = player;
            }
        }

        player.playerId = playerEntity.OwnerId;

        player.playerName = playerName;
        player.playerColor = playerColor;
        player.BasePlayer = playerEntity;
        player.SteamId = playerEntity.playerSteamId.Value;
        

        player.ApplyNameAndColor(player.playerName, player.playerColor);

        return player;
    }
    internal static BasePlayer ServerCreatePlayer(string playerName, ulong steamId, Color24 playerColor, bool isHost) {

        NetPlayer serverPlayer = null;
        if (!isHost) {
            var lesPeer = new LESKrokNetServerPeer();
            serverPlayer = Net.ServerEntityManager.AddPlayer(lesPeer);
            lesPeer.plrId = serverPlayer.Id;
        }
        var playerEntity = Net.ServerEntityManager.AddEntity<BasePlayer>(e => {
            e.playerSteamId.Value = steamId;
            e.playerName.Value = playerName;
            e.playerColor.Value = playerColor;
        });
        playerEntity.serverNetPlayer = serverPlayer;
        if (!isHost) {
            Net.ServerEntityManager.AddController<BasePlayerController>(serverPlayer, playerEntity);
        }
        


        var plr = CreatePlayerPrefab( playerEntity , playerName, playerColor);

#if !RELEASE
        log.l($"LiteEntitySystem: Player joined: \"{playerEntity}\" - {playerEntity.player}");
#endif 

        return playerEntity;
    }


    public BasePlayer(EntityParams entityParams) : base(entityParams) {
        MinigameTypeId.Value = 255;
    }
    protected override void OnConstructed() {
        base.OnConstructed();

        CreatePlayerGameObject();

        playerName.ValueChanged += (e) => {
            OnNameChange();
        };
    }
    void CreatePlayerGameObject() {
        if (player == null && Net.IsClient) {
            if (Net.TryGetSteamTransport(out var tsteam)) {
                if (playerSteamId.Value == 0) {
                    return;
                }
            }
            var plr = CreatePlayerPrefab(this, playerName.Value, playerColor.Value);
        }
    }

    protected override void OnLateConstructed() {
        base.OnLateConstructed();
    }
    protected override void OnSyncGroupsChanged(SyncGroup enabledGroups) {
        base.OnSyncGroupsChanged(enabledGroups);
    }

    protected override void OnLagCompensationStart() {
        base.OnLagCompensationStart();
    }

    protected override void OnLagCompensationEnd() {
        base.OnLagCompensationEnd();
    }

    protected override void OnDestroy() {
        base.OnDestroy();

        if (player != null) {
            Net.RemoveServerPlayer(serverNetPlayer);
            serverNetPlayer = null;

            player.BasePlayer = null;
            ScavPlayer.Destroy(player.gameObject);
            player = null;
        }
    }

    protected override void Update() {
        base.Update();

        if (player != null) {
            if (IsServer && IsLocalControlled) {
                var nextCommand = new PlayerInputPacket();
                nextCommand.CollectInput();
                nextCommand.SetInput(this);
                ApplyInput();
            }
        } else {
            CreatePlayerGameObject();
        }
    }
    protected override void VisualUpdate() {
        base.Update();

        if (player != null) {
            if (!IsServer && !IsLocalControlled) {
                ApplyInput();
            } 

        }

    }
}





public class LESKrokNetServerPeer : AbstractNetPeer {
    internal knetid plrId = byte.MaxValue;

    public LESKrokNetServerPeer() {
    }

    public override void TriggerSend() {
        if (Net.Transport is TransportLiteNetLib tlnl) {
            tlnl.NetManager.TriggerUpdate();
        } else if (Net.Transport is TransportSteamworks tsteam) {
            tsteam.FlushNagle(plrId);
        }
    }
    public override void SendReliableOrdered(ReadOnlySpan<byte> data) {
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, data, plrId);
    }
    public override void SendUnreliable(ReadOnlySpan<byte> data) { 
        Net.Server_SendTo(DeliveryMethod.Unreliable, data, plrId); 
    }
    public override int GetMaxUnreliablePacketSize() => 1000;
    public override string ToString() => $"{this?.GetType().Name} - {DebugHelp.GetPlayerFullDebugString(plrId)}";
}
public class LESKrokNetClientPeer : AbstractNetPeer {

    public LESKrokNetClientPeer() {
    }

    public override void TriggerSend() {
        if (Net.Transport is TransportLiteNetLib tlnl) {
            tlnl.NetManager.TriggerUpdate();
        } else if (Net.Transport is TransportSteamworks tsteam) {
            tsteam.FlushNagle();
        }
    }
    public override void SendReliableOrdered(ReadOnlySpan<byte> data) => Net.Client_Send(DeliveryMethod.ReliableOrdered, data);
    public override void SendUnreliable(ReadOnlySpan<byte> data) => Net.Client_Send(DeliveryMethod.Unreliable, data);
    public override int GetMaxUnreliablePacketSize() => 1000;
    public override string ToString() => this.GetType().Name;
}




