using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

// host only. clients already apply the teleport and the ragdoll pose the host sends.
// folded in from tk2 - replaces the old Con telekinesis glue.

static class Grab {
    internal static bool TryRemote(GameObject go, out NetBody nb) {
        nb = null;
        if (go == null)
            return false;
        Body body = go.GetComponent<Body>();
        if (body == null) {
            Limb limb = go.GetComponent<Limb>();
            if (limb != null)
                body = limb.body;
        }
        if (body == null)
            body = go.GetComponentInParent<Body>();
        if (body == null || !body.TryGetNetBody(out nb) || nb == null || nb.IsLocal)
            return false;
        return true;
    }

    internal static Limb HeldLimb(GameObject go, Body body) {
        Limb held = go.GetComponent<Limb>();
        if (held == null)
            held = go.GetComponentInParent<Limb>();
        if (held == null)
            held = body.GetUpperTorso();
        return held;
    }

    internal static Body Host() {
        PlayerCamera cam = PlayerCamera.main;
        if (cam == null)
            return null;
        return cam.body;
    }

    // elders brace (~300). medcrate is 70 - drag strength never treats a target as heavier than that.
    internal const float MedcrateMass = 70f;
    const float ElderMass = 100f;

    // a fixture stays put. nothing pushes back on the caster.
    // heavy props used to brace at 100 kg (WeighBuilding) - only map fixtures brace now.
    internal static bool TryAnchor(GameObject go, out Transform held) {
        held = null;
        BuildingEntity building = go.GetComponent<BuildingEntity>();
        if (building == null)
            building = go.GetComponentInParent<BuildingEntity>();
        if (building != null && IsFixture(building.id)) {
            held = building.transform;
            return true;
        }
        return false;
    }

    // elders are braces. other walkers can be picked up.
    internal static SpiderHandler Creature(Rigidbody2D rb) {
        if (rb == null || rb.mass >= ElderMass)
            return null;
        SpiderHandler spider = rb.GetComponent<SpiderHandler>();
        if (spider == null)
            spider = rb.GetComponentInParent<SpiderHandler>();
        return spider;
    }

    static bool IsFixture(string id) {
        return id == "geyser"
            || id == "dropcapsule"
            || id == "drillpod"
            || id == "drillpodbroken"
            || id == "reinforceddoor"
            || id == "shuttledoor";
    }

    // crates are clicked colliders with no rigidbody, so stock teleports them. give them one the beam can pull.
    internal static bool TryPull(GameObject go, out Rigidbody2D rb) {
        // the geyser click is a child trigger. the solid building is the parent.
        BuildingEntity building = go.GetComponent<BuildingEntity>();
        if (building == null)
            building = go.GetComponentInParent<BuildingEntity>();
        if (building != null)
            go = building.gameObject;

        rb = go.GetComponent<Rigidbody2D>();
        if (rb == null)
            rb = go.GetComponentInParent<Rigidbody2D>();
        bool added = false;
        int keptLayer = go.layer;
        bool keptOptimize = building != null && building.ignoreBodyOptimize;
        if (rb == null) {
            rb = go.AddComponent<Rigidbody2D>();
            rb.gravityScale = 1f;
            rb.mass = 8f;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            added = true;
        }
        // a terminal has no rigidbody, and a dropped skullcrusher is only 10 kg. size gives them a real weight.
        if (building != null)
            WeighBuilding(go, rb);
        // glowplants sit on Descriptor, which does not collide with Ground, so a dynamic body falls out of the map.
        int ground = LayerMask.NameToLayer("Ground");
        if (ground >= 0 && Physics2D.GetIgnoreLayerCollision(go.layer, ground))
            go.layer = 0;
        if (building != null)
            building.ignoreBodyOptimize = true;
        if (added)
            Loose.Lend(go, rb, keptLayer, building, keptOptimize);
        Loose.Listen(go);
        return rb != null;
    }

    // medcrate is 4 by 4 and 70 kg. a building lighter than its own sprite gets that density.
    static void WeighBuilding(GameObject go, Rigidbody2D rb) {
        Vector3 scale = go.transform.lossyScale;
        float sx = Mathf.Abs(scale.x);
        float sy = Mathf.Abs(scale.y);
        float area = 0f;
        Collider2D[] cols = go.GetComponents<Collider2D>();
        for (int i = 0; i < cols.Length; i++) {
            Collider2D col = cols[i];
            if (col == null || col.isTrigger)
                continue;
            float piece = 0f;
            BoxCollider2D box = col as BoxCollider2D;
            CircleCollider2D circle = col as CircleCollider2D;
            CapsuleCollider2D capsule = col as CapsuleCollider2D;
            if (box != null)
                piece = Mathf.Abs(box.size.x * sx * box.size.y * sy);
            else if (circle != null)
                piece = circle.radius * circle.radius * Mathf.PI * sx * sy;
            else if (capsule != null)
                piece = Mathf.Abs(capsule.size.x * sx * capsule.size.y * sy);
            else {
                Vector3 bounds = col.bounds.size;
                piece = Mathf.Abs(bounds.x * bounds.y);
            }
            if (piece > area)
                area = piece;
        }
        SpriteRenderer sprite = go.GetComponent<SpriteRenderer>();
        if (sprite != null && sprite.sprite != null) {
            Vector3 size = Vector3.Scale(sprite.sprite.bounds.size, scale);
            float spriteArea = Mathf.Abs(size.x * size.y);
            if (spriteArea > area)
                area = spriteArea;
        }
        float sized = area * (70f / 16f);
        if (sized > rb.mass)
            rb.mass = sized;
    }
}

