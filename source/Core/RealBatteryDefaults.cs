namespace RealBattery
{
    // ============================================================================
    //  RBDefaults  [v3.2.0]
    //  Single source of truth for every chemistry/behavior default value.
    //
    //  Both the inline-cfg path (KSPField initializers in RealBattery / EVAService)
    //  and the chemistry-DB path (RealBatteryChemistry POCO initializers) reference
    //  these constants, so the two fallback sets can never drift apart again.
    //
    //  These are the canonical "valid" values (formerly the KSPField defaults). A
    //  field omitted from both the MODULE node and the REALBATTERY_CHEMISTRY node
    //  resolves to the value defined here.
    // ============================================================================
    public static class RBDefaults
    {
        // --- Electrical ---
        public const float  HighEClevel       = 0.95f;
        public const float  LowEClevel        = 0.90f;
        public const float  Crate             = 1.0f;

        // --- Degradation ---
        public const double CycleDurability   = 1.0;
        public const double SelfDischargeRate = 0.01;
        public const bool   EvaRefurbishEnabled = true;
        public const double SparePartsPerKWh  = 10.0;
        public const int    EVAminLevel       = 0;

        // --- Thermal ---
        public const double ThermalLoss       = 0.01;
        public const float  TempOptimal       = 350f;
        public const float  TempOverheat      = 450f;
        public const float  TempRunaway       = 550f;
        public const double RunawayHeatFactor = 0.0;

        // --- Behavior flags ---
        public const bool   FixedOutput       = false;
        public const bool   BatteryStaged     = false;
        public const string KeepWarmMode      = "false";
        public const float  TempKeepWarmLo    = 500f;
        public const float  TempKeepWarmHi    = 600f;
        public const bool   SelfRunaway       = false;
        public const double RunawayBaseChance = 0.0;
        public const bool   LifeDecay         = false;
        public const bool   InfiniteCycles    = false;
        public const string CrateScale        = "false";
    }
}
