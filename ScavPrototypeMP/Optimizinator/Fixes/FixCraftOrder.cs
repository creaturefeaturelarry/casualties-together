using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// host Recipe_TryMake already spends the items and spawns the result, then TellTheServerThatICrafted asks the server to craft again.
/// skip that second request when Net.IsServer. clients still send it.
/// </summary>
internal static class FixCraftOrder
{
	// host skip on the client send, plus a log on the server craft result.
	internal static void NoteHost()
	{
		LogLocal();
	}

	// server side of the same craft. accepted is whether Server_CraftItemForClient took it.
	internal static void LogResult(NetBody pb, Recipe recipe, bool accepted)
	{
		Write(recipe, pb != null && pb.plr != null ? pb.plr.playerName : "?", pb != null && pb.IsLocal, accepted, null);
	}

	// CraftCommit passes why the server accepted or refused. the three-arg call leaves that blank.
	internal static void LogResult(NetBody pb, Recipe recipe, bool accepted, string reason)
	{
		Write(recipe, pb != null && pb.plr != null ? pb.plr.playerName : "?", pb != null && pb.IsLocal, accepted, reason);
	}

	// host never hits LogResult, so record the local craft from the camera's selected recipe.
	private static void LogLocal()
	{
		Recipe recipe = null;
		PlayerCamera cam = PlayerCamera.main;
		if (cam != null && cam.selectedRecipe >= 0 && cam.selectedRecipe < Recipes.recipes.Count)
			recipe = Recipes.recipes[cam.selectedRecipe];
		Write(recipe, "host", true, true, null);
	}

	// one line per recipe+result, deduped for 2 seconds inside BugLog.
	private static void Write(Recipe recipe, string who, bool local, bool accepted, string reason)
	{
		string name = recipe != null ? recipe.fullName : "?";
		int index = recipe != null ? recipe.index : -1;
		string why = string.IsNullOrEmpty(reason) ? "" : " " + reason;
		BugLog.Write("craft:" + index + ":" + accepted, "craft recipe=" + index + " " + name + " by=" + who + " local=" + local + " accepted=" + accepted + why);
	}
}
