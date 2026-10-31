
using BepInEx;
using BepInEx.Logging;
using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
using LiteNetLib.Utils;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Color = UnityEngine.Color;



namespace Together;





[AttributeUsage(AttributeTargets.Field)]
public class MultiplayerRuleByteAttribute : Attribute {
    public byte min = 0;
    public byte limit;
    public string postfix ;
    public MultiplayerRuleByteAttribute(byte max = 100, string unit = "%") {
        limit = max;
        postfix = unit;
    }
    public MultiplayerRuleByteAttribute(byte min, byte max, string unit = "") {
        limit = max;
        postfix = unit;
    }
}
[AttributeUsage(AttributeTargets.Field)]
public class MultiplayerRuleFloatAttribute : Attribute {
    public float mi;
    public float ma;
    public string postfix;
    public MultiplayerRuleFloatAttribute(float min, float max, string unit = "") {
        mi = min;
        ma = max;
        postfix = unit;
    }
}

[Serializable]
public partial struct MultiplayerGameRules : INetSerializeByMemcpy {

#if !RELEASE
    public bool sv_cheats = true;
    public bool AllowClientCheatCommands = true;
#else
    public bool sv_cheats = false;
    public bool AllowClientCheatCommands = false;
#endif


    [MultiplayerRuleByte(Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT, "")]
#if TESTING
    public byte PLAYER_COUNT_LIMIT = 200;  // DONE
#else
    public byte PLAYER_COUNT_LIMIT = 6;  // DONE
#endif

    public bool ShowPlayerDirections = true; // DONE
    public bool EnableNametags = true; // DONE
    public bool EnableStatusIcons = true; // DONE
    public bool UnchippedHideNametags = true; // DONE

    public bool EnableChatbox = true; // DONE 

    public bool OnlyProximityChat = false; // DONE 
    public bool UnchippedProximityChat = true; // DONE 

    public bool UnchippedIsIndividual = true; // DONE  // in pre-run settings, everyone can choose on their own to be chipped or no. if this is "false", everyone is same as host

    public bool ConsentForFeeding = true;


    [MultiplayerRuleByte]
    public byte ScatterMinGroupSize = 51;
    [MultiplayerRuleFloat(0,1500, "M")]
    public float ScatterPunishDistance = 80; // in meters
    public bool PlayerScatterDisallowed => Multiplayer.rules.ScatterPunishDistance != 0 && Multiplayer.rules.ScatterPunishDistance < 449; // 1499 in tiles to meters

    [MultiplayerRuleByte(101)]
    public byte LayerFinishPlrPercent = 60; // 0 = one player, 50 = half, 100 = everyone alive must be at the end, 101 = disable

    public bool LayerFinishKeepXOffset = true;

    [MultiplayerRuleByte]
    public byte StragglerRadlinePercent = 30;
    public bool RadlineExtraDamage = true;

    public bool NoInventoryLock = false;  // and steal stuff from other inventory even when theyre conscious
    //public bool SharedTraderReputation = true;     // no 
   
    public bool EnableSleep = false; // DONE  
    public bool EnableTimeManipulation = false; // DONE // kinda broken, allows time manipulation (of course its synchronized but i find it too chaotic to let everyone spam fast forward)


    public bool SpeechImpairedChat = true;
    public bool HearingLossChat = true;   // DONE UNTESTED 
    public bool SpeechImpairedVC = true;
    public bool HearingLossVC = true;   // DONE UNTESTED 
    public bool MindwipeDisablesChat = true; // REQUIRES  SpeechImpairedChat and HearingLossChat
    public bool DeadTextchat = true; // DONE     //  text chat and voice chat are not separated from dead (forced true in tutorial)
    public bool DeadVoicechat = true;  // requires "CommunicateWithTheDead"
    public bool SleepingMute = false;
    public bool TrackPlayerDeaths = true;


    public bool Permadeath = false;         // DONE // disable all forms of non-cheat respawn
    public bool ReviveOnNextLevel = false;  // DONE // respawn all dead players when reaching next layer
    public bool ReviveFromTrader = true;    // DONE //  recruit trader as a form of respawn


    public bool RespawnKeepInventory = false;
    public bool RespawnKeepSkills = false;


    public bool AllowSpectatorFreecam = true;
    public bool AllowPush = true; 

    public bool AlwaysAllowCarry = false;
    public bool AllowPiggyback => PiggybackMaxStack > 0;
    [MultiplayerRuleByte(254,"")]
#if RELEASE 
    public byte PiggybackMaxStack = 1;
#else
    public byte PiggybackMaxStack = 3;
#endif
    [MultiplayerRuleFloat(0, 2, "")]
    public float PiggybackWeightMultiplier = 0.8f;// DONE 

    [MultiplayerRuleByte(1,50, "")]
    public byte MinigameMaxPlayers = 50;


    public bool SpectateWhileUnconscious = false;



    public bool EnableMP3Sync = true;
    [MultiplayerRuleByte(4,"")]
    public byte VoicechatQuality = 4;
    public bool VoicechatEnabled = true; // DONE
                                         //public bool VoicechatEffects = true;   // muffling, health related, shit like that  (and also hearingloss afects this)
                                         // and also if disable voice chat and regular chat if mindwiped (also hearing voicechat is very muffled)
                                         // nah let it be just the speechimpaired and hearimpaired

    [MultiplayerRuleFloat(0, 1500, "M")]
    public float ProximityHearDistance = 55; // in meters

    public bool CharacterYapPublic = true; // broadcast whatever the character is feeling (all body.talker.Talk but the system is too obfuscated )


    public bool Teams = false;
    public bool PVP = false;
    public bool PVPCombatDismember = true;
    public float PVPMoodDebuff = 0.5f;
    public float PVPDamageMultiplier = 1f;

    public bool LateJoinAllowed = true;
    public bool LateJoinSpectate = false;
    public bool SpawnOnPlayer = true;

    public bool AmputateHealthyPlayers = true;

    public float AdditionalBrainRegen = 1f;
    public float AdditionalHealthRegen = 1f; // DONE  // speed up for broken bones, dislocations and shit like that (but except skin and muscle on thorax+head)
    public float AdditionalHealthDecay = 1f; // DONE      // extra bleed and brain damage while the red circle is up and no one is close enough to help
    public bool LastStandAllowed = true;



    public float SelfharmWitnessMoodDebuff = 3f;

    public bool DisconnectShouldSaveAnything => SavePlayerState || SavePlayerInventory || SavePlayerPosition;
    public bool SavePlayerState = true;
    public bool SavePlayerInventory = true;  // on disconnect, remember what the player had,  if false player will drop everything on disconnect (but health saved)
    public bool SavePlayerPosition = true;


    // DEDICATED SERVER STUFF 

    //[KrokoshaRuleDescription("Automatically start game when reaching \"DServerMinPlrsToStart\" players, and automatically skip next layer when \"LayerFinishConditionPlayerPercent\" is reached.")]
    public bool AutoContinue = false; // DONE  

    // for AutoContinue: minimum player count to start the game 
    public ushort AutoMinPlrsToStart = 2;
    public bool AutoExitWhenAllDied = true;
    public bool AutoExitWhenAllLeft = true;



    //getters
    public bool CanReviveOnNextLevel() {
        return !Permadeath && ReviveOnNextLevel;
    }
    public bool CanReviveFromTrader() {
        return !Permadeath && ReviveFromTrader;
    }
    public bool CanCommunicateWithTheDeadTC() {
        return DeadTextchat || Util.IsTutorialWorld();
    }
    public bool CanCommunicateWithTheDeadVC() {
        return DeadVoicechat || Util.IsTutorialWorld();
    }
    public bool CheckIfLayerContinueIsDisabled() {
        return LayerFinishPlrPercent > 100;
    }
    public bool DisableSleep => !EnableSleep;
    public bool DisableTimeManipulation => !EnableTimeManipulation;


    public Dictionary<string,string> ConvertToSerializableDict() {
        var v = new Dictionary<string, string>();

        var prev = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            foreach (var fld in this.GetType().GetFields()) {
                var ogvalue = fld.GetValue(this);

                v[fld.Name] = ogvalue.ToString();
            }
        } finally {
            CultureInfo.CurrentCulture = prev;
        }

