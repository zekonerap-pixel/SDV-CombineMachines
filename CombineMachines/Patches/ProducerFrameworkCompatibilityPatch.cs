using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Linq;
using System.Reflection;
using SObject = StardewValley.Object;

namespace CombineMachines.Patches
{
    /// <summary>
    /// Optional compatibility for Producer Framework Mod (PFM).
    ///
    /// PFM can calculate a machine timer in multiple assignments (for example, applying
    /// SubtractTimeOfDay after setting the base duration). If Combine Machines accelerates an
    /// intermediate assignment, PFM can subsequently clamp the timer to one minute. This patch
    /// lets PFM finish its calculation first and then applies the combined processing speed once
    /// to the final timer.
    /// </summary>
    internal static class ProducerFrameworkCompatibilityPatch
    {
        private const string ProducerFrameworkModId = "Digus.ProducerFrameworkMod";
        private const string ProducerRuleControllerTypeName = "ProducerFrameworkMod.Controllers.ProducerRuleController";
        private static bool IsPatched;

        internal static void Entry(IModHelper helper, Harmony harmony)
        {
            // Producer Framework may load after Combine Machines. GameLaunched is the first point at
            // which all mod assemblies are guaranteed to have been loaded.
            helper.Events.GameLoop.GameLaunched += (sender, e) => TryPatch(helper, harmony);
        }

        private static void TryPatch(IModHelper helper, Harmony harmony)
        {
            if (IsPatched || !helper.ModRegistry.IsLoaded(ProducerFrameworkModId))
                return;

            try
            {
                Type controllerType = AccessTools.TypeByName(ProducerRuleControllerTypeName);
                MethodInfo produceOutput = controllerType?
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method =>
                        method.Name == "ProduceOutput" &&
                        method.GetParameters().Any(parameter => parameter.Name == "producer") &&
                        method.GetParameters().Any(parameter => parameter.Name == "probe"));

                if (produceOutput == null)
                {
                    ModEntry.Logger.Log(
                        $"{nameof(ProducerFrameworkCompatibilityPatch)}: Producer Framework Mod is installed, " +
                        "but ProducerRuleController.ProduceOutput could not be found. PFM timer compatibility was not enabled.",
                        LogLevel.Warn
                    );
                    return;
                }

                harmony.Patch(
                    original: produceOutput,
                    prefix: new HarmonyMethod(typeof(ProducerFrameworkCompatibilityPatch), nameof(ProduceOutputPrefix))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(typeof(ProducerFrameworkCompatibilityPatch), nameof(ProduceOutputFinalizer))
                    {
                        priority = Priority.Last
                    }
                );

                IsPatched = true;
                ModEntry.Logger.Log(
                    $"{nameof(ProducerFrameworkCompatibilityPatch)}: Enabled Producer Framework Mod timer compatibility.",
                    LogLevel.Trace
                );
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(
                    $"Unhandled Error while enabling {nameof(ProducerFrameworkCompatibilityPatch)}:\n{ex}",
                    LogLevel.Error
                );
            }
        }

        private static void ProduceOutputPrefix(SObject producer, bool probe)
        {
            if (!probe && producer != null)
                ExternalTimerFallbackPatch.BeginExternalFrameworkTimerCalculation(producer);
        }

        private static Exception ProduceOutputFinalizer(SObject producer, bool probe, Exception __exception)
        {
            try
            {
                // PFM has now completed all of its own duration calculations, including
                // SubtractTimeOfDay. Only the final timer should be accelerated.
                if (!probe && producer != null && __exception == null)
                {
                    ExternalTimerFallbackPatch.TryApplySpeedToCurrentTimer(
                        producer,
                        "Producer Framework Mod final timer"
                    );
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(
                    $"Unhandled Error in {nameof(ProducerFrameworkCompatibilityPatch)}.{nameof(ProduceOutputFinalizer)}:\n{ex}",
                    LogLevel.Error
                );
            }
            finally
            {
                if (!probe && producer != null)
                    ExternalTimerFallbackPatch.EndExternalFrameworkTimerCalculation(producer);
            }

            return __exception;
        }
    }
}
