using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;
using Object = UnityEngine.Object;

namespace Together;


public class Spectator : CasualtiesTogetherSingletonWorldOnly {

    static Transform SpectatorTarget;
    public static void SetCameraFollow(Transform t) {
        SpectatorTarget = t;
        // PlayerCamera.following was removed
        if (PlayerCamera.main != null) {
            var f = Traverse.Create(PlayerCamera.main).Field("following");
            if (f.FieldExists()) {
                f.SetValue(SpectatorTarget);
            }
        }
    }
    public static Transform GetCameraFollow() {
        return SpectatorTarget;
    }

    void Update() {
        if (PlayerCamera.main != null) {
            if (SpectatorTarget != null) {
                var f = Traverse.Create(PlayerCamera.main).Field("customLookPos");
                if (f.FieldExists()) {
                    Vector2? newValue = SpectatorTarget.transform.position;
                    f.SetValue(newValue);
                }
            }
        }
    }

}


