using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.U2D.IK;
using Random = UnityEngine.Random;

namespace Together;







[HarmonyPatch(typeof(RadioactiveObject), nameof(RadioactiveObject.Update))]
static class RadioactiveObject_Update_MultiplayerPatch {

    static bool Prefix(RadioactiveObject __instance) {
        if (Net.IsRunning) {

            // the entire function is remade for multiplayer heree !
            foreach (var bodynpc in NetBody.all_instances) {
                var body = bodynpc.body;
                

                //OG: RadioactiveObject.Update V5t4 

                float num = Vector2.Distance(__instance.transform.position, body.transform.position) * 0.3f;
                float num2 = __instance.radAtZero * (1f / Mathf.Max(num * num, 1f));
                if (num2 > 0.1f) {
                    body.radiationSickness += num2 * Time.deltaTime;
                    PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(bodynpc, num2 * 0.5f);
                }

            }

            return false;
        }
        return true;
    }


}






[HarmonyPatch(typeof(XalorisScript), nameof(XalorisScript.OnWillRenderObject))]
public static class XalorisScript_OnWillRenderObject_MultiplayerPatch {

    public class Krokosha_XalorisScript_OverrideComponent : UnityEngine.MonoBehaviour {
        float lastTime = 0f;
        void Update() {
            //OG: XalorisScript.OnWillRenderObject V4
            if (Time.time - lastTime > 0.5f) {
                lastTime = Time.time;
                foreach (var obj in ScavPlayer.GetPlayerBodiesInRadius(transform.position, 5.5f)) {
                    obj.body.septicShock += 0.074f;
                }
            }
        }
    }

    static bool Prefix(XalorisScript __instance) {
        if (Net.IsRunning) {
            var tr = __instance.GetOrAddComponent<Krokosha_XalorisScript_OverrideComponent>();
            return false;
        }
        return true;
    }
}







[HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.Stab))]
static class SpikeStabberScript_Stab_MultiplayerPatch {
    static void Postfix(SpikeStabberScript __instance) {
        __instance.GetComponent<Animator>().speed = 1;
        if (Net.IsRunning) {
            if (NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var si)) {
                if (Net.IsServer) {
                    
                } else {
                    si.SetIgnoreTimeForRoundTrip();
                }
            }
        }
    }

}

[HarmonyPatch(typeof(JumpPadScript), nameof(JumpPadScript.OnCollisionEnter2D))]
internal static class JumpPadScript_OnCollisionEnter2D_MultiplayerPatch {
    internal class ShakerState {
        internal Vector2 pos;
        internal Vector2 vel;
        internal ShakerState(Shaker s) {
            pos = s.pos;
            vel = s.velocity;
        }
        internal void Apply(Shaker s) {
            s.pos = pos;
            s.velocity = vel;
        }
    }

    static float cooldownBefore;

    static bool Prefix(JumpPadScript __instance, Collision2D collision, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        cooldownBefore = __instance.cooldown;
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main.shaker);


        try {
            if (__instance.disabled || !(__instance.cooldown <= 0f) || !collision.rigidbody || collision.rigidbody.isKinematic) {
                
            } else {
                if (Util.TryGetBodyFromObject(collision.gameObject, out var bodyObj) && bodyObj.TryGetNetBody(out var nb)) {
                    nb.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), nb, 1));
                }
            }
        } catch (Exception e) { log.error(e); }

        // host ghost should not get launched. the owner already left the pad.
        return !TrapClaim.SkipRemote(collision.collider);
    }
    static void Postfix(JumpPadScript __instance, Collision2D collision, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        TrapClaim.JumpFired(__instance, cooldownBefore);
        if (collision.gameObject.TryGetComponent<Limb>(out var limb)) {
            if (!limb.IsBodyLocal()) { // reset the camera shake
                __state.Apply(PlayerCamera.main.shaker);
            }
        }
    }
}
[HarmonyPatch(typeof(CoilScript), nameof(CoilScript.Shock))]
internal static class CoilScript_Shock_MultiplayerPatch {
    static bool Prefix(CoilScript __instance, Limb limb, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main.shaker);

        if (TrapClaim.SkipRemote(limb))
            return false;

        try {
            if (__instance.cooldown <= 0f && limb.TryGetNetBody(out var nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), nb, 1));
            }
        } catch (Exception e) { log.error(e); }

        if (__instance.cooldown <= 0f && limb.IsBodyLocal())
            TrapClaim.ArmCoil(__instance);
        return true;
    }
    static void Postfix(CoilScript __instance, Limb limb, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        TrapClaim.FinishCoil(__instance, limb);
        if (!limb.IsBodyLocal()) { // reset the camera shake
            __state.Apply(PlayerCamera.main.shaker);
        }
    }
}

