using System;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

/// <summary>
/// host sync visits one chunk per physics step.
/// the set is the four chunks nearest each living player, rebuilt after a full pass
/// or when a player crosses a chunk border.
/// past 12 chunks, several share a step so a pass still finishes in 12 steps
/// (about four times a second at 50 Hz).
/// </summary>
internal static class ChunkStamp
{
	const int MaxStepsPerPass = 12;

	static readonly HashSet<long> Union = new HashSet<long>();
	static readonly HashSet<long> DueNow = new HashSet<long>();
	static readonly Dictionary<int, Vector2Int> PlayerChunk = new Dictionary<int, Vector2Int>();
	static readonly HashSet<long> Exact = new HashSet<long>();
	static long[] _order = Array.Empty<long>();
	static int _cursor;
	static float _fixed = -1f;
	static int _frame = -1;
	static int _living;

	internal static Vector2Int ChunkOf(Vector2 pos)
	{
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists)
			return default;
		return world.BlockToChunkPos(world.WorldToBlockPos(pos));
	}

	internal static long Pack(Vector2Int chunk)
	{
		return ((long)chunk.x << 32) | (uint)chunk.y;
	}

	// true when this position's chunk is being written this physics step.
	internal static bool Due(Vector2 pos, out bool inUnion)
	{
		Step();
		long key = Pack(ChunkOf(pos));
		inUnion = Union.Contains(key);
		return inUnion && DueNow.Contains(key);
	}

	// exact chunk under a living player. refreshed once a frame, and only rebuilt when a player crosses.
	internal static bool NearPlayer(Vector2 pos)
	{
		EnsurePlayers();
		if (Exact.Count == 0)
			return true;
		return Exact.Contains(Pack(ChunkOf(pos)));
	}

	static void Step()
	{
		if (Time.fixedTime == _fixed)
			return;
		_fixed = Time.fixedTime;
		bool moved = PlayersMoved();
		if (_order.Length == 0 || moved)
		{
			Rebuild();
			return;
		}
		_cursor += Batch();
		if (_cursor >= _order.Length)
		{
			_cursor = 0;
			Rebuild();
			return;
		}
		MarkDue();
	}

	// one chunk per step until the union passes 12. after that, enough chunks
	// share the step that the whole pass still completes in 12 physics steps.
	static int Batch()
	{
		int count = _order.Length;
		if (count <= MaxStepsPerPass)
			return 1;
		return (count + MaxStepsPerPass - 1) / MaxStepsPerPass;
	}

	static void MarkDue()
	{
		DueNow.Clear();
		int count = _order.Length;
		if (count == 0)
			return;
		int end = _cursor + Batch();
		if (end > count)
			end = count;
		for (int i = _cursor; i < end; i++)
			DueNow.Add(_order[i]);
	}

	static bool PlayersMoved()
	{
		int seen = 0;
		bool moved = false;
		foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
		{
			if (player == null || player.body == null)
				continue;
			seen++;
			int id = player.body.GetInstanceID();
			Vector2Int chunk = ChunkOf(player.body.transform.position);
			if (!PlayerChunk.TryGetValue(id, out Vector2Int prev) || prev != chunk)
			{
				PlayerChunk[id] = chunk;
				moved = true;
			}
		}
		if (seen != PlayerChunk.Count)
		{
			PlayerChunk.Clear();
			foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
			{
				if (player == null || player.body == null)
					continue;
				PlayerChunk[player.body.GetInstanceID()] = ChunkOf(player.body.transform.position);
			}
			moved = true;
		}
		return moved;
	}

	static void Rebuild()
	{
		Union.Clear();
		WorldGeneration world = WorldGeneration.world;
		if (world != null && world.worldExists)
		{
			foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
			{
				if (player == null || player.body == null)
					continue;
				AddFour(world, player.body.transform.position);
			}
		}
		if (Union.Count == 0)
		{
			_order = Array.Empty<long>();
			_cursor = 0;
			DueNow.Clear();
			return;
		}
		_order = new long[Union.Count];
		Union.CopyTo(_order);
		Array.Sort(_order);
		if (_cursor >= _order.Length)
			_cursor = 0;
		MarkDue();
	}

	// the chunk under the player, plus the three that share the nearest corner.
	static void AddFour(WorldGeneration world, Vector2 pos)
	{
		Vector2Int block = world.WorldToBlockPos(pos);
		int size = WorldGeneration.CHUNKSIZE;
		if (size < 1)
			return;
		Vector2Int chunk = world.BlockToChunkPos(block);
		int localX = block.x - chunk.x * size;
		int localY = block.y - chunk.y * size;
		int stepX = localX >= size / 2 ? 1 : -1;
		int stepY = localY >= size / 2 ? 1 : -1;
		Add(world, chunk.x, chunk.y);
		Add(world, chunk.x + stepX, chunk.y);
		Add(world, chunk.x, chunk.y + stepY);
		Add(world, chunk.x + stepX, chunk.y + stepY);
	}

	static void Add(WorldGeneration world, int chunkX, int chunkY)
	{
		int size = WorldGeneration.CHUNKSIZE;
		Vector2Int clamped = world.BlockToChunkPos(new Vector2Int(chunkX * size, chunkY * size));
		Union.Add(Pack(clamped));
	}

	static void EnsurePlayers()
	{
		if (_frame == Time.frameCount)
			return;
		_frame = Time.frameCount;
		int seen = 0;
		bool moved = false;
		foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
		{
			if (player == null || player.body == null)
				continue;
			seen++;
			int id = player.body.GetInstanceID();
			Vector2Int chunk = ChunkOf(player.body.transform.position);
			if (!PlayerChunk.TryGetValue(id, out Vector2Int prev) || prev != chunk)
			{
				PlayerChunk[id] = chunk;
				moved = true;
			}
		}
		if (seen != _living || seen != PlayerChunk.Count)
		{
			_living = seen;
			PlayerChunk.Clear();
			foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
			{
				if (player == null || player.body == null)
					continue;
				PlayerChunk[player.body.GetInstanceID()] = ChunkOf(player.body.transform.position);
			}
			moved = true;
		}
		if (!moved && Exact.Count == seen)
			return;
		Exact.Clear();
		foreach (KeyValuePair<int, Vector2Int> pair in PlayerChunk)
			Exact.Add(Pack(pair.Value));
	}
}
