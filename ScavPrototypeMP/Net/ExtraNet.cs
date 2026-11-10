using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Linq;

namespace Together;





public static class ExtraNet {



    //AI generated
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> _fieldCache = new();
    private static readonly BindingFlags _flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static FieldInfo[] GetCachedFields(Type type) {
        return _fieldCache.GetOrAdd(type, t => t.GetFields(_flags).Where(x => x.FieldType.IsSimpleType()).ToArray());
    }

    // --- Write ---

    public static void PutReflected<T>(this NetDataWriter writer, T obj) {
        foreach (FieldInfo field in GetCachedFields(typeof(T)))
            writer.PutAny(field.GetValue(obj));
    }

    public static void PutReflected(this NetDataWriter writer, object obj, IEnumerable<FieldInfo> fields) {
        foreach (FieldInfo field in fields)
            writer.PutAny(field.GetValue(obj));
    }

    // --- Read ---

    public static T GetReflected<T>(this NetDataReader reader) where T : new() {
        var obj = new T();
        foreach (FieldInfo field in GetCachedFields(typeof(T))) {
            reader.GetAny(out object value, field.FieldType);
            field.SetValue(obj, value);
        }
        return obj;
    }

    public static void GetReflected<T>(this NetDataReader reader, out T obj) where T : new() {
        obj = reader.GetReflected<T>();
    }

    public static void GetReflected<T>(this NetDataReader reader, T obj, IEnumerable<FieldInfo> fields) {
        foreach (FieldInfo field in fields) {
            reader.GetAny(out object value, field.FieldType);
            field.SetValue(obj, value);
        }
    }

}



public static class ExtraNetBoolPacked {
    //AI generated
    private static readonly ConcurrentDictionary<Type, FieldSet> _cache = new();
    private static readonly BindingFlags _flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private struct FieldSet {
        public FieldInfo[] BoolFields;
        public FieldInfo[] OtherFields;
    }

    private static FieldSet GetCached(Type type) {
        return _cache.GetOrAdd(type, t => {
            var all = t.GetFields(_flags);
            var bools = new List<FieldInfo>();
            var others = new List<FieldInfo>();
            foreach (var f in all) {
                if (!f.FieldType.IsSimpleType()) {

                    continue;
                }
                if (f.FieldType == typeof(bool))
                    bools.Add(f);
                else
                    others.Add(f);
            }
            return new FieldSet { BoolFields = bools.ToArray(), OtherFields = others.ToArray() };
        });
    }

    // --- Write ---

    public static void PutReflected<T>(NetDataWriter writer, T obj) {
        var set = GetCached(typeof(T));
        foreach (var f in set.OtherFields)
            writer.PutAny(f.GetValue(obj));

        if (set.BoolFields.Length > 0)
            PutPackedBits(writer, set.BoolFields, obj);
    }

    public static void PutReflected(NetDataWriter writer, object obj, IEnumerable<FieldInfo> fields) {
        var bools = new List<FieldInfo>();
        var others = new List<FieldInfo>();
        foreach (var f in fields) {
            if (f.FieldType == typeof(bool))
                bools.Add(f);
            else
                others.Add(f);
        }

        foreach (var f in others)
            writer.PutAny(f.GetValue(obj));

        if (bools.Count > 0)
            PutPackedBits(writer, bools, obj);
    }

    // --- Read ---

    public static T GetReflected<T>(NetDataReader reader) where T : new() {
        var obj = new T();
        var set = GetCached(typeof(T));
        foreach (var f in set.OtherFields) {
            reader.GetAny(out object value, f.FieldType);
            f.SetValue(obj, value);
        }
        if (set.BoolFields.Length > 0)
            GetPackedBits(reader, set.BoolFields, obj);
        return obj;
    }

    public static void GetReflected<T>(NetDataReader reader, out T obj) where T : new() {
        obj = reader.GetReflected<T>();
    }

    public static void GetReflected<T>(NetDataReader reader, T obj, IEnumerable<FieldInfo> fields) {
        var bools = new List<FieldInfo>();
        var others = new List<FieldInfo>();
        foreach (var f in fields) {
            if (f.FieldType == typeof(bool))
                bools.Add(f);
            else
                others.Add(f);
        }

        foreach (var f in others) {
            reader.GetAny(out object value, f.FieldType);
            f.SetValue(obj, value);
        }
        if (bools.Count > 0)
            GetPackedBits(reader, bools, obj);
    }

    // --- Bit packing helpers ---

    private static void PutPackedBits(NetDataWriter writer, List<FieldInfo> boolFields, object obj) {
        int count = boolFields.Count;
        int byteCount = (count + 7) / 8;
        byte[] bits = new byte[byteCount];

        for (int i = 0; i < count; i++) {
            if ((bool)boolFields[i].GetValue(obj))
                bits[i / 8] |= (byte)(1 << (i % 8));
        }

        writer.Put(bits);
    }

    private static void PutPackedBits(NetDataWriter writer, FieldInfo[] boolFields, object obj) {
        int count = boolFields.Length;
        int byteCount = (count + 7) / 8;
        byte[] bits = new byte[byteCount];

        for (int i = 0; i < count; i++) {
            if ((bool)boolFields[i].GetValue(obj))
                bits[i / 8] |= (byte)(1 << (i % 8));
        }

        writer.Put(bits);
    }

    private static void GetPackedBits(NetDataReader reader, FieldInfo[] boolFields, object obj) {
        int count = boolFields.Length;
        int byteCount = (count + 7) / 8;
        byte[] bits = new byte[byteCount];
        reader.Get(out bits);

        for (int i = 0; i < count; i++) {
            bool val = (bits[i / 8] & (1 << (i % 8))) != 0;
            boolFields[i].SetValue(obj, val);
        }
    }

    private static void GetPackedBits(NetDataReader reader, List<FieldInfo> boolFields, object obj) {
        int count = boolFields.Count;
        int byteCount = (count + 7) / 8;
        byte[] bits = new byte[byteCount];
        reader.Get(out bits);

        for (int i = 0; i < count; i++) {
            bool val = (bits[i / 8] & (1 << (i % 8))) != 0;
            boolFields[i].SetValue(obj, val);
        }
    }
}