[HarmonyPatch(typeof(BearTrap), nameof(BearTrap.OnTriggerEnter2D))]
internal static class BearTrap_OnCollisionEnter2D_MultiplayerPatch {
    static int sprangId;

    static bool Prefix(BearTrap __instance, Collider2D other, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        sprangId = 0;
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main.shaker);

        if (TrapClaim.SkipRemote(other))
            return false;
        if (!__instance.activated)
            sprangId = __instance.GetInstanceID();

        try {
            if (!__instance.activated && other.TryGetComponent(out Limb component2) && component2.TryGetNetBody(out var nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), nb, 1));
            }
        } catch (Exception e) {
            log.error(e);
        }

        return true;
    }
    static void Postfix(BearTrap __instance, Collider2D other, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        if (other != null && other.TryGetComponent<Limb>(out var limb)) {
            if (sprangId == __instance.GetInstanceID() && limb.IsBodyLocal() && __instance.activated)
                TrapClaim.AfterLocalBear(__instance, limb);
            if (!limb.IsBodyLocal()) { // reset the camera shake
                __state.Apply(PlayerCamera.main.shaker);
            }
        }
    }
}


[HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.CheckStab))]
internal static class SpikeStabberScript_CheckStab_MultiplayerPatch {
    static bool Prefix(SpikeStabberScript __instance, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main.shaker);

        // the ray is late. the owning client already rolled this on contact.
        if (Net.IsRunning) {
            try {
                TrapClaim.SpikeFromAnim(__instance);
            } catch (Exception e) {
                log.error(e);
            }
            return false;
        }

        try {

            var array = System.Linq.Enumerable.ToHashSet(
                // OG: SpikeStabberScript.CheckStab V7.0.1
                Physics2D.RaycastAll(__instance.transform.position, __instance.transform.up, 6f)
                
                .Select(x => x.transform.GetComponent<Limb>()).Where(x => x != null).Select(x => x.GetNetBody()).Where(x => x != null));
            
            foreach (var item in array) {
                item.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), item, 1));
            }

        } catch (Exception e) {
            log.error(e);
        }
        return true;
    }
    static void Postfix(SpikeStabberScript __instance, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        if (!KM.DistanceCheck(__instance.transform.position, Camera.main.transform.position, 10)) { // reset the camera shake
            __state.Apply(PlayerCamera.main.shaker);
        }
    }
}








[HarmonyPatch(typeof(MineScript), nameof(MineScript.OnCollisionEnter2D))]
static class MineScript_OnCollisionEnter2D_MultiplayerPatch {
    static bool Prefix(MineScript __instance, Collision2D collision) {
        if (__instance.pressed) { // i dont want to trigger postfix on server again
            return false;
        }

        if (Net.IsRunning) {
            if (Net.IsClient) {
                // the click is the original. the claim waits out the same 0.8 the host counts.
                TrapClaim.ArmMine(__instance, collision.collider);
            } else {
                //OG: MineScript.OnCollisionEnter2D V5t5  only 4 lines  i need to properly check the distance here
                if ((bool)collision.collider.attachedRigidbody && !collision.collider.attachedRigidbody.isKinematic && NetBody.GetNearestBody(__instance.transform.position, true, false).Item2 < 64f*64f) {
                    __instance.pressed = (true);
                    Sound.Play("mine", __instance.transform.position);
                    __instance.GetComponent<SpriteRenderer>().sprite = __instance.pressedSprite;

                    // tell everyone that mine was pressd
                    var writer = Net.CreateWriter(NetmsgId.CLIENT_MineStepped);

                    writer.Put((Vector2)__instance.transform.position);

                    Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);

                    // WRITER END;
                }
                return false;
            }
        }


        return true;
    }
}


public class TrackerTurret : BaseComponentTracker<TurretScript> {

    bool client_forceshoot = false;

    bool last_didBeep = false;
    bool last_didShoot = false;

    public float timeSinceFired {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return og.timeSinceFired; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set { og.timeSinceFired = value; } }
    public float beepTime {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return og.beepTime; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set { og.beepTime = (value); } }
    public bool didShoot {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return og.didShoot; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set { og.didShoot = (value); } }



    protected override void TrackerAwake() {

    }

    public void NudgeShoot() {
        if (didShoot) {
            return;
        }
        og.didBeep = true;
        beepTime = 0.5f;
        didShoot = false;
    }
    public void NudgeBeep(bool isVisible) {
        if (!og.didBeep || (beepTime >= 0.5f && timeSinceFired > 14f)) {
            last_didBeep = false;
            didShoot = false;
            beepTime = (isVisible ? 0f : (-0.5f));
            Sound.Play("turretsee", base.transform.position, twoDimensional: true, pitchShift: false);
            og.didBeep = true;
        }
    }



