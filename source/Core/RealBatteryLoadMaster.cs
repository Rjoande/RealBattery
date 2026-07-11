using System;
using System.Collections.Generic;
using System.Linq;

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

        protected override void OnStart()
        {
            base.OnStart();            

            GameEvents.onVesselChange.Add(ReadAllRealBatteryModules);
            GameEvents.onVesselStandardModification.Add(ReadAllRealBatteryModules);
            GameEvents.onVesselWasModified.Add(ReadAllRealBatteryModules);

            ReadAllRealBatteryModules();
        }
        
        private void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(ReadAllRealBatteryModules);
            GameEvents.onVesselStandardModification.Remove(ReadAllRealBatteryModules);
            GameEvents.onVesselWasModified.Remove(ReadAllRealBatteryModules);
        }

        private List<RealBattery> rbList = new List<RealBattery>();
        public void ReadAllRealBatteryModules(Vessel gameEventVessel = null)
        {
            RBLog.Verbose("[LoadMaster] INF ReadAllRealBatteryModules");
            RBLog.Verbose("[LoadMaster] INF ReadAllRealBatteryModules vesselName: " + vessel.GetDisplayName());

            if (vessel == null || vessel.Parts == null)
            {
                //nothing to do
                return;
            }

            if (!vessel.loaded)
                return;

            rbList = vessel.FindPartModulesImplementing<RealBattery>();
        }           

        public void FixedUpdate()
        {
            RBLog.Verbose("[LoadMaster] INF FixedUpdate vesselName: " + vessel.GetDisplayName());
            if (!HighLogic.LoadedSceneIsFlight)
            {
                RBLog.Verbose("[LoadMaster] INF return because LoadedSceneIsFlight");
                return;
            }
            
            if (vessel == null)
            {
                RBLog.Verbose("[LoadMaster] INF return because vessel == null");
                return;
            }

            if (!vessel.loaded)
            {
                RBLog.Verbose("[LoadMaster] INF return because loaded");
                return;
            }

            // get vessel wide EC status (missing or available)
            vessel.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out double EC_amount, out double EC_maxAmount);

            
            RBLog.Verbose("[LoadMaster] INF FixedUpdate EC_maxAmount: " + EC_maxAmount);
            RBLog.Verbose("[LoadMaster] INF FixedUpdate EC_amount: " + EC_amount);
            RBLog.Verbose("[LoadMaster] INF FixedUpdate rbList.Count: " + rbList.Count);

            if (EC_maxAmount > 0 && rbList.Count != 0)
            {
                // HighEClevel: use the lowest among active batteries so an unusual threshold
                // on one battery doesn't block charging for the whole vessel.
                // LowEClevel: use the highest among active batteries (most conservative) so
                // a battery with an unusually low threshold doesn't force premature discharge.
                var activeBatteries = rbList.Where(rb => !rb.BatteryDisabled).ToList();
                var refSource = activeBatteries.Any() ? activeBatteries : rbList;

                double HighEClevel = refSource.OrderBy(rb => rb.HighEClevel).First().HighEClevel;
                double LowEClevel  = refSource.OrderByDescending(rb => rb.LowEClevel).First().LowEClevel;

                double EC_delta_highLevel = EC_amount - EC_maxAmount * HighEClevel;  //amount of available EC for charging: 980 - 1000 * 0.95 =   30EC
                double EC_delta_lowLevel =  EC_amount - EC_maxAmount * LowEClevel; //amount of missing EC for discharging:  500 - 1000 * 0.9  = -400EC

                // -----------------------------------------------------------------
                // CrateScale: chemistries that opt in scale their effective C-rate by
                // the number of participating batteries, for this tick only (never
                // persisted). "add" multiplies by n, "reduce" multiplies by 1/n with a
                // 0.1x floor. Original Crate values are restored in the finally block.
                // -----------------------------------------------------------------
                int nScale = rbList.Count(rb => rb.CrateScale != "false" && !rb.BatteryDisabled);
                var crateBackup = new Dictionary<RealBattery, float>();
                if (nScale > 0)
                {
                    foreach (RealBattery rb in rbList)
                    {
                        if (rb.BatteryDisabled || rb.CrateScale == "false") continue;
                        crateBackup[rb] = rb.Crate;
                        if (rb.CrateScale == "add")
                            rb.Crate = rb.Crate * nScale;
                        else if (rb.CrateScale == "reduce")
                            rb.Crate = (float)Math.Max(rb.Crate * 0.1, rb.Crate / nScale);
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
                    rbList = rbList.OrderByDescending(rb => rb.part.GetResourcePriority())
                        .ThenByDescending(rb => rb.SC_SOC)
                        .ThenByDescending(rb => rb.Crate)
                        .ToList();

                    foreach (RealBattery rb in rbList)
                    {
                        RBLog.Verbose("[LoadMaster] INF EC_delta_lowLevel < 0");
                        RBLog.Verbose(String.Format("{0:F1} - {1:F1} - {2:F1} - {3:F1}", EC_delta_highLevel, EC_delta_lowLevel, EC_amount, EC_maxAmount));
                        double deltaSucked = rb.XferECtoRealBattery(EC_delta_lowLevel);

                        RBLog.Verbose("RealBatteryLoadMaster: deltaSucked: " + deltaSucked.ToString());

                        EC_delta_lowLevel -= deltaSucked;
                        _deadBandFlushed = false;
                    } 
                }
                else if (wantCharge)
                {
                    _lastLoadMode = EcLoadMode.Charging;
                    //now reverse cowgirl for charging
                    rbList = rbList.OrderByDescending(rb => rb.part.GetResourcePriority())
                        .ThenBy(rb => rb.SC_SOC)
                        .ThenByDescending(rb => rb.Crate)
                        .ToList();

                    foreach (RealBattery rb in rbList)
                    {
                        RBLog.Verbose("[LoadMaster] INF EC_delta_highLevel > 0");
                        RBLog.Verbose(String.Format("{0:F1} - {1:F1} - {2:F1} - {3:F1}", EC_delta_highLevel, EC_delta_lowLevel, EC_amount, EC_maxAmount));
                        double deltaSucked = rb.XferECtoRealBattery(EC_delta_highLevel);

                        RBLog.Verbose("RealBatteryLoadMaster: deltaSucked: " + deltaSucked.ToString());

                        EC_delta_highLevel -= deltaSucked;
                        _deadBandFlushed = false;
                    }
                }
                else
                {
                    _lastLoadMode = EcLoadMode.Idle;
                    // No net charging or discharging requested by levels.
                    // Still allow fixed-output batteries to push EC continuously.
                    bool anyFixed = false;
                    foreach (RealBattery rb in rbList)
                    {
                        if (rb.FixedOutput && rb.SC_SOC > 0 && !rb.BatteryDisabled)
                        {
                            anyFixed = true;
                            RBLog.Verbose("[LoadMaster] Call FixedOutput push");
                            // Pass EC_delta_highLevel (>= 0 here) — rb will discharge anyway when FixedOutput is true.
                            double deltaSucked = rb.XferECtoRealBattery(EC_delta_highLevel);
                            RBLog.Verbose("RealBatteryLoadMaster: deltaSucked: " + deltaSucked.ToString());
                            EC_delta_highLevel -= deltaSucked;
                        }
                        else if (!rb.FixedOutput && !_deadBandFlushed)
                        {
                            // First dead-band frame: flush residual lastECpower and SH flux
                            rb.XferECtoRealBattery(0);
                        }
                    }
                    if (!anyFixed)
                    RBLog.Verbose("RealBatteryLoadMaster: nothing to do in the else path");
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
