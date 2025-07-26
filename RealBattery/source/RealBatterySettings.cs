using KSP;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        [GameParameters.CustomFloatParameterUI("#LOC_RB_DayLenght", toolTip = "#LOC_RB_DayLenght_desc", minValue = 6f, maxValue = 24f, stepCount = 2)]
        public float DayLengthHours = 6f;

        //[GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_DischargeInterval", toolTip = "#LOC_RB_Settings_DischargeInterval_desc", minValue = 1f, maxValue = 60f, stepCount = 60, displayFormat = "F0")]
        //public float selfDischargeIntervalMin = 60f;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_BatteryWear", toolTip = "#LOC_RB_Settings_BatteryWear_desc")]
        public bool enableBatteryWear = true;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_HeatSimulation", toolTip = "#LOC_RB_Settings_HeatSimulation_desc")]
        public bool enableHeatSimulation = true;

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
        //public static double SelfDischargeInterval => (Instance?.selfDischargeIntervalMin ?? 10.0) * 60.0;
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
