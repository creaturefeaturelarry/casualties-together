using BepInEx.Configuration;

namespace Together;

internal static class CpuAdvConfig
{
	internal static ConfigEntry<bool> Enabled;
	internal static ConfigEntry<float> PropWakeDistance;
	internal static ConfigEntry<float> EnemyWakeDistance;
	internal static ConfigEntry<float> TickSeconds;
	internal static ConfigEntry<int> MaxFlipsPerTick;
	internal static ConfigEntry<float> MaxViewHalfHeight;

	internal static void Bind()
	{
		ConfigFile config = Plugin.main.Config;
		Enabled = config.Bind("CpuAdv", "Enabled", true, "Turn props and enemies off outside the camera, inside the 3x3 ChunkSleep already keeps awake.");
		PropWakeDistance = config.Bind("CpuAdv", "PropWakeDistance", 8f, "Units outside the camera view that turn a prop on.");
		EnemyWakeDistance = config.Bind("CpuAdv", "EnemyWakeDistance", 60f, "Units outside the camera view that turn an enemy on.");
		TickSeconds = config.Bind("CpuAdv", "TickSeconds", 0.1f, "Seconds between cull passes.");
		MaxFlipsPerTick = config.Bind("CpuAdv", "MaxFlipsPerTick", 8, "Maximum SetActive changes per pass.");
		MaxViewHalfHeight = config.Bind("CpuAdv", "MaxViewHalfHeight", 0f, "Largest camera half-height the cull will fill. 0 uses the game's normal view.");
	}
}
