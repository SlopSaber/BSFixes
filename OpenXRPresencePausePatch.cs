using HarmonyLib;

namespace BSFixes
{
    [HarmonyPatch(typeof(UnityXRSystemState), nameof(UnityXRSystemState.IsAppFocusCurrentlyLost))]
    internal static class OpenXRPresencePausePatch
    {
        private static void Postfix(UnityXRSystemState __instance, ref bool __result)
        {
            // Some OpenXR runtimes report no user presence while input focus
            // remains active. Focus loss still pauses the game normally.
            if (__result && __instance.hasInputFocus && !__instance.hasHmdMounted)
                __result = false;
        }
    }
}
