using KSP.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SystemHeat;
using TMPro;
using UnityEngine;
using static PartModule;

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
            float tempNow = RealBatterySettings.UseSystemHeat && systemHeat != null
                ? systemHeat.currentLoopTemperature
                : (float)part.temperature;

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

        // --- Wire event at runtime so range reflects field value ---------------------

        public void RB_AfterOnStart()
        {
            var ev = Events[nameof(EvaRefurbish)];
            if (ev == null) return;

            // Hide button entirely if prohibited
            if (!EvaRefurbishEnabled || !RealBatterySettings.EnableEVARefurbush)
            {
                ev.active = false;
                ev.guiActiveUnfocused = false;
                return;
            }
            
            ev.guiActiveUnfocused = true;
            ev.externalToEVAOnly = true;
            ev.unfocusedRange = (float)Math.Max(1.0, EvaRefurbishRange);

            // Compute and display required SpareParts in PAW label (constant per part)
            double scMax = part.Resources.Contains("StoredCharge") ? part.Resources["StoredCharge"].maxAmount : 0.0;
            int needSp = scMax > 0 ? (int)Math.Ceiling(SparePartsPerKWh * scMax) : 0;
            ev.guiName = Localizer.Format("#LOC_RB_EVARepair", needSp);
            
            /*// Initial label (will be refreshed dynamically if enabled)
            int needSp = ComputeNeededSp();
            ev.guiName = Localizer.Format("#LOC_RB_EVARepair", needSp);*/
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
