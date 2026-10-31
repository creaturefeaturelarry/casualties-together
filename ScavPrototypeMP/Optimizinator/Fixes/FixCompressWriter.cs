using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace KrokV5Optimization;

/// <summary>
/// CompressWriter copied the gzip result into a new byte[].
/// write it into the NetDataWriter that already exists. callers that keep their own copy are unchanged.
/// </summary>
internal static class FixCompressWriter
{
	internal static long Hits;

	private static byte[] _copyScratch = new byte[256];
	private static byte[] _gzipRaw = new byte[256];

	// this file's counters on the 10s report.
	internal static string Evidence() => "writer=" + Hits;

	// true means the packet was rewritten. false leaves the caller on Util.Compress.
	internal static bool Apply(object w2, int start_to_ignore) => Try(w2, start_to_ignore);

	// gzip bytes after `start`, then Put them back over that same range. the header before `start` stays.
	private static bool Try(object writer, int start)
	{
		if (!CompressPool.On || writer == null || CompressPool.GzipBusy)
			return false;
		Type type = writer.GetType();
		PropertyInfo lengthProp = type.GetProperty("Length");
		PropertyInfo dataProp = type.GetProperty("Data");
		if (lengthProp == null || dataProp == null)
			return false;
		int length = (int)lengthProp.GetValue(writer) - start;
		byte[] data = dataProp.GetValue(writer) as byte[];
		if (length < 0 || data == null || !Monitor.TryEnter(CompressPool.GzipGate))
			return false;
		try
		{
			if (CompressPool.GzipBusy)
				return false;
			CompressPool.GzipBusy = true;
			try
			{
				// copy out first. gzip reads that copy while the writer buffer is still the uncompressed packet.
				_copyScratch = CompressPool.Ensure(_copyScratch, length);
				if (length > 0)
					Buffer.BlockCopy(data, start, _copyScratch, 0, length);
				ResettableDeflate.WriteGzip(_copyScratch, 0, length, CompressPool.GzipOut);
				int outLen = (int)CompressPool.GzipOut.Length;
				if (outLen > ushort.MaxValue)
					return false;
				_gzipRaw = CompressPool.Ensure(_gzipRaw, outLen);
				if (outLen > 0)
					Buffer.BlockCopy(CompressPool.GzipOut.GetBuffer(), 0, _gzipRaw, 0, outLen);
				MethodInfo setPos = type.GetMethod("SetPosition", new[] { typeof(int) });
				MethodInfo put = type.GetMethod("PutBytesWithLength", new[] { typeof(byte[]), typeof(int), typeof(ushort) });
				if (setPos == null || put == null)
					return false;
				setPos.Invoke(writer, new object[] { start });
				put.Invoke(writer, new object[] { _gzipRaw, 0, (ushort)outLen });
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
