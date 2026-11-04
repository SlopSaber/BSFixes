using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BSFixes
{
    // The scene-transition loading spinner is drawn 0.75 m in front of each
    // camera, 0.28 m right and 0.29 m down, which puts it in the lower-right
    // corner of both the headset view and desktop cameras. Drop the right and
    // down offsets so it is centred in front of whichever camera renders it.
    [HarmonyPatch]
    internal static class CenteredLoadingSpinnerPatch
    {
        private const float OffsetRight = 0.28f;
        private const float OffsetDown = 0.29f;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("MainEffectLoadingIndicatorPass"), "RecordRenderGraph");
        }

        private static bool Prepare() => TargetMethod() != null;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int right = list.FindIndex(code => IsFloat(code, OffsetRight));
            int down = list.FindIndex(code => IsFloat(code, OffsetDown));
            // Change nothing unless both offsets appear exactly once.
            if (right < 0 || down < 0 || list.FindLastIndex(code => IsFloat(code, OffsetRight)) != right
                || list.FindLastIndex(code => IsFloat(code, OffsetDown)) != down)
                return list;
            list[right].operand = 0f;
            list[down].operand = 0f;
            return list;
        }

        private static bool IsFloat(CodeInstruction code, float value)
        {
            return code.opcode == OpCodes.Ldc_R4 && code.operand is float operand && operand == value;
        }
    }
}
