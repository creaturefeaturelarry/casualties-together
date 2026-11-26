using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

internal static class CullWorld
{
	private const int CellSize = 32;
	private const int LabsDepth = 11;
	private const float ViewPad = 10f;
	private const float OffGrace = 1f;
	// After a layer finishes, everything is awake. Spend the turn-off allowance faster
	// until only the 3x3 around the player is still drawn, and never longer than this.
	private const float CatchUpSeconds = 20f;
	private const int CatchUpScale = 10;
	// HandleCameraPosition shifts the camera toward the cursor by up to 2 units, or 10 while zoomTime > 0.
	// Following that shift turns the same props on and off for the whole time the mouse is moving.
	private const float LookAhead = 10f;

	private sealed class Entry
	{
		internal int Id;
		internal GameObject Go;
		internal Vector2 Pos;
		internal Vector2 CenterOffset;
		internal Vector2 Half;
		internal bool Enemy;
		internal float OffAt;
		internal bool Parked;
		internal bool InGrid;
		internal long Cell;
	}

	private static readonly List<Entry> Entries = new List<Entry>(512);
	private static readonly Dictionary<int, int> ById = new Dictionary<int, int>();
	private static readonly Dictionary<long, List<int>> Cells = new Dictionary<long, List<int>>();
	private static readonly List<int> Visit = new List<int>(256);
	private static readonly HashSet<int> VisitSeen = new HashSet<int>();
	private static readonly List<long> Hot = new List<long>(256);
	private static readonly HashSet<long> HotSet = new HashSet<long>();
	private static readonly Dictionary<int, bool> ChunkDrawn = new Dictionary<int, bool>();
	private static readonly Dictionary<int, float> ChunkOffAt = new Dictionary<int, float>();

	private static bool _scanned;
	private static bool _wasGenerating = true;
	private static int _depth = -1;
	private static float _age;
	private static int _cursor;
	private static int _hotCursor;
	private static bool _haveView;
	private static bool _pendingFar;
	private static float _viewX;
	private static float _viewY;
	private static float _viewH;
	private static bool _sawWorld;
	private static int _sawOwns = -1;
	private static float _beatAt;
	private static int _added;
	private static int _persistentSkipped;
	private static float _catchUpUntil;
	private static int _stamp;
	private static bool _hosting;
	private static float _prop;
	private static float _enemy;
	private static readonly List<Vector2> Others = new List<Vector2>(8);
	private static readonly List<Rect2> Guards = new List<Rect2>(8);

	internal static bool OwnsVisibility(WorldGeneration world)
	{
		return Watching(world);
	}

	internal static bool WatchingSpawns()
	{
		return Watching(WorldGeneration.world);
	}

	private static bool Watching(WorldGeneration world)
	{
		return CpuAdvConfig.Enabled != null && CpuAdvConfig.Enabled.Value
			&& world != null && !world.generatingWorld && world.worldExists
			&& world.biomeDepth != LabsDepth;
	}

	internal static void ApplyVisibleChunks(WorldGeneration world)
	{
	}

