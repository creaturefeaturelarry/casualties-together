using HarmonyLib;
using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using LiteNetLib;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;






[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.LoadAllMusic))]
public static class MP3Menu_LoadAllMusic_MultiplayerPatch {

    [HarmonyReversePatch]
    public static IEnumerator LoadAllMusic(object instance, string folderPath) {
        throw new NotImplementedException();
    }

    public static IEnumerator Patched_LoadAllMusic(MP3Menu __instance, string folderPath) {

        if (!Multiplayer.IsNetworkActiveAndIsClient()) {
            if (Net.IsServer) {
                Multiplayer.DoMultiplayerStatusMessageLog("SERVER: Loading custom music.");
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Loading custom music.");
            }
            yield return LoadAllMusic(__instance, folderPath);
        }

        if (MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance != null) {
            GameObject.Destroy(MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance);
            MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance = null;
        }

        if (!Multiplayer.IsNetworkActiveAndIsClient()) {
            if (Multiplayer.IsNetworkActiveAndIsServer()) {
                Multiplayer.DoMultiplayerStatusMessageLog("SERVER: Sending custom music list to players.");
                MP3PlayerServerAudioStreamer.Server_SendMusicList();
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Finished loading custom music.");
            }
        }
    }

    public static bool Prefix(MP3Menu __instance, string folderPath, ref IEnumerator __result) {
        __result = Patched_LoadAllMusic(__instance, folderPath);
        return false;
    }
}


[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.UpdateList))]
public static class MP3Menu_UpdateList_MultiplayerPatch {
    public static Item last_locally_used_mp3_player = null;

    public static bool Prefix(MP3Menu __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsServer) {
            return true;
        }
        if (!Multiplayer.rules.EnableMP3Sync) {
            return true;
        }
        // now here im totally a client

        if (MP3Menu.dropdownList == null) {
            // it must load the list

            Multiplayer.DoMultiplayerStatusMessageLog("CLIENT: Requesting server for the music list.");

            var writer = Net.CreateWriter(NetmsgId.SERVER_MP3RequestMusicList);

            writer.Put((int)1);

            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

            //let it temporarily just create the default sound list, otherwise it errors

            return true;
        }
        return false;
    }
}

[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.Play))]
public static class MP3Menu_Play_MultiplayerPatch {



    public static void Server_PlayThisSongOnThisMp3Player(AudioClip audio, Item mp3player) {
        var g = mp3player.GetOrAddComponent<MP3PlayerServerAudioStreamer>();
        g.Play(audio);
    }

    public static bool Prefix(MP3Menu __instance) {
        if (!Net.IsRunning || !Multiplayer.rules.EnableMP3Sync) {
            return true;
        }
        if (MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player != null) {
            var val = __instance.dropdown.value;

            if (Net.IsClient) {

                if (NetObjectRegistry.TryGetSyncInfoOrRegister(MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player, out var si)) {

                    var writer = Net.CreateWriter(NetmsgId.SERVER_MP3PlayCustomMusic);

                    writer.Put((int)val);
                    writer.Put(si.syncId);

                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                    // WRITER END;
                } else {
                    NetObjectRegistry.AlertObjectNotRegistered(true);
                }

            } else {

                var clip = MP3Menu.clips[val];

                Server_PlayThisSongOnThisMp3Player(clip, MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player);

            }

            return false;
        }
        //if (Util.IsInMainMenu()) return true;//idk

#if DEBUG || RELEASETEST || true
        Multiplayer.DoMultiplayerStatusMessageError("MP3Menu.Play -> Failed to play a song? Unknown mp3player item?????");
#endif
        //return false;
        return true; // let them replace background music with commands 
    }
}
[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.Exit))]
public static class MP3Menu_Exit_MultiplayerPatch {
    public static bool Prefix(MP3Menu __instance) {
        MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player = null;
        return true;
    }
}


public abstract class MP3PlayerAudioStreamPlayerBase : StreamedAudioOutput {
    new void Awake() {
        max_hear_distance = 82;
        is_voice = false;
        base.Awake();
    }
}


public class MP3PlayerAudioStreamPlayer_ClientOnly : MP3PlayerAudioStreamPlayerBase {
    void LateUpdate() {
        if (TimeTheBufferEnded > 30) {
            TimeTheBufferEnded = 20;
            Destroy(this);
        }
    }
}

