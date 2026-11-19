using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using Together;
using Unity.VisualScripting.FullSerializer;
using UnityEngine;

namespace KrokV5Optimization;

// optimizer and sync swap. installed by Together.Plugin, not a second behaviour.
public static class OptimizerMain {
    public const string GUID = "com.local.krokv5.optimization";
    public const string Name = "KrokV5Optimization";
    public const string Version = "1.0.31";

    internal static ManualLogSource Log;
	internal static ConfigEntry<bool> CompressPooling;
	internal static ConfigEntry<bool> CacheImguiStyles;
	internal static ConfigEntry<bool> BugLogEnabled;
	internal static ConfigEntry<bool> InvTrace;
	internal static ConfigEntry<float> ReportSeconds;

	private static Harmony _harmony;
	private static float _reportAt;
	private static bool _installed;

	internal static void Install()
	{
		if (_installed)
			return;
		_installed = true;
		Log = Plugin.Logger;
		var Config = Plugin.main.Config;

        CompressPooling = Plugin.main.Config.Bind("Memory", "CompressPooling", true, "Reuse one gzip/deflate codec instead of a new stream per packet.");
		CacheImguiStyles = Plugin.main.Config.Bind("Memory", "CacheImguiStyles", true, "Skip idle skin rebuilds and reuse GUIContent, GUIStyle, and RectOffset.");
		BugLogEnabled = Plugin.main.Config.Bind("Probe", "BugLog", true, "Log shower, pickup, craft, give, carry, revive, and sim-range lines.");
		InvTrace = Plugin.main.Config.Bind("Probe", "InvTrace", false, "Per-item inventory trace. Off by default.");
		ReportSeconds = Plugin.main.Config.Bind("Probe", "ReportSeconds", 160f, "How often to print the memory counters. 0 hides the line.");

		_harmony = new Harmony(OptimizerMain.GUID);
		if (CompressPooling.Value)
		{
			Try(typeof(FixUtilCompress));
			Try(typeof(FixUtilDecompress));
			Try(typeof(FixUtilCompressDeflate));
			Try(typeof(FixUtilDecompressDeflate));
			Try(typeof(FixChunkBlock));
			Try(typeof(FixChunkFluid));
		}
		if (CacheImguiStyles.Value)
		{
			Try(typeof(FixSkinSkip));
			Arm(FixGuiContent.Arm);
			Arm(FixGuiStyle.Arm);
			// sharing one RectOffset across style slots crashed the menu, so this stays off.
			Log.LogDebug("[KrokV5Opt] imgui rewrite methods=" + ImguiScan.Patched);
		}

		Try(typeof(FixParticles));
		Try(typeof(FixChunkHashes));
		Arm(FixWorldBlocks.Arm);
		Try(typeof(FixLiquidWalk));
		Try(typeof(FixGuiLayout));
		Try(typeof(FixContainerInfo));
		Try(typeof(FixSyringeHold));
		Try(typeof(FixChoicePrompt));
		Try(typeof(FixSyncRegister));
		Try(typeof(FixHostPickup));
		Try(typeof(FixContainerBroke));
		Arm(FixInvSync.Arm);
		Try(typeof(FixScrapDesc));
		Try(typeof(FixItemTooltip));
		Try(typeof(FixInvDrag));
		Try(typeof(FixSimRange));
		FindTheBug.Install(_harmony);
		Try(typeof(ItemTick));
		Try(typeof(HeldSeat));
		Try(typeof(BodyCache));
		_reportAt = Time.realtimeSinceStartup + Mathf.Max(1f, ReportSeconds.Value);
		Log.LogDebug($"[KrokV5Opt] v{OptimizerMain.Version} compress={CompressPooling.Value} imgui={CacheImguiStyles.Value}");

		SyncFixes();
    }

    static void SyncFixes() {
        ChangeSync.Install(_harmony);
        BagPlace.Install(_harmony);
        Log.LogDebug("[KrokV5Optimization] v" + OptimizerMain.Version + " host stamp is quadrants, rare circles are one player every 0.1s");
    }


	internal static void LateTick()
	{
		if (!_installed)
			return;
		FixMenuFps.Tick();
	}

	internal static void Tick()
	{
		if (!_installed)
			return;
		SafetyNet.Tick();
        SlowStamp.Tick();
        BodyViewParticles.Tick();
        WallholeZones.Tick();
        BagPlace.Tick();
        FixInvSync.Tick();
		if (ReportSeconds.Value <= 0f)
			return;
		if (Time.realtimeSinceStartup < _reportAt)
			return;
		_reportAt = Time.realtimeSinceStartup + ReportSeconds.Value;
		Log.LogDebug("[KrokV5Opt] " + Report.Line());
	}

	internal static void Shutdown()
	{
		if (!_installed)
			return;
		_installed = false;
		_harmony?.UnpatchSelf();
		_harmony = null;
	}

	private static void Try(Type patch)
	{
		try
		{
			_harmony.PatchAll(patch);
		}
		catch (Exception ex)
		{
			Log.LogWarning("[KrokV5Opt] patch skipped " + patch.Name + ": " + ex.Message);
		}
	}

	private static void Arm(Action<Harmony> arm)
	{
		try
		{
			arm(_harmony);
		}
		catch (Exception ex)
		{
			Log.LogWarning("[KrokV5Opt] patch skipped: " + ex.Message);
		}
	}
}
