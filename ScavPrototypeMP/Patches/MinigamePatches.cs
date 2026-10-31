using HarmonyLib;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static BandageMinigame;
using static Together.MinigameMPManager;
using Random = UnityEngine.Random;




namespace Together;




public class MinigameMPManager : CasualtiesTogetherSingleton {

    #region MINIGAME_SESSION_TYPES


    public class MinigameSession {
        public bool minigame_ended = false;
        public ushort session_id;
        public byte minigame_type;
        public float maxdist = 19;
        public bool object_required = false;
        public GameObject obj;
        public readonly HashSet<ScavPlayer> involved_players = new();
        public readonly HashSet<ScavPlayer> all_players_that_ever_were_involved = new();

        public IReadOnlyList<knetid> InvolvedPlayersClientIds(bool ignorehost = false) {
            var g = involved_players.Select(p => p.playerId).ToList();
            if (ignorehost) {
                g.Remove(Net.SERVER_CLIENTID);
            }
            return g;
        }

        public virtual void Init() {
        }
        public virtual void Update() {
        }
        public virtual void FixedUpdate() {
        }
        public virtual void OnPlayerJoin(ScavPlayer plr) {
        }
    }



    public abstract class _LimbMedicalMinigameSession : MinigameSession {
        public Limb limb => obj.GetComponent<Limb>();

        public override void Init() {
            object_required = true;
            maxdist = SharedMain.max_player_interaction_distance * 1.7f;
        }
    }

    public class ShrapnelMinigameSession : _LimbMedicalMinigameSession {
        public readonly List<Vector2> shrapnel_locations = new List<Vector2> { 
            //OG: ShrapnelMinigame.prefab V5t4
            new Vector2 (x: -248f, y: -316.85f),
            new Vector2 (x: -143f, y: -316.85f),
            new Vector2 (x: 0f, y: -316.85004f),
            new Vector2 (x: 116f, y: -316.85004f),
            new Vector2 (x: 208f, y: -316.85f),
        };

        public readonly List<(double,ScavPlayer)> last_shrapnel_movement = new();


        public override void Init() {
            base.Init();


            //OG: ShrapnelMinigame.Start V5t4 
            int num = shrapnel_locations.Count() - limb.shrapnel;
            for (int i = 0; i < shrapnel_locations.Count(); i++) {
                var pos = shrapnel_locations[i];
                pos += Random.insideUnitCircle * 20f;
                if (num > 0) {
                    num--;
                    pos += Vector2.up * 2500f;
                }
                shrapnel_locations[i] = pos;


                last_shrapnel_movement.Add((0.0, null));
            }

        }

