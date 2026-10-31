using HarmonyLib;
using CasualtiesTogetherUtils;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Collections;
using LiteNetLib;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;















public class HeaterMultiplayerRewrite : MonoBehaviour {
    Heater heater;

    void Awake() {
        heater = GetComponent<Heater>();
    }

    void Update() {
        //OG: Heater.OnWillRenderObject  THE ENTIRE FUNCTION I REIMPLEMENTED IT HERE

        if (heater.enabled && Time.time - heater.lastTime > 0.5f) {
            heater.lastTime = (Time.time);

            // just made it that it loops through all bodies
            foreach (var nb in NetBody.GetBodiesInRadius(heater.transform.position,heater.maxDistance)) {
                var body = nb.body;

                if (
                    ((heater.isHeater && body.temperature < heater.desiredTemp) || (!heater.isHeater && body.temperature > heater.desiredTemp))
                ) {
                    body.temperature = Mathf.Lerp(body.temperature, heater.desiredTemp, heater.tempLerpStrength);
                }
            }
        }
    }

}


// this can be used on dead players
public class CorpseScriptMultiplayerRewrite : MonoBehaviour {

    public bool animalCorpse = true;
    public byte corpseSpriteIndex = 0;

    public HashSet<NetBody> people_that_seen_it = new();

    //OG: CorpseScript.OnWillRenderObject   V5t4   
    public const float SEE_RADIUS = 7f;

    void Awake() {
        if (TryGetComponent<CorpseScript>(out var corpse)) { 
            animalCorpse = corpse.animalCorpse;
            corpseSpriteIndex = (byte)System.Array.IndexOf(corpse.startSprites, corpse.GetComponent<SpriteRenderer>().sprite);
        } else if (TryGetComponent<Body>(out var body)) {
            animalCorpse = false;
        }
    }

    void Start() {
        InvokeRepeating(nameof(SlowUpdate), UnityEngine.Random.value * 2, 0.1f);
    }

    void SlowUpdate() {
        //OG: CorpseScript.OnWillRenderObject   V5t4   the player seeing part

        if (!animalCorpse) {
            // if this script is on a dead player, and player got revived remove this component 
            if (TryGetComponent<NetBody>(out var npc) ) {

                if (npc.body.alive) {
                    Destroy(this);
                    return;
                }

                if ( npc.timeHasBeenDead < 2) {
                    return;
                }
            }

            foreach (var plr in ScavPlayer.GetPlayerBodiesInRadius(transform.position, SEE_RADIUS)) {
                if ( !people_that_seen_it.Contains(plr) ) {
                    people_that_seen_it.Add(plr);

#if !RELEASE
                    log.l($"{plr} has seen corpse {npc} at {((Vector2Int16)((Vector2)transform.position))}");
#endif

                    if (
                        !plr.IsLocal || // prevent double debuff because i left original method doing its thing
                        npc != null // but always do this for player corpses
                    ) { 

                        plr.body.happiness -= 3.5f * plr.body.desensitizedMult;
                        plr.body.sicknessAmount += 6f * plr.body.desensitizedMult;
                        plr.body.desensitizedMult *= 0.9f;
                        plr.body.corpsesSeen++;
                        if (plr.body.totalHappiness < -55f) {
                            plr.body.talker.Talk(Locale.GetCharacter("seecorpsesuicidal"), null, force: true);
                        } else if (plr.body.corpsesSeen < 9) {
                            plr.body.talker.Talk(Locale.GetCharacter("seecorpse"));
                            plr.body.eyeScareTime = 4f;
                        } else {
                            plr.body.talker.Talk(Locale.GetCharacter("seecorpsedesensitized"));
                        }
                    }

                }
            }
        }


    }

    private void OnWillRenderObject() {
        //OG: CorpseScript.OnWillRenderObject  V5t4   the staring part

        foreach (var plr in ScavPlayer.GetPlayerBodiesInRadius(transform.position, SEE_RADIUS)) {

            plr.body.overrideLookTime = 0.5f;
            plr.body.overrideLookPos = transform.position;
            if (plr.body.corpsesSeen < 9) {
                plr.body.eyeScareTime = 0.5f;
            }
            
        }

    }


    void OnDestroy() {
        if (animalCorpse) return;
        if (gameObject && gameObject.scene.isLoaded && Util.IsWorldGenerated() && (!TryGetComponent(out BuildingEntity b) || b.GetHealth() <= 0f)) {
            var nearest = NetBody.GetNearestBody(transform.position, true, true);
            if (nearest.Item2 > 10 * 10) return;

#if TESTING
            log.l($"CORPSE BREAK APPLY MOOD DEBUFF");
#endif

            foreach (var witness in ScavPlayer.GetPlayerBodiesInRadius(transform.position, 28)) {
                if (Net.IsServer && witness.body.IsBodyLocal()) {
                    continue;
                }
                var is_attacker = witness.body.attackCooldown > 0f;
                if ( witness.body.conscious) {

#if TESTING
                    log.l($"CORPSE BREAK APPLY MOOD DEBUFF -> {witness}");
#endif

                    var oldhapines = witness.body.happiness;
                    
                    //OG: CorpseScript.OnDestroy V510
                    var total_damage = 5f;

                    // check if the witness actually didnt see em
                    if (!Util.AccurateRaycastInteractionCheckObstruction(nearest.Item1.body, witness.body, false)) {
                        total_damage *= 0.5f; 
                    }

                    witness.body.happiness -= total_damage * witness.body.desensitizedMult;
                    witness.body.eyeScareTime = 5f;

                    if (is_attacker) {
                        witness.body.talker.Talk(Locale.GetCharacter("breakcorpse"));
                    }
                }
            }


        }
    }


}






