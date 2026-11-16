using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Together;

// The host applies the block damage the client reports, not its own recomputation, which can differ and left the
// host block healthier, so a resync restored it after the client broke it. The figure is bounded by a multiple of the host's own.
internal static class BlockDamageAuthority {
    const float MaxClientToHostRatio = 3f;
    const float MaxBlockDamage = 2000f;
    const float LogDifferenceFraction = 0.01f;

    // set while the client applies damage the host relayed, which is not damage this player dealt
    public static bool ApplyingRelayedDamage;
    static float localBlockDamageThisSwing;

    public static void RecordLocalDamage(float damage) {
        if (Net.IsClient && !ApplyingRelayedDamage)
            localBlockDamageThisSwing += damage;
    }

    // damage the world applied since the last swing (an explosion, say) must not ride along with this swing
    public static void BeginLocalSwing() {
        localBlockDamageThisSwing = 0f;
    }

    public static float TakeLocalBlockDamage() {
        float taken = localBlockDamageThisSwing;
        localBlockDamageThisSwing = 0f;
        return taken;
    }

    public static float ChooseBlockDamage(float hostOwnDamage, float clientReportedDamage, knetid clientId) {
        bool usable = !float.IsNaN(clientReportedDamage) && !float.IsInfinity(clientReportedDamage)
            && clientReportedDamage > 0f && clientReportedDamage <= MaxBlockDamage
            && clientReportedDamage <= hostOwnDamage * MaxClientToHostRatio;
        float chosen = usable ? clientReportedDamage : hostOwnDamage;
        if (!usable || Mathf.Abs(clientReportedDamage - hostOwnDamage) > hostOwnDamage * LogDifferenceFraction) {
            log.l($"BLOCKDMG client={clientId} hostOwn={Format(hostOwnDamage)} clientReported={Format(clientReportedDamage)}"
                + $" used={Format(chosen)} {(usable ? "client figure used" : "client figure rejected")}", false);
        }
        return chosen;
    }

    static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock), new Type[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
public static class WorldGenerationDamageBlockAuthorityPatch {
    public static void Postfix(float dmg) {
        if (Net.IsRunning)
            BlockDamageAuthority.RecordLocalDamage(dmg);
    }
}
