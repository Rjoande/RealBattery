using System;
using UnityEngine;

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
                return;

            if (node.HasNode("REALBATTERY_ENERGY"))
            {
                var n = node.GetNode("REALBATTERY_ENERGY");
                snapshotNode = new VesselEnergySnapshot { vesselId = vessel.id };

                // Culture-safe, non-throwing: a malformed/missing value leaves the field at its
                // C# default instead of taking down the whole VesselModule load.
                n.TryGetValue("timestamp", ref snapshotNode.timestamp);
                n.TryGetValue("storedChargeAmount", ref snapshotNode.storedChargeAmount);
                n.TryGetValue("storedChargeMaxAmount", ref snapshotNode.storedChargeMaxAmount);
                n.TryGetValue("totalDischargeRate", ref snapshotNode.totalDischargeRate);
                n.TryGetValue("netEC_Gross", ref snapshotNode.netEC_Gross);
                n.TryGetValue("netEC_True", ref snapshotNode.netEC_True);
                n.TryGetValue("solarECproduced", ref snapshotNode.solarECproduced);
                n.TryGetValue("ExpUT", ref snapshotNode.ExpUT);
                n.TryGetValue("mainBody", ref snapshotNode.mainBody);
                n.TryGetValue("mainBodyName", ref snapshotNode.mainBodyName);
                n.TryGetValue("tToTransition", ref snapshotNode.tToTransition);
                n.TryGetValue("period", ref snapshotNode.period);
                n.TryGetValue("orbitalShadowFrac", ref snapshotNode.orbitalShadowFrac);
                n.TryGetValue("isEscape", ref snapshotNode.isEscape);

                // startPhase lives in the REALBATTERY_ENERGY sub-node (n), not the outer VesselModule
                // node — it is written there by OnSave. Reading from the wrong node meant this always
                // fell back to the default.
                if (n.HasValue("startPhase") && Enum.TryParse(n.GetValue("startPhase"), ignoreCase: true, out IllumPhase parsed))
                {
                    snapshotNode.startPhase = parsed;      // store back into the enum field
                }
                else
                {
                    snapshotNode.startPhase = IllumPhase.Sunlit; // safe default
                }

                snapshotLoaded = true; // <=== Impedisce doppio carico

                if (RBLog.VerboseEnabled)
                    RBLog.Verbose(
                        $"[OnLoad] Restored snapshot for '{vessel.vesselName}': " +
                        $"timestamp={snapshotNode.timestamp:F1}, " +
                        $"storedCharge={snapshotNode.storedChargeAmount:F3}/{snapshotNode.storedChargeMaxAmount:F3} kWh, " +
                        $"netEC_Gross={snapshotNode.netEC_Gross:F3} EC/s, netEC_True={snapshotNode.netEC_True:F3} EC/s, " +
                        $"{(vessel.LandedOrSplashed ? "landed on " : snapshotNode.isEscape ? "escaping from " : "orbiting ")}{snapshotNode.mainBodyName}, " +
                        $"startPhase={snapshotNode.startPhase}, tToTransition={snapshotNode.tToTransition:F1}s"
                    );
            }
        }

        private bool hasInitialized = false;

        protected override void OnStart()
        {
            base.OnStart();

            if (snapshotNode != null)
            {
                // Restore only — do NOT also call UpdateEnergySnapshot here. ApplySnapshot (run
                // shortly after via RealBatterySnapshotManager.DelayedApplyAllSnapshots, which
                // mutates the REAL StoredCharge resources) needs the full elapsed deltaTime since
                // this snapshot's timestamp; consuming part of that interval here first would
                // shrink what ApplySnapshot sees. Vessels that go off-rails get a fully fresh
                // snapshot from CaptureSnapshot anyway (see OnGoOffRails below); vessels that
                // stay packed this session simply keep this restored (self-consistent) snapshot
                // until they do.
                BackgroundSimulator.RestoreEnergySnapshot(snapshotNode);
            }
        }

        // Native per-instance lifecycle hook instead of a GameEvents subscription: KSP creates
        // one RealBatteryVesselModule per vessel in the save on every scene load, but only
        // vessels that actually go off-rails this session would ever unsubscribe from a
        // GameEvents.onVesselGoOffRails.Add — the rest leaked a dead delegate in that static
        // list until the next scene change swept them (KSPCommunityFixes' MemoryLeaks patch
        // logged "Removed a onVesselGoOffRails callback..." for each one). OnGoOffRails() is
        // called directly on this instance by KSP, so there is nothing to subscribe to or
        // unsubscribe from.
        public override void OnGoOffRails()
        {
            base.OnGoOffRails();

            if (hasInitialized) return;
            hasInitialized = true;
            BackgroundSimulator.CaptureSnapshot(vessel);
        }

        protected override void OnSave(ConfigNode node)
        {
            base.OnSave(node);

            // Called when saving vessel snapshot
            var snap = BackgroundSimulator.GetEnergySnapshot(vessel.id);
            if (snap == null) return;

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

            if (RBLog.VerboseEnabled)
                RBLog.Verbose(
                    $"[OnSave] '{vessel.vesselName}': StoredCharge={snap.storedChargeAmount:F3}/{snap.storedChargeMaxAmount:F3} kWh, " +
                    $"netEC_Gross={snap.netEC_Gross:F3} EC/s, netEC_True={snap.netEC_True:F3} EC/s, " +
                    $"{(vessel.LandedOrSplashed ? "landed on " : snap.isEscape ? "escaping from " : "orbiting ")}{snap.mainBodyName}, " +
                    $"startPhase={snap.startPhase}, tToTransition={snap.tToTransition:F1}s, " +
                    $"ExpUT={(snap.ExpUT > 0 ? snap.ExpUT.ToString("F0") : "-")}"
                );
        }
    }
}
