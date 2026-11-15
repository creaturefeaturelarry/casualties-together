using System;

// no unity types, so tests can run the countdown.
internal sealed class MpCooldown {
    private float _until;

    internal bool IsActive(float now) {
        return now < _until;
    }

    internal float Remaining(float now) {
        return Math.Max(0f, _until - now);
    }

    internal int RemainingSeconds(float now) {
        return (int)Math.Ceiling(Remaining(now));
    }

    internal void Set(float now, float seconds) {
        _until = now + Math.Max(0f, seconds);
    }

    internal void Clear() {
        _until = 0f;
    }

    internal static string FormatDuration(int seconds) {
        if (seconds <= 0) {
            return "0 seconds";
        }

        if (seconds >= 60) {
            int minutes = (seconds + 59) / 60;
            return minutes + (minutes == 1 ? " minute" : " minutes");
        }

        return seconds + (seconds == 1 ? " second" : " seconds");
    }
}