using System;
using System.Reflection;

using HarmonyLib;
using UnityEngine;

namespace RealBatteryBVBridge
{
    // Optional, self-disabling compatibility bridge between BonVoyage and RealBattery.
    // Only does anything if BOTH BonVoyage and RealBattery are installed, and only applies
    // patches if BonVoyage does not already expose native RealBattery support (the
    // BonVoyage.RealBatterySupport marker shipped by RealBattery's own upstream PR — see
    // M6_BonVoyage_PR/ in the RealBattery repo). A missing 0Harmony.dll or an incompatible
    // BonVoyage version only disables this bridge; RealBattery.dll itself never references
    // this assembly and is entirely unaffected either way.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class BridgeBootstrap : MonoBehaviour
    {
        private const string HarmonyId = "RealBattery.BVBridge";

        private void Start()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RealBatteryBVBridge] Bootstrap failed, bridge disabled: {ex}");
            }
        }

        private static void Run()
        {
            if (!IsAssemblyLoaded("RealBattery"))
            {
                Debug.Log("[RealBatteryBVBridge] RealBattery not found, bridge inactive.");
                return;
            }

            Assembly bvAssembly = FindAssembly("BonVoyage");
            if (bvAssembly == null)
            {
                Debug.Log("[RealBatteryBVBridge] BonVoyage not found, bridge inactive.");
                return;
            }

            if (HasNativeSupport(bvAssembly))
            {
                Debug.Log("[RealBatteryBVBridge] BonVoyage already has native RealBattery support (BonVoyage.RealBatterySupport marker found) — bridge inactive, no patches applied.");
                return;
            }

            var harmony = new Harmony(HarmonyId);
            int patchesApplied = 0;

            MethodInfo capacityMethod = AccessTools.Method("BonVoyage.RoverController:GetAvailableEC_Batteries");
            if (capacityMethod != null)
            {
                harmony.Patch(capacityMethod, postfix: new HarmonyMethod(typeof(CapacityPatch), nameof(CapacityPatch.Postfix)));
                patchesApplied++;
            }
            else
            {
                Debug.LogWarning("[RealBatteryBVBridge] BonVoyage.RoverController.GetAvailableEC_Batteries not found (incompatible BV version?) — capacity patch skipped.");
            }

            MethodInfo drainMethod = AccessTools.Method("BonVoyage.BVController:ProcessResources");
            if (drainMethod != null)
            {
                harmony.Patch(drainMethod, postfix: new HarmonyMethod(typeof(DrainPatch), nameof(DrainPatch.Postfix)));
                patchesApplied++;
            }
            else
            {
                Debug.LogWarning("[RealBatteryBVBridge] BonVoyage.BVController.ProcessResources not found (incompatible BV version?) — drain patch skipped.");
            }

            Debug.Log($"[RealBatteryBVBridge] Bridge active: {patchesApplied}/2 patches applied against BonVoyage {bvAssembly.GetName().Version}.");
        }

        private static bool IsAssemblyLoaded(string name) => FindAssembly(name) != null;

        private static Assembly FindAssembly(string name)
        {
            foreach (var loaded in AssemblyLoader.loadedAssemblies)
            {
                if (loaded.name == name)
                    return loaded.assembly;
            }
            return null;
        }

        private static bool HasNativeSupport(Assembly bvAssembly)
        {
            Type markerType = bvAssembly.GetType("BonVoyage.RealBatterySupport");
            if (markerType == null) return false;

            FieldInfo enabledField = markerType.GetField("Enabled", BindingFlags.Public | BindingFlags.Static);
            object value = enabledField?.GetValue(null);
            return value is bool b && b;
        }
    }
}
