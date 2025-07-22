using KSP;
using RealBattery;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static RealBattery.RealBatterySettings;
using static VehiclePhysics.EnergyProvider;

namespace RealBattery
{
    public static class BackgroundSimulator
    {
        private static HashSet<Guid> vesselsNeedingRecalculation = new HashSet<Guid>();
        private static Dictionary<Guid, VesselSnapshot> vesselSnapshots = new Dictionary<Guid, VesselSnapshot>();
        private static Dictionary<Guid, VesselEnergySnapshot> energySnapshots = new Dictionary<Guid, VesselEnergySnapshot>();

        public static bool HasSnapshot(Guid vesselId)
        {
            return energySnapshots.ContainsKey(vesselId);
        }

        public static void CaptureSnapshot(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return;

            Debug.Log($"[RealBattery] CaptureSnapshot called in scene {HighLogic.LoadedScene} — vessel.loaded={vessel.loaded}, vessel.packed={vessel.packed}");

            Guid id = vessel.id;
            double currentUT = Planetarium.GetUniversalTime();

            double totalSCamount = 0;
            double totalSCmaxAmount = 0;
            double totalECproduced = 0;
            double totalECconsumed = 0;
            double totalDischargeRate = 0;
            //double totalChargeRate = 0;

            VesselSnapshot snapshot = new VesselSnapshot(); // MOD: uso esplicito compatibile con C# 7.3
            snapshot.vesselId = id;
            snapshot.timestamp = currentUT;
            snapshot.partSnapshots = new List<PartSnapshot>();

            int maxSpecialistLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(vessel, "Engineer");

            foreach (Part part in vessel.parts)
            {
                double partECproduced = 0;
                double partECconsumed = 0;

                ModuleEnergyEstimator.EstimateECUsage(part, ref totalECproduced, ref totalECconsumed, maxSpecialistLevel);

                totalECproduced += partECproduced;
                totalECconsumed += partECconsumed;

                if (partECproduced > 0 || partECconsumed > 0)
                {
                    PartSnapshot partSnap = new PartSnapshot();
                    partSnap.partName = part.partInfo.name;
                    partSnap.VesselECproducedPerSecond = partECproduced;
                    partSnap.VesselECconsumedPerSecond = partECconsumed;
                    snapshot.partSnapshots.Add(partSnap);
                }

                if (part.Modules.Contains("RealBattery"))
                {
                    var pm = part.Modules["RealBattery"];
                    if (pm == null) continue;

                    bool isEnabled = true;
                    var disabledField = pm.Fields["BatteryDisabled"];
                    if (disabledField != null)
                    {
                        var raw = disabledField.GetValue(pm);
                        if (raw != null && bool.TryParse(raw.ToString(), out bool disabled))
                            isEnabled = !disabled; // BatteryDisabled == true -> isEnabled = false
                    }

                    if (!isEnabled)
                    {
                        Debug.Log($"[RealBattery] Skipping disabled battery in part '{part.partInfo.title}'");
                        continue;
                    }

                    PartResource sc = part.Resources.Get("StoredCharge");
                    if (sc != null)
                    {
                        totalSCamount += sc.amount;
                        totalSCmaxAmount += sc.maxAmount;
                    }

                    double dischargeRate = 0;
                    //double chargeEfficiency = 1.0;

                    var dischargeField = pm.Fields["DischargeRate"];
                    if (dischargeField != null)
                    {
                        var rawValue = dischargeField.GetValue(pm);
                        if (rawValue != null && double.TryParse(rawValue.ToString(), out double rate))
                        {
                            dischargeRate = rate;
                            totalDischargeRate += rate;
                            Debug.Log($"[RealBattery] Read DischargeRate={rate:F3} EC/s from part '{part.partInfo.title}'");
                        }
                    }

                    /*var efficiencyField = pm.Fields["SOC_ChargeEfficiency"];
                    if (efficiencyField != null)
                    {
                        var rawEff = efficiencyField.GetValue(pm);
                        if (rawEff != null && double.TryParse(rawEff.ToString(), out double eff))
                        {
                            chargeEfficiency = eff;
                            Debug.Log($"[RealBattery] Read ChargeEfficiency={eff:F3} from part '{part.partInfo.title}'");
                        }
                    }

                    double effectiveChargeRate = dischargeRate * chargeEfficiency;
                    totalChargeRate += effectiveChargeRate;*/
                }
            }

            vesselSnapshots[id] = snapshot;

            VesselEnergySnapshot energySnapshot = new VesselEnergySnapshot();
            energySnapshot.vesselId = id;
            energySnapshot.timestamp = currentUT;
            energySnapshot.storedChargeAmount = totalSCamount;
            energySnapshot.storedChargeMaxAmount = totalSCmaxAmount;
            energySnapshot.totalDischargeRate = totalDischargeRate;
            //energySnapshot.totalChargeRate = totalChargeRate;
            energySnapshot.netECProductionRate = totalECproduced - totalECconsumed;

            energySnapshots[id] = energySnapshot;

            Debug.Log($"[RealBattery] CaptureSnapshot for vessel '{vessel.vesselName}'");
            Debug.Log($"[RealBattery] StoredCharge total: {totalSCamount:F3}/{totalSCmaxAmount:F3} kWh");
            Debug.Log($"[RealBattery] Net EC production rate: {totalECproduced:F3} - {totalECconsumed:F3} = {(totalECproduced - totalECconsumed):F3} EC/s");
            Debug.Log($"[RealBattery] Total DischargeRate: {totalDischargeRate:F3} EC/s");
            //Debug.Log($"[RealBattery] Total ChargeRate: {totalChargeRate:F3} EC/s");
        }

