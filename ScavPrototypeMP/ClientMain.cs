using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;








public static class CoopKeybinds {
    const string prefix = "casualtiestogether_";
    public const string pointfingerat = prefix + "pointfingerat"; // KeyCode.Mouse2
    public const string chat = prefix + "chat"; // KeyCode.Slash 
    public const string voicechat = prefix + "voicechat"; // KeyCode.V 
    public const string woundview = prefix + "woundview"; //, KeyCode.R 
    public const string push = prefix + "push"; // KeyCode.F 
    public const string carry = prefix + "carry";  // KeyCode.O 
    public const string inventory = prefix + "inventory"; // KeyCode.I 
    public const string piggyback = prefix + "piggyback"; // KeyCode.P 
    public const string showplrs = prefix + "showplrs"; // KeyCode.LeftShift 

    public static Dictionary<string, KeyCode> as_dict = new Dictionary<string, KeyCode> {
        { CoopKeybinds.pointfingerat, KeyCode.Mouse2 },
        { CoopKeybinds.chat, KeyCode.Slash },
        { CoopKeybinds.voicechat, KeyCode.V },
        { CoopKeybinds.woundview , KeyCode.R },
        { CoopKeybinds.push, KeyCode.F },
        { CoopKeybinds.piggyback, KeyCode.P },
        { CoopKeybinds.carry, KeyCode.O },
        { CoopKeybinds.inventory, KeyCode.I },
        { CoopKeybinds.showplrs, KeyCode.LeftShift },
    };
    public static bool IsWoundViewSameAsOG() {
        return KeyBinds.GetBind(CoopKeybinds.woundview) == KeyBinds.GetBind("woundview");
    }
}







[HarmonyPatch(typeof(InvButton), "body", MethodType.Getter)]
public static class InvButton_get_body_MultiplayerPatch {
    public static Body focused_body;
    public static Vector2 focused_body_position_smooth = Vector2.zero;

#if !RELEASE
    static bool _getter_working_announced = false;
    static void Prefix() {
        if (!_getter_working_announced) {
            _getter_working_announced = true;
            Plugin.log.LogInfo($"MethodType.Getter  is working: {new StackTrace()}");
        }
    }
#endif 



    public static void Postfix(InvButton __instance, ref Body __result) {
        if (Net.IsRunning) {
            if (!PlayerCamera.main.radialOpen || focused_body == PlayerCamera.main.body) {
                focused_body = null;
            }
            __result = focused_body ?? PlayerCamera.main.body;
        }
    }
}
















public class ClientMain : MonoBehaviour {

    public static int LOCAL_PING = -1; // in ms 
    public static float RTT_IN_SECONDS_with_margin => (float)LOCAL_PING * (0.00401f ); // 0.00202f

    public static float AdaptiveSyncTimerDelta = 0.03f;
    public static float ServerPerformanceScale = 1f;

    public static int SERVER_FPS = 60;
    public static int SERVER_TPS = 60;
    public static byte MY_CONNECTION_QUALITY = 0;

    public static ServerWorldStateSingleton ServerWorldState => Net.EntityManager?.GetSingleton<ServerWorldStateSingleton>();

    public static bool ServerIsGeneratingWorld { 
        get {
            return ServerWorldState?.server_is_generating_world.Value ?? false;
        }
        set {
            var state = ServerWorldState;
            if (state != null) {
                state.server_is_generating_world.Value = value;
            }
        }
    }
    public static bool IsInTemporaryWorldPause() {
        return (SharedMain.LocalWorldIsGenerating || (Net.IsClient && ClientMain.ServerIsGeneratingWorld));
    }





