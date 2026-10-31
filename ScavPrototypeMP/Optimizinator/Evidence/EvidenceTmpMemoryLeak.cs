using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace KrokV5Optimization;

// the dead TextMesh buffer is the previous character array, dropped when a longer string is laid out.
// log the object and the first line once that array is large enough to be the 4 MiB one.
internal static class EvidenceTmpMemoryLeak
{
	internal static int Lines;

	// a TMP_CharacterInfo is a few hundred bytes, so 160 entries is about 64KB.
	const int MinEntries = 160;
	const int MaxLines = 40;

	static readonly Dictionary<int, int> Seen = new Dictionary<int, int>();

	internal static string Evidence() => "tmpGrow=" + Lines;

	internal static void Arm(Harmony harmony)
	{
		Bind(harmony, typeof(TextMeshPro));
		Bind(harmony, typeof(TextMeshProUGUI));
	}

	static void Bind(Harmony harmony, System.Type type)
	{
		var method = AccessTools.Method(type, "SetArraySizes");
		if (method == null)
		{
			FindTheBug.Write("tmp grow log missed SetArraySizes on " + type.Name);
			return;
		}
		harmony.Patch(method,
			prefix: new HarmonyMethod(typeof(EvidenceTmpMemoryLeak), nameof(Prefix)),
			postfix: new HarmonyMethod(typeof(EvidenceTmpMemoryLeak), nameof(Postfix)));
	}

	static void Prefix(TMP_Text __instance, ref int __state)
	{
		TMP_CharacterInfo[] info = __instance.textInfo != null ? __instance.textInfo.characterInfo : null;
		__state = info != null ? info.Length : 0;
	}

	static void Postfix(TMP_Text __instance, int __state)
	{
		if (!FindTheBug.On || Lines >= MaxLines || __instance == null)
			return;
		TMP_CharacterInfo[] info = __instance.textInfo != null ? __instance.textInfo.characterInfo : null;
		int now = info != null ? info.Length : 0;
		if (now < MinEntries || now <= __state)
			return;
		int id = __instance.GetInstanceID();
		if (Seen.TryGetValue(id, out int logged) && now <= logged)
			return;
		Seen[id] = now;
		Lines++;
		string raw = __instance.text ?? "";
		int end = raw.IndexOf('\n');
		if (end < 0 || end > 80)
			end = raw.Length < 80 ? raw.Length : 80;
		string line = raw.Substring(0, end);
		FindTheBug.Write("tmp grow path=" + PathOf(__instance)
			+ " old=" + __state
			+ " new=" + now
			+ " chars=" + raw.Length
			+ " \"" + line + "\"");
	}

	static string PathOf(Component component)
	{
		Transform transform = component.transform;
		string path = transform.name;
		for (int depth = 0; transform.parent != null && depth < 6; depth++)
		{
			transform = transform.parent;
			path = transform.name + "/" + path;
		}
		return path;
	}
}
