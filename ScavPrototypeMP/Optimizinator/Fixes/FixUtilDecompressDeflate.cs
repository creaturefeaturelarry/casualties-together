using System;
using System.Threading;
using CasualtiesTogetherUtils;
using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// Util.DecompressDeflate built a new DeflateStream per packet.
/// reuse one raw inflater. a payload that is not raw deflate is refused.
/// </summary>
[HarmonyPatch(typeof(Util), nameof(Util.DecompressDeflate))]
internal static class FixUtilDecompressDeflate
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "inflate=" + Hits + " inflateDecline=" + CompressPool.InflateDecline;

	// false runs stock Util.DecompressDeflate.
	static bool Prefix(byte[] compressed_data, ref byte[] __result) => !Try(compressed_data, out __result);

	// raw deflate, not gzip. a bad payload falls through to stock.
	internal static bool Try(byte[] compressed, out byte[] result)
	{
		result = null;
		if (!CompressPool.On || compressed == null || CompressPool.DeflateBusy || !Monitor.TryEnter(CompressPool.DeflateGate))
			return false;
		try
		{
			if (CompressPool.DeflateBusy)
				return false;
			CompressPool.DeflateBusy = true;
			try
			{
				if (!ResettableDeflate.TryInflateRaw(compressed, CompressPool.DeflateDecOut))
				{
					CompressPool.InflateDecline++;
					return false;
				}
				result = CompressPool.CopyExact(CompressPool.DeflateDecOut);
				Hits++;
				return true;
			}
			catch (Exception ex)
			{
				CompressPool.Note(ex);
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
