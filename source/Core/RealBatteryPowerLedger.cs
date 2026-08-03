using System;
using System.Collections.Generic;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryPowerLedger  [ContractVersion 1, added campaign B / 2026-08]
    //  Public, version-stable reporting contract for third-party mods that manage their own
    //  background/offline energy accounting (e.g. a rover autopilot that keeps driving while
    //  the vessel is unloaded) and need to reconcile it against RealBattery once the vessel is
    //  loaded again. Speaks plain ElectricCharge (EC) units only; StoredCharge, SC_SOC,
    //  WearCounter and the 3600 EC/SC ratio stay implementation details.
    //
    //  Semantics: REPORTING, not commanding. The caller declares "I consumed X EC over this
    //  period" and RealBattery — the sole authority on its own model — decides how to apply it
    //  (pro-quota StoredCharge drain, wear, BatteryLife, SC_SOC). This deliberately replaces
    //  the M6b "DrainEc/ChargeEc" command-style surface (archived, unshipped, in
    //  RealBatteryAPI_draft/) after Pietro found that shape unconvincing — see
    //  PLAN_BV_API_Integration.md §B1 for the full design rationale.
    //
    //  Idempotency/anti-double-reporting is the CALLER's responsibility: this contract holds
    //  no state between calls (beyond the one-time-per-vessel unloaded-vessel warning below).
    //
    //  ContractVersion increments only for additive, non-breaking changes — existing method
    //  signatures are frozen once shipped. See RealBatteryPowerLedgerWrapper.cs (T-B1.3) for a
    //  copy-paste, reflection-based consumer template that degrades to a no-op when
    //  RealBattery isn't installed.
    // ============================================================================
    public static class RealBatteryPowerLedger
    {
        public const int ContractVersion = 1;

        // Warn once per vessel (session-lifetime), not on every call — same pattern as
        // AlarmManager's _knownNoRbVessels negative cache.
        private static readonly HashSet<Guid> _warnedUnloadedVessels = new HashSet<Guid>();

        /// <summary>
        /// Sum of DischargeRate (EC/s, already derated by BatteryLife/Crate) across all
        /// eligible RealBattery parts on the vessel. Zero if the vessel isn't loaded or has
        /// none. No logging (may be called frequently by a caller's own planning code).
        /// </summary>
        public static double GetDischargeableEcPerSec(Vessel vessel)
        {
            double total = 0.0;
            foreach (var rb in EnumerateEligibleBatteries(vessel))
                total += rb.DischargeRate; // already EC/s (kW)-equivalent, no conversion needed
            return total;
        }

        /// <summary>
        /// Currently stored energy (EC-equivalent) across all eligible RealBattery parts on
        /// the vessel. Zero if the vessel isn't loaded or has none. No logging.
        /// </summary>
        public static double GetAvailableEc(Vessel vessel)
        {
            double total = 0.0;
            foreach (var rb in EnumerateEligibleBatteries(vessel))
            {
                PartResource sc = rb.part.Resources.Get("StoredCharge");
                if (sc == null) continue;
                total += sc.amount * RealBattery.EC2SCratio;
            }
            return total;
        }

        /// <summary>
        /// Reports that the caller consumed ecConsumed EC from this vessel's RealBatteries
        /// over the last deltaTimeSeconds. RealBattery drains StoredCharge pro-quota of
        /// effective capacity across all eligible parts, credits wear/BatteryLife/SC_SOC
        /// exactly as the live simulation would, and returns the EC actually covered — request
        /// semantics, like Part.RequestResource: may be less than ecConsumed if the batteries
        /// can't cover it. deltaTimeSeconds is used only for a plausibility check against
        /// GetDischargeableEcPerSec (logged, never blocking), not for any computation.
        ///
        /// Loaded vessels only (vessel.parts populated) — returns 0.0 and logs a one-time
        /// warning per vessel otherwise. Not supported: ProtoVessel/unloaded-vessel reporting.
        /// </summary>
        public static double ReportConsumedEc(Vessel vessel, double ecConsumed, double deltaTimeSeconds)
        {
            if (vessel == null || ecConsumed <= 0.0) return 0.0;

            if (!vessel.loaded)
            {
                if (_warnedUnloadedVessels.Add(vessel.id))
                    RBLog.Warn($"[ReportConsumedEc] '{vessel.vesselName}': called on a vessel that isn't loaded — this contract version only supports loaded vessels; nothing was drained.");
                return 0.0;
            }

            double scRequested = ecConsumed / RealBattery.EC2SCratio;
            double scCovered = DrainSc(vessel, scRequested, out int participantCount);
            double ecCovered = scCovered * RealBattery.EC2SCratio;

            // Plausibility check: flag if the caller reports far more than physically
            // dischargeable over the declared period. Never blocks — only signals.
            if (deltaTimeSeconds > 0.0)
            {
                double maxPlausible = GetDischargeableEcPerSec(vessel) * deltaTimeSeconds * 1.5;
                if (ecConsumed > maxPlausible)
                    RBLog.Warn($"[ReportConsumedEc] '{vessel.vesselName}': reported {ecConsumed:F1} EC over {deltaTimeSeconds:F0}s exceeds 1.5x the dischargeable rate ({maxPlausible:F1} EC) — plausibility check only, not blocked.");
            }

            RBLog.Info($"[ReportConsumedEc] '{vessel.vesselName}': reported={ecConsumed:F1} EC over {deltaTimeSeconds:F0}s, covered={ecCovered:F1} EC ({scCovered:F4} SC) across {participantCount} batteries");

            return ecCovered;
        }

        // ------------------------------------------------------------------------------

        // Eligibility (D6, PLAN_BV_API_Integration.md §B1.1bis): enabled, not a one-shot
        // FixedOutput (thermal) battery (D1), has StoredCharge. IsPrimary and InfiniteCycles
        // batteries participate with no special-casing here (D2/D3) — InfiniteCycles skips
        // wear in DrainSc below, same as the live simulation.
        private static bool IsEligible(RealBattery rb, Part part)
        {
            return !rb.BatteryDisabled && !rb.FixedOutput && part.Resources.Contains("StoredCharge");
        }

        private static IEnumerable<RealBattery> EnumerateEligibleBatteries(Vessel vessel)
        {
            if (vessel?.parts == null) yield break;
            foreach (Part part in vessel.parts)
            {
                if (!part.Modules.Contains("RealBattery")) continue;
                RealBattery rb = part.Modules.GetModule<RealBattery>();
                if (rb == null || !IsEligible(rb, part)) continue;
                yield return rb;
            }
        }

        private static double EffectiveLife(RealBattery rb)
        {
            double actualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
            return Math.Min(actualLife, rb.ThermalCapFactor);
        }

        // Drains scRequested (StoredCharge units) pro-quota of effective capacity across all
        // eligible batteries, clamping each part at 0 individually — mirrors the pro-quota
        // pattern already used by BackgroundSimulator.ApplySnapshot and RealBatteryLoadMaster.
        // Credits wear/BatteryLife/SC_SOC exactly as the live simulation would. Returns the SC
        // actually drained (<= scRequested).
        private static double DrainSc(Vessel vessel, double scRequested, out int participantCount)
        {
            participantCount = 0;
            if (scRequested <= 1e-9) return 0.0;

            var candidates = new List<RealBattery>();
            var capacities = new List<double>();
            double totalCapacity = 0.0;

            foreach (var rb in EnumerateEligibleBatteries(vessel))
            {
                PartResource sc = rb.part.Resources.Get("StoredCharge");
                if (sc == null) continue;

                double effectiveCap = sc.maxAmount * EffectiveLife(rb);
                if (effectiveCap <= 1e-9) continue;

                candidates.Add(rb);
                capacities.Add(effectiveCap);
                totalCapacity += effectiveCap;
            }

            if (totalCapacity <= 1e-9) return 0.0;

            double totalDrained = 0.0;
            for (int i = 0; i < candidates.Count; i++)
            {
                RealBattery rb = candidates[i];
                PartResource sc = rb.part.Resources.Get("StoredCharge");
                double share = capacities[i] / totalCapacity;
                double requested = scRequested * share;

                double before = sc.amount;
                double after = Math.Max(0.0, before - requested);
                double drained = before - after;
                if (drained <= 1e-9) continue;

                sc.amount = after;
                totalDrained += drained;
                participantCount++;

                double wearShare = 0.0;
                if (!rb.InfiniteCycles)
                {
                    wearShare = drained / rb.EngineerBonus();
                    rb.WearCounter += wearShare;
                    rb.UpdateBatteryLife();
                }

                rb.SC_SOC = sc.maxAmount > 0 ? sc.amount / sc.maxAmount : 0.0;

                if (RBLog.VerboseEnabled)
                    RBLog.Verbose($"[ReportConsumedEc] '{rb.part.partInfo?.title}': -{drained:F4} SC, wear +{wearShare:F4}, life={rb.BatteryLife:P1}, soc={rb.SC_SOC:P1}");
            }

            return totalDrained;
        }
    }
}
