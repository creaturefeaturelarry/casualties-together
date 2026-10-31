


using BepInEx;
using BepInEx.Logging;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Together;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using KrokV5Optimization;
using System.Net;
using LiteNetLib;



namespace Together {


    [BepInProcess("CasualtiesUnknown.exe")]
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin {
        internal static ManualLogSource log;
        internal static new ManualLogSource Logger => log;
        internal const string FORCEDISABLE_PLAYERPREF_KEY = "CasualtiesMP_FORCE_DISABLE_MP_MOD";
        public static Plugin main { get; private set; }


        internal Harmony harmony;


        public static readonly string TARGET_GAME_VERSION = "7.0.1";
        public static readonly string MOD_VERSION = BuildInfo.MOD_VERSION;
        public static string FULL_MOD_VERSION { get; internal set; }

        Plugin() {
            FULL_MOD_VERSION = BuildInfo.MOD_VERSION;
        }

#if DEBUG
        public readonly static bool IS_DEBUG_BUILD = true;
#else
        public readonly static bool IS_DEBUG_BUILD = false;
#endif
#if TESTING
        public readonly static bool IS_TESTING_BUILD = true;
#else
        public readonly static bool IS_TESTING_BUILD = false;
#endif
#if RELEASE
        public readonly static bool IS_RELEASE_BUILD = true;
#else
        public readonly static bool IS_RELEASE_BUILD = false;
#endif
#if FULLGAME
        public readonly static bool IS_FULLGAME_BUILD = true;
#else
        public readonly static bool IS_FULLGAME_BUILD = false;
#endif


        public static GameObject s;

        public readonly static bool OG_RUNINGACKGROUND = false;


        internal class LoadErrorNotifier : MonoBehaviour {
            public static string errorstr = "";
            void Start() {
                KrokoshaCasualtiesMP_DISABLED_STATE.KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER.ON_CLICK_WILL_ENABLE_THE_MOD = false;
                this.GetOrAddComponent<KrokoshaCasualtiesMP_DISABLED_STATE.KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER>();
            }
            void OnGUI() {
                if (GameObject.FindObjectOfType<Body>()) {
                    return;
                }
                GUI.Label(new Rect(20, 20, Screen.width, Screen.height), $"CO-OP MOD v{Plugin.FULL_MOD_VERSION} failed to load!!!!!! \n\n{errorstr}");
            }
        }

        public static bool IsExeSameAsInSteam() {
            if (!KSteam.Loaded) return false;

            var installdir = KSteam.GetAppInstallDir();

            if (installdir.IsNullOrWhiteSpace()) {
                return false;
            }

            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            var contains = exePath.Contains(installdir);

            return contains;
        }

        static bool TryStartFromSteam() {
            if (!KSteam.Loaded) return false;

            if (KSteam.STEAM_APPID == KSteam.CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO
                || KSteam.STEAM_APPID == KSteam.CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST
            ) {
                if (!IsExeSameAsInSteam()) {
                    return false;
                }

                Application.Quit();
                Process.Start(new ProcessStartInfo {
                    FileName = $"steam://run/{KSteam.STEAM_APPID}",
                    UseShellExecute = true
                });
                return true;
            }
            return false;
        }

        public static void RestartGame() {

            try {
#if !DONT_RESTART_IF_RESTART_BULLSHIT
                try {
                    if (TryStartFromSteam()) {
                        Together.log.l("Relaunching the game through steam!");
                        return;
                    }
                } catch (System.Exception e) {
                    Together.log.error("STEAM RESTART FAILED " + e.ToString());
                }

                //vvvvvvvvvv THIS SHIT DOESNT WORK CUZ BEPINEX DOESNT INJECT AFTER RESTART !! vvvvvvvvvvvvvv

                //// and try launch through the exe
                //try {
                //    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                //    Process.Start(new ProcessStartInfo {
                //        FileName = exePath,
                //        UseShellExecute = false,
                //        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                //    });
                //    KrokoshaCasualtiesMP.log.l("Relaunching " + exePath);
                //    Application.Quit();
                //    return;
                //} catch (System.Exception e2) {
                //    KrokoshaCasualtiesMP.log.error("GENERIC RESTART FAILED:" + e2.ToString());
                //}
#endif
            } catch (System.Exception e3) {
                UnityEngine.Debug.LogError("GAME RESTART FAILED:" + e3.ToString());
            }

            Application.Quit();
        }


        private void Awake() {

            main = this;
            s = this.gameObject;

            SUCCESFULLY_INITIALIZED = false;

#if !RELEASE 
            Application.runInBackground = true;
            DebugMenuSettings.is_verbose = true;
            DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = true;
            Chat._DEV_LOG_CHAT = true;
#endif 


            // to prevent it from being deleted by unity, same as the  HideManagerGameObject in config, but i do it forcefully
            gameObject.hideFlags = HideFlags.HideAndDontSave;


            // Plugin startup logic
            log = base.Logger;
            //log = BepInEx.Logging.Logger.CreateLogSource("Krokosha_MP_CU");
            // Multiplayer Co-op Mod for Casualties: Unknown





            try {
                Awake_Stage1(); // that later calls stage 2
            } catch (Exception e) {
                log.LogError("Awake_Stage1: " + e.ToString());
            }
            OnInitFinish?.Invoke();
        }

        public static event Action OnInitFinish;


        void Awake_Stage1() {

            try {
                FORCE_DISABLE_MP_MOD = PlayerPrefsExtended.GetBool(FORCEDISABLE_PLAYERPREF_KEY, FORCE_DISABLE_MP_MOD);
            } catch (Exception e) {
                log.LogError("PlayerPrefs: FORCE_DISABLE_MP_MOD: LOADING: " + e.ToString());
            }
            CheckCommandlineArgsForThisMod();

            try {
                PlayerPrefsExtended.SetBool(FORCEDISABLE_PLAYERPREF_KEY, FORCE_DISABLE_MP_MOD);
                PlayerPrefs.Save();
                PlayerPrefsExtended.SetBool("KrokoshaCasualtiesMP_FORCE_DISABLE_MP_MOD", true); 
            } catch (Exception e) {
                log.LogError("PlayerPrefs: FORCE_DISABLE_MP_MOD: SAVING: " + e.ToString());
            }

            try {
                Lang.InternalLoadLocale();
            } catch (Exception e) {
                log.LogError("Lang.InternalLoadLocale FATAL: " + e.ToString());
            }

            if (FORCE_DISABLE_MP_MOD) {
                log.LogInfo("Multiplayer Mod Self-Destruct activated.");
                try {
                    this.AddComponent<KrokoshaCasualtiesMP_DISABLED_STATE.KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER>();
                } catch (Exception e) {
                    log.LogError("MP-SWITCH BUTTON CREATION: " + e.ToString());
                }
                try {
                    Destroy(this);
                } catch (Exception e) {
                    log.LogError("FORCE_DISABLE_MP_MOD: !!! KILLING MYSELF FAILED !!!: " + e.ToString());
                }
                return;
            }

            Awake_Stage2();
        }




        void _PickUsername() {
            try {
                if (KSteam.Loaded) {
                    UIMainMenu.USERINPUT_NAME = KSteam.GetLocalUsername();
                    return;
                }
            } catch (Exception e) {
                log.LogError("_PickName: " + e.ToString());
            }
            UIMainMenu.USERINPUT_NAME = "Player" + UnityEngine.Random.Range(0, 9999).ToString();

        }




        void Awake_Stage2() {

#if PLAYTEST
            DebugMenuSettings.is_verbose = true;
            DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = true;
            Chat._DEV_LOG_CHAT = true;
#endif



            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            OptimizerMain.Log = Logger;

            if (Application.version != TARGET_GAME_VERSION) {
                LoadErrorNotifier.errorstr += $"Mismatching version detected: TARGET: {TARGET_GAME_VERSION} - CURRENT: {Application.version}";

                Together.log.error(LoadErrorNotifier.errorstr);

                LoadErrorNotifier.errorstr += "\n";
            }

            try {
                DoHarmonyPatchAll(harmony);
            } catch (Exception e) {
                Together.log.error($"Plugin {MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load! Make sure you have correct version!\n\n{e}\n");
                LoadErrorNotifier.errorstr += "\n" + e.ToString();

                if (BepInEx.Paths.ExecutablePath.Any(c => c > 127)) {
                    LoadErrorNotifier.errorstr += $"\n\n{Lang.Get("loaderror_possible_nonascii_path")}\n{BepInEx.Paths.ExecutablePath}\n";
                }

                this.AddComponent<LoadErrorNotifier>();
                Application.runInBackground = false;

                return;
            }
            Application.runInBackground = true;

            log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} version {Plugin.FULL_MOD_VERSION} is loaded!");
            log.LogInfo($"Game version: \"{Application.version}\"");





            KSteam.CheckSteam();

            UIMainMenu._USE_STEAM_MENU = KSteam.Loaded;

            if (!plrname_override) {
                _PickUsername();
            }

#if !RELEASE
            Multiplayer.INPUT_PASSWORD = "AMOGUS";
#elif PLAYTEST
            //Multiplayer.INPUT_PASSWORD = "PLAYTEST";
#endif


            try {
                CoopModAssets.LoadAssets();
            } catch (Exception e) {
                Together.log.error($"{MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load ASSETS!!!\n\n{e}\n");
                LoadErrorNotifier.errorstr += "\n" + e.ToString();

                this.AddComponent<LoadErrorNotifier>();
                return;
            }

            log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} assets loaded!");

