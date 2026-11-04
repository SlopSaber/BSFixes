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
    // Unity's OpenXR plugin sizes the swapchain from the runtime recommendation
    // once, so SteamVR resolution changes made while the game runs were ignored
    // (the 2019-era OpenVR path re-read it live). Follow the runtime's current
    // recommendation, falling back to SteamVR's settings file, and drive the
    // XR display's render scale until the measured eye width matches.
    internal static class SteamVRResolutionSync
    {
        private const string AppSection = "steam.app.620980";
        private const float PollIntervalSeconds = 0.5f;
        private static IPALogger logger;
        private static string settingsPath;
        private static float nextPollTime;
        private const int MaxCorrections = 4;
        private const int WidthTolerance = 2;
        private static float gameScale;
        private static float appliedFactor = 1f;
        private static bool hasGameScale;
        private static bool needsApply;
        // Baseline of the current XR display: eye width at factor 1 and the game
        // scale it was measured at, plus each source's value at that moment.
        private static bool hasBaseline;
        private static float baseWidth;
        private static float baseGameScale;
        private static int baseRecommended;
        private static bool hasFileBaseline;
        private static float baselineAreaScale;
        private static bool hasFileRead;
        private static float lastAreaScale;
        private static bool lastManualOverride;
        private static int targetWidth;
        private static int corrections;
        private static bool verifyResize;
        private static bool warnedUnreachable;
        private static int previousRenderWidth;
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
        // Only the single settings worker reads or writes this cache.
        private static string cachedPath;
        private static long cachedLength;
        private static DateTime cachedWriteTime;
        private static float cachedAreaScale;
        private static bool cachedManualOverride;

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
                // A new display means a new swapchain built from the current recommendation.
                activeDisplay = display;
                readRevision++;
                readyRead = null;
                hasBaseline = false;
                needsApply = true;
            }

            bool steamVR = display != null && OpenXRRuntime.name.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) >= 0;
            UpdateSettingsRead(steamVR && hasGameScale && settingsPath != null);
            if (display == null || !hasGameScale)
                return;

            int width = GetRenderWidth(display);
            if (width == 0 || display.scaleOfAllRenderTargets <= 0f)
                return;
            bool hasRecommendation = OpenXRViewRecommendation.TryGet(out int recommended, out int maxWidth);

            if (!hasBaseline)
            {
                hasBaseline = true;
                baseGameScale = gameScale;
                baseWidth = width * gameScale / display.scaleOfAllRenderTargets;
                baseRecommended = hasRecommendation ? recommended : 0;
                hasFileBaseline = false;
                appliedFactor = 1f;
                targetWidth = 0;
                string runtime = hasRecommendation ? $"{recommended} (max {maxWidth})"
                    : OpenXRViewRecommendation.HasHandles ? "unavailable" : "unknown (no instance handle)";
                string file = hasFileRead ? $"; SteamVR {(lastManualOverride ? "manual" : "automatic")} scale {lastAreaScale:0.###}" : "";
                logger.Info($"Resolution sync active: XR display {width} pixels per eye; OpenXR recommends {runtime}{file}.");
            }
            if (!hasFileBaseline && hasFileRead)
            {
                hasFileBaseline = true;
                baselineAreaScale = lastAreaScale;
            }

            // Prefer the runtime's own recommendation; SteamVR's settings file is the
            // fallback when the runtime keeps reporting its launch-time size.
            float runtimeFactor = hasRecommendation && baseRecommended > 0 ? (float)recommended / baseRecommended : float.NaN;
            float fileFactor = hasFileBaseline ? Mathf.Sqrt(lastAreaScale / baselineAreaScale) : float.NaN;
            float factor;
            string source;
            if (!float.IsNaN(runtimeFactor) && Mathf.Abs(runtimeFactor - 1f) >= 0.002f) { factor = runtimeFactor; source = "OpenXR recommendation"; }
            else if (!float.IsNaN(fileFactor)) { factor = fileFactor; source = "SteamVR settings"; }
            else { factor = 1f; source = "launch size"; }

            int target = Mathf.RoundToInt(baseWidth * gameScale / baseGameScale * factor);
            if (maxWidth > 0) target = Math.Min(target, maxWidth);
            if (target != targetWidth || needsApply)
            {
                bool changed = Mathf.Abs(factor - appliedFactor) >= 0.001f;
                needsApply = false;
                targetWidth = target;
                corrections = 0;
                warnedUnreachable = false;
                appliedFactor = factor;
                if (changed)
                {
                    string runtimeNow = hasRecommendation ? recommended.ToString() : "n/a";
                    string fileNow = float.IsNaN(fileFactor) ? "n/a" : fileFactor.ToString("0.###");
                    logger.Info($"Resolution change from {source}: target {target} pixels per eye (factor {factor:0.###}; OpenXR {runtimeNow} vs launch {baseRecommended}; SteamVR file factor {fileNow}).");
                }
            }

            if (Math.Abs(width - targetWidth) <= WidthTolerance)
            {
                if (verifyResize)
                    logger.Info($"XR display resized from {previousRenderWidth} to {width} pixels per eye (scale {display.scaleOfAllRenderTargets:0.###}).");
                verifyResize = false;
                return;
            }
            if (corrections >= MaxCorrections)
            {
                if (!warnedUnreachable)
                    logger.Warn($"XR display stays {width} pixels per eye; the target is {targetWidth} (scale {display.scaleOfAllRenderTargets:0.###}).");
                warnedUnreachable = true;
                return;
            }

            // Closed loop on the measured eye width: correct whether or not Unity
            // re-reads the runtime recommendation when it rebuilds the swapchain.
            float scale = Mathf.Clamp(display.scaleOfAllRenderTargets * targetWidth / width, 0.1f, 4f);
            if (Mathf.Abs(scale - display.scaleOfAllRenderTargets) < 0.0005f)
                return;
            if (!verifyResize)
                previousRenderWidth = width;
            display.scaleOfAllRenderTargets = scale;
            corrections++;
            verifyResize = true;
        }

        private static void UpdateSettingsRead(bool eligible)
        {
            if (!eligible)
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
            hasFileRead = true;
            lastAreaScale = completedRead.Value.AreaScale;
            lastManualOverride = completedRead.Value.ManualOverride;
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
                var file = new FileInfo(path);
                long length = file.Length;
                DateTime writeTime = file.LastWriteTimeUtc;
                if (path == cachedPath && length == cachedLength && writeTime == cachedWriteTime)
                {
                    areaScale = cachedAreaScale;
                    manualOverride = cachedManualOverride;
                    return true;
                }

                string text = File.ReadAllText(path);
                cancellation.ThrowIfCancellationRequested();
                JObject config = JObject.Parse(text);
                JObject steamVR = config["steamvr"] as JObject;
                manualOverride = steamVR?["supersampleManualOverride"]?.Value<bool>() == true;
                float global = manualOverride ? steamVR?["supersampleScale"]?.Value<float>() ?? 1f : 1f;
                float appPercent = (config[AppSection] as JObject)?["resolutionScale"]?.Value<float>() ?? 100f;
                areaScale = global * appPercent / 100f;
                if (areaScale < 0.1f || areaScale > 20f || float.IsNaN(areaScale))
                    return false;
                file.Refresh();
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != writeTime)
                    return false;
                cachedPath = path;
                cachedLength = length;
                cachedWriteTime = writeTime;
                cachedAreaScale = areaScale;
                cachedManualOverride = manualOverride;
                return true;
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
