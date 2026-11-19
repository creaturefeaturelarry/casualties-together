
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;
using KrokV5Optimization;

namespace Together;




public class StreamedAudioOutput : MonoBehaviour {

    #region COOL_AUDIO_EFFECTS_PARAMETERS

    protected bool is_voice = false; // if the sound effect should state that this is a voice from a mouth
    protected bool speech_impaired = false;
    protected bool is_body_alive = false;
    public Body body = null;

    #endregion

    public float max_hear_distance = 82;


    public const float PREBUFFER_LATENCY = 0.1f;
    public int MIN_SAMPLECOUNT_PREBUFFERED => (int)(PREBUFFER_LATENCY * (float)clip.frequency);

    internal int SAMPLE_RATE => clip.frequency;
    internal int FRAME_SIZE => Voicechat.CalculateFrameSizeForSamplerate(SAMPLE_RATE);


    public float cur_volume_output = 0;
    protected float _real_custom_volume = 1f;
    public float custom_volume_set {
        get {
            return _real_custom_volume;
        }
        set {
            _real_custom_volume = Math.Max(0, value);
        }
    }

    public AudioLowPassFilter lowpass { get; protected set; }
    public AudioSource audioSource { get; protected set; }
    internal AudioClip clip;

    public int sample_rate { get; private set; }
    public int half_sample_rate => (int)(sample_rate * 0.5f);
    public int quarter_sample_rate => (int)(sample_rate * 0.25f);
    internal float vc_lastsample1;
    internal float vc_lastsample2;
    internal int vc_endpos;
    internal int vc_buffered_amount;

    protected float TimeTheBufferEnded = 0;
    protected float TimeTheBufferFirstReceivedBlob = 0;
    internal MaxCapacityQueue<float> vc_buffer = new(Voicechat.DEFAULT_SAMPLE_RATE);

    internal bool force_no_effects = false;

    protected static float[] opus_decode_buffer = new float[2048];
    OpusDecoder opus_decoder = null;



    internal void EnsureOpusDecoder() {
        if (opus_decoder == null || opus_decoder.GetSampleRate() != sample_rate) {
            opus_decoder?.Dispose();
            opus_decoder = new OpusDecoder(sample_rate, 1, false);
        }
    }

    public bool OpusDecode(ushort samplerate, byte[] data, //ushort data_len,
        out float[] voicedata) {
        SetSamplerate(samplerate);
        EnsureOpusDecoder();

        int decoded_blob_size = opus_decoder.Decode(data, data.Length, opus_decode_buffer, FRAME_SIZE // data_len
            , false);

        if (decoded_blob_size > 0) {
            voicedata = new float[decoded_blob_size];
            Buffer.BlockCopy(opus_decode_buffer, 0, voicedata, 0, decoded_blob_size * sizeof(float));
            return true;
        } else {
#if !RELEASE
            Plugin.log.LogWarning($" VOICECHAT: OpusDecode: Failed to decode opus??? (data, {data.Length}, opus_decode_buffer, {FRAME_SIZE})");
#endif
            voicedata = new float[0];
            return false;
        }
    }


    public override string ToString() {
        if (this is VoicechatOutput vco && vco.plr != null) {
            return $"VC_OUTPUT(plr:{vco.plr.ToString()}, freq:{sample_rate})";
        }
        return $"STREAMED_AUDIO_OUTPUT({this.name}, freq:{sample_rate})";
    }


    public float GetFinalVolume() {
        var final_volume = _real_custom_volume;
        if (is_voice) {
            final_volume *= Voicechat.my_listener_volume;
            
            if (Voicechat.listener_is_mindwiped) {
                return final_volume;
            } else {
                final_volume *= 1.15f;
            }
        } else {
            final_volume *= Voicechat.mp3_listener_volume;
        }
        if (Util.IsWorldGenerated()) {
            final_volume *= 1.3f;// for some reason in world everything is much quieter
        }
        return final_volume;
    }



    protected void UpdateLowPassEffects(in bool is_watered) {

        float muffle_target = 22000;

        if (is_voice) {
            if (is_body_alive) {
                if (Voicechat.VCRULE_speechimpaired) {
                    if (is_watered) {
                        muffle_target = 1000;
                    }
                    if (speech_impaired) {
                        muffle_target = 1200;
                    }
                }

                if (Voicechat.listener_is_mindwiped) { // now they dont understand nobody
                    muffle_target = 300f;
                }
            }
        } else {
            if (is_watered) {
                muffle_target = 1100;
            }
        }

        if (Voicechat.VCRULE_hearingloss && (!is_voice || is_body_alive) && Util.TryGetLocalBody(out var locbody) && locbody.conscious) {

            var sourcepos = audioSource.transform.position;

            if (is_voice && is_body_alive) {
                sourcepos = body.GetHead().GetPosition();
            }

            if (Util.QuickGroundLinecastCheck(locbody.GetHead().GetPosition(), sourcepos) ) {
                muffle_target *= 0.6f;

                //TODO nah this sounds like shit, do real raytraced audio????????
            }

        }


        if (
            muffle_target < 22000
        ) {
            lowpass.enabled = true;
            if (is_watered) {
                lowpass.cutoffFrequency = muffle_target;
            } else {
                lowpass.cutoffFrequency = Mathf.MoveTowards(lowpass.cutoffFrequency, muffle_target, Time.unscaledDeltaTime * 45000f);
            }
            audioSource.maxDistance = max_hear_distance * 0.5f;
            return;
        }
        if (lowpass.cutoffFrequency != 22000) {
            lowpass.cutoffFrequency = Mathf.MoveTowards(lowpass.cutoffFrequency, 22000, Time.unscaledDeltaTime * 45000f);
        } else {
            lowpass.enabled = false;
        }
        audioSource.maxDistance = max_hear_distance;

    }


    protected void UpdatePitchAligner() {

        var speed = Time.unscaledDeltaTime * 0.1f;
        if (MIN_SAMPLECOUNT_PREBUFFERED * 0.4f > vc_buffered_amount) {
            if (last_blob_was_last) {
                pitchtarget = Mathf.Min(pitchtarget, 0.998f);
            } else {
                pitchtarget = Mathf.Min(pitchtarget, 0.94f);
            }
        } else if (vc_buffered_amount > half_sample_rate) {
            if (vc_buffered_amount > SAMPLE_RATE) {
                pitchtarget = Mathf.Max(pitchtarget, 1.1f);
            } else {
                pitchtarget = Mathf.Max(pitchtarget, 1.02f);
            }
        }
        audioSource.pitch = Mathf.MoveTowards(audioSource.pitch, pitchtarget, speed);
    }

    public bool is_watered = false;
    public float pitchtarget = 1;
    void EffectsUpdate() {
        if (force_no_effects) {
            audioSource.spatialBlend = 0f;
            return;
        }

        if (Util.IsWorldGenerated()) {

            audioSource.spatialBlend = 1f;
            audioSource.bypassReverbZones = Voicechat.listener_is_mindwiped;
            if (Voicechat.VCRULE_hearingloss && !Voicechat.listener_is_mindwiped) {
                if (audioSource.outputAudioMixerGroup == null ) {
                    audioSource.outputAudioMixerGroup = WorldGeneration.world.soundMixerGroup;
                } 
            } else {
                if (audioSource.outputAudioMixerGroup != null) {
                    audioSource.outputAudioMixerGroup = null;
                }
            }
        } else {
            audioSource.spatialBlend = 0f;
            
            if (audioSource.outputAudioMixerGroup != null) {
                audioSource.outputAudioMixerGroup = null;
            }
            
        }
        is_watered = false;


        if (is_voice) {
            if (is_body_alive) {
                is_watered = body.inWater;
                audioSource.dopplerLevel = VoicechatOutput.default_voice_doppler;
            } else {
                audioSource.dopplerLevel = 0;
            }
        } else {
            is_watered = Mathf.Abs(transform.position.x) < (float)WorldGeneration.world.halfWidth && Mathf.Abs(transform.position.y) < (float)WorldGeneration.world.halfHeight && FluidManager.main.HasLiquid(WorldGeneration.world.WorldToBlockPos(transform.position));
        }
        UpdateLowPassEffects(is_watered);

        if (Util.IsInMainMenu()) {
            audioSource.maxDistance = max_hear_distance;
        } else if (is_voice) {
            var is_voicechat = this is VoicechatOutput;

            audioSource.minDistance = audioSource.maxDistance * 0.24f;

            pitchtarget = 1f;
            if (is_voicechat && is_body_alive && Voicechat.VCRULE_speechimpaired) {

                {// pitch editing, mostly from drugs
                    var abshap = Mathf.Abs(body.opiateHappiness / 70f) * 0.5f;
                    if (body.brainHealth < 93) {
                        abshap = Mathf.Clamp01(Mathf.Max(abshap, KM.RemapClamped(body.brainHealth, 93, 30, 0, 0.3f)));
                    }

                    if (abshap > 0.002f) {
                        pitchtarget += (float)Math.Sin(Time.realtimeSinceStartupAsDouble + (12.0 + abshap * 9)) * abshap;

                        if (body.opiateHappiness > 0) {
                            pitchtarget += abshap * 0.01f;
                        } else {
                            pitchtarget -= abshap * 0.01f;
                        }
                    }
                }
            } 
            UpdatePitchAligner();
            return;
        } else {

            audioSource.maxDistance = max_hear_distance;
            pitchtarget = 1f;
            UpdatePitchAligner();
            return;
        }
        // just reset it all
        pitchtarget = 1f;
        UpdatePitchAligner();

    }




    public void OnHearabilityRuleUpdate() {
        // make sure the mixer is there
        if ((bool)WorldGeneration.world && Voicechat.VCRULE_hearingloss) {
            audioSource.outputAudioMixerGroup = WorldGeneration.world.soundMixerGroup;
        } else {
            audioSource.outputAudioMixerGroup = null;
        }
    }



    protected virtual void Awake() {
        sample_rate = 24000;

        audioSource = this.AddComponent<AudioSource>();
        audioSource.loop = true;

        audioSource.volume = 1;
        audioSource.spatialBlend = 1;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 10f;
        audioSource.maxDistance = max_hear_distance;
        audioSource.dopplerLevel = 0.3f;

        lowpass = this.AddComponent<AudioLowPassFilter>();
        lowpass.cutoffFrequency = 22000;

        OnHearabilityRuleUpdate();

        SetSamplerate(Voicechat.DEFAULT_SAMPLE_RATE);
    }
    void Start() {
        vc_buffer.Clear();
    }


    protected void SetSamplerate(ushort newrate) {
        newrate = Voicechat.GetClosestSamplerate(newrate);

        if (clip != null) {
            if (clip.frequency != newrate) {
                audioSource.Stop();
                audioSource.clip = null;
                Destroy(clip);
                clip = null;
                vc_buffer.Clear();
                vc_buffer = new(newrate);
            } else {
                return;
            }
        }

        sample_rate = newrate;
        clip = AudioClip.Create("VOICE_CHAT_CLIP_" + this.name, newrate * 2, 1, newrate, false);
        audioSource.clip = clip;
    }


    protected void StartPlayback() {
        if (sample_rate == 0) {
            sample_rate = Voicechat.DEFAULT_SAMPLE_RATE;
        }

        vc_endpos = (vc_buffer.Count + audioSource.timeSamples) % clip.samples;
        vc_buffered_amount = vc_buffer.Count;
        // the rate stays put for the life of the clip. a new array only when it changes.
        if (_playbackSamples == null || _playbackSamples.Length != sample_rate)
            _playbackSamples = new float[sample_rate];
        var gggg = _playbackSamples;

        for (int i = 0; i < gggg.Length; i++) {
            if (vc_buffer.Count == 0) {
                gggg[i] = vc_lastsample1;
                vc_lastsample1 = Mathf.MoveTowards(vc_lastsample1, 0, 2.0f / sample_rate);
                continue;
            }
            vc_lastsample1 = vc_buffer.Dequeue();
            gggg[i] = vc_lastsample1;
        }

        clip.SetData(gggg, audioSource.timeSamples);


        EffectsUpdate();

        last_blob_receive_time = Time.realtimeSinceStartupAsDouble;

        if (Voicechat.VCRULE_speechimpaired && is_voice && body != null && body.alive) {
            audioSource.pitch = Mathf.Clamp(body.brainHealth * 0.012f + 0.08f , 0.7f, 1f);
        } else {
            audioSource.pitch = 1f;
        }


        audioSource.Play();
        _last_timeSamples = audioSource.timeSamples;
    }
    protected void StopPlayback() {
        vc_buffered_amount = 0;
        TimeTheBufferFirstReceivedBlob = 0;
        audioSource.Stop();
    }

    // public SetData uses the array length as the sample count. this one takes the count, so a long zero buffer can fill a short gap.
    delegate bool ClipWrite(AudioClip clip, float[] data, int numsamples, int samplesOffset);
    static ClipWrite _clipWrite;
    static bool _clipWriteMiss;

    static ClipWrite ClipWriter() {
        if (_clipWrite != null || _clipWriteMiss)
            return _clipWrite;
        MethodInfo method = AccessTools.Method(typeof(AudioClip), nameof(AudioClip.SetData), new[] { typeof(AudioClip), typeof(float[]), typeof(int), typeof(int) });
        if (method == null) {
            _clipWriteMiss = true;
            if (OptimizerMain.Log != null)
                OptimizerMain.Log.LogWarning("[KrokV5Opt] audio silence kept allocating; counted SetData is missing");
            return null;
        }
        // the counted write is private. a delegate from this assembly would be refused, so call it through a skip-visibility method.
        var dyn = new DynamicMethod("krok_clip_silence", typeof(bool), new[] { typeof(AudioClip), typeof(float[]), typeof(int), typeof(int) }, typeof(StreamedAudioOutput).Module, true);
        ILGenerator il = dyn.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        _clipWrite = (ClipWrite)dyn.CreateDelegate(typeof(ClipWrite));
        return _clipWrite;
    }