            // MULTIPLAYER
            // also create my global object that will track everything

            var mainsingleton = this.AddComponent<Multiplayer>();


            try {
                Multiplayer._FIRST_INIT();
                log.LogInfo($"Created multiplayer singleton GameObject!");
            } catch (Exception e) {

                Together.log.error($"{MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load!\n\n{e}\n");
                LoadErrorNotifier.errorstr += "\n" + e.ToString();


                this.AddComponent<LoadErrorNotifier>();
                Component.Destroy(mainsingleton);
                return;
            }

            SUCCESFULLY_INITIALIZED = true;

            GlobalDark_Awake_MultiplayerPatch.SetBetaBuildMPText();


            try {
                OptimizerMain.Install();
            } catch (Exception e) {
                log.LogError(e);
            }


            base.StartCoroutine(DelayedDelayedTestGameStartImmidiate());
        }


        void DoHarmonyPatchAll(Harmony harmony) {
            var patchTypes = GetHarmonyPatchesInNamespaces("Together", "CasualtiesMultiplayerAPI", "CasualtiesTogetherUtils");
            //harmony.PatchAll(); // i can do later: harmony.UnpatchSelf();

            foreach (var patch in patchTypes) {
                try {
                    harmony.PatchAll(patch);
                } catch (Exception ex) {
                    log.LogError($"Failed to patch: {patch.Name}: {ex.Message}");
                }
            }

            ExtraPatches.ApplyPatches(harmony);
        }
        public static List<Type> GetHarmonyPatchesInNamespaces(params string[] namespaces) {
                return Assembly.GetExecutingAssembly()
                    .GetTypesSafely()
                    .Where(t =>
                        t.Namespace != null &&
                        namespaces.Any(ns =>
                            t.Namespace == ns ||
                            t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)) &&
                        t.IsDefined(typeof(HarmonyPatch), false))
                    .ToList();
        }

        void MultiplayerUnpatch() {
            if (harmony != null) {
                try {
                    ExtraPatches.UnpatchExtraPatches(harmony);
                    harmony.UnpatchSelf();
                    harmony = null;
                } catch (Exception e) {
                    log.LogError(e);
                }

            }
            if (Multiplayer.Singletons != null) {
                foreach (var item in Multiplayer.Singletons) {
                    if (item != null) {
                        Component.Destroy(item);
                    }
                }
                Multiplayer.Singletons.Clear();
            } else {
                Multiplayer.Singletons = new();
            }
            if (this.TryGetComponent(out Multiplayer mp)) {
                Component.Destroy(mp);
            }
        }



        public static void StartCoroutine(IEnumerator f) {
            s.GetComponent<Multiplayer>().StartCoroutine(f);
        }

        private void Start() {
            Invoke(nameof(DelayedCoroutineToSkipScavIntro), 0.5f);
            log.LogInfo($"Invoked main {MyPluginInfo.PLUGIN_GUID} function, 1s...");
        }

        private void OnDestroy() {
            try {
                OptimizerMain.Shutdown();
            } catch (Exception e) { }
            try {
                Net.ShutdownReset();
            } catch (Exception e) { }
            Together.log.l("Plugin.OnDestroy()");
        }

        public static void SkipMainMenuIntro() {
            try {
                Plugin.SkipWarningScreen();
                ScrollableText.ForceClose();
            } catch (Exception e) {
                Together.log.error(e);
            }
        }

        public static void SkipWarningScreen() {
            var o = GameObject.Find("Canvas/Warning");
            if (o != null && o.activeSelf) {
                Plugin.log.LogInfo("Removing warning screen! ");
                o.SetActive(false);
            }
        }

        private void DelayedCoroutineToSkipScavIntro() {
#if !RELEASE
            SkipWarningScreen();
#endif
        }

        public static bool SUCCESFULLY_INITIALIZED { get; private set; }
        public static bool FORCE_DISABLE_MP_MOD { get; private set; }
        public static bool FORCE_NO_STEAM { get; private set; }
        public static bool AnyCmdArgsPassed { get; private set; }

        public static List<string> commands_to_run = new();
        public static string test_scenario = null;
        public static bool dump_game_ids { get; private set; }
        public static bool start_network { get; private set; }
        public static bool do_immidiate_start { get; private set; }
        public static int startworld { get; private set; }
        public static bool plrname_override { get; private set; }
        public static bool rules_override { get; private set; }
        static string current_rule_to_set;

        public static bool cmd_start__steam_lobby = false;
        public static string cmd_start__steam_lobby_type = "public";
        public static bool cmd_start__is_client = false;
        public static bool cmd_start__is_dedicated_server = false;

