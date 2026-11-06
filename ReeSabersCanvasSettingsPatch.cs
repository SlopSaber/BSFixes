using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HMUI;
using UnityEngine;

namespace BSFixes
{
    [HarmonyPatch(typeof(CurvedCanvasSettingsHelper), nameof(CurvedCanvasSettingsHelper.GetCurvedCanvasSettings))]
    internal static class ReeSabersCanvasSettingsPatch
    {
        private static readonly FieldInfo Cache = AccessTools.Field(typeof(CurvedCanvasSettingsHelper), "_curvedCanvasCache");
        private static readonly bool IsSupportedVersion = Application.version.StartsWith("1.45.3", StringComparison.Ordinal);

        private static void Postfix(CurvedCanvasSettingsHelper __instance, Canvas canvas, ref CurvedCanvasSettings __result)
        {
            if (__result != null || canvas == null || !IsSupportedVersion)
                return;

            Canvas root = canvas.rootCanvas;
            if (root == null || root.name != "BSMLFloatingScreen") return;
            ViewController view = root.GetComponentInChildren<ViewController>(true);
            if (view == null || view.GetType().FullName != "ReeSabers.EditorScreenViewController") return;

            __result = root.GetComponent<CurvedCanvasSettings>();
            if (__result == null)
            {
                __result = root.gameObject.AddComponent<CurvedCanvasSettings>();
                Debug.Log("[BSFixes] Restored curvature settings for ReeSabers editor screen.");
            }
            // Existing helpers can retain a null entry from before the screen gained curvature.
            __instance.Reset();
            if (Cache?.GetValue(null) is Dictionary<Canvas, CurvedCanvasSettings> cache)
            {
                cache.Remove(canvas);
                cache.Remove(root);
            }
        }
    }
}
