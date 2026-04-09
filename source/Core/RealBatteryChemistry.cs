using UnityEngine;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryChemistry  [v3]
    //  Plain data object (POCO) that holds all parameters for one chemistry entry.
    //  Loaded and cached by RealBatteryChemistryDB; consumed by RealBattery.OnStart().
    //
    //  Field names match the REALBATTERY_CHEMISTRY cfg node keys 1-to-1 so that
    //  RealBatteryChemistryDB can populate them with ConfigNode.TryGetValue() /
    //  ConfigNode.TryGetValue() without renaming.
    //
    //  KeepWarmMode string values:
    //    "false" – no thermal upkeep logic (all standard chemistries)
    //    "warm"  – upkeep required BELOW TempKeepWarmHi (molten-salt / LMB logic)
    //    "cryo"  – upkeep required ABOVE TempKeepWarmLo (SMES / cryogenic logic)
    // ============================================================================
    public class RealBatteryChemistry
    {
        // --- Identity ---
        public string name        = "";
        public string displayName = "";

        // --- Electrical ---
        public float      HighEClevel           = 0.95f;
        public float      LowEClevel            = 0.90f;
        public float      Crate                 = 1.0f;
        public FloatCurve ChargeEfficiencyCurve = new FloatCurve();

        // --- Degradation ---
        public double CycleDurability     = 1000.0;
        public double SelfDischargeRate   = 0.01;
        public bool   EvaRefurbishEnabled = true;
        public double SparePartsPerKWh    = 10.0;

        // --- Thermal ---
        public double ThermalLoss       = 0.15;
        public float  TempOverheat      = 435f;
        public float  TempRunaway       = 535f;
        public double RunawayHeatFactor = 0.0;

        // --- Behavior flags ---
        public bool   FixedOutput    = false;
        public bool   BatteryStaged  = false;

        // KeepWarmMode: "false" | "warm" | "cryo"
        public string KeepWarmMode   = "false";
        public float  TempKeepWarmLo = 500f;
        public float  TempKeepWarmHi = 600f;

        public bool   SelfRunaway       = false;
        public double RunawayBaseChance = 0.0;

        public bool   LifeDecay      = false;
        public bool   InfiniteCycles = false;
    }
}
