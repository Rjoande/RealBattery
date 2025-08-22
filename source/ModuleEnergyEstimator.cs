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

namespace RealBattery
{
    public static class ModuleEnergyEstimator
    {
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
                    Debug.Log($"[RealBattery] ResourceConverter su '{part.partInfo.title}' — Active: {converter.IsActivated}, UseSpecialistBonus: {converter.UseSpecialistBonus}");

                    double factor = 1.0;
                    if (converter.UseSpecialistBonus)
                    {
                        string trait = module.moduleName == "SnackProcessor" ? "Scientist" : "Engineer";
                        int maxLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(part.vessel, trait);
                        factor = converter.SpecialistEfficiencyFactor * maxLevel + converter.SpecialistBonusBase;
                        Debug.Log($"[RealBattery] Specialist bonus factor calculated ({trait}, level {maxLevel}): {factor:F3}");
                    }

                    foreach (var input in converter.inputList)
                    {
                        if (input.ResourceName == "ElectricCharge")
                        {
                            double rate = input.Ratio * factor;
                            partECconsumed += rate;
                            Debug.Log($"[RealBattery] INPUT EC: -{rate:F3} EC/s");
                        }
                    }

                    foreach (var output in converter.outputList)
                    {
                        if (output.ResourceName == "ElectricCharge")
                        {
                            double rate = output.Ratio * factor;
                            partECproduced += rate;
                            Debug.Log($"[RealBattery] OUTPUT EC: +{rate:F3} EC/s");
                        }
                    }
                }

                // ResourceHarvester
                if (module is ModuleResourceHarvester harvester /*&& harvester.IsActivated*/)
                {
                    Debug.Log($"[RealBattery] Found ModuleResourceHarvester on '{part.partInfo.title}' — Active: {harvester.IsActivated}, status: {harvester.status}, UseSpecialistBonus: {harvester.UseSpecialistBonus}");

                    if (harvester.IsActivated)
                    {
                        double factor = 1.0;
                        if (harvester.UseSpecialistBonus)
                        {
                            int maxLevel = ModuleEnergyEstimator.GetMaxSpecialistLevel(part.vessel, "Engineer") + 1;
                            factor = harvester.SpecialistEfficiencyFactor * maxLevel + harvester.SpecialistBonusBase;
                            Debug.Log($"[RealBattery] Specialist bonus factor calculated (Engineer, level {maxLevel-1}): {factor:F3}");
                        }

                        foreach (var input in harvester.inputList)
                        {
                            if (input.ResourceName == "ElectricCharge")
                            {
                                double rate = input.Ratio * factor;
                                partECconsumed += rate;
                                Debug.Log($"[RealBattery] INPUT EC: -{rate:F3} EC/s");
                            }
                        }

                        if (harvester.outputList != null)
                        {
                            foreach (var output in harvester.outputList)
                            {
                                if (output.ResourceName == "ElectricCharge")
                                {
                                    double rate = output.Ratio * factor;
                                    partECproduced += rate;
                                    Debug.Log($"[RealBattery] OUTPUT EC: +{rate:F3} EC/s");
                                }
                            }
                        }
                    }
                }

                // Radiator
                if (module is ModuleActiveRadiator radiator && radiator.IsCooling)
                {
                    foreach (var res in radiator.resHandler.inputResources)
                    {
                        if (res.name == "ElectricCharge" && res.rate > 0)
                        {
                            //double rate = res.rate * 0.5; // consumo stimato medio
                            partECconsumed += res.rate;
                            Debug.Log($"[RealBattery] Radiator '{part.partInfo.title}' is cooling — estimated EC input: -{res.rate:F3} EC/s");
                        }
                    }
                }

