using LiteEntitySystem;
using LiteEntitySystem.Internal;
using UnityEngine;

namespace Together;

// bodies and anything carried on a body stay every tick.
// a loose SyncInfo is sent only inside that player's view radius.
// les only asks this for an active player. a joiner still on a baseline gets everything.
internal static class SendBudget
{
	internal static bool Allow(InternalEntity entity, NetPlayer player)
	{
		if (player == null || entity == null)
			return true;
		if (entity is not SyncInfo info || OnBody(info))
		{
			SyncStep.Arm(entity, player);
			return true;
		}
		bool send = SyncStep.InView(info, player);
		SyncStep.Dist(send);
		if (send)
			SyncStep.Arm(entity, player);
		return send;
	}

	static bool OnBody(SyncInfo info)
	{
		return info.go != null && info.go.transform.parent != null && info.go.transform.parent.GetComponentInParent<Body>() != null;
	}
}
