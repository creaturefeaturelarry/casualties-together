using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

// host placement. same blue line as telekinesis. the prop is not simulated while it is held.
static class MoveTool {
    internal static GameObject Held;
    internal static Body HeldBody;

    // first scale we saw, so a later grab cannot treat the resized body as the new normal.
    static readonly Dictionary<int, Vector3> basis = new Dictionary<int, Vector3>();

    class FrozenBody {
        public Rigidbody2D rb;
        public RigidbodyType2D type;
        public bool simulated;
        public float gravity;
        public RigidbodyInterpolation2D interp;
    }

    static readonly List<FrozenBody> frozen = new List<FrozenBody>();

    internal static void Drop() {
        GameObject go = Held;
        Held = null;
        if (go == null) {
            HeldBody = null;
            return;
        }
        Restore();
        ReleaseColliders(go);
        if (HeldBody != null)
            JointMotors.Restore();
        HeldBody = null;
        Publish(go, false);
        Link.Clear();
    }

    internal static void Present() {
        if (!Con._DEV_MOVETOOL || !Multiplayer.IsNetworkActiveAndIsServer() || !Con.CanCheat()) {
            if (Held != null)
                Drop();
            Con._DEV_MOVETOOL = false;
            Beam.Hide();
            return;
        }

        if (!Con.IsConsoleOpen()) {
            if (Input.GetKeyDown(KeyCode.Mouse1))
                Take(Pick(Util.GetCursorWorldPos()));
            if (Held != null && Input.GetKeyUp(KeyCode.Mouse1)) {
                Snap(Held.transform, false);
                Drop();
            }
        }

        if (Held == null) {
            Beam.Hide();
            return;
        }

        Snap(Held.transform, !Con.IsConsoleOpen());
        Publish(Held, true);
        DrawBeam(Held.transform);
    }

    static void Take(GameObject go) {
        if (go == Held)
            return;
        if (Held != null)
            Drop();
        if (go == null)
            return;
        Held = go;
        HeldBody = go.GetComponent<Body>();
        Remember(go.transform);
        ScaledLights.Remember(go.transform);
        // a chunk parent disables the prop when that chunk sleeps, even after it was dragged away.
        // a player stays parented. pulling the rig apart leaves the limbs behind.
        if (HeldBody == null && go.transform.parent != null)
            go.transform.SetParent(null, true);
        if (HeldBody != null)
            SettlePlayer(HeldBody);
        Freeze(go);
        HoldColliders(go);
        Link.Clear();
    }

    static void Freeze(GameObject go) {
        frozen.Clear();
        Rigidbody2D[] bodies = go.GetComponentsInChildren<Rigidbody2D>(true);
        for (int i = 0; i < bodies.Length; i++) {
            Rigidbody2D rb = bodies[i];
            if (rb == null)
                continue;
            frozen.Add(new FrozenBody {
                rb = rb,
                type = rb.bodyType,
                simulated = rb.simulated,
                gravity = rb.gravityScale,
                interp = rb.interpolation,
            });
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.gravityScale = 0f;
            rb.interpolation = RigidbodyInterpolation2D.None;
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.simulated = true;
        }
    }

    static void Restore() {
        for (int i = 0; i < frozen.Count; i++) {
            FrozenBody saved = frozen[i];
            Rigidbody2D rb = saved.rb;
            if (rb == null)
                continue;
            rb.bodyType = saved.type;
            rb.simulated = saved.simulated;
            rb.gravityScale = saved.gravity;
            rb.interpolation = saved.interp;
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.position = rb.transform.position;
            rb.rotation = rb.transform.eulerAngles.z;
        }
        frozen.Clear();
    }