// the grabbed point is pulled toward the cursor. the caster is not part of that force.
[HarmonyPatch(typeof(Con), "FixedUpdate")]
static class DragPatch {
    // meters of lead, times this, is the acceleration for something that weighs as much as you. heavier things get less.
    const float AccelPerMeter = 48f;
    // a tile is one world unit. past this lead the pull stays flat, so a far grab cannot wind up a harder yank.
    const float MaxLead = 15f;
    // taken off along the current motion each second. a still cursor lets the rubberband die instead of ringing.
    const float SettleAccel = 8f;
    // share of the pull that hits the grabbed limb first. the other limbs pay it back, so the body still goes with the cursor.
    const float SwingShare = 0.45f;

    internal static Body HeldVictim;

    static bool Prefix() {
        Loose.Tick();
        if (!Con._DEV_TELEKINESIS || !Multiplayer.IsNetworkActiveAndIsServer()) {
            CreatureHold.Release();
            SetHeld(null);
            return true;
        }
        GameObject go = Con._DEV_TELEKINESIS_OBJECT;
        if (go == null || !Con.CanCheat()) {
            CreatureHold.Release();
            SetHeld(null);
            return true;
        }

        Vector2 cursor = Util.GetCursorWorldPos();
        Con.lastworldcursorpos = cursor;

        // stock glues a crate to the cursor. the beam has to own every grab or that path still runs.
        Body victim = null;
        Limb heldLimb = null;
        Transform heldTransform;
        Rigidbody2D rb = null;
        bool brace = false;
        if (Grab.TryRemote(go, out NetBody nb) && nb.body != null) {
            victim = nb.body;
            if (nb.IsPiggybacking())
                nb.StopPiggyback();
            if (victim.currentClimbable != null)
                victim.StopClimbing();
            if (victim.standing)
                victim.Ragdoll();
            // conscious bodies burn shock at 10 per second. above 10 they cannot stand.
            victim.shock = 100f;
            victim.moveDir = Vector2.zero;
            heldLimb = Grab.HeldLimb(go, victim);
            if (heldLimb == null || heldLimb.rb == null) {
                SetHeld(null);
                return false;
            }
            heldTransform = heldLimb.transform;
            rb = heldLimb.rb;
        } else if (Grab.TryAnchor(go, out heldTransform))
            brace = true;
        else if (!Grab.TryPull(go, out rb)) {
            SetHeld(null);
            return false;
        } else
            heldTransform = rb.transform;

        if (!brace) {
            rb.simulated = true;
            if (rb.bodyType != RigidbodyType2D.Dynamic)
                rb.bodyType = RigidbodyType2D.Dynamic;
            rb.WakeUp();
        }
        CreatureHold.Sync(brace ? null : Grab.Creature(rb));
        if (!brace && rb != null && CreatureHold.Held == null)
            Loose.Arm(rb.gameObject, rb);
        SetHeld(victim);

        Link.Bind(heldTransform, cursor);
        Vector2 anchor = Link.Anchor(heldTransform);

        // acceleration grows with the lead. the caster is not in this. walls do not cut it.
        Vector2 toCursor = cursor - (brace ? anchor : rb.position);
        float dist = toCursor.magnitude;
        Vector2 dir = dist > 0.05f ? toCursor / dist : Vector2.zero;
        float weight = DragMath.BodyMass(Grab.Host());

        SpiderHandler spider = CreatureHold.Held;
        bool coasting = rb != null && rb.velocity.sqrMagnitude > 0.04f;
        if (!brace && (dir.sqrMagnitude > 0f || coasting) && spider != null) {
            Vector2 useAccel = DragMath.PullAccel(dir, dist, rb.mass, weight, rb.velocity, AccelPerMeter, MaxLead, SettleAccel);
            rb.AddForce(useAccel * rb.mass);
            rb.angularVelocity *= 0.96f;
            if (spider.stunTime < 0.35f)
                spider.stunTime = 0.35f;
        } else if (!brace && (dir.sqrMagnitude > 0f || coasting)) {
            float targetMass = victim != null ? DragMath.BodyMass(victim) : rb.mass;
            if (targetMass < 0.2f)
                targetMass = 0.2f;
            if (targetMass > Grab.MedcrateMass)
                targetMass = Grab.MedcrateMass;
            Vector2 useAccel = DragMath.PullAccel(dir, dist, targetMass, weight, rb.velocity, AccelPerMeter, MaxLead, SettleAccel);
            if (victim != null)
                DragMath.PushBody(victim, useAccel, SwingShare, heldLimb);
            else {
                rb.AddForceAtPosition(useAccel * rb.mass, anchor);
                rb.angularVelocity *= 0.96f;
            }
        }
        return false;
    }

    static void SetHeld(Body victim) {
        PlayerCrush.Sync(victim);
        if (HeldVictim == victim) {
            if (victim != null)
                JointMotors.Relax(victim);
            return;
        }
        JointMotors.Restore();
        HeldVictim = victim;
        if (victim != null)
            JointMotors.Relax(victim);
    }

}

