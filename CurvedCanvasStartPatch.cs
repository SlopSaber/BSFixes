using System.Collections;
using HarmonyLib;
using HMUI;
using UnityEngine;

namespace BSFixes
{
    [HarmonyPatch(typeof(CurvedCanvasSettings), "Start")]
    internal static class CurvedCanvasStartPatch
    {
        private static bool Prefix(CurvedCanvasSettings __instance)
        {
            if (__instance.canvas != null)
                return true;

            // The original Start only disables raycasts and sets this channel.
            // Wait if the Canvas is not active yet, then finish that setup.
            __instance.raycastTarget = false;
            __instance.StartCoroutine(EnableShaderChannelWhenCanvasIsReady(__instance));
            return false;
        }

        private static IEnumerator EnableShaderChannelWhenCanvasIsReady(CurvedCanvasSettings settings)
        {
            while (settings != null)
            {
                Canvas canvas = settings.canvas;
                if (canvas != null)
                {
                    canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord2;
                    yield break;
                }

                yield return null;
            }
        }
    }
}
