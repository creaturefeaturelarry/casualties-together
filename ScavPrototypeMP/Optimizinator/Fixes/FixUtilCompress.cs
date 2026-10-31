using System;
using System.Threading;
using CasualtiesTogetherUtils;
using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// Util.Compress built a new GZipStream per packet. each one pinned DeflateStreamNative.UnmanagedReadOrWrite.
/// reuse one gzip deflater.
/// </summary>
[HarmonyPatch(typeof(Util), nameof(Util.Compress))]
internal static class FixUtilCompress
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "gzip=" + Hits;

	// false runs stock Util.Compress. true means __result is our gzip bytes.
	static bool Prefix(byte[] data, ref byte[] __result) => !Try(data, out __result);

	// one gzip deflater for the process. GzipBusy refuses a nested call so stock can run instead of resetting mid-write.
	internal static bool Try(byte[] data, out byte[] result)
	{
		result = null;
		if (!CompressPool.On || data == null || CompressPool.GzipBusy || !Monitor.TryEnter(CompressPool.GzipGate))
			return false;
		try
		{
			if (CompressPool.GzipBusy)
				return false;
			CompressPool.GzipBusy = true;
			try
			{
				ResettableDeflate.WriteGzip(data, 0, data.Length, CompressPool.GzipOut);
				result = CompressPool.CopyExact(CompressPool.GzipOut);
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
				CompressPool.GzipBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(CompressPool.GzipGate);
		}
	}
}
