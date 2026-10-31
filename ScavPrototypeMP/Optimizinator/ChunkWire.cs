using System;
using System.Reflection;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

// shared chunk writer. FixChunkBlock and FixChunkFluid own the Together methods.
internal static class ChunkWire
{
	internal const int Tiles = 32;
	internal const int TileBytes = Tiles * Tiles;
	internal static readonly byte[] Raw = new byte[TileBytes];

	private static int _faults;
	private static int _ready;
	private static MethodInfo _create;
	private static MethodInfo _putByte;
	private static MethodInfo _putBytes;
	private static MethodInfo _send;
	private static object _reliable;
	private static object _unreliable;

	internal static void Note(Exception ex)
	{
		_faults++;
		if (_faults <= 3)
			OptimizerMain.Log.LogWarning($"[KrokV5Opt] chunk deflate fell back: {ex.GetType().Name}: {ex.Message}");
	}

	// CreateWriter(in Enum) and Server_SendToClients(in DeliveryMethod, in writer, in knetid).
	// AccessTools.Method(typeof(Enum)) misses the by-ref arg. cache a miss or every chunk rescans Net and logs a Harmony warning.
	internal static bool CanWrite()
	{
		if (_create == null && _ready == 0)
		{
			_create = Find(typeof(Net), "CreateWriter", typeof(Enum).MakeByRefType());
			if (_create == null)
			{
				_ready = -1;
				OptimizerMain.Log.LogWarning("[KrokV5Opt] chunk writer missing CreateWriter(in Enum)");
			}
		}
		return _create != null;
	}

	internal static bool CanSend(object playerId)
	{
		if (!CanWrite() || _ready < 0)
			return false;
		if (_send == null)
		{
			_send = FindSend(playerId);
			if (_send == null)
			{
				_ready = -1;
				OptimizerMain.Log.LogWarning("[KrokV5Opt] chunk wire unresolved; original send stays");
				return false;
			}
			_ready = 1;
		}
		return true;
	}

	internal static object Begin(NetmsgId id)
	{
		if (_create == null)
			return null;
		return _create.Invoke(null, new object[] { (Enum)id });
	}

	internal static bool PutByte(object writer, byte value)
	{
		_putByte ??= writer.GetType().GetMethod("Put", new[] { typeof(byte) });
		if (_putByte == null)
			return false;
		_putByte.Invoke(writer, new object[] { value });
		return true;
	}

	internal static bool PutBytes(object writer, byte[] compressed)
	{
		_putBytes ??= writer.GetType().GetMethod("PutBytesWithLength", new[] { typeof(byte[]) });
		if (_putBytes == null)
			return false;
		_putBytes.Invoke(writer, new object[] { compressed });
		return true;
	}

	internal static bool Send(object writer, bool reliable, object playerId)
	{
		if (_send == null)
			return false;
		_send.Invoke(null, new[] { reliable ? _reliable : _unreliable, writer, playerId });
		return true;
	}

	private static MethodInfo Find(Type type, string name, Type only)
	{
		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (method.Name != name)
				continue;
			ParameterInfo[] pars = method.GetParameters();
			if (pars.Length == 1 && pars[0].ParameterType == only)
				return method;
		}
		return null;
	}

	private static MethodInfo FindSend(object playerId)
	{
		if (playerId == null)
			return null;
		Type delivery = AccessTools.TypeByName("LiteNetLib.DeliveryMethod");
		if (delivery == null)
			return null;
		_reliable = Enum.Parse(delivery, "ReliableUnordered");
		_unreliable = Enum.Parse(delivery, "Unreliable");
		Type idRef = playerId.GetType().MakeByRefType();
		foreach (MethodInfo method in typeof(Net).GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (method.Name != "Server_SendToClients")
				continue;
			ParameterInfo[] pars = method.GetParameters();
			if (pars.Length == 3 && pars[2].ParameterType == idRef)
				return method;
		}
		return null;
	}

	internal static void Pack(ushort[,] cells, int originX, int originY)
	{
		int n = 0;
		for (int row = 0; row < Tiles; row++)
		{
			for (int col = 0; col < Tiles; col++)
				Raw[n++] = (byte)cells[col + originX, row + originY];
		}
	}

	internal static void Pack(byte[,] cells, int originX, int originY)
	{
		int n = 0;
		for (int row = 0; row < Tiles; row++)
		{
			for (int col = 0; col < Tiles; col++)
				Raw[n++] = cells[col + originX, row + originY];
		}
	}
}