    void Update() {
        if (!Net.IsRunning) return;

        float dist = 999999;


        SyncInfo si = null;
        if (Net.IsClient) { // make the turret not shoot if im client, only when specifically requested by the server 

            dist = KM.DistanceSqr(PlayerCamera.main.transform.position, this.transform.position);


            //if (!client_forceshoot) {
            //    didShoot = false;
            //    beepTime = 0f;
            //} 
            //if (client_forceshoot && didShoot) {
            //    client_forceshoot = false;
            //}

            NetObjectRegistry.SyncRegistry.TryGetValue(gameObject, out si);
            
        } else {

            dist = NetBody.GetNearestBody(this.transform.position, true, false).Item2;

            if (dist < 10000) {
                // just register it
                if (NetObjectRegistry.TryGetSyncInfoOrRegister(gameObject, out si)) {
                }

            }
        }




        if (!last_didBeep && og.didBeep) {
            if (Net.IsClient) {

                var writer = Net.CreateWriter(NetmsgId.SERVER_TurretDoBeep);
                writer.Put((Vector2)transform.position);
                writer.Put(og.spr.isVisible);
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                // WRITER END;
                
            } else {
                NetObjectRegistry.SafeNewGO(gameObject);

                if (si != null || NetObjectRegistry.TryGetSyncInfo(gameObject,out si)) {

                    var writer = Net.CreateWriter(NetmsgId.CLIENT_TurretDoBeep_Relay);
                    writer.Put(si.syncId);
                    writer.Put((bool)(beepTime >= 0));
                    Net.Server_SendToClients( DeliveryMethod.ReliableUnordered , writer, ServerMain.AllClientIdsExceptHost);
                    // WRITER END;

                }

            }
        }




        if (!last_didShoot && didShoot) {

            if (Net.IsServer) {
                if (si != null || NetObjectRegistry.TryGetSyncInfo(gameObject, out si)) {
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_TurretDoShoot_Relay, si.syncId, true, false);
                }
            } else {
                if ( ScavPlayer.LocalPlayer.TryGetNetBody(out var nb)) {
                    nb.SetNetHealthSyncIgnoreTime();
                }
            }
        }


        last_didBeep = og.didBeep;
        last_didShoot = didShoot;
    }


    [ServerReceiver((byte)NetmsgId.SERVER_TurretDoBeep)]
    static void Server_TurretDoBeep(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        //log.l($"TurretDoBeep actually works lol {clientId} ");
#endif
        reader.Get(out Vector2 turretpos);
        reader.Get(out bool isVisible);


        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) && body.alive ) {

            if (KM.DistanceCheck_PreSqr(turretpos, (Vector2)body.transform.position, 10000f)) {


                // tur   -39 -> -41
                // plr down: -46
                // plr up: -33
                // so about 7 blocks

                if (Math.Abs(turretpos.y - body.transform.position.y) > 8 ) { // if hes not on the same level as the turret, tell its sus

                    log.l($"TurretDoBeep  turret IS NOT ON THE SAME LEVEL AS THE PLR  BRAAHH !!! ");
                    return;
                }


                var touching = Physics2D.OverlapCircleAll(turretpos, 1f);

                foreach (var item2 in touching) {

                    if (item2.TryGetComponent<TurretScript>(out var i) && (Vector2)i.transform.position == turretpos) {
                        var tst = i.GetOrAddComponent<TrackerTurret>();
                        tst.NudgeBeep(isVisible);

                        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(turretpos, $"S: TurretDoBeep {plr} ");
                        return;
                    } 
                }

                log.l($"TurretDoBeep  turret was not found !!!!!!!!!!!!!!!!!!!!!!!! ");

            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TurretDoBeep_Relay)]
    static void Client_TurretDoBeep_Relay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        //log.l($"CLIENT TurretDoBeep_Relay actually works lol {_serverid} ");
#endif
        reader.Get(out knetid turretid);
        reader.Get(out bool isVisible);

        if (NetObjectRegistry.TryGetSyncInfo(turretid, out var si)) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"C: TurretDoBeep_Relay  ");

            var tst = si.go.GetOrAddComponent<TrackerTurret>();
            tst.NudgeBeep(isVisible);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TurretDoShoot_Relay)]
    static void Client_TurretDoShoot_Relay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid turretid);

        if (NetObjectRegistry.TryGetSyncInfo(turretid, out var si) ) {

            var tst = si.go.GetOrAddComponent<TrackerTurret>();

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(si.go.transform.position, $"C: TurretDoShoot_Relay cur_shoot:{tst.didShoot}  ");

            tst.NudgeShoot();

        }
    }

}




    // i basically rewrote the entire sound cannon here
