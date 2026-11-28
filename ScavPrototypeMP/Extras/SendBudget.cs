using LiteEntitySystem;
using LiteEntitySystem.Internal;

namespace Together;

// every diff goes out the tick it is built. holding a grab for later ticks
// made the client replay the whole move and fling the body on release.
internal static class SendBudget
{
	internal static bool Allow(InternalEntity entity, NetPlayer player)
	{
		return true;
	}
}
