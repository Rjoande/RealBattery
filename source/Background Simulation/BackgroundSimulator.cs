using CommNet.Network;
using KSP;
using KSP.UI.Screens;
using RealBattery;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VehiclePhysics;
using static PropTools;
using static RealBattery.RealBatterySettings;
using static Targeting;
using static VehiclePhysics.EnergyProvider;

namespace RealBattery
{
    public static class BackgroundSimulator
    {
        // ---------------------------------------------------------------------
        // Helpers to read SystemHeat loop temperature (loaded or proto)
        // ---------------------------------------------------------------------
        private static bool TryGetSystemHeatTemp(Part part, out float tempK)
        {
            tempK = 0f;
            // Loaded module path
            var sh = part?.Modules?.GetModule("ModuleSystemHeat");
            if (sh != null)
            {
                var f = sh.Fields["currentLoopTemperature"] ?? sh.Fields["loopTemperature"];
                if (f != null)
                {
                    var v = f.GetValue(sh);
                    if (v != null && float.TryParse(v.ToString(), out tempK)) return true;
                }
            }
            // Proto snapshot path
            var pps = part?.protoPartSnapshot;
            var snap = pps?.modules?.FirstOrDefault(m => m.moduleName == "ModuleSystemHeat");
            var node = snap?.moduleValues;
            if (node != null)
            {
                string s = node.GetValue("currentLoopTemperature") ?? node.GetValue("loopTemperature");
                if (!string.IsNullOrEmpty(s) && float.TryParse(s, out tempK)) return true;
            }
            return false;
        }

        // Temperature -> upkeep multiplier (same curve as flight):
        // >=600 K -> 0; <=500 K -> 1; linear in between. When heat sim disabled, caller should skip.
        private static float KeepWarmTempMulFrom(float tK)
        {
            if (tK >= 600f) return 0f;
            if (tK <= 500f) return 1f;
            return 1f - Mathf.Clamp01((tK - 500f) / 100f);
        }


        private static HashSet<Guid> vesselsNeedingRecalculation = new HashSet<Guid>();
        private static Dictionary<Guid, VesselSnapshot> vesselSnapshots = new Dictionary<Guid, VesselSnapshot>();
        private static Dictionary<Guid, VesselEnergySnapshot> energySnapshots = new Dictionary<Guid, VesselEnergySnapshot>();

        // Keep illumination data per vessel
                
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
            double realDischargeEC = 0;
                        
            double solarECproduced = 0;
            double expUT = 0; // 0 means "no depletion expected / not appliable"
                        
            VesselSnapshot snapshot = new VesselSnapshot
            {
                vesselId = id,
                timestamp = currentUT,
                partSnapshots = new List<PartSnapshot>()
            };

            int maxSpecialistLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(vessel, "Engineer");
            
            foreach (Part part in vessel.parts)
            {
                double partECprod = 0;
                double partECcons = 0;

                // NEW signature (see next section)
                ModuleEnergyEstimator.EstimateECUsage(part, ref partECprod, ref partECcons, maxSpecialistLevel);

                totalECproduced += partECprod;
                totalECconsumed += partECcons;

                if (partECprod > 0 || partECcons > 0)
                {
                    snapshot.partSnapshots.Add(new PartSnapshot
                    {
                        partName = part.partInfo.name,
                        VesselECproducedPerSecond = partECprod,
                        VesselECconsumedPerSecond = partECcons
                        
                    });
                    Debug.Log($"[RealBattery] PartSnapshot created.");
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

                    if (pm is RealBattery rb && isEnabled)
                    {
                        realDischargeEC += rb.lastECpower; // negative if discharging
                    }

                    PartResource sc = part.Resources.Get("StoredCharge");
                    if (sc != null)
                    {
                        totalSCamount += sc.amount;
                        totalSCmaxAmount += sc.maxAmount;
                    }

                    double dischargeRate = 0;

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

                    // Check KeepWarm flag
                    bool keepWarm = false;
                    var keepWarmField = pm.Fields["KeepWarm"];
                    if (keepWarmField != null)
                    {
                        var raw = keepWarmField.GetValue(pm);
                        if (raw != null && bool.TryParse(raw.ToString(), out bool kw))
                            keepWarm = kw;
                    }

                    if (keepWarm)
                    {
                        // Read dischargeRate (kW ≈ EC/s base)
                        if (dischargeField != null)
                        {
                            var rv = dischargeField.GetValue(pm);
                            if (rv != null && double.TryParse(rv.ToString(), out double rate))
                                dischargeRate = rate;
                        }

                        if (dischargeRate > 1e-6)
                        {
                            // Base upkeep (warmup and operational share same baseline in BG)
                            double upkeep = dischargeRate * RealBatterySettings.KeepWarmFrac; // EC/s nominal
                            
                            if (RealBatterySettings.EnableHeatSimulation)
                            {
                                // Try precise SystemHeat loop temperature; fallback to coarse 0.5×
                                if (TryGetSystemHeatTemp(part, out float tK))
                                {
                                    float mul = KeepWarmTempMulFrom(tK); // 0..1
                                    upkeep *= mul;
                                }
                                else
                                {
                                    // Fallback when loop temp is unavailable in background
                                    upkeep *= 0.5;
                                }
                            }
                            totalECconsumed += upkeep;
                            Debug.Log($"[RealBattery][BG] KeepWarm upkeep +{upkeep:F3} EC/s on '{part.partInfo?.title}'");
                        }
                    }
                }
            }