        public static void ApplySnapshot(Vessel vessel)
        {
            if (vessel == null || !energySnapshots.ContainsKey(vessel.id)) return;

            var snap = energySnapshots[vessel.id];

            double currentTime = Planetarium.GetUniversalTime();
            double deltaTime = currentTime - snap.timestamp;

            if (deltaTime < 1.0)
            {
                Debug.Log($"[RealBattery] ApplySnapshot skipped for vessel '{vessel.vesselName}' — Δt = {deltaTime:F1} s too small.");
                return;
            }

            double hoursPerDay = RealBatterySettings.Instance?.GetHoursPerDay() ?? 6.0;
            double deltaSC_vessel = snap.netECProductionRate * deltaTime / 3600.0;

            double totalCapacity = 0;
            List<(PartResource sc, RealBattery rb)> batteries = new List<(PartResource, RealBattery)>();

            foreach (Part part in vessel.parts)
            {
                if (part.Modules.Contains("RealBattery"))
                {
                    var rb = part.Modules.GetModule<RealBattery>();
                    var sc = part.Resources.Get("StoredCharge");
                    if (sc == null || sc.maxAmount <= 0) continue;

                    double virtualCap = sc.maxAmount * rb.BatteryLife;
                    totalCapacity += virtualCap;
                    batteries.Add((sc, rb));
                }
            }

            if (totalCapacity <= 0 || batteries.Count == 0)
            {
                Debug.Log("[RealBattery] No eligible batteries found to apply snapshot.");
                return;
            }

            Debug.Log($"[RealBattery] ApplySnapshot for vessel '{vessel.vesselName}': Δt = {deltaTime:F1} s since last snapshot");

            foreach (var (sc, rb) in batteries)
            {
                double virtualCap = sc.maxAmount * rb.BatteryLife;
                double efficiency = rb.ChargeEfficiencyCurve.Evaluate((float)rb.SC_SOC);
                //double deltaSC = snap.netECProductionRate * deltaTime / 3600.0; // EC/s to kWh
                //double newAmount = Math.Max(0, Math.Min(sc.maxAmount * rb.BatteryLife, sc.amount + deltaSC));
                double newAmount = deltaSC_vessel * (virtualCap / totalCapacity);
                newAmount = Math.Max(0, Math.Min(virtualCap, newAmount));
                double deltaSC = newAmount - sc.amount;

                Debug.Log($"[RealBattery] Snapshot debug for '{sc.part.partInfo.title}':");
                Debug.Log($"  SC.amount = {sc.amount:F6} / max = {sc.maxAmount:F6}");
                Debug.Log($"  BatteryLife = {rb.BatteryLife:F4}");
                Debug.Log($"  virtualCap = {virtualCap:F6}");
                Debug.Log($"  snap.storedChargeAmount = {snap.storedChargeAmount:F6}");
                Debug.Log($"  totalCapacity = {totalCapacity:F6}");
                Debug.Log($"  newAmount (pre-clamp) = {snap.storedChargeAmount * (virtualCap / totalCapacity):F6}");
                Debug.Log($"  newAmount (final) = {newAmount:F6}");
                Debug.Log($"  deltaSC = {deltaSC:F6}");

                bool isEnabled = true;
                var disabledField = rb.Fields["BatteryDisabled"];
                if (disabledField != null && disabledField.GetValue(rb) is bool disabledFlag)
                    isEnabled = !disabledFlag;

                // Battery ON
                if (isEnabled)
                {
                    
                    if (snap.netECProductionRate > 0.00001 && efficiency > 0.0001)
                    {
                        // Float charge
                        if (rb.SC_SOC >= 0.99 && rb.ChargeEfficiencyCurve.Evaluate(1.0f) > 0.0001)
                        {
                            double cycleAmount = virtualCap * 0.001;
                            rb.WearCounter += cycleAmount;
                            rb.UpdateBatteryLife();
                            Debug.Log($"[RealBattery] Float-charge simulated on '{sc.part.partInfo.title}': +{cycleAmount:F5} kWh wear");
                        }

                        // Charge
                        else
                        {
                            double deltaToApply = Math.Abs(deltaSC * efficiency);
                            if (deltaToApply > 0.00001)
                            {
                                sc.amount = Math.Min(virtualCap, sc.amount + deltaToApply);
                                rb.WearCounter += deltaToApply;
                                rb.UpdateBatteryLife();
                                Debug.Log($"[RealBattery] Charged '{sc.part.partInfo.title}': +{deltaToApply:F3} kWh @ {efficiency:P0} efficiency");
                            }
                            else
                            {
                                Debug.Log($"[RealBattery] Skipped charge on '{sc.part.partInfo.title}': delta too small ({deltaToApply:F6} kWh)");
                            }
                            Debug.Log($"[RealBattery] deltaSC {deltaSC} = deltaToApply {deltaToApply} * efficiency {efficiency}");
                        }
                    }

                    // Discharge
                    else if (snap.netECProductionRate < -0.00001 && deltaSC < -0.00001)
                    {
                        sc.amount = newAmount;
                        rb.WearCounter += Math.Abs(deltaSC);
                        rb.UpdateBatteryLife();
                        Debug.Log($"[RealBattery] Discharged '{sc.part.partInfo.title}': {deltaSC:F3} kWh");
                    }

                    // Self-discharge
                    else
                    {
                        if (sc.amount > 0.00001)
                        {
                            double socLossPerDay = rb.SelfDischargeRate / (rb.BatteryLife > 0 ? rb.BatteryLife : 1.0);
                            double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);
                            double lossSOC = socLossPerSecond * deltaTime;
                            double lossAmount = lossSOC * sc.maxAmount;

                            sc.amount = Math.Max(0, sc.amount - lossAmount);
                            Debug.Log($"[RealBattery] Self-discharge on '{sc.part.partInfo.title}': -{lossAmount:F4} kWh");
                        }
                    }
                }

                // Self-discharge (unplugged battery)
                else
                {
                    if (sc.amount > 0.00001)
                    {
                        double socLossPerDay = rb.SelfDischargeRate / (rb.BatteryLife > 0 ? rb.BatteryLife : 1.0);
                        double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);
                        double lossSOC = socLossPerSecond * deltaTime;
                        double lossAmount = lossSOC * sc.maxAmount;

                        sc.amount = Math.Max(0, sc.amount - lossAmount);
                        Debug.Log($"[RealBattery] Autoself-discharge (disabled) on '{sc.part.partInfo.title}': -{lossAmount:F4} kWh");
                    }
                }

