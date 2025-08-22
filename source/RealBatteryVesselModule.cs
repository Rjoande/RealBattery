using System;
using UnityEngine;
using KSP;

namespace RealBattery
{
    public class RealBatteryVesselModule : global::VesselModule
    {
        private bool snapshotLoaded = false;
        private VesselEnergySnapshot snapshotNode;

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
                    netECProductionRate = double.Parse(n.GetValue("netECProductionRate"))
                };

                snapshotLoaded = true; // <=== Impedisce doppio carico
                Debug.Log($"[RealBattery] OnLoad: restored snapshot for vessel '{vessel.vesselName}': timestamp={snapshotNode.timestamp:F1}, storedCharge={snapshotNode.storedChargeAmount:F3}/{snapshotNode.storedChargeMaxAmount:F3} kWh, discharge={snapshotNode.totalDischargeRate:F3} EC/s, netEC={snapshotNode.netECProductionRate:F3} EC/s");
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
                //BackgroundSimulator.ApplySnapshot(vessel);
                Debug.Log($"[RealBattery] EnergySnapshot updated for vessel '{vessel.vesselName}' on scene load.");
            }

            // ❗ Aspetta un paio di frame prima di catturare snapshot iniziale
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

            // Solo se lo snapshot esiste, lo salviamo — altrimenti evitiamo di sovrascrivere
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
            n.AddValue("netECProductionRate", snap.netECProductionRate);

            Debug.Log($"[RealBattery] OnSave: saving snapshot with DischargeRate={snap.totalDischargeRate:F3} EC/s");
        }
    }
}