    public static void DoVerboseObjInfo(Component itemorbuild, ref string ttname, ref string ttdesc) {
        if (log.verbose) {
            if (itemorbuild == null) {
                return;
            }
            ttname += "</color><color=#ffffffff>";
            if (NetObjectRegistry.TryGetSyncInfo(itemorbuild, out var si)) {
                ttname += $" (netID: {si.syncId})";
                ttdesc = $"{ttdesc}\nIGNORE TICK: {(Net.ServerTick - si.lastUpdateTick)}";
#if !RELEASE
                if (Net.ClientEntityManager != null) {
                    ttdesc += $"\nPROC TICK: {(Net.ClientEntityManager.LastProcessedTick)}";
                }
                ttdesc += $"\nPOS: {si._position.Value}";
                ttdesc += $"\nINTPOS: {si._position.InterpolatedValue}";
#endif
                ttdesc += $"\nRELAX: {si.relaxed_combat_sync}";
            } else {
                if (NetObjectRegistry.ObjectShouldNotBeSynced(itemorbuild.gameObject)) {
                    ttname += $" (netID: IGNORED)";
                } else {
                    ttname += $" (netID: NONE??? BUG!!!!!)";
                }
            }
            if (itemorbuild != null && itemorbuild.TryGetComponent<BodyGetterOverrider>(out var bodyoverrider)) {
                var txt = "UNKNOWN";
                var b = bodyoverrider.GetBody();
                if (b.IsBodyLocal()) {
                    txt = "LOCAL";
                } else if (b != null && b.TryGetComponent(out NetBody nb)) {
                    txt = nb.ToString();
                }
                ttdesc = $"{ttdesc}\nBODY OVR: {txt}";
            }
            if (itemorbuild is Item item) {
                ttdesc = $"{ttdesc}\nitem ID: {item.id}";
            }
            if (itemorbuild is BuildingEntity build) {
                ttdesc = $"{ttdesc}\nbuilding ID: {build.id}";
            }
        }
    }

    void Start() {

        Multiplayer.RegisterCustomClientReceiver("CustomCommand", RunCustomCommand);

        Multiplayer.RegisterCustomClientReceiver("cleanworld", ( reader) => {
#if DEBUG
            log.l("Client received cleanworld");
#endif
            Con.CleanWorld();
        });

    }


    void Update() {

        {
            if (Net.IsRunning && Net.IsClient) {
                // For server to not get overwhelmed 
                ServerPerformanceScale = Mathf.Clamp(Mathf.Lerp(ClientMain.SERVER_FPS, ClientMain.SERVER_TPS, 0.5f) * Time.fixedDeltaTime, 0.01f, 1f);
            } else {
                ServerPerformanceScale = 1;
            }
            AdaptiveSyncTimerDelta = Time.unscaledDeltaTime * ServerPerformanceScale;
        }

        var cursorpos = Util.GetCursorWorldPos();


        var locplr = ScavPlayer.LocalPlayer;
        if (Net.IsConnected && locplr != null) {

            var current_sync_delta = (float)(Time.realtimeSinceStartupAsDouble - _last_sync_time);
            bool cansendsyncnow = current_sync_delta * ServerPerformanceScale >= 0.1f;

            if (locplr.BasePlayer != null) {
                locplr.BasePlayer.CameraPos.Value = Camera.main.transform.position;
            }

            if (Util.IsWorldGenerated()) {

                var locbody = locplr.body;
                if (locbody != null) {

                    if (!cansendsyncnow) {
                        // i had an idea of sometypa adaptive sync, but maybe its just a waste of time. but i did it anyway, lets see how it works 
                        if (locbody.alive) {

                            var curmovedir_is_not_zero = locbody.moveDir != Vector2.zero;

                            var newlook = (Vector2Int16)(locbody.targetLookPos);

                            if (curmovedir_is_not_zero || !locbody.standing) {
                                cansendsyncnow = current_sync_delta >= 0.035f; // faster
                            }

                            if (!cansendsyncnow) {

                                if (newlook != last_looktarget) {
                                    cansendsyncnow = current_sync_delta >= 0.065f; // faster
                                } else {
                                    if (last_movedir_thing == Vector2.zero) {
                                        if (curmovedir_is_not_zero) {
                                            cansendsyncnow = true;
                                        }
                                    } else {
                                        if (locbody.moveDir == Vector2.zero) {
                                            cansendsyncnow = true;
                                        }
                                    }
                                }

                            }

                            last_movedir_thing = locbody.moveDir;
                            last_looktarget = (Vector2Int16)(locbody.targetLookPos);
                        }

                    }


                    if (cansendsyncnow) {
                        ClientMain.Client_SendCharacterSyncPacket();
                    }
                }
            }
        }
    }



    public static void Client_SendCharacterSyncPacket() {

        if (!ScavPlayer.TryGetLocalNetBody(out var loc_nb)) {
            Plugin.log.LogError($"BRO WTFFFFFFF IS THIS !!?!??!?!?!?!??!?!?!?!??!?!!?!?!?!??");
            return;
        }

        var mydata = new ClientToServer_NetBodySyncPacket(loc_nb);
        
        var writer = Net.CreateWriter(NetmsgId.SERVER_ClientCharacterSyncUpdate);
        writer.Put(mydata);
        if (mydata.IncludeRagData() && loc_nb.alive) {
            WriteRagdollPacket(ref writer, loc_nb.body);
        }
        writer.CompressWriter();
        Net.Client_Send(DeliveryMethod.Unreliable, writer);
        // WRITER END;
    }

