using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine.XR.OpenXR.Features;

namespace BSFixes
{
    // Unity's OpenXR plugin reads the runtime's recommended eye size only when it
    // creates the swapchain. Re-ask the runtime directly so a resolution change
    // made in SteamVR (or another runtime) while the game runs can be followed.
    internal static class OpenXRViewRecommendation
    {
        private const int XrTypeViewConfigurationView = 41;
        private const int XrViewConfigurationTypePrimaryStereo = 2;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetInstanceProcAddr(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int EnumerateViewConfigurationViews(ulong instance, ulong systemId, int viewConfigurationType,
            uint viewCapacityInput, out uint viewCountOutput, [In, Out] ViewConfigurationView[] views);

        [StructLayout(LayoutKind.Sequential)]
        private struct ViewConfigurationView
        {
            public int Type;
            public IntPtr Next;
            public uint RecommendedImageRectWidth;
            public uint MaxImageRectWidth;
            public uint RecommendedImageRectHeight;
            public uint MaxImageRectHeight;
            public uint RecommendedSwapchainSampleCount;
            public uint MaxSwapchainSampleCount;
        }

        private static readonly PropertyInfo ProcAddrProperty = typeof(OpenXRFeature).GetProperty("xrGetInstanceProcAddr",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly ViewConfigurationView[] Views = new ViewConfigurationView[2];
        private static ulong instance;
        private static ulong system;
        private static EnumerateViewConfigurationViews enumerate;
        private static bool lookupFailed;

        internal static bool HasHandles => instance != 0 && system != 0;

        internal static void SetInstance(ulong value)
        {
            instance = value;
            system = 0;
            enumerate = null;
            lookupFailed = false;
        }

        internal static void SetSystem(ulong value) => system = value;

        internal static void ClearInstance(ulong value)
        {
            if (instance == value) SetInstance(0);
        }

        // Width is per eye in pixels; the runtime may change it at any time.
        internal static bool TryGet(out int recommendedWidth, out int maxWidth)
        {
            recommendedWidth = 0;
            maxWidth = 0;
            if (!HasHandles || lookupFailed) return false;
            if (enumerate == null && !Resolve()) return false;

            for (int i = 0; i < Views.Length; i++)
                Views[i] = new ViewConfigurationView { Type = XrTypeViewConfigurationView };
            if (enumerate(instance, system, XrViewConfigurationTypePrimaryStereo, (uint)Views.Length, out uint count, Views) != 0 || count == 0)
                return false;
            recommendedWidth = (int)Views[0].RecommendedImageRectWidth;
            maxWidth = (int)Views[0].MaxImageRectWidth;
            return recommendedWidth > 0;
        }

        private static bool Resolve()
        {
            IntPtr procAddr = ProcAddrProperty?.GetValue(null) is IntPtr value ? value : IntPtr.Zero;
            if (procAddr == IntPtr.Zero) return false;
            var getProc = (GetInstanceProcAddr)Marshal.GetDelegateForFunctionPointer(procAddr, typeof(GetInstanceProcAddr));
            if (getProc(instance, "xrEnumerateViewConfigurationViews", out IntPtr function) != 0 || function == IntPtr.Zero)
            {
                lookupFailed = true;
                return false;
            }
            enumerate = (EnumerateViewConfigurationViews)Marshal.GetDelegateForFunctionPointer(function, typeof(EnumerateViewConfigurationViews));
            return true;
        }
    }

    // Unity passes the instance and system handles to every enabled feature; the
    // game's BeatSaberXRFeature does not override these, so the base methods run.
    [HarmonyPatch(typeof(OpenXRFeature))]
    internal static class OpenXRHandlePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("OnInstanceCreate")]
        private static void InstanceCreated(ulong __0) => OpenXRViewRecommendation.SetInstance(__0);

        [HarmonyPostfix]
        [HarmonyPatch("OnSystemChange")]
        private static void SystemChanged(ulong __0) => OpenXRViewRecommendation.SetSystem(__0);

        [HarmonyPostfix]
        [HarmonyPatch("OnInstanceDestroy")]
        private static void InstanceDestroyed(ulong __0) => OpenXRViewRecommendation.ClearInstance(__0);
    }
}
