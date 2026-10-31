using System.Collections.Generic;
using KrokV5Optimization;
using UnityEngine;

namespace Together;

// a thrown item, a fired hook, or a lit stick of dynamite.
// for 10s it stamps the same 3x3 a player does, so the chunk it enters stays simulated.
public sealed class SimKeep : MonoBehaviour
{
	const float Window = 10f;

	static readonly List<SimKeep> Live = new List<SimKeep>(8);

	float Until;

	// starts the window, or pushes it out if this object was already kept.
	public static void Arm(GameObject go)
	{
		if (go == null)
			return;
		SimKeep keep = go.GetComponent<SimKeep>();
		if (keep == null)
			keep = go.AddComponent<SimKeep>();
		float until = Time.time + Window;
		if (until > keep.Until)
			keep.Until = until;
		if (!Live.Contains(keep))
			Live.Add(keep);
	}

	// lit dynamite calls this every update. the first sight of the fuse starts the window.
	public static void ArmIfIdle(GameObject go)
	{
		if (go == null)
			return;
		SimKeep keep = go.GetComponent<SimKeep>();
		if (keep != null && keep.Until > Time.time)
			return;
		Arm(go);
	}

	void OnDestroy()
	{
		Live.Remove(this);
	}

	internal static void Stamp(bool[,] table, WorldGeneration world)
	{
		if (table == null || world == null)
			return;
		float now = Time.time;
		for (int i = Live.Count - 1; i >= 0; i--)
		{
			SimKeep keep = Live[i];
			if (keep == null)
			{
				Live.RemoveAt(i);
				continue;
			}
			if (now >= keep.Until)
			{
				Live.RemoveAt(i);
				Destroy(keep);
				continue;
			}
			FixSimRange.Mark(table, world, keep.transform.position);
		}
	}

	// true when pos sits in some kept object's 3x3. empty is the common case.
	internal static bool Covers(Vector2 pos)
	{
		if (Live.Count == 0)
			return false;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists)
			return false;
		float now = Time.time;
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		for (int i = 0; i < Live.Count; i++)
		{
			SimKeep keep = Live[i];
			if (keep == null || now >= keep.Until)
				continue;
			Vector2Int at = world.BlockToChunkPos(world.WorldToBlockPos(keep.transform.position));
			int dx = chunk.x - at.x;
			int dy = chunk.y - at.y;
			if (dx < 0)
				dx = -dx;
			if (dy < 0)
				dy = -dy;
			if (dx <= 1 && dy <= 1)
				return true;
		}
		return false;
	}
}
