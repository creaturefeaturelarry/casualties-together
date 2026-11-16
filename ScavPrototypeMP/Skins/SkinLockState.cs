// lock flags only. chat and net live on SkinLock.
internal sealed class SkinLockState {
    internal bool IsLocked { get; private set; }
    internal bool HasLocalSkin { get; set; }
    internal bool HadLocalSkin { get; set; }
    internal bool CanChangeOnce { get; private set; }

    internal bool CanChangeLocal(bool multiplayerActive, bool rateLimited = false) {
        if (!multiplayerActive) {
            return true;
        }

        if (rateLimited) {
            return false;
        }

        if (!IsLocked) {
            return true;
        }

        if (CanChangeOnce) {
            return true;
        }

        return !HasLocalSkin && !HadLocalSkin;
    }

    internal bool SetLocked(bool locked) {
        bool changed = IsLocked != locked;
        IsLocked = locked;
        CanChangeOnce = false;
        return changed;
    }

    internal bool ApplyRemote(bool locked, bool allowOneChange) {
        bool wasLocked = IsLocked;
        IsLocked = locked;

        if (!IsLocked) {
            CanChangeOnce = false;
        } else if (allowOneChange) {
            CanChangeOnce = true;
        }

        return wasLocked != IsLocked;
    }

    internal bool ConsumeChoice() {
        if (!CanChangeOnce) {
            return false;
        }

        CanChangeOnce = false;
        return true;
    }

    internal void Reset() {
        IsLocked = false;
        HasLocalSkin = false;
        HadLocalSkin = false;
        CanChangeOnce = false;
    }
}