using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using LiteEntitySystem;
using LiteNetLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.EnterpriseServices;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;




namespace Together;


[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterAllCommands))]
static class ConsoleScript_RegisterAllCommands_MultiplayerPatch { 
    static void Postfix() {
        Con._RegisterMultiplayerConsoleCommands();
    }
}

[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterPlayerDetails))]
static class ConsoleScript_RegisterPlayerDetails_MultiplayerPatch {
    static void Prefix(ConsoleScript __instance, ref bool __state) {
        __state = __instance.playerDetailsRegistered;
    }
    static void Postfix(ConsoleScript __instance, ref bool __state) {
        if (!__state) {
            {
                Command command2 = ConsoleScript.SearchExact("setlimbfield");
                command2.argAutofill[1].Add("all");
            }

        }
    }
}



[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.TryExecuteCommand))]
static class ConsoleScript_TryExecuteCommand_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string[] args, bool addToLog ) {

        if (addToLog && Multiplayer.IsNetworkActiveAndIsClient() && Con.client_adminmode) {

            if (args.Length == 0 || string.IsNullOrEmpty(args[0])) {
                return false; // OG immidiately returns
            }

            if (Con.localonly_commands.Contains(args[0])) { // allow to toggle the admin modes
                return true;
            }
            if (Con.banned_admin_commands.Contains(args[0])) {
                log.l($"{args[0]} is a banned admin command.");
                return false;
            }

            var thing = String.Join(" ", args);

            log.l($"Sending command to server: \"{thing}\"");

            Con.Client_ExecuteAdminCommand(thing);

            if (addToLog) {
                __instance.AddCommandToLogAndClearInput();
            }
            return false;
        }

        return true;
    }
    static void Postfix() {

    }
}





[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.LogToConsole))]
static class ConsoleScript_LogToConsole_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string text) {
        
        if (Multiplayer.IsNetworkActiveAndIsServer()) {
            if (text.StartsWith("The character with Unicode value \\u")) { // cuz it spams ts
                return true;
            }

            Con.Server_SendConsoleLog(text, Con.server_admins.Select(x => x.playerId).ToList());
        }

        return true;
    }
}



[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.ParsePosition))]
static class ConsoleScript_ParsePosition_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string s, ref Vector2 __result) {
        var plr = Con.GetCommandCallerPlayer();
        if (Con.TryGetTaggedPlayerName(s, out var plrname)) {
            var foundthing = Con.GetBodyForCommand(plrname, plr);// this function checks mp macros 
            if (foundthing != null) {
                __result = foundthing.transform.position;
            } else {
                throw new Exception("\"" + s + "\" player not found.");
            }
            return false;
        }
        if (plr != null) {
            if (s == "cursor") {
                __result = plr.CursorWorldPos;
                return false;
            } else if (s == "player") {
                __result = plr.pos;
                return false;
            }
        }
    
        return true;
    }
}



[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.ParseBool))]
static class ConsoleScript_ParseBool_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string s, ref bool __result) {
        s = s.ToLower();
        

        if (bool.TryParse(s, out __result)) {
            return false;
        }
        // i like the half life console, i need the number boolean
        if (int.TryParse(s, out var n)) {
            __result = true;
            if (n == 0) __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.ToggleActiveState))]
static class ConsoleScript_ToggleActiveState_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance) {
        if (!__instance.active) { //It's going to be active
            
        }
        return true;
    }
}



public class Con : CasualtiesTogetherSingleton {
    public static ConsoleScript con => ConsoleScript.instance;


    public static bool IsConsoleOpen() {
        return (bool)ConsoleScript.instance && ConsoleScript.instance.active;
    }
    public static void OpenConsole() {
        ConsoleScript.CheckForConsole();
        if (!Con.con.active) {
            Con.con.ToggleActiveState();
        }
    }
   