        float synctimer = 0f;
        public override void FixedUpdate() {
            synctimer -= Time.fixedDeltaTime;

            if (synctimer <= 0f) {
                synctimer = 0.065f;

                var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameSchrapnelLocations);

                writer.Put(new LimbNetId(limb));
                writer.Put((ushort)shrapnel_locations.Count()); // guranteed 5

                for (int i = 0; i < shrapnel_locations.Count(); i++) {
                    Vector2 pos = shrapnel_locations[i];
                    var ownership = last_shrapnel_movement[i];

                    writer.Put(pos);
                    if (ownership.Item2 == null) {
                        writer.Put(false);
                    } else {
                        writer.Put(true);
                        writer.Put(ownership.Item2.playerId);
                    }

                    if (ownership.Item1 < Time.realtimeSinceStartupAsDouble && ownership.Item2 != null) {
                        ownership.Item2 = null;
                        last_shrapnel_movement[i] = ownership;
                    }

                }
                    

                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: InvolvedPlayersClientIds());
                // WRITER END;

            }





        }


        public static List<ScavPlayer> client_shrapnel_owners = new List<ScavPlayer> { null, null, null, null, null };
        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameSchrapnelLocations, false)]
        static void ClientReceiver_MinigameSchrapnelLocations(knetid _, LiteNetLib.Utils.NetDataReader reader) {

            ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = false;

            reader.Get(out LimbNetId netlimb);
            reader.Get(out ushort shracount); // guranteed 5

            if (MinigameBase.main.currentMinigame && MinigameBase.main.currentMinigame is ShrapnelMinigame sm && netlimb.TryGetNetBodyAndLimb(out var pb, out var limb) ) {

                var objects = Traverse.Create(sm).Field("objects").GetValue<List<RectTransform>>();
                var currentlyHeld = Traverse.Create(sm).Field("currentlyHeld").GetValue<RectTransform>();

                for (int i = 0; i < shracount; i++) {
                    reader.Get(out Vector2 loc);

                    var shraobj = objects[i];
                    if (currentlyHeld != shraobj) {
                        float jump = Vector2.Distance(shraobj.anchoredPosition, loc);
                        if (jump > 120f)
                            NoteSlow("shrapnel snap " + i + " by " + Mathf.RoundToInt(jump));
                        if (shraobj.anchoredPosition != loc) {
                            ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = true;
                        }
                        shraobj.anchoredPosition = loc;
                    }

                    reader.Get(out bool has_owner);

                    if (has_owner) {
                        reader.Get(out knetid ownerid);
                        client_shrapnel_owners[i] = ScavPlayer.GetPlayerFromClientId(ownerid);
                    } else {
                        client_shrapnel_owners[i] = null;
                    }
                }

            }
        }

        

        [ServerReceiver((byte)NetmsgId.SERVER_ShrapnelMinigame_MovingShrapnel)]
        static void ServerReceiver_ShrapnelMinigame_MovingShrapnel(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

            reader.Get(out byte shrap_index);
            reader.Get(out Vector2 new_pos);

            if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && Util.IsFinite(new_pos)) {

                var sess = GetSessionThisPlayerIsInvolvedIn(plr);

                // clamp it into bounds 
                //OG: MinigameBase.UpdateHandPhysics V5t4
                KM.v2clampSquare(ref new_pos, 524f);
               

                if (sess != null && sess is ShrapnelMinigameSession sms && shrap_index < sms.shrapnel_locations.Count          ) {
                    var last_ownership = sms.last_shrapnel_movement[shrap_index];
                    if ((last_ownership.Item2 == plr || last_ownership.Item1 < Time.realtimeSinceStartupAsDouble)) {
                        last_ownership.Item1 = Time.realtimeSinceStartupAsDouble + 0.15f;
                        last_ownership.Item2 = plr;
                        sms.last_shrapnel_movement[shrap_index] = last_ownership;   

                        var oldpos = sms.shrapnel_locations[shrap_index];

                        //OG: ShrapnelMinigame.Update V5t4
                        if (new_pos.y > 35f) {
                        } else {
                            new_pos = new Vector2(oldpos.x, new_pos.y);
                        }
                        if (new_pos.y < -364f) {
                            new_pos = new Vector2(new_pos.x, -364f);
                        }


                        sms.shrapnel_locations[shrap_index] = new_pos;


                        int num = 0;
                        foreach (var pos in sms.shrapnel_locations) {
                            if (pos.y < 34f) {
                                num++;
                            }
                        }
                        sms.limb.shrapnel = num;

                        if (sms.limb.shrapnel != num && sms.limb.TryGetNetBody(out var nb)) {
                            MedicalSync.Server_QueueSendCharacterHealth(nb);
                        }

                        if (num == 0) {
                            Server_EndMinigame(sess, true);
                        }

                    } else {
                        NoteSlow("shrapnel move dropped, held by " + last_ownership.Item2);
                    }

                } else {
                    NoteSlow(sess == null ? "shrapnel move dropped, no session" : "shrapnel move dropped, bad session or index " + shrap_index);
                }
            }
        }


    }

    public class DislocationMinigameSession : _LimbMedicalMinigameSession {


        public Vector2 bonepos;
        public Vector2 boneVelocity;


        public static Vector2 FinishSpot => DislocationMinigame.FinishSpot;

        public static float CalculateDislocationTimer(Vector2 bone_anchoredPosition) {
            //OG: DislocationMinigame.Start V5t4
            return Mathf.Clamp(Vector2.Distance(bone_anchoredPosition, FinishSpot) * 0.2f, 0f, 100f);
        }
        public const float dislocation_timer_minigame_end_treshholddddddddddd = 3f;
        
        public static void BonePositionTick(ref Vector2 bone_anchoredPosition, ref Vector2 boneVelocity) {
            //OG: DislocationMinigame.Start V5t4
            bone_anchoredPosition += boneVelocity * Time.fixedDeltaTime * 3.5f;
            boneVelocity = Vector2.Lerp(boneVelocity, Vector2.zero, Time.fixedDeltaTime * 3.5f);
            bone_anchoredPosition = new Vector2(Mathf.Clamp(bone_anchoredPosition.x, -500f, 900f), Mathf.Clamp(bone_anchoredPosition.y, -500f, 500f));
        }


        public override void Init() {
            base.Init();

            //OG: DislocationMinigame.Start V5t4
            bonepos = FinishSpot + DislocationMinigame.GetRandomPointOnRightQuarter() * (limb.dislocationTimer * 5f);

        }
        static float disl_timer_tolerance = 1.4f;
        float synctimer = 0f;
        public override void FixedUpdate() {
            synctimer -= Time.fixedDeltaTime;

            //OG: DislocationMinigame.Update V5t4 
            BonePositionTick(ref bonepos, ref boneVelocity);
            limb.dislocationTimer = CalculateDislocationTimer(bonepos);
            if (limb.dislocationTimer < (dislocation_timer_minigame_end_treshholddddddddddd + disl_timer_tolerance)) { // just larger tolerance
                limb.UnDislocate();
                limb.dislocationTimer = 0;
                MedicalSync.Server_QueueSendCharacterHealth(limb.GetNetBody(),true);
                Server_EndMinigame(this, false);
                obj = null; // to get deleted on next frame
            } else {
                if (synctimer <= 0f) {
                    synctimer = 0.06234456f;


                    SendBone(InvolvedPlayersClientIds(), false);

                }
            }
        }




        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameDislocationBonepos, false)]
        static void ClientReceiver_MinigameDislocationBonepos(knetid _, LiteNetLib.Utils.NetDataReader reader) {

            ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = false;

            reader.Get(out LimbNetId limb);
            reader.Get(out Vector2 bonepos);
            reader.Get(out Vector2 boneVelocity);
            // a denied hit carries one extra bool. the regular broadcast stops after the velocity.
            bool hard = reader.AvailableBytes > 0 && reader.GetBool();

            if (MinigameBase.main?.currentMinigame && MinigameBase.main.currentMinigame is DislocationMinigame dm) {

                DislocationMinigame_CheckForHit_MultiplayerPatch.just_received_new_packet = true;
                DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_bonepos = bonepos;
                DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_boneVelocity = boneVelocity;
                if (hard) {
                    // drop the local hit. the next sample is where the bone actually is.
                    MinigameMPManager.client_dislocation_mg_ignoretime = 0;
                    dm.boneVelocity = boneVelocity;
                    var root = Minigame.game != null ? Minigame.game.spawnedMiniGame : null;
                    if (root != null && root.childCount > 2 && root.GetChild(2) is RectTransform rt)
                        rt.anchoredPosition = bonepos;
                    Note("dislocation deny, bone pulled to host");
                }

            }
        }

        // unreliable sample, or a reliable deny with the host bone.
        internal void SendBone(System.Collections.Generic.IReadOnlyList<knetid> who, bool hard) {
            if (limb == null || who == null || who.Count == 0)
                return;
            var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameDislocationBonepos);
            writer.Put(new LimbNetId(limb));
            writer.Put(bonepos);
            writer.Put(boneVelocity);
            if (hard)
                writer.Put(true);
            Net.Server_SendToClients(hard ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: who);
        }

        internal void DenyBone(ScavPlayer plr) {
            if (plr == null)
                return;
            SendBone(new List<knetid> { plr.playerId }, true);
        }

    }



    public abstract class _UnlockingMinigameSession : MinigameSession {
        public BuildingEntity building => obj.GetComponent<BuildingEntity>();
        public Openable openable => obj.GetComponent<Openable>();

        public override void Init() {
            object_required = true;
            if (obj.TryGetComponent<UsableObject>(out var uo)) {
                maxdist = KeypadMinigame_CheckCasts_MultiplayerPatch.GetUsabilityDistanceMP(uo) * 1.1f;
            }
        }
        
    }

    public class KeypadMinigameSession : _UnlockingMinigameSession {
        public string match { get; protected set; }
        public string current_input = ""; // its edited from the checkinput patch, where its handled properly

        public override void Init() {
            base.Init();

            //OG: Openable.OnUse V5t4
            if (string.IsNullOrEmpty(openable.code)) {
                openable.code = KeypadMinigame.GenerateCode();
            }
            match = openable.code;

        }
        public override void OnPlayerJoin(ScavPlayer plr) {

            if (!plr.IsLocal) {
                Multiplayer.Server_SendSimpleMessageToOneClient((byte)NetmsgId.CLIENT_MinigameKeypadCode, plr.playerId, match, true);
            }
            AnnounceCurrentInput();
        }



        public void AnnounceCurrentInput() {

            var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameKeypadCurrentInput);
            writer.Put(current_input, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: InvolvedPlayersClientIds());
            // WRITER END;
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameKeypadCurrentInput, false)]
        static void ClientReceiver_MinigameKeypadCurrentInput(knetid _, LiteNetLib.Utils.NetDataReader reader) {

            reader.Get(out string code, true);

            if (MinigameBase.main.currentMinigame is KeypadMinigame kp) {
                kp.current = code;
            }
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameKeypadCode)]
        static void ClientReceiver_MinigameKeypadCode(knetid _, LiteNetLib.Utils.NetDataReader reader) {

            client_i_know_my_minigame_session = true;
            
            reader.Get(out string code, true);
           
            if (MinigameBase.main.currentMinigame is KeypadMinigame kp) {

                kp.match = code;
                if (kp.toDestroy.TryGetComponent<Openable>(out var openable)) {
                    openable.code = (code);
                }

                //OG: KeypadMinigame.Start 
                var matchText = Minigame.game.spawnedMiniGame.GetChild(0).GetChild(0).GetComponent<TextMeshProUGUI>();
                matchText.text = KeypadMinigame.AddDashes(code);
            }

        }
    }



    #endregion











    void OnGUI() {
        if (!log.verbose) return;

        var curmg = MinigameBase.main?.currentMinigame;
        if (curmg == null) return;

        // local hand info is done in the main OnGUI unctions
        if (curmg is ShrapnelMinigame sm) {

            var objects = sm.objects;
                    
            for (int i = 0; i < 5; i++) {
                var shra = objects[i];
                var pos = shra.position;
                pos = new Vector2(pos.x, Screen.height - pos.y);
                var owner = ShrapnelMinigameSession.client_shrapnel_owners[i];
                GUI.Label(new Rect(pos.x, pos.y, 200, 20), owner.ToSafeString() );
            }
        }
    }






    // server-only
    public static readonly List<MinigameSession> active_sessions = new();
    static ushort idcounter;

    static readonly Dictionary<string, float> NoteUntil = new Dictionary<string, float>();

    // one line, always. ends and denies are rare.
    internal static void Note(string why) {
        if (log.verbose) Plugin.log.LogInfo("[mg] " + (Net.IsServer ? "host " : "client ") + why);
    }

    // same line at most once a second. snaps and rejected drags would otherwise fill the log.
    internal static void NoteSlow(string why) {
        float now = Time.unscaledTime;
        int cut = why.LastIndexOf(" by ", StringComparison.Ordinal);
        string key = cut > 0 ? why.Substring(0, cut) : why;
        if (NoteUntil.TryGetValue(key, out float until) && now < until)
            return;
        NoteUntil[key] = now + 1f;
        Note(why);
    }

    // create new session or get an existing one 
    public static MinigameSession Server_GetSessionForObject<T>( GameObject obj, byte minigame_type, ScavPlayer plr_that_requested  ) where T : MinigameSession, new() {
        
        var found_sess = active_sessions.FirstOrDefault((session) => session.obj == obj && session.minigame_type == minigame_type)  ;
        if (found_sess == null) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(obj.transform.position, $"S: GetSessionForObject, CREATING NEW SESSION");

            found_sess = new T { 
                obj = obj,
                session_id = idcounter++,
                minigame_type = minigame_type
            };
            active_sessions.Add(found_sess);
            found_sess.Init();
        } else {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(obj.transform.position, $"S: GetSessionForObject, SESSION ALREADY EXISTS");
        }

        if ( plr_that_requested != null ) {
            if ( found_sess.involved_players.Count != 0 && found_sess.involved_players.Count >= Multiplayer.rules.MinigameMaxPlayers ) {

                Server_ForceEndMinigameForPlayer(plr_that_requested);
                plr_that_requested.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("minigame_deny_full"));

                return null;
            }
            found_sess.involved_players.Add(plr_that_requested);
            found_sess.all_players_that_ever_were_involved.Add(plr_that_requested);
            found_sess.OnPlayerJoin(plr_that_requested);
        }

        return found_sess;
    }
    public static MinigameSession GetSessionThisPlayerIsInvolvedIn( ScavPlayer plr_that_requested){
        return active_sessions.FirstOrDefault((session) => session.involved_players.Contains(plr_that_requested) && plr_that_requested.IsInMinigame && plr_that_requested.minigame_session == session.session_id && plr_that_requested.minigame_current_type == session.minigame_type );
    }
    public static bool TryGetSessionThisPlayerIsInvolvedIn(ScavPlayer plr_that_requested, out MinigameSession session) {
        session = GetSessionThisPlayerIsInvolvedIn(plr_that_requested);
        if (session == null) return false;
        return true;
    }
    public static bool TryGetSessionThisPlayerIsInvolvedIn<T>(ScavPlayer plr_that_requested, out T session) where T : MinigameSession {
        MinigameSession ses = GetSessionThisPlayerIsInvolvedIn(plr_that_requested);
        
        if (ses != null && ses is T nssss) {
            session = nssss;
            return true;
        }

        session = null;
        return false;
    }




    public static double client_dislocation_mg_ignoretime = 0;
    public static bool client_i_know_my_minigame_session = false;

    static byte last_sent_handimage = 2;
    static double last_sent_handpos = 0;
    public static void Client_SendHandPos() {
        var handspriteindex = GetLocalHandSpriteIndex();
        if (last_sent_handimage != handspriteindex) {
#if DEBUG
            log.l($"Sending sprite last:{last_sent_handimage} NEW:{handspriteindex} ");
#endif
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_MinigameHandSprite, handspriteindex, true);
            last_sent_handimage = handspriteindex;
        }

        if (Time.realtimeSinceStartupAsDouble - last_sent_handpos > 0.06f) {
            last_sent_handpos = Time.realtimeSinceStartupAsDouble;

            var mousepos = GetLocalMousePosMinigameSpace();


            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_MinigameHandpos, Minigame.game.handPos, mousepos, false);
        }

        var bind = KeyBinds.GetBind("attack");
        var handStartedClicking = Input.GetKeyDown(bind);
        var handStoppedClicking = Input.GetKeyUp(bind);

        if (handStartedClicking) {
            
        } 
        if (handStoppedClicking) {
            
        }
    }




    public static Vector2 GetLocalMousePosMinigameSpace() {
        
        //OG: MinigameBase.UpdateHandPhysics V5t4
        Vector2 vector = Input.mousePosition;
        vector -= (Vector2)MinigameBase.main.minigameScreen.transform.position;
        vector /= PlayerCamera.uiScale;

        KM.v2clampSquare(ref vector, 524f);
        return vector;
    }


    public static byte GetLocalHandSpriteIndex() {
        if (MinigameBase.main?.currentMinigame != null) {
            var image = MinigameBase.main.handTransform.GetComponent<Image>();
            return (byte)Array.IndexOf(MinigameBase.main.handSprites, image.sprite);
        }
        return 2;
    }
    public static bool Server_ValidateHandSprite(byte index, NetBody npc) {
        if (MinigameBase.main == null)  return false;
        if (index >= MinigameBase.main.handSprites.Length) return false;
        

        // OG: MinigameBase.UpdateHandSprite V5t4
        if (npc.body.limbs[5].dismembered) {
            if (index != 10) { // dismembered is always 10 !!
                return false;
            }
        } else {
            if (index == 10) { // dismembered
                return false;
            }
        }

        return true;
    }




    public static AnyObjectNetId GetMinigameTargetObjectNetId(Minigame mg) {
        var limbfield = Traverse.Create(mg).Field("limb");
        var limb = limbfield.GetValue<Limb>();

        var todestroyfield = Traverse.Create(mg).Field("toDestroy");
        var building = todestroyfield.GetValue<BuildingEntity>();

        AnyObjectNetId objnet = new();
        if (limb != null && limb.TryGetNetBody(out var nb)) {
            objnet = new(limb);
        } else if (building != null && building.TryGetTracker(out var tracker)) {
            objnet = new(tracker);
        }
        return objnet;
    }


    static TwoWayDictionary<Type, byte> minigame_ids;


    public static Type GetMinigameTypeFromIdNullable(byte mg) {
        if (minigame_ids.TryGet(mg, out var minigame)) {
            return minigame;
        }
        return null;
    }
    public static bool TryGetMinigameTypeFromId(byte mg, out Type minigame) {
        return minigame_ids.TryGet(mg, out minigame);
    }
    public static byte GetMinigameTypeId(Type mg) {
        return minigame_ids[mg];
    }

    void Awake() {
        minigame_ids = GenerateInheritedClassesIdDict(typeof(Minigame));
        client_dislocation_mg_ignoretime = Time.realtimeSinceStartupAsDouble;
    }


    void CleanUnusedSessions() {

        // remove all sessions with no active players 
        active_sessions.RemoveAll((sess) => {

            if (sess.obj == null) {// check if the object was deleted

                Server_EndMinigame(sess, false);
                Note("session " + sess.session_id + " dropped, target gone");
#if DEBUG
                log.l($"REMOVING SESSION BECAUSE THE OBJECT WAS DELETED {sess.session_id}");
#endif
                return true;// and delete the session
            }

            if (sess.involved_players.Count > 0) {
                return false;
            }
            Note("session " + sess.session_id + " dropped, nobody left");
#if DEBUG
            log.l($"REMOVING SESSION {sess.session_id}");
#endif
            return true;
        });
    }


    void Update () {
        if ( Util.IsInWorld() && MinigameBase.main?.currentMinigame && client_i_know_my_minigame_session) {
            MinigameMPManager.Client_SendHandPos();
        }

        if (Net.IsServer) {
            CleanUnusedSessions();

            foreach (var sess in active_sessions) {
                sess.Update();
            }
        }

    }


    void FixedUpdate() {
        if (Net.IsServer) {

            foreach (var sess in active_sessions) {
                if (sess.obj != null) {
                    foreach (var plr in sess.involved_players) {
                        if (!KM.DistanceCheck(plr.pos, sess.obj.transform.position, sess.maxdist) && plr.IsInMinigame) {
                            Server_ForceEndMinigameForPlayer(plr);
                            break; // because foreach doesnt like shit being deleted as its going through it 
                        }
                    } 
                } else if (sess.object_required) {
                    
                    Server_EndMinigame(sess, false);
                    continue ;
                    
                }

                sess.FixedUpdate();
                
            }
        }
    }

    


    public static TwoWayDictionary<Type, byte> GenerateInheritedClassesIdDict(Type base_type ) {
        
        var inherited_classes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                // dis is ai generated i hope this works 
                try {
                    return a.GetTypes();
                } catch (ReflectionTypeLoadException ex) {
                    return ex.Types.Where(t => t != null)!;
                }
            })
            .Where(t => {
                    try {
                        return t.IsClass && !t.IsAbstract && base_type.IsAssignableFrom(t);
                    } catch (Exception e) {
                        return false;
                    }
                }
            )
            .ToList()
        ;
        inherited_classes.Sort((a, b) => a.Name.CompareTo(b.Name)); // sort it alphabetically just in case to be more consistent 

        var dict = new TwoWayDictionary<Type, byte>();
        byte id = 0;

        foreach (var type in inherited_classes) {
#if !RELEASE
            Plugin.log.LogWarning($"DEV: GENERATING TYPE IDS: BASE: {base_type} -- {id} -> {type}");
#endif
            dict.Add(type, id++);
        }

        return dict;
    }


    public static void Server_EndMinigame(MinigameSession sess, bool actually_delete_session) {
        if (sess.minigame_ended) {
            return;
        }
#if DEBUG
        log.l($"running  Server_EndMinigame");
#endif
        sess.minigame_ended = true;
        Note("end session " + sess.session_id + " type " + sess.minigame_type + (actually_delete_session ? " delete" : " keep"));

        List<knetid > list = new List<knetid>();      
        
        foreach (var plr in sess.involved_players) {
            list.Add(plr.playerId);
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameForceEnd);
        writer.Put(sess.session_id);
        writer.Put(sess.minigame_type);

        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: list);
        // WRITER END;

        if (actually_delete_session) {
            active_sessions.Remove(sess);
        }
    }

    public static void Server_PlayerExitedMinigame(ScavPlayer plr) {
        plr.minigame_current_type = 255;
        plr.minigame_currentItem = null;
        plr.minigame_targetobject = new AnyObjectNetId(); // null object

        foreach (var sess in active_sessions) {
            sess.involved_players.Remove(plr);
        }
    }

    public static void Server_ForceEndMinigameForPlayer(ScavPlayer plr) {
        Note("force end " + plr.MinigameType + " session " + plr.minigame_session);

        Server_PlayerExitedMinigame(plr);

        Multiplayer.Server_SendSimpleMessageToOneClient((byte)NetmsgId.CLIENT_MinigameForceEnd, plr.playerId , (knetid)0, true);
    }

    public static void Server_SendCurrentInvolved(ScavPlayer plr, MinigameSession session) {
       
        var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameInvolvedPlayers);

        HashSet<knetid> plrids = new HashSet<knetid> { plr.playerId};

        if (session == null) {
            writer.Put(idcounter++);
            writer.Put(0);

            writer.Put((ushort)1);
            writer.Put((knetid)plr.playerId);
            writer.Put((byte)2);

        } else {
            writer.Put(session.session_id);
            writer.Put(session.minigame_type);


            List<ScavPlayer> all_possible_plrs = new();
            foreach (var iplr in session.involved_players) {
                
                all_possible_plrs.Add(iplr);
                plrids.Add(iplr.playerId);
            }


            writer.Put((ushort)all_possible_plrs.Count());
            foreach (var item in all_possible_plrs) {
                writer.Put(item.playerId);
                writer.Put(item.minigame_handsprite);

                if (writer.Length > 1000) {
                    break;
                }
            }
        }


        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: plrids.ToList());
        // WRITER END;


    }
    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameInvolvedPlayers, false)]
    static void ClientReceiver_MinigameInvolvedPlayers(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        if (Minigame.game.currentMinigame == null) {
            return;
        }

        client_i_know_my_minigame_session = true;

        reader.Get(out ushort my_session_id);
        reader.Get(out byte minigame_type);
        reader.Get(out ushort other_plrs_count);

        var localsci = ScavPlayer.LocalPlayer;
        localsci.minigame_session = my_session_id;
        // i know the type

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(localsci.pos, $"C: involved plrs count: {other_plrs_count}");

        Client_DeleteAllRemoteHands();

        for (int i = 0; i < other_plrs_count; i++) {
            reader.Get(out knetid involved_plr);
            reader.Get(out byte theirhandsprite);

            if (ScavPlayer.TryGetPlayerFromClientId(involved_plr, out var plr) && plr != localsci) {
                plr.minigame_session = my_session_id;
                plr.minigame_current_type = minigame_type;
                plr.minigame_handsprite = theirhandsprite;

                Client_CreateARemoteHand(plr);
            }
        }

    }

    class MinigameRemotePeerHand : MonoBehaviour {
        public static readonly List<MinigameRemotePeerHand> all_instances = new();
        void OnDestroy() => all_instances.Remove(this);

        public Vector2 handVelocity = Vector2.zero;

        public ScavPlayer plr;

        public RectTransform nametagTransform;
        public RectTransform handTransform;
        public Image handSprite;

        void Awake() {
            handTransform = GetComponent<RectTransform>();  
            handSprite = GetComponent<Image>();
            all_instances.Add(this);
        }

        void Update() {
            if (plr == null) {
                Destroy(gameObject);
                return;
            }
            var delta = Time.deltaTime;

            //OG: MinigameBase.UpdateHandPhysics V5t4 
            Vector2 vector = plr.minigame_mousepos;
            
            float maxLength = 75f + 10f; // changed + 10f
            float num2 = 0.25f;
            float num3 = 4f + 1f; // changed + 1f
            if (plr.body != null) {
                num3 += plr.body.skills.STRFrom10 * 0.3f;
                num3 *= plr.body.consciousness * 0.01f;
                num2 *= plr.body.consciousness * 0.01f;
            }
            float num4 = 1.5f;
            Vector2 b = Vector2.ClampMagnitude((vector - handTransform.anchoredPosition) * num2, maxLength);
            handVelocity = Vector2.Lerp(handVelocity, b, num3 * delta);
            handVelocity = Vector2.Lerp(handVelocity, Vector2.zero, delta * num4);


            handTransform.anchoredPosition += handVelocity * delta * 120f;


            handTransform.anchoredPosition = Vector2.Lerp(
                handTransform.anchoredPosition,
                plr.minigame_handpos, 
                delta
            );

            handTransform.eulerAngles = new Vector3(0f, 0f, handVelocity.x * 0.5f );

            //OG: MinigameBase.UpdateHandSprite V501 
            handTransform.localScale = new Vector3((plr.body?.handSlot == 0) ? 1f : (-1f), 1f, 1f);


            handSprite.sprite = MinigameBase.main.handSprites[plr.minigame_handsprite];

            if (nametagTransform != null) {
                nametagTransform.rotation = Quaternion.identity;
            }

        }

        void Start() {
            if (plr != null && plr.body != null) {

                UIManager.CreateLabel(handTransform, "RemoteHandPlayerName", null, null, out var go, out var text, out var rt);
                rt.sizeDelta *= 4;
                text.text = plr.playerName;
                text.color = plr.playerColor;
                rt.pivot = Vector2.zero;
                rt.sizeDelta = new Vector2(text.preferredWidth, text.preferredHeight);
                rt.anchorMin = new Vector2(0, 0.74f);
                rt.anchorMax = new Vector2(0.38f, 1.0f);
                text.richText = false;

                CharStatusVisuals.TryMakeNametagFancy(text, plr);
                CharStatusVisuals.FancyNametagUpdateColor(text, plr.playerColor);

                nametagTransform = rt;

            }
        }


    }
    public static void Client_DeleteAllRemoteHands() {

        foreach (var item in MinigameRemotePeerHand.all_instances) {
            Destroy(item.gameObject);
        }
        // the list gets cleared automatically from destroying
    }

    static void Client_CreateARemoteHand(ScavPlayer whos_hand_is_this) {

        var n = Instantiate(MinigameBase.main.handTransform.gameObject);
        var mrph = n.GetOrAddComponent<MinigameRemotePeerHand>();
        mrph.plr = whos_hand_is_this;
        mrph.handTransform.SetParent(MinigameBase.main.handTransform.parent) ;
        mrph.handTransform.localScale = MinigameBase.main.handTransform.localScale;
        mrph.handSprite.color = new Color(r: 1, g: 1, b: 1, a: 0.3f);//og Color(r: 1, g: 1, b: 1, a: 0.8235294f)

    }




    [ServerReceiver((byte)NetmsgId.SERVER_MinigameHandSprite)]
    static void ServerReceiver_MinigameHandSprite(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out byte sprindex);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && pb.body.alive ) {
            if (Server_ValidateHandSprite(sprindex, pb)) {
            } else {
                Plugin.log.LogWarning($"SUS: bogus handsprite index: {sprindex}");
                sprindex = 2;
            }

            var sess = active_sessions.FirstOrDefault((session) => session.involved_players.Contains(plr) && plr.IsInMinigame ) ;

            if (sess != null) {

                plr.minigame_handsprite = sprindex;


                var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameHandSprite_Relay);
                writer.Put(clientId);
                writer.Put(sprindex);
                Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: sess.InvolvedPlayersClientIds());
                // WRITER END;
            }
            
        }
    }
    [ServerReceiver((byte)NetmsgId.SERVER_MinigameHandpos)]
    static void ServerReceiver_MinigameHandpos(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 pos);
        reader.Get(out Vector2 mousepos);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && pb.body.alive) {
            //OG: MinigameBase.UpdateHandPhysics V5t4
            if (mousepos.x > 524f) {
                mousepos.x = 524f;
            }
            if (mousepos.y > 524f) {
                mousepos.y = 524f;
            }
            if (mousepos.x < -524f) {
                mousepos.x = -524f;
            }
            if (mousepos.y < -524f) {
                mousepos.y = -524f;
            }

            plr.minigame_handpos = pos;
            plr.minigame_mousepos = mousepos;

            var sess = active_sessions.FirstOrDefault((session) => session.involved_players.Contains(plr) && plr.IsInMinigame);

            if (sess != null) {


                var writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameHandpos_Relay);
                writer.Put(clientId);
                writer.Put(pos);
                writer.Put(mousepos);
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: sess.InvolvedPlayersClientIds());
                // WRITER END;

                
            }
        }

    }
    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameHandpos_Relay, false)]
    static void ClientReceiver_MinigameHandposRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid clientid);
        reader.Get(out Vector2 pos);
        reader.Get(out Vector2 mousepos);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientid, out var plr, out var pb) && !pb.IsLocal ) {
            plr.minigame_handpos = pos;
            plr.minigame_mousepos = mousepos;
        }
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameHandSprite_Relay, false)]
    static void ClientReceiver_MinigameHandSpriteRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid clientid);
        reader.Get(out byte spr);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientid, out var plr, out var pb) && !pb.IsLocal) {
            plr.minigame_handsprite = spr;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameForceEnd, false)]
    static void ClientReceiver_MinigameForceEnd(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(ScavPlayer.LocalPlayer.pos, $"C: Server told me to stop the minigame.");
        }

        MinigameBase.main?.EndMinigame();
        Note("server closed the minigame");
    }




    [ServerReceiver((byte)NetmsgId.SERVER_MinigameStart)]
    static void ServerReceiver_MinigameStart(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"MinigameStart received {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out byte mtype);
        reader.Get(out knetid currentitem_syncid);
        reader.Get(out AnyObjectNetId objnet);

        if (
            minigame_ids.TryGet(mtype, out var realtype) && // check if client isnt sending bogus minigame id
            ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) 
        ) {
            Server_PlayerExitedMinigame(plr);

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent((plr.pos), $"S: MinigameStart {pb}: {realtype}, objnet:{objnet}");
            if (log.verbose) log.l($"SERVER: MinigameStart {plr}: {realtype.Name}, objnet:{objnet}");

            plr.minigame_current_type = mtype;
            

            if (ItemSync.TryGetItemSyncInfo(currentitem_syncid, out var curitem_si) ) {
                if (!ItemSync.CheckIfBodyReachThisItem(curitem_si, pb.body)) {
                    log.sus($"{plr} is trying to use {curitem_si} but they cant even reach it");
                }
                plr.minigame_currentItem = curitem_si.item;
                if (curitem_si.IsAED()) {
                    //OG: AEDMinigame.Start V520dev1
                    curitem_si.item.battery.DrainCharge(0.01f);

                    ServerMain.Server_AnnounceSound(curitem_si.transformPosition, "aedstart", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));
                }
            } else {
                plr.minigame_currentItem = null;
            }


            var mgsess = _Server_CreateMinigameSession(plr, realtype, mtype, objnet, ref reader);
            if (mgsess != null) {
                plr.minigame_session = mgsess.session_id;

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(mgsess.obj.transform.position, $"S: involved plrs {String.Join(" ", mgsess.involved_players)}", Color.cyan, 4);
                }

                Server_SendCurrentInvolved(plr, mgsess);
            }

        }
    }
    static void Server_DenyMinigame(ScavPlayer plr) {
        Note("deny " + plr.MinigameType);
#if TESTING
        log.warn($"Denied minigame for {plr} - {plr.MinigameType}\n{new StackTrace()}");
#endif
        Server_ForceEndMinigameForPlayer(plr);
        plr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("minigame_deny_server"));
    }

    static MinigameSession _Server_CreateMinigameSession( ScavPlayer plr, Type realtype, byte realtype_id, AnyObjectNetId objnet, ref LiteNetLib.Utils.NetDataReader reader) {

        MinigameSession mgsess = null;
        var pb = plr.netBody;

        var is_just_cpr = realtype == typeof(CPRMinigame);
        if (realtype == typeof(AEDMinigame) || is_just_cpr) {
            if (objnet.TryGetNetBody( out var target_pb) && KM.DistanceCheck(pb.body, target_pb.body, 20) && (!is_just_cpr || CPRHandler.IsCPRBeingPerformedOnThisBody_IsThisGuyOrNobody(target_pb.body, plr) )) {
            } else {
                Server_DenyMinigame(plr);
                return mgsess;
            }
        } else if (realtype == typeof(ShrapnelMinigame) || realtype == typeof(DislocationMinigame)) {
            if (objnet.TryGetNetBodyAndLimb(out var target_pb, out var target_limb) && KM.DistanceCheck(pb.body, target_pb.body, 20)) {

                if (realtype == typeof(ShrapnelMinigame) && target_limb.shrapnel > 0) {
                    mgsess = Server_GetSessionForObject<ShrapnelMinigameSession>(target_limb.gameObject, realtype_id, plr);
                } else if (realtype == typeof(DislocationMinigame) && target_limb.dislocated) {
                    mgsess = Server_GetSessionForObject<DislocationMinigameSession>(target_limb.gameObject, realtype_id, plr);
                } else {
                    Server_DenyMinigame(plr);
                    return mgsess;
                }

            } else {
                Server_DenyMinigame(plr);
                return mgsess;
            }
        } else if (realtype == typeof(LockpingMinigame) || realtype == typeof(KeypadMinigame)) {
            if (objnet.TryGetSyncInfo(out var si) && si.go.TryGetComponent(out Openable openable) && KM.DistanceCheck(pb.body, si.go.transform, 20)) {
                if (realtype == typeof(LockpingMinigame) && !openable.isKeypad) {
                    
                } else if (realtype == typeof(KeypadMinigame) && openable.isKeypad) {
                    mgsess = Server_GetSessionForObject<KeypadMinigameSession>(si.go, realtype_id, plr);
                } else {
                    Server_DenyMinigame(plr);
                    return mgsess;
                }
            } else {
                Server_DenyMinigame(plr);
                return mgsess;
            }
        }
        plr.minigame_targetobject = objnet;
        return mgsess;
    }


    [ServerReceiver((byte)NetmsgId.SERVER_MinigameEnd)]
    static void ServerReceiver_MinigameEnd(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        Plugin.log.LogWarning($"ServerReceiver_MinigameEnd: {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif


        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) ) {

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent((plr.pos), $"S: MinigameEnd {pb}");

            var sess = GetSessionThisPlayerIsInvolvedIn(plr);
            
            Server_PlayerExitedMinigame(plr);
            if (sess != null) {
#if !RELEASE
                Plugin.log.LogWarning($"SERVER: MinigameEnd PROPERLY :) {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif
                Server_SendCurrentInvolved(plr, sess);
            } else {
                // yes this happens and its not sus 
            }
        }
    }




}




