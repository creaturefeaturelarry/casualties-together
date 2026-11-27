using UnityEngine;

namespace Together;

// a client's gunshot on an enemy is reported by net id, and the host applies it once.
// the client gate (TurretScript_Shoot_MultiplayerPatch.Shoot_EnemyHitIsForwarded) and the host check
// (GunScript_Fire_MultiplayerPatch.ServerReceiver__PlayerBodyShoot) both ask this, so they can't disagree.
// Rigidbody type is not part of it: the host's BuildingOptimization makes far enemies Static, and the
// client copy mirrors that, so "is it Dynamic" missed lab enemies.
internal static class EnemyHitForward {
    // TurretScript.Shoot casts 200. pose lag and the barrel slack go on top.
    internal const float ShotRange = 200f;
    internal const float ReachSlack = 24f;

    internal static bool IsForwardableEnemy(BuildingEntity b) {
        return IsForwardableEnemy(b, out _);
    }

    internal static bool IsForwardableEnemy(BuildingEntity b, out SyncInfo si) {
        si = null;
        if (b == null || b.gameObject == null) {
            return false;
        }
        // registered first: it's a dictionary lookup, and most building hits are unregistered walls.
        if (!NetObjectRegistry.TryGetSyncInfo(b.gameObject, out si) || si is null || si.IsDestroyed) {
            si = null;
            return false;
        }
        if (!IsEnemyKind(b)) {
            si = null;
            return false;
        }
        return true;
    }

    // props, doors, crates and traps are not in here. they keep the old path.
    internal static bool IsEnemyKind(BuildingEntity b) {
#if FULLGAME
        // the scientist keeps its own shot rules (ScientistGate, EndLabScript.BeginEscapeSequence).
        if (b == ScientistGate.HealthBuilding || b.CompareTag("Scientist")) {
            return false;
        }
#endif
        if (b.animal) {
            return true;
        }
        if (b.TryGetComponent(out SpiderHandler _)
            || b.TryGetComponent(out CrystalEnemy _)
            || b.TryGetComponent(out TraderScript _)
            || b.TryGetComponent(out TurretScript _)) {
            return true;
        }
#if FULLGAME
        if (b.TryGetComponent(out GuardScript _)
            || b.TryGetComponent(out CeilingTurret _)
            || b.TryGetComponent(out LaserDroneScript _)
            || b.TryGetComponent(out BloodSuckerScript _)) {
            return true;
        }
        // a den's script can sit on a child. it only counts when this is the building the host registered for it
        // (LayerBuildingStream.BuildingOf), not a room that happens to contain one.
        BloodSuckerScript den = b.GetComponentInChildren<BloodSuckerScript>(true);
        if (den != null && den.GetComponentInParent<BuildingEntity>() == b) {
            return true;
        }
#endif
        return false;
    }

    // the barrel is already checked against the gun. this is only "could a 200 ray reach it".
    internal static bool InShotReach(Vector2 from, SyncInfo si) {
        if (si is null || si.go == null) {
            return false;
        }
        return KM.DistanceCheck(from, (Vector2)si.go.transform.position, ShotRange + ReachSlack);
    }
}
