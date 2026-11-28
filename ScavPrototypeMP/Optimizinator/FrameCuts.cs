using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// Limb heal and the slow crystal effects tick at 10 Hz. The owed slice is applied on the tick
// so bleed, drip, and septic still advance with real time. Shader writes skip an unchanged value.
static class SlowTick {
    public const int Spread = 6;
    const float Cap = 0.25f;

    static readonly Dictionary<int, float> Owed = new Dictionary<int, float>(512);
    public static float Step;

    public static bool Due(object obj) {
        if (obj == null)
            return true;
        int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        Owed.TryGetValue(id, out float owed);
        owed += Time.deltaTime;
        if (owed > Cap)
            owed = Cap;
        int slot = (id & int.MaxValue) % Spread;
        if ((Time.frameCount % Spread) != slot) {
            Owed[id] = owed;
            return false;
        }
        Step = owed;
        Owed[id] = 0f;
        return true;
    }

    public static float Delta() {
        return Step > 0f ? Step : Time.deltaTime;
    }

    public static void ClearStep() {
        Step = 0f;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GetRunSettingFloat))]
static class RunSettingFloatCache {
    static int _frame = -1;
    static float _heal;
    static float _infectSpeed;
    static float _infectChance;
    static bool _haveHeal;
    static bool _haveSpeed;
    static bool _haveChance;

    static bool Prefix(string name, ref float __result) {
        int frame = Time.frameCount;
        if (frame != _frame) {
            _frame = frame;
            _haveHeal = false;
            _haveSpeed = false;
            _haveChance = false;
        }
        if (name == "healingrate" && _haveHeal) {
            __result = _heal;
            return false;
        }
        if (name == "infectionspeed" && _haveSpeed) {
            __result = _infectSpeed;
            return false;
        }
        if (name == "infectionchance" && _haveChance) {
            __result = _infectChance;
            return false;
        }
        return true;
    }

    static void Postfix(string name, float __result) {
        if (name == "healingrate") {
            _heal = __result;
            _haveHeal = true;
        } else if (name == "infectionspeed") {
            _infectSpeed = __result;
            _haveSpeed = true;
        } else if (name == "infectionchance") {
            _infectChance = __result;
            _haveChance = true;
        }
    }
}

// local body keeps full visuals. another body in the local view ticks at 10 Hz.
// a body outside that view skips HandleVisuals and HandleSounds.
static class BodyViewStep {
    static int _frame = -1;
    static int _id;
    static bool _allow;
    static bool _known;
    static int _camFrame = -1;
    static Camera _cam;
    static float _halfW;
    static float _halfH;

    internal static bool Allow(Body body) {
        if (body == null || !Net.IsRunning || Util.IsBodyLocal(body)) {
            SlowTick.Step = 0f;
            return true;
        }
        int frame = Time.frameCount;
        int id = body.GetInstanceID();
        if (_known && _frame == frame && _id == id)
            return _allow;
        _frame = frame;
        _id = id;
        _known = true;
        if (!InLocalView(body.transform.position)) {
            _allow = false;
            return false;
        }
        _allow = SlowTick.Due(body);
        return _allow;
    }

    internal static void End(Body body) {
        if (body != null && Net.IsRunning && !Util.IsBodyLocal(body))
            SlowTick.ClearStep();
    }

    static bool InLocalView(Vector2 pos) {
        int frame = Time.frameCount;
        if (_camFrame != frame) {
            _camFrame = frame;
            _cam = Camera.main;
            if (_cam != null) {
                _halfH = _cam.orthographicSize;
                if (_halfH < 1f)
                    _halfH = 8f;
                _halfW = _halfH * _cam.aspect;
            }
        }
        if (_cam == null)
            return true;
        const float margin = 3f;
        Vector2 c = _cam.transform.position;
        float dx = pos.x - c.x;
        float dy = pos.y - c.y;
        float limW = _halfW + margin;
        float limH = _halfH + margin;
        return dx * dx <= limW * limW && dy * dy <= limH * limH;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HandleVisuals))]
