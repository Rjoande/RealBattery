using System;

using HarmonyLib;
using UnityEngine;

namespace RealBatteryBVBridge
{
    // Postfix for BonVoyage.RoverController.GetAvailableEC_Batteries(). Harmony patches
    // internal/private members fine without a compile-time reference to BV's assembly, but
    // the patch method itself can only see BV types as `object` since RoverController is
    // internal to BonVoyage.dll.
    internal static class CapacityPatch
    {
        internal static void Postfix(object __instance, ref double __result)
        {
            try
            {
                Vessel vessel = Traverse.Create(__instance).Field("vessel").GetValue<Vessel>();
                if (vessel == null) return;

                double? rbTotal = RealBatteryFieldAccess.ComputeCapacityEc(vessel);
                if (rbTotal.HasValue)
                    __result = rbTotal.Value;
                // else: vessel has no RealBattery parts — leave BV's own stock result alone
                // (a mixed rover with only stock ElectricCharge tanks uses the stock path).
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RealBatteryBVBridge] CapacityPatch failed, leaving BonVoyage's own result untouched: {ex}");
            }
        }
    }
}