    static float _RAGDOLL_DIST_MARGIN = 0.05f * 0.05f;
    static float _RAGDOLL_ANGVEL_QUANTIZE = 100;
    public static void WriteRagdollPacket(ref LiteNetLib.Utils.NetDataWriter writer, in Body body) {
        var start = writer.Length;
        
        foreach (var limb in body.limbs) {
            writer.Put((Vector2)limb.rb.position);
            writer.Put((float)limb.rb.rotation);
            writer.Put((Vector2_4byte_200)limb.rb.velocity);
            writer.Put(Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE));

        }
        var end = writer.Length;
    }


    public static void ReadRagdollPacket(ref LiteNetLib.Utils.NetDataReader reader, Body body) {
        body.Ragdoll();
        bool apply = body != null;
        var center = body.GetUpperTorso().GetPosition();
        foreach (var limb in body.limbs) {
            reader.Get(out Vector2 limb_rb_position);
            reader.Get(out float limb_rb_rotation);
            reader.Get(out Vector2_4byte_200 limb_rb_velocity);
            reader.Get(out short limb_rb_angularVelocity);

            if (apply && !limb.dismembered && Vector2.SqrMagnitude(limb.rb.position - limb_rb_position) > _RAGDOLL_DIST_MARGIN) {
                if (Net.IsServer) {
                    if (limb_rb_rotation.IsFinite() && (Vector2.SqrMagnitude(limb_rb_position - center) < 100)) {
                    } else {
#if !RELEASE
                        log.sus($"{DebugHelp.GetPlayerFullDebugString(body)} something is sus about {limb} ");
#endif
                        apply = false;
                        continue;
                    }
                }

                limb.transform.position = limb_rb_position;
                limb.rb.rotation = limb_rb_rotation;
                limb.rb.velocity = limb_rb_velocity;
                limb.rb.angularVelocity = Util_MPExtensions.UnquantizeFloatFromShort(limb_rb_angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);
            }
        }
        if (body.IsRagdolling() && body.TryGetNetBody(out var nb))  {
            nb.SetBodyPosition(center);
        }
    }



    void LateUpdate() {


    }





    double _last_sync_time = 0;
    Vector2 last_movedir_thing = Vector2.zero;
    Vector2Int16 last_looktarget = new();






    //TODO move ts somewhere idk but not here!!
    // used for "Feed" feature in player interaction menu
    public static bool ItemCanBeUsedOnThisBody(Item item, Body target) {

        // cant use tools
        if (CombatStuff.ItemIsUsedForAttacking(item)) {
            return false;
        }

        if (item.Stats.wearable) {
            return true; // allow putting on clothes
        }

        if (item.id == "dynamite") { //nah activate it urself
            return false;
        }

        // check if it has dangerous liquids  // or nah?
        //if (target.conscious) {
        //    if (item.TryGetComponent(out WaterContainerItem skibidi)) {
        //        foreach (var aaaa in skibidi.stack) {
        //            if (Liquids.DangerList.Contains(aaaa.liquidId)) {
        //                return false;
        //            }
        //        }
        //    }
        //}

        if (item.id == "autopump") {
            return true;
        }
        // no only local player may use it
        if (item.id == "mp3player") return false;
        
        
        // idk do i return false by default? cuz ppl can feed just random pills and drugs? 
        return true;
    }


    #region PLAYER_INTERACTIONS




    internal static void _PLRINT_Piggyback(NetBody target) {
        if (target.IsPiggybackable() && target.body.overEncumberance < 0.5) {

            var me = ScavPlayer.GetLocalNetBodyNullable();

            if (me != null ) {
                target = target.GetPiggybackStackTop();

                var my_up = me.CountPiggybackStackUp(); 
                var down = target.CountPiggybackStackDown();

                if (down + my_up + 1 > Multiplayer.rules.PiggybackMaxStack) {
                    return;
                }

                if ( me.StartPiggyback(target)) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestPiggyback, target.netId, true);
                } 
            }
        }
    }
    internal static void _PLRINT_Carry(NetBody target) {

        var me = ScavPlayer.GetLocalNetBodyNullable();
        
        if (Net.IsServer || target.StartPiggyback(me, true)) {
            target.SetNetIgnoreTime();
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestCarryPerson, target.netId, true);
        }
        
    }

    internal static void _PLRINT_Inventory(NetBody target) {
        if (Multiplayer.rules.NoInventoryLock || !target.body.conscious) {
            InvButton_get_body_MultiplayerPatch.focused_body = target.body;
            InvButton_get_body_MultiplayerPatch.focused_body_position_smooth = Camera.main.WorldToScreenPoint(InvButton_get_body_MultiplayerPatch.focused_body.transform.position);
            PlayerCamera.main.radialOpen = true;
        }
    }
    internal static void _PLRINT_GiveItem(NetBody target, Item item) {
        if (item != null && item.TryGetSyncInfo(out var si)) {
            Util.DoAlert(String.Format(Lang.Get("plrint_giveitem_offered"),target.bodyName, PlayerCamera.ItemHoverDescription(item).Item1));

            var writer = Net.CreateWriter(NetmsgId.SERVER_GiveItem);
            writer.Put(si.syncId);
            writer.Put(target.netId);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);


        }
    }
    
    #endregion



















    public static void Do_The_Modifier_Announcement(string layerPrefix, string layerDescription) {
        //OG: WorldGeneration.FinishWorldGeneration   
        string text = Locale.GetOther("layer") + " " + (WorldGeneration.world.biomeDepth + 1) + "\n" + WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth];
        if (!string.IsNullOrEmpty(layerPrefix)) {
            string text2 = "<color=\"orange\">" + layerPrefix + "</color> ";
            text = Locale.GetOther("layer") + " " + (WorldGeneration.world.biomeDepth + 1) + "\n" + text2 + WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth];
            string text3 = "<color=\"orange\">" + layerDescription + "</color>";
            PlayerCamera.main.StartCoroutine(PlayerCamera.main.DoAlertDelayed(text3, important: false, 6f));
        }
        PlayerCamera.main.DoAlert(text, important: true);
    }


    internal class SavedHingeJointState : MonoBehaviour {
        public Vector3 a;
        public Quaternion b;
        public Vector3 aa;
        public Quaternion bb;
        public bool useLimits;
        public float limitsmax;
        public float limitsmin;
        public float referenceAngle;
        public float h;
        public float l;

        public Rigidbody2D connectedBody;
        public HingeJoint2D hinge;
    }

    internal static void _InitializePlayerCharacter() {

        if (ServerMain.CHARACTER_PREFAB == null) {

            var og_char = GameObject.Find("Experiment"); // player character is stored only in the scene, i must get it

            if (og_char != null) {
                {
                    var a = og_char.GetComponentsInChildren<HingeJoint2D>();
                    foreach (var hinge in a) {
                        var ogggg = hinge.AddComponent<SavedHingeJointState>();
                        ogggg.a = hinge.transform.localPosition;
                        ogggg.b = hinge.transform.localRotation;
                        ogggg.aa = hinge.connectedBody.transform.localPosition;
                        ogggg.bb = hinge.connectedBody.transform.localRotation;
                        ogggg.useLimits = hinge.useLimits;
                        ogggg.limitsmax = hinge.limits.max;
                        ogggg.limitsmin = hinge.limits.min;
                        ogggg.referenceAngle = hinge.referenceAngle;

                        ogggg.hinge = hinge;

                        ogggg.connectedBody = hinge.connectedBody;
                    }
                }

                ServerMain.CHARACTER_PREFAB = GameObject.Instantiate(og_char);

                ServerMain.CHARACTER_PREFAB.SetActive(false);
            } else {
                if (Net.IsRunning) {
                    Multiplayer.DoMultiplayerStatusMessageError("PLAYER CHARACTER NOT FOUND!!!!!!!!!!!!!!!! RESTART THE GAME! RESTART !!!!!!!!!!!!!!!!!!!!");
                }
            }
        }


    }








    internal static void _RegisterClientReceivers() // CLIENT RECEIVERS  CLIENT RECEIVERS CLIENT RECEIVERS CLIENT RECEIVERS CLIENT RECEIVERS CLIENT RECEIVERS
    {
#if DEBUG
        Plugin.log.LogInfo("REGISTERING CLIENT Receivers");
#endif
        foreach (var item in
            Multiplayer.all_network_receivers_from_attributes
        ) {
            if (item.Key.GetReceiverType() == NetworkMessageReceiverAttribute.ReceiverType.Client) {

                var attr = (ClientReceiverAttribute)item.Key;

                Net.RegisterClientReceiver(
                    attr.GetMessageId(),
                    (knetid clientId, LiteNetLib.Utils.NetDataReader reader) => {
                        if (Net.IsHost && attr.ignore_host) {
                            if (log.verbose)
                                Multiplayer.DoMultiplayerStatusMessageError($"DEV: I AM HOST, I CANT DO THIS SHIT: {attr.GetMessageName()}\n{new StackTrace()}");
                            else
                                log.error($"DEV: I AM HOST, I CANT DO THIS SHIT: {attr.GetMessageName()}");
                            return;
                        }

                        try {
                            item.Value.Invoke(clientId, reader);
                        } catch (Exception e) {
                            Plugin.log.LogError($"ERROR: Client receiver: {attr.GetMessageName()}\n{e}");
                        }
                    }
                );

            }

        }

    }







	[ClientReceiver((byte)NetmsgId.PlayerDamageBlockRelay)]
	static void ClientReceiver__PlayerDamageBlockRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out knetid who);
		reader.Get(out Vector2Int pos);
		reader.Get(out float dmg);

		if (NetBody.TryGetNetBodyFromId(who, out var nb)) {
			if (!nb.IsBodyLocal()) {
				var blockDamage = WorldGeneration.world.GetBlockDamage(pos);

				if (blockDamage == null) {
					WorldGeneration.world.DamageBlock(pos, dmg, true, false);
				} else {
					var diff = dmg - blockDamage.damage;

					WorldGeneration.world.DamageBlock(pos, diff, !Mathf.Approximately(blockDamage.damage, dmg), false);
				}
			} else {
				// this scope does get triggered, i think of it as confirmation, but ignored
			}
		}
	}





	[ClientReceiver((byte)NetmsgId.CurrentPingOfAllPlayers)]
	static void ClientReceiver__CurrentPingOfAllPlayers(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		for (; ; ) {
			reader.Get(out bool next);
			if (!next) {
				break;
			}

			reader.Get(out knetid plrid);
			reader.Get(out float ping);

			if (ScavPlayer.TryGetPlayerFromClientId(plrid, out var plr)) {
				plr.ping = ping;
                if (plr.IsLocal) {
                    ClientMain.LOCAL_PING = (int)(ping * 1000f);
                }
            }
		}
		
	}



	[ClientReceiver((byte)NetmsgId.CLIENT_AnnounceDoAlert,false)]
	static void ClientReceiver__AnnounceDoAlert(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out string alert);

		Lang.MsgTryParse(ref alert);

		reader.Get(out bool important);

		if (PlayerCamera.main) {
			log.l($"Received DoAlert: {alert} ");
			PlayerCamera.main.DoAlert(alert, important);
		}
	}


    [ClientReceiver((byte)NetmsgId.CustomLogMessage, false)]
    static void ClientReceiver__CustomLogMessage(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out string msg);
        reader.Get(out bool is_error);

        if (is_error) {
            Multiplayer.DoMultiplayerStatusMessageError(msg);
        } else {
            Multiplayer.DoMultiplayerStatusMessageLog(msg);
        }
    }





    static HashSet<Vector2Int> SetBlockChunks = new();
    [ClientReceiver((byte)NetmsgId.SetBlock)]
	static void ClientReceiver__SetBlock(knetid _, LiteNetLib.Utils.NetDataReader reader) {
		if (!SharedMain.LocalWorldIsGenerated) return;

        while (!reader.EndOfData) {
            reader.Get(out WorldGeneration_SetBlock_MultiplayerPatch.ChangedBlockInfo block);
            var pos = (Vector2Int)block.pos;

#if DEBUG
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                var realPos = WorldGeneration.world.BlockToWorldPos(pos);
                log.devevent($"C: SetBlock {block.block} - {block.GetBlockInfo()?.name}  {block.pos} -> {realPos}", realPos);
            }
#endif

            SetBlockChunks.Add(pos / WorldGeneration.CHUNKSIZE);

            if (block.block == 0) {
                var bdamage = WorldGeneration.world.GetBlockDamage(pos);
                if (bdamage != null) {
                    bdamage.DestroySprite();
                    WorldGeneration.world.blockDamages.Remove(bdamage);
                }
            }
            WorldGeneration.world.SetBlockNoUpdate(pos, block.block);
        }

        foreach (var v in SetBlockChunks) {
            WorldGeneration.world.UpdateChunk(v);
        }

        SetBlockChunks.Clear();
    }




	[ClientReceiver((byte)NetmsgId.CreateExplosionEffect)]
	static void ClientReceiver__CreateExplosionEffect(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		if (!TrapClaim.ReadBoom(reader, out var boomId, out var boom))
			return;

		WorldGeneration_CreateExplosion_MultiplayerPatch.ForceCreateExplosionEffect(boom);
		TrapClaim.ClientSawBoom(boomId, boom);
	}





	[ClientReceiver((byte)NetmsgId.CLIENT_PlayerJumpRelay)]
	static void ClientReceiver__PlayerJumpRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
		reader.Get(out knetid who);

		if (NetBody.TryGetNetBodyFromId(who, out var nb) && Util.IsWorldGenerated()) {
			nb.body.Jump();
		}
	}



    [ClientReceiver((byte)NetmsgId.CLIENT_forcespectator)]
    static void ClientReceiver__forcespectator(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (!UIInGame.SpectatorModeActive) {
            log.l("CLIENT: received command from server to enable spectator mode");
            UIInGame.StartSpectatorMode();
        }
    }







    internal static Dictionary<string, Action<string, LiteNetLib.Utils.NetDataReader>> ClientCustomCommandsDict = new();
    
    internal static void RunCustomCommand(LiteNetLib.Utils.NetDataReader reader) {
        if (Net.IsServer) {
            return;
        }

        var com = reader.GetString();

        if (ClientCustomCommandsDict.TryGetValue(com, out var action)) {
            try {
                action.Invoke( com, reader);
            } catch (Exception ex) {
                log.error(ex.Message);
            }
        } else {
            // unkown command 
        }
    }



    [ClientReceiver((byte)NetmsgId.CLIENT_GoBackToMainMenu)]
	static void ClientReceiver__GoBackToMainMenu(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		Multiplayer.DoMultiplayerStatusMessageLog("Received announcement to go to main menu.");

		Multiplayer.showMultiplayerMenu = true;
		PlayerCamera.main.ToMainMenu();

	}







	[ClientReceiver((byte)NetmsgId.PlayerPointFingerAtRelay)]
	static void ClientReceiver__PlayerPointFingerAtRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out knetid who);
		reader.Get(out Vector2 pos);
        reader.Get(out byte t);

        if (ScavPlayer.TryGetPlayerFromClientId(who, out var plr) ) {

			if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, $"C: PlayerPointFingerAtRelay {plr}");
			//if (log.verbose) log.l($"CLIENT: received PlayerPointFingerAtRelay: {pos}");

			plr.PointFingerAt(pos,t);
		}

	}








    [ClientReceiver((byte)NetmsgId.CurrentWorldStateSync)]
	static void ClientReceiver__CurrentWorldStateSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out SerializableRandomState randomstate);
        reader.Get(out bool worldgenerated);

        ServerIsGeneratingWorld = !worldgenerated;

        UnityEngine.Random.state = randomstate.State;
    }




	[ClientReceiver((byte)NetmsgId.CLIENT_Announce_LayerModifier)]
	static void ClientReceiver__Announce_LayerModifier(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out ushort modifierindex);

		var lm = LayerModifier.availableModifiers[modifierindex];

		Plugin.log.LogInfo($"CLIENT: Received layer modifier: {nameof(lm)}  index: {lm.modifierIndex}");

        Util.CallLambdaWhenWorldGenerates(() => {

            lm.Initialize(WorldGeneration.world);
            lm.active = true;

            if (log.verbose) log.l($"Applied layer modifier {lm.GetType()}");

            //OG: WorldGeneration.ApplyLayerModifiers 
            var body = PlayerCamera.main.body;

            var pf = Locale.GetOther("layermodifier" + lm.modifierIndex);
            var dsc = Locale.GetOther("layermodifier" + lm.modifierIndex + "dsc");

            WorldGeneration.world.layerPrefix = (pf);
            WorldGeneration.world.layerDescription = (dsc);

            Do_The_Modifier_Announcement(pf, dsc);
        });

	}




	[ClientReceiver((byte)NetmsgId.CLIENT_Announce_RegenerateWorld)]
	static void ClientReceiver__Announce_RegenerateWorld(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out ushort doPod);

		var wginst = WorldGeneration.world;
		if ((Net.IsClient) && wginst ) {

            if (wginst.generatingWorld) {
                wginst.StopCoroutine("GenerateWorld");
                wginst.generatingWorld = false;
                Multiplayer.DoMultiplayerStatusMessageLog("Received RegenerateWorld announcement. (CANCELLING GenerateWorld !!!)");
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Received RegenerateWorld announcement.");
            }

                //OG: WorldGeneration.ContinueRun 

            PlayerCamera.main.body.forceWalk = false;
			wginst.savePanel.SetActive(false);

			//OG: DrillPod.Update is also sligtly mixed into this  

			bool dopod = (doPod == 1);

			wginst.ResetLayerModifiers();

			wginst.doPod = dopod;
			wginst.StartCoroutine("RegenerateWorld", dopod);

			if (dopod) {
				Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
			}
		}
	}




    [ClientReceiver((byte)NetmsgId.CLIENT_CreateInstance, false)]
    static void ClientReceiver__CreateInstance(uint _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 where);
        reader.Get(out float rot);
        reader.Get(out string soundname, true);

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(where, $"C: CreateInstance {soundname}");

        Utils.Create(soundname, where, rot);
    }





    [ClientReceiver((byte)NetmsgId.PlayThisSoundHere, false)]
	static void ClientReceiver__PlayThisSoundHere(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out Vector2 where);
		reader.Get(out string soundname, true);

		if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(where, $"C: PlayThisSoundHere {soundname}");

		Sound.Play(soundname, where);
        //Util.PlayWorldSoundOnScreenIfInRange(soundname, where,1,1,128); // TODO also send radius and use this
	}


    [ClientReceiver((byte)NetmsgId.ServerInfoUpdate, false)]
    static void ClientReceiver__ServerInfoUpdate(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        Net.CurrentServerInfo.Deserialize(reader);

    }
    


    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerBarkRelay)]
	static void ClientReceiver__PlayerBarkRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out knetid who);

		if (ScavPlayer.TryGetPlayerAndBodyFromClientId(who, out var plr, out var body) && !plr.IsLocal) {
			if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"C: Bark");
            body.eatTime = 0;
            body.GetComponent<PantSound>().Bark();
			if (body.eatTime == 0.5f) {// to account for jitter
				body.eatTime = 0.45f;
			}
		}
		
	}




	[ClientReceiver((byte)NetmsgId.CLIENT_BodyTeleportPacket)]
	static void ClientReceiver__SingleCharacterPositionsSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid plrId);
        reader.Get(out knetid body_netId);
        reader.Get(out BodyTeleportPacket pack);

		if (NetBody.TryGetNetBodyFromId(body_netId, out var nb)) {
            if ((nb.IsPlayer && nb.IsLocal)) {
                if (Util.IsGeneratingWorld() || !Util.IsInWorld()) {
                    _last_reminderpack_while_generating_received = true;
                }
                _last_reminderpack_while_generating = pack;
                if (nb != null) {
                    nb.OnReceiveSyncPacket(pack, true);
                }
                // its applied in finishworldgeneration
            } else {
                nb.OnReceiveSyncPacket(pack);
            }
		} else if (ScavPlayer.LocalPlayer != null && plrId == ScavPlayer.LocalPlayer.playerId && (Util.IsGeneratingWorld() || !Util.IsInWorld())) {
            // the spawn packet beat the body. keep it so the wait can finish, apply once the body exists.
            _last_reminderpack_while_generating = pack;
            _last_reminderpack_while_generating_received = true;
            log.l("CLIENT: spawn location arrived before the body. holding it.");
        }
	}

