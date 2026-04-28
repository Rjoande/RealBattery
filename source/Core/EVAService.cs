using KSP.Localization;
using System;
using System.Linq;
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
        public bool BlockIfLatchedThermal = true;      // Thermal (latched by staging) cannot be serviced

        [KSPField(isPersistant = false)]
        public double SafeTempMargin = 0.0;            // Optional °K margin below overheat

        // Target B9PS subtypeName for EVA chemistry upgrade.
        // Must be explicitly set to "none" on subtypes that prohibit upgrade,
        // because B9PS does not reset absent fields when switching subtypes.
        [KSPField(isPersistant = false)]
        public string EVAupgrade = "none";

        // Minimum Engineer star level for refurbish/upgrade (from chemistry DB).
        [KSPField(isPersistant = false)]
        public int EVAminLevel = 0;

        // --- UI Event (EVA only) -----------------------------------------------------

        [KSPEvent(guiActive = false, guiActiveUnfocused = true, externalToEVAOnly = true, unfocusedRange = 2.5F,
                  guiName = "#LOC_RB_EVARepair", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
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

            // Guard: battery must be disabled before servicing
            if (!BatteryDisabled) { Msg("#LOC_RB_EVARepair_TurnOff"); return; }

            // Temperature guard
            float tempNow;
            if (RealBatterySettings.UseSystemHeat)
            {
                var sh = systemHeat ?? SystemHeatBridge.GetModule(part);
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

            // Guard: engineer level (from current chemistry). Bypassed in Sandbox mode.
            bool sandboxMode = HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX;
            if (!sandboxMode && pcm.experienceLevel < EVAminLevel)
            {
                ScreenMessages.PostScreenMessage(
                    Localizer.Format("#LOC_RB_EVARepair_LowLevel", EVAminLevel), 5f, ScreenMessageStyle.UPPER_CENTER);
                RBLog.Info($"[EVARefurbish] Engineer level {pcm.experienceLevel} < required {EVAminLevel}.");
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
            if (!TryConsumeSparePartsFromVessel(needSp, out double availableSp))
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
                  unfocusedRange = 2.5F, guiName = "#LOC_RB_EVAUpgrade",
                  groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public void EvaUpgradeChemistry()
        {
            // Guard: global setting
            if (!RealBatterySettings.EnableEVARefurbush) { Msg("#LOC_RB_EVARepair_Disabled"); return; }
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (TimeWarp.CurrentRate > 1.0f) { Msg("#LOC_RB_EVARepair_Timewarp"); return; }

            // Guard: battery must be disabled before servicing
            if (!BatteryDisabled) { Msg("#LOC_RB_EVARepair_TurnOff"); return; }

            // Guard: temperature
            float tempNow;
            if (RealBatterySettings.UseSystemHeat)
            {
                var sh = systemHeat ?? SystemHeatBridge.GetModule(part);
                tempNow = (sh != null && SystemHeatBridge.TryGetLoopTempK(sh, out float tK))
                    ? tK : (float)part.temperature;
            }
            else tempNow = (float)part.temperature;
            if (tempNow > (float)(TempOverheat - SafeTempMargin)) { Msg("#LOC_RB_EVARepair_TooHot"); return; }

            // Guard: EVA engineer within range
            var eva = FlightGlobals.ActiveVessel;
            if (eva == null || !eva.isEVA ||
                Vector3.Distance(eva.transform.position, part.transform.position) > EvaRefurbishRange)
            { Msg("#LOC_RB_EVARepair_NoKerbal"); return; }

            var pcm = eva.rootPart?.protoModuleCrew != null && eva.rootPart.protoModuleCrew.Count > 0
                ? eva.rootPart.protoModuleCrew[0] : null;
            if (pcm == null || !string.Equals(pcm.trait, "Engineer", StringComparison.OrdinalIgnoreCase))
            { Msg("#LOC_RB_EVARepair_NotEngineer"); return; }

            // Resolve target subtype from B9PS
            var b9 = part.Modules.GetModule<ModuleB9PartSwitch>();
            if (b9 == null) { RBLog.Error("[EVAUpgrade] ModuleB9PartSwitch not found."); Msg("#LOC_RB_EVARepair_Failed"); return; }
            var targetSubtype = b9.subtypes.FirstOrDefault(s => s.Name == EVAupgrade);
            if (targetSubtype == null) { RBLog.Warn($"[EVAUpgrade] Subtype '{EVAupgrade}' not found on part."); Msg("#LOC_RB_EVARepair_Failed"); return; }

            // Resolve target chemistry. subtypeName == ChemistryID is an enforced project convention:
            // all SUBTYPE names in RealBattery patches must match their ChemistryID exactly.
            var targetChem = RealBatteryChemistryDB.Get(EVAupgrade);
            if (targetChem == null) { RBLog.Warn($"[EVAUpgrade] Chemistry '{EVAupgrade}' not in DB."); Msg("#LOC_RB_EVARepair_Failed"); return; }

            // Guard: engineer level (from target chemistry). Bypassed in Sandbox mode.
            bool sandboxMode = HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX;
            if (!sandboxMode && pcm.experienceLevel < targetChem.EVAminLevel)
            {
                ScreenMessages.PostScreenMessage(
                    Localizer.Format("#LOC_RB_EVARepair_LowLevel", targetChem.EVAminLevel),
                    5f, ScreenMessageStyle.UPPER_CENTER);
                RBLog.Info($"[EVAUpgrade] Engineer level {pcm.experienceLevel} < required {targetChem.EVAminLevel}.");
                return;
            }

            // Guard: tech unlock (from B9PS target subtype's upgradeRequired).
            // Bypassed in Sandbox mode where the tech tree is not active.
            string techRequired = targetSubtype.upgradeRequired;
            if (!string.IsNullOrEmpty(techRequired) && !sandboxMode && !PartUpgradeManager.Handler.IsUnlocked(techRequired))
            {
                Msg("#LOC_RB_EVAUpgrade_Locked");
                RBLog.Info($"[EVAUpgrade] Tech '{techRequired}' not unlocked.");
                return;
            }

            // SP cost from target chemistry
            double scMax = part.Resources.Contains("StoredCharge")
                ? part.Resources["StoredCharge"].maxAmount : 0.0;
            if (scMax <= 0.0) { Msg("#LOC_RB_EVARepair_Failed"); return; }
            double needSp = Math.Ceiling(targetChem.SparePartsPerKWh * scMax);

            if (!TryConsumeSparePartsFromVessel(needSp, out double availableSp))
            {
                ScreenMessages.PostScreenMessage(
                    Localizer.Format("#LOC_RB_EVARepair_NoParts_Detail", (int)needSp, (int)availableSp),
                    5f, ScreenMessageStyle.UPPER_CENTER);
                RBLog.Info($"[EVAUpgrade] Not enough SpareParts: need={needSp:F0}, available={availableSp:F0}");
                return;
            }

            // Apply subtype switch via B9PS public API.
            // This automatically updates resources, mass, cost, and all DATA fields
            // (including ChemistryID and EVAupgrade) as defined in the target subtype.
            RBLog.Info($"[EVAUpgrade] Switching '{part.partInfo?.title}' to subtype '{EVAupgrade}'...");
            b9.SwitchSubtype(EVAupgrade);

            // B9PS updates maxAmount but does not fill resources — do it explicitly.
            var resEC = part.Resources.Get("ElectricCharge");
            if (resEC != null) resEC.amount = resEC.maxAmount;
            var resSC = part.Resources.Get("StoredCharge");
            if (resSC != null) resSC.amount = resSC.maxAmount;

            // Force re-resolution of chemistry parameters from DB after subtype switch,
            // because OnStart() is not called again and chemistry fields may be stale.
            if (!string.IsNullOrEmpty(ChemistryID))
                ApplyChemistryFromDB();

            // Reset wear state (same as EvaRefurbish)
            WearCounter        = 0.0;
            BatteryLife        = 1.0;
            ThermalCapFactor   = 1.0;
            ThermalCapNotified = false;
            EOLToastSent       = false;
            BGSelfRunawaySent  = false;
            UpdateBatteryLife();

            // Refresh PAW labels and event visibility (EVAupgrade field updated by B9PS switch)
            RB_AfterOnStart();

            ScreenMessages.PostScreenMessage(
                $"{part.partInfo.title}: {Localizer.Format("#LOC_RB_EVAUpgrade_Done", targetChem.displayName)}",
                5f, ScreenMessageStyle.UPPER_CENTER);
            RBLog.Info($"[EVAUpgrade] Done. New subtype='{EVAupgrade}', SP consumed={needSp:F0}.");
        }

        // --- Wire events at runtime so range and labels reflect field values ----------

        public void RB_AfterOnStart()
        {
            RBLog.Info($"[RB_AfterOnStart] EvaRefurbishEnabled={EvaRefurbishEnabled}, EnableEVARefurbush={RealBatterySettings.EnableEVARefurbush}");

            // Global gate: hide all EVA events if servicing is disabled (part or settings level).
            bool serviceEnabled = EvaRefurbishEnabled && RealBatterySettings.EnableEVARefurbush;
            if (!serviceEnabled)
            {
                RBLog.Info("[RB_AfterOnStart] EVA servicing disabled, hiding all events.");
                Events[nameof(EvaRefurbish)].active = false;
                Events[nameof(EvaRefurbish)].guiActiveUnfocused = false;
                return;
            }

            // Shared range (same for all EVA events).
            float evaRange = (float)Math.Max(1.0, EvaRefurbishRange);

            // --- EvaRefurbish --------------------------------------------------------
            var evRepair = Events[nameof(EvaRefurbish)];
            if (evRepair != null)
            {
                double scMax = part.Resources.Contains("StoredCharge")
                    ? part.Resources["StoredCharge"].maxAmount : 0.0;
                int needSp = scMax > 0 ? (int)Math.Ceiling(SparePartsPerKWh * scMax) : 0;
                evRepair.active = true;
                evRepair.guiActiveUnfocused = true;
                evRepair.externalToEVAOnly = true;
                evRepair.unfocusedRange = evaRange;
                evRepair.guiName = Localizer.Format("#LOC_RB_EVARepair", needSp);
                RBLog.Info($"[RB_AfterOnStart] EvaRefurbish visible, needSp={needSp}");
            }

            // --- EvaUpgradeChemistry -------------------------------------------------
            var evUpgrade = Events[nameof(EvaUpgradeChemistry)];
            if (evUpgrade != null)
            {
                bool upgradeAvailable = !string.IsNullOrEmpty(EVAupgrade)
                                        && EVAupgrade != "none"
                                        && RealBatterySettings.EnableEVARefurbush
                                        && RealBatteryChemistryDB.Get(EVAupgrade) != null;
                evUpgrade.active             = upgradeAvailable;
                evUpgrade.guiActiveUnfocused = upgradeAvailable;
                if (upgradeAvailable)
                {
                    evUpgrade.externalToEVAOnly = true;
                    evUpgrade.unfocusedRange    = (float)Math.Max(1.0, EvaRefurbishRange);
                    var targetChem = RealBatteryChemistryDB.Get(EVAupgrade);
                    double scMax = part.Resources.Contains("StoredCharge")
                        ? part.Resources["StoredCharge"].maxAmount : 0.0;
                    int needSp = scMax > 0 ? (int)Math.Ceiling(targetChem.SparePartsPerKWh * scMax) : 0;
                    evUpgrade.guiName = Localizer.Format("#LOC_RB_EVAUpgrade", targetChem.displayName, needSp);
                    RBLog.Info($"[RB_AfterOnStart] EvaUpgrade visible: target='{EVAupgrade}', needSp={needSp}");
                }
                else
                {
                    RBLog.Info($"[RB_AfterOnStart] EvaUpgrade hidden: EVAupgrade='{EVAupgrade}'");
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
