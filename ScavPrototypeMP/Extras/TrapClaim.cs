using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

// owning client rolls the trap. host applies that claim and publishes health.
// contact traps still have to be within 32. turrets and explosions do not.
internal static class TrapClaim {
    const byte KindBear = 1;
    const byte KindSpike = 2;
    const byte KindCoil = 3;
    const byte KindJump = 4;
    const byte KindTurret = 5;
    const byte KindBoom = 6;
    const byte KindGun = 7;

    const float ContactReach = 32f;
    const float SpikeWindow = 2f;
    const float BearWindow = 0.75f;
    const float BoomWindow = 2f;

    class Window {
        public float until;
        public HashSet<ushort> who;
    }

    class Boom {
        public knetid id;
        public float until;
        public ExplosionParams param;
        public HashSet<ushort> who;
    }

    static readonly Dictionary<int, Window> windows = new Dictionary<int, Window>();
    static readonly Dictionary<int, float> turretNext = new Dictionary<int, float>();
    static readonly Dictionary<int, float> stabbed = new Dictionary<int, float>();
    static readonly List<Boom> booms = new List<Boom>();
    static readonly List<Collider2D> hidden = new List<Collider2D>();
    static readonly List<bool> hiddenWas = new List<bool>();

    internal static knetid pendingBoom;
    static CoilScript armedCoil;
    static ushort turretMask;
    static knetid turretId;
    static bool turretClaim;
    static bool gunShot;
    static knetid gunId;
    static NetBody turretLocal;
    static Vector2 lastLocalBoom;
    static float lastLocalBoomTime;
    static int hideDepth;

    internal static bool SkipRemote(Component hit) {
        if (!Net.IsRunning || hit == null)
            return false;
        if (!Util.TryGetBodyFromObject(hit, out var body) || body == null)
            return false;
        return !Util.IsBodyLocal(body);
    }

    internal static bool IsLocal(Component hit, out Body body) {
        body = null;
        if (hit == null || !Util.TryGetBodyFromObject(hit, out body) || body == null)
            return false;
        return Util.IsBodyLocal(body);
    }

    static void Send(byte kind, knetid id, ushort mask) {
        if (!Net.IsClient || id.id == 0)
            return;
        var writer = Net.CreateWriter(NetmsgId.SERVER_TrapClaim);
        writer.Put(kind);
        writer.Put(id);
        writer.Put(mask);
        writer.Put(0f);
        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }

    // one shot when a trap is spent. each machine then ticks the float itself.
    static void Relay(byte kind, knetid id, float cooldown) {
        if (!Net.IsServer || id.id == 0)
            return;
        var writer = Net.CreateWriter(NetmsgId.CLIENT_TrapState);
        writer.Put(kind);
        writer.Put(id);
        writer.Put(cooldown);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    static void RelayOf(byte kind, Component trap, float cooldown) {
        if (trap != null && NetObjectRegistry.TryGetSyncInfo(trap, out var si))
            Relay(kind, si.syncId, cooldown);
    }

    static void Ignore(Body body) {
        if (body != null && body.TryGetNetBody(out var nb))
            nb.SetNetHealthSyncIgnoreTime();
    }

    internal static void Publish(NetBody nb) {
        if (nb == null || !Net.IsServer)
            return;
        MedicalSync.Server_QueueSendCharacterHealth(nb, true);
    }

    static bool Near(Component trap, Body body) {
        if (trap == null || body == null)
            return false;
        return KM.DistanceCheck(trap.transform.position, body.transform.position, ContactReach);
    }

    static bool TakeWindow(byte kind, knetid source, knetid player, bool spent, float window) {
        int key = (kind << 16) ^ source.id;
        float now = Time.unscaledTime;
        if (!windows.TryGetValue(key, out var w) || now > w.until) {
            if (spent)
                return false;
            w = new Window { until = now + window, who = new HashSet<ushort>() };
            windows[key] = w;
        }
        if (windows.Count > 400)
            windows.Clear();
        return w.who.Add(player.id);
    }

    static void OpenWindow(byte kind, knetid source, bool spent, float window) {
        int key = (kind << 16) ^ source.id;
        float now = Time.unscaledTime;
        if (!windows.TryGetValue(key, out var w) || now > w.until) {
            if (spent)
                return;
            windows[key] = new Window { until = now + window, who = new HashSet<ushort>() };
        }
    }

    static void Hurt(Component src, Body body) {
        if (src == null || body == null || !body.TryGetNetBody(out var nb))
            return;
        var building = src.GetComponent<BuildingEntity>();
        if (building == null)
            building = src.GetComponentInParent<BuildingEntity>();
        if (building != null)
            nb.OnDamageEvent(new BodyDamageEvent(building, nb, 1));
    }

    static ushort Bit(Limb limb) {
        int index = Util.GetLimbIndex(limb);
        if (index < 0 || index > 15)
            return 0;
        return (ushort)(1 << index);
    }

    static void Each(Body body, ushort mask, System.Action<Limb> fn) {
        if (body == null || body.limbs == null)
            return;
        int i = 0;
        foreach (var limb in body.limbs) {
            if (i > 15)
                break;
            if (limb != null && (mask & (1 << i)) != 0)
                fn(limb);
            i++;
        }
    }

    internal static void AfterLocalBear(BearTrap trap, Limb limb) {
        if (trap == null || limb == null || !limb.IsBodyLocal())
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(trap, out var si))
            return;
        Ignore(limb.body);
        if (Net.IsClient)
            Send(KindBear, si.syncId, Bit(limb));
        else if (limb.TryGetNetBody(out var nb)) {
            Publish(nb);
            Relay(KindBear, si.syncId, 0f);
        }
    }

