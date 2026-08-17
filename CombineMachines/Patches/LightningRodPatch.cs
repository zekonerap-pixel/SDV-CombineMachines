using CombineMachines.Helpers;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using System;
using SObject = StardewValley.Object;

namespace CombineMachines.Patches
{
    /// <summary>
    /// Handles combined Lightning Rod output. Lightning Rods don't create their Battery Pack through
    /// the normal Object.OutputMachine path, so the generic machine output multiplier never sees it.
    /// Apply MultiplyItems once when the rod transitions to ready for harvest instead.
    /// </summary>
    internal static class LightningRodPatch
    {
        private const string LightningRodQualifiedItemId = "(BC)9";

        internal static void Entry(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.minutesElapsed)),
                prefix: new HarmonyMethod(typeof(LightningRodPatch), nameof(MinutesElapsedPrefix)),
                postfix: new HarmonyMethod(typeof(LightningRodPatch), nameof(MinutesElapsedPostfix))
            );
        }

        private static void MinutesElapsedPrefix(SObject __instance, out bool __state)
        {
            __state = __instance?.readyForHarvest.Value ?? false;
        }

        private static void MinutesElapsedPostfix(SObject __instance, bool __state)
        {
            try
            {
                if (__instance == null ||
                    !Context.IsWorldReady ||
                    !Game1.IsMasterGame ||
                    ModEntry.UserConfig == null ||
                    __state ||
                    !__instance.readyForHarvest.Value ||
                    !string.Equals(__instance.QualifiedItemId, LightningRodQualifiedItemId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!__instance.TryGetCombinedQuantity(out int combinedQuantity) || combinedQuantity <= 1)
                    return;

                if (!ModEntry.UserConfig.ShouldModifyInputsAndOutputs(__instance) || __instance.heldObject.Value == null)
                    return;

                double processingPower = ModEntry.UserConfig.ComputeProcessingPower(combinedQuantity);
                if (processingPower <= 1.0)
                    return;

                int previousStack = __instance.heldObject.Value.Stack;
                double desiredStack = previousStack * processingPower;
                int newStack = RNGHelpers.WeightedRound(desiredStack);

                if (newStack <= previousStack)
                    return;

                __instance.heldObject.Value.Stack = newStack;

                ModEntry.Logger.Log(
                    $"{nameof(LightningRodPatch)}: Multiplied completed Lightning Rod output from " +
                    $"{previousStack} to {newStack} Battery Pack(s) using combined quantity {combinedQuantity} " +
                    $"({(processingPower * 100.0).ToString("0.##")}% power).",
                    ModEntry.InfoLogLevel
                );
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(
                    $"Unhandled Error in {nameof(LightningRodPatch)}.{nameof(MinutesElapsedPostfix)}:\n{ex}",
                    LogLevel.Error
                );
            }
        }
    }
}
