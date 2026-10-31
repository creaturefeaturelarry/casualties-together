using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Together;

public class AnyDataContainerComponent : MonoBehaviour {
    public readonly Dictionary<string, object> dict = new();

    public void Set(string key, object val) {
        dict[key] = val;
    }


    public bool WasntTouched(string key) {
        if (!dict.TryGetValue(key, out var val) || (val == (object)false || val == null)) {
            dict[key] = true;
            return true;
        }
        return false;
    }

    public T Get<T>(string key) {
        return (T)dict[key];
    }
    public T GetOrDefault<T>(string key, T d) {
        if (dict.TryGetValue(key, out var val)) {
            return (T)val;
        }
        return d;
    }
    
    public object this[string key] { 
        get {
            return dict[key];
        }
        set {
            dict[key] = value;
        }
    }

}

public class BodyGetterUpdater : MonoBehaviour {
    public float cooldown = 10f;
    public float t = 0f;
    public Func<GameObject, bool> condition = null;
    void Update() {
        t += Time.deltaTime;

        if (t > cooldown) {
            t = 0f;
            if (condition == null || condition(this.gameObject)) 
                this.GetOrAddComponent<BodyGetterOverrider>().SetNearestBody();
        }
    }
}
public class BodyGetterOverrider : MonoBehaviour {

    // Use GetBody()!!!
    public Body body = null;

    void Start() {
        GetBody();
    }

    public void SetBody(Body b) {
        body = b;
        foreach (var item in this.GetComponentsInChildren<BodyGetterOverrider>(true)) {
            item.body = b;
        }
    }
    public Body GetBody() {
        if (body == null) {
            if (TryGetBodyFromParent()) {
                return body;
            } else {
                SetNearestBody();
                if (body == null) {
                    body = PlayerCamera.main.body;
                }
                return body;
            }
        }
        return body;
    }
    public Body GetBody(object source) {
        return GetBody();
    }
#if TESTING 
#if TESTING 
    float timer = 0;
#endif
    public Body GetBodyDev() {
        var b = GetBody();
#if TESTING
        if (Time.realtimeSinceStartup - timer > 1 && DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            timer = Time.realtimeSinceStartup;
            var str = $"GetBodyDev: {b.BodyToString()}";
            DebugHelp.OnNetEvent(this.transform.position, str, Color.cyan, 0.95f);
        }
#endif
        return b;
    }
#endif

    public static Body GetBodyStatic(object source) {

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        Component c = null;
        if (source is Component) {
            c = (Component)source;
        } else if (source is CrystalEffect crystal) {
            c = crystal.crystal;
        } else {
            throw new InvalidTypeException($"{source.GetType().FullName} ({source.ToSafeString()}) is not supported in GetBodyStatic!");
        }

        var overrider = c.GetComponent<BodyGetterOverrider>();

        if (overrider == null)
            overrider = c.AddComponent<BodyGetterOverrider>();

        var result = overrider.GetBody(c);

        if (result == null) {
            throw new InvalidOperationException($"Could not resolve Body for {c.GetType().FullName}");
        }

        return result;
    }

    public void SetNearestBody() {
        var nb = NetBody.GetNearestBodyPrioritizeAlive(this.transform.position).Item1;
        if (nb != null) {
            body = nb.body;
        }
    }

    public bool TryGetBodyFromParent() {
        return TryGetBodyFromParent(out var _);
    }
    public bool TryGetBodyFromParent(out Body b) {
        b = GetComponentInParent<Body>();
        if (b != null) {
            body = b;
            return true;
        }
        return false;
    }


    // AI generated transpilers
    private static readonly MethodInfo GetBodyStaticMethodInfo =
        AccessTools.Method(typeof(BodyGetterOverrider), nameof(BodyGetterOverrider.GetBodyStatic));

    public static IEnumerable<CodeInstruction> TranspileGetter(
        IEnumerable<CodeInstruction> instructions,
        Type targetType) {
        var codes = new List<CodeInstruction>(instructions);

        for (int i = 0; i < codes.Count; i++) {
            var code = codes[i];

            if ((code.opcode == OpCodes.Call ||
                 code.opcode == OpCodes.Callvirt) &&
                code.operand is MethodInfo method &&
                method.IsSpecialName &&
                method.Name.StartsWith("get_") &&
                method.ReturnType == typeof(Body) &&
                method.DeclaringType != null &&
                targetType.IsAssignableFrom(method.DeclaringType)) {
                code.opcode = OpCodes.Call;
                code.operand = GetBodyStaticMethodInfo;
            }
        }

        return codes;
    }


