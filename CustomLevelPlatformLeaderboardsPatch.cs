using HarmonyLib;

namespace BSFixes
{
    // Custom levels have no platform leaderboard, so the Steam handler can only report
    // "Leaderboard id ... not found" for them. Answer those requests without the lookup.
    [HarmonyPatch(typeof(SteamPlatformLeaderboardsHandler))]
    internal static class CustomLevelPlatformLeaderboardsPatch
    {
        private static bool IsCustomLevel(string levelId)
        {
            return levelId != null && levelId.StartsWith("custom_level_", System.StringComparison.Ordinal);
        }

        [HarmonyPrefix]
        [HarmonyPatch(
            nameof(SteamPlatformLeaderboardsHandler.GetScores),
            new[] { typeof(BeatmapKey), typeof(int), typeof(int), typeof(PlatformLeaderboardsModel.ScoresScope), typeof(ulong), typeof(PlatformLeaderboardsModel.GetScoresCompletionHandler) },
            new[] { ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
        private static bool GetScoresPrefix(ref BeatmapKey beatmapKey, PlatformLeaderboardsModel.GetScoresCompletionHandler completionHandler, ref HMAsyncRequest __result)
        {
            if (!IsCustomLevel(beatmapKey.levelId))
            {
                return true;
            }

            __result = new HMAsyncRequest();
            completionHandler?.Invoke(PlatformLeaderboardsModel.GetScoresResult.Failed, null, -1);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(
            nameof(SteamPlatformLeaderboardsHandler.UploadScore),
            new[] { typeof(LeaderboardScoreUploader.ScoreData), typeof(PlatformLeaderboardsModel.UploadScoreCompletionHandler) })]
        private static bool UploadScorePrefix(LeaderboardScoreUploader.ScoreData scoreData, PlatformLeaderboardsModel.UploadScoreCompletionHandler completionHandler, ref HMAsyncRequest __result)
        {
            if (scoreData == null || !IsCustomLevel(scoreData.beatmapKey.levelId))
            {
                return true;
            }

            __result = new HMAsyncRequest();
            // Ok removes the score from the upload queue; a failure would retry it.
            completionHandler?.Invoke(PlatformLeaderboardsModel.UploadScoreResult.Ok);
            return false;
        }
    }
}
