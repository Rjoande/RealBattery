using UnityEngine;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryTuning
    //  Optional global calibration overrides, read once from an optional
    //  REALBATTERY_TUNING cfg node. Lets a small set of empirically-tuned constants be
    //  adjusted without recompiling: the cryo waste-heat flux per liter, tuned against
    //  CryoTanks (a 1200 L SMES vs. a matched LH2 tank), and the two coefficients of the
    //  passive cooling that batteries get under SystemHeat.
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

        // Passive cooling under SystemHeat: effective area = factor x (volume in m3)^(2/3), so a
        // 200 L battery gets about the convective area of the tiny radiator panel (0.02 m2).
        internal const float DEFAULT_PASSIVE_AREA_FACTOR = 0.06f;
        // Emissivity of that area for the radiative term. Kept low on purpose: it should carry
        // slow discharges only, leaving sustained high power to radiators.
        internal const float DEFAULT_PASSIVE_EMISSIVITY = 0.1f;

        public static float PassiveAreaFactor { get; private set; } = DEFAULT_PASSIVE_AREA_FACTOR;
        public static float PassiveEmissivity { get; private set; } = DEFAULT_PASSIVE_EMISSIVITY;

        void Start() => Load();

        internal static void Load()
        {
            float cryoWasteHeatPerL = DEFAULT_CRYO_WASTE_HEAT_W_PER_L;
            float passiveAreaFactor = DEFAULT_PASSIVE_AREA_FACTOR;
            float passiveEmissivity = DEFAULT_PASSIVE_EMISSIVITY;

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REALBATTERY_TUNING");
            if (nodes != null)
            {
                // Only one global tuning node is expected; if more than one is present, the last
                // one read wins (consistent with how a user override patch would be applied last).
                foreach (ConfigNode node in nodes)
                {
                    node.TryGetValue("cryoWasteHeatPerL", ref cryoWasteHeatPerL);
                    node.TryGetValue("passiveAreaFactor", ref passiveAreaFactor);
                    node.TryGetValue("passiveEmissivity", ref passiveEmissivity);
                }
            }

            CryoWasteHeatPerL = cryoWasteHeatPerL;
            PassiveAreaFactor = Mathf.Max(0f, passiveAreaFactor);
            PassiveEmissivity = Mathf.Max(0f, passiveEmissivity);
            RBLog.Boot($"[RealBatteryTuning] cryoWasteHeatPerL = {CryoWasteHeatPerL} (default={DEFAULT_CRYO_WASTE_HEAT_W_PER_L}), " +
                       $"passiveAreaFactor = {PassiveAreaFactor}, passiveEmissivity = {PassiveEmissivity}");
        }
    }
}
