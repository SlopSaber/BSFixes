using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BSFixes
{
    // MediaAsyncLoader.LoadSpriteAsync copies every downloaded image into a
    // mipmapped texture with SetPixels(GetPixels()). That builds a managed
    // Color[] of 16 bytes per pixel (about 4 MB for a 512x512 cover) on the
    // main thread. Copy the raw mip 0 bytes instead; Apply still builds mips.
    [HarmonyPatch]
    internal static class MediaSpritePixelCopyPatch
    {
        private static readonly MethodInfo GetPixels = AccessTools.Method(typeof(Texture2D), nameof(Texture2D.GetPixels), new[] { typeof(int) });
        private static readonly MethodInfo SetPixels = AccessTools.Method(typeof(Texture2D), nameof(Texture2D.SetPixels), new[] { typeof(Color[]), typeof(int) });

        [ThreadStatic]
        private static Texture2D source;

        private static MethodBase TargetMethod()
        {
            MethodInfo loader = AccessTools.Method(typeof(MediaAsyncLoader), "LoadSpriteAsync");
            return loader == null ? null : AccessTools.AsyncMoveNext(loader);
        }

        private static bool Prepare() => TargetMethod() != null;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int getIndex = list.FindIndex(code => code.Calls(GetPixels));
            int setIndex = list.FindIndex(code => code.Calls(SetPixels));
            // Leave the method untouched unless it has exactly the expected copy.
            if (getIndex < 0 || setIndex < getIndex || list.FindLastIndex(code => code.Calls(GetPixels)) != getIndex)
                return list;
            list[getIndex] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MediaSpritePixelCopyPatch), nameof(CaptureSource))).WithLabels(list[getIndex].labels);
            list[setIndex] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MediaSpritePixelCopyPatch), nameof(CopyCaptured))).WithLabels(list[setIndex].labels);
            return list;
        }

        private static Color[] CaptureSource(Texture2D texture, int mipLevel)
        {
            source = texture;
            return null;
        }

        private static void CopyCaptured(Texture2D destination, Color[] unused, int mipLevel)
        {
            Texture2D from = source;
            source = null;
            if (from != null && from.format == destination.format && from.isReadable
                && from.width == destination.width && from.height == destination.height)
            {
                try
                {
                    destination.SetPixelData(from.GetPixelData<byte>(mipLevel), mipLevel);
                    return;
                }
                catch (Exception)
                {
                    // Fall through to the original copy.
                }
            }
            if (from != null)
                destination.SetPixels(from.GetPixels(mipLevel), mipLevel);
        }
    }
}