[HarmonyPatch(typeof(HandCrankMinigame), nameof(HandCrankMinigame.PhysicsUpdate))]
public static class HandCrankMinigame_PhysicsUpdate_MultiplayerPatch {

    public static float crankProgress = 0;

    public const float CRANK_SEND_RATE = 5f;

    //OG: HandCrankMinigame.PhysicsUpdate V7.0.1
    public const float CRANK_CHARGE_AMOUNT = 3.3E-05f;
    public const float CRANK_STAMINA_USE = 0.015f;


    static void Prefix(HandCrankMinigame __instance) {
        if (!Net.IsRunning) return;
        if (!__instance.held) return;

        float z = __instance.crank.localEulerAngles.z;
        var new_crank_localRotation = HandCrankMinigame.GetAngleFromVector(Minigame.game.handPos.normalized);
        float num = Mathf.Abs(Mathf.DeltaAngle(new_crank_localRotation, z));

        crankProgress += num;

        if (crankProgress > CRANK_SEND_RATE && NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem, out var si) && Net.IsClient) {
            var writer = Net.CreateWriter(NetmsgId.SERVER_HandCrankMinigame);
            writer.Put(si.syncId);
            writer.Put(crankProgress);
            crankProgress = 0;
            Net.Client_Send(DeliveryMethod.Unreliable, writer);
        }

    }



    [ServerReceiver((byte)NetmsgId.SERVER_HandCrankMinigame)]
    static void ServerReceiver_HandCrankMinigame(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid syncid);
        reader.Get(out float crankProgress);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && ItemSync.TryGetItemSyncInfo(syncid, out var si) && ItemSync.CheckIfBodyReachThisItem(si, pb.body, 20, false) && crankProgress.IsFinite()) {
            if (si.item.IsHandcrank()) {
                si.item.battery.DrainCharge(-(crankProgress * CRANK_CHARGE_AMOUNT));
                pb.body.stamina -= crankProgress * CRANK_STAMINA_USE;
                si.item.condition = Mathf.Clamp01(si.item.condition);
            } else {
                MinigameMPManager.NoteSlow("hand crank dropped, not a crank");
            }
        } else {
            MinigameMPManager.NoteSlow("hand crank dropped, reach or sync");
        }
    }

}


[HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.GetEstimationRange))]
static class ManualDefibMinigame_GetEstimationRange_MultiplayerPatch {
    static bool Prefix(ManualDefibMinigame __instance, ref float __result) { //Rewrite 
        float num = 70f - Util.GetLocalBody().skills.INTFrom10 * 5f;
        if (num < 5f) {
            num = 5f;
        }
        __result = num;
        return false;
    }
}




// remove the megalovania explosion
[HarmonyPatch(typeof(KeypadMinigame), nameof(KeypadMinigame.CheckForMegalovania))]
static class abc {
    static bool Prefix(KeypadMinigame __instance) {
        return false;
    }
}


// called from PlayerCamera and anyone calling useLimbAction
[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.StartMinigame))]
public static class MinigameBase_StartMinigame_MultiplayerPatch {

    public static byte selfharmminigame_last_cuts_count = 0;
    public static bool ignore_next_mgstart_cuz_its_not_me = false;
    public static Dictionary<string, OnBandageUse> known_bandages_onuse_lambdas = new Dictionary<string, OnBandageUse>();

    static void ResetMinigameOverriderStates() {
        DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_bonepos = new Vector2(500, 0);
        DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_boneVelocity = Vector2.zero;

        AmputationMinigame_PhysicsUpdate_MultiplayerPatch.last_cutProgress = 0;
        MinigameMPManager.client_i_know_my_minigame_session = false;
        for (int i = 0; i < MinigameMPManager.ShrapnelMinigameSession.client_shrapnel_owners.Count; i++) {
            MinigameMPManager.ShrapnelMinigameSession.client_shrapnel_owners[i] = null;
        }
    }

    static bool Prefix(MinigameBase __instance, Minigame minigame, Item item, ref bool __state) {

        __state = __instance.currentMinigame == null;

        AnyObjectNetId objnet = MinigameMPManager.GetMinigameTargetObjectNetId(minigame);
		var is_medical_minigame = objnet.TryGetLimb(out var limb);

		if (is_medical_minigame) {
			if (minigame is BandageMinigame bmini) {
				known_bandages_onuse_lambdas[item.id] = bmini.OnUse;
				BandageMinigame_DoBandageAction_MultiplayerPatch.bandagethingcounter = 14; // to make one sound as soone as the player starts rolling
			}
		}

		if (!Net.IsRunning) return true;

        if (__instance.currentMinigame != null) {
            return false;// the OG
        }
        

        
        // after the bandage has been registered, we can abort the minigame if its not local
        if (ignore_next_mgstart_cuz_its_not_me) {
            ignore_next_mgstart_cuz_its_not_me = false;
            return false;
        }

        if (is_medical_minigame) {
            // these are guranteed to be called only locally from playercamera
            var is_non_item_medical_minigame = is_medical_minigame && ((minigame is ShrapnelMinigame) || (minigame is DislocationMinigame) || (minigame is CPRMinigame));
            if (is_non_item_medical_minigame) {
                ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = false;
                PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_on_limb = limb;
            } else if (
                PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_on_limb == null
            ) {
                //most likely a remote peer opened the game (now it prevents amputationminigame phantom-opening) 
#if !RELEASE
                Plugin.log.LogInfo($"most likely a remote peer opened the game!!!\n minigame: {minigame}\nSTACK: {new StackTrace()}");
#endif 
                return false;
            }



            if (minigame is SyringeMinigame smg) {

                WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb = limb;
                WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount = 0;
                SyringeMinigame_Update_MultiplayerPatch.last_fill_amount = item.condition;
                SyringeMinigame_Update_MultiplayerPatch.last_was_different = false;
                SyringeMinigame_Update_MultiplayerPatch.is_start_or_end = true;
                SyringeMinigame_Update_MultiplayerPatch.fill_milestone_timer = SyringeMinigame_Update_MultiplayerPatch.SYRINGE_SEND_RATE;

            } else if (minigame is DislocationMinigame dmini) {

            } else if (minigame is AmputationMinigame amini) {
                if (PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_item != null) {
                    if (NetObjectRegistry.TryGetSyncInfo(PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_item, out var si)) {
                        AmputationMinigame_PhysicsUpdate_MultiplayerPatch.cutter_item = PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_item;
                    } else {
                        NetObjectRegistry.AlertObjectNotRegistered(true);
                        return false;
                    }
                } else {
                    return false;
                }
                
            }

        } 

        ResetMinigameOverriderStates();
        return true;
    }

