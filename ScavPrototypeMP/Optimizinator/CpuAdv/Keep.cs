using UnityEngine;

namespace Together;

internal static class Keep
{
	internal static bool Reject(GameObject go)
	{
		if (go == null)
			return true;
		for (Transform cur = go.transform; cur != null; cur = cur.parent)
		{
			if (cur.GetComponent<Body>() != null)
				return true;
			if (cur.GetComponent<Limb>() != null)
				return true;
			if (cur.GetComponent<BlueHeart>() != null)
				return true;
			if (cur.GetComponent<ElderThornbackBehaviour>() != null)
				return true;
			if (NameKept(cur.name))
				return true;
			BuildingEntity build = cur.GetComponent<BuildingEntity>();
			if (build != null && NameKept(build.id))
				return true;
		}
		return false;
	}

	internal static bool UsesWideRange(GameObject go)
	{
		if (go == null)
			return false;
		if (go.GetComponent<SpiderHandler>() != null)
			return true;
		if (go.GetComponent<DamagingCrate>() != null)
			return true;
		if (go.GetComponent<TurretScript>() != null)
			return true;
		if (go.GetComponent<CeilingTurret>() != null)
			return true;
		if (go.GetComponent<StalactiteDropper>() != null)
			return true;
		if (go.GetComponent<SoundCannon>() != null)
			return true;
		if (HazardName(go.name))
			return true;
		BuildingEntity build = go.GetComponent<BuildingEntity>();
		if (build != null && HazardName(build.id))
			return true;
		Item item = go.GetComponent<Item>();
		if (item != null && HazardName(item.id))
			return true;
		return false;
	}

	internal static bool IsEnemy(GameObject go)
	{
		return UsesWideRange(go);
	}

	internal static bool IsCullable(GameObject go)
	{
		if (go == null || Reject(go))
			return false;
		if (go.GetComponent<SpiderHandler>() != null)
			return true;
		if (go.GetComponent<BuildingEntity>() != null)
			return true;
		if (go.GetComponent<Item>() != null)
			return true;
		if (go.name.IndexOf("wallhole", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		return false;
	}

	internal static bool IsCullRoot(Transform t)
	{
		if (t == null || !IsCullable(t.gameObject))
			return false;
		for (Transform p = t.parent; p != null; p = p.parent)
		{
			if (Reject(p.gameObject))
				return false;
			if (IsCullable(p.gameObject))
				return false;
		}
		return true;
	}

	private static bool NameKept(string value)
	{
		if (string.IsNullOrEmpty(value))
			return false;
		if (value.IndexOf("thornbackelder", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		if (value.IndexOf("blueheart", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		return false;
	}

	private static bool HazardName(string value)
	{
		if (string.IsNullOrEmpty(value))
			return false;
		if (value.IndexOf("stalactite", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		if (value.IndexOf("skullcrusher", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		if (value.IndexOf("turret", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		if (value.IndexOf("dynamite", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		if (value.IndexOf("soundcannon", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return true;
		return false;
	}
}
