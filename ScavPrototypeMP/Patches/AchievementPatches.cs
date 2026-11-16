#if FULLGAME
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// host cheat commands set SteamManager.disableAchievements - stamp that onto every client
// and into the shared save so the whole lobby/save stays achievement-locked.
public static class AchievementBreakSync {
    public const string SaveHeaderKey = "CHEATED";
    const string AlertMsg = "<color=#ff006a>WARNING: This multiplayer save used a cheat command. Achievements are disabled until you restart the game.</color>";

    public static void ApplyLocal(bool showAlert) {
        if (SteamManager.disableAchievements) {
            return;
        }
        SteamManager.disableAchievements = true;
        if (!showAlert) {
            return;
        }
        // consoleScript.Alert() toggles the console open - fine for SP save load, bad when
        // every late joiner gets StartGameAnnouncement with the cheated flag.
        if (ConsoleScript.instance != null) {
            ConsoleScript.instance.LogToConsole(AlertMsg);
        }
        if (PlayerCamera.main != null) {
            PlayerCamera.main.DoAlert("Achievements disabled (multiplayer cheat/save).", important: true);
        }
    }

    // host -> everyone else (and optional one joiner)
    public static void Server_Broadcast(bool showAlert, List<knetid> only = null) {
        if (!Net.IsServer || !Net.IsRunning) {
            return;
        }
        SteamManager.disableAchievements = true;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_AchievementsDisabled);
        writer.Put(showAlert);
        if (only != null) {
            Net.Server_SendToClientsVeryReliable(writer, only);
        } else {
            Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
        }
    }

    // trailing flag after runsettings on StartGameAnnouncement (older clients ignore EndOfData)
    public static void WriteAnnouncementFlag(NetDataWriter writer) {
        writer.Put(SteamManager.disableAchievements);
        FullgameWorldCompat.WriteWorldExtras(writer);
        try {
            CharacterSelectWait.WriteSpeciesRoster(writer);
        } catch (System.Exception e) {
            Together.log.error("species roster skipped: " + e);
        }
    }

    public static void ReadAnnouncementFlag(NetDataReader reader) {
        if (reader.EndOfData) {
            return;
        }
        reader.Get(out bool cheated);
        if (cheated) {
            ApplyLocal(showAlert: true);
        }
        FullgameWorldCompat.ReadWorldExtras(reader);
        CharacterSelectWait.ReadSpeciesRoster(reader);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_AchievementsDisabled)]
    static void Client_AchievementsDisabled(knetid _, NetDataReader reader) {
        bool showAlert = true;
        if (!reader.EndOfData) {
            reader.Get(out showAlert);
        }
        ApplyLocal(showAlert);
    }
}

// stock calls SteamManager.TryUnlockAchievement from Body/Limb/item use with no owner check.
// in MP the host re-runs remotes' eats/updates, so those would unlock for the host (and relays
// for whoever simulates). Gate: only the local body may grant achievements.
[HarmonyPatch(typeof(SteamManager), nameof(SteamManager.TryUnlockAchievement))]
static class SteamManager_TryUnlockAchievement_LocalOnly_MultiplayerPatch {
    static bool Prefix(bool force) {
        if (!Net.IsRunning) {
            return true;
        }
        // tutorial / scripted force unlocks stay as-written (those paths are local scenes).
        if (force) {
            return true;
        }

        Body actor = ResolveActorBody();
        if (actor == null) {
            // ambient spawn counters etc. - attribute to the local camera body if any.
            if (PlayerCamera.main == null || PlayerCamera.main.body == null) {
                return false;
            }
            return true;
        }
        return Util.IsBodyLocal(actor);
    }

    // prefer the MP override Body.Update / BetterUseItem already select; else talker speaker.
    static Body ResolveActorBody() {
        CoUtils cu = CoUtils_instance_MultiplayerPatch.cur_override_instance;
        if (cu != null && cu.TryGetComponent(out Body fromOverride) && fromOverride != null) {
            return fromOverride;
        }
        if (SpeechCharContext.speaker != null) {
            return SpeechCharContext.speaker;
        }
        return null;
    }
}

// prosthetic / bone mend can run off medical paths that don't sit inside Body.Update.
[HarmonyPatch(typeof(Limb), nameof(Limb.AttachProsthetic))]
[HarmonyPatch(typeof(Limb), nameof(Limb.MendBone))]
static class Limb_AchievementOwner_MultiplayerPatch {
    internal static Body AttachingBody;

    static void Prefix(Limb __instance, ref CoUtils __state) {
        AttachingBody = __instance != null ? __instance.body : null;
        __state = null;
        if (!Net.IsRunning || __instance == null || __instance.body == null) {
            return;
        }
        __state = CoUtils_instance_MultiplayerPatch.cur_override_instance;
        __instance.body.GetCoUtilsInstance().SelectForMPOverride();
    }

    static void Postfix(ref CoUtils __state) {
        AttachingBody = null;
        if (!Net.IsRunning) {
            return;
        }
        CoUtils_instance_MultiplayerPatch.cur_override_instance = __state;
    }
}
#endif