    static void ApplyBear(BearTrap trap, Limb limb) {
        if (trap == null || limb == null)
            return;
        Hurt(trap, limb.body);
        limb.body.shock = 100f;
        limb.pain += 100f;
        limb.body.adrenaline += 80f;
        if (Random.Range(0f, 1f) < 0.5f)
            limb.BreakBone();
        float armor = limb.GetArmorReduction();
        limb.skinHealth -= Random.Range(70f, 100f) / armor;
        limb.muscleHealth -= Random.Range(50f, 100f) / armor;
        limb.bleedAmount += Random.Range(14f, 18f) / armor;
        limb.DamageWearables(0.9f);
        if (limb.isHead) {
            limb.body.consciousness = 0f;
            limb.body.brainHealth -= Random.Range(0f, 40f);
            if (Random.value > 0.6f)
                limb.body.RemoveEye();
        }
        if (Util.IsBodyLocal(limb.body)) {
            limb.body.Ragdoll();
            if (limb.rb != null)
                limb.rb.constraints = RigidbodyConstraints2D.FreezeAll;
            PlayerCamera.main.shaker.Shake(50f);
        }
        SpendBear(trap);
        if (Net.IsServer)
            RelayOf(KindBear, trap, 0f);
        Sound.Play("beartrap", trap.transform.position);
        Sound.Play("gore", limb.transform.position);
    }

    internal static void SpendBear(BearTrap trap) {
        if (trap == null || trap.activated)
            return;
        trap.activated = true;
        var sprite = trap.GetComponent<SpriteRenderer>();
        if (sprite != null && trap.closeSprite != null)
            sprite.sprite = trap.closeSprite;
        if (trap.transform.childCount > 0)
            Object.Destroy(trap.transform.GetChild(0).gameObject);
    }

    internal static void SpikeNow(SpikeStabberScript spike, Component hit, Body body) {
        if (spike == null || body == null || !Util.IsBodyLocal(body))
            return;
        RollSpike(spike, body, hit);
    }

    internal static void SpikeFromAnim(SpikeStabberScript spike) {
        if (!Net.IsRunning || spike == null)
            return;
        var cam = PlayerCamera.main;
        if (cam != null && cam.body != null && Util.IsBodyLocal(cam.body))
            RollSpike(spike, cam.body, null);
    }

    static void RollSpike(SpikeStabberScript spike, Body body, Component touch) {
        ushort mask = 0;
        var hits = Physics2D.RaycastAll(spike.transform.position, spike.transform.up, 6f);
        for (int i = 0; i < hits.Length; i++) {
            var t = hits[i].transform;
            if (t == spike.transform || !t.TryGetComponent<Limb>(out var limb))
                continue;
            if (limb.body != body || Random.Range(0f, 1f) >= 0.5f)
                continue;
            ushort bit = Bit(limb);
            if (bit == 0 || (mask & bit) != 0)
                continue;
            mask |= bit;
        }
        if (mask == 0 && touch != null && touch.TryGetComponent<Limb>(out var touched) && touched.body == body)
            mask = Bit(touched);
        if (mask == 0) {
            // the anim still played. the host has to spend its copy or the next sync rearms this one.
            TellSpike(spike, body, 0);
            return;
        }

        int key = spike.GetInstanceID() ^ body.GetInstanceID();
        float now = Time.unscaledTime;
        if (stabbed.TryGetValue(key, out var until) && now < until)
            return;
        stabbed[key] = now + 1.2f;
        if (stabbed.Count > 400)
            stabbed.Clear();

        Each(body, mask, limb => ApplySpikeLimb(spike, limb));
        Sound.Play("loudStab", spike.transform.position);
        if (spike.spikeRenderer != null)
            spike.spikeRenderer.sprite = spike.hitSpikeSprite;
        Scare(spike.transform.position);
        Ignore(body);
        TellSpike(spike, body, mask);
    }