    public static bool CanCheat() {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient) {
            return Multiplayer.rules.AllowClientCheatCommands;
        } else {
            return Multiplayer.rules.sv_cheats;
        }
    }
    public static string CheatWarningText() {
        if (!Net.IsRunning) {
            return "huh";
        }
        if (!Multiplayer.rules.sv_cheats) {
            return "Server rule 'sv_cheats' is disabled";
        }
        if (!Multiplayer.rules.AllowClientCheatCommands) {
            return "Server rule 'AllowClientCheatCommands' is disabled";
        }
        return "hello";
    }

    public static void ConFailIfNetworkAlreadyRunning() {
        if (Net.IsRunning) {
            throw new Exception("Network system is already running!");
        }
    }
    public static void ConFailIfNetworkNotRunning() {
        if (!Net.IsRunning) {
            throw new Exception("Network system is not running!");
        }
    }
    public static void ConFailIfNotInSteamLobby() {
        if (!Net.IsRunning) {
            throw new Exception("Network system is not running!");
        }
        if (!Net.TryGetSteamTransport(out var tsteam )) {
            throw new Exception("You can do that only in a Steam lobby!");
        }
    }
    public static void ConFailIfNotConnected() {
        if (!Net.IsConnected) {
            throw new Exception("Network system is not running or not connected!");
        }
    }
    public static void ConFailIfNoLocalPlayer() {
        if (ScavPlayer.LocalPlayer == null) {
            throw new Exception("Local player not found!");
        }
    }

    public static void ConFailIfCheatsDisabled() {
        if (!CanCheat()) {
            throw new Exception("Cheats disabled. " + CheatWarningText());
        }
    }
    public static void ConFailIfNetworkIsRunningAndIsClient() {
        if (Net.IsRunning && Net.IsClient) {
            throw new Exception("Client can't do that.");
        }
    }
    public static void ConFailIfNetworkIsRunningAndIsServer() {
        if (Net.IsRunning && Net.IsServer) {
            throw new Exception("Server can't do that.");
        }
    }

    public static void ConFailIfNotInMainMenu() {
        if (!Util.IsInMainMenu()) {
            throw new Exception("This can only be done in main menu.");
        }
    }

    public static bool TryGetTaggedPlayerName(string s, out string name) {
        // Allow writing any player username 
        if (s.TryTrimStart("player:", out name, true)) {
            return true;
        } else {
            if (s.TryTrimStart("plr:", out name, true)) {
                return true;
            }
        }
        name = null;
        return false;
    }

    // updated for new 5pretest5  console system
    public static bool ParseBool01(string str, bool enabledisable = true) {
        str = str.ToLower();
        // idk y
        if (enabledisable) {
            if (str == "enable") {
                return true;
            }
            if (str == "disable") {
                return false;
            }
        }
        

        if (bool.TryParse(str, out var result)) {
            return result;
        }
        // i like the half life console, i need the number boolean
        if (int.TryParse(str, out var n)) {
            if (n == 0) return false;
            return true;
        }


        throw new Exception("\"" + str + "\" is not a valid boolean value! (true/false)");// (0/1)
    }

    public static void UnNoclip() { 
        if (ConsoleScript.instance.noClip) {
            PlayerCamera.main.body.rb.simulated = true;
            ConsoleScript.instance.noClip = false;
        }
    }


    public static Command RegisterCommandModern(string name, string desc, Action<string[], Command> action, params (string type, string typeDesc, string fallback, List<string> autofill)[] args) {
        var argAutofill = new Dictionary<int, List<string>>();
        var argDesc = new List<(string t, string d)>();


        for (int i = 0; i < args.Count(); i++) {
            var arg = args[i];
            if (arg.autofill != null) {
                argAutofill[i] = arg.autofill;
            }
            string typeStr = arg.type;
            string typeDesc = arg.typeDesc;
            if (arg.fallback != null) {
                typeDesc += $" (default: {arg.fallback})";
            }
            argDesc.Add((typeStr, typeDesc));
        }

        var defaultArgs = args.ToList(); // Duplicate it
        defaultArgs.Insert(0,(name,name,name,null));

        Command c = null;
        c = new Command(name, desc, (string[] splited) => {

            var dargs = defaultArgs.Select(x => x.fallback).ToList();
            //Merge
            for (int i = 0; i < dargs.Count(); i++) {
                var argInfo = defaultArgs[i];
                dargs[i] = splited[i];
                if (dargs[i] == null) {
                    throw new Exception($"Command \"{name}\": Argument {i}: Expected {argInfo.type} ({argInfo.typeDesc}), got null.");
                }
            }

            action(dargs.ToArray(), c);
        },
            argAutofill,
            argDesc.ToArray()
        );

        return RegisterCommand(c);
    }

    public static Command RegisterCommand(Command comm) {
        if (ConsoleScript.instance == null) {
            Util.CallLambdaWhen(() => { return ConsoleScript.instance != null; }, () => {
                RegisterCommand(comm);
            } );
            if (log.verbose) log.warn($"Registering command too early! {comm.name}");
        } else {
            if (!ConsoleScript.Commands.Contains(comm))
                ConsoleScript.Commands.Add(comm);
        }
        return comm;
    }
    public static Command RegisterCommand_LocalOnly(Command comm) {
        RegisterCommand(comm);
        localonly_commands.Add(comm.name);
        return comm;
    }
    public static Command RegisterCommand_BannedAdmin(Command comm) {
        RegisterCommand(comm);
        banned_admin_commands.Add(comm.name);
        return comm;
    }


    public static bool _DEV_CHATSPY = false;
    public static float _DEV_ZOOM = 1f;
    public static bool _DEV_HIDEHUD  {
        get {// OG: ConsoleScript  ui command V6.1demo
            if (PlayerCamera.main == null) return false;
            return !PlayerCamera.main.mainCanvas.gameObject.activeSelf;
        }
        set {// OG: ConsoleScript  ui command V6.1demo
            if (PlayerCamera.main == null) return;
            PlayerCamera.main.mainCanvas.gameObject.SetActive(!value);
        }
    }
    public static bool _DEV_GODMODE = false;

    public static bool _DEV_FREECAM { // freecam is now official
        get {
            if (PlayerCamera.main == null) {
                return false;
            }
            return PlayerCamera.main.isFreecam;
        }
        set {
            if (PlayerCamera.main == null) {
                return;
            }
            PlayerCamera.main.isFreecam = value;
        }
    }

    public static bool _DEV_NO_VISION_EFFECT = false;

    internal static bool _DEV_AUDIO_ONLY_ON_FOCUS = false;
    internal static float _DEV_LAST_VOLUME = 1f;

    public static bool _DEV_TELEKINESIS = false;
    public static GameObject _DEV_TELEKINESIS_OBJECT = null;


    public static GameObject SpawnThingOnPlayer(string resourceid, Body body = null, bool give_it_to_em = false, GameObject container = null) {
        if (Net.IsDedicatedServer && body == null) {
            if (ScavPlayer.BodyToPlayerDict.Count > 0) {
                body = ScavPlayer.BodyToPlayerDict.First().Key;
            }
        }
        body ??= PlayerCamera.main.body;

        var resource = Resources.Load(resourceid);

        if (!resource) {
            Plugin.log.LogError($"Unknown resource: {resourceid}");
            return null;
        }

        var t = UnityEngine.Object.Instantiate(resource, (Vector2)body.transform.position + UnityEngine.Random.insideUnitCircle, Quaternion.Euler(0f, 0f, 0)) as GameObject;

        if (t.TryGetComponent<AmmoScript>(out var ammo)) {
            ammo.rounds = ammo.maxRounds;
        }
        if (t.TryGetComponent<GunScript>(out var gun)) {
            gun.roundsInMag = gun.magCapacity;
            if (gun.feedType == GunScript.FeedType.Mag) {
                gun.hasMag = true;
            }
            gun.roundInChamber = GunScript.RoundInChamber.Round;
        }
        if (give_it_to_em && t.TryGetComponent<Item>(out var item)) {
            if (container && container.TryGetComponent<Container>(out var cntr)) {
                cntr.LoadItem(item);
            } else {
                body.ForceAutoPickUpItem(item);
            }
        }
        return t;
    }


    public static void DelayRunCommand(float delay_sec, string cmd) {
        Plugin.StartCoroutine(_DelayRunCommandCoroutine(delay_sec, cmd));
    }
    
    static IEnumerator _DelayRunCommandCoroutine(float delay_sec, string cmd) {
        yield return new WaitForSecondsRealtime(delay_sec);

        RunCommand(cmd);
    }

    public static void RunCommand(string cmd, bool addToLog = false) {
        try {
            var args = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ConsoleScript.instance.TryExecuteCommand(args, addToLog);
        } catch (Exception e) {
            log.error($"RunCommand {cmd} - {e}");
        }
    }
    public static void RunCommandUnsafe(string cmd, bool addToLog = false) {
        var args = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ConsoleScript.instance.TryExecuteCommand(args, addToLog);
    }



    public static bool CanFreecam() {
        if (Util.IsInMainMenu()) {
            return false;
        }

        if (CanCheat()) {
            return true;
        }

        if (Multiplayer.rules.SpectateWhileUnconscious) {
            if (!PlayerCamera.main.body.conscious) {
                return true;
            }
        } else {
            if (!PlayerCamera.main.body.alive) {
                return true;
            }
        }
        return false;
    
    }


    public static void CleanWorld() {
        // remove bleed and vomit particels
       // foreach (var item in GameObject.FindObjectsOfType<_BleedParticleStartTrackerComponent>()) {  GameObject.Destroy(item.gameObject);    }
        // remove block damage particles
        foreach (var item in WorldGeneration.world.blockDamages) {
            item.damage = 99999999999;
            item.UpdateSprite();
        }
        WorldGeneration.world.blockDamages.Clear();

        // delete all monsters
        foreach (var item in GameObject.FindObjectsOfType<SpiderHandler>()) {
            GameObject.Destroy(item.gameObject);
        }
        // delete all trash items
        foreach (var item in GameObject.FindObjectsOfType<Item>()) {
            if (NetObjectRegistry.lowpriority_objects_resourceids.Contains("casing")) {
                GameObject.Destroy(item.gameObject);
            }
        }
        // remove explosion blast marks
        foreach (var item in GameObject.FindObjectsOfType<SpriteRenderer>()) {
            if (item.gameObject.name.Contains("blastmark") && item.sortingOrder == -9050) {
                GameObject.Destroy(item.gameObject);
            }
        }
    }


    static ScavPlayer CommandCallerPlayer = null;
    // Can return null !
    public static ScavPlayer GetCommandCallerPlayer() { 
        if (CommandCallerPlayer == null) {
            return ScavPlayer.LocalPlayer;
        }
        return CommandCallerPlayer;
    }
    public static Vector2 GetCommandCallerCursorPos() {
        var caller = GetCommandCallerPlayer();
        if (caller != null) {
            return caller.CursorWorldPos;
        }
        return Util.GetCursorWorldPos();
    }

    internal static void _RegisterMultiplayerConsoleCommands() {
        var con = ConsoleScript.instance;

        // patched commands
        {


            Command tp = ConsoleScript.SearchExact("tp");
            tp.action = delegate (string[] args) {
                con.CheckForWorld();
                ConFailIfCheatsDisabled();

                Vector2 vector = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                ScavPlayer plr_being_teleported_to = null;
                if (args.Length > 1) {
                    vector = con.ParsePosition(args[1]);
                    var s = args[1];
                    if (Con.TryGetTaggedPlayerName(s, out var plrname)) {

                        var foundthing = GetBodyForCommand(plrname, Con.GetCommandCallerPlayer());// this function checks mp macros 
                        if (foundthing != null) {
                            plr_being_teleported_to = foundthing.plr;
                        } 

                    }
                }

                if (args.Length > 2 && Net.IsRunning) {

                    var plrname = args[2];

                    bool succ = false;
                    var isall = plrname == "@a";

                    var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                        
                        if (!isall) 
                            plrname = plr.playerName;
                            
                        if ( Net.IsServer ) {
                            plr.Server_TeleportCharacter(vector);
                        } else {
                            plr.netBody.SetBodyPosition(vector);
                        }
                        succ = true;
                        
                    }, true, GetCommandCallerPlayer(), true, false);

                    if (succ && res.Item1) {
                        con.LogToConsole($"Teleported: {plrname} to {vector}");
                    } else {
                        if (res.Item1) {
                            con.LogToConsole($"Failed to teleport: {plrname}");
                        } else {
                            con.LogToConsole($"Failed to teleport: {plrname} {res.Item2}");
                        }
                    }
                } else {
                    //vanilla
                    
                    PlayerCamera.main.body.transform.position = vector;
                    PlayerCamera.main.transform.position = vector;
                    con.LogToConsole($"Teleported player to {vector}.");
                }
            };
            tp.argDescription = tp.argDescription.AddToArray(("player", "optional, Who to teleport"));



            Command kill = ConsoleScript.SearchExact("kill");
            kill.action = delegate (string[] args) {
                con.CheckForWorld();

                if (Net.IsRunning) {
                    ConFailIfCheatsDisabled();
                }

                if (args.Length > 1 ) {

                    var plrname = args[1];
                    var isall = plrname == "@a";
                    var succ = false;
                    var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                        plr.body.brainHealth = 0;
                        plr.body.heartRate = 0;
                        plr.netBody.OnDamageEvent(new BodyDamageEvent("<key:plrdeath_command>", plr.netBody, 1));
                        plrname = plr.playerName;
                        succ = true;
                        con.LogToConsole($"Killed: {plrname} ");
                    }, true, GetCommandCallerPlayer(), true, false);

                    if (succ) {
                        if (isall) {
                            con.LogToConsole( "Killed everyone :) ");
                        }
                    } else {
                        con.LogToConsole($"{plrname} not found. ");
                    }

                } else {
                    //vanilla

                    PlayerCamera.main.body.brainHealth = 0f;
                    PlayerCamera.main.body.heartRate = 0;
                    con.LogToConsole("Killed the player.");
                }

            };
            kill.argDescription = kill.argDescription.AddToArray(("player", "optional"));



            Command setbodyfield = ConsoleScript.SearchExact("setbodyfield");
            setbodyfield.action = delegate (string[] args) {
                con.CheckForWorld();
                con.CheckArgumentCount(args, 2);
                string text = args[1];
                FieldInfo field = typeof(Body).GetField(text);
                object obj = TypeDescriptor.GetConverter(field.FieldType).ConvertFromInvariantString(args[2]);

                if (args.Length > 3 && Net.IsRunning) {
                    ConFailIfNetworkIsRunningAndIsClient();

                    var result = PerformActionOnPlayersByName(args[3], (ScavPlayer plr) => {

                        field.SetValue(plr.body, obj);
                        con.LogToConsole($"Set '{plr.playerName}' body field \"" + text + "\" to \"" + obj.ToString() + "\".");

                    }, true, GetCommandCallerPlayer(),true, false);

                    if (result.Item1) {
                        // something was set then ok
                    } else {
                        con.LogToConsole($"ERROR: Set body field: {args[3]}  - {result.Item2}");
                    }

                } else {
                    //vanilla


                    field.SetValue(PlayerCamera.main.body, obj);
                    con.LogToConsole("Set player body field \"" + text + "\" to \"" + obj.ToString() + "\".");
                }

            };
            setbodyfield.argDescription = setbodyfield.argDescription.AddToArray(("player", "optional, name of a player to mess with"));



            Command setlimbfield = ConsoleScript.SearchExact("setlimbfield");
            setlimbfield.action = delegate (string[] args) {
                con.CheckForWorld();
                con.CheckArgumentCount(args, 3);

                var limbname = args[1];
                var is_all = (limbname.ToLower() == "all");
                Limb localplayer_limb = is_all ? PlayerCamera.main.body.GetHead() : PlayerCamera.main.body.LimbByName(limbname);
                if (!localplayer_limb) {
                    throw new Exception("\"" + args[1] + "\" is not a valid limb!");
                }
                string text = args[2];
                FieldInfo field = typeof(Limb).GetField(text);
                object obj = TypeDescriptor.GetConverter(field.FieldType).ConvertFromInvariantString(args[3]);


                if (args.Length > 4 && Net.IsRunning) {
                    ConFailIfNetworkIsRunningAndIsClient();

                    var plrname = args[4];

                    var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {

                        if (is_all) {
                            foreach (var l in plr.body.limbs) {
                                field.SetValue(l, obj);
                            }
                        } else {
                            var targetlimb = plr.body.LimbByName(limbname);
                            field.SetValue(targetlimb, obj);
                        }

                        con.LogToConsole($"Set {plr.playerName}'s \"" + limbname + "\" field \"" + text + "\" to \"" + obj.ToString() + "\".");

                    }, true, GetCommandCallerPlayer(), true, false);

                    if (result.Item1) {
                        // something was set then ok
                    } else {
                        con.LogToConsole($"ERROR: Set limb field: {plrname}  - {result.Item2}");
                    }

                } else {
                    //vanilla

                    if (is_all) {
                        foreach (var l in PlayerCamera.main.body.limbs) {
                            field.SetValue(l, obj);
                        }
                    } else {
                        field.SetValue(localplayer_limb, obj);
                    }
                    con.LogToConsole("Set \"" + limbname + "\" field \"" + text + "\" to \"" + obj.ToString() + "\".");

                }


            };
            setlimbfield.argDescription = setlimbfield.argDescription.AddToArray(("player", "optional, name of a player to mess with"));



            Command heal = ConsoleScript.SearchExact("heal");
            heal.action = delegate (string[] args) {

                con.CheckForWorld();

                if (args.Length > 1 && Net.IsRunning) {
                    ConFailIfNetworkIsRunningAndIsClient();

                    var plrname = args[1];
                    var everyone = plrname == "@a";

                    var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {

                        plr.body.ResetHealth();

                        if (!everyone) {
                            con.LogToConsole("Healed: " + plr.playerName);
                        }

                    }, true, GetCommandCallerPlayer(), true, false);

                    if (result.Item1) {
                        if (everyone) {
                            con.LogToConsole("Healed everyone.");
                        }
                    } else {
                        con.LogToConsole($"ERROR: Heal: {plrname}  - {result.Item2}");
                    }

                } else {
                    PlayerCamera.main.body.ResetHealth();
                    con.LogToConsole("Healed the player.");
                }

            };
            heal.argDescription = heal.argDescription.AddToArray(("player", "optional, name of a player to heal"));



            Command addliquid = ConsoleScript.SearchExact("addliquid");
            addliquid.action = delegate (string[] args) {

                con.CheckArgumentCount(args, 2);
                con.CheckForWorld();

                if (Net.IsRunning && args.Length > 3) {
                    ConFailIfNetworkIsRunningAndIsClient();

                    var plrname = args[3];
                    var everyone = plrname == "@a";

                    var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {

                        if (plr.body == null) {
                            return;
                        }

                        var body = plr.body;

                        Item item = body.GetItem(0);
                        if (!item) {
                            con.LogToConsole($"{plr} - Not holding an item in the main hand!");
                            return;
                        }
                        WaterContainerItem component = item.GetComponent<WaterContainerItem>();
                        if (!component) {
                            con.LogToConsole($"{plr} - " + "\"" + item.fullName + "\" is not a liquid container!");
                            return;
                        }
                        string text = args[1];
                        float num = con.ParseFloat(args[2]);
                        if (!Liquids.LiquidExists(text)) {
                            con.LogToConsole($"{plr} - " + "\"" + text + "\" is not a valid liquid id!");
                            return;
                        }
                        component.AddLiquid(text, num);
                        con.LogToConsole($"Added {num}mL of {text} to {item.fullName} ({component.CurrentTotal}mL/{component.Capacity}mL).");

                    }, true, GetCommandCallerPlayer(), true, false);

                    if (result.Item1) {
                        if (everyone) {
                            con.LogToConsole("addliquided everyone.");
                        }
                    } else {
                        con.LogToConsole($"ERROR: addliquid: {plrname}  - {result.Item2}");
                    }

                } else {

                    var body = PlayerCamera.main.body;

                    Item item = body.GetItem(0);
                    if (!item) {
                        throw new Exception("Not holding an item in the main hand!");
                    }
                    WaterContainerItem component = item.GetComponent<WaterContainerItem>();
                    if (!component) {
                        throw new Exception("\"" + item.fullName + "\" is not a liquid container!");
                    }
                    string text = args[1];
                    float num = con.ParseFloat(args[2]);
                    if (!Liquids.LiquidExists(text)) {
                        throw new Exception("\"" + text + "\" is not a valid liquid id!");
                    }
                    component.AddLiquid(text, num);
                    con.LogToConsole($"Added {num}mL of {text} to {item.fullName} ({component.CurrentTotal}mL/{component.Capacity}mL).");
                }


            };
            addliquid.argDescription = addliquid.argDescription.AddToArray(("player", "optional, name of a player to addliquid"));


            {
                Command alert = ConsoleScript.SearchExact("alert");
                alert.action = delegate (string[] args) {
                    con.CheckArgumentCount(args, 2);
                    con.CheckForWorld();
                    string text6 = string.Join(" ", args.Skip(2));
                    bool important = con.ParseBool(args[1]);

                    Util.DoAlert(text6, important);
                    if (Multiplayer.IsNetworkActiveAndIsServer()) {
                        ServerMain.Server_AnnounceAlert(text6, important, true, ServerMain.AllClientIdsExceptHost);
                        con.LogToConsole("Announced display alert \"" + text6 + "\"");
                    } else {
                        con.LogToConsole("Displayed alert \"" + text6 + "\"");
                    }
                };
            }


            {
                Command unchipped = ConsoleScript.SearchExact("unchipped");
                unchipped.action = delegate (string[] args) {
                    con.CheckArgumentCount(args, 1);
                    con.CheckForWorld();

                    bool unchipped = con.ParseBool(args[1]);
                    if (args.Length > 2 && Net.IsRunning) {
                        ConFailIfNetworkIsRunningAndIsClient();

                        var plrname = args[2];

                        var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {

                            plr.SetUnchipped(unchipped);
                            con.LogToConsole($"Set unchipped of {plr.playerName} to { unchipped}.");

                        }, true, GetCommandCallerPlayer(), true, false);

                        if (result.Item1) {
                            // something was set then ok
                        } else {
                            con.LogToConsole($"ERROR: Set unchipped: {plrname}  - {result.Item2}");
                        }
                    } else {
                        WorldGeneration.world.SetUnchipped(unchipped);
                        con.LogToConsole("Set unchipped to " + unchipped + ".");
                    }
                };
                unchipped.argDescription = unchipped.argDescription.AddToArray(("player", "optional, name of a player to mess with"));
            }


            Command addxp = ConsoleScript.SearchExact("addxp");
            addxp.action = delegate (string[] args) {
                con.CheckArgumentCount(args, 2);
                con.CheckForWorld();
                float num5 = Mathf.Max(con.ParseFloat(args[2]), 0f);

                if (args.Length > 3 && Net.IsRunning) {
                    ConFailIfNetworkIsRunningAndIsClient();

                    var plrname = args[3];

                    var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {

                        switch (args[1]) {
                            case "str":
                            plr.body.skills.AddExp(0, num5);
                            con.LogToConsole($"Gave {plr.playerName} {Mathf.RoundToInt(num5)} strength experience.");
                            break;
                            case "res":
                            plr.body.skills.AddExp(1, num5);
                            con.LogToConsole($"Gave {plr.playerName} {Mathf.RoundToInt(num5)} resilience experience.");
                            break;
                            case "int":
                            plr.body.skills.AddExp(2, num5);
                            con.LogToConsole($"Gave {plr.playerName} {Mathf.RoundToInt(num5)} intelligence experience.");
                            break;
                            default:
                            con.LogToConsole("\"" + args[1] + "\" is not a valid skill type!");
                            break;
                        }

                    }, true, GetCommandCallerPlayer(), true, false);

                    if (result.Item1) {
                        // something was set then ok
                    } else {
                        con.LogToConsole($"ERROR: Set experience: {plrname}  - {result.Item2}");
                    }

                } else {
                    //vanilla

                    var body = PlayerCamera.main.body;
                    switch (args[1]) {
                        case "str":
                        body.skills.AddExp(0, num5);
                        con.LogToConsole($"Gave the player {Mathf.RoundToInt(num5)} strength experience.");
                        break;
                        case "res":
                        body.skills.AddExp(1, num5);
                        con.LogToConsole($"Gave the player {Mathf.RoundToInt(num5)} resilience experience.");
                        break;
                        case "int":
                        body.skills.AddExp(2, num5);
                        con.LogToConsole($"Gave the player {Mathf.RoundToInt(num5)} intelligence experience.");
                        break;
                        default:
                        throw new Exception("\"" + args[1] + "\" is not a valid skill type!");
                    }

                }

            };
            addxp.argDescription = addxp.argDescription.AddToArray(("player", "optional, name of a player to mess with"));

            {

                Command resetskills = ConsoleScript.SearchExact("resetskills");
                resetskills.action = delegate (string[] args) {
                    con.CheckForWorld();

                    if (args.Length > 1 && Net.IsRunning) {
                        ConFailIfNetworkIsRunningAndIsClient();

                        var plrname = args[1];

                        var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {

                            var body = plr.body;
                            body.skills.STR = 0;
                            body.skills.RES = 0;
                            body.skills.INT = 0;
                            body.skills.expSTR = 0f;
                            body.skills.expRES = 0f;
                            body.skills.expINT = 0f;
                            body.skills.UpdateExpBoundaries();

                            con.LogToConsole($"Reset all {plr.playerName} skills.");

                        }, true, GetCommandCallerPlayer(), true, false);

                        if (result.Item1) {
                            // something was set then ok
                        } else {
                            con.LogToConsole($"ERROR: Reset experience: {plrname}  - {result.Item2}");
                        }

                    } else {
                        //vanilla

                        var body = PlayerCamera.main.body;
                        body.skills.STR = 0;
                        body.skills.RES = 0;
                        body.skills.INT = 0;
                        body.skills.expSTR = 0f;
                        body.skills.expRES = 0f;
                        body.skills.expINT = 0f;
                        body.skills.UpdateExpBoundaries();
                        con.LogToConsole("Reset all player skills.");

                    }

                };
                resetskills.argDescription = resetskills.argDescription.AddToArray(("player", "optional, name of a player to mess with"));


            }


            {

                Command timescale = ConsoleScript.SearchExact("timescale");
                timescale.action = delegate (string[] args) {
                    con.CheckArgumentCount(args, 1);
                    con.CheckForWorld();
                    ConFailIfNetworkIsRunningAndIsClient();
                    var num = con.ParseFloat(args[1]); 
                    if (Mathf.Abs(num) != 1 && num != 0) {
                        if (Net.IsRunning) {
                            if (Multiplayer.rules.DisableTimeManipulation) {
                                num = 1;
                            } else {
                                num = Mathf.Round(num);
                            }
                        }
                    }
                    Time.timeScale = num;
                    con.LogToConsole("Set timescale to " + num + ".");
                };


            }

        }


        {
            HashSet<string> ogcommands = new HashSet<string>{
                //V7.0.1 OG COMMANDS:
                "help",
                "heal",
                "coagulate",
                "kill",
                "spawn",
                "spawncategory",
                "tp",
                "skiplayer",
                "skiptext",
                "log",
                "talk",
                "framerate",
                "alert",
                "saveandquit",
                "resetskills",
                "fucklore",
                "timescale",
                "setconsoleheight",
                "setconsolecolor",
                "copylog",
                "clear",
                "addxp",
                "loglocale",
                "nukeplayerprefs",
                "openfolder",
                "setbodyfield",
                "setlimbfield",
                "amputate",
                "unchipped",
                "addcustomcommand",
                "addliquid",
                "locate",
                "removecustomcommand",
                "music",
                "bind",
                "repeat",
                "explode",
                "floodfill",
                "echo",
                "ui",
                "freecam",
                "starterkit",
                "noclip",
                "playsound",
                "fullbright",
                "plushies",
                "errorlogging",
            };
            HashSet<string> allowedcommands = new HashSet<string>{
                "krok rule EnableNametags",
                "krok rules",
                "krok help",
                "krok maxplayers",
                "krok kick",
                // og, non-cheat commands
                "pixelate",
                "help",
                "setvolume",
                "talk",
                // pretest5 commands (non-cheat)
                "log",
                "framerate",
                "volume",
                "saveandquit",
                "fucklore",
                "setconsoleheight",
                "setconsolecolor",
                "copylog",
                "clear",
                "loglocale",
                "openfolder",
                "addcustomcommand",
                "removecustomcommand",
                "timescale",
                "music",
                "bind",
                //"repeat", // nah people can spam this way 
                "echo",
                "errorlogging",
            };

            foreach (var command in ConsoleScript.Commands) {
                if (ogcommands.Contains(command.name)) {
                    if (!allowedcommands.Contains(command.name)) {
                        var og_action = command.action;
                        command.action = (string[] args) => {
                            ConFailIfCheatsDisabled();

                            og_action(args);
                        };
                    }
                }
            }

        }




        {
            HashSet<string> serveronlycommands = new HashSet<string>{
                "spawn",
            };


            var all_og_commands = ConsoleScript.Commands.Select(x => x.name).ToArray();
            foreach (var command in ConsoleScript.Commands) {
                if (serveronlycommands.Contains(command.name)) {
                    var og_action = command.action;
                    command.action = (string[] args) => {
                        ConFailIfNetworkIsRunningAndIsClient();

                        og_action(args);
                    };
                }
            }

        }


        if (Plugin.dump_game_ids) {
            log.l($"all og commands: \n{String.Join("\n", ConsoleScript.Commands.Select(x => x.name).ToArray())}");
        }


        RegisterCommand(new Command("objdelete", "MP - Despawn a specific object.", (string[] splited) => {

            var go = GetObjectForCommand(splited.Length > 1 ? splited[1] : "cursor",false);

            if (go != null) {
                con.LogToConsole($"Deleted: {DebugHelp.GetGameObjectFullDebugString(go)}");
                GameObject.Destroy(go);   
            } else {
                con.LogToConsole($"Object wasn't found.");
            }

        }, null, ("obj", "Object to delete")));


        RegisterCommand(new Command("objinfo", "MP - Print object info.", (string[] splited) => {

            var go = GetObjectForCommand(splited.Length > 1 ? splited[1] : "cursor", false);

            if (go != null) {
                var str = $"{DebugHelp.GetGameObjectFullDebugString(go)}";
                if (go.TryGetComponent(out Item i)) {
                    var container = ItemSync.ItemGetContainerInfo(i);
                    if (container.has_container) {
                        str += $" {container.ToString()}";
                    }
                }
                str += $" pos:{(Vector2)go.transform.position}";
                con.LogToConsole(str);
            } else {
                con.LogToConsole($"Object wasn't found.");
            }

        }, null, ("obj", "Object to delete")));



        RegisterCommand(new Command("objtalkersay", "MP - Hijack object's Talker component.", (string[] splited) => {
            con.CheckArgumentCount(splited, 2);

            try {
                var objname = splited[1];

                string nameoftalkerobject = "";
                GameObject foundtalker_go = GetObjectOrBodyForCommand(objname);
                Talker foundtalker = null;

                if (!Util.TryGetTalkerOnObject(foundtalker_go, out foundtalker, out nameoftalkerobject)) {
                    var cursorpos = GetCommandCallerCursorPos();

                    var collider2Darray = Physics2D.OverlapPointAll(cursorpos);
                    for (int i = 0; i < collider2Darray.Length; i++) {
                        var collider2D2 = collider2Darray[i];
                        if (Util.TryGetTalkerOnObject(collider2D2.gameObject, out foundtalker, out nameoftalkerobject)) {
                            foundtalker_go = collider2D2.gameObject;
                            break;
                        }
                    }
                } 


                var msg = String.Join(" ", splited.Skip(2));

                if (foundtalker != null && foundtalker_go != null) {

                    if (Multiplayer.IsNetworkActiveAndIsServer()) {
                        if (NetObjectRegistry.TryGetSyncInfo(foundtalker_go, out var si)) {
                            if (ServerMain.ForceTalkerSayAndAnnounce(si, msg, null)) {
                            } else {
                                con.LogToConsole($"Failed to make {nameoftalkerobject} say \"{msg}\"");
                                return;
                            }
                        } else if (foundtalker_go.TryGetComponent(out NetBody pb)) {
                            if (ServerMain.ForceTalkerSayAndAnnounce(pb, msg, null)) {
                            } else {
                                con.LogToConsole($"Failed to make {nameoftalkerobject} say \"{msg}\"");
                                return;
                            }
                        } else {
                            NetObjectRegistry.AlertObjectNotRegistered(true);
                        }
                    }

                    Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, msg, true);

                    con.LogToConsole($"Made {nameoftalkerobject} say \"{msg}\"");

                } else {
                    con.LogToConsole($"Specified talker wasn't found.");
                }
            } catch (Exception e) {
                log.error(e);
            }

        }, null, ("obj", "Object with a Talker to talk")));






        RegisterCommand(new Command("chatspy", "MP- force to show and log all chat messages", (string[] splited) => {

            if (splited.Count() < 2) {
                Chat._DEV_LOG_CHAT = !Chat._DEV_LOG_CHAT;
            } else {
                Chat._DEV_LOG_CHAT = ParseBool01(splited[1]);
            }
            _DEV_CHATSPY = Chat._DEV_LOG_CHAT;

            if (!CanCheat()) {
                _DEV_CHATSPY = false;
                log.l($"_DEV_LOG_CHAT = {Chat._DEV_LOG_CHAT}");
            } else {
                log.l($"_DEV_CHATSPY = {_DEV_CHATSPY} ");
            }

        }, null));




        RegisterCommand(new Command("earthquake", "MP - control earthquake", (string[] args) => {
            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(args, 1);
            con.CheckForWorld();
            var world = WorldGeneration.world;

            var arg = args[1].ToLower();
            if (arg == "now") {
                //OG: WorldGeneration.Update  V6.0-pre4
                world.earthquakeDelay = UnityEngine.Random.Range(600f, 1750f);
                world.earthquakeTime = UnityEngine.Random.Range(3f, 25f);
                Time.timeScale = 1f;

                con.LogToConsole($"Started earthquake right now.");
            } else {
                WorldMP.earthquake_enabled = ParseBool01(arg, true);

                if (!WorldMP.earthquake_enabled) {
                    world.earthquakeTime = 0;
                }

                con.LogToConsole($"Earthquake active: {WorldMP.earthquake_enabled}");
            }

        }, new Dictionary<int, List<string>> { { 0, new List<string> { "now", "disable", "enable" } } }, ("string", "Action")));







        var dumpbodyvars_command = new Command("dumpbodyvars", "MP - dump all fields of a body (for health desync debugging)", (string[] splited) => {
            con.CheckForWorld();
#if RELEASE
            ConFailIfCheatsDisabled();
#endif

            if (!Net.IsRunning) {
                log.l( Util.GetLocalBody().DumpBodyVars());
                return;
            }


            string plrname = "@m";
            if ( splited.Length > 1 ) {
                plrname = splited[1];
            }

            var isall = plrname == "@a";
            var hasplr = false;
            var succ = false;

            var giv = (NetBody npc) => {
                hasplr = true;

                if (!isall) {
                    plrname = npc.bodyName;
                }

                log.l(npc.ToString() + " " + npc.body.DumpBodyVars());
            };
            var res = PerformActionOnBodiesByName(plrname, giv, true, false, GetCommandCallerPlayer(), false);

            if (!hasplr) {
                con.LogToConsole($"{plrname} -> body not found. ");
            }
        }, new Dictionary<int, List<string>> {
            // get all item names
            { 1, Resources.LoadAll<GameObject>("").Where((GameObject x) => (bool)x.GetComponent<Item>()).Select((GameObject x) => x.name).ToList() }
        },
            ("player", "to dumpbodyvars to"),
            ("string item_id", "Item resource ID, multiple is allowed")
        );
        RegisterCommand(dumpbodyvars_command);

        var give_command = new Command("give", "MP - give an item to a player", (string[] splited) => {

            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfCheatsDisabled();
            con.CheckArgumentCount(splited, 2);

            var items = splited.ToList().GetRange(2, splited.Length - 2);
            string plrname = splited[1];

            var isall = plrname == "@a";
            var hasplr = false;
            var succ = false;

            var giv = (NetBody npc) => {
                hasplr = true;

                if (!isall) {
                    plrname = npc.bodyName;
                }

                var givenItems = items;
                foreach (var itemname in items) {
                    if (SpawnThingOnPlayer(itemname, npc.body, true)) {
                        succ = true;
                    } else {
                        givenItems = new List<string>(givenItems);
                        givenItems.Remove(itemname);
                        con.LogToConsole($"{itemname} is not an item.");
                    }
                }
                items = givenItems;
                if (succ && givenItems.Count > 0) {
                    con.LogToConsole($"{npc.bodyName} got the {String.Join(" ", givenItems)}");
                }
            };
            var res = PerformActionOnBodiesByName(plrname, giv, true, false, GetCommandCallerPlayer(), false);

            if (!hasplr) {
                con.LogToConsole($"{plrname} -> body not found. ");
            }
        }, new Dictionary<int, List<string>> {
            // get all item names
            { 1, Resources.LoadAll<GameObject>("").Where((GameObject x) => (bool)x.GetComponent<Item>()).Select((GameObject x) => x.name).ToList() } 
        },
            ("player", "to give to"), 
            ("string item_id", "Item resource ID, multiple is allowed")
        );
        RegisterCommand(give_command);

        var respawn_command = RegisterCommand(new Command("respawn", "MP - respawn a player", (string[] splited) => {

            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckForWorld();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 1);

            Vector2 goalpos = new Vector2();
            if (splited.Length >= 3) {
                goalpos = con.ParsePosition(splited[2]);
            } else {
                goalpos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);
            }

            var plrname = splited[1];
            var isall = plrname == "@a";
            var succ = false;
            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!isall) {
                    plrname = plr.playerName;
                }
                plr.Server_RespawnCharacter(goalpos, false);
                succ = true;
            }, true, GetCommandCallerPlayer(),true, false);
            if (succ && res.Item1) {
                con.LogToConsole($"Respawned: {plrname}");
            } else {
                if (res.Item1) {
                    con.LogToConsole($"Failed to respawn: {plrname}");
                } else {
                    con.LogToConsole($"Failed to respawn: {plrname} {res.Item2}");
                }
            }
        }, new Dictionary<int, List<string>> {},
            ("player", "to respawn to"),
            ("position", "optional")
        ));

        RegisterCommand(new Command("clearinventory", "MP - clear inventory of a player", (string[] splited) => {

            ConFailIfCheatsDisabled();
            con.CheckForWorld();

            var delete = false;
            if (splited.Length > 1) {
                delete = con.ParseBool(splited[1]);
            }

            var dothing = (Body b) => {
                if (delete) {
                    foreach (var item in b.GetAllItemsThorough()) {
                        GameObject.Destroy(item.gameObject);
                    }
                    b.Body_DropAllItems();
                } else { 
                    b.Body_DropAllItems();
                }
            };

            if (Net.IsRunning && splited.Length > 2) {
                var plrname = splited[2];
                var isall = plrname == "@a";
                var succ = false;
                var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    if (!isall) {
                        plrname = plr.playerName;
                    }
                    dothing(plr.body);
                    succ = true;
                }, true, GetCommandCallerPlayer(), true, false);
                if (succ && res.Item1) {
                    con.LogToConsole($"Cleared inventory: {plrname}");
                } else {
                    if (res.Item1) {
                        con.LogToConsole($"Failed to clear inventory: {plrname}");
                    } else {
                        con.LogToConsole($"Failed to clear inventory: {plrname} {res.Item2}");
                    }
                }
            } else {
                dothing(Util.GetLocalBody());
            }

            
        }, new Dictionary<int, List<string>> { },
          ("bool", "delete items"),
          ("player", "optional")
        ));

        RegisterCommand(new Command("godmode", "MP - revive every player and keep resetting their health", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ref bool val = ref _DEV_GODMODE;
            var target = !val;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            val = target;
            ConsoleScript.instance.LogToConsole($"godmode {val}");
        }, null));

        RegisterCommand_LocalOnly(new Command("zoom", "MP - zoom camera", (string[] splited) => {
            con.CheckArgumentCount(splited, 1);
            con.CheckForWorld();

            if (!float.TryParse(splited[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var target)) {
                ConsoleScript.instance.LogToConsole("Thats not a nubmer bro");
                return;
            }
            if (target < .001) {
                throw new ArgumentOutOfRangeException("Zoom can't be 0 or your perception of reality will shatter.");
            }

            if (Mathf.Abs(target) < 1 && !CanCheat()) {
                target = 1f;
            }

            Camera.main.orthographicSize = (Camera.main.orthographicSize * _DEV_ZOOM) / target; // default: 37,5

            var ppc = PlayerCamera.main.GetComponent<PixelPerfectCamera>();
            ppc.assetsPPU = (int)(target * 8); //default is: 8

            _DEV_ZOOM = target;
            ConsoleScript.instance.LogToConsole($"zoom {_DEV_ZOOM} orthographicSize={Camera.main.orthographicSize} PPU={ppc.assetsPPU}");
        }, null, ("float zoom", "the bigger the number, the closer you see")));

        RegisterCommand_LocalOnly(new Command("spectate", "MP - spectator mode", (string[] splited) => {

            var target = !UIInGame.SpectatorModeActive;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }

            if (target) {
                ConFailIfCheatsDisabled();
                UIInGame.StartSpectatorMode();
                
                // User may want to spectate a specific target 
                if (splited.Length > 2) {
                    var targetname = splited[2];
                    GameObject obj = GetObjectOrBodyForCommand(targetname);
                    
                    if (obj != null) {
                        UIInGame.Spectator_SetTarget(obj.transform);
                    }
                }
                
            } else {
                UIInGame.StopSpectatorMode();
            }

            con.LogToConsole($"Spectator mode: {UIInGame.SpectatorModeActive}");
        }, null));



        RegisterCommand(new Command("mpopenmenu", "MP", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            var target = !UIMainMenu.mainmenu_open;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            UIMainMenu.SetOpen(target);
            ConsoleScript.instance.LogToConsole($"UIMainMenu.SetOpen({target});");
        }, null));

        RegisterCommand(new Command("settile", "MP", (string[] splited) => {
            ConFailIfCheatsDisabled();

            ushort tileid = ushort.Parse(splited[1]);

            Vector2 goalpos = new Vector2();
            if (splited.Length < 4) {
                goalpos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);
            } else {
                goalpos.x = float.Parse(splited[2], CultureInfo.InvariantCulture);
                goalpos.y = float.Parse(splited[3], CultureInfo.InvariantCulture);
            }
            Vector2Int realpos = WorldGeneration.world.WorldToBlockPos(goalpos);

            WorldGeneration.world.SetBlock(realpos, tileid);

            ConsoleScript.instance.LogToConsole($"Set tile {realpos.x} {realpos.y} id to {tileid} ({WorldGeneration.world.GetBlockInfo(tileid)?.name})");
        }, null));
        
        RegisterCommand(new Command("setfluid", "MP", (string[] splited) => {
            ConFailIfCheatsDisabled();

            byte fluidid = byte.Parse(splited[1]);

            Vector2 goalpos = new Vector2();
            if (splited.Length < 4) {
                goalpos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);
            } else {
                goalpos.x = float.Parse(splited[2], CultureInfo.InvariantCulture);
                goalpos.y = float.Parse(splited[3], CultureInfo.InvariantCulture);
            }
            Vector2Int realpos = WorldGeneration.world.WorldToBlockPos(goalpos);

            FluidManager.main.fluid[realpos.x, realpos.y] = fluidid;

            ConsoleScript.instance.LogToConsole($"Set fluid {realpos.x} {realpos.y} id to {fluidid} ({FluidManager.WorldFluidToLiquidID[fluidid]})");
        }, null));

        RegisterCommand_LocalOnly(new Command("spawnpoint", "MP-SERVER- Set default spawn point. (It resets after world generates)", (string[] splited) => {
            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 1);
            con.CheckForWorld();

            var pos = con.ParsePosition(splited[1]);

            Body_PlaceBody_MultiplayerPatch.SpawnLocation = pos;
            Multiplayer.rules.SpawnOnPlayer = false;

            log.l($"SpawnLocation set to: {pos}");
        }, null, ("position", "New spawn point")) );

        RegisterCommand_LocalOnly(new Command("vote", "MP- Call a vote.", (string[] splited) => {
            var aaa = String.Join(" ", splited);

            {
                var writer = Multiplayer.CreateNamedWriter("CustomCommand");
                writer.Put(aaa);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                // WRITER END; 
            }
        }, null, ("type", "kick/mutevc/mutetc"), ("player", "")));

        RegisterCommand(new Command("createvote", "MP-SERVER- Call a custom vote.", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 5);

            var argcount = 1;

            var toRun = (splited[argcount++]);
            var timeout = con.ParseFloat(splited[argcount++]);
            var percent = con.ParseFloat(splited[argcount++]);
            //if (Mathf.Abs(percent) > 1f) 
            {
                percent = Mathf.Clamp(percent, -100f, 100f) / 100f;
            }

            var textWords = splited.Skip(4);
            var titleAndTextToUnmerge = String.Join(" ", textWords).Trim();

            //createvote give_@a_shotgun 5 10 hello bye
            //createvote explode_plr:@r 5 10 explosion gamble

            var title = "vote title";
            var text = "vote text";

            {
                var tt = splited.ElementAtOrDefault(argcount++);
                var te = splited.ElementAtOrDefault(argcount++);
                if (tt != null) {
                    title = tt.Replace('_', ' ');
                }
                if (te != null) {
                    text = te.Replace('_', ' ');
                }
            }
            // Start vote
            {

                VoteSystem.Server_AnnounceVote(title, text, timeout, (HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore) => {
                    var total = (float)(voted_yes.Count + voted_no.Count + voted_ignore.Count);
                    var yes = (float)voted_yes.Count;
                    var no = (float)voted_no.Count;
                    if (total > 0) {
                        float perc = 0;
                        if (percent >= 0) {
                            perc = yes / total;
                        } else {
                            perc = no / total;
                        }

                        Chat.Server_ChatAnnouncement($"<key:vote_result> {Mathf.RoundToInt(perc * 100f)}%/{Mathf.RoundToInt(percent * 100f)}%");
                        if (perc >= Mathf.Abs(percent)) {
                            con.RunCommandString(toRun);
                        }
                    }
                });

            }
        }, null, ("string command", "Command to run if vote is accepted."), ("float time", "Time for vote. (Default: 10)"), ("float percent", "Percentage of votes required. (Default: 50)"), ("string", "Title"), ("string", "Text")) );

        RegisterCommand(new Command("kick", "MP-SERVER- Forcefully disconnect player.", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 1);

            var reason = "Kicked!";
            if (splited.Count() > 2) {
                reason = "Kicked: " + string.Join(" ", splited.Skip(2));
            }

            var plrname = splited[1];
            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!plr.IsLocal) {
                    Plugin.log.LogInfo($"Server entered command to kick: {plr}");
                    plr.Server_Kick(reason);
                    if (plrname != "@a") {
                        plrname = plr.playerName;
                    }
                }
            }, false, GetCommandCallerPlayer(), false, false);
            if (res.Item1) {
                log.l($"Kicked: {plrname} - {reason}");
            } else {
                log.l($"Failed to kick: {plrname} {res.Item2}");
            }

        }, null, ("player", ""), ("string reason", "optional")));

        RegisterCommand(new Command("ban", "MP-SERVER- Ban player.", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();

            con.CheckArgumentCount(splited, 1);
            var plrname = splited[1];

            var reason = "Banned!";
            if (splited.Count() > 2) {
                reason = "Banned: " + string.Join(" ", splited.Skip(2));
            }

            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!plr.IsLocal) {
                    Plugin.log.LogInfo($"Server entered command to ban: {plr}");
                    BanList.Add("NULL", plr.playerName, plr.SteamId);
                    plr.Server_Kick(reason);
                    if (plrname != "@a") {
                        plrname = plr.playerName;
                    }
                }
            }, false, GetCommandCallerPlayer(), false, false);
            if (res.Item1) {
                log.l($"Banned: {plrname} - {reason}");
            } else {
                if (ulong.TryParse(plrname, out var parsed_as_steamid) && parsed_as_steamid != 0 && plrname.StartsWith("7656119")) {
                    BanList.Add("NULL", "", parsed_as_steamid);
                    log.l($"Banned Steam ID: {parsed_as_steamid} ");
                } else {
                    log.l($"Failed to Ban: {plrname} {res.Item2}");
                }
            }

        }, null, ("player", ""), ("string reason", "optional")));
        
        RegisterCommand(new Command("ipban", "MP-SERVER- IP Ban player. (Non-steam only)", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();

            con.CheckArgumentCount(splited, 1);
            var plrname = splited[1];

            if (!(Net.Transport is TransportLiteNetLib lnl)) {
                throw new Exception("Only available for direct IP connection mode.");
            }


            var reason = "Banned!";
            if (splited.Count() > 2) {
                reason = "Banned: " + string.Join(" ", splited.Skip(2));
            }

            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!plr.IsLocal) {
                    var peer = lnl.ClientIdToPeerDict[plr.playerId];
                    if (peer == null) {
                        throw new Exception($"{plr} does not have net peer object ????");
                    }
                    Plugin.log.LogInfo($"Server entered command to IP ban: {plr}");
                    BanList.Add(peer.Address.ToString(), plr.playerName, plr.SteamId);
                    plr.Server_Kick(reason);
                    if (plrname != "@a") {
                        plrname = plr.playerName;
                    }
                }
            }, false, GetCommandCallerPlayer(), false, false);

            if (res.Item1) {
                log.l($"Banned: {plrname} - {reason}");
            } else {
                if (Net.TryParseIPPORT(plrname, out var realip, out var _)) {
                    BanList.Add(realip, "", 0);
                    log.l($"Banned IP: {realip} ");
                } else {
                    log.l($"Failed to Ban: {plrname} {res.Item2}");
                }
            }
        }, null, ("player", ""), ("string reason", "optional")));
        
        RegisterCommand(new Command("banlist", "MP- Show all players you've banned. Local.", (string[] splited) => {
            log.l($"BANLIST: {BanList.Instance.filePath}\n{String.Join("\n",BanList.Entries)} ");
        }, null));
        
        RegisterCommand(new Command("unban", "MP- Remove a player from your ban list.", (string[] splited) => {

            con.CheckArgumentCount(splited, 1);
            var plrname = splited[1];

            BanList.BanEntry found = null;
            if (ulong.TryParse(plrname, out var parsed_as_steamid) && parsed_as_steamid != 0) {
                found = BanList.Entries.FirstOrDefault(x => x.steamid == parsed_as_steamid);
            }
            if (found == null) {
                found = BanList.Entries.FirstOrDefault(x => x.ip == plrname);
            }
            if (found == null) {
                found = BanList.Entries.FirstOrDefault(x => x.name == plrname);
            }

            if (found != null) {
                BanList.Remove(found);
                log.l("Unbanned: " + found.ToString());
                return;
            } else {
                log.l($"\"{plrname}\" was not found in your ban list.");
            }

        }, null, ("user", "SteamID or IP or Name")));

        RegisterCommand(new Command("say", "MP - say something in chat", (string[] splited) => {
            ConFailIfNetworkNotRunning();

            var con = ConsoleScript.instance;

            var msg = String.Join(" ", splited.Skip(1));

            Chat.SendChatMessage(msg,true);


            con.LogToConsole($"Chat message sent: " + msg);
        }, null, ("string", "message")));

        RegisterCommand(new Command("skybg", "MP dev", (string[] splited) => {
            con.CheckForWorld();

            bool has_params = false;
            float r = 0;
            Color c = Color.white;
            if (splited.Length == 3) {
                has_params = true;
                c = con.ParseColor(splited[1]);
                r = con.ParseFloat(splited[2]);
            }

            if (WorldMP.HasSkyBackground()) {
                WorldMP.RemoveSkyBackground();
            } else {
                ConFailIfCheatsDisabled();
                if (has_params) {
                    WorldMP.CreateSkyBackground(c,r);
                } else {
                    WorldMP.CreateSkyBackground();
                }
            }
            ConsoleScript.instance.LogToConsole($"skybg {WorldMP.HasSkyBackground()} ");
        }, null, ("color", "skyColor"), ("float", "rainIntensity")));

        RegisterCommand_LocalOnly(new Command("snowbg", "MP dev", (string[] splited) => {
            var target = !PlayerCamera.main.backgroundSnow.activeSelf;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            PlayerCamera.main.backgroundSnow.SetActive(target);

            ConsoleScript.instance.LogToConsole($"snowbg {PlayerCamera.main.backgroundSnow.activeSelf} ");
        }, null));

        RegisterCommand(new Command("map", "MP dev", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNotInMainMenu();

            var m = "debug";
            if (splited.Length > 1) {
                m = splited[1];
            }

            var prs = PreRunScript.instance;

            if (m == "debug") {
                log.l("Loading debug world");
                WorldMP.LoadDebugWorld();
            } else if (m == "tutorial") {
                log.l("Loading tutorial world");
                prs.StartTutorial();
            } else {
                log.l("Loading vanilla world");
                prs.StartRun();
            }
        }, new Dictionary<int, List<string>> { { 0,
                new List<string> { "debug", "tutorial", "vanilla" }
            } }));

        RegisterCommand_LocalOnly(new Command("novisioneffect", "MP", (string[] splited) => {
            ref bool val = ref _DEV_NO_VISION_EFFECT;
            var target = !val;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            if (target) {
                Con.ConFailIfCheatsDisabled();
            }
            val = target;
            ConsoleScript.instance.LogToConsole($"novisioneffect {val}");
        }, null));



        RegisterCommand_LocalOnly(new Command("telekinesis", "MP dev", (string[] splited) => {
            ref bool val = ref _DEV_TELEKINESIS;
            var target = !val;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            if (target) {
                ConFailIfNetworkIsRunningAndIsClient();
                ConFailIfCheatsDisabled();
            }
            val = target;
            ConsoleScript.instance.LogToConsole($"telekinesis {val}");
        }, null));


        RegisterCommand(new Command("gamemode", "MP dev", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNotInMainMenu();


            if (GamemodeManager.HasGamemode()) {
                log.l($"Exited gamemode. {GamemodeManager.GetGamemode()}");
                GamemodeManager.DeleteGamemode();
            } else {
                con.CheckArgumentCount(splited, 1);
                var gamemodename = splited[1];

                var gm = GamemodeManager.GetAllAvailableGamemodes();

                foreach (var item in gm) {
                    if (item.Name == gamemodename) {

                        var br = GamemodeManager.SetGamemode(item);
                        try {
                            br.Init(splited.Skip(2).ToArray());
                        }
                        catch (Exception ex) {
                            GamemodeManager.DeleteGamemode();
                            log.error($"Failed to Init Gamemode {item.Name}: {ex.ToString()}");
                        }

                        break;
                    }
                }

                con.LogToConsole($"Gamemode \"{gamemodename}\" was not found.");

            }

        }, new Dictionary<int, List<string>> {
            // get all item names
            { 0, GamemodeManager.GetAllAvailableGamemodes().Select(x => x.Name).ToList() }
        },
            ("gamemode", ""),
            ("arg", "")
        ));

        RegisterCommand(new Command("radline", "MP - Activate/Deactivate radline", (string[] args) => {
            con.CheckForWorld();
            ConFailIfNetworkIsRunningAndIsClient();

            var TARGET = !RadiationLine.line.active;

            if (args.Length > 1) {
                TARGET = ConsoleScript.instance.ParseBool(args[1]);
            }

            if (TARGET) {
                RadiationLine.line.Activate();
            } else {
                RadiationLine.line.Deactivate();
                RadiationLine.line.timeGone = -10;
            }
            ServerMain.timer_RareUpdateSyncClients += 1000f;
            ConsoleScript.instance.LogToConsole("Radiation line active: " + RadiationLine.line.active);

        }, null, ("bool", "Activate/Deactivate radline")));

        RegisterCommand_LocalOnly(new Command("musicvolume", "MP", (string[] splited) => {

            var layer = 0f;
            if (splited.Length > 1) {
                layer = con.ParseFloat(splited[1]);
            }
            MusicManager_Start_MultiplayerPatch.musicvolume = layer;

            ConsoleScript.instance.LogToConsole($"custom_music_volume {MusicManager_Start_MultiplayerPatch.musicvolume}");
        }, null, ("float", "volume level 0-1")));

        RegisterCommand_LocalOnly(new Command("audio-only-on-focus", "MP - vc test", (string[] splited) => {
            var target = !_DEV_AUDIO_ONLY_ON_FOCUS;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }

            if (_DEV_AUDIO_ONLY_ON_FOCUS != target) {
                if (target) {
                    _DEV_LAST_VOLUME = AudioListener.volume;
                } else {
                    AudioListener.volume = _DEV_LAST_VOLUME;
                }
            }

            _DEV_AUDIO_ONLY_ON_FOCUS = target;
            ConsoleScript.instance.LogToConsole($"audio_only_on_focus {_DEV_AUDIO_ONLY_ON_FOCUS}");
        }, null));
        RegisterCommand_LocalOnly(new Command("vclistenervolume-individual", "MP - mute or change volume of a specific player, local", (string[] splited) => {
            con.CheckArgumentCount(splited, 2);

            var plridentifier = splited[1];
            var newvolume = Mathf.Max(0, con.ParseFloat(splited[2]));

            try {
                if (TryGetPlayerFromPartialName(plridentifier, out var plr, null)) {
                    plr.vc_output.custom_volume_set = newvolume;
                    con.LogToConsole($"Set vc volume {plr} to {newvolume}");
                } else {
                    con.LogToConsole($"Player does not exist: {plridentifier}");
                }
            } catch (System.Exception e) {
                log.error(e);
            }

        }, null, ("player", ""), ("float", "volume level 0-1")));

        RegisterCommand_LocalOnly(new Command("win32setwindowtitle", "MP dev", (string[] splited) => {
            
            var msg = String.Join(" ", splited.Skip(1));

            win32windowtitlechanger.SetTitle(msg);

            con.LogToConsole($"win32windowtitlechanger.SetTitle(\"{msg}\")");
        }, null, ("string", "message")));

        RegisterCommand(new Command("loadcustommusic", "MP-SERVER- force reload all custom music right now", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();

            MP3PlayerServerAudioStreamer.Server_ForceLoadMusic();
        }, null));

        RegisterCommand_BannedAdmin(new Command("adminpriv-password", "MP-SERVER- set admin password (ANYONE CAN USE THIS PASSWORD)", (string[] splited) => {
            con.CheckArgumentCount(splited, 1);

            server_admin_password = splited[1];

            log.l("Set server admin password.");
        }, null , ("password", "choose strong password")));

        RegisterCommand_BannedAdmin(new Command("adminpriv", "MP-SERVER- give or remove admin privileges", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();

            con.CheckArgumentCount(splited, 2);

            var action = splited[1].ToLower();

            var plridentifier = splited[2];

            if (TryGetPlayerFromPartialName(plridentifier, out var plr, null)) {
                if (plr.IsLocal) {
                    log.l("Local player already has this privilege.");
                    return;
                }

                if (action == "add") {
                    plr.ServerPlayerState.admin_privilege = true;
                    removed_server_admins.Remove(plr.GetPersistentId());
                    log.l($"Gave admin privilege to: {plr}");
                } else if (action == "remove") {
                    plr.ServerPlayerState.admin_privilege = false;
                    server_admins.Remove(plr);
                    removed_server_admins.Add(plr.GetPersistentId());
                    log.l($"Removed admin privilege from: {plr}");

                    var writer = Net.CreateWriter(NetmsgId.CLIENT_Console_DenyAdminMode);
                    writer.Put(false);
                    Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer,  plr.playerId);
                    // WRITER END;

                } else {
                    con.LogToConsole($"{action} -> it should be \"add\" or  \"remove\"");
                }
            } else {
                con.LogToConsole($"{plridentifier} -> player does not exist");
            }
        },
        new Dictionary<int, List<string>> { { 0, 
                new List<string> {"add", "remove"}
            } }
        , ("action", ""), ("player", "")));

        RegisterCommand_LocalOnly(new Command("admin", "MP-CLIENT- toggle admin mode, run commands on the server and see its log", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            ConFailIfNetworkIsRunningAndIsServer();

            var pass = "";
            if (splited.Length > 1) {
                pass = splited[1];
            }

            client_adminmode = !client_adminmode;
            client_isadmin = client_isadmin || client_adminmode;

            log.l($"Requesting admin console: {client_adminmode}");

            var writer = Net.CreateWriter(NetmsgId.SERVER_Console_ActivateAdminMode);
            writer.Put(client_adminmode);
            writer.Put(pass);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            // WRITER END;


        }, null, ("password", "optional (u dont need password if ur whitelisted)")));

        RegisterCommand_LocalOnly(new Command("printposition", "MP", (string[] splited) => {

            con.CheckArgumentCount(splited, 1);

            var pos = con.ParsePosition(splited[1]);

            log.l($"pos {splited[1]} ->\t {pos}");

        }, null, ("position", "")));

        RegisterCommand(new Command("cleanworld", "MP- delete blast marks, block damages, casings, blood", (string[] splited) => {
            ConFailIfCheatsDisabled();
            con.CheckForWorld();

            Con.CleanWorld();
            
            if (Net.IsServer) {
                var writer = Multiplayer.CreateNamedWriter("CustomCommand");
                writer.Put("cleanworld");
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
                // WRITER END; 
            }

            bool aaaaaa = false;
            if (splited.Length > 1) {
                aaaaaa = con.ParseBool(splited[1]);
            }
        }, null));

        RegisterCommand(new Command("setplrname", "MP-SERVER- force rename a player", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();

            con.CheckArgumentCount(splited, 2);

            var plridentifier = splited[1];
            var newname = splited[2];

            bool succ = false;


            var renamer = (ScavPlayer plr) => {
                succ = true;

                con.LogToConsole($"Renamed {plr} to {newname}");
                plr.nameIsCustom = true;
                plr.ApplyNameAndColor(newname,plr.playerColor);
                
            };

            try {

                var res = PerformActionOnPlayersByName(plridentifier, renamer, true, GetCommandCallerPlayer(), false, false);

                if (!res.Item1) {
                    con.LogToConsole("Rename error: " + res.Item2);
                }
            } catch (System.Exception e) {
                log.error(e);
            }


        }, null, ("player", ""), ("string", "new name")));

        RegisterCommand(new Command("setplrcolor", "MP-SERVER- force recolor a player", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            
            con.CheckArgumentCount(splited, 2);

            var plridentifier = splited[1];

            Color newcolor;
            if (TryGetPlayerFromPartialName(splited[2],out var colorplrrrrrrrrr, null)) {
                newcolor = colorplrrrrrrrrr.playerColor;
            } else {
                newcolor = con.ParseColor(splited[2]);
            }

            if (Net.IsRunning) {

                bool succ = false;

                var recolorerr = (ScavPlayer plr) => {
                    succ = true;

                    con.LogToConsole($"Recolored {plr} to {newcolor}");
                    plr.ApplyNameAndColor(plr.playerName, newcolor);
                    
                };

                try {
                    var res = PerformActionOnPlayersByName(plridentifier, recolorerr, true, GetCommandCallerPlayer(), false, false);

                    if (!res.Item1) {
                        con.LogToConsole("Recolor error: " + res.Item2);
                    }
                } catch (System.Exception e) {
                    log.error(e);
                }
            } else {
                UIMainMenu.SetInputColor(newcolor);
                con.LogToConsole($"Set local color to {UIMainMenu.LAST_VALID_INPUT_COLOR}");
            }

        }, null, ("player", ""), ("color", "new color")));

        RegisterCommand_LocalOnly(new Command("mpdumplocale", "MP - dump locale of multiplayer mod", (string[] splited) => {
            var con = ConsoleScript.instance;

            var text = new StringBuilder("// KROKOSHA CO-OP MOD LOCALE DUMP:\n");


            if (splited.Length > 1) {
                var lang = splited[1];

                text.AppendLine("\n// " + lang.ToSafeString());
                var lan = new Language();
                Dictionary<string,string> copy = new(Lang.dict[lang]);
                foreach (var i in copy) {
                    lan.other[(Lang.LocalPrefixTag + i.Key)] = i.Value;
                }

                text.Append(JsonConvert.SerializeObject(lan, Formatting.Indented));
            } else {
                foreach (var item in Lang.dict) {
                    var lan = new Language();
                    Dictionary<string, string> copy = new(item.Value);
                    foreach (var i in copy) {
                        lan.other[(Lang.LocalPrefixTag + i.Key)] = i.Value;
                    }
                    text.AppendLine("\n// " + item.Key);

                    text.Append(JsonConvert.SerializeObject(lan, Formatting.Indented));
                }
            }


            text.AppendLine("\n// END\n// COPY IT FROM LOG FILE INSTEAD OF CONSOLE BECAUSE IT CLEARS FORMATTING HERE");

            log.l(text.ToString());
        }, null));

        RegisterCommand_LocalOnly(new Command("mpverbose", "MP dev - Enable verbose logging and debug screens", (string[] splited) => {
            var target = !Multiplayer.verbose;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            DebugMenuSettings.is_verbose = target;
            DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = target;
            DebugMenuSettings._DEV_ENABLE_STEAM_LOG = target;

            log.l($"DEV : verbose = {Multiplayer.verbose} ");
        }, null));
        
        RegisterCommand_LocalOnly(new Command("syncinfo", "MP dev - Show debug sync info on objects. (DEPRECATED)", (string[] splited) => {
            var target = !NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = target;

            log.l($"NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = {NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING} ");
        }, null));

        RegisterCommand(new Command("servermute", "MP-SERVER- mute player chat or voice", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            ConFailIfNetworkIsRunningAndIsClient();

            con.CheckArgumentCount(splited, 2);

            bool istc = false;
            bool isvc = false;
            var type = splited[1].ToLower();
            var plrname = splited[2];
            bool has_target = false;
            bool target = true;
            if (splited.Length > 3) {
                has_target = true;
                target = con.ParseBool(splited[3]);
            }
            if (type == "tc") {
                istc = true;
            } else if (type == "vc") {
                isvc = true;
            } else if (type == "all") {
                isvc = true;
                istc = true;
                has_target = true;
            } else {
                throw new Exception($"Unknown chat type: {type}");
            }

            var res = PerformActionOnPlayersByName(plrname, (plr) => {
                if (isvc) {
                    if (has_target) {
                        plr.server_mute_vc = target;
                    } else {
                        plr.server_mute_vc = !plr.server_mute_vc;
                    }
                    log.l($"{plr}.mute_vc = {plr.server_mute_vc}");
                } 
                if (istc) {
                    if (has_target) {
                        plr.server_mute_tc = target;
                    } else {
                        plr.server_mute_tc = !plr.server_mute_tc;
                    }
                    log.l($"{plr}.mute_tc = {plr.server_mute_tc}");
                }
            }, true, GetCommandCallerPlayer(), false, false);

            if (!res.Item1) {
                con.LogToConsole("servermute fail: " + res.Item2);
            }

        }, null, ("type", "tc/vc/all"), ("player", ""), ("bool", "optional")));

        RegisterCommand(new Command("serverpause", "MP - Pause time.", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            con.CheckForWorld();
            ConFailIfNetworkIsRunningAndIsClient();

            bool doPause = Time.timeScale != 0;
            if (splited.Length > 1) {
                doPause = con.ParseBool(splited[1]);
            }
            doPause = !doPause;

            PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Paused);
            Time.timeScale = doPause.ToByte();

        }, null, ("bool", "1 pause, 0 continue")));

        RegisterCommand(new Command("syncreset", "MP dev - Unregister all objects.", (string[] splited) => {
            if (Net.IsClient) {
                return;
            }
            ConFailIfNetworkNotRunning();

            if (Net.Transport != null && Net.Transport is TransportLiteNetLib tlnl) {
                tlnl.NetManager.Statistics.Reset();
            }

            bool deleteInventory = false;
            if (splited.Length > 1) {
                deleteInventory = con.ParseBool(splited[1]);
            }
            if (deleteInventory) {
                NetObjectRegistry.UnregisterAllObjects();
                log.l($"NetObjectRegistry.UnregisterAllObjects();");
            } else {
                NetObjectRegistry.UnregisterAllObjectsButKeepInventory();
                log.l($"NetObjectRegistry.UnregisterAllObjectsButKeepInventory();");
            }
        }, null, ("bool", "1 keep inventory, 0 for simple")));

        RegisterCommand(new Command("syncregisterall", "MP dev - Register all objects in the world. (IT LAGS !!!!!)", (string[] splited) => {
            ConFailIfNetworkNotRunning();

            foreach (var item in (GameObject.FindObjectsOfType<Item>().Select(x => x.gameObject).Union(GameObject.FindObjectsOfType<BuildingEntity>().Select(x => x.gameObject)))) {
                if (NetObjectRegistry.ObjectShouldBeSynced(item)) {
                    NetObjectRegistry.NewGO(item);
                }
            }
            log.l($"called NetObjectRegistry.NewGO on everything");
        }, null));
        
        RegisterCommand_LocalOnly(new Command("maxplayers", "MP - SERVER - Set player limit", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();

            con.CheckArgumentCount(splited,1);
            Multiplayer.PLAYER_COUNT_LIMIT = (byte)Mathf.Clamp(con.ParseInt(splited[1]),0,251);

            con.LogToConsole($"Set player count limit to: {Multiplayer.PLAYER_COUNT_LIMIT}");

        }, null, ("int","")));

        RegisterCommand_LocalOnly(new Command("chatboxpos", "MP - set position and size of chat box (everything is a scale in float (0-1)) (OBSOLETE)", (string[] splited) => {
            if (splited.Count() == 1) {
                Chat.SETTING_posX = 1;
                Chat.SETTING_posY = 1;
                Chat.SETTING_sizeX = 1;
                Chat.SETTING_sizeY = 1;
                Chat.SETTING_scale = 1;
                con.LogToConsole($"Chatbox: Reset chatbox location.");
                return;
            }


            con.CheckArgumentCount(splited, 5);

            Chat.SETTING_posX = Mathf.Clamp01(con.ParseFloat(splited[1]));
            Chat.SETTING_posY = Mathf.Clamp01(con.ParseFloat(splited[2]));
            Chat.SETTING_sizeX = con.ParseFloat(splited[3]);
            Chat.SETTING_sizeY = con.ParseFloat(splited[4]);
            Chat.SETTING_scale = con.ParseFloat(splited[5]);

            con.LogToConsole($"Chatbox: SETTING NEW PARAMETERS:");
            con.LogToConsole($"Chatbox: setting_posX  = {(Chat.SETTING_posX * 100f)}%");
            con.LogToConsole($"Chatbox: setting_posY  = {(Chat.SETTING_posY * 100f)}%");
            con.LogToConsole($"Chatbox: setting_sizeX = {(Chat.SETTING_sizeX * 100f)}%");
            con.LogToConsole($"Chatbox: setting_sizeY = {(Chat.SETTING_sizeY * 100f)}%");
            con.LogToConsole($"Chatbox: setting_scale = {(Chat.SETTING_scale * 100f)}%");

        }, null, ("float", "X pos"), ("float", "Y pos"), ("float", "X size"), ("float", "Y size"), ("float", "scale")));

        RegisterCommand_LocalOnly(new Command("chatpreview", "MP dev - toggle chat text preview (OBSOLETE)", (string[] splited) => {
           
            ref bool val = ref Chat._DEV_ENABLE_PREVIEW;
            var target = !val;
            if (splited.Length > 1) {
                target = ParseBool01(splited[1]);
            }
            val = target;
            con.LogToConsole($"Chat._DEV_ENABLE_PREVIEW = {val}");

        }, null, ("bool", "")));

        RegisterCommand(new Command("tomainmenu", "MP - go to main menu", (string[] splited) => {
            con.CheckForWorld();
            PlayerCamera.main.ToMainMenu();
        }, null));

        RegisterCommand_LocalOnly(new Command("currentrules", "MP - show all multiplayer game rules", (string[] splited) => {
            var rules = "Krokosha Co-op mod game rules:\n";
            foreach (var fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                rules += $"rule {fld.Name} [{fld.FieldType.Name}] // current: {fld.GetValue(Multiplayer.rules)}\n";
            }
            con.LogToConsole(rules);
        }, null));
        RegisterCommand(new Command("resetrules", "MP - reset all multiplayer game rules to default values", (string[] splited) => {
            Multiplayer.rules = new MultiplayerGameRules();
            con.LogToConsole("Reset all rules to default.");
        }, null));
        RegisterCommand(new Command("rule", "MP - set multiplayer game rule", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 2);

            var rulename = splited[1];
            var rulevalstr = splited[2];
            foreach (var fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                if (fld.Name == rulename) {
                    try {
                        object typedValue;
                        if (fld.FieldType == typeof(bool)) {
                            typedValue = ParseBool01(rulevalstr);
                        } else {
                            typedValue = Convert.ChangeType(rulevalstr, fld.FieldType, CultureInfo.InvariantCulture);
                        }
                        TypedReference rulesref = __makeref(Multiplayer.rules);
                        fld.SetValueDirect(rulesref, typedValue);
                        
                        Multiplayer.ApplyGameRules();

                        con.LogToConsole($"Succesfully set rule '{fld.Name}' to '{fld.GetValue(Multiplayer.rules)}'.");
                        return;
                    }
                    catch (Exception ex) {
                        con.LogToConsole($"Failed to set rule '{fld.Name}': {ex.Message}");
                        return;
                    }
                }
            }
            con.LogToConsole($"Rule '{rulename}' does not exist!");

        }, new Dictionary<int, List<string>> { { 0, typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name).ToList() } }, ("string rule", "Rule Name"), ("value", "New value")));

        RegisterCommand(new Command("setlocalname", "MP - Set your multiplayer username.", delegate (string[] args) {
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            if (PlayerCamera.main) {
                throw new Exception("Name can only be set in main menu.");
            }
            ConFailIfNetworkAlreadyRunning();
            var name = Multiplayer.SanitizeTextInput(args[1]);
            con.LogToConsole("Set your username to: " + name);
            UIMainMenu.USERINPUT_NAME = name;
        }, null, ("string name", "Your new name")));
        RegisterCommand(new Command("setlocalpassword", "MP - Set your multiplayer password.", delegate (string[] args) {
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            if (PlayerCamera.main) {
                throw new Exception("Password can only be set in main menu.");
            }
            ConFailIfNetworkAlreadyRunning();
            var newpass = args[1];
            con.LogToConsole("Set your password to: " + newpass);
            Multiplayer.INPUT_PASSWORD = newpass;
        }, null, ("string password", "")));


        RegisterCommand(new Command("setservername", "MP - Set your server name.", delegate (string[] args) {
            Con.ConFailIfNetworkIsRunningAndIsClient();
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            var name = (string.Join(" ", args.Skip(1)));
            Multiplayer.SetServerName(name);
            con.LogToConsole("Set server name to: " + Net.CurrentServerInfo.name);
        }, null, ("string name", "New name")));
        RegisterCommand(new Command("setserverdesc", "MP - Set your server description.", delegate (string[] args) {
            Con.ConFailIfNetworkIsRunningAndIsClient();
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            var desc = (string.Join(" ", args.Skip(1)) );
            Multiplayer.SetServerDescription(desc);
            con.LogToConsole("Set server desc to: " + Net.CurrentServerInfo.description);
        }, null, ("string name", "New description")));
        

        #region STEAM_CONNECTDISCONNECT

        RegisterCommand_LocalOnly(new Command("joinlobby", "MP - Client Connect to an active steam lobby.", delegate (string[] args) {

            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();

            var lobbyId = ulong.Parse(args[1]);

            TransportSteamworks.OnWantToJoinLobby(lobbyId);

            con.LogToConsole("Starting client and attempting join: " + lobbyId);
            
        }, null, ("SteamId", "")));
        RegisterCommand_LocalOnly(new Command("hostlobby", "MP - Host steam lobby.", delegate (string[] args) {

            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            con.CheckArgumentCount(args, 2);

            var lobbytype = con.ParseInt(args[1]);
            Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED = con.ParseBool(args[2]);

            var type = KSteam.NumToLobbyType(lobbytype);

            var nettttt = Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED ? Net.NetType.DedicatedServer : Net.NetType.Host;

            Multiplayer.DoMultiplayerStatusMessageLog($"Hosting Steam Lobby: {nettttt}, {type}" );

            TransportSteamworks.OnWantToHostLobby(
                nettttt,
                type
            );

        }, null, ("int", "Lobby type (2 = public, 1 = friends only, 0 = private)"), ("bool", "dedicated")));
        RegisterCommand_LocalOnly(new Command("getlobbyinfo", "MP - Log info about your steam lobby.", delegate (string[] args) {

            ConFailIfNotInSteamLobby();

            log.l("LOBBY INFO:\n" + KSteam.CURRENT_LOBBY.DebugReadableDump());

        }, null));
        RegisterCommand_LocalOnly(new Command("dumplobbysearch", "MP - Dump all lobbies shown in server browser.", delegate (string[] args) {
            // clients are fine
            var filtered = con.ParseBool(args[1]);

            var text = new StringBuilder("// LOBBY SEARCH DUMP:\n");
            foreach (var item in (filtered ? UIServerBrowser.CurFilteredServerList : UIServerBrowser.CurServerList)) {
                if (item.steam_lobby_info != null) {
                    text.AppendLine(item.steam_lobby_info.DebugReadableDump());
                }
            }
            text.AppendLine("===============");

            log.l(text.ToString());
        }, null, ("bool", "filtered")));

        #endregion


        #region IP_CONNECTDISCONNECT

        RegisterCommand_LocalOnly(new Command("connect", "MP - Client Connect to an active server.", delegate (string[] args) {

            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            if (args.Length > 1) {
                UIMainMenu.USERINPUT_IPPORT = args[1];
            }
            con.LogToConsole("Starting client and attempting connection: " + UIMainMenu.USERINPUT_IPPORT);
            TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Client);

        }, null, ("string ip:port", "optional, IP and PORT of target server")));
        RegisterCommand_LocalOnly(new Command("starthost", "MP - Host the game.", delegate (string[] args) {

            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();

            if (args.Length > 1) {
                if (ushort.TryParse(args[1], out var portnum)) {
                    UIMainMenu.USERINPUT_IPPORT = "0.0.0.0:" + args[1];
                } else {
                    UIMainMenu.USERINPUT_IPPORT = args[1];
                    // use the already inputted  Multiplayer.input_ipport_text
                }
            }

            con.LogToConsole("Starting Host: " + UIMainMenu.USERINPUT_IPPORT);

            TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Host);

        }, null, ("int port", "")));
        RegisterCommand_LocalOnly(new Command("startserver", "MP - Start a dedicated server. (you'll have no player character)", delegate (string[] args) {

            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();

            if (args.Length > 1) {
                if (ushort.TryParse(args[1], out var portnum)) {
                    UIMainMenu.USERINPUT_IPPORT = "0.0.0.0:" + args[1];
                } else {
                    UIMainMenu.USERINPUT_IPPORT = args[1];
                    // use the already inputted  Multiplayer.input_ipport_text
                }
            }

            con.LogToConsole("Starting Server: " + UIMainMenu.USERINPUT_IPPORT);

            TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.DedicatedServer);

        }, null, ("int port", "")));

        #endregion


        RegisterCommand(new Command("pasterunsettings", "MP - Paste run settings from a string.", delegate (string[] args) {
            if (Util.IsInWorld()) {
                ConFailIfCheatsDisabled();
            }
            ConFailIfNetworkIsRunningAndIsClient();

            con.CheckArgumentCount(args, 1);
            WorldMP.ApplyRunSettings(args[1]);
        }, null, ("string base64", "")));

        {
            var runset = RegisterCommand(new Command("runset", "MP - Set a run setting.", delegate (string[] args) {
                if (Util.IsInWorld()) {
                    ConFailIfCheatsDisabled();
                }
                ConFailIfNetworkIsRunningAndIsClient();

                con.CheckArgumentCount(args, 2);

                var ruleName = args[1];
                var valueString = args[2];

                var originals = WorldMP.GetNormalRunSettings();
                if (!originals.TryGetValue(ruleName, out var value)) {
                    throw new Exception($"Unknown run setting: {ruleName}");
                }

                object parsedValue = TypeDescriptor.GetConverter(value.GetType()).ConvertFromInvariantString(valueString);

                if (PreRunScript.instance != null) {
                    if (PreRunScript.instance.runSettings != null) {
                        PreRunScript.instance.runSettings[ruleName] = parsedValue;
                    }
                    PreRunScript.instance.UpdateAllSettingDisplays();
                }
                if (WorldGeneration.runSettings != null) {
                    WorldGeneration.runSettings[ruleName] = parsedValue;
                }

                con.LogToConsole($"Set run setting: {ruleName} to {parsedValue}");

            }, null, ("string name", ""), ("value", "")));

            runset.GetArgAutofillList(0).AddRange(WorldMP.GetNormalRunSettings().Keys);
        }


        RegisterCommand_LocalOnly(new Command("disconnect", "MP - Disconnect from server or shutdown server.", delegate (string[] args) {

            ConFailIfNetworkNotRunning();
            log.l("Disconnect.");

            Multiplayer.ShutdownNetwork();

        }, null));


        RegisterCommand_LocalOnly(new Command("screensize", "MP dev - set game window resolution", (string[] splited) => {
            Screen.SetResolution(fullscreenMode: FullScreenMode.Windowed, width: con.ParseInt(splited[1]), height: con.ParseInt(splited[2]));
        }, null));