            solarECproduced = ModuleEnergyEstimator.SolarPanelsBaseOutput(vessel);

            // NEW: split nets
            double netEC_Gross = totalECproduced + solarECproduced - totalECconsumed; // already corrected if needed, includes solar
            double netEC_True = totalECproduced - totalECconsumed; // excludes solar, uncorrected

            // --- Correct net EC production using actual lastECpower from RealBatteries (only if discharging) ---
            if (netEC_Gross < 0 && realDischargeEC < 0 && Math.Abs(realDischargeEC) < Math.Abs(netEC_Gross))
            {
                double ratio = Math.Abs(realDischargeEC / netEC_Gross);
                double correctedEC = netEC_Gross * ratio;

                Debug.Log($"[RealBattery] Correcting netEC_Gross for vessel '{vessel.vesselName}' — before: {netEC_Gross:F3} EC/s, after: {correctedEC:F3} EC/s (ratio={ratio:F3})");

                netEC_Gross = correctedEC;
            }

            if (netEC_True < 0 && realDischargeEC < 0 && Math.Abs(realDischargeEC) < Math.Abs(netEC_True))
            {
                double ratio = Math.Abs(realDischargeEC / netEC_True);
                double correctedEC = netEC_True * ratio;

                Debug.Log($"[RealBattery] Correcting netEC_True for vessel '{vessel.vesselName}' — before: {netEC_True:F3} EC/s, after: {correctedEC:F3} EC/s (ratio={ratio:F3})");

                netEC_True = correctedEC;
            }

            IllumPhase IllumStartPhase = 0;
            double IllumtToTransition = 0;
            double IllumOrbitalShadowFrac = 0;

            IlluminationStatus(vessel, ref IllumStartPhase, ref IllumtToTransition, ref IllumOrbitalShadowFrac);

            double period = vessel.LandedOrSplashed
                    ? Math.Max(vessel.mainBody.rotationPeriod, 1.0)
                    : Math.Max(vessel.orbit.period, 1.0);
            int mainBody = vessel.mainBody.flightGlobalsIndex;
            string mainBodyName = vessel.mainBody.name;
            IllumPhase startPhase = IllumStartPhase;
            double tToTransition = IllumtToTransition;
            double orbitalShadowFrac = IllumOrbitalShadowFrac;
            bool isEscape = vessel.orbit.ApA > vessel.mainBody.sphereOfInfluence;

            vesselSnapshots[id] = snapshot;

            var energySnapshot = new VesselEnergySnapshot
            {
                vesselId = id,
                timestamp = currentUT,
                storedChargeAmount = totalSCamount,
                storedChargeMaxAmount = totalSCmaxAmount,
                totalDischargeRate = totalDischargeRate,
                solarECproduced = solarECproduced,
                netEC_Gross = netEC_Gross,
                netEC_True = netEC_True,
                ExpUT = 0,
                period = period,
                mainBody = mainBody,
                mainBodyName = mainBodyName,
                startPhase = startPhase,
                tToTransition = tToTransition,
                orbitalShadowFrac = orbitalShadowFrac,
                isEscape = isEscape,
            };

            // Compute expected depletion UT (including user-configured lead time).
            // Only when "true" net is negative (i.e., vessel is draining batteries) and there is energy stored.
            if (netEC_Gross < -1e-6 && totalSCamount > 1e-9)
            {
                // seconds until empty using netEC_Gross magnitude, then apply the lead-time in seconds
                double secondsToEmpty = (totalSCamount * 3600.0) / Math.Abs(netEC_Gross);
                double lead = RealBatterySettings.LowPowerLeadSeconds;
                expUT = Planetarium.GetUniversalTime() + Math.Max(0.0, secondsToEmpty - lead);
                energySnapshot.ExpUT = expUT;
            }
            else
            {
                energySnapshot.ExpUT = 0; // not discharging or invalid -> no alarm
            }

            energySnapshots[id] = energySnapshot;

            Debug.Log($"[RealBattery] CaptureSnapshot '{vessel.vesselName}': netEC_Gross={energySnapshot.netEC_Gross:F3}, netEC_True={energySnapshot.netEC_True:F3}, solar~{solarECproduced:F3} EC/s");
        }

