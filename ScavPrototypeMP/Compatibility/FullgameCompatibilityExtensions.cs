using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Together;




public static class BuildingEntityGetters {
    private static readonly FieldInfo _fieldHealth = Util.GetField<BuildingEntity>("_health", "health");
    private static readonly MethodInfo _methodCheckDamage = Util.GetMethod<BuildingEntity>("CheckDamage");

    static BuildingEntityGetters() {
        if (_fieldHealth == null)
            throw new Exception($"{typeof(BuildingEntity)} health wasn't found");
        if (_methodCheckDamage == null && Util.GameIsFull())
            log.error($"{typeof(BuildingEntity)} CheckDamage wasn't found");
    }

    public static float GetHealth(this BuildingEntity b) {
        return (float)_fieldHealth.GetValue(b);
    }

    public static void SetHealth(this BuildingEntity b, float value, bool checkDamage = true) {
        _fieldHealth.SetValue(b, value);
        if (checkDamage && _methodCheckDamage != null) {
            _methodCheckDamage.Invoke(b, new object[0]);
        }
    }


}