    static void Postfix(MinigameBase __instance, Minigame minigame, Item item, ref bool __state) {
        if (!Net.IsRunning) return;

#if !RELEASE
        if (__instance.currentMinigame != null) {
            Plugin.log.LogInfo($"MinigameBase.StartMinigame: STARTED MINIGAME!!!\n__instance.currentMinigame: {__instance.currentMinigame}\nSTACK: {new StackTrace()}");
        }
#endif

        if (__instance.currentMinigame && !Net.IsDedicatedServer) {

            var localsci = ScavPlayer.LocalPlayer;
            localsci.minigame_current_type = MinigameMPManager.GetMinigameTypeId(minigame.GetType());

            AnyObjectNetId objnet = MinigameMPManager.GetMinigameTargetObjectNetId(minigame);
            var is_medical_minigame = objnet.TryGetLimb(out var limb);
            
            bool doSend = __state;
            
            if (doSend) {

                if (minigame is KeypadMinigame mg) {
                    if (Net.IsClient) {
                        mg.match = "-- LOADING --";
                    }
                }
                knetid currentitem_syncid = 0;
                if (MinigameBase.main.currentItem != null ) {
                    if (NetObjectRegistry.TryGetSyncInfo(MinigameBase.main.currentItem, out var curitemsi)) {
                        currentitem_syncid = curitemsi.syncId;
                    } else {
                        NetObjectRegistry.AlertObjectNotRegistered(true);
                    }
                }

                var writer = Net.CreateWriter(NetmsgId.SERVER_MinigameStart);
                writer.Put(localsci.minigame_current_type);
                writer.Put(currentitem_syncid);
                writer.Put(objnet);

                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                // WRITER END;
            }
        }
    }
}



[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.EndMinigame))]
public static class MinigameBase_EndMinigame_MultiplayerPatch {
    static bool Prefix(MinigameBase __instance) {
#if !RELEASE
        if (__instance.currentMinigame != null) {
            Plugin.log.LogInfo($"MinigameBase.EndMinigame: __instance.currentMinigame: {__instance.currentMinigame}\nSTACK: {new StackTrace()}");
        }
#endif

        MinigameMPManager.Client_DeleteAllRemoteHands();
        MinigameMPManager.client_i_know_my_minigame_session = false;

        if (Net.IsRunning) {
            
            try {
                if ((bool)__instance.currentMinigame) {
                    if (__instance.currentMinigame is SelfHarmMinigame sharm) {
                        Body harmBody = YOU_SHOULD_KILL_YOURSELF_NOOOW.selfHarmMinigameBody;
                        // a stale suicide on somebody else's body still closes through this ui. do not claim we were the victim.
                        if (harmBody == null || Util.IsBodyLocal(harmBody)) {
                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SelfHarmMinigameMinigameEnd, (byte)(sharm.cutsDone == 5 ? 0 : (sharm.isEye ? 1 : 2)), true);
                        }

                    } else if (Net.IsClient) {

                        if (__instance.currentMinigame is KeypadMinigame kmini) {
                            if (kmini.current == kmini.match && kmini.toDestroy) {
                                // now its handled by new system
                            }
                        } else if (__instance.currentMinigame is LockpingMinigame lmini) {
                            if (lmini.lockProgress >= 1f && lmini.toDestroy && lmini.toDestroy.GetHealth() == 0f) {
                                if (NetObjectRegistry.TryGetSyncInfoOrRegister(lmini.toDestroy.gameObject, out var si)) {
                                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_LockpickingMinigame_Finish_success, si.syncId, true);
                                    EvidenceBag.Arm();
                                    EvidenceBag.Line("lockpick client sent sync=" + si.syncId + " " + EvidenceBag.Building(lmini.toDestroy));
                                } else {
                                    EvidenceBag.Line("lockpick client no sync " + EvidenceBag.Building(lmini.toDestroy));
                                }
                            } else if (lmini.toDestroy != null && (lmini.lockProgress >= 1f || lmini.toDestroy.GetHealth() <= 0.5f)) {
                                EvidenceBag.Line("lockpick client held progress=" + lmini.lockProgress.ToString("0.##") + " " + EvidenceBag.Building(lmini.toDestroy));
                            }
                        } else if (__instance.currentMinigame is SyringeMinigame smgmini) {
                            var l = WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb;
                            var pb = l.body.GetComponent<NetBody>();
                            if (l.shrapnel == 1 && l.pain > 20f) {
                                if (NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem.gameObject, out var si)) {
                                    SyringeMinigame_Update_MultiplayerPatch.Client_SendCurrentInjectedAmount(true);
                                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SyringMinigame_BrokeNeedle, si.syncId, pb.netId, Util.GetLimbIndex(l), true);
                                }
                            }
                        } else if (__instance.currentMinigame is DislocationMinigame dmini) {

                            if (Net.IsClient && !dmini.limb.dislocated) {
                                var writer = Net.CreateWriter(NetmsgId.DislocationFixSuccess);
                                writer.Put(new LimbNetId(dmini.limb));
                                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                            }

                        } else if (__instance.currentMinigame is AmputationMinigame amini) {
                            
                            if (Net.IsClient && amini.cutProgress >= 1 && NetObjectRegistry.TryGetSyncInfo(AmputationMinigame_PhysicsUpdate_MultiplayerPatch.cutter_item, out var si)) {

                                var writer = Net.CreateWriter(NetmsgId.SERVER_AmputationMinigame_success);
                                writer.Put(si.syncId);
                                writer.Put(new LimbNetId(amini.limb));
                                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                                // WRITER END;
                            }
                            AmputationMinigame_PhysicsUpdate_MultiplayerPatch.cutter_item = null;
                        }


                    }

                }

            } catch (Exception e) {
                log.error($"EndMinigame patch: {e.ToString()}");
            }

            var writer2 = Net.CreateWriter(NetmsgId.SERVER_MinigameEnd);
            writer2.Put(true);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer2);

        }


        return true;
    }



    
    

    [ServerReceiver((byte)NetmsgId.SERVER_SelfHarmMinigameMinigameEnd)]
    static void Server_SelfHarmMinigameMinigameEnd(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {


        reader.Get(out byte type);

#if !RELEASE
        Plugin.log.LogWarning($"Server_SelfHarmMinigameMinigameEnd {DebugHelp.GetPlayerFullDebugString(clientId)}: TYPE: {type} ");
#endif 

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId,out var plr, out var body) && body.alive && body.totalHappiness < -50) {

            Limb limb = null;
            YOU_SHOULD_KILL_YOURSELF_NOOOW.SelfHarm_Copypasted(type, body, ref limb);

            var writer = Net.CreateWriter(NetmsgId.CLIENT_SelfHarmRelay);
            writer.Put(type);
            writer.Put(new LimbNetId(limb));
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
            // WRITER END;

            MedicalSync.Server_QueueSendCharacterHealth(plr.netBody, true);

        }
    }

    public static bool CanAmputate(this Limb limb) {
        if (limb.body.IsDeadOrCriticallyDying() || Multiplayer.rules.AmputateHealthyPlayers) {
            return CombatStuff.CheckIfItsOkeyToDismemberThatLimb(limb);
        } else {
            //OG: Item.DoAmputate V5t5
            bool anyLegGone = false;
            for (int i = 9; i < 15; i++) {
                if (limb.body.limbs[i].dismembered) {
                    anyLegGone = true;
                    break;
                }
            }

            if (limb.infectionAmount > 60f && Array.IndexOf(limb.body.limbs, limb) > 2 && (!anyLegGone || !limb.isLegLimb)) {
                return true;
            }
            return false;
        }
    }
    
    [ServerReceiver((byte)NetmsgId.SERVER_AmputationMinigame_success)]
    static void Server_AmputationMinigame_success(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid item_syncid);
        reader.Get(out LimbNetId netlimb);

#if !RELEASE
        Plugin.log.LogWarning($"Server_AmputationMinigame_success {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif 

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, netlimb.bodyNetId, netlimb.limbId,item_syncid, out var healer, out var target, out var si)
            && si.CanBeUsedToAmputate()
        ) {
            
            Limb limb = netlimb.GetLimb();

            if (limb.CanAmputate() && 
                limb.muscleHealth < 70f// just for sure check if the cutting actually started
            ) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(limb.transform.position, $"S: AmputationMinigame_success {healer} -> {target} {si}");


                AmputationMinigame_Update_MultiplayerPatch.AmputationMinigame_FinishAmputateCopypasted(limb);


                target.OnDamageEvent(new BodyDamageEvent(healer.netBody, target, si.item, 100));
                

                if (limb.isHead) 
                    healer.body.happiness -= Mathf.Min(10, Multiplayer.rules.PVPMoodDebuff * 10);

                return;
            }

        }
    }

    

    [ServerReceiver((byte)NetmsgId.DislocationFixSuccess)]
    static void Server_DislocationFixSuccess(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out LimbNetId netlimb);

#if TESTING
        log.l($"DislocationFixSuccess {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif
        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var healer) && netlimb.TryGetNetBodyAndLimbSafe(out var n,out var limb)) {
            var sess = GetSessionThisPlayerIsInvolvedIn(plr);
            if (sess != null && sess is DislocationMinigameSession dms && dms.obj != null && dms.limb == limb) {
                if (
                    limb.dislocated && limb.dislocationTimer < 10
                ) {
                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(limb.transform.position, $"S: DislocationFixSuccess {healer} -> {netlimb}");

                    limb.UnDislocate();

                    return;
                }
            }
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_SyringMinigame_BrokeNeedle)]
    static void Server_SyringMinigame_BrokeNeedle(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if !RELEASE
        Plugin.log.LogWarning($"Server_SyringMinigame_BrokeNeedle {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif 

        reader.Get(out knetid needleid);
        reader.Get(out knetid targetplr);
        reader.Get(out byte limbid);

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, targetplr, limbid, needleid, out var plr, out var targetsci, out var si)) 
        {
            var limb = targetsci.body.limbs[limbid];

            if (limb.shrapnel == 1) {//needle was already broken
                return; 
            }

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: broke needle oops {plr.playerName} -> {targetsci.playerName} {si}");


            if (!plr.IsLocal) { 
                //OG: SyringeMinigame.Update   V5t4   it ends mingame
                limb.shrapnel = Math.Max(limb.shrapnel, 1);
                limb.pain += 20f;
            }


            ServerMain.Server_AnnounceSound(targetsci.body.transform.position, "bullethit", ServerMain.GetListOfClientIdsExceptThis(clientId));

        }
    }




    [ServerReceiver((byte)NetmsgId.SERVER_LockpickingMinigame_Finish_success)]
    static void Server_LockpickingMinigame_Finish_success(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        

        reader.Get(out knetid building_syncid);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)) {
        
            if ( pb.body.conscious && NetObjectRegistry.TryGetSyncInfo (building_syncid,out var si)
                && si.go.TryGetComponent<Openable>(out var openable) && KM.DistanceCheck(pb.body, si.go.transform, 20) &&
                !openable.isKeypad
                ) {
                // fuck verification, let cheaters cheat
                si.building.SetHealth(0f);
                EvidenceBag.Arm();
                EvidenceBag.Line("lockpick server ok client=" + clientId + " " + EvidenceBag.Building(si.building));
        
                ServerMain.Server_AnnounceSound(si.transformPosition, "unlock", ServerMain.AllClientIds);
        
            } else {
                EvidenceBag.Line("lockpick server deny client=" + clientId
                    + " conscious=" + (pb != null && pb.body != null && pb.body.conscious)
                    + " sync=" + (NetObjectRegistry.TryGetSyncInfo(building_syncid, out var denied) ? denied.syncId.ToString() : "none"));
                MinigameMPManager.Note("lockpick denied");
                plr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_lockpick"));
            }
        }
    }


}