static class Body_HandleVisuals_Delta {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return CrystalSeptic_Update_Tick.RewriteDelta(instructions);
    }
}

[HarmonyPatch(typeof(Body), "HandleSounds")]
static class Body_HandleSounds_Step {
    static bool Prefix(Body __instance) {
        return BodyViewStep.Allow(__instance);
    }

    static void Postfix(Body __instance) {
        BodyViewStep.End(__instance);
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return CrystalSeptic_Update_Tick.RewriteDelta(instructions);
    }
}

// one other player per body each half second. a finished pass replaces the nearest the postfix uses.
static class DyingAloneScan {
    const float Interval = 0.5f;
    const float UnknownSqr = 0f;

    struct Pass {
        public float Next;
        public int Cand;
        public bool Any;
        public float Best;
        public bool Ready;
        public bool PublishedAny;
        public float PublishedBest;
    }

    static readonly List<Body> Order = new List<Body>(16);
    static int OrderCount = -1;
    static readonly Dictionary<int, Pass> ByBody = new Dictionary<int, Pass>(16);
    static readonly Dictionary<int, NetBody> Nets = new Dictionary<int, NetBody>(16);

    internal static void Read(Body subject, out bool anyNear, out float nearSqr) {
        if (subject == null) {
            anyNear = true;
            nearSqr = UnknownSqr;
            return;
        }
        int id = subject.GetInstanceID();
        if (!ByBody.TryGetValue(id, out Pass pass))
            pass = new Pass { Best = float.PositiveInfinity, PublishedBest = UnknownSqr, PublishedAny = true };
        float now = Time.unscaledTime;
        if (now >= pass.Next) {
            pass.Next = now + Interval;
            CheckOne(subject, ref pass);
        }
        ByBody[id] = pass;
        anyNear = pass.PublishedAny;
        nearSqr = pass.PublishedBest;
    }

    static void CheckOne(Body subject, ref Pass pass) {
        EnsureOrder();
        if (Order.Count == 0) {
            pass.Ready = true;
            pass.PublishedAny = false;
            pass.PublishedBest = float.PositiveInfinity;
            return;
        }
        if (pass.Cand >= Order.Count) {
            pass.PublishedAny = pass.Any;
            pass.PublishedBest = pass.Any ? pass.Best : float.PositiveInfinity;
            pass.Ready = true;
            pass.Cand = 0;
            pass.Any = false;
            pass.Best = float.PositiveInfinity;
        }
        Body candidate = Order[pass.Cand];
        pass.Cand++;
        if (candidate == null || candidate == subject)
            return;
        if (!NetOf(candidate, out NetBody other) || other.body == null)
            return;
        if (!other.body.conscious && !other.body.sleeping)
            return;
        float dist = ((Vector2)other.transform.position - (Vector2)subject.transform.position).sqrMagnitude;
        if (!pass.Any || dist < pass.Best) {
            pass.Any = true;
            pass.Best = dist;
        }
    }

    static void EnsureOrder() {
        var dict = ScavPlayer.BodyToPlayerDict;
        if (dict.Count == OrderCount)
            return;
        Order.Clear();
        foreach (var pair in dict)
            Order.Add(pair.Key);
        OrderCount = dict.Count;
    }

    static bool NetOf(Body body, out NetBody net) {
        int id = body.GetInstanceID();
        if (Nets.TryGetValue(id, out net) && net != null)
            return true;
        net = body.GetComponent<NetBody>();
        if (net == null) {
            Nets.Remove(id);
            return false;
        }
        Nets[id] = net;
        return true;
    }
}

[HarmonyPatch(typeof(Limb), "Update")]
static class Limb_Update_Tick {
    struct Key : System.IEquatable<Key> {
        public int Id;
        public int Name;

        public bool Equals(Key other) {
            return Id == other.Id && Name == other.Name;
        }

        public override bool Equals(object obj) {
            return obj is Key other && Equals(other);
        }

