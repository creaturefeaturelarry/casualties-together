using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Together;

// grabberplant and grabbershroom are both GrabberPlant. host owns the tip.
// the ik is one fabrik pass: tip goes to targetPos, then each bone keeps its length
// along last frame's direction, then the root pins and the bones push back out.
// joint positions are the result, not the input. poking one of them kinks the rest.
[HarmonyPatch(typeof(GrabberPlant), nameof(GrabberPlant.Update))]
static class GrabberPlant_Update_MultiplayerPatch
{
    static readonly AccessTools.FieldRef<GrabberPlant, Rigidbody2D> GrabBody =
        AccessTools.FieldRefAccess<GrabberPlant, Rigidbody2D>(nameof(GrabberPlant.grabBody));

    sealed class Bone
    {
        internal float[] Base;
        internal bool On;
        internal float Scale;
    }

    static readonly ConditionalWeakTable<GrabberPlant, Bone> Bones = new ConditionalWeakTable<GrabberPlant, Bone>();

    // exact chunk under a living player. NearPlayer is true when nobody is around, so check the list first.
    internal static bool Live(Vector2 pos)
    {
        if (ScavPlayer.AllLivingPlayers.Count == 0)
            return false;
        return ChunkStamp.NearPlayer(pos);
    }

    // stock start multiplies every bone by one random 0.6 to 1.4. remember the prefab lengths first.
    internal static void Remember(GrabberPlant grabber)
    {
        IKHandle ik = grabber.GetComponent<IKHandle>();
        if (ik == null || ik.segments == null || ik.segments.Count == 0)
            return;
        Bone bone = Bones.GetOrCreateValue(grabber);
        if (bone.Base != null)
            return;
        bone.Base = new float[ik.segments.Count];
        for (int i = 0; i < bone.Base.Length; i++)
            bone.Base[i] = ik.segments[i].length;
    }

    // one float, and only when it changes. the solve reads lengths, not the drawn joints.
    internal static void WriteScale(GrabberPlant grabber, SyncInfo si)
    {
        if (!Bones.TryGetValue(grabber, out Bone bone) || bone.Base == null || bone.Base.Length == 0)
            return;
        IKHandle ik = grabber.GetComponent<IKHandle>();
        if (ik == null || ik.segments == null || ik.segments.Count == 0)
            return;
        float basis = bone.Base[0];
        if (basis < 0.0001f)
            return;
        float scale = ik.segments[0].length / basis;
        if (Mathf.Abs(si.specialdata2.Value - scale) < 0.001f)
            return;
        si.specialdata2.Value = scale;
    }

    // put the host tip on the vine. skip a local grab, that one is aiming at the limb.
    internal static void Follow(GrabberPlant grabber, SyncInfo si)
    {
        IKHandle ik = grabber.GetComponent<IKHandle>();
        if (ik != null)
            ApplyScale(grabber, si, ik);
        // a held vine keeps aiming at the limb. the bones can still match the host.
        if (GrabBody(grabber) != null)
            return;
        Vector2_4byte_512 raw = si.grabberplant_tipPos.Value;
        if (raw.x == 0 && raw.y == 0)
            return;
        Vector2 tip = si.grabberplant_tipPos.InterpolatedValue;
        grabber.tipPos = tip;
        if (ik != null)
            ik.targetPos = tip;
    }

    // match the host bone lengths, then leave UpdateIK alone so the line stays a real chain.
    static void ApplyScale(GrabberPlant grabber, SyncInfo si, IKHandle ik)
    {
        float scale = si.specialdata2.Value;
        if (scale < 0.5f || scale > 1.5f)
            return;
        if (!Bones.TryGetValue(grabber, out Bone bone) || bone.Base == null)
            return;
        if (bone.On && Mathf.Abs(bone.Scale - scale) < 0.001f)
            return;
        int count = ik.segments.Count;
        if (count > bone.Base.Length)
            count = bone.Base.Length;
        for (int i = 0; i < count; i++)
            ik.segments[i].length = bone.Base[i] * scale;
        ik.RecalculateLength();
        bone.On = true;
        bone.Scale = scale;
    }

    // stock update swings a local tip. clients put the host one back so the next solve aims there.
    static void Postfix(GrabberPlant __instance)
    {
        if (!Net.IsRunning || Net.IsServer)
            return;
        if (!Live(__instance.transform.position))
            return;
        if (!__instance.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
            return;
        Follow(__instance, tracker.syncInfo);
    }
}

// grab the prefab lengths before start multiplies them.
[HarmonyPatch(typeof(GrabberPlant), nameof(GrabberPlant.Start))]
static class GrabberPlant_Start_MultiplayerPatch
{
    static void Prefix(GrabberPlant __instance)
    {
        GrabberPlant_Update_MultiplayerPatch.Remember(__instance);
    }
}
