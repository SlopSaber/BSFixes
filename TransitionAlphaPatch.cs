using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HMUI;
using UnityEngine;
using UnityEngine.UI;

namespace BSFixes
{
    // Screen and view controller transitions fade a CanvasGroup's alpha every frame.
    // Unity reports any CanvasGroup change to every UIBehaviour below it, and each
    // Selectable and HMUI interactable responds by walking its parents to recompute
    // whether interaction is allowed. Alpha does not affect that answer, so the
    // recompute is skipped while a transition only changes alpha.
    internal static class TransitionAlphaPatch
    {
        private static readonly MethodInfo SetAlpha = AccessTools.PropertySetter(typeof(CanvasGroup), nameof(CanvasGroup.alpha));
        private static readonly MethodInfo SetAlphaOnly = AccessTools.Method(typeof(TransitionAlphaPatch), nameof(SetTransitionAlpha));
        private static int alphaOnlyDepth;

        internal static void Apply(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(TransitionAlphaPatch), nameof(Transpiler)));
            foreach (MethodBase method in AlphaWriters())
                harmony.Patch(method, transpiler: transpiler);

            var skip = new HarmonyMethod(AccessTools.Method(typeof(TransitionAlphaPatch), nameof(SkipDuringAlphaChange)));
            foreach (Type type in new[] { typeof(Selectable), typeof(Interactable), typeof(InteractableTextMeshPro) })
            {
                MethodInfo handler = AccessTools.DeclaredMethod(type, "OnCanvasGroupChanged");
                if (handler != null)
                    harmony.Patch(handler, prefix: skip);
            }
        }

        private static IEnumerable<MethodBase> AlphaWriters()
        {
            foreach (Type owner in new[] { typeof(HMUI.Screen), typeof(ViewControllerTransitionHelpers) })
            {
                foreach (Type type in new[] { owner }.Concat(AccessTools.InnerTypes(owner)))
                {
                    foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters) continue;
                        bool writesAlpha;
                        try { writesAlpha = PatchProcessor.ReadMethodBody(method).Any(op => op.Value is MethodInfo called && called == SetAlpha); }
                        catch (Exception) { writesAlpha = false; }
                        if (writesAlpha) yield return method;
                    }
                }
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(SetAlpha))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = SetAlphaOnly;
                }
                yield return instruction;
            }
        }

        private static void SetTransitionAlpha(CanvasGroup group, float alpha)
        {
            if (group.alpha == alpha) return;
            alphaOnlyDepth++;
            try { group.alpha = alpha; }
            finally { alphaOnlyDepth--; }
        }

        private static bool SkipDuringAlphaChange() => alphaOnlyDepth == 0;
    }
}