// kept out of the HarmonyPatch type so the analyzer does not treat helpers as patch methods.
static class DragMath {
    internal static Vector2 PullAccel(Vector2 dir, float dist, float mass, float weight, Vector2 velocity, float accelPerMeter, float maxLead, float settleAccel) {
        float useMass = mass < 0.2f ? 0.2f : mass;
        // never slower than swinging a medcrate - super-heavy props still move.
        if (useMass > Grab.MedcrateMass)
            useMass = Grab.MedcrateMass;
        float useDist = dist > maxLead ? maxLead : dist;
        float scale = weight / useMass;
        Vector2 pull = dir * (useDist * accelPerMeter * scale);
        float speed = velocity.magnitude;
        if (speed < 0.05f)
            return pull;
        return pull - velocity * (settleAccel * scale / speed);
    }

    internal static float BodyMass(Body body) {
        if (body == null || body.limbs == null)
            return 1f;
        float sum = 0f;
        Limb[] limbs = body.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb != null && limb.rb != null)
                sum += limb.rb.mass;
        }
        return sum > 0.2f ? sum : 1f;
    }

    internal static void PushBody(Body body, Vector2 accel, float swingShare, Limb lead = null) {
        Limb[] limbs = body.limbs;
        float leadMass = 0f;
        float otherMass = 0f;
        bool swing = lead != null && lead.rb != null && lead.rb.simulated && accel.sqrMagnitude > 1f;
        if (swing) {
            leadMass = lead.rb.mass;
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                if (limb == null || limb == lead || limb.rb == null || !limb.rb.simulated)
                    continue;
                otherMass += limb.rb.mass;
            }
            if (leadMass < 0.01f || otherMass < 0.01f)
                swing = false;
        }
        Vector2 extra = swing ? accel * swingShare : Vector2.zero;
        float pay = swing ? leadMass / otherMass : 0f;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null || !limb.rb.simulated)
                continue;
            Vector2 a = accel;
            if (swing)
                a += limb == lead ? extra : -extra * pay;
            limb.rb.AddForce(a * limb.rb.mass, ForceMode2D.Force);
        }
    }
}

// a conscious ragdoll still turns its head toward the look point. that freezes the pose while the beam is holding them.
[HarmonyPatch(typeof(Body), "FixedUpdate")]
static class HeldRagdollPatch {
    static bool Prefix(Body __instance) {
        return DragPatch.HeldVictim != __instance;
    }
}

// hinge motors hold a standing pose. drop them while the beam has the body, then put the authored setting back.
static class JointMotors {
    static readonly Dictionary<HingeJoint2D, bool> motors = new Dictionary<HingeJoint2D, bool>();

    internal static void Relax(Body body) {
        Limb[] limbs = body.limbs;
        if (limbs == null)
            return;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null)
                continue;
            if (limb.rb.gravityScale < 1f)
                limb.rb.gravityScale = 1f;
            HingeJoint2D joint = limb.joint;
            if (joint == null)
                continue;
            if (!motors.ContainsKey(joint))
                motors.Add(joint, joint.useMotor);
            joint.useMotor = false;
        }
    }

    internal static void Restore() {
        foreach (KeyValuePair<HingeJoint2D, bool> pair in motors) {
            if (pair.Key != null)
                pair.Key.useMotor = pair.Value;
        }
        motors.Clear();
    }
}

// the grab point on the held object. the line is drawn straight through whatever is in between.
static class Link {
    static GameObject held;
    static Vector2 localPoint;

    internal static void Bind(Transform limb, Vector2 cursor) {
        if (held != limb.gameObject) {
            held = limb.gameObject;
            localPoint = limb.InverseTransformPoint(cursor);
        }
    }

    internal static Vector2 Anchor(Transform limb) {
        return limb.TransformPoint(localPoint);
    }

    internal static void Clear() {
        held = null;
        Beam.Hide();
    }
}

// thin bright line. the demo has no noise texture; the shimmer is the beam itself.
static class Beam {
    const int Points = 7;
    static LineRenderer core;
    static LineRenderer haze;

    internal static void Show(Vector2 from, Vector2 to) {
        Ensure();
        core.enabled = true;
        haze.enabled = true;
        Write(core, from, to);
        Write(haze, from, to);
    }

    internal static void Hide() {
        if (core != null)
            core.enabled = false;
        if (haze != null)
            haze.enabled = false;
    }

    static void Write(LineRenderer line, Vector2 from, Vector2 to) {
        Vector2 dir = to - from;
        float len = dir.magnitude;
        Vector2 perp = len > 0.001f ? new Vector2(-dir.y, dir.x) / len : Vector2.right;
        for (int i = 0; i < Points; i++) {
            float t = i / (float)(Points - 1);
            float amp = 0f;
            if (i != 0 && i != Points - 1)
                amp = (Mathf.PerlinNoise(t * 4f, Time.time * 24f) - 0.5f) * 0.07f;
            line.SetPosition(i, (Vector3)(Vector2.Lerp(from, to, t) + perp * amp));
        }
    }

    static void Ensure() {
        if (core != null)
            return;
        GameObject root = new GameObject("TelekinesisBeam");
        Object.DontDestroyOnLoad(root);
        core = Make(root, 0.027f, new Color(0.5425f, 0.8798f, 1f, 0.95f), 40);
        GameObject soft = new GameObject("TelekinesisBeamHaze");
        soft.transform.SetParent(root.transform, false);
        haze = Make(soft, 0.09f, new Color(0.5425f, 0.8798f, 1f, 0.28f), 39);
    }