public class OnWillRenderObject_ForceForMPComponent : MonoBehaviour {

    public SyncInfoGameObjectTracker tracker => GetComponent<SyncInfoGameObjectTracker>();
    public Renderer renderer => GetComponent<Renderer>();
    public MonoBehaviour target;


#if (!RELEASE) && false
    void OnGUI() {

        if (log.verbose) {
            var ogfont = GUI.skin.label.fontSize;

            var pos = (Vector2)Camera.main.WorldToScreenPoint((Vector2)transform.position + Vector2.up);
            pos = new Vector2(pos.x, Screen.height - pos.y);

            //if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) 
                {

                const float bsize = 17;
                const float elsize = 13;

                float y = -elsize*1.5f;

                GUI.skin.label.normal.textColor = UnityEngine.Color.white;

                GUI.skin.label.fontSize = 10;
                GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), $"t: {tracker!=null} renderer:{renderer != null} visible:{renderer?.isVisible} t_inview:{tracker?.is_within_anyones_view}  forcing: {renderer?.isVisible == false && tracker?.is_within_anyones_view == true}");
                y += elsize;

            }

            GUI.skin.label.normal.textColor = UnityEngine.Color.white;
            GUI.skin.label.fontSize = ogfont;
        }
    }
#endif

    void Update() {
        if (Net.IsClient) {
            if (target is GunmineScript gun)
                TrapClaim.TickClientGun(gun);
            return;
        }
        try {
            if (tracker != null && target.TryGetComponent<Renderer>(out var renderer)) {
                //is_visible_from_camera = is_visible_from_camera || target.GetComponent<Renderer>()?.isVisible;
                bool is_visible_from_camera = renderer.isVisible == true;

                if (!is_visible_from_camera && tracker.isVisible) {

                    //MethodInfo method = Util.GetMethod(target, "OnWillRenderObject");
                    MethodInfo method = target.GetType().GetMethod("OnWillRenderObject", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                    if (method == null) {
                        throw new System.Exception($"OnWillRenderObject wasnt found wtf {target.GetType()}");
                    }

                    method.Invoke(target, null);


                }
            }
        } catch (System.Exception e ) {
            Plugin.log.LogError($"Failed force OnWillRenderObject for {target?.name}\n{e}");
        }
    }
}




public class TrackerBuildingEntityBackgroundified : UnityEngine.MonoBehaviour {

    public static readonly List<TrackerBuildingEntityBackgroundified> all_instances = new();
    void OnEnable() => all_instances.Add(this);
    void OnDisable() => all_instances.Remove(this);

    public bool is_backgroundified = false;
    public bool is_a_background_tilemap = false;

}



public class BaseSpecialStaticEntityTracker : UnityEngine.MonoBehaviour {
    protected byte REQ = (byte)NetmsgId.IsThereASandvine__;

    Vector2 pos => transform.position;
    public bool was_synced = false;

    float checktimer = 1;
    
    void Start() {
        checktimer = UnityEngine.Random.value;
    }


    void FixedUpdate() {

        if (!Net.IsRunning) {
            GameObject.Destroy(this);
            return;
        }
        if (was_synced && !WantsAnotherPass()) {
            return;
        } else {

            checktimer -= Time.fixedDeltaTime;

            if (checktimer < 0) {
                checktimer = UnityEngine.Random.value*0.4f;

                if (!SharedMain.LocalWorldIsGenerated) { return; }

                if (SharedMain.LastWorldgenFinishTime < 5) { return; }

                if (Net.IsClient) {
                    if (ClientMain.ServerIsGeneratingWorld) {
                        return;
                    }

                    if (!CheckDist(PlayerCamera.main.transform.position)) { // if i cant see it ignore it
                        return;
                    }
                } else {
                    ScavPlayer closest_plr = null;
                    foreach (var plr in ScavPlayer.BodyToPlayerDict.Values) {
                        if (plr.ServerPlayerState.is_loaded_in && CheckDist(plr.pos)) { // if someone sees it log it
                            closest_plr = plr;
                            break;
                        }
                    }
                    if (closest_plr == null) { // so if no one sees it, ignore it 
                        return;
                    }
                }

            } else {
                return;
            }







            if (Net.IsClient) {

                var writer = Net.CreateWriter(REQ);

                writer.Put((Vector2)transform.position);

                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                // WRITER END;

            } else {
                Server_Announce();
            }
            was_synced = !WantsAnotherPass();
        }
    }

    // climbables stay awake until every loaded client has the rope. one announce
    // used to set was_synced and a late joiner never got it unless someone climbed.
    protected virtual bool WantsAnotherPass() => false;


    virtual public bool CheckDist(Vector2 pos) {
        return KM.DistanceCheck_PreSqr(pos, this.transform.position, 64 * 64);
    }

    virtual public void Server_Announce() {
        if (!was_synced) {
            was_synced = true;
        }
    }

    public static T GetAtPos<T>(Vector2 pos) where T : BaseSpecialStaticEntityTracker {
        foreach (var item in UnityEngine.Object.FindObjectsOfType<T>()) {
            if ((Vector2)(item.transform.position) == (Vector2)(pos)) {
                return item;
            }
        }
        return null;
    }

}




