using System;
using System.Threading.Tasks;
using HarmonyLib;

namespace BSFixes
{
    // Pack cells ask for the entitlement of every level in a pack, one call per level.
    // Custom levels and packs are always owned; answer them with an ordinal prefix check
    // and one shared completed task instead of a culture-sensitive comparison and a new
    // task per level.
    [HarmonyPatch(typeof(BeatmapLevelsEntitlementModel))]
    internal static class CustomLevelEntitlementPatch
    {
        private static readonly Task<EntitlementStatus> Owned = Task.FromResult(EntitlementStatus.Owned);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(BeatmapLevelsEntitlementModel.GetLevelEntitlementStatusAsync))]
        private static bool Level(string levelId, ref Task<EntitlementStatus> __result)
            => !TryOwned(levelId, "custom_level_", ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(BeatmapLevelsEntitlementModel.GetPackEntitlementStatusAsync))]
        private static bool Pack(string levelPackId, ref Task<EntitlementStatus> __result)
            => !TryOwned(levelPackId, "custom_levelpack_", ref __result);

        private static bool TryOwned(string id, string prefix, ref Task<EntitlementStatus> result)
        {
            if (id == null || !id.StartsWith(prefix, StringComparison.Ordinal)) return false;
            result = Owned;
            return true;
        }
    }
}
