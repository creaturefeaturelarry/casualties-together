using System;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace KrokV5Optimization;

internal static class ItemSyncId
{
	private static MethodInfo _tryGet;
	private static PropertyInfo _syncId;
	private static FieldInfo _rawId;
	private static bool _looked;

	internal static int Of(Item item)
	{
		if (item == null)
			return 0;
		if (!_looked)
		{
			_looked = true;
			Type ext = AccessTools.TypeByName("Together.Util_MPExtensions");
			Type sync = AccessTools.TypeByName("Together.SyncInfo");
			if (ext != null && sync != null)
			{
				_tryGet = ext.GetMethod(
					"TryGetSyncInfo",
					BindingFlags.Public | BindingFlags.Static,
					null,
					new[] { typeof(Item), sync.MakeByRefType() },
					null);
			}
			if (_tryGet == null)
				OptimizerMain.Log.LogWarning("[KrokV5Opt] sync id missing TryGetSyncInfo(Item)");
		}
		if (_tryGet == null)
			return 0;
		try
		{
			object[] args = { item, null };
			if (!(_tryGet.Invoke(null, args) is true) || args[1] == null)
				return 0;
			if (_syncId == null)
				_syncId = args[1].GetType().GetProperty("syncId");
			object id = _syncId != null ? _syncId.GetValue(args[1]) : args[1].GetType().GetField("syncId")?.GetValue(args[1]);
			if (id == null)
				return 0;
			if (_rawId == null)
				_rawId = id.GetType().GetField("id");
			object raw = _rawId != null ? _rawId.GetValue(id) : id;
			return raw is ushort n ? n : 0;
		}
		catch (Exception ex)
		{
			_tryGet = null;
			OptimizerMain.Log.LogWarning("[KrokV5Opt] sync id failed: " + ex.GetType().Name);
			return 0;
		}
	}
}