                // Science Converter (Lab)
                if (module is ModuleScienceConverter lab && lab.IsActivated)
                {
                    partECconsumed += lab.powerRequirement;
                    Debug.Log($"[RealBattery] ScienceLab '{part.partInfo.title}' is running — EC: -{lab.powerRequirement:F3} EC/s");
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

                // SolarPanel
                if (module is ModuleDeployableSolarPanel panel && panel.deployState == ModuleDeployablePart.DeployState.EXTENDED)
                {
                    double output = panel.chargeRate;
                    string type = panel.isTracking ? "Tracking" : "Static";

                    if (!panel.isTracking) output *= 0.5;

                    // Distance from the Sun (in meters)
                    double referenceDistance = FlightGlobals.GetBodyByName("Kerbin")?.orbit?.semiMajorAxis ?? 13599840256.0;

                    Vector3d sunPosition = Planetarium.fetch.Sun.position;
                    Vector3d vesselPosition = part.vessel.GetWorldPos3D();

                    double currentDistance = (vesselPosition - sunPosition).magnitude;
                    double distanceEfficiency = Math.Pow(referenceDistance / currentDistance, 2.0);

                    // Calculate fraction of time in the Sun
                    double exposureFactor = 1.0;

                    if (FlightGlobals.currentMainBody != Planetarium.fetch.Sun)
                    {
                        if (part.vessel.Landed || part.vessel.Splashed)
                        {
                            // On the ground --> estimates a long-term average (inaccurate if deltaTime < planet rotation period)
                            exposureFactor = 0.5;
                        }
                        else
                        {
                            // Circular orbit --> geometric shadow fraction
                            double altitude = part.vessel.altitude;
                            double semiMajorAxis = part.vessel.orbit.semiMajorAxis;

                            if (altitude >= 0 && semiMajorAxis > 0 && (1.0 - altitude / semiMajorAxis) <= 1.0)
                            {
                                double eclipseAngle = Math.Asin(1.0 - (altitude / semiMajorAxis));
                                double f_sun = 1.0 - eclipseAngle / Math.PI;
                                exposureFactor = Math.Max(0.0, Math.Min(1.0, f_sun));
                            }
                            else
                            {
                                Debug.LogWarning("[RealBattery] Invalid orbit parameters for solar exposure calculation.");
                            }
                        }
                    }

                    double totalEfficiency = distanceEfficiency * exposureFactor;
                    output *= totalEfficiency;

                    Debug.Log($"[RealBattery] SolarPanel '{part.partInfo.title}' — Type: {type}, eff_dist={(distanceEfficiency * 100.0):F1}%, eff_sun={(exposureFactor * 100.0):F1}%, total_eff={(totalEfficiency * 100.0):F1}%");

                    partECproduced += output;
                    Debug.Log($"[RealBattery] SolarPanel '{part.partInfo.title}' — chargeRate: +{output:F3} EC/s");
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

                // Near Future Techs
                if (module.moduleName == "DischargeCapacitor")
                {
                    bool enabled = false;
                    bool discharging = false;
                    double dischargeRate = 0;
                    double chargeRate = 0;

                    try
                    {
                        bool.TryParse(module.Fields.GetValue("Enabled")?.ToString(), out enabled);
                        bool.TryParse(module.Fields.GetValue("IsDischarging")?.ToString(), out discharging);
                        double.TryParse(module.Fields.GetValue("DischargeRate")?.ToString(), out dischargeRate);
                        double.TryParse(module.Fields.GetValue("ChargeRate")?.ToString(), out chargeRate);
                    }
                    catch { }

                    if (enabled)
                    {
                        if (discharging && dischargeRate > 0)
                        {
                            partECproduced += dischargeRate;
                            Debug.Log($"[RealBattery] DischargeCapacitor '{part.partInfo.title}' — discharging +{dischargeRate:F3} EC/s");
                        }
                        else if (chargeRate > 0)
                        {
                            partECconsumed += chargeRate;
                            Debug.Log($"[RealBattery] DischargeCapacitor '{part.partInfo.title}' — charging -{chargeRate:F3} EC/s");
                        }
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

                /*/ Snacks!
                if (module.moduleName == "SnackProcessor")
                {
                    bool isActivated = false;
                    try
                    {
                        bool.TryParse(module.Fields.GetValue("IsActivated")?.ToString(), out isActivated);
                    }
                    catch { }

                    if (isActivated)
                    {
                        double factor = 1.0;
                        double baseBonus = 0, efficiency = 0;

                        if (module.Fields.GetValue("UseSpecialistBonus")?.ToString() == "True")
                        {
                            double.TryParse(module.Fields.GetValue("SpecialistBonus")?.ToString(), out baseBonus);
                            double.TryParse(module.Fields.GetValue("SpecialistEfficiencyFactor")?.ToString(), out efficiency);

                            factor = baseBonus + efficiency * (GetMaxSpecialistLevel(part.vessel, "Scientist")+1);
                        }

                        var config = part.partInfo?.partConfig;
                        if (config != null)
                        {
                            foreach (var modNode in config.GetNodes("MODULE"))
                            {
                                if (modNode.GetValue("name") == "SnackProcessor")
                                {
                                    foreach (var inputNode in modNode.GetNodes("INPUT_RESOURCE"))
                                    {
                                        if (inputNode.HasValue("ResourceName") && inputNode.GetValue("ResourceName") == "ElectricCharge" &&
                                            inputNode.HasValue("Ratio") && double.TryParse(inputNode.GetValue("Ratio"), out double ratio))
                                        {
                                            double rate = ratio * factor;
                                            partECconsumed += rate;
                                            Debug.Log($"[RealBattery] SnackProcessor '{part.partInfo.title}' — EC input: -{rate:F3} EC/s");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (module.moduleName == "SoilRecycler")
                {
                    bool isActivated = false;
                    try
                    {
                        bool.TryParse(module.Fields.GetValue("IsActivated")?.ToString(), out isActivated);
                    }
                    catch { }

                    if (isActivated)
                    {
                        double factor = 1.0;
                        double baseBonus = 0, efficiency = 0;

                        if (module.Fields.GetValue("UseSpecialistBonus")?.ToString() == "True")
                        {
                            double.TryParse(module.Fields.GetValue("SpecialistBonus")?.ToString(), out baseBonus);
                            double.TryParse(module.Fields.GetValue("SpecialistEfficiencyFactor")?.ToString(), out efficiency);

                            factor = baseBonus + efficiency * (GetMaxSpecialistLevel(part.vessel, "Engineer") + 1);
                        }

                        var config = part.partInfo?.partConfig;
                        if (config != null)
                        {
                            foreach (var modNode in config.GetNodes("MODULE"))
                            {
                                if (modNode.GetValue("name") == "SoilRecycler")
                                {
                                    foreach (var inputNode in modNode.GetNodes("INPUT_RESOURCE"))
                                    {
                                        if (inputNode.HasValue("ResourceName") && inputNode.GetValue("ResourceName") == "ElectricCharge" &&
                                            inputNode.HasValue("Ratio") && double.TryParse(inputNode.GetValue("Ratio"), out double ratio))
                                        {
                                            double rate = ratio * factor;
                                            partECconsumed += rate;
                                            Debug.Log($"[RealBattery] SoilRecycler '{part.partInfo.title}' — EC input: -{rate:F3} EC/s");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }*/
            }
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
