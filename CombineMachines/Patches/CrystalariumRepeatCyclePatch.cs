using CombineMachines.Helpers;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Machines;
using System;
using SObject = StardewValley.Object;

namespace CombineMachines.Patches
{
    /// <summary>
    /// Handles Crystalarium output cycles which are regenerated outside the caller paths tracked by
    /// <see cref="ProcessingPatches.OutputMachinePatch"/>.
    /// </summary>
    internal static class CrystalariumRepeatCyclePatch
    {
        private const string ModDataExecutingFunctionKey = "CombineMachines_ExecutingFunction";
        private const string CrystalariumQualifiedItemId = "(BC)21";

        /// <summary>Register the Crystalarium fallback using the mod's existing Harmony instance.</summary>
        internal static void Entry(Harmony harmony)
        {
            // Routine processing diagnostics shouldn't flood the normal SMAPI console in Debug builds.
            ModEntry.InfoLogLevel = LogLevel.Trace;

            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.OutputMachine)),
                postfix: new HarmonyMethod(typeof(CrystalariumRepeatCyclePatch), nameof(OutputMachinePostfix))
            );
        }

        /// <summary>
        /// Apply the combined-machine processing power when a Crystalarium automatically starts a
        /// follow-up cycle and the normal OutputMachine patch doesn't recognize the caller.
        /// </summary>
        private static void OutputMachinePostfix(
            SObject __instance,
            MachineData machine,
            MachineOutputRule outputRule,
            Item inputItem,
            Farmer who,
            GameLocation location,
            bool probe)
        {
            try
            {
                if (probe || !Game1.IsMasterGame || ModEntry.UserConfig == null)
                    return;

                if (__instance == null || __instance.QualifiedItemId != CrystalariumQualifiedItemId)
                    return;

                if (!__instance.TryGetCombinedQuantity(out int combinedQuantity) || combinedQuantity <= 1)
                    return;

                // If a known caller is active, ProcessingPatches.OutputMachinePatch already handles
                // this invocation. This fallback is only for the Crystalarium's automatic repeat path.
                if (__instance.modData.ContainsKey(ModDataExecutingFunctionKey))
                    return;

                // OutputMachine should have created the next product by this point.
                if (__instance.heldObject.Value == null)
                    return;

                // IncreaseSpeed mode: shorten every automatically generated follow-up cycle too.
                ProcessingPatches.OutputMachinePatch.TryUpdateMinutesUntilReady(__instance, combinedQuantity);

                // MultiplyItems mode: apply the combined processing power to every follow-up output.
                // No extra gems are consumed here; a Crystalarium only needs its original inserted gem.
                if (ModEntry.UserConfig.ShouldModifyInputsAndOutputs(__instance))
                {
                    double processingPower = ModEntry.UserConfig.ComputeProcessingPower(combinedQuantity);
                    int previousStack = __instance.heldObject.Value.Stack;
                    double desiredStack = previousStack * Math.Max(1.0, processingPower);
                    __instance.heldObject.Value.Stack = RNGHelpers.WeightedRound(desiredStack);
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(
                    $"Unhandled Error in {nameof(CrystalariumRepeatCyclePatch)}.{nameof(OutputMachinePostfix)}:\n{ex}",
                    LogLevel.Error
                );
            }
        }
    }
}
