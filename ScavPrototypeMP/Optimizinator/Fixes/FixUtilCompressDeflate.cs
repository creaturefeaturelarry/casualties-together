using System;
using System.Threading;
using CasualtiesTogetherUtils;
using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// Util.CompressDeflate built a new DeflateStream per packet and pinned the same native callback.
/// reuse one raw deflater. wire bytes stay the same.
/// </summary>
[HarmonyPatch(typeof(Util), nameof(Util.CompressDeflate))]
internal static class FixUtilCompressDeflate
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "deflate=" + Hits;

	// false runs stock Util.CompressDeflate.
	static bool Prefix(byte[] data, ref byte[] __result) => !Try(data, out __result);

	// also called by the chunk sends, which already skipped stock. CopyExact is a private array; the shared stream is reused next call.
	internal static bool Try(byte[] data, out byte[] result)
	{
		result = null;
		if (!CompressPool.On || data == null || CompressPool.DeflateBusy || !Monitor.TryEnter(CompressPool.DeflateGate))
			return false;
		try
		{
			if (CompressPool.DeflateBusy)
				return false;
			CompressPool.DeflateBusy = true;
			try
			{
				ResettableDeflate.WriteRaw(data, 0, data.Length, CompressPool.DeflateOut);
				result = CompressPool.CopyExact(CompressPool.DeflateOut);
				Hits++;
				return true;
			}
			catch (Exception ex)
			{
				CompressPool.Note(ex);
				result = null;
				return false;
			}
			finally
			{
				CompressPool.DeflateBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(CompressPool.DeflateGate);
		}
	}
}
