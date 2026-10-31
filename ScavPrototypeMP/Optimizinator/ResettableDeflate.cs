using System;
using System.IO;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace KrokV5Optimization;

// one raw deflater and one inflater per format, reset between packets.
// DeflateStream has no reset, and each one pins DeflateStreamNative.UnmanagedReadOrWrite for the process lifetime.
// output is rfc 1951 raw deflate, or rfc 1952 gzip around it. stock DeflateStream / GZipStream accept that.
internal static class ResettableDeflate
{
	private const int ScratchBytes = 8192;
	private const int MaxOutputBytes = 32 * 1024 * 1024;

	private static readonly byte[] GzipHeader =
	{
		0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff
	};

	private static readonly byte[] DummyByte = { 0 };

	private static readonly Deflater GzipDeflater = new Deflater(Deflater.DEFAULT_COMPRESSION, true);
	private static readonly Deflater RawDeflater = new Deflater(Deflater.DEFAULT_COMPRESSION, true);
	private static readonly Inflater GzipInflater = new Inflater(true);
	private static readonly Inflater RawInflater = new Inflater(true);
	private static readonly Crc32 Crc = new Crc32();
	private static readonly byte[] Scratch = new byte[ScratchBytes];

	internal static void WriteGzip(byte[] data, int offset, int length, MemoryStream dest)
	{
		dest.SetLength(0);
		dest.Position = 0;
		dest.Write(GzipHeader, 0, GzipHeader.Length);
		DeflateRaw(GzipDeflater, data, offset, length, dest);
		Crc.Reset();
		if (length > 0)
			Crc.Update(new ArraySegment<byte>(data, offset, length));
		WriteU32(dest, (uint)Crc.Value);
		WriteU32(dest, unchecked((uint)length));
	}

	internal static void WriteRaw(byte[] data, int offset, int length, MemoryStream dest)
	{
		dest.SetLength(0);
		dest.Position = 0;
		DeflateRaw(RawDeflater, data, offset, length, dest);
	}

	internal static bool TryInflateGzip(byte[] compressed, MemoryStream dest)
	{
		if (compressed == null || compressed.Length < 18)
			return false;
		if (!TrySkipGzipHeader(compressed, out int payload))
			return false;

		try
		{
			if (!Inflate(GzipInflater, compressed, payload, compressed.Length - payload, dest, out bool fedDummy))
				return false;
			if (fedDummy || GzipInflater.RemainingInput < 8)
				return false;

			int trailer = compressed.Length - GzipInflater.RemainingInput;
			if (trailer < payload || trailer + 8 > compressed.Length)
				return false;

			uint crc = ReadU32(compressed, trailer);
			uint size = ReadU32(compressed, trailer + 4);
			int length = (int)dest.Length;
			if (unchecked((uint)length) != size)
				return false;

			Crc.Reset();
			if (length > 0)
				Crc.Update(new ArraySegment<byte>(dest.GetBuffer(), 0, length));
			return unchecked((uint)Crc.Value) == crc;
		}
		catch (Exception)
		{
			return false;
		}
	}

	internal static bool TryInflateRaw(byte[] compressed, MemoryStream dest)
	{
		if (compressed == null || compressed.Length == 0)
			return false;

		try
		{
			if (!Inflate(RawInflater, compressed, 0, compressed.Length, dest, out bool fedDummy))
				return false;
			if (!fedDummy && RawInflater.RemainingInput != 0)
				return false;
			if (fedDummy && RawInflater.RemainingInput > 1)
				return false;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void DeflateRaw(Deflater deflater, byte[] data, int offset, int length, MemoryStream dest)
	{
		deflater.Reset();
		try
		{
			if (length > 0)
				deflater.SetInput(data, offset, length);
			deflater.Finish();
			int spins = 0;
			while (!deflater.IsFinished)
			{
				int n = deflater.Deflate(Scratch, 0, Scratch.Length);
				if (n > 0)
				{
					dest.Write(Scratch, 0, n);
					if (dest.Length > MaxOutputBytes)
						throw new InvalidDataException("deflate output exceeded cap");
					continue;
				}

				if (deflater.IsFinished || deflater.IsNeedingInput)
					break;
				if (++spins > 8)
					throw new InvalidDataException("deflate made no progress");
			}

			if (!deflater.IsFinished)
				throw new InvalidDataException("deflate did not finish");
		}
		catch
		{
			deflater.Reset();
			throw;
		}
	}

	private static bool Inflate(Inflater inflater, byte[] input, int offset, int count, MemoryStream dest, out bool fedDummy)
	{
		fedDummy = false;
		dest.SetLength(0);
		dest.Position = 0;
		inflater.Reset();
		inflater.SetInput(input, offset, count);
		int spins = 0;
		while (true)
		{
			int n = inflater.Inflate(Scratch, 0, Scratch.Length);
			if (n > 0)
			{
				dest.Write(Scratch, 0, n);
				if (dest.Length > MaxOutputBytes)
					return false;
				spins = 0;
				continue;
			}

			if (inflater.IsFinished)
				return true;
			if (inflater.IsNeedingDictionary)
				return false;
			if (inflater.IsNeedingInput && !fedDummy)
			{
				inflater.SetInput(DummyByte, 0, 1);
				fedDummy = true;
				continue;
			}

			if (++spins > 8)
				return false;
		}
	}

	private static bool TrySkipGzipHeader(byte[] data, out int offset)
	{
		offset = 0;
		if (data.Length < 18 || data[0] != 0x1f || data[1] != 0x8b || data[2] != 8)
			return false;

		byte flags = data[3];
		if ((flags & 0xe0) != 0)
			return false;

		int i = 10;
		if ((flags & 4) != 0)
		{
			if (i + 2 > data.Length)
				return false;
			int extra = data[i] | (data[i + 1] << 8);
			i += 2 + extra;
		}

		if ((flags & 8) != 0)
			i = SkipCString(data, i);
		if ((flags & 16) != 0)
			i = SkipCString(data, i);
		if ((flags & 2) != 0)
			i += 2;
		if (i < 10 || i > data.Length - 8)
			return false;

		offset = i;
		return true;
	}

	private static int SkipCString(byte[] data, int i)
	{
		while (i < data.Length)
		{
			if (data[i++] == 0)
				return i;
		}

		return -1;
	}

	private static void WriteU32(MemoryStream dest, uint value)
	{
		dest.WriteByte((byte)value);
		dest.WriteByte((byte)(value >> 8));
		dest.WriteByte((byte)(value >> 16));
		dest.WriteByte((byte)(value >> 24));
	}

	private static uint ReadU32(byte[] data, int offset)
	{
		return (uint)(data[offset]
			| (data[offset + 1] << 8)
			| (data[offset + 2] << 16)
			| (data[offset + 3] << 24));
	}
}
