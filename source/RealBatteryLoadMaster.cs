using RealBattery;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEngine;

namespace RealBattery
{
    class RealBatteryLoadMaster : global::VesselModule
    {
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
                double LowEClevel = rbList.First().LowEClevel; //they all (should) have the same levels tho
                double HighEClevel = rbList.First().HighEClevel;

                double EC_delta_highLevel = EC_amount - EC_maxAmount * HighEClevel;  //amount of available EC for charging: 980 - 1000 * 0.95 =   30EC
                double EC_delta_lowLevel =  EC_amount - EC_maxAmount * LowEClevel; //amount of missing EC for discharging:  500 - 1000 * 0.9  = -400EC

                
                if (EC_delta_lowLevel < 0)
                {
                    // sort the list by SC_SOC for discharging and run discharge
                    rbList = rbList.OrderBy(rb => rb.part.GetResourcePriority()).ThenByDescending(rb => rb.SC_SOC).ToList();

                    foreach (RealBattery rb in rbList)
                    {
                        RBLog.Verbose("[LoadMaster] INF EC_delta_lowLevel < 0");
                        RBLog.Verbose(String.Format("{0:F1} - {1:F1} - {2:F1} - {3:F1}", EC_delta_highLevel, EC_delta_lowLevel, EC_amount, EC_maxAmount));
                        double deltaSucked = rb.XferECtoRealBattery(EC_delta_lowLevel);

                        RBLog.Verbose("RealBatteryLoadMaster: deltaSucked: " + deltaSucked.ToString());

                        EC_delta_lowLevel -= deltaSucked;
                    } 
                }                
                else if (EC_delta_highLevel > 0)
                {
                    //now reverse cowgirl for charging
                    rbList = rbList.OrderByDescending(rb => rb.part.GetResourcePriority()).ThenBy(rb => rb.SC_SOC).ToList();

                    foreach (RealBattery rb in rbList)
                    {
                        RBLog.Verbose("[LoadMaster] INF EC_delta_highLevel > 0");
                        RBLog.Verbose(String.Format("{0:F1} - {1:F1} - {2:F1} - {3:F1}", EC_delta_highLevel, EC_delta_lowLevel, EC_amount, EC_maxAmount));
                        double deltaSucked = rb.XferECtoRealBattery(EC_delta_highLevel);

                        RBLog.Verbose("RealBatteryLoadMaster: deltaSucked: " + deltaSucked.ToString());

                        EC_delta_highLevel -= deltaSucked;
                    }
                }
                else
                {
                    RBLog.Verbose("RealBatteryLoadMaster: nothing to do in the else path");
                }

            }
        }
    }
}