[HarmonyPatch(typeof(LockpingMinigame), nameof(LockpingMinigame.Update))]
public static class LockpingMinigame_Update_MultiplayerPatch {

    static void Prefix(LockpingMinigame __instance) {
        if (!Net.IsRunning) return;

        if (__instance.clickingInside  ) {

            //OG: LockpingMinigame.Update V501 
            if (__instance.lockProgress >= __instance.MaxTurnProgress()) {
                
                if (__instance.lockProgress >= 1f ) {
                    return;
                }
                var timeWasStuckField = Traverse.Create(__instance).Field("timeWasStuck");
                var timeWasStuck = timeWasStuckField.GetValue<float>();
                if (timeWasStuck + Time.deltaTime > 0.5f) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_LockpingMinigame_breakclaw,  false);

                    //timeWasStuck = 0f; // its guranteed to reset in the og script
                }
            } 
        }


        // let the server session handler open the lock, because if i let client complete this, they will exit the minigame earlier than the server counts it up on their own

        //OG: LockpingMinigame.Update V501 
        var maxprogress = 1f - (Time.deltaTime * (0.66f + (__instance.pickLevel + 1) * 0.065f * 1.1f));
        if (__instance.lockProgress > maxprogress && __instance.toDestroy != null && NetObjectRegistry.TryGetSyncInfoOrRegister(__instance.toDestroy, out var si)) {
            
        }
            
        

        
    }
   


    [ServerReceiver((byte)NetmsgId.SERVER_LockpingMinigame_breakclaw)]
    static void Server_LockpingMinigame(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var lockpicker) && lockpicker.conscious 
        ) {
            ServerMain.Server_AnnounceSound(lockpicker.slots[lockpicker.handSlot].limb.transform.position, $"gore2", ServerMain.GetListOfClientIdsExceptThis(clientId));

            //OG: Openable.OnUse V501
            lockpicker.FindByIdSurface("lockpickingkit", out var it);
            if (lockpicker.skills.INT < 10) { // its kinda kinda ? kinda guranteed? that theyre lockpicking with a lockipck??
                it = null;
            }
            //OG: LockpingMinigame.Update V501  where it damages the claw
            if (it != null) { // this is pretty much reliant on Openable.OnUse
                it.SetCondition(it.condition - 0.03f);
            } else {
                lockpicker.slots[lockpicker.handSlot].limb.pain += 20f;
                lockpicker.clawHealth -= 15f;
            }

        }
    }


}






[HarmonyPatch(typeof(DislocationMinigame), nameof(DislocationMinigame.CheckForHit))]
public static class DislocationMinigame_CheckForHit_MultiplayerPatch {

    public static bool just_received_new_packet = false;
    public static Vector2 lastreceived_bonepos =        new Vector2(500, 0);
    public static Vector2 lastreceived_boneVelocity =   Vector2.zero;

    public static Vector2 real_bonepos = new Vector2(500, 0);
    public static Vector2 boneVelocity = Vector2.zero;

    public static RectTransform bone => Minigame.game.spawnedMiniGame.GetChild(2).GetComponent<RectTransform>();
    


    class laststate {
        public bool CheckForHitActuallyRuns;

        public Vector2 lastvel;
    }


    static void LimitBoneLocationForMP() {

        var basedmt = (Minigame.game.currentMinigame as DislocationMinigame);
       
        var pos = bone.anchoredPosition;
        var vel = basedmt.boneVelocity;

        MinigameMPManager.DislocationMinigameSession.BonePositionTick(ref pos, ref vel);
        var new_dislocationTimer = MinigameMPManager.DislocationMinigameSession.CalculateDislocationTimer(pos);

        if (new_dislocationTimer <= MinigameMPManager.DislocationMinigameSession.dislocation_timer_minigame_end_treshholddddddddddd) {

            var normal = (pos - MinigameMPManager.DislocationMinigameSession.FinishSpot);
            var magnitude = normal.magnitude;
            if (magnitude > 1E-05f) {
                normal /= magnitude;
            } else {
                normal = Vector2.zero;
            }

            bone.anchoredPosition = MinigameMPManager.DislocationMinigameSession.FinishSpot + normal * 3.01f;

            var reflected_velocity = basedmt.boneVelocity - (normal * Vector2.Dot(basedmt.boneVelocity, normal));
            basedmt.boneVelocity = (reflected_velocity);

        }
    }


    static void Prefix(DislocationMinigame __instance, ref laststate __state) {
        if (!Net.IsRunning) return;


        var limb = __instance.limb;
        boneVelocity = __instance.boneVelocity;

        if (!limb.IsBodyLocal() && limb.body.averagePain > 75 && Util.GetLocalBody().averagePain < 75) { // allow 
            limb.body.averagePain = 74;
        }

        real_bonepos = bone.anchoredPosition;

        __state = new laststate {
            CheckForHitActuallyRuns = limb.body.averagePain > 75f || boneVelocity.magnitude > 60f || !Minigame.game.handClicking || Minigame.game.handVelocity.magnitude < 4f,
            lastvel = boneVelocity
        };


    }
    static void Postfix(DislocationMinigame __instance, ref laststate __state) {
        if (!Net.IsRunning) return;




        var basetraverse = (MinigameBase.main);
        var forceUngrab = basetraverse.forceUngrab;
        var firstFrameForceUngrab = basetraverse.firstFrameForceUngrab;
        if ( forceUngrab && firstFrameForceUngrab) {// check if ForceUngrab just ran
                
            if (!__state.CheckForHitActuallyRuns) {
                    
                boneVelocity = __instance.boneVelocity;// get new bone velocity potentially after moving it


                var diff = boneVelocity - __state.lastvel;

#if !RELEASE
                Plugin.log.LogWarning($"Hit the bone {diff} ");
#endif 

                var writer = Net.CreateWriter(NetmsgId.SERVER_DislocationMinigame_dislocatingtypeshit);

                writer.Put(diff);
                writer.Put(__instance.hasWrench);// fuck it, im tired dawg, let cheaters cheat

                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                // WRITER END;


                if (Net.IsClient) {
                    MinigameMPManager.client_dislocation_mg_ignoretime = Time.realtimeSinceStartupAsDouble + ClientMain.RTT_IN_SECONDS_with_margin + 1.0;
                }
            }
        }

        var timediff =  MinigameMPManager.client_dislocation_mg_ignoretime - Time.realtimeSinceStartupAsDouble;
        if (timediff > 0) {
            var tolistenscale = Mathf.Clamp01(1.0f - (float)timediff);

            if (just_received_new_packet) {
                float jump = Vector2.Distance(bone.anchoredPosition, lastreceived_bonepos);
                if (jump > 40f)
                    MinigameMPManager.NoteSlow("dislocation snap by " + Mathf.RoundToInt(jump));
                just_received_new_packet = false;

                bone.anchoredPosition = Vector2.Lerp(bone.anchoredPosition, lastreceived_bonepos, tolistenscale);
            } else {
                bone.anchoredPosition = (Vector2.Lerp(bone.anchoredPosition, lastreceived_bonepos, tolistenscale * Time.deltaTime * 0.5f));
            }
            __instance.boneVelocity = (Vector2.Lerp(__instance.boneVelocity, lastreceived_boneVelocity, tolistenscale * Time.deltaTime));

            } else {
            if (just_received_new_packet) {
                float jump = Vector2.Distance(bone.anchoredPosition, lastreceived_bonepos);
                if (jump > 40f)
                    MinigameMPManager.NoteSlow("dislocation snap by " + Mathf.RoundToInt(jump));
                just_received_new_packet = false;

                bone.anchoredPosition = lastreceived_bonepos;
                __instance.boneVelocity = (lastreceived_boneVelocity);
            } else {
                bone.anchoredPosition = (Vector2.Lerp(bone.anchoredPosition, lastreceived_bonepos, Time.deltaTime ));
            }
        }
        MinigameMPManager.DislocationMinigameSession.BonePositionTick(ref lastreceived_bonepos, ref lastreceived_boneVelocity);




        LimitBoneLocationForMP();

    }

    [ServerReceiver((byte)NetmsgId.SERVER_DislocationMinigame_dislocatingtypeshit)]
    static void Server_DislocationMinigame_dislocatingtypeshit(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 newvel);
        reader.Get(out bool haswrench);

#if !RELEASE
        Plugin.log.LogWarning($"Server_DislocationMinigame_dislocatingtypeshit {DebugHelp.GetPlayerFullDebugString(clientId)} newvel:{newvel}  wrench:{haswrench}");
#endif

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var healerbody) && healerbody.conscious &&
            Util.IsFinite(newvel)
        ) {
            if (MinigameMPManager.TryGetSessionThisPlayerIsInvolvedIn<MinigameMPManager.DislocationMinigameSession>(plr, out var dms)  &&
                dms.boneVelocity.magnitude <= 60f  //OG: DislocationMinigame.CheckForHit V5t4
            ) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: dislocation hit: {newvel}");

                // hand vel cant really go above somewhere about 50
                // so this velocity nudge is like  ,  so *1.3*20 its about 1300

                dms.boneVelocity += Vector2.ClampMagnitude(newvel,1300f);

                if (!plr.IsLocal) {

                    //OG: DislocationMinigame.CheckForHit V5t4  
                    if (haswrench) {
                        dms.limb.pain += UnityEngine.Random.Range(4f, 10f);
                    } else {
                        dms.limb.pain += UnityEngine.Random.Range(15f, 24f);
                        if (UnityEngine.Random.value > 0.995f) {
                            dms.limb.BreakBone();
                        }
                    }
                }

                ServerMain.Server_AnnounceSound(dms.limb.transform.position, $"boneHit", ServerMain.GetListOfClientIdsExceptThis(clientId));

            } else if (MinigameMPManager.TryGetSessionThisPlayerIsInvolvedIn<MinigameMPManager.DislocationMinigameSession>(plr, out var fast) && fast.boneVelocity.magnitude > 60f) {
                MinigameMPManager.NoteSlow("dislocation hit dropped by speed " + Mathf.RoundToInt(fast.boneVelocity.magnitude));
                fast.DenyBone(plr);
            } else {
                MinigameMPManager.NoteSlow("dislocation hit dropped, no session");
            }
        }
    }


}







[HarmonyPatch(typeof(ShrapnelMinigame), nameof(ShrapnelMinigame.Update))]
public static class ShrapnelMinigame_Update_MultiplayerPatch {

