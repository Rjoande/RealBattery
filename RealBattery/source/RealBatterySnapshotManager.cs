using UnityEngine;
using KSP;
using System;
using System.Collections;

namespace RealBattery
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RealBatterySnapshotManager : MonoBehaviour
    {
        private Vessel lastActiveVessel;

        public void Awake()
        {
            // Eseguito prima di Start(), utile per ripristinare dati
            foreach (Vessel vessel in FlightGlobals.Vessels)
            {
                if (vessel != null && BackgroundSimulator.HasSnapshot(vessel.id))
                {
                    var snap = BackgroundSimulator.GetEnergySnapshot(vessel.id);
                    if (snap != null)
                    {
                        BackgroundSimulator.RestoreEnergySnapshot(snap);
                        Debug.Log($"[RealBattery][OnLoad] Restored snapshot: NetEC={snap.netECProductionRate:F3}");
                    }
                }
            }
        }

        public void Start()
        {
            GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitch);
            GameEvents.onVesselSwitching.Add(OnVesselSwitching);
            GameEvents.onVesselChange.Add(OnVesselChanged);
            GameEvents.onGameStateSave.Add(OnGameSave);

            StartCoroutine(DelayedApplyAllSnapshots());
            StartCoroutine(DelayedCaptureAll());

            Debug.Log("[RealBattery] Snapshot manager initialized.");
        }

        public void OnDestroy()
        {
            GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitch);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.onVesselChange.Remove(OnVesselChanged);
            GameEvents.onGameStateSave.Remove(OnGameSave);
        }
        private void OnGameSave(ConfigNode node)
        {
            foreach (var vessel in FlightGlobals.VesselsLoaded)
            {
                if (vessel != null)
                {
                    BackgroundSimulator.CaptureSnapshot(vessel);
                    Debug.Log($"[RealBattery] Snapshot captured on game save for vessel '{vessel.vesselName}'.");
                }
            }
        }

        // Cattura snapshot quando si lascia la scena di volo (es. verso Tracking Station)
        private void OnSceneSwitch(GameEvents.FromToAction<GameScenes, GameScenes> data)
        {
            if (data.from == GameScenes.FLIGHT)
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel != null)
                {
                    BackgroundSimulator.CaptureSnapshot(vessel);
                    Debug.Log($"[RealBattery] Snapshot captured on scene switch for vessel '{vessel.vesselName}'.");
                }
            }
        }

        // Cattura snapshot prima di cambiare nave (es. via "Passa a" o cambio fisico in volo)
        private void OnVesselSwitching(Vessel from, Vessel to)
        {
            if (from != null)
            {
                BackgroundSimulator.CaptureSnapshot(from);
                Debug.Log($"[RealBattery] Snapshot captured before vessel switch (from '{from.vesselName}').");
            }
        }

        // Dopo il cambio nave, ricordiamo la nuova attiva (per evitare doppio salvataggio)
        private void OnVesselChanged(Vessel newVessel)
        {
            lastActiveVessel = newVessel;
        }

        private IEnumerator DelayedApplyAllSnapshots()
        {
            yield return null;  // Frame 1
            yield return null;  // Frame 2 — assicura che i moduli siano inizializzati

            if (HighLogic.LoadedSceneIsFlight)
            {
                foreach (var vessel in FlightGlobals.VesselsLoaded)
                {
                    if (vessel != null && BackgroundSimulator.HasSnapshot(vessel.id))
                    {
                        Debug.Log($"[RealBattery] Applying snapshot to vessel '{vessel.vesselName}' after scene load...");
                        BackgroundSimulator.ApplySnapshot(vessel);
                        BackgroundSimulator.UpdateEnergySnapshot(vessel);  // opzionale ma consigliato
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

            yield return new WaitForSeconds(0.2f); // sicurezza extra

            BackgroundSimulator.CaptureSnapshot(vessel);
            Debug.Log($"[RealBattery] Delayed snapshot captured for vessel '{vessel.vesselName}' after unpack.");
        }
    }
}
