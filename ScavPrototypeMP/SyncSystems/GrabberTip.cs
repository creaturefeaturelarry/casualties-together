using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LiteNetLib;
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
	static readonly AccessTools.FieldRef<GrabberPlant, float> GrabbedTime =
		AccessTools.FieldRefAccess<GrabberPlant, float>("grabbedTime");
	static readonly AccessTools.FieldRef<GrabberPlant, List<CircleCollider2D>> Cols =
		AccessTools.FieldRefAccess<GrabberPlant, List<CircleCollider2D>>("cols");

	const float GrabReach = 3.2f;
	const string Yank = "grabber_yank";

	static bool _savedFeel;
	static float _shock;
	static float _panic;

    sealed class Bone
    {
        internal float[] Base;
        internal bool On;
        internal float Scale;
    }

    static readonly ConditionalWeakTable<GrabberPlant, Bone> Bones = new ConditionalWeakTable<GrabberPlant, Bone>();

    // the 3x3 a player already keeps awake. the exact chunk missed a vine you were standing next to.
    internal static bool Live(Vector2 pos)
    {
        if (ScavPlayer.AllLivingPlayers.Count == 0)
            return false;
        return SharedMain.WorldInSimRange(pos);
    }

	// tipPos is the lead point FixedUpdate drags a body toward. the line reaches the limb locally.
	internal static Vector2 Aim(GrabberPlant grabber)
	{
		return grabber.tipPos;
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

	// put the host tip on the vine. a local grab still follows that lead, and aims the line at the limb.
	internal static void Follow(GrabberPlant grabber, SyncInfo si)
	{
		IKHandle ik = grabber.GetComponent<IKHandle>();
		if (ik != null)
			ApplyScale(grabber, si, ik);
		Vector2_4byte_512 raw = si.grabberplant_tipPos.Value;
		if (!(raw.x == 0 && raw.y == 0))
			grabber.tipPos = si.grabberplant_tipPos.InterpolatedValue;
		if (ik == null)
			return;
		Rigidbody2D held = GrabBody(grabber);
		if (held != null)
			ik.targetPos = grabber.transform.InverseTransformPoint(held.position);
		else
			ik.targetPos = grabber.tipPos;
		ik.UpdateIK();
		Draw(ik);
		PlaceCols(grabber, ik);
	}

	// stock update parks a circle on every fourth bone. clients skip that update, so the circles sat on the root.
	static void PlaceCols(GrabberPlant grabber, IKHandle ik)
	{
		List<CircleCollider2D> cols = Cols(grabber);
		if (cols == null || ik.segments == null)
			return;
		int bones = ik.segments.Count;
		int n = cols.Count;
		for (int i = 0; i < n; i++)
		{
			int seg = i * 4;
			if (seg >= bones || cols[i] == null)
				continue;
			cols[i].offset = ik.segments[seg].pos;
		}
	}

    // IKHandle already drew this frame from the local sway. put the host tip on the line.
    static void Draw(IKHandle ik)
    {
        LineRenderer line = ik.lineRenderer;
        if (line == null || ik.segments == null)
            return;
        line.SetPosition(0, ik.rootPos);
        int count = ik.segments.Count;
        for (int i = 0; i < count; i++)
        {
            Vector2 p = ik.segments[i].pos;
            line.SetPosition(i + 1, new Vector3(p.x, p.y, -i * 0.02f));
        }
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

	// stock update swings a local tip and only notices PlayerCamera.main. clients follow the host aim and grab themselves.
	static bool Prefix(GrabberPlant __instance)
	{
		_savedFeel = false;
		if (Net.IsServer && PlayerCamera.main != null && PlayerCamera.main.body != null)
		{
			_shock = PlayerCamera.main.body.shock;
			_panic = PlayerCamera.main.body.eyePanicTime;
			_savedFeel = true;
		}
		if (!Net.IsRunning || Net.IsServer)
			return true;
		if (!Live(__instance.transform.position))
			return true;
		if (!__instance.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
			return true;
		return false;
	}

	static void Postfix(GrabberPlant __instance)
	{
		if (!Net.IsRunning)
			return;
		if (Net.IsServer)
		{
			EaseHost(__instance);
			return;
		}
		if (!Live(__instance.transform.position))
			return;
		if (!__instance.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
			return;
		Follow(__instance, tracker.syncInfo);
		HoldLocal(__instance, tracker.syncInfo);
	}

	// stock shocks and panics the camera body for whoever is held. a remote grab should not do that to the host.
	static void EaseHost(GrabberPlant grabber)
	{
		Body host = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		if (!_savedFeel || host == null || !HeldOther(grabber, host))
			return;
		host.shock = _shock;
		host.eyePanicTime = _panic;
		Limb limb = GrabBody(grabber).GetComponent<Limb>();
		if (limb == null || limb.body == null)
			return;
		limb.body.shock = Mathf.Max(limb.body.shock, 20f);
		limb.body.eyePanicTime = 0.5f;
	}

	static bool HeldOther(GrabberPlant grabber, Body host)
	{
		Rigidbody2D held = GrabBody(grabber);
		if (held == null)
			return false;
		Limb limb = held.GetComponent<Limb>();
		return limb != null && limb.body != null && limb.body != host;
	}

	// the vine only checks the camera body, so a client never got caught. they ragdoll and FixedUpdate drags that limb to the lead.
	static void HoldLocal(GrabberPlant grabber, SyncInfo si)
	{
		Body me = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		if (me == null)
			return;
		if (GrabBody(grabber) != null)
		{
			me.shock = Mathf.Max(me.shock, 20f);
			me.eyePanicTime = 0.5f;
			if (Time.time - GrabbedTime(grabber) > 2f || !me.conscious)
				GrabBody(grabber) = null;
			return;
		}
		if (!me.alive || !me.conscious)
			return;
		if (Time.time - GrabbedTime(grabber) <= 5f)
			return;
		Vector2_4byte_512 raw = si.grabberplant_tipPos.Value;
		if (raw.x == 0 && raw.y == 0)
			return;
		Vector2 worldTip = grabber.transform.TransformPoint(grabber.tipPos);
		if (Vector2.Distance(worldTip, me.transform.position) >= GrabReach)
			return;
		Rigidbody2D limb = PickLimb(me);
		if (limb == null)
			return;
		GrabbedTime(grabber) = Time.time;
		GrabBody(grabber) = limb;
		me.Ragdoll();
		me.Scream();
		if (Time.timeScale > 1f)
			PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal, false);
		Ask(si);
	}

	static void Ask(SyncInfo si)
	{
		if (si == null || si.syncId.id == 0)
			return;
		LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
		writer.Put(Yank + " " + si.syncId.id.ToString());
		Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
	}

	static Rigidbody2D PickLimb(Body body)
	{
		if (body == null || body.limbs == null || body.limbs.Length == 0)
			return null;
		int start = Random.Range(0, body.limbs.Length);
		for (int n = 0; n < body.limbs.Length; n++)
		{
			Limb limb = body.limbs[(start + n) % body.limbs.Length];
			if (limb != null && limb.rb != null && !limb.dismembered)
				return limb.rb;
		}
		return null;
	}

	[CTPostInit]
	static void Init()
	{
		ServerMain.ServerCustomCommandsDict[Yank] = (plr, cmd, args) =>
		{
			if (!Net.IsServer || plr == null || plr.body == null || args == null || args.Length < 2)
				return;
			if (!ushort.TryParse(args[1], out ushort syncId))
				return;
			if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null)
				return;
			if (!si.go.TryGetComponent(out GrabberPlant grabber))
				return;
			if (!plr.body.alive || !plr.body.conscious)
				return;
			if (GrabBody(grabber) != null)
				return;
			if (Time.time - GrabbedTime(grabber) <= 5f)
				return;
			Vector2 worldTip = grabber.transform.TransformPoint(grabber.tipPos);
			if (Vector2.Distance(worldTip, plr.body.transform.position) > GrabReach + 2f)
				return;
			Rigidbody2D limb = PickLimb(plr.body);
			if (limb == null)
				return;
			GrabbedTime(grabber) = Time.time;
			GrabBody(grabber) = limb;
			plr.body.Ragdoll();
			plr.body.Scream();
		};
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
