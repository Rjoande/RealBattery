using KSP.Localization;
using System;
using UnityEngine;
using B9PartSwitch;

namespace RealBattery
{
    public partial class RealBattery
    {
        // --- Tuning knobs (configurable via MODULE fields / B9PS per subtype) --------

        [KSPField(isPersistant = false)]
        public bool EvaRefurbishEnabled = true;        // Gate per-part/subtype

        [KSPField(isPersistant = false)]
        public double EvaRefurbishRange = 2.5;         // Max EVA distance (m)

        //[KSPField(isPersistant = false)]
        //public double SparePartsFlat = 0.0;            // If > 0, use a flat SpareParts cost

        [KSPField(isPersistant = false)]
        public double SparePartsPerKWh = 10.0;         // Otherwise, scaled cost (per kWh to restore)

        [KSPField(isPersistant = false)]
        public bool RequireBatteryDisabled = true;     // Avoid hot-swap exploits

        [KSPField(isPersistant = false)]
        public bool BlockIfLatchedThermal = true;      // Thermal (latched by staging) cannot be serviced

        [KSPField(isPersistant = false)]
        public double SafeTempMargin = 0.0;            // Optional °K margin below overheat

        // Target chemistry for EVA upgrade; empty = upgrade prohibited.
        [KSPField(isPersistant = false)]
        public string EVAupgrade = "";

        // PARTUPGRADE id required to unlock the EVA upgrade; empty = no tech gate.
        [KSPField(isPersistant = false)]
        public string EVAupgradeTech = "";

        // Minimum Engineer star level for refurbish/upgrade (from chemistry DB).
        [KSPField(isPersistant = false)]
        public int EVAminLevel = 0;

        // --- UI Event (EVA only) -----------------------------------------------------