        return v;
    }
    public static MultiplayerGameRules ConvertFromSerializableDict(Dictionary<string,string> r) {
        var v = new MultiplayerGameRules();

        foreach (var item in r) {
            var rule_name = item.Key;
            var rule_value = item.Value;

            var field2 = v.GetType().GetField(rule_name);
            if (field2 == null) {
                log.warn($"RULE DECODER: UNKNOWN RULE: {rule_name} (attempt to set it to: {rule_value})");
                continue;
            }
            object obj4;
            
            try {
                if (field2.FieldType == typeof(bool)) {
                    obj4 = Con.ParseBool01(rule_value);
                } else {
                    obj4 = System.ComponentModel.TypeDescriptor.GetConverter(field2.FieldType).ConvertFromInvariantString(rule_value);
                }
            } catch (Exception e) {
                log.warn($"RULE DECODER: SET RULE, BUT VALUE IS INVALID:\nINPUT: {rule_name} = {rule_value}\n{e.Message}");
                continue;
            }

            TypedReference rulesref = __makeref(v);
            field2.SetValueDirect(rulesref, obj4);

        }

        return v;
    }

    public void SetDefault() {
        // do nothing
    }

    public MultiplayerGameRules() { 
        // do nothing, set all the shit individually
    }
}


public class TagPlayerInfo {
    public Multiplayer.IntroductionPlayerInfo intro;
    public knetid plrId;
    public NetPlayer netPlayer;
    public ScavPlayer plr;
    public Dictionary<string, object> data;
}


[AttributeUsage(AttributeTargets.Method)]
public abstract class NetworkMessageReceiverAttribute : Attribute {
    
    public enum ReceiverType : byte { 
        Client,
        Server
    }

    public byte GetMessageId() {
        if (this is ServerReceiverAttribute skra) {
            return (skra.messageId);
        }
        if (this is ClientReceiverAttribute ckra) {
            return (ckra.messageId);
        }
        throw new AccessViolationException();
    }
    public string GetMessageName() {
        return Net.ByteMsgIdToName(GetMessageId());
    }
    public ReceiverType GetReceiverType() {
        if (this is ServerReceiverAttribute skra) {
            return ReceiverType.Server;
        }
        if (this is ClientReceiverAttribute ckra) {
            return ReceiverType.Client;
        }
        throw new AccessViolationException();
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class CTPostInitAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Method)]
public class ServerReceiverAttribute : NetworkMessageReceiverAttribute {
    public byte messageId { get; private set; }
    public ServerReceiverAttribute(byte name) {
        this.messageId = name;
    }
}
[AttributeUsage(AttributeTargets.Method)]
public class ClientReceiverAttribute : NetworkMessageReceiverAttribute {
    public byte messageId { get; private set; }
    public bool ignore_host { get; private set; }
    public ClientReceiverAttribute(byte name, bool ignore_host = true) {
        this.messageId = name;
        this.ignore_host = ignore_host;
    }
}


abstract public class CasualtiesTogetherSingleton : MonoBehaviour {
}



public class Multiplayer : MonoBehaviour {

    public static ulong GAME_FILES_HASH = 0;


#if DEBUG
    public const bool IS_DEBUG_BUILD = true;
#else
    public const bool IS_DEBUG_BUILD = false;
#endif
#if RELEASETEST || PLAYTEST
    public const bool IS_TESTING_BUILD = true;
#else
    public const bool IS_TESTING_BUILD = false;
#endif
#if RELEASE
    public const bool IS_RELEASE_BUILD = true;
#else
    public const bool IS_RELEASE_BUILD = false;
#endif


    private void OnDestroy() {

        try {
            try {
                if (Plugin.FORCE_DISABLE_MP_MOD) {

                } else {
                    if (Plugin.s != null) {
                        Plugin.s.GetOrAddComponent<Plugin.LoadErrorNotifier>();
                    }
                    if (String.IsNullOrWhiteSpace(Plugin.LoadErrorNotifier.errorstr)) {
                        Plugin.LoadErrorNotifier.errorstr = "Multiplayer Singleton got deleted???";
                    }
                }
                
                Together.log.l("Multiplayer.OnDestroy()");
            } catch (System.Exception e) {
                log.error("Multiplayer.OnDestroy: " + e.ToString());
            }
        } catch (System.Exception _) {  }

        Application.runInBackground = Plugin.OG_RUNINGACKGROUND;
        
    }

    private void OnApplicationQuit() {
        Application.runInBackground = Plugin.OG_RUNINGACKGROUND;
    }

    public static bool verbose => DebugMenuSettings.is_verbose;
    public const int CONST_MAX_POSSIBLE_PLAYER_COUNT = 200;

    public const int MAX_NAME_LENGTH = 16;

    internal static byte PLAYER_COUNT_LIMIT {
        get { return rules.PLAYER_COUNT_LIMIT; }
        set { rules.PLAYER_COUNT_LIMIT = value; }
    }

    public static string multiplayerStatusMessage = "Game initialized.";
    public static double lastMultiplayerStatusMessageChangeTime = 0;

    public static bool showMultiplayerMenu = true;
    
    public static string Server_GetJoinSecret() {
        if (SERVER_JOIN_SECRETS.Count > 0) {
            return SERVER_JOIN_SECRETS.Last();
        }
        return "1";
    }
    public static List<string> SERVER_JOIN_SECRETS = new();
    public static string CLIENT_JOIN_SECRET = "0"; // used by Discord OnJoin and the approved OnJoinRequested
    public static string INPUT_PASSWORD = "";
    public static bool SERVER_TOGGLE_CHECK_GAME_HASH = true;
    public static bool SERVER_TOGGLE_ENFORCE_MODLIST = false;
    public static bool SERVER_TOGGLE_SHOULD_HOST_DEDICATED = false;

    public static MultiplayerGameRules rules = new MultiplayerGameRules();
    public static readonly JsonPresetsSaver<MultiplayerGameRules> RulesSaver = new("mp_rule_presets.json");

    public static bool DedicatedServerDisableVisuals = false;


    public static event Action OnSceneChangeOrWorldStartGenerate;
    internal static void _InvokeOnSceneChangeOrWorldStartGenerate() {
        OnSceneChangeOrWorldStartGenerate?.Invoke();
    }


    void Update2s() {
        while (SERVER_JOIN_SECRETS.Count < 4) {
            SERVER_JOIN_SECRETS.Add(Guid.NewGuid().ToString("N"));
        }
        SERVER_JOIN_SECRETS.Add(Guid.NewGuid().ToString("N"));

        while (SERVER_JOIN_SECRETS.Count > 12) {
            SERVER_JOIN_SECRETS.RemoveAt(0);
            SERVER_JOIN_SECRETS.RemoveAt(0);
        }
    }


    void Start() {
        InvokeRepeating(nameof(Update2s), 0.1f, 2);

        if (!Plugin.plrname_override) {
#if RELEASE
            {
                var s = PlayerPrefs.GetString("KrokoshaMultiplayer_LastPlayerName");
                if (!String.IsNullOrWhiteSpace(s)) {
                    UIMainMenu.USERINPUT_NAME = s;
                }
            }
            {
                var s = PlayerPrefs.GetString("KrokoshaMultiplayer_LastPlayerColor");
                if (!String.IsNullOrWhiteSpace(s) && ColorUtility.TryParseHtmlString(s, out var color)) {
                    UIMainMenu.SetInputColor(color);
                } 
            }
#endif
        }

        if (String.IsNullOrEmpty(Net.MyServerInfo.name)) {
            Net.MyServerInfo.name = "Expedition " + UIMainMenu.USERINPUT_NAME;
            {
#if RELEASE
                var s = PlayerPrefs.GetString("KrokoshaMultiplayer_LastMyLobbyName");
                if (!String.IsNullOrWhiteSpace(s)) {
                    Net.MyServerInfo.name = s;
                }
#endif
            }
        }


        if (!ScavPlayer.CheckIfPlrColorIsValid(UIMainMenu.LAST_VALID_INPUT_COLOR)) {
            log.l("Picked random color cuz its not picked.");
            
            UIMainMenu.SetInputColor(ScavPlayer.NAMETAG_DEFAULT_COLORS_RANDOMIZED[new System.Random().Next(0, ScavPlayer.NAMETAG_DEFAULT_COLORS_RANDOMIZED.Length)]);
        }
    }

    

#if !RELEASE
    static NetBody selected_person_for_the_fucking_devautoaim_keybind_tging = null;
#endif
    void Update() {

        Net.DoUpdate();

        if (ConsoleScript.instance?.active == true) {
            Con.UpdatePlayerListAutocomplete();
        }


#if !RELEASE 
        
        if (Util.IsInWoundView()) {

            var limb = PlayerCamera.main.selectedLimb;
            if (limb != null) {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad0)) {
                    var n = UnityEngine.Input.GetKeyDown(KeyCode.LeftShift) ? -10f : 10f;
                    limb.bleedAmount += n;
                    limb.skinHealth -= n;
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad1)) {
                    limb.shrapnel += UnityEngine.Input.GetKeyDown(KeyCode.LeftShift) ? -1 : 1;
                    if (limb.shrapnel < 0) {
                        limb.shrapnel = 0;
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad2)) {
                    if (limb.broken) {
                        limb.MendBone();
                    } else {
                        limb.BreakBone();
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad3)) {
                    if (limb.dislocated) {
                        limb.UnDislocate();
                        limb.dislocationTimer = 0;
                    } else {
                        limb.Dislocate();
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad4)) {
                    if (limb.dismembered) {
                        limb.dismembered = false;
                        limb.gameObject.SetActive(value: true);
                    } else {
                        limb.Dismember();
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad5)) {
                    var n = UnityEngine.Input.GetKeyDown(KeyCode.LeftShift) ? -10f : 10f;
                    limb.infectionAmount += n;
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
            }
        } 

