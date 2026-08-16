using CombineMachines.Helpers;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Objects;
using System;
using System.Runtime.CompilerServices;
using SObject = StardewValley.Object;

namespace CombineMachines.Patches
{
    /// <summary>
    /// Compatibility fallback for data-driven machines whose OutputMachine calls happen outside
    /// the vanilla caller paths tracked by <see cref="ProcessingPatches.OutputMachinePatch"/>.
    ///
    /// Unknown callers are handled conservatively: outputs which Stardew recalculates on collection
    /// keep their combined output multiplier, while other unknown processing paths are accelerated
    /// instead of multiplying inputs/outputs that may be consumed by custom mod code.
    /// </summary>
    internal static class GenericMachineFallbackPatch
    {
        private const string ModDataExecutingFunctionKey = "CombineMachines_ExecutingFunction";
        private const string CrystalariumQualifiedItemId = "(BC)21";

        // RecalculateOnCollect may invoke OutputMachine repeatedly if collection fails (for example,
        // because the player's inventory is full). Track the actual output instance without writing
        // modData onto the produced item, since that could interfere with normal item stacking.
        private static readonly ConditionalWeakTable<Item, object> ModifiedRecalculatedOutputs = new();

        internal static void Entry(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.OutputMachine)),
                postfix: new HarmonyMethod(typeof(GenericMachineFallbackPatch), nameof(OutputMachinePostfix))
            );
        }

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
                if (probe || !Game1.IsMasterGame || ModEntry.UserConfig == null || __instance == null)
                    return;

                if (!__instance.TryGetCombinedQuantity(out int combinedQuantity) || combinedQuantity <= 1)
                    return;

                // A tracked caller means ProcessingPatches.OutputMachinePatch already handled this call.
                if (__instance.modData.ContainsKey(ModDataExecutingFunctionKey))
                    return;

                // Only use this fallback for actual Stardew 1.6 data-driven machines.
                if (__instance.GetMachineData() == null)
                    return;

                // These machine types have dedicated logic elsewhere in the mod.
                if (__instance.IsTapper() || __instance is Cask || __instance is CrabPot || __instance.IsScarecrow())
                    return;

                // Crystalariums have a dedicated repeat-cycle patch which can safely preserve
                // MultiplyItems semantics without consuming extra gems on automatic cycles.
                if (string.Equals(__instance.QualifiedItemId, CrystalariumQualifiedItemId, StringComparison.Ordinal))
                    return;

                // OutputMachine should have produced a held item by this point.
                if (__instance.heldObject.Value == null)
                    return;

                // Some machines (notably Bee Houses) regenerate their output immediately before the
                // player collects it. That replacement can discard a multiplier previously applied
                // when the output first became ready, so reapply it to the freshly recalculated item.
                if (outputRule?.RecalculateOnCollect == true)
                {
                    TryApplyRecalculatedOutputMultiplier(__instance, combinedQuantity);
                    return;
                }

                if (TryApplyCompatibilitySpeed(__instance, combinedQuantity, out int previousMinutes, out int newMinutes, out double durationMultiplier))
                {
                    ModEntry.Logger.Log(
                        $"{nameof(GenericMachineFallbackPatch)}: Applied compatibility fallback to {__instance.DisplayName} " +
                        $"({__instance.QualifiedItemId}); MinutesUntilReady {previousMinutes} -> {newMinutes} " +
                        $"({(durationMultiplier * 100.0).ToString("0.##")}% duration).",
                        ModEntry.InfoLogLevel
                    );
                }
                else
                {
                    ModEntry.Logger.Log(
                        $"{nameof(GenericMachineFallbackPatch)}: Detected unsupported OutputMachine caller for combined machine " +
                        $"{__instance.DisplayName} ({__instance.QualifiedItemId}), but no positive processing duration was available to accelerate.",
                        LogLevel.Trace
                    );
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(
                    $"Unhandled Error in {nameof(GenericMachineFallbackPatch)}.{nameof(OutputMachinePostfix)}:\n{ex}",
                    LogLevel.Error
                );
            }
        }

        /// <summary>
        /// Preserve MultiplyItems behavior for machine rules which replace/recalculate their output
        /// immediately before collection (for example Bee Houses).
        /// </summary>
        private static bool TryApplyRecalculatedOutputMultiplier(SObject machine, int combinedQuantity)
        {
            if (!ModEntry.UserConfig.ShouldModifyInputsAndOutputs(machine))
                return false;

            Item output = machine.heldObject.Value;
            if (output == null || ModifiedRecalculatedOutputs.TryGetValue(output, out _))
                return false;

            double processingPower = ModEntry.UserConfig.ComputeProcessingPower(combinedQuantity);
            int previousStack = output.Stack;
            double desiredStack = previousStack * Math.Max(1.0, processingPower);
            int newStack = RNGHelpers.WeightedRound(desiredStack);

            output.Stack = newStack;
            ModifiedRecalculatedOutputs.Add(output, new object());

            ModEntry.LogTrace(
                combinedQuantity,
                machine,
                machine.TileLocation,
                "HeldObject.Stack (RecalculateOnCollect)",
                previousStack,
                desiredStack,
                newStack,
                processingPower
            );

            return true;
        }

        /// <summary>
        /// Apply combined processing power as a duration reduction without relying on the configured
        /// processing mode. This is intentionally used only for unknown callers, where multiplying
        /// ingredients and outputs cannot be done safely.
        /// </summary>
        private static bool TryApplyCompatibilitySpeed(
            SObject machine,
            int combinedQuantity,
            out int previousMinutes,
            out int newMinutes,
            out double durationMultiplier)
        {
            previousMinutes = machine.MinutesUntilReady;
            newMinutes = previousMinutes;
            durationMultiplier = 1.0;

            if (previousMinutes <= 0 || machine.readyForHarvest.Value)
                return false;

            double processingPower = ModEntry.UserConfig.ComputeProcessingPower(combinedQuantity);
            if (processingPower <= 1.0)
                return false;

            durationMultiplier = 1.0 / processingPower;
            double targetMinutes = previousMinutes * durationMultiplier;
            newMinutes = RNGHelpers.WeightedRound(targetMinutes);

            // Stardew checks most machine timers in 10-minute increments, so preserve the same
            // weighted rounding behavior as the normal Combine Machines processing patch.
            int smallestDigit = newMinutes % 10;
            newMinutes -= smallestDigit;
            if (RNGHelpers.RollDice(smallestDigit / 10.0))
                newMinutes += 10;

            // Avoid instant completion, which can result in machines producing no held output.
            newMinutes = Math.Max(10, newMinutes);

            if (newMinutes >= previousMinutes)
                return false;

            machine.MinutesUntilReady = newMinutes;
            if (newMinutes <= 0)
                machine.readyForHarvest.Value = true;

            return true;
        }
    }
}
