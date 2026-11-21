using CUCoreLib.Networking;
using Newtonsoft.Json.Linq;
using System;
using System.Reflection;

// a locked client keeps the skin it already sent. a joiner gets one more change.
internal static class SkinLock {
    internal const string LockChannel = "spritereplacer.skin.lock";

    private static readonly SkinLockState State = new SkinLockState();

    internal static bool IsLocked => State.IsLocked;

    internal static bool HasLocalSkin {
        get => State.HasLocalSkin;
        set => State.HasLocalSkin = value;
    }

    internal static bool HadLocalSkin {
        get => State.HadLocalSkin;
        set => State.HadLocalSkin = value;
    }

    internal static bool CanChangeOnce => State.CanChangeOnce;

    internal static bool CanChangeLocal =>
        State.CanChangeLocal(MpBody.IsActive, MpSkinSync.IsCoolingDown);

    internal static void Toggle() {
        if (!MpBody.IsHost) {
            return;
        }

        SetLocked(!IsLocked);
    }

    internal static void SetLocked(bool locked) {
        if (!MpBody.IsHost) {
            return;
        }

        bool changed = State.SetLocked(locked);
        Log("host set locked=" + locked + ".");

        if (changed) {
            Announce(locked);
            MpSkinSync.OnLockChanged();
        }

        try {
            MultiplayerApi.Broadcast(LockChannel, Payload(false), false, true);
        } catch (Exception ex) {
            Log("broadcast failed: " + ex.Message);
        }

        SkinMenuUI.RefreshLockUi();
    }

    internal static void ApplyRemote(JToken token) {
        JObject payload = token as JObject;
        if (payload == null) {
            return;
        }

        JToken lockedToken = payload["locked"];
        if (lockedToken == null) {
            return;
        }

        bool allow = payload["allowOneChange"] != null &&
                     payload["allowOneChange"].Value<bool>();

        bool changed = State.ApplyRemote(lockedToken.Value<bool>(), allow);

        if (changed) {
            Announce(IsLocked);
        }

        if (CanChangeOnce) {
            SkinMenuUI.OpenPopupForChoice();
        }

        SkinMenuUI.RefreshLockUi();
    }

    internal static void ConsumeChoice() {
        if (!State.ConsumeChoice()) {
            return;
        }

        SkinMenuUI.RefreshLockUi();
    }

    internal static JObject Payload(bool allowOneChange, float retryIn = 0f,
        int strikes = 0, bool accepted = false) {
        JObject payload = new JObject {
            ["locked"] = IsLocked,
            ["allowOneChange"] = allowOneChange
        };

        if (retryIn > 0f) {
            payload["retryIn"] = retryIn;
            payload["strikes"] = strikes;
            payload["accepted"] = accepted;
        }

        return payload;
    }

    internal static void Reset() {
        if (!State.IsLocked && !State.HasLocalSkin && !State.CanChangeOnce) {
            return;
        }

        State.Reset();
        SkinMenuUI.RefreshLockUi();
    }

    private static void Announce(bool locked) {
        ChatMessage(locked
            ? "[Skin Replacer] The host locked all skins."
            : "[Skin Replacer] The host released the skin lock.");
    }

    internal static void AnnounceRateLimit(int seconds, int strikes, bool lastSkinShared) {
        string duration = MpCooldown.FormatDuration(seconds);
        string prefix = lastSkinShared
            ? "The last skin is shared. "
            : (strikes >= 2 ? "Repeated requests. " : "");
        ChatMessage("[Skin Replacer] " + prefix
            + "Skin changes paused for " + duration + ".");
    }

    internal static void AnnounceCacheCleared(int count) {
        if (!MpBody.IsActive) {
            return;
        }

        ChatMessage(count == 0
            ? "[Skin Replacer] The skin cache is empty."
            : "[Skin Replacer] Cleared " + count + " cached skins.");
    }

    private static void ChatMessage(string message) {
        try {
            Assembly assembly = MultiplayerApi.KrokMpAssembly;
            Type chat = assembly == null ? null : assembly.GetType("Together.Chat");
            if (chat == null) {
                return;
            }

            MethodInfo method = chat.GetMethod("LogMessage",
                new[] { typeof(string), typeof(string), typeof(bool).MakeByRefType() });
            if (method == null) {
                return;
            }

            object[] args = { message, string.Empty, false };
            method.Invoke(null, args);
        } catch (Exception ex) {
            Log("chat failed: " + ex.Message);
        }
    }

    private static void Log(string message) {
        if (SpriteReplacerPlugin.Log == null || string.IsNullOrEmpty(message)) {
            return;
        }
        // lock toggle is the only routine chatter we keep
        if (message.StartsWith("host set locked=", StringComparison.Ordinal)
            || message.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0) {
            SpriteReplacerPlugin.Log.LogInfo("[MP lock] " + message);
        }
    }
}