// should be used only by server 
public class MP3PlayerServerAudioStreamer : MP3PlayerAudioStreamPlayerBase {


    public Item item => GetComponent<Item>();
    AudioSource musicplayersource;


    public List<float> pcm_buffer_from_the_filter_thing = new List<float>(4096);    // should be always mono, its in sample rate AudioSettings.outputSampleRate

    ushort target_samplerate = Voicechat.STANDART_FREQUENCIES.Last();

    AudioOpusEncoding encoder;

    public void CheckRequiredProcessing(AudioClip original) {
        target_samplerate = Mathf.Min(original.frequency, Voicechat.SAMPLE_RATE) > 32000 ? Voicechat.STANDART_FREQUENCIES.Last() : Voicechat.GetClosestSamplerate((ushort)original.frequency);

    }

    public void Play(AudioClip audio) {
        musicplayersource.Stop();
        CheckRequiredProcessing(audio);

        if (audio.channels == 0) {
            musicplayersource.clip = null;
            return;
        }

        musicplayersource.clip = audio;

        queued_resampled_samples.Clear();
        pcm_buffer_from_the_filter_thing.Clear();

        //last_timeSamples = 0;
        musicplayersource.Play();
    }

    //float last_time = 0;
    //int last_timeSamples = 0;

    Queue<float> queued_resampled_samples = new();

    void LateUpdate() {


        if (musicplayersource.isPlaying) {
            if ( SharedMain.LocalWorldIsGenerating ) {
                queued_resampled_samples.Clear();
                pcm_buffer_from_the_filter_thing.Clear();
                return;
            }

            if (!Multiplayer.rules.EnableMP3Sync) {
                Destroy(this);  
                return;
            }


            // check if it ran out of battery 
            if (item.condition <= 0) {
                musicplayersource.Stop();
                return;
            } else {
                // maybe make it use power to play music? but like very little tho
            }




            if (pcm_buffer_from_the_filter_thing.Count >= Voicechat.system_framesize) {

                var resampleddata = Voicechat.ResampleVoiceBlob(pcm_buffer_from_the_filter_thing, AudioSettings.outputSampleRate, target_samplerate);
                pcm_buffer_from_the_filter_thing.Clear();

                foreach (var item in resampleddata) {
                    queued_resampled_samples.Enqueue(item);
                }

            }


            var required_frame_size = Voicechat.CalculateFrameSizeForSamplerate(target_samplerate);
            if (queued_resampled_samples.Count > required_frame_size) {
                var frame = new float[required_frame_size];
                while (queued_resampled_samples.Count > required_frame_size) {

                    for (int i = 0; i < frame.Length; i++)
                        frame[i] = queued_resampled_samples.Dequeue();


                    if (Voicechat.mp3_listener_volume > 0 ) {
                        ReceiveVoiceBlob((ushort)target_samplerate, frame, true);
                    }


                    var plrs = ScavPlayer.GetPlayersInRadius(this.transform.position, audioSource.maxDistance);
                    plrs.Remove(ScavPlayer.LocalPlayer);

                    if (plrs.Count > 0 && NetObjectRegistry.TryGetSyncInfoOrRegister(gameObject,out var si)) {





                        //SEND IT
                        var voiceblob = encoder.Encode(frame, (ushort)target_samplerate);

                        if (voiceblob.Length > 0) {

                            var freq_index = (byte)Array.IndexOf(AudioOpusEncoding.STANDART_FREQUENCIES, (ushort)target_samplerate);

                            var target_client_ids = plrs.Select(x => x.playerId).ToList();


                            var writer = Net.CreateWriter(NetmsgId.CLIENT_StreamedAudioToClients);

                            writer.Put(si.syncId);
                            writer.Put(freq_index);
                            writer.PutBytesWithLength(voiceblob);

                            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: target_client_ids);

                            // WRITER END;
                        }


                    }
                }
            }

        } 

        if (AudioListener.volume == 0 && !warned_0_vol) {
            warned_0_vol = true;
            Multiplayer.DoMultiplayerStatusMessageError("SERVER: Can't stream mp3player music with 0 volume!!!!");
        }

    }
    static bool warned_0_vol = false;


    void OnDestroy() {
        if (musicplayersource != null)
            Destroy(musicplayersource.gameObject);

        encoder?.Dispose();
    }

    new void Awake() {
        if (Net.IsClient) {
            Destroy(this);
#if DEBUG || RELEASETEST
            log.error("DEV: MUSIC STREAMER WAS CREATED NOT ON SERVER !!! DELETING MYSELF!!!");
#endif
            return;
        }
        encoder = new AudioOpusEncoding();

        var go = new GameObject("mp3play");
        go.AddComponent<MP3PlayerAudioReader>().owner = this;
        musicplayersource = go.AddComponent<AudioSource>();
        musicplayersource.volume = 1;
        musicplayersource.loop = false;
        musicplayersource.minDistance = 9999999f;
        musicplayersource.maxDistance = 99999999f;
        musicplayersource.rolloffMode = AudioRolloffMode.Linear;
        musicplayersource.bypassEffects = false;
        musicplayersource.bypassListenerEffects = true;
        musicplayersource.bypassReverbZones = true;
        musicplayersource.dopplerLevel = 0;
        musicplayersource.spatialBlend = 0;
        //musicplayersource.transform.position = new Vector3(99999999,999999,9999999);

        base.Awake();

    }

    public class MP3PlayerAudioReader : MonoBehaviour {
        public float reader_volume = 0;
        public MP3PlayerServerAudioStreamer owner;
        void OnAudioFilterRead(float[] data, int channels) {
            try {
                if (!owner.musicplayersource.isPlaying) {
                    return;
                }

                if (owner.pcm_buffer_from_the_filter_thing.Count > Voicechat.system_framesize * 25) {
#if TESTING
                    if (log.verbose)
                        log.l("pcm_buffer_from_the_filter_thing is overflowing!!!!!!!!!!!!!");
#endif
                    return;
                }

                reader_volume = Voicechat.GetFrameMaxVol(data);

                float inv_volgetter = 1f / (AudioListener.volume == 0 ? 1f : AudioListener.volume);
                float inv_chan = 1f / (float)channels;

                int chan_count = 0;
                float cur_sample = 0;

                for (int i = 0; i < data.Length; i++) {

                    cur_sample += data[i];
                    chan_count++;
                    if (chan_count >= channels) {
                        owner.pcm_buffer_from_the_filter_thing.Add((cur_sample * inv_chan) * inv_volgetter);
                        chan_count = 0;
                        cur_sample = 0;
                    }

                    // and set it to 0 so that this audiosource doesnt do any sound
                    data[i] = 0;
                }
            }
            catch (Exception e) {
#if TESTING
                if (log.verbose)
                    Plugin.log.LogError("MP3 PLAYER OnAudioFilterRead:\n"+e.ToString());
#endif
            }


        }
    }


    
    internal static void Server_SendMusicList(IReadOnlyList<ScavPlayer> targets = null) {
        targets ??= ServerMain.AllPlayersExceptHost.ToList();

        var target_client_ids = targets.Select(x => x.playerId).ToList();

        // i was testing shi, TODO maybe use this actually
        //var w2 = new NetDataWriter(true,64);
        //w2.PutArray(MP3Menu.dropdownList.Select(x => x.text).ToArray());

        var writer = Net.CreateWriter(NetmsgId.CLIENT_MP3MenuServerIsSendingMusicList);

        
        writer.Put((int)MP3Menu.dropdownList.Count);
        foreach (var item in MP3Menu.dropdownList) {
            writer.Put(item.text);
        }

        writer.CompressWriter();

        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: target_client_ids);

        // WRITER END;
    }

    public static GameObject temp_loader_mp3menu_instance = null;
    public static bool unprivated_MP3Menu_loadingMusic {
        get {
            return (bool)Traverse.Create(typeof(MP3Menu)).Field("loadingMusic").GetValue();
        }
        set {
            Traverse.Create(typeof(MP3Menu)).Field("loadingMusic").SetValue(value);
        }
    }

    public static void Server_ForceLoadMusic() {
        if (!unprivated_MP3Menu_loadingMusic) {
            MP3Menu.dropdownList = null; // reset it to reload the list again

            temp_loader_mp3menu_instance = Utils.Create("Special/MP3SongSelect", PlayerCamera.main?.mainCanvas?.transform ?? Plugin.s.transform);
            temp_loader_mp3menu_instance.GetComponent<MP3Menu>().dropdown.enabled = false;
            temp_loader_mp3menu_instance.transform.position = new Vector3(99999, 99999, 99999);
            temp_loader_mp3menu_instance.transform.localScale = Vector3.one * 0.001f;
            Destroy(temp_loader_mp3menu_instance, 120);
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_MP3RequestMusicList)]
    static void Server_MP3RequestMusicList(knetid cid, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out int val);

        if (ScavPlayer.TryGetPlayerFromClientId(cid, out var plr) ) {
            if (MP3Menu.dropdownList == null) {
                Server_ForceLoadMusic();
            } else {
                Server_SendMusicList(new List<ScavPlayer> { plr });
            }
        }
    }
    [ServerReceiver((byte)NetmsgId.SERVER_MP3PlayCustomMusic)]
    static void Server_MP3PlayCustomMusic(knetid cid, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out int val);
        reader.Get(out knetid mp3plr_syncid);

        if (ScavPlayer.TryGetPlayerFromClientId(cid, out var plr) && plr.IsConscious()) {
            if (NetObjectRegistry.TryGetSyncInfo(mp3plr_syncid, out var si) && si.IsItem() && si.item.id == "mp3player" && ItemSync.CheckIfBodyReachThisItem(si,plr.body)) {
                if (val >= MP3Menu.clips.Count) {
                    return;// sus
                }
                var clip = MP3Menu.clips[val];  

                MP3Menu_Play_MultiplayerPatch.Server_PlayThisSongOnThisMp3Player(clip,si.item);  
            }
        }

    }




    [ClientReceiver((byte)NetmsgId.CLIENT_MP3MenuServerIsSendingMusicList)]
    static void Client_MP3MenuServerIsSendingMusicList(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        log.l("CLIENT: Received Music list.");
        reader = reader.DecompressReader();

        reader.Get(out int musiccount);
        MP3Menu.dropdownList = new List<TMP_Dropdown.OptionData>();
        for (int i = 0; i < musiccount; i++) {
            reader.Get(out string text);
            MP3Menu.dropdownList.Add(new TMP_Dropdown.OptionData(text));   
        }

        var prs = FindObjectOfType<MP3Menu>();
        if (prs != null) {
            prs.dropdown.ClearOptions();
            prs.dropdown.AddOptions(MP3Menu.dropdownList);
        }

        //reader.Dispose(); // because im using DecompressReader !! ( but fr i never tested without it and probably i dont wanna )

    }


    [ClientReceiver((byte)NetmsgId.CLIENT_StreamedAudioToClients)]
    static void Client_StreamedAudioToClients(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (Voicechat.mp3_listener_volume <= 0 || SharedMain.LocalWorldIsGenerating) {
            return;
        }

        reader.Get(out knetid source_syncid);

        //var data = packet.GetSBytes(); // old decoder
        //var frame = data.Select(x => (float)((float)x / (float)sbyte.MaxValue)).ToArray();

        if (NetObjectRegistry.TryGetSyncInfo(source_syncid, out var si)) {
            var go = si.go;

            reader.Get(out byte freq_index);
            var freq = AudioOpusEncoding.STANDART_FREQUENCIES[freq_index];

            reader.Get(out byte[] data);

            var streamoutput = go.GetOrAddComponent<MP3PlayerAudioStreamPlayer_ClientOnly>();

            if (!streamoutput.OpusDecode(freq, data, out var frame)) {
#if DEBUG || RELEASETEST
                Plugin.log.LogWarning($" MP3PLAY: CLIENT: Failed to decode opus??? ");
#endif 
                return;
            }

            streamoutput.ReceiveVoiceBlob(freq, frame.ToArray(),true);

        }// else {
         //   NetObjectRegistry.Client_RequestObjectInfo(source_syncid);
         //}
    }







}



internal class MP3PlayerMPPatches : CasualtiesTogetherSingleton {

    void KrokoshaSingletonEvent_OnSceneChange() {
        MP3Menu.clips?.Clear();
        MP3Menu.dropdownList?.Clear();
        MP3Menu.clips = null;
        MP3Menu.dropdownList = null;
    }

}
