using System.Collections.Generic;
using BeatSaber.Settings;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using IPALogger = IPA.Logging.Logger;
using QualitySettings = BeatSaber.Settings.QualitySettings;

namespace BSFixes
{
    internal static class HighQualityObstacles
    {
        private static readonly int DisplacementStrength = Shader.PropertyToID("_DisplacementStrength");
        private static readonly Dictionary<Material, Material> Materials = new Dictionary<Material, Material>();
        private static IPALogger logger;
        private static bool highQuality;
        private static bool distortionEnabled;
        private static bool hasSettings;

        internal static void Initialize(IPALogger pluginLogger)
        {
            logger = pluginLogger;
        }

        internal static void ApplySettings(Settings settings)
        {
            bool newHighQuality = settings.quality.obstacles == QualitySettings.ObstacleQuality.High;
            bool newDistortionEnabled = settings.quality.screenDisplacementEffects;
            bool changed = !hasSettings || highQuality != newHighQuality || distortionEnabled != newDistortionEnabled;
            highQuality = newHighQuality;
            distortionEnabled = newDistortionEnabled;
            hasSettings = true;

            // The HD shader samples the captured screen even with a zero UV offset.
            ScreenDisplacementEffectRendererFeature.enabled = highQuality || distortionEnabled;
            if (!changed)
                return;

            foreach (KeyValuePair<Material, Material> pair in Materials)
            {
                if (pair.Key != null && pair.Value != null)
                    pair.Value.SetFloat(DisplacementStrength, distortionEnabled ? pair.Key.GetFloat(DisplacementStrength) : 0f);
            }

            if (highQuality && !distortionEnabled)
                logger.Info("Keeping high-quality obstacles with screen distortion disabled; refraction strength is zero.");
        }

        internal static void SetCoreMaterial(Renderer renderer, bool screenDisplacementEffects)
        {
            Material source = renderer.sharedMaterial;
            if (source == null || !source.HasProperty(DisplacementStrength))
                return;

            if (!Materials.TryGetValue(source, out Material material) || material == null)
            {
                material = new Material(source)
                {
                    name = source.name + " (BSFixes)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                Materials[source] = material;
            }

            material.SetFloat(DisplacementStrength, screenDisplacementEffects ? source.GetFloat(DisplacementStrength) : 0f);
            renderer.sharedMaterial = material;
        }

        internal static void EnsureScreenCapture()
        {
            // Map settings can write the feature flag without applying graphics settings.
            if (highQuality)
                ScreenDisplacementEffectRendererFeature.enabled = true;
        }

        internal static void Stop()
        {
            foreach (Material material in Materials.Values)
            {
                if (material != null)
                    Object.Destroy(material);
            }

            Materials.Clear();
        }
    }

    [HarmonyPatch(typeof(ObstacleMaterialSetter), nameof(ObstacleMaterialSetter.Init))]
    internal static class HighQualityObstacleMaterialPatch
    {
        private static void Prefix(QualitySettings.ObstacleQuality obstacleQuality, ref bool screenDisplacementEffects, out bool __state)
        {
            __state = screenDisplacementEffects;
            if (obstacleQuality == QualitySettings.ObstacleQuality.High)
                screenDisplacementEffects = true;
        }

        private static void Postfix(QualitySettings.ObstacleQuality obstacleQuality, Renderer ____obstacleCoreRenderer, bool __state)
        {
            if (obstacleQuality == QualitySettings.ObstacleQuality.High)
                HighQualityObstacles.SetCoreMaterial(____obstacleCoreRenderer, __state);
        }
    }

    [HarmonyPatch(typeof(ObstacleController), "InitGraphics")]
    internal static class HighQualityObstacleLayerPatch
    {
        private static void Prefix(Settings settings)
        {
            HighQualityObstacles.ApplySettings(settings);
        }

        private static void Postfix(Settings settings, GameObject[] ____layerSwitch)
        {
            if (settings.quality.obstacles != QualitySettings.ObstacleQuality.High || settings.quality.screenDisplacementEffects)
                return;

            int layer = LayerMask.NameToLayer("ScreenDisplacement");
            if (layer < 0)
                return;

            foreach (GameObject wrapper in ____layerSwitch)
                wrapper.layer = layer;
        }
    }

    [HarmonyPatch(typeof(SettingsApplicatorSO), nameof(SettingsApplicatorSO.ApplyGraphicSettings))]
    internal static class HighQualityObstacleSettingsPatch
    {
        private static void Postfix(Settings settings)
        {
            HighQualityObstacles.ApplySettings(settings);
        }
    }

    [HarmonyPatch(typeof(ScreenDisplacementEffectRendererFeature), nameof(ScreenDisplacementEffectRendererFeature.AddRenderPasses))]
    internal static class HighQualityObstacleCapturePatch
    {
        private static void Prefix()
        {
            HighQualityObstacles.EnsureScreenCapture();
        }
    }

    [HarmonyPatch(typeof(DepthTextureController), "Init")]
    internal static class HighQualityObstacleDepthPatch
    {
        private static void Postfix(DepthTextureController __instance, SettingsManager settingsManager)
        {
            if (settingsManager.settings.quality.obstacles != QualitySettings.ObstacleQuality.High ||
                settingsManager.settings.quality.screenDisplacementEffects)
                return;

            Camera camera = __instance.GetComponent<Camera>();
            camera.depthTextureMode |= DepthTextureMode.Depth;
            Shader.EnableKeyword("DEPTH_TEXTURE_ENABLED");
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture = true;
        }
    }
}
