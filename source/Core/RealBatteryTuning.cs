using UnityEngine;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryTuning  [plan M3 / T3.5]
    //  Optional global calibration overrides, read once from an optional
    //  REALBATTERY_TUNING cfg node. Lets a small set of empirically-tuned constants be
    //  adjusted without recompiling — currently only the cryo waste-heat flux per liter,
    //  added to support the T3b structured calibration protocol (PLAN_Fix_Refactoring.md).
    //
    //  Absent node or key -> falls back to the compiled-in default below, which is the
    //  value RealBattery shipped with before this knob existed (2026-07 decision, see
    //  CLAUDE.md: "0.22 was hypertrophic; 0.00002 empirically matches CryoTanks").
    //
    //  Lifecycle: loaded once at MainMenu, same pattern as RealBatteryChemistryDB.
    // ============================================================================
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RealBatteryTuning : MonoBehaviour
    {
        // Canonical default — do not change outside the T3b calibration protocol.
        internal const float DEFAULT_CRYO_WASTE_HEAT_W_PER_L = 0.00002f;

        public static float CryoWasteHeatPerL { get; private set; } = DEFAULT_CRYO_WASTE_HEAT_W_PER_L;

        void Start() => Load();

        internal static void Load()
        {
            float cryoWasteHeatPerL = DEFAULT_CRYO_WASTE_HEAT_W_PER_L;

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REALBATTERY_TUNING");
            if (nodes != null)
            {
                // Only one global tuning node is expected; if more than one is present, the last
                // one read wins (consistent with how a user override patch would be applied last).
                foreach (ConfigNode node in nodes)
                    node.TryGetValue("cryoWasteHeatPerL", ref cryoWasteHeatPerL);
            }

            CryoWasteHeatPerL = cryoWasteHeatPerL;
            RBLog.Boot($"[RealBatteryTuning] cryoWasteHeatPerL = {CryoWasteHeatPerL} (default={DEFAULT_CRYO_WASTE_HEAT_W_PER_L})");
        }
    }
}
