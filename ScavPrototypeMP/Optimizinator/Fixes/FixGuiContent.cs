using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// each UI pass did new GUIContent for layout and again for paint.
/// keep one per site and write the text into it.
/// </summary>
internal static class FixGuiContent
{
	internal static long Rent;
	internal static long New;

	private static readonly Dictionary<int, GUIContent> Contents = new Dictionary<int, GUIContent>();
	private static int _nextSite = 1;

	// this file's counters on the 10s report.
	internal static string Evidence() => "guiRent=" + Rent + " guiNew=" + New;

	// ImguiScan walks Together and swaps these two constructors for Content / ContentTip.
	internal static void Arm(Harmony harmony)
	{
		ConstructorInfo content = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string) });
		ConstructorInfo contentTip = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string), typeof(string) });
		ImguiScan.Arm(harmony, new[] { content, contentTip }, AccessTools.Method(typeof(FixGuiContent), nameof(Transpile)));
	}

	// site is the call site baked in by the transpiler, not the text. same site reuses one GUIContent.
	internal static GUIContent Content(string text, int site)
	{
		if (!Contents.TryGetValue(site, out GUIContent content) || content == null)
		{
			content = new GUIContent();
			Contents[site] = content;
			New++;
		}
		else
		{
			Rent++;
		}
		content.text = text ?? "";
		content.image = null;
		content.tooltip = null;
		return content;
	}

	// tooltip overload. still one GUIContent per site; the tip is written onto it.
	internal static GUIContent ContentTip(string text, string tip, int site)
	{
		GUIContent content = Content(text, site);
		content.tooltip = tip;
		return content;
	}

	// new GUIContent(string) and new GUIContent(string, string) become calls. the int pushed first is a stable id for that instruction.
	private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
	{
		ConstructorInfo content = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string) });
		ConstructorInfo contentTip = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string), typeof(string) });
		MethodInfo contentCall = AccessTools.Method(typeof(FixGuiContent), nameof(Content));
		MethodInfo tipCall = AccessTools.Method(typeof(FixGuiContent), nameof(ContentTip));
		foreach (CodeInstruction ins in instructions)
		{
			if (ins.opcode != OpCodes.Newobj || !(ins.operand is ConstructorInfo ctor))
			{
				yield return ins;
				continue;
			}
			MethodInfo call = ctor == content ? contentCall : ctor == contentTip ? tipCall : null;
			if (call == null)
			{
				yield return ins;
				continue;
			}
			// labels have to move onto the new ldc, or a branch that targeted the newobj lands in the middle of the call.
			int site = _nextSite++;
			var id = new CodeInstruction(OpCodes.Ldc_I4, site);
			id.labels.AddRange(ins.labels);
			id.blocks.AddRange(ins.blocks);
			yield return id;
			yield return new CodeInstruction(OpCodes.Call, call);
		}
	}
}
