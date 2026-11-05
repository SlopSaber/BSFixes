using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using IPALogger = IPA.Logging.Logger;

namespace BSFixes
{
    // The game ships no TMP default font, so text created before a mod assigns one falls back to the
    // missing LiberationSans asset. Use the game's own UI font as the default instead.
    internal static class DefaultFontAssetFix
    {
        private static readonly string[] PreferredFonts = { "Teko-Medium SDF", "Teko-Medium SDF No Glow" };
        private static IPALogger logger;
        private static bool listening;

        internal static void Start(IPALogger log)
        {
            logger = log;
            if (TryApply()) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            listening = true;
        }

        internal static void Stop()
        {
            if (!listening) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            listening = false;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (TryApply()) Stop();
        }

        private static bool TryApply()
        {
            try
            {
                if (TMP_Settings.instance == null) return false;
                if (TMP_Settings.defaultFontAsset != null) return true;

                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                var font = PreferredFonts
                    .Select(name => fonts.FirstOrDefault(candidate => candidate != null && candidate.name == name))
                    .FirstOrDefault(candidate => candidate != null)
                    ?? fonts.FirstOrDefault(candidate => candidate != null && candidate.name.StartsWith("Teko", StringComparison.Ordinal));
                if (font == null) return false;

                TMP_Settings.defaultFontAsset = font;
                logger?.Debug($"Using {font.name} as the TextMeshPro default font.");
                return true;
            }
            catch (Exception ex)
            {
                logger?.Debug($"Could not set the TextMeshPro default font: {ex.Message}");
                return true;
            }
        }
    }
}
