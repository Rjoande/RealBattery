using System;

using HarmonyLib;
using UnityEngine;

namespace RealBatteryBVBridge
{
    // Postfix for BonVoyage.BVController.ProcessResources(). Runs after BV's own fuel/
    // propellant processing; drains the EC deficit accumulated by the rover's virtual battery
    // accounting (MaxUsedEC - CurrentEC) from RealBattery's StoredCharge, and resets CurrentEC
    // to MaxUsedEC afterwards so the same deficit is never drained twice (the bug fixed in the
    // deprecated custom-DLL fork this bridge replaces).
    //
    // Note (2026-08, campaign B): an earlier in-game test found this drain producing no effect
    // on a live BV 1.5.1.3 install; the root cause was never conclusively isolated before
    // RealBattery pivoted to proposing native BonVoyage support instead (see
    // PLAN_BV_API_Integration.md §B2). This bridge remains the fallback for players on a BV
    // build without native support. If it resurfaces, the per-branch diagnostic Debug.Log
    // calls this method used during that investigation can be restored from git history.
    internal static class DrainPatch
    {
        internal static void Postfix(object __instance)
        {
            try
            {
                if (__instance.GetType().Name != "RoverController") return;

                Traverse instanceTraverse = Traverse.Create(__instance);
                Vessel vessel = instanceTraverse.Field("vessel").GetValue<Vessel>();
                if (vessel == null) return;

                Traverse batteriesTraverse = instanceTraverse.Field("batteries");
                if (!batteriesTraverse.FieldExists()) return;

                bool use = ReadBoolWithFallback(batteriesTraverse, "Use", "UseBatteries");
                if (!use) return;

                double maxUsedEC = batteriesTraverse.Field("MaxUsedEC").GetValue<double>();
                if (maxUsedEC <= 0.0) return;

                double currentEC = batteriesTraverse.Field("CurrentEC").GetValue<double>();
                double ecUsed = maxUsedEC - currentEC;
                if (ecUsed <= 0.01) return; // nothing to drain

                double scToDrain = ecUsed / RealBatteryFieldAccess.EcPerSc;
                bool drained = RealBatteryFieldAccess.DrainAndCreditWear(vessel, scToDrain);

                if (drained)
                {
                    batteriesTraverse.Field("CurrentEC").SetValue(maxUsedEC); // anti-double-drain
                    Debug.Log($"[RealBatteryBVBridge] '{vessel.vesselName}': drained {scToDrain:F3} kWh of StoredCharge for {ecUsed:F1} EC used by BonVoyage.");
                }
                // else: vessel has no RealBattery parts — leave BV's own CurrentEC bookkeeping
                // alone (shouldn't normally happen if GetAvailableEC_Batteries() already
                // reported an RB-derived capacity for this same vessel, but stay defensive).
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RealBatteryBVBridge] DrainPatch failed: {ex}");
            }
        }

        private static bool ReadBoolWithFallback(Traverse batteriesTraverse, string primaryName, string fallbackName)
        {
            Traverse f = batteriesTraverse.Field(primaryName);
            if (f.FieldExists()) return f.GetValue<bool>();

            f = batteriesTraverse.Field(fallbackName);
            return f.FieldExists() && f.GetValue<bool>();
        }
    }
}
