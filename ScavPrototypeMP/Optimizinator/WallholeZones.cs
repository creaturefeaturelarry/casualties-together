using CasualtiesTogetherUtils;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

// each wallhole caches a 64x64 box at spawn. this machine only turns it on
// when that box touches the local player's 3x3. other players do not count.
public static class WallholeZones
{
	const float Zone = 64f;

	struct Hole
	{
		public int Id;
		public GameObject Go;
		public float MinX;
		public float MinY;
		public float MaxX;
		public float MaxY;
		public bool On;
	}

	static readonly List<Hole> Holes = new List<Hole>(128);
	static readonly HashSet<int> Ids = new HashSet<int>();
	static int _chunkX;
	static int _chunkY;
	static bool _haveChunk;
	static bool _dirty;

	internal static void Note(GameObject go)
	{
		if (go == null)
			return;
		if (go.transform.parent == null || go.transform.parent.GetComponent<Tilemap>() == null)
			return;
		string name = go.name;
		if (string.IsNullOrEmpty(name) || name.IndexOf("wallholes", System.StringComparison.OrdinalIgnoreCase) < 0)
			return;
		int id = go.GetInstanceID();
		if (!Ids.Add(id))
			return;
		Vector3 p = go.transform.position;
		float half = Zone * 0.5f;
		Holes.Add(new Hole
		{
			Id = id,
			Go = go,
			MinX = p.x - half,
			MinY = p.y - half,
			MaxX = p.x + half,
			MaxY = p.y + half,
			On = go.activeSelf,
		});
		_dirty = true;
	}

	internal static void Tick()
	{
		if (Holes.Count == 0)
			return;
		if (!Window(out float minX, out float minY, out float maxX, out float maxY, out int cx, out int cy))
		{
			_haveChunk = false;
			return;
		}
		if (!_dirty && _haveChunk && _chunkX == cx && _chunkY == cy)
			return;
		_dirty = false;
		_haveChunk = true;
		_chunkX = cx;
		_chunkY = cy;
		for (int i = Holes.Count - 1; i >= 0; i--)
		{
			Hole hole = Holes[i];
			if (hole.Go == null)
			{
				Ids.Remove(hole.Id);
				int last = Holes.Count - 1;
				Holes[i] = Holes[last];
				Holes.RemoveAt(last);
				continue;
			}
			bool on = hole.MaxX > minX && hole.MinX < maxX && hole.MaxY > minY && hole.MinY < maxY;
			if (hole.On == on)
				continue;
			hole.Go.SetActive(on);
			hole.On = on;
			Holes[i] = hole;
		}
	}

	// the local body only. the host union would turn on holes around every player.
	static bool Window(out float minX, out float minY, out float maxX, out float maxY, out int cx, out int cy)
	{
		minX = minY = maxX = maxY = 0f;
		cx = cy = 0;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists)
			return false;
		Vector2 pos;
		if (Util.TryGetLocalBody(out Body body) && body != null)
			pos = body.transform.position;
		else if (Camera.main != null)
			pos = Camera.main.transform.position;
		else
			return false;
		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
		cx = chunk.x;
		cy = chunk.y;
		int size = WorldGeneration.CHUNKSIZE;
		if (size < 1)
			size = 64;
		int x0 = (chunk.x - 1) * size;
		int y0 = (chunk.y - 1) * size;
		int x1 = (chunk.x + 2) * size;
		int y1 = (chunk.y + 2) * size;
		minX = x0 - world.halfWidth;
		minY = y0 - world.halfHeight;
		maxX = x1 - world.halfWidth;
		maxY = y1 - world.halfHeight;
		return true;
	}
}

[HarmonyPatch(typeof(Object), "Instantiate", typeof(Object), typeof(Vector3), typeof(Quaternion), typeof(Transform))]
static class WallholeSpawn_Note
{
	static void Postfix(Object __result)
	{
		WallholeZones.Note(__result as GameObject);
	}
}
