using IPA;
using UnityEngine;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        private ILogHandler originalHandler;
        private FilteringLogHandler filteringHandler;

        [Init]
        public void Init(IPALogger logger)
        {
            originalHandler = Debug.unityLogger.logHandler;
            filteringHandler = new FilteringLogHandler(originalHandler);
            Debug.unityLogger.logHandler = filteringHandler;
            logger.Info("Installed harmless Unity startup log filters.");
        }

        [OnExit]
        public void OnExit()
        {
            if (filteringHandler != null && ReferenceEquals(Debug.unityLogger.logHandler, filteringHandler))
                Debug.unityLogger.logHandler = originalHandler;
        }
    }
}