#if !RELEASE
        RegisterCommand_LocalOnly(new Command("TEMP_known_entities_with_nonunique_id_to_og", "MP DEV DEV DELETEME", (string[] splited) => {

            log.l($"TEMP_known_entities_with_nonunique_id_to_og START =============");
            foreach (var item in ScavMPBuildingSynchronizer.TEMP_known_entities_with_nonunique_id_to_og) {
                log.l($"{item.Key} -> {item.Value}");
            }
            log.l($"TEMP_known_entities_with_nonunique_id_to_og END ==============");

        }, null));

        {
            var krok = new Command("krok", "DEPRECATED- Multiplayer mod control. (legacy from before 1.12, no autocompletion)", delegate (string[] args) {
                con.LogToConsole(KrokoshaLegacyCommand(string.Join(" ", args), args.ToList()));
            }, null);
            RegisterCommand(krok);
        }
#endif


        if (Plugin.s != null) {
            try {
                Plugin.s.SendMessage("OnRegisterConsoleCommands",SendMessageOptions.DontRequireReceiver);
            } catch(Exception e) {
                log.error(e);
            }
        }

        log.l("Patched the console!");
        if (Plugin.s != null && Plugin.s.TryGetComponent(out Plugin.LoadErrorNotifier wrongver)) {
            log.error("WrongVersionNotifier:\n" + Plugin.LoadErrorNotifier.errorstr);
        }


        UpdatePlayerListAutocomplete();
    }




    public static HashSet<string> localonly_commands = new HashSet<string>{
        "clear",
        "noclip",
        "copylog",
        "volume",
        "musicvolume",
        "pixelate",
        "spectate",
        "fullbright",
        "unchipped",

        "echo",
        "bind",
        "addcustomcommand",
        "removecustomcommand",

        "framerate",
        "setconsoleheight",
        "setconsolecolor",
    };
    public static HashSet<string> banned_admin_commands = new HashSet<string>{
        "noclip",
        "volume",
        "musicvolume",
        "adminpriv",
        "pixelate",
        "spectate",
        "fullbright",
        "music",


        "echo",
        "bind",
        "addcustomcommand",
        "removecustomcommand",

        "framerate",
        "setconsoleheight",
        "setconsolecolor",
    };


    public static bool Server_CheckBannedCommand(in ScavPlayer plr,in string first_arg) {
        var com = first_arg;
        if (localonly_commands.Contains(com)) {
            log.sus($"{plr} tried to execute a local-only command: {com}");
            return true;
        }
        if (banned_admin_commands.Contains(com)) {
            log.sus($"{plr} tried to execute a banned admin command: {com}");
            return true;
        }
        return false;
    }


    static bool SanitizeClientAdminCommand(ScavPlayer plr, ref string[] args, Vector2 cursorpos) {
        var realcomm = ConsoleScript.SearchExact(args[0]);
        if (realcomm != null) {
            try {
                if (realcomm.name == "repeat") {
                    var commands = args[3];
                    foreach (var item in commands.Split(';')) {
                        var splited = item.Replace('_', ' ').Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (splited.Length > 0) {
                            if (Server_CheckBannedCommand(plr, splited[0])) {
                                return false;
                            }
                        }
                    }
                } else if (realcomm.name == "spawn") {
                    if (args.Length == 2) {
                        args = args.AddToArray("cursor");
                    }
                }

            } catch (Exception e) {
                log.error($"SERVER: REQUEST FROM {plr} TO TryExecuteCommand \n" + e.ToString());
                return false;
            }

            // Just in case keep the old parser here
            try {
                for (int i = 0; i < Math.Min(args.Length - 1, realcomm.argDescription.Length); i++) {
                    ref string arg = ref args[i + 1];
                    if (realcomm.argDescription[i].shortDesc.StartsWith("position")) {
                        if (arg == "cursor") {
                            arg = $"{cursorpos.x.ToString(CultureInfo.InvariantCulture)},{cursorpos.y.ToString(CultureInfo.InvariantCulture)}";
                        } else if (arg == "player") {
                            arg = $"plr:id:{plr.playerId}";
                        } else if (TryGetTaggedPlayerName(arg, out var realname)) {
                            if (realname == "@m") {
                                arg = $"plr:id:{plr.playerId}";
                            } else if (realname == "@c") {
                                var aaaa = GetBodyForCommandOnCursor(cursorpos, true);
                                if (aaaa != null && aaaa.IsPlayer) {
                                    arg = $"plr:id:{aaaa.plr.playerId}";
                                }
                            }
                        }
                    } else if (realcomm.argDescription[i].shortDesc.StartsWith("player")) {
                        if (arg == "@m") {
                            arg = $"id:{plr.playerId}";
                        } else if (arg == "@c") {
                            var aaaa = GetBodyForCommandOnCursor(cursorpos, true);
                            if (aaaa != null && aaaa.IsPlayer) {
                                arg = $"id:{aaaa.plr.playerId}";
                            }
                        }
                    }
                }
            } catch (Exception e) {
                log.error($"SERVER: REQUEST FROM {plr} TO TryExecuteCommand \n" + e.ToString());
                return false;
            }


        }
        return true;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Console_TryExecuteCommand)]
    static void ServerReceiver_Console_TryExecuteCommand(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out string command);
        reader.Get(out Vector2 cursorpos);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            if (server_admins.Contains(plr) || plr.ServerPlayerState.admin_privilege) {
                var args = command.Trim().Split(' ');

                if (Server_CheckBannedCommand(plr,args[0])) {
                    return;
                }

                if (!SanitizeClientAdminCommand(plr, ref args, cursorpos)) {
                    return;
                }


                log.l($"[<color=red>ADMIN</color>] {plr} executes \"{String.Join(" ", args)}\"");

                try {
                    CommandCallerPlayer = plr;
                    CommandCallerPlayer.CursorWorldPos = cursorpos;
                    ConsoleScript.instance.TryExecuteCommand(args, false);
                } catch (Exception e) {
                    log.error($"SERVER: REQUEST FROM {plr} TryExecuteCommand \n" + e.ToString());
                }
                CommandCallerPlayer = null;

            }
        }

    }

    [ServerReceiver((byte)NetmsgId.SERVER_Console_ActivateAdminMode)]
    static void ServerReceiver_Console_ActivateAdminMode(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out bool fsdfdsdfs);
        reader.Get(out string pass);
        
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            if (fsdfdsdfs && !plr.IsLocal) {
                var is_pass = (
                    !String.IsNullOrEmpty(server_admin_password) &&
                    pass == server_admin_password
                );

                if (
                    !removed_server_admins.Contains(plr.GetPersistentId()) &&
                    (plr.ServerPlayerState.admin_privilege || is_pass || KnownPersons.IsSteamUserPrivileged(plr.SteamId))
                ) {
                    server_admins.Add(plr);
                    var str = $"[<color=red>ADMIN</color>] {plr} <color=green><b>ENTERS</b></color> server console!";
                    if (is_pass) {
                        plr.ServerPlayerState.admin_privilege = true;
                        str += " (Entered correct password)";
                    }
                    log.l(str);
                } else {
                    var writer = Net.CreateWriter(NetmsgId.CLIENT_Console_DenyAdminMode);
                    writer.Put(false);
                    Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: plr.playerId);
                    // WRITER END;
                }
            } else {
                server_admins.Remove(plr);
                log.l($"[<color=red>ADMIN</color>] {plr} <color=red><b>EXITS</b></color> server console!");
            }
        }

    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Console_DenyAdminMode)]
    static void ClientReceiver_Console_DenyAdminMode(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        client_adminmode = false;
        client_isadmin = false;
        log.l("Server denied server console access.");
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_Console_Log)]
    static void ClientReceiver_Console_Log(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out byte[] compressed_string);

        var uncompressed = Util.DecompressDeflate(compressed_string);

        var as_string = Encoding.UTF8.GetString(uncompressed);

        ConsoleScript.instance.LogToConsole("[<color=red>SERVER</color>] " + as_string);
    }

    







    public static void UpdatePlayerListAutocomplete() {

        List<string> player_colon_plrlist = new();
        List<string> plrlist = new();
        List<string> macros_plus_plrlist = new();

        if (Net.IsRunning) {
            plrlist = ScavPlayer.ClientIdToPlayerDict.Values.Select(x => x.playerName).ToList();

            player_colon_plrlist = new List<string> { "@c", "@r", "@m" };
            player_colon_plrlist.AddRange(plrlist);
            for (int i = 0; i < player_colon_plrlist.Count; i++) {
                player_colon_plrlist[i] = "plr:" + player_colon_plrlist[i];
            }

            var allmacros = new List<string> { "@c", "@a", "@o", "@r", "@m" };
            allmacros.AddRange(plrlist);
            macros_plus_plrlist = allmacros.ToList();
        }


        foreach (var com in ConsoleScript.Commands) {

            for (int i = 0; i < com.argDescription.Length; i++) {
                if (com.argDescription[i].shortDesc.StartsWith("player")) {
                    com.GetArgAutofill()[i] = macros_plus_plrlist;
                }

                if (com.argDescription[i].shortDesc.StartsWith("position")) {
                    //OG: Command.Command V5t5 
                    var og = new List<string> { "cursor", "player", "random", "#,#" };
                    og.AddRange(player_colon_plrlist);

                    com.GetArgAutofill()[i] = og;
                }
            }
        }
    }

    public static void Server_SendConsoleLog(in string text, ScavPlayer plr) => Server_SendConsoleLog(text, new List<knetid> { plr.playerId} );
    public static void Server_SendConsoleLog(in string text, IReadOnlyList<knetid> targets = null) {
        if (targets == null) {
            targets = Con.server_admins.Select(x => x.playerId).ToList();
        }
        if (targets.Count > 0) {
            byte[] compressed = new byte[0];
            try {
                compressed = Util.CompressDeflate(Encoding.UTF8.GetBytes(text));

                var writer = Net.CreateWriter(NetmsgId.CLIENT_Console_Log);
                writer.PutBytesWithLength(compressed);

                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: targets);

                // WRITER END;
            } catch (Exception e) {
                // not log.error cuz it gets recursive
                Plugin.log.LogError($"ConsoleScript_LogToConsole_MultiplayerPatch compressed len:{compressed.Length}\n{e.ToString()}\n{new StackTrace()}");
            }
        }
    }
    public static bool CanExecuteAdminCommands() {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsServer) {
            return true;
        }
        return client_isadmin;
    }
    public static void ExecuteCommandAdminNoLog(in string command) {
        if (!Net.IsRunning || Net.IsServer) {
            con.TryExecuteCommand(command.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), false);
        } else {
            Client_ExecuteAdminCommand(command);
        }
    }
    public static void Client_ExecuteAdminCommand(in string command) { 

        var writer = Net.CreateWriter(NetmsgId.SERVER_Console_TryExecuteCommand);
        writer.Put(command);
        writer.Put(Util.GetCursorWorldPos());

        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

        // WRITER END;
    }


    internal static HashSet<ScavPlayer> server_admins = new();
    internal static HashSet<string> removed_server_admins = new();
    internal static string server_admin_password = "";
    public static bool client_adminmode = false;
    public static bool client_isadmin = false;
    void Start() {

        ScavPlayer.OnPlayerLeft += (ScavPlayer plr) => {
            server_admins.Remove(plr);
        };

    }






    #region CONSOLE_ONFRAME



    void Update() {

        if (_DEV_TELEKINESIS) {
            var cursorpos = Util.GetCursorWorldPos();
            if (UnityEngine.Input.GetKeyDown(KeyCode.Mouse1)) {
                _DEV_TELEKINESIS_OBJECT = null;
                var hits = Physics2D.OverlapPointAll(cursorpos);
                foreach (var obj in hits) {

                    var b = obj.GetComponentInParent<Body>();
                    if (b != null) {
                        if (Util.IsBodyLocal(b)) {
                            continue;
                        }
                        if (b.TryGetComponent(out Limb l)) {
                            _DEV_TELEKINESIS_OBJECT = l.gameObject;
                        } else {
                            _DEV_TELEKINESIS_OBJECT = b.gameObject;
                        }
                    } else {
                        if (obj.TryGetComponent(out BuildingEntity bb)) {
                            _DEV_TELEKINESIS_OBJECT = obj.gameObject;
                        }
                        if (obj.TryGetComponent(out Rigidbody2D rb)) {
                            _DEV_TELEKINESIS_OBJECT = rb.gameObject;
                        }
                        
                    }
                    if (_DEV_TELEKINESIS_OBJECT != null) {
                        if (_DEV_TELEKINESIS_OBJECT.TryGetComponent<ChunkScript>(out var c)) {
                            _DEV_TELEKINESIS_OBJECT = null;
                            continue;
                        }

                        break;
                    }
                }
                log.l($"telekinesis click: " + _DEV_TELEKINESIS_OBJECT.ToSafeString());
            }
            if (UnityEngine.Input.GetKeyUp(KeyCode.Mouse1)) {
                _DEV_TELEKINESIS_OBJECT = null;
            }
        }
    }

    static Vector2 lastworldcursorpos = new Vector2();
    void FixedUpdate() {
        var cursorpos = Util.GetCursorWorldPos();
        var cursorposfixeddelta = -(lastworldcursorpos - cursorpos);

        if (_DEV_TELEKINESIS) {

            if (!Con.CanCheat()) {
                _DEV_TELEKINESIS = false;
                _DEV_TELEKINESIS_OBJECT = null;
            }


            if (_DEV_TELEKINESIS_OBJECT != null) {
                if (((_DEV_TELEKINESIS_OBJECT.TryGetComponent(out Limb l) && l.TryGetNetBody(out NetBody npc)) || _DEV_TELEKINESIS_OBJECT.TryGetComponent(out  npc)) && !npc.IsLocal) {
                    
                    {
                        npc.body.SetVelocity(Vector2.zero);
                        npc.body.rb.gravityScale = 0;
                        _DEV_TELEKINESIS_OBJECT.transform.position += (Vector3)cursorposfixeddelta;
                    }

                    if (Multiplayer.IsNetworkActiveAndIsServer()) {
                        npc.body.SetVelocity(Vector2.zero);
                        npc.Server_RemindPlayersCurrentState( false);
                    }
                } else {
                    if (_DEV_TELEKINESIS_OBJECT.TryGetComponent(out Rigidbody2D rb) && rb.bodyType == RigidbodyType2D.Dynamic && rb.simulated) {
                        var dirtogo = KM.NormalTo(rb.position, cursorpos, out var dist);

                        //var force = (dirtogo * (dist * rb.mass * 5));// - Physics2D.gravity;

                        rb.AddForce(-Physics2D.gravity * 0.6f);

                        rb.velocity += dirtogo * 5;
                    } else {
                        _DEV_TELEKINESIS_OBJECT.transform.position += (Vector3)cursorposfixeddelta;
                        if (_DEV_TELEKINESIS_OBJECT.TryGetComponent(out TraderScript ts)) {
                            var v = (Vector2)_DEV_TELEKINESIS_OBJECT.transform.position;
                            ts.MoveRange = new RangeF(v.x, v.x);
                            ts.desiredPos = v;
                        }

                    }
                    if (_DEV_TELEKINESIS_OBJECT.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker)) {
                        if (tracker.syncInfo != null) {
                            //NetObjectRegistry.Server_QueueSync(tracker.syncInfo);
                        }
                    }
                }
            }

        }
        lastworldcursorpos = cursorpos;
    }

    // disabling volume for focus loss, to hear only one game at the time 
    void LateUpdate() {
        if (_DEV_AUDIO_ONLY_ON_FOCUS) {
            if (Application.isFocused) {
                AudioListener.volume = _DEV_LAST_VOLUME;
            } else {
                if (AudioListener.volume > 0.0101f) {
                    _DEV_LAST_VOLUME = AudioListener.volume;
                }
                AudioListener.volume = 0.01f;
            }
        }

    }










    public static List<ScavPlayer> GetPlayersForCommand(string plrname, ScavPlayer caller, bool onlyOne, bool allowMacros, List<ScavPlayer> toFilter = null) {

        if (toFilter == null)
            toFilter = ScavPlayer.ClientIdToPlayerDict.Values.ToList();

        var list = new List<ScavPlayer>();

        if (string.IsNullOrEmpty(plrname) || toFilter.Count == 0) {
            return list;
        }
        var loweredPlrname = plrname.ToLower();

        if (plrname.StartsWith('@') && (allowMacros || loweredPlrname == "@c")) {

            if (!onlyOne) {
                if (loweredPlrname == "@a") { // All players
                    return toFilter;
                } else if (loweredPlrname == "@o") { // all players except caller
                    toFilter.Remove(caller);
                    return toFilter;
                }
            }

            if (loweredPlrname.StartsWith("@m") && caller != null && toFilter.Contains(caller)) { // me
                list.Add(caller);
                return list;
            } else if (loweredPlrname.StartsWith("@r")) { // Random

                int requiredCount = 1;
                // Allow numbers:  @r:2   - Random 2 players
                if (!onlyOne) {
                    int colon = -1;
                    for (int i = 2; i < loweredPlrname.Length; i++) { if (char.IsDigit(loweredPlrname[i])) { colon = i; break; } }
                    if (colon != -1) {
                        var loweredPlrnamePlayer = loweredPlrname.Substring(colon);
                        if (int.TryParse(loweredPlrnamePlayer, out var c) && c > 0) {
                            requiredCount = c;
                        }
                    }
                }
                requiredCount = Math.Min(requiredCount, toFilter.Count);

                toFilter.ShuffleRandom();

                return toFilter.GetRange(0, requiredCount);
            } else if (loweredPlrname.StartsWith("@c")) { // cursor
                var cursorPos = Util.GetCursorWorldPos();
                if (caller != null) {
                    cursorPos = caller.CursorWorldPos;
                }

                var person = GetBodyForCommandOnCursor(cursorPos, true);

                list.Add(person.plr);

                return list;
            } else {
                return list;
            }

        } else {

            if (plrname.TryTrimStart("body:", out var bodyId, true)) {
                if (ushort.TryParse(bodyId, out var bodyNetId) && NetBody.TryGetNetBodyFromId(bodyNetId, out var nb) && nb.IsPlayer) {
                    list.Add(nb.plr);
                    return list;
                }
            } else if (!onlyOne && plrname.TryTrimStart("team:", out var team, true)) {
                if (Color24.TryParseHex(team, out var teamColor)) {
                } else if (TryGetPlayerFromPartialName(plrname, out var plr, toFilter)) {
                    teamColor = plr.playerColor;
                } else {
                    return list;
                }
                return toFilter.Where(x => x.playerColor == teamColor).ToList();
            } else {
                if (TryGetPlayerFromPartialName(plrname, out var plr, toFilter)) {
                    list.Add(plr);
                    return list;
                }
            }
        }
        return list;
    }


    // used for console commands 
    public static bool TryGetPlayerFromPartialName(string partialname, out ScavPlayer player, List<ScavPlayer> toSearchIn = null) {
        if (toSearchIn == null) {
            toSearchIn = ScavPlayer.ClientIdToPlayerDict.Values.ToList();
        }

        if (partialname.StartsWith("id:")) {
            var supposed_id = partialname.Substring(3);
            if (ushort.TryParse(supposed_id, out var id)) {
                if (ScavPlayer.TryGetPlayerFromClientId(id, out player) && toSearchIn.Contains(player)) {
                    return true;
                }
            }
        }
        //if (partialname.StartsWith("7656119"))
        if (partialname.StartsWith("765611")) {
            if (ulong.TryParse(partialname, out var steamId)) {
                player = toSearchIn.FirstOrDefault(x => x.SteamId == steamId);
                if (player != null) {
                    return true;
                }
            }
        }


        foreach (var plr in toSearchIn) {
            if (plr.playerName == partialname) {
                player = plr;
                return true;
            }
        }
        // run it again but check the name more relaxed
        foreach (var plr in toSearchIn) {
            if (plr.playerName.StartsWith(partialname, StringComparison.OrdinalIgnoreCase)) {
                player = plr;
                return true;
            }
        }
        player = null;
        return false;
    }


    public static NetBody GetBodyForCommandOnCursor(Vector2 cursorpos, bool onlyPlayers = false) {

        var body = Util.GetBodyOnPos(cursorpos);
        NetBody person = null;
        if (body != null) {
            body.TryGetNetBody(out person);
        }
        var nearestperson = NetBody.GetNearestBody(cursorpos, true, false);

        if (person == null) {
            person = nearestperson.Item1;
        }

        if (person != null) {
            if (onlyPlayers && !(person.IsPlayer)) {
                return null;
            }
            return person;
        } else {
            return null;
        }
    }

    // this doesnt have @a 
    public static NetBody GetBodyForCommand(string name, ScavPlayer caller) {

        if (string.IsNullOrEmpty(name)) {
            return null;
        }
        var lowered_plrname = name.ToLower();
        var bodyId = lowered_plrname.TrimStart("body:");

        var players = GetPlayersForCommand(name, caller, true, true, ScavPlayer.BodyToPlayerDict.Values.ToList());
        if (players != null && players.Count > 0) {
            return players[0].netBody;
        }

        {
            if (ushort.TryParse(bodyId, out var bodyNetId) && NetBody.TryGetNetBodyFromId(bodyNetId, out var nb)) {
                return nb;
            }
        }

        return null;
    }

    // idk what i was trying to do here
    public static Tuple<bool, string> PerformActionOnPlayersByName(string plrname, Action<ScavPlayer> func, bool allow_macros, ScavPlayer callerPlr, bool require_body, bool onlyOne) {

        if (string.IsNullOrEmpty(plrname)) {
            return Tuple.Create(false, "Empty name");
        }

        var players = GetPlayersForCommand(plrname, callerPlr, onlyOne, allow_macros, (require_body ? ScavPlayer.BodyToPlayerDict.Values.ToList() : ScavPlayer.ClientIdToPlayerDict.Values.ToList()));


        if (players.Count > 0) {
            foreach (var item in players) {
                func(item);
            }
            return Tuple.Create(true, "Success");
        }
        return Tuple.Create(false, "Player not found");
    }

    public static Tuple<bool, string> PerformActionOnBodiesByName(string plrname, Action<NetBody> func, bool allow_macros, bool only_players, ScavPlayer callerPlr, bool onlyOne) {

        if (string.IsNullOrEmpty(plrname)) {
            return Tuple.Create(false, "Empty name");
        }
        var lowered_plrname = plrname.ToLower();
        var bodyId = lowered_plrname.TrimStart("body:");

        var players = GetPlayersForCommand(plrname, callerPlr, onlyOne, allow_macros, ScavPlayer.BodyToPlayerDict.Values.ToList());


        if (players.Count > 0) {
            foreach (var item in players) {
                func(item.netBody);
            }
            return Tuple.Create(true, "Success");
        }
        {
            if (ushort.TryParse(bodyId, out var bodyNetId) && NetBody.TryGetNetBodyFromId(bodyNetId, out var nb)) {
                func(nb);
                return Tuple.Create(true, "Found body");
            }
        }
        if ((bodyId == "@m" || bodyId == "@r")  && Util.TryGetLocalBody(out var b) && b.TryGetNetBody(out var netb)) {
            func(netb);
            return Tuple.Create(true, "Success");
        }
        return Tuple.Create(false, "Body not found");
    }


    public static SyncInfo GetSyncInfoForCommand(string objname) {
        var obj = GetObjectForCommand(objname, true);
        if (obj == null) { return null; }   
        return NetObjectRegistry.SyncRegistry[obj];
    }
    public static GameObject GetObjectForCommand(string objname, bool require_syncinfo, Func<GameObject,bool> condition = null) {
        if (condition == null) condition = (x) => { return true; };
        GameObject go = null;
        objname = objname.Trim().ToLower();
        if (!string.IsNullOrEmpty(objname)) {
            var split = objname.Split(':');
            if (split[0] == "syncid") {
                split = split.Skip(1).ToArray();
            }
            var firststring = split[0];
            if (ushort.TryParse(firststring, out var syncid)) {
                if (NetObjectRegistry.TryGetSyncInfo(syncid, out var si)) {
                    go = si.go;
                }
            } else {
                if (firststring == "@r" || firststring == "random") {
                    var array = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive: true).Where(x => x.IsItemOrBuilding());
                    if (require_syncinfo) {
                        array = array.Where(x => NetObjectRegistry.ObjectShouldBeSynced(x) && condition(x));
                    }
                    if (array.Count() > 0) {
                        go = array.PickRandom();
                        if (require_syncinfo) {
                            NetObjectRegistry.TryGetSyncInfoOrRegister(go, out var si);
                        }
                    }
                } else if (firststring == "@c" || firststring == "cursor") {
                    var cursorpos = GetCommandCallerCursorPos();

                    var collider2Darray = Physics2D.OverlapPointAll(cursorpos);
                    for (int i = 0; i < collider2Darray.Length; i++) {
                        var gameObject = collider2Darray[i].gameObject;
                        if (gameObject.IsItemOrBuilding() && condition(gameObject) && (!require_syncinfo || NetObjectRegistry.TryGetSyncInfoOrRegister(gameObject, out var si))) {
                            go = gameObject;
                            break;
                        }
                    }
                }
                //TODO object by resource id
                //TODO object by position
            }
        }

        return go;
    }
    public static GameObject GetObjectOrBodyForCommand(string objname) {
        var go = GetObjectForCommand(objname, false); 
        if (go == null) {
            var nb = GetBodyForCommand(objname, GetCommandCallerPlayer());
            if (nb == null && TryGetTaggedPlayerName(objname, out var plrname)) {
                nb = GetBodyForCommand(plrname, GetCommandCallerPlayer());
            }
            if (nb != null) {
                go = nb.gameObject;
            }
        }

        return go;
    }













    #endregion // CONSOLE_ONFRAME




