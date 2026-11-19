using System.Collections.Generic;
using UnityEngine;

// tables keyed by GetInstanceID() outlive the objects. these drop the keys whose object is gone, and only once a
// table has grown past a threshold that follows its live size, so the scan over all objects of the type is rare.
internal static class StaleIds {
    internal const int FirstThreshold = 8;

    internal static HashSet<int> LiveIds<TObject>() where TObject : UnityEngine.Object {
        HashSet<int> live = new HashSet<int>();
        foreach (TObject item in Resources.FindObjectsOfTypeAll<TObject>()) {
            live.Add(item.GetInstanceID());
        }

        return live;
    }

    internal static void Prune<TObject, TValue>(Dictionary<int, TValue> table, ref int threshold)
        where TObject : UnityEngine.Object {
        if (table.Count < threshold) {
            return;
        }

        RemoveMissing(table, LiveIds<TObject>());
        threshold = Mathf.Max(FirstThreshold, table.Count * 2);
    }

    internal static void Prune<TObject>(HashSet<int> table, ref int threshold)
        where TObject : UnityEngine.Object {
        if (table.Count < threshold) {
            return;
        }

        HashSet<int> live = LiveIds<TObject>();
        table.RemoveWhere(id => !live.Contains(id));
        threshold = Mathf.Max(FirstThreshold, table.Count * 2);
    }

    internal static void RemoveMissing<TValue>(Dictionary<int, TValue> table, HashSet<int> live) {
        List<int> gone = new List<int>();
        foreach (int id in table.Keys) {
            if (!live.Contains(id)) {
                gone.Add(id);
            }
        }

        foreach (int id in gone) {
            table.Remove(id);
        }
    }
}
