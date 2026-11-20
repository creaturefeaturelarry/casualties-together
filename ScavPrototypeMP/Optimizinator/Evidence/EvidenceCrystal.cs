using System.Collections.Generic;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

// one line a second. host write rate against the client copy, so a laggy manifestation shows up in the log.
internal static class EvidenceCrystal
{
	struct Sample
	{
		internal int Id;
		internal int Writes;
		internal float Gap;
		internal float Err;
		internal float Age;
		internal Vector2 Pos;
		internal Vector2 Raw;
		internal float Last;
		internal bool Stuck;
		internal bool Static;
		internal bool Have;
	}

	static readonly Dictionary<int, Sample> Rows = new Dictionary<int, Sample>(8);
	static float _next = -1f;

	internal static void Host(SyncInfo si)
	{
		if (si == null || si.go == null)
			return;
		Note(si, si.go.transform.position, si.go.transform.position, true);
	}

	internal static void Client(SyncInfo si)
	{
		if (si == null || si.go == null)
			return;
		Vector2 raw = si._position.Value;
		Note(si, si.go.transform.position, raw, false);
	}

	static void Note(SyncInfo si, Vector2 pos, Vector2 raw, bool host)
	{
		float now = Time.unscaledTime;
		if (_next < 0f)
			_next = now + 1f;
		int id = si.syncId.id;
		Sample row;
		if (!Rows.TryGetValue(id, out row))
			row = new Sample { Id = id };
		if (row.Have)
		{
			float gap = now - row.Last;
			if (gap > row.Gap)
				row.Gap = gap;
		}
		row.Have = true;
		row.Last = now;
		row.Writes++;
		row.Pos = pos;
		row.Raw = raw;
		float err = Vector2.Distance(pos, raw);
		if (err > row.Err)
			row.Err = err;
		float age = (float)(Time.realtimeSinceStartupAsDouble - si.lastPositionUpdateTime);
		if (age > row.Age)
			row.Age = age;
		if (si.go.TryGetComponent(out CrystalEnemy enemy))
		{
			row.Stuck = TraverseStuck(enemy);
		}
		if (si.go.TryGetComponent(out Rigidbody2D rb))
			row.Static = rb.bodyType != RigidbodyType2D.Dynamic || !rb.simulated;
		Rows[id] = row;
		if (now >= _next)
			Flush(now, host);
	}

	static bool TraverseStuck(CrystalEnemy enemy)
	{
		object value = HarmonyLib.Traverse.Create(enemy).Field("stuck").GetValue();
		return value is bool bit && bit;
	}

	static void Flush(float now, bool host)
	{
		_next = now + 1f;
		if (Rows.Count == 0 || Plugin.log == null)
			return;
		Sample worst = default;
		int n = 0;
		int writes = 0;
		foreach (KeyValuePair<int, Sample> pair in Rows)
		{
			Sample row = pair.Value;
			if (!row.Have)
				continue;
			n++;
			writes += row.Writes;
			float score = host ? row.Gap : row.Err + row.Age;
			float worstScore = host ? worst.Gap : worst.Err + worst.Age;
			if (n == 1 || score > worstScore)
				worst = row;
		}
		if (n == 0)
			return;
		float hz = writes / (float)n;
		log.l("[findthebuginator] crystal side=" + (host ? "host" : "client")
			+ " n=" + n
			+ " hz=" + hz.ToString("0.0")
			+ " gap=" + worst.Gap.ToString("0.000")
			+ " err=" + worst.Err.ToString("0.00")
			+ " age=" + worst.Age.ToString("0.00")
			+ " worst=" + worst.Id
			+ " pos=" + worst.Pos.x.ToString("0.0") + "," + worst.Pos.y.ToString("0.0")
			+ " raw=" + worst.Raw.x.ToString("0.0") + "," + worst.Raw.y.ToString("0.0")
			+ " stuck=" + (worst.Stuck ? 1 : 0)
			+ " frozen=" + (worst.Static ? 1 : 0), false);
		Rows.Clear();
	}
}