public class TrackerSoundCannon : BaseComponentTracker<SoundCannon> {

    bool announced_charge = false;


    public bool charging = false;
    public bool spent = false;
    public float endAngle;
    bool hostLocked;
    public float checkTime = 1f;
    public float maxDistance = 50f;
    public float chargeTime = 0f;


    // host spent state. barrel z is the angle it froze on.
    public void soundcannon_spent(float angle) {
        endAngle = angle;
        hostLocked = true;
        (og).spent = true;
        PointBarrel(angle);
        // a charge that already started still gets to fire
        if (charging && chargeTime > 1f && !spent)
            return;
        spent = true;
        charging = false;
    }

    void PointBarrel(float angle) {
        if (transform.childCount == 0)
            return;
        transform.GetChild(0).eulerAngles = new Vector3(0f, 0f, angle);
    }

    void PullHostSpent() {
        if (Net.IsServer || hostLocked)
            return;
        if (NetObjectRegistry.TryGetSyncInfo(gameObject, out var si) && si.specialdata1.Value != 0)
            soundcannon_spent(si.specialdata2.Value);
    }

    void PublishSpent() {
        if (!Net.IsServer)
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(gameObject, out var si))
            return;
        si.specialdata1.Value = 1;
        si.specialdata2.Value = endAngle;
    }


    protected override void TrackerAwake() {
        
    }



    //OG: SoundCannon.CanSeePos    copypasted cuz shits private
    public static bool CanSeePos(Transform me, Vector2 to) {
        RaycastHit2D[] array = Physics2D.LinecastAll(me.transform.position, to, LayerMask.GetMask("Ground"));
        bool flag = false;
        RaycastHit2D[] array2 = array;
        foreach (RaycastHit2D raycastHit2D in array2) {
            if (raycastHit2D.transform != me.transform) {
                flag = true;
                break;
            }
        }
        return !flag;
    }


    // returns just closest body if none are visible
    public static NetBody GetClosestVisibleBody(Transform me) {

        NetBody closestbody = null;
        var closestdist = 99999999999f;

        NetBody closestvisiblebody = null;
        var closestvisibledist = 9999999999f;

        foreach (var item in ScavPlayer.BodyToPlayerDict) {

            var headpos = item.Value.netBody.GetHeadPos();

            var dist = KM.DistanceSqr(me.position,headpos);

            if (dist < closestdist ) {
                closestdist = dist;
                closestbody = item.Value.netBody;
            }

            if (dist < closestvisibledist && CanSeePos(me,headpos)) {
                closestvisibledist = dist;
                closestvisiblebody = item.Value.netBody;
            }
        }

        return closestvisiblebody ?? closestbody;
    }
    


    // the original  Update() is disabled, because the entire funcion is inflexible
    void Update() {
        PullHostSpent();

        if (spent) {
            if (hostLocked)
                PointBarrel(endAngle);
            return;
        }

        if (!WorldGeneration.world || WorldGeneration.world.generatingWorld) {
            return;
        }
        if (!Net.IsRunning) {
            Destroy(this);
        }


        checkTime -= Time.deltaTime;
        if (checkTime < 0f) {
            checkTime = Net.IsClient ? 0.4f : 0.1f;

            if (!NetObjectRegistry.TryGetSyncInfo(gameObject, out var _si) && ScavPlayer.GetDistanceToNearestLivingPlayer(transform.position).Item2 < 128f) {
#if DEBUG
                log.l($"DELETEME: Force registering sound cannon ");
#endif
                NetObjectRegistry.NewGO(gameObject);
                return;
            }

            if ( !charging && !spent) {

                foreach (var item in ScavPlayer.BodyToPlayerDict) {

                    var headpos = item.Key.limbs[(int)Body.LimbNum.Head].transform.position;

                    if (KM.DistanceCheck(transform.position, headpos, maxDistance) && CanSeePos(this.transform,headpos)) {
                        charging = true;
                        break;
                    }
                }

            }
        }

        if (!NetObjectRegistry.TryGetSyncInfo(gameObject, out var si)) {
            return;
        }

        if (!charging) {
            return;
        }
        if (!announced_charge) {
            Sound.Play("sonarcharge", base.transform.position, twoDimensional: true, pitchShift: false);

            if (Net.IsRunning) {
                if (Net.IsClient) {
                } else {
#if DEBUG
                    log.l($"DELETEME: sending SoundCannonISCHARGING!!!! ");
#endif
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_SoundCannonISCHARGING____, si.syncId, true, false);

                }
            }
            announced_charge = true;

        }
        chargeTime += Time.deltaTime;


        var closestplayerbody = GetClosestVisibleBody(this.transform);
        var closestbody = closestplayerbody.body;

        Vector2 nearestplrpos = closestbody.transform.position;
        Vector2 nearestheadpos = closestplayerbody.GetHeadPos();
        
        var goallookdir = -KM.NormalTo(nearestheadpos,base.transform.position, out var distance);

        if (hostLocked)
            PointBarrel(endAngle);
        else
            base.transform.GetChild(0).eulerAngles = new Vector3(0f, 0f, Mathf.LerpAngle(base.transform.GetChild(0).eulerAngles.z, Vector2.SignedAngle(Vector2.up, goallookdir), Time.deltaTime * 4f));

        var current_lookdir = base.transform.GetChild(0).up;


        if ((chargeTime < 5f)) {
            return;
        }
        spent = true;
        charging = false;

        (og).spent = (true);
        if (hostLocked)
            PointBarrel(endAngle);
        else if (Net.IsServer) {
            if (transform.childCount > 0)
                endAngle = transform.GetChild(0).eulerAngles.z;
            PublishSpent();
        }
        

        foreach (var plr in ScavPlayer.AllLivingPlayers) {

            var body = plr.body;
            var headpos = plr.netBody.GetHeadPos();


            var gaollookdirforthisplr = -KM.NormalTo(headpos, base.transform.position, out var distance_to_this_plr);

            var dot = Vector2.Dot(current_lookdir, gaollookdirforthisplr);

#if DEBUG
            log.l($"DELETEME: FIRING SOUND CANNON!!!!!!!!!: {plr.playerName} dot: {dot} ");
#endif

            var insameenvironment = FluidManager.main.HasLiquid(WorldGeneration.world.WorldToBlockPos(base.transform.position)) == body.inWater;


            if ( CanSeePos(this.transform, headpos) && dot > 0.55f && insameenvironment) {
                
                //OG: SoundCannon.Update

                if (body.bodyAffect.wasWater) {
                    if (plr.IsLocal)
                        Sound.Play("sonarmegaouchfixed", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                    body.hearingLoss = 100f;
                    body.internalBleeding += 100f;
                    body.happiness -= 10f;
                    body.shock = 100f;
                    body.limbs[0].pain = 100f;
                    body.brainHealth -= 20f;
                    if (plr.IsLocal) { 
                        PlayerCamera.main.shaker.Shake(3000f);
                        PlayerCamera.main.bonusAbber = -70f;
                        PlayerCamera.main.globalMuteTime += 120f;
                    }
                } else {
                    if (plr.IsLocal)
                        Sound.Play("sonarouch", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                    body.hearingLoss += 60f;
                    body.internalBleeding += UnityEngine.Random.Range(20f, 30f);
                    body.happiness -= 2f;
                    body.shock = 65f;
                    body.limbs[0].pain = 80f;
                    body.brainHealth -= 5f;
                    if (plr.IsLocal) {
                        PlayerCamera.main.shaker.Shake(1000f);
                        PlayerCamera.main.bonusAbber = -30f;
                        PlayerCamera.main.globalMuteTime += 30f;
                    }
                }
                if (plr.IsLocal) {
                    Sound.Play("tinnitus", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                }
                body.consciousness -= 69f;
                body.eyeCloseTime = 10f;
                body.eyeScareTime = 25f;
                body.adrenaline += 100f;
                body.respiratoryRate = 0f;
                Limb[] limbs = body.limbs;
                foreach (Limb obj in limbs) {
                    obj.muscleHealth -= UnityEngine.Random.Range(20f, 30f);
                    obj.pain += 40f;
                }

            } else {
                //OG: SoundCannon.Update
                body.consciousness -= 30f;
                body.eyeScareTime = 4f;
                if (plr.IsLocal) {
                    Sound.Play("sonarouchblocked", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                    PlayerCamera.main.shaker.Shake();
                    PlayerCamera.main.globalMuteTime += 3f;
                }
            }
        }


    }
}


[HarmonyPatch(typeof(SoundCannon), "Update")]
public static class SoundCannon_Update_MultiplayerPatch {
    public static bool Prefix(SoundCannon __instance) {
        if (Net.IsRunning) {
            __instance.GetOrAddComponent<TrackerSoundCannon>();
            return false; // i completely reimplemented it in my tracker component 
        }
        return true;
    }
}






[HarmonyPatch(typeof(StalactiteDropper), nameof(StalactiteDropper.Drop))]
public static class StalactiteDropper_Drop_MultiplayerPatch {

    static void Prefix(StalactiteDropper __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            if (NetObjectRegistry.TryGetSyncInfoOrRegister(__instance,out var si) ) {
                si.SetIgnoreTimeForRoundTrip(1.0f + Vector2.Distance(__instance.transform.position,PlayerCamera.main.transform.position) * 0.3f ); // let it hit the local player , ima make the damage client authoritative 
            
            }
        }
    }

}

[HarmonyPatch(typeof(StalactiteDropper), nameof(StalactiteDropper.OnWillRenderObject))]
public static class StalactiteDropper_OnWillRenderObject_MultiplayerPatch {

    static void Prefix(StalactiteDropper __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient() && 
            __instance.GetComponent<Rigidbody2D>().bodyType != RigidbodyType2D.Dynamic // check if its still attached to the ceiling
        ) {

            //OG: StalactiteDropper.OnWillRenderObject V5t4 
            RaycastHit2D raycastHit2D = Physics2D.Raycast(__instance.transform.position, Vector2.down, 40f, LayerMask.GetMask("Body", "Limb"));
            if ((bool)raycastHit2D ) {

                if (NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var si)) {

                    // check if its actually local player
                    if (raycastHit2D.collider.TryGetComponent<Limb>(out var l) && Util.IsBodyLocal(l.body)) {
                    } else if (raycastHit2D.collider.TryGetComponent<Body>(out var b) && Util.IsBodyLocal(b)) {
                    } else {
                        return; // if not a local body , return right now 
                    }


                    var cd = __instance.GetOrAddComponent<ScavTraps.DamagingCrateCooldownThing>();
                    if (!cd.stalagtite_dropped) {
                        cd.stalagtite_dropped = true;

                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_ITriggeredStalactiteDropperDropCounter, si.syncId,false);
                    }


                }
            }
            
        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_ITriggeredStalactiteDropperDropCounter)]
    static void ServerReceiver_ITriggeredStalactiteDropperDropCounter(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid stalagtite_syncid);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) && pb.alive &&
            NetObjectRegistry.TryGetSyncInfo(stalagtite_syncid, out var si) && si.go.TryGetComponent<StalactiteDropper>(out var stlgt)
        ) {
            if (pb.transform.position.y < stlgt.transform.position.y && pb.transform.position.y + 50 > stlgt.transform.position.y) {
                if (Mathf.Abs(pb.transform.position.x - stlgt.transform.position.x) < 5 ) {

#if !RELEASE
                    log.l($"SERVER, ITriggeredStalactiteDropperDropCounter actually works lol {DebugHelp.GetPlayerFullDebugString(clientId)}  si:{si} ");
#endif

                    stlgt.counting = true;
                }
            }

        }
    }



}


