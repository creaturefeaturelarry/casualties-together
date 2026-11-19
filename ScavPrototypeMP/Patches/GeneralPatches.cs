using CasualtiesTogetherUtils;
using DiscordRPC;
using DiscordRPC.Logging;
using DiscordRPC.Message;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace Together;







[HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.Update))]
static class PauseHandler_Update_MultiplayerPatch {
    static void Prefix(PauseHandler __instance, ref float __state) {
        __state = Time.timeScale;
    }
    static void Postfix(PauseHandler __instance, ref float __state) {
        if (Net.IsRunning) {
            Time.timeScale = __state;

            __instance.background.color *= 0.5f;
        }
    }
}





[HarmonyPatch(typeof(Locale), nameof(Locale.LoadLanguage))]
static class Locale_LoadLanguage_MultiplayerPatch {
    static void Postfix() {
        // make the local text for keybinds work
        foreach (var keybind_label in Lang.EN.Where(x => x.Key.StartsWith("gameset_"))) {
            var key = "gameset" + keybind_label.Key.TrimStart("gameset_");
            if (!Locale.currentLang.other.ContainsKey(key)) {
                Locale.currentLang.other[key] = keybind_label.Value;
            }
        }
    }
}

[HarmonyPatch(typeof(MusicManager), nameof(MusicManager.Start))]
public static class MusicManager_Start_MultiplayerPatch {

    static float GetMusicVolumeSetting() {
        try {
            return Settings.Get<SettingFloat>("musicvolume").value;
        } catch (Exception e) {
#if TESTING
            log.error(e);
#endif
        }
        return 1;
    }

    public static float musicvolume {
        get {
            return GetMusicVolumeSetting();
        }
        set {
            var set = Settings.Get<SettingFloat>("musicvolume");
            set.value = value;
            set.Apply();
        }
    }
    public static AudioSource source;

    public static float musicsources_volume {
        get {
            return musicvolume;
        }
        set {
            if (MusicManager.main != null) {
                MusicManager.main.origVolume = (value);
            }
            if (PreRunScript.instance != null) {
                PreRunScript.instance.menuSource.volume = value * 0.6666f;
            }
        }
    }

    static void Postfix(MusicManager __instance) {
        source = __instance.GetComponent<AudioSource>();
    }
}





[HarmonyPatch(typeof(RPCManager), nameof(RPCManager.GetCurrentRichPresence))]
static class RPCManager_GetCurrentRichPresence_MultiplayerPatch {
    struct dffdsfdsfdssfs {
        public bool aaaaaaaaaaaaaaaa;
        public bool bbbbbbbbbbbbbbbb;
    }

    static void Prefix(RPCManager __instance, ref dffdsfdsfdssfs __state) {

    }

    static double someTimerIdkForWhat = 0;
    static void Postfix(RPCManager __instance, ref dffdsfdsfdssfs __state, ref RichPresence __result) {


        var issteam = Net.TryGetSteamTransport(out var tsteam);
        var ask_to_join = (KSteam.CURRENT_LOBBY.locked || Net.CurrentServerInfo.midjoin_lock_active);
        var is_joinable__do_the_button = issteam;
        if (is_joinable__do_the_button) {
            if (Net.IsClient) {
                is_joinable__do_the_button = !KSteam.CURRENT_LOBBY.locked && !Net.CurrentServerInfo.midjoin_lock_active;
            } else {
                // always allow the button for the server owner 
            }
        }
        if (!UIBullshit.DISCORD_ALLOWJOINBUTTON) {
            is_joinable__do_the_button = false;
        }


        if (is_joinable__do_the_button) {
            var secret = KSteam.CURRENT_LOBBY.lobby_steamID.ToString() + ":" + Together.Multiplayer.Server_GetJoinSecret();
            __result.Secrets = new Secrets() {
                JoinSecret = secret, // when secret is missing, discord will not show join button
            };
#if !RELEASE 
            log.l($"CREATED SECRET: {secret}");
#endif   
        } else {
            ask_to_join = true;
        }

        if (UIBullshit.DISCORD_ONLYASKTOJOINBUTTON) {
            ask_to_join = true;
        }

        if (Net.IsRunning) {
            if (String.IsNullOrEmpty(__result.State)) {
                if (ScavPlayer.ClientIdToPlayerDict.Count <= 1) {
                    __result.State = "Waiting for players...";
                } else {
                    if (Util.IsInMainMenu()) {
                        //__result.State = "Wa";
                    } else {
                        __result.State = "Playing Multiplayer";
                    }
                }
            } else {
                if (ScavPlayer.TryGetLocalNetBody(out var nb) && nb.body.conscious) {
                    if (Util.IsInWoundView() && WoundView.view.body != null && WoundView.view.body.TryGetNetBody(out var pacient) && !pacient.IsLocal) {
                        __result.State = $"Treating {pacient.bodyName}";
                    }
                }
            }
            var t = Time.realtimeSinceStartupAsDouble;
            var diff = t - someTimerIdkForWhat;

            if (Util.IsInMainMenu()) {

                string details = "Multiplayer";
                if (Net.TryGetSteamTransport(out var ts) && Net.CurrentServerInfo.IsJoinable() && !KSteam.CURRENT_LOBBY.locked) {
                    if (Net.IsServer) {
                        details = $"Hosting server: {Net.CurrentServerInfo.name}";
                    } else {
                        details = $"In a server: {Net.CurrentServerInfo.name}";
                    }
                } else {
                    details = Net.IsServer ? "Hosting server" : "In Lobby";
                }
                __result.Details = $"{details}";

                if (diff > 20) {
                    someTimerIdkForWhat = t;
                }
            } else {
                var fgdfdgssdf = __result.Details;
                __result.Details = __result.State;
                __result.State = fgdfdgssdf;
            }

            __result.Party = new Party() {
                ID = issteam ? ("steam_" + KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID.ToString("X")) : ("directconnect_" + Net.CurrentServerInfo.name.StableHash().ToString("X")),
                Size = ScavPlayer.ClientIdToPlayerDict.Count,
                Max = Net.CurrentServerInfo.plr_max,
                Privacy = ask_to_join ? Party.PrivacySetting.Private : Party.PrivacySetting.Public,
            };

            __result.Timestamps = RPCManager_Initialize_MultiplayerPatch.rpcStartTime;
        }
    }
}



