using BepInEx.Configuration;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

// one switch for every evidence logger. off means the hooks are not installed.
internal static class FindTheBug
{
	internal static ConfigEntry<bool> Enabled;

	internal static bool On => Enabled != null && Enabled.Value;

	internal static void Install(Harmony harmony)
	{
		Enabled = Plugin.main.Config.Bind("Probe", "FindTheBug", false, "Evidence loggers. Off installs nothing.");
		if (!On)
			return;
		EvidenceTmpMemoryLeak.Arm(harmony);
		EvidenceBag.Arm(harmony);
	}

	internal static void Write(string line)
	{
		if (!On || OptimizerMain.Log == null)
			return;
        if (!Together.log.verbose) return;
        OptimizerMain.Log.LogInfo("[KrokV5Opt] " + line);
	}
}
