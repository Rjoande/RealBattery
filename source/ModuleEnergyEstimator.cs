// RealBattery Background EC Simulator – refined estimation of module EC usage/production
// This version includes logic for:
// - Command modules (with hibernation)
// - ResourceConverter & Harvester (specialist bonus)
// - SolarPanels (distance, occlusion, tracking/static)
// - RTGs
// - Radiators (damped input)
// - Lights (basic: no blinking estimation)
// - Science Lab (data processing)

using KSP;
using System;
using System.Collections.Generic;
using UnityEngine;
using static VehiclePhysics.EnergyProvider;

namespace RealBattery
{
    public static class ModuleEnergyEstimator
    {
        // Comments in English, as requested
        private static bool TryGetField<T>(PartModule m, string name, out T value)
        {
            var f = m.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(T))
            {
                value = (T)f.GetValue(m);
                return true;
            }
            value = default;
            return false;
        }

        public static void EstimateECUsage(Part part, ref double partECproduced, ref double partECconsumed, int maxSpecialistLevel)
        {
            foreach (var module in part.Modules)
            {
                // Command
                if (module is ModuleCommand command)
                {
                    double multiplier = 1.0;

                    if (command.hibernation)
                    {
                        multiplier = command.hibernationMultiplier;
                        Debug.Log($"[RealBattery] ModuleCommand on '{part.partInfo.title}' is HIBERNATING — multiplier = {multiplier:F2}");
                    }
                    else
                    {
                        Debug.Log($"[RealBattery] ModuleCommand on '{part.partInfo.title}' is ACTIVE — no multiplier");
                    }

                    foreach (var res in command.resHandler.inputResources)
                    {
                        if (res.name == "ElectricCharge")
                        {
                            double rate = res.rate * multiplier;
                            partECconsumed += rate;
                            Debug.Log($"[RealBattery] ModuleCommand EC consumption: base={res.rate:F3}, adjusted={rate:F3} EC/s on '{part.partInfo.title}'");
                        }
                    }
                }

                // ResourceConverter
                if (module is ModuleResourceConverter converter && converter.IsActivated)
                {
                    Debug.Log($"[RealBattery] ResourceConverter on '{part.partInfo.title}' — Active: {converter.IsActivated}, UseSpecialistBonus: {converter.UseSpecialistBonus}");

                    double factor = 1.0;
                    if (converter.UseSpecialistBonus)
                    {
                        string trait = module.moduleName == "SnackProcessor" ? "Scientist" : "Engineer";
                        int maxLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(part.vessel, trait);
                        factor = converter.SpecialistEfficiencyFactor * maxLevel + converter.SpecialistBonusBase;
                        Debug.Log($"[RealBattery] Specialist bonus factor calculated ({trait}, level {maxLevel}): {factor:F3}");
                    }

                    double eff = 1.0;
                    if (!TryGetField<double>(converter, "ThermalEfficiency", out eff))
                        if (!TryGetField<float>(converter, "EfficiencyBonus", out var effF)) eff = 1.0; else eff = effF;
                    eff = Math.Max(0.0, Math.Min(1.0, eff));

                    foreach (var input in converter.inputList)
                    {
                        if (input.ResourceName == "ElectricCharge")
                        {
                            double rate = input.Ratio * factor * eff;
                            partECconsumed += rate;
                            Debug.Log($"[RealBattery] Converter '{part.partInfo.title}' — EC: -{rate:F3} EC/s (eff={eff:F2})");
                        }
                    }
                    foreach (var output in converter.outputList)
                    {
                        if (output.ResourceName == "ElectricCharge")
                        {
                            double rate = output.Ratio * factor * eff;
                            partECproduced += rate;
                        }
                    }
                }

                // ResourceHarvester
                if (module is ModuleResourceHarvester harvester && harvester.IsActivated)
                {
                    Debug.Log($"[RealBattery] Found ModuleResourceHarvester on '{part.partInfo.title}' — Active: {harvester.IsActivated}, status: {harvester.status}, UseSpecialistBonus: {harvester.UseSpecialistBonus}");
                                        
                    double factor = 1.0;
                    if (harvester.UseSpecialistBonus)
                    {
                        int maxLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(part.vessel, "Engineer") + 1;
                        factor = harvester.SpecialistEfficiencyFactor * maxLevel + harvester.SpecialistBonusBase;
                        Debug.Log($"[RealBattery] Specialist bonus factor calculated (Engineer, level {maxLevel-1}): {factor:F3}");
                    }

                    double eff = 1.0;
                    // Some builds expose "EfficiencyBonus" or "ThermalEfficiency"
                    if (!TryGetField<float>(harvester, "EfficiencyBonus", out var effF))
                        eff = 1.0;
                    else eff = Mathf.Clamp01(effF);

                    foreach (var input in harvester.inputList)
                        if (input.ResourceName == "ElectricCharge")
                            partECconsumed += input.Ratio * factor * eff;
                }

                // Radiator
                if (module is ModuleActiveRadiator radiator)
                {
                    // Only if actually cooling
                    if (radiator.IsCooling && radiator.resHandler?.inputResources != null)
                    {
                        double maxRate = 0;
                        foreach (var r in radiator.resHandler.inputResources)
                            if (r.name == "ElectricCharge") maxRate = Math.Max(maxRate, r.rate);

                        double frac = 0.5; // conservative fallback
                                           // Known internal names seen across KSP versions:
                        if (TryGetField<double>(radiator, "currentCooling", out var currentCooling) &&
                            TryGetField<float>(radiator, "maxEnergyTransfer", out var maxEnergyTransfer) &&
                            maxEnergyTransfer > 0f)
                        {
                            frac = Mathf.Clamp01((float)(currentCooling / (double)maxEnergyTransfer));
                        }
                        else if (TryGetField<float>(radiator, "currentRadiatorNormalized", out var norm))
                        {
                            frac = Mathf.Clamp01(norm);
                        }

                        double rate = maxRate * frac;
                        partECconsumed += rate;
                        Debug.Log($"[RealBattery] Radiator '{part.partInfo.title}' — EC: -{rate:F3} EC/s (frac={frac:F2})");
                    }
                }

                // Science Converter (Lab)
                if (module is ModuleScienceConverter lab)
                {
                    bool running = lab.IsActivated;
                    // Optional: read private flags when available to confirm activity
                    if (TryGetField<bool>(lab, "isOperational", out var op)) running &= op;

                    if (running)
                    {
                        partECconsumed += lab.powerRequirement;
                        Debug.Log($"[RealBattery] ScienceLab '{part.partInfo.title}' — EC: -{lab.powerRequirement:F3} EC/s");
                    }
                }

                // Light
                if (module is ModuleLight light && light.isOn && light.useResources)
                {
                    foreach (var res in light.resHandler.inputResources)
                    {
                        if (res.name == "ElectricCharge")
                        {
                            partECconsumed += res.rate;
                            Debug.Log($"[RealBattery] ModuleLight (ON): -{res.rate:F3} EC/s from '{part.partInfo.title}'");
                        }
                    }
                }

                // Generator
                if (module is ModuleGenerator gen)
                {
                    foreach (var res in gen.resHandler.outputResources)
                    {
                        if (res.name == "ElectricCharge" && res.rate > 0)
                        {
                            partECproduced += res.rate;
                            Debug.Log($"[RealBattery] ModuleGenerator: +{res.rate:F3} EC/s from '{part.partInfo.title}'");
                        }
                    }
                }

                // ====== THIRD-PARTY MODUES ======

                // SCANsat
                if (module.moduleName == "SCANsat")
                {
                    bool isScanning = false;

                    try
                    {
                        var field = module.Fields.GetValue("scanning");
                        if (field != null)
                            bool.TryParse(field.ToString(), out isScanning);
                    }
                    catch { }

                    if (isScanning && module.resHandler?.inputResources != null)
                    {
                        foreach (var res in module.resHandler.inputResources)
                        {
                            if (res.name == "ElectricCharge" && res.rate > 0)
                            {
                                partECconsumed += res.rate;
                                Debug.Log($"[RealBattery] SCANsat '{part.partInfo.title}' — Scanning ON — EC: -{res.rate:F3} EC/s");
                            }
                        }
                    }
                    else
                    {
                        Debug.Log($"[RealBattery] SCANsat '{part.partInfo.title}' — not scanning or no EC use");
                    }
                }

                // CryoTanks support (ModuleCryoTank)
                if (module.moduleName == "ModuleCryoTank")
                {
                    bool cooling = false;
                    try
                    {
                        var flag = module.Fields.GetValue("CoolingEnabled");
                        if (flag != null)
                            bool.TryParse(flag.ToString(), out cooling);
                    }
                    catch { }

                    if (cooling)
                    {
                        ConfigNode config = part.partInfo?.partConfig;
                        if (config != null)
                        {
                            var moduleNodes = config.GetNodes("MODULE");
                            foreach (var modNode in moduleNodes)
                            {
                                if (modNode.GetValue("name") == "ModuleCryoTank")
                                {
                                    var boiloffNodes = modNode.GetNodes("BOILOFFCONFIG");
                                    foreach (var boilNode in boiloffNodes)
                                    {
                                        if (!boilNode.HasValue("FuelName") || !boilNode.HasValue("CoolingCost"))
                                            continue;

                                        string fuel = boilNode.GetValue("FuelName");
                                        if (!part.Resources.Contains(fuel) || part.Resources[fuel].maxAmount <= 0)
                                            continue;

                                        if (double.TryParse(boilNode.GetValue("CoolingCost"), out double cost))
                                        {
                                            partECconsumed += cost;
                                            Debug.Log($"[RealBattery] CryoTank '{part.partInfo.title}' — CoolingCost: -{cost:F3} EC/s (for {fuel})");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }


                // Far Future Techs & Space Dust
                if (module.moduleName == "ModuleSpaceDustHarvester")
                {
                    bool enabled = false;
                    double cost = 0;

                    try
                    {
                        var enabledRaw = module.Fields.GetValue("Enabled");
                        if (enabledRaw != null)
                            bool.TryParse(enabledRaw.ToString(), out enabled);

                        var costRaw = module.Fields.GetValue("PowerCost");
                        if (costRaw != null)
                            double.TryParse(costRaw.ToString(), out cost);
                    }
                    catch { }

                    if (enabled && cost > 0)
                    {
                        partECconsumed += cost;
                        Debug.Log($"[RealBattery] {module.moduleName} '{part.partInfo.title}' — Enabled, EC: -{cost:F3} EC/s");
                    }
                }

                if (module.moduleName == "ModuleSpaceDustScanner")
                {
                    bool enabled = false;
                    double powerCost = 0;

                    try
                    {
                        bool.TryParse(module.Fields.GetValue("Enabled")?.ToString(), out enabled);
                        double.TryParse(module.Fields.GetValue("PowerCost")?.ToString(), out powerCost);
                    }
                    catch { }

                    if (enabled && powerCost > 0)
                    {
                        partECconsumed += powerCost;
                        Debug.Log($"[RealBattery] SpaceDustScanner '{part.partInfo.title}' — EC consumption: -{powerCost:F3} EC/s (Enabled)");
                    }
                    else
                    {
                        Debug.Log($"[RealBattery] SpaceDustScanner '{part.partInfo.title}' — not scanning or PowerCost = 0");
                    }
                }

                if (module.moduleName == "ModuleAntimatterTank")
                {
                    bool enabled = false;
                    double cost = 0;

                    try
                    {
                        bool.TryParse(module.Fields.GetValue("ContainmentEnabled")?.ToString(), out enabled);
                        double.TryParse(module.Fields.GetValue("ContainmentCost")?.ToString(), out cost);
                    }
                    catch { }

                    if (enabled && cost > 0)
                    {
                        partECconsumed += cost;
                        Debug.Log($"[RealBattery] AntimatterTank '{part.partInfo.title}' — EC consumption: -{cost:F3} EC/s");
                    }
                }

                if (module.moduleName == "FusionReactor")
                {
                    bool enabled = false;
                    double ecRate = 0;

                    try
                    {
                        bool.TryParse(module.Fields.GetValue("Enabled")?.ToString(), out enabled);
                        double.TryParse(module.Fields.GetValue("CurrentPowerProduced")?.ToString(), out ecRate);
                    }
                    catch { }

                    if (enabled && ecRate > 0)
                    {
                        partECproduced += ecRate;
                        Debug.Log($"[RealBattery] FusionReactor '{part.partInfo.title}' — EC production: +{ecRate:F3} EC/s");
                    }
                }

                if (module.moduleName == "ModuleSystemHeatFissionReactor")
                {
                    bool enabled = false;

                    try
                    {
                        bool.TryParse(module.Fields.GetValue("Enabled")?.ToString(), out enabled);
                    }
                    catch { }

                    if (enabled)
                    {
                        ConfigNode config = part.partInfo?.partConfig;
                        if (config != null)
                        {
                            foreach (var modNode in config.GetNodes("MODULE"))
                            {
                                if (modNode.GetValue("name") == "ModuleSystemHeatFissionReactor" && modNode.HasNode("ElectricalGeneration"))
                                {
                                    var curveNode = modNode.GetNode("ElectricalGeneration");
                                    var keys = curveNode.GetValues("key");
                                    double maxOutput = 0;

                                    foreach (var key in keys)
                                    {
                                        string[] split = key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                        if (split.Length >= 2 && double.TryParse(split[1], out double value))
                                            maxOutput = Math.Max(maxOutput, value);
                                    }

                                    if (maxOutput > 0)
                                    {
                                        partECproduced += maxOutput;
                                        Debug.Log($"[RealBattery] FissionReactor '{part.partInfo.title}' — estimated max EC: +{maxOutput:F3} EC/s (from ElectricalGeneration curve)");
                                    }
                                }
                            }
                        }
                    }
                }

                // Benjee's 10 MMSEV
                if (module.moduleName == "ModulePETTurbine")
                {
                    bool isActive = false;
                    double chargeRate = 0;

                    try
                    {
                        bool.TryParse(module.Fields.GetValue("isActive")?.ToString(), out isActive);
                        double.TryParse(module.Fields.GetValue("chargeRate")?.ToString(), out chargeRate);
                    }
                    catch { }

                    chargeRate *= 0.7;

                    if (isActive && chargeRate > 0)
                    {
                        partECproduced += chargeRate;
                        Debug.Log($"[RealBattery] PETTurbine '{part.partInfo.title}' — EC production: +{chargeRate:F3} EC/s");
                    }
                }
            }
        }

        // Returns rough total EC/s from all *active* vessel solar panels.
        // Rules: only panels that are extended (or static) count; non-tracking panels get a 0.5 penalty.
        // No distance scaling here.
        public static double SolarPanelsBaseOutput(Vessel vessel)
        {
            if (vessel == null) return 0.0;

            // 1/r^2 w.r.t Kerbin SMA as reference (same as current estimator)
            double refDist = FlightGlobals.GetBodyByName("Kerbin")?.orbit?.semiMajorAxis ?? 13599840256.0;
            double currDist = vessel.distanceToSun;
            double invSqrScale = Math.Pow(refDist / Math.Max(currDist, 1.0), 2.0);

            double totalPVECps = 0.0;
            int ecId = PartResourceLibrary.ElectricityHashcode;

            foreach (var part in vessel.parts)
            {
                var panels = part.FindModulesImplementing<ModuleDeployableSolarPanel>();
                if (panels == null || panels.Count == 0) continue;

                foreach (var panel in panels)
                {
                    if (panel == null) continue;

                    // Consider active if extended; static panels (non-breakable) count as always-extended
                    bool isExtended = (panel.deployState == ModuleDeployablePart.DeployState.EXTENDED) || !panel.isBreakable;
                    if (!isExtended) continue;

                    double rate = 0.0;

                    // Prefer the EC output defined in resHandler (when available)
                    var outs = panel.resHandler?.outputResources;
                    if (outs != null && outs.Count > 0)
                    {
                        for (int i = 0; i < outs.Count; i++)
                        {
                            if (outs[i].id == ecId && outs[i].rate > 0)
                            {
                                rate = outs[i].rate;
                                break; // first EC output found is enough
                            }
                        }
                    }

                    // Fallback: use the module's chargeRate
                    if (rate <= 0.0) rate = Math.Max(0.0, panel.chargeRate);

                    // Simple incidence penalty for non-tracking panels
                    if (!panel.isTracking) rate *= 0.5;
                                        
                    Debug.Log($"[RealBattery] SolarPanel '{part.partInfo.title}' rough prod: +{rate:F3} EC/s");

                    totalPVECps += rate;
                }
            }

            totalPVECps *= invSqrScale;

            Debug.Log($"[RealBattery] SolarPanelsBaseOutput for '{vessel.vesselName}': {totalPVECps:F3} EC/s");

            return totalPVECps;
        }

        public static int GetMaxSpecialistLevel(Vessel vessel, string trait)
        {
            int max = 0;
            foreach (var part in vessel.parts)
            {
                var protoCrew = part.protoPartSnapshot?.protoModuleCrew;
                if (protoCrew != null)
                {
                    foreach (var crew in protoCrew)
                    {
                        if (crew.trait == trait)
                            max = Math.Max(max, crew.experienceLevel);
                    }
                }
            }
            return max;
        }
    }
}