public class TrackerClimbableEntity : BaseSpecialStaticEntityTracker {
    public const string sandvine_new_id = "Special/sandvinerope";// originally "sandvine";
    public const string climbingropeextended_id = "climbingropeextended";
    public const string mushroomrope_new_id = "Special/mushroomrope";

    public string og_id = null;

#if (!RELEASE) && false
    void OnGUI() {
        if (log.verbose) {

            var ogfont = GUI.skin.label.fontSize;

            var pos = (Vector2)Camera.main.WorldToScreenPoint(transform.position);
            pos = new Vector2(pos.x, Screen.height - pos.y);

            if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {

                const float bsize = 17;
                const float elsize = 13;

                float y = 0;

                GUI.skin.label.normal.textColor = UnityEngine.Color.white;

                GUI.skin.label.fontSize = 10;
                GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "pos: " + pos);
                y += elsize;
                GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "was_synced: " + was_synced);
                y += elsize;

                var climbable = GetComponent<Climbable>();
                if (climbable.points.Count > 0) {
                    GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "downwardsVelocity: " + climbable.downwardsVelocity);
                    y += elsize;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "point[0]: " + climbable.points[0]);
                    y += elsize;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "point[1]: " + climbable.points[1]);
                    y += elsize;
                } else {
                }

                GUI.Label(new Rect(pos.x, pos.y + y, 400, bsize), "point.Count: " + climbable.points.Count);
                y += elsize;
            }

            GUI.skin.label.normal.textColor = UnityEngine.Color.white;
            GUI.skin.label.fontSize = ogfont;
        }
    }
