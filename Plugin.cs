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
            pluginLogger = logger;
            originalHandler = Debug.unityLogger.logHandler;
            filteringHandler = new FilteringLogHandler(originalHandler);
            Debug.unityLogger.logHandler = filteringHandler;
            HighQualityObstacles.Initialize(logger);
            DefaultFontAssetFix.Start(logger);
            harmony.PatchAll(typeof(Plugin).Assembly);
            try { TransitionAlphaPatch.Apply(harmony); }
            catch (System.Exception exception) { logger.Warn($"Transition alpha patch skipped: {exception.Message}"); }
            logger.Info("Installed runtime compatibility fixes and Unity startup log filters.");
        }

        [OnStart]
        public void OnStart()
        {
            SteamVRResolutionSync.Start(pluginLogger);
        }

        [OnExit]
        public void OnExit()
        {
            SteamVRResolutionSync.Stop();
            harmony.UnpatchSelf();
            HighQualityObstacles.Stop();
            DefaultFontAssetFix.Stop();
            if (filteringHandler != null && ReferenceEquals(Debug.unityLogger.logHandler, filteringHandler))
                Debug.unityLogger.logHandler = originalHandler;
        }
    }
}