#if !RELEASE
        public static bool starterkit { get; private set; }
        public static bool dowindowposthing { get; private set; }
#endif



        static void CheckCommandlineArgsForThisMod() {
#if TESTING
            log.LogInfo("Checking args...");
#endif

            startworld = 0;

            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++) {
                if (args[i].TryTrimStart("--ksmulti-", out var t)) {
                    args[i] = "--mp-" + t;
                }
            }

            dump_game_ids = false;

            Func<string, string, bool> gggg = (string arg, string thing) => {
                return arg == "--" + thing
                    || arg == "-" + thing
                    || arg == thing;
            };


            for (int i = 0; i < args.Length; i++) {
                var arg = args[i];


                //--mp-test amputate_and_regrow


                if (arg == ("--mp-disable") ||
                    gggg(arg, "nomp") ||
                    gggg(arg, "no-mp") ||
                    gggg(arg, "nomultiplayer") ||
                    gggg(arg, "no-multiplayer")
                ) {
                    FORCE_DISABLE_MP_MOD = true;
                    var msg = $"CMD: Force-Disable multiplayer";
                    log.LogInfo(msg);
                    try {
                        Util.CallLambdaWhen(() => { return Con.con != null; }, () => { Con.con.LogToConsole(msg); }, 1f);
                    } catch (Exception eeeee) {

                    }
                    return;
                }

                if (arg == ("--mp-starthost")) {
                    start_network = true;
                    Plugin.cmd_start__is_client = false;
                }
                if (arg == ("--mp-startclient")) {
                    start_network = true;
                    Plugin.cmd_start__is_client = true;
                }
                if (arg == ("--mp-startserver")) {
                    start_network = true;
                    Plugin.cmd_start__is_client = false;
                    Plugin.cmd_start__is_dedicated_server = true;

                    Multiplayer.rules.AutoContinue = true;
                }
                if (arg == ("--mp-sethost")) {
                    Plugin.cmd_start__is_client = false;
                    Plugin.cmd_start__is_dedicated_server = false;
                }
                if (arg == ("--mp-setclient")) {
                    Plugin.cmd_start__is_client = true;
                }
                if (arg == ("--mp-setserver")) {
                    Plugin.cmd_start__is_client = false;
                    Plugin.cmd_start__is_dedicated_server = true;

                    Multiplayer.rules.AutoContinue = true;
                }


                if (arg == ("--dump-game-ids")) {
                    dump_game_ids = true;
                }

                if (arg == ("--mp-startnetwork")) {
                    start_network = true;
                }
                if (arg == ("--mp-immidiate-start")) {
                    do_immidiate_start = true;
                }
                if (arg == ("--mp-setstarttutorial")) {
                    startworld = 1;
                }
                if (arg == ("--mp-setstartdebug")) {
                    startworld = 2;
                }
                if (arg == ("--mp-verbose")) {
                    DebugMenuSettings.is_verbose = true;
                    DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = true;
                }
#if !RELEASE
                if (arg == ("--mpdev-sort-windowpos")) {
                    dowindowposthing = true;
                }
                if (arg == ("--kmultidev-starterkit")) {
                    starterkit = true;
                }
#endif
#if TESTING
                if (arg == ("--kmultidev-setwindownamefromusername")) {
                    win32windowtitlechanger.SetTitle(Application.productName + UIMainMenu.USERINPUT_NAME);
                }
#endif
                if (arg == ("--mp-ip-port")) {
                    i++; if (i == args.Length) break; arg = args[i];

                    UIMainMenu.USERINPUT_IPPORT = arg;
                }
                if (arg == ("--mp-setname")) {
                    i++; if (i == args.Length) break; arg = args[i];

                    UIMainMenu.USERINPUT_NAME = Multiplayer.SanitizeTextInput(arg);
                    plrname_override = true;
                    log.LogInfo($"CMD: set name to: {UIMainMenu.USERINPUT_NAME}");
                }
                if (arg == ("--mp-setpass")) {
                    i++; if (i == args.Length) break; arg = args[i];

                    AnyCmdArgsPassed = true;
                    Multiplayer.INPUT_PASSWORD = arg;
                    log.LogInfo($"CMD: set password to: {Multiplayer.INPUT_PASSWORD}");
                }
                if (arg == ("--mp-setrule")) {
                    i++; if (i == args.Length) break; arg = args[i];
                    current_rule_to_set = arg;
                    i++; if (i == args.Length) break; arg = args[i];

                    rules_override = true;

                    AnyCmdArgsPassed = true;

#if !RELEASE
                    if (current_rule_to_set == "FriendlyFire") {
                        current_rule_to_set = "PVP";
                    }
#endif

                    var field2 = typeof(MultiplayerGameRules).GetField(current_rule_to_set);
                    if (field2 == null) {
                        log.LogError($"CMD: UNKNOWN RULE: {current_rule_to_set} (attempt to set it to: {arg})");
                        continue;
                    }
                    object obj4;

                    try {
                        if (field2.FieldType == typeof(bool)) {
                            obj4 = Con.ParseBool01(arg);
                        } else {
                            obj4 = System.ComponentModel.TypeDescriptor.GetConverter(field2.FieldType).ConvertFromInvariantString(arg);
                        }
                    } catch (Exception e) {
                        log.LogError($"CMD: SET RULE, BUT VALUE IS INVALID:\nINPUT: {current_rule_to_set} = {arg}\n{e.Message}");
                        continue;
                    }

                    TypedReference rulesref = __makeref(Multiplayer.rules);
                    field2.SetValueDirect(rulesref, obj4);

                    log.LogInfo($"CMD: set rule: {current_rule_to_set} = {field2.GetValue(Multiplayer.rules)}");

                    continue;
                }
                if (arg == ("--mp-test")) {
                    i++; if (i == args.Length) break; arg = args[i];

                    test_scenario = arg;
                }
                if (arg == ("--mp-runcommand") || arg == "--ksm-con") {
                    i++; if (i == args.Length) break; arg = args[i];
                    AnyCmdArgsPassed = true;
                    commands_to_run.Add(arg);
                }
                if (arg == ("--mp-servername") ) {
                    i++; if (i == args.Length) break; arg = args[i];
                    AnyCmdArgsPassed = true;
                    Net.MyServerInfo.name = arg;
                }
                if (arg == ("--mp-hoststeam")) {
                    cmd_start__steam_lobby = true;
                    start_network = true;

                    log.LogInfo($"CMD: Will start steam lobby");
                    i++; if (i == args.Length) break; arg = args[i];
                    cmd_start__steam_lobby_type = arg;

                    log.LogInfo($"CMD: Will start steam lobby of type:{arg}");


                }




                if (arg == "--mp-nosteam"
                    || gggg(arg, "nosteam")
                    || gggg(arg, "no-steam")
                ) {
                    FORCE_NO_STEAM = true;
                }


            }


            AnyCmdArgsPassed = AnyCmdArgsPassed || FORCE_NO_STEAM || start_network || startworld != 0 || do_immidiate_start || plrname_override;
            if (start_network) {
                Application.runInBackground = true;
                log.LogInfo($"CMD: Will start network.");
            }
            if (do_immidiate_start) {
                if (startworld != 0) {
                    log.LogInfo($"CMD: Will start immidiately in {startworld}.");
                } else {
                    log.LogInfo($"CMD: Will start immidiately.");
                }
            }
        }


        private IEnumerator DelayedDelayedTestGameStartImmidiate() {

            while (WorldGeneration.world == null && PreRunScript.instance == null) {
                yield return null;
            }

            ConsoleScript.CheckForConsole();
#if !RELEASE
            Con.RunCommand("musicvolume 0.011", true);
#endif


            log.LogInfo($"Found args... {start_network} {start_network} {Plugin.cmd_start__is_client} ");
#if !RELEASE 
            if (dowindowposthing) {
                if (Plugin.cmd_start__is_client) {
                    //Screen.SetResolution()
                    Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, new Vector2Int(Screen.width, 0));

                }
                
            }
            if (Together.log.verbose || true) {
                ScavPlayer.OnPlayerJoined += (plr) => {
                    
                    if (Net.IsServer && Net.Transport is TransportLiteNetLib lnl) {
                        var plrs = new HashSet<ScavPlayer>();
                        {
                            var peers = new List<NetPeer>();
                            lnl.NetManager.GetConnectedPeers(peers);
                            foreach (var peer in peers) {
                                try {
                                    if (peer.Tag != null && peer.Tag is TagPlayerInfo klpe && klpe.plr != null) {

                                        if (IPAddress.IsLoopback(peer.Address)) {
                                            //if (peer.Address.ToString() == Net.IP_LOCALHOST_IPv4) {
                                            plrs.Add(klpe.plr);
                                        }
                                    }
                                } catch (Exception e) {

                                }
                            }
                        }
                        plrs.Remove(ScavPlayer.LocalPlayer);
                        var queue = new Queue<ScavPlayer>(plrs);

                        var fixt = false;
                        var localPlayersCount = 1 + plrs.Count();

                        if (localPlayersCount == 0) {
                            Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, new Vector2Int(0, 0));
                            Screen.SetResolution(1060, 600, false);
                            return;
                        }

                        var gridX = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(localPlayersCount)));
                        var gridY = Mathf.Max(1, Mathf.CeilToInt((float)localPlayersCount / gridX)); 

                        var screenX = Mathf.Min(1060, Display.main.systemWidth / gridX);
                        var screenY = Mathf.Min(600, Display.main.systemHeight / gridY);

                        Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, new Vector2Int(0, 0));
                        Screen.SetResolution(screenX, screenY, false);

                        for (int y = 0; y < gridY; y++) {
                            for (int x = 0; x < gridX; x++) {
                                if (!fixt) {
                                    fixt = true;
                                    continue;
                                }
                                var plr2 = queue.Dequeue();

                                var writer = Net.CreateWriter(NetmsgId.CLIENT_SetWindowPos);
                                writer.Put(new Vector2Int(x * screenX, y * screenY));
                                writer.Put(new Vector2Int(screenX, screenY));
                                Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr2);
                            }
                        }
                    }
                    
                };
                
            }
