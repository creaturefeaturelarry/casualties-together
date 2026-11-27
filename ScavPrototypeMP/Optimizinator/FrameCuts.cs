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