    public static IEnumerable<CodeInstruction> TranspileField(
        IEnumerable<CodeInstruction> instructions,
        Type targetType) {
        var codes = new List<CodeInstruction>(instructions);

        for (int i = 0; i < codes.Count; i++) {
            var code = codes[i];

            if (code.opcode == OpCodes.Ldfld &&
                code.operand is FieldInfo field &&
                field.FieldType == typeof(Body) &&
                field.DeclaringType != null &&
                targetType.IsAssignableFrom(field.DeclaringType)) {
                code.opcode = OpCodes.Call;
                code.operand = GetBodyStaticMethodInfo;
            }
        }

        return codes;
    }


    public static IEnumerable<CodeInstruction> TranspileRawAccess(
        IEnumerable<CodeInstruction> instructions,
        Type targetType) {
        var codes = new List<CodeInstruction>(instructions);

        for (int i = 0; i < codes.Count - 1; i++) {
            var first = codes[i];
            var second = codes[i + 1];

            if (first.opcode == OpCodes.Ldsfld &&
                first.operand is FieldInfo singletonField &&
                singletonField.FieldType == typeof(PlayerCamera) &&
                second.opcode == OpCodes.Ldfld &&
                second.operand is FieldInfo aField &&
                aField.DeclaringType == typeof(PlayerCamera) &&
                aField.FieldType == typeof(Body)) {
                // Replace singleton load with the accessing component.
                first.opcode = OpCodes.Ldarg_0;
                first.operand = null;

                // Replace
                second.opcode = OpCodes.Call;
                second.operand = GetBodyStaticMethodInfo;
            }
        }

        return codes;
    }


    public static IEnumerable<CodeInstruction> TranspileAll(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        Type targetType) {
        var codes = instructions;

        codes = TranspileGetter(codes, targetType);
        codes = TranspileField(codes, targetType);
        codes = TranspileRawAccess(codes, targetType);

        return codes;
    }
    public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(
            instructions,
            generator,
            __originalMethod.DeclaringType);
    }
    public static void HarmonyPatchEntireClass(Harmony harmony, Type targetType) {
        const BindingFlags flags =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            //BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        var methods = targetType.GetMethods(flags).Cast<MethodBase>();
        //.Concat(targetType.GetConstructors(flags));

        foreach (var method in methods.Distinct()) {
            if (method.IsAbstract || method.ContainsGenericParameters)
                continue;

            harmony.Patch(
                method,
                transpiler: new HarmonyMethod(
                    typeof(BodyGetterOverrider),
                    nameof(BodyGetterOverrider.Transpiler)));
        }
    }
}


public static class TranspilerHelpers {



    public static void ShakerOverridenMethod(object source, float intensity) {
#if DEBUG
        log.l($"ShakerOverrider called {source} - {intensity}");
#endif
        if (Net.IsRunning) {
            Component c = null;
            if (source is CrystalEffect ce) {
                c = ce.crystal;
            } else if (source is Component) {
                c = (Component)source;
            }
            if (c != null) {
                if (Util.TryGetBodyFromObject(c, out var b)) {
                } else if (c.TryGetComponent(out BodyGetterOverrider bgo)) {
                    b = bgo.body;
                }
                if (b != null && !b.IsBodyLocal()) {
                    return;
                }
            }
        }

        PlayerCamera.main.shaker.Shake(intensity);
    }

    public static void CoroutineOverridenMethod(object source, string name) {
#if DEBUG
        log.l($"CoroutineOverrider called {source} - {name}");
#endif
        if (Net.IsRunning) {
            var FlashBrief = name == "FlashBrief";
            if (source is CrystalEffect cEffect) {
                if (FlashBrief) {
                    var bgo = cEffect.crystal.GetOrAddComponent<BodyGetterOverrider>();
                    var data = cEffect.crystal.GetOrAddComponent<AnyDataContainerComponent>();
                    if (!bgo.GetBody().IsBodyLocal()) {
                        return;
                    }
                }
            }
        }

        PlayerCamera.main.StartCoroutine(name);
    }


    // Mostly AI generated


    private static readonly FieldInfo PlayerCameraMainField =
        AccessTools.Field(typeof(PlayerCamera), nameof(PlayerCamera.main));

    private static readonly MethodInfo StartCoroutineMethod =
        AccessTools.Method(
            typeof(MonoBehaviour),
            nameof(MonoBehaviour.StartCoroutine),
            new[] { typeof(string) }
        );