[HarmonyPatch(typeof(DamagingCrate), nameof(DamagingCrate.OnCollisionEnter2D))]
public static class DamagingCrate_OnCollisionEnter2D_MultiplayerPatch {


    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
        var codes = new List<CodeInstruction>(instructions);

        int i = 10;

        while (!( codes[i-1].ToString().Contains("LimbFromObject") && codes[i ].opcode == OpCodes.Stloc_1 )) { // find the part where it gets the limb
            i++;
        }

        while (!(
            codes[i + 1].opcode == OpCodes.Ldarg_0
            &&
            codes[i + 2].ToString().Contains("DamagingCrate") && codes[i + 2].ToString().Contains("type") ) ) { // find the part where it finished that f statemenmt
            i++;
        }

#if TESTING
        if (log.verbose) log.l($"DamagingCrate OnCollisionEnter2D TRANSPILER LimbFromObject and stloc.1 {i}"); // should be 59
#endif

        var pfsfieldsearch = i;

        while (!(codes[pfsfieldsearch].ToString().Contains("prevFrameSpeed")) ) { // find the part where it gets the limb
            pfsfieldsearch++;
        }
        var prevFrameSpeed_field = codes[pfsfieldsearch].operand;
        


        //ldarg.0
        //ldloc.1
        //ldarg.0
        //ldfld	valuetype [UnityEngine.CoreModule]UnityEngine.Vector2 DamagingCrate::prevFrameSpeed
        //call	bool [KrokoshaCasualtiesMP]KrokoshaCasualtiesMP.ScavTraps::DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck(object, class Limb, valuetype [UnityEngine.CoreModule]UnityEngine.Vector2)
        //brfalse.s	67 (00D8) ldarg.0 
        //ret