        public static void ApplySnapshot(Vessel vessel)
        {
            int maxSpecialistLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(vessel, "Engineer");
            double EngBonus = 0.95 + 0.06 * maxSpecialistLevel;

            if (vessel == null || !energySnapshots.ContainsKey(vessel.id)) return;

            var snap = energySnapshots[vessel.id];

            double currentTime = Planetarium.GetUniversalTime();
            double deltaTime = currentTime - snap.timestamp;

            if (deltaTime < 60.0)
            {
                Debug.Log($"[RealBattery] ApplySnapshot skipped (Δt too small)");
                return;
            }

            // --- Tunables / epsilons ---
            const double EPS = 1e-6;   // generic numeric epsilon
            const double CAP_EPS = 1e-9;   // min virtual capacity to consider in distribution

            double hoursPerDay = RealBatterySettings.GetHoursPerDay();

            // Base delta from TRUE net (no solar) — convert EC/s to kWh
            double deltaSC_true = (snap.netEC_True * deltaTime) / 3600.0;

            double deltaSC_solar = 0.0;

                deltaSC_solar = SimulateSolar(vessel, deltaTime, hoursPerDay);

            Debug.Log($"[RealBattery][ApplySnapshot] Solar contribution computed: {deltaSC_solar:F3} kWh");

            // Net vessel delta (kWh) to distribute additively among (eligible) batteries
            double deltaSC_vessel = deltaSC_true + deltaSC_solar;

            Debug.Log($"[RealBattery] ApplySnapshot debug: netEC_True={snap.netEC_True:F3}, netEC_solarEst={deltaSC_solar * 3600.0 / deltaTime:F3}, netEC_ProdRate={snap.netEC_Gross:F3}, deltaSC_vessel={deltaSC_vessel:F3} kWh");

            if (Math.Abs(deltaSC_vessel) < EPS && Math.Abs(snap.netEC_Gross) > EPS)
            {
                Debug.LogWarning(
                    $"[RealBattery] ApplySnapshot: Using fallback delta from netEC_Gross " +
                    $"(true/solar estimate unavailable) → netEC_Gross={snap.netEC_Gross:F3} EC/s"
                );

                deltaSC_vessel = (snap.netEC_Gross * deltaTime) / 3600.0;
            }

            // Build lists and compute total distributable virtual capacity (enabled batteries only)
            var allBatteries = new List<(PartResource sc, RealBattery rb, bool isEnabled, double virtualCap)>();
            var distribBatteries = new List<(PartResource sc, RealBattery rb, double virtualCap)>();
            double totalCapacity = 0.0;

            foreach (Part part in vessel.parts)
            {
                if (!part.Modules.Contains("RealBattery")) continue;

                var rb = part.Modules.GetModule<RealBattery>();
                var sc = part.Resources.Get("StoredCharge");
                if (sc == null || sc.maxAmount <= 0) continue;

                // Check enabled flag
                bool isEnabled = true;
                var disabledField = rb.Fields["BatteryDisabled"];
                if (disabledField != null && disabledField.GetValue(rb) is bool disabledFlag)
                    isEnabled = !disabledFlag;

                double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                double virtualCap = sc.maxAmount * ActualLife;

                allBatteries.Add((sc, rb, isEnabled, virtualCap));

                // Only enabled and with meaningful virtual capacity participate in distribution
                if (isEnabled && virtualCap > CAP_EPS)
                {
                    totalCapacity += virtualCap;
                    distribBatteries.Add((sc, rb, virtualCap));
                }
            }

            if (totalCapacity <= CAP_EPS || distribBatteries.Count == 0)
            {
                Debug.Log("[RealBattery] No eligible batteries found to apply snapshot.");
                return;
            }

            Debug.Log($"[RealBattery] ApplySnapshot for vessel '{vessel.vesselName}': Δt = {deltaTime:F1} s since last snapshot");

            // Decide vessel intent based on the delta actually being distributed
            bool vesselWantsCharge = (deltaSC_vessel > EPS);
            bool vesselWantsDischarge = (deltaSC_vessel < -EPS);

            // --- Precompute cycle-wear context (vessel-level) ---
            bool initEscape = snap.isEscape;
            bool finalEscape = vessel.orbit.ApA > vessel.mainBody.sphereOfInfluence;
            bool boundOrSurface = vessel != null && (vessel.LandedOrSplashed || (!initEscape && !finalEscape));
            double cycleWear = Math.Max(0.0, Math.Abs(snap.netEC_True));
            bool appliedCycleWearThisTick = false;

            // Pass 1: charge/discharge
            foreach (var (sc, rb, virtualCap) in distribBatteries)
            {
                double efficiency = rb.ChargeEfficiencyCurve.Evaluate((float)rb.SC_SOC);
                double share = virtualCap / totalCapacity;
                double deltaPart = deltaSC_vessel * share;

                // Charging
                if (deltaPart > EPS && vesselWantsCharge && efficiency > EPS)
                {
                    double effDelta = deltaPart * efficiency;
                    double target = Math.Min(virtualCap, sc.amount + effDelta);
                    double applied = target - sc.amount;

                    if (applied > EPS)
                    {
                        sc.amount = target;
                        rb.WearCounter += Math.Abs(applied) / EngBonus;
                        rb.UpdateBatteryLife();
                        Debug.Log($"[RealBattery] Charged '{sc.part.partInfo.title}': +{applied:F3} kWh @ {efficiency:P0} eff");
                    }

                    // === Wear if a charge/discharge cycle has been simulated (non-perma-sunlight) ===
                    if (snap.netEC_True < 0 && boundOrSurface && cycleWear > EPS)
                    {
                        rb.WearCounter += cycleWear / EngBonus;
                        rb.UpdateBatteryLife();
                        appliedCycleWearThisTick = true; // prevent self-discharge this tick
                        Debug.Log($"[RealBattery] Cycle wear on '{sc.part.partInfo.title}': +{cycleWear:F5} kWh (surface/bound orbit)");
                    }

                    // === Float-charge if battery already at or near full ===
                    else if (sc.amount >= virtualCap - EPS)
                    {
                        // Wear calculation based on the remaining simulation time
                        // Example: if it charges in half deltaTime, the other half is float-charge
                        double timeFractionFull = Math.Max(0.0, (deltaTime - (applied > EPS ? (applied / (effDelta / deltaTime)) : 0.0)) / deltaTime);
                        if (timeFractionFull > 0)
                        {
                            double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                            double lossPerSecond = (rb.SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0)) / (hoursPerDay * 3600.0) * virtualCap;
                            double cycleAmount = lossPerSecond * (deltaTime * timeFractionFull);
                            rb.WearCounter += cycleAmount / EngBonus;
                            rb.UpdateBatteryLife();
                            Debug.Log($"[RealBattery] Float-charge simulated on '{sc.part.partInfo.title}': +{cycleAmount:F5} kWh wear over {timeFractionFull:P0} of background time");
                        }
                    }
                }

                // Discharging
                else if (deltaPart < -EPS && vesselWantsDischarge)
                {
                    double target = Math.Max(0.0, sc.amount + deltaPart);
                    double applied = target - sc.amount;

                    if (applied < -EPS)
                    {
                        sc.amount = target;
                        rb.WearCounter += Math.Abs(applied) / EngBonus;
                        rb.UpdateBatteryLife();
                        Debug.Log($"[RealBattery] Discharged '{sc.part.partInfo.title}': {applied:F3} kWh");
                    }
                }
            }

