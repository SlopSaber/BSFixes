using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BSFixes
{
    [HarmonyPatch(typeof(MaterialPropertyBlockColorSetter), "Awake")]
    internal static class ColdplaySpherePatch
    {
        private static readonly AccessTools.FieldRef<MaterialPropertyBlockController, Renderer[]> Renderers =
            AccessTools.FieldRefAccess<MaterialPropertyBlockController, Renderer[]>("_renderers");
        private static readonly AccessTools.FieldRef<MaterialPropertyBlockColorSetter, int> PropertyId =
            AccessTools.FieldRefAccess<MaterialPropertyBlockColorSetter, int>("_propertyId");
        private static readonly AccessTools.FieldRef<MaterialPropertyBlockColorSetter, string> PropertyName =
            AccessTools.FieldRefAccess<MaterialPropertyBlockColorSetter, string>("_property");

        internal static bool IsColdplay(Component component) =>
            Application.version.StartsWith("1.45.3", StringComparison.Ordinal) &&
            component.gameObject.scene.name == "ColdplayEnvironment";

        private static void Prefix(MaterialPropertyBlockColorSetter __instance)
        {
            Restore(__instance);
        }

        internal static bool Restore(MaterialPropertyBlockColorSetter setter)
        {
            if (setter == null || !IsColdplay(setter)) return false;
            MaterialPropertyBlockController controller = setter.materialPropertyBlockController;
            if (controller == null) return false;
            if (setter.name == "EmissionTexColor" && controller.name == "MaterialPropertyBlockController" && controller.renderers.Length == 3)
                return true;
            if (setter.name != "EmissionTexColor_Noise" || PropertyName(setter) != "_Color")
                return false;

            Transform lightColor = setter.transform.parent;
            Renderer flat = lightColor.Find("SphereScale/SphereMeshFlat")?.GetComponent<Renderer>();
            if (flat == null || controller.renderers.Length != 2)
                return false;

            // The previous sphere used one color/effect block for all three mesh variants.
            Renderer[] noise = controller.renderers;
            Renderers(controller) = new[] { flat, noise[0], noise[1] };
            PropertyName(setter) = "_EmissionTexColor";
            PropertyId(setter) = Shader.PropertyToID("_EmissionTexColor");
            controller.name = "MaterialPropertyBlockController";
            setter.name = "EmissionTexColor";

            GameObject root = lightColor.root.gameObject;
            var materials = root.GetComponent<ColdplaySphereMaterials>() ?? root.AddComponent<ColdplaySphereMaterials>();
            foreach (Renderer renderer in controller.renderers)
                materials.Apply(renderer);
            return true;
        }
    }

    // The split flat-color light is redundant once the original shared sphere block is restored.
    [HarmonyPatch]
    internal static class ColdplayFlatSphereLightPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(LightWithIdMonoBehaviour), "OnEnable");
            yield return AccessTools.DeclaredMethod(typeof(LightWithIdMonoBehaviour), "Start");
        }

        private static bool Prefix(LightWithIdMonoBehaviour __instance)
        {
            if (!ColdplaySpherePatch.IsColdplay(__instance) ||
                !(__instance is InstancedMaterialLightWithId) || __instance.name != "EmissionTexColor_Flat")
                return true;

            Transform parent = __instance.transform.parent;
            Transform noise = parent.Find("EmissionTexColor_Noise") ?? parent.Find("EmissionTexColor");
            return noise == null || !ColdplaySpherePatch.Restore(noise.GetComponent<MaterialPropertyBlockColorSetter>());
        }
    }

    internal sealed class ColdplaySphereMaterials : MonoBehaviour
    {
        private static readonly HashSet<ColdplaySphereMaterials> Owners = new HashSet<ColdplaySphereMaterials>();
        private readonly Dictionary<Material, Material> replacements = new Dictionary<Material, Material>();
        private readonly Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();

        private void Awake() => Owners.Add(this);

        internal void Apply(Renderer renderer)
        {
            if (renderer == null || originals.ContainsKey(renderer)) return;
            Material[] source = renderer.sharedMaterials;
            Material[] target = (Material[])source.Clone();
            for (int i = 0; i < target.Length; i++)
            {
                Material material = target[i];
                if (material == null || (material.name != "SpheresFlat" && material.name != "SpheresWithNoise" && material.name != "SpheresWithNoiseRBW"))
                    continue;

                if (!replacements.TryGetValue(material, out Material replacement))
                {
                    replacement = new Material(material) { name = material.name, hideFlags = HideFlags.HideAndDontSave };
                    replacement.SetColor("_Color", Color.white);
                    if (material.name == "SpheresFlat")
                    {
                        replacement.DisableKeyword("_EMISSIONCOLORTYPE_WHITEBOOST");
                        replacement.SetFloat("_EmissionColorType", 0f);
                        replacement.SetFloat("_EmissionBrightness", 0.5f);
                        replacement.SetFloat("_EmissionTexBloomIntensity", 0.5f);
                        replacement.SetFloat("_SDFNoiseIntensity", 0.7f);
                        replacement.SetFloat("_SDFNoiseScale", 0.01f);
                    }
                    else if (material.name == "SpheresWithNoise")
                    {
                        replacement.SetFloat("_EmissionBrightness", 1f);
                    }

                    if (material.name != "SpheresWithNoiseRBW")
                    {
                        replacement.SetFloat("_SDFPointIntensity", 1.14f);
                        replacement.SetColor("_EmissionTexColor", Color.white);
                    }

                    replacements.Add(material, replacement);
                }

                target[i] = replacement;
            }

            originals.Add(renderer, source);
            renderer.sharedMaterials = target;
        }

        private void OnDestroy()
        {
            Owners.Remove(this);
            foreach (var original in originals)
                if (original.Key != null) original.Key.sharedMaterials = original.Value;
            originals.Clear();
            foreach (Material replacement in replacements.Values)
                if (replacement != null) Destroy(replacement);
            replacements.Clear();
        }

        internal static void Stop()
        {
            foreach (ColdplaySphereMaterials owner in new List<ColdplaySphereMaterials>(Owners))
            {
                if (owner == null) continue;
                owner.OnDestroy();
                Destroy(owner);
            }
        }
    }
}