#endif

            if (start_network) {
                Application.runInBackground = true;
                if (Plugin.cmd_start__steam_lobby) {
                    cmd_start__steam_lobby_type = cmd_start__steam_lobby_type.ToLower();
                    TransportSteamworks.OnWantToHostLobby(
                        Plugin.cmd_start__is_dedicated_server ? Net.NetType.DedicatedServer : Net.NetType.Host,
                        cmd_start__steam_lobby_type == "private" ? ELobbyType.k_ELobbyTypePrivate : (
                            cmd_start__steam_lobby_type == "friendsonly" ? ELobbyType.k_ELobbyTypeFriendsOnly :
                            ELobbyType.k_ELobbyTypePublic
                        )
                    );
                } else if (Plugin.cmd_start__is_client) {
                    while (!Net.IsRunning && Util.IsInMainMenu()) {
                        TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Client);
                        yield return new WaitForSeconds(0.1f);
                        while (Net.IsConnecting) {
                            yield return null;
                        }
                        if (Multiplayer.IsNetworkActiveAndIsServer() || Util.IsInWorld()) {// It's been cancelled
                            Together.log.l("Cancelled forced client connect.");
                            break;
                        }
                    }
                } else {
                    if (Plugin.cmd_start__is_dedicated_server) {
                        TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.DedicatedServer);
                    } else {
                        TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Host);
                    }
                    yield return new WaitForSeconds(1.0f);
                }
            } 

            ConsoleScript.CheckForConsole();
            foreach (var item in commands_to_run) {
                try {
                    Together.log.l($"CMD: Running command \"{item}\"");
                    ConsoleScript.instance.RunCommandString(item);
                } catch (Exception e) {
                    Together.log.error($"CMD: Error while running command \"{item}\"\n{e}");
                }
            }



            if (do_immidiate_start) {
                SkipWarningScreen();

                if (do_immidiate_start && !(start_network && Plugin.cmd_start__is_client)) {

                    var prs = PreRunScript.instance;
                    if (startworld != 0) {
                        if (startworld == 1) {
                            prs.StartTutorial();
                        } else
                        if (startworld == 2) {
                            WorldMP.LoadDebugWorld();
                        } else {
                            SceneManager.LoadScene("SampleScene");

                            Util.CallLambdaWhen(() => { return WorldGeneration.world != null; }, () => {
                                WorldGeneration.world.biomeOverride = startworld == 1 ? WorldGeneration.OverrideSceneType.Tutorial : (startworld == 0 ? WorldGeneration.OverrideSceneType.None : WorldGeneration.OverrideSceneType.Debug);
                            });

                            if (Net.IsServer)
                                ServerMain.Server_Announce_GAME_START();
                        }
                    } else {
                        prs.StartRun();
                    }

#if !RELEASE
                    if (starterkit) {
                        while (!Util.IsInWorld() || WorldGeneration.world.generatingWorld) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(1.0f);
                        yield return Con.ЗаспавниНахуйВсёШтобТеститьНахуй();
                    }
#endif
                }

            }


