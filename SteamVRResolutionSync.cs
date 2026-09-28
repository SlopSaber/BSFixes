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
    // Changing SteamVR resolution afterward does not resize that swapchain.
    internal static class SteamVRResolutionSync
    {
        private const string AppSection = "steam.app.620980";
        private const float PollIntervalSeconds = 2f;
        private static IPALogger logger;
        private static string settingsPath;
        private static float nextPollTime;
        private static float gameScale;
        private static float baselineAreaScale;
        private static float appliedFactor = 1f;
        private static bool baselineManualOverride;
        private static bool hasGameScale;
        private static bool hasBaseline;
        private static bool warnedModeChange;

        internal static void Start(IPALogger pluginLogger)
        {
            logger = pluginLogger;
            settingsPath = FindSettingsPath();
            Application.onBeforeRender += OnBeforeRender;
        }

        internal static void Stop()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        internal static float AdjustGameScale(float scale)
        {
            gameScale = scale;
            hasGameScale = true;
            return scale * appliedFactor;
        }

        private static void OnBeforeRender()
        {
            if (Time.realtimeSinceStartup < nextPollTime)
                return;

            nextPollTime = Time.realtimeSinceStartup + PollIntervalSeconds;
            float areaScale;
            bool manualOverride;
            if (!hasGameScale || settingsPath == null || !XRSettings.isDeviceActive ||
                OpenXRRuntime.name.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) < 0 ||
                !TryReadAreaScale(out areaScale, out manualOverride))
                return;

            if (!hasBaseline)
            {
                baselineAreaScale = areaScale;
                baselineManualOverride = manualOverride;
                hasBaseline = true;
                logger.Info($"SteamVR resolution sync active: {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} per eye, SteamVR scale {areaScale:0.###}.");
                return;
            }

            // Auto and manual use different SteamVR baselines. A switch between
            // them needs a new OpenXR recommendation on the next game launch.
            if (manualOverride != baselineManualOverride)
            {
                if (!warnedModeChange)
                {
                    logger.Warn("SteamVR automatic/manual resolution mode changed; restart Beat Saber to use its new baseline.");
                    warnedModeChange = true;
                }
                return;
            }

            float factor = Mathf.Sqrt(areaScale / baselineAreaScale);
            if (Mathf.Abs(factor - appliedFactor) < 0.001f)
                return;

            appliedFactor = factor;
            XRSettings.eyeTextureResolutionScale = gameScale * factor;
            logger.Info($"SteamVR resolution changed; eye texture scale is now {XRSettings.eyeTextureResolutionScale:0.###}.");
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