#endif



    }





    public static bool AdditionalApprovalCheck( ref NetDataReader reader, string plrname, ref string reject_reason) {
        if (reject_reason == null) {
            reject_reason = "Connection rejected";
        }

        if (Util.IsInWorld() && !rules.LateJoinAllowed) {
            reject_reason = "Game is already in progress.";
            return false;
        }

        return true;
    }


    public static string FULL_VERSION_TAG { get; private set; }
    

    public static void DoMultiplayerStatusMessageLog(string msg) {
        lastMultiplayerStatusMessageChangeTime = Time.realtimeSinceStartupAsDouble;
        multiplayerStatusMessage = msg;
        msg = "Status: " + msg;
        log.l(msg);
    }
    public static void DoMultiplayerStatusMessageError(string msg) {
        lastMultiplayerStatusMessageChangeTime = Time.realtimeSinceStartupAsDouble;
        multiplayerStatusMessage = msg;
        msg = "Status ERROR: " + msg;
        log.error( msg);
    }



    public static void SetServerName(string newname) {

        if (newname.Length > Net.MAX_SERVER_NAME_LENGTH) {
            newname = newname.Substring(0, Net.MAX_SERVER_NAME_LENGTH);
        }

        if (newname != Net.MyServerInfo.name) {
            newname = Multiplayer.SanitizeRichTextAllowSimpleTags(newname);
            if (newname == Net.MyServerInfo.name) return;

            Net.MyServerInfo.name = newname;
            if (!Net.IsRunning || Net.IsServer) {
                Net.CurrentServerInfo.name = newname;
            }

            if (IsNetworkActiveAndIsServer()) {
                ServerMain._Server_OnServerNameChange();
            }
        }
    }
    public static void SetServerDescription(string newdesc) {

        if (newdesc.Length > Net.MAX_SERVER_DESC_LENGTH) {
            newdesc = newdesc.Substring(0, Net.MAX_SERVER_DESC_LENGTH);
        }
        if (newdesc.CountIndices('\n') > Net.MAX_SERVER_DESC_NEWLINES) {
            newdesc = newdesc.Substring(0, newdesc.LastIndexOf('\n'));
        }

        if (newdesc != Net.MyServerInfo.description) {
            newdesc = Multiplayer.SanitizeRichTextAllowSimpleTags(newdesc);
            if (newdesc == Net.MyServerInfo.description) return;

            Net.MyServerInfo.description = newdesc;
            if (!Net.IsRunning || Net.IsServer) {
                Net.CurrentServerInfo.description = newdesc;
            }

            if (IsNetworkActiveAndIsServer()) {
                ServerMain._Server_OnServerNameChange();
            }
        }
    }


    public static string SanitizeTextInputAllowSpaces(string s) {
        return Regex.Replace(s, "[^a-zA-Z0-9_#$()+=\\- ]", "").TrimStart();
    }
    public static string SanitizeTextInput(string s) {
        return Regex.Replace(s, "[^a-zA-Z0-9_#$()+=-]", "");
    }
    public static string SanitizeRichText(string s) {
        StringBuilder b = new(s.Length);
        bool inside = false;
        foreach (char c in s) {
            if (c == '<') {
                inside = true;
            } else if (inside) {
                inside = c != '>';
            } else {
                b.Append(c);
            }
        }
        return b.ToString();
    }
    public static string SanitizeRichTextAllowSimpleTags(string s) {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        StringBuilder b = new(s.Length);

        int i = 0;
        while (i < s.Length) {
            if (s[i] != '<') {
                b.Append(s[i++]);
                continue;
            }

            int end = s.IndexOf('>', i + 1);
            if (end == -1) {
                b.Append(s[i++]);
                continue;
            }

            string tag = s.Substring(i + 1, end - i - 1).Trim();

            // Simple tags
            if (tag is "b" or "/b" or "i" or "/i" or "s" or "/s") {
                b.Append('<').Append(tag).Append('>');
            }
            // <color=value> and </color>
            else if (tag.StartsWith("color=", StringComparison.OrdinalIgnoreCase)) {
                string value = tag.Substring(6).Trim();

                // Only allow simple color values, e.g. red, #FF0000, #FF0000FF
                if (Regex.IsMatch(value, @"^(?:[a-zA-Z]+|#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)$")) {
                    b.Append("<color=").Append(value).Append('>');
                }
            } else if (tag.Equals("/color", StringComparison.OrdinalIgnoreCase)) {
                b.Append("</color>");
            }

            i = end + 1;
        }

        return b.ToString();
    }





    public const string CANT_PLAY_SP_MSG = "You can't play singleplayer with MP mod active.\nTo play singleplayer go to \"MP Mod Menu > Settings > General > Deactivate Multiplayer Mod\"\nOr start an empty server.";





    public static bool DEV_VERBOSE_SHOW_CURSOR_INFO = true;

    static void _GUI_RenderMainGUI() {


#if !RELEASE
        {
            var t = "";
            if (Net.ClientEntityManager != null) {
                var _entityManager = Net.ClientEntityManager;
                t = $@"
C_ServerTick: {_entityManager.ServerTick}
C_Tick: {_entityManager.Tick}
C_LPRCS: {_entityManager.LastProcessedTick}
C_StoredCommands: {_entityManager.StoredCommands}
C_Entities: {_entityManager.EntitiesCount}
C_ServerInputBuffer: {_entityManager.ServerInputBuffer}
C_LerpBufferCount: {_entityManager.LerpBufferCount}
C_LerpBufferTime: {_entityManager.LerpBufferTimeLength}
Jitter: {_entityManager.NetworkJitter}
PendingRemove: {_entityManager.PendingToRemoveEntites}";
            } else
            if (Net.ServerEntityManager != null) {
                var _entityManager = Net.ServerEntityManager;
                t = $@"
S_Tick: {_entityManager.Tick}
S_Entities: {_entityManager.EntitiesCount}
S_LerpFactor: {_entityManager.LerpFactor}";
            }

            var content = KrokV5Optimization.FixGuiContent.Content(t, 35);
            var size = GUI.skin.label.CalcSize(content);

            GUI.Label(new Rect(Screen.width - 400, 20, size.x, size.y), t);
            
        }
#endif




        if (Con._DEV_HIDEHUD) {
            return;
        }



        if (UnityEngine.Input.GetKey(KeyCode.K) && UnityEngine.Input.GetKeyDown(KeyCode.R)) {

        }

        float y = 20;
        const float elsize = 20f;

        var skibidi = Application.version;
        if (skibidi.IsNullOrWhiteSpace()) {
            skibidi = Plugin.TARGET_GAME_VERSION;
        }

#if TESTING
        GUI.Label(new Rect(20, y, 1100, elsize), $"MULTIPLAYER CO-OP MOD v{Plugin.FULL_MOD_VERSION} FOR  CASUALTIES: UNKNOWN {skibidi} APPID: {KSteam.STEAM_APPID} \"TESTING BUILD\" \"TESTING BUILD\" 2" ); 
        y += elsize;
#elif !RELEASE
        GUI.Label(new Rect(20, y, 1100, elsize), $"MULTIPLAYER CO-OP MOD v{Plugin.FULL_MOD_VERSION} FOR  CASUALTIES: UNKNOWN {skibidi} APPID: {KSteam.STEAM_APPID} \"TESTING BUILD\" \"TESTING BUILD\" \"TESTING BUILD\" v");
        y += elsize;
#endif



        if (Net.IsDedicatedServer) {
            GUI.Label(new Rect(20, y, 500, elsize), "DEDICATED SERVER: SWITCH COUNTER: " + Mathf.Round(ServerMain._ded_server_switch_counter).ToString());
            y += elsize;
        }

        if (verbose) {
            GUI.Label(new Rect(20, y, 500, elsize), "FPS: " + (ServerMain.CURRENT_FPS).ToString() + "   TPS: " + (ServerMain.CURRENT_TPS).ToString());
            y += elsize;


            GUI.Label(new Rect(20, y, 500, elsize), "UTC: " + DateTime.UtcNow.ToString("HH:mm:ss.fff"));
            y += elsize;

        }


        if (verbose && DEV_VERBOSE_SHOW_CURSOR_INFO) {
            var yy = Screen.height - UnityEngine.Input.mousePosition.y + elsize;
            if (Util.TryGetLocalBody(out var _)) {

                if (MinigameBase.main?.currentMinigame) {

                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "mg handpos: " + MinigameBase.main.handPos.ToString());
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "mg sprite : " + MinigameMPManager.GetLocalHandSpriteIndex().ToString());
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "mg lmb : " + MinigameBase.main.handClicking.ToString());
                    yy += elsize;

                } else {

                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "dist: " + Vector2.Distance(PlayerCamera.main.body.transform.position, (Vector2)Camera.main.ScreenToWorldPoint(UnityEngine.Input.mousePosition)));
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "pos: " + ((Vector2)Camera.main.ScreenToWorldPoint(UnityEngine.Input.mousePosition)).ToString());
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "vel: " + (PlayerCamera.main.body.rb.velocity).ToString());
                    yy += elsize;
                }

            }
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "time: " + Math.Round(Time.realtimeSinceStartupAsDouble));
            yy += elsize;

