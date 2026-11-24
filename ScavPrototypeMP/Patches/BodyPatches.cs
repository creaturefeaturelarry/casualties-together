using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together {




    [HarmonyPatch(typeof(MindwipeScript), nameof(MindwipeScript.Start))]
    public static class MindwipeScript_Start_MultiplayerPatch {
        static bool Prefix(MindwipeScript __instance) {
            if (!Net.IsRunning) {
                return true;
            }

            Body body = __instance.GetComponent<Body>();
            if (body.IsBodyLocal()) { // do the sequence end sound
                if (ScavPlayer.LocalPlayer != null) {
                    if (ScavPlayer.LocalPlayer.TryGetNetBody(out NetBody nb)) {
                        nb.SetNetHealthSyncIgnoreTime(1f);
                    }
                }
                return true;
            } else {
                if (__instance.active) {
                    return false;
                }
                __instance.StartCoroutine("WipeRoutine"); // only do the mindwipe sequence
                return false;
            }
        }
    }
    [HarmonyPatch(typeof(MindwipeScript), nameof(MindwipeScript.WipeRoutine))]
    public static class MindwipeScript_WipeRoutine_MultiplayerPatch {

        class MindwipeMPTracker : MonoBehaviour {
            public GameObject go;
            void Update() {
                if (!TryGetComponent<MindwipeScript>(out MindwipeScript mw)) {
                    if (go != null) {
                        Destroy(go);
                    }
                    if (this != null) {
                        Destroy(this);
                    }
                }
            }
        }

        public static IEnumerator Copypasted_WipeRoutine(MindwipeScript __instance) {
            yield return new WaitForSeconds(2f);
            Body body = __instance.GetComponent<Body>();
            float timer = 0f;
            Limb[] limbs;
            while (timer < 3f) {
                body.Ragdoll();
                body.shock = 20f;
                body.hearingLoss += Time.deltaTime * 50f;
                if (body.IsBodyLocal()) {
                    PlayerCamera.main.bonusAbber += Time.deltaTime * 8f;
                }

                limbs = body.limbs;
                foreach (Limb obj in limbs) {
                    obj.rb.angularVelocity += Random.Range(-400f, 400f);
                    obj.rb.velocity += Random.insideUnitCircle * 2f;
                    obj.pain += Time.deltaTime * 100f;
                }
                timer += Time.deltaTime;
                yield return null;
            }
            limbs = body.limbs;
            for (int i = 0; i < limbs.Length; i++) {
                limbs[i].pain = 0f;
            }
            body.consciousness = 0f;
            body.energy = 0f;
            body.sleeping = true;
            body.hearingLoss = 80f;
            body.strokeAmount = 0f;
            if (body.alive) {
                body.brainHealth += 50f;
            }
            body.skills.INT = 0;
            body.skills.expINT = 0f;
            body.skills.UpdateExpBoundaries();

            if (body.IsBodyLocal()) {
                // idk maann, let the server keep the recipes ig
                if (Net.IsClient) {
                    foreach (Recipe recipe in Recipes.recipes) {
                        recipe.hasMadeBefore = false;
                    }
                }

                MusicManager.main.StopSong();
                GameObject vign = GameObject.Instantiate(Resources.Load("Special/MindwipeVignette"), PlayerCamera.main.mainCanvas.transform) as GameObject;
                vign.transform.SetAsFirstSibling();
                vign.AddComponent<MindwipeMPTracker>().go = vign;
            }
            __instance.active = true;
        }


        static bool Prefix(MindwipeScript __instance, ref IEnumerator __result) {
            if (!Net.IsRunning) {
                return true;
            }

            __result = Copypasted_WipeRoutine(__instance);
            return false;
        }
    }










    [HarmonyPatch(typeof(Skills), nameof(Skills.CheckForLevelUp))]
    public static class Skills_CheckForLevelUp_MultiplayerPatch {
        static void Prefix(Skills __instance, ref int level, ref float xp, ref int xpToLevel) {
            if (xp >= (float)(int.MaxValue - 1)) { // fix level overflow crash 
                xp = (int.MaxValue - 100);
            } else if (xp < 0) {
                xp = (int.MaxValue / 2);
            }
        }
        static void Postfix(Skills __instance, ref bool __result) {
            if (!Net.IsRunning) {
                return;
            }

            foreach (Body item in ScavPlayer.BodyToPlayerDict.Keys) {
                if (item.skills == __instance) {
                    if (!Util.IsBodyLocal(item)) {
                        __result = false; // it shouldnt cause any problems, it only triggers the visuals, sound and alert
                    }
                    break;
                }
            }
        }
    }



    [HarmonyPatch(typeof(Skills), nameof(Skills.UpdateExpBoundaries))]
    static class Skills_UpdateExpBoundaries_MultiplayerPatch {
        static void Postfix(Skills __instance) {  // fix level overflow crash 
            if (__instance.maxSTR < 0) {
                __instance.maxSTR = int.MaxValue;
            }
            if (__instance.maxINT < 0) {
                __instance.maxINT = int.MaxValue;
            }
            if (__instance.maxRES < 0) {
                __instance.maxRES = int.MaxValue;
            }
        }
    }




    // owner client is the source for xp its own body actually earns. the host copy does not
    // carry, hurt, or read, and the next health snapshot writes the host numbers back.
    // host-only grants (trader, combat, layer skip, console) call HostGrant so they are not dropped.
    [HarmonyPatch(typeof(Skills), nameof(Skills.AddExp))]
    internal static class SkillSync {
        struct ExpState {
            public bool skipped;
            public bool report;
            public int str;
            public int res;
            public int intel;
            public Body body;
        }

        struct Stamp {
            public byte stat;
            public float amount;
            public float until;
        }

        static int _hostGrant;
        static int _allowVanilla;
        static bool _swallowedSwing;
        static float _nextFlush;
        static readonly float[] _pending = new float[3];
        static readonly Dictionary<int, Stamp> _recent = new Dictionary<int, Stamp>();

        const float FlushGap = 0.5f;
        const float GrantCap = 5000f;
        const float DedupeWindow = 2f;

        static SkillSync() {
            Multiplayer.RegisterCustomServerReceiver("together.passive-skill", OnPassiveSkill);
        }

        // a swing the attack patch took away from vanilla, so vanilla never reached its strength grant.
        internal static void NoteSwallowedSwing() {
            _swallowedSwing = true;
        }

        internal static bool TakeSwallowedSwing() {
            bool swallowed = _swallowedSwing;
            _swallowedSwing = false;
            return swallowed;
        }

        // host-authoritative vanilla (haggle) still has to pay the remote body.
        internal static void AllowHostVanilla(Action act) {
            _allowVanilla++;
            try {
                act();
            } finally {
                _allowVanilla--;
            }
        }

        internal static void HostGrant(Body body, int stat, float xp) {
            if (!Apply(body, stat, xp)) {
                return;
            }
            if (Net.IsServer && body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.ForceHealth(nb);
            }
        }

        internal static void TickFlush() {
            if (Time.unscaledTime < _nextFlush) {
                return;
            }
            _nextFlush = Time.unscaledTime + FlushGap;
            Flush();
        }

        static bool Prefix(Skills __instance, int stat, float xp, ref ExpState __state) {
            Body body = BodyOf(__instance);
            __state.body = body;
            bool remoteHost = IsRemoteOnHost(body);
            if (remoteHost && _hostGrant == 0 && _allowVanilla == 0) {
                __state.skipped = true;
                return false;
            }
            // stock AddExp toasts and plays the level-up sting on PlayerCamera.main. a remote body must not do that.
            if (remoteHost) {
                ApplyQuiet(__instance, stat, xp);
                __state.skipped = true;
                return false;
            }
            if (ShouldReport(body)) {
                __state.report = true;
                __state.str = body.skills.STR;
                __state.res = body.skills.RES;
                __state.intel = body.skills.INT;
            }
            return true;
        }

        static void Postfix(Skills __instance, int stat, float xp, ref ExpState __state) {
            if (__state.skipped || !__state.report || __state.body == null) {
                return;
            }
            if (stat < 0 || stat > 2 || xp <= 0f || float.IsNaN(xp) || float.IsInfinity(xp)) {
                return;
            }
            _pending[stat] += xp;
            bool leveled = __state.body.skills.STR > __state.str
                || __state.body.skills.RES > __state.res
                || __state.body.skills.INT > __state.intel;
            if (xp >= 1f || leveled) {
                Flush();
            }
        }

        static void Flush() {
            if (!Net.IsClient || Net.IsServer) {
                return;
            }
            float str = _pending[0];
            float res = _pending[1];
            float intel = _pending[2];
            if (str <= 0f && res <= 0f && intel <= 0f) {
                return;
            }
            _pending[0] = _pending[1] = _pending[2] = 0f;
            NetDataWriter writer = Multiplayer.CreateNamedWriter("together.passive-skill");
            writer.Put(Cap(str));
            writer.Put(Cap(res));
            writer.Put(Cap(intel));
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        static void OnPassiveSkill(ScavPlayer plr, LiteNetLib.Utils.NetDataReader reader) {
            if (plr == null || plr.body == null || plr.body.skills == null) {
                return;
            }
            float str = reader.GetFloat();
            float res = reader.GetFloat();
            float intel = reader.GetFloat();
            bool any = false;
            any |= ApplyReported(plr.body, 0, str);
            any |= ApplyReported(plr.body, 1, res);
            any |= ApplyReported(plr.body, 2, intel);
            if (any && plr.netBody != null) {
                MedicalSync.ForceHealth(plr.netBody);
            }
        }

        // same math as Skills.AddExp, without the camera alert or the level-up sting.
        static void ApplyQuiet(Skills skills, int stat, float xp) {
            if (skills == null || stat < 0 || stat > 2 || xp <= 0f || float.IsNaN(xp) || float.IsInfinity(xp)) {
                return;
            }
            xp *= Skills.xpGainMult;
            switch (stat) {
                case 0:
                    xp *= skills.STRGainMult;
                    skills.expSTR += xp;
                    skills.CheckForLevelUp(ref skills.STR, skills.expSTR, ref skills.maxSTR);
                    break;
                case 1:
                    xp *= skills.RESGainMult;
                    skills.expRES += xp;
                    skills.CheckForLevelUp(ref skills.RES, skills.expRES, ref skills.maxRES);
                    break;
                default:
                    xp *= skills.INTGainMult;
                    skills.expINT += xp;
                    skills.CheckForLevelUp(ref skills.INT, skills.expINT, ref skills.maxINT);
                    break;
            }
        }

        static bool ApplyReported(Body body, int stat, float xp) {
            if (xp > GrantCap) {
                return false;
            }
            return Apply(body, stat, xp);
        }

        static bool Apply(Body body, int stat, float xp) {
            if (body == null || body.skills == null || stat < 0 || stat > 2) {
                return false;
            }
            if (xp <= 0f || float.IsNaN(xp) || float.IsInfinity(xp)) {
                return false;
            }
            if (Duplicate(body, stat, xp)) {
                return false;
            }
            Remember(body, stat, xp);
            _hostGrant++;
            try {
                body.skills.AddExp(stat, xp);
            } finally {
                _hostGrant--;
            }
            return true;
        }

        static float Cap(float xp) {
            if (xp <= 0f || float.IsNaN(xp) || float.IsInfinity(xp)) {
                return 0f;
            }
            return xp > GrantCap ? GrantCap : xp;
        }

        static bool ShouldReport(Body body) {
            return _hostGrant == 0 && Net.IsClient && !Net.IsServer && IsLocalOwner(body);
        }

        static bool IsRemoteOnHost(Body body) {
            return Net.IsServer && body != null && body.TryGetNetPlayer(out ScavPlayer owner) && owner != null && !owner.IsLocal;
        }

        static bool IsLocalOwner(Body body) {
            return body != null && body.skills != null && body.TryGetNetPlayer(out ScavPlayer owner) && owner != null && owner.IsLocal;
        }

        static Body BodyOf(Skills skills) {
            if (skills == null) {
                return null;
            }
            PlayerCamera cam = PlayerCamera.main;
            if (cam != null && cam.body != null && cam.body.skills == skills) {
                return cam.body;
            }
            foreach (Body body in ScavPlayer.BodyToPlayerDict.Keys) {
                if (body != null && body.skills == skills) {
                    return body;
                }
            }
            return null;
        }

        static bool Duplicate(Body body, int stat, float xp) {
            if (xp < 1f || !_recent.TryGetValue(body.GetInstanceID(), out Stamp stamp)) {
                return false;
            }
            if (Time.unscaledTime > stamp.until || stamp.stat != stat) {
                return false;
            }
            return Mathf.Abs(stamp.amount - xp) < 0.05f;
        }

        static void Remember(Body body, int stat, float xp) {
            if (xp < 1f) {
                return;
            }
            _recent[body.GetInstanceID()] = new Stamp {
                stat = (byte)stat,
                amount = xp,
                until = Time.unscaledTime + DedupeWindow,
            };
        }
    }



    [HarmonyPatch(typeof(PantSound), nameof(PantSound.Bark))]
    static class PantSound_Bark_MultiplayerPatch {
        static void Postfix(PantSound __instance) {
            if (!Net.IsRunning) {
                return;
            }

            if (__instance.body != null && __instance.body.TryGetNetBody(out NetBody nb)) {
                nb.lastTimeBark = Time.realtimeSinceStartupAsDouble;
            }
        }
    }





    [HarmonyPatch(typeof(Body), nameof(Body.Jump))]
    static class Body_Jump_MultiplayerPatch {
        struct preJumpState {
            public bool last_wantsJumpInput;
            public bool last_reserved1;
        }

        static bool Prefix(Body __instance, ref preJumpState __state) {
            __state.last_wantsJumpInput = PlayerCamera.main.wantsJumpInput;

            if (__instance.TryGetComponent(out NetBody nb)) {
                nb.StopPiggyback(); // jump to cancel piggyback
            }
            return true;
        }
        static void Postfix(Body __instance, ref preJumpState __state) {

            if (__instance.IsBodyLocal()) {
                if (!PlayerCamera.main.wantsJumpInput) { // actually jumped 
                    if (Net.IsRunning && Net.IsClient && Util.IsWorldGenerated()) {
                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerJump, false);
                    }
                }
            } else {
                PlayerCamera.main.wantsJumpInput = __state.last_wantsJumpInput;

            }
            if (Net.IsServer) {
                if (__instance.TryGetComponent(out NetBody nb)) {

                    NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerJumpRelay);
                    writer.Put(nb.netId);
                    Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.GetListOfClientIdsExceptThisAndHost(nb));
                    // WRITER END;

                }
            }
        }
    }




    [HarmonyPatch(typeof(Body), nameof(Body.HandlePeriodicChecks))]
    public static class Body_HandlePeriodicChecks_MultiplayerPatch {

        // remote bodies share synced brainHealth; vanilla braindamagefx drives PlayerCamera.main
        // (craft / wound / radial / timescale / etc) so other players' damage was opening local UI.
        struct PeriodicState {
            public bool halfMinuteTick;
            public bool suppressedBrainFx;
            public bool savedForceNoMovement;
        }

        static void Prefix(Body __instance, ref PeriodicState __state) {
            Body body = __instance;

            __state = new PeriodicState {
                halfMinuteTick = body.halfMinuteCheckTime + Time.deltaTime > 30f,
            };

            if (!Net.IsRunning || !__state.halfMinuteTick || body.brainHealth >= 95f) {
                return;
            }

            if (Util.IsBodyLocal(body)) {
                // reenable flash brain for local player (cuz i overriden it with the patch)
                if (body.consciousness > 20f && (double)UnityEngine.Random.value > (double)body.brainHealth * 0.01 && !PlayerCamera.main.lastStandPanel.activeSelf) {
                    if (!UIInGame.SpectatorModeActive) {
                        PlayerCamera.main.ForceFlashBrain();
                    }
                }
                return;
            }

            // vanilla gate: brainHealth < 95 && braindamagefx && !forceNoMovement
            __state.savedForceNoMovement = body.forceNoMovement;
            body.forceNoMovement = true;
            __state.suppressedBrainFx = true;
        }

        static void Postfix(Body __instance, ref PeriodicState __state) {
            Body body = __instance;

            if (Net.IsClient && !Net.IsServer && body != null && Util.IsBodyLocal(body)) {
                SkillSync.TickFlush();
            }

            if (__state.suppressedBrainFx) {
                body.forceNoMovement = __state.savedForceNoMovement;
            }

            // stock reads CoUtils.instance. in multiplayer that getter is the singleton,
            // while the drink timer often lives on the body's own CoUtils. the speed buff
            // still runs. the moodle and the saturation tint look at this flag.
            if (Net.IsRunning && body != null) {
                bool fromOwn = StimulantActive(body.GetCoUtilsInstance());
                if (Util.IsBodyLocal(body))
                    fromOwn = fromOwn || StimulantActive(CoUtils_instance_MultiplayerPatch.original_local_instance);
                if (Net.IsServer)
                    body.onHardStimulants = fromOwn;
                else if (fromOwn)
                    body.onHardStimulants = true;
            }

            if (!Net.IsRunning || !__state.halfMinuteTick || Util.IsBodyLocal(body)) {
                return;
            }

            // belt-and-suspenders if any FX still leaked before the suppress
            if (PlayerCamera.main != null && PlayerCamera.main.threatMusicTime == 8f) {
                PlayerCamera.main.threatMusicTime = 0f;
            }
        }

        static bool StimulantActive(CoUtils cu) {
            if (cu == null)
                return false;
            return cu.DurationOf("midgradestimulant") > 0f
                || cu.DurationOf("lowgradestimulant") > 0f
                || cu.DurationOf("highgradestimulant") > 0f;
        }

    }




    [HarmonyPatch(typeof(Body), nameof(Body.PlaceBody))]
    public static class Body_PlaceBody_MultiplayerPatch {
        public static bool has_spawn_location = false;
        public static Vector2 SpawnLocation = new Vector2(0, 500);

        static Vector2 ogcampos;
        static Vector2 ogbodypos;

        // drill pod / doPod / ascent: everyone belongs on the same spot, not spread by prior X.
        public static bool ShouldKeepLayerXOffset() {
            if (!Multiplayer.rules.LayerFinishKeepXOffset) {
                return false;
            }
            if (WorldGeneration.world != null && WorldGeneration.world.doPod) {
                return false;
            }
            if (WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 10) {
                return false;
            }
            return true;
        }

        static bool Prefix(Body __instance) {
            if (!Net.IsRunning) {
                return true;
            }

            ogbodypos = __instance.transform.position;
            ogcampos = PlayerCamera.main.transform.position;

            bool ascent = WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 10;
            if ((ShouldKeepLayerXOffset() || ascent) && ScavPlayer.TryGetLocalNetBody(out NetBody pb) && Net.IsServer && ScavPlayer.LocalPlayer.IsAlive()) {
                float x = ascent ? 0f : ScavPlayer.LocalPlayer.ServerPlayerState.layer_transition_x_offset;
                pb.SetBodyPosition(Util.PlaceBody_FindSpawnLocation(x, pb.body));
                return false;
            }

            return true;
        }
        static void Postfix(Body __instance) {
            if (!Net.IsRunning) {
                return;
            }

            Body body = __instance;
            if (body.IsBodyLocal()) {
                if (Net.IsClient) {
                    __instance.transform.position = ogbodypos;
                } else {
                    has_spawn_location = true;
                    Vector2 podSpawn = body.transform.position;
                    bool packTogether = WorldGeneration.world != null && (WorldGeneration.world.doPod || WorldGeneration.world.biomeDepth == 10);
                    bool levelSpawn = WorldSpawn.Applies(WorldGeneration.world);
                    MapPoses.TryApply(body);
                    foreach (NetBody item in NetBody.all_instances) {
                        if (item.IsPlayer && !item.IsBodyLocal()) {
                            item.plr.ResetEntropy();
                            if (item.body != null && MapPoses.TryApply(item.body))
                                continue;
                            // a saved level spawn is one spot. pod and ascent match the host. otherwise keep their X.
                            item.SetBodyPosition(levelSpawn
                                ? WorldSpawn.Pos
                                : packTogether
                                ? podSpawn
                                : new Vector2(item.transform.position.x, podSpawn.y));
                        }
                    }
                }

                SpawnLocation = body.transform.position;
            } else {
                ogcampos = PlayerCamera.main.transform.position;
            }

#if !RELEASE && false
            KrokoshaScavMultiConsoleCommands.SpawnThingOnPlayer("fentanyl", body, true);
            KrokoshaScavMultiConsoleCommands.SpawnThingOnPlayer("combatpen", body, true);
            KrokoshaScavMultiConsoleCommands.SpawnThingOnPlayer("mp3player", body, true);
#endif

        }
    }






    // tryLastStand is server-authoritative
    [HarmonyPatch(typeof(Body), nameof(Body.TryLastStand))]
    public static class Body_TryLastStand_MultiplayerPatch {

        public static void ForceLastStand(this Body b) {
            force = true;
            b.lastHappiness[9] = 100;
            b.TryLastStand();
            force = false;
        }


        // the call for laststand sequence
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            bool didit = false;

            for (int i = 0; i < codes.Count - 6; i++) {
                if (
                    codes[i].opcode == OpCodes.Ldsfld &&

                    codes[i + 1].opcode == OpCodes.Ldsfld &&

                    codes[i + 2].opcode == OpCodes.Callvirt &&
                    codes[i + 2].operand is MethodInfo method &&
                    method.Name == nameof(PlayerCamera.LastStandSequence) &&

                    codes[i + 3].opcode == OpCodes.Callvirt &&
                    codes[i + 3].operand is MethodInfo method2 &&
                    method2.Name == "StartCoroutine" &&

                    codes[i + 4].opcode == OpCodes.Pop
                ) {
                    CodeInstruction ci = new CodeInstruction(OpCodes.Nop);
                    ci.MoveLabelsFrom(codes[i]);

                    codes.RemoveRange(i, 5);


                    codes.Insert(i, ci);

                    i += 1;


                    didit = true;
#if DEBUG 
                    Plugin.log.LogInfo($"Body_TryLastStand_MultiplayerPatch transpiler: succesfully patched. ");
#endif
                }
            }

            if (!didit) {
                Plugin.log.LogError($"Body_TryLastStand_MultiplayerPatch transpiler: Not a single pattern was found :(  ");
            }

            return codes;
        }




        static bool force = false;

        static bool Prefix(Body __instance, ref bool __state) {
            __state = false;

            if (!__instance.succesfullyRolledLastStand && Util.IsBodyLocal(__instance)) {
                __state = true;
            }
            if (Net.IsRunning) {
                if (!Multiplayer.rules.LastStandAllowed) {
                    return false;
                }

                if (Multiplayer.IsNetworkActiveAndIsServer()) {
                } else {
                    // client cant roll this
                    if (!force) {
                        return false;
                    }
                }
                __instance.GetCoUtilsInstance().SelectForMPOverride();
            }

            force = false;
            return true;
        }
        static void Postfix(Body __instance, ref bool __state) {
            CoUtilsMultiplayer.DeselectMPOverride();

            if (__state && __instance.succesfullyRolledLastStand) {

                if (__instance.succesfullyRolledLastStand && Net.IsRunning) {
                    Plugin.log.LogInfo($"{ScavPlayer.LocalPlayer.playerName}, rolled Last Stand succesfully. ");
                } else {
                    Plugin.log.LogInfo($"Server host character rolled Last stand. outcome: {__instance.succesfullyRolledLastStand} ");
                }

                //OG: Body.TryLastStand V5t3
                PlayerCamera.main.StartCoroutine(PlayerCamera.main.LastStandSequence());

            } else if (Multiplayer.IsNetworkActiveAndIsServer()) {
                if (__instance.TryGetNetPlayer(out ScavPlayer plr)) {
                    if (__instance.succesfullyRolledLastStand) {
                        Plugin.log.LogInfo($"{plr.playerName}, rolled Last Stand succesfully ");
                    } else {
                        Plugin.log.LogInfo($"Server rolled Last stand for {plr.playerName} outcome: {__instance.succesfullyRolledLastStand} ");
                    }

                    MedicalSync.Server_SendCharacterHealth(plr.netBody);
                }
            }

        }
    }










    [HarmonyPatch(typeof(Body), nameof(Body.DoWorkout))]
    public static class Body_DoWorkout_MultiplayerPatch {


        [HarmonyReversePatch]
        public static IEnumerator DoWorkout(object instance, Body.WorkoutType type) {
            throw new NotImplementedException();
        }


        public static bool Prefix(Body __instance, Body.WorkoutType type, ref IEnumerator __result) {
            __result = Patched_DoWorkout(__instance, type);
            return false;
        }

        public static IEnumerator Patched_DoWorkout(object instance, Body.WorkoutType type) {
            Body body = (Body)instance;

            if (Net.IsRunning) {
                if (Util.IsBodyLocal(body) && body.TryGetNetBody(out NetBody pb)) {
                    if (log.verbose) {
                        log.l($"Sending StartExcercise  {type}");
                    }

                    if (Net.IsClient) {
                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_StartExcercise, (ushort)type, true);
                    } else {
                        Server_RelayStartExcercise(Net.SERVER_CLIENTID, type);
                    }
                }
            }

            yield return DoWorkout(instance, type);

            if (Net.IsRunning) {
                if (body.TryGetNetBody(out NetBody pb)) {
                    if (Net.IsClient) {
                        if (body.IsBodyLocal()) {
                            if (log.verbose) {
                                log.l($"CLIENT: Sending RequestStopExcercise ");
                            }

                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestStopExcercise, true);
                        }
                    } else {
                        if (log.verbose) {
                            log.l($"SERVER: Sending StopExcercise ");
                        }

                        Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_StopExcercise, pb.netId, true, false);
                    }
                }
            }
        }



        public static void ForceStopExcercising(Body body) {

            //OG: Body.DoWorkout V5t3  where it does the break;, attack cooldown must be above zero for it to cancel
            body.attackCooldown = Mathf.Max(body.attackCooldown, Time.deltaTime * 1.1f);
        }

        static void Server_RelayStartExcercise(knetid exorcist, Body.WorkoutType type) {

            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_StartExcerciseRelay);
            writer.Put(exorcist);
            writer.Put((byte)type);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
            // WRITER END;
        }

        [ServerReceiver((byte)NetmsgId.SERVER_StartExcercise)]
        static void ServerReceiver_StartExcercise(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
            reader.Get(out ushort yup);
            Body.WorkoutType type = (Body.WorkoutType)yup;

            if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body) && body.conscious) {

                body.StartCoroutine(body.DoWorkout(type));

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(body.transform.position, $"S: StartExcercise {plr} {type} ");
                }

                Server_RelayStartExcercise(clientId, type);

            }
        }

        [ServerReceiver((byte)NetmsgId.SERVER_RequestStopExcercise)]
        static void Server_RequestStopExcercise(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
            if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body)) {

                ForceStopExcercising(body);

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(body.transform.position, $"S: RequestStopExcercise {plr} ");
                }

                // itll be relayed to everyone after it stops because of the postfix
            }
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_StartExcerciseRelay)]
        static void Client_StartExcercise(knetid _, LiteNetLib.Utils.NetDataReader reader) {

            reader.Get(out knetid who);
            reader.Get(out byte yup);
            Body.WorkoutType type = (Body.WorkoutType)yup;

            if (ScavPlayer.TryGetPlayerAndBodyFromClientId(who, out ScavPlayer plr, out Body body) && !plr.IsLocal) {

                body.StartCoroutine(body.DoWorkout(type));

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(body.transform.position, $"C: StartExcerciseRelay {plr} {type} ");
                }
            }
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_StopExcercise)]
        static void Client_StopExcercise(knetid _, LiteNetLib.Utils.NetDataReader reader) {
#if !RELEASE
            Plugin.log.LogWarning($"Client_StopExcercise actually works lol ");
#endif

            reader.Get(out knetid who);

            if (ScavPlayer.TryGetPlayerAndBodyFromClientId(who, out ScavPlayer plr, out Body body)) {

                ForceStopExcercising(body);

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(body.transform.position, $"C: StopExcercise {plr} ");
                }
            }
        }
    }





    //    // remove heart thumps for remote peers

    [HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
    public static class Body_HandleCirculation_MultiplayerPatch {
        struct aaaaaaaaaeeeeeeeeeeuhh {
            public float brainHealth;
        }
        static void Prefix(Body __instance, ref aaaaaaaaaeeeeeeeeeeuhh __state) {
            __state.brainHealth = __instance.brainHealth;
            if (!Net.IsRunning) {
                return;
            }

            Sound_Play_MultiplayerPatch.is_the_fucking_updateheart_shit = (!__instance.TryGetNetBody(out NetBody pb) || !((pb.IsLocal || (Util.IsInWoundView() && WoundView.view.body == __instance)) && __instance.alive));

        }
        static void Postfix(Body __instance, ref aaaaaaaaaeeeeeeeeeeuhh __state) {
            Sound_Play_MultiplayerPatch.is_the_fucking_updateheart_shit = false;

            if (!Net.IsRunning) {
                return;
            }

            if (__instance.TryGetComponent(out NetBody nb)) {
                if (__instance.brainDying && __instance.brainHealth < __state.brainHealth) { // if is dying from cardiac arrest 

                    (NetBody, float) nearestpossiblemedic = ScavPlayer.GetNearestConsciousPlayerToThisBody(nb);
                    bool last_standing = nearestpossiblemedic.Item1 == null; // no one else was found, this person is the last one

                    if (!last_standing) {
                        float timediff = (float)(Time.unscaledTimeAsDouble - nb._lasttime_nobraindamage_from_circulation);

                        const float timeuntilstartdying = 15f;
                        const float timeuntilfullydying = 30f;


                        float movescale = 0f;

                        if (timediff > timeuntilfullydying) { // dying too long
                            movescale = 1;
                        } else if (timediff < timeuntilstartdying) {
                            movescale = 0;
                        } else {
                            movescale = timediff / timeuntilfullydying;
                        }

                        __instance.brainHealth = Mathf.Lerp(__state.brainHealth, __instance.brainHealth, movescale);
                    }


                } else {
                    nb._lasttime_nobraindamage_from_circulation = Time.unscaledTimeAsDouble;
                }
            }
        }
    }




    [HarmonyPatch(typeof(Body), nameof(Body.canTakeNap), MethodType.Getter)]
    public static class Body_get_canTakeNap_MultiplayerPatch {

#if !RELEASE
        static bool _getter_working_announced = false;
        static void Prefix() {
            if (!_getter_working_announced) {
                _getter_working_announced = true;
                Plugin.log.LogInfo($"MethodType.Getter  is working: {new StackTrace()}");
            }
        }
#endif 


        public static bool force = false;
        public static void Postfix(Body __instance, ref bool __result) {
            if (Net.IsRunning && !force) {
                if (Multiplayer.rules.DisableSleep) {
                    __result = false;
                }
                if (Util.IsBodyLocal(__instance)) {
                } else {
                    __result = true;
                }
            }
            force = false;
        }
    }






    #region SELFHARMER_PATCHES

    [HarmonyPatch(typeof(SelfHarmer), nameof(SelfHarmer.Update))]
    public static class SelfHarmer_Update_MultiplayerPatch {
        static void Postfix(SelfHarmer __instance) {
            // this one is just to stop scary ambience when sad and its not local player
            if (Net.IsRunning) {
                Body body = __instance.GetComponent<Body>();
                if (!Util.IsBodyLocal(body)) {
                    if (__instance.scarySource.isPlaying) {
                        __instance.scarySource.Stop();
                        __instance.scarySource.volume = 0;
                        __instance.scarySource.loop = false;
                    }
                }
            }
        }
    }











    [HarmonyPatch(typeof(SelfHarmer), nameof(SelfHarmer.AttemptHarm))]
    public static class YOU_SHOULD_KILL_YOURSELF_NOOOW {


        public static void DoMoodPenaltyInRadius(Body depressedguy, float emotional_damage, float maxdist) {

            // make everyone in radius sad
            foreach (NetBody witness in ScavPlayer.GetPlayerBodiesInRadius(depressedguy.transform.position, maxdist)) {
                if (witness.body != depressedguy && witness.body.alive) {

                    float oldhapines = witness.body.happiness;

                    float total_damage = emotional_damage;

                    // check if the witness actually saw em
                    if (witness.body.conscious && Util.AccurateRaycastInteractionCheckObstruction(depressedguy, witness.body, false)) {
                        total_damage += emotional_damage * 0.5f; // and double it
                    }

                    witness.body.happiness -= total_damage * witness.body.desensitizedMult;

#if !RELEASE
                    if (log.verbose) {
                        log.l($"DoMoodPenaltyInRadius WITNESS: {witness.playerName} old hap:{oldhapines} cur hap:{witness.body.happiness} ");
                    }
#endif
                }
            }
        }


        public static void SelfHarmClawAnim(Body body) {
            //OG: SelfHarmer.ClawAnim V5t5
            SelfHarmer harmer = body.GetComponent<SelfHarmer>();
            MethodInfo method = Util.GetMethod<SelfHarmer>("ClawAnim");
            method.Invoke(harmer, null);
        }
        public static void SelfHarm_Copypasted(byte type, Body body, ref Limb limb) {

            bool isJustSuicide = type == 0;
            bool isEye = type == 1;
            bool isSelfharm = type == 2;


            if (isSelfharm) {
                //OG: SelfHarmer.SelfHarm V5t5
                limb ??= body.limbs[Random.Range(1, body.limbs.Length)];
                limb.muscleHealth -= 13f;
                limb.skinHealth -= 35f;
                limb.bleedAmount += Random.Range(6f, 10f);
                limb.pain += Random.Range(20f, 30f);
                WoundView.view.AddImageToLimb(limb, Resources.Load<Sprite>("Special/selfHarmWound"), flip: false, (Limb l) => l.skinHealth >= 95f);
                body.rb.AddForce(Random.insideUnitCircle * 500f, ForceMode2D.Impulse);
                body.talker.Talk(Locale.GetCharacter("selfharm"), null, force: true);
                body.happiness += 4f;
                Sound.Play("selfharm", body.transform.position);

                if (Util.IsBodyLocal(body)) { // addition
                    Sound.Play("harmSting", Vector2.zero, twoDimensional: true, pitchShift: false, null, 0.7f);
                }

                SelfHarmClawAnim(body);

                YOU_SHOULD_KILL_YOURSELF_NOOOW.DoMoodPenaltyInRadius(body, Multiplayer.rules.SelfharmWitnessMoodDebuff * 0.25f, 10); // addition
            } else {
                //OG: SelfHarmer.Suicide V5t5
                limb ??= (isEye ? body.limbs[0] : ((Random.Range(0f, 1f) < 0.5f) ? body.limbs[4] : body.limbs[7]));
                limb.muscleHealth -= 30f;
                limb.skinHealth -= 70f;
                limb.bleedAmount += Random.Range(45f, 60f);
                limb.pain += Random.Range(25f, 35f);
                body.adrenaline += 50f;
                body.rb.AddForce(Random.insideUnitCircle * 700f, ForceMode2D.Impulse);
                if (!isEye) {
                    body.talker.Talk(Locale.GetCharacter("suicide"), null, force: true);
                }
                body.happiness += (isEye ? 16f : 12.5f);
                Sound.Play("suicide", body.transform.position);

                if (Util.IsBodyLocal(body)) { // addition: local body check
                    MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/whathaveidone"));
                    Sound.Play("harmSting", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 0.7f);
                }

                WoundView.view.AddImageToLimb(limb, Resources.Load<Sprite>("Special/suicideWound"), flip: false, (Limb l) => l.skinHealth >= 95f);
                if (isEye) {
                    body.RemoveEye();
                }
                SelfHarmClawAnim(body);

                YOU_SHOULD_KILL_YOURSELF_NOOOW.DoMoodPenaltyInRadius(body, Multiplayer.rules.SelfharmWitnessMoodDebuff, 25); // addition
            }
        }




        public static void Force(SelfHarmer __instance) {
            force_run = true;

            __instance.AttemptHarm();

            force_run = false;
        }
        static bool force_run = false;

        // body whose WaitForMinigame opened the local self-harm ui. end packet uses this.
        public static Body selfHarmMinigameBody;

        private static bool Prefix(SelfHarmer __instance) {
            if (!Net.IsRunning) {
                return true;
            }

            if (!force_run) {
                Body body = __instance.GetComponent<Body>();

                //OG: SelfHarmer.AttemptHarm V5t5
                // so everything except plushie and gun will put user into a minigame, so i must avoid those

                if (body.FindByIdThorough("plushie", out Item it)) {
#if !RELEASE
                    log.l($"AttemptHarm -> body.FindByIdThorough(\"plushie\" true ");
#endif 

                    if (Multiplayer.IsNetworkActiveAndIsServer()) {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_SelfHarmRelay);
                        writer.Put((byte)20);
                        writer.Put(new LimbNetId(body.GetHead()));
                        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                        // WRITER END;

                        return true;
                    }

                } else if (!Util.IsBodyLocal(body)) {
                    // remote body. the listen host ticks Update for these too, and Net.IsClient is false there.
                    // stock suicide would open the eye minigame on this machine and maim the camera body.
                    __instance.cooldownTime = 120f;
                    return false;
                } else if (body.totalHappiness > -90f) {
                    // local camera body. stock selfharm minigame, other players hear about it from the end packet.
                    return true;
                } else if (body.FindByTagThorough("gun", out Item gun)) {
                    // local camera body with a gun. stock Suicide stays here. do not relay it.
                    if (Multiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetBody(out NetBody nb)) {
                        nb.OnDamageEvent(new BodyDamageEvent(nb, nb, gun, 100));
                    }
                    return true;
                } else {
                    // local camera body, no gun. vanilla RemoveEye hits PlayerCamera.main.body, which is this player.
                    return true;
                }

                // all selfharm is completely overriden
                __instance.cooldownTime = 120f;
                return false;

            }

            return true;
        }



        [ClientReceiver((byte)NetmsgId.CLIENT_force_talker_say, true)]
        static void Client_ForceTalkerSay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

            reader.Get(out knetid objm_syncid);
            reader.Get(out byte type);
            reader.Get(out string mmmmm);

            object nammmmmm = null;
            GameObject go = null;
            if (type == 0) {
                if (NetObjectRegistry.TryGetSyncInfoLive(objm_syncid, out SyncInfo si)) {
#if DEBUG
                    log.l($"Client_ForceTalkerSay {si} : {mmmmm} ");
#endif
                    go = si.go;
                    nammmmmm = si;
                }
            } else if (type == 1) {
                if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(objm_syncid, out ScavPlayer plr, out NetBody pb)) {
#if DEBUG
                    log.l($"Client_ForceTalkerSay {plr} : {mmmmm} ");
#endif
                    go = pb.gameObject;
                    nammmmmm = pb;
                }
            }
            if (go != null && Util.TryGetTalkerOnObject(go, out Talker foundtalker, out string _)) {


                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(go.transform.position, $"C: {nammmmmm.ToString()} ForceTalkerSay: \"{mmmmm}\"");
                }
