using System;
using UnityEngine;

namespace BSFixes
{
    internal sealed class FilteringLogHandler : ILogHandler
    {
        private readonly ILogHandler innerHandler;
        private bool loggedActivationTrace;

        public FilteringLogHandler(ILogHandler innerHandler)
        {
            this.innerHandler = innerHandler;
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            innerHandler.LogException(exception, context);
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string message = FormatMessage(format, args);
            if (IsKnownHarmlessMessage(logType, message))
                return;

            if (!loggedActivationTrace && logType == LogType.Error &&
                message == "GameObjects can not be made active when they are being destroyed.")
            {
                loggedActivationTrace = true;
                innerHandler.LogFormat(logType, context, "{0}\nActivation caller:\n{1}",
                    message, new System.Diagnostics.StackTrace(1, false).ToString());
                return;
            }

            innerHandler.LogFormat(logType, context, format, args);
        }

        private static string FormatMessage(string format, object[] args)
        {
            if (args == null || args.Length == 0)
                return format ?? string.Empty;

            try
            {
                return string.Format(format ?? string.Empty, args);
            }
            catch (FormatException)
            {
                return format ?? string.Empty;
            }
        }

        private static bool IsKnownHarmlessMessage(LogType logType, string message)
        {
            if (logType == LogType.Warning &&
                (message.Contains("[SettingsIO] Decode: Unknown property 'quest.cpu_level'") ||
                 message.Contains("[SettingsIO] Decode: Unknown property 'quest.gpu_level'") ||
                 message.Contains("[DlcPromoPanelModel] No PromoPanel assets discovered") ||
                 message.Contains("OnLevelWasLoaded was found on PluginComponent") ||
                 message.Contains("This message has been deprecated in a later version of Unity.") ||
                 message.Contains("Add a delegate to SceneManager.sceneLoaded instead to get notifications after scene loading has completed") ||
                 message.Contains("Your project uses a scriptable render pipeline. You can use Camera.stereoTargetEye only with the built-in renderer.")))
                return true;

            return logType == LogType.Error &&
                message.StartsWith("Command cvar.System.", StringComparison.Ordinal) &&
                message.Contains("has invalid command name!");
        }
    }
}