#if !RELEASE
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "UI scaled: " + (Vector2Int16)(UIBullshit.GetCursorPosInGUI()));
            yy += elsize;
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "UI unscaled: " + (Vector2Int16)(UIBullshit.GetCursorPosInGUI() / UIBullshit.uiScale));
            yy += elsize;
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "UI scalar: " + (Util.GetCursorPos() / Util.GetResolution()));
            yy += elsize;
#endif
        }

        if (Util.TryGetLocalBody(out var LOCALBODY)) {

            if (verbose) {
                var pos = Util.GetLocalBodyPosition();
                if (UIInGame.SpectatorModeActive) {
                    pos = PlayerCamera.main.transform.position;
                }

                GUI.Label(new Rect(20, y, 500, elsize), $"Position: x:{(int)pos.x} y:{(int)pos.y}");
                y += elsize;

                GUI.Label(new Rect(20, y, 500, elsize), "REGISTERED OBJECTS COUNT: " + NetObjectRegistry.SyncRegistry.Count.ToString());
                y += elsize;

            }


#if !RELEASE
            if (UnityEngine.Input.GetKey(KeyCode.PageUp) || UnityEngine.Input.GetKey(KeyCode.PageDown)) {
                GUI.Label(new Rect(20, y, 500, elsize), "ORTHO SIZE: " + Camera.main.orthographicSize.ToString());
                y += elsize;
            }
#endif

        }

        if (ConsoleScript.instance.noClip) {
            GUI.Label(new Rect(20, y, 200, elsize), "NOCLIP ACTIVE");
            y += elsize;
        }

        if (Con._DEV_GODMODE) {
            GUI.Label(new Rect(20, y, 200, elsize), "GODMODE ACTIVE");
            y += elsize;
        }



        {


            if (UIInGame.SpectatorModeActive) {
                GUI.Label(new Rect(20, y, 400, elsize), "Spectator mode active ");
                y += elsize;
            }
            if (UIInGame.interaction_menu_target_body != null) {
                GUI.Label(new Rect(20, y, 400, elsize), "Player interaction menu active " + UIInGame.interaction_menu_target_body.ToString());
                y += elsize;
            }

#if !RELEASE
            if (WorldGeneration.world) {
                var locplrpos = (PlayerCamera.main.body.alive ? PlayerCamera.main.body.transform.position : Camera.main.transform.position);
                var locplr_syncchunkpos = WorldChunkSync.WorldPosToSyncchunkCoordinateSafe(locplrpos);
                GUI.Label(new Rect(20, y, 700, elsize), $"SYNCCHUNK: {locplr_syncchunkpos.x} {locplr_syncchunkpos.y}");
                y += elsize;
            }
            if (Util.IsInWoundView()) {

                if (Time.realtimeSinceStartup % 1f > 0.6f) {
                    if (Net.IsClient && Net.IsRunning) {
                        GUI.Label(new Rect(20, y, 400, elsize), "CLIENT CANT DO DEV LIMB HEALTH MANIPULATION");
                    } else {
                        GUI.Label(new Rect(20, y, 900, elsize), "(dev limb health binds:   Keypad0 bleeding,  Keypad1 shrapnel,  Keypad2 break bone,  Keypad3 dislocate,  Keypad4 amputate,  Keypad5 infect): ");
                    }
                }
                y += elsize;
            }
#endif

            if (verbose) {

                if (Util.IsInWoundView() && !Util.IsBodyLocal(WoundView.view.body)) {
                    GUI.Label(new Rect(20, y, 400, elsize), "Viewing health panel of player: " + WoundView.view.body.GetComponent<NetBody>().bodyName);
                    y += elsize;
                }

                if (MinigameMPManager.active_sessions.Count > 0) {
                    GUI.Label(new Rect(20, y, 400, elsize), "MINIGAME SESSIONS: ");
                    y += elsize;
                    foreach (var item in MinigameMPManager.active_sessions) {
                        var str = "";
                        foreach (var plr in item.involved_players) {
                            str += plr.playerName + " " + plr.IsInMinigame.ToString() + " " + item.involved_players.Contains(plr).ToString() + " " + plr.minigame_session + " " + plr.minigame_current_type + " | ";
                        }

#if !RELEASE
                        GUI.Label(new Rect(20, y, 1000, elsize), $"type:{item.minigame_type} id:{item.session_id} -- {str}");
                        //GUI.Label(new Rect(20, y, 1000, elsize), $"type:{item.minigame_type} id:{item.session_id} -- {String.Join(" ", item.involved_players)}");
                        y += elsize;
                        GUI.Label(new Rect(20, y, 1000, elsize), $"     SCI IDS: {String.Join(" ", item.InvolvedPlayersClientIds())}");
                        y += elsize;
                        if (item is MinigameMPManager.KeypadMinigameSession kpses) {

                            GUI.Label(new Rect(20, y, 1000, elsize), $"     input: {kpses.current_input}");
                            y += elsize;
                        }
#endif
                    }
                }

                if (Minigame.game?.currentMinigame != null) {
                        if (Minigame.game.currentMinigame is DislocationMinigame dm) {
                            GUI.Label(new Rect(20, y, 1000, elsize), $"DISLOCATION MINIGAME");
                            y += elsize;
                            GUI.Label(new Rect(20, y, 1000, elsize), $"BONE POS: {DislocationMinigame_CheckForHit_MultiplayerPatch.real_bonepos}");
                            y += elsize;
                            GUI.Label(new Rect(20, y, 1000, elsize), $"BONE VEL: {DislocationMinigame_CheckForHit_MultiplayerPatch.boneVelocity}");
                            y += elsize;
                        } else if (Minigame.game.currentMinigame is LockpingMinigame lm) {
                            GUI.Label(new Rect(20, y, 500, elsize), $"LOCKPICKING MINIGAME");
                            y += elsize;
#if !RELEASE
                            GUI.Label(new Rect(20, y, 500, elsize), $"SWEETSPOT: {lm.correctAngle}");
                            y += elsize;
                            GUI.Label(new Rect(20, y, 500, elsize), $"MaxTurnProgress: {lm.MaxTurnProgress()}");
                            y += elsize;
#endif
                            GUI.Label(new Rect(20, y, 500, elsize), $"HAND ANGLE: {lm.HandAngle}");
                            y += elsize;
                        }


                        GUI.Label(new Rect(20, y, 400, elsize), "HAND VEL: " + MinigameBase.main.handVelocity.ToString());
                        y += elsize;
                        GUI.Label(new Rect(20, y, 400, elsize), "HAND VEL MAGN: " + ((int)MinigameBase.main.handVelocity.magnitude).ToString());
                        y += elsize;
                    }
                }



            }


            bool did_show_status_message = false;

            if (Util.IsInPauseMenuOrMainMenu() || Net.IsDedicatedServer) {
                var corrected_mouse_pos = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);

                y += 10;

            } else {
                if (!Net.IsRunning) {
                    GUI.Label(new Rect(20, y, 1280, elsize * 2), Lang.Get("singleplayer_warn"));
                    y += elsize * 2;
                } else {
                    if (log.verbose) {
                        var mode = Net.IsHost ? "Host" : Net.IsDedicatedServer ? "Server" : "Client";
                        GUI.Label(new Rect(20, y, 200, elsize), "Mode: " + mode);
                        y += elsize;

                        if (!Net.IsConnecting && ScavPlayer.LocalPlayer != null && Net.IsClient) {
                            GUI.Label(new Rect(20, y, 200, elsize), "Ping: " + (ClientMain.LOCAL_PING));
                            y += elsize;
                        }
                    }
                }
            }
            if ( !did_show_status_message && (
                    Time.realtimeSinceStartupAsDouble - lastMultiplayerStatusMessageChangeTime < 10.0 // || !Util.IsInWorld()
            ) ) {
                var ogwordwrap = GUI.skin.label.wordWrap;
                GUI.skin.label.wordWrap = true;

                var txt = "Last Status Message:\n" + multiplayerStatusMessage;

                var texthiehgt = GUI.skin.label.CalcHeight(KrokV5Optimization.FixGuiContent.Content(txt, 36), 400);

                GUI.Label(new Rect(20, y, 400, texthiehgt), txt);
                y += texthiehgt;
                did_show_status_message = true;

                GUI.skin.label.wordWrap = ogwordwrap;
            }




            if (Voicechat.my_output_volume > 0) {
                if (UIBullshit.IsAnyMenuOpen()) {
                    GUI.Label(new Rect(20, y, 300, elsize), "Voicechat current volume level: " + Voicechat.my_output_volume.ToString("F2"));
                    y += elsize;
                } else {
                    GUI.Label(new Rect(20, y, 200, elsize), "Voicechat active.");
                    y += elsize;
                }
            }