#endif


    void Awake() {
        REQ = (byte)NetmsgId.SERVER_IsThereARope__;
    }

    /*
        sandvinerope = sandvine  (mod patches this into "sandvinerope")
        climbingropeextended = climbingropeextended
        mushroomrope = mushroomrope
    */


    public bool is_ladder => GetComponent<BuildingEntity>().id == "ladder";
    public bool is_sandvine => GetComponent<BuildingEntity>().id == sandvine_new_id;
    public bool is_mushroomrope => GetComponent<BuildingEntity>().id == mushroomrope_new_id;
    public bool is_mushroomrope_or_sandvine => is_sandvine || is_mushroomrope || is_ladder; // they are basically the same in their prefab structures
    public bool is_climbingropeextended => GetComponent<BuildingEntity>().id == climbingropeextended_id;


    Vector2 spritescale {
        get {
            if (is_mushroomrope_or_sandvine) {
                return transform.localScale;
            }
            if (is_climbingropeextended) {
                return transform.GetChild(0).localScale;
            }
            return Vector2.one;
        }
        set {
            if (is_mushroomrope_or_sandvine) {
                transform.localScale = value;
            }
            if (is_climbingropeextended) {
                transform.GetChild(0).localScale = value;
            }
        }
    }
    float spritey {
        get {
            if (is_mushroomrope_or_sandvine) {
                return GetComponent<SpriteRenderer>().size.y;
            }
            if (is_climbingropeextended) {
                return 100;
            }
            return 100;
        }

        set {
            if (is_mushroomrope_or_sandvine) {
                GetComponent<SpriteRenderer>().size = new Vector2(2.5f, value);
            }
            if (is_climbingropeextended) {
                // do nothing
            }
        }
    }

    bool isPointless = false;
    public bool announce_reliable = true;
    readonly HashSet<ScavPlayer> _told = new();
    SpriteRenderer[] _sprites;
    const float RopeSeeDistSqr = 64f * 64f;

    protected override bool WantsAnotherPass() {
        if (!Net.IsServer)
            return false;
        var climbable = GetComponent<Climbable>();
        if (climbable == null || climbable.points == null || climbable.points.Count < 2)
            return true;
        return HasUntoldClient();
    }

    bool HasUntoldClient() {
        var clients = ServerMain.AllClientIdsExceptHost;
        for (int i = 0; i < clients.Count; i++) {
            if (!ScavPlayer.TryGetPlayerFromClientId(clients[i], out var plr) || plr == null)
                continue;
            if (plr.ServerPlayerState == null || !plr.ServerPlayerState.is_loaded_in)
                continue;
            if (!_told.Contains(plr))
                return true;
        }
        return false;
    }

    public override void Server_Announce() {
        var climbable = GetComponent<Climbable>();
        if (climbable == null || climbable.points == null || climbable.points.Count < 2) {
            if (climbable != null && !isPointless && climbable.points != null && climbable.points.Count == 0) {
                log.error($"Rope is pointless {NetObjectRegistry.DebugStringGameObject(gameObject)}  {(Vector2)climbable.transform.position}");
                isPointless = true;
            }
            was_synced = false;
            return;
        }

        // climbing forces a refresh. standing nearby only tells clients who missed the earlier pass.
        bool refresh = !announce_reliable;
        announce_reliable = true;

        var clients = ServerMain.AllClientIdsExceptHost;
        var targets = new List<knetid>(clients.Count);
        for (int i = 0; i < clients.Count; i++) {
            if (!ScavPlayer.TryGetPlayerFromClientId(clients[i], out var plr) || plr == null)
                continue;
            if (plr.ServerPlayerState == null || !plr.ServerPlayerState.is_loaded_in)
                continue;
            if (!refresh && _told.Contains(plr))
                continue;
            targets.Add(clients[i]);
            _told.Add(plr);
        }
        if (targets.Count == 0) {
            was_synced = !HasUntoldClient();
            return;
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_ThereIsARope____);

        writer.Put((Vector2)climbable.transform.position);
        writer.Put(climbable.GetComponent<BuildingEntity>().id, true);
        writer.Put(climbable.points.ToArray());
        writer.Put((float)climbable.downwardsVelocity);

        writer.Put(spritescale);
        writer.Put(spritey);

        Net.Server_SendToClients(refresh ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableUnordered, writer, clientIds: targets);

        was_synced = !HasUntoldClient();
    }

    // ropes are not normal buildings, so a host explode never reaches the client copy.
    // the client explosion is visual only and leaves the vine standing.
    void OnDestroy() {
        if (!Net.IsServer || !Util.IsWorldGenerated())
            return;
        var world = WorldGeneration.world;
        if (world == null || world.generatingWorld)
            return;
        var build = GetComponent<BuildingEntity>();
        if (build != null && build.GetHealth() > 0.5f)
            return;
        var writer = Net.CreateWriter(NetmsgId.CLIENT_NoTheresNoRopeFuckOff);
        writer.Put((Vector2)transform.position);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }



    public override bool CheckDist(Vector2 pos) {


        var climbable = GetComponent<Climbable>();
        if (climbable == null) { // it may be sandvine begining and end decorations, i dont need to sync those
            Object.Destroy(this);
            return false;
        }
        if (NearSprite(pos))
            return true;
        if (climbable.points == null || climbable.points.Count == 0) {
            if (Net.IsClient) { // to force it to request syncinfo
                return true;
            }
            return false;
        }
        int last = climbable.points.Count - 1;
        for (int i = 0; i < last; i++) {
            var closest = KM.v2getClosestPointOnLine(climbable.points[i], climbable.points[i + 1], pos);
            if ((closest - pos).sqrMagnitude < RopeSeeDistSqr)
                return true;
        }
        return false;
    }

    bool NearSprite(Vector2 pos) {
        if (_sprites == null)
            _sprites = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < _sprites.Length; i++) {
            var sprite = _sprites[i];
            if (sprite != null && sprite.bounds.SqrDistance(pos) <= RopeSeeDistSqr)
                return true;
        }
        return ((Vector2)transform.position - pos).sqrMagnitude < RopeSeeDistSqr;
    }



    [ClientReceiver((byte)NetmsgId.CLIENT_NoTheresNoRopeFuckOff)]
    static void Client_NoTheresNoRopeFuckOff(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        // Read the data
        reader.Get(out Vector2 pos);

#if !RELEASE
        log.warn($"Client_NoTheresNoRopeFuckOff {pos}");
#endif

        var sv = GetAtPos<TrackerClimbableEntity>(pos);
        if (sv != null) {
            GameObject.Destroy(sv.gameObject);
        }
    }



    [ClientReceiver((byte)NetmsgId.CLIENT_ThereIsARope____)]
    static void Client_ThereIsARope(knetid _, LiteNetLib.Utils.NetDataReader reader) {
#if !RELEASE
        log.l($"ROPEEEEEEEEEEEEEEEEEE ThereIsARope!!!! actually works   ");
#endif

        // Read the data   so the mod only syncs the already translated dialogues
        reader.Get(out Vector2 rope_pos);
        reader.Get(out string rope_resourceid, true);

        var rope = GetAtPos<TrackerClimbableEntity>(rope_pos);
        if (rope == null) {
            rope = UnityEngine.Object.Instantiate(Resources.Load(rope_resourceid), rope_pos, Quaternion.identity).GetOrAddComponent<TrackerClimbableEntity>();
        }// repeat 100 0 explode_player
        rope.was_synced = true;


        //OG: WorldGeneration.GenerateWorld  V5t4 , theres only two places where Ropes get created and they are the same
        var climbable = rope.GetComponent<Climbable>();


        reader.Get(out Vector2[] newpoints);
        reader.Get(out float downwardsVelocity);

        reader.Get(out Vector2 scale);
        reader.Get(out float spritey);

        climbable.points = new List<Vector2>(newpoints);


        climbable.downwardsVelocity = downwardsVelocity;

        rope.spritescale = scale;
        rope.spritey = spritey;

        var spr = rope.GetComponent<SpriteRenderer>();
        if (rope.is_sandvine) {
            //OG: WorldGeneration.WorldPlaceEntities V5t5 
            spr.color = Color.Lerp(Color.gray, Color.white, UnityEngine.Random.value);
            spr.flipX = UnityEngine.Random.value > 0.5f;
        } else if (rope.is_ladder) {
            spr.size = new Vector2(1.5f, spr.size.y);
        }

        climbable.CalculateTotalLength();


    }




    [ServerReceiver((byte)NetmsgId.SERVER_IsThereARope__)]
    static void Server_IsThereARope(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if !RELEASE
        log.l($"ROPEEEEEEEEEEEEEEEEEE IsThereARope?? actually works  {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out Vector2 pos);


        var thing = GetAtPos<TrackerClimbableEntity>(pos);
        if (thing == null) {
            var writer = Net.CreateWriter(NetmsgId.CLIENT_NoTheresNoRopeFuckOff);

            writer.Put((Vector2)pos);

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: clientId);

            // WRITER END;
        } else {
            thing.Server_Announce();
        }

        // ghost em
        // they already triggered the bool so they wont ask again 
    }


}






