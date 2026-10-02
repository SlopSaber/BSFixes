using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
        private static Task<SettingsReadResult> pendingRead;
        private static SettingsReadResult? readyRead;
        private static CancellationTokenSource readCancellation;
        private static int session;
        private static int readRevision;
        private static bool running;
        private static bool settingsPathResolved;
        private static bool readEligible;

        private enum SettingsReadOperation
        {
            FindPath,
            ReadScale
        }

        private readonly struct SettingsReadRequest
        {
            internal readonly SettingsReadOperation Operation;
            internal readonly string Path;
            internal readonly int Session;
            internal readonly int Revision;
            internal readonly CancellationToken Cancellation;

            internal SettingsReadRequest(SettingsReadOperation operation, string path, int currentSession, int revision, CancellationToken cancellation)
            {
                Operation = operation;
                Path = path;
                Session = currentSession;
                Revision = revision;
                Cancellation = cancellation;
            }
        }

        private readonly struct SettingsReadResult
        {
            internal readonly SettingsReadRequest Request;
            internal readonly string Path;
            internal readonly float AreaScale;
            internal readonly bool ManualOverride;
            internal readonly bool Success;
            internal readonly Exception Error;

            internal SettingsReadResult(SettingsReadRequest request, string path, float areaScale, bool manualOverride, bool success, Exception error)
            {
                Request = request;
                Path = path;
                AreaScale = areaScale;
                ManualOverride = manualOverride;
                Success = success;
                Error = error;
            }
        }

        internal static void Start(IPALogger pluginLogger)
        {
            if (running)
                return;

            logger = pluginLogger;
            running = true;
            session++;
            settingsPath = null;
            settingsPathResolved = false;
            readEligible = false;
            readyRead = null;
            readCancellation = new CancellationTokenSource();
            if (pendingRead == null)
                QueueRead(SettingsReadOperation.FindPath, null);
            pollerObject = new GameObject("BSFixes SteamVR Resolution Sync");
            pollerObject.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(pollerObject);
            pollerObject.AddComponent<SteamVRResolutionPoller>();
        }

        internal static void Stop()
        {
            running = false;
            session++;
            readyRead = null;
            readCancellation?.Cancel();
            readCancellation?.Dispose();
            readCancellation = null;
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
            if (!running)
                return;

            CompleteRead();
            if (!settingsPathResolved && pendingRead == null)
                QueueRead(SettingsReadOperation.FindPath, null);

            if (Time.realtimeSinceStartup < nextPollTime)
                return;

            nextPollTime = Time.realtimeSinceStartup + PollIntervalSeconds;
            XRDisplaySubsystem display = GetRunningDisplay();
            if (!ReferenceEquals(display, activeDisplay))
            {
                activeDisplay = display;
                readRevision++;
                readyRead = null;
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

            if (!hasGameScale || settingsPath == null || display == null ||
                OpenXRRuntime.name.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (readEligible)
                    readRevision++;
                readEligible = false;
                readyRead = null;
                return;
            }

            readEligible = true;
            SettingsReadResult? completedRead = readyRead;
            readyRead = null;
            if (pendingRead == null)
                QueueRead(SettingsReadOperation.ReadScale, settingsPath);
            if (!completedRead.HasValue || !completedRead.Value.Success)
                return;

            float areaScale = completedRead.Value.AreaScale;
            bool manualOverride = completedRead.Value.ManualOverride;

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

        private static void QueueRead(SettingsReadOperation operation, string path)
        {
            var request = new SettingsReadRequest(operation, path, session, readRevision, readCancellation.Token);
            pendingRead = Task.Run(() => ProcessRead(request));
        }

        private static void CompleteRead()
        {
            if (pendingRead == null || !pendingRead.IsCompleted)
                return;

            SettingsReadResult result = pendingRead.GetAwaiter().GetResult();
            pendingRead = null;
            if (result.Request.Session != session || result.Request.Cancellation.IsCancellationRequested)
                return;
            if (result.Request.Operation == SettingsReadOperation.ReadScale && result.Request.Revision != readRevision)
                return;

            if (result.Request.Operation == SettingsReadOperation.FindPath)
            {
                settingsPathResolved = true;
                settingsPath = result.Path;
            }
            if (result.Error != null)
                ExceptionDispatchInfo.Capture(result.Error).Throw();
            if (result.Request.Operation == SettingsReadOperation.ReadScale)
                readyRead = result;
        }

        private static SettingsReadResult ProcessRead(SettingsReadRequest request)
        {
            try
            {
                request.Cancellation.ThrowIfCancellationRequested();
                if (request.Operation == SettingsReadOperation.FindPath)
                {
                    string path = FindSettingsPath();
                    request.Cancellation.ThrowIfCancellationRequested();
                    return new SettingsReadResult(request, path, 1f, false, true, null);
                }

                bool success = TryReadAreaScale(request.Path, request.Cancellation, out float areaScale, out bool manualOverride);
                request.Cancellation.ThrowIfCancellationRequested();
                return new SettingsReadResult(request, request.Path, areaScale, manualOverride, success, null);
            }
            catch (Exception error)
            {
                return new SettingsReadResult(request, null, 1f, false, false, error);
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

        private static bool TryReadAreaScale(string path, CancellationToken cancellation, out float areaScale, out bool manualOverride)
        {
            areaScale = 1f;
            manualOverride = false;
            try
            {
                string text = File.ReadAllText(path);
                cancellation.ThrowIfCancellationRequested();
                JObject config = JObject.Parse(text);
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
            string steamPath;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                steamPath = key?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamPath))
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Valve\Steam"))
                    steamPath = key?.GetValue("InstallPath") as string;
            }
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