#if !RELEASE
    public static IEnumerator ЗаспавниНахуйВсёШтобТеститьНахуй() {
        Plugin.log.LogInfo("Spawning testing pack of items...");

#if DEBUG
        { // give shit to ppl
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(0, out var plr) && plr.body is Body body && body != null) {

                    yield return null;


                    var d = SpawnThingOnPlayer("duffelbag", body, true);
                    var b = SpawnThingOnPlayer("bikehelmet", body, true);

                    var mdkit = SpawnThingOnPlayer("medkit", body, true);
                    SpawnThingOnPlayer("riflemagazine", body, true, mdkit);
                    SpawnThingOnPlayer("chocolatebar", body, true, mdkit);

                    SpawnThingOnPlayer("rifle", body, true);

                    SpawnThingOnPlayer("alcohol", body, true);
                    SpawnThingOnPlayer("glowplantfruit", body, true);

                    SpawnThingOnPlayer("mushtail", body, true, d);
                    SpawnThingOnPlayer("splint", body, true, d);
                    SpawnThingOnPlayer("bandage", body, true, d);
                    SpawnThingOnPlayer("heroin", body, true, d);
                    SpawnThingOnPlayer("foliage", body, true, d);
                    SpawnThingOnPlayer("flashlight", body, true, d);
                }
            }
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(1, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    SpawnThingOnPlayer("pistol", body, true);
                    SpawnThingOnPlayer("flashlight", body, true);

                    var tlbx = SpawnThingOnPlayer("toolbox", body, true);
                    SpawnThingOnPlayer("fentanyl", body, true);

                    SpawnThingOnPlayer("balaclava", body, true);

                    SpawnThingOnPlayer("dynamite", body, true);
                    SpawnThingOnPlayer("banana", body, true, tlbx);

                    SpawnThingOnPlayer("kneepads", body, true);


                    SpawnThingOnPlayer("heroin", body, true, tlbx);
                    SpawnThingOnPlayer("bandage", body, true, tlbx);
                    SpawnThingOnPlayer("bruisekit", body, true, tlbx);
                    SpawnThingOnPlayer("mushtail", body, true, tlbx);
                    SpawnThingOnPlayer("foliage", body, true, tlbx);
                }
            }
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(2, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    SpawnThingOnPlayer("machete", body, true);
                    SpawnThingOnPlayer("lightbulb", body, true);
                    SpawnThingOnPlayer("pizzaslice", body, true);
                    var p = SpawnThingOnPlayer("pouch", body, true);
                    SpawnThingOnPlayer("rag", body, true);
                    SpawnThingOnPlayer("sodacan", body, true);
                    SpawnThingOnPlayer("tornshirt", body, true);


                    SpawnThingOnPlayer("geigefruit", body, true, p);
                    SpawnThingOnPlayer("geofruit", body, true, p);
                    SpawnThingOnPlayer("foliage", body, true, p);

                }
            }
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(3, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    SpawnThingOnPlayer("shotgun", body, true);
                    SpawnThingOnPlayer("milk", body, true);
                    SpawnThingOnPlayer("12gauge", body, true);
                    SpawnThingOnPlayer("pickaxe", body, true);
                    SpawnThingOnPlayer("hoodie", body, true);

                    SpawnThingOnPlayer("headlamp", body, true);

                    var lp = SpawnThingOnPlayer("legpouch", body, true);
                    SpawnThingOnPlayer("dryfoliage", body, true, lp);
                    SpawnThingOnPlayer("naltrexone", body, true, lp);
                    SpawnThingOnPlayer("opium", body, true, lp);


                }
            }

        }
