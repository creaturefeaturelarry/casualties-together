

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Together;








// it actually just deletes oldest items when the capacity has gone max
public class MaxCapacityQueue<T> {
    readonly Queue<T> queue = new();
    private int _capacity = 0;

    public MaxCapacityQueue(int capacity) {
        _capacity = capacity;
    }

    public void Enqueue(T val) {
        queue.Enqueue(val);

        while (queue.Count > _capacity) {
            queue.Dequeue();
        }
    }
    public T Dequeue() {
        return queue.Dequeue();
    }
    public T ElementAt(int i) {
        return queue.ElementAt(i);
    }
    public void Clear() {
        queue.Clear();
    }
    public bool Contains(in T value) {
        return queue.Contains(value);
    }
    public Queue<T> JustGiveTheQueue() {
        return queue;
    }
    public int Count => queue.Count;
}





// this is ai generated
public class TwoWayDictionary<T1, T2> {
    private readonly Dictionary<T1, T2> _forward = new();
    private readonly Dictionary<T2, T1> _reverse = new();

    public void Add(T1 key, T2 value) {
        if (_forward.ContainsKey(key) || _reverse.ContainsKey(value))
            throw new ArgumentException("Duplicate key or value.");

        _forward.Add(key, value);
        _reverse.Add(value, key);
    }

    public bool TryGet(T1 key, out T2 value) => _forward.TryGetValue(key, out value);
    public bool TryGet(T2 value, out T1 key) => _reverse.TryGetValue(value, out key);

    public T2 GetValueSafe(T1 key   ) => _forward.GetValueSafe(key);
    public T1 GetValueSafe(T2 value) => _reverse.GetValueSafe(value);
    

    public T2 this[T1 key] {
        get {
             return _forward[key];
        }
        set {
            Remove(key);
            Remove(value);
            Add(key, value);
        }
    }

    public T1 this[T2 key2] {
        get {
            return _reverse[key2];
        }
        set {
            this[value] = key2;
        }
    }

    //public bool Remove(T1 key) => RemoveByFirst(key);
    //public bool Remove(T2 key) => RemoveBySecond(key);

    public bool Remove(T1 key) {
        if (_forward.TryGetValue(key, out var value)) {
            _forward.Remove(key);
            if (_reverse.TryGetValue(value, out var reverseKey) &&
                EqualityComparer<T1>.Default.Equals(reverseKey, key)) {
                _reverse.Remove(value);
            }
            return true;
        }
        return false;
    }

    public bool Remove(T2 value) {
        if (_reverse.TryGetValue(value, out var key)) {
            _reverse.Remove(value);
            if (_forward.TryGetValue(key, out var forwardValue) &&
                EqualityComparer<T2>.Default.Equals(forwardValue, value)) {
                _forward.Remove(key);
            }
            return true;
        }
        return false;
    }

    public void Clear() {
        _forward.Clear();
        _reverse.Clear();
    }

    public int Count => _forward.Count;
}














