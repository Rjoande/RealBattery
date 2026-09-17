using System;
using System.Collections.Generic;
using System.Reflection;

namespace RealBatteryBVBridge
{
    // Reads/writes RealBattery's PartModule fields via reflection, without a compile-time
    // reference to RealBattery.dll (RealBattery's own public API was explored in M6b but
    // parked — see RealBatteryAPI_draft/ at the repo root — so this bridge stays self-
    // contained via reflection, same approach as the RealBattery-side of the BonVoyage PR).
    //
    // Most fields used here are genuine KSPFields (readable via the stock BaseField API);
    // CachedEngineerLevel is a plain public field (not a KSPField), so it needs ordinary
    // System.Reflection instead.
    internal static class RealBatteryFieldAccess
    {
        // 1 StoredCharge (RealBattery) = 3600 ElectricCharge (BV's internal accounting unit).
        // Must match RealBatterySupport.EcPerSc on the BonVoyage side of this integration.
        internal const double EcPerSc = 3600.0;

        private static FieldInfo _cachedEngineerLevelField;
        private static bool _cachedEngineerLevelFieldResolved;

        private static MethodInfo _updateBatteryLifeMethod;
        private static bool _updateBatteryLifeMethodResolved;

        private static PropertyInfo _enableBatteryWearProperty;
        private static bool _enableBatteryWearResolved;

        internal static bool GetBaseFieldBool(PartModule module, string fieldName)
        {
            BaseField f = module.Fields[fieldName];
            return f != null && f.GetValue(module) is bool b && b;
        }

        internal static double GetBaseFieldDouble(PartModule module, string fieldName, double fallback = 0.0)
        {
            BaseField f = module.Fields[fieldName];
            if (f == null) return fallback;
            return f.GetValue(module) is double d ? d : fallback;
        }

        internal static void SetBaseFieldDouble(PartModule module, string fieldName, double value)
        {
            module.Fields[fieldName]?.SetValue(value, module);
        }

        private static int GetCachedEngineerLevel(PartModule module)
        {
            if (!_cachedEngineerLevelFieldResolved)
            {
                _cachedEngineerLevelFieldResolved = true;
                _cachedEngineerLevelField = module.GetType().GetField("CachedEngineerLevel", BindingFlags.Public | BindingFlags.Instance);
            }

            object value = _cachedEngineerLevelField?.GetValue(module);
            return value is int i ? i : 0;
        }

        private static void InvokeUpdateBatteryLife(PartModule module)
        {
            if (!_updateBatteryLifeMethodResolved)
            {
                _updateBatteryLifeMethodResolved = true;
                _updateBatteryLifeMethod = module.GetType().GetMethod("UpdateBatteryLife", BindingFlags.Public | BindingFlags.Instance);
            }

            _updateBatteryLifeMethod?.Invoke(module, null);
        }

        // RealBatterySettings.EnableBatteryWear is a static property on RealBattery.dll's
        // RealBatterySettings class — resolved once, by name, from the loaded RealBattery
        // assembly (no compile-time reference).
        private static bool EnableBatteryWear
        {
            get
            {
                if (!_enableBatteryWearResolved)
                {
                    _enableBatteryWearResolved = true;
                    foreach (var loaded in AssemblyLoader.loadedAssemblies)
                    {
                        if (loaded.name != "RealBattery") continue;
                        var settingsType = loaded.assembly.GetType("RealBattery.RealBatterySettings");
                        _enableBatteryWearProperty = settingsType?.GetProperty("EnableBatteryWear", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }

                object value = _enableBatteryWearProperty?.GetValue(null);
                return value is bool b ? b : true; // default matches RealBatterySettings' own fallback
            }
        }

        private struct Candidate
        {
            internal PartModule Module;
            internal PartResource Sc;
        }

        private static List<Candidate> CollectActiveBatteries(Vessel vessel)
        {
            var list = new List<Candidate>();
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (!part.Modules.Contains("RealBattery")) continue;

                PartModule rb = part.Modules["RealBattery"];
                if (GetBaseFieldBool(rb, "BatteryDisabled")) continue;
                if (!part.Resources.Contains("StoredCharge")) continue;

                list.Add(new Candidate { Module = rb, Sc = part.Resources["StoredCharge"] });
            }
            return list;
        }

        /// <summary>
        /// Sum of effective StoredCharge capacity (BatteryLife/ThermalCapFactor-derated,
        /// converted to BV's EC-equivalent unit) across all non-disabled RealBattery parts.
        /// Returns null if the vessel has no such parts, so the caller can leave BV's own
        /// result untouched instead of reporting a zero capacity.
        /// </summary>
        internal static double? ComputeCapacityEc(Vessel vessel)
        {
            var candidates = CollectActiveBatteries(vessel);
            if (candidates.Count == 0) return null;

            bool enableWear = EnableBatteryWear;
            double total = 0.0;
            foreach (var c in candidates)
            {
                double batteryLife = GetBaseFieldDouble(c.Module, "BatteryLife", 1.0);
                double actualLife = enableWear ? batteryLife : 1.0;
                double thermalCap = GetBaseFieldDouble(c.Module, "ThermalCapFactor", 1.0);
                double effectiveLife = Math.Min(actualLife, thermalCap);

                total += c.Sc.maxAmount * effectiveLife * EcPerSc;
            }
            return total;
        }

        /// <summary>
        /// Drains scToDrain (StoredCharge units) pro-quota of nominal capacity across all
        /// non-disabled RealBattery parts, crediting wear and realigning SC_SOC on each one
        /// exactly as RealBattery's own XferECtoRealBattery would. Returns false (no-op) if
        /// the vessel has no RealBattery parts, so the caller knows not to touch anything else
        /// (e.g. BV's own CurrentEC bookkeeping) either.
        /// </summary>
        internal static bool DrainAndCreditWear(Vessel vessel, double scToDrain)
        {
            var candidates = CollectActiveBatteries(vessel);
            if (candidates.Count == 0) return false;

            double totalCapacity = 0.0;
            foreach (var c in candidates) totalCapacity += c.Sc.maxAmount;
            if (totalCapacity <= 0.0) return false;

            // CachedEngineerLevel is pushed identically to every battery on the vessel by
            // RealBatteryLoadMaster, so reading it off the first participating battery is enough.
            int engineerLevel = GetCachedEngineerLevel(candidates[0].Module);
            double engBonus = 0.95 + 0.06 * engineerLevel; // must mirror RealBattery.EngineerBonus()

            foreach (var c in candidates)
            {
                double share = c.Sc.maxAmount / totalCapacity;
                double scShare = scToDrain * share;
                if (scShare <= 0.0) continue;

                c.Sc.amount = Math.Max(0.0, c.Sc.amount - scShare);

                double wearShare = scShare / engBonus;
                double wearCounter = GetBaseFieldDouble(c.Module, "WearCounter") + wearShare;
                SetBaseFieldDouble(c.Module, "WearCounter", wearCounter);
                InvokeUpdateBatteryLife(c.Module);

                double newSoc = c.Sc.maxAmount > 0 ? c.Sc.amount / c.Sc.maxAmount : 0.0;
                SetBaseFieldDouble(c.Module, "SC_SOC", newSoc);
            }

            return true;
        }
    }
}