#if RELEASE
    internal static bool _last_reminderpack_while_generating_received = false;
#else
    internal static bool _last_reminderpack_while_generating_received__ = false; 
    internal static bool _last_reminderpack_while_generating_received {
        get {
            return _last_reminderpack_while_generating_received__;
        }
        set {
            if (value) {
                log.l($"last_reminderpack_while_generating_received set to true from: {new StackTrace()}");
            }
            _last_reminderpack_while_generating_received__ = value;
        }
    }
#endif
    internal static BodyTeleportPacket _last_reminderpack_while_generating;

    
    [ClientReceiver((byte)NetmsgId.CLIENT_ragdollpack)]
    static void ClientReceiver__ragdollpack(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid netbodyid);
        if (NetBody.TryGetNetBodyFromId(netbodyid,out var nb)) {
            reader = reader.DecompressReader();

            //ClientMain.ReadRagdollPacket(ref reader, nb.body);
        }
    }

    [ClientReceiver((byte)NetmsgId.GenerateWorld_SeedAnnounce)]
    static void ClientReceiver__GenerateWorld_SeedAnnounce(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out LastBeforeGenerationState rand);

        Multiplayer.DoMultiplayerStatusMessageLog("GenerateWorld_SeedAnnounce, received worldgen seed.");

        WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = false;
        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams = rand;

    }

    [ClientReceiver((byte)NetmsgId.StartGameAnnouncement)]
	static void ClientReceiver_StartGameAnnouncement(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out ServerMain.StartGameAnnouncementPacket sgap);
        reader.Get(out string runsettings, true);

        Multiplayer.rules = sgap.rules;
        Multiplayer.ApplyGameRules();


        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.randomstate = sgap.randomstate;

		WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = true;
		

		UnityEngine.Random.state = sgap.randomstate;


        // setting the settings
        try {
            WorldMP.ApplyRunSettings(runsettings);

            sgap.prefs.ApplyPrefs();

        } catch (Exception e) {
            log.error(e);
        }


        SaveSystem.loadedRun = false;
		if (PreRunScript.instance != null) {
            Multiplayer.DoMultiplayerStatusMessageLog($"### STARTING GAME (RECEIVED ANNOUNCEMENT FROM SERVER) ###");

            ScavGameStartPatch.starting_game = true;
			PreRunScript.instance.StartCoroutine(PreRunScript.instance.WaitLoad());
			Multiplayer.showMultiplayerMenu = false;
		} else {
            if (Util.IsInWorld()) {
                log.warn("Received StartGameAnnouncement but im in the world already... bruh");
            } else {
                log.warn("Received StartGameAnnouncement but theres no PreRunScript, what???");
            }
		}

	}



    [ClientReceiver((byte)NetmsgId.CLIENT_RunSettings)]
    static void ClientReceiver__RunSettingsSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader = reader.DecompressReader();

        reader.Get(out string runsettings, true);
        reader.Get(out MultiplayerGameRules new_rules);

        if (runsettings != WorldMP.CompileRunSettings()) {
            if (log.verbose)
                Multiplayer.DoMultiplayerStatusMessageLog("Run settings changed.");

            WorldMP.ApplyRunSettings(runsettings);
            if (PreRunScript.instance != null) {
                PreRunScript.instance.UpdateAllSettingDisplays();
            }
        }



        { // apply the rules

            var count = 0;
            var msg = "";
            try {
                foreach (var fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    if (fld.GetValue(Multiplayer.rules).ToString() != fld.GetValue(new_rules).ToString()) {
                        msg += $"\n{fld.Name} = {fld.GetValue(new_rules)}";
                        count += 1;
                    }
                }
                msg = $"Server changed {count} game rules:{msg}";
            } catch (Exception e) {
                log.error(e);
            }

            if (count > 0) {
                Multiplayer.rules = new_rules;
                Multiplayer.ApplyGameRules();

                if (log.verbose && !String.IsNullOrWhiteSpace(msg) && count > 0)
                    Multiplayer.DoMultiplayerStatusMessageLog(msg);
            }


        }

    }






    [ClientReceiver((byte)NetmsgId.CLIENT_SetTimeScaleRelay)]
	static void ClientReceiver__SetTimeScaleRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
		reader.Get(out ushort sc);

		if ((PlayerCamera.SpeedType)sc == PlayerCamera.SpeedType.Paused)
			return;

		if (PlayerCamera.main != null && !Multiplayer.rules.DisableTimeManipulation) {
			if (log.verbose) log.l($"SetTimeScale: Received SetTimeScale from server: {(PlayerCamera.SpeedType)sc}");

			PlayerCamera_SetTimeScale_MultiplayerPatch.force = true;
			PlayerCamera.main.SetTimeScale((PlayerCamera.SpeedType)sc, true);

            ClientMain.ServerWorldState.Deserialize();
		}
	}


    [ClientReceiver((byte)NetmsgId.CLIENT_CustomPrompt, false)]
    static void ClientReceiver__CustomPrompt(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint key);
        reader.Get(out string promptid);
        reader.Get(out string title);
        reader.Get(out string text);

        UIChoicePrompt.ClientDoNetPrompt(key,promptid,title,text);
    }











}
