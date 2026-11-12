using IPA;
using UnityEngine;
using HarmonyLib;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        private readonly Harmony harmony = new Harmony("iPixelGalaxy.BSFixes");
        private ILogHandler originalHandler;
        private FilteringLogHandler filteringHandler;
        private IPALogger pluginLogger;

        [Init]
        public void Init(IPALogger logger)
        {
            CompatibilitySummary.Reset();
            pluginLogger = logger;
            originalHandler = Debug.unityLogger.logHandler;
            filteringHandler = new FilteringLogHandler(originalHandler);
            Debug.unityLogger.logHandler = filteringHandler;
            HighQualityObstacles.Initialize(logger);
            DefaultFontAssetFix.Start(logger);
            try { harmony.PatchAll(typeof(Plugin).Assembly); }
            catch
            {
                CompatibilitySummary.Write(harmony, logger, "incomplete (automatic registration failed)");
                throw;
            }
            CompatibilitySummary.Record("Transition alpha", "starting");
            try { TransitionAlphaPatch.Apply(harmony); }
            catch (System.Exception exception)
            {
                CompatibilitySummary.Failed("Transition alpha", exception);
                logger.Warn($"Transition alpha patch skipped: {exception.Message}");
            }
        }

        [OnStart]
        public void OnStart()
        {
            bool completed = false;
            try
            {
                SteamVRResolutionSync.Start(pluginLogger);
                CompatibilitySummary.Record("Type warm-up", "starting");
                try { TypeWarmup.Start(harmony, pluginLogger); }
                catch (System.Exception exception)
                {
                    CompatibilitySummary.Failed("Type warm-up", exception);
                    pluginLogger.Warn($"Type warm-up skipped: {exception.Message}");
                }
                completed = true;
            }
            finally
            {
                CompatibilitySummary.Write(harmony, pluginLogger, completed ? "startup snapshot" : "incomplete startup snapshot");
            }
        }

        [OnExit]
        public void OnExit()
        {
            SteamVRResolutionSync.Stop();
            TypeWarmup.Stop();
            harmony.UnpatchSelf();
            HighQualityObstacles.Stop();
            DefaultFontAssetFix.Stop();
            if (filteringHandler != null && ReferenceEquals(Debug.unityLogger.logHandler, filteringHandler))
                Debug.unityLogger.logHandler = originalHandler;
        }
    }
}
