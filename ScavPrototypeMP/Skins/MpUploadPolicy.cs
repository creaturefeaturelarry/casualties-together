using System.Collections.Generic;

// a locked client gets one upload, then one extra. later uploads wait for the lock to change.
internal sealed class MpUploadPolicy {
    internal enum Decision {
        Reject,
        Accept,
        AcceptOnce
    }

    private readonly HashSet<uint> _allowance = new HashSet<uint>();
    private readonly HashSet<uint> _seen = new HashSet<uint>();

    internal Decision Check(uint clientId, bool locked, bool hasSkin) {
        if (!locked) {
            return Decision.Accept;
        }

        if (!_seen.Contains(clientId)) {
            _seen.Add(clientId);
            if (!hasSkin) {
                _allowance.Add(clientId);
                return Decision.Accept;
            }
        }

        if (_allowance.Remove(clientId)) {
            return Decision.AcceptOnce;
        }

        return Decision.Reject;
    }

    internal bool HasAllowance(uint clientId) {
        return _allowance.Contains(clientId);
    }

    internal void MarkSeen(uint clientId) {
        if (clientId != 0) {
            _seen.Add(clientId);
        }
    }

    internal void Clear() {
        _allowance.Clear();
        _seen.Clear();
    }
}