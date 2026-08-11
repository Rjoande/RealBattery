using System;
using System.Collections.Generic;
using System.Linq;
using KSP.Localization;
using KSP.UI.Screens;
using UnityEngine;

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

        // Temperature -> upkeep multiplier (mirrors RealBattery.KeepWarmTempMultiplier).
        // mode: "warm" -> 1 when cold, 0 when hot (LO->HI = 1->0)
        //       "cryo" -> 0 when cold, 1 when hot (LO->HI = 0->1)
        // When heat sim disabled, caller should skip or pass mode="false" (returns 0).
        private static float KeepWarmTempMulFrom(float tK, string mode, float lo, float hi)
        {
            if (mode == "false") return 0f;
            float span = Mathf.Max(hi - lo, 1f);
            float t    = Mathf.Clamp01((tK - lo) / span); // 0 at lo, 1 at hi
            return mode == "cryo" ? t : 1f - t;
        }

        // Mirrors RealBattery.ReadPartVolumeL(): reads the RBbaseVolume cfg key set by MM
        // patches; 0 if absent or unparseable.
        private static double ReadPartVolumeL(Part part)
        {
            if (part?.partInfo?.partConfig != null &&
                part.partInfo.partConfig.HasValue("RBbaseVolume") &&
                double.TryParse(part.partInfo.partConfig.GetValue("RBbaseVolume"),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
                return v;
            return 0.0;
        }


        private static HashSet<Guid> vesselsNeedingRecalculation = new HashSet<Guid>();
        private static Dictionary<Guid, VesselSnapshot> vesselSnapshots = new Dictionary<Guid, VesselSnapshot>();
        private static Dictionary<Guid, VesselEnergySnapshot> energySnapshots = new Dictionary<Guid, VesselEnergySnapshot>();

        // Keep illumination data per vessel
                
        public static bool HasSnapshot(Guid vesselId)
        {
            return energySnapshots.ContainsKey(vesselId);
        }

        // Read-only accessor for RealBatteryPowerLedger (netEC_Gross/netEC_True, ContractVersion 2).
        // Returns the most recently captured snapshot as-is — captured on scene switch, vessel
        // switch, game save, and once on flight-scene load (RealBatterySnapshotManager), not on
        // every physics tick. May be a few seconds to minutes old for a vessel that has stayed
        // loaded and active without switching away; still the same figure RB's own alarm system
        // (ExpUT) already trusts, so it's not a new source of uncertainty.
        internal static bool TryGetEnergySnapshot(Guid vesselId, out VesselEnergySnapshot snapshot)
        {
            return energySnapshots.TryGetValue(vesselId, out snapshot);
        }

        public static void CaptureSnapshot(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) return;

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[CaptureSnapshot] scene={HighLogic.LoadedScene}, vessel='{vessel.vesselName}', packed={vessel.packed}");

            Guid id = vessel.id;
            double currentUT = Planetarium.GetUniversalTime();

            double totalSCamount = 0;
            double totalSCmaxAmount = 0;
            double totalECproduced = 0;
            double totalECconsumed = 0;
            double totalDischargeRate = 0;
            double realDischargeEC = 0;
                        
            double solarECproduced;
            double expUT; // 0 means "no depletion expected / not appliable"
                        
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
                        continue;

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
                        }
                    }

                    // Read KeepWarmMode (v3); fall back to KeepWarm bool (v2 cfg compat).
                    string keepWarmMode   = "false";
                    float  tempKeepWarmLo = 500f;
                    float  tempKeepWarmHi = 600f;

                    var kwModeField = pm.Fields["KeepWarmMode"];
                    if (kwModeField != null)
                    {
                        var raw = kwModeField.GetValue(pm);
                        if (raw != null) keepWarmMode = raw.ToString();
                    }
                    else
                    {
                        // v2 fallback
                        var kwField = pm.Fields["KeepWarm"];
                        if (kwField != null)
                        {
                            var raw = kwField.GetValue(pm);
                            if (raw != null && bool.TryParse(raw.ToString(), out bool kw) && kw)
                                keepWarmMode = "warm";
                        }
                    }

                    var loField = pm.Fields["TempKeepWarmLo"];
                    if (loField != null) { var r = loField.GetValue(pm); if (r != null && float.TryParse(r.ToString(), out float lo)) tempKeepWarmLo = lo; }
                    var hiField = pm.Fields["TempKeepWarmHi"];
                    if (hiField != null) { var r = hiField.GetValue(pm); if (r != null && float.TryParse(r.ToString(), out float hi)) tempKeepWarmHi = hi; }

                    if (keepWarmMode != "false" && dischargeRate > 1e-6)
                    {
                        // Cryo waste-heat-mode batteries draw zero EC upkeep — cooling is driven
                        // by a SystemHeat flux instead, both loaded and in background. Mirrors
                        // RealBattery.UsesCryoWasteHeat().
                        bool usesCryoWasteHeat = keepWarmMode == "cryo" && RealBatterySettings.UseCryoWasteHeatMode;
                        if (!usesCryoWasteHeat)
                        {
                            // Volume-based scaling, mirroring RealBattery.KeepWarmECperSec(): base
                            // on part volume (RBbaseVolume) when available, otherwise fall back to
                            // StoredCharge capacity — same fallback the loaded sim uses.
                            double volumeL = ReadPartVolumeL(part);
                            double scalingBase = volumeL > 0.0 ? volumeL : (sc?.maxAmount ?? 0.0);
                            double upkeep = scalingBase * RealBatterySettings.KeepWarmFrac; // EC/s nominal

                            if (RealBatterySettings.EnableHeatSimulation)
                            {
                                // Try precise SystemHeat loop temperature; fallback to coarse 0.5×
                                if (TryGetSystemHeatTemp(part, out float tK))
                                {
                                    float mul = KeepWarmTempMulFrom(tK, keepWarmMode, tempKeepWarmLo, tempKeepWarmHi);
                                    upkeep *= mul;
                                }
                                else
                                {
                                    // Fallback when loop temp is unavailable in background
                                    upkeep *= 0.5;
                                }
                            }
                            totalECconsumed += upkeep;
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

                if (RBLog.VerboseEnabled)
                    RBLog.Verbose($"[CaptureSnapshot] Corrected netEC_Gross for '{vessel.vesselName}': {netEC_Gross:F3} -> {correctedEC:F3} EC/s (ratio={ratio:F3})");

                netEC_Gross = correctedEC;
            }

            if (netEC_True < 0 && realDischargeEC < 0 && Math.Abs(realDischargeEC) < Math.Abs(netEC_True))
            {
                double ratio = Math.Abs(realDischargeEC / netEC_True);
                double correctedEC = netEC_True * ratio;

                if (RBLog.VerboseEnabled)
                    RBLog.Verbose($"[CaptureSnapshot] Corrected netEC_True for '{vessel.vesselName}': {netEC_True:F3} -> {correctedEC:F3} EC/s (ratio={ratio:F3})");

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
                energySnapshot.ExpUT = -1; // explicitly suppressed: vessel not draining
            }

            energySnapshots[id] = energySnapshot;

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[CaptureSnapshot] '{vessel.vesselName}': netEC_Gross={energySnapshot.netEC_Gross:F3}, netEC_True={energySnapshot.netEC_True:F3}, solar~{solarECproduced:F3} EC/s");
        }

        public static void ApplySnapshot(Vessel vessel)
        {
            if (!RealBatterySettings.EnableBackgroundSimulation) return;

            int maxSpecialistLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(vessel, "Engineer");
            double EngBonus = 0.95 + 0.06 * maxSpecialistLevel;

            if (vessel == null || !energySnapshots.ContainsKey(vessel.id)) return;

            var snap = energySnapshots[vessel.id];

            double currentTime = Planetarium.GetUniversalTime();
            double deltaTime = currentTime - snap.timestamp;

            if (deltaTime < 60.0) return; // too small an interval to be worth simulating

            // --- Tunables / epsilons ---
            const double EPS = 1e-6;   // generic numeric epsilon
            const double CAP_EPS = 1e-9;   // min virtual capacity to consider in distribution

            double hoursPerDay = RealBatterySettings.GetHoursPerDay();

            // Base delta from TRUE net (no solar) — convert EC/s to kWh
            double deltaSC_true = (snap.netEC_True * deltaTime) / 3600.0;

            double deltaSC_solar;

                deltaSC_solar = SimulateSolar(vessel, deltaTime, hoursPerDay);

            // Net vessel delta (kWh) to distribute additively among (eligible) batteries
            double deltaSC_vessel = deltaSC_true + deltaSC_solar;

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[ApplySnapshot] netEC_True={snap.netEC_True:F3}, solar={deltaSC_solar:F3} kWh, netEC_Gross={snap.netEC_Gross:F3}, deltaSC_vessel={deltaSC_vessel:F3} kWh");

            if (Math.Abs(deltaSC_vessel) < EPS && Math.Abs(snap.netEC_Gross) > EPS)
            {
                RBLog.Warn(
                    $"[ApplySnapshot] Using fallback delta from netEC_Gross " +
                    $"(true/solar estimate unavailable) -> netEC_Gross={snap.netEC_Gross:F3} EC/s"
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

            if (totalCapacity <= CAP_EPS || distribBatteries.Count == 0) return;

            RBLog.Info($"[ApplySnapshot] '{vessel.vesselName}': Δt={deltaTime:F0}s, Δ={deltaSC_vessel:F3} kWh");

            // Decide vessel intent based on the delta actually being distributed
            bool vesselWantsCharge = (deltaSC_vessel > EPS);
            bool vesselWantsDischarge = (deltaSC_vessel < -EPS);

            // --- Precompute cycle-wear context (vessel-level) ---
            bool initEscape = snap.isEscape;
            bool finalEscape = vessel.orbit.ApA > vessel.mainBody.sphereOfInfluence;
            bool boundOrSurface = vessel != null && (vessel.LandedOrSplashed || (!initEscape && !finalEscape));
            bool deepSpaceCruise = !boundOrSurface;
            bool protectFloatCharge = deepSpaceCruise && RealBatterySettings.DeepSpaceProtection;

            // Cycle wear represents the energy actually cycled (charged by day, discharged by
            // night) over the elapsed background period — not an instantaneous power (EC/s)
            // reused directly as if it were an energy (kWh), independent of how long the period
            // lasted. darkFrac approximates the unlit fraction of the period: 0.5 on the surface
            // (matches the fixed day/night split assumed by SimulateSolar_Planet), and
            // snap.orbitalShadowFrac in orbit.
            bool isSurfaceVessel = vessel.LandedOrSplashed;
            double darkFrac = isSurfaceVessel ? 0.5 : Clamp(snap.orbitalShadowFrac, 0.0, 1.0);
            double cycleWearTotal = Math.Abs(snap.netEC_True) * deltaTime * darkFrac / 3600.0; // kWh, vessel-level
            if (cycleWearTotal > EPS)
                RBLog.Verbose($"[ApplySnapshot] Cycle wear (vessel-level): {cycleWearTotal:F5} kWh " +
                              $"(|netEC_True|={Math.Abs(snap.netEC_True):F3} EC/s, deltaTime={deltaTime:F1}s, darkFrac={darkFrac:P0}) " +
                              $"for vessel '{vessel.vesselName}', distributed pro-quota by capacity share.");
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
                        if (!rb.InfiniteCycles)
                        {
                            rb.WearCounter += Math.Abs(applied) / EngBonus;
                            rb.UpdateBatteryLife();
                        }
                        if (RBLog.VerboseEnabled)
                            RBLog.Verbose($"[ApplySnapshot] Charged '{sc.part.partInfo.title}': +{applied:F3} kWh @ {efficiency:P0} eff");
                    }

                    // === Wear if a charge/discharge cycle has been simulated (non-perma-sunlight) ===
                    if (snap.netEC_True < 0 && boundOrSurface && cycleWearTotal > EPS)
                    {
                        double cycleWearShare = cycleWearTotal * share; // pro-quota by capacity, same as the charge distribution above
                        if (!rb.InfiniteCycles)
                        {
                            rb.WearCounter += cycleWearShare / EngBonus;
                            rb.UpdateBatteryLife();
                        }
                        appliedCycleWearThisTick = true; // prevent self-discharge this tick
                    }

                    // === Float-charge if battery already at or near full ===
                    else if (sc.amount >= virtualCap - EPS && !protectFloatCharge)
                    {
                        // Wear calculation based on the remaining simulation time
                        // Example: if it charges in half deltaTime, the other half is float-charge
                        double timeFractionFull = Math.Max(0.0, (deltaTime - (applied > EPS ? (applied / (effDelta / deltaTime)) : 0.0)) / deltaTime);
                        if (timeFractionFull > 0 && !rb.InfiniteCycles)
                        {
                            double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                            double lossPerSecond = (rb.SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0)) / (hoursPerDay * 3600.0) * virtualCap;
                            double cycleAmount = lossPerSecond * (deltaTime * timeFractionFull);
                            rb.WearCounter += cycleAmount / EngBonus;
                            rb.UpdateBatteryLife();
                            if (RBLog.VerboseEnabled)
                                RBLog.Verbose($"[ApplySnapshot] Float-charge simulated on '{sc.part.partInfo.title}': +{cycleAmount:F5} kWh wear over {timeFractionFull:P0} of background time");
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
                        if (!rb.InfiniteCycles)
                        {
                            rb.WearCounter += Math.Abs(applied) / EngBonus;
                            rb.UpdateBatteryLife();
                        }
                        if (RBLog.VerboseEnabled)
                            RBLog.Verbose($"[ApplySnapshot] Discharged '{sc.part.partInfo.title}': {applied:F3} kWh");
                    }
                }
            }

            // --- Pass 2: self-discharge, LifeDecay, SelfRunaway ---
            foreach (var (sc, rb, isEnabled, virtualCap) in allBatteries)
            {
                // --- SelfRunaway RIP check (scaled per-chemistry rate) ---
                // Guarded by the SelfRunawayInBackground setting.
                // RNG covers the full elapsed background interval.
                if (RealBatterySettings.SelfRunawayInBackground
                    && rb.SelfRunaway && rb.RunawayBaseChance > 0.0
                    && !rb.forcedRunawayActive && rb.SC_SOC >= 0.01)
                {
                    double ripHoursPerDay   = RealBatterySettings.GetHoursPerDay();
                    double ripDecayPerSec   = rb.SelfDischargeRate / (ripHoursPerDay * 3600.0);
                    double ripHalfLifeHours = ripDecayPerSec > 0.0
                        ? Math.Log(2) / ripDecayPerSec / 3600.0
                        : double.PositiveInfinity;
                    if (!double.IsInfinity(ripHalfLifeHours) && ripHalfLifeHours > 0.0)
                    {
                        float pPerHour = (float)(rb.RunawayBaseChance / ripHalfLifeHours * RealBatterySettings.SelfRunawayChanceMultiplier);
                        float pTotal   = 1f - Mathf.Pow(Mathf.Max(0f, 1f - pPerHour), (float)(deltaTime / 3600.0));
                        if (pTotal > 0f && UnityEngine.Random.value < pTotal)
                        {
                            rb.BatteryLife        = 0.0;
                            sc.amount             = 0.0;
                            rb.SC_SOC             = 0.0;
                            rb.BGSelfRunawaySent  = true;
                            rb.UpdateBatteryLife();

                            string partName   = sc.part.partInfo?.title ?? sc.part.partName;
                            string vesselName = vessel.vesselName;
                            var msg = new MessageSystem.Message(
                                Localizer.Format("#LOC_RB_BG_SelfRunaway_title"),
                                Localizer.Format("#LOC_RB_BG_SelfRunaway_msg", partName, vesselName),
                                MessageSystemButton.MessageButtonColor.RED,
                                MessageSystemButton.ButtonIcons.ALERT
                            );
                            MessageSystem.Instance?.AddMessage(msg);

                            RBLog.Warn($"[RealBattery][BG][RIP] Background self-runaway on '{partName}' ({vesselName}) (p={pTotal:P4})");
                            continue; // nothing more to do for this battery
                        }
                    }
                }

                // --- LifeDecay: SelfDischargeRate decays BatteryLife, not SoC ---
                // Unconditional (radioactive decay doesn't care about ON/OFF state).
                if (rb.LifeDecay && RealBatterySettings.EnableSelfDischarge)
                {
                    if (RealBatterySettings.EnableBatteryWear)
                    {
                        double lifeDecayPerSec = rb.SelfDischargeRate / (hoursPerDay * 3600.0);
                        rb.BatteryLife = Math.Max(0.0, rb.BatteryLife - lifeDecayPerSec * deltaTime);
                        rb.UpdateBatteryLife();
                        if (RBLog.VerboseEnabled)
                            RBLog.Verbose($"[ApplySnapshot] LifeDecay on '{sc.part.partInfo?.title}': BatteryLife={rb.BatteryLife:F6}");
                    }
                    continue; // skip SoC self-discharge for LifeDecay cells
                }

                // --- Standard self-discharge ---
                if (sc.amount <= EPS) continue;

                bool idle = !vesselWantsCharge && !vesselWantsDischarge;
                bool shouldSelfDischarge =
                    (!isEnabled) ||                                 // disabled -> always self-discharge
                    (!appliedCycleWearThisTick && (                 // only if no cycle wear this tick
                        (rb.IsPrimary && !vesselWantsDischarge) ||  // primary: unless actively discharging
                        (!rb.IsPrimary && idle)                     // rechargeable: only when truly idle
                    ));

                if (!shouldSelfDischarge) continue;

                double ActualLife = RealBatterySettings.EnableBatteryWear ? rb.BatteryLife : 1.0;
                double socLossPerDay    = rb.SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0);
                double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);
                double lossAmount       = socLossPerSecond * deltaTime * sc.maxAmount;

                double newAmount = Math.Max(0.0, sc.amount - lossAmount);
                double applied   = sc.amount - newAmount;
                if (applied > EPS)
                {
                    sc.amount = newAmount;
                    if (RBLog.VerboseEnabled)
                    {
                        string kind = isEnabled ? "Self-discharge" : "Autoself-discharge (disabled)";
                        RBLog.Verbose($"[ApplySnapshot] {kind} on '{sc.part.partInfo.title}': -{applied:F4} kWh");
                    }
                }
            }

            snap.timestamp = currentTime;
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
                if (RBLog.VerboseEnabled)
                    RBLog.Verbose($"[RestoreEnergySnapshot] NetEC={snap.netEC_Gross:F3}");
            }
            else
            {
                RBLog.Warn("[RestoreEnergySnapshot] Attempted to restore a null snapshot");
            }
        }

        public static void UpdateEnergySnapshot(Vessel vessel)
        {
            if (vessel == null || !energySnapshots.ContainsKey(vessel.id)) return;

            var snap = energySnapshots[vessel.id];
            double currentUT = Planetarium.GetUniversalTime();
            double deltaTime = currentUT - snap.timestamp;

            if (deltaTime <= 0) return;

            double deltaEnergy = 0;
            double hoursPerDay = RealBatterySettings.GetHoursPerDay();

            if (snap.netEC_Gross > 0.00001 && snap.totalDischargeRate > 0)
            {
                // Simulate maintenance (floatcharge): if producing but the batteries are full -> maintenance cycles
                double cycleFraction = 0.001; // 0.1% of capacity per snapshot
                double simulatedWearKWh = snap.storedChargeMaxAmount * cycleFraction;
                deltaEnergy = 0; // no actual energy change

                if (RBLog.VerboseEnabled)
                    RBLog.Verbose($"[UpdateEnergySnapshot] Float-charge simulation active: +{simulatedWearKWh:F3} kWh wear equivalent");
            }
            else if (snap.netEC_Gross > 0.00001)
            {
                double effectiveRate = Math.Min(snap.netEC_Gross, snap.totalDischargeRate);
                deltaEnergy = (effectiveRate * deltaTime) / 3600.0;
            }
            else if (snap.netEC_Gross < -0.00001 && snap.totalDischargeRate > 0)
            {
                double effectiveRate = Math.Min(Math.Abs(snap.netEC_Gross), snap.totalDischargeRate);
                deltaEnergy = -(effectiveRate * deltaTime) / 3600.0;
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
            }

            snap.storedChargeAmount += deltaEnergy;
            snap.storedChargeAmount = Math.Max(0, Math.Min(snap.storedChargeMaxAmount, snap.storedChargeAmount));

            // Advance the timestamp: this call already consumed [snap.timestamp, currentUT) for
            // its estimate. Whoever mutates snap state owns advancing its clock — a second call
            // (or ApplySnapshot afterwards) must see a fresh interval, not re-consume this one.
            snap.timestamp = currentUT;

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[UpdateEnergySnapshot] '{vessel.vesselName}' -> {snap.storedChargeAmount:F3}/{snap.storedChargeMaxAmount:F3} kWh");
        }

        public static double SimulateSolar(Vessel vessel, double deltaTime, double hoursPerDay)
        {
            double total_kWh;

            var snap = energySnapshots[vessel.id];

            //int sun = Planetarium.fetch.Sun.flightGlobalsIndex;
            // Resolve the correct star for the initial and final main bodies (Kopernicus multistar).
            // Fallback to stock Sun when Kopernicus data is unavailable.
            CelestialBody initMainBody = GetBodyByFlightGlobalsIndex(snap.mainBody) ?? Planetarium.fetch.Sun;
            CelestialBody finalMainBody = vessel.mainBody ?? Planetarium.fetch.Sun;
            CelestialBody initStar = ResolveStarForBody(initMainBody, out _);
            CelestialBody finalStar = ResolveStarForBody(finalMainBody, out _);
            int initStarIdx = initStar.flightGlobalsIndex;
            int finalStarIdx = finalStar.flightGlobalsIndex;
            int initBody = snap.mainBody;
            int finalBody = vessel.mainBody.flightGlobalsIndex;

            bool initEscape = snap.isEscape;
            bool finalEscape = vessel.orbit.ApA > vessel.mainBody.sphereOfInfluence;

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[SimulateSolar] InitBody={snap.mainBodyName} (escape={initEscape}), FinalBody={vessel.mainBody.name} (escape={finalEscape}), Δt={deltaTime:F1}s");

            // Scenario selection
            if (vessel.LandedOrSplashed || (!initEscape && !finalEscape))
            {
                // Surface mode: always simulate using initial orbit snapshot
                total_kWh = SimulateSolar_Planet(vessel, deltaTime);
            }
            else if (initBody == initStarIdx || finalBody == finalStarIdx || initEscape || finalEscape)
            {
                total_kWh = SimulateSolar_Sun(vessel, deltaTime);
            }
            else
            {
                RBLog.Warn("[SimulateSolar] No matching scenario found, defaulting to rough solar output.");
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

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[SimulateSolar_Sun] AvgECps={avgECps:F3} EC/s -> Total={total_kWh:F3} kWh");

            return total_kWh;
        }

        // Simulate starting from snapshot data, moving forward in time
        private static double SimulateSolar_Planet(Vessel vessel, double deltaTime)
        {
            var snap = energySnapshots[vessel.id];

            // Resolve correct star for surface day/night and orbital eclipse logic
            // (multistar/Kopernicus-aware; falls back to Planetarium.fetch.Sun internally).
            CelestialBody sun = ResolveStarForBody(vessel.mainBody, out _);

            // If the snapshot was taken with orbital naming, remap to surface naming.
            if (vessel.LandedOrSplashed && (snap.startPhase == IllumPhase.Sunlit || snap.startPhase == IllumPhase.Shadow))
            {
                // Remap labels and (optionally) refresh tToTransition & period for surface consistency
                snap.startPhase = (SolarElevationRad(vessel, sun) > 0.0) ? IllumPhase.Day : IllumPhase.Night;

                // Optional but recommended for full coherence if state changed since capture:
                snap.tToTransition = TimeToSurfaceTransition(vessel, sun, Math.Max(vessel.mainBody.rotationPeriod, 1.0));
                snap.period = Math.Max(vessel.mainBody.rotationPeriod, 1.0);
            }

            // Surface or orbit branch for period and lit fraction
            bool isSurface = vessel.LandedOrSplashed;

            double P = Math.Max(snap.period, 1.0);
            double t = deltaTime;

            double total_kWh;

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

                    if (RBLog.VerboseEnabled)
                        RBLog.Verbose($"[SimulateSolar_Planet] Surface-Polar: lat={latAbs:F2}° ≥ {POLAR_LAT_THRESHOLD_DEG}°, " +
                            $"blendedFrac={blendedFrac:P1} -> AvgECps={avgECps:F3} EC/s, Δt={t:F1}s, Total={total_kWh:F3} kWh");
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

            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[SimulateSolar_Planet] period={P:F1}s | litFracCycle={litFracCycle:P1} | Ecycle={Ecycle_kWh:F3} kWh | fullCycles={N} | remainder={r:F1}s");

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

                if (RBLog.VerboseEnabled)
                    RBLog.Verbose($"[SimulateSolar_Planet] seg={seg:F1}s | Lit={isLit} | EnergyThisSeg={segEnergy:F4} kWh");

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
            if (RBLog.VerboseEnabled)
                RBLog.Verbose($"[SimulateSolar_Planet] total={total_kWh:F3} kWh");
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
            var body = vessel.mainBody;
            var sun = ResolveStarForBody(body, out _);

            if (vessel.LandedOrSplashed)
            {
                // --- SURFACE BRANCH ---
                double Pday = Math.Max(body.rotationPeriod, 1.0);
                
                // Day/night from solar elevation
                double sunEl = SolarElevationRad(vessel, sun);
                bool isDay = sunEl > 0.0;
                startPhase = isDay ? IllumPhase.Day : IllumPhase.Night;

                // Very light model for daylight fraction: cos(latitude) (good enough on airless bodies)
                //double latRad = vessel.latitude * Math.PI / 180.0;
                
                // Time to sunrise/sunset using dH/dt ~ 2π/Pday * cos(lat)
                tToTransition = TimeToSurfaceTransition(vessel, sun, Pday);
            }
            else
            {
                // --- ORBITAL BRANCH ---
                double R = body.Radius;
                double a = vessel.orbit.semiMajorAxis;
                //double P = Math.Max(vessel.orbit.period, 1.0);
                
                // Geometric umbra fraction (clamped)
                double s = Clamp(R / Math.Max(a, R + 1.0), 0.0, 1.0);
                double theta = Math.Asin(s);                // half-angle of eclipse
                double fracShadow = theta / Math.PI;
                orbitalShadowFrac = Clamp(fracShadow, 0.0, 1.0);

                // Phase and time-to-transition derived from the same in-plane frame, so they
                // can never disagree with each other (see OrbitalIlluminationStatus).
                OrbitalIlluminationStatus(vessel, body, sun, out startPhase, out tToTransition);
            }
        }

        // Helper for double precision clamp (since Math.Clamp is not available in .NET Framework 4.8)
        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static CelestialBody ResolveStarForBody(CelestialBody body, out double luminosity)
        {
            luminosity = 1.0;
            if (body == null) return Planetarium.fetch.Sun;

            if (KopernicusStarResolver.TryResolveStar(body, out var star, out var lum))
            {
                if (star != null)
                {
                    luminosity = lum;
                    return star;
                }
            }
            return Planetarium.fetch.Sun;
        }

        private static CelestialBody GetBodyByFlightGlobalsIndex(int idx)
        {
            if (FlightGlobals.Bodies == null) return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                var b = FlightGlobals.Bodies[i];
                if (b != null && b.flightGlobalsIndex == idx)
                    return b;
            }
            return null;
        }


        // Computes the current Sunlit/Shadow phase and the time to the next terminator crossing
        // for an orbiting vessel, from a single self-consistent in-plane frame.
        //
        // Fixes three bugs present in the previous split IsInEclipse/TimeToOrbitalTransition
        // implementation (2026-07-14):
        //   - IsInEclipse tested the angle to the SUB-solar direction (sun.position - body.position)
        //     and called that "eclipse" — it actually flagged the fully-lit side and missed the
        //     real umbra entirely.
        //   - The orbital frame was built from Orbit.GetOrbitNormal(), which is expressed in KSP's
        //     internal orbit frame (Y/Z swapped vs. world space) and was mixed directly with
        //     world-space vectors (r_world, antiSun) without conversion, making theta_now
        //     essentially arbitrary relative to the real geometry.
        //   - The true-anomaly-based dt from Orbit.GetDTforTrueAnomaly() was only rejected on
        //     NaN/Infinity/negative, not on dt > one period, so a bad frame could yield a
        //     time-to-transition larger than the orbital period itself.
        //
        // Deriving both phase and timing from the same theta_now/phi here means they can never
        // contradict each other, which the old split implementation didn't guarantee either.
        private static void OrbitalIlluminationStatus(Vessel v, CelestialBody body, CelestialBody sun,
            out IllumPhase phase, out double tToTransition)
        {
            Vector3d r_world = v.GetWorldPos3D() - body.position;          // vessel position, body frame
            Vector3d antiSun = (body.position - sun.position).normalized;  // anti-sun direction from body

            // Orbit normal from the actual world-space state vectors (position × velocity), not
            // Orbit.GetOrbitNormal() — see note above. Cross(h, X) for any in-plane X points 90°
            // ahead of X in the direction of motion, which is what makes theta_now (below) grow
            // forward in time regardless of KSP's internal frame conventions.
            Vector3d h = Vector3d.Cross(r_world, v.obt_velocity);
            if (h.sqrMagnitude < 1e-12)
            {
                // Degenerate (near-radial) state vector; treat as always lit.
                phase = IllumPhase.Sunlit;
                tToTransition = double.PositiveInfinity;
                return;
            }
            h = h.normalized;

            Vector3d aProj = antiSun - Vector3d.Dot(antiSun, h) * h;       // anti-sun projected onto orbit plane
            if (aProj.sqrMagnitude < 1e-12)
            {
                // High beta-angle: sun direction ~perpendicular to the orbit plane, no eclipse expected.
                phase = IllumPhase.Sunlit;
                tToTransition = double.PositiveInfinity;
                return;
            }
            Vector3d e1 = aProj.normalized;                 // 0° = anti-sun projected (shadow center)
            Vector3d e2 = Vector3d.Cross(h, e1);            // 90° ahead, in the direction of motion

            // Current in-plane angle from the anti-sun axis (0 = deepest shadow, ±π = sub-solar point).
            double x = Vector3d.Dot(r_world, e1);
            double y = Vector3d.Dot(r_world, e2);
            double theta_now = Math.Atan2(y, x);            // [-π, π]

            // Shadow half-angle φ (use current radius).
            double rmag = Math.Max(r_world.magnitude, body.Radius + 1.0);
            double phi = Math.Asin(Clamp(body.Radius / rmag, 0.0, 1.0));    // [0..π/2]

            // In shadow iff within the umbra half-angle of the anti-sun axis.
            phase = (Math.Abs(theta_now) < phi) ? IllumPhase.Shadow : IllumPhase.Sunlit;

            // Angular advance to the next terminator (+φ or -φ), forward in the direction of motion.
            double d1 = WrapTo2Pi(phi - theta_now);
            double d2 = WrapTo2Pi(-phi - theta_now);
            double dTheta = Math.Min(d1, d2);

            // Convert to time via mean motion. The whole illumination model already assumes a
            // circular approximation elsewhere (orbitalShadowFrac from asin(R/a), fixed
            // alternating lit/dark segments in SimulateSolar_Planet), so a true-anomaly-based
            // conversion doesn't add real accuracy here and risks wrapping past a full period.
            // Clamp to at most one period as a hard safety bound.
            double P = Math.Max(v.orbit.period, 1.0);
            double n = 2.0 * Math.PI / P;
            double dt = dTheta / Math.Max(n, 1e-6);
            tToTransition = Math.Max(1e-3, Math.Min(dt, P));
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
