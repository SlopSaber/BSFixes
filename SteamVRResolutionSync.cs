using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    // SteamVR's OpenXR recommendation is used when Unity creates the swapchain.
    // Later changes need to be applied to the active XR display subsystem.
    internal static class SteamVRResolutionSync
    {
        private const string AppSection = "steam.app.620980";
        private const float PollIntervalSeconds = 0.5f;
        private static IPALogger logger;
        private static string settingsPath;
        private static float nextPollTime;
        private static float gameScale;
        private static float baselineAreaScale;
        private static float appliedFactor = 1f;
        private static int previousRenderWidth;
        private static bool hasGameScale;
        private static bool hasBaseline;
        private static bool verifyResize;
        private static bool needsApply;
        private static bool retriedUnexpectedReset;
        private static GameObject pollerObject;
        private static XRDisplaySubsystem activeDisplay;
        private static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>(1);

        internal static void Start(IPALogger pluginLogger)
        {
            logger = pluginLogger;
            settingsPath = FindSettingsPath();
            pollerObject = new GameObject("BSFixes SteamVR Resolution Sync");
            pollerObject.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(pollerObject);
            pollerObject.AddComponent<SteamVRResolutionPoller>();
        }

        internal static void Stop()
        {
            if (pollerObject != null)
                UnityEngine.Object.Destroy(pollerObject);
            pollerObject = null;
        }

        internal static float AdjustGameScale(float scale)
        {
            gameScale = scale;
            hasGameScale = true;
            needsApply = true;
            return scale * appliedFactor;
        }

        internal static void Poll()
        {
            if (Time.realtimeSinceStartup < nextPollTime)
                return;

            nextPollTime = Time.realtimeSinceStartup + PollIntervalSeconds;
            XRDisplaySubsystem display = GetRunningDisplay();
            if (!ReferenceEquals(display, activeDisplay))
            {
                activeDisplay = display;
                needsApply = true;
                retriedUnexpectedReset = false;
            }
            if (verifyResize)
            {
                int width = GetRenderWidth(display);
                if (width == 0 || previousRenderWidth == 0)
                    logger.Warn("SteamVR XR display render size could not be compared after the scale change.");
                else if (width == previousRenderWidth)
                    logger.Warn($"SteamVR requested a new render target scale, but the XR display is still {width} pixels wide.");
                else
                    logger.Info($"SteamVR XR display resized from {previousRenderWidth} to {width} pixels per eye.");
                verifyResize = false;
            }

            float areaScale;
            bool manualOverride;
            if (!hasGameScale || settingsPath == null || display == null ||
                OpenXRRuntime.name.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) < 0 ||
                !TryReadAreaScale(out areaScale, out manualOverride))
                return;

            if (!hasBaseline)
            {
                baselineAreaScale = areaScale;
                hasBaseline = true;
                logger.Info($"SteamVR resolution sync active: XR display {GetRenderWidth(display)} pixels per eye, SteamVR {(manualOverride ? "manual" : "automatic")} scale {areaScale:0.###}.");
                return;
            }

            // SteamVR does not persist the effective automatic scale. Treat it
            // as 100% for live adjustment; a fresh launch uses the exact runtime
            // recommendation if automatic scaling differs from that baseline.
            float factor = Mathf.Sqrt(areaScale / baselineAreaScale);
            bool steamVRChanged = Mathf.Abs(factor - appliedFactor) >= 0.001f;
            appliedFactor = factor;
            float desiredScale = gameScale * factor;
            bool mismatch = Mathf.Abs(display.scaleOfAllRenderTargets - desiredScale) >= 0.001f;
            if (steamVRChanged || needsApply || (mismatch && !retriedUnexpectedReset))
            {
                retriedUnexpectedReset = !steamVRChanged && !needsApply && mismatch;
                needsApply = false;
                if (mismatch)
                {
                    previousRenderWidth = GetRenderWidth(display);
                    display.scaleOfAllRenderTargets = desiredScale;
                    verifyResize = true;
                }
                if (steamVRChanged)
                    logger.Info($"SteamVR resolution changed to {(manualOverride ? "manual" : "automatic")} {areaScale:0.###}; XR display render target scale is now {display.scaleOfAllRenderTargets:0.###}.");
            }
        }

        private static XRDisplaySubsystem GetRunningDisplay()
        {
            displays.Clear();
            SubsystemManager.GetSubsystems(displays);
            foreach (XRDisplaySubsystem display in displays)
                if (display.running)
                    return display;
            return null;
        }

        private static int GetRenderWidth(XRDisplaySubsystem display)
        {
            if (display == null || display.GetRenderPassCount() == 0)
                return 0;
            display.GetRenderPass(0, out XRDisplaySubsystem.XRRenderPass renderPass);
            return renderPass.renderTargetDesc.width;
        }

        private static bool TryReadAreaScale(out float areaScale, out bool manualOverride)
        {
            areaScale = 1f;
            manualOverride = false;
            try
            {
                JObject config = JObject.Parse(File.ReadAllText(settingsPath));
                JObject steamVR = config["steamvr"] as JObject;
                manualOverride = steamVR?["supersampleManualOverride"]?.Value<bool>() == true;
                float global = manualOverride ? steamVR?["supersampleScale"]?.Value<float>() ?? 1f : 1f;
                float appPercent = (config[AppSection] as JObject)?["resolutionScale"]?.Value<float>() ?? 100f;
                areaScale = global * appPercent / 100f;
                return areaScale >= 0.1f && areaScale <= 20f && !float.IsNaN(areaScale);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (Newtonsoft.Json.JsonException) { return false; }
            catch (FormatException) { return false; }
        }

        private static string FindSettingsPath()
        {
            string steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamPath))
                steamPath = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
            if (string.IsNullOrEmpty(steamPath))
                return null;

            string path = Path.Combine(steamPath, "config", "steamvr.vrsettings");
            return File.Exists(path) ? path : null;
        }
    }

    internal sealed class SteamVRResolutionPoller : MonoBehaviour
    {
        private void Update()
        {
            SteamVRResolutionSync.Poll();
        }
    }

    [HarmonyPatch(typeof(SettingsApplicatorSO), nameof(SettingsApplicatorSO.ApplyGraphicSettings))]
    internal static class SteamVRResolutionPatch
    {
        private static readonly MethodInfo EyeScaleSetter = AccessTools.PropertySetter(typeof(XRSettings), nameof(XRSettings.eyeTextureResolutionScale));
        private static readonly MethodInfo AdjustScale = AccessTools.Method(typeof(SteamVRResolutionSync), nameof(SteamVRResolutionSync.AdjustGameScale));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(EyeScaleSetter))
                    yield return new CodeInstruction(OpCodes.Call, AdjustScale);
                yield return instruction;
            }
        }
    }
}