    static void Snap(Transform t, bool scrollOk) {
        if (HeldBody != null)
            SettlePlayer(HeldBody);
        if (scrollOk) {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0.001f || wheel < -0.001f) {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift)
                    Scale(t, wheel);
                else if (HeldBody == null) {
                    t.Rotate(0f, 0f, wheel * 15f, Space.World);
                    MapPoseAnchor spun = t.GetComponent<MapPoseAnchor>();
                    if (spun != null) {
                        spun.keepRot = true;
                        spun.rot = t.eulerAngles.z;
                    }
                }
            }
        }
        Vector2 cursor = Util.GetCursorWorldPos();
        Link.Bind(t, cursor);
        Vector2 anchor = Link.Anchor(t);
        t.position += (Vector3)(cursor - anchor);
        for (int i = 0; i < frozen.Count; i++) {
            Rigidbody2D rb = frozen[i].rb;
            if (rb == null)
                continue;
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.position = rb.transform.position;
            rb.rotation = rb.transform.eulerAngles.z;
        }
    }

    static void Scale(Transform t, float notches) {
        Remember(t);
        Vector3 origin = basis[t.gameObject.GetInstanceID()];
        bool player = t.GetComponent<Body>() != null;
        // players shrink to half and grow to 10x. everything else is 5x smaller through 20x bigger.
        float minMul = player ? 0.5f : 0.2f;
        float maxMul = player ? 10f : 20f;
        float factor = Mathf.Pow(1.1f, notches);
        Vector3 s = t.localScale;
        s.x = ClampAxis(s.x, origin.x, factor, minMul, maxMul);
        s.y = ClampAxis(s.y, origin.y, factor, minMul, maxMul);
        t.localScale = s;
        ScaledLights.Apply(t);
    }

    static void Remember(Transform t) {
        NoteOriginal(t, t.localScale);
    }

    internal static void NoteOriginal(Transform t, Vector3 original) {
        if (t == null)
            return;
        int id = t.gameObject.GetInstanceID();
        if (!basis.ContainsKey(id))
            basis[id] = original;
    }

    internal static bool HasBasis(GameObject go) {
        return go != null && basis.ContainsKey(go.GetInstanceID());
    }

    internal static bool TryBasisMag(Transform t, out float mag) {
        mag = 1f;
        if (t == null || !basis.TryGetValue(t.gameObject.GetInstanceID(), out Vector3 origin))
            return false;
        mag = Mathf.Max(Mathf.Abs(origin.x), Mathf.Abs(origin.y));
        if (mag < 0.0001f)
            mag = 1f;
        return true;
    }

    internal static bool Dragging(Body body) {
        return body != null && HeldBody == body;
    }

    internal static bool IsHeld(Body body) {
        if (Dragging(body))
            return true;
        if (Net.IsServer || body == null)
            return false;
        if (!body.TryGetNetBody(out NetBody nb) || nb == null || nb.syncBody == null)
            return false;
        return nb.syncBody._moveHeld.Value;
    }

    // the owner's machine keeps its own physics. while the host is dragging them, that copy follows the host.
    internal static void Follow(NetBody nb) {
        if (Net.IsServer || nb == null || nb.body == null || nb.syncBody == null)
            return;
        nb.SetBodyPosition(nb.syncBody._position.Value);
        HoldStill(nb.body.rb, true);
        Limb[] limbs = nb.body.limbs;
        if (limbs == null)
            return;
        for (int i = 0; i < limbs.Length; i++) {
            if (limbs[i] != null)
                HoldStill(limbs[i].rb, true);
        }
    }

    internal static void ReleaseFollow(Body body) {
        if (Net.IsServer || body == null)
            return;
        HoldStill(body.rb, false);
        Limb[] limbs = body.limbs;
        if (limbs == null)
            return;
        for (int i = 0; i < limbs.Length; i++) {
            if (limbs[i] != null)
                HoldStill(limbs[i].rb, false);
        }
    }

    static readonly Dictionary<int, RigidbodyType2D> heldTypes = new Dictionary<int, RigidbodyType2D>();

    static void HoldStill(Rigidbody2D rb, bool hold) {
        if (rb == null)
            return;
        int id = rb.GetInstanceID();
        if (!hold) {
            if (heldTypes.TryGetValue(id, out RigidbodyType2D type)) {
                rb.bodyType = type;
                heldTypes.Remove(id);
            }
            return;
        }
        if (!heldTypes.ContainsKey(id))
            heldTypes[id] = rb.bodyType;
        rb.velocity = Vector2.zero;
        rb.angularVelocity = 0f;
        if (rb.bodyType != RigidbodyType2D.Kinematic)
            rb.bodyType = RigidbodyType2D.Kinematic;
    }

    static float ClampAxis(float value, float origin, float factor, float minMul, float maxMul) {
        float sign = value < 0f ? -1f : 1f;
        float baseMag = Mathf.Abs(origin);
        if (baseMag < 0.0001f)
            baseMag = 1f;
        float mag = Mathf.Abs(value);
        if (mag < 0.0001f)
            mag = baseMag;
        mag *= factor;
        float lo = baseMag * minMul;
        float hi = baseMag * maxMul;
        if (mag < lo)
            mag = lo;
        if (mag > hi)
            mag = hi;
        return sign * mag;
    }

    // turning forces the height back to 1. a resized player keeps the height they had.
    static void SettlePlayer(Body body) {
        Vector3 size = body.transform.localScale;
        body.moveDir = Vector2.zero;
        body.shock = 100f;
        if (body.currentClimbable != null)
            body.StopClimbing();
        if (body.TryGetNetBody(out NetBody nb) && nb != null && nb.IsPiggybacking())
            nb.StopPiggyback();
        if (body.standing)
            body.Ragdoll();
        JointMotors.Relax(body);
        body.transform.localScale = size;
    }

    static GameObject Pick(Vector2 cursor) {
        Collider2D[] hits = Physics2D.OverlapPointAll(cursor);
        for (int i = 0; i < hits.Length; i++) {
            Collider2D hit = hits[i];
            if (hit == null)
                continue;
            GameObject obj = hit.gameObject;
            if (obj.GetComponent<ChunkScript>() != null)
                continue;
            Item item = obj.GetComponentInParent<Item>();
            Body body = obj.GetComponentInParent<Body>();
            // a loose item is world loot. worn gear is part of the person holding it.
            if (item != null && body == null)
                continue;
            if (body != null)
                return body.gameObject;
            BuildingEntity building = obj.GetComponentInParent<BuildingEntity>();
            if (building != null)
                return building.gameObject;
            Rigidbody2D rb = obj.GetComponentInParent<Rigidbody2D>();
            if (rb != null && rb.GetComponentInParent<Item>() == null)
                return rb.gameObject;
        }
        return null;
    }

    static void DrawBeam(Transform held) {
        Body host = Grab.Host();
        if (host == null) {
            Beam.Hide();
            return;
        }
        Link.Bind(held, Util.GetCursorWorldPos());
        Vector2 from = host.rb != null && host.standing ? host.rb.position : (Vector2)host.transform.position;
        if (!host.standing) {
            Limb torso = host.GetUpperTorso();
            if (torso != null && torso.rb != null)
                from = torso.rb.position;
        }
        Beam.Show(from, Link.Anchor(held));
    }

    static void Publish(GameObject go, bool ghost) {
        if (go == null || !Net.IsServer)
            return;
        if (!NetObjectRegistry.ObjectShouldBeSynced(go))
            return;
        if (string.IsNullOrEmpty(SyncInfo.GetItemID(go)) && !NetObjectRegistry.IsRegistered(go))
            return;
        SyncInfo si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(go);
        if (si == null || si.IsDestroyed)
            return;
        si.placeGhost.Value = ghost;
        si.WriteObjectIntoPacket();
    }

    class SavedCols {
        public Collider2D[] cols;
        public bool[] on;
    }

    static readonly Dictionary<GameObject, SavedCols> colliders = new Dictionary<GameObject, SavedCols>();

    internal static void HoldColliders(GameObject go) {
        if (go == null || colliders.ContainsKey(go))
            return;
        Collider2D[] cols = go.GetComponentsInChildren<Collider2D>(true);
        bool[] on = new bool[cols.Length];
        for (int i = 0; i < cols.Length; i++) {
            Collider2D col = cols[i];
            on[i] = col != null && col.enabled;
            if (col != null)
                col.enabled = false;
        }
        colliders[go] = new SavedCols { cols = cols, on = on };
    }

    internal static void ReleaseColliders(GameObject go) {
        if (colliders.Count == 0 || go == null || !colliders.TryGetValue(go, out SavedCols saved))
            return;
        colliders.Remove(go);
        for (int i = 0; i < saved.cols.Length; i++) {
            Collider2D col = saved.cols[i];
            if (col != null)
                col.enabled = saved.on[i];
        }
    }
}

// the host is dragging this person. pasting the last pose would pull them out of the beam.
[HarmonyPatch(typeof(SyncBody), nameof(SyncBody.ApplyDataToBody))]
static class MoveTool_HoldBody_MultiplayerPatch {
    static bool Prefix(SyncBody __instance) {
        if (!Net.IsServer || __instance == null || __instance.nb == null || __instance.nb.body == null)
            return true;
        return MoveTool.HeldBody != __instance.nb.body;
    }
}

// SwitchDir writes the height back to 1. keep a resized player's height.
[HarmonyPatch(typeof(Body), "SwitchDir")]
static class MoveTool_KeepHeight_MultiplayerPatch {
    static void Prefix(Body __instance, ref float __state) {
        __state = MoveTool.HasBasis(__instance.gameObject) ? __instance.transform.localScale.y : float.NaN;
    }

    static void Postfix(Body __instance, float __state) {
        if (float.IsNaN(__state))
            return;
        Vector3 s = __instance.transform.localScale;
        s.y = __state;
        __instance.transform.localScale = s;
    }
}
