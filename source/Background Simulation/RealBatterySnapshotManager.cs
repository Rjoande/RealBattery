using UnityEngine;
using System.Collections;

namespace RealBattery
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RealBatterySnapshotManager : MonoBehaviour
    {
        private Vessel lastActiveVessel;

        public void Start()
        {
            GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitch);
            GameEvents.onVesselSwitching.Add(OnVesselSwitching);
            GameEvents.onVesselChange.Add(OnVesselChanged);
            GameEvents.onGameStateSave.Add(OnGameSave);
            GameEvents.onVesselCreate.Add(OnVesselCreate);

            StartCoroutine(DelayedApplyAllSnapshots());
            StartCoroutine(DelayedCaptureAll());
        }

        public void OnDestroy()
        {
            GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitch);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.onVesselChange.Remove(OnVesselChanged);
            GameEvents.onGameStateSave.Remove(OnGameSave);
            GameEvents.onVesselCreate.Remove(OnVesselCreate);
        }
        private void OnGameSave(ConfigNode node)
        {
            foreach (var vessel in FlightGlobals.VesselsLoaded)
            {
                if (vessel != null)
                    BackgroundSimulator.CaptureSnapshot(vessel);
            }
        }

        // Capture snapshots when leaving the flight scene (e.g. to Tracking Station)
        private void OnSceneSwitch(GameEvents.FromToAction<GameScenes, GameScenes> data)
        {
            if (data.from == GameScenes.FLIGHT)
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel != null)
                    BackgroundSimulator.CaptureSnapshot(vessel);
            }
        }

        // Take snapshots before switching ships (e.g. via "Switch To" or in-flight physics change)
        private void OnVesselSwitching(Vessel from, Vessel to)
        {
            if (from != null)
                BackgroundSimulator.CaptureSnapshot(from);
        }

        // After changing ships, remember the new one active (to avoid double saving)
        private void OnVesselChanged(Vessel newVessel)
        {
            lastActiveVessel = newVessel;
        }

        // A vessel created mid-flight (stage separation, undocking, EVA construction...) only
        // gets a snapshot from the triggers above once it's later switched away from, saved, or
        // actually goes off physics rails — and that last one can silently no-op:
        // BackgroundSimulator.CaptureSnapshot starts with "if (!vessel.loaded) return;", and KSP
        // notifies VesselModule.OnGoOffRails() only AFTER a vessel has already been unloaded, not
        // before. A piece that drifts out of physics range without an intervening scene/vessel
        // switch or save (e.g. a jettisoned stage nobody ever revisits) can end up with no RAM
        // snapshot and no ProtoVessel fallback (never saved either) — it just vanishes from the
        // fleet overview instead of degrading to a STALE estimate. Capturing here as soon as the
        // vessel exists guarantees a baseline while it's still unquestionably loaded, regardless
        // of whether OnGoOffRails ever fires as expected for it later.
        private void OnVesselCreate(Vessel vessel)
        {
            if (vessel != null)
                StartCoroutine(DelayedCaptureNewVessel(vessel));
        }

        private IEnumerator DelayedCaptureNewVessel(Vessel vessel)
        {
            // Same one-frame precaution DelayedApplyAllSnapshots already takes ("ensures that
            // modules are initialized") before reading battery state — a part's own RealBattery
            // module is present on this exact frame already (module lists don't change after a
            // part exists), but freshly-split values like lastECpower/StoredCharge.amount may
            // still reflect a not-yet-settled transitional state right at onVesselCreate.
            yield return null;

            if (vessel == null || !vessel.loaded) yield break;

            // Skip dead stages/debris with no RealBattery parts at all — no point capturing (or
            // even looking further into) a vessel this mod has nothing to say about.
            if (!HasRealBatteryParts(vessel)) yield break;

            BackgroundSimulator.CaptureSnapshot(vessel);
        }

        private static bool HasRealBatteryParts(Vessel vessel)
        {
            if (vessel?.parts == null) return false;
            foreach (Part part in vessel.parts)
                if (part.Modules.Contains("RealBattery")) return true;
            return false;
        }

        private IEnumerator DelayedApplyAllSnapshots()
        {
            yield return null;  // Frame 1
            yield return null;  // Frame 2 — ensures that modules are initialized

            if (HighLogic.LoadedSceneIsFlight)
            {
                foreach (var vessel in FlightGlobals.VesselsLoaded)
                {
                    if (vessel != null && BackgroundSimulator.HasSnapshot(vessel.id))
                    {
                        BackgroundSimulator.ApplySnapshot(vessel);
                        BackgroundSimulator.UpdateEnergySnapshot(vessel);  // optional but recommended
                    }
                }
            }
        }

        private IEnumerator DelayedCaptureAll()
        {
            yield return new WaitForSecondsRealtime(1.0f);

            if (HighLogic.LoadedSceneIsFlight)
            {
                foreach (var vessel in FlightGlobals.Vessels)
                {
                    if (vessel != null && vessel.loaded)
                        StartCoroutine(DelayedCaptureSingle(vessel));
                }
            }
        }

        private IEnumerator DelayedCaptureSingle(Vessel vessel)
        {
            while (vessel != null && (vessel.packed || !vessel.loaded))
                yield return null;

            yield return new WaitForSeconds(0.2f); // extra security

            BackgroundSimulator.CaptureSnapshot(vessel);
        }
    }
}