#if TESTING
            while (!Util.IsInWorld() || WorldGeneration.world.generatingWorld) { yield return new WaitForSeconds(0.1f); }
            PlayerCamera.main.body.forceWalk = false; // fuck tutoril world 
#endif


            if (test_scenario != null) {
                yield return DEV_RunTestScenario();
            }


        }
        static IEnumerator DEV_RunTestScenario() {

#if !RELEASE
            switch (test_scenario) {
                case "amputate_and_regrow": {
                    if (!Plugin.cmd_start__is_client) {

                        yield return new WaitForSecondsRealtime(0.1f);

                        Con.RunCommand($"krok tp @a -3.25 67.75");

                        yield return new WaitForSecondsRealtime(0.2f);
                        var body = ScavPlayer.GetBodyFromClientId(1);

                        body.limbs[3].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[6].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[12].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[9].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[0].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[2].Dismember();

                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[1].Dismember();

                        yield return new WaitForSecondsRealtime(1.0f);
                        body.ResetHealth();
                        body.shock = 0;
                        body.Stand(true);

                    } else {
                        var body = Util.GetLocalBody();

                        yield return new WaitForSecondsRealtime(1.9f);
                        while (body.limbs[0].dismembered) { yield return new WaitForSeconds(0.1f); }
                        body.ResetHealth();
                        body.shock = 0;
                        body.Stand(true);
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.Jump();

                    }

                    break;
                }


                case "jungle_perf": {
                    Con.RunCommand("volume 0.011", true);


                    if (!Plugin.cmd_start__is_client) {
                        //yield return new WaitForSecondsRealtime(1f);
                        Multiplayer.rules.ScatterPunishDistance = 1500;
                        Multiplayer.ApplyGameRules();

                        //WorldGeneration.world.baseLootRarity = 4;
                        WorldGeneration.world.trapRarityMultiplier = 4;
                        Con.RunCommand("skiplayer 4", true);

                        yield return new WaitForSecondsRealtime(3f);

                        while (!Util.IsInWorld() || WorldGeneration.world.generatingWorld) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(2.0f);


                        Con.RunCommand("give @a pistol shotgun mp3player shotgun legpouch bigpack paincream waterbottle foliage foliage foliage foliage bandage foliage tourniquet splint bandage experimentflesh rifle machete bikehelmet hoodie sneakers fentanyl shotgun riflemagazine dynamite", true);

                        yield return new WaitForSeconds(2.0f);

                        Con.RunCommand("godmode", true);

                        while (!WorldGeneration.world.generatingWorld) {
                            yield return new WaitForSeconds(5f);

                            if (ScavPlayer.BodyToPlayerDict.All(x => x.Key.moveDir == Vector2.zero)) {
                                Con.RunCommand($"tp {Mathf.Round(UnityEngine.Random.Range(-1f, 1f) * 500)},{Mathf.Round(UnityEngine.Random.Range(-1f, 1f) * 500)} @r");
                            }
                        }


                    } else {


                    }

                    break;
                }

                case "sleep_test": {


                    if (!Plugin.cmd_start__is_client) {
                        //yield return new WaitForSecondsRealtime(1f);
                        Multiplayer.rules.EnableSleep = true;
                        Multiplayer.ApplyGameRules();


                        yield return new WaitForSecondsRealtime(3f);

                        while (!Util.IsWorldGenerated()) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(2.0f);

                        Con.RunCommand("setbodyfield energy 20 @a", true);
                        foreach (var item in ScavPlayer.BodyToPlayerDict.Values) {
                            MedicalSync.Server_QueueSendCharacterHealth(item.netBody);
                        }

                        yield return new WaitForSeconds(2.0f);


                        if (Util.TryGetLocalBody(out var lb)) {
                            //lb.TakeANap();
                            WoundView.view.TakeANap();
                        }


                    } else {

                        while (!Util.IsWorldGenerated()) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(7.0f);

                        Con.RunCommand("setbodyfield energy 13", true);
                        Multiplayer.rules.EnableSleep = true;

                        WoundView.view.TakeANap();
                    }

                    break;
                }


                case "save_test": {
                    Con.RunCommand("volume 0.011", true);


                    if (!Plugin.cmd_start__is_client) {

                        Con.RunCommand("give @a fentanyl");
                        Con.RunCommand("give id:1 hoodie pistol");

                        yield return new WaitForSecondsRealtime(3f);

                        Con.RunCommand("saveandquit");

                    } else {


                    }

                    break;
                }
                case "saveload_test": {
                    Con.RunCommand("volume 0.011", true);


                    if (!Plugin.cmd_start__is_client) {

                        Con.RunCommand("give @a fentanyl");
                        Con.RunCommand("give id:1 hoodie pistol");

                        yield return new WaitForSecondsRealtime(3f);

                        Con.RunCommand("saveandquit");

                        yield return new WaitForSecondsRealtime(3f);


                        var prs = PreRunScript.instance;
                        if (prs != null) {
                            prs.LoadRun();
                        }
                        yield return new WaitForSecondsRealtime(3f);

                    } else {


                    }

                    break;
                }
                case "cpr_test": {
                    if (!Plugin.cmd_start__is_client) {

                        yield return new WaitForSecondsRealtime(0.1f);

                        Con.RunCommand($"give @a legpouch bigpack aed manualdefibrillator bandage shotgun morphine");

                        yield return new WaitForSecondsRealtime(1f);

                        while (!Application.isFocused) {
                            yield return new WaitForSecondsRealtime(0.1f);
                        }
                        while (true) {
                            yield return new WaitForSecondsRealtime(0.4f);

                            Together.log.l("STOPPING HEART, press Mouse3 to continue");
                            var body = Con.GetBodyForCommand("@r",null).body;

                            body.heartRate = 0;

                            yield return new WaitForSecondsRealtime(1f);

                            while (!Input.GetKey(KeyCode.Mouse3)) {
                                yield return new WaitForSecondsRealtime(0.1f);
                            }
                            Together.log.l("DEFIBRILATION, press Mouse3 to continue");

                            body.fibrillationProgress = 90;

                            yield return new WaitForSecondsRealtime(1f);

                            while (!Input.GetKey(KeyCode.Mouse3)) {
                                yield return new WaitForSecondsRealtime(0.1f);
                            }
                        }

                    } else {
                        var body = Util.GetLocalBody();


                    }

                    break;
                }

                case "decaytest": {
                    var body = Util.GetLocalBody();
                    if (!Plugin.cmd_start__is_client) {

                        yield return new WaitForSecondsRealtime(0.1f);

                        var lp = Con.SpawnThingOnPlayer("foliagebag", body, true);
                        Con.SpawnThingOnPlayer("pizzaslice", body, true, lp);

                        //Con.RunCommand($"give @a foliagebag bandage foliagebag pizzaslice foliagebag foliagebag bandage bandage foliagebag bigpack bandage bandage bandage");

                        yield return new WaitForSecondsRealtime(1f);


                    } else {


                    }

                    break;
                }
                default: {
                    break;
                }
            }

#endif 
            yield return null;
        }


        private void Update() {
            FrameBuckets.NoteFrame(Time.unscaledDeltaTime);
            long tick = FrameBuckets.Start();
            PlayerCheckpoint.Tick();
            CraftCommit.Tick();
            MoveCommit.Tick();
            FrameBuckets.Add(FrameBuckets.IdTick, tick);
            ChunkSleep.Tick();
            BagSleep.Tick();
            OptimizerMain.Tick();
        }

        private void FixedUpdate() {
            BagSleep.WriteHost();
        }

        private void LateUpdate() {
            OptimizerMain.LateTick();
            ChunkSleep.LateFly();
        }
        [ClientReceiver((byte)NetmsgId.CLIENT_SetWindowPos)]
        static void ClientReceiver__SetWindowPos(knetid _, LiteNetLib.Utils.NetDataReader reader) {
            if (Net.Transport != null && Net.Transport is TransportLiteNetLib lnl && IPAddress.IsLoopback(IPAddress.Parse(lnl.ip)) && Together.log.verbose) {
            } else {
                return;
            }

            reader.Get(out Vector2Int pos);
            reader.Get(out Vector2Int size);

            Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, pos);
            Screen.SetResolution(size.x, size.y, false);

        }

    }





    public static class win32windowtitlechanger {

        public static bool changed_the_title = false;


        //og: CasualtiesUnknown    (known from Application.productName )


        static IntPtr hwnd = IntPtr.Zero;

        public static void SetTitle(string newname) {
            if (hwnd == IntPtr.Zero) {
                hwnd = Process.GetCurrentProcess().MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero) {
#if TESTING
                log.error($"ERROR FAILED TO CHANGE THE WINDOWTEXT, WINDOW NOT FOUND!!!! WHAT !!!?!?: {Process.GetCurrentProcess()}");
#endif
            } else {
#if TESTING
                log.l($"win32windowtitlechanger: Set name to: {newname}");
#endif
                SetWindowText(hwnd, newname);
                changed_the_title = true;
            }
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowText", CharSet = CharSet.Unicode)]
        static extern bool SetWindowText(System.IntPtr hwnd, System.String lpString);
    }





#if !RELEASE
    [HarmonyPatch(typeof(Settings), nameof(Settings.ApplyAll))]
    static class Settings_ApplyAll_MultiplayerPatch {
        static bool Prefix(Settings __instance) {
            try {
                Util.SetSetting("reswidth", Screen.width);
                Util.SetSetting("resheight", Screen.height);
                Util.SetSetting("fullscreen", Screen.fullScreen);
            } catch (Exception e) {
                log.error(e);
            }
            return true;
        }
    }
#endif

}