	internal static void Tick()
	{
		if (CpuAdvConfig.Enabled == null || !CpuAdvConfig.Enabled.Value)
			return;

		WorldGeneration world = WorldGeneration.world;
		if (world == null)
		{
			if (_sawWorld)
				Layer("world gone");
			if (Entries.Count > 0 || ChunkDrawn.Count > 0)
				DropTracking("world missing");
			_scanned = false;
			_wasGenerating = true;
			_sawWorld = false;
			_sawOwns = -1;
			return;
		}
		_sawWorld = true;

		bool generating = world.generatingWorld;
		if (generating)
		{
			// Clear() yields once, then FindObjectsOfType, which skips inactive objects.
			// Props we turned off would survive into the next layer and the load would not finish.
			if (!_wasGenerating)
				RestoreAndClear("generation started depth=" + world.biomeDepth);
			_wasGenerating = true;
			_scanned = false;
			Beat(world, false, "gen");
			return;
		}
		if (_wasGenerating)
		{
			Layer("gen finished " + WorldLine(world));
			_catchUpUntil = Time.unscaledTime + CatchUpSeconds;
			Layer("catch-up turn-off x" + CatchUpScale);
		}
		_wasGenerating = false;

		if (!world.worldExists)
		{
			Beat(world, false, "wait");
			return;
		}

		if (world.biomeDepth == LabsDepth)
		{
			if (_depth != LabsDepth)
			{
				RestoreAndClear("entered labs depth=" + world.biomeDepth);
				Layer("cull off, lab layer");
			}
			_depth = LabsDepth;
			_scanned = false;
			NoteOwns(world, false);
			Beat(world, false, "labs");
			return;
		}

		if (_depth != world.biomeDepth)
		{
			RestoreAndClear("depth " + _depth + " -> " + world.biomeDepth);
			_depth = world.biomeDepth;
			_scanned = false;
		}
		NoteOwns(world, true);

		if (!_scanned)
		{
			Layer("scan begin " + WorldLine(world));
			Scan(world);
			_scanned = true;
			_haveView = false;
			_pendingFar = true;
		}

		Camera cam = ViewCamera();
		bool camOk = cam != null && cam.orthographic;
		Body localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		_hosting = HostWatch.IsHost();
		if (_hosting)
			HostWatch.CopyOthers(Others, localBody);
		else
			Others.Clear();
		int stamps = (camOk ? 1 : 0) + Others.Count;
		if (stamps == 0)
			return;

		Vector2 anchor = camOk ? Anchor(cam) : Vector2.zero;
		float half = 0f;
		if (camOk)
		{
			float liveHalf = cam.orthographicSize;
			float cap = ViewCap.HalfHeight(cam);
			half = cap > 0.5f && liveHalf > cap ? cap : liveHalf;
		}
		bool moved = camOk && (!_haveView
			|| (anchor.x - _viewX) * (anchor.x - _viewX) + (anchor.y - _viewY) * (anchor.y - _viewY) > 0.25f
			|| Mathf.Abs(half - _viewH) > 0.05f);
		if (moved)
			_pendingFar = true;
		bool catchUp = CatchUpActive(world, camOk ? anchor : (Vector2?)null);
		if (!_hosting && !moved && !_pendingFar && !catchUp)
			return;

		float dt = Time.unscaledDeltaTime;
		_age += dt;
		float interval = CpuAdvConfig.TickSeconds != null ? CpuAdvConfig.TickSeconds.Value : 0.1f;
		if (interval < 0.02f)
			interval = 0.02f;
		if (_age < interval)
			return;
		_age = 0f;
		if (camOk)
		{
			_haveView = true;
			_viewX = anchor.x;
			_viewY = anchor.y;
			_viewH = half;
		}

		_prop = CpuAdvConfig.PropWakeDistance != null ? CpuAdvConfig.PropWakeDistance.Value : 8f;
		_enemy = CpuAdvConfig.EnemyWakeDistance != null ? CpuAdvConfig.EnemyWakeDistance.Value : 60f;
		if (_prop < 0f)
			_prop = 0f;
		if (_enemy < 0f)
			_enemy = 0f;
		if (_hosting)
			_prop *= 2f;
		if (_enemy < _prop)
			_enemy = _prop;

		Guards.Clear();
		Rect2 cameraView = default;
		if (camOk)
		{
			cameraView = ViewOf(cam, anchor);
			Guards.Add(cameraView);
		}
		for (int i = 0; i < Others.Count; i++)
		{
			Vector2 p = Others[i];
			Guards.Add(new Rect2(p.x, p.y, p.x, p.y));
		}

		if (_stamp >= stamps)
			_stamp = 0;
		int stamp = _stamp;
		_stamp++;
		if (_stamp >= stamps)
			_stamp = 0;

		Rect2 view;
		bool drawChunks;
		if (camOk && stamp == 0)
		{
			view = cameraView;
			drawChunks = true;
		}
		else
		{
			int index = stamp - (camOk ? 1 : 0);
			Vector2 p = Others[index];
			view = new Rect2(p.x, p.y, p.x, p.y);
			drawChunks = false;
		}

		int budget = CpuAdvConfig.MaxFlipsPerTick != null ? CpuAdvConfig.MaxFlipsPerTick.Value : 8;
		if (budget < 1)
			budget = 1;
		// Each other player is a separate wake circle, visited in turn. A flat cap
		// leaves those circles behind. Two extra flips per other player keeps up
		// without turning a crowded host into one long hitch.
		if (_hosting && Others.Count > 0)
			budget += Others.Count * 2;
		// tilemap toggles are cheap. SetActive on a prop is the frame spike, so it keeps most of the budget.
		int chunkBudget = budget / 4;
		if (chunkBudget < 1)
			chunkBudget = 1;
		if (budget > 2 && chunkBudget > budget - 2)
			chunkBudget = budget - 2;
		int rest = budget - chunkBudget;
		int farBudget = rest / 2;
		int nearBudget = rest - farBudget;
		if (nearBudget < 1)
			nearBudget = 1;
		if (catchUp)
		{
			farBudget *= CatchUpScale;
			chunkBudget *= CatchUpScale;
		}

		RefreshActiveInView(view);
		ApplyObjects(view, nearBudget);
		float reach = _enemy > _prop ? _enemy : _prop;
		int turnedOff = SweepFar(view.Expand(reach), farBudget);
		if (turnedOff == 0 && stamp == stamps - 1)
			_pendingFar = false;
		Beat(world, true, _hosting ? "host" : "play");
	}

