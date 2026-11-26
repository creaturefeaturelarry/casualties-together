using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// the physics cap keeps a late frame at two steps. those two steps would walk
// a shorter distance than the frame lasted. the local body uses a stronger
// step so the distance matches the real frame. everyone else stays on the cap.
static class LocalWalk
{
	internal static float Scale = 1f;
	internal static int Depth;

	internal static void Note(float real, float cap)
	{
		Depth = 0;
		Scale = 1f;
		if (real > 0.5f || cap < 0.001f || real <= cap + 0.0001f)
			return;
		float scale = real / cap;
		if (scale > 4f)
			scale = 4f;
		Scale = scale;
	}

	[HarmonyPatch(typeof(Body), "FixedUpdate")]
	static class Step
	{
		static void Prefix(Body __instance, ref bool __state)
		{
			__state = false;
			if (Scale <= 1.001f || __instance == null || !__instance.standing || PlayerCamera.main == null || !__instance.IsBodyLocal())
				return;
			__state = true;
			Depth++;
		}

		static void Postfix(ref bool __state)
		{
			if (__state && Depth > 0)
				Depth--;
		}
	}

	[HarmonyPatch(typeof(Body), "actualMaxSpeed", MethodType.Getter)]
	static class MaxSpeed
	{
		static void Postfix(ref float __result)
		{
			if (Depth > 0)
				__result *= Scale;
		}
	}

	[HarmonyPatch(typeof(Body), "actualMoveForce", MethodType.Getter)]
	static class MoveForce
	{
		static void Postfix(ref float __result)
		{
			if (Depth > 0)
				__result *= Scale;
		}
	}
}