        public override int GetHashCode() {
            return Id ^ (Name * 397);
        }
    }

    static readonly Dictionary<Key, float> Shader = new Dictionary<Key, float>(1024);

    static void SetFloat(Material mat, string name, float value) {
        if (mat == null || name == null)
            return;
        Key key = new Key { Id = mat.GetInstanceID(), Name = name.GetHashCode() };
        if (Shader.TryGetValue(key, out float prev) && prev == value)
            return;
        Shader[key] = value;
        mat.SetFloat(name, value);
    }

    static bool Prefix(Limb __instance) {
        return SlowTick.Due(__instance);
    }

    static void Postfix() {
        SlowTick.ClearStep();
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        MethodInfo delta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        MethodInfo owed = AccessTools.Method(typeof(SlowTick), nameof(SlowTick.Delta));
        MethodInfo setFloat = AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(string), typeof(float) });
        MethodInfo setCached = AccessTools.Method(typeof(Limb_Update_Tick), nameof(SetFloat));
        foreach (CodeInstruction code in instructions) {
            if (code.Calls(delta))
                yield return new CodeInstruction(OpCodes.Call, owed);
            else if (code.Calls(setFloat))
                yield return new CodeInstruction(OpCodes.Call, setCached);
            else
                yield return code;
        }
    }
}

[HarmonyPatch(typeof(CrystalSeptic), "Update")]
static class CrystalSeptic_Update_Tick {
    static bool Prefix(CrystalSeptic __instance) {
        return SlowTick.Due(__instance);
    }

    static void Postfix() {
        SlowTick.ClearStep();
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return RewriteDelta(instructions);
    }

    internal static IEnumerable<CodeInstruction> RewriteDelta(IEnumerable<CodeInstruction> instructions) {
        MethodInfo delta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        MethodInfo owed = AccessTools.Method(typeof(SlowTick), nameof(SlowTick.Delta));
        foreach (CodeInstruction code in instructions) {
            if (code.Calls(delta))
                yield return new CodeInstruction(OpCodes.Call, owed);
            else
                yield return code;
        }
    }
}

[HarmonyPatch(typeof(CrystalDripping), "Update")]
static class CrystalDripping_Update_Tick {
    static bool Prefix(CrystalDripping __instance) {
        return SlowTick.Due(__instance);
    }

    static void Postfix() {
        SlowTick.ClearStep();
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return CrystalSeptic_Update_Tick.RewriteDelta(instructions);
    }
}

[HarmonyPatch(typeof(CrystalTemperature), "Update")]
static class CrystalTemperature_Update_Tick {
    static bool Prefix(CrystalTemperature __instance) {
        return SlowTick.Due(__instance);
    }

    static void Postfix() {
        SlowTick.ClearStep();
    }
}

[HarmonyPatch(typeof(CrystalHealing), "Update")]
static class CrystalHealing_Update_Tick {
    static bool Prefix(CrystalHealing __instance) {
        return SlowTick.Due(__instance);
    }

    static void Postfix() {
        SlowTick.ClearStep();
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Update))]
static class Body_Update_PainkillersCache {
    static readonly Dictionary<int, Painkillers> Found = new Dictionary<int, Painkillers>(32);

    static Painkillers Get(Body body) {
        int id = body.GetInstanceID();
        if (Found.TryGetValue(id, out Painkillers have) && have != null)
            return have;
        Painkillers got = body.GetComponent<Painkillers>();
        if (got != null)
            Found[id] = got;
        else
            Found.Remove(id);
        return got;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        MethodInfo get = AccessTools.Method(typeof(Body_Update_PainkillersCache), nameof(Get));
        bool done = false;
        foreach (CodeInstruction code in instructions) {
            if (!done && code.opcode == OpCodes.Call) {
                string op = code.operand is MethodBase method ? method.Name : (code.operand != null ? code.operand.ToString() : "");
                if (op.IndexOf("GetComponent", System.StringComparison.Ordinal) >= 0) {
                    done = true;
                    yield return new CodeInstruction(OpCodes.Call, get);
                    continue;
                }
            }
            yield return code;
        }
    }
}