namespace KrokoshaCasualtiesMP_DISABLED_STATE {
    internal class KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER : MonoBehaviour {
        internal static bool ON_CLICK_WILL_ENABLE_THE_MOD = true;
        GameObject MP_LINK_BUTTON = null;
        public static Sprite mpmod_icon = null;
        void Awake() {
            try {
                mpmod_icon = Together.CoopModAssets.LoadSprite("mp.png");
            } catch (Exception ex) {
                doerror(ex.ToString());
            }
#if !DONT_RESTART_IF_RESTART_BULLSHIT
            try {
                KSteam.CheckSteam();
            } catch (Exception ex) {
                doerror("Attempt to load steam" + ex.ToString());
            }
#endif
            Application.runInBackground = false;
        }

        void DoTheNormalButton() { // if it cant create the button, the person is better off just playing singleplayer
            if (MP_LINK_BUTTON == null) {
                var prerun = PreRunScript.instance;
                if (prerun != null) {//is in main menu

                    try {
                        var credits = prerun.transform.Find("Credits");
                        var trans = prerun.transform.Find("Credits/TranslationRepo");
                        if (trans != null) {
                            MP_LINK_BUTTON = GameObject.Instantiate(trans.gameObject, prerun.transform, false).gameObject;
                            MP_LINK_BUTTON.name = "KROKMP_BUTTON_LINK";

                            var rt = MP_LINK_BUTTON.GetComponent<RectTransform>();
                            //rt.anchoredPosition = new Vector2(50 + 90, rt.anchoredPosition.y);
                            rt.anchoredPosition = new Vector2(50, rt.anchoredPosition.y);
                            rt.SetSiblingIndex(credits.GetSiblingIndex());

                            var tt = MP_LINK_BUTTON.GetComponent<UITooltip>();

                            var tool = ON_CLICK_WILL_ENABLE_THE_MOD ? "mainmenu_mp_button_tooltip_lastresort_enable_mod" : "mainmenu_mp_button_tooltip_lastresort_disable_mod";

                            tt.localeName = Lang.LocalPrefixTag + tool;
                            tt.skipLocale = true;

                            tt.tipName = Lang.Get(tool);

                            var img = MP_LINK_BUTTON.GetComponent<Image>();
                            img.sprite = mpmod_icon;
                            img.color = Color.red;

                            var button = MP_LINK_BUTTON.GetComponent<UnityEngine.UI.Button>();
                            button.onClick.RemoveAllListeners();
                            button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                            button.onClick.AddListener(() => {

                                PlayerPrefsExtended.SetBool(Plugin.FORCEDISABLE_PLAYERPREF_KEY, !ON_CLICK_WILL_ENABLE_THE_MOD);

                                try {
                                    PlayerPrefs.Save();
                                    log.l($"SET {Plugin.FORCEDISABLE_PLAYERPREF_KEY} => {ON_CLICK_WILL_ENABLE_THE_MOD}");
                                } catch (System.Exception e) {
                                }

                                if (ON_CLICK_WILL_ENABLE_THE_MOD) {

                                } else {

                                }
                                Plugin.RestartGame();

                            });
                        }

                    } catch (System.Exception e) {
                        doerror("DoTheNormalButton failure " + e.ToString());
                    }

                }
            }
        }
        void Update() {
            if (mpmod_icon == null) return; // its fucked then
            try {
                DoTheNormalButton();
            } catch (Exception ex) {
                doerror(ex.ToString());
            }
        }
        int errorcounter = 0;
        void doerror(string str) {
            if (errorcounter < 3) {
                errorcounter++;

                str = "KrokoshaCasualtiesMP_DISABLED: " + str + "\nFULL STACKTRACE:\n" + new StackTrace();

                if (Together.Plugin.log != null) {
                    Together.Plugin.log.LogError(str);
                } else {
                    print(str);
                }
            }
        }
    }


}








