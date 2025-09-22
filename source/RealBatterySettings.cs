using KSP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RealBattery
{
    public class RealBatterySettings : GameParameters.CustomParameterNode
    {
        [GameParameters.CustomParameterUI("#LOC_RB_Log", toolTip = "#LOC_RB_Log_desc")]
        public bool enableDebugLogging = false;

        [GameParameters.CustomParameterUI("#LOC_RB_VerboseLog", toolTip = "#LOC_RB_VerboseLog_desc")]
        public bool enableVerboseLoadLogs = false;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_SelfDischarge", toolTip = "#LOC_RB_Settings_SelfDischarge_desc")]
        public bool enableSelfDischarge = true;

        [GameParameters.CustomFloatParameterUI("#LOC_RB_DayLenght", toolTip = "#LOC_RB_DayLenght_desc", minValue = 6f, maxValue = 24f, displayFormat = "F0", stepCount = 2)]
        public float DayLengthHours = 6f;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_BatteryWear", toolTip = "#LOC_RB_Settings_BatteryWear_desc")]
        public bool enableBatteryWear = true;

        // === LEGACY | Will be removed in v2.3 ===

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_HeatSimulation", toolTip = "#LOC_RB_Settings_HeatSimulation_desc")]
        public bool enableHeatSimulation = true;

        // === READY FOR v2.3 ===

        // UI: "Heat Production"
        /*[GameParameters.CustomParameterUI("#LOC_RB_Settings_HeatProduction", toolTip = "#LOC_RB_Settings_HeatProduction_tip")]
        public bool enableHeatSimulation = true;

        // UI: "Use SystemHeat"
        [GameParameters.CustomParameterUI("#LOC_RB_Settings_UseSystemHeat", toolTip = "#LOC_RB_Settings_UseSystemHeat_tip")]
        public bool useSystemHeat = true;

        // Helper: detect SystemHeat presence at runtime
        internal static bool SystemHeatAvailable =>
            AssemblyLoader.loadedAssemblies.Any(a => a.assembly.GetName().Name == "SystemHeat");

        // Enable/disable fields dynamically
        public override bool Enabled(MemberInfo member, GameParameters parameters)
        {
            if (member.Name == nameof(useSystemHeat))
                return enableHeatSimulation && SystemHeatAvailable; // hide/disable when not applicable
            return true;
        }

        public override bool Interactible(MemberInfo member, GameParameters parameters)
        {
            if (member.Name == nameof(useSystemHeat))
                return enableHeatSimulation && SystemHeatAvailable; // greyed out if parent OFF or SH missing
            return true;
        }

        // --- Polar surface simplification (configurable in difficulty menu) ---

        [GameParameters.CustomFloatParameterUI(
            "#LOC_RB_Settings_PolarThreshold",   // localized title
            minValue = 0f,
            maxValue = 90f,
            displayFormat = "F0",
            stepCount = 90,
            toolTip = "#LOC_RB_Settings_PolarThreshold_tip"
        )]
        public float PolarLatitudeThresholdDeg = 66f;

        [GameParameters.CustomFloatParameterUI(
            "#LOC_RB_Settings_PolarFrac",       // localized title
            minValue = 0f,
            maxValue = 1f,
            displayFormat = "F2",
            stepCount = 100,
            toolTip = "#LOC_RB_Settings_PolarFrac_tip"
        )]
        public float PolarConstantLitFrac = 0.35f;*/

        public override string Title => "RealBattery";
        public override string Section => "RealBattery";
        public override string DisplaySection => "RealBattery";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        public static RealBatterySettings Instance => HighLogic.CurrentGame?.Parameters?.CustomParams<RealBatterySettings>();
       
        public static bool UseSelfDischarge => Instance?.enableSelfDischarge ?? true;
        public static bool UseBatteryWear => Instance?.enableBatteryWear ?? true;
        public static bool UseHeatSimulation => Instance?.enableHeatSimulation ?? true;
        //public static bool UseSystemHeat => Instance?.useSystemHeat ?? true;
        public double GetHoursPerDay()
        {
            return DayLengthHours;
        }
    }

    public static class RBLog
    {
        public static void Log(string message)
        {
            if (RealBatterySettings.Instance?.enableDebugLogging == true)
                Debug.Log("[RealBattery] " + message);
        }

        public static void Warn(string message)
        {
            if (RealBatterySettings.Instance?.enableDebugLogging == true)
                Debug.LogWarning("[RealBattery] " + message);
        }

        public static void Error(string message)
        {
            Debug.LogError("[RealBattery] " + message);
        }

        public static void Verbose(string message)
        {
            if (RealBatterySettings.Instance?.enableVerboseLoadLogs == true)
                Debug.Log("[RealBattery:runtime] " + message);
        }
    }
}
