using System;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// the main menu never sets the menu-open flag, so fps is uncapped there.
/// with no world loaded, cap at 60 and turn vsync off. restore the video settings once a world exists.
/// </summary>
internal static class FixMenuFps
{
	private const int Cap = 60;

	private static bool _capping;

	// called from the main plugin LateUpdate. world == null is the menu and the loading screen.
	internal static void Tick()
	{
		if (WorldGeneration.world == null)
		{
			if (!_capping)
			{
				_capping = true;
				OptimizerMain.Log?.LogInfo("[KrokV5Opt] main menu fps " + Cap);
			}
			if (QualitySettings.vSyncCount != 0)
				QualitySettings.vSyncCount = 0;
			if (Application.targetFrameRate != Cap)
				Application.targetFrameRate = Cap;
			return;
		}

		if (!_capping)
			return;
		Restore();
		_capping = false;
	}

	// vsync and framerate come from the video settings. 0 framerate means uncapped (-1).
	private static void Restore()
	{
		try
		{
			SettingBool vsync = Settings.Get<SettingBool>("vsync");
			QualitySettings.vSyncCount = vsync != null && vsync.value ? 1 : 0;
			SettingInt rate = Settings.Get<SettingInt>("framerate");
			int value = rate != null ? rate.value : 0;
			Application.targetFrameRate = value == 0 ? -1 : Mathf.Max(10, value);
		}
		catch (Exception ex)
		{
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			OptimizerMain.Log?.LogWarning("[KrokV5Opt] menu fps restore failed: " + ex.Message);
		}
	}
}