[HarmonyPatch(typeof(RPCManager), nameof(RPCManager.Initialize))]
internal static class RPCManager_Initialize_MultiplayerPatch {
    struct unusedStateData {
        public bool aaaaaaaaaaaaaaaa;
        public bool bbbbbbbbbbbbbbbb;
    }
    internal static Timestamps rpcStartTime = Timestamps.Now;

    public static void QuickReset() {
        try {
            if (RPCManager.instance != null && RPCManager.instance.client != null) {
                RPCManager.instance.client.ClearPresence();
                RPCManager.instance.timer = 0;
            }
        } catch (Exception e) {
            log.error(e);
        }
    }

    public static string BetterDiscordUserToString(User dsusr) {
        if (String.IsNullOrWhiteSpace(dsusr.DisplayName)) {
            return $"{dsusr.Username} - {dsusr.ID}";
        }
        return $"{dsusr.DisplayName} - {dsusr.Username} - {dsusr.ID}";
    }

    static bool Prefix(RPCManager __instance, ref unusedStateData __state) {
        rpcStartTime = Timestamps.Now;
        __instance.client = new DiscordRpcClient("1506330530394804254");

        __instance.client.RegisterUriScheme();

        // https://lachee.github.io/discord-rpc-csharp/articles/features/joining.html?q=join 
        __instance.client.Subscribe(DiscordRPC.EventType.JoinRequest);
        __instance.client.OnJoinRequested += async (sender, args) => {

#if TESTING
            log.l($"DiscordRPC: OnJoinRequested called - user:{args.User}");
#else
            log.l($"DiscordRPC: OnJoinRequested called");
#endif

            if (Net.IsRunning && Net.IsServer) { // DiscordRPC cant check if the user is a friend 
                var userTostring = args.User.ToString();
                //if (!string.IsNullOrEmpty(DisplayName)) {
                //    userTostring= DisplayName;
                //}
                //if (Discriminator != 0) {
                //    userTostring= Username + "#" + Discriminator.ToString("D4");
                //}
                //userTostring= Username;

                var prompt = new UIChoicePrompt.Prompt {
                    text = String.Format(Lang.Get("discord_joinrequesttext"), $"{userTostring}"), // Lang.Get("discord_joinrequest", $"{args.User} wants to join your game.")
                    name = Lang.Get("discord_joinrequest"),
                    size = new Vector2(0.4f, 0.18f),
                    choices = new UIChoicePrompt.Prompt.PromptChoice[] {
                        new UIChoicePrompt.Prompt.PromptChoice {
                            text = Lang.Get("discord_joinallow"),
                            action = () => {
                                log.l($"DiscordRPC: Accepted Join Request for user: {RPCManager_Initialize_MultiplayerPatch.BetterDiscordUserToString(args.User)}");
                            }
                        },
                        new UIChoicePrompt.Prompt.PromptChoice {
                            text = Lang.Get("discord_joindecline"),
                            action = () => {
                                log.l($"DiscordRPC: Declined Join Request for user: {RPCManager_Initialize_MultiplayerPatch.BetterDiscordUserToString(args.User)}");
                            }
                        },
                    },
                };

                await UIChoicePrompt.DoPromptAsync(prompt);

                if (prompt.chosenchoice == 0) {
                    __instance.client.Respond(args, true);
                } else {
                    __instance.client.Respond(args, false);
                }

            } else {
#if TESTING
                log.error("RECEIVED ASK TO JOIN AS A CLIENT??? SHOULDN'T HAPPEN!!!");
#endif
                __instance.client.Respond(args, false);
            }
        };
        __instance.client.Subscribe(DiscordRPC.EventType.Join);
        __instance.client.OnJoin += (sender, e) => {
#if TESTING
            log.l($"DiscordRPC: OnJoin called - Secret: {e.Secret}");
#else
            log.l($"DiscordRPC: OnJoin called ");
#endif
            if (!Multiplayer.IsNetworkActiveOrIsInGame()) {
                var splits = e.Secret.Split(':');
                Multiplayer.CLIENT_JOIN_SECRET = splits[1];
                TransportSteamworks.OnWantToJoinLobby(ulong.Parse(splits[0]));
            }
            // else maybe show an alert?
        };


        __instance.client.OnReady += delegate (object sender, ReadyMessage e) {
            if (log.verbose) log.l($"Discord RPC Ready: {RPCManager_Initialize_MultiplayerPatch.BetterDiscordUserToString(e.User)}");
        };
#if !RELEASE
        __instance.client.Logger = new ConsoleLogger {
            Level = LogLevel.Warning
        };
        __instance.client.OnError += delegate (object sender, ErrorMessage e) {
            log.error("Discord RPC Error: " + e.Message);
        };
        __instance.client.OnConnectionFailed += delegate (object sender, ConnectionFailedMessage e) {
            log.error("Discord RPC OnConnectionFailed: " + e);
        };
#else
        __instance.client.OnError += delegate (object sender, ErrorMessage e) {
            Debug.LogError("Discord RPC Error: " + e.Message);
        };
#endif
        var res = __instance.client.Initialize();
        if (res) {
            __instance.client.SetPresence(__instance.GetCurrentRichPresence());
        } else {
            log.error("Failed to initialize discord rpc");
        }

        return false;
    }
}