#endif

        Utils.Create("soundcannon", new Vector2(77, -127), 0);
        Utils.Create("trader1", new Vector2(20, -52), 0);
        Utils.Create("trader2", new Vector2(25, -52), 0);
        Utils.Create("trader3", new Vector2(30, -52), 0);

        Utils.Create("rechargingstation", new Vector2(42, -50), 0);
        Utils.Create("smallbattery", new Vector2(44, -50), 0).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("mediumbattery", new Vector2(44, -51), 0).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("largebattery", new Vector2(44, -51.5f), 0).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("smallbattery", new Vector2(45, -50), 0).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("mediumbattery", new Vector2(45, -51), 0).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("largebattery", new Vector2(45, -51.5f), 0).GetComponent<Item>().SetCondition(0.5f);

        Utils.Create("skullcrusher", new Vector2(45, -40), 0);

        Utils.Create("turret", new Vector2(20, -40), 0);

        Utils.Create("shadecrawler", new Vector2(96, -59), 0);

        Utils.Create("overgrowntick", new Vector2(100, -73), 0);

        Utils.Create("CaveTicks", new Vector2(113, -73), 0);





        SpawnThingOnPlayer("plasticbag");
        yield return new WaitForSeconds(1.0f);
        for (int j = 0; j < 3; j++) {
            SpawnThingOnPlayer(ItemLootPool.pool["food"].PickRandom());
            SpawnThingOnPlayer(ItemLootPool.pool["water"].PickRandom());
            SpawnThingOnPlayer(ItemLootPool.pool["medical"].PickRandom());
        }
        yield return new WaitForSeconds(0.1f);
        for (int j = 0; j < 2; j++) {
            SpawnThingOnPlayer("duffelbag");
            SpawnThingOnPlayer("bikehelmet");
        }
        yield return new WaitForSeconds(0.1f);
        for (int j = 0; j < 5; j++) {
            SpawnThingOnPlayer("dynamite");
        }
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("toolbox");
        SpawnThingOnPlayer("medkit");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("rifle");
        SpawnThingOnPlayer("riflemagazine");
        SpawnThingOnPlayer("riflemagazine");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("shotgun");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("pistol");
        SpawnThingOnPlayer("smallmagazine");

        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("fentanyl");
        SpawnThingOnPlayer("fentanyl");
        SpawnThingOnPlayer("fentanyl");


        yield return new WaitForSeconds(0.1f);
