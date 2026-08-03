using System;
using System.Collections.Generic;

using UnityEngine;

namespace RealBattery
{
    class RealBatteryLoadMaster : global::VesselModule
    {
        private bool _deadBandFlushed = false; // Tracks whether we've flushed the dead band for non-fixed batteries in the current frame.

        // Charge/discharge branch state, kept across ticks so the threshold check below can
        // apply hysteresis (Schmitt trigger) instead of a bare zero-crossing comparison.
        private enum EcLoadMode { Idle, Charging, Discharging }
        private EcLoadMode _lastLoadMode = EcLoadMode.Idle;

        // Hysteresis margin (fraction of vessel EC_maxAmount) around HighEClevel/LowEClevel.
        // Background consumer noise (antennas, reaction wheels, etc.) can make EC_amount
        // hover right at a threshold; without a margin the branch below flips every tick,
        // causing lastECpower/GUI_power and the SystemHeat idle flux to jitter. Only gates
        // which branch runs this tick - the EC_delta amount passed to XferECtoRealBattery
        // is unchanged.
        private const double EC_MODE_HYSTERESIS_FRACTION = 0.002; // 0.2% of EC_maxAmount

        // Static sort comparisons for the discharge/charge participant ordering below (P3):
        // avoids the per-tick OrderBy...ToList() allocation chain in favor of an in-place
        // List<T>.Sort(). Not guaranteed stable on ties (unlike LINQ's OrderBy), but a 3-way tie
        // on priority+SOC+rate makes the batteries functionally interchangeable anyway.
        private static readonly Comparison<RealBattery> DischargeOrderComparison = (a, b) =>
        {
            int c = b.part.GetResourcePriority().CompareTo(a.part.GetResourcePriority());
            if (c != 0) return c;
            c = b.SC_SOC.CompareTo(a.SC_SOC);
            if (c != 0) return c;
            return b.Crate.CompareTo(a.Crate);
        };
        private static readonly Comparison<RealBattery> ChargeOrderComparison = (a, b) =>
        {
            int c = b.part.GetResourcePriority().CompareTo(a.part.GetResourcePriority());
            if (c != 0) return c;
            c = a.SC_SOC.CompareTo(b.SC_SOC); // ascending: fill the least-charged battery first
            if (c != 0) return c;
            return b.Crate.CompareTo(a.Crate);
        };

        protected override void OnStart()
        {
            base.OnStart();

            GameEvents.onVesselChange.Add(ReadAllRealBatteryModules);
            GameEvents.onVesselStandardModification.Add(ReadAllRealBatteryModules);
            GameEvents.onVesselWasModified.Add(ReadAllRealBatteryModules);
            GameEvents.onVesselCrewWasModified.Add(ReadAllRealBatteryModules);

            ReadAllRealBatteryModules();
        }

        private void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(ReadAllRealBatteryModules);
            GameEvents.onVesselStandardModification.Remove(ReadAllRealBatteryModules);
            GameEvents.onVesselWasModified.Remove(ReadAllRealBatteryModules);
            GameEvents.onVesselCrewWasModified.Remove(ReadAllRealBatteryModules);
        }

        private List<RealBattery> rbList = new List<RealBattery>();
        public void ReadAllRealBatteryModules(Vessel gameEventVessel = null)
        {
            if (vessel == null || vessel.Parts == null)
                return;

            if (!vessel.loaded)
                return;

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[ReadAllRealBatteryModules] vessel='{vessel.GetDisplayName()}'");

            rbList = vessel.FindPartModulesImplementing<RealBattery>();

            // Cache the max Engineer specialist level once per vessel structural/crew change
            // instead of every battery re-scanning all vessel parts on every EngineerBonus()
            // call (O(batteries x parts) per tick otherwise). Pushed to each battery directly
            // since the LoadMaster already holds the up-to-date rbList here.
            int engLvl = Mathf.Clamp(ModuleEnergyEstimator.GetMaxSpecialistLevel(vessel, "Engineer"), 0, 5);
            foreach (RealBattery rb in rbList)
                rb.CachedEngineerLevel = engLvl;
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || !vessel.loaded)
                return;

