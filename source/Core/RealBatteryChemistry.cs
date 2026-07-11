using System.Collections.Generic;
using UnityEngine;

namespace RealBattery
{
    // ============================================================================
    //  ResourceRequirement  [v3.2.0]
    //  One auxiliary resource flow tied to battery charge/discharge, parsed from a
    //  RESOURCE_EXTRA sub-node of REALBATTERY_CHEMISTRY. Fully optional in cfg.
    // ============================================================================
    public class ResourceRequirement
    {
        public string name  = "";          // resource name
        public double ratio = 0.0;         // units per EC/s of actual transfer
        public string mode  = "discharge"; // "charge" | "discharge"
        public string type  = "input";     // "input" | "output"

        // Parses one RESOURCE_EXTRA node. Shared by the chemistry-DB loader and the
        // inline-cfg path so both stay in sync. All fields optional (TryGetValue).
        public static ResourceRequirement Load(ConfigNode node)
        {
            var req = new ResourceRequirement();
            node.TryGetValue("name",  ref req.name);
            node.TryGetValue("ratio", ref req.ratio);
            node.TryGetValue("mode",  ref req.mode);
            node.TryGetValue("type",  ref req.type);
            return req;
        }
    }

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

        // Defaults below mirror RBDefaults (canonical single source of truth) so the
        // chemistry-DB fallback set can never drift from the inline-cfg KSPField set.

        // --- Electrical ---
        public float      HighEClevel           = RBDefaults.HighEClevel;
        public float      LowEClevel            = RBDefaults.LowEClevel;
        public float      Crate                 = RBDefaults.Crate;
        public FloatCurve ChargeEfficiencyCurve = new FloatCurve();

        // --- Degradation ---
        public double CycleDurability     = RBDefaults.CycleDurability;
        public double SelfDischargeRate   = RBDefaults.SelfDischargeRate;
        public bool   EvaRefurbishEnabled = RBDefaults.EvaRefurbishEnabled;
        public double SparePartsPerKWh    = RBDefaults.SparePartsPerKWh;
        public int    EVAminLevel         = RBDefaults.EVAminLevel;

        // --- Thermal ---
        public double ThermalLoss       = RBDefaults.ThermalLoss;
        public float  TempOverheat      = RBDefaults.TempOverheat;
        public float  TempRunaway       = RBDefaults.TempRunaway;
        public float  TempOptimal       = RBDefaults.TempOptimal;  // operational target K communicated to SystemHeat loop
        public double RunawayHeatFactor = RBDefaults.RunawayHeatFactor;

        // --- Behavior flags ---
        public bool   FixedOutput    = RBDefaults.FixedOutput;
        public bool   BatteryStaged  = RBDefaults.BatteryStaged;

        // KeepWarmMode: "false" | "warm" | "cryo"
        public string KeepWarmMode   = RBDefaults.KeepWarmMode;
        public float  TempKeepWarmLo = RBDefaults.TempKeepWarmLo;
        public float  TempKeepWarmHi = RBDefaults.TempKeepWarmHi;

        public bool   SelfRunaway       = RBDefaults.SelfRunaway;
        public double RunawayBaseChance = RBDefaults.RunawayBaseChance;

        public bool   LifeDecay      = RBDefaults.LifeDecay;
        public bool   InfiniteCycles = RBDefaults.InfiniteCycles;

        // --- v3.2.0 additions ---
        // Auxiliary resource flows (RESOURCE_EXTRA sub-nodes); empty when none defined.
        public List<ResourceRequirement> ResourceExtras = new List<ResourceRequirement>();

        // CrateScale: "false" (no scaling) | "add" | "reduce"; how this battery's
        // C-rate scales with the count of participating batteries on the vessel.
        public string CrateScale = RBDefaults.CrateScale;
    }
}