#if !DEBUG
        {
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(0, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    var b = SpawnThingOnPlayer("bikehelmet", body, true);

                    SpawnThingOnPlayer("pistol", body, true);
                    SpawnThingOnPlayer("shotgun", body, true);
                    SpawnThingOnPlayer("shotgun", body, true);

                }
            }
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(1, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    SpawnThingOnPlayer("rifle", body, true);
                    SpawnThingOnPlayer("dynamite", body, true);
                    SpawnThingOnPlayer("banana", body, true);
                    SpawnThingOnPlayer("bandage", body, true);

                    SpawnThingOnPlayer("shotgun", body, true);
                    SpawnThingOnPlayer("shotgun", body, true);

                }
            }
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(2, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    SpawnThingOnPlayer("machete", body, true);
                    SpawnThingOnPlayer("lightbulb", body, true);
                    SpawnThingOnPlayer("pizzaslice", body, true);

                }
            }
            {
                if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(3, out var plr) && plr.body is Body body && body != null) {

                    yield return null;

                    SpawnThingOnPlayer("milk", body, true);
                    SpawnThingOnPlayer("shotgun", body, true);
                    SpawnThingOnPlayer("12gauge", body, true);
                    SpawnThingOnPlayer("pickaxe", body, true);


                    var lp = SpawnThingOnPlayer("legpouch", body, true);
                    SpawnThingOnPlayer("dryfoliage", body, true, lp);


                }
            }

            {
                foreach (var body in ScavPlayer.BodyToPlayerDict.Keys) {

                    SpawnThingOnPlayer("machete", body, true);
                }
            }
        }
