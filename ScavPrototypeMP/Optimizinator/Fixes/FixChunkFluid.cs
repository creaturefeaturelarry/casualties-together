using HarmonyLib;
using LiteNetLib.Utils;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// the fluid chunk send built a new DeflateStream, same as the block send.
/// zip with the shared deflater and return the caller's packet buffer.
/// </summary>
internal static class FixChunkFluid
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "chunkFluid=" + Hits;

	// true when the shared deflater produced the writer. false leaves the stock zipper.
	internal static bool TryReplace(Vector2UInt8 syncchunk_coordinate, out NetDataWriter writer)
	{
		writer = null;
		if (OptimizerMain.CompressPooling != null && !OptimizerMain.CompressPooling.Value)
			return false;
		try
		{
			if (!Try(syncchunk_coordinate, out object boxed) || !(boxed is NetDataWriter packed))
				return false;
			writer = packed;
			return true;
		}
		catch (System.Exception ex)
		{
			ChunkWire.Note(ex);
			return false;
		}
	}

	// same pack as blocks, message id is the fluid chunk. does not send; the caller sends the writer we return.
	private static bool Try(Vector2UInt8 coord, out object writer)
	{
		writer = null;
		byte[,] fluid = WorldChunkSync.fluid;
		if (fluid == null || !ChunkWire.CanWrite())
			return false;

		ChunkWire.Pack(fluid, coord.x * ChunkWire.Tiles, coord.y * ChunkWire.Tiles);
		if (!FixUtilCompressDeflate.Try(ChunkWire.Raw, out byte[] compressed) || compressed == null)
			return false;

		writer = ChunkWire.Begin(NetmsgId.CLIENT_WorldFluidTilemapChunk);
		if (writer == null || !ChunkWire.PutByte(writer, coord.x) || !ChunkWire.PutByte(writer, coord.y) || !ChunkWire.PutBytes(writer, compressed))
		{
			writer = null;
			return false;
		}
		Hits++;
		return true;
	}
}
