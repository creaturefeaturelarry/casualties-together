using HarmonyLib;

namespace Together.UI.TeammateList;

[HarmonyPatch(typeof(NetBody), nameof(NetBody.SetUnchipped))]
internal static class ChipCachePatch {
    private static void Postfix(NetBody __instance, bool unchipped) {
        if (__instance == null) {
            return;
        }
        TeammateListHost.NoteUnchipped(__instance.plr, unchipped);
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetUnchipped))]
internal static class WorldUnchippedChipCachePatch {
    private static void Postfix(bool unchipped) {
        TeammateListHost.ViewerUnchipped = unchipped;
        TeammateListHost.Roster?.ResampleChipFlags();
    }
}