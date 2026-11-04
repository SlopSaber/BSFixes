using System.Collections.Generic;
using HarmonyLib;

namespace BSFixes
{
    // LevelCollectionTableView.SetData rebuilds the alphabet scrollbar by sorting
    // the whole level list with culture-aware string comparison on the main
    // thread. Opening the same collection again passes the same levels in the
    // same order, so the previous sorted list and index are reused.
    [HarmonyPatch(typeof(AlphabetScrollbarInfoBeatmapLevelHelper), nameof(AlphabetScrollbarInfoBeatmapLevelHelper.CreateData))]
    internal static class AlphabetScrollbarSortCachePatch
    {
        private static BeatmapLevel[] cachedInput;
        private static IReadOnlyList<AlphabetScrollInfo.Data> cachedData;
        private static IReadOnlyList<BeatmapLevel> cachedSorted;

        private static bool Prefix(IReadOnlyList<BeatmapLevel> beatmapLevels, bool sortBeatmapLevels,
            out IReadOnlyList<BeatmapLevel> sortedBeatmapLevels, ref IReadOnlyList<AlphabetScrollInfo.Data> __result)
        {
            sortedBeatmapLevels = beatmapLevels;
            if (!sortBeatmapLevels || beatmapLevels == null || beatmapLevels.Count == 0 || !Matches(beatmapLevels))
                return true;
            sortedBeatmapLevels = cachedSorted;
            __result = cachedData;
            return false;
        }

        private static void Postfix(IReadOnlyList<BeatmapLevel> beatmapLevels, bool sortBeatmapLevels,
            IReadOnlyList<BeatmapLevel> sortedBeatmapLevels, IReadOnlyList<AlphabetScrollInfo.Data> __result)
        {
            if (!sortBeatmapLevels || beatmapLevels == null || beatmapLevels.Count == 0 || __result == null)
                return;
            if (ReferenceEquals(__result, cachedData))
                return;
            var input = new BeatmapLevel[beatmapLevels.Count];
            for (int i = 0; i < input.Length; i++)
                input[i] = beatmapLevels[i];
            cachedInput = input;
            cachedData = __result;
            cachedSorted = sortedBeatmapLevels;
        }

        private static bool Matches(IReadOnlyList<BeatmapLevel> levels)
        {
            var input = cachedInput;
            if (input == null || input.Length != levels.Count)
                return false;
            for (int i = 0; i < input.Length; i++)
            {
                if (!ReferenceEquals(input[i], levels[i]))
                    return false;
            }
            return true;
        }
    }
}