    static LineRenderer Make(GameObject go, float width, Color color, int order) {
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = Points;
        line.startWidth = width;
        line.endWidth = width;
        line.numCapVertices = 0;
        line.numCornerVertices = 0;
        line.sortingOrder = order;
        line.startColor = color;
        line.endColor = color;
        line.enabled = false;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader != null)
            line.material = new Material(shader) { color = Color.white };
        return line;
    }
}

// the server copies ragdoll sync back onto remote bodies. that wipe lands after the pull and deletes the fall.
[HarmonyPatch(typeof(SyncBody), "ApplyDataToBody")]
static class KeepPhysicsPatch {
    static bool Prefix(SyncBody __instance) {
        if (!Multiplayer.IsNetworkActiveAndIsServer() || __instance == null || __instance.nb == null)
            return true;
        if (!Con._DEV_TELEKINESIS)
            return true;
        return !(Grab.TryRemote(Con._DEV_TELEKINESIS_OBJECT, out NetBody nb) && nb == __instance.nb);
    }
}

// write the pose after physics, so clients get the hang instead of the pre-gravity position.
[HarmonyPatch(typeof(Con), "LateUpdate")]
static class RemindPatch {
    static void Postfix() {
        if (!Con._DEV_TELEKINESIS || !Multiplayer.IsNetworkActiveAndIsServer()) {
            Beam.Hide();
            return;
        }
        GameObject go = Con._DEV_TELEKINESIS_OBJECT;
        if (go == null) {
            Beam.Hide();
            return;
        }
        Transform heldTransform = null;
        if (Grab.TryRemote(go, out NetBody nb) && nb.body != null) {
            nb.Server_RemindPlayersCurrentState(false);
            Limb held = Grab.HeldLimb(go, nb.body);
            if (held != null)
                heldTransform = held.transform;
        } else if (Grab.TryAnchor(go, out heldTransform)) {
        } else if (Grab.TryPull(go, out Rigidbody2D rb))
            heldTransform = rb.transform;
        Body host = Grab.Host();
        if (heldTransform == null || host == null) {
            Beam.Hide();
            return;
        }
        Link.Bind(heldTransform, Util.GetCursorWorldPos());
        Vector2 anchor = Link.Anchor(heldTransform);
        Vector2 from = host.rb != null && host.standing ? host.rb.position : host.transform.position;
        if (!host.standing) {
            Limb torso = host.GetUpperTorso();
            if (torso != null && torso.rb != null)
                from = torso.rb.position;
        }
        Beam.Show(from, anchor);
    }
}

// a body we added so the beam could pull a prop. once it rests, it goes back to scenery.
static class Loose {
    // stock medcrate / falling-crate smash profile (type 1).
    const int FallingCrateType = 1;

    class Loan {
        public GameObject go;
        public Rigidbody2D rb;
        public int layer;
        public bool added;
        public BuildingEntity building;
        public bool optimize;
        public bool missile;
        public float rest;
        // damagingCrate we granted for telekinesis smash - strip only this one on settle.
        public bool crateOwned;
    }

    static readonly List<Loan> loans = new List<Loan>();

    internal static void Lend(GameObject go, Rigidbody2D rb, int layer, BuildingEntity building, bool optimize) {
        Loan loan = Find(go);
        if (loan == null) {
            loan = new Loan();
            loans.Add(loan);
        }
        loan.go = go;
        loan.rb = rb;
        loan.layer = layer;
        loan.added = true;
        loan.building = building;
        loan.optimize = optimize;
        loan.missile = true;
        loan.rest = 0f;
        EnsureFallingCrate(loan);
    }

    internal static void Arm(GameObject go, Rigidbody2D rb) {
        Loan loan = Find(go);
        if (loan == null) {
            loan = new Loan();
            loan.layer = go.layer;
            loans.Add(loan);
        }
        loan.go = go;
        loan.rb = rb;
        loan.missile = true;
        loan.rest = 0f;
        EnsureFallingCrate(loan);
        Listen(go);
    }

    // smash like a falling medcrate until the prop settles (re-add if a hit stripped it).
    static void EnsureFallingCrate(Loan loan) {
        if (loan == null || loan.go == null)
            return;
        DamagingCrate crate = loan.go.GetComponent<DamagingCrate>();
        if (crate == null) {
            crate = loan.go.AddComponent<DamagingCrate>();
            crate.type = FallingCrateType;
            loan.crateOwned = true;
        } else if (loan.crateOwned)
            crate.type = FallingCrateType;
    }

    // the plant's solid collider often meets the ground, while the player only overlaps a trigger.
    internal static void Listen(GameObject go) {
        if (go == null || go.GetComponent<MissileTouch>() != null)
            return;
        go.AddComponent<MissileTouch>();
    }

    internal static bool IsMissile(GameObject go) {
        if (go == null)
            return false;
        if (go.GetComponentInParent<SpiderHandler>() != null)
            return false;
        if (Impact.Live() && Impact.IsHeld(go))
            return true;
        for (int i = 0; i < loans.Count; i++) {
            Loan loan = loans[i];
            if (!loan.missile || loan.go == null)
                continue;
            Transform a = go.transform;
            Transform b = loan.go.transform;
            if (a == b || a.IsChildOf(b) || b.IsChildOf(a))
                return true;
        }
        return false;
    }