        var newInstructions = new List<CodeInstruction>();


        newInstructions.Add(new CodeInstruction(OpCodes.Ldarg_0));

        newInstructions.Add(new CodeInstruction(OpCodes.Ldloc_1));

        newInstructions.Add(new CodeInstruction(OpCodes.Ldarg_0));

        newInstructions.Add(new CodeInstruction(OpCodes.Ldfld, prevFrameSpeed_field));

        newInstructions.Add(new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(ScavTraps), nameof(ScavTraps.DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck))));

        var endofpatch_label = il.DefineLabel();
        codes[i + 1].labels.Add(endofpatch_label);

        newInstructions.Add(new CodeInstruction(OpCodes.Brfalse_S, endofpatch_label));

        newInstructions.Add(new CodeInstruction(OpCodes.Ret));

        codes.InsertRange(i + 1, newInstructions);

#if TESTING
        if (log.verbose) {
            log.l($"DamagingCrate OnCollisionEnter2D TRANSPILER SUCCESS :)  ");
        }
#endif
        return codes;
    }


}





public class ScavTraps : CasualtiesTogetherSingleton {

    public class DamagingCrateCooldownThing : MonoBehaviour {
        public bool stalagtite_dropped = false;
        public double last_hit = -10000000;
    }

    static readonly Dictionary<int, double> hostCrateHit = new Dictionary<int, double>();

