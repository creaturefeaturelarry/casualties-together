using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

// one walk of Together per fix. FixGuiContent, FixGuiStyle, and FixRectOffset pass their own constructors.
internal static class ImguiScan
{
	internal static int Patched;

	internal static void Arm(Harmony harmony, ConstructorInfo[] wanted, MethodInfo transpile)
	{
		var set = new HashSet<ConstructorInfo>(wanted);
		BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		foreach (Type type in typeof(UIBullshit).Assembly.GetTypes())
		{
			if (type.Namespace != "Together" && type.Namespace != "CasualtiesTogetherUtils")
				continue;
			MethodInfo[] methods;
			try
			{
				methods = type.GetMethods(flags);
			}
			catch
			{
				continue;
			}
			foreach (MethodInfo method in methods)
			{
				if (method.IsAbstract || method.ContainsGenericParameters || method.Name == "_GUI_DrawMPMainMenu")
					continue;
				if (!Mentions(method, set))
					continue;
				try
				{
					harmony.Patch(method, transpiler: new HarmonyMethod(transpile));
					Patched++;
				}
				catch (Exception ex)
				{
					OptimizerMain.Log.LogWarning("[KrokV5Opt] imgui rewrite skipped " + type.Name + "." + method.Name + ": " + ex.Message);
				}
			}
		}
	}

	private static bool Mentions(MethodInfo method, HashSet<ConstructorInfo> wanted)
	{
		MethodBody body = method.GetMethodBody();
		if (body == null)
			return false;
		byte[] il = body.GetILAsByteArray();
		if (il == null)
			return false;
		for (int i = 0; i < il.Length - 4; i++)
		{
			if (il[i] != 0x73)
				continue;
			int token = BitConverter.ToInt32(il, i + 1);
			try
			{
				MethodBase resolved = method.Module.ResolveMethod(token);
				if (resolved is ConstructorInfo ctor && wanted.Contains(ctor))
					return true;
			}
			catch
			{
			}
		}
		return false;
	}
}