    internal static void Tick() {
        float dt = Time.fixedDeltaTime > 0.0001f ? Time.fixedDeltaTime : 0.02f;
        for (int i = loans.Count - 1; i >= 0; i--) {
            Loan loan = loans[i];
            if (loan.go == null) {
                loans.RemoveAt(i);
                continue;
            }
            if (Impact.IsHeld(loan.go)) {
                loan.rest = 0f;
                EnsureFallingCrate(loan);
                continue;
            }
            // still airborne / rolling - keep (or restore) DamagingCrate smash.
            if (loan.missile)
                EnsureFallingCrate(loan);
            Rigidbody2D rb = loan.rb;
            bool asleep = rb == null || rb.IsSleeping();
            bool slow = rb != null && rb.velocity.magnitude < 0.45f && Mathf.Abs(rb.angularVelocity) < 15f;
            if (asleep || slow)
                loan.rest += dt;
            else
                loan.rest = 0f;
            if (loan.rest < 0.2f)
                continue;
            if (loan.crateOwned) {
                DamagingCrate crate = loan.go.GetComponent<DamagingCrate>();
                if (crate != null)
                    Object.Destroy(crate);
                loan.crateOwned = false;
            }
            if (loan.added) {
                loan.go.layer = loan.layer;
                if (loan.building != null)
                    loan.building.ignoreBodyOptimize = loan.optimize;
                if (rb != null)
                    Object.Destroy(rb);
            }
            loans.RemoveAt(i);
        }
    }

    static Loan Find(GameObject go) {
        for (int i = 0; i < loans.Count; i++) {
            if (loans[i].go == go)
                return loans[i];
        }
        return null;
    }
}

// a walker keeps stepping while held, so the beam fights its legs. stun freezes that force.
static class CreatureHold {
    const float TossSpeed = 10f;
    const float FullStunSpeed = 30f;

    static SpiderHandler held;
    static SpiderHandler flung;
    static float flungUntil;

    internal static SpiderHandler Held {
        get { return held; }
    }

    internal static void Sync(SpiderHandler spider) {
        if (held == spider)
            return;
        Release();
        held = spider;
    }

    internal static void Release() {
        if (held == null)
            return;
        Rigidbody2D body = held.GetComponent<Rigidbody2D>();
        float speed = body != null ? body.velocity.magnitude : 0f;
        // a toss is a third of a second. a hard throw is a full second, long enough to hit and take the damage.
        if (speed >= TossSpeed) {
            float t = (speed - TossSpeed) / (FullStunSpeed - TossSpeed);
            if (t > 1f)
                t = 1f;
            float stun = Mathf.Lerp(0.35f, 1f, t);
            if (held.stunTime < stun)
                held.stunTime = stun;
            flung = held;
            flungUntil = Time.time + stun;
        }
        held = null;
    }

    internal static bool IsFlung(GameObject go) {
        if (flung == null || go == null || Time.time > flungUntil)
            return false;
        Transform a = go.transform;
        Transform b = flung.transform;
        return a == b || a.IsChildOf(b) || b.IsChildOf(a);
    }
}

// a grab point that never takes a hit can be leaned on forever. a real slam spends it.
static class Impact {
    const float Slow = 6f;
    const float Fast = 26f;
    static int lastId;
    static float nextTime;

    internal static bool Live() {
        return Con._DEV_TELEKINESIS && Multiplayer.IsNetworkActiveAndIsServer() && Con._DEV_TELEKINESIS_OBJECT != null;
    }

    internal static bool IsHeld(GameObject go) {
        GameObject held = Con._DEV_TELEKINESIS_OBJECT;
        if (held == null || go == null)
            return false;
        Transform a = go.transform;
        Transform b = held.transform;
        return a == b || a.IsChildOf(b) || b.IsChildOf(a);
    }

    internal static void Hurt(GameObject go, float speed, float scale) {
        if (go == null || speed < Slow)
            return;
        if (!Live() && !CreatureHold.IsFlung(go) && scale <= 1f)
            return;
        float damage = Amount(speed) * scale;
        BuildingEntity build = go.GetComponent<BuildingEntity>();
        if (build == null)
            build = go.GetComponentInParent<BuildingEntity>();
        if (build != null) {
            Spend(build, delegate { build.health -= damage; });
            return;
        }
        Limb limb = go.GetComponent<Limb>();
        if (limb == null)
            limb = go.GetComponentInParent<Limb>();
        if (limb == null)
            return;
        Object key = limb.body != null ? limb.body : (Object)limb;
        Spend(key, delegate {
            float left = limb.muscleHealth - damage;
            limb.muscleHealth = left > 0f ? left : 0f;
        });
    }

    internal static void Hurt(GameObject go, float speed) {
        Hurt(go, speed, 1f);
    }

    // 6 m/s is a bump, about a jump of speed is the top of the band.
    static float Amount(float speed) {
        float t = Mathf.InverseLerp(Slow, Fast, speed);
        return Mathf.Lerp(10f, 100f, t);
    }

    static void Spend(Object key, System.Action apply) {
        int id = key.GetInstanceID();
        if (id == lastId && Time.time < nextTime)
            return;
        lastId = id;
        nextTime = Time.time + 0.25f;
        apply();
    }
}

