using System;
using System.IO;
using HarmonyLib;
using Microsoft.Win32;

namespace BSFixes
{
    [HarmonyPatch(typeof(LivStaticWrapper), nameof(LivStaticWrapper.Init))]
    internal static class LivWithoutAppPatch
    {
        private static readonly bool LivAppAvailable = HasLivApp();

        private static bool Prefix()
        {
            return LivAppAvailable;
        }

        private static bool HasLivApp()
        {
            if (PathExists(Environment.GetEnvironmentVariable("LIV_APP_PATH")))
                return true;

            try
            {
                return PathExists(Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\LIV.App", "SDKRuntimePath", null) as string) ||
                    PathExists(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\LIV.App", "SDKRuntimePath", null) as string);
            }
            catch
            {
                // Keep the game's LIV path if the app state cannot be read.
                return true;
            }
        }

        private static bool PathExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = path.Trim('"');
            return File.Exists(path) || Directory.Exists(path);
        }
    }
}