public class TrackerStaticCaveTicks : BaseSpecialStaticEntityTracker {
    
    void Awake() {
        REQ = (byte)NetmsgId.SERVER_Krokosha_CaveTicks_Tracker_request;
    }

    public override void Server_Announce() {
        //if (!was_synced) 
            {
            was_synced = true;

            var writer = Net.CreateWriter(NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_announce);

            writer.Put((Vector2)transform.position);


#if TESTING
            log.l($"SERVER , sending  Krokosha_CaveTicks_Tracker_announce     {(Vector2)transform.position} ");
#endif

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);

            // WRITER END;
        }
    }


    [ClientReceiver((byte)NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_deny)]
    static void Client_NoTheresNoSandvineFuckOff(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 pos);

#if TESTING
        log.l($"CLIENT, Krokosha_CaveTicks_Tracker_deny actually works     {pos} ");
#endif

        var sv = GetAtPos<TrackerStaticCaveTicks>(pos);
        if (sv != null) {
            GameObject.Destroy(sv.gameObject);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_announce)]
    static void Client_Krokosha_CaveTicks_Tracker_announce(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 pos);

#if TESTING
        log.l($"CLIENT, Krokosha_CaveTicks_Tracker_announce actually works     {pos} ");
#endif

        if (GetAtPos<TrackerStaticCaveTicks>(pos) == null) {
            GameObject svRope = (GameObject)UnityEngine.Object.Instantiate(Resources.Load("CaveTicks"), pos, Quaternion.identity);
            
            // and force the thing to be true
            var abomination = svRope.GetOrAddComponent<TrackerStaticCaveTicks>();
            abomination.was_synced = true;
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_Krokosha_CaveTicks_Tracker_request)]
    static void Server_Krokosha_CaveTicks_Tracker_request(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        
        reader.Get(out Vector2 pos);

#if TESTING
        log.l($"Krokosha_CaveTicks_Tracker_request actually works  {DebugHelp.GetPlayerFullDebugString(clientId)}   {pos} ");
#endif

        var thing = GetAtPos<TrackerStaticCaveTicks>(pos);
        if (thing == null) {
            var writer = Net.CreateWriter(NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_deny);

            writer.Put((Vector2)pos);

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: clientId);

            // WRITER END;

        } else {
            thing.Server_Announce();
        }

        // ghost em
        // they already triggered the bool so they wont ask again 
    }

}






[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Backgroundify))]
public static class BuildingEntity_Backgroundify_MultiplayerPatch {
    static void Prefix(BuildingEntity __instance) {
        var tr = __instance.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
        tr.is_backgroundified = true;
    }
}


[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.CheckSeating))]
internal static class BuildingEntity_CheckSeating_MultiplayerPatch {
    static bool Prefix() {
        // Client instances do not have the worldgen support block or its listeners.
        return !(Net.IsRunning && Net.IsClient);
    }
}

[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Update))]
public static class BuildingEntity_Update_MultiplayerPatch {

    static bool Prefix(BuildingEntity __instance, ref Vector3 __state) {
        __state = new Vector3();
        // The server sends the destruction and loot; clients must not roll their own drops.
        // the skip used to drop the break particle, the dust, and AnimalDeath with them.
        if (Net.IsRunning && Net.IsClient && __instance.GetHealth() < 0.5f) {
            PlayDeathOnce(__instance);
            return false;
        }
        return true;
    }

    // one shot. health stays under 0.5 until the destroy packet, and Update would fire every frame.
    internal static void PlayDeathOnce(BuildingEntity be) {
        if (be == null || be.GetComponent<ClientDeathFxPlayed>() != null)
            return;
        be.gameObject.AddComponent<ClientDeathFxPlayed>();

        if (be.TryGetComponent(out SpriteRenderer sprite) && sprite.sprite != null) {
            var broken = Object.Instantiate(Resources.Load("BuildingBreakParticle"), be.transform.position, be.transform.rotation) as GameObject;
            if (broken != null && broken.TryGetComponent(out ParticleSystem ps)) {
                var shape = ps.shape;
                shape.texture = sprite.sprite.texture;
                shape.sprite = sprite.sprite;
                ps.Play();
            }
        }
        Object.Instantiate(Resources.Load<GameObject>("DustBig"), be.transform.position, Quaternion.identity);
        PlayAnimalDeathFx(be);
        Sound.Play("footstep/Rock/11", be.transform.position);
    }

    // stock AnimalDeath also grants xp. the host already did that. this is only the sound and the burst.
    static void PlayAnimalDeathFx(BuildingEntity be) {
        if (be.TryGetComponent(out CrystalEnemy _)) {
            Sound.Play("crystalenemydeath", be.transform.position, false, false);
            var distort = Utils.Create("Special/CrystalDistort", be.transform.position, 0f);
            if (distort != null)
                distort.AddComponent<CrystalDeathAnimation>();
            return;
        }
        if (be.TryGetComponent(out SpiderHandler spider)) {
            Sound.Play("gore", be.transform.position);
            if (spider.doDeathExplode)
                Object.Instantiate(Resources.Load("BloodExplosion"), be.transform.position, Quaternion.identity);
            return;
        }
        if (be.TryGetComponent(out TraderScript trader)) {
            Vector3 pos = trader.torso != null ? trader.torso.position : be.transform.position;
            Sound.Play("gore", pos);
            Object.Instantiate(Resources.Load("BloodExplosion"), pos, Quaternion.identity);
        }
    }
    static void Postfix(BuildingEntity __instance) {
        if (__instance != null)
            ChunkSleep.Watch(__instance);

        //OG: BuildingEntity.Update V5t4
        if (__instance.TryGetComponent(out Rigidbody2D rb) && !__instance.ignoreBodyOptimize) {
            BuildingOptimization(rb);
        }
    }