[HarmonyPatch(typeof(BuildingEntity), "OnCollisionEnter2D")]
static class BuildingImpactPatch {
    static void Postfix(BuildingEntity __instance, Collision2D collision) {
        if (__instance == null)
            return;
        GameObject other = collision.collider != null ? collision.collider.gameObject : null;
        float speed = collision.relativeVelocity.magnitude;
        if (other != null && (Loose.IsMissile(__instance.gameObject) || (Impact.Live() && Impact.IsHeld(__instance.gameObject)))) {
            PlayerCrush.NoteContact(collision, __instance.gameObject);
            SpiderHandler foe = other.GetComponent<SpiderHandler>();
            if (foe == null)
                foe = other.GetComponentInParent<SpiderHandler>();
            if (foe != null)
                Impact.Hurt(foe.gameObject, speed, 2f);
        }
        bool mine = CreatureHold.IsFlung(__instance.gameObject) || (Impact.Live() && Impact.IsHeld(__instance.gameObject));
        if (!mine)
            return;
        Impact.Hurt(__instance.gameObject, speed);
    }
}

[HarmonyPatch(typeof(Limb), "OnCollisionEnter2D")]
static class LimbImpactPatch {
    static void Postfix(Limb __instance, Collision2D col) {
        if (__instance == null)
            return;
        PlayerCrush.NoteLimb(__instance, col);
        PlayerCrush.NoteSprite(__instance, col);
        if (!Impact.Live())
            return;
        float speed = col.relativeVelocity.magnitude;
        GameObject other = col.collider != null ? col.collider.gameObject : null;
        // the holder hitting the brace, or the thing being thrown hitting the world.
        if (other != null && Impact.IsHeld(other))
            Impact.Hurt(other, speed);
        if (__instance.body != null && Impact.IsHeld(__instance.body.gameObject))
            Impact.Hurt(__instance.gameObject, speed);
    }
}

// the stun keeps them limp until the hit. this is what spends their health on that impact.
[HarmonyPatch(typeof(SpiderHandler), "OnCollisionEnter2D")]
static class SpiderImpactPatch {
    static void Postfix(SpiderHandler __instance, Collision2D collision) {
        if (__instance == null)
            return;
        GameObject go = __instance.gameObject;
        float speed = collision.relativeVelocity.magnitude;
        GameObject other = collision.collider != null ? collision.collider.gameObject : null;
        // a thrown prop into a walker hits twice as hard as throwing the walker itself.
        if (other != null && Loose.IsMissile(other)) {
            Impact.Hurt(go, speed, 2f);
            return;
        }
        bool mine = CreatureHold.IsFlung(go) || (Impact.Live() && Impact.IsHeld(go));
        if (!mine)
            return;
        Impact.Hurt(go, speed);
    }
}

// together turns player-vs-player collisions off. a held or just-thrown body hits like a skullcrusher until it slows down.
[HarmonyPatch(typeof(Body), "OnCollisionEnter2D")]
static class BodyCrushPatch {
    static void Postfix(Body __instance, Collision2D collision) {
        PlayerCrush.NoteBody(__instance, collision);
        PlayerCrush.NoteSpriteBody(__instance, collision);
    }
}

static class PlayerCrush {
    const float HitSpeed = 6f;
    // a spike trap stabs on any touch. a plant only shoves, so a slower bump still has to count.
    internal const float PropHitSpeed = 2.5f;
    const float RestSpeed = 4f;
    const float FlightTime = 2.5f;

    static Body live;
    static float until;
    static int lastId;
    static float nextTime;

    internal static void Sync(Body victim) {
        if (victim != null) {
            if (live != null && live != victim)
                Collide(live, true);
            live = victim;
            until = Time.time + FlightTime;
            Collide(live, false);
            return;
        }
        if (live == null)
            return;
        if (Time.time > until || Rested(live)) {
            Collide(live, true);
            live = null;
            return;
        }
        Collide(live, false);
    }

    internal static void NoteLimb(Limb limb, Collision2D col) {
        if (limb == null || limb.body == null)
            return;
        Note(limb.body, col, limb);
    }

    internal static void NoteBody(Body body, Collision2D col) {
        Note(body, col, null);
    }

    internal static void NoteSprite(Limb limb, Collision2D col) {
        if (limb == null || limb.body == null)
            return;
        NoteSprite(limb.body, col, limb);
    }

    internal static void NoteSpriteBody(Body body, Collision2D col) {
        NoteSprite(body, col, null);
    }

    // a thrown prop is not a body, so the player-vs-player pass never sees it.
    static void NoteSprite(Body owner, Collision2D col, Limb ownerLimb) {
        if (owner == null || col == null)
            return;
        GameObject missile = MissileOf(col, owner.transform);
        if (missile == null)
            return;
        NoteContact(col, missile, owner, ownerLimb);
    }

    internal static void NoteContact(Collision2D col, GameObject missile) {
        if (col == null || missile == null)
            return;
        Body victim = ForeignBody(col, missile.transform);
        Limb limb = victim != null ? LimbOf(null, victim, col) : null;
        NoteContact(col, missile, victim, limb);
    }

    internal static void NoteTrigger(GameObject missile, Collider2D other) {
        Rigidbody2D sprite = RigidOf(missile);
        float speed = sprite != null ? sprite.velocity.magnitude : 0f;
        NoteTrigger(missile, other, speed);
    }

