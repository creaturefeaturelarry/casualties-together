using System;
using System.Reflection;
using UnityEngine;

namespace Together;

/// <summary>
/// Cull window stays at the game's normal camera size. Zoom-out mods grow orthographicSize;
/// that extra view stays empty.
/// </summary>
internal static class ViewCap
{
	private static bool _looked;
	private static FieldInfo _normalSize;
	private static float _cached;

	internal static float HalfHeight(Camera cam)
	{
		float configured = CpuAdvConfig.MaxViewHalfHeight != null ? CpuAdvConfig.MaxViewHalfHeight.Value : 0f;
		if (configured > 0.5f)
			return configured;
		if (_cached > 0.5f)
			return _cached;

		float stock = StockSize();
		if (stock > 0.5f)
		{
			_cached = stock;
			return _cached;
		}
		if (cam != null && cam.orthographicSize > 0.5f)
			_cached = cam.orthographicSize;
		return _cached;
	}

	private static float StockSize()
	{
		if (!_looked)
			FindField();
		if (_normalSize == null)
			return 0f;

		UnityEngine.Object[] found;
		try
		{
			found = UnityEngine.Object.FindObjectsOfType(_normalSize.DeclaringType);
		}
		catch (Exception)
		{
			return 0f;
		}
		if (found == null)
			return 0f;
		for (int i = 0; i < found.Length; i++)
		{
			if (found[i] == null)
				continue;
			object raw;
			try
			{
				raw = _normalSize.GetValue(found[i]);
			}
			catch (Exception)
			{
				continue;
			}
			if (raw is float value && value > 0.5f)
				return value;
		}
		return 0f;
	}

	private static void FindField()
	{
		_looked = true;
		Assembly[] assemblies;
		try
		{
			assemblies = AppDomain.CurrentDomain.GetAssemblies();
		}
		catch (Exception)
		{
			return;
		}
		for (int i = 0; i < assemblies.Length; i++)
		{
			Type[] types;
			try
			{
				types = assemblies[i].GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}
			catch (Exception)
			{
				continue;
			}
			if (types == null)
				continue;
			for (int t = 0; t < types.Length; t++)
			{
				Type type = types[t];
				if (type == null)
					continue;
				FieldInfo field = type.GetField("normalOrthographicSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null && field.FieldType == typeof(float))
				{
					_normalSize = field;
					return;
				}
			}
		}
	}
}