    static void BuildingOptimization(Rigidbody2D rb) {

        //OG: BuildingEntity.Update V6.0-pre5 , that one liner but extended and just upgraded for multiplayer

        if (!WorldGeneration.world.worldExists || !(Time.timeScale <= 5f)) {
            rb.bodyType = RigidbodyType2D.Static;
            return;
        }

        var inSimRange = SharedMain.WorldInSimRange(rb.position);

        rb.bodyType = inSimRange ? RigidbodyType2D.Dynamic : RigidbodyType2D.Static;

    }

    // a transpiler to set the distance check flag to always true, because it does not matter , i need to check it on my own in FreshItemDrop constructror + i also move the body optimizer to somewhere else, code copied cuz it stops physics outside the hosts view

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);

        int i = 0;

        while (!(codes[i].opcode == OpCodes.Ldfld && codes[i].ToString().Contains("ignoreBodyOptimize") )) { // find where it checks ignorebodyoptimize
            i++;
        }
        i++; // now its the brtrue

        var brture_on_bodyoptimizescopestart = i;

        var bodyoptimizescopeendlabel = (System.Reflection.Emit.Label)(codes[brture_on_bodyoptimizescopestart].operand) ;

        var brture_on_bodyoptimizescopeend = i+1;


        while (!(codes[i].opcode == OpCodes.Ldstr && codes[i].operand as string == "footstep/Rock/11") ) { // find where it plays the sound
            if (codes[i].labels.Contains(bodyoptimizescopeendlabel)) {
                brture_on_bodyoptimizescopeend = i ;
                if (log.verbose) log.l($"bodyoptimizescopeendlabel FOUND !!!!!!!");
            }

            i++;
        }

#if DEBUG
        if (log.verbose) log.l($"BuildingEntity Update TRANSPILER found the sound string {i}");
#endif

        while (!(codes[i].opcode == OpCodes.Stloc_1)) { // find where it sets the boolean
            i++;
        }

#if DEBUG
        if (log.verbose) log.l($"BuildingEntity Update TRANSPILER found where it sets the boolean {i}");
#endif

        int beginning = i;

        while (!(codes[beginning].opcode == OpCodes.Ldarg_0)) { // find the ldarg.0 
            beginning-- ;
        }

#if DEBUG
        if (log.verbose) log.l($"BuildingEntity Update TRANSPILER found ldarg.0  at {beginning}");
#endif

        codes[beginning].opcode = OpCodes.Ldc_I4_1;
        
        codes.RemoveRange(beginning+1,  i -1 - beginning);



        brture_on_bodyoptimizescopestart++;

        codes.RemoveRange(brture_on_bodyoptimizescopestart, brture_on_bodyoptimizescopeend - brture_on_bodyoptimizescopestart);


        if (log.verbose)  {
            log.l($"BuildingEntity Update TRANSPILER SUCCESS :) ");
        }
        return codes;
    }

}

// marks a client building that already played its death burst. the health byte stays dead until the destroy arrives.
sealed class ClientDeathFxPlayed : MonoBehaviour {
}





[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Start))]
public static class BuildingEntity_Start_MultiplayerPatch {
    public const string identified_bg_prefix = "KMPSR_"; // special resource tag prefix for the id

    static void Prefix(BuildingEntity __instance) {
        __instance.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
    }

    static void Postfix(BuildingEntity __instance) {
        if (!Net.IsRunning) return;
        
        if (__instance.TryGetComponent(out TrackerClimbableEntity ropetracker) && !string.IsNullOrEmpty(ropetracker.og_id)) {
            //OG: BuildingEntity.Start V510
            __instance.fullName = Locale.GetBuilding(ropetracker.og_id);
            if (!__instance.skipDescriptionSet) {
                __instance.description = Locale.GetBuilding(ropetracker.og_id + "dsc");
            }
        } else if (__instance.TryGetComponent(out TraderScript traderrrr)) {
            // traders all have same id (not representing resource name), but their "character" value is consistently pointing to the correct id
            __instance.id = "trader" + (traderrrr.character + 1).ToString();
        } else if (__instance.TryGetComponent(out SoundCannon sndcannon)) {
            __instance.GetOrAddComponent<TrackerSoundCannon>();
        } else if (__instance.GetComponent<Heater>()) {
            // i rewrote the heater to support multiple players
            __instance.GetOrAddComponent<HeaterMultiplayerRewrite>();
        } else if (__instance.TryGetComponent(out TurretScript turret)) {
            __instance.GetOrAddComponent<TrackerTurret>();
        } else if (__instance.TryGetComponent(out CorpseScript corpse)) {
            __instance.GetOrAddComponent<CorpseScriptMultiplayerRewrite>();
        } else if (__instance.TryGetComponent(out GunmineScript _gunminescripppptptttt)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = _gunminescripppptptttt;
        } else if (__instance.TryGetComponent(out StalactiteDropper _gffdgfdfdgStalactiteDropper)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = _gffdgfdfdgStalactiteDropper;
        } else if (__instance.TryGetComponent(out SpiderHandler SPIDAR)) {
            var spoodertracker = SPIDAR.GetOrAddComponent<TrackerSpiderHandler>();
        } else if (__instance.id == "background") {
            log.error($"DEV: Error, unknown background: {__instance} - id:({__instance.id}) at {(Vector2)__instance.transform.position} ");
        } else if (__instance.id == "shuttledoor" && __instance.transform.parent?.TryGetComponent(out ShuttleStartOpen ssoooooooooooooooooooo) == true) {
            __instance.GetOrAddComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>();
            return;
        }

        if (WorldGeneration.world && !WorldGeneration.world.generatingWorld && __instance.gameObject.activeInHierarchy && NetObjectRegistry.ObjectShouldBeSynced(__instance.gameObject)) {
            // same 3x3 the far pass uses. the 64-unit gate registered a floor the next slow pass then deleted.
            if (SharedMain.WorldInSimRange(__instance.transform.position))
                NetObjectRegistry.NewGO(__instance.gameObject);
        }
        
        
    }
}