        [KSPEvent(guiActive = false, guiActiveUnfocused = true, externalToEVAOnly = true, unfocusedRange = 2.5F, guiName = "#LOC_RB_EVARepair")]  // "Replace Battery (EVA)"
        public void EvaRefurbish()
        {
            // Basic guards
            if (!EvaRefurbishEnabled || !RealBatterySettings.EnableEVARefurbush) { Msg("#LOC_RB_EVARepair_Disabled"); return; }
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (TimeWarp.CurrentRate > 1.0f) { Msg("#LOC_RB_EVARepair_Timewarp"); return; }

            // Safety: block thermal batteries latched ON
            if (BlockIfLatchedThermal && FixedOutput && ActivationLatched)
            {
                Msg("#LOC_RB_EVARepair_Latched");
                return;
            }

            // Optional: require the battery to be disabled before servicing
            if (RequireBatteryDisabled && !BatteryDisabled)
            {
                Msg("#LOC_RB_EVARepair_TurnOff");
                return;
            }

            // Temperature guard
            float tempNow;
            if (RealBatterySettings.UseSystemHeat)
            {
                var sh = (systemHeat != null) ? systemHeat : SystemHeatBridge.GetModule(part);
                tempNow = (sh != null && SystemHeatBridge.TryGetLoopTempK(sh, out float tK)) ? tK : (float)part.temperature;
            }
            else tempNow = (float)part.temperature;

            if (tempNow > (float)(TempOverheat - SafeTempMargin))
            {
                Msg("#LOC_RB_EVARepair_TooHot");
                return;
            }

            // Check EVA kerbal within range
            var eva = FlightGlobals.ActiveVessel;
            if (eva == null || !eva.isEVA || Vector3.Distance(eva.transform.position, part.transform.position) > EvaRefurbishRange)
            {
                Msg("#LOC_RB_EVARepair_NoKerbal");
                return;
            }

            // Robust trait check: the EVA vessel has exactly one crew member; require trait == "Engineer".
            var pcm = eva?.rootPart?.protoModuleCrew != null && eva.rootPart.protoModuleCrew.Count > 0
                ? eva.rootPart.protoModuleCrew[0]
                : null;
            if (pcm == null || !string.Equals(pcm.trait, "Engineer", StringComparison.OrdinalIgnoreCase))
            { Msg("#LOC_RB_EVARepair_NotEngineer"); return; }

            // Engineer level check (uses current chemistry's EVAminLevel)
            if (pcm.experienceLevel < EVAminLevel)
            {
                ScreenMessages.PostScreenMessage(
                    Localizer.Format("#LOC_RB_EVARepair_LowLevel", EVAminLevel), 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            // Compute SpareParts cost
            // Capacity in kWh: we use StoredCharge's maxAmount as "kWh" canonical unit in RB.
            double scMax = part.Resources.Contains("StoredCharge") ? part.Resources["StoredCharge"].maxAmount : 0.0;
            if (scMax <= 0.0) { Msg("#LOC_RB_EVARepair_Failed"); return; }

            // Full replacement: cost depends ONLY on capacity, not on current wear.
            // Net cost = ceil( SparePartsPerKWh * kWh )
            double needSp = Math.Ceiling(SparePartsPerKWh * scMax);
            RBLog.Info($"[EVARefurbish] NeedSP={needSp:F0} (kWh={scMax:F2}, Rate={SparePartsPerKWh:F2}) on part '{part.partInfo?.title}'");

            // Consume SpareParts at VESSEL level, ignoring NO_FLOW by iterating parts.
            double availableSp;
            if (!TryConsumeSparePartsFromVessel(needSp, out availableSp))
            {
                var detail = Localizer.Format("#LOC_RB_EVARepair_NoParts_Detail", (int)needSp, (int)availableSp);
                ScreenMessages.PostScreenMessage(detail, 5f, ScreenMessageStyle.UPPER_CENTER);
                RBLog.Info($"[EVARefurbish] Not enough SpareParts: need={needSp:F0}, available={availableSp:F0}");
                return;
            }

            // Apply refurbish
            try
            {
                WearCounter = 0.0;
                BatteryLife = 1.0;
                UpdateBatteryLife();
                var sc = part.Resources.Get("StoredCharge");
                sc.amount = sc.maxAmount;
                EOLToastSent = false;
                BGSelfRunawaySent = false;
                //if (Fields["BatteryHealthStatus"] != null)
                //    BatteryHealthStatus = $"{(BatteryLife * 100):F0}%";

                ScreenMessages.PostScreenMessage(
                    $"{part.partInfo.title}: {Localizer.Format("#LOC_RB_EVARepair_Done")}",
                    5f, ScreenMessageStyle.UPPER_CENTER);

                RBLog.Info($"[RealBattery] EVA refurbish OK on '{part.partInfo.title}', SpareParts used: {needSp:F0}");
            }
            catch (Exception ex)
            {
                RBLog.Error($"[RealBattery] EVA refurbish failed: {ex}");
                Msg("#LOC_RB_EVARepair_Failed");
            }
        }

        // --- EVA chemistry upgrade ---------------------------------------------------

        [KSPEvent(guiActive = false, guiActiveUnfocused = true, externalToEVAOnly = true,
                  unfocusedRange = 2.5F, guiName = "#LOC_RB_EVAUpgrade")]
        public void EvaUpgradeChemistry()
        {
            // Guard 1: upgrade prohibited for this subtype (handled by RB_AfterOnStart hiding the button)
            if (string.IsNullOrEmpty(EVAupgrade)) return;

            // Guard 2: EVA refurbish globally disabled
            if (!RealBatterySettings.EnableEVARefurbush) { Msg("#LOC_RB_EVARepair_Disabled"); return; }

            // Guard 3: only in flight
            if (!HighLogic.LoadedSceneIsFlight) return;

            // Guard 4: time warp
            if (TimeWarp.CurrentRate > 1.0f) { Msg("#LOC_RB_EVARepair_Timewarp"); return; }

            // Guard 5: require battery disabled
            if (RequireBatteryDisabled && !BatteryDisabled) { Msg("#LOC_RB_EVARepair_TurnOff"); return; }

            // Guard 6: temperature
            float tempNow;
            if (RealBatterySettings.UseSystemHeat)
            {
                var sh = (systemHeat != null) ? systemHeat : SystemHeatBridge.GetModule(part);
                tempNow = (sh != null && SystemHeatBridge.TryGetLoopTempK(sh, out float tK)) ? tK : (float)part.temperature;
            }
            else tempNow = (float)part.temperature;
            if (tempNow > (float)(TempOverheat - SafeTempMargin)) { Msg("#LOC_RB_EVARepair_TooHot"); return; }

            // Guard 7: EVA engineer in range
            var eva = FlightGlobals.ActiveVessel;
            if (eva == null || !eva.isEVA || Vector3.Distance(eva.transform.position, part.transform.position) > EvaRefurbishRange)
            { Msg("#LOC_RB_EVARepair_NoKerbal"); return; }

            var pcm = eva?.rootPart?.protoModuleCrew != null && eva.rootPart.protoModuleCrew.Count > 0
                ? eva.rootPart.protoModuleCrew[0] : null;
            if (pcm == null || !string.Equals(pcm.trait, "Engineer", StringComparison.OrdinalIgnoreCase))
            { Msg("#LOC_RB_EVARepair_NotEngineer"); return; }

            // Guard 8: engineer level (target chemistry requirement)
            var targetChem = RealBatteryChemistryDB.Get(EVAupgrade);
            if (targetChem == null) { Msg("#LOC_RB_EVARepair_Failed"); return; }

            if (pcm.experienceLevel < targetChem.EVAminLevel)
            {
                ScreenMessages.PostScreenMessage(
                    Localizer.Format("#LOC_RB_EVARepair_LowLevel", targetChem.EVAminLevel), 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            // Guard 9: tech gate
            if (!string.IsNullOrEmpty(EVAupgradeTech) && !PartUpgradeManager.Handler.IsUnlocked(EVAupgradeTech))
            { Msg("#LOC_RB_EVAUpgrade_Locked"); return; }

            // Guard 10: capacity sanity
            double scMax = part.Resources.Contains("StoredCharge") ? part.Resources["StoredCharge"].maxAmount : 0.0;
            if (scMax <= 0.0) { Msg("#LOC_RB_EVARepair_Failed"); return; }

            // Guard 11: SpareParts
            double needSp = Math.Ceiling(targetChem.SparePartsPerKWh * scMax);
            double availableSp;
            if (!TryConsumeSparePartsFromVessel(needSp, out availableSp))
            {
                ScreenMessages.PostScreenMessage(
                    Localizer.Format("#LOC_RB_EVARepair_NoParts_Detail", (int)needSp, (int)availableSp),
                    5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            try
            {
                // Capture SoC before rescaling
                double previousSoC = SC_SOC;

                // Rescale resources, mass, cost from target B9_TANK_TYPE
                ApplyB9TankTypeUpgrade(EVAupgrade);

                // Switch chemistry
                ChemistryID = EVAupgrade;
                ApplyChemistryFromDB();

                // Reset wear state (same as EvaRefurbish)
                WearCounter = 0.0;
                BatteryLife = 1.0;
                ThermalCapFactor = 1.0;
                ThermalCapNotified = false;
                EOLToastSent = false;
                BGSelfRunawaySent = false;
                UpdateBatteryLife();

                // Restore SoC proportionally
                var sc = part.Resources.Get("StoredCharge");
                sc.amount = Math.Min(previousSoC * sc.maxAmount, sc.maxAmount);
                SC_SOC = sc.maxAmount > 0 ? sc.amount / sc.maxAmount : 0.0;

                // Refresh PAW labels for both events
                RB_AfterOnStart();

                ScreenMessages.PostScreenMessage(
                    $"{part.partInfo.title}: {Localizer.Format("#LOC_RB_EVAUpgrade_Done", targetChem.displayName)}",
                    5f, ScreenMessageStyle.UPPER_CENTER);

                RBLog.Info($"[EVAUpgrade] Chemistry upgraded to '{ChemistryID}' on '{part.partInfo?.title}', SP used: {needSp:F0}");
            }
            catch (Exception ex)
            {
                RBLog.Error($"[EVAUpgrade] Failed: {ex}");
                Msg("#LOC_RB_EVARepair_Failed");
            }
        }

        private void ApplyB9TankTypeUpgrade(string targetTankTypeName)
        {
            var b9 = part.Modules.GetModule<ModuleB9PartSwitch>();
            if (b9 == null) { RBLog.Error("[EVAUpgrade] ModuleB9PartSwitch not found."); return; }
            double baseVolume = b9.baseVolume;

            ConfigNode currentNode = FindB9TankTypeNode(ChemistryID);
            ConfigNode targetNode  = FindB9TankTypeNode(targetTankTypeName);
            if (targetNode == null) { RBLog.Error($"[EVAUpgrade] B9_TANK_TYPE '{targetTankTypeName}' not found."); return; }

            double currentMass = 0, currentCost = 0, targetMass = 0, targetCost = 0;
            if (currentNode != null)
            {
                currentNode.TryGetValue("tankMass", ref currentMass);
                currentNode.TryGetValue("tankCost", ref currentCost);
            }
            targetNode.TryGetValue("tankMass", ref targetMass);
            targetNode.TryGetValue("tankCost", ref targetCost);

            part.mass          += (float)((targetMass - currentMass) * baseVolume);
            part.partInfo.cost += (float)((targetCost - currentCost) * baseVolume);

            foreach (ConfigNode resNode in targetNode.GetNodes("RESOURCE"))
            {
                string resName = "";
                double upv = 0.0;
                resNode.TryGetValue("name", ref resName);
                resNode.TryGetValue("unitsPerVolume", ref upv);
                if (string.IsNullOrEmpty(resName) || upv <= 0.0) continue;

                var pr = part.Resources.Get(resName);
                if (pr == null) continue;
                pr.maxAmount = upv * baseVolume;
                pr.amount    = Math.Min(pr.amount, pr.maxAmount);
            }

            RBLog.Info($"[EVAUpgrade] Applied B9TankType '{targetTankTypeName}': " +
                       $"baseVolume={baseVolume:F3}, massDelta={(targetMass - currentMass) * baseVolume:F4}, " +
                       $"costDelta={(targetCost - currentCost) * baseVolume:F1}");
        }

        private static ConfigNode FindB9TankTypeNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var urlConfig in GameDatabase.Instance.GetConfigs("B9_TANK_TYPE"))
            {
                string n = "";
                if (urlConfig.config.TryGetValue("name", ref n) && n == name)
                    return urlConfig.config;
            }
            return null;
        }

        // --- Wire events at runtime so range and labels reflect field values ----------

        public void RB_AfterOnStart()
        {
            // --- EvaRefurbish --------------------------------------------------------
            var evRepair = Events[nameof(EvaRefurbish)];
            if (evRepair != null)
            {
                bool repairAllowed = EvaRefurbishEnabled && RealBatterySettings.EnableEVARefurbush;
                evRepair.active             = repairAllowed;
                evRepair.guiActiveUnfocused = repairAllowed;
                if (repairAllowed)
                {
                    evRepair.externalToEVAOnly = true;
                    evRepair.unfocusedRange    = (float)Math.Max(1.0, EvaRefurbishRange);
                    double scMax = part.Resources.Contains("StoredCharge")
                        ? part.Resources["StoredCharge"].maxAmount : 0.0;
                    int needSp = scMax > 0 ? (int)Math.Ceiling(SparePartsPerKWh * scMax) : 0;
                    evRepair.guiName = Localizer.Format("#LOC_RB_EVARepair", needSp);
                }
            }

            // --- EvaUpgradeChemistry -------------------------------------------------
            var evUpgrade = Events[nameof(EvaUpgradeChemistry)];
            if (evUpgrade != null)
            {
                bool upgradeAvailable = !string.IsNullOrEmpty(EVAupgrade)
                                        && RealBatterySettings.EnableEVARefurbush
                                        && RealBatteryChemistryDB.Get(EVAupgrade) != null;
                evUpgrade.active             = upgradeAvailable;
                evUpgrade.guiActiveUnfocused = upgradeAvailable;
                if (upgradeAvailable)
                {
                    evUpgrade.externalToEVAOnly = true;
                    evUpgrade.unfocusedRange    = (float)Math.Max(1.0, EvaRefurbishRange);
                    var targetChem = RealBatteryChemistryDB.Get(EVAupgrade);
                    double scMax   = part.Resources.Contains("StoredCharge")
                        ? part.Resources["StoredCharge"].maxAmount : 0.0;
                    int needSp = scMax > 0 ? (int)Math.Ceiling(targetChem.SparePartsPerKWh * scMax) : 0;
                    evUpgrade.guiName = Localizer.Format("#LOC_RB_EVAUpgrade", targetChem.displayName, needSp);
                }
            }
        }

        private double GetKWhCapacity()
        {
            var resList = part?.Resources;
            if (resList == null) return 0.0;
            if (resList.Contains("StoredCharge")) return resList["StoredCharge"].maxAmount;
            return 0.0;
        }

        /*// Returns ceil(10 * kWh) or 0 if capacity not yet available.
        private int ComputeNeededSp()
        {
            double scMax = GetKWhCapacity();
            if (scMax <= 0.0) return 0;
            return (int)Math.Ceiling(SparePartsPerKWh * scMax);
        }*/

        // Consume SpareParts across the whole vessel.
        // This bypasses NO_FLOW by directly subtracting from PartResource.amount on each part.
        private bool TryConsumeSparePartsFromVessel(double need, out double available)
        {
            available = 0.0;
            if (vessel == null || need <= 0) return false;
            double remaining = Math.Ceiling(need); // enforce integer units as design

            // First pass: collect donors and how much we'll take
            var donors = new System.Collections.Generic.List<(Part part, PartResource res, double take)>();
            foreach (var p in vessel.parts)
            {
                if (p?.Resources == null) continue;
                var pr = p.Resources.Get("SpareParts");
                if (pr == null || pr.amount <= 0.0) continue;
                double take = Math.Min(remaining - available, pr.amount);
                if (take > 0)
                {
                    donors.Add((p, pr, take));
                    available += take;
                    if (available + 1e-6 >= remaining) break;
                }
            }

            if (available + 1e-6 < remaining) return false; // not enough across the vessel

            // Second pass: apply subtraction; keep integer semantics
            foreach (var d in donors)
            {
                // Subtract and clamp
                d.res.amount = Math.Max(0.0, d.res.amount - d.take);
                RBLog.Verbose($"[EVARefurbish] Took {d.take:F0} SP from part '{d.part?.partInfo?.title}' (left={d.res.amount:F0})");
            }
            RBLog.Info($"[EVARefurbish] Total SP consumed: {remaining:F0} from {donors.Count} donor parts; vessel had {available:F0} available.");
            return true;
        }

        private void Msg(string locKey)
            => ScreenMessages.PostScreenMessage(Localizer.Format(locKey), 4f, ScreenMessageStyle.UPPER_CENTER);
    }
}