	internal static void Consider(GameObject go)
	{
		if (go == null || !WatchingSpawns())
			return;
		RegisterTree(go);
	}

	private static void Scan(WorldGeneration world)
	{
		float started = Time.realtimeSinceStartup;
		_persistentSkipped = 0;
		BuildingEntity[] builds = Object.FindObjectsOfType<BuildingEntity>(true);
		for (int i = 0; i < builds.Length; i++)
		{
			if (builds[i] != null)
				TryAdd(builds[i].transform);
		}
		Item[] items = Object.FindObjectsOfType<Item>(true);
		for (int i = 0; i < items.Length; i++)
		{
			if (items[i] != null)
				TryAdd(items[i].transform);
		}
		SpiderHandler[] spiders = Object.FindObjectsOfType<SpiderHandler>(true);
		for (int i = 0; i < spiders.Length; i++)
		{
			if (spiders[i] != null)
				TryAdd(spiders[i].transform);
		}
		int wallholes = ScanWallholes(world);
		int enemies = 0;
		int asleep = 0;
		for (int i = 0; i < Entries.Count; i++)
		{
			Entry entry = Entries[i];
			if (entry == null || entry.Go == null)
				continue;
			if (entry.Enemy)
				enemies++;
			if (!entry.Go.activeSelf)
				asleep++;
		}
		int ms = (int)((Time.realtimeSinceStartup - started) * 1000f);
		Layer("scan " + WorldLine(world)
			+ " buildings=" + builds.Length
			+ " items=" + items.Length
			+ " spiders=" + spiders.Length
			+ " wallholes=" + wallholes
			+ " tracked=" + Entries.Count
			+ " enemies=" + enemies
			+ " asleep=" + asleep
			+ " persistent=" + _persistentSkipped
			+ " ms=" + ms);
		_added = 0;
	}

	private static void RegisterTree(GameObject go)
	{
		TryAdd(go.transform);
		Transform[] kids = go.GetComponentsInChildren<Transform>(true);
		for (int i = 0; i < kids.Length; i++)
			TryAdd(kids[i]);
	}

	private static void TryAdd(Transform t)
	{
		if (!Keep.IsCullRoot(t))
			return;
		GameObject go = t.gameObject;
		// Custom item templates live inactive in DontDestroyOnLoad. Waking them
		// lets the next layer's Clear() destroy the template, and later spawns return null.
		if (go.scene.buildIndex < 0)
		{
			_persistentSkipped++;
			return;
		}
		int id = go.GetInstanceID();
		if (ById.ContainsKey(id))
			return;

		Bake(go, out Vector2 offset, out Vector2 half);
		var entry = new Entry
		{
			Id = id,
			Go = go,
			Pos = go.transform.position,
			CenterOffset = offset,
			Half = half,
			Enemy = Keep.IsEnemy(go),
			OffAt = -1f,
		};
		int index = Entries.Count;
		Entries.Add(entry);
		ById[id] = index;
		Place(entry, index);
		_added++;
	}

	private static void Bake(GameObject go, out Vector2 offset, out Vector2 half)
	{
		bool any = false;
		Bounds box = new Bounds(go.transform.position, Vector3.zero);
		Collider2D[] cols = go.GetComponentsInChildren<Collider2D>(true);
		for (int i = 0; i < cols.Length; i++)
		{
			if (cols[i] == null)
				continue;
			if (!any)
			{
				box = cols[i].bounds;
				any = true;
			}
			else
				box.Encapsulate(cols[i].bounds);
		}
		if (!any)
		{
			Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
			for (int i = 0; i < renderers.Length; i++)
			{
				if (renderers[i] == null)
					continue;
				if (!any)
				{
					box = renderers[i].bounds;
					any = true;
				}
				else
					box.Encapsulate(renderers[i].bounds);
			}
		}
		Vector2 pos = go.transform.position;
		if (!any)
		{
			offset = Vector2.zero;
			half = new Vector2(0.5f, 0.5f);
			return;
		}
		offset = (Vector2)box.center - pos;
		half = box.extents;
		if (half.x < 0.25f)
			half.x = 0.25f;
		if (half.y < 0.25f)
			half.y = 0.25f;
	}

	private static void RefreshActiveInView(Rect2 view)
	{
		Collect(view.Expand(_enemy > 0f ? _enemy : 60f));
		for (int i = 0; i < Visit.Count; i++)
		{
			int index = Visit[i];
			if (index < 0 || index >= Entries.Count)
				continue;
			Entry entry = Entries[index];
			if (entry == null || entry.Go == null || !entry.Go.activeInHierarchy)
				continue;
			Vector2 pos = entry.Go.transform.position;
			if ((pos - entry.Pos).sqrMagnitude < 0.0001f)
				continue;
			entry.Pos = pos;
			Place(entry, index);
		}
	}

	private static int ApplyObjects(Rect2 view, int budget)
	{
		float reach = _enemy > _prop ? _enemy : _prop;
		Collect(view.Expand(reach));
		if (Visit.Count == 0)
			return budget;

		int start = _cursor;
		if (start >= Visit.Count)
			start = 0;
		for (int n = 0; n < Visit.Count && budget > 0; n++)
		{
			int slot = (start + n) % Visit.Count;
			Entry entry = Entries[Visit[slot]];
			if (entry == null || entry.Go == null)
				continue;
			float wake = entry.Enemy ? _enemy : _prop;
			bool want = Distance(entry, view) <= wake;
			if (!Flip(entry, want, ref budget))
				continue;
			if (want)
				NoteHot(entry.Cell);
		}
		_cursor = Visit.Count == 0 ? 0 : (start + 1) % Visit.Count;
		return budget;
	}

	/// <summary>
	/// cells outside the wake ring still hold objects that started active.
	/// walk those cells a few at a time and turn them off. the near ring is handled above.
	/// </summary>
	private static int SweepFar(Rect2 near, int budget)
	{
		int turned = 0;
		int guard = Hot.Count;
		int seen = 0;
		while (budget > 0 && seen < guard && Hot.Count > 0)
		{
			if (_hotCursor >= Hot.Count)
				_hotCursor = 0;
			long key = Hot[_hotCursor];
			seen++;
			if (!Cells.TryGetValue(key, out List<int> list) || list.Count == 0)
			{
				RemoveHotAt(_hotCursor);
				continue;
			}
			if (CellHits(key, near))
			{
				_hotCursor++;
				continue;
			}
			for (int i = list.Count - 1; i >= 0 && budget > 0; i--)
			{
				int index = list[i];
				if (index < 0 || index >= Entries.Count)
				{
					list.RemoveAt(i);
					continue;
				}
				Entry entry = Entries[index];
				if (entry == null || entry.Go == null)
				{
					list.RemoveAt(i);
					continue;
				}
				if (!entry.Go.activeSelf)
					continue;
				if (!Flip(entry, false, ref budget))
					continue;
				turned++;
			}
			if (!CellHasActive(list))
				RemoveHotAt(_hotCursor);
			else
				_hotCursor++;
		}
		return turned;
	}

	private static bool CatchUpActive(WorldGeneration world, Vector2? anchor)
	{
		if (_catchUpUntil <= 0f)
			return false;
		if (Time.unscaledTime >= _catchUpUntil)
		{
			_catchUpUntil = 0f;
			Layer("catch-up done timeout");
			return false;
		}
		if (anchor.HasValue && CulledToNeighborhood(world, anchor.Value))
		{
			_catchUpUntil = 0f;
			Layer("catch-up done 3x3");
			return false;
		}
		return true;
	}

	// The player's chunk plus the eight around it. Done when every other chunk renderer is off.
	private static bool CulledToNeighborhood(WorldGeneration world, Vector2 anchor)
	{
		TilemapRenderer[,] maps = world.renderChunks;
		if (maps == null)
			return false;
		Vector2Int mine = world.BlockToChunkPos(world.WorldToBlockPos(anchor));
		int w = maps.GetLength(0);
		int h = maps.GetLength(1);
		for (int x = 0; x < w; x++)
		{
			int dx = x - mine.x;
			if (dx < 0)
				dx = -dx;
			for (int y = 0; y < h; y++)
			{
				int dy = y - mine.y;
				if (dy < 0)
					dy = -dy;
				if (dx <= 1 && dy <= 1)
					continue;
				TilemapRenderer renderer = maps[x, y];
				if (renderer != null && renderer.enabled)
					return false;
			}
		}
		return true;
	}

	private static void ApplyChunks(WorldGeneration world, Rect2 view, int budget)
	{
		TilemapRenderer[,] maps = world.renderChunks;
		if (maps == null || budget <= 0)
			return;
		int w = maps.GetLength(0);
		int h = maps.GetLength(1);
		int size = WorldGeneration.CHUNKSIZE;
		for (int x = 0; x < w && budget > 0; x++)
		{
			for (int y = 0; y < h && budget > 0; y++)
			{
				TilemapRenderer renderer = maps[x, y];
				if (renderer == null)
					continue;
				Vector2 a = world.BlockToWorldPos(new Vector2Int(x * size, y * size));
				Vector2 b = world.BlockToWorldPos(new Vector2Int(x * size + size, y * size + size));
				float x0 = a.x < b.x ? a.x : b.x;
				float x1 = a.x > b.x ? a.x : b.x;
				float y0 = a.y < b.y ? a.y : b.y;
				float y1 = a.y > b.y ? a.y : b.y;
				bool draw = view.Overlaps(x0 - 0.5f, y0 - 0.5f, x1 + 0.5f, y1 + 0.5f);
				int id = renderer.GetInstanceID();
				bool on = renderer.enabled;
				if (draw)
				{
					ChunkOffAt.Remove(id);
					if (!on)
					{
						renderer.enabled = true;
						budget--;
					}
					ChunkDrawn[id] = true;
					continue;
				}
				if (!on)
				{
					ChunkDrawn[id] = false;
					continue;
				}
				if (!ChunkOffAt.TryGetValue(id, out float since))
				{
					ChunkOffAt[id] = Time.unscaledTime;
					continue;
				}
				if (Time.unscaledTime - since < OffGrace)
					continue;
				renderer.enabled = false;
				ChunkOffAt.Remove(id);
				ChunkDrawn[id] = false;
				budget--;
			}
		}
	}

	private static void Collect(Rect2 area)
	{
		Visit.Clear();
		VisitSeen.Clear();
		int x0 = CellOf(area.X0);
		int x1 = CellOf(area.X1);
		int y0 = CellOf(area.Y0);
		int y1 = CellOf(area.Y1);
		for (int x = x0; x <= x1; x++)
		{
			for (int y = y0; y <= y1; y++)
			{
				if (!Cells.TryGetValue(Key(x, y), out List<int> list))
					continue;
				for (int i = list.Count - 1; i >= 0; i--)
				{
					int index = list[i];
					if (index < 0 || index >= Entries.Count || Entries[index] == null || Entries[index].Go == null)
					{
						list.RemoveAt(i);
						continue;
					}
					if (VisitSeen.Add(index))
						Visit.Add(index);
				}
			}
		}
	}

	private static float Distance(Entry entry, Rect2 view)
	{
		Vector2 center = entry.Pos + entry.CenterOffset;
		return view.DistanceToBox(center.x - entry.Half.x, center.y - entry.Half.y, center.x + entry.Half.x, center.y + entry.Half.y);
	}

	private static void Place(Entry entry, int index)
	{
		long next = Key(CellOf(entry.Pos.x), CellOf(entry.Pos.y));
		if (entry.InGrid && entry.Cell == next && Cells.ContainsKey(next))
		{
			List<int> same = Cells[next];
			for (int i = 0; i < same.Count; i++)
			{
				if (same[i] == index)
					return;
			}
		}
		if (entry.InGrid && Cells.TryGetValue(entry.Cell, out List<int> old))
		{
			for (int i = old.Count - 1; i >= 0; i--)
			{
				if (old[i] == index)
					old.RemoveAt(i);
			}
		}
		entry.Cell = next;
		entry.InGrid = true;
		if (!Cells.TryGetValue(next, out List<int> list))
		{
			list = new List<int>(8);
			Cells[next] = list;
		}
		list.Add(index);
		if (entry.Go != null && entry.Go.activeSelf)
			NoteHot(next);
	}

	private static void NoteHot(long cell)
	{
		if (HotSet.Add(cell))
			Hot.Add(cell);
	}

	private static void RemoveHotAt(int index)
	{
		long cell = Hot[index];
		HotSet.Remove(cell);
		int last = Hot.Count - 1;
		Hot[index] = Hot[last];
		Hot.RemoveAt(last);
		if (_hotCursor > index)
			_hotCursor--;
	}

	private static bool CellHits(long key, Rect2 area)
	{
		int x = (int)(key >> 32);
		int y = unchecked((int)(key & 0xFFFFFFFF));
		float x0 = x * CellSize;
		float y0 = y * CellSize;
		return area.Overlaps(x0, y0, x0 + CellSize, y0 + CellSize);
	}

	private static bool CellHasActive(List<int> list)
	{
		for (int i = 0; i < list.Count; i++)
		{
			int index = list[i];
			if (index < 0 || index >= Entries.Count)
				continue;
			Entry entry = Entries[index];
			if (entry != null && entry.Go != null && entry.Go.activeSelf)
				return true;
		}
		return false;
	}

	private static int ScanWallholes(WorldGeneration world)
	{
		TilemapRenderer[,] maps = world.renderChunks;
		if (maps == null)
			return 0;
		int before = Entries.Count;
		int w = maps.GetLength(0);
		int h = maps.GetLength(1);
		for (int x = 0; x < w; x++)
		{
			for (int y = 0; y < h; y++)
			{
				TilemapRenderer renderer = maps[x, y];
				if (renderer == null)
					continue;
				Transform chunk = renderer.transform;
				int children = chunk.childCount;
				for (int i = 0; i < children; i++)
				{
					Transform child = chunk.GetChild(i);
					if (child != null && child.name.IndexOf("wallhole", System.StringComparison.OrdinalIgnoreCase) >= 0)
						TryAdd(child);
				}
			}
		}
		return Entries.Count - before;
	}

	private static void DropTracking(string why)
	{
		Layer("drop " + why + " tracked=" + Entries.Count + " chunksHeld=" + ChunkDrawn.Count);
		Entries.Clear();
		ById.Clear();
		Cells.Clear();
		Visit.Clear();
		VisitSeen.Clear();
		Hot.Clear();
		HotSet.Clear();
		ChunkDrawn.Clear();
		ChunkOffAt.Clear();
		_cursor = 0;
		_hotCursor = 0;
		_haveView = false;
		_pendingFar = false;
		_added = 0;
		_stamp = 0;
	}

	private static void RestoreAndClear(string why)
	{
		int asleep = 0;
		int kept = 0;
		for (int i = 0; i < Entries.Count; i++)
		{
			Entry entry = Entries[i];
			if (entry != null && entry.Go != null && !entry.Go.activeSelf)
			{
				if (entry.Go.scene.buildIndex < 0)
				{
					kept++;
					continue;
				}
				if (ChunkSleep.IsAsleep(entry.Go) || !ChunkSleep.KeepsAwake(entry.Go))
				{
					kept++;
					continue;
				}
				entry.Go.SetActive(true);
				asleep++;
			}
		}
		Layer("reset " + why + " woke=" + asleep + " kept=" + kept + " tracked=" + Entries.Count);
		Entries.Clear();
		ById.Clear();
		Cells.Clear();
		Visit.Clear();
		VisitSeen.Clear();
		Hot.Clear();
		HotSet.Clear();
		ChunkDrawn.Clear();
		ChunkOffAt.Clear();
		_cursor = 0;
		_hotCursor = 0;
		_haveView = false;
		_pendingFar = false;
		_added = 0;
		_stamp = 0;
	}

	private static void NoteOwns(WorldGeneration world, bool owns)
	{
		int next = owns ? 1 : 0;
		if (_sawOwns == next)
			return;
		_sawOwns = next;
		Layer("visibility " + (owns ? "mod" : "stock") + " " + WorldLine(world));
	}

	private static void Beat(WorldGeneration world, bool owns, string phase)
	{
		if (Time.unscaledTime < _beatAt)
			return;
		_beatAt = Time.unscaledTime + 2f;
		int chunks = 0;
		int chunksOn = 0;
		TilemapRenderer[,] maps = world.renderChunks;
		if (maps != null)
		{
			int w = maps.GetLength(0);
			int h = maps.GetLength(1);
			for (int x = 0; x < w; x++)
			{
				for (int y = 0; y < h; y++)
				{
					TilemapRenderer renderer = maps[x, y];
					if (renderer == null)
						continue;
					chunks++;
					if (renderer.enabled)
						chunksOn++;
				}
			}
		}
		int active = 0;
		int asleep = 0;
		int enemies = 0;
		for (int i = 0; i < Entries.Count; i++)
		{
			Entry entry = Entries[i];
			if (entry == null || entry.Go == null)
				continue;
			if (entry.Enemy)
				enemies++;
			if (entry.Go.activeSelf)
				active++;
			else
				asleep++;
		}
		string who = "none";
		PlayerCamera cam = PlayerCamera.main;
		if (cam != null && cam.body != null)
		{
			Vector3 p = cam.body.transform.position;
			who = p.x.ToString("0.0") + "," + p.y.ToString("0.0");
		}
		Layer("beat " + phase
			+ " " + WorldLine(world)
			+ " owns=" + (owns ? 1 : 0)
			+ " player=" + who
			+ " anchor=" + _viewX.ToString("0.0") + "," + _viewY.ToString("0.0")
			+ " half=" + _viewH.ToString("0.0")
			+ " chunks=" + chunksOn + "/" + chunks
			+ " objects=" + active + "/" + Entries.Count
			+ " asleep=" + asleep
			+ " enemies=" + enemies
			+ " added=" + _added
			+ " far=" + (_pendingFar ? 1 : 0)
			+ " host=" + (_hosting ? 1 : 0)
			+ " prop=" + _prop.ToString("0.#")
			+ " stamp=" + _stamp + "/" + ((1) + Others.Count));
		_added = 0;
	}

	private static string WorldLine(WorldGeneration world)
	{
		int cw = 0;
		int ch = 0;
		if (world.renderChunks != null)
		{
			cw = world.renderChunks.GetLength(0);
			ch = world.renderChunks.GetLength(1);
		}
		return "depth=" + world.biomeDepth
			+ " gen=" + (world.generatingWorld ? 1 : 0)
			+ " exists=" + (world.worldExists ? 1 : 0)
			+ " chunks=" + cw + "x" + ch
			+ " map=" + world.width + "x" + world.height;
	}

	private static void Layer(string message)
	{
		if (Plugin.Logger != null)
			Plugin.Logger.LogInfo("cpuadv " + message);
	}

	private static Camera ViewCamera()
	{
		Camera cam = Camera.main;
		if (cam != null && cam.orthographic)
			return cam;
		if (!HostWatch.IsDedicated())
			return cam;
		PlayerCamera player = PlayerCamera.main;
		if (player == null)
			return cam;
		cam = player.GetComponent<Camera>();
		if (cam == null)
			cam = player.GetComponentInChildren<Camera>();
		return cam;
	}

	private static Vector2 Anchor(Camera cam)
	{
		// A dedicated host has no player body where the view is. The camera is the place being looked at.
		if (HostWatch.IsDedicated())
			return cam.transform.position;
		PlayerCamera player = PlayerCamera.main;
		if (player != null && player.body != null)
			return player.body.transform.position;
		return cam.transform.position;
	}

	private static Rect2 ViewOf(Camera cam, Vector2 center)
	{
		float live = cam.orthographicSize;
		float cap = ViewCap.HalfHeight(cam);
		float half = cap > 0.5f && live > cap ? cap : live;
		float hx = half * Mathf.Max(0.01f, cam.aspect) + LookAhead;
		float hy = half + LookAhead;
		return new Rect2(center.x - hx, center.y - hy, center.x + hx, center.y + hy).Expand(ViewPad);
	}

	private static bool TryCamera(out Rect2 view)
	{
		view = default;
		Camera cam = ViewCamera();
		if (cam == null || !cam.orthographic)
			return false;
		view = ViewOf(cam, Anchor(cam));
		return true;
	}

	private static int CellOf(float v)
	{
		return Mathf.FloorToInt(v / CellSize);
	}

	private static long Key(int x, int y)
	{
		return ((long)x << 32) ^ (uint)y;
	}

	private struct Rect2
	{
		internal float X0, Y0, X1, Y1;

		internal Rect2(float x0, float y0, float x1, float y1)
		{
			X0 = x0;
			Y0 = y0;
			X1 = x1;
			Y1 = y1;
		}

		internal Rect2 Expand(float pad)
		{
			return new Rect2(X0 - pad, Y0 - pad, X1 + pad, Y1 + pad);
		}

		internal bool Overlaps(float x0, float y0, float x1, float y1)
		{
			return x1 >= X0 && x0 <= X1 && y1 >= Y0 && y0 <= Y1;
		}

		internal float DistanceToBox(float x0, float y0, float x1, float y1)
		{
			float dx = 0f;
			if (x1 < X0)
				dx = X0 - x1;
			else if (x0 > X1)
				dx = x0 - X1;
			float dy = 0f;
			if (y1 < Y0)
				dy = Y0 - y1;
			else if (y0 > Y1)
				dy = y0 - Y1;
			if (dx == 0f && dy == 0f)
				return 0f;
			return Mathf.Sqrt(dx * dx + dy * dy);
		}
	}

	private static bool Held(Entry entry)
	{
		float wake = entry.Enemy ? _enemy : _prop;
		for (int i = 0; i < Guards.Count; i++)
		{
			if (Distance(entry, Guards[i]) <= wake)
				return true;
		}
		return false;
	}

	internal static bool HoldsOff(GameObject go)
	{
		if (go == null || !ById.TryGetValue(go.GetInstanceID(), out int index))
			return false;
		if (index < 0 || index >= Entries.Count)
			return false;
		Entry entry = Entries[index];
		return entry != null && entry.Parked;
	}

	private static bool Flip(Entry entry, bool wantOn, ref int budget)
	{
		if (entry.Go == null)
			return false;
		if (!ChunkSleep.KeepsAwake(entry.Go))
			return false;
		if (wantOn && ChunkSleep.IsAsleep(entry.Go))
			return false;
		bool on = entry.Go.activeSelf;
		if (wantOn)
		{
			entry.OffAt = -1f;
			entry.Parked = false;
			if (on)
				return false;
			entry.Go.SetActive(true);
			budget--;
			return true;
		}
		if (Held(entry))
		{
			entry.OffAt = -1f;
			return false;
		}
		if (!on)
		{
			entry.OffAt = -1f;
			return false;
		}
		if (entry.OffAt < 0f)
		{
			entry.OffAt = Time.unscaledTime;
			return false;
		}
		if (Time.unscaledTime - entry.OffAt < OffGrace)
			return false;
		entry.Go.SetActive(false);
		entry.Parked = true;
		entry.OffAt = -1f;
		budget--;
		return true;
	}
}
