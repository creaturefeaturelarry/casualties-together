using System;
using System.IO;

namespace KrokV5Optimization;

// shared gzip/deflate buffers. one gate per format so compress, decompress, and CompressWriter cannot reset the same deflater.
internal static class CompressPool
{
	internal static readonly MemoryStream GzipOut = new MemoryStream(4096);
	internal static readonly MemoryStream DeflateOut = new MemoryStream(4096);
	internal static readonly MemoryStream GzipDecOut = new MemoryStream(4096);
	internal static readonly MemoryStream DeflateDecOut = new MemoryStream(4096);
	internal static readonly object GzipGate = new object();
	internal static readonly object DeflateGate = new object();
	internal static bool GzipBusy;
	internal static bool DeflateBusy;
	internal static long InflateDecline;

	private static int _faults;

	internal static bool On => OptimizerMain.CompressPooling == null || OptimizerMain.CompressPooling.Value;

	internal static void Note(Exception ex)
	{
		_faults++;
		if (_faults <= 3)
			OptimizerMain.Log.LogWarning($"[KrokV5Opt] compress fell back: {ex.GetType().Name}: {ex.Message}");
	}

	internal static byte[] CopyExact(MemoryStream stream)
	{
		int len = (int)stream.Length;
		if (len == 0)
			return Array.Empty<byte>();
		byte[] owned = new byte[len];
		Buffer.BlockCopy(stream.GetBuffer(), 0, owned, 0, len);
		return owned;
	}

	internal static byte[] Ensure(byte[] buffer, int length)
	{
		if (length < 0)
			length = 0;
		if (buffer != null && buffer.Length >= length)
			return buffer;
		int cap = buffer == null || buffer.Length < 256 ? 256 : buffer.Length;
		while (cap < length)
			cap *= 2;
		return new byte[cap];
	}
}