[HarmonyPatch(typeof(SpiderHandler), nameof(SpiderHandler.PlayThreatMusic))]
static class SpiderHandler_PlayThreatMusic_MultiplayerPatch {
    static bool Prefix(SpiderHandler __instance) { // make it play the music if its actually within radius of local player.  cuz it gets triggered for any body 
        return KM.DistanceCheck(Camera.main.transform.position, __instance.transform.position, __instance.seeDistance);
    }
}




// fuck it, make the spider damage client-authoritative
[HarmonyPatch(typeof(SpiderHandler), nameof(SpiderHandler.CheckForLimbDamage))]
public static class SpiderHandler_CheckForLimbDamage_MultiplayerPatch {
    static bool Prefix(SpiderHandler __instance, ref Collision2D collision) { 
        if ( NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si) ) {

            Limb limb = Body.LimbFromObject(collision.gameObject, __instance.transform.position);
            if (limb != null && limb.TryGetNetBody(out var pb)) {
                if (!pb.IsBodyLocal()) { // wait for the players confirmation that they have been actually bit
                    if (pb.body.conscious) {// if theyre unconscious, server will handle the damage
                        return false;
                    }
                } else {
                    if (Net.IsClient && pb.body.conscious && __instance.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker)) { // client must tell the server if theyve been hit
                        //OG: SpiderHandler.CheckForLimbDamage V5t4 
                        if ((Vector2.Dot(((Vector2)collision.transform.position - collision.GetContact(0).point).normalized, __instance.transform.up) > __instance.minVectorDotToBite)) {

                            if (log.verbose) Plugin.log.LogInfo($"DEV: SpiderJustBitMe {si} ");

                            // so its guranteed that the body gets hit, tell the server about it
                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SpiderJustBitMeFuck, tracker.syncId, Util.GetLimbIndex(limb), false);

                            pb.SetNetHealthSyncIgnoreTime();

                            // and allow it to go through to bite his ass
                        }
                    }
				}

				pb.RollDamageRandom();

                pb.OnDamageEvent(new BodyDamageEvent(__instance.bld, pb, __instance.biteMuscleDamage));
			}
        }

		return true;
    }

    //OG: SpiderHandler.CheckForLimbDamage V5t4
    public static void Spider_BiteDamage(SpiderHandler spider, Limb limb ) {
        var spoodertracker = spider.GetOrAddComponent<TrackerSpiderHandler>();

        if (limb.TryGetNetBody(out var nb)) {
            nb.RollDamageRandom();
            nb.OnDamageEvent(new BodyDamageEvent(spider.bld, nb, spider.biteMuscleDamage));
        }

        if (spider is SpiderHandlerTBE) {
            spider.biteCooldown = 5; // cuz biteCoolToSet is set to 0 on elders brah
        } else {
            spider.biteCooldown = spider.biteCoolToSet;
        }

        limb.body.happiness -= spider.happinessLoss;
        spider.target = (spider.transform.position - limb.transform.position).normalized * 15f + spider.transform.position;
        spider.moveTime = spider.retreatMoveTime;
        spider.DamageLimb(limb);
        if (spider.hitConnected) {
            Limb[] connectedLimbs = limb.connectedLimbs;
            foreach (Limb limb2 in connectedLimbs) {
                spider.DamageLimb(limb2);
            }
        }

        if (Net.IsServer && nb != null && spoodertracker.tracker) {

            var writer = Net.CreateWriter(NetmsgId.CLIENT_SpiderAttackEffect);
            writer.Put(nb.netId);
            writer.Put(spoodertracker.tracker.syncId);
            writer.Put(spider.transform.position);
            writer.Put(spider.transform.eulerAngles.z);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThis(nb.netId));
            // WRITER END;
        }

    }


    [ServerReceiver((byte)NetmsgId.SERVER_SpiderJustBitMeFuck)]
    static void Server_SpiderJustBitMeFuck(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"SpiderJustBitMeFuck actually works lol {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out knetid spider_syncid);
        reader.Get(out byte limbid);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) &&
            body.conscious && // assuming that theyre fighting the creature, else server will do the damage on their own
            limbid < body.limbs.Count() && 
            NetObjectRegistry.TryGetSyncInfo(spider_syncid, out var si) && si.IsSpider()
        ) {

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"S: SpiderJustBitMeFuck");

            Spider_BiteDamage(si.spider, body.limbs[limbid]);


        }
    }


    // elder bite ignores the limb that was hit and rolls a random one.
    // the bitten client was rolling too, so a leg could come off and drop the pouch,
    // then the host snapshot put that leg back and tore the arm off instead.
    [HarmonyPatch(typeof(SpiderHandlerTBE), nameof(SpiderHandlerTBE.DamageLimb))]
    static class SpiderHandlerTBE_DamageLimb_MultiplayerPatch {
        static bool Prefix() {
            return Net.IsServer;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_SpiderAttackEffect,false)]
    static void Client_SpiderAttackEffect(knetid _, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"SpiderAttackEffect actually works lol  ");
