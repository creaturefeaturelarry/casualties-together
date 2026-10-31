using System;
using System.Threading;
using CasualtiesTogetherUtils;
using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// Util.Decompress built a new GZipStream per packet and pinned the same native callback.
/// reuse one gzip inflater. a payload that is not gzip is refused.
/// </summary>
[HarmonyPatch(typeof(Util), nameof(Util.Decompress))]
internal static class FixUtilDecompress
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "gunzip=" + Hits;

	// false runs stock Util.Decompress, including when the bytes are not gzip.
	static bool Prefix(byte[] compressed_data, ref byte[] __result) => !Try(compressed_data, out __result);

	// a failed inflate returns false on purpose so stock can try. inflateDecline counts those.
	internal static bool Try(byte[] compressed, out byte[] result)
	{
		result = null;
		if (!CompressPool.On || compressed == null || CompressPool.GzipBusy || !Monitor.TryEnter(CompressPool.GzipGate))
			return false;
		try
		{
			if (CompressPool.GzipBusy)
				return false;
			CompressPool.GzipBusy = true;
			try
			{
				if (!ResettableDeflate.TryInflateGzip(compressed, CompressPool.GzipDecOut))
				{
					CompressPool.InflateDecline++;
					return false;
				}
				result = CompressPool.CopyExact(CompressPool.GzipDecOut);
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
				CompressPool.GzipBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(CompressPool.GzipGate);
		}
	}
}
