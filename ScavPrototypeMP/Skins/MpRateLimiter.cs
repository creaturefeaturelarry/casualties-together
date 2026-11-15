using System;
using System.Collections.Generic;

// each block lasts longer than the last. the map is capped so fake client ids cannot grow it.
internal sealed class MpRateLimiter {
    internal const int MaxTrackedClients = 64;
    internal const float StrikeResetSeconds = 120f;

    internal static readonly float[] Penalties = { 10f, 60f, 300f, 900f };

    internal struct Result {
        internal bool Allowed;
        internal bool AcceptOnce;
        internal float RetryIn;
        internal int Strikes;
    }

    private struct ClientState {
        internal float WindowStart;
        internal int Count;
        internal int Strikes;
        internal float BlockedUntil;
    }

    private readonly Dictionary<uint, ClientState> _clients =
        new Dictionary<uint, ClientState>();
    private readonly int _maxEvents;
    private readonly float _seconds;

    internal MpRateLimiter(int maxEvents, float seconds) {
        _maxEvents = Math.Max(1, maxEvents);
        _seconds = Math.Max(0.1f, seconds);
    }

    internal Result Check(uint clientId, float now) {
        if (!_clients.TryGetValue(clientId, out ClientState state)) {
            if (_clients.Count >= MaxTrackedClients) {
                Prune(now);
                if (_clients.Count >= MaxTrackedClients) {
                    return new Result { RetryIn = Penalties[0] };
                }
            }

            state = new ClientState { WindowStart = now };
        }

        if (now < state.BlockedUntil) {
            return new Result {
                RetryIn = state.BlockedUntil - now,
                Strikes = state.Strikes
            };
        }

        if (now >= state.BlockedUntil + StrikeResetSeconds) {
            state.Strikes = 0;
        }

        if (now - state.WindowStart >= _seconds) {
            state.WindowStart = now;
            state.Count = 0;
        }

        if (state.Count < _maxEvents) {
            state.Count++;
            _clients[clientId] = state;
            return new Result { Allowed = true, Strikes = state.Strikes };
        }

        state.Strikes = Math.Min(state.Strikes + 1, Penalties.Length);
        state.BlockedUntil = now + Penalties[state.Strikes - 1];
        state.WindowStart = now;
        state.Count = 0;
        _clients[clientId] = state;

        return new Result {
            AcceptOnce = true,
            RetryIn = Penalties[state.Strikes - 1],
            Strikes = state.Strikes
        };
    }

    internal void Remove(uint clientId) {
        _clients.Remove(clientId);
    }

    internal void Clear() {
        _clients.Clear();
    }

    private void Prune(float now) {
        List<uint> expired = new List<uint>();
        foreach (KeyValuePair<uint, ClientState> pair in _clients) {
            if (now >= pair.Value.BlockedUntil &&
                now - pair.Value.WindowStart >= _seconds) {
                expired.Add(pair.Key);
            }
        }

        foreach (uint clientId in expired) {
            _clients.Remove(clientId);
        }
    }
}