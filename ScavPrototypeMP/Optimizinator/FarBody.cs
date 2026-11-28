using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Together;

// a remote player outside the view circle keeps a root position for the shift arrow.
// the body, its limbs, and its rigidbodies do not tick.
static class FarBody
{
	const float Band = 8f;
	const string Slow = "SlowerUpdate";

	sealed class State
	{
		public List<Behaviour> Off;
		public List<Rigidbody2D> Asleep;
	}

	static readonly Dictionary<NetBody, State> Held = new Dictionary<NetBody, State>();

	internal static void Install(Harmony harmony)
	{
		MethodInfo update = AccessTools.DeclaredMethod(typeof(NetBody), "Update");
		MethodInfo step = AccessTools.DeclaredMethod(typeof(NetBody), "FixedUpdate");
		if (update != null)
			harmony.Patch(update, prefix: new HarmonyMethod(typeof(FarBody), nameof(SkipNet)));
		if (step != null)
			harmony.Patch(step, prefix: new HarmonyMethod(typeof(FarBody), nameof(SkipNet)));
	}

	static bool SkipNet(NetBody __instance)
	{
		return !Held.ContainsKey(__instance);
	}

	// true: caller leaves the body alone. the root was just moved.
	internal static bool Hold(NetBody nb, Vector2 at)
	{
		if (nb == null || nb.body == null)
			return false;
		if (!Away(nb, at))
		{
			if (Held.ContainsKey(nb))
				Release(nb);
			return false;
		}
		Park(nb);
		nb.body.transform.position = at;
		return true;
	}

	static bool Away(NetBody nb, Vector2 at)
	{
		if (!Net.IsRunning || Net.IsServer || !Net.IsClient)
			return false;
		if (!nb.IsPlayer || nb.IsBodyLocal())
			return false;
		if (nb.carrying_person != null || nb.piggybacking_on != null)
			return false;
		if (UIInGame.SpectatorModeActive && UIInGame.SpectatorCurrentTarget == nb.transform)
			return false;
		bool parked = Held.ContainsKey(nb);
		return !SyncStep.NearAny(at, parked ? -Band : Band);
	}

	static void Park(NetBody nb)
	{
		if (!Held.TryGetValue(nb, out State state))
		{
			state = new State { Off = new List<Behaviour>(), Asleep = new List<Rigidbody2D>() };
			Behaviour[] behaviours = nb.GetComponentsInChildren<Behaviour>(true);
			for (int i = 0; i < behaviours.Length; i++)
			{
				Behaviour behaviour = behaviours[i];
				if (behaviour == null || behaviour is NetBody || !behaviour.enabled)
					continue;
				behaviour.enabled = false;
				state.Off.Add(behaviour);
			}
			Rigidbody2D[] bodies = nb.GetComponentsInChildren<Rigidbody2D>(true);
			for (int i = 0; i < bodies.Length; i++)
			{
				Rigidbody2D rb = bodies[i];
				if (rb == null || !rb.simulated)
					continue;
				rb.velocity = Vector2.zero;
				rb.angularVelocity = 0f;
				rb.simulated = false;
				state.Asleep.Add(rb);
			}
			nb.CancelInvoke(Slow);
			Held[nb] = state;
			return;
		}
		for (int i = 0; i < state.Off.Count; i++)
		{
			Behaviour behaviour = state.Off[i];
			if (behaviour != null)
				behaviour.enabled = false;
		}
		for (int i = 0; i < state.Asleep.Count; i++)
		{
			Rigidbody2D rb = state.Asleep[i];
			if (rb != null)
				rb.simulated = false;
		}
	}

	static void Release(NetBody nb)
	{
		if (!Held.TryGetValue(nb, out State state))
			return;
		Held.Remove(nb);
		for (int i = 0; i < state.Asleep.Count; i++)
		{
			Rigidbody2D rb = state.Asleep[i];
			if (rb != null)
				rb.simulated = true;
		}
		for (int i = 0; i < state.Off.Count; i++)
		{
			Behaviour behaviour = state.Off[i];
			if (behaviour != null)
				behaviour.enabled = true;
		}
		nb.InvokeRepeating(Slow, 0.1f, 0.1f);
	}
}