    public static bool last_packet_was_a_change = false;
    static float synctimer = 0f;

    static RectTransform last_held = null;

    public static void Prefix (ShrapnelMinigame __instance, List<RaycastResult> uiCasts, ref float __state) {
        if (!Net.IsRunning) return;

        var shtraverse = Traverse.Create(__instance);
        var l = shtraverse.Field("limb").GetValue<Limb>();
        if (l == null) { // then theres nothing to do brah
            return;
        }


        var objects = shtraverse.Field("objects").GetValue<List<RectTransform>>();
        var currentlyHeld = shtraverse.Field("currentlyHeld").GetValue<RectTransform>();

        bool forcesend_important = false;
        if (currentlyHeld != null) {

            byte shraindex = (byte)objects.IndexOf(currentlyHeld);
            var owner = MinigameMPManager.ShrapnelMinigameSession.client_shrapnel_owners[shraindex];

            if (owner == null || owner == ScavPlayer.LocalPlayer) {

                synctimer -= Time.deltaTime;

                // force send now if i just took out the last shrapeneeell
                if (currentlyHeld.anchoredPosition.y < 35f) {
                    var heldOffset = shtraverse.Field("heldOffset").GetValue<Vector2>();
                    if (Minigame.game.handPos.y + heldOffset.y > 35f) {
                        currentlyHeld.anchoredPosition = Minigame.game.handPos + heldOffset; // to gurantee that i will not spam this message every frame
                        synctimer = 0f;
                        forcesend_important = true;
                    }
                }
            } else { // someone else already holds the shrapnel, dont allow me to grab it too 
                NoteSlow("shrapnel ungrab, owner " + owner);
                currentlyHeld = null;
                Minigame.game.ForceUngrab();
            }


        } else {
            if (last_held != null) {
                synctimer = 0f;
                forcesend_important = true;
            }
        }

        if (synctimer <= 0f && currentlyHeld != null) {
            synctimer = 0.05f;
            if (l.TryGetNetBody(out var target_pb)) {

                byte shraindex = (byte)objects.IndexOf(currentlyHeld);

                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_ShrapnelMinigame_MovingShrapnel, shraindex, currentlyHeld.anchoredPosition, forcesend_important);
                
            }
        }

        var source = Minigame.game.spawnedMiniGame.GetComponent<AudioSource>();
        __state = source.volume;

        last_held = currentlyHeld;
        
    }
    public static void Postfix(ShrapnelMinigame __instance, List<RaycastResult> uiCasts, ref float __state) {
        if (!Net.IsRunning) return;


        //source.volume = Mathf.MoveTowards(source.volume, ((bool)currentlyHeld && Mathf.Abs(Minigame.game.handVelocity.y) > 0.2f && currentlyHeld.anchoredPosition.y <= 35f) ? 1f : 0f, Time.deltaTime * 5f); // OG

        var currentlyHeld = Traverse.Create(__instance).Field("currentlyHeld").GetValue<RectTransform>();

        if (currentlyHeld == null ) {
            var source = Minigame.game.spawnedMiniGame.GetComponent<AudioSource>();
            source.volume = Mathf.MoveTowards(__state, (last_packet_was_a_change) ? 1f : 0f, Time.deltaTime * 5f);
        }

    }

}

[HarmonyPatch(typeof(ShrapnelMinigame), nameof(ShrapnelMinigame.BreakGrasp))]
public static class ShrapnelMinigame_BreakGrasp_MultiplayerPatch {

    public static void Prefix(ShrapnelMinigame __instance) {
        if (!Net.IsRunning) return;

        var basetrav = Traverse.Create(__instance);
        var currentlyHeld = basetrav.Field("currentlyHeld").GetValue<RectTransform>();
        if (currentlyHeld != null) {
            var l = basetrav.Field("limb").GetValue<Limb>();
            if (l != null && l.TryGetNetBody(out var pb)) {

                var writer = Net.CreateWriter(NetmsgId.SERVER_ShrapnelMinigame_BreakGrasp);
                writer.Put(new LimbNetId(l));
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                // WRITER END;

            }
        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_ShrapnelMinigame_BreakGrasp)]
    static void Server_ShrapnelMinigame_BreakGrasp(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out LimbNetId limbnetid);

        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var healerbody) && healerbody.conscious &&
            limbnetid.TryGetNetBodyAndLimbSafe(out var target_nb, out var limb) &&
            Util.DoFullInteractionCheck(healerbody, target_nb.body, false, SharedMain.max_player_interaction_distance * 1.5f) 
        ) {
            if (limb.shrapnel > 0) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: failed shrapnel {plr} -> {limbnetid}");

                if (!plr.IsLocal) {
                    //OG: ShrapnelMinigame.BreakGrasp 
                    limb.skinHealth -=  UnityEngine.Random.Range(4f, 6f);
                    limb.bleedAmount += UnityEngine.Random.Range(0.4f, 1f);
                    limb.pain += UnityEngine.Random.Range(9f, 16f);
                }
                ServerMain.Server_AnnounceSound(target_nb.position, $"gore{UnityEngine.Random.Range(1, 6)}", ServerMain.GetListOfClientIdsExceptThis(clientId));

                MedicalSync.Server_QueueSendCharacterHealth(target_nb);
                return;
            }
            // sus
        }
    }


}







[HarmonyPatch(typeof(SyringeMinigame), nameof(SyringeMinigame.Update))]
public static class SyringeMinigame_Update_MultiplayerPatch {
    public static float last_fill_amount = 0;
    public static bool last_was_different = false;

    public static bool is_start_or_end = false;

    public const float SYRINGE_SEND_RATE = 0.5f;
    public static float fill_milestone_timer = SYRINGE_SEND_RATE;


    public static void Client_SendCurrentInjectedAmount(bool force_sound) {

        var limb = WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb;
        bool item_synced = ItemSync.TryGetSyncInfo(Minigame.game.currentItem, out var si);

        if (item_synced && WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount > 0) {

            fill_milestone_timer = SYRINGE_SEND_RATE;
#if !RELEASE
            Plugin.log.LogWarning($"Client_SendCurrentInjectedAmount SENDING AMOUNT: {WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount}  {WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb}");
#endif


            var writer = Net.CreateWriter(NetmsgId.SERVER_SyringeMinigameInjectShit);

            writer.Put(si.syncId);
            writer.Put(WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount);

            writer.Put(ScavPlayer.GetClientIdFromBody(limb.body));
            writer.Put((byte)Util.GetLimbIndex(limb));


            writer.Put(is_start_or_end || force_sound);
            is_start_or_end = false;



            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

            // WRITER END;


            WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount = 0;

        }
    }


    public static void Postfix(SyringeMinigame __instance) {
        if (!Net.IsRunning) return;

        var is_different = SyringeMinigame_Update_MultiplayerPatch.last_fill_amount != __instance.syringeFill.fillAmount;

        var limb = WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb;

        if (is_different && limb.TryGetNetBody(out var pb)) {
            pb.SetNetHealthSyncIgnoreTime(0.1f);
        }

        if (Minigame.game.currentItem == null) { // cuz it causes errors in the frame the minigame is closing
            return;
        }

        bool item_synced = ItemSync.TryGetSyncInfo(Minigame.game.currentItem, out var si);

        var just_stopped_injecting = !is_different && last_was_different;
        if ((just_stopped_injecting || fill_milestone_timer <= 0f) && item_synced) {

            Client_SendCurrentInjectedAmount(just_stopped_injecting);

        } else {
            if (is_different) {
                if (item_synced) {
                    si.SetIgnoreTimeForRoundTrip(0.1f);
                }
                fill_milestone_timer -= Time.deltaTime;
            } else {
                is_start_or_end = true;
                fill_milestone_timer = SYRINGE_SEND_RATE;
            }
        }
        last_was_different = is_different;
        SyringeMinigame_Update_MultiplayerPatch.last_fill_amount = __instance.syringeFill.fillAmount;

        
    }



    [ServerReceiver((byte)NetmsgId.SERVER_SyringeMinigameInjectShit)]
    static void Server_SyringeMinigameInjectShit(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syringe_syncid);
        reader.Get(out float ml);

        reader.Get(out knetid target_clientId);
        reader.Get(out byte limbId);

        reader.Get(out bool do_sound);

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, target_clientId, limbId, syringe_syncid, out var healer, out var target, out var si) &&
            Util.IsFinite(ml) && 
            si.IsLiquidContainer() 
        ) {

            if (!healer.IsLocal){
                target.GetCoUtilsInstance().SelectForMPOverride();
                WaterContainerItem wat = si.liquidcontainer;
                wat.Inject(target.body.limbs[limbId], ml);
            }

            if (do_sound) {
                ServerMain.Server_AnnounceSound(target.body.transform.position, "syringe", ServerMain.GetListOfClientIdsExceptThis(clientId));
            }

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(target.body.transform.position, $"S: Syringe {healer} -> {target} item:{si}");

            return;
        }
        if (healer != null) {
            healer.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_syringeinject"));
		    MinigameMPManager.Server_ForceEndMinigameForPlayer(healer);
        }
	}

}







[HarmonyPatch(typeof(WaterContainerItem), nameof(WaterContainerItem.Inject))]
public static class WaterContainerItem_Inject_MultiplayerPatch {
    public static Limb listen_to_limb = null;
    public static float listen_to_limb_amount = 0;

    static bool Prefix(WaterContainerItem __instance, Limb limb, float amount) {
        if (!Net.IsRunning) return true;

        limb.GetCoUtilsInstance().SelectForMPOverride();

        if (limb == listen_to_limb) {
            listen_to_limb_amount += amount;
        }
        if (NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si)) {
            si.SetIgnoreTimeForRoundTrip();
        }
        
        return true;
    }

#if DEBUG
    [CTPostInit]
    static void RegisterDebugCommands() {
        Con.RegisterCommandModern("testcoutils", "DEBUG MP - Do a coutils test scenario", (split, comm) => {
            log.l("testcoutils");
            if (Net.IsServer) {
                Con.RunCommand("give @a bruisekit combatpen bruisekit combatpen bigpack pistol");
                foreach (var item in NetBody.all_instances) {
                    //item.body.GetLimb(Body.LimbNum.LegF).muscleHealth *= 0.4f;
                    //item.body.GetLimb(Body.LimbNum.LegF).skinHealth *= 0.4f;
                }
            }
        });
    }
#endif
}



[HarmonyPatch(typeof(BandageMinigame), nameof(BandageMinigame.DoBandageAction))]
public static class BandageMinigame_DoBandageAction_MultiplayerPatch {
    