#if TESTING
        try {
            
            if (!UnityEngine.Input.GetKey(KeyCode.LeftShift)) {
                
                if (KSteam.Loaded) {
                    GUI.Label(new Rect(20, y, 2000, elsize), "Steam loaded.");
                    y += elsize;

                    if (KSteam.IS_IN_LOBBY) {
                        GUI.Label(new Rect(20, y, 2000, elsize), $"In Lobby: " + "SteamID: " + KSteam.CURRENT_LOBBY.lobby_steamID.ToString() + " - Slots: " + KSteam.CURRENT_LOBBY.members.Count + "/" + KSteam.CURRENT_LOBBY.memberlimit);
                        y += elsize;
                    }

                } else {
                    GUI.Label(new Rect(20, y, 2000, elsize), "STEAM DISABLED ");
                    y += elsize;
                }


                if (Net.Transport != null) {

                    if (Net.Transport is TransportLiteNetLib tlnl) {

                        GUI.Label(new Rect(20, y, 2000, elsize), "LNL ");
                        y += elsize;

                        if (tlnl.CLIENT_PEER != null) {
                            GUI.Label(new Rect(20, y, 2000, elsize), $"CLIENT_PEER STATE: {tlnl.CLIENT_PEER.ConnectionState} ");
                            y += elsize;
                        }

                        GUI.Label(new Rect(20, y, 2000, elsize), $"PEERS: {tlnl.NetManager.ConnectedPeersCount} ");
                        y += elsize;
                        var peers = new List<NetPeer>();
                        tlnl.NetManager.GetConnectedPeers(peers);
                        foreach (var item in peers) {
                            if (item.Tag != null && item.Tag is TagPlayerInfo kple) {
                                GUI.Label(new Rect(20, y, 2000, elsize), $"{item.Id}: {kple.intro.userName} - {item}  ping:{item.Ping} ");
                                y += elsize;
                            } else {
                                GUI.Label(new Rect(20, y, 2000, elsize), $"user {item.Id} - {item}  ping:{item.Ping} ");
                                y += elsize;
                            }
                        }

                    } else {
                        GUI.Label(new Rect(20, y, 2000, elsize), "Steam transport running. " + (KSteam.IS_IN_LOBBY ? $"Is in lobby: {KSteam.CURRENT_LOBBY.lobby_steamID}" : "" ));
                        y += elsize;

#if !RELEASE
                        if (KSteam.IS_IN_LOBBY) {
                            GUI.Label(new Rect(20, y, 2000, elsize), "MEMBERS: " );
                            y += elsize;
                            foreach (var item in KSteam.CURRENT_LOBBY.members) {
                                GUI.Label(new Rect(20, y, 2000, elsize), item.Key.ToString());
                                y += elsize;
                                foreach (var item2 in item.Value.metadata) {
                                    GUI.Label(new Rect(20, y, 2000, elsize), " - " + item2.ToString());
                                    y += elsize;
                                }
                            }
                        }
#endif
                    }

                }
            }

        } catch (Exception e) {
            log.error("Main GUI Error: " + e.ToString());
        } 


#endif
        

    }





    private void OnGUI() {

        if (Con.IsConsoleOpen()) {
            return;
        }

        try {
            _GUI_RenderMainGUI();
        } catch (Exception e) {
            log.error(e);
        }

    }

    internal static NetDataWriter GetClientConnectIntroductionPacket( NetDataWriter writer = null) {

        var credentials_buffer = writer ?? new NetDataWriter();

        credentials_buffer.Put(GAME_FILES_HASH);
        credentials_buffer.Put(FULL_VERSION_TAG, true);
        credentials_buffer.Put(UIMainMenu.USERINPUT_NAME, true);

        var l = credentials_buffer.Length;

        credentials_buffer.Put(INPUT_PASSWORD, true);
        credentials_buffer.Put(UIMainMenu.LAST_VALID_INPUT_COLOR);


        credentials_buffer.Put(CLIENT_JOIN_SECRET, true);
        credentials_buffer.PutArray(GetModGUIDs());


        credentials_buffer.CompressWriter(l);

        return credentials_buffer;
    }

    public struct IntroductionPlayerInfo {
        public ulong fileHash;
        public string version;
        public string userName;
        public string password;
        public string secret;
        public Color24 color;
        public string[] modlist;
        public HashSet<string> modlist_hashset;
    }

    public static bool ReadClientConnectIntroductionPacket(NetDataReader credentialsBuffer, ref IntroductionPlayerInfo userInfo) {
        userInfo.fileHash = 0;
        userInfo.version = "";
        userInfo.userName = "NONAME";
        userInfo.secret = "0";
        userInfo.modlist = null;
        bool aaaaaaaaaaa = true;
        try {
            credentialsBuffer.Get(out userInfo.fileHash);
            credentialsBuffer.Get(out userInfo.version, true);
            credentialsBuffer.Get(out userInfo.userName, true);
            credentialsBuffer = credentialsBuffer.DecompressReader();
            credentialsBuffer.Get(out userInfo.password, true);
            credentialsBuffer.Get(out userInfo.color);
            if (!credentialsBuffer.EndOfData) { // 4.0.0
                credentialsBuffer.Get(out userInfo.secret, true);
                credentialsBuffer.TryGetStringArray(out userInfo.modlist);
            } 
        } catch (Exception e) {
            aaaaaaaaaaa = false;
        }
        return aaaaaaaaaaa;
    }
    public static bool ValidateClientConnectIntroductionPacket(NetDataReader reader, ref IntroductionPlayerInfo userInfo, out string deny_reason, bool check_name = true, bool userHasMasterKey = false) {
        userInfo.userName = "EMPTY_STRING";
        userInfo.color = Color24.black;
        deny_reason = $"Server broke or some shi ig.";

        try {

            if (!Multiplayer.ReadClientConnectIntroductionPacket(reader, ref userInfo)) {
                deny_reason = ( $"Corrupt payload.");
                return false;
            }

            if (!userHasMasterKey) {
                if (userInfo.version != (Multiplayer.FULL_VERSION_TAG) || userInfo.modlist == null) {
#if !RELEASE
                    if (userInfo.modlist == null)
                        deny_reason = ($"modlist == null");
                    else
                        deny_reason = ($"Version mismatch.");
#else
                    deny_reason = ($"Version mismatch.");
#endif
                    return false;
                }
                if (Multiplayer.SERVER_TOGGLE_CHECK_GAME_HASH) {
                    if (Multiplayer.GAME_FILES_HASH != userInfo.fileHash) {
                        deny_reason = ($"Game file Hashes mismatch.");
                        return false;
                    }
                }
            }
            if (check_name) {
                if (userInfo.userName.Length <= 2) {
                    deny_reason = ( $"Name too short.");
                    return false;
                }
                if (userInfo.userName != Multiplayer.SanitizeTextInput(userInfo.userName)) {
                    deny_reason = ( $"Corrupt payload.");
                    return false;
                }
            }
            
            if (!userHasMasterKey) {
                if (Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST && !userHasMasterKey) {
                    if (!(new HashSet<string>(GetModGUIDs()).SetEquals(userInfo.modlist))) {
                        deny_reason = ($"This server requires different set of mods.");
                        return false;
                    }
                }
#if TESTING
                log.l($"plr {userInfo.userName}  DISCORD SECRET TEST:{userInfo.secret} ");
#endif
                if (Multiplayer.SERVER_JOIN_SECRETS.Contains(userInfo.secret)) {
                    // move it to the front to get removed faster 
                    Multiplayer.SERVER_JOIN_SECRETS.Remove(userInfo.secret);
                    Multiplayer.SERVER_JOIN_SECRETS.Insert(0, userInfo.secret);
                } else {
                    if (!String.IsNullOrEmpty(Multiplayer.INPUT_PASSWORD) && userInfo.password != Multiplayer.INPUT_PASSWORD) {
                        deny_reason = ($"Wrong password.");
                        return false;
                    }
                }

                if (ScavPlayer.CheckIfPlrColorIsValid(userInfo.color)) {
                } else {
                    deny_reason = ($"Invalid color.");
                    return false;
                }
            }
            

            deny_reason = "Success";
            userInfo.password = null;
            return true;
        } catch {
            deny_reason = $"Corrupt payload.";
            return false ;
        }
    }







    #region OLD_SENDERS


    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(true);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, bool data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    public static void Client_SendSimpleMessageToServer(in byte name, byte data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }

    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, byte data1, Vector2 data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data1);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, Vector2 data, Vector2 data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, ushort data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, byte data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, ushort data2, byte data3, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        writer.Put(data3);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }

    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, float data2, byte data3, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        writer.Put(data3);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, float data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }
    // just send a reliable message
    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, byte data2, byte data3, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        writer.Put(data3);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }



    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
        // WRITER END;
    }
    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, ushort data, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
        // WRITER END;
    }
    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, Vector2 data, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
        // WRITER END;
    }
    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, Vector2 data, Vector2 data2, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        writer.Put(data);
        writer.Put(data2);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
        // WRITER END;
    }
    public static void Server_SendSimpleMessageToOneClient(in NetmsgId name, ushort clientId, bool reliable = true) => Server_SendSimpleMessageToOneClient((byte)name, clientId, reliable);

    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(true);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
        // WRITER END;
    }
    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, ushort data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
        // WRITER END;
    }
    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, float data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
        // WRITER END;
    }
    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, Vector2 data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
        // WRITER END;
    }
    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, string data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data, true);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
        // WRITER END;
    }

    #endregion



    // i might be stupid or this is really how they want us to shutdown the network,  like wtf why not expose ShutdownInternal bro, unity devs are stupid af bro
    public static void ShutdownNetwork()  {
        UIInGame.StopSpectatorMode();
        
        GoBackToMainMenuIfImClientAndInGame();

        Net.ShutdownReset();
    }

    public static void _JustDisconnect() {
        UIInGame.StopSpectatorMode();
        
        Net.ShutdownReset();
    }



    readonly static float OG_dislocationHealSpeed = Limb.dislocationHealSpeed;
    readonly static float OG_boneHealSpeed = Limb.boneHealSpeed;

    public static void ApplyGameRules() {

        Limb.dislocationHealSpeed = OG_dislocationHealSpeed + OG_dislocationHealSpeed * rules.AdditionalHealthRegen;
        Limb.boneHealSpeed = OG_boneHealSpeed + OG_boneHealSpeed * rules.AdditionalHealthRegen;

        foreach (var fld in typeof(MultiplayerGameRules).GetFields()) {
            //GUI.Label(new Rect(20, newy, 200, elsize), $"{fld.Name} [{fld.FieldType.Name}] // current: {fld.GetValue(KrokoshaScavMultiplayerComponent.rules)}");

            var ogvalue = fld.GetValue(Multiplayer.rules);

            if (fld.HasAttribute(typeof(MultiplayerRuleByteAttribute))) {
                var attr = fld.GetAttribute<MultiplayerRuleByteAttribute>();
                TypedReference rulesref = __makeref(Multiplayer.rules);

                byte p = (byte)ogvalue;

                if (p < attr.min) {
                    p = attr.min;

                    fld.SetValueDirect(rulesref, p);
                }
                if (p > attr.limit) {
                    p = attr.limit;

                    fld.SetValueDirect(rulesref, p);
                }
            } 
        }


        if (rules.VoicechatQuality >= Voicechat.STANDART_FREQUENCIES.Length || rules.VoicechatQuality < 0) {
            rules.VoicechatQuality = (byte)Voicechat.STANDART_FREQUENCIES.Length;
        }
        Voicechat.SAMPLE_RATE = Voicechat.STANDART_FREQUENCIES[rules.VoicechatQuality];

        if (Util.IsInWorld()) {
            try {

                var txt = PlayerCamera.main.timescaleText;
                if (txt != null) {
                    txt.transform.parent.gameObject.SetActive(rules.EnableTimeManipulation);
                }

            }  catch (Exception e) {
                log.error("TimeManipulation UI switch ERROR: " + e.ToString());
            }
        }


        Plugin.s.SendMessage("KrokoshaSingletonEvent_OnRulesUpdate", SendMessageOptions.DontRequireReceiver);




    }
    

    public static bool IsNetworkActiveAndIsServer() {
        return Net.IsRunning && Net.IsServer; //   NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer
    }
    public static bool IsNetworkActiveAndIsClient() {
        return Net.IsRunning && Net.IsClient; //   NetworkManager.Singleton.IsClient
    }
    public static bool IsNetworkActiveOrIsInGame() {
        return Net.IsRunning || Util.IsInWorld();
    }
    public static bool IsNetworkActiveAndIsInGame() {
        return Net.IsRunning && Util.IsInWorld();
    }
    public static bool IsNetworkActiveAndIsWorldGenerated() {
        return Net.IsRunning && Util.IsWorldGenerated();
    }



    public static void GoBackToMainMenuIfImClientAndInGame() {
        if (Net.IsClient && Util.IsInWorld()) {
            DoMultiplayerStatusMessageLog("Went back to main menu because disconnected."); 
            Multiplayer.showMultiplayerMenu = true;
            PlayerCamera.main.ToMainMenu();
        }
    }



    public static void ResetNetworkManager() {
#if MP_GAMEMODES
        GamemodeManager.DeleteGamemode();
#endif

        GoBackToMainMenuIfImClientAndInGame();

        Net.ShutdownReset();
    }


    public delegate void HandleMessageDelegate(knetid senderClientId, LiteNetLib.Utils.NetDataReader messagePayload);

    internal static List<Delegate> all_postinit_from_attributes = new();
    internal static Dictionary<NetworkMessageReceiverAttribute, HandleMessageDelegate> all_network_receivers_from_attributes = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<PluginInfo> GetBepinPlugins() {
        var l = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.ToList();

        var toremove_guids = new string[] {
            MyPluginInfo.PLUGIN_GUID,
            "dannad.krmultiupdater",
        };

        l.RemoveAll(x => x.Metadata == null || toremove_guids.Contains(x.Metadata.GUID));
        return l;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string[] GetModGUIDs() {
        return GetBepinPlugins().Select(x => x.Metadata.GUID).ToArray();
    }


    static void _MpOnSceneChangeOrWorldStartGenerate() {
        try {
            if (Util.IsInWorld()) {
                if (WorldGeneration.world != null) {
                    log.l($"SCENE CHANGE: Is In World,  Layer: {WorldGeneration.world.biomeDepth}");
                } else {
                    log.l($"SCENE CHANGE: Is In World,  No world?");
                }
            } else {
                log.l($"SCENE CHANGE: Is In Main Menu ");
            }
        } catch (Exception e) {
            log.error("OnSceneChangeOrWorldStartGenerate Scene log: " + e.ToString());
        }



        foreach (var i in ScavPlayer.ClientIdToPlayerDict) {
            if (i.Value.ServerPlayerState != null) {
                i.Value.ServerPlayerState.OnSceneChangeOrWorldStartGenerate();
            }
            i.Value.LateJoin = false;
        }
        RadiationLine_Update_MultiplayerPatch.timeGone = 0;
        ServerMain._ded_server_switch_counter = -20;

        if (MoodleManager.main != null) {
            MoodleManager.main.body = (Util.GetLocalBody());
        }

        if (Net.Transport != null && Net.Transport is TransportLiteNetLib tlnl) { // reset statistics
            tlnl.NetManager.Statistics.Reset();
        }

        {
            var state = ClientMain.ServerWorldState;
            if (state != null) {
                state.ambientLightIntensity.Value = WorldGeneration_UpdateAmbientLight_MultiplayerPatch.GetRunSettingsAmbientLightIntensity();
            }
        }
    }

    static internal List<Component> Singletons = new();

    // CALLED ONLY FROM PLUGIN ENTRY POINT
    internal static void _FIRST_INIT()  {
        FULL_VERSION_TAG = Application.version + "_MPv" + Plugin.MOD_VERSION;

#if !RELEASE
        log.l("Mod list: " + String.Join(", ", GetBepinPlugins()));
#endif

        try {
            foreach (var x in CoopKeybinds.as_dict) {
                Util.RegisterKeybind(x.Key, x.Value);
            }
        } catch (Exception e) {
            log.error("FirstInit -> RegisterKeybind\n" + e.ToString());
        }

        {

            var colors = new Color[]
            { // ai generated colors
                // Oranges
                new Color(1f, 0.5f, 0f),        // Orange
                new Color(1f, 0.4f, 0f),        // Dark Orange
                new Color(1f, 0.6f, 0.2f),      // Light Orange

                // Purples
                new Color(0.5f, 0f, 1f),        // Purple
                new Color(0.6f, 0.2f, 0.8f),    // Soft Purple
                new Color(0.4f, 0f, 0.6f),      // Dark Purple

                // Pinks
                new Color(1f, 0.4f, 0.7f),      // Hot Pink
                new Color(1f, 0.6f, 0.8f),      // Light Pink
                new Color(0.9f, 0.3f, 0.5f),    // Rose

                // Greens
                new Color(0.5f, 1f, 0.5f),      // Light Green
                new Color(0.2f, 0.8f, 0.2f),    // Bright Green
                new Color(0.1f, 0.5f, 0.1f),    // Dark Green
                new Color(0.3f, 0.7f, 0.3f),    // Mint

                // Blues
                new Color(0.4f, 0.7f, 1f),      // Sky Blue
                new Color(0.2f, 0.4f, 0.8f),    // Deep Blue
                new Color(0.5f, 0.8f, 1f),      // Light Sky
                new Color(0.1f, 0.3f, 0.6f),    // Navy-ish

                // Teals
                new Color(0f, 0.5f, 0.5f),      // Teal
                new Color(0f, 0.7f, 0.7f),      // Bright Teal

                // Yellows
                new Color(1f, 0.9f, 0.3f),      // Soft Yellow
                new Color(1f, 0.8f, 0f),        // Golden
                new Color(0.9f, 0.7f, 0.1f),    // Mustard

                // Browns
                new Color(0.6f, 0.3f, 0f),      // Brown
                new Color(0.4f, 0.2f, 0.1f),    // Dark Brown
                new Color(0.7f, 0.5f, 0.3f),    // Light Brown

                // Grays
                //new Color(0.7f, 0.7f, 0.7f),    // Light Gray
                //new Color(0.5f, 0.5f, 0.5f),    // Medium Gray
                //new Color(0.3f, 0.3f, 0.3f),    // Dark Gray

                // Extra vibrant gamer-style colors
                new Color(0f, 1f, 0.6f),        // Neon Green
                new Color(1f, 0f, 0.6f),        // Neon Pink
                new Color(0.6f, 0f, 0f),        // Dark Red
                new Color(0.8f, 0.2f, 0.2f),    // Soft Red
                new Color(0.6f, 0.8f, 0.2f),    // Lime
            };

            ScavPlayer.NAMETAG_DEFAULT_COLORS = ScavPlayer.NAMETAG_DEFAULT_COLORS.AddRangeToArray(colors);


            colors.ShuffleRandom();

            ScavPlayer.NAMETAG_DEFAULT_COLORS_RANDOMIZED = (new Color[] {
                Color.red,
                Color.green,
                Color.blue,
                Color.cyan,
                Color.yellow,
                Color.magenta,
                Color.white
            }).AddRangeToArray(colors);
        }




        try {
            if (!Util.IsInWorld()) {
                UIManager.OnMainMenuLoad();
            }
        } catch (Exception e) {
            log.error("FirstInit -> UIManager.OnMainMenuLoad\n" + e.ToString());
        }

        if (CoUtils_instance_MultiplayerPatch.original_local_instance == null) {
            CoUtils.instance.DurationOf("calling_this_just_to_trigger_the_getter");
        }

        OnSceneChangeOrWorldStartGenerate += _MpOnSceneChangeOrWorldStartGenerate;

        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnLoaded;




        Singletons.Add(Plugin.s.AddComponent<DebugHelp>() );
        Singletons.Add(Plugin.s.AddComponent<SharedMain>());
        Singletons.Add(Plugin.s.AddComponent<ClientMain>());
        Singletons.Add(Plugin.s.AddComponent<ServerMain>());
		

		// do reflection shi
		var my_assembly = Assembly.GetAssembly(typeof(CasualtiesTogetherSingleton));
        var my_types = my_assembly.GetTypesSafely();

        // create and initialize all sub systems components

        foreach (Type type in my_types) {
            try {

                UIMainMenu.GetSettingsFromStaticClass(type); // to trigger the default value saver thing


                if (type.IsClass && !type.IsAbstract && type.IsSubclassOf(typeof(CasualtiesTogetherSingleton))) {
#if DEBUG && false
                    Plugin.log.LogInfo($"DEV: Found Singleton to add: {type.Name}");
#endif
                    Singletons.Add(Plugin.s.AddComponent(type));
                }


                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (MethodInfo methodInfo in methods) {
                    try {
                        var netReceiverAttribute = methodInfo.GetAttribute<NetworkMessageReceiverAttribute>(true);
                        if (netReceiverAttribute != null) {
#if DEBUG && false
                            Plugin.log.LogInfo($"Found ServerReceiverAttribute method: {sreciver.GetReceiverType()}  {sreciver.GetMessageName()}   ");
#endif
                            all_network_receivers_from_attributes.Add(
                                netReceiverAttribute,
                                methodInfo.CreateDelegate<HandleMessageDelegate>()
                            );
                        } else {
                            var initAttribute = methodInfo.GetAttribute<CTPostInitAttribute>(true);
                            if (initAttribute != null) {
                                all_postinit_from_attributes.Add(methodInfo.CreateDelegate<Action>());
                            }
                        }
                    } catch (Exception e) {
                        log.error($"FirstInit -> method scanning : {methodInfo}: \n{e.ToString()}");
                    }
                }

            } catch (Exception e) {
                log.error($"FirstInit -> class scanning loop: TYPE:{type} \n{e.ToString()}");
            }
        }


        Util.DelayCallLambda(0.1f, () => {
            if (!win32windowtitlechanger.changed_the_title) win32windowtitlechanger.SetTitle(_PickGameWindowTitle());
        });

        Singletons.Reverse();

        CalculateModHash();


        foreach (var type in all_postinit_from_attributes) {
            try {
                type.DynamicInvoke();
            } catch (Exception e) {
                log.error($"FirstInit -> PostInit: {type.GetMethodInfo()}\n{e.ToString()}");
            }
        }
    }
    static void CalculateModHash() {

        try {
            {
                var assemblycsharppath = Path.Combine(
                    Application.dataPath,
                    "Managed/Assembly-CSharp.dll"
                );
                if (File.Exists(assemblycsharppath)) {
                    GAME_FILES_HASH = Util.GetFileHash(assemblycsharppath);
                } else {
                    DoMultiplayerStatusMessageError("GAME HASH CALCULATION: Assembly-CSharp.dll DOES NOT EXIST????");
                }
            }

            {
                var asm = Assembly.GetExecutingAssembly();
                var mpmodassemblypath = asm.Location;

                if (File.Exists(mpmodassemblypath)) {
                    GAME_FILES_HASH += Util.GetFileHash(mpmodassemblypath);
                } else {
                    DoMultiplayerStatusMessageError($"GAME HASH CALCULATION: {asm.FullName} DOES NOT EXIST????");
                }
            }

        } catch (Exception e) {
            log.error("GAME HASH CALCULATION: " + e.ToString());
        }
        if (FULL_VERSION_TAG.EndsWith("fork")) {
            var str = ((ushort)GAME_FILES_HASH).ToString("X");
            FULL_VERSION_TAG += str;
            Plugin.FULL_MOD_VERSION += str;
        }

        GlobalDark_Awake_MultiplayerPatch.SetBetaBuildMPText();
    }

    


    public static void OnSceneLoaded(Scene a, LoadSceneMode b) {
        
#if DEBUG
        Plugin.log.LogWarning("DEV: SCENE LOADED");
#endif
        ServerMain._ded_server_switch_counter = -10;
        WoundView_UpdateView_MultiplayerPatch.OG_showInfection = null;
        ServerMain.Server_LastPlayerStates.Clear();


        var is_world = a.name == "SampleScene";
        // leaving the practice scene. the next real run is allowed to use mp_save again.
        if (!is_world)
            SavesystemPatch.PreserveSave = false;

        if (Net.IsRunning) {


            try { // remove the intro yapping
                PreRunScript.didIntro = (true);

                if (ScrollableText.textObj != null) {
                    UnityEngine.Object.Destroy(ScrollableText.textObj.gameObject);
                }
            } catch (Exception e) {
                log.error("remove the intro yapping -> \n" + e.ToString());
            }



            if (PlayerCamera.main?.body != null && !Net.IsDedicatedServer) {
                ScavPlayer.BodyToPlayerDict[PlayerCamera.main.body] = ScavPlayer.LocalPlayer;
            }

            // get the player character model! cuz its not a prefab!
            if (is_world) {

                try { // make the multiplicator of player image
                    WorldGeneration.world.loadingObject.GetOrAddComponent<LoadingScreenSpecimenDuplicator>();
                }   catch (Exception e) {
                    log.error("LoadingScreenCreatureDuplicator -> \n" + e.ToString());
                }

                ClientMain._InitializePlayerCharacter();
            }

        }
        if (is_world ) {

            if (!Net.IsRunningSteam) {
                Plugin.log.LogInfo("Local player username: " + UIMainMenu.USERINPUT_NAME);
            } else {
                Plugin.log.LogInfo("Local player username: " + KSteam.GetLocalUsername());
            }
            Plugin.log.LogInfo("Run settings: " + WorldMP.CompileRunSettings());


            if (Net.IsDedicatedServer) {
                UIInGame.StartSpectatorMode();
            } else {
                // and add the player body tracker to local body!
                var pb = PlayerCamera.main.body.GetOrAddComponent<NetBody>();
                
                pb.BackupLocalPlayerInit();

                UIMainMenu.SetOpen(false);
            }

        } else {
            try {
                UIManager.OnMainMenuLoad();
            } catch (Exception e) {
                log.error("OnSceneLoaded -> UIManager.OnMainMenuLoad\n" + e.ToString());
            }
            WorldMP.ResetWorldParameters();
        }

        Multiplayer._InvokeOnSceneChangeOrWorldStartGenerate();


        ServerMain.Server_LastPlayerStates.Clear();
    }
    public static void OnSceneUnLoaded(Scene a) {
#if DEBUG
        Plugin.log.LogWarning("DEV: SCENE UNLOADED");
#endif
        NetObjectRegistry.UnregisterAllObjects();
        
    }

    














    static string _PickGameWindowTitle() {
        var r = new System.Random();
        var pickrandom = (string[] x) => {
            return x[r.Next(0, x.Length)];
        };
        var gamename = Application.productName;
        if (r.NextDouble() > 0.2f) {
            gamename = "Casualties: Unknown";
            if (r.NextDouble() > 0.5f) {
                gamename = "Casualties Unknown";
                if (r.NextDouble() > 0.2f) {
                    gamename = pickrandom(new string[]{
                        "Casualties: Multiple",
                        "Casualties: Multiple",
                        "Casualties: Together",
                        "Casualties: Many",
                        "Casualties: Manyplayer",
                        "Casualties: Multiplayer",
                        "Casualties: Market Pliers",
                        "Casualties: Multi-Pliers",
                        "Casualties: Some",
                        "Casualties: Some Amount",
                        "Casualties: Co-op",
                    });
                }
            }
        }

        if (r.NextDouble() > 0.3) {
            gamename = gamename + ": " + pickrandom(new string[]{
                "Multiplayer",
                "Multiplied",
                "Co-op",
                "bluetooth support",
                "communism",
                "Manyplayer",
                "Die with friends",
                "death in poverty",
                "overdose with friends !!!",
                "fentanyl with friends !!!",
                "yipeee!!!  *falls off a cliff*",
                "furry suffering",
                "Just add multiplayer",
                "Krokosha's Multiplayer mod",
                "PEAK for masochists",
                "PEAK, but you go down",
                "thats a weird terraria",
                "Now with MULTIPLAYER",
                "The SALAD",
                "Cooperation behavioral research",
            });
        }
        return gamename;
    }




    public static NetDataWriter CreateNamedWriter(string name) {
        return CreateNamedWriter((uint)name.StableHash());
    }
    public static NetDataWriter CreateNamedWriter(uint nameHash) {
        var ndw = new NetDataWriter();
        ndw.Put((byte)NetmsgId.CustomMessage);
        ndw.Put(nameHash);
        return ndw;
    }


    static TwoWayDictionary<uint, string> _ReceiversOriginalNames = new();
    static Dictionary<uint, Action<ScavPlayer, NetDataReader>> _ServerReceivers = new();
    static Dictionary<uint, Action<NetDataReader>> _ClientReceivers = new();

    public static uint RegisterCustomServerReceiver(string name, Action<ScavPlayer, NetDataReader> receiver) {
        var hash = (uint)name.StableHash();
        if (_ServerReceivers.ContainsKey(hash)) {
            throw new DuplicateNameException($"Hash of \"{name}\" collided! ({hash.ToString()}) Change the receiver name!");
        }
        InternalAddCustomServerReceiver(hash, receiver);
        _ReceiversOriginalNames[hash] = name;
        return hash;
    }
    public static uint RegisterCustomClientReceiver(string name, Action<NetDataReader> receiver) {
        var hash = (uint)name.StableHash();
        if (_ClientReceivers.ContainsKey(hash)) {
            throw new DuplicateNameException($"Hash of \"{name}\" collided! ({hash.ToString()}) Change the receiver name!");
        }
        InternalAddCustomClientReceiver(hash, receiver);
        _ReceiversOriginalNames[hash] = name;
        return hash;
    }

    public static void InternalAddCustomServerReceiver(uint id, Action<ScavPlayer, NetDataReader> receiver) {
        if (_ServerReceivers.ContainsKey(id)) {
            throw new DuplicateNameException($"Receiver id collided! ({id.ToString()}) Change the id!");
        }
        _ServerReceivers[id] = receiver;
    }
    public static void InternalAddCustomClientReceiver(uint id, Action< NetDataReader> receiver) {
        if (_ClientReceivers.ContainsKey(id)) {
            throw new DuplicateNameException($"Receiver id collided! ({id.ToString()}) Change the id!");
        }
        _ClientReceivers[id] = receiver;
    }


    [ClientReceiver((byte)NetmsgId.CustomMessage, false)]
    static void ClientReceiver__CustomMessage(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint hash);
        if (_ClientReceivers.TryGetValue(hash, out var func)) {
            try {
                func(reader);
            } catch (Exception e) {
                var s = new StackTrace(e,0);
                var messageStr = _ReceiversOriginalNames.GetValueSafe(hash) ?? hash.ToString();
                log.error($"ClientCustomReceiver: \"{messageStr}\": {e.Message}\n{s}");
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.CustomMessage)]
    static void ServerReceiver__CustomMessage(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint hash);
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && _ServerReceivers.TryGetValue(hash, out var func)) {
            try {
                func(plr, reader);
            } catch (Exception e) {
                var s = new StackTrace(e,0);
                var messageStr = _ReceiversOriginalNames.GetValueSafe(hash) ?? hash.ToString();
                log.error($"ServerCustomReceiver: \"{messageStr}\": {e.Message}\n{s}");
            }
        }
    }


    public static void InternalAddServerReceiver(byte name, Multiplayer.HandleMessageDelegate receiver) {
        Multiplayer.all_network_receivers_from_attributes.Add(new ServerReceiverAttribute(name), receiver);
    }
    public static void InternalAddClientReceiver(byte id, Multiplayer.HandleMessageDelegate receiver, bool ignoreHost = false) {
        Multiplayer.all_network_receivers_from_attributes.Add(new ClientReceiverAttribute(id, ignoreHost), receiver);
    }


}
