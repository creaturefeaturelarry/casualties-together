using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Together;









public static class Util_MPExtensions {
    public static NetBody GetNetBody(this Limb limb) {
        return limb.body.GetComponent<NetBody>();
    }
    public static NetBody GetNetBody(this Body body) {
        return body.GetComponent<NetBody>();
    }
    public static bool TryGetNetBody(this Limb limb, out NetBody nb) {
        return limb.body.TryGetComponent(out nb);
    }
    public static bool TryGetNetBody(this Body body, out NetBody nb) {
        return body.TryGetComponent(out nb);
    }
    public static bool TryGetNetBody(this Vomiter vomiter, out NetBody nb) {
        return vomiter.TryGetComponent(out nb);
    }
    public static bool TryGetNetBody(this FacialExpression face, out NetBody nb) {
        return face.body.TryGetComponent(out nb);
    }
    public static bool TryGetNetPlayer(this Limb limb, out ScavPlayer plr) {
        return limb.body.TryGetNetPlayer(out plr);
    }
    public static bool TryGetNetPlayer(this NetBody nb, out ScavPlayer plr) {
        if (nb.IsPlayer) {
            plr = nb.plr;
            return true;
        }
        plr = null;
        return false;
    }
    public static bool TryGetNetPlayer(this Body body, out ScavPlayer plr) {
        if (body.TryGetNetBody(out var nb) && nb.IsPlayer) {
            plr = nb.plr;
            return true;
        }
        plr = null;
        return false;
    }

    public static bool TryGetSyncInfo(this Item i, out SyncInfo si) {
        return NetObjectRegistry.TryGetSyncInfo(i, out si);
    }
    public static bool TryGetSyncInfo(this BuildingEntity b, out SyncInfo si) {
        return NetObjectRegistry.TryGetSyncInfo(b, out si);
    }
    public static bool TryGetTracker(this Item i, out SyncInfoGameObjectTracker tracker) {
        if (i == null) { tracker = null; return false; }
        return i.gameObject.TryGetComponent(out tracker);
    }
    public static bool TryGetTracker(this BuildingEntity b, out SyncInfoGameObjectTracker tracker) {
        if (b == null) { tracker = null; return false; }
        return b.gameObject.TryGetComponent(out tracker);
    }
    public static bool TryGetTracker(this GameObject go, out SyncInfoGameObjectTracker tracker) {
        if (go == null) { tracker = null; return false; }
        return go.TryGetComponent(out tracker);
    }






    public static short QuantizeFloatToShort(float value, float max) {
        float normalized = Mathf.Clamp(value / max, -1f, 1f);
        return (short)Math.Round(normalized * short.MaxValue);
    }

    public static float UnquantizeFloatFromShort(short quantizedValue, float max) {
        return (quantizedValue / (float)short.MaxValue) * max;
    }

    public static ushort QuantizeFloatToUShort(float value, float max) {
        float normalized = Mathf.Clamp01(value / max);
        return (ushort)Math.Round(normalized * ushort.MaxValue);
    }

    public static float UnquantizeFloatFromUShort(ushort quantizedValue, float max) {
        return (quantizedValue / (float)ushort.MaxValue) * max;
    }


}











public class SimpleAhhTimer {
    public double curtime {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Time.realtimeSinceStartupAsDouble;
    }
    public double last_fired = 0;
    public double interval = 0;
    public SimpleAhhTimer(double interval = 1, float startdelay = 0) {
        this.interval = interval;
        if (startdelay > 0)
            this.last_fired = curtime + startdelay;
    }
    public bool Check() {
        if (curtime - last_fired >= interval) {
            last_fired = curtime;
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DeliveryMethod DoReliability() {
        return Check() ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T DoElse<T>(in T a, in T b) {
        return Check() ? a : b;
    }

}










