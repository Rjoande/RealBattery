using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryChemistryDB  [v3]
    //  Loads all REALBATTERY_CHEMISTRY config nodes from the GameDatabase and
    //  exposes them via a static registry.
    //
    //  Lifecycle: [KSPAddon] fires at MainMenu (once=true), by which point
    //  ModuleManager has finished patching and the GameDatabase is fully populated.
    //  The static Dictionary persists for the entire game session.
    //
    //  Usage:
    //    RealBatteryChemistry chem = RealBatteryChemistryDB.Get("Li_ion");
    //    if (chem != null) { /* copy fields onto the module */ }
    // ============================================================================
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RealBatteryChemistryDB : MonoBehaviour
    {
        private static readonly Dictionary<string, RealBatteryChemistry> _registry =
            new Dictionary<string, RealBatteryChemistry>(StringComparer.OrdinalIgnoreCase);

        // ---- Unity lifecycle ----

        void Start() => Load();

        // ---- Public API ----

        /// <summary>
        /// Returns the chemistry registered under <paramref ChemistryID="id"/>, or null if not found.
        /// </summary>
        public static RealBatteryChemistry Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            _registry.TryGetValue(id, out RealBatteryChemistry chem);
            return chem;
        }

        /// <summary>
        /// Number of chemistries currently loaded.
        /// </summary>
        public static int Count => _registry.Count;

        // ---- Loading (internal so unit-tests can invoke it directly) ----

        internal static void Load()
        {
            _registry.Clear();

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REALBATTERY_CHEMISTRY");
            RBLog.Boot($"[ChemistryDB] Found {nodes.Length} REALBATTERY_CHEMISTRY node(s).");

            foreach (ConfigNode node in nodes)
            {
                RealBatteryChemistry chem = ParseNode(node);
                if (chem == null) continue;

                if (_registry.ContainsKey(chem.name))
                    RBLog.Warn($"[ChemistryDB] Duplicate chemistry '{chem.name}' — overwriting earlier entry.");

                _registry[chem.name] = chem;
                RBLog.Boot($"[ChemistryDB]   + '{chem.name}'");
            }

            RBLog.Boot($"[ChemistryDB] Ready: {_registry.Count} chemistries registered.");
        }

        // ---- Parsing ----

        private static RealBatteryChemistry ParseNode(ConfigNode node)
        {
            string name = "";
            if (!node.TryGetValue("ChemistryID", ref name) || string.IsNullOrEmpty(name))
            {
                RBLog.Warn("[ChemistryDB] REALBATTERY_CHEMISTRY node has no 'ChemistryID' — skipping.");
                return null;
            }

            var c = new RealBatteryChemistry { name = name };

            node.TryGetValue("displayName",         ref c.displayName);

            // --- Electrical ---
            node.TryGetValue("HighEClevel",          ref c.HighEClevel);
            node.TryGetValue("LowEClevel",           ref c.LowEClevel);
            node.TryGetValue("Crate",                ref c.Crate);

            ConfigNode curveNode = node.GetNode("ChargeEfficiencyCurve");
            if (curveNode != null)
                c.ChargeEfficiencyCurve.Load(curveNode);

            // --- Degradation ---
            node.TryGetValue("CycleDurability",      ref c.CycleDurability);
            node.TryGetValue("SelfDischargeRate",    ref c.SelfDischargeRate);
            node.TryGetValue("EvaRefurbishEnabled",  ref c.EvaRefurbishEnabled);
            node.TryGetValue("SparePartsPerKWh",     ref c.SparePartsPerKWh);
            node.TryGetValue("EVAminLevel",          ref c.EVAminLevel);

            // --- Thermal ---
            node.TryGetValue("ThermalLoss",          ref c.ThermalLoss);
            node.TryGetValue("TempOverheat",         ref c.TempOverheat);
            node.TryGetValue("TempRunaway",          ref c.TempRunaway);
            node.TryGetValue("RunawayHeatFactor",    ref c.RunawayHeatFactor);

            // --- Behavior flags ---
            node.TryGetValue("FixedOutput",          ref c.FixedOutput);
            node.TryGetValue("BatteryStaged",        ref c.BatteryStaged);
            node.TryGetValue("KeepWarmMode",         ref c.KeepWarmMode);
            node.TryGetValue("TempKeepWarmLo",       ref c.TempKeepWarmLo);
            node.TryGetValue("TempKeepWarmHi",       ref c.TempKeepWarmHi);
            node.TryGetValue("SelfRunaway",          ref c.SelfRunaway);
            node.TryGetValue("RunawayBaseChance",    ref c.RunawayBaseChance);
            node.TryGetValue("LifeDecay",            ref c.LifeDecay);
            node.TryGetValue("InfiniteCycles",       ref c.InfiniteCycles);

            return c;
        }
    }
}