            // --- Pass 2: self-discharge ---
            foreach (var (sc, rb, isEnabled, virtualCap) in allBatteries)
            {
                if (sc.amount <= EPS) continue;

                // Non-rechargeable "primary" heuristic (better than the current virtualCap<=EPS)
                bool isPrimary = rb.CycleDurability <= 1 || rb.ChargeEfficiencyCurve.Evaluate(0f) <= EPS;

                bool idle = !vesselWantsCharge && !vesselWantsDischarge;
                bool shouldSelfDischarge =
                    (!isEnabled) ||                                 // disabled -> always self-discharge
                    (!appliedCycleWearThisTick && (                 // only if no cycle wear this tick
                        (isPrimary && !vesselWantsDischarge) ||     // primary: unless actively discharging
                        (!isPrimary && idle)                        // rechargeable: only when truly idle
                    ));

                if (!shouldSelfDischarge) continue;

                double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                double socLossPerDay = rb.SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0);
                double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);
                double lossAmount = socLossPerSecond * deltaTime * sc.maxAmount;

                double newAmount = Math.Max(0.0, sc.amount - lossAmount);
                double applied = sc.amount - newAmount;
                if (applied > EPS)
                {
                    sc.amount = newAmount;
                    string kind = isEnabled ? "Self-discharge" : "Autoself-discharge (disabled)";
                    Debug.Log($"[RealBattery] {kind} on '{sc.part.partInfo.title}': -{applied:F4} kWh");
                }
            }

            // Final logs
            foreach (var (sc, rb, isEnabled, _) in allBatteries)
            {
                double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                Debug.Log(
                    $"[RealBattery] Applied StoredCharge to part '{sc.part.partInfo.title}': " +
                    $"{sc.amount:F3}/{sc.maxAmount:F3} kWh | " +
                    $"SOC={rb.SC_SOC:P1}, Enabled={isEnabled}, " +
                    $"NetΔ={(deltaSC_vessel >= 0 ? "+" : "")}{deltaSC_vessel:F3} kWh, " +
                    $"Eff@SOC={rb.ChargeEfficiencyCurve.Evaluate((float)rb.SC_SOC):P1}, " +
                    $"Eff@100%={rb.ChargeEfficiencyCurve.Evaluate(1.0f):P1}, " +
                    $"BatteryLife={ActualLife:P0}, Wear={rb.WearCounter:F2} kWh with EngBonus={EngBonus:F2}"
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
                Debug.Log($"[RealBattery][OnLoad] Restored snapshot: NetEC={snap.netEC_Gross:F3}");
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
            double hoursPerDay = RealBatterySettings.GetHoursPerDay();

            if (snap.netEC_Gross > 0.00001 && snap.totalDischargeRate > 0)
            {
                // Simulate maintenance (floatcharge): if producing but the batteries are full -> maintenance cycles
                double cycleFraction = 0.001; // 0.1% of capacity per snapshot
                double simulatedWearKWh = snap.storedChargeMaxAmount * cycleFraction;
                deltaEnergy = 0; // no actual energy change, but the log records it

                Debug.Log($"[RealBattery] Float-charge simulation active (background): +{simulatedWearKWh:F3} kWh wear equivalent");
            }
            else if (snap.netEC_Gross > 0.00001)
            {
                double effectiveRate = Math.Min(snap.netEC_Gross, snap.totalDischargeRate);
                deltaEnergy = (effectiveRate * deltaTime) / 3600.0;
                Debug.Log($"[RealBattery] Background charging: +{deltaEnergy:F3} kWh (rate {effectiveRate:F2} EC/s)");
            }
            else if (snap.netEC_Gross < -0.00001 && snap.totalDischargeRate > 0)
            {
                double effectiveRate = Math.Min(Math.Abs(snap.netEC_Gross), snap.totalDischargeRate);
                deltaEnergy = -(effectiveRate * deltaTime) / 3600.0;
                Debug.Log($"[RealBattery] Background discharging: {deltaEnergy:F3} kWh (rate {effectiveRate:F2} EC/s)");
            }
            else
            {
                // Passive self-discharge
                double selfDischargeSOCperDay = 0;

                foreach (var part in vessel.parts)
                {
                    if (part.Modules.Contains("RealBattery"))
                    {
                        var rb = part.Modules.GetModule<RealBattery>();
                        var sc = part.Resources.Get("StoredCharge");
                        if (sc == null || sc.amount <= 0) continue;

                        double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                        selfDischargeSOCperDay += rb.SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0);
                    }
                }

                double lossEnergy = (snap.storedChargeMaxAmount * selfDischargeSOCperDay * deltaTime) / (hoursPerDay * 3600.0);
                deltaEnergy = -lossEnergy;
                Debug.Log($"[RealBattery] Background self-discharge: -{lossEnergy:F3} kWh");
            }

            snap.storedChargeAmount += deltaEnergy;
            snap.storedChargeAmount = Math.Max(0, Math.Min(snap.storedChargeMaxAmount, snap.storedChargeAmount));

            Debug.Log($"[RealBattery] Updated snapshot for vessel '{vessel.vesselName}' → {snap.storedChargeAmount:F3}/{snap.storedChargeMaxAmount:F3} kWh");
        }

        public static double SimulateSolar(Vessel vessel, double deltaTime, double hoursPerDay)
        {
            double total_kWh = 0.0;

            var snap = energySnapshots[vessel.id];

            int sun = Planetarium.fetch.Sun.flightGlobalsIndex;
            int initBody = snap.mainBody;
            int finalBody = vessel.mainBody.flightGlobalsIndex;

            bool initEscape = snap.isEscape;
            bool finalEscape = vessel.orbit.ApA > vessel.mainBody.sphereOfInfluence;

            Debug.Log($"[RealBattery][SolarSim] InitBody={snap.mainBodyName} (escape={initEscape}), FinalBody={vessel.mainBody.name} (escape={finalEscape}), Δt={deltaTime:F1}s");

            // Scenario selection
            if (vessel.LandedOrSplashed || (!initEscape && !finalEscape))
            {
                // Surface mode: always simulate using initial orbit snapshot
                Debug.Log($"[RealBattery][SolarSim] Vessel '{vessel.vesselName}' stayed on {vessel.mainBody.name}, simulating night/day cycle...");
                total_kWh = SimulateSolar_Planet(vessel, deltaTime);
            }
            else if (initBody == sun || finalBody == sun || initEscape || finalEscape)
            {
                Debug.Log($"[RealBattery][SolarSim] Vessel '{vessel.vesselName}' has been in heliocentric orbit and/or changed SOI, ignoring shadow phases...");
                total_kWh = SimulateSolar_Sun(vessel, deltaTime);
            }
            else
            {
                Debug.LogWarning($"[RealBattery][SolarSim] No matching scenario found, defaulting to rough solar output...");
                total_kWh = snap.solarECproduced;
            }

            return total_kWh;
        }

        private static double SimulateSolar_Sun(Vessel vessel, double deltaTime)
        {
            var snap = energySnapshots[vessel.id];
            
            double solarECnow = ModuleEnergyEstimator.SolarPanelsBaseOutput(vessel);

            double avgECps = (snap.solarECproduced + solarECnow) / 2.0;
            double total_kWh = (avgECps * deltaTime) / 3600.0;

            Debug.Log($"[RealBattery][SolarSim] AvgECps={avgECps:F3} EC/s → Total={total_kWh:F3} kWh");

            return total_kWh;
        }

        // Simulate starting from snapshot data, moving forward in time
        private static double SimulateSolar_Planet(Vessel vessel, double deltaTime)
        {
            var snap = energySnapshots[vessel.id];

            // If the snapshot was taken with orbital naming, remap to surface naming.
            if (vessel.LandedOrSplashed && (snap.startPhase == IllumPhase.Sunlit || snap.startPhase == IllumPhase.Shadow))
            {
                // Remap labels and (optionally) refresh tToTransition & period for surface consistency
                snap.startPhase = (SolarElevationRad(vessel, Planetarium.fetch.Sun) > 0.0) ? IllumPhase.Day : IllumPhase.Night;

                // Optional but recommended for full coherence if state changed since capture:
                snap.tToTransition = TimeToSurfaceTransition(vessel, Planetarium.fetch.Sun, Math.Max(vessel.mainBody.rotationPeriod, 1.0));
                snap.period = Math.Max(vessel.mainBody.rotationPeriod, 1.0);
            }

            // Surface or orbit branch for period and lit fraction
            bool isSurface = vessel.LandedOrSplashed;

            double P = Math.Max(snap.period, 1.0);
            double t = deltaTime;

            double total_kWh = 0;

            // --- Polar surface simplification (constant but reduced production) ---
            // If latitude is beyond a threshold (polar circle), assume a constant reduced output
            // instead of alternating Day/Night. This avoids edge-cases with very long dawn/dusk,
            // and KSP has no axial tilt anyway.
            if (isSurface)
            {
                double POLAR_LAT_THRESHOLD_DEG = RealBatterySettings.PolarLatitudeThresholdDeg;
                double POLAR_CONST_LIT_FRAC = RealBatterySettings.PolarConstantLitFrac;
                double latAbs = Math.Abs(vessel.latitude);
                
                if (latAbs >= POLAR_LAT_THRESHOLD_DEG)
                {
                    double blendedFrac = BlendPolarLitFrac(latAbs, POLAR_LAT_THRESHOLD_DEG, POLAR_CONST_LIT_FRAC);
                    // Average EC/s at this location treated as constant over Δt
                    double avgECps = snap.solarECproduced * blendedFrac;
                    total_kWh = (avgECps * t) / 3600.0;

                    Debug.Log($"[RealBattery][SolarSim] Surface-Polar: lat={latAbs:F2}° ≥ {POLAR_LAT_THRESHOLD_DEG}°, " +
                    $"blendedFrac={blendedFrac:P1} → AvgECps={avgECps:F3} EC/s, Δt={t:F1}s, Total={total_kWh:F3} kWh");
                    return total_kWh;
                }
            }

            long N = (long)Math.Floor(t / P);   // full cycles
            double r = t - N * P;               // remainder

            // On surface, approximate day/night split as 50/50 (good default)
            double litFracCycle = isSurface
                ? 0.5
                : Clamp(1.0 - snap.orbitalShadowFrac, 0.0, 1.0);

            double Ecycle_kWh = (snap.solarECproduced * litFracCycle * P) / 3600.0;

            Debug.Log($"[RealBattery][SolarSim] Orbit/Surface: period={P:F1}s | litFracCycle={litFracCycle:P1} | Ecycle={Ecycle_kWh:F3} kWh | fullCycles={N} | remainder={r:F1}s");

            double rem_kWh = 0.0;
            double rem = r;
            IllumPhase phase = snap.startPhase;

            double toEdge = Math.Max(snap.tToTransition, 0.0);

            while (rem > 1e-6)
            {
                double seg = (toEdge > 1e-6) ? Math.Min(rem, toEdge) : rem;
                // Use Day/Night for surface, Sunlit/Shadow for orbit
                bool isLit = isSurface ? (phase == IllumPhase.Day) : (phase == IllumPhase.Sunlit);

                double segEnergy = isLit ? (snap.solarECproduced * seg) / 3600.0 : 0.0;
                rem_kWh += segEnergy;

                Debug.Log($"[RealBattery][SolarSim] seg={seg:F1}s | Lit={isLit} | EnergyThisSeg={segEnergy:F4} kWh");

                rem -= seg;
                // Flip phase appropriately based on context
                if (isSurface)
                    phase = (phase == IllumPhase.Day ? IllumPhase.Night : IllumPhase.Day);
                else
                    phase = (phase == IllumPhase.Sunlit ? IllumPhase.Shadow : IllumPhase.Sunlit);

                double litDur = litFracCycle * P;
                double darkDur = (1.0 - litFracCycle) * P;
                toEdge = (isSurface
                    ? (phase == IllumPhase.Day ? litDur : darkDur)
                    : (phase == IllumPhase.Sunlit ? litDur : darkDur));
            }

            total_kWh = N * Ecycle_kWh + rem_kWh;
            Debug.Log($"[RealBattery][SolarSim] Orbit/Surface total={total_kWh:F3} kWh");
            return total_kWh;
        }

        private static double BlendPolarLitFrac(double latAbsDeg, double blendStartDeg, double polarConstFrac)
        {
            // Base (non-polar) fraction with no axial tilt
            const double BASE_FRAC = 0.5;

            // Guard against degenerate parameters
            if (blendStartDeg >= 90.0) return BASE_FRAC;
            polarConstFrac = Clamp(polarConstFrac, 0.0, 1.0);

            double lat = Clamp(latAbsDeg, blendStartDeg, 90.0);
            double k = (lat - blendStartDeg) / (90.0 - blendStartDeg); // 0 at start, 1 at pole

            double frac = BASE_FRAC * (1.0 - k) + polarConstFrac * k;
            return Clamp(frac, 0.0, 1.0);
        }

        // --- Helper: precompute illumination & panels --------------------------------
        private static void IlluminationStatus(Vessel vessel, ref IllumPhase startPhase, ref double tToTransition, ref double orbitalShadowFrac)
        {
            var sun = Planetarium.fetch.Sun;
            var body = vessel.mainBody;
            
            if (vessel.LandedOrSplashed)
            {
                // --- SURFACE BRANCH ---
                double Pday = Math.Max(body.rotationPeriod, 1.0);
                
                // Day/night from solar elevation
                double sunEl = SolarElevationRad(vessel, sun);
                bool isDay = sunEl > 0.0;
                startPhase = isDay ? IllumPhase.Day : IllumPhase.Night;

                // Very light model for daylight fraction: cos(latitude) (good enough on airless bodies)
                double latRad = vessel.latitude * Math.PI / 180.0;
                
                // Time to sunrise/sunset using dH/dt ~ 2π/Pday * cos(lat)
                tToTransition = TimeToSurfaceTransition(vessel, sun, Pday);
            }
            else
            {
                // --- ORBITAL BRANCH ---
                double R = body.Radius;
                double a = vessel.orbit.semiMajorAxis;
                double P = Math.Max(vessel.orbit.period, 1.0);
                
                // Geometric umbra fraction (clamped)
                double s = Clamp(R / Math.Max(a, R + 1.0), 0.0, 1.0);
                double theta = Math.Asin(s);                // half-angle of eclipse
                double fracShadow = theta / Math.PI;
                orbitalShadowFrac = Clamp(fracShadow, 0.0, 1.0);

                // Current phase: analytic eclipse test (line of sight to Sun behind body)
                startPhase = IsInEclipse(vessel, body, sun) ? IllumPhase.Shadow : IllumPhase.Sunlit;

                // Time to next boundary (approx. circular): distance in angle / mean motion
                tToTransition = TimeToOrbitalTransition(vessel, body, sun);
            }
        }

        // Helper for double precision clamp (since Math.Clamp is not available in .NET Framework 4.8)
        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }


        // Returns true if vessel is behind the body w.r.t the Sun (umbra test)
        private static bool IsInEclipse(Vessel v, CelestialBody body, CelestialBody sun)
        {
            Vector3d r = v.GetWorldPos3D() - body.position;     // vessel from body center
            Vector3d s = sun.position - body.position;          // sun from body center

            double rMag = r.magnitude;
            double sMag = s.magnitude;
            if (rMag < 1.0 || sMag < 1.0) return false;

            // angle between r and s
            double cosang = Vector3d.Dot(r, s) / (rMag * sMag);
            cosang = Clamp(cosang, -1.0, 1.0);
            double ang = Math.Acos(cosang);

            // eclipse if ang < asin(R / |r|)
            double limit = Math.Asin(Clamp(body.Radius / rMag, 0.0, 1.0));
            return ang < limit;
        }

        // Time to next Sunlit/Shadow boundary assuming near-circular motion
        private static double TimeToOrbitalTransition(Vessel v, CelestialBody body, CelestialBody sun)
        {
            // --- 1) Frame construction in orbital plane ---
            Vector3d r_world = v.GetWorldPos3D() - body.position;          // vessel position in body frame
            Vector3d h = v.orbit.GetOrbitNormal().normalized;               // orbit plane normal
            Vector3d antiSun = (body.position - sun.position).normalized;   // anti-sun direction from body
            Vector3d aProj = antiSun - Vector3d.Dot(antiSun, h) * h;        // project anti-sun onto orbit plane
            if (aProj.sqrMagnitude< 1e-12)
            {
                // High beta-angle: no eclipse expected; return a very large time.
                return double.PositiveInfinity;
            }
            Vector3d e1 = aProj.normalized;                 // 0° = anti-sun projected
            Vector3d e2 = Vector3d.Cross(h, e1);            // 90° = prograde direction in-plane

            // --- 2) Current in-plane angle from anti-sun axis ---
            double x = Vector3d.Dot(r_world, e1);
            double y = Vector3d.Dot(r_world, e2);
            double theta_now = Math.Atan2(y, x);            // [-π, π]

            // --- 3) Shadow half-angle φ (use current radius) ---
            double rmag = Math.Max(r_world.magnitude, body.Radius + 1.0);
            double phi = Math.Asin(Clamp(body.Radius / rmag, 0.0, 1.0));    // [0..π/2]

            // --- 4) Angular advance to the next terminator (forward) ---
            // Terminators are at +φ and -φ in this frame.
            double d1 = WrapTo2Pi(phi - theta_now);         // advance to +φ
            double d2 = WrapTo2Pi(-phi - theta_now);        // advance to -φ
            double dTheta = Math.Min(d1, d2);               // smallest positive advance

            // --- 5) Convert Δθ to forward time via true anomaly if possible ---
            // For circular or near-circular orbits, Δθ ≈ Δ(true anomaly).
            double TA_now = v.orbit.trueAnomaly;            // radians
            double TA_tgt = WrapTo2Pi(TA_now + dTheta);
            double ut = Planetarium.GetUniversalTime();

            // Try high-fidelity conversion first (KSP Orbit API). If not available, fallback to mean motion.
            double dt;
            try
            {
                // Some KSP versions expose GetDTforTrueAnomaly(ta, UT) or timeToTrueAnomaly(ta).
                // Prefer GetDTforTrueAnomaly if present because it handles eccentric orbits correctly.
                dt = v.orbit.GetDTforTrueAnomaly(TA_tgt, ut);
                if (double.IsNaN(dt) || double.IsInfinity(dt) || dt< 0)
                    throw new Exception("GetDTforTrueAnomaly returned invalid dt");
            }
            catch
            {
                // Fallback: assume circular motion at mean motion n = 2π/P.
                double P = Math.Max(v.orbit.period, 1.0);
                double n = 2.0 * Math.PI / P;               // rad/s
                dt = dTheta / Math.Max(n, 1e-6);
            }

            // Safety clamp (avoid zero-length segments due to numeric noise)
            return Math.Max(dt, 1e-3);
        }

        // Wrap to [0, 2π)
        private static double WrapTo2Pi(double x)
        {
            double t = x % (2.0 * Math.PI);
            if (t < 0) t += 2.0 * Math.PI;
            return t;
        }

        // Solar elevation above local horizon (radians) — robust against floating point noise
        private static double SolarElevationRad(Vessel v, CelestialBody sun)
        {
            Vector3d up = (v.GetWorldPos3D() - v.mainBody.position).normalized;
            Vector3d sunDir = (sun.position - v.GetWorldPos3D()).normalized;

            // Cosine of zenith angle
            double cosz = Vector3d.Dot(up, sunDir);
            double zenith = Math.Acos(Clamp(cosz, -1.0, 1.0));
            double elev = (Math.PI / 2.0) - zenith; // Elevation in radians

            // Snap very small values to 0 to avoid spurious transitions near the horizon
            const double epsElev = 0.5 * Math.PI / 180.0; // 0.5° in radians
            if (Math.Abs(elev) < epsElev)
                elev = 0.0;

            return elev;
        }

        // Time to next sunrise/sunset using body rotation rate — validated against phase duration
        private static double TimeToSurfaceTransition(Vessel v, CelestialBody sun, double Pday)
        {
            double latRad = v.latitude * Math.PI / 180.0;

            // Angular speed of the sun's apparent motion (rad/s) — avoid zero at poles
            double dHdt = (2.0 * Math.PI / Math.Max(Pday, 1.0)) * Math.Max(Math.Cos(latRad), 1e-3);

            // Current solar elevation
            double elev = SolarElevationRad(v, sun);

            // Handle polar day/night cases — no transition expected
            if (elev > 0 && Math.Abs(Math.Cos(latRad)) < 1e-3)
                return Pday; // Sun always above horizon (polar day)
            if (elev < 0 && Math.Abs(Math.Cos(latRad)) < 1e-3)
                return 0.0; // Sun always below horizon (polar night)

            // Distance to horizon crossing (radians)
            double de = Math.Max(Math.Abs(elev), 1e-6);

            // Approximate time until elevation crosses zero (linear near horizon)
            double dt = de / dHdt;

            // Minimum bound to avoid spurious ultra-short segments
            const double minTransition = 60.0; // 1 minute
            if (dt < minTransition)
                dt = minTransition;

            return dt;
        }
    }

    // Per-part snapshot
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

        // NEW: split net EC
        public double netEC_Gross;   // includes solar (rough, from ModuleEnergyEstimator)
        public double netEC_True;    // excludes solar (used first in ApplySnapshot)
        public double solarECproduced;

        // Low-power alarm: expected depletion Universal Time (including lead time). 0 means "not applicable".
        public double ExpUT;

        public int mainBody;
        public string mainBodyName;
        public IllumPhase startPhase;
        public double tToTransition;
        public double period;
        //public double totalLitECps;
        public double orbitalShadowFrac;
        public bool isEscape;
    }

    // --- Illumination & solar precomputation data ------------------------------

    public class SolarPanelInfo
    {
        // EC/s when fully lit and at current distance (already scaled by 1/r^2 and tracking/static)
        public double litECperSec;
    }

    public enum IllumPhase { Sunlit, Shadow, Day, Night }
}
