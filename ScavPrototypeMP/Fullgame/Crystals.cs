using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Together;


#if FULLGAME



[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.OnCollisionEnter2D))]
static class CrystalBehaviour_OnCollisionEnter2D_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, Collision2D collision) {
        if (!Net.IsRunning) return true;

        if (SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(__instance.transform.position)) {
            var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
            var data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
            var is_body_hit = Util.TryGetBodyFromObject(collision.gameObject, out var body);
            if (is_body_hit) bgo.body = body;
            data["CrystalCollision"] = collision;
            data["CrystalCollisionIsBody"] = is_body_hit;
            foreach (CrystalEffect effect in __instance.effects) {
                effect.Touched(collision.gameObject);
            }
        }

        return false;
    }
}
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.BuildingHit))]
static class CrystalBehaviour_BuildingHit_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, AttackInfo atk) {
        if (!Net.IsRunning) return true;

        if (SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(__instance.transform.position)) {
            var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
            var data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
            if (Body_Attack_MultiplayerPatch.LastAttacker != null) {
                bgo.body = Body_Attack_MultiplayerPatch.LastAttacker;
                data["CrystalBuildingHitAttacker"] = Body_Attack_MultiplayerPatch.LastAttacker;
            }
            data["CrystalBuildingHitAtk"] = atk;
            foreach (CrystalEffect effect in __instance.effects) {
                effect.Hit(atk);
            }
        }

        return false;
    }
}

[HarmonyPatch(typeof(CrystalBurning), nameof(CrystalBurning.Burn))]
internal static class CrystalBurning_Burn_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }
    static bool Prefix(CrystalBurning __instance, ref Body body) {
        if (!Net.IsRunning || __instance.crystal == null) return true;

        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body != null) {
            body = bgo.body;
        }

        return true;
    }
}

[HarmonyPatch(typeof(CrystalEMP), nameof(CrystalEMP.TryEMP))]
internal static class CrystalEMP_TryEMP_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalEMP __instance) {
        var crystal = __instance.crystal;
        var activated = __instance.activated;
        if (!Net.IsRunning || crystal == null) return true;

        return true;
    }
}

[HarmonyPatch(typeof(CrystalElectric), nameof(CrystalElectric.Shock))]
internal static class CrystalElectric_Shock_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalElectric __instance, ref Body body) {
        if (!Net.IsRunning || __instance.crystal == null) return true;

        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body != null) {
            body = bgo.body;
        }

        return true;
    }
}



[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.body), MethodType.Getter)]
static class CrystalEnemy_get_body_MultiplayerPatch {

#if !RELEASE
    static bool _getter_working_announced = false;
    static void Prefix() {
        if (!_getter_working_announced) {
            _getter_working_announced = true;
            Plugin.log.LogInfo($"MethodType.Getter  is working: {new StackTrace()}");
        }
    }
#endif 

    static void Postfix(CrystalEnemy __instance, ref Body __result) {
        if (!Net.IsRunning) return;

        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body == null) {
            bgo.SetNearestBody();
            if (bgo.body == null) {
                bgo.body = PlayerCamera.main.body;
            }
        }
        __result = bgo.body;

    }
}


[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Update))]
internal static class CrystalEnemy_Update_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalEnemy __instance) {
        if (!Net.IsRunning) return true;

        var bgu = __instance.GetComponent<BodyGetterUpdater>();
        if (bgu == null) {
            bgu = __instance.AddComponent<BodyGetterUpdater>();
            bgu.cooldown = 5f;
            bgu.condition = (g) => { 
                if (g.TryGetComponent(out CrystalEnemy ce)) {
                    if (ce.close) {
                        List<RaycastHit2D> rh = new();
                        var result = Physics2D.Linecast(ce.body.GetLowerTorso().transform.position, ce.transform.position, new ContactFilter2D() { layerMask = LayerMask.GetMask("Ground") }, rh);
                        if (result > 0) {
                            rh.RemoveAll((x) => {
                                return x.transform == null || Util.TryGetBodyFromObject(x.transform, out var _) || ce.transform == x.transform;
                            });
                            result = rh.Count;
                        }
                        return result > 0;// something is blocking the view, pick another player
                    }
                    return true;
                }
                return true; 
            };
        }

        return true;
    }
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Awake))]
static class CrystalEnemy_Awake_MultiplayerPatch {
    static void Postfix(CrystalEnemy __instance) {
        if (!Net.IsRunning) return;
        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody(); // Just pick a nearest body
    }
}



[HarmonyPatch(typeof(CrystalHealing), nameof(CrystalHealing.Touched))]
internal static class CrystalHealing_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        instructions = TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null);

        return instructions;
    }

    static bool Prefix(CrystalHealing __instance, GameObject touched) {
        if (!Net.IsRunning) return true;

        return true;
    }
}


[HarmonyPatch(typeof(CrystalKinetic), nameof(CrystalKinetic.Touched))]
internal static class CrystalKinetic_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalKinetic __instance) {
        var crystal = __instance.crystal;
        if (!Net.IsRunning || crystal == null) return true;

        return true;
    }
}



[HarmonyPatch(typeof(CrystalMetamorphic), nameof(CrystalMetamorphic.Touched))]
internal static class CrystalMetamorphic_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        instructions = TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null);

        return instructions;
    }

    static bool Prefix(CrystalMetamorphic __instance, GameObject touched) {
        if (!Net.IsRunning) return true;

        return true;
    }
}





[HarmonyPatch(typeof(CrystalSeptic), nameof(CrystalSeptic.Update))]
internal static class CrystalSeptic_Update_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalSeptic __instance) {
        if (!Net.IsRunning) return true;

        var bgu = __instance.GetOrAddComponent<BodyGetterUpdater>();

        return true;
    }
}



[HarmonyPatch(typeof(CrystalTeleport), nameof(CrystalTeleport.Touched))]
internal static class CrystalTeleport_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
        codes = TranspilerHelpers.PatchCoroutines(codes, __originalMethod, null);

        return codes;
    }

    static bool Prefix(CrystalTeleport __instance) {
        var crystal = __instance.crystal;
        if (!Net.IsRunning || crystal == null) return true;

        return true;
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Hit))]
internal static class CrystalUnstable_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
        codes = TranspilerHelpers.PatchCoroutines(codes, __originalMethod, null);

        return codes;
    }

    static bool Prefix(CrystalUnstable __instance) {
        var crystal = __instance.crystal;
        if (!Net.IsRunning || crystal == null) return true;

        return true;
    }
}

#endif







