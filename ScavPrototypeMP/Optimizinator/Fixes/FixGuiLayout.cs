using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// the HUD drew twice inside one UI event. skip the second call. layout and the visible draw still each run once.
/// </summary>
internal static class FixGuiLayout
{
	internal static long Skip;

	static int _frame = -1;
	static int _event = int.MinValue;
	static int _count;

	// this file's counters on the 10s report.
	internal static string Evidence() => "guiHud=" + Skip;

	// false skips the second _GUI_DoInGameUI in the same frame and IMGUI event. layout and repaint are different events, so each still runs once.
	internal static bool Allow()
	{
		int frame = Time.frameCount;
		int kind = Event.current == null ? -1 : (int)Event.current.type;
		if (frame != _frame || kind != _event)
		{
			_frame = frame;
			_event = kind;
			_count = 0;
		}
		_count++;
		if (_count == 1)
			return true;
		Skip++;
		return false;
	}
}