[HarmonyPatch(typeof(Body), "HandleGroundedState")]
static class Body_HandleGroundedState_SideCast {
    static int _mask = int.MinValue;

    static int Mask() {
        if (_mask == int.MinValue)
            _mask = LayerMask.GetMask("Ground");
        return _mask;
    }

    static RaycastHit2D Side(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance, int layerMask, RaycastHit2D floor, Body body) {
        if (body.moveDir.x == 0f && floor.collider != null)
            return default;
        return Physics2D.BoxCast(origin, size, angle, direction, distance, layerMask);
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        MethodInfo getMask = AccessTools.Method(typeof(LayerMask), nameof(LayerMask.GetMask));
        MethodInfo box = AccessTools.Method(typeof(Physics2D), nameof(Physics2D.BoxCast), new[] {
            typeof(Vector2), typeof(Vector2), typeof(float), typeof(Vector2), typeof(float), typeof(int)
        });
        MethodInfo side = AccessTools.Method(typeof(Body_HandleGroundedState_SideCast), nameof(Side));
        MethodInfo mask = AccessTools.Method(typeof(Body_HandleGroundedState_SideCast), nameof(Mask));
        for (int i = 0; i < list.Count; i++) {
            if (!list[i].Calls(getMask) || i < 6)
                continue;
            if (list[i - 6].opcode != OpCodes.Ldc_I4_1 || list[i - 5].opcode != OpCodes.Newarr)
                continue;
            if (list[i - 4].opcode != OpCodes.Dup || list[i - 3].opcode != OpCodes.Ldc_I4_0)
                continue;
            if (list[i - 2].opcode != OpCodes.Ldstr || (list[i - 2].operand as string) != "Ground")
                continue;
            if (list[i - 1].opcode != OpCodes.Stelem_Ref)
                continue;
            CodeInstruction repl = new CodeInstruction(OpCodes.Call, mask);
            repl.labels.AddRange(list[i - 6].labels);
            repl.blocks.AddRange(list[i - 6].blocks);
            list[i - 6] = repl;
            list.RemoveRange(i - 5, 6);
            i -= 5;
        }
        int boxes = 0;
        for (int i = 0; i < list.Count; i++) {
            if (!list[i].Calls(box))
                continue;
            boxes++;
            if (boxes == 1)
                continue;
            CodeInstruction floor = new CodeInstruction(OpCodes.Ldloc_1);
            floor.labels.AddRange(list[i].labels);
            floor.blocks.AddRange(list[i].blocks);
            list[i].labels.Clear();
            list[i].blocks.Clear();
            list[i] = new CodeInstruction(OpCodes.Call, side);
            list.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(i, floor);
            i += 2;
        }
        return list;
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.Update))]
static class Item_Update_SleepWrites {
    static void Simulated(Rigidbody2D rb, bool on) {
        if (rb != null && rb.simulated != on)
            rb.simulated = on;
    }

    static void Enabled(Behaviour behaviour, bool on) {
        if (behaviour != null && behaviour.enabled != on)
            behaviour.enabled = on;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        MethodInfo sim = AccessTools.Method(typeof(Rigidbody2D), "set_simulated");
        MethodInfo enabled = AccessTools.Method(typeof(Behaviour), "set_enabled");
        MethodInfo simWrite = AccessTools.Method(typeof(Item_Update_SleepWrites), nameof(Simulated));
        MethodInfo enabledWrite = AccessTools.Method(typeof(Item_Update_SleepWrites), nameof(Enabled));
        foreach (CodeInstruction code in instructions) {
            if (code.Calls(sim))
                yield return new CodeInstruction(OpCodes.Call, simWrite);
            else if (code.Calls(enabled))
                yield return new CodeInstruction(OpCodes.Call, enabledWrite);
            else
                yield return code;
        }
    }
}