    private static readonly MethodInfo CoroutineOverrideMethod =
        AccessTools.Method(
            typeof(TranspilerHelpers),
            nameof(TranspilerHelpers.CoroutineOverridenMethod)
        );

    public static IEnumerable<CodeInstruction> PatchCoroutines(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod,
        MethodInfo overrideMethod,
        params HashSet<string> coroutineNames) {
        var codes = new List<CodeInstruction>(instructions);

        if (overrideMethod == null) {
            overrideMethod = CoroutineOverrideMethod;
        }

#if DEBUG
        if (__originalMethod != null) {
            Plugin.log.LogDebug($"[TRANSPILER] Patching PlayerCamera Coroutine for\t{__originalMethod.Name} {__originalMethod.DeclaringType}");
        }
#endif

        for (int i = 0; i < codes.Count - 2; i++) {
            // Match the singleton field load.
            if (codes[i].opcode != OpCodes.Ldsfld ||
                !Equals(codes[i].operand, PlayerCameraMainField))
                continue;

            // Match the coroutine name.
            if (codes[i + 1].opcode != OpCodes.Ldstr)
                continue;

            string coroutineName = codes[i + 1].operand as string;

            if (coroutineName == null)
                continue;

            if (coroutineNames != null &&
                !coroutineNames.Contains(coroutineName))
                continue;

            // Match StartCoroutine(string).
            if (!codes[i + 2].Calls(StartCoroutineMethod))
                continue;

            // Replace singleton with the current instance.
            codes[i] = new CodeInstruction(OpCodes.Ldarg_0);

            // Replace StartCoroutine with the custom override.
            codes[i + 2] = new CodeInstruction(
                OpCodes.Call,
                CoroutineOverrideMethod
            );

            // The original result is discarded by a following pop.
            if (i + 3 < codes.Count &&
                codes[i + 3].opcode == OpCodes.Pop) {
                codes.RemoveAt(i + 3);
            }
        }

        return codes;
    }


    public static IEnumerable<CodeInstruction> PatchShakers(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        MethodBase __originalMethod
    ) {
        var codes = new List<CodeInstruction>(instructions);

        var replacementMethod = AccessTools.Method(
            typeof(TranspilerHelpers),
            nameof(TranspilerHelpers.ShakerOverridenMethod),
            new[] { typeof(object), typeof(float) }
        );

#if DEBUG
        log.l($"[TRANSPILER] Patching PlayerCamera Shaker for\t{__originalMethod.ToSafeString()}");
#endif

        for (int i = 0; i <= codes.Count - 4; i++) {

            if (codes[i + 3].operand is not MethodInfo originalMethod ||
                originalMethod.Name != nameof(Shaker.Shake) ||
                originalMethod.DeclaringType != typeof(Shaker)) {
                continue;
            } else {

#if DEBUG
                log.l($"[TRANSPILER] Patching Shaker: Found shaker on {i}\n{codes[i]}\n{codes[i + 1]}\n{codes[i + 2]}\n{codes[i + 3]}");
#endif

            }

            if (codes[i]    .opcode != OpCodes.Ldsfld ||
                codes[i + 1].opcode != OpCodes.Ldfld ||
                codes[i + 2].opcode != OpCodes.Ldc_R4 ||
                codes[i + 3].opcode != OpCodes.Callvirt) {
                continue;
            }

            if (codes[i].operand is not FieldInfo mainField ||
                mainField.Name != nameof(PlayerCamera.main) ||
                mainField.DeclaringType != typeof(PlayerCamera)) {
                continue;
            }

            if (codes[i + 1].operand is not FieldInfo shakerField ||
                shakerField.Name != nameof(PlayerCamera.shaker)) {
                continue;
            }
#if DEBUG
            log.l($"[TRANSPILER] Patching Shaker: Patching shaker!");
#endif

            var loadThis = new CodeInstruction(OpCodes.Ldarg_0);
            var loadArgument = new CodeInstruction(codes[i + 2]);
            var callReplacement = new CodeInstruction(
                OpCodes.Call,
                replacementMethod
            );

            // Preserve labels.
            for (int j = i; j < i + 4; j++) {
                loadThis.labels.AddRange(codes[j].labels);
                codes[j].labels.Clear();
            }

            // Preserve exception-block metadata from the first instruction.
            loadThis.blocks.AddRange(codes[i].blocks);
            codes[i].blocks.Clear();

            codes.RemoveRange(i, 4);

            codes.InsertRange(i, new[] {
                    loadThis,
                    loadArgument,
                    callReplacement
                });

            i += 2;
        }

        return codes;
    }

}

