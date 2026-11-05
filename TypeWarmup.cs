using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IPA.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    // Entering Solo and starting a level analyze hundreds of types for injection and compile their
    // methods on first call, all inside one frame. The types Zenject analyzes after the menu first
    // loads are recorded, and on later launches they are analyzed and compiled ahead of time on idle
    // menu frames under a small per-frame budget. Work stays on the main thread, so static
    // constructors that a compile triggers run on the thread they would normally run on.
    internal sealed class TypeWarmup : MonoBehaviour
    {
        private const string MenuScene = "MainMenu";
        private const double FrameBudgetMs = 2;
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly HashSet<string> Recorded = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal);
        private static bool recording;
        private static IPALogger log;
        private static TypeWarmup instance;
        private static string ListPath => Path.Combine(UnityGame.UserDataPath, "BSFixes", "TypeWarmup.txt");

        private Func<Type, object> analyze;
        private bool warmed;

        internal static void Start(Harmony harmony, IPALogger logger)
        {
            log = logger;
            MethodInfo tryGetInfo = AccessTools.TypeByName("Zenject.TypeAnalyzer") is Type analyzer
                ? AccessTools.Method(analyzer, "TryGetInfo", new[] { typeof(Type) }) : null;
            if (tryGetInfo == null)
            {
                logger.Warn("Type warm-up skipped: Zenject.TypeAnalyzer.TryGetInfo was not found.");
                return;
            }
            harmony.Patch(tryGetInfo, postfix: new HarmonyMethod(AccessTools.Method(typeof(TypeWarmup), nameof(Analyzed))));
            var host = new GameObject("BSFixes Type Warmup");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<TypeWarmup>();
            instance.analyze = type => tryGetInfo.Invoke(null, new object[] { type });
        }

        internal static void Stop()
        {
            if (instance != null) Destroy(instance.gameObject);
            instance = null;
        }

        private static void Analyzed(object[] __args)
        {
            Type type = __args.Length > 0 ? __args[0] as Type : null;
            if (!recording || type == null || type.ContainsGenericParameters || type.Assembly.IsDynamic) return;
            string name = type.AssemblyQualifiedName;
            if (name != null && !Known.Contains(name)) Recorded.Add(name);
        }

        private void OnEnable() => SceneManager.activeSceneChanged += SceneChanged;
        private void OnDisable() => SceneManager.activeSceneChanged -= SceneChanged;
        private void OnApplicationQuit() => Save();

        private void SceneChanged(Scene previous, Scene next)
        {
            if (next.name != MenuScene) return;
            if (!recording)
            {
                // Types analyzed while the menu itself starts are already paid for; record what follows.
                recording = true;
                LoadKnown();
            }
            else Save();
            if (!warmed)
            {
                warmed = true;
                StartCoroutine(Warm());
            }
        }

        private static void LoadKnown()
        {
            try
            {
                if (File.Exists(ListPath))
                    foreach (string line in File.ReadAllLines(ListPath))
                        if (line.Length > 0) Known.Add(line);
            }
            catch (Exception error) { log?.Debug($"Type warm-up list unreadable: {error.Message}"); }
        }

        private static void Save()
        {
            if (Recorded.Count == 0) return;
            try
            {
                Known.UnionWith(Recorded);
                Recorded.Clear();
                Directory.CreateDirectory(Path.GetDirectoryName(ListPath));
                File.WriteAllLines(ListPath, Known.OrderBy(name => name, StringComparer.Ordinal));
            }
            catch (Exception error) { log?.Debug($"Type warm-up list not saved: {error.Message}"); }
        }

        private IEnumerator Warm()
        {
            // Let the menu finish its own startup work first.
            yield return new WaitForSecondsRealtime(5f);
            string[] names = Known.ToArray();
            if (names.Length == 0) yield break;

            var total = Stopwatch.StartNew();
            var budget = Stopwatch.StartNew();
            int types = 0, methods = 0, failed = 0;
            foreach (string name in names)
            {
                Type type = null;
                try { type = Type.GetType(name, false); }
                catch { }
                if (type == null || type.ContainsGenericParameters) continue;
                types++;
                try { analyze(type); }
                catch { failed++; }
                foreach (MethodBase method in Methods(type))
                {
                    try
                    {
                        method.MethodHandle.GetFunctionPointer();
                        methods++;
                    }
                    catch { failed++; }
                    if (budget.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                    {
                        yield return null;
                        budget.Restart();
                    }
                }
            }
            log?.Info($"Warmed {types} types and {methods} methods over {total.ElapsedMilliseconds} ms ({failed} skipped).");
        }

        private static IEnumerable<MethodBase> Methods(Type type)
        {
            MethodBase[] candidates;
            try { candidates = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).ToArray(); }
            catch { yield break; }
            foreach (MethodBase method in candidates)
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.IsStatic && method.IsConstructor) continue;
                MethodImplAttributes flags;
                try { flags = method.GetMethodImplementationFlags(); }
                catch { continue; }
                if ((flags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0
                    || (method.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
                yield return method;
            }
        }
    }
}