    public static bool HostCrateReady(knetid crate, knetid player) {
        int key = (crate.id << 16) ^ player.id;
        double now = Time.realtimeSinceStartupAsDouble;
        if (hostCrateHit.TryGetValue(key, out var last) && now - last < 1.0)
            return false;
        hostCrateHit[key] = now;
        if (hostCrateHit.Count > 400)
            hostCrateHit.Clear();
        return true;
    }


    //OG: DamagingCrate.OnCollisionEnter2D V5t4 
    // called only from transpiled section
    // return false to continue original function
    public static bool DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck(object ddddddddd, Limb limb, Vector2 prevFrameSpeed) {
        var dmgcrate = ddddddddd as DamagingCrate;


        var islocal = Util.IsBodyLocal(limb.body);
        if (!islocal) {
            limb.body.Ragdoll(); // maybe just ragdoll em just for the illusion 
            return true;
        } else {
            if (Net.IsRunning && !NetObjectRegistry.TryGetSyncInfoOrRegister(dmgcrate, out var si)) {
                // if is client it will just get deleted
                if (Net.IsClient) {
                    return true;
                }
            }


            var cd = dmgcrate.GetOrAddComponent<DamagingCrateCooldownThing>();

            var timePassed = Time.realtimeSinceStartupAsDouble - cd.last_hit;
            // make a cooldown instead of the deleter

            cd.last_hit = Time.realtimeSinceStartupAsDouble;
            if (timePassed < 1f) {
                return true;
            }


        }

#if !RELEASE
        log.l($"running DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck {limb} - {NetObjectRegistry.DebugStringGameObject(dmgcrate.gameObject)}");
#endif

        var prevspeed = prevFrameSpeed.magnitude;

        DamagingCrate_OnCollisionEnter2D_TheDamagingPartStolen(dmgcrate,limb,prevspeed);
        return true;
    }

    //OG: DamagingCrate.OnCollisionEnter2D V5t5 
    public static void DamagingCrate_OnCollisionEnter2D_TheDamagingPartStolen(DamagingCrate dmgcrate, Limb limb, float prevspeed) {
        
        var islocal = Util.IsBodyLocal(limb.body);

        if (dmgcrate.type == 1) {
            if (limb.isHead) {
                limb.body.consciousness = 20f / limb.GetArmorReduction();
            }
            limb.muscleHealth -= prevspeed * 0.75f / limb.GetArmorReduction();
            limb.skinHealth -= prevspeed / limb.GetArmorReduction();
            limb.bleedAmount += prevspeed * 0.1f / limb.GetArmorReduction();
            limb.DamageWearables(0.1f);
        } else {
            if (limb.isHead) {
                limb.body.consciousness = 0f / limb.GetArmorReduction();
            }
            limb.muscleHealth -= prevspeed * 1.25f / limb.GetArmorReduction();
            limb.skinHealth -= prevspeed * 0.6f / limb.GetArmorReduction();
            limb.bleedAmount += prevspeed * 0.05f / limb.GetArmorReduction();
            if (Random.value < 0.5f && prevspeed > 25f) {
                limb.Dislocate();
            }
            if (Random.value < 0.5f && prevspeed > 45f) {
                limb.BreakBone();
            }
            limb.DamageWearables(0.25f);
        }
        
        Sound.Play(dmgcrate.stalactiteHitSound, dmgcrate.transform.position, twoDimensional: islocal);
        if (Net.IsRunning) {

            BuildingEntity build = null;
            if (limb.TryGetNetBody(out var nb) && dmgcrate.TryGetComponent(out build)) {
#if !RELEASE
                log.warn($"DamaginCrate damage event actually works! {nb.ToSafeString()} {build.ToSafeString()}");
#endif
                nb.OnDamageEvent(new BodyDamageEvent(build, nb, prevspeed));
            }


            if (Net.IsClient) {
                if (islocal) {
                    if ( NetObjectRegistry.TryGetSyncInfoOrRegister(dmgcrate, out var si)) {
                        if (limb.TryGetNetBody(out var locnb)) {
                            locnb.SetNetHealthSyncIgnoreTime(0.05f);
                        }

#if !RELEASE
                        log.l($"CLIENT, sending DamagingCrate_OnCollisionEnter2D_hit {prevspeed} ");
#endif

                        var writer = Net.CreateWriter(NetmsgId.SERVER_DamagingCrate_OnCollisionEnter_D_hit);
                        writer.Put(si.syncId);
                        writer.Put(prevspeed);
                        writer.Put(Util.GetLimbIndex(limb));
                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                        // WRITER END;
                    }
                }
            } else if (limb.TryGetNetBody(out var pb)) {
                ServerMain.Server_AnnounceSound(dmgcrate.transform.position, dmgcrate.stalactiteHitSound, ServerMain.GetListOfClientIdsExceptThisAndHost(pb.netId));
            
                // force drop the stalagtite if it wasnt dropped yet
                if (dmgcrate.TryGetComponent<StalactiteDropper>(out var stalagtite) && dmgcrate.GetComponent<Rigidbody2D>().bodyType != RigidbodyType2D.Dynamic) {
                    stalagtite.Drop();
                }
            }


        }
        

        limb.body.Ragdoll();

        //if (islocal) { // nah never delete it 
        //    Object.Destroy(dmgcrate);
        //}

    }


