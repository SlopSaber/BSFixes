using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    // Reads SteamVR's live settings through OpenVR alongside the game's OpenXR session.
    // Connects as a background application (never starts SteamVR) and is never shut
    // down, because shutting down the shared client could end the OpenXR session.
    internal static class OpenVRSettings
    {
        private const int ApplicationBackground = 3;
        private const string SettingsInterface = "FnTable:IVRSettings_003";
        private const string SystemInterface = "FnTable:IVRSystem_022";
        // IVRSettings_003 function table indices (openvr.h declaration order).
        private const int SettingsGetBool = 5;
        private const int SettingsGetFloat = 7;
        private const int SettingsFunctionCount = 8;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint InitInternal2(ref int error, int applicationType, [MarshalAs(UnmanagedType.LPStr)] string startupInfo);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetGenericInterface([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetBoolFn([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate float GetFloatFn([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static bool attempted;
        private static bool available;
        private static GetBoolFn getBool;
        private static GetFloatFn getFloat;

        internal static bool TryConnect(IPALogger logger)
        {
            if (attempted)
                return available;
            attempted = true;
            try
            {
                string library = FindLibrary();
                if (library == null)
                {
                    logger.Info("OpenVR settings are unavailable: no SteamVR runtime is registered.");
                    return false;
                }
                IntPtr module = LoadLibrary(library);
                if (module == IntPtr.Zero)
                {
                    logger.Warn($"OpenVR could not be loaded from {library} (error {Marshal.GetLastWin32Error()}).");
                    return false;
                }
                var getInterface = Marshal.GetDelegateForFunctionPointer<GetGenericInterface>(GetProcAddress(module, "VR_GetGenericInterface"));
                int error = 0;
                // Another mod may already hold a client in this process; otherwise connect.
                if (getInterface(SystemInterface, ref error) == IntPtr.Zero)
                {
                    var init = Marshal.GetDelegateForFunctionPointer<InitInternal2>(GetProcAddress(module, "VR_InitInternal2"));
                    error = 0;
                    init(ref error, ApplicationBackground, null);
                    if (error != 0)
                    {
                        logger.Info($"OpenVR settings are unavailable (init error {error}).");
                        return false;
                    }
                }
                error = 0;
                IntPtr table = getInterface(SettingsInterface, ref error);
                if (table == IntPtr.Zero || error != 0)
                {
                    logger.Warn($"OpenVR settings interface is unavailable (error {error}).");
                    return false;
                }
                var functions = new IntPtr[SettingsFunctionCount];
                Marshal.Copy(table, functions, 0, SettingsFunctionCount);
                getBool = Marshal.GetDelegateForFunctionPointer<GetBoolFn>(functions[SettingsGetBool]);
                getFloat = Marshal.GetDelegateForFunctionPointer<GetFloatFn>(functions[SettingsGetFloat]);
                available = true;
                logger.Info("OpenVR connected for live SteamVR settings.");
            }
            catch (Exception ex)
            {
                logger.Warn($"OpenVR settings are unavailable: {ex.Message}");
            }
            return available;
        }

        internal static bool TryGetFloat(string section, string key, out float value)
        {
            value = 0f;
            if (!available)
                return false;
            int error = 0;
            value = getFloat(section, key, ref error);
            return error == 0;
        }

        internal static bool TryGetBool(string section, string key, out bool value)
        {
            value = false;
            if (!available)
                return false;
            int error = 0;
            value = getBool(section, key, ref error);
            return error == 0;
        }

        private static string FindLibrary()
        {
            string paths = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
            if (!File.Exists(paths))
                return null;
            foreach (JToken runtime in (JObject.Parse(File.ReadAllText(paths))["runtime"] as JArray) ?? new JArray())
            {
                string library = Path.Combine((string)runtime, "bin", "win64", "openvr_api.dll");
                if (File.Exists(library))
                    return library;
            }
            return null;
        }
    }
}
