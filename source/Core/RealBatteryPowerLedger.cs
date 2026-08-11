using System;
using System.Collections.Generic;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryPowerLedger  [ContractVersion 3, added campaign B / 2026-08]
    //  Public, version-stable reporting contract for third-party mods that manage their own
    //  background/offline energy accounting (e.g. a rover autopilot that keeps driving while
    //  the vessel is unloaded) and need to reconcile it against RealBattery once the vessel is
    //  loaded again. Speaks plain ElectricCharge (EC) units only; StoredCharge, SC_SOC,
    //  WearCounter and the 3600 EC/SC ratio stay implementation details — internally every
    //  figure below is computed from StoredCharge (kWh), the unit RB's own model actually
    //  reasons in, and converted to EC only at the return statement (RealBattery.EC2SCratio).
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
    //
    //  ContractVersion 2 (2026-08, backlog point 2 — vessel autonomy estimate): five read-only
    //  additions, all loaded-vessels-only like the ContractVersion 1 read methods above, driven
    //  by the same background-simulation figures RB's own alarm system (ExpUT) already trusts:
    //  GetMaxEc / GetEffectiveMaxEc (nominal vs. wear/thermal-derated capacity), GetNetEcPerSecGross
    //  / GetNetEcPerSecTrue (with/without solar, see BackgroundSimulator.VesselEnergySnapshot),
    //  and GetSecondsToEmpty (the same quantity behind the low-power alarm's ExpUT, but without
    //  the configurable alarm lead-time baked in).
    //
    //  ContractVersion 3 (2026-08, same backlog item — consolidation + live figure): the four
    //  loaded-vessel aggregates from v1/v2 (GetMaxEc, GetEffectiveMaxEc, GetAvailableEc,
    //  GetDischargeableEcPerSec) no longer walk the vessel's parts on every call — they read a
    //  single cache RealBatteryLoadMaster recomputes once per FixedUpdate, so every caller
    //  (RB's own UI, an RPM/MAS prop, any third-party mod) shares one computation instead of
    //  each re-deriving its own. Signatures/semantics are unchanged (additive per the rule
    //  above). New: GetNetEcPerSecLive, the true instantaneous net EC/s for a *loaded* vessel
    //  (sum of each battery's raw, unsmoothed lastECpower — not the display-smoothed GUI_power
    //  a PAW reads) — the live counterpart to GetNetEcPerSecGross/True, which stay
    //  snapshot-based and are still the only source for an unloaded/background vessel.
    // ============================================================================
    public static class RealBatteryPowerLedger
    {
        public const int ContractVersion = 3;

        // Warn once per vessel (session-lifetime), not on every call — same pattern as
        // AlarmManager's _knownNoRbVessels negative cache.
        private static readonly HashSet<Guid> _warnedUnloadedVessels = new HashSet<Guid>();

        /// <summary>
        /// Sum of DischargeRate (EC/s, already derated by BatteryLife/Crate) across all
        /// eligible RealBattery parts on the vessel. Zero if the vessel isn't loaded or has
        /// none. Backed by RealBatteryLoadMaster's per-FixedUpdate cache (see remarks on
        /// GetMaxEc) — no logging, cheap enough to call every frame.
        /// </summary>
        public static double GetDischargeableEcPerSec(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return 0.0;
            var lm = RealBatteryLoadMaster.GetInstance(vessel);
            return lm?.CachedDischargeableEcPerSec ?? 0.0;
        }

        /// <summary>
        /// Currently stored energy (EC-equivalent) across all eligible RealBattery parts on
        /// the vessel. Zero if the vessel isn't loaded or has none. Backed by
        /// RealBatteryLoadMaster's per-FixedUpdate cache (see remarks on GetMaxEc) — no
        /// logging, cheap enough to call every frame.
        /// </summary>
        public static double GetAvailableEc(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return 0.0;
            var lm = RealBatteryLoadMaster.GetInstance(vessel);
            return lm?.CachedAvailableEc ?? 0.0;
        }

        /// <summary>
        /// Nominal (rated) energy capacity, EC-equivalent, across all eligible RealBattery
        /// parts — sc.maxAmount, undiminished by wear or thermal derating. Zero if the vessel
        /// isn't loaded or has none.
        ///
        /// This and the other read-only vessel-wide aggregates below (GetEffectiveMaxEc,
        /// GetAvailableEc, GetDischargeableEcPerSec, GetNetEcPerSecLive) are O(1) reads of a
        /// single aggregate RealBatteryLoadMaster (the same VesselModule that already drives
        /// RB's live EC/SC transfer) recomputes once per FixedUpdate from its own battery list
        /// — not a fresh per-call walk of the vessel's parts. Every caller (RB's own UI, an RPM/MAS
        /// prop, a third-party mod) reads the exact same number, computed once per tick
        /// regardless of how many callers ask. May lag by up to one physics tick behind a
        /// structural change (new/removed battery) — same latency RealBatteryLoadMaster's own
        /// EC/SC transfer logic already has.
        /// </summary>
        public static double GetMaxEc(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return 0.0;
            var lm = RealBatteryLoadMaster.GetInstance(vessel);
            return lm?.CachedMaxEc ?? 0.0;
        }

        /// <summary>
        /// Effective (usable right now) energy capacity, EC-equivalent, across all eligible
        /// RealBattery parts — sc.maxAmount derated by wear (BatteryLife, if wear is enabled)
        /// and ThermalCapFactor, same EffectiveLife() used internally by ReportConsumedEc's
        /// pro-quota drain. Always &lt;= GetMaxEc. Zero if the vessel isn't loaded or has none.
        /// See remarks on GetMaxEc for how this is computed/cached.
        /// </summary>
        public static double GetEffectiveMaxEc(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return 0.0;
            var lm = RealBatteryLoadMaster.GetInstance(vessel);
            return lm?.CachedEffectiveMaxEc ?? 0.0;
        }

        /// <summary>
        /// Net EC/s currently flowing into (positive) or out of (negative) this vessel's
        /// eligible RealBatteries, right now — sum of each battery's <c>lastECpower</c>, the
        /// raw per-tick transfer RealBattery itself computes and BackgroundSimulator already
        /// trusts as ground truth for calibrating its own solar/consumption estimate. Distinct
        /// from GUI_power (a separately-smoothed, display-only value meant to keep the PAW
        /// readout from flickering) — this is the unsmoothed figure, safe for a caller doing
        /// its own math with it. Zero if the vessel isn't loaded, has no eligible batteries, or
        /// is genuinely idle. See remarks on GetMaxEc for how this is computed/cached.
        ///
        /// For an unloaded/background vessel, see GetNetEcPerSecGross/GetNetEcPerSecTrue
        /// instead — this method only ever reflects a loaded vessel's real, current state.
        /// </summary>
        public static double GetNetEcPerSecLive(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return 0.0;
            var lm = RealBatteryLoadMaster.GetInstance(vessel);
            return lm?.CachedNetEcPerSecLive ?? 0.0;
        }

        /// <summary>
        /// Net vessel EC balance including solar production, EC/s (negative = net drain,
        /// positive = net surplus/charging) — same figure RB's own background simulation and
        /// low-power alarm (ExpUT) are computed from
        /// (<see cref="BackgroundSimulator.VesselEnergySnapshot.netEC_Gross"/>).
        /// Reflects the most recently captured snapshot for this vessel (captured on scene
        /// switch, vessel switch, game save, and once on flight-scene load) — not recomputed
        /// on every call. Returns <see cref="double.NaN"/> if no snapshot exists yet for this
        /// vessel (e.g. queried in the same frame the vessel first loads). No logging.
        /// </summary>
        public static double GetNetEcPerSecGross(Vessel vessel)
        {
            if (vessel == null) return double.NaN;
            if (!BackgroundSimulator.TryGetEnergySnapshot(vessel.id, out var snap)) return double.NaN;
            return snap.netEC_Gross;
        }

        /// <summary>
        /// Net vessel EC balance excluding solar production, EC/s (negative = net drain,
        /// positive = net surplus) — the conservative/worst-case counterpart of
        /// <see cref="GetNetEcPerSecGross"/>, useful for an estimate that shouldn't assume
        /// current sun exposure holds (e.g. a vessel about to enter a body's shadow). Same
        /// staleness caveat as GetNetEcPerSecGross. Returns <see cref="double.NaN"/> if no
        /// snapshot exists yet. No logging.
        /// </summary>
        public static double GetNetEcPerSecTrue(Vessel vessel)
        {
            if (vessel == null) return double.NaN;
            if (!BackgroundSimulator.TryGetEnergySnapshot(vessel.id, out var snap)) return double.NaN;
            return snap.netEC_True;
        }

        /// <summary>
        /// Seconds until this vessel's RealBatteries are depleted at the current net rate
        /// (<see cref="GetNetEcPerSecGross"/>) — the same underlying quantity behind the
        /// low-power alarm's ExpUT, but returned as a plain duration with no alarm lead-time
        /// subtracted (RealBatterySettings.LowPowerLeadSeconds, player-configurable, is an
        /// alarm-timing concern, not part of this figure). Returns
        /// <see cref="double.PositiveInfinity"/> if the vessel is at a net surplus or exactly
        /// balanced (never runs out at the current rate), or <see cref="double.NaN"/> if no
        /// data is available yet. No logging.
        /// </summary>
        public static double GetSecondsToEmpty(Vessel vessel)
        {
            double netGross = GetNetEcPerSecGross(vessel);
            if (double.IsNaN(netGross)) return double.NaN;
            if (netGross >= -1e-6) return double.PositiveInfinity;

            double availableEc = GetAvailableEc(vessel);
            return availableEc / Math.Abs(netGross);
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
        // internal (not private): RealBatteryLoadMaster's per-tick cache reuses this exact
        // definition so the vessel-wide aggregate and DrainSc's pro-quota fan-out can never
        // silently disagree on which batteries count.
        internal static bool IsEligible(RealBattery rb, Part part)
        {
            return !rb.BatteryDisabled && !rb.FixedOutput && part.Resources.Contains("StoredCharge");
        }

        internal static IEnumerable<RealBattery> EnumerateEligibleBatteries(Vessel vessel)
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

        // internal (not private): shared with RealBatteryLoadMaster's per-tick cache, same reason as IsEligible above.
        internal static double EffectiveLife(RealBattery rb)
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