    void WriteSilence(int count, int offset) {
        if (count <= 0 || clip == null)
            return;
        if (_silenceSamples == null || _silenceSamples.Length < count)
            _silenceSamples = new float[count];
        ClipWrite write = ClipWriter();
        int channels = clip.channels;
        if (write == null || channels <= 0) {
            float[] exact = _silenceSamples.Length == count ? _silenceSamples : new float[count];
            clip.SetData(exact, offset);
            return;
        }
        write(clip, _silenceSamples, count / channels, offset);
    }



    int _last_timeSamples = 0;
    // StartPlayback fills one rate-sized buffer. silence writes a prefix of this one.
    float[] _playbackSamples;
    float[] _silenceSamples;

    protected void Update() {

        if (SharedMain.LocalWorldIsGenerating) {
            StopPlayback();
            vc_buffer.Clear();
            return;
        }

        if (audioSource.isPlaying) {
        } else {
            cur_volume_output = 0;
        }

        if (vc_buffer.Count > 0) {
            TimeTheBufferFirstReceivedBlob += Time.unscaledDeltaTime;
            if (TimeTheBufferFirstReceivedBlob > PREBUFFER_LATENCY) { // ah make it not wait for the buffer to fill and play immidietly
                if (!audioSource.isPlaying) {
                    StartPlayback();
                }
            }
        } else {
            TimeTheBufferEnded += Time.unscaledDeltaTime;
        }
        if (audioSource.isPlaying) {

            EffectsUpdate();

            var travelled_distance = audioSource.timeSamples - _last_timeSamples;

            if (_last_timeSamples > audioSource.timeSamples) { // just looped
                travelled_distance = audioSource.timeSamples - (_last_timeSamples - clip.samples);

                //Plugin.log.LogInfo($" vc_buffered_amount. {vc_buffered_amount}  "); // around 5000 which is what i expected
            }
            vc_buffered_amount -= travelled_distance;

            // create silence in the data i already played
            if (travelled_distance > 0 ) {
                var difflastsaaa = _last_timeSamples - 6;
                if (difflastsaaa < 0) {
                    difflastsaaa += audioSource.timeSamples;
                }
                
                // SetData writes the whole array. the private write takes a count, so one zero buffer can cover a shorter frame.
                WriteSilence(travelled_distance, difflastsaaa);
                
            }


            _last_timeSamples = audioSource.timeSamples;

            if (vc_buffered_amount <= 0) {
                StopPlayback();
                //} else if (vc_buffered_amount <= MIN_SAMPLECOUNT_PREBUFFERED) {
                // i want to add silence because speech sounds kinda broken  with enough silence times
            } else {

            }
        }
    }








    void OnDestroy() {
        if (audioSource != null && gameObject != audioSource.gameObject)
            Destroy(audioSource.gameObject);
        if (lowpass != null)
            Destroy(lowpass);
        if (audioSource != null)
            Destroy(audioSource);

        if (clip != null)
            Destroy(clip);


    }

    public bool last_blob_was_last = false;
    double last_blob_receive_time;

    bool _warnedTooMuchSamples = false;
    public void ReceiveVoiceBlob(ushort freq, in float[] data, in bool is_not_last) {
        
        SetSamplerate(freq);
        TimeTheBufferEnded = 0;
        cur_volume_output = Voicechat.GetFrameMaxVol(data) ;

        var vol = GetFinalVolume();
        
        if (audioSource.isPlaying) {
            var new_buffered_amount = vc_buffered_amount + data.Length;

            // if theres too much packets and it will overlap the clip, dont do that
            if (new_buffered_amount >= clip.samples) {
                //ah fuck it dont do nothin, drop it

                if (log.verbose && !_warnedTooMuchSamples) {
                    _warnedTooMuchSamples =true;
                    log.error($"VOICECHAT: TOO MUCH SAMPLES: {this.ToString()}: {new_buffered_amount} >= {clip.samples}");
                }

            } else {
                for (int i = 0; i < data.Length; i++) {
                    data[i] = KM.Clamp11(data[i]) * vol;
                }

                var time_since_last_blob = (float)(Time.unscaledTimeAsDouble - last_blob_receive_time);
                var left_distance = clip.samples - new_buffered_amount;

                if (!is_not_last) {
                    last_blob_was_last = true;
                }

                clip.SetData(data, vc_endpos);
                vc_endpos += data.Length;
                vc_buffered_amount = new_buffered_amount;


                if (vc_endpos >= clip.samples) {
                    vc_endpos -= clip.samples;
                }

#if true
                // make a fade out incase it unintentionally tresspasses on unbuffered territory, so that the sound is somewhat predictable
                left_distance = Math.Min(clip.samples - vc_buffered_amount, quarter_sample_rate);
                if (left_distance > 10) {
                    var data2 = new float[left_distance];
                    for (int i = 0; i < left_distance; i++) {
                        data2[i] = data[data.Length - 1] * Mathf.Clamp01(1 - (i / 24) * 3f);
                    }
                
                    clip.SetData(data2, vc_endpos);
                }
#endif
                last_blob_receive_time = Time.unscaledTimeAsDouble;
            }

        } else {
            for (int i = 0; i < data.Length; i++) {
                vc_buffer.Enqueue(KM.Clamp11(data[i]) * vol);
            }
        }

        if (KM.DistanceCheck(audioSource.transform.position, Camera.main.transform.position, max_hear_distance * 0.5f)) {
            Voicechat.smooth_bg_music_volume_target = Mathf.Min(Voicechat.smooth_bg_music_volume_target, 1 - Mathf.Clamp01(cur_volume_output * 4f) * Voicechat.bg_music_attenuation);
        }
    }



}

internal class VoicechatOutput : StreamedAudioOutput {

    public static float default_voice_doppler = 0.23f;

    protected override void Awake() {
        is_voice = true;

        plr = GetComponent<ScavPlayer>();

        max_hear_distance = Voicechat.MAX_HEAR_DISTANCE;

        var vc_go = new GameObject("streamedaudiooutput");
        audioSource = vc_go.AddComponent<AudioSource>();
        audioSource.loop = true;
        

        audioSource.spatialBlend = 1;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 25f;
        audioSource.dopplerLevel = default_voice_doppler;
        audioSource.volume = 1;

        lowpass = vc_go.AddComponent<AudioLowPassFilter>();
        lowpass.cutoffFrequency = 22000;

        DontDestroyOnLoad(vc_go);
        SetSamplerate(Voicechat.DEFAULT_SAMPLE_RATE);
    }

