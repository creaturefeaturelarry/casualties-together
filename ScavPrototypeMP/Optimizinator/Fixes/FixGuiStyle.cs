using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// each UI pass copied GUIStyle and dropped the copy.
/// keep one per site. rebuild only when the skin changes.
/// </summary>
internal static class FixGuiStyle
{
	internal static long Hit;
	internal static long New;
	internal static int SkinGen = 1;

	private sealed class StyleSlot
	{
		internal GUIStyle Style;
		internal GUIStyle Source;
		internal int Gen;
	}

	private static readonly Dictionary<int, StyleSlot> Styles = new Dictionary<int, StyleSlot>();
	private static int _nextSite = 1;

	// this file's counters on the 10s report.
	internal static string Evidence() => "styleHit=" + Hit + " styleNew=" + New;

	// FixSkinSkip calls this when the skin actually rebuilds, so cached copies are thrown out next use.
	internal static void NoteSkinApplied() => SkinGen++;

	// swap `new GUIStyle(other)` for Copy. ImguiScan only patches methods that contain that constructor.
	internal static void Arm(Harmony harmony)
	{
		ConstructorInfo style = AccessTools.Constructor(typeof(GUIStyle), new[] { typeof(GUIStyle) });
		ImguiScan.Arm(harmony, new[] { style }, AccessTools.Method(typeof(FixGuiStyle), nameof(Transpile)));
	}

	// same site, same source, same skin generation: reuse. richText/fontSize/wordWrap still follow the source.
	internal static GUIStyle Copy(GUIStyle source, int site)
	{
		if (source == null)
			return new GUIStyle();
		if (!Styles.TryGetValue(site, out StyleSlot slot) || slot == null)
		{
			slot = new StyleSlot();
			Styles[site] = slot;
		}
		if (slot.Style != null && slot.Gen == SkinGen && ReferenceEquals(slot.Source, source))
		{
			GUIStyle style = slot.Style;
			if (style.richText != source.richText)
				style.richText = source.richText;
			if (style.fontSize != source.fontSize)
				style.fontSize = source.fontSize;
			if (style.wordWrap != source.wordWrap)
				style.wordWrap = source.wordWrap;
			Hit++;
			return style;
		}
		slot.Style = new GUIStyle(source);
		slot.Source = source;
		slot.Gen = SkinGen;
		New++;
		return slot.Style;
	}

	// one site id per newobj in the method. that id does not change between calls, so the dictionary key is stable.
	private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
	{
		ConstructorInfo style = AccessTools.Constructor(typeof(GUIStyle), new[] { typeof(GUIStyle) });
		MethodInfo copy = AccessTools.Method(typeof(FixGuiStyle), nameof(Copy));
		foreach (CodeInstruction ins in instructions)
		{
			if (ins.opcode != OpCodes.Newobj || !(ins.operand is ConstructorInfo ctor) || ctor != style)
			{
				yield return ins;
				continue;
			}
			int site = _nextSite++;
			var id = new CodeInstruction(OpCodes.Ldc_I4, site);
			id.labels.AddRange(ins.labels);
			id.blocks.AddRange(ins.blocks);
			yield return id;
			yield return new CodeInstruction(OpCodes.Call, copy);
		}
	}
}