            // get vessel wide EC status (missing or available)
            vessel.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out double EC_amount, out double EC_maxAmount);

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[FixedUpdate] vessel='{vessel.GetDisplayName()}' EC={EC_amount:F1}/{EC_maxAmount:F1}, batteries={rbList.Count}");

            if (EC_maxAmount > 0 && rbList.Count != 0)
            {
                // HighEClevel: use the lowest among active batteries so an unusual threshold
                // on one battery doesn't block charging for the whole vessel.
                // LowEClevel: use the highest among active batteries (most conservative) so
                // a battery with an unusually low threshold doesn't force premature discharge.
                // Falls back to scanning ALL batteries if none are enabled. Single pass, no
                // allocations (replaces the former Where().ToList() + two OrderBy().First()).
                double highEnabled = double.MaxValue, lowEnabled = double.MinValue;
                double highAll = double.MaxValue, lowAll = double.MinValue;
                bool anyEnabled = false;
                int nScale = 0;
                foreach (RealBattery rbScan in rbList)
                {
                    if (rbScan.HighEClevel < highAll) highAll = rbScan.HighEClevel;
                    if (rbScan.LowEClevel > lowAll) lowAll = rbScan.LowEClevel;
                    if (!rbScan.BatteryDisabled)
                    {
                        anyEnabled = true;
                        if (rbScan.HighEClevel < highEnabled) highEnabled = rbScan.HighEClevel;
                        if (rbScan.LowEClevel > lowEnabled) lowEnabled = rbScan.LowEClevel;
                        if (rbScan.CrateScale != "false") nScale++;
                    }
                }
                double HighEClevel = anyEnabled ? highEnabled : highAll;
                double LowEClevel  = anyEnabled ? lowEnabled : lowAll;

                double EC_delta_highLevel = EC_amount - EC_maxAmount * HighEClevel;  //amount of available EC for charging: 980 - 1000 * 0.95 =   30EC
                double EC_delta_lowLevel =  EC_amount - EC_maxAmount * LowEClevel; //amount of missing EC for discharging:  500 - 1000 * 0.9  = -400EC

                // -----------------------------------------------------------------
                // CrateScale: chemistries that opt in scale their effective C-rate by the
                // number of participating batteries, for this tick only (never persisted).
                // Original Crate values are restored in the finally block. (nScale computed
                // in the scan above.) Both curves are neutral (m=1) at n=1 and use each
                // battery's own CrateScaleFactor (f) as the shared steepness/asymptote param
                // (plan M5/T5.2, chosen at STOP 5a over the old ×n / ÷n-with-floor curves):
                //   "add"    — saturating: m(n) = 1 + (f-1)*(n-1)/(n-1+f), asymptote f
                //   "reduce" — hyperbolic: m(n) = f/(n-1+f), decays smoothly towards 0
                // -----------------------------------------------------------------
                var crateBackup = new Dictionary<RealBattery, float>();
                if (nScale > 0)
                {
                    foreach (RealBattery rb in rbList)
                    {
                        if (rb.BatteryDisabled || rb.CrateScale == "false") continue;
                        crateBackup[rb] = rb.Crate;
                        float f = Math.Max(0.01f, rb.CrateScaleFactor);
                        if (rb.CrateScale == "add")
                            rb.Crate = rb.Crate * (1f + (f - 1f) * (nScale - 1) / (nScale - 1 + f));
                        else if (rb.CrateScale == "reduce")
                            rb.Crate = rb.Crate * (f / (nScale - 1 + f));
                    }
                }

                // Schmitt-trigger branch selection: which side of the margin decides depends
                // on the mode we were already in, so a value hovering right at the threshold
                // doesn't flip the branch every tick.
                double hysteresis = EC_maxAmount * EC_MODE_HYSTERESIS_FRACTION;
                bool wantDischarge;
                bool wantCharge;
                switch (_lastLoadMode)
                {
                    case EcLoadMode.Discharging:
                        wantDischarge = EC_delta_lowLevel < hysteresis;
                        wantCharge = !wantDischarge && EC_delta_highLevel > hysteresis;
                        break;
                    case EcLoadMode.Charging:
                        wantCharge = EC_delta_highLevel > -hysteresis;
                        wantDischarge = !wantCharge && EC_delta_lowLevel < -hysteresis;
                        break;
                    default:
                        wantDischarge = EC_delta_lowLevel < -hysteresis;
                        wantCharge = !wantDischarge && EC_delta_highLevel > hysteresis;
                        break;
                }

                try
                {
                if (wantDischarge)
                {
                    _lastLoadMode = EcLoadMode.Discharging;
                    // sort the list by SC_SOC for discharging and run discharge
                    rbList.Sort(DischargeOrderComparison);

                    foreach (RealBattery rb in rbList)
                    {
                        double deltaSucked = rb.XferECtoRealBattery(EC_delta_lowLevel);
                        EC_delta_lowLevel -= deltaSucked;
                        _deadBandFlushed = false;
                    }
                }
                else if (wantCharge)
                {
                    _lastLoadMode = EcLoadMode.Charging;
                    //now reverse cowgirl for charging
                    rbList.Sort(ChargeOrderComparison);

                    foreach (RealBattery rb in rbList)
                    {
                        double deltaSucked = rb.XferECtoRealBattery(EC_delta_highLevel);
                        EC_delta_highLevel -= deltaSucked;
                        _deadBandFlushed = false;
                    }
                }
                else
                {
                    _lastLoadMode = EcLoadMode.Idle;
                    // No net charging or discharging requested by levels.
                    // Still allow fixed-output batteries to push EC continuously.
                    foreach (RealBattery rb in rbList)
                    {
                        if (rb.FixedOutput && rb.SC_SOC > 0 && !rb.BatteryDisabled)
                        {
                            // Pass EC_delta_highLevel (>= 0 here) — rb will discharge anyway when FixedOutput is true.
                            double deltaSucked = rb.XferECtoRealBattery(EC_delta_highLevel);
                            EC_delta_highLevel -= deltaSucked;
                        }
                        else if (!rb.FixedOutput && !_deadBandFlushed)
                        {
                            // First dead-band frame: flush residual lastECpower and SH flux
                            rb.XferECtoRealBattery(0);
                        }
                    }
                    _deadBandFlushed = true;
                }
                }
                finally
                {
                    // Restore original C-rates so the scaling lasts only this tick.
                    foreach (var kv in crateBackup)
                        kv.Key.Crate = kv.Value;
                }
            }
        }
    }
}
