using CasualtiesTogetherUtils;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

// the body collider grows with the transform. the movement checks still use the unscaled box.
static class BodyScale {
    public static float Factor(Body body) {
        if (body == null)
            return 1f;
        float y = Mathf.Abs(body.transform.localScale.y);
        if (y < 0.02f)
            return 1f;
        return y;
    }

    static Vector2 AbsScale(Body body) {
        Vector3 s = body.transform.localScale;
        return new Vector2(Mathf.Max(0.02f, Mathf.Abs(s.x)), Mathf.Max(0.02f, Mathf.Abs(s.y)));
    }

    struct Box {
        public bool skip;
        public bool sized;
        public bool wasStanding;
        public Vector2 size;
        public Vector2 offset;
        public float edge;
        public float jump;
        public Vector2 orig;
    }

    static void Scale(Body body, out Box state) {
        state = default;
        if (Mathf.Abs(Factor(body) - 1f) < 0.02f || body.col == null) {
            state.skip = true;
            return;
        }
        BoxCollider2D col = body.col;
        state.size = col.size;
        state.offset = col.offset;
        state.edge = col.edgeRadius;
        Vector2 scale = AbsScale(body);
        col.size = Vector2.Scale(col.size, scale);
        col.offset = Vector2.Scale(col.offset, scale);
        col.edgeRadius *= scale.y;
    }

    static void Restore(Body body, Box state) {
        if (state.skip || body.col == null)
            return;
        body.col.size = state.size;
        body.col.offset = state.offset;
        body.col.edgeRadius = state.edge;
    }

    // the stock probe reads col.size as world units and ignores the transform scale, so a
    // small body still "lands" at a normal person's feet and plays the dust every frame.
    // in the air the down-cast is a fixed 3.5, which reaches the floor from a small hop.
    [HarmonyPatch(typeof(Body), "HandleGroundedState")]
    static class Ground {
        static void Prefix(Body __instance, out Box __state) {
            Scale(__instance, out __state);
            if (__state.skip)
                return;
            __state.jump = __instance.jumpSpeed;
            __state.orig = __instance.origColSize;
            float factor = Factor(__instance);
            __instance.jumpSpeed *= factor;
            __instance.origColSize = Vector2.Scale(__state.orig, AbsScale(__instance));
        }

