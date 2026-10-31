using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// UIChoicePrompt draws Accept/Deny with the small button skin.
/// draw that prompt with small:false, then put the small skin back.
/// </summary>
internal static class FixChoicePrompt
{
	private static bool _sized;

	// swap in the large button skin before the prompt draws. progress 0 means the prompt is not up.
	internal static void Begin()
	{
		_sized = false;
		if (UIChoicePrompt.prompt_show_progress == 0f || GUI.skin == null || GUI.skin.button == null)
			return;
		UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
		_sized = true;
	}

	// put the small skin back. _sized is only set when Prefix actually changed it.
	internal static void End()
	{
		if (!_sized || GUI.skin == null || GUI.skin.button == null)
			return;
		_sized = false;
		UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
	}
}
