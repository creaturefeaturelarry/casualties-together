using System;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KrokV5Optimization;

// ccl prefab cache. quiet unless findthebug is on, and the hooks are not installed then either.
internal static class EvidenceCache
{
	static bool _inside;
	static bool _done;
	static int _depth;
	static float _genStart = -1f;
	static string _lastId = "";
	static int _mainThread;
	static int _lines;

	internal static string Evidence() => "cacheTrace=" + _lines;

	internal static void Arm(Harmony harmony)
	{
		_mainThread = Thread.CurrentThread.ManagedThreadId;
		MethodInfo corpse = AccessTools.Method(typeof(CorpseScript), "Start");
		if (corpse != null)
		{
			HarmonyMethod first = new HarmonyMethod(typeof(EvidenceCache), nameof(BeforeCorpse));
			first.priority = Priority.First;
			harmony.Patch(corpse, prefix: first);
		}

		Type resolveOwner = AccessTools.TypeByName("CUCoreLib.Helpers.CustomInstantiate");
		MethodInfo resolve = resolveOwner == null ? null : AccessTools.Method(resolveOwner, "ResolvePrefab");
		if (resolve != null)
			harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(EvidenceCache), nameof(BeforeResolve)));

		Type cache = AccessTools.TypeByName("CUCoreLib.Helpers.ResourceCache");
		MethodInfo tryInit = cache == null ? null : AccessTools.Method(cache, "TryInitialize");
		if (tryInit != null)
		{
			harmony.Patch(
				tryInit,
				prefix: new HarmonyMethod(typeof(EvidenceCache), nameof(BeforeTryInit)),
				postfix: new HarmonyMethod(typeof(EvidenceCache), nameof(AfterTryInit)));
		}

		Type console = AccessTools.TypeByName("CUCoreLib.Patches.ConsolePatch");
		MethodInfo spawnIds = console == null ? null : AccessTools.Method(console, "GetVanillaSpawnIds");
		if (spawnIds != null)
		{
			harmony.Patch(
				spawnIds,
				prefix: new HarmonyMethod(typeof(EvidenceCache), nameof(BeforeSpawnIds)),
				postfix: new HarmonyMethod(typeof(EvidenceCache), nameof(AfterSpawnIds)));
		}

		MethodInfo generate = AccessTools.Method(typeof(WorldGeneration), "GenerateWorld");
		if (generate != null)
		{
			harmony.Patch(
				generate,
				prefix: new HarmonyMethod(typeof(EvidenceCache), nameof(WorldGenEnter)),
				postfix: new HarmonyMethod(typeof(EvidenceCache), nameof(WorldGenExit)));
		}

		MethodInfo place = AccessTools.Method(typeof(WorldGeneration), "WorldPlacePlayer");
		if (place != null)
		{
			harmony.Patch(
				place,
				prefix: new HarmonyMethod(typeof(EvidenceCache), nameof(PlaceEnter)),
				postfix: new HarmonyMethod(typeof(EvidenceCache), nameof(PlaceExit)));
		}

		// do not patch Resources.Load. one line per sprite/clip is what made mpverbose logs enormous.
		FindTheBug.Write("cache trace on");
	}

	static void TryPatch(Harmony harmony, MethodInfo target, string prefix, string postfix)
	{
		if (target == null || target.IsGenericMethodDefinition)
			return;
		try
		{
			harmony.Patch(
				target,
				prefix: prefix == null ? null : new HarmonyMethod(typeof(EvidenceCache), prefix),
				postfix: postfix == null ? null : new HarmonyMethod(typeof(EvidenceCache), postfix));
		}
		catch (Exception ex)
		{
			FindTheBug.Write("cache patch failed " + target.Name + ": " + ex.GetType().Name + " " + ex.Message);
		}
	}

	static void BeforeCorpse(CorpseScript __instance)
	{
		if (_done || __instance == null)
			return;
		string cats = "null";
		if (__instance.categories != null)
		{
			StringBuilder built = new StringBuilder();
			for (int i = 0; i < __instance.categories.Length; i++)
			{
				if (i > 0)
					built.Append(",");
				built.Append(__instance.categories[i]);
			}
			cats = __instance.categories.Length + ":" + built;
		}
		Vector3 pos = __instance.transform.position;
		Say("corpse id=" + __instance.GetInstanceID()
			+ " name=" + __instance.name
			+ " animal=" + (__instance.animalCorpse ? "1" : "0")
			+ " cats=" + cats
			+ " pos=" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0"));
	}

	static void BeforeResolve(string id)
	{
		if (_done)
			return;
		_lastId = id ?? "";
		Say("resolve id=" + _lastId);
	}

	static void BeforeTryInit()
	{
		if (_inside || _done)
			return;
		_inside = true;
		Say("tryinit id=" + _lastId + " stack=" + ShortStack());
		Say("full before");
	}

	static void AfterTryInit()
	{
		if (!_inside)
			return;
		Say("full after prefabs=" + PrefabCount());
		_inside = false;
		_done = true;
	}

	static void BeforeSpawnIds()
	{
		Say("spawnids before");
	}

	static void AfterSpawnIds()
	{
		Say("spawnids after");
	}

	static void WorldGenEnter()
	{
		_genStart = Time.realtimeSinceStartup;
		Say("worldgen enter");
	}

	static void WorldGenExit()
	{
		Say("worldgen exit ms=" + SinceGen());
	}

	static void PlaceEnter()
	{
		Say("place enter");
	}

	static void PlaceExit()
	{
		Say("place exit");
	}

	static int PrefabCount()
	{
		Type cache = AccessTools.TypeByName("CUCoreLib.Helpers.ResourceCache");
		FieldInfo field = cache == null ? null : AccessTools.Field(cache, "AllPrefabs");
		if (field == null)
			return -1;
		System.Collections.IDictionary dict = field.GetValue(null) as System.Collections.IDictionary;
		if (dict == null)
			return -1;
		return dict.Count;
	}

	static string ShortStack()
	{
		string[] rows = Environment.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		StringBuilder built = new StringBuilder();
		int kept = 0;
		for (int i = 0; i < rows.Length && kept < 6; i++)
		{
			string row = rows[i].Trim();
			if (row.IndexOf("EvidenceCache", StringComparison.Ordinal) >= 0)
				continue;
			if (row.IndexOf("System.Environment", StringComparison.Ordinal) >= 0)
				continue;
			if (built.Length > 0)
				built.Append(" | ");
			built.Append(row);
			kept++;
		}
		return built.ToString();
	}

	static string SinceGen()
	{
		if (_genStart < 0f)
			return "na";
		return ((Time.realtimeSinceStartup - _genStart) * 1000f).ToString("0");
	}

	static void Say(string detail)
	{
		string gen = "?";
		string scene = "";
		try
		{
			scene = SceneManager.GetActiveScene().name;
			WorldGeneration world = WorldGeneration.world;
			if (world == null)
				gen = "noworld";
			else
				gen = world.generatingWorld ? "1" : "0";
		}
		catch (Exception)
		{
			gen = "err";
		}
		int thread = Thread.CurrentThread.ManagedThreadId;
		FindTheBug.Write("cache " + detail
			+ " f=" + Time.frameCount
			+ " mem=" + (GC.GetTotalMemory(false) / (1024 * 1024))
			+ " thr=" + thread
			+ " main=" + (thread == _mainThread ? "1" : "0")
			+ " depth=" + _depth
			+ " since=" + SinceGen()
			+ " gen=" + gen
			+ " scene=" + scene);
		_lines++;
	}
}