#endif
        yield return new WaitForSeconds(0.1f);

        SpawnThingOnPlayer("12gauge");
        SpawnThingOnPlayer("12gauge");
        SpawnThingOnPlayer("12gauge");
        SpawnThingOnPlayer("12gauge");
        SpawnThingOnPlayer("12gauge");
        SpawnThingOnPlayer("12gauge");
        SpawnThingOnPlayer("12gauge");



        yield return new WaitForSeconds(1.0f);
        // flashlight rot    270  looks to the right 
        // flashlight rot    90  looks to the left 
        // flashlight rot    0  looks up 
        // flashlight rot    180  looks down 


        Utils.Create("flashlight", new Vector2(69, 45), 270).GetComponent<CustomItemBehaviour>().state = 1;
        Utils.Create("flashlight", new Vector2(147, 45), 90).GetComponent<CustomItemBehaviour>().state = 1;


        SpawnThingOnPlayer("machete").transform.position = new Vector2(76, 44);
        SpawnThingOnPlayer("wrench").transform.position = new Vector2(77.5f, 44);
        SpawnThingOnPlayer("sledgehammer").transform.position = new Vector2(79, 44);
        SpawnThingOnPlayer("rifle").transform.position = new Vector2(83, 44);
        SpawnThingOnPlayer("tweezers").transform.position = new Vector2(84.5f, 44);
        SpawnThingOnPlayer("saline").transform.position = new Vector2(84.5f, 46);
        SpawnThingOnPlayer("tourniquet").transform.position = new Vector2(78.1f, 44);
        SpawnThingOnPlayer("medicalsuture").transform.position = new Vector2(84.5f, 46);


        Utils.Create("flashlight", new Vector2(-43, 66), 270).GetComponent<CustomItemBehaviour>().state = 1;
        Utils.Create("flashlight", new Vector2(0, 53), 0).GetComponent<CustomItemBehaviour>().state = 1;
        Utils.Create("flashlight", new Vector2(48, 66), 90).GetComponent<CustomItemBehaviour>().state = 1;

        Utils.Create("dropcapsule", new Vector2(20, 70), 0);
        Utils.Create("lifepodchest", new Vector2(24.5f, 70), 0);
        Utils.Create("lifepodchest", new Vector2(29, 70), 0);

        for (int i = 555; i < 567; i++) {
            WorldGeneration.world.SetBlock(new Vector2Int(579, i), 20);
            WorldGeneration.world.SetBlock(new Vector2Int(661, i), 20);
        }

        for (int i = 577; i < 590; i++) {
            WorldGeneration.world.SetBlock(new Vector2Int(467, i), 20);
            WorldGeneration.world.SetBlock(new Vector2Int(562, i), 20);
        }

        var pppp = WorldGeneration.world.WorldToBlockPos(new Vector2(-390, 0));
        for (int x = pppp.x; x < pppp.x + 50; x++) {
            for (int y = pppp.y; y < pppp.y + 50; y++) {
                FluidManager.main.fluid[x, y] = 1;
            }
        }

    }

    public static string KrokoshaLegacyCommand(string inputtext, List<string> args) {
        var splited = args;

        string helptext = "Krokosha Scav Multiplayer mod commands (HOST ONLY) (DEPRECATED): \n";
        helptext += "krok maxplayers [number] // Set player count limit.\n";
        helptext += "krok rule // Set multiplayer game rules\n";
        helptext += "krok rules // List multiplayer game rules\n";
        //helptext += "// ( cheats, plr macros:  @a = all, @m = me, @c = cursor)\n";
        helptext += "krok CHEATS // enable all cheat commands\n";
#if TESTING
        helptext += "// (now dev-only, break the game)\n";
#endif
#if !RELEASE
        helptext += "krok vomit [player] // vomit\n";
        helptext += "krok screenshotmode\n";
        helptext += "krok freeze // freezes monsters\n";
#endif
#if TESTING
        helptext += "krok syncinfo // more debug info\n";
        helptext += "krok syncevent // visualise events tied to a pos\n";
        helptext += "krok verbose // verbose\n";
        helptext += "krok cleanworld // despawn monsters and useless items\n";
        helptext += "krok objreset // unregister all objects, to test sync again\n";
        helptext += "krok sync // Toggle specific synchronization systems (DEV ONLY):\n" +
#endif
#if !RELEASE
                        "\tkrok sync tile // World tiles sync.\n" +
                        "\tkrok sync fluid // World fluids sync.\n" +
                        "\tkrok sync health // Player health sync.\n";
#endif




        if (args.Count() < 2) {
            return helptext;
        }


        if (args[1].ToLower() == "cheats") {
            if (Net.IsClient && Net.IsRunning) {
                return "server-only";
            }
            var tell_goahead = !CanCheat();


            var target = !Multiplayer.rules.sv_cheats;
            if (args.Count > 2) {
                target = ParseBool01(args[2]);
            }

            Multiplayer.rules.sv_cheats = target;
            Multiplayer.rules.AllowClientCheatCommands = target;
            Multiplayer.ApplyGameRules();

            if (Multiplayer.rules.sv_cheats) {

                if (tell_goahead) {
                    return "Cheats enabled.";
                }
                return "no";
            } else {
                return "Cheats disabled.";
            }
        }


        switch (args[1]) {

            case "objreset": { // reset registered objects

                NetObjectRegistry.UnregisterAllObjectsButKeepInventory();

                return $"unregistered all registered objects ";
            }
            case "syncinfo": {

                if (args.Count() < 3) {
                    NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = !NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING;
                } else {
                    NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = ParseBool01(args[3]);
                }
                return $"syncinfo: {NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING} ";
            }
            case "verbose": {

                if (args.Count() < 3) {
                    DebugMenuSettings.is_verbose = !Multiplayer.verbose;
                } else {
                    DebugMenuSettings.is_verbose = ParseBool01(args[3]);
                }
                return $"verbose: {Multiplayer.verbose} ";
            }

#if TESTING
            case "cleanworld": {
                ConFailIfCheatsDisabled();

                // remove bleed and vomit particels
                foreach (var item in GameObject.FindObjectsOfType<_BleedParticleStartTrackerComponent>()) {
                    GameObject.Destroy(item.gameObject);
                }
                // remove block damage particles
                foreach (var item in WorldGeneration.world.blockDamages) {
                    item.damage = 99999999999;
                    item.UpdateSprite();
                }
                WorldGeneration.world.blockDamages.Clear();

                // delete all monsters
                foreach (var item in GameObject.FindObjectsOfType<SpiderHandler>()) {
                    GameObject.Destroy(item.gameObject);
                }
                // delete all trash items
                foreach (var item in GameObject.FindObjectsOfType<Item>()) {
                    if (NetObjectRegistry.lowpriority_objects_resourceids.Contains(item.id)) {
                        GameObject.Destroy(item.gameObject);
                    }
                }
                // remove explosion blast marks
                foreach (var item in GameObject.FindObjectsOfType<SpriteRenderer>()) {
                    if (item.gameObject.name.Contains("blastmark") && item.sortingOrder == -9050) {
                        GameObject.Destroy(item.gameObject);
                    }
                }


                return "cleanworld";
            }
#endif
#if DEBUG || RELEASETEST


            case "freeze": {


                // freeze all monsters ( for my elder thornback screenshot)
                foreach (var item in GameObject.FindObjectsOfType<SpiderHandler>()) {
                    if (item.TryGetComponent<Rigidbody2D>(out var rb)) {
                        rb.bodyType = RigidbodyType2D.Static;
                    }
                }


                return "freezed monsters";
            }

#endif
            case "help": {
                return helptext;
            }
            case "maxplayers": {
                ConFailIfNetworkAlreadyRunning();
                ConFailIfNetworkIsRunningAndIsClient();


                if (splited.Count() < 3) {
                    return $"expected a number ";
                } else {
                    Multiplayer.PLAYER_COUNT_LIMIT = (byte)Mathf.Clamp((ushort.Parse(splited[2])), 0, 251);
                    return $"Set player count limit to: {Multiplayer.PLAYER_COUNT_LIMIT} ";
                }

            }
            case "rules": {
                var rules = "Krokosha Co-op mod game rules:\n";
                foreach (var fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    rules += $"krok rule {fld.Name} [{fld.FieldType.Name}] // current: {fld.GetValue(Multiplayer.rules)}\n";
                }
                return rules;
            }
            case "rule": {

                if (Net.IsRunning && Net.IsClient) {
                    return $"server-only ";
                } else if (args.Count() < 4) {
                    return $"expected a rule name and value ";
                } else {
                    ConFailIfNetworkIsRunningAndIsClient();
                    var rulename = args[2];
                    var rulevalstr = args[3];
                    foreach (var fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                        if (fld.Name == rulename) {
                            try {
                                object typedValue;
                                if (fld.FieldType == typeof(bool)) {
                                    typedValue = ParseBool01(rulevalstr);
                                } else {
                                    typedValue = Convert.ChangeType(rulevalstr, fld.FieldType, CultureInfo.InvariantCulture);
                                }
                                TypedReference rulesref = __makeref(Multiplayer.rules);
                                fld.SetValueDirect(rulesref, typedValue);
                                //fld.SetValue(KrokoshaScavMultiplayerComponent.rules, typedValue);
                                Multiplayer.ApplyGameRules();

                                return ($"Succesfully set rule '{fld.Name}' to '{fld.GetValue(Multiplayer.rules)}'.");
                            } catch (Exception ex) {
                                return ($"Failed to set rule '{fld.Name}': {ex.Message}");
                            }
                        }
                    }
                    return ($"Rule '{rulename}' does not exist!");
                }
            }

            default:
            break;
        }


        return "THIS IS DEPRECATED";



    }
#endif


}







