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
    /// PFM can calculate a machine timer in multiple assignments and can optionally subtract the
    /// current time of day from long-running producer rules. Combine Machines must wait until PFM
    /// finishes those assignments, then apply processing power once. For SubtractTimeOfDay rules,
    /// the declared base duration is used so repeated accelerated cycles don't get progressively
    /// shorter as the in-game clock advances.
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
                        method.GetParameters().Any(parameter => parameter.Name == "producerRule") &&
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

        private static Exception ProduceOutputFinalizer(
            SObject producer,
            object producerRule,
            bool probe,
            object __result,
            Exception __exception)
        {
            try
            {
                if (!probe && producer != null && __exception == null && __result != null)
                {
                    // PFM's SubtractTimeOfDay is useful for aligning long vanilla-style cycles to the
                    // clock, but after a 20x/80x speed-up it makes each repeated cycle shorter than the
                    // previous one. In that case use the rule's declared duration as the stable source.
                    if (TryGetStableBaseDuration(producerRule, __result, out int baseDurationMinutes))
                    {
                        ExternalTimerFallbackPatch.TryApplySpeedToBaseDuration(
                            producer,
                            baseDurationMinutes,
                            "Producer Framework Mod base timer"
                        );
                    }
                    else
                    {
                        // Rules without SubtractTimeOfDay can safely use PFM's completed final timer.
                        ExternalTimerFallbackPatch.TryApplySpeedToCurrentTimer(
                            producer,
                            "Producer Framework Mod final timer"
                        );
                    }
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

        /// <summary>
        /// Get the duration PFM declared for the selected output before SubtractTimeOfDay modifies it.
        /// Reflection keeps this integration optional, so Combine Machines doesn't need a compile-time
        /// dependency on Producer Framework Mod.
        /// </summary>
        private static bool TryGetStableBaseDuration(object producerRule, object outputConfig, out int baseDurationMinutes)
        {
            baseDurationMinutes = 0;

            if (!TryGetBoolMember(producerRule, "SubtractTimeOfDay", out bool subtractTimeOfDay) || !subtractTimeOfDay)
                return false;

            // OutputConfig.MinutesUntilReady overrides ProducerRule.MinutesUntilReady when present.
            if (TryGetPositiveIntMember(outputConfig, "MinutesUntilReady", out baseDurationMinutes))
                return true;

            return TryGetPositiveIntMember(producerRule, "MinutesUntilReady", out baseDurationMinutes);
        }

        private static bool TryGetBoolMember(object instance, string memberName, out bool value)
        {
            value = false;
            object rawValue = GetMemberValue(instance, memberName);
            if (rawValue == null)
                return false;

            if (rawValue is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            return bool.TryParse(rawValue.ToString(), out value);
        }

        private static bool TryGetPositiveIntMember(object instance, string memberName, out int value)
        {
            value = 0;
            object rawValue = GetMemberValue(instance, memberName);
            if (rawValue == null)
                return false;

            if (rawValue is int intValue)
            {
                value = intValue;
                return value > 0;
            }

            return int.TryParse(rawValue.ToString(), out value) && value > 0;
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null)
                return null;

            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property.GetValue(instance);

            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }
    }
}
