using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Together;

// krok math
public static class KM {






    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2Int ToInt(this Vector2 v2) {
        return new Vector2Int(Mathf.RoundToInt(v2.x), Mathf.RoundToInt(v2.y));
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 ClampLength(Vector2 v2, float maxLength) {
        var m = (v2.sqrMagnitude);
        if (m > maxLength * maxLength) {
            var n = (float)Math.Sqrt(m);
            return new Vector2((v2[0] / n) * maxLength, (v2[1] / n) * maxLength);
        }

        return v2;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 AngleToVector2(float rad) {
        return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 ClampDistance(Vector2 vec2, Vector2 center, float max_magnitude) {
        var diff = vec2 - center;
        return center + ClampLength(diff, max_magnitude);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RemapClamped(this float value, float from1, float to1, float from2, float to2) {
        return Mathf.Clamp((value - from1) / (to1 - from1) * (to2 - from2) + from2, from2, to2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInRange(this int number, int min, int max) {
        return number >= min && number <= max;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInRange(this float number, float min, float max) {
        return number >= min && number <= max;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInRange(this double number, double min, double max) {
        return number >= min && number <= max;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp11(float a) {
        return Mathf.Clamp(a, -1f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp11(double a) {
        return Math.Clamp(a, -1.0, 1.0);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnityEngine.Vector2 NormalTo(in UnityEngine.Vector2 from, in UnityEngine.Vector2 to, out float magnitude) {
        var vec = (to - from);

        magnitude = vec.magnitude;
        if (magnitude > 1E-05f) {
            vec /= magnitude;
        } else {
            vec = Vector2.zero;
        }

        return vec;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnityEngine.Vector2 NormalTo(in UnityEngine.Vector2 from, in UnityEngine.Vector2 to) {
        var vec = (to - from);

        float magnitude = vec.magnitude;
        if (magnitude > 1E-05f) {
            vec /= magnitude;
        } else {
            vec = Vector2.zero;
        }

        return vec;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnityEngine.Vector2 Normal(UnityEngine.Vector2 a, out float magnitude) {

        magnitude = a.magnitude;
        if (magnitude > 1E-05f) {
            a /= magnitude;
        } else {
            a = Vector2.zero;
        }

        return a;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSqr(in UnityEngine.Vector2 a, in UnityEngine.Vector2 b) {
        float num = a.x - b.x;
        float num2 = a.y - b.y;
        return (num * num + num2 * num2);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DistanceCheck(in UnityEngine.Vector2 a, in UnityEngine.Vector2 b, float max) {
        float num = a.x - b.x;
        float num2 = a.y - b.y;
        float dist = (num * num + num2 * num2);
        return dist < (max * max);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DistanceCheck_PreSqr(in UnityEngine.Vector2 a, in UnityEngine.Vector2 b, float max_squared) {
        float num = a.x - b.x;
        float num2 = a.y - b.y;
        float dist = (num * num + num2 * num2);
        return dist < max_squared;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DistanceCheck_PreSqr(in UnityEngine.Vector2 a, in UnityEngine.Vector2 b, float max_squared, out float actual_dist) {
        float num = a.x - b.x;
        float num2 = a.y - b.y;
        actual_dist = (num * num + num2 * num2);
        return actual_dist < max_squared;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DistanceCheck(in UnityEngine.Component ba, in UnityEngine.Component bb, float max) {
        if (ba == bb) return true;

        UnityEngine.Vector2 a = ba.transform.position;
        UnityEngine.Vector2 b = bb.transform.position;

        float num = a.x - b.x;
        float num2 = a.y - b.y;
        float dist = (num * num + num2 * num2);
        return dist < (max * max);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DistanceCheck(in Body ba, in SyncInfo bb, float max) {

        UnityEngine.Vector2 a = ba.transform.position;
        UnityEngine.Vector2 b = bb.go.transform.position;

        float num = a.x - b.x;
        float num2 = a.y - b.y;
        float dist = (num * num + num2 * num2);
        return dist < (max * max);
    }





    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 v3v2z(Vector2 a, float z) {
        return new Vector3(a.x, a.y, z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 v2getClosestPointOnLine(Vector2 a, Vector2 b, Vector2 point) {

        var AP = point - a;
        var AB = b - a;

        var dist = AB.sqrMagnitude;
        if (dist < 1E-05f) {
            return a;
        }
        var t = Mathf.Clamp01(Vector2.Dot(AP, AB) / dist);

        return a + AB * t;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void v2clampSquare(ref Vector2 vec2, float a) {
        if (vec2.x > a)
            vec2.x = a;
        if (vec2.y > a)
            vec2.y = a;
        if (vec2.x < -a)
            vec2.x = -a;
        if (vec2.y < -a)
            vec2.y = -a;
    }


}