[HarmonyPatch(typeof(Observer), nameof(Observer.RolledLastStand))]
static class Observer_RolledLastStand_MultiplayerPatch {
    static bool Prefix(Observer __instance) {
        try {
            if (Util.GetLocalBody().lastStandTime == 300) {
                return true;
            } else {
                return false;
            }
        } catch (Exception e) {
            log.error(e);
        }
        return true;
    }
}

[HarmonyPatch(typeof(Observer), nameof(Observer.GunSuicide))]
static class Observer_GunSuicide_MultiplayerPatch {
    static bool Prefix(Observer __instance) {
        try {
            if (Util.GetLocalBody().totalHappiness <= -90) {
                return true;
            } else {
                return false;
            }
        } catch (Exception e) {
            log.error(e);
        }
        return true;
    }
}





[HarmonyPatch(typeof(Sound), nameof(Sound.Play), new System.Type[] { typeof(string), typeof(Vector2), typeof(bool), typeof(bool), typeof(Transform), typeof(float), typeof(float), typeof(bool), typeof(bool) })]
static class Sound_Play_MultiplayerPatch {

    public static bool is_the_fucking_updateheart_shit = false;
    public static bool force = false;

    public static float nontwoDimensionalvolumemultiplier = 1.5f;

    static bool Prefix(ref string clip, ref Vector2 pos, ref bool twoDimensional, ref bool pitchShift, ref Transform follow, ref float volume, float pitch, bool noReverb, bool ignoreMixer) {

        if (force) {
            force = false;
            return true;
        }

        if (!Net.IsRunning) return true;


        if (clip == null) { // cuz clip is gonna be null anyway
            return false;
        }

        if (is_the_fucking_updateheart_shit) {
            is_the_fucking_updateheart_shit = false;

            return false;
        }
        // check if im actually depressed, cuz this sound is played only from selfharm
        if (clip == "harmSting" && Util.TryGetLocalBody(out var b) &&
            b.happiness > -70 // OG: SelfHarmer.Update  V520dev , first lines 
        ) {

            return false;
        }

        if ((clip == "caveticks") || (clip == "headhit" && !Limb_ImpactDamage_MultiplayerPatch.last_impact_is_local_body)) {
            return false;
        }

        var og_twoDimensional = twoDimensional;

        if (clip.StartsWith("exert") || clip == "Saw") {
            // position is already provided
            twoDimensional = false;
        } else if (WorldGeneration_CreateExplosion_MultiplayerPatch.is_inside_CreateExplosion && WorldGeneration_CreateExplosion_MultiplayerPatch.last_explosion_params != null && WorldGeneration_CreateExplosion_MultiplayerPatch.last_explosion_params.sound == clip) {
            pos = Vector2.LerpUnclamped(WorldGeneration_CreateExplosion_MultiplayerPatch.last_explosion_params.position, Camera.main.transform.position, 0.8f);
            twoDimensional = false;
        } else if (clip == "fireworkpop") {
            var campos = Camera.main.transform.position;
            if (pos.y > campos.y) {
                pos = new Vector2(Mathf.Lerp(pos.x, campos.x, 0.7f), Mathf.Lerp(pos.y, campos.y, 0.87f));
            }
            volume *= nontwoDimensionalvolumemultiplier;
            twoDimensional = false;
        }

        if (!twoDimensional && og_twoDimensional) {
            volume *= nontwoDimensionalvolumemultiplier;
        }
        return true;
    }
}