        static void Postfix(Body __instance, Box __state) {
            if (!__state.skip) {
                __instance.jumpSpeed = __state.jump;
                __instance.origColSize = __state.orig;
            }
            Restore(__instance, __state);
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            System.Reflection.MethodInfo factor = AccessTools.Method(typeof(BodyScale), nameof(Factor));
            bool didBox = false;
            bool didReach = false;
            bool didSkin = false;
            foreach (CodeInstruction ins in instructions) {
                yield return ins;
                if (ins.opcode != OpCodes.Ldc_R4 || !(ins.operand is float value))
                    continue;
                bool scale = false;
                if (value == 3.5f && !didReach) {
                    didReach = true;
                    scale = true;
                } else if (value == 0.25f && !didBox) {
                    didBox = true;
                    scale = true;
                } else if (value == 0.2f && !didSkin) {
                    didSkin = true;
                    scale = true;
                }
                if (!scale)
                    continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, factor);
                yield return new CodeInstruction(OpCodes.Mul);
            }
        }
    }

    // takeoff dust is placed a full body below the root.
    [HarmonyPatch(typeof(Body), "Jump")]
    static class JumpDust {
        static void Prefix(Body __instance, out Vector2 __state) {
            __state = __instance.origColSize;
            if (Mathf.Abs(Factor(__instance) - 1f) < 0.02f)
                return;
            __instance.origColSize = Vector2.Scale(__state, AbsScale(__instance));
        }

        static void Postfix(Body __instance, Vector2 __state) {
            __instance.origColSize = __state;
        }
    }

    static int physicsBox;

    // getting up measures the full body. a small body is stood up at a normal waist, then falls.
    [HarmonyPatch(typeof(Body), nameof(Body.Stand))]
    static class GetUp {
        static void Prefix(Body __instance, out Box __state) {
            __state = default;
            __state.wasStanding = __instance.standing;
            Scale(__instance, out Box box);
            __state.skip = box.skip;
            __state.size = box.size;
            __state.offset = box.offset;
            __state.edge = box.edge;
            __state.orig = __instance.origColSize;
            // HandlePhysics already swapped origColSize for the world size.
            if (!box.skip && physicsBox <= 0) {
                __instance.origColSize = Vector2.Scale(__state.orig, AbsScale(__instance));
                __state.sized = true;
            }
        }

        static void Postfix(Body __instance, Box __state) {
            if (__state.sized)
                __instance.origColSize = __state.orig;
            Restore(__instance, __state);
            if (!__state.wasStanding && __instance.standing)
                Weight.Mark(__instance);
        }
    }

    // walking steps up from the unscaled feet, which are under the floor, so a small body gets lifted back to the waist.
    [HarmonyPatch(typeof(Body), "TryStepUp")]
    static class Step {
        static void Prefix(Body __instance, out Box __state) {
            // HandlePhysics calls this before it rewrites the box, so the size is still local.
            Scale(__instance, out __state);
        }

        static void Postfix(Body __instance, Box __state) {
            Restore(__instance, __state);
        }
    }

    // the ceiling check and the crouch box are origColSize, so a small body is treated as full height while moving.
    [HarmonyPatch(typeof(Body), "HandlePhysics")]
    static class PhysicsBox {
        static void Prefix(Body __instance, out Vector2 __state) {
            physicsBox++;
            __state = __instance.origColSize;
            if (Mathf.Abs(Factor(__instance) - 1f) < 0.02f)
                return;
            __instance.origColSize = Vector2.Scale(__state, AbsScale(__instance));
        }

        static void Postfix(Body __instance, Vector2 __state) {
            physicsBox--;
            Vector2 scaled = __instance.origColSize;
            __instance.origColSize = __state;
            if (scaled == __state || __state.x < 0.001f || __state.y < 0.001f || __instance.col == null)
                return;
            Vector2 back = new Vector2(__state.x / scaled.x, __state.y / scaled.y);
            __instance.col.size = Vector2.Scale(__instance.col.size, back);
            __instance.col.offset = Vector2.Scale(__instance.col.offset, back);
        }
    }

    [HarmonyPatch(typeof(Body), "actualJumpSpeed", MethodType.Getter)]
    static class Jump {
        static void Postfix(Body __instance, ref float __result) {
            __result *= Factor(__instance);
        }
    }

    [HarmonyPatch(typeof(Body), "actualMaxSpeed", MethodType.Getter)]
    static class Speed {
        static void Postfix(Body __instance, ref float __result) {
            __result *= Factor(__instance);
        }
    }

    // a small body was falling a full person's distance. keep the fall in proportion, and leave a jetpack's zero gravity alone.
    [HarmonyPatch(typeof(Body), "FixedUpdate")]
    static class Weight {
        static readonly Dictionary<int, float> last = new Dictionary<int, float>();
        static readonly Dictionary<int, float> footScale = new Dictionary<int, float>();
        static readonly Dictionary<int, Calm> calm = new Dictionary<int, Calm>();

        struct Calm {
            public int until;
            public float spike;
        }

        // stand already put the feet down, so the next plant must not shift them again.
        internal static void Mark(Body body) {
            if (body == null)
                return;
            footScale[body.GetInstanceID()] = Factor(body);
        }

        static void Postfix(Body __instance) {
            CalmDown(__instance);
            if (MoveTool.IsHeld(__instance)) {
                if (!Net.IsServer && __instance.TryGetNetBody(out NetBody nb))
                    MoveTool.Follow(nb);
                return;
            }
            MoveTool.ReleaseFollow(__instance);
            Plant(__instance);
            float factor = Factor(__instance);
            if (__instance.rb != null)
                Pull(__instance.rb, factor);
            Limb[] limbs = __instance.limbs;
            if (limbs == null || Mathf.Abs(factor - 1f) < 0.02f)
                return;
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                if (limb != null && limb.rb != null)
                    Pull(limb.rb, factor);
            }
        }

        static void Pull(Rigidbody2D rb, float factor) {
            float g = rb.gravityScale;
            if (g <= 0.05f)
                return;
            int id = rb.GetInstanceID();
            bool known = last.TryGetValue(id, out float prev);
            // standing gravity is 1, and a released jump raises it to 2.5. both are full-size rates.
            float stock = 0f;
            if (Mathf.Abs(g - 1f) < 0.08f)
                stock = 1f;
            else if (Mathf.Abs(g - 2.5f) < 0.12f)
                stock = 2.5f;
            else if (known && Mathf.Abs(g - prev) < 0.08f)
                return;
            else
                return;
            float next = stock * factor;
            rb.gravityScale = next;
            last[id] = next;
        }

        // the collider is centered on the waist. shrinking it leaves the feet at the old waist, then they fall.
        static void Plant(Body body) {
            if (Net.IsClient && !Net.IsServer && !body.IsBodyLocal())
                return;
            float now = Factor(body);
            int id = body.GetInstanceID();
            if (!footScale.TryGetValue(id, out float prev)) {
                footScale[id] = now;
                return;
            }
            if (Mathf.Abs(now - prev) < 0.001f)
                return;
            float half = body.origColSize.y * 0.5f;
            float dy = half * (now - prev);
            Shift(body, dy);
            footScale[id] = now;
        }

        struct Frozen {
            public Rigidbody2D rb;
            public RigidbodyType2D type;
            public bool simulated;
            public Vector2 vel;
            public float ang;
        }

        // moving a dynamic body by its transform makes the next physics step treat the shift as velocity.
        // a one-unit plant was arriving as about -50, which is the shift divided by one fixed frame.
        static void Shift(Body body, float dy) {
            var frozen = new List<Frozen>(8);
            Freeze(frozen, body.rb);
            Limb[] limbs = body.limbs;
            if (limbs != null) {
                for (int i = 0; i < limbs.Length; i++) {
                    Limb limb = limbs[i];
                    if (limb != null)
                        Freeze(frozen, limb.rb);
                }
            }
            body.transform.position += new Vector3(0f, dy, 0f);
            Physics2D.SyncTransforms();
            for (int i = 0; i < frozen.Count; i++) {
                Frozen held = frozen[i];
                if (held.rb == null)
                    continue;
                held.rb.position = held.rb.transform.position;
                held.rb.simulated = held.simulated;
                held.rb.bodyType = held.type;
                held.rb.velocity = new Vector2(held.vel.x, 0f);
                held.rb.angularVelocity = held.ang;
            }
            float step = Time.fixedDeltaTime;
            if (step < 0.005f)
                step = 0.02f;
            calm[body.GetInstanceID()] = new Calm {
                until = Time.frameCount + 3,
                spike = Mathf.Abs(dy) / step,
            };
        }

        static void Freeze(List<Frozen> frozen, Rigidbody2D rb) {
            if (rb == null)
                return;
            for (int i = 0; i < frozen.Count; i++) {
                if (frozen[i].rb == rb)
                    return;
            }
            frozen.Add(new Frozen {
                rb = rb,
                type = rb.bodyType,
                simulated = rb.simulated,
                vel = rb.velocity,
                ang = rb.angularVelocity,
            });
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.bodyType = RigidbodyType2D.Kinematic;
        }

        static void CalmDown(Body body) {
            if (body.rb == null || !calm.TryGetValue(body.GetInstanceID(), out Calm held))
                return;
            if (Time.frameCount > held.until) {
                calm.Remove(body.GetInstanceID());
                return;
            }
            Vector2 vel = body.rb.velocity;
            if (vel.y < 0f - held.spike * 0.5f)
                body.rb.velocity = new Vector2(vel.x, 0f);
        }
    }
}