                Debug.Log(
                    $"[RealBattery] Applied StoredCharge to part '{sc.part.partInfo.title}': " +
                    $"{sc.amount:F3}/{sc.maxAmount:F3} kWh | " +
                    $"SOC={rb.SC_SOC:P1}, Enabled={isEnabled}, " +
                    $"NetEC={(snap.netECProductionRate >= 0 ? "+" : "")}{snap.netECProductionRate:F3} EC/s, " +
                    $"Eff@SOC={rb.ChargeEfficiencyCurve.Evaluate((float)rb.SC_SOC):P1}, " +
                    $"Eff@100%={rb.ChargeEfficiencyCurve.Evaluate(1.0f):P1}, " +
                    //$"EffCalc={efficiency:F3}, " +
                    $"BatteryLife={rb.BatteryLife:P0}, Wear={rb.WearCounter:F2} kWh"
                );
            }

            snap.timestamp = currentTime;
            Debug.Log($"[RealBattery] ApplySnapshot executed on vessel '{vessel.vesselName}'.");
        }


        public static VesselEnergySnapshot GetEnergySnapshot(Guid id)
        {
            return energySnapshots.TryGetValue(id, out var snap) ? snap : null;
        }

        public static void RestoreEnergySnapshot(VesselEnergySnapshot snap)
        {
            if (snap != null)
            {
                energySnapshots[snap.vesselId] = snap;
                Debug.Log($"[RealBattery][OnLoad] Restored snapshot: NetEC={snap.netECProductionRate:F3}");
            }
            else
            {
                Debug.LogWarning("[RealBattery] Attempted to restore null snapshot");
            }
        }

        public static void UpdateEnergySnapshot(Vessel vessel)
        {
            if (vessel == null || !energySnapshots.ContainsKey(vessel.id)) return;

            var snap = energySnapshots[vessel.id];
            double currentUT = Planetarium.GetUniversalTime();
            double deltaTime = currentUT - snap.timestamp;

            if (deltaTime <= 0)
            {
                Debug.Log("[RealBattery] DeltaTime <= 0, skipping UpdateEnergySnapshot.");
                return;
            }

            double deltaEnergy = 0;
            double hoursPerDay = RealBatterySettings.Instance?.GetHoursPerDay() ?? 6.0;

            if (snap.netECProductionRate > 0.00001 && snap.totalDischargeRate > 0)
            {
                // Simula mantenimento: produco ma batterie sono piene → cicli di mantenimento
                double cycleFraction = 0.001; // 0.1% della capacità per snapshot
                double simulatedWearKWh = snap.storedChargeMaxAmount * cycleFraction;
                deltaEnergy = 0; // nessuna variazione energetica effettiva, ma il log la registra

                Debug.Log($"[RealBattery] Float-charge simulation active (background): +{simulatedWearKWh:F3} kWh wear equivalent");
            }
            else if (snap.netECProductionRate > 0.00001)
            {
                double effectiveRate = Math.Min(snap.netECProductionRate, snap.totalDischargeRate);
                deltaEnergy = (effectiveRate * deltaTime) / 3600.0;
                Debug.Log($"[RealBattery] Background charging: +{deltaEnergy:F3} kWh (rate {effectiveRate:F2} EC/s)");
            }
            else if (snap.netECProductionRate < -0.00001 && snap.totalDischargeRate > 0)
            {
                double effectiveRate = Math.Min(Math.Abs(snap.netECProductionRate), snap.totalDischargeRate);
                deltaEnergy = -(effectiveRate * deltaTime) / 3600.0;
                Debug.Log($"[RealBattery] Background discharging: {deltaEnergy:F3} kWh (rate {effectiveRate:F2} EC/s)");
            }
            else
            {
                // Autoscarica passiva
                double selfDischargeSOCperDay = 0;

                foreach (var part in vessel.parts)
                {
                    if (part.Modules.Contains("RealBattery"))
                    {
                        var rb = part.Modules.GetModule<RealBattery>();
                        var sc = part.Resources.Get("StoredCharge");
                        if (sc == null || sc.amount <= 0) continue;

                        selfDischargeSOCperDay += rb.SelfDischargeRate / (rb.BatteryLife > 0 ? rb.BatteryLife : 1.0);
                    }
                }

                double lossEnergy = (snap.storedChargeMaxAmount * selfDischargeSOCperDay * deltaTime) / (hoursPerDay * 3600.0);
                deltaEnergy = -lossEnergy;
                Debug.Log($"[RealBattery] Background self-discharge: -{lossEnergy:F3} kWh");
            }

            snap.storedChargeAmount += deltaEnergy;
            snap.storedChargeAmount = Math.Max(0, Math.Min(snap.storedChargeMaxAmount, snap.storedChargeAmount));
            //snap.timestamp = currentUT;

            Debug.Log($"[RealBattery] Updated snapshot for vessel '{vessel.vesselName}' → {snap.storedChargeAmount:F3}/{snap.storedChargeMaxAmount:F3} kWh");
        }

    }

    // Per-part snapshot (non modificato)
    public class VesselSnapshot
    {
        public Guid vesselId;
        public double timestamp;
        public List<PartSnapshot> partSnapshots;
    }

    public class PartSnapshot
    {
        public string partName;
        public double VesselECproducedPerSecond;
        public double VesselECconsumedPerSecond;
    }

    public class VesselEnergySnapshot
    {
        public Guid vesselId;
        public double timestamp;
        public double storedChargeAmount;
        public double storedChargeMaxAmount;
        public double totalDischargeRate;
        //public double totalChargeRate;
        public double netECProductionRate;
    }
}
