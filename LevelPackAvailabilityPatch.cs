using System.Reflection;
using HarmonyLib;

namespace BSFixes
{
    [HarmonyPatch]
    internal static class LevelPackAvailabilityPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("LevelPackDetailViewController"), "RefreshAvailabilityAsync");
        }

        private static bool Prefix(object ____pack)
        {
            // DidActivate can run before SetData has supplied a pack. SetData
            // requests another refresh, so defer availability work until then.
            return ____pack != null;
        }
    }
}
