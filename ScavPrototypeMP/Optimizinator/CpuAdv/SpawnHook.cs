using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together;

internal static class SpawnHook
{
	private static bool _logged;

	internal static void Apply(Harmony harmony)
	{
		MethodInfo postfix = typeof(SpawnHook).GetMethod(nameof(AfterInstantiate), BindingFlags.Static | BindingFlags.NonPublic);
		var patched = new HarmonyMethod(postfix);
		foreach (MethodInfo method in typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (method.Name != "Instantiate" || method.IsGenericMethodDefinition)
				continue;
			if (method.ReturnType != typeof(UnityEngine.Object) && !typeof(Component).IsAssignableFrom(method.ReturnType) && method.ReturnType != typeof(GameObject))
				continue;
			harmony.Patch(method, postfix: patched);
		}
	}

	private static void AfterInstantiate(object __result)
	{
		try
		{
			if (!CullWorld.WatchingSpawns())
				return;
			if (!TrySceneObject(__result, out GameObject go))
				return;
			CullWorld.Consider(go);
		}
		catch (Exception ex)
		{
			if (_logged)
				return;
			_logged = true;
			Plugin.Logger?.LogWarning("spawn hook kept a generation error to itself: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static bool TrySceneObject(object result, out GameObject go)
	{
		go = null;
		UnityEngine.Object unity = result as UnityEngine.Object;
		if (unity == null)
			return false;
		go = unity as GameObject;
		if (go == null)
		{
			Component component = unity as Component;
			if (component == null)
				return false;
			go = component.gameObject;
		}
		return go != null && go.scene.IsValid();
	}
}
