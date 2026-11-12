using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    internal static class CompatibilitySummary
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Outcomes = new Dictionary<string, string>();

        internal static void Reset()
        {
            lock (Gate)
            {
                Outcomes.Clear();
                Outcomes["Media sprite copy"] = "not inspected";
                Outcomes["Centered spinner"] = "not inspected";
                Outcomes["Transition alpha"] = "not attempted";
                Outcomes["Type warm-up"] = "not started";
            }
        }

        internal static void Record(string feature, string outcome)
        {
            lock (Gate) Outcomes[feature] = outcome;
        }

        internal static void Failed(string feature, Exception exception)
        {
            lock (Gate)
            {
                Outcomes.TryGetValue(feature, out string previous);
                Outcomes[feature] = previous + "; startup failed (" + exception.GetType().Name + ")";
            }
        }

        internal static void Write(Harmony harmony, IPALogger logger, string phase)
        {
            try
            {
                var methods = harmony.GetPatchedMethods().ToArray();
                var groups = new Dictionary<string, HashSet<MethodBase>>();
                foreach (MethodBase method in methods)
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                    {
                        if (patch.owner != harmony.Id) continue;
                        string name = patch.PatchMethod.DeclaringType?.Name ?? patch.PatchMethod.Name;
                        if (!groups.TryGetValue(name, out var targets))
                            groups[name] = targets = new HashSet<MethodBase>();
                        targets.Add(method);
                    }
                }

                KeyValuePair<string, string>[] checks;
                lock (Gate) checks = Outcomes.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                logger.Info($"Compatibility {phase}: {methods.Length} target methods registered. Registration does not confirm runtime repair.");
                logger.Info("Registered groups: " + (groups.Count == 0 ? "none" : string.Join(", ", groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value.Count))) + ".");
                logger.Info("Startup checks: " + string.Join("; ", checks.Select(pair => pair.Key + ": " + pair.Value)) + ".");
                logger.Info("Runtime version guards: ReeSabers canvas " + (ReeSabersCanvasSettingsPatch.IsSupportedVersion
                    ? "eligible for 1.45.3; matching runtime objects/scenes remain unverified."
                    : "inactive on this version (require 1.45.3)."));
            }
            catch (Exception exception)
            {
                try { logger?.Warn("Compatibility summary unavailable: " + exception.Message); }
                catch { }
            }
        }
    }
}
