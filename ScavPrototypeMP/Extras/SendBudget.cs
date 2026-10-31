using LiteEntitySystem;
using LiteEntitySystem.Internal;
using UnityEngine;

namespace Together;

// bodies and anything on a body stay every tick.
// a ground item or building that is not near a player waits three ticks out of four.
// les only asks this for an active player. a joiner still on a baseline gets everything.
internal static class SendBudget
{
	internal static bool Allow(InternalEntity entity, NetPlayer player)
	{
		if (player == null || entity == null || Keep(entity))
			return true;
		ushort tick = entity.EntityManager != null ? entity.EntityManager.Tick : (ushort)0;
		return (entity.Id & 3) == (tick & 3);
	}

	static bool Keep(InternalEntity entity)
	{
		if (entity is not SyncInfo info)
			return true;
		if (info.tracker != null && info.tracker.isNearby)
			return true;
		if (info.go != null && info.go.transform.parent != null && info.go.transform.parent.GetComponentInParent<Body>() != null)
			return true;
		return false;
	}
}
