using KSP;
using KSP.Localization;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RealBattery
{
    // ================================================================
    //  RealBatterySettings (Facade)
    //  - Static facade that aggregates values from the three tabs:
    //    RBParams_Simulation, RBParams_Advanced and RBParams_Debug.
    //  - Keep method/property names stable for the rest of the codebase.
    // ================================================================
    public static class RealBatterySettings
    {
        // ----- Convenience accessors to the active game's parameter nodes -----
        private static RBParams_Simulation S => HighLogic.CurrentGame?.Parameters?.CustomParams<RBParams_Simulation>();
        private static RBParams_Advanced A => HighLogic.CurrentGame?.Parameters?.CustomParams<RBParams_Advanced>();
        private static RBParams_Debug D => HighLogic.CurrentGame?.Parameters?.CustomParams<RBParams_Debug>();

        // ----- Core Settings -----
        /*public static bool UseLowPowerMessage => A?.enableLowPowerMessage ?? true;*/
        public static bool UseLowPowerAlarm => S?.enableLowPowerAlarm ?? true;
        public static bool UseKAC => S?.useKACAlarms ?? true;
        public static bool EnableSelfDischarge => S?.enableSelfDischarge ?? true;
        public static bool EnableBatteryWear => S?.enableBatteryWear ?? true;
        public static bool EnableHeatSimulation => S?.enableHeatSimulation ?? true;
        public static bool UseSystemHeat => (S?.useSystemHeat ?? true) && SystemHeatAvailable;
        public static bool EnableThermalRunaway => S?.enableThermalRunaway ?? true;
        public static bool EnableEVARefurbush => S?.enableEVARefurbish ?? true;

        // ----- Advanced -----
        public static float KeepWarmFrac => A?.KeepWarmFrac ?? 0.05f;
        public static float WarmupSeconds => A?.WarmupSeconds ?? 60f;
        public static double LowPowerMinWindowSeconds => Math.Max(0.0, (A?.LowPowerMinWindow ?? 5f) * 60.0);
        public static double LowPowerLeadSeconds => Math.Max(0.0, (A?.LowPowerLead ?? 2f) * 60.0);
        public static float RunawayBaseMagnitude => A?.runawayBaseMagnitude ?? 0.25f;
        public static float SelfRunawayChancePerHour => A?.SelfRunawayChancePerHour ?? 0.05f;

        public static float DayLengthHours => A?.DayLengthHours ?? 6f;
        public static float PolarLatitudeThresholdDeg => A?.PolarLatitudeThresholdDeg ?? 80f;
        public static float PolarConstantLitFrac => A?.PolarConstantLitFrac ?? 0.50f;


        // ----- Logging -----
        public static bool DisablePCM => D?.disablePCM ?? false;
        public static bool EnableDebugLogging => D?.enableDebugLogging ?? false;
        public static bool EnableVerboseLoadLogs => D?.enableVerboseLoadLogs ?? false;

        // ----- External mods availability -----
        public static bool SystemHeatAvailable
        {
            get
            {
                try { return AssemblyLoader.loadedAssemblies.Any(a => a.name == "SystemHeat"); }
                catch { return false; }
            }
        }
        public static bool KACIsInstalled()
        {
            try { return AssemblyLoader.loadedAssemblies.Any(a => a.name == "KerbalAlarmClock"); }
            catch { return false; }
        }

        // ----- Utilities (logging) -----
        public static void Verbose(string msg) { RBLog.Verbose(msg); }
        public static void Log(string msg) { RBLog.Info(msg); }
        public static void Warn(string msg) { RBLog.Warn(msg); }
        public static void Error(string msg) { RBLog.Error(msg); }

        // ----- Helpers used elsewhere in the mod -----
        public static float GetHoursPerDay()
        {
            var configured = A?.DayLengthHours ?? 0f;
            if (configured > 0.0001f) return configured;
            return DetectHomeworldDayLengthHours();
        }
        private static float DetectHomeworldDayLengthHours()
        {
            try
            {
                // FlightGlobals.GetHomeBody() is the robust way to get the player's home world.
                var home = FlightGlobals.GetHomeBody();
                if (home != null)
                {
                    // Use solar day if defined; otherwise sidereal rotation period.
                    double seconds =
                    #if KSP122 || KSP121 || KSP120
                    home.solarDayLength > 0 ? home.solarDayLength :
                    #endif
                    (home.rotationPeriod > 0 ? home.rotationPeriod : 21600.0);
                    float hours = (float)(seconds / 3600.0);
                    if (EnableVerboseLoadLogs)
                        Debug.Log($"[RealBattery][Settings] Auto day length = {hours:F2} h (home='{home.bodyName}').");
                    return Mathf.Clamp(hours, 0.1f, 48f);
                }
            }
            catch { /* ignore and fallback */ }
            // Fallback to stock Kerbin day (6 hours)
            if (EnableVerboseLoadLogs)
                Debug.Log("[RealBattery][Settings] Auto day detection failed, using fallback 6.00 h.");
            return 6f;
        }
}

    // ================================================================
    //  TAB 1: Core Simulation
    // ================================================================
    public class RBParams_Simulation : GameParameters.CustomParameterNode
    {
        public override string Section => "RealBattery";
        public override string DisplaySection => "RealBattery";
        public override int SectionOrder => 1;
        public override string Title => "#LOC_RB_Settings_Tab_Simulation";
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        // --- Battery model ---
        [GameParameters.CustomParameterUI("#LOC_RB_Settings_SelfDischarge", toolTip = "#LOC_RB_Settings_SelfDischarge_desc")]
        public bool enableSelfDischarge = true;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_BatteryWear", toolTip = "#LOC_RB_Settings_BatteryWear_desc")]
        public bool enableBatteryWear = true;

        [GameParameters.CustomParameterUI("#LOC_RB_Set_EnableEVARefurbish", toolTip = "#LOC_RB_Set_EnableEVARefurbish_desc", autoPersistance = true)]
        public bool enableEVARefurbish = true;

        // --- Heat simulation & SystemHeat ---
        [GameParameters.CustomParameterUI("#LOC_RB_Settings_HeatProduction", toolTip = "#LOC_RB_Settings_HeatProduction_tip")]
        public bool enableHeatSimulation = true;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_UseSystemHeat", toolTip = "#LOC_RB_Settings_UseSystemHeat_tip")]
        public bool useSystemHeat = true;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_ThermalRunaway", toolTip = "#LOC_RB_Settings_ThermalRunaway_tip")]
        public bool enableThermalRunaway = true;

        [GameParameters.CustomParameterUI("#LOC_RB_Settings_LowPowerAlarm", toolTip = "#LOC_RB_Settings_LowPowerAlarm_tip")]
        public bool enableLowPowerAlarm = false;

        // Child option: KAC backend (visible only if KAC installed + alarm enabled)
        [GameParameters.CustomParameterUI("#LOC_RB_Settings_UseKAC", toolTip = "#LOC_RB_Settings_UseKAC_tip")]
        public bool useKACAlarms = false;

        public override bool Enabled(MemberInfo member, GameParameters parameters)
        {
            if (member.Name == nameof(enableEVARefurbish))
                return enableBatteryWear;

            if (member.Name == nameof(useSystemHeat))
                return enableHeatSimulation && RealBatterySettings.SystemHeatAvailable;

            if (member.Name == nameof(enableThermalRunaway))
                return enableHeatSimulation;

            if (member.Name == nameof(useKACAlarms))
                return enableLowPowerAlarm && RealBatterySettings.KACIsInstalled();

            return true;
        }
    }

    // ================================================================
    //  TAB 2: Advanced
    // ================================================================
    public class RBParams_Advanced : GameParameters.CustomParameterNode
    {
        public override string Section => "RealBattery";
        public override string DisplaySection => "RealBattery";
        public override int SectionOrder => 2;
        public override string Title => "#LOC_RB_Settings_Tab_Advanced";
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_KeepWarmFrac", toolTip = "#LOC_RB_Settings_KeepWarmFrac_tip", minValue = 0f, maxValue = 0.50f, stepCount = 51, displayFormat = "F2")]
        public float KeepWarmFrac = 0.05f;
        
        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_WarmupSeconds", toolTip = "#LOC_RB_Settings_WarmupSeconds_tip",minValue = 5f, maxValue = 600f, stepCount = 596, displayFormat = "F0")]
        public float WarmupSeconds = 60f;

        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_LowPowerMinWindow", toolTip = "#LOC_RB_Settings_LowPowerMinWindow_tip", minValue = 0f, maxValue = 120f, stepCount = 121)]
        public float LowPowerMinWindow = 5f;

        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_LowPowerLead", toolTip = "#LOC_RB_Settings_LowPowerLead_tip", minValue = 0f, maxValue = 120f, stepCount = 121)]
        public float LowPowerLead = 2f;

        // --- Runaway Magnitude Modifier ---
        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_RunawayMagnitude_title", toolTip = "#LOC_RB_Settings_RunawayMagnitude_desc", minValue = 0.1f, maxValue = 2.0f, stepCount = 20, displayFormat = "F2")]
        public float runawayBaseMagnitude = 0.25f;

        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_SelfRunawayChance", toolTip = "#LOC_RB_Settings_SelfRunawayChance_tip", minValue = 0f, maxValue = 0.10f, stepCount = 11, displayFormat = "P0")]
        public float SelfRunawayChancePerHour = 0.05f; // default 5%/h
        
        // --- Polar constant lighting approximation (for solar) ---
        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_PolarThreshold", toolTip = "#LOC_RB_Settings_PolarThreshold_tip", minValue = 60f, maxValue = 89f, stepCount = 30)]
        public float PolarLatitudeThresholdDeg = 80f;

        [GameParameters.CustomFloatParameterUI("#LOC_RB_Settings_PolarFrac", toolTip = "#LOC_RB_Settings_PolarFrac_tip", minValue = 0f, maxValue = 1f, stepCount = 21, displayFormat = "F2")]
        public float PolarConstantLitFrac = 0.80f;

        // --- Time base ---
        [GameParameters.CustomFloatParameterUI("#LOC_RB_DayLenght", toolTip = "#LOC_RB_DayLenght_desc", minValue = 0f, maxValue = 24f, stepCount = 241, displayFormat = "F1")]
        public float DayLengthHours = 0f; // 0 = Auto-detect
    }

    // ================================================================
    //  TAB 3: Debug
    // ================================================================
    public class RBParams_Debug : GameParameters.CustomParameterNode
    {
        public override string Section => "RealBattery";
        public override string DisplaySection => "RealBattery";
        public override int SectionOrder => 3;
        public override string Title => "#LOC_RB_Settings_Tab_Debug";
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        // Disable PCM logic for debug
        [GameParameters.CustomParameterUI("#LOC_RB_DisablePCM", toolTip = "#LOC_RB_DisablePCM_desc")]
        public bool disablePCM = false;

        // --- Logging ---
        [GameParameters.CustomParameterUI("#LOC_RB_Log", toolTip = "#LOC_RB_Log_desc")]
        public bool enableDebugLogging = false;

        [GameParameters.CustomParameterUI("#LOC_RB_VerboseLog", toolTip = "#LOC_RB_VerboseLog_desc")]
        public bool enableVerboseLoadLogs = false;

        /*private readonly string version = "v2.3";

        [GameParameters.CustomStringParameterUI("")]
        public string InfoText;

        public RBParams_Info()
        {
            InfoText = Localizer.Format("#LOC_RB_Settings_About", version);
        }*/
    }
}
