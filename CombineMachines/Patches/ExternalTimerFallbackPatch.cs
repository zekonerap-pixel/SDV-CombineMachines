using CombineMachines.Helpers;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using System;
using System.Collections.Generic;
using System.Reflection;
using SObject = StardewValley.Object;

namespace CombineMachines.Patches
{
    /// <summary>
    /// Compatibility fallback for machine frameworks which assign MinutesUntilReady directly instead
    /// of going through Stardew's normal Object.OutputMachine paths.
    /// </summary>
    internal static class ExternalTimerFallbackPatch
    {
        private static readonly HashSet<SObject> CurrentlyAdjusting = new HashSet<SObject>();
        private static readonly HashSet<SObject> OutputMachineInProgress = new HashSet<SObject>();
        private static readonly HashSet<SObject> ExternalFrameworkTimerCalculationInProgress = new HashSet<SObject>();

        internal static void Entry(Harmony harmony)
        {
            MethodInfo minutesSetter = AccessTools.PropertySetter(typeof(SObject), nameof(SObject.MinutesUntilReady));
            if (minutesSetter == null)
            {
                ModEntry.Logger.Log(
                    $"{nameof(ExternalTimerFallbackPatch)}: Could not find the {nameof(SObject.MinutesUntilReady)} property setter; external timer compatibility is disabled.",
                    LogLevel.Warn
                );
                return;
            }

            harmony.Patch(
                original: minutesSetter,
                prefix: new HarmonyMethod(typeof(ExternalTimerFallbackPatch), nameof(MinutesUntilReadyPrefix)),
                postfix: new HarmonyMethod(typeof(ExternalTimerFallbackPatch), nameof(MinutesUntilReadyPostfix))
            );

            // OutputMachine already has dedicated handling in ProcessingPatches and
            // GenericMachineFallbackPatch. Keep this fallback out of those calls so a timer
            // isn't accelerated twice. The finalizer removes the guard after all postfixes run.
            MethodInfo outputMachine = AccessTools.Method(typeof(SObject), nameof(SObject.OutputMachine));
            if (outputMachine != null)
            {
                harmony.Patch(
                    original: outputMachine,
                    prefix: new HarmonyMethod(typeof(ExternalTimerFallbackPatch), nameof(OutputMachinePrefix))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(typeof(ExternalTimerFallbackPatch), nameof(OutputMachineFinalizer))
                    {
                        priority = Priority.Last
                    }
                );
            }
        }

        /// <summary>
        /// Temporarily suppress the generic setter watcher while an external framework is calculating
        /// a machine timer in multiple steps. The framework compatibility patch should call
        /// <see cref="TryApplySpeedToCurrentTimer"/> once after its calculation is complete.
        /// </summary>
        internal static void BeginExternalFrameworkTimerCalculation(SObject machine)
        {
            if (machine != null)
                ExternalFrameworkTimerCalculationInProgress.Add(machine);
        }

        internal static void EndExternalFrameworkTimerCalculation(SObject machine)
        {
            if (machine != null)
                ExternalFrameworkTimerCalculationInProgress.Remove(machine);
        }

        private static void OutputMachinePrefix(SObject __instance)
        {
            if (__instance != null)
                OutputMachineInProgress.Add(__instance);
        }

        private static Exception OutputMachineFinalizer(SObject __instance, Exception __exception)
        {
            if (__instance != null)
                OutputMachineInProgress.Remove(__instance);

            return __exception;
        }

        private static void MinutesUntilReadyPrefix(SObject __instance, out int __state)
        {
            __state = __instance?.MinutesUntilReady ?? 0;
        }

        private static void MinutesUntilReadyPostfix(SObject __instance, int __state)
        {
            try
            {
                if (__instance == null ||
                    !Context.IsWorldReady ||
                    !Game1.IsMasterGame ||
                    ModEntry.UserConfig == null ||
                    CurrentlyAdjusting.Contains(__instance) ||
                    OutputMachineInProgress.Contains(__instance) ||
                    ExternalFrameworkTimerCalculationInProgress.Contains(__instance))
                {
                    return;
                }

                int assignedMinutes = __instance.MinutesUntilReady;

                // Normal ticking reduces the value. We only care about a new/restarted cycle where
                // another framework has assigned a larger positive duration.
                if (assignedMinutes <= 0 || assignedMinutes <= __state)
                    return;

                TryApplySpeedToCurrentTimer(__instance, "external timer assignment");
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(
                    $"Unhandled Error in {nameof(ExternalTimerFallbackPatch)}.{nameof(MinutesUntilReadyPostfix)}:\n{ex}",
                    LogLevel.Error
                );
            }
        }

        /// <summary>
        /// Apply the configured IncreaseSpeed processing power to the machine's current timer exactly once.
        /// This is also used by compatibility patches which need to wait until another framework has finished
        /// a multi-step timer calculation before Combine Machines modifies the final value.
        /// </summary>
        internal static bool TryApplySpeedToCurrentTimer(SObject machine, string source)
        {
            if (machine == null ||
                !Context.IsWorldReady ||
                !Game1.IsMasterGame ||
                ModEntry.UserConfig == null ||
                CurrentlyAdjusting.Contains(machine))
            {
                return false;
            }

            int assignedMinutes = machine.MinutesUntilReady;
            if (assignedMinutes <= 0 || machine.readyForHarvest.Value)
                return false;

            if (!machine.TryGetCombinedQuantity(out int combinedQuantity) || combinedQuantity <= 1)
                return false;

            if (machine is Cask || machine is CrabPot || machine.IsScarecrow() || machine.IsTapper())
                return false;

            if (!ModEntry.UserConfig.ShouldModifyProcessingSpeed(machine))
                return false;

            double processingPower = ModEntry.UserConfig.ComputeProcessingPower(combinedQuantity);
            if (processingPower <= 1.0)
                return false;

            double desiredMinutes = assignedMinutes / processingPower;
            int newMinutes = RNGHelpers.WeightedRound(desiredMinutes);

            // Match the normal Combine Machines timing behavior and Stardew's 10-minute machine ticks.
            int smallestDigit = newMinutes % 10;
            newMinutes -= smallestDigit;
            if (RNGHelpers.RollDice(smallestDigit / 10.0))
                newMinutes += 10;

            newMinutes = Math.Max(10, newMinutes);
            if (newMinutes >= assignedMinutes)
                return false;

            try
            {
                CurrentlyAdjusting.Add(machine);
                machine.MinutesUntilReady = newMinutes;
            }
            finally
            {
                CurrentlyAdjusting.Remove(machine);
            }

            ModEntry.Logger.Log(
                $"{nameof(ExternalTimerFallbackPatch)}: Accelerated {source} for " +
                $"{machine.DisplayName} ({machine.QualifiedItemId}) from {assignedMinutes} to {newMinutes} minutes " +
                $"using combined quantity {combinedQuantity} ({(processingPower * 100.0).ToString("0.##")}% power).",
                ModEntry.InfoLogLevel
            );

            return true;
        }
    }
}