#if !RELEASE
                if (log.verbose) {
                    log.l($"CLIENT: received ForceTalkerSay {nammmmmm.ToString()} : {mmmmm} ");
                }
#endif
                Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, mmmmm, true);
            }
        }


        [ClientReceiver((byte)NetmsgId.CLIENT_SelfHarmRelay)]
        static void Client_SelfHarmMinigameMinigameEnd(knetid servid, LiteNetLib.Utils.NetDataReader reader) {

            reader.Get(out byte type);
            reader.Get(out LimbNetId limbid);

#if !RELEASE
            log.l($"SelfHarmRelay type:{type} ");
#endif 

            if (limbid.TryGetNetBodyAndLimb(out NetBody pb, out Limb limb)) {
                Body body = pb.body;

                if (type == 20) {
                    if (body.FindByIdThorough("plushie", out Item it)) {
                        YOU_SHOULD_KILL_YOURSELF_NOOOW.Force(body.GetComponent<SelfHarmer>());
                    }
                } else if (type == 10) {
                    // stale relay. the owner runs Suicide on their own machine, so this must not start it here.
                } else {

                    // because local has already done it and seen it 
                    if (!Util.IsBodyLocal(body)) {
                        YOU_SHOULD_KILL_YOURSELF_NOOOW.SelfHarm_Copypasted(type, body, ref limb);
                    }
                }

            }
        }

    }




    #endregion




    // string StartCoroutine("Suicide") skips the AttemptHarm prefix. only the camera body may run it.
    [HarmonyPatch(typeof(SelfHarmer), "Suicide")]
    public static class SelfHarmer_Suicide_MultiplayerPatch {
        private static bool Prefix(SelfHarmer __instance, ref IEnumerator __result) {
            if (!Net.IsRunning) {
                return true;
            }

            Body body = __instance.GetComponent<Body>();
            if (body != null && !Util.IsBodyLocal(body)) {
                __instance.harming = false;
                __instance.cooldownTime = 120f;
                __result = Skip();
                return false;
            }

            return true;
        }

        private static IEnumerator Skip() {
            yield break;
        }
    }

    // remember which body opened the ui, so a stale minigame cannot report itself as our death.
    [HarmonyPatch(typeof(SelfHarmer), "WaitForMinigame")]
    public static class SelfHarmer_WaitForMinigame_MultiplayerPatch {
        private static void Prefix(SelfHarmer __instance) {
            YOU_SHOULD_KILL_YOURSELF_NOOOW.selfHarmMinigameBody = __instance.GetComponent<Body>();
        }
    }

    // pause facial expressions when dead
    [HarmonyPatch(typeof(FacialExpression), nameof(FacialExpression.Update))]
    static class FacialExpression_Update_MultiplayerPatch {
        class FacialExpressionPause : MonoBehaviour {
            public FacialExpression face => GetComponent<FacialExpression>();

            public bool lastAlive = true;
            public List<Eye> originalEyeList;
            public List<Eye> pausedEyeList;
            void Awake() {
                if (TryGetComponent(out FacialExpression face)) {
                    originalEyeList = face.eyeList;
                }
            }
            public void SetPausedEye(Eye curEye) {
                pausedEyeList = new();
                for (int i = 0; i < originalEyeList.Count; i++) {
                    pausedEyeList.Add(curEye);
                }
            }
            public (byte, bool) GetCurrentExpressionId() {
                Sprite curEyeSprite = face.eyes.sprite;
                byte index = 2; // probably no eyes, so it's closed
                Eye curEye = originalEyeList.Find(x => x.front == curEyeSprite || x.back == curEyeSprite);
                if (curEye.front != null) {
                    index = (byte)originalEyeList.IndexOf(curEye);
                }
                return (index, face.doBackEye);
            }
            public Sprite GetEyeSprite(byte? index, bool? doBackEye) {
                (byte i, bool b) = GetCurrentExpressionId();

                i = index.GetValueOrDefault(i);
                b = doBackEye.GetValueOrDefault(b);

                Eye curEye = originalEyeList[i];
                Sprite curEyeSprite = (b ? curEye.back : curEye.front);
                return curEyeSprite;
            }
            public Eye GetFrozenEye(byte? index, bool? doBackEye) {
                Sprite curEyeSprite = GetEyeSprite(index, doBackEye);

                Eye curEye = new Eye { front = curEyeSprite, back = curEyeSprite, lightMult = 0f };

                return curEye;
            }
        }
        static bool Prefix(FacialExpression __instance) {
            FacialExpression face = __instance;
            Body body = face.body;

            FacialExpressionPause data = face.GetOrAddComponent<FacialExpressionPause>();
            if (data.originalEyeList == null) {
                data.originalEyeList = face.eyeList;
            }
            if (body.alive != data.lastAlive) {
                if (body.alive) {
                    face.eyeList = data.originalEyeList;
                    data.pausedEyeList = null;
                } else {// replaced all sprites with current
                    if (data.pausedEyeList == null) {
                        (byte index, bool doBackEye) = data.GetCurrentExpressionId();

                        if (index == 3 || index == 5 || index == 7) {//panic or cry face, reset to default
                            index = 0;
                        }

                        Eye curEye = data.GetFrozenEye(index, doBackEye);
                        data.SetPausedEye(curEye);

                        if (Multiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetBody(out NetBody nb)) {
                            NetDataWriter writer = Multiplayer.CreateNamedWriter("FacePause");
                            writer.Put(nb.netId);
                            writer.Put(index);
                            writer.Put(doBackEye);
                            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
                        }
                    }

                    face.eyeList = data.pausedEyeList;

                }
            }
            data.lastAlive = body.alive;
            return true;
        }
        [CTPostInit]
        static void FacePause() {
            Multiplayer.RegisterCustomClientReceiver("FacePause", (r) => {
                r.Get(out knetid nbId);
                r.Get(out byte index);
                r.Get(out bool doBackEye);
                if (NetBody.TryGetNetBodyFromId(nbId, out NetBody nb)) {
                    Body body = nb.body;
                    FacialExpression face = body.GetFace();
                    FacialExpressionPause data = face.GetOrAddComponent<FacialExpressionPause>();

                    Eye curEye = data.GetFrozenEye(index, doBackEye);

                    data.SetPausedEye(curEye);

                    if (!body.alive) {
                        face.eyeList = data.pausedEyeList;
                    }
                }
            });
        }
    }

    // sleepCycleSpeed is both conscious drain and unconscious regen.
    // median pull flips sign while unconscious so the lobby trends together.
    // solo sleep ramps to 3x over SoloSleepBonusRampSeconds.
    // together sleep is timescale, applied in ServerMain.HandleSleepUpdate.
    // riding on someone is scored Okay in Body_HandleGroundedState_PiggySleep.
    [HarmonyPatch(typeof(Body), "HandleBody")]
    static class Body_HandleBody_MpSleepSpeed {
        static void Prefix(Body __instance, out float __state) {
            __state = __instance.sleepCycleSpeed;
            if (!Net.IsRunning || __instance == null || !__instance.alive || !__instance.TryGetNetBody(out NetBody nb) || !nb.IsPlayer) {
                return;
            }

            float mult = 1f;
            if (ServerMain.TryGetLivingEnergyMedian(out float median)) {
                float offset = (__instance.energy - median) * 0.01f;
                if (!__instance.conscious) {
                    offset = -offset;
                }
                mult *= Mathf.Clamp(1f + offset, 0.01f, 3f);
            }

            if (!Multiplayer.rules.DisableSleep && __instance.sleeping && !__instance.conscious && !ServerMain.CheckIfEveryoneIsSleeping()) {
                float ramp = 0f;
                if (nb.soloSleepBonusStart >= 0f) {
                    ramp = Mathf.Clamp01((Time.realtimeSinceStartup - nb.soloSleepBonusStart) / ServerMain.SoloSleepBonusRampSeconds);
                }
                mult *= Mathf.Lerp(1f, 3f, ramp);
            }

            if (mult != 1f) {
                __instance.sleepCycleSpeed = __state * mult;
            }
        }

        static void Postfix(Body __instance, float __state) {
            if (__instance != null) {
                __instance.sleepCycleSpeed = __state;
            }
        }
    }

    // ragdoll ground check scores the floor under a rider. a piggyback is a fine place to sleep.
    [HarmonyPatch(typeof(Body), "HandleGroundedState")]
    static class Body_HandleGroundedState_PiggySleep {
        static void Postfix(Body __instance) {
            if (!Net.IsRunning || __instance == null || !__instance.alive) {
                return;
            }
            if (__instance.TryGetNetBody(out NetBody nb) && nb.piggybacking_on != null) {
                __instance.curSleep = Body.SleepQuality.Okay;
            }
        }
    }
}