using System.Collections.Generic;
using UnityEngine;

namespace Together;

// Client-only. One line a second while remote bodies are being placed.
internal static class EnemyMoveProbe
{
	internal const int Interp = 0;
	internal const int RagdollSnap = 1;
	internal const int RagdollHold = 2;
	internal const int SimOut = 3;

	const float Period = 1f;

	struct Sample
	{
		internal Vector2 Raw;
		internal Vector2 Applied;
		internal float RawTime;
		internal int Gen;
		internal bool Have;
		internal bool Npc;
	}

	const byte Spider = 1;
	const byte LooseItem = 2;

	struct ObjSample
	{
		internal Vector2 Raw;
		internal Vector2 Applied;
		internal float RawTime;
		internal int Gen;
		internal byte Kind;
		internal bool Have;
		internal bool Moved;
	}

	sealed class KindAcc
	{
		internal int Notes, Raw, Flat, Stale, Hidden;
		internal float DtMin, DtMax, MaxStep, MaxGap, MaxPhys, MaxAge;
		internal string Worst = "";

		internal void Clear()
		{
			Notes = Raw = Flat = Stale = Hidden = 0;
			DtMin = DtMax = MaxStep = MaxGap = MaxPhys = MaxAge = 0f;
			Worst = "";
		}

		internal void Log(string kind, int n, int moving)
		{
			if (Notes == 0 || Plugin.log == null)
				return;
			float hz = n > 0 ? Notes / (float)n : 0f;
			float rawHz = moving > 0 ? Raw / (float)moving : 0f;
			if (log.verbose)
				Plugin.log.LogDebug(
					"[object move] " + kind
					+ " n=" + n
					+ " moving=" + moving
					+ " hz=" + hz.ToString("0.0")
					+ " rawHz=" + rawHz.ToString("0.0")
					+ " rawDt=" + (Raw > 0 ? DtMin.ToString("0.00") + ".." + DtMax.ToString("0.00") : "-")
					+ " stale=" + Stale
					+ " flat=" + Flat
					+ " hidden=" + Hidden
					+ " age=" + MaxAge.ToString("0.00")
					+ " maxStep=" + MaxStep.ToString("0.00")
					+ " maxGap=" + MaxGap.ToString("0.00")
					+ " maxPhys=" + MaxPhys.ToString("0.00")
					+ " worst=" + Worst);
		}
	}

	static readonly Dictionary<int, Sample> Prev = new Dictionary<int, Sample>(32);
	static readonly Dictionary<int, ObjSample> Objs = new Dictionary<int, ObjSample>(64);
	static readonly KindAcc SpiderAcc = new KindAcc();
	static readonly KindAcc ItemAcc = new KindAcc();
	static readonly List<int> Drop = new List<int>(32);
	static float _next = -1f;
	static int _window;
	static int _notes;
	static int _interp, _ragdollSnap, _ragdollHold, _simOut, _flat;
	static int _rawChanges;
	static int _npcRaw;
	static float _rawDtMin, _rawDtMax;
	static float _npcDtMin, _npcDtMax;
	static float _maxStep, _maxGap, _maxPhys;
	static string _worst = "";

	internal static void Note(SyncBody sb, int branch, Vector2 applied)
	{
		if (Net.IsServer || sb == null || sb.nb == null || sb.nb.body == null || sb.nb.IsBodyLocal())
			return;

		float now = Time.unscaledTime;
		if (_next < 0f)
			_next = now + Period;
		if (now >= _next)
			Flush(now);

		Vector2 raw = sb._position.Value;
		Vector2 before = sb.nb.body.transform.position;
		int id = sb.Id;
		float step = 0f;
		float phys = 0f;
		float rawDt = -1f;

		bool npc = !sb.nb.IsPlayer;
		if (Prev.TryGetValue(id, out Sample prev) && prev.Have)
		{
			step = Vector2.Distance(applied, prev.Applied);
			phys = Vector2.Distance(before, prev.Applied);
			if (Vector2.SqrMagnitude(raw - prev.Raw) > 0.0001f)
			{
				_rawChanges++;
				rawDt = now - prev.RawTime;
				if (_rawChanges == 1 || rawDt < _rawDtMin)
					_rawDtMin = rawDt;
				if (rawDt > _rawDtMax)
					_rawDtMax = rawDt;
				if (npc)
				{
					_npcRaw++;
					if (_npcRaw == 1 || rawDt < _npcDtMin)
						_npcDtMin = rawDt;
					if (rawDt > _npcDtMax)
						_npcDtMax = rawDt;
				}
			}
		}

		float gap = Vector2.Distance(raw, applied);
		if (gap < 0.02f)
			_flat++;
		if (gap > _maxGap)
			_maxGap = gap;

		_notes++;
		if (branch == Interp)
			_interp++;
		else if (branch == RagdollSnap)
			_ragdollSnap++;
		else if (branch == RagdollHold)
			_ragdollHold++;
		else
			_simOut++;

		if (phys > _maxPhys)
			_maxPhys = phys;
		if (step > _maxStep)
		{
			_maxStep = step;
			_worst = sb.nb + " " + BranchName(branch)
				+ " step=" + step.ToString("0.00")
				+ " gap=" + gap.ToString("0.00")
				+ " phys=" + phys.ToString("0.00")
				+ (rawDt >= 0f ? " rawDt=" + rawDt.ToString("0.00") : "");
		}

		Sample next = prev;
		next.Applied = applied;
		next.Gen = _window;
		next.Have = true;
		next.Npc = npc;
		if (rawDt >= 0f || !prev.Have)
		{
			next.Raw = raw;
			next.RawTime = now;
		}
		Prev[id] = next;
	}

	internal static void NoteObject(SyncInfo si, Vector2 applied, Vector2 before, bool stale)
	{
		if (Net.IsServer || si == null || si.go == null)
			return;

		byte kind;
		if (si.spider != null)
			kind = Spider;
		else if (si.item != null)
			kind = LooseItem;
		else
			return;

		float now = Time.unscaledTime;
		if (_next < 0f)
			_next = now + Period;
		if (now >= _next)
			Flush(now);

		KindAcc acc = kind == Spider ? SpiderAcc : ItemAcc;
		Vector2 raw = si._position.Value;
		int id = si.Id;
		float step = 0f;
		float phys = 0f;
		float rawDt = -1f;
		Objs.TryGetValue(id, out ObjSample prev);
		bool same = prev.Have && prev.Kind == kind;
		if (same)
		{
			step = Vector2.Distance(applied, prev.Applied);
			phys = Vector2.Distance(before, prev.Applied);
			if (Vector2.SqrMagnitude(raw - prev.Raw) > 0.0001f)
			{
				rawDt = now - prev.RawTime;
				acc.Raw++;
				if (acc.Raw == 1 || rawDt < acc.DtMin)
					acc.DtMin = rawDt;
				if (rawDt > acc.DtMax)
					acc.DtMax = rawDt;
			}
		}

		float gap = Vector2.Distance(raw, applied);
		if (gap < 0.02f)
			acc.Flat++;
		if (gap > acc.MaxGap)
			acc.MaxGap = gap;
		acc.Notes++;
		if (stale)
			acc.Stale++;
		if (si.tracker != null && !si.tracker.isVisible)
			acc.Hidden++;
		float age = (float)(Time.realtimeSinceStartupAsDouble - si.lastPositionUpdateTime);
		if (age > acc.MaxAge)
			acc.MaxAge = age;
		if (phys > acc.MaxPhys)
			acc.MaxPhys = phys;
		if (step > acc.MaxStep)
		{
			acc.MaxStep = step;
			acc.Worst = Label(si, kind)
				+ " step=" + step.ToString("0.00")
				+ " gap=" + gap.ToString("0.00")
				+ " phys=" + phys.ToString("0.00")
				+ (stale ? " stale" : "")
				+ (rawDt >= 0f ? " rawDt=" + rawDt.ToString("0.00") : "");
		}

		ObjSample next = prev;
		next.Applied = applied;
		next.Kind = kind;
		next.Gen = _window;
		next.Have = true;
		next.Moved = same && prev.Gen == _window && prev.Moved;
		if (rawDt >= 0f)
			next.Moved = true;
		if (rawDt >= 0f || !same)
		{
			next.Raw = raw;
			next.RawTime = now;
		}
		Objs[id] = next;
	}

	static void Flush(float now)
	{
		int bodies = 0;
		int npcs = 0;
		Drop.Clear();
		foreach (KeyValuePair<int, Sample> pair in Prev)
		{
			if (pair.Value.Gen == _window && pair.Value.Have)
			{
				bodies++;
				if (pair.Value.Npc)
					npcs++;
			}
			else
				Drop.Add(pair.Key);
		}
		for (int i = 0; i < Drop.Count; i++)
			Prev.Remove(Drop[i]);

		if (_notes > 0 && Plugin.log != null)
		{
			float hz = bodies > 0 ? _notes / (float)bodies : 0f;
			if (log.verbose)
				Plugin.log.LogDebug(
					"[enemy move] remote=" + bodies
					+ " hz=" + hz.ToString("0.0")
					+ " interp=" + _interp
					+ " ragdollSnap=" + _ragdollSnap
					+ " ragdollHold=" + _ragdollHold
					+ " simOut=" + _simOut
					+ " flat=" + _flat
					+ " rawChanges=" + _rawChanges
					+ " rawDt=" + (_rawChanges > 0 ? _rawDtMin.ToString("0.00") + ".." + _rawDtMax.ToString("0.00") : "-")
					+ (npcs > 0
						? " npc=" + npcs
							+ " npcRaw=" + _npcRaw
							+ " npcRawDt=" + (_npcRaw > 0 ? _npcDtMin.ToString("0.00") + ".." + _npcDtMax.ToString("0.00") : "-")
						: "")
					+ " maxStep=" + _maxStep.ToString("0.00")
					+ " maxGap=" + _maxGap.ToString("0.00")
					+ " maxPhys=" + _maxPhys.ToString("0.00")
					+ " worst=" + _worst);
		}

		int spiders = 0;
		int spiderMoving = 0;
		int items = 0;
		int itemMoving = 0;
		Drop.Clear();
		foreach (KeyValuePair<int, ObjSample> pair in Objs)
		{
			if (pair.Value.Gen == _window && pair.Value.Have)
			{
				if (pair.Value.Kind == Spider)
				{
					spiders++;
					if (pair.Value.Moved)
						spiderMoving++;
				}
				else
				{
					items++;
					if (pair.Value.Moved)
						itemMoving++;
				}
			}
			else
				Drop.Add(pair.Key);
		}
		for (int i = 0; i < Drop.Count; i++)
			Objs.Remove(Drop[i]);
		SpiderAcc.Log("spider", spiders, spiderMoving);
		ItemAcc.Log("item", items, itemMoving);

		_next = now + Period;
		_window++;
		_notes = _interp = _ragdollSnap = _ragdollHold = _simOut = _flat = _rawChanges = _npcRaw = 0;
		_rawDtMin = _rawDtMax = _npcDtMin = _npcDtMax = 0f;
		_maxStep = _maxGap = _maxPhys = 0f;
		_worst = "";
		SpiderAcc.Clear();
		ItemAcc.Clear();
	}

	static string Label(SyncInfo si, byte kind)
	{
		string name = kind == Spider ? "spider" : "item";
		GameObject go = si.go;
		if (go != null)
		{
			if (kind == LooseItem && go.TryGetComponent(out Item item) && !string.IsNullOrEmpty(item.id))
				name = item.id;
			else if (go.TryGetComponent(out BuildingEntity building) && !string.IsNullOrEmpty(building.id))
				name = building.id;
		}
		return name + " #" + si.Id;
	}

	static string BranchName(int branch)
	{
		if (branch == Interp)
			return "interp";
		if (branch == RagdollSnap)
			return "ragdollSnap";
		if (branch == RagdollHold)
			return "ragdollHold";
		return "simOut";
	}
}