    public static bool Prefix(BandageMinigame __instance) {
        if (Net.IsRunning) {
            var l = __instance.limb;
            var pb = l.body.GetComponent<NetBody>();

#if DEBUG
            log.l($"DoBandageAction called for: {l}  {l.body}  ");
#endif

            if (NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem.gameObject, out var si)) {
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_BandageMinigame_DoBandageAction, si.syncId,  pb.netId, Util.GetLimbIndex(l), true);
                si.SetIgnoreTimeForRoundTrip();
            }

        }
        return true;
    }


    public static int bandagethingcounter = 0;


    [ServerReceiver((byte)NetmsgId.SERVER_BandageMinigame_DoBandageAction)]
    static void Server_BandageMinigame_DoBandageAction(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if !RELEASE
        Plugin.log.LogInfo($"Server_BandageMinigame_DoBandageAction {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out knetid bandageid);
        reader.Get(out knetid targetplr);
        reader.Get(out byte limbid);

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId,targetplr,limbid,bandageid, out var healerPlr, out var targetnb, out var item_si)) {
            if (MinigameBase_StartMinigame_MultiplayerPatch.known_bandages_onuse_lambdas.TryGetValue(item_si.item.id, out var onuse_lambda)) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(healerPlr.body.transform.position, $"S: bandaging bro {healerPlr.playerName} -> {targetnb.playerName} {item_si}");

                var limb = targetnb.body.limbs[limbid];

                if (!healerPlr.IsLocal) {
                    healerPlr.GetCoUtilsInstance().SelectForMPOverride();

                    //OG: BandageMinigame.DoBandageAction   V5t4   it ends mingame
                    onuse_lambda.Invoke(1f / 18f);
                    // dressing divides the roll by 12 and subtracts that, so the last tick goes under 0
                    if (item_si.item != null && item_si.item.condition < 0f)
                        item_si.item.condition = 0f;
                }

                bandagethingcounter ++;
                if (bandagethingcounter > 14) {

                    // if someone is helping them, make em happier
                    if (healerPlr != targetnb) { 
                        targetnb.body.happiness += 0.2f;
                    }

                    ServerMain.Server_AnnounceSound(targetnb.pos, "bandage", ServerMain.GetListOfClientIdsExceptThis(clientId));
                    bandagethingcounter = 0;
                }

                return;
            } 
            Plugin.log.LogError($"Wtf is this bandage bro (server somehow skipped it or wat?): ID:{item_si.item.id}   SCI:{item_si} ");

			healerPlr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_bandage1"));
			MinigameMPManager.Server_ForceEndMinigameForPlayer(healerPlr);
			
        }
        if (healerPlr != null) {
            //healersci.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_bandage2"));
            MinigameMPManager.Server_ForceEndMinigameForPlayer(healerPlr);
        }
    }






}













// this get triggered only on keydown for the clicks
[HarmonyPatch(typeof(KeypadMinigame), nameof(KeypadMinigame.CheckCasts))]
public static class KeypadMinigame_CheckCasts_MultiplayerPatch {

    public static float GetUsabilityDistanceMP(UsableObject uo ) {
        //OG: PlayerCamera.TryPickupFromWorld  V5t2 ,  where it checks distance to usableobject
        return 10f * uo.rangeMultiplier * 1.4f;
    }


    public static bool CheckIfCloseEnoughForMinigame(UsableObject uo, NetBody pb) {
        //OG: PlayerCamera.TryPickupFromWorld  V5t2 ,  where it checks distance to usableobject
        return KM.DistanceCheck(uo.transform.position, pb.pos, GetUsabilityDistanceMP(uo));
    }

    static bool Prefix(KeypadMinigame __instance, List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) return true;

        if (__instance.toDestroy == null || !CheckIfCloseEnoughForMinigame(__instance.toDestroy.GetComponent<UsableObject>(), ScavPlayer.LocalPlayer.netBody)) {
            // too far
            MinigameBase.main.EndMinigame();
            return false;
        }


        if (NetObjectRegistry.TryGetSyncInfo(__instance.toDestroy.gameObject, out var si)) { 

            int numbernum = -1;

            foreach (RaycastResult uiCast in uiCasts) {
                if (__instance.inputButtons.Contains(uiCast.gameObject)) {
                    numbernum = int.Parse(uiCast.gameObject.transform.GetChild(0).GetComponent<TextMeshProUGUI>().text);
                    break;
                }
                if (uiCast.gameObject == Traverse.Create(__instance).Field( "clearButton").GetValue<GameObject>()) {
                    numbernum = 10;
                    break;
                }
            }

            if (numbernum != -1) {

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(__instance.toDestroy.transform.position, $"C: sending Keypress {numbernum}");

                //Vector2.Distance(body.transform.position, usableObject.transform.position) < 10f * usableObject.rangeMultiplier

                var writer = Net.CreateWriter(NetmsgId.SERVER_KeypadMinigame_Keypress);
                writer.Put(si.syncId);
                writer.Put((byte)numbernum);
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                // WRITER END;


            }
                
        }

        

        return true;
    }
    static void Postfix(KeypadMinigame __instance) {

        // fix the issue of double keypress
        if (ScavPlayer.LocalPlayer != null) {

            var sess = (MinigameMPManager.GetSessionThisPlayerIsInvolvedIn(ScavPlayer.LocalPlayer));

            if (sess != null && sess is MinigameMPManager.KeypadMinigameSession kpses) {
                __instance.current = kpses.current_input;
            }
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_KeypadMinigame_Keypress)]
    static void Server_KeypadMinigame_Keypress(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"KeypadMinigame_Keypress actually works lol {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out knetid syncid);
        reader.Get(out byte keypadnum);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && plr.body && plr.body.conscious &&
            NetObjectRegistry.TryGetSyncInfo(syncid,out var si) && si.IsBuilding() && si.go.TryGetComponent<UsableObject>(out var uo) && 
            si.go.TryGetComponent<Openable>(out var o) && o.isKeypad  // this is the only thing capable of triggering Keypad minigame in the game
            
        ) {
            if (!(keypadnum >= 0 && keypadnum <= 10)) {
                log.sus($"weirdass keypad number {plr} -> num: {keypadnum} ");
                return;
            }
            var sess = (MinigameMPManager.GetSessionThisPlayerIsInvolvedIn(plr));

            if (sess != null && sess is MinigameMPManager.KeypadMinigameSession kpses) {
                if (CheckIfCloseEnoughForMinigame(uo,plr.netBody)) {

                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: KeypadMinigame_Keypress {keypadnum} {si}");

                    if (keypadnum == 10) { // 10 is clear button
                        keypadnum = 0;

                        kpses.current_input = "";
                    } else {
                        kpses.current_input += keypadnum.ToString();
                    }

                    if (kpses.current_input.Length > kpses.match.Length) {
                        kpses.current_input = kpses.current_input.Substring(0, kpses.match.Length);
                    } else {
                        kpses.AnnounceCurrentInput();

                        if (kpses.current_input == kpses.match) {
                            kpses.building.SetHealth(0, false);
                            EvidenceBag.Arm();
                            EvidenceBag.Line("keypad server ok " + EvidenceBag.Building(kpses.building));
                        }
                    }

                    // tell this to everyone including host
                    ServerMain.Server_AnnounceSound(plr.body.transform.position, "beep" + keypadnum.ToString(), ServerMain.GetListOfClientIdsExceptThis(clientId));

                    return;
                } else {
                    MinigameMPManager.Server_ForceEndMinigameForPlayer(plr); 
                    return;
                }
            }
            //SUS
        }
    }


}
















// keep the og adrenaline because of how the stupid minigame is made
[HarmonyPatch(typeof(AmputationMinigame), nameof(AmputationMinigame.Update))]
static class AmputationMinigame_Update_MultiplayerPatch {

    public static void AmputationMinigame_FinishAmputateCopypasted(Limb limb) {
        if (Net.IsServer) {
            if ((limb.body.IsDeadOrCriticallyDying()) || limb.infectionAmount < 90) { // the limb is totally infected 
                CombatStuff.BetterDismember(limb);
            } else {
                limb.Dismember();
            }
        }
        //OG: AmputationMinigame.Update V5t5
        Limb[] connectedLimbs = limb.connectedLimbs;
        foreach (Limb obj in connectedLimbs) {
            obj.infected = false;
            obj.infectionAmount = 0f;
            obj.SetDisinfect(300f);
            obj.bleedAmount *= 0.5f;
        }
        limb.body.traumaAmount -= 20f;
    }

    static float og_adrenaline = 0;
    static bool Prefix(AmputationMinigame __instance) {
        if (!Net.IsRunning) return true;

        var limb = __instance.limb;
        og_adrenaline = PlayerCamera.main.body.adrenaline;

        //OG: AmputationMinigame.Update V5t5 rewritten here
        if (__instance.cutProgress >= 1f) {
            AmputationMinigame_FinishAmputateCopypasted(limb);
            Minigame.game.EndMinigame();
            return false;
        }
        return true;
    }
    static void Postfix(AmputationMinigame __instance) {
        PlayerCamera.main.body.adrenaline = og_adrenaline;
    }
}


[HarmonyPatch(typeof(AmputationMinigame), nameof(AmputationMinigame.PhysicsUpdate))]
public static class AmputationMinigame_PhysicsUpdate_MultiplayerPatch {

    public static Item cutter_item;
    public static float last_cutProgress = 0;

    public static void Prefix(AmputationMinigame __instance) {
        if (!Net.IsRunning) return;
        var scalarmusclehealth = Mathf.Clamp01(__instance.limb.muscleHealth / 100);
        __instance.cutProgress = Mathf.Max(__instance.cutProgress, 1f - (0.5f + scalarmusclehealth * 0.5f)); // sorta sync it, and make it remember the cut state

    }

    public static void Postfix(AmputationMinigame __instance) {
        if (!Net.IsRunning) return;
        //OG: AmputationMinigame.PhysicsUpdate V5t5

        if (__instance.cutProgress > last_cutProgress + 0.08f) {
            last_cutProgress += 0.08f;

            if (NetObjectRegistry.TryGetSyncInfo(cutter_item, out var si)) {
                __instance.limb.body.adrenaline = 100f;

                if (Net.IsClient) {
                    var writer = Net.CreateWriter(NetmsgId.SERVER_AmputationMinigame_cutting);
                    writer.Put(si.syncId);
                    writer.Put(new LimbNetId(__instance.limb));
                    Net.Client_Send(DeliveryMethod.Unreliable, writer);
                    // WRITER END;
                }
            }
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_AmputationMinigame_cutting)]
    static void Server_AmputationMinigame_cutting(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid item_syncid);
        reader.Get(out LimbNetId netlimb);

#if !RELEASE
        log.l($"AmputationMinigame_cutting {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, netlimb.bodyNetId, netlimb.limbId, item_syncid, out var healer, out var target, out var si)
             && si.CanBeUsedToAmputate()
          ) {

            Limb limb = netlimb.GetLimb();

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(limb.transform.position, $"S: Ampu_cut {healer} -> {target} item:{si} musclehealth:{limb.muscleHealth}");

            if (limb.CanAmputate()) {
                var supposed_progress = 0.09f;// slightly more than 0.04 cuz  idk

                // OG: AmputationMinigame.PhysicsUpdate V5t5 
                var timed_progress = supposed_progress * 100;

                // OG: AmputationMinigame.Update V6.1
                limb.body.adrenaline = Mathf.Max(limb.body.adrenaline, 60f);
                limb.pain += timed_progress * 0.5f;
                limb.skinHealth -= timed_progress;
                limb.muscleHealth -= timed_progress;
                limb.bleedAmount += timed_progress * 0.5f;

                // Log the damage event
                var last = target.GetLastDamageEvent();
                if (last == null || last.attackerBody != healer.netBody) {
                    target.OnDamageEvent(new BodyDamageEvent(healer.netBody, target, si.item, supposed_progress));
                }

                return;
            }

        }

        if (healer != null && target != null) {
            healer.Server_DoAlertSingle("Denied amputation cut (try again)", false);
            log.warn($"Denied amputation cutting for {healer} -> {target}");
            MinigameMPManager.Server_ForceEndMinigameForPlayer(healer);
        }



    }



}