    static void TellSpike(SpikeStabberScript spike, Body body, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(spike, out var si))
            return;
        if (Net.IsClient) {
            Send(KindSpike, si.syncId, mask);
            return;
        }
        if (mask != 0 && body != null && body.TryGetNetBody(out var nb))
            Publish(nb);
        Relay(KindSpike, si.syncId, 0f);
    }

    static void ApplySpikeLimb(SpikeStabberScript spike, Limb limb) {
        Hurt(spike, limb.body);
        float armor = limb.GetArmorReduction();
        float mult = spike.damageMult;
        if (Util.IsBodyLocal(limb.body) && limb.rb != null)
            limb.rb.AddForce(spike.transform.up * 300f * mult, ForceMode2D.Impulse);
        limb.skinHealth -= Random.Range(50f, 80f) / armor * mult;
        limb.muscleHealth -= Random.Range(30f, 50f) / armor * mult;
        limb.bleedAmount += Random.Range(0f, 40f) / armor * mult;
        limb.pain += Random.Range(70f, 100f) / armor * mult;
        limb.body.shock = 70f * mult;
        limb.body.adrenaline = 100f;
        limb.DamageWearables(1f * mult);
        if (Random.Range(0f, 1f) < 0.3f / armor * mult)
            limb.BreakBone();
        if (limb.isHead) {
            limb.body.consciousness = 0f;
            if (Random.Range(0f, 1f) < 0.5f * mult) {
                limb.body.brainHealth -= Random.Range(5f, 70f) * mult / armor;
                limb.body.Disfigure();
            } else {
                limb.body.RemoveEye();
            }
        }
        if (limb.isHead || limb.isVital)
            limb.body.internalBleeding += Random.Range(16f, 30f) * mult / armor;
        limb.body.Scream();
        if (Util.IsBodyLocal(limb.body))
            PlayerCamera.main.shaker.Shake(50f);
    }

    static void Scare(Vector2 pos) {
        var hits = Physics2D.OverlapCircleAll(pos, 15f, LayerMask.GetMask("Body", "Limb"));
        for (int i = 0; i < hits.Length; i++) {
            if (hits[i].TryGetComponent<Body>(out var body)) {
                body.eyeScareTime = 4f;
                body.eyePanicTime = 0.5f;
            }
            if (hits[i].TryGetComponent<Limb>(out var limb)) {
                limb.body.eyeScareTime = 4f;
                limb.body.eyePanicTime = 0.5f;
            }
        }
    }

    internal static void ArmCoil(CoilScript coil) {
        armedCoil = coil;
    }

    internal static void FinishCoil(CoilScript coil, Limb limb) {
        if (armedCoil != coil || coil == null || limb == null) {
            return;
        }
        armedCoil = null;
        if (!limb.IsBodyLocal())
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(coil, out var si))
            return;
        Ignore(limb.body);
        if (Net.IsClient)
            Send(KindCoil, si.syncId, Bit(limb));
        else if (limb.TryGetNetBody(out var nb)) {
            Publish(nb);
            Relay(KindCoil, si.syncId, 30f);
        }
    }

    static void ApplyCoil(CoilScript coil, Limb limb) {
        if (coil == null || limb == null || limb.body == null)
            return;
        Hurt(coil, limb.body);
        bool local = Util.IsBodyLocal(limb.body);
        if (local)
            limb.body.Ragdoll();
        foreach (var limb2 in limb.body.limbs) {
            if (limb2 == null)
                continue;
            limb2.muscleHealth -= Random.Range(0f, 40f);
            limb2.skinHealth -= Random.Range(0f, 25f);
            limb2.pain = 100f;
            if (local && limb2.rb != null)
                limb2.rb.velocity = ((Vector2)limb2.transform.position - (Vector2)coil.transform.position).normalized * 30f;
        }
        limb.body.temperature += 5f;
        limb.body.consciousness = 0f;
        limb.body.shock = 100f;
        limb.body.respiratoryRate = 0f;
        limb.body.adrenaline = 50f;
        limb.body.fibrillationProgress = 0f;
        coil.cooldown = 30f;
        if (coil.light != null)
            coil.light.intensity = 1f;
        if (Net.IsServer)
            RelayOf(KindCoil, coil, 30f);
        Sound.Play("zap", coil.transform.position);
        if (local)
            PlayerCamera.main.shaker.Shake();
    }

    internal static void JumpFired(JumpPadScript pad, float cooldownBefore) {
        if (!Net.IsRunning || pad == null || cooldownBefore > 0f || pad.cooldown <= 0f)
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(pad, out var si))
            return;
        if (Net.IsClient)
            Send(KindJump, si.syncId, 0);
        else
            Relay(KindJump, si.syncId, 15f);
    }

    internal static void PlayJump(JumpPadScript pad) {
        if (pad == null)
            return;
        if (pad.light != null)
            pad.light.intensity = 1f;
        Sound.Play("jumppad", pad.transform.position);
    }

    internal static void RaiseIfIdle(JumpPadScript pad, byte seconds) {
        if (pad != null && seconds > 0 && pad.cooldown <= 0f)
            pad.cooldown = seconds;
    }

    internal static void RaiseIfIdle(CoilScript coil, byte seconds) {
        if (coil != null && seconds > 0 && coil.cooldown <= 0f)
            coil.cooldown = seconds;
    }

    internal static void RaiseGun(GunmineScript gun, byte seconds) {
        if (gun == null || seconds == 0 || gun.cooldown > 0f)
            return;
        gun.cooldown = seconds;
        gun.didReload = false;
    }

    internal static void PlayGun(GunmineScript gun) {
        if (gun == null)
            return;
        Sound.Play("rifleshot", gun.transform.position);
        var puff = gun.GetComponentInChildren<ParticleSystem>();
        if (puff != null)
            puff.Play();
    }

    // the render callback does not run on a client who cannot see the mine. the ray is one unit, so the owner has to tick it.
    internal static void TickClientGun(GunmineScript gun) {
        if (!Net.IsClient || gun == null)
            return;
        var hit = Physics2D.Raycast(gun.transform.position, gun.transform.up, 1f, LayerMask.GetMask("Body", "Limb"));
        bool local = hit && Util.TryGetBodyFromObject(hit.collider, out var body) && body != null && Util.IsBodyLocal(body);
        if (!local)
            gun.cooldown -= Time.deltaTime;
        if (gun.cooldown < 1f && !gun.didReload) {
            gun.didReload = true;
            Sound.Play("reload", gun.transform.position);
        }
        if (gun.cooldown > 0f || !local)
            return;
        PlayGun(gun);
        TurretScript.Shoot(new FireInfo {
            pos = gun.transform.position + gun.transform.up * 0.25f,
            dir = gun.transform.up,
            ignoreTrans = gun.transform
        });
        gun.didReload = false;
        gun.cooldown = 6f;
    }

    internal static void BeginShot() {
        turretMask = 0;
        turretId = default;
        turretClaim = false;
        gunShot = false;
        gunId = default;
        turretLocal = null;
    }

    internal static void NoteTurret(Body body, Limb limb) {
        if (!Net.IsRunning || body == null || !Util.IsBodyLocal(body))
            return;
        if (!body.TryGetNetBody(out var nb))
            return;
        var info = TurretScript_Shoot_MultiplayerPatch.last_fire_info;
        GunmineScript gun = null;
        bool isGun = info.ignoreTrans != null && info.ignoreTrans.TryGetComponent(out gun);
        if (Net.IsServer) {
            turretLocal = nb;
            if (isGun && NetObjectRegistry.TryGetSyncInfo(gun, out var gsi)) {
                gunShot = true;
                gunId = gsi.syncId;
            }
            return;
        }
        if (isGun) {
            if (!NetObjectRegistry.TryGetSyncInfo(gun, out var gsi))
                return;
            gunShot = true;
            turretId = gsi.syncId;
            turretMask |= Bit(limb);
            Ignore(body);
            return;
        }
        if (info.ignoreTrans == null || !info.ignoreTrans.TryGetComponent<TurretScript>(out var turret))
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(turret, out var si))
            return;
        turretClaim = true;
        turretId = si.syncId;
        turretMask |= Bit(limb);
        Ignore(body);
    }

    internal static void EndShot() {
        if (turretLocal != null)
            Publish(turretLocal);
        if (gunShot && Net.IsServer && gunId.id != 0)
            Relay(KindGun, gunId, 6f);
        else if (gunShot && Net.IsClient)
            Send(KindGun, turretId, turretMask);
        else if (turretClaim)
            Send(KindTurret, turretId, turretMask);
        BeginShot();
    }

    struct ArmedMine {
        internal Body body;
        internal float until;
    }

    static readonly Dictionary<int, ArmedMine> armedMines = new Dictionary<int, ArmedMine>();

    // contact only arms it. stock waits 0.8s after pressed before CreateExplosion.
    internal static void ArmMine(MineScript mine, Component hit) {
        if (mine == null || mine.exploded || !IsLocal(hit, out var body))
            return;
        int id = mine.GetInstanceID();
        if (armedMines.ContainsKey(id))
            return;
        if (armedMines.Count > 64)
            armedMines.Clear();
        armedMines[id] = new ArmedMine { body = body, until = Time.time + 0.8f };
    }

    internal static void TickMine(MineScript mine) {
        if (mine == null)
            return;
        int id = mine.GetInstanceID();
        if (!armedMines.TryGetValue(id, out var arm))
            return;
        if (Time.time < arm.until)
            return;
        armedMines.Remove(id);
        if (arm.body == null)
            return;
        ClientMine(mine, arm.body);
    }

    internal static void ClientMine(MineScript mine, Component hit) {
        if (mine == null || !IsLocal(hit, out var body))
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(mine, out var si))
            return;
        var param = new ExplosionParams { position = mine.transform.position + Vector3.up };
        if (!FreshBoom(param.position))
            return;
        ushort mask = ApplyBoomLocal(body, param);
        if (mask == 0)
            return;
        Ignore(body);
        Send(KindBoom, si.syncId, mask);
    }

    internal static void ClientSawBoom(knetid id, ExplosionParams param) {
        if (param == null || !FreshBoom(param.position))
            return;
        var cam = PlayerCamera.main;
        if (cam == null || cam.body == null || !Util.IsBodyLocal(cam.body))
            return;
        ushort mask = ApplyBoomLocal(cam.body, param);
        if (mask == 0 || id.id == 0)
            return;
        Ignore(cam.body);
        Send(KindBoom, id, mask);
    }

    static bool FreshBoom(Vector2 pos) {
        float now = Time.unscaledTime;
        if (now - lastLocalBoomTime < 1f && KM.DistanceCheck(pos, lastLocalBoom, 2f))
            return false;
        lastLocalBoom = pos;
        lastLocalBoomTime = now;
        return true;
    }

    static ushort ApplyBoomLocal(Body body, ExplosionParams param) {
        ushort mask = 0;
        if (body == null || body.limbs == null)
            return 0;
        int i = 0;
        foreach (var limb in body.limbs) {
            if (limb == null) {
                i++;
                continue;
            }
            if (i > 15)
                break;
            if (Physics2D.Linecast(param.position, limb.transform.position, LayerMask.GetMask("Ground")) || !KM.DistanceCheck(param.position, limb.transform.position, param.range)) {
                i++;
                continue;
            }
            mask |= (ushort)(1 << i);
            ApplyBoomLimb(limb, param, true);
            i++;
        }
        return mask;
    }

    static void ApplyBoomLimb(Limb limb, ExplosionParams param, bool local) {
        float armor = limb.GetArmorReduction();
        if (Random.Range(0f, 1f) < param.skinDamageChance)
            limb.skinHealth -= param.skinDamage.RandomFromRange() / armor;
        limb.muscleHealth -= param.muscleDamage.RandomFromRange() / armor;
        limb.body.shock = 100f;
        if (local) {
            limb.body.lastTimeStepVelocity = ((Vector2)limb.body.transform.position - param.position).normalized * param.velocity;
            limb.body.Ragdoll();
        }
        if (!limb.hasShrapnel)
            limb.shrapnel = Random.value < param.shrapnelChance ? 5 : 0;
        limb.DamageWearables(param.shrapnelChance);
        if (limb.isVital && Random.value < 0.5f)
            limb.body.internalBleeding += param.muscleDamage.RandomFromRange() * 0.4f / armor;
        if (Random.Range(0f, 1f) < param.bleedChance)
            limb.bleedAmount += param.bleedAmount.RandomFromRange() / armor;
        if (Random.Range(0f, 1f) < param.boneBreakChance / armor)
            limb.BreakBone();
        if (Random.Range(0f, 1f) < param.dislocationChance / armor)
            limb.Dislocate();
        if (!limb.isHead)
            return;
        limb.body.consciousness = 0f;
        if (Random.Range(0f, 1f) < 0.7f / armor)
            limb.body.brainHealth -= param.muscleDamage.RandomFromRange() / armor * 0.5f;
        if (Random.Range(0f, 1f) < param.disfigureChance / armor)
            limb.body.Disfigure();
        if (Random.Range(0f, 1f) < param.disfigureChance / armor)
            limb.body.RemoveEye();
    }

    internal static void HostBoom(ExplosionParams param) {
        if (param == null)
            return;
        var id = pendingBoom;
        pendingBoom = default;
        if (id.id == 0)
            id = NextBoomId();
        float now = Time.unscaledTime;
        booms.Add(new Boom {
            id = id,
            until = now + BoomWindow,
            param = param,
            who = new HashSet<ushort>(),
        });
        if (booms.Count > 32)
            booms.RemoveRange(0, booms.Count - 16);

        var writer = Net.CreateWriter(NetmsgId.CreateExplosionEffect);
        WriteBoom(writer, id, param);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);

        try {
            var hits = Physics2D.OverlapCircleAll(param.position, param.range);
            var seen = new HashSet<NetBody>();
            for (int i = 0; i < hits.Length; i++) {
                if (Util.TryGetBodyFromObject(hits[i], out var body) && body.TryGetNetBody(out var nb))
                    seen.Add(nb);
            }
            foreach (var nb in seen)
                nb.OnDamageEvent(new BodyDamageEvent(ServerMain.PickRandomDeathMessageTag("plrdeath_exploded"), nb));
        } catch (System.Exception e) {
            log.error(e);
        }
    }

    internal static void ClearPending() {
        pendingBoom = default;
    }

    static knetid NextBoomId() {
        ushort serial = 50000;
        for (int i = 0; i < 64; i++) {
            ushort id = (ushort)(serial + (Time.frameCount + i) % 15000);
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out _))
                return id;
        }
        return (ushort)(50000 + (Time.frameCount % 15000));
    }

    internal static void WriteBoom(NetDataWriter writer, knetid id, ExplosionParams param) {
        writer.Put(param.position);
        writer.Put(param.velocity);
        writer.Put(param.range);
        writer.Put(id);
        writer.Put(param.muscleDamage.min);
        writer.Put(param.muscleDamage.max);
        writer.Put(param.skinDamage.min);
        writer.Put(param.skinDamage.max);
        writer.Put(param.skinDamageChance);
        writer.Put(param.boneBreakChance);
        writer.Put(param.dislocationChance);
        writer.Put(param.disfigureChance);
        writer.Put(param.bleedChance);
        writer.Put(param.bleedAmount.min);
        writer.Put(param.bleedAmount.max);
        writer.Put(param.shrapnelChance);
        writer.Put(param.structuralDamage);
        writer.Put(param.sound ?? "explosion");
    }

    internal static bool ReadBoom(NetDataReader reader, out knetid id, out ExplosionParams param) {
        id = default;
        param = null;
        if (reader.AvailableBytes < 12)
            return false;
        reader.Get(out Vector2 pos);
        reader.Get(out float vel);
        reader.Get(out float range);
        param = new ExplosionParams { position = pos, velocity = vel, range = range };
        if (reader.AvailableBytes < 2)
            return true;
        reader.Get(out id);
        if (reader.AvailableBytes < 4)
            return true;
        param.muscleDamage = new RangeF(reader.GetFloat(), reader.GetFloat());
        param.skinDamage = new RangeF(reader.GetFloat(), reader.GetFloat());
        param.skinDamageChance = reader.GetFloat();
        param.boneBreakChance = reader.GetFloat();
        param.dislocationChance = reader.GetFloat();
        param.disfigureChance = reader.GetFloat();
        param.bleedChance = reader.GetFloat();
        param.bleedAmount = new RangeF(reader.GetFloat(), reader.GetFloat());
        param.shrapnelChance = reader.GetFloat();
        param.structuralDamage = reader.GetFloat();
        param.sound = reader.GetString();
        return true;
    }

    internal static void HideRemoteBodies() {
        if (hideDepth++ > 0)
            return;
        foreach (var nb in NetBody.all_instances) {
            if (nb == null || nb.body == null || nb.IsBodyLocal())
                continue;
            var cols = nb.body.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < cols.Length; i++) {
                var col = cols[i];
                if (col == null || col.GetComponentInParent<Item>() != null)
                    continue;
                hidden.Add(col);
                hiddenWas.Add(col.enabled);
                col.enabled = false;
            }
        }
    }

    internal static void ShowRemoteBodies() {
        if (hideDepth <= 0)
            return;
        if (--hideDepth > 0)
            return;
        for (int i = 0; i < hidden.Count; i++) {
            if (hidden[i] != null)
                hidden[i].enabled = hiddenWas[i];
        }
        hidden.Clear();
        hiddenWas.Clear();
    }

    internal static void PublishLocals(ExplosionParams param) {
        if (param == null || !Net.IsServer)
            return;
        foreach (var nb in NetBody.all_instances) {
            if (nb == null || nb.body == null || !nb.IsBodyLocal())
                continue;
            if (KM.DistanceCheck(param.position, nb.body.transform.position, param.range))
                Publish(nb);
        }
    }

    static Boom FindBoom(knetid id) {
        float now = Time.unscaledTime;
        for (int i = booms.Count - 1; i >= 0; i--) {
            if (booms[i].until < now)
                continue;
            if (id.id != 0 && booms[i].id.id == id.id)
                return booms[i];
        }
        return null;
    }

    static Boom LatestBoom() {
        float now = Time.unscaledTime;
        for (int i = booms.Count - 1; i >= 0; i--) {
            if (booms[i].until >= now)
                return booms[i];
        }
        return null;
    }

    static void ConsumeMine(MineScript mine, knetid id) {
        mine.pressed = true;
        mine.exploded = true;
        if (mine.build != null)
            mine.build.SetHealth(0f);
        var sprite = mine.GetComponent<SpriteRenderer>();
        if (sprite != null && mine.pressedSprite != null)
            sprite.sprite = mine.pressedSprite;
        var writer = Net.CreateWriter(NetmsgId.CLIENT_MineStepped);
        writer.Put((Vector2)mine.transform.position);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        pendingBoom = id;
        var explosion = new ExplosionParams { position = mine.transform.position + Vector3.up };
        WorldGeneration.CreateExplosion(explosion);
        pendingBoom = default;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TrapClaim)]
    static void Recv(knetid clientId, NetDataReader reader) {
        reader.Get(out byte kind);
        reader.Get(out knetid id);
        reader.Get(out ushort mask);
        reader.Get(out float _);
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out var nb) || nb == null || nb.body == null)
            return;
        if (kind == KindJump) {
            AcceptJump(id, nb);
            return;
        }

        bool ok = false;
        if (kind == KindBear)
            ok = AcceptBear(id, nb, mask);
        else if (kind == KindSpike)
            ok = AcceptSpike(id, nb, mask);
        else if (kind == KindCoil)
            ok = AcceptCoil(id, nb, mask);
        else if (kind == KindTurret)
            ok = AcceptTurret(id, nb, mask);
        else if (kind == KindGun)
            ok = AcceptGun(id, nb, mask);
        else if (kind == KindBoom)
            ok = AcceptBoom(id, nb, mask);

        if (!ok)
            Publish(nb);
    }

    static void AcceptJump(knetid id, NetBody nb) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<JumpPadScript>(out var pad))
            return;
        if (pad.cooldown > 0f)
            return;
        // the body is already in the air by the time this lands, so 32 is too tight
        if (!KM.DistanceCheck(pad.transform.position, nb.body.transform.position, 64f))
            return;
        pad.cooldown = 15f;
        PlayJump(pad);
        Relay(KindJump, id, 15f);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TrapState)]
    static void RecvState(knetid _, NetDataReader reader) {
        reader.Get(out byte kind);
        reader.Get(out knetid id);
        reader.Get(out float cooldown);
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null)
            return;
        if (kind == KindJump && si.go.TryGetComponent<JumpPadScript>(out var pad)) {
            bool idle = pad.cooldown <= 0f;
            RaiseIfIdle(pad, (byte)Mathf.Clamp(Mathf.CeilToInt(cooldown), 0, 255));
            if (idle)
                PlayJump(pad);
        }
        else if (kind == KindCoil && si.go.TryGetComponent<CoilScript>(out var coil))
            RaiseIfIdle(coil, (byte)Mathf.Clamp(Mathf.CeilToInt(cooldown), 0, 255));
        else if (kind == KindBear && si.go.TryGetComponent<BearTrap>(out var bear))
            SpendBear(bear);
        else if (kind == KindSpike && si.go.TryGetComponent<SpikeStabberScript>(out var spike) && !spike.activated)
            spike.Stab();
        else if (kind == KindGun && si.go.TryGetComponent<GunmineScript>(out var gun)) {
            bool idle = gun.cooldown <= 0f;
            RaiseGun(gun, (byte)Mathf.Clamp(Mathf.CeilToInt(cooldown), 0, 255));
            if (idle)
                PlayGun(gun);
        }
    }

    static bool AcceptBear(knetid id, NetBody nb, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<BearTrap>(out var trap))
            return false;
        if (!Near(trap, nb.body))
            return false;
        if (!TakeWindow(KindBear, id, nb.netId, trap.activated, BearWindow))
            return false;
        Limb limb = null;
        Each(nb.body, mask, l => limb = l);
        if (limb == null)
            return false;
        ApplyBear(trap, limb);
        Publish(nb);
        return true;
    }

    static bool AcceptSpike(knetid id, NetBody nb, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<SpikeStabberScript>(out var spike))
            return false;
        if (!Near(spike, nb.body))
            return false;
        if (mask == 0) {
            // graze spent the anim and missed the ray. leave this player free to be hit if they step into it.
            OpenWindow(KindSpike, id, spike.activated, SpikeWindow);
            if (!spike.activated)
                spike.Stab();
            RelayOf(KindSpike, spike, 0f);
            return true;
        }
        if (!TakeWindow(KindSpike, id, nb.netId, spike.activated, SpikeWindow))
            return false;
        if (!spike.activated)
            spike.Stab();
        RelayOf(KindSpike, spike, 0f);
        Each(nb.body, mask, limb => ApplySpikeLimb(spike, limb));
        if (spike.spikeRenderer != null)
            spike.spikeRenderer.sprite = spike.hitSpikeSprite;
        Publish(nb);
        return true;
    }

    static bool AcceptCoil(knetid id, NetBody nb, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<CoilScript>(out var coil))
            return false;
        if (coil.cooldown > 0f)
            return false;
        if (!Near(coil, nb.body))
            return false;
        Limb limb = null;
        Each(nb.body, mask, l => limb = l);
        if (limb == null && nb.body.limbs != null) {
            foreach (var l in nb.body.limbs) {
                if (l != null) {
                    limb = l;
                    break;
                }
            }
        }
        if (limb == null)
            return false;
        ApplyCoil(coil, limb);
        Publish(nb);
        return true;
    }

    static bool AcceptTurret(knetid id, NetBody nb, ushort mask) {
        if (mask == 0)
            return false;
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<TurretScript>(out _))
            return false;
        int key = (id.id << 16) ^ nb.netId.id;
        float now = Time.unscaledTime;
        if (turretNext.TryGetValue(key, out var next) && now < next)
            return false;
        turretNext[key] = now + 0.15f;
        if (turretNext.Count > 400)
            turretNext.Clear();
        Each(nb.body, mask, limb => TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(limb, null, false));
        Publish(nb);
        return true;
    }

    static bool AcceptGun(knetid id, NetBody nb, ushort mask) {
        if (mask == 0)
            return false;
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<GunmineScript>(out var gun))
            return false;
        if (gun.cooldown > 0f)
            return false;
        gun.cooldown = 6f;
        gun.didReload = false;
        PlayGun(gun);
        Relay(KindGun, id, 6f);
        Each(nb.body, mask, limb => TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(limb, null, false));
        Publish(nb);
        return true;
    }

    static bool AcceptBoom(knetid id, NetBody nb, ushort mask) {
        var boom = FindBoom(id);
        if (boom == null && id.id != 0 && NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.go != null && si.go.TryGetComponent<MineScript>(out var mine) && !mine.exploded) {
            ConsumeMine(mine, id);
            boom = FindBoom(id);
        }
        if (boom == null)
            boom = LatestBoom();
        if (boom == null)
            return false;
        if (!boom.who.Add(nb.netId.id))
            return false;
        if (mask == 0) {
            int i = 0;
            foreach (var limb in nb.body.limbs) {
                if (i > 15)
                    break;
                if (limb != null)
                    mask |= (ushort)(1 << i);
                i++;
            }
        }
        Each(nb.body, mask, limb => ApplyBoomLimb(limb, boom.param, false));
        Publish(nb);
        return true;
    }
}

[HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.OnTriggerEnter2D))]
static class SpikeStabberScript_OnTrigger_TrapClaim {
    static bool Prefix(SpikeStabberScript __instance, Collider2D collision, ref bool __state) {
        __state = __instance.activated;
        return !TrapClaim.SkipRemote(collision);
    }

    static void Postfix(SpikeStabberScript __instance, Collider2D collision, bool __state) {
        if (!Net.IsRunning || __state)
            return;
        if (!TrapClaim.IsLocal(collision, out var body))
            return;
        TrapClaim.SpikeNow(__instance, collision, body);
    }
}

[HarmonyPatch(typeof(CoilScript), nameof(CoilScript.OnCollisionEnter2D))]
static class CoilScript_OnCollision_TrapClaim {
    static bool Prefix(CoilScript __instance, Collision2D collision) {
        if (__instance.cooldown > 0f)
            return true;
        return !TrapClaim.SkipRemote(collision.collider);
    }
}

[HarmonyPatch(typeof(MineScript), nameof(MineScript.Update))]
static class MineScript_Update_TrapClaim {
    static void Prefix(MineScript __instance) {
        if (Net.IsClient)
            TrapClaim.TickMine(__instance);
        if (!Net.IsServer || !__instance.pressed || __instance.exploded)
            return;
        if (__instance.timeSincePressed + Time.deltaTime <= 0.8f)
            return;
        if (NetObjectRegistry.TryGetSyncInfo(__instance, out var si))
            TrapClaim.pendingBoom = si.syncId;
    }

    static void Postfix() {
        TrapClaim.ClearPending();
    }
}

// host ray hits the lagged ghost and spends the mine. the owner is the one actually standing in the barrel.
[HarmonyPatch(typeof(GunmineScript), nameof(GunmineScript.OnWillRenderObject))]
static class GunmineScript_OnWillRender_TrapClaim {
    static bool Prefix(GunmineScript __instance) {
        if (!Net.IsRunning)
            return true;
        if (Net.IsClient)
            return false;
        if (__instance.cooldown > 0f)
            return true;
        var hit = Physics2D.Raycast(__instance.transform.position, __instance.transform.up, 1f, LayerMask.GetMask("Body", "Limb"));
        if (!hit)
            return true;
        if (!Util.TryGetBodyFromObject(hit.collider, out var body) || body == null || Util.IsBodyLocal(body))
            return true;
        __instance.cooldown -= Time.deltaTime;
        if (__instance.cooldown < 1f && !__instance.didReload) {
            __instance.didReload = true;
            Sound.Play("reload", __instance.transform.position);
        }
        return false;
    }
}