// a light's radius is in world units. the transform scale does not change it.
static class ScaledLights {
    struct Base {
        public float outer;
        public float inner;
        public float scale;
    }

    static readonly Dictionary<int, Base> saved = new Dictionary<int, Base>();

    internal static void Remember(Transform root) {
        if (root == null)
            return;
        float now = Mag(root);
        Light2D[] lights = root.GetComponentsInChildren<Light2D>(true);
        for (int i = 0; i < lights.Length; i++) {
            Light2D light = lights[i];
            if (light == null || saved.ContainsKey(light.GetInstanceID()))
                continue;
            float origin = now;
            if (MoveTool.TryBasisMag(root, out float basis))
                origin = basis;
            saved[light.GetInstanceID()] = new Base {
                outer = light.pointLightOuterRadius,
                inner = light.pointLightInnerRadius,
                scale = origin,
            };
        }
    }

    internal static void Apply(Transform root) {
        if (root == null)
            return;
        Remember(root);
        float now = Mag(root);
        Light2D[] lights = root.GetComponentsInChildren<Light2D>(true);
        for (int i = 0; i < lights.Length; i++) {
            Light2D light = lights[i];
            if (light == null || !saved.TryGetValue(light.GetInstanceID(), out Base basis))
                continue;
            if (basis.scale < 0.0001f)
                continue;
            float mul = now / basis.scale;
            light.pointLightOuterRadius = basis.outer * mul;
            light.pointLightInnerRadius = basis.inner * mul;
        }
    }

    static float Mag(Transform root) {
        Vector3 s = root.lossyScale;
        float m = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
        return m < 0.0001f ? 1f : m;
    }
}