#endif

        reader.Get(out knetid target_clientid);
        reader.Get(out knetid spider_syncid);
        reader.Get(out Vector2 pos);
        reader.Get(out float angle);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(target_clientid, out var plr, out var body) &&
            NetObjectRegistry.TryGetSyncInfo(spider_syncid, out var si) && si.IsSpider()
        ) {
            var spider = si.spider;

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.body.transform.position, $"C: SpiderAttackEffect");

            //OG: SpiderHandler.CheckForLimbDamage V5t4
            Sound.Play(spider.biteSound, spider.transform.position);
            body.eyeScareTime = 5f;
            spider.PlayThreatMusic();
            if (spider.clawAnim) {
                GameObject obj = UnityEngine.Object.Instantiate(Resources.Load<GameObject>("ClawAnim"));
                obj.transform.eulerAngles = new Vector3(0f, 0f, angle); // i changed this cuz fuck it
                obj.transform.position = spider.transform.position;
                obj.transform.SetParent(spider.transform);
                UnityEngine.Object.Destroy(obj, 5f);
            }

        }
    }

}


public class TrackerSpiderHandler : BaseComponentTracker<SpiderHandler> {
    
    protected override void TrackerAwake() { // called in Awake()
    }

    void LateUpdate() {
        if ( SharedMain.WorldInSimRange(transform.position) ) {
            if (!TryGetComponent(out SyncInfoGameObjectTracker tracker) && gameObject != null) {
                NetObjectRegistry.NewGO(gameObject);
            } 
        }
    }

    // AnimalDeath gets triggered only by SendMessage from the BuildingEntity
    public void AnimalDeath() {
        //OG: SpiderHandler.AnimalDeath V5t4
        
        // make the exp thing apply to all players
        foreach (var item in NetBody.GetBodiesInRadius(og.transform.position, 32f)) {
            if (!Util.IsBodyLocal(item.body)) {// PlayerCamera.main.body already gets the exp
                item.body.skills.AddExp(1, og.maxHealth / 100f);
            }
        }
    }
}







internal class ScavMPBuildingSynchronizer : CasualtiesTogetherSingleton {


    public static bool BuildingHasActivePhysics(BuildingEntity building) {
        // btw dont check if its sleeping , i dont need it to affect this
        if (building.TryGetComponent(out Rigidbody2D rb) && rb.bodyType == RigidbodyType2D.Dynamic) {
            return true;
        }
        if (building.TryGetComponent(out GrabberPlant gp)) {
            return true;
        }

        return false;
    }
    public static bool BuildingHasActivePhysics(SyncInfo si) {
        return BuildingHasActivePhysics(si.building);
    }


    public static Dictionary<string, GameObject> known_entities_with_nonunique_id = new ();
    public static Dictionary<string, string> TEMP_known_entities_with_nonunique_id_to_og = new();
    

    internal static int cur_buildings_check_index = 0;
    internal static List<BuildingEntity> cur_buildings_check = new List<BuildingEntity>();


    public static void RegisterNonUniqueIdBg(Transform ch, bool report_fail) {
        if (ch.TryGetComponent(out BuildingEntity build)) {
            if ((bool)ch.TryGetComponent(out Tilemap tilemmeep)) { // this is most likely a background yay!
                if (build.id == "background") {

                    var og_res_name = ch.parent.name;
                    var newName = BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix + og_res_name;
                    if (log.verbose)
#if !RELEASE
                        log.l($"Found BG: id:{build.id},\tparent:{ch.parent},\tRENAMED: {newName}\t{new StackTrace()}");
#else
                        log.l($"Found BG: id:{build.id},\tparent:{ch.parent},\tRENAMED: {newName}");
#endif


                    build.id = newName;

                    ScavMPBuildingSynchronizer.known_entities_with_nonunique_id[build.id] = build.gameObject;
                    ScavMPBuildingSynchronizer.TEMP_known_entities_with_nonunique_id_to_og[build.id] = og_res_name;

                    var shittracker = build.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
                    shittracker.is_a_background_tilemap = true;

                    build.name = build.id;

                    return;
                } else if (!build.id.StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix)) { // if doesnt start witrh prefix
                    if (log.verbose || (!log.verbose && !string.IsNullOrEmpty(build.id)))
                        log.error($"wtf is this building tilemap id? (its the part where i check backgrounds type shit): {build.id}");
                    return;
                }
            }
        }
        if (report_fail) {
            log.warn($"RegisterNonUniqueIdBg -> \"{ch?.name}\" wtf is this");
        }
    }


}