    [ServerReceiver((byte)NetmsgId.SERVER_DamagingCrate_OnCollisionEnter_D_hit)]
    static void ServerReceiver_ClientGotHitBy_DamagingCrate_OnCollisionEnter2D(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid crate_syncid);
        reader.Get(out float prevspeed);
        reader.Get(out byte limbindex);

        if (MedicalSync.TryGetPlayerAndLimb(clientId, limbindex, out var pb, out var limb) && pb.alive && Util.IsFinite(prevspeed) && prevspeed > 4f &&
            NetObjectRegistry.TryGetSyncInfo(crate_syncid, out var si) && si.go.TryGetComponent(out DamagingCrate dmgcrate)
        ) {
#if TESTING
            log.devevent($"Server recieved: DamagingCrate hit: {si} -> {DebugHelp.GetPlayerFullDebugString(clientId)} force:{Mathf.Round(prevspeed)}", dmgcrate.transform.position);
#endif

            if (KM.DistanceCheck(dmgcrate.transform.position, limb.GetPosition(), 32)) {
                if (!ScavTraps.HostCrateReady(crate_syncid, clientId))
                    return;
                DamagingCrate_OnCollisionEnter2D_TheDamagingPartStolen(dmgcrate, limb, Mathf.Abs(prevspeed));
                if (limb.TryGetNetBody(out var hitNb))
                    TrapClaim.Publish(hitNb);
                return;
            } else {
#if TESTING
                log.sus($"TOO FAR? DamagingCrate hit: crateId: {crate_syncid} prevspeed:{prevspeed} limbindex:{limbindex}");
#endif
            }
        }
#if TESTING
        log.sus($"Weird DamagingCrate hit: crateId: {crate_syncid} prevspeed:{prevspeed} limbindex:{limbindex}");
#endif
    }






    [ClientReceiver((byte)NetmsgId.CLIENT_SoundCannonISCHARGING____)]
    static void ClientReceiver__SoundCannonISCHARGING(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid syncid);

        if (NetObjectRegistry.TryGetSyncInfo(syncid, out var si) && si.go.TryGetComponent(out TrackerSoundCannon tr) && !tr.spent) {
            tr.charging = true;
        }

    }


    // fun fact: this receiver was made wayy before the object registry system, thats why its using a position instead of syncid 
    [ClientReceiver((byte)NetmsgId.CLIENT_MineStepped)]
    static void ClientReceiver__MineStepped(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out Vector2 minepos);

#if DEBUG
            log.l($"DELETEME: received MineStepped {minepos} ");
#endif

        Collider2D[] array = Physics2D.OverlapCircleAll(minepos, 2.0f);
        foreach (Collider2D obj2 in array) {
            if (obj2.TryGetComponent(out MineScript component)) {

                //OG: MineScript.OnCollisionEnter2D V4 
                if (!(bool)(component).pressed) {
                    // disable the mine, and make its sprite like this
                    Sound.Play("mine", minepos);
                    component.GetComponent<SpriteRenderer>().sprite = component.pressedSprite;
                    component.exploded = (true);
                }
                break;
            }
        }

        // explosion is triggered by server separately

    }



}
