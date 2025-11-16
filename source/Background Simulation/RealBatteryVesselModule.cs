using System;
using UnityEngine;
using KSP;

namespace RealBattery
{
    public class RealBatteryVesselModule : global::VesselModule
    {
        private bool snapshotLoaded = false;
        private VesselEnergySnapshot snapshotNode;

        // Called when loading vessel snapshot
        protected override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);

            if (snapshotLoaded)
            {
                Debug.Log($"[RealBattery] OnLoad skipped: snapshot already loaded for vessel '{vessel.vesselName}'");
                return;
            }

            if (node.HasNode("REALBATTERY_ENERGY"))
            {
                var n = node.GetNode("REALBATTERY_ENERGY");
                snapshotNode = new VesselEnergySnapshot
                {
                    vesselId = vessel.id,
                    timestamp = double.Parse(n.GetValue("timestamp")),
                    storedChargeAmount = double.Parse(n.GetValue("storedChargeAmount")),
                    storedChargeMaxAmount = double.Parse(n.GetValue("storedChargeMaxAmount")),
                    totalDischargeRate = double.Parse(n.GetValue("totalDischargeRate")),
                    netEC_Gross = double.Parse(n.GetValue("netEC_Gross")),
                    netEC_True = double.Parse(n.GetValue("netEC_True")),
                    solarECproduced = double.Parse(n.GetValue("solarECproduced")),
                    ExpUT = n.HasValue("ExpUT") ? double.Parse(n.GetValue("ExpUT")) : 0d,

                    mainBody = int.Parse(n.GetValue("mainBody")),
                    mainBodyName = (n.GetValue("mainBodyName")),
                    tToTransition = double.Parse(n.GetValue("tToTransition")),
                    period = double.Parse(n.GetValue("period")),
                    orbitalShadowFrac = double.Parse(n.GetValue("orbitalShadowFrac")),
                    isEscape = bool.Parse(n.GetValue("isEscape"))
                };

                if (node.HasValue("startPhase") && Enum.TryParse(node.GetValue("startPhase"), ignoreCase: true, out IllumPhase parsed))
                {
                    snapshotNode.startPhase = parsed;      // store back into the enum field
                }
                else
                {
                    snapshotNode.startPhase = IllumPhase.Sunlit; // safe default
                }

                snapshotLoaded = true; // <=== Impedisce doppio carico
                Debug.Log
                (
                    $"[RealBattery] OnLoad: restored snapshot for vessel '{vessel.vesselName}': " +
                    $"timestamp={snapshotNode.timestamp:F1}, " +
                    $"storedCharge={snapshotNode.storedChargeAmount:F3}/{snapshotNode.storedChargeMaxAmount:F3} kWh, " +
                    $"discharge={snapshotNode.totalDischargeRate:F3} EC/s, " +
                    $"netEC_Gross={snapshotNode.netEC_Gross:F3} EC/s, " +
                    $"netEC_True={snapshotNode.netEC_True:F3} EC/s" +
                    $"solarECproduced={snapshotNode.solarECproduced:F3} EC/s"
                );
                Debug.Log
                (
                    $"[RealBattery] OnSave: vessel '{vessel.vesselName}' was " +
                    $"{(vessel.LandedOrSplashed ? "landed on " : snapshotNode.isEscape ? "escaping from " : "orbiting ")}" +
                    $"{snapshotNode.mainBodyName}" +
                    $"{(!vessel.LandedOrSplashed && !snapshotNode.isEscape ? $" every {snapshotNode.period:F0}s" : string.Empty)}, " +
                    $"orbitalShadowFrac={(snapshotNode.orbitalShadowFrac * 100):F2}%, " +
                    $"startPhase={snapshotNode.startPhase}, " +
                    $"tToTransition={snapshotNode.tToTransition:F1}s."
                );
            }
        }

        private bool hasInitialized = false;

        protected override void OnStart()
        {
            base.OnStart();

            if (snapshotNode != null)
            {
                BackgroundSimulator.RestoreEnergySnapshot(snapshotNode);
                BackgroundSimulator.UpdateEnergySnapshot(vessel);
                Debug.Log($"[RealBattery] EnergySnapshot updated for vessel '{vessel.vesselName}' on scene load.");
            }

            // Wait a couple of frames before capturing the initial snapshot
            GameEvents.onVesselGoOffRails.Add(OnVesselReady);
        }

        private void OnVesselReady(Vessel v)
        {
            if (v == vessel && !hasInitialized)
            {
                hasInitialized = true;
                BackgroundSimulator.CaptureSnapshot(vessel);
                Debug.Log($"[RealBattery] Initial snapshot captured for vessel '{vessel.vesselName}' after physics start.");
                GameEvents.onVesselGoOffRails.Remove(OnVesselReady);
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            base.OnSave(node);

            // Called when saving vessel snapshot
            var snap = BackgroundSimulator.GetEnergySnapshot(vessel.id);
            if (snap == null)
            {
                Debug.Log($"[RealBattery] OnSave: no snapshot to save for vessel '{vessel.vesselName}'");
                return;
            }

            var n = node.AddNode("REALBATTERY_ENERGY");

            n.AddValue("timestamp", snap.timestamp);
            n.AddValue("storedChargeAmount", snap.storedChargeAmount);
            n.AddValue("storedChargeMaxAmount", snap.storedChargeMaxAmount);
            n.AddValue("totalDischargeRate", snap.totalDischargeRate);
            n.AddValue("netEC_Gross", snap.netEC_Gross);
            n.AddValue("netEC_True", snap.netEC_True);
            n.AddValue("solarECproduced", snap.solarECproduced);
            n.AddValue("ExpUT", snap.ExpUT);
            n.AddValue("mainBody", snap.mainBody);
            n.AddValue("mainBodyName", snap.mainBodyName);
            n.AddValue("startPhase", snap.startPhase.ToString());
            n.AddValue("tToTransition", snap.tToTransition);
            n.AddValue("period", snap.period);
            n.AddValue("orbitalShadowFrac", snap.orbitalShadowFrac);
            n.AddValue("isEscape", snap.isEscape);

            Debug.Log
            (
                $"[RealBattery] OnSave: saving snapshot for vessel '{vessel.vesselName}': " +
                $"StoredCharge={snap.storedChargeAmount:F3}/{snap.storedChargeMaxAmount:F3} kWh" +
                $"DischargeRate={snap.totalDischargeRate:F3} EC/s, " +
                $"netEC_Gross={snap.netEC_Gross:F3} EC/s, " +
                $"netEC_True={snap.netEC_True:F3} EC/s, " +
                $"solarECproduced={snap.solarECproduced:F3} EC/s, " +
                $"ExpUT={(snap.ExpUT > 0 ? snap.ExpUT.ToString("F0") : "-")}"
            );
            Debug.Log
            (
                $"[RealBattery] OnSave: vessel '{vessel.vesselName}' is " +
                $"{(vessel.LandedOrSplashed ? "landed on " : snap.isEscape ? "escaping from " : "orbiting ")}" + 
                $"{snap.mainBodyName}" +
                $"{(!vessel.LandedOrSplashed && !snap.isEscape ? $" every {snap.period:F0}s" : string.Empty)}, " +
                $"orbitalShadowFrac={(snap.orbitalShadowFrac*100):F2}%, " +
                $"startPhase={snap.startPhase}, " +
                $"tToTransition={snap.tToTransition:F1}s."
            );
        }
    }
}