    internal static void NoteTrigger(GameObject missile, Collider2D other, float speed) {
        if (missile == null || other == null || !Loose.IsMissile(missile))
            return;
        Body victim = BodyOf(other);
        if (!Foreign(victim, missile.transform))
            return;
        if (speed < PropHitSpeed)
            return;
        Limb limb = LimbOf(other, victim, null);
        if (!Crush(victim, limb, speed))
            return;
        Shove(victim, RigidOf(missile));
    }

    static void NoteContact(Collision2D col, GameObject missile, Body victim, Limb limb) {
        if (col == null || missile == null || !Loose.IsMissile(missile))
            return;
        if (!Foreign(victim, missile.transform))
            return;
        float speed = col.relativeVelocity.magnitude;
        Rigidbody2D sprite = RigidOf(missile);
        if (sprite != null && sprite.velocity.magnitude > speed)
            speed = sprite.velocity.magnitude;
        if (speed < PropHitSpeed)
            return;
        if (limb == null)
            limb = LimbOf(null, victim, col);
        if (!Crush(victim, limb, speed))
            return;
        Shove(victim, RigidOf(missile));
    }

    static GameObject MissileOf(Collision2D col, Transform victim) {
        if (IsMissileSide(col.collider, victim))
            return col.collider.gameObject;
        if (IsMissileSide(col.otherCollider, victim))
            return col.otherCollider.gameObject;
        if (col.gameObject != null && Loose.IsMissile(col.gameObject) && !col.gameObject.transform.IsChildOf(victim))
            return col.gameObject;
        return null;
    }

    static bool IsMissileSide(Collider2D col, Transform victim) {
        if (col == null || !Loose.IsMissile(col.gameObject))
            return false;
        return !col.transform.IsChildOf(victim);
    }

    static Body ForeignBody(Collision2D col, Transform missile) {
        Body a = BodyOf(col.collider);
        if (Foreign(a, missile))
            return a;
        Body b = BodyOf(col.otherCollider);
        if (Foreign(b, missile))
            return b;
        if (col.gameObject != null) {
            Body g = BodyOf(col.gameObject.GetComponent<Collider2D>());
            if (g == null) {
                Limb limb = col.gameObject.GetComponent<Limb>();
                if (limb == null)
                    limb = col.gameObject.GetComponentInParent<Limb>();
                if (limb != null)
                    g = limb.body;
                if (g == null)
                    g = col.gameObject.GetComponentInParent<Body>();
            }
            if (Foreign(g, missile))
                return g;
        }
        int count = col.contactCount;
        for (int i = 0; i < count; i++) {
            ContactPoint2D point = col.GetContact(i);
            Body hit = BodyOf(point.collider);
            if (Foreign(hit, missile))
                return hit;
            hit = BodyOf(point.otherCollider);
            if (Foreign(hit, missile))
                return hit;
        }
        return null;
    }

    static bool Foreign(Body body, Transform missile) {
        if (body == null || missile == null)
            return false;
        if (body == Grab.Host())
            return false;
        return !missile.IsChildOf(body.transform);
    }

    static Rigidbody2D RigidOf(GameObject go) {
        if (go == null)
            return null;
        Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
        if (rb == null)
            rb = go.GetComponentInParent<Rigidbody2D>();
        return rb;
    }