[HarmonyPatch(typeof(Sound), nameof(Sound.Play), new System.Type[] { typeof(AudioClip), typeof(Vector2), typeof(bool), typeof(bool), typeof(Transform), typeof(float), typeof(float), typeof(bool), typeof(bool) })]
static class Sound_Play2_MultiplayerPatch {
    static float default_max_range = 160;
    static float default_max_range_sqr = default_max_range * default_max_range;
    public static bool force = false;


    [HarmonyReversePatch]
    public static AudioSource Play(AudioClip clip, Vector2 pos, bool twoDimensional, bool pitchShift, Transform follow, float volume, float pitch, bool noReverb, bool ignoreMixer) {
        throw new System.NotImplementedException();
    }

    static bool Prefix(AudioClip clip, ref Vector2 pos, ref bool twoDimensional, bool pitchShift, Transform follow, float volume, float pitch, bool noReverb, bool ignoreMixer, ref AudioSource __result) {

        if (force) {
            force = false;
            return true;
        }

        if (!Net.IsRunning) return true;

        if (clip == null) { // thats what OG does
            return false;
        }

        var og_twoDimensional = twoDimensional;

        if (twoDimensional) {
            if (Body_StartClimbing_MultiplayerPatch.last_interacted_climbable != null && Body_StartClimbing_MultiplayerPatch.last_interacted_climbable.climbSounds != null && Body_StartClimbing_MultiplayerPatch.last_interacted_climbable.climbSounds.Contains(clip)) {
                if (Body_StartClimbing_MultiplayerPatch.last_interacted_body != null && !Body_StartClimbing_MultiplayerPatch.last_interacted_body.IsBodyLocal()) {
                    pos = Body_StartClimbing_MultiplayerPatch.last_interacted_body.transform.position;
                    twoDimensional = false;
                } else {
                    return true;
                }
            } else if (
                (clip.name.Contains("gun") || clip.name.StartsWith("shot") || clip.name.StartsWith("pistol") || clip.name.StartsWith("rifle"))
                && GunScript_Update_MultiplayerPatch.current_executing_gun != null
            ) {
                // position is already provided

                if (GunScript_Update_MultiplayerPatch.current_executing_gun.TryGetComponent(out TrackerGunScript gst) && gst.body.IsBodyLocal()) {

                    twoDimensional = true; // since the body is local, hear this in its full form ig
                } else {
                    if (clip.name.EndsWith("shot")) {
                        pos = Vector2.LerpUnclamped(GunScript_Update_MultiplayerPatch.current_executing_gun.transform.position, Camera.main.transform.position, 0.3f);
                    }
                    twoDimensional = false;
                }

            } else {
                return true;
            }
        } else {
            if (Util.IsInWorld()) {
                var pant = PlayerCamera.main.body.GetPantSound();
                if (pant.barkSounds.Contains(clip)) {
                    pos = Vector2.LerpUnclamped(pos, Camera.main.transform.position, 0.7f);
                    twoDimensional = false;
                }
            }

        }


        var should_be_less_dist = true;

        if (should_be_less_dist) {
            var dist = KM.DistanceSqr(pos, Camera.main.transform.position);
            var volscale =
              1f;

            if (!twoDimensional && og_twoDimensional) {
                volume *= Sound_Play_MultiplayerPatch.nontwoDimensionalvolumemultiplier;
            }

            if (dist < default_max_range_sqr) {
                pos = Vector2.LerpUnclamped(pos, Camera.main.transform.position, 0.3f);

                var s = Play(clip, pos, twoDimensional, pitchShift, follow, volume * volscale, pitch, noReverb, ignoreMixer);

                __result = s;
            }
            return false;
        }


        return true;
    }
}