    public ScavPlayer plr { get; private set; }
    public NetBody npc => plr.netBody;

    public Color CalculateVoiceChatIconColor() {
        return CalculateVoiceChatIconColor(out var idk);
    }
    public Color CalculateVoiceChatIconColor(out float realcurvol) {
        realcurvol = 0f;
        if (plr.IsLocal) {
            realcurvol = Voicechat.my_output_volume;
        } else {
            realcurvol = cur_volume_output;
        }
        var c = (Color)plr.playerColor;
        return new Color(c.r, c.g, c.b, Mathf.Clamp01(realcurvol + 0.3f));
    }

    void LateUpdate() {
        if (plr != null) {
            is_body_alive = plr.IsAlive();
            if (is_body_alive) {
                body = plr.body;

                var og = (Vector2)audioSource.transform.position;
                var headpos = body.GetHead().GetPosition();

                if (!KM.DistanceCheck(og,headpos,64)) {
                    audioSource.transform.position = headpos;
                } else {
                    var newpos = Vector2.Lerp(KM.ClampDistance(og, headpos, 4), headpos, Time.unscaledDeltaTime * 6);
                    audioSource.transform.position = Vector2.Lerp(og, newpos, 0.5f);
                }


                Voicechat.BodyOpenMouthForSpeaking(body, cur_volume_output);

            } else {
                if (Util.IsWorldGenerated()) {
                    audioSource.transform.position = plr.CameraViewPos;
                } else {
                    var vals = ScavPlayer.ClientIdToPlayerDict.Values.ToList();
                    var i = vals.IndexOf(plr);

                    var progress = (((float)i + 0.5f) - ((float)vals.Count * 0.5f)) / vals.Count * 4;
                    audioSource.transform.position = Camera.main.transform.position + Vector3.forward * 4;
                }
            }
            if (is_body_alive && Voicechat.VCRULE_speechimpaired) {
                if (body.talker.body != null) {
                    speech_impaired = plr.body.talker.impairedSpeech;
                }
            } else {
                speech_impaired = false;
            }
        } else {
            is_body_alive = false;
        }
    }


}

internal class VoicechatOutputDebugVisualiser : VoicechatOutput {
    
    public float curmaxvol_in_opus_decoder = 0;
    void Update() {
        base.Update();

        curmaxvol_in_opus_decoder = 0;
        foreach (var item in opus_decode_buffer) {
            if (Mathf.Abs(curmaxvol_in_opus_decoder) < Mathf.Abs(item)) {
                curmaxvol_in_opus_decoder = item;
            }
        }
    }
    
}

internal class AudioOpusEncoding : IDisposable {

    public static ushort[] STANDART_FREQUENCIES = new ushort[]{ // standart opus rates
        8000 ,
        12000,
        16000,
        24000,
        48000
    };

    internal byte[] opus_encode_buffer = new byte[1024];
    internal OpusEncoder opus_encoder = null;

    public byte[] Encode(in float[] data, ushort input_samplerate) {
        EnsureOpusEncoder(input_samplerate);

        int encoded_blob_len = 0;
        try {
            encoded_blob_len = opus_encoder.Encode(data, data.Length, opus_encode_buffer, opus_encode_buffer.Length);

            if (encoded_blob_len > 0) {
                var b = new byte[encoded_blob_len];
                Buffer.BlockCopy(opus_encode_buffer, 0, b, 0, encoded_blob_len);

                return b;
            }
        } catch (Exception e) {
            log.warn($"OPUS ENCODE ERROR: samplerate: {opus_encoder.GetSampleRate()}  data.Length:{data.Length} \n{e} ");
            return new byte[0];
        }
        log.warn($"OPUS ENCODE ERROR: samplerate: {opus_encoder.GetSampleRate()}  data.Length:{data.Length} ");
        return new byte[0];
    }
    void EnsureOpusEncoder(ushort target_samplerate) {
        if (opus_encoder == null || opus_encoder.GetSampleRate() != target_samplerate) {
            opus_encoder?.Dispose();

            opus_encoder = new OpusEncoder(target_samplerate, 1, OpusPredefinedValues.OPUS_APPLICATION_VOIP);
            opus_encoder.SetVbr(true);
        }
    }

    public void Dispose() {
        if (opus_encoder != null) {
            opus_encoder.Dispose();
            opus_encoder = null;
        }
    }
}


public class Voicechat :  CasualtiesTogetherSingleton {

    public static Voicechat singleton { get; private set; }


    // OPUS is strict about its frequencies
    public static ushort[] STANDART_FREQUENCIES => AudioOpusEncoding.STANDART_FREQUENCIES;


    public const int DEFAULT_MAX_SAMPLE_RATE = 48000;
    public const int DEFAULT_SAMPLE_RATE = 24000; 
    public const int DEFAULT_MIN_SAMPLE_RATE = 8000;

    public const int DEFAULT_FRAME_DURATION_MS = 40;
    public static float DEFAULT_FRAME_DURATION_S => ((float)DEFAULT_FRAME_DURATION_MS) / 1000f;


    private static AudioClip mic_clip;
    public static int SAMPLE_RATE = DEFAULT_SAMPLE_RATE;  
    public static int FRAME_SIZE => CalculateFrameSizeForSamplerate(SAMPLE_RATE);
    public static int CalculateFrameSizeForSamplerate(int sr) {
        return sr / 1000 * DEFAULT_FRAME_DURATION_MS;
    }

    public static bool IS_RECORDING { get; protected set; }
    private static int last_mic_pos = 0;

    public const string NOMIC = "NO_MICROPHONE_SELECTED";

    internal const string _PREF_MICNAME = "KrokoshaMultiplayer_MicrophoneName";
    internal static string _PREF_MICMODE { 
        get {
            var f = typeof(Voicechat).GetField(nameof(current_microphone_mode));
            return f.GetAttribute<SettingDeclarerThingyChoiceAttribute>().GetPrefKey(f);
        }
    }

    public static string current_microphone { get; protected set; }


    [SettingDeclarerThingyFloat(0, 2, "setting_vcattenuation")]
    public static float bg_music_attenuation = 1;
    [SettingDeclarerThingyFloat(0, 2, "setting_mp3vol")]
    public static float mp3_listener_volume = 1.0f;
    [SettingDeclarerThingyFloat(0, 2, "setting_vclistenvol")]
    public static float my_listener_volume = 1.0f;
    [SettingDeclarerThingyFloat(0, 2, "setting_micvol")]
    public static float my_volume = 1.0f; // is controlled from OnGUI in the main component
    [SettingDeclarerThingyChoice("setting_micmode", new string[] {
        "setting_micmode_choice_off",
        "setting_micmode_choice_ptt",
        "setting_micmode_choice_on",
        "setting_micmode_choice_toggle"
    })]
    public static byte current_microphone_mode = 1; // default: push to talk 
    public static bool IsMicModeAlwaysOn() {
        return current_microphone_mode == 2;
    }
    public static bool IsMicModePushToTalk() {
        return current_microphone_mode == 1;
    }
    public static bool IsMicModeToggleToTalk() {
        return current_microphone_mode == 3;
    }
    public static bool IsMicModeDisabled() {
        return current_microphone_mode == 0;
    }
    public static bool IsPushToTalkKeyPushed() {
        return Input.GetKey(KeyBinds.GetBind(CoopKeybinds.voicechat));// && Application.isFocused;
    }

    public static float MINIMUM_VOLUME_TRESHOLD = 0.05f;
    public static float MINIMUM_VOLUME_TRESHOLD_TIME = 0.5f;

    public static float AlwaysOn_TimeBelowTreshold = 0;

    public static float my_output_volume = 0.0f; // live value 


    public static bool VCRULE_hearingloss => Multiplayer.rules.HearingLossVC;
    public static bool VCRULE_speechimpaired => Multiplayer.rules.SpeechImpairedVC;
    public static bool VCRULE_mindwiperule => Multiplayer.rules.MindwipeDisablesChat;
    public static bool VCRULE_deadchat => Multiplayer.rules.CanCommunicateWithTheDeadVC();
    public static bool VCRULE_enabled => Multiplayer.rules.VoicechatEnabled;


    public static bool listener_is_mindwiped = false;


    public static float MAX_HEAR_EFFECT_PROCESSING_DISTANCE => MAX_HEAR_DISTANCE * 1.3f;
    public static float MAX_HEAR_DISTANCE => Util.MetersToTiles(Multiplayer.rules.ProximityHearDistance);

    static Queue<float> my_mic_data_buffer = new();

    static AudioOpusEncoding encoder;

    internal static bool ENABLE_LOCAL_LOOPBACK = false;
    internal static VoicechatOutputDebugVisualiser ENABLE_LOCAL_LOOPBACK_VCINSTANCE = null;
    internal static int TEMPDEV_LAST_OPUS_ENCODED_SIZE = 0;
    internal static int TEMPDEV_LAST_OPUS_BEFORE_ENCODED_SIZE = 0;

    // event called from main singleton
    void KrokoshaSingletonEvent_OnSceneChange() {
        Voicechat.ENABLE_LOCAL_LOOPBACK = false;

        KrokoshaSingletonEvent_OnRulesUpdate();
    }
    void KrokoshaSingletonEvent_OnRulesUpdate() {
        foreach (var item in FindObjectsOfType<StreamedAudioOutput>()) {
            item.OnHearabilityRuleUpdate();
        }
    }


    public static ushort GetClosestSamplerate(ushort my) {
        var res = STANDART_FREQUENCIES[0];
        foreach (var item in STANDART_FREQUENCIES) {
            if (my < item) {
                return res;
            }
            res = item;
        }
        return res;
    }
    public static ushort GetClosestNumber(ushort my, ushort[] ushorts) {
        var res = ushorts[0];
        foreach (var item in ushorts) {
            if (my < item) {
                return res;
            }
            res = item;
        }
        return res;
    }



    private void Awake() {
        if (current_microphone == null) { 
            current_microphone = NOMIC;
        }
        encoder = new AudioOpusEncoding();
        singleton = this;
    }



    private void Start() {

        current_microphone = SettingsJsonThing.json.GetString(_PREF_MICNAME, NOMIC);

        if (current_microphone == Voicechat.NOMIC) {
            Plugin.log.LogInfo($" VOICECHAT: Start(): Attempting to select default microphone. ");
        }
        StartRecording(current_microphone);
        if (!IS_RECORDING && Microphone.devices.Length > 0) { // this never happened, but just in case try to pick at least some microphone 
            StartRecording(Microphone.devices[0]);
        }

    }


    static void CopyMicSamplesIntoBufferAndApplyVolume(ref float[] samples, ref int i, int until) {
        while (true) {
            // unity mic is always mono
            float vol = samples[i] * my_volume;//float vol = KM.clamp11(samples[i] * my_volume);

            i++;

            my_mic_data_buffer.Enqueue(vol);

            if (i < until) {
            } else {
                break;
            }
        }
    }

    public static bool VoiceChatIsEnabledLocallyOnly() {
        if (Util.IsGeneratingWorld()) {// to avoid glitching
            return false;
        }
        return my_volume > 0 && (!(IsMicModeDisabled()));
    }
    public static bool VoiceChatIsEnabled() {
        if (!VoiceChatIsEnabledLocallyOnly()) {// to avoid glitching
            return false;
        }
        return (!((!VCRULE_enabled && Net.IsRunning) || Net.IsDedicatedServer));
    }


    static bool _toggle_to_talk_toggled = false;
    static bool _push_to_talk_pushed = false;
    private static bool RecordMicUpdate() {

        if (!VoiceChatIsEnabled()) {
            _push_to_talk_pushed = false;
            _toggle_to_talk_toggled = false;
            return false;
        }

        if (!Voicechat.ENABLE_LOCAL_LOOPBACK) {
            if (IsMicModePushToTalk()) {
                if (IsPushToTalkKeyPushed()) {
                    _push_to_talk_pushed = true;
                }
                if (!_push_to_talk_pushed) {
                    return false;
                }
            } else if(IsMicModeToggleToTalk()) {
                if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.voicechat))) {
                    _toggle_to_talk_toggled = !_toggle_to_talk_toggled;
                }
                if (_toggle_to_talk_toggled) {
                    _push_to_talk_pushed = true;
                }
                if (!_push_to_talk_pushed) {
                    return false;
                }
            } else if (current_microphone_mode != 2) {
                return false;
            }

            var locplr = ScavPlayer.LocalPlayer;
            if (locplr != null) {
                if (
                    VCRULE_speechimpaired //&& current_microphone_mode == 1 && _push_to_talk_pushed
                ) {
                    if (VCRULE_mindwiperule && locplr.IsAliveAndMindwiped()) {
                        if (
                            !_did_mindwipe_alert
                        ) {
                            _push_to_talk_pushed = false;
                            _did_mindwipe_alert = true;
                            Util.DoAlert(Lang.Get("voicechat_deny_mindwipe"));
                            log.l("VOICECHAT: Can't speak = mindwiped");
                        }
                        return false;
                    }
                    if (CheckIfPlayerIsUnconsciousToMute(locplr)) {
                        if (
                            !_did_consciousness_muted_alert
                        ) {
                            _push_to_talk_pushed = false;
                            _did_consciousness_muted_alert = true;
                            Util.DoAlert(Lang.Get("voicechat_deny_unconscious"));
                            log.l("VOICECHAT: Can't speak = unconscious");
                        }
                        return false;
                    }
                }
            }
        }

        if (!IS_RECORDING // start recording if not recording
            || (mic_clip.frequency != SAMPLE_RATE)// restart the recorder if sample rate suddenly changed
        ) {
            StartRecording(current_microphone);
        }

        if (IS_RECORDING && mic_clip.channels > 0) {
            int pos = Microphone.GetPosition(GetMic());

            // check if we reached amout of samples enough for buffer

            var didloop = pos < last_mic_pos;
            var micpos_distance = pos - last_mic_pos;
            if (didloop) {
                micpos_distance = pos - (last_mic_pos - mic_clip.samples);
            }

            if (pos == last_mic_pos) {
                return true; // wait for the mic to move
            }




            // turns out, unity microphone is always guranteed mono !!! 
            {
                int i = 0;
                if (!didloop) {
                    var sample = new float[micpos_distance];
                    mic_clip.GetData(sample, last_mic_pos);
                    CopyMicSamplesIntoBufferAndApplyVolume(ref sample, ref i, sample.Length);
                } else {
                    if (pos == 0) {
                        var lastLoopSamples = new float[mic_clip.samples - last_mic_pos];
                        mic_clip.GetData(lastLoopSamples, last_mic_pos);

                        CopyMicSamplesIntoBufferAndApplyVolume(ref lastLoopSamples, ref i, lastLoopSamples.Length);
                    } else {
                        var lastLoopSamples = new float[mic_clip.samples - last_mic_pos];
                        var currLoopSamples = new float[pos];

                        mic_clip.GetData(lastLoopSamples, last_mic_pos);
                        mic_clip.GetData(currLoopSamples, 0);

                        CopyMicSamplesIntoBufferAndApplyVolume(ref lastLoopSamples, ref i, lastLoopSamples.Length);
                        i = 0;
                        CopyMicSamplesIntoBufferAndApplyVolume(ref currLoopSamples, ref i, currLoopSamples.Length);
                    }
                }
            }
            last_mic_pos = pos;

            SendAllAvailableMicData();

            return true;
        }

        return false;
    }

    static void SendAllAvailableMicData(bool dontcare_if_it_dont_smaller_than_buffer = false) {
        float[] frame;


        if (my_mic_data_buffer.Count < FRAME_SIZE) {
            if (!dontcare_if_it_dont_smaller_than_buffer) {
                return; // wait for the buffer to fill
            }
        } else {
            // its kinda guranteed that dontcare_if_it_dont_smaller_than_buffer doesnt reach here

            frame = new float[FRAME_SIZE];

            UpdateOpusComplexity();
            while (my_mic_data_buffer.Count >= frame.Length) {

                for (int i = 0; i < frame.Length; i++)
                    frame[i] = my_mic_data_buffer.Dequeue();

                //SEND IT

                var isnotlast = IsMicModeAlwaysOn() || // it never stops
                    my_mic_data_buffer.Count >= frame.Length || // loop will iterate once more
                    IsPushToTalkKeyPushed() // push to talk is still pushed
                    ;// || (dontcare_if_it_dont_smaller_than_buffer && my_mic_data_buffer.Count > 0); // it will send another finishing packet

                Client_SendMicDataFrame(frame, isnotlast);
            }
            _push_to_talk_pushed = false;
        }



    }

    public static float smooth_bg_music_volume = 1;
    public static float smooth_bg_music_volume_target = 1;


    public static int system_framesize = 48000;

    private static bool _did_mindwipe_alert = false;
    private static bool _did_consciousness_muted_alert = false;

    void UpdateMicrophone() {
        if (RecordMicUpdate()) {
            // is all good 
            var b = Util.GetLocalBodyNullable();
            if (b != null && b.alive) {
                BodyOpenMouthForSpeaking(b, my_output_volume);
            }

            if (my_output_volume < MINIMUM_VOLUME_TRESHOLD) {
                AlwaysOn_TimeBelowTreshold += Time.deltaTime;
            }
        } else {
            ENABLE_LOCAL_LOOPBACK = false;
            StopRecording();
        }
    }

    void UpdateBackgroundMusicAttenuation() {
        smooth_bg_music_volume = Mathf.MoveTowards(smooth_bg_music_volume, smooth_bg_music_volume_target, Time.unscaledDeltaTime);
        smooth_bg_music_volume_target = Mathf.Min(1, smooth_bg_music_volume_target + Time.unscaledDeltaTime / 8);
        MusicManager_Start_MultiplayerPatch.musicsources_volume = Mathf.Max(0, smooth_bg_music_volume * MusicManager_Start_MultiplayerPatch.musicvolume);
    }

    void UpdateMicrophoneLoopback() {

        if (Voicechat.ENABLE_LOCAL_LOOPBACK) {
            if ((!UIBullshit.IsAnyMenuOpen() && Util.IsWorldGenerated())) {
                Voicechat.ENABLE_LOCAL_LOOPBACK = false;
            }
        } else {
            if (ENABLE_LOCAL_LOOPBACK_VCINSTANCE != null) {
                Destroy(ENABLE_LOCAL_LOOPBACK_VCINSTANCE.gameObject);
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE = null;
            }
        }

        // visualiser in settings
        if (UIBullshit.main == null || UIBullshit.main.egg == null || !UIBullshit.main.egg.gameObject.activeInHierarchy) return;
        
        float newwriteheight = 0;
        if (Voicechat.ENABLE_LOCAL_LOOPBACK_VCINSTANCE != null) {
            newwriteheight = Voicechat.ENABLE_LOCAL_LOOPBACK_VCINSTANCE.curmaxvol_in_opus_decoder;
        }
        if (!Voicechat.ENABLE_LOCAL_LOOPBACK) {
            newwriteheight = Voicechat.my_output_volume;
        }

        if (UIMainMenu.writeX_flipflop) {
            newwriteheight = -Mathf.Abs(newwriteheight);
        } else {
            newwriteheight = Mathf.Abs(newwriteheight);
        }

        if (Mathf.Abs(UIBullshit.main.egg.writeHeight) < Mathf.Abs(newwriteheight)) {
            UIBullshit.main.egg.writeHeight = KM.Clamp11(newwriteheight);
        }
        
    }

    private void Update() {
        system_framesize = Voicechat.CalculateFrameSizeForSamplerate(AudioSettings.outputSampleRate);
        listener_is_mindwiped = ScavPlayer.LocalPlayer != null && VCRULE_hearingloss && VCRULE_mindwiperule && ScavPlayer.LocalPlayer.IsAliveAndMindwiped();

        UpdateMicrophone();

        if (!Multiplayer.DedicatedServerDisableVisuals) {// change the volume according to if someone is talking over it
            UpdateBackgroundMusicAttenuation();
        }

        UpdateMicrophoneLoopback();

    }

    public static void BodyOpenMouthForSpeaking(Body b, float volume) {
        b.eatTime = Math.Max(b.eatTime, volume - 0.05f);
    }



    const int MAX_FAILURES_UNTIL_DISABLE_VC = 5;
    static int START_FAILURE_COUNT = 0;

    // or can be used to set the mic
    public static void StartRecording(string micName) {
        StopRecording();

        last_mic_pos = 0;
        current_microphone = micName;

        try {
            mic_clip = Microphone.Start(GetMic(), true, 2, SAMPLE_RATE);
            IS_RECORDING = mic_clip != null;
            if (!IS_RECORDING) {
                log.error($" VOICECHAT: StartRecording(): Failed to start recording: {micName} ");
            }
        } catch (Exception e) {
            Plugin.log.LogInfo($" VOICECHAT: StartRecording(): Failed to set mic to: {micName} - {e.ToString()}");
            current_microphone = Voicechat.NOMIC;
            IS_RECORDING = false;
        }
        if (!IS_RECORDING) {
            START_FAILURE_COUNT++;
            if (START_FAILURE_COUNT > MAX_FAILURES_UNTIL_DISABLE_VC) {
                Multiplayer.DoMultiplayerStatusMessageError($"Failed to initialize microphone {START_FAILURE_COUNT} times. Microphone is now disabled.");
                START_FAILURE_COUNT = 0;
                current_microphone_mode = 0;
            }
        }

    }

    // ALWAYS USE THIS FOR USING UNITY MICROPHONE API 
    public static string GetMic() {
        if (current_microphone == Voicechat.NOMIC) {
            return null;
        }
        return current_microphone;
    }

    public static void StopRecording() {
        my_output_volume = 0f;
        if (IS_RECORDING) {

            SendAllAvailableMicData(true);

            my_mic_data_buffer.Clear();

            IS_RECORDING = false;

            Microphone.End(GetMic());
            Destroy(mic_clip);
            mic_clip = null;

        }
    }

    public static void SetMicrophone(string micName) {

        if (micName != current_microphone && Multiplayer.verbose) {
            Plugin.log.LogInfo($" VOICECHAT: SetMicrophone(): {micName} ");
        }
#if DEBUG
#else
        SettingsJsonThing.json.SetString(_PREF_MICNAME, micName);
#endif
        StartRecording(micName);
    }

    public static void SetMicrophoneMode(int mode) => SetMicrophoneMode((byte)mode);
    public static void SetMicrophoneMode(byte mode) {
        SettingsJsonThing.json.SetInt(_PREF_MICMODE, mode);
        current_microphone_mode = mode;
    }




    static float _complexityupdatetimer = 0;
    static void UpdateOpusComplexity() { // why am i doing this? is there any substantial benefit?
        if (encoder.opus_encoder == null) {
            return;
        }
        _complexityupdatetimer += Time.unscaledDeltaTime;
        if (_complexityupdatetimer > 1) {
            _complexityupdatetimer = 0;
            var newcomp = (int)KM.RemapClamped(1f / Time.unscaledDeltaTime, 20, 60, 0, 10);
            if (newcomp != encoder.opus_encoder.GetComplexity()) {
                encoder.opus_encoder.SetComplexity(newcomp);
            }
        }
    }


    static void Client_SendMicDataFrame(float[] data, bool is_not_last) {



        var minmax = GetFrameMaxVol(data);

        my_output_volume = minmax;


        if (ENABLE_LOCAL_LOOPBACK) {
            if (ENABLE_LOCAL_LOOPBACK_VCINSTANCE == null) {
                var go = new GameObject("vc_loopback");
                DontDestroyOnLoad(go);
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE = go.AddComponent<VoicechatOutputDebugVisualiser>();

                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.audioSource.spatialBlend = 0f;
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.force_no_effects = true;
            }
            ENABLE_LOCAL_LOOPBACK_VCINSTANCE.transform.position = Camera.main.transform.position;
            {
                byte[] voiceblob = encoder.Encode(data, (ushort)SAMPLE_RATE);
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.OpusDecode((ushort)SAMPLE_RATE, voiceblob, out var voicedata);
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.ReceiveVoiceBlob((ushort)SAMPLE_RATE, voicedata, is_not_last);
                TEMPDEV_LAST_OPUS_ENCODED_SIZE = voiceblob.Length;
                TEMPDEV_LAST_OPUS_BEFORE_ENCODED_SIZE = data.Length;
            }
            return;
        }


        if (my_output_volume > MINIMUM_VOLUME_TRESHOLD) {
            AlwaysOn_TimeBelowTreshold = 0;
        } else {
            if (AlwaysOn_TimeBelowTreshold > MINIMUM_VOLUME_TRESHOLD_TIME) { // dont send empty packets
                return;
            }
        }

        if (Net.IsConnected && !Net.IsDedicatedServer) {

            // new opus encoder 
            byte[] voiceblob = encoder.Encode(data, (ushort)SAMPLE_RATE);

            if (voiceblob.Length > 0) {

                TEMPDEV_LAST_OPUS_ENCODED_SIZE = voiceblob.Length;
                TEMPDEV_LAST_OPUS_BEFORE_ENCODED_SIZE = data.Length;


                var writer = Net.CreateWriter(NetmsgId.SERVER_VoiceChatBlob);

                writer.Put((byte)Array.IndexOf(STANDART_FREQUENCIES, (ushort)SAMPLE_RATE));
                writer.PutBytesWithLength(voiceblob);
                writer.Put((ushort)data.Length);
                writer.Put(is_not_last);

                Net.Client_Send(DeliveryMethod.Unreliable, writer);

                // WRITER END;

            } else {
#if TESTING
                log.warn($" VOICECHAT: Failed to encode opus to send???  data.Length:{data.Length} ");
#endif
            }

        }

    }



    public static bool CheckIfPlayerIsUnconsciousToMute(ScavPlayer plr) {
        if (Multiplayer.rules.SleepingMute) {
            if (plr.IsAliveAndNotConscious()) {
                return true;
            }
        } else {
            if (plr.IsAliveAndUnconsciousAndNotSleeping()) {
                return true;
            }
        }
        return false;
    }
    public static bool CheckIfIsAliveAndNotCriticallyDying(ScavPlayer plr) {
        //if (Multiplayer.rules.DyingHearDead) {
        //    return plr.IsAlive();
        //} else 
        {
            return plr.IsAliveAndNotCriticallyDying();
        }
    }

    





    [ClientReceiver((byte)NetmsgId.CLIENT_VoiceChatBlobRelay)]
    static void ClientReceiver__VoiceChatBlobRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (Voicechat.my_listener_volume <= 0) return;
        if (SharedMain.LocalWorldIsGenerating) return;

        reader.Get(out knetid speakerId);

        if (ScavPlayer.TryGetPlayerFromClientId(speakerId, out var plr) && plr.vc_output != null) {

            reader.Get(out byte freq_index);
            var freq = STANDART_FREQUENCIES[freq_index];

            reader.Get(out byte[] data);
            reader.Get(out bool is_not_last);

            if (
                !plr.vc_output.OpusDecode(freq, data, out var frame)
            ) {
#if !RELEASE
                Plugin.log.LogWarning($" VOICECHAT: CLIENT: Failed to decode opus??? ");
#endif
                return;
            }

            plr.vc_output.ReceiveVoiceBlob(freq, frame, is_not_last);
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_VoiceChatBlob)]
    static void ServerReceiver__VoiceChatBlob(knetid clientId, LiteNetLib.Utils.NetDataReader reader ) {
        if (!VCRULE_enabled) return;
        if (SharedMain.LocalWorldIsGenerating) return;

        reader.Get(out byte freq_index);
        reader.Get(out byte[] data);
        reader.Get(out bool is_not_last);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && plr.vc_output != null) {

            if (plr.server_mute_vc) {
                if (plr.ServerPlayerState.didmutewarn < 20)
                    plr.Server_DoAlertSingle("<key:mmct_plr_vcmuted>", false);
                plr.ServerPlayerState.didmutewarn++;

                return;
            }


            if (freq_index > STANDART_FREQUENCIES.Length) {
#if !RELEASE 
                Plugin.log.LogInfo($"SUS: VOICECHAT client sent wrong samplerate index: {freq_index} ");
#endif // its possible to get this message while in the process of switching samplerate, so its not a sus thing
                return;
            }

            var freq = STANDART_FREQUENCIES[freq_index];

            if (freq < DEFAULT_MIN_SAMPLE_RATE || freq > DEFAULT_MAX_SAMPLE_RATE || freq > SAMPLE_RATE) {
                //Plugin.log.LogInfo($"SUS: VOICECHAT client sent wrong samplerate {freq} ");
                return;
            }


            // check if this guy can even speak 
            if (plr.IsAlive()) {
                if (VCRULE_speechimpaired) {
                    if (VCRULE_mindwiperule && plr.IsAliveAndMindwiped()) {
                        return;
                    }
                    if (CheckIfPlayerIsUnconsciousToMute(plr)) {
                        return;
                    }
                    // drop the packet as if stuttering
                    if (plr.body.brainHealth < 75) {
                        var r = UnityEngine.Random.Range(0, 100);
                        if (plr.body.brainHealth + 10 < r) {
                            //log.l("dropping voice packet due to brain damage");
                            return;
                        }
                    }
                }
            }

            bool server_heard_it = false;
            float[] voicedata_frame = null;
            if (!plr.IsLocal) {
                // now lets decode opus here
                if (!plr.vc_output.OpusDecode(freq, data, out voicedata_frame)) {
#if TESTING
                    log.warn($" VOICECHAT: SERVER: Failed to decode opus??? ");
#endif
                    return;
                }

                if (plr.vc_output.vc_buffered_amount + voicedata_frame.Length >= plr.vc_output.clip.samples) {
                    // it can sometimes happen
                    return;
                }

                if (Net.IsDedicatedServer) {
                    server_heard_it = true;
                    
                    if (Voicechat.my_listener_volume > 0 && !Multiplayer.DedicatedServerDisableVisuals)
                        plr.vc_output.ReceiveVoiceBlob(freq, voicedata_frame, is_not_last);
                }
            }



            var targets = Server_VoiceRelayGetTargets(plr);

            if (ScavPlayer.LocalPlayer != null && targets.Remove(ScavPlayer.LocalPlayer)) {
                if (
                    !server_heard_it &&
                    Voicechat.my_listener_volume > 0
                ) { // loop it back right now
                    if (voicedata_frame == null) {

                        if (!plr.vc_output.OpusDecode(freq, data, out voicedata_frame)) {
#if TESTING
                            log.warn($" VOICECHAT: SERVER: Failed to decode opus??? 2 ");
#endif
                            return;
                        }
                    }

                    plr.vc_output.ReceiveVoiceBlob(freq, voicedata_frame, is_not_last);
                }
            }

            if (targets.Count == 0) { // no one can hear u lol
                return;
            }

            var target_client_ids = targets.Select(x => x.playerId).ToList();


            var writer = Net.CreateWriter(NetmsgId.CLIENT_VoiceChatBlobRelay);

            writer.Put(clientId);
            writer.Put(freq_index);
            writer.PutBytesWithLength(data);
            writer.Put(is_not_last);

            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: target_client_ids);

            // WRITER END;


        }

    }

    static List<ScavPlayer> Server_VoiceRelayGetTargets(ScavPlayer yapper_plr) {
        var plrpos = yapper_plr.pos;

        var targets = new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values);
        targets.Remove((ScavPlayer)yapper_plr);

        // if is in world, optimize the relay by not sending the packets to players who are out of range
        if (Util.IsWorldGenerated()) {
            var presqr = yapper_plr.vc_output.max_hear_distance * yapper_plr.vc_output.max_hear_distance;
            var presqr_fullmouth = presqr * 0.5f;

            var fullmouth = yapper_plr.ImpairedSpeech();
            var dist_to_check_sqr = fullmouth ? presqr_fullmouth : presqr;

            if (yapper_plr.IsAlive()) {
            } else {
                if (VCRULE_deadchat) {
                } else {
                    targets.RemoveAll(t => {
                        return CheckIfIsAliveAndNotCriticallyDying(yapper_plr);
                    });
                }
            }

            // remove everyone who is out of range
            targets.RemoveAll(t => {
                var camerapos = t.pos;

                if (KM.DistanceCheck_PreSqr(camerapos, plrpos, dist_to_check_sqr)) {
                    return false;
                }
                return true;
            });

        }

        return targets;
    }



    public static float GetFrameMaxVol(IEnumerable<float> voice_frame) {
        var max_vol = 0f;
        foreach (var f in voice_frame) {
            max_vol = Math.Max(Mathf.Abs(f), max_vol);
        }
        return max_vol;
    }


    public static float[] ResampleVoiceBlob(in IReadOnlyList<float> data, int input_samplerate, int target_samplerate) {
        double ratio = (double)target_samplerate / input_samplerate;

        var newarrlen = Mathf.RoundToInt((float)((float)data.Count * ratio));

        var output = new float[newarrlen];

        for (int i = 0; i < newarrlen; i++) {

            var source_i = (double)(((double)i) / ratio);
            var j = (int)Math.Floor(source_i);


            double fraction = source_i - j;

            var val = data[j];
            var nextval = val;
            if (j + 1 < data.Count) {
                nextval = data[j + 1];
            }

            // lerp but keep it double for precision, but idk if its really needed
            float result = (float)(val + (nextval - val) * fraction);

            output[i] = result;
        }
        return output;
    }



    private void OnApplicationQuit() {
        StopRecording();
        current_microphone_mode = 0; // so that it wouldnt accidentally restart (even tho i didnt test if this would ever happen  )
    }

}