    // half is what two equal masses do. a lighter sprite gives less. a heavier one does not launch them harder than a person would.
    static void Shove(Body victim, Rigidbody2D sprite) {
        if (victim == null || sprite == null || victim.limbs == null)
            return;
        float spriteMass = sprite.mass > 0.2f ? sprite.mass : 0.2f;
        float bodyMass = 0f;
        Limb[] limbs = victim.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb != null && limb.rb != null)
                bodyMass += limb.rb.mass;
        }
        if (bodyMass < 0.2f)
            bodyMass = 0.2f;
        float share = spriteMass / (spriteMass + bodyMass);
        if (share > 0.5f)
            share = 0.5f;
        Vector2 kick = sprite.velocity * share;
        if (kick.sqrMagnitude < 0.01f)
            return;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null || !limb.rb.simulated)
                continue;
            limb.rb.velocity += kick;
        }
        sprite.velocity -= kick * (bodyMass / spriteMass);
    }

    static void Note(Body owner, Collision2D col, Limb ownerLimb) {
        if (live == null || owner == null || col == null)
            return;
        Body other = BodyOf(col.collider);
        if (other == null || other == owner)
            return;
        Body victim;
        if (owner == live)
            victim = other;
        else if (other == live)
            victim = owner;
        else
            return;
        if (col.relativeVelocity.magnitude <= HitSpeed)
            return;
        Limb limb = ownerLimb != null && ownerLimb.body == victim ? ownerLimb : LimbOf(col.collider, victim, col);
        Crush(victim, limb, col.relativeVelocity.magnitude);
    }

    // skullcrusher type 2. one contact per person, and the thrown body stays in one piece.
    static bool Crush(Body victim, Limb limb, float speed) {
        if (victim == null || limb == null)
            return false;
        int id = victim.GetInstanceID();
        if (id == lastId && Time.time < nextTime)
            return false;
        lastId = id;
        nextTime = Time.time + 0.25f;
        float armor = limb.GetArmorReduction();
        if (armor < 0.05f)
            armor = 0.05f;
        if (limb.isHead)
            victim.consciousness = 0f;
        limb.muscleHealth -= speed * 1.25f / armor;
        limb.skinHealth -= speed * 0.6f / armor;
        limb.bleedAmount += speed * 0.05f / armor;
        if (Random.value < 0.5f && speed > 25f)
            limb.Dislocate();
        if (Random.value < 0.5f && speed > 45f)
            limb.BreakBone();
        limb.DamageWearables(0.25f);
        victim.Ragdoll();
        return true;
    }

    static Body BodyOf(Collider2D col) {
        if (col == null)
            return null;
        Limb limb = col.GetComponent<Limb>();
        if (limb == null)
            limb = col.GetComponentInParent<Limb>();
        if (limb != null && limb.body != null)
            return limb.body;
        Body body = col.GetComponent<Body>();
        if (body == null)
            body = col.GetComponentInParent<Body>();
        return body;
    }

    static Limb LimbOf(Collider2D col, Body victim, Collision2D hit) {
        if (col != null) {
            Limb limb = col.GetComponent<Limb>();
            if (limb == null)
                limb = col.GetComponentInParent<Limb>();
            if (limb != null && limb.body == victim)
                return limb;
        }
        Vector2 pos = victim.transform.position;
        if (hit != null && hit.contactCount > 0)
            pos = hit.GetContact(0).point;
        else if (col != null)
            pos = col.bounds.center;
        return victim.GetClosestLimb(pos);
    }

    static bool Rested(Body body) {
        if (body == null)
            return true;
        float best = body.rb != null ? body.rb.velocity.sqrMagnitude : 0f;
        Limb[] limbs = body.limbs;
        if (limbs == null)
            return best < RestSpeed * RestSpeed;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null)
                continue;
            float sq = limb.rb.velocity.sqrMagnitude;
            if (sq > best)
                best = sq;
        }
        return best < RestSpeed * RestSpeed;
    }

    static void Collide(Body body, bool ignore) {
        if (body == null)
            return;
        if (!ignore)
            body.SetLimbCollisionType(CollisionDetectionMode2D.Continuous);
        Collider2D[] mine = body.GetComponentsInChildren<Collider2D>(true);
        Body[] bodies = Object.FindObjectsOfType<Body>();
        for (int i = 0; i < bodies.Length; i++) {
            Body other = bodies[i];
            if (other == null || other == body)
                continue;
            Collider2D[] theirs = other.GetComponentsInChildren<Collider2D>(true);
            for (int a = 0; a < mine.Length; a++) {
                Collider2D ca = mine[a];
                if (ca == null)
                    continue;
                for (int b = 0; b < theirs.Length; b++) {
                    Collider2D cb = theirs[b];
                    if (cb == null || cb == ca)
                        continue;
                    Physics2D.IgnoreCollision(ca, cb, ignore);
                }
            }
        }
    }
}

// a glowplant's layer never starts a player contact, so the body just gets pushed aside.
// a spike trap has its own trigger, which is why that one already ragdolls.
sealed class MissileTouch : MonoBehaviour {
    readonly Collider2D[] overlaps = new Collider2D[16];
    Collider2D[] cols;
    ContactFilter2D filter;
    bool filterReady;
    float lastSpeed;

    void OnCollisionEnter2D(Collision2D col) {
        PlayerCrush.NoteContact(col, gameObject);
    }

    void OnTriggerEnter2D(Collider2D other) {
        PlayerCrush.NoteTrigger(gameObject, other);
    }

    void FixedUpdate() {
        if (!Loose.IsMissile(gameObject)) {
            lastSpeed = 0f;
            return;
        }
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb == null)
            rb = GetComponentInParent<Rigidbody2D>();
        float now = rb != null ? rb.velocity.magnitude : 0f;
        // the plant stops on the body, so the speed that matters is the one from the frame before.
        float speed = now > lastSpeed ? now : lastSpeed;
        lastSpeed = now;
        if (speed < PlayerCrush.PropHitSpeed)
            return;
        if (cols == null)
            cols = GetComponentsInChildren<Collider2D>();
        Vector2 dir = now > 0.2f ? rb.velocity / now : Vector2.zero;
        for (int i = 0; i < cols.Length; i++) {
            Collider2D col = cols[i];
            if (col == null || !col.enabled)
                continue;
            Vector2 size = col.bounds.size;
            if (size.x < 0.05f || size.y < 0.05f)
                continue;
            if (!filterReady) {
                filter = new ContactFilter2D();
                filter.NoFilter();
                filterReady = true;
            }
            Vector2 center = (Vector2)col.bounds.center + dir * 0.2f;
            int found = Physics2D.OverlapBox(center, size + new Vector2(0.25f, 0.25f), 0f, filter, overlaps);
            for (int h = 0; h < found; h++) {
                Collider2D other = overlaps[h];
                if (other == null || other.transform == transform || other.transform.IsChildOf(transform))
                    continue;
                PlayerCrush.NoteTrigger(gameObject, other, speed);
            }
        }
    }
}

// the owner keeps sending a standing pose. ignore it for as long as the host is holding them.
[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.Server_HasForcedServerOwnershipOfBody))]
static class HoldOwnershipPatch {
    static void Postfix(ScavPlayer __instance, ref bool __result) {
        if (__result || __instance == null || __instance.netBody == null)
            return;
        if (!Con._DEV_TELEKINESIS)
            return;
        if (Grab.TryRemote(Con._DEV_TELEKINESIS_OBJECT, out NetBody nb) && nb == __instance.netBody)
            __result = true;
    }
}