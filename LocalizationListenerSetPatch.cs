using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BGLib.Polyglot;
using HarmonyLib;

namespace BSFixes
{
    // LocalizationModel keeps every localized text in one multicast delegate.
    // AddOnLocalizeEvent removes and re-adds the listener and RemoveOnLocalizeEvent
    // removes it, and each of those copies the whole invocation list. With the
    // number of localized texts mods create, destroying one text costs tens of
    // milliseconds. A set gives the same membership in constant time.
    [HarmonyPatch(typeof(LocalizationModel))]
    internal static class LocalizationListenerSetPatch
    {
        private static readonly ConditionalWeakTable<LocalizationModel, HashSet<ILocalize>> Listeners = new ConditionalWeakTable<LocalizationModel, HashSet<ILocalize>>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LocalizationModel.AddOnLocalizeEvent))]
        private static bool Add(LocalizationModel __instance, ILocalize localize)
        {
            Listeners.GetOrCreateValue(__instance).Add(localize);
            localize.OnLocalize(__instance);
            return false;
        }

        // Listeners registered before this patch are still in the original delegate.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(LocalizationModel.RemoveOnLocalizeEvent))]
        private static bool Remove(LocalizationModel __instance, ILocalize localize)
            => !(Listeners.TryGetValue(__instance, out var listeners) && listeners.Remove(localize));

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LocalizationModel.SelectedLanguage), MethodType.Setter)]
        private static void BeforeLanguage(LocalizationModel __instance, out LocalizationLanguage __state)
            => __state = __instance.SelectedLanguage;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(LocalizationModel.SelectedLanguage), MethodType.Setter)]
        private static void AfterLanguage(LocalizationModel __instance, LocalizationLanguage __state)
        {
            if (__instance.SelectedLanguage == __state || !Listeners.TryGetValue(__instance, out var listeners)) return;
            foreach (ILocalize localize in new List<ILocalize>(listeners))
            {
                if (localize is UnityEngine.Object unityObject && unityObject == null)
                {
                    listeners.Remove(localize);
                    continue;
                }
                localize.OnLocalize(__instance);
            }
        }
    }
}
