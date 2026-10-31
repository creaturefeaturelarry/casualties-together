using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Together;



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










