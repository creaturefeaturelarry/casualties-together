using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// the mp button skin rebuilt every UI pass, which is what allocated the GUIStyle and RectOffset copies.
/// skip the rebuild when scale, screen size, and the recalc flag are unchanged and no menu is open.
/// </summary>
internal static class FixSkinSkip
{
	internal static long Skip;
	internal static long Run;

	private static FieldInfo _recalc;
	private static float _lastScale = float.NaN;
	private static int _lastW = -1;
	private static int _lastH = -1;
	private static bool _stamped;

	// this file's counters on the 10s report.
	internal static string Evidence() => "skinSkip=" + Skip + " skinRun=" + Run;

	// false skips _GUI_MPUISetSkinValues. a menu or a choice prompt still rebuilds, because those change the skin on purpose.
	internal static bool Allow()
	{
		if (OptimizerMain.CacheImguiStyles != null && !OptimizerMain.CacheImguiStyles.Value)
			return true;

		_recalc ??= AccessTools.Field(typeof(UIBullshit), "_recalculate_skin");
		bool recalc = _recalc != null && _recalc.GetValue(null) is true;
		float scale = UIBullshit.uiScale;
		int w = Screen.width;
		int h = Screen.height;
		bool changed = float.IsNaN(_lastScale)
		               || Mathf.Abs(scale - _lastScale) > 0.0001f
		               || w != _lastW
		               || h != _lastH
		               || recalc
		               || !_stamped;
		bool prompt = UIChoicePrompt.prompt_show_progress != 0f;
		bool menu = prompt || UIBullshit.IsAnyMenuOpen() || UIMainMenu.IsOpen() || UIMainMenu.mainmenu_open;
		if (changed || menu)
		{
			_lastScale = scale;
			_lastW = w;
			_lastH = h;
			_stamped = true;
			FixGuiStyle.NoteSkinApplied();
			Run++;
			return true;
		}

		Skip++;
		return false;
	}
}
