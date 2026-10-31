using HarmonyLib;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// Server_Sendchunk built a new DeflateStream per chunk.
/// zip with the shared deflater. packet bytes stay the same.
/// </summary>
internal static class FixChunkBlock
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "chunkBlock=" + Hits;

	// false skips stock Server_Sendchunk. a failure returns true so the original zipper still runs.
	internal static bool AllowStock(Vector2UInt8 syncchunk_coordinate, ScavPlayer plr, bool reliable)
	{
		if (OptimizerMain.CompressPooling != null && !OptimizerMain.CompressPooling.Value)
			return true;
		try
		{
			return !Try(syncchunk_coordinate, plr, reliable);
		}
		catch (System.Exception ex)
		{
			ChunkWire.Note(ex);
			return true;
		}
	}

	// pack the 32x32 block tile, deflate it, send CLIENT_WorldTilemapChunk. false means "use stock".
	private static bool Try(Vector2UInt8 coord, ScavPlayer plr, bool reliable)
	{
		if (plr == null || plr.ServerPlayerState == null || plr.ServerPlayerState.known_chunks == null)
			return false;
		if (!ChunkWire.CanSend(plr.playerId))
			return false;
		ushort[,] blocks = WorldChunkSync.worldBlocks;
		if (blocks == null)
			return false;

		ChunkWire.Pack(blocks, coord.x * ChunkWire.Tiles, coord.y * ChunkWire.Tiles);
		if (!FixUtilCompressDeflate.Try(ChunkWire.Raw, out byte[] compressed) || compressed == null)
			return false;

		object writer = ChunkWire.Begin(NetmsgId.CLIENT_WorldTilemapChunk);
		if (writer == null || !ChunkWire.PutByte(writer, coord.x) || !ChunkWire.PutByte(writer, coord.y) || !ChunkWire.PutBytes(writer, compressed))
			return false;
		if (!ChunkWire.Send(writer, reliable, plr.playerId))
			return false;
		plr.ServerPlayerState.known_chunks[coord.x, coord.y] = true;
		Hits++;
		return true;
	}
}
