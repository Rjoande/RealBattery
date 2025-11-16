using KSP;
using KSP.Localization;
using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RealBattery
{
    // ---------- Minimal snapshot DTO we need for alarms/messages ----------
    internal class RBLiteSnapshot
    {
        public Guid VesselGuid;
        public uint PersistentId;
        public string VesselName;
        public double StoredChargeAmount; // kWh
        public double NetEC_True;         // EC/s (negative = draining)
        public double ExpUT;              // UT when warning should fire (0 = N/A)
        public bool FromProto;            // true if loaded from ProtoVessel node
    }

    // ---------- Logging helpers ----------
    internal static class RBAlarmUtil
    {
        public static void Dbg(string msg) => Debug.Log($"[RealBattery][Alarm][DBG] {msg}");
        public static void Log(string msg) => Debug.Log($"[RealBattery][Alarm] {msg}");
        public static void Warn(string msg) => Debug.LogWarning($"[RealBattery][Alarm][WARN] {msg}");
        public static void Err(string msg) => Debug.LogError($"[RealBattery][Alarm][ERR] {msg}");
    }

    // ---------- Session cache for proto-derived ExpUT (avoid drift/resets) ----------
    internal static class RBAlarmSessionCache
    {
        // key: vessel persistentId, val: computed ExpUT (proto-only)
        public static readonly Dictionary<uint, double> ProtoComputedExpUT = new Dictionary<uint, double>();
    }

    // ---------- Witty message helper (random localized body with fallback) ----------
    internal static class RBToastLib
    {
        private static readonly string[] Keys =
        {
        "#LOC_RB_DeplToastMsg_A",
        "#LOC_RB_DeplToastMsg_B",
        "#LOC_RB_DeplToastMsg_C",
        "#LOC_RB_DeplToastMsg_D",
        "#LOC_RB_DeplToastMsg_E",
    };

        /// <summary>Return a random witty localized body; fallback to default if missing.</summary>
        public static string FormatRandom(string vesselName)
        {
            try
            {
                int idx = UnityEngine.Random.Range(0, Keys.Length);
                string key = Keys[idx];
                string text = Localizer.Format(key, vesselName);
                // Fallback if the localization is missing or placeholder wasn't resolved
                if (string.IsNullOrEmpty(text) || text.Contains("<<1>>") || text == key)
                    return Localizer.Format("#LOC_RB_DeplToastMsg", vesselName);
                return text;
            }
            catch
            {
                return Localizer.Format("#LOC_RB_DeplToastMsg", vesselName);
            }
        }
    }

    // ======================================================================
    // ============== SHARED ONE-SHOT SYNC (used by SC and TS) ==============
    // ======================================================================
    internal static class RBAlarmSync
    {
        // Hysteresis for UT drift when deciding to update an existing alarm
        private static double UpdateTolerance => Math.Max(30.0, 0.10 * RealBatterySettings.LowPowerLeadSeconds);

        public static void SyncAllOnce(string contextTag)
        {
            if (!RealBatterySettings.UseLowPowerAlarm)
            {
                RBAlarmUtil.Dbg($"[{contextTag}] Low-power alarms disabled; skipping sync.");
                return;
            }

            double now = Planetarium.GetUniversalTime();
            int created = 0, updated = 0, deleted = 0, kept = 0, skipped = 0;

            RBAlarmUtil.Dbg($"[{contextTag}] Sync start @UT={now:F1}, vessels={FlightGlobals.Vessels?.Count ?? 0}");

            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v.vesselType == VesselType.EVA) { skipped++; continue; }

                var snap = GetBestEffortSnapshot(v);
                if (snap == null) { skipped++; continue; }

                // Retro-compat fill (with proto cache)
                ComputeExpUTIfMissing(snap);

                double exp = snap.ExpUT;
                bool hasExisting = HasAlarm(v.persistentId);

                if (exp > now)
                {
                    // Create only if (no existing) AND (>= MinWindow)
                    bool windowOKForCreation = (exp > now + RealBatterySettings.LowPowerMinWindowSeconds);

                    if (!hasExisting && windowOKForCreation)
                    {
                        CreateOrReplaceAlarm(v, exp);
                        created++;
                        continue;
                    }

                    if (hasExisting)
                    {
                        // Update only if drift is significant; otherwise keep
                        var backend = RBAlarmBackendProvider.Get();
                        if (backend.TryGetAlarmUT(v.persistentId, out var ut) && Math.Abs(ut - exp) > UpdateTolerance)
                        {
                            DeleteAlarm(v.persistentId);
                            CreateOrReplaceAlarm(v, exp);
                            updated++;
                            RBAlarmUtil.Dbg($"[Sync] Updated alarm for '{v.vesselName}' (Δ={Math.Abs(ut - exp):F1}s > tol={UpdateTolerance:F1}s).");
                        }
                        else kept++;
                        continue;
                    }

                    // Reaching here: below MinWindow and no existing → skip (do not create/cancel)
                    skipped++;
                }
                else
                {
                    // Expired/past or invalid → delete existing if any
                    if (hasExisting)
                    {
                        DeleteAlarm(v.persistentId);
                        deleted++;
                    }
                    else skipped++;
                }
            }

            RBAlarmUtil.Log($"[{contextTag}] Sync summary: created={created} updated={updated} deleted={deleted} kept={kept} skipped={skipped}");
        }

        // ---------------- helpers (static) ----------------

        // Backend abstraction replaces direct stock calls
        private static bool HasAlarm(uint vesselId) => RBAlarmBackendProvider.Get().HasAlarm(vesselId);

        private static void CreateOrReplaceAlarm(Vessel v, double expUT)
        {
            var backend = RBAlarmBackendProvider.Get();
            string title = Localizer.Format("#LOC_RB_DeplAlarmTitle", v.vesselName);
            string notes = RBToastLib.FormatRandom(v.vesselName);
            backend.CreateOrReplace(v.persistentId, v.vesselName, expUT, title, notes);
        }
        private static void DeleteAlarm(uint vesselId)
        {
    var backend = RBAlarmBackendProvider.Get();
    backend.Delete(vesselId);
            }

internal static RBLiteSnapshot GetBestEffortSnapshot(Vessel v)
        {
            // 1) Try RAM snapshot (BackgroundSimulator)
            var mem = BackgroundSimulator.GetEnergySnapshot(v.id);
            if (mem != null)
            {
                return new RBLiteSnapshot
                {
                    VesselGuid = v.id,
                    PersistentId = v.persistentId,
                    VesselName = v.vesselName,
                    StoredChargeAmount = mem.storedChargeAmount,
                    NetEC_True = mem.netEC_True,
                    ExpUT = mem.ExpUT,
                    FromProto = false
                };
            }
            // 2) Fallback: ProtoVessel serialization
            return TryReadSnapshotFromProtoVessel(v);
        }

        internal static RBLiteSnapshot TryReadSnapshotFromProtoVessel(Vessel v)
        {
            try
            {
                if (v?.protoVessel == null) return null;

                var vesselNode = new ConfigNode("VESSEL");
                v.protoVessel.Save(vesselNode); // official, stable API
                var modules = vesselNode.GetNodes("MODULE");

                ConfigNode rbVm = null;
                foreach (var m in modules)
                {
                    if (m.HasValue("name") && m.GetValue("name") == "RealBatteryVesselModule")
                    {
                        rbVm = m; break;
                    }
                }
                if (rbVm == null) { RBAlarmUtil.Dbg($"Proto ok but no MODULE RealBatteryVesselModule for '{v.vesselName}'."); return null; }

                var rb = rbVm.GetNode("REALBATTERY_ENERGY");
                if (rb == null) { RBAlarmUtil.Dbg($"MODULE found but no REALBATTERY_ENERGY node for '{v.vesselName}'."); return null; }

                double sc = SafeParse(rb, "storedChargeAmount", 0);
                double net = SafeParse(rb, "netEC_True", 0);
                double exp = SafeParse(rb, "ExpUT", 0);

                return new RBLiteSnapshot
                {
                    VesselGuid = v.id,
                    PersistentId = v.persistentId,
                    VesselName = v.vesselName,
                    StoredChargeAmount = sc,
                    NetEC_True = net,
                    ExpUT = exp,
                    FromProto = true
                };
            }
            catch (Exception ex)
            {
                RBAlarmUtil.Warn($"ProtoVessel read failed for '{v?.vesselName}': {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        internal static void ComputeExpUTIfMissing(RBLiteSnapshot snap)
        {
            if (snap.ExpUT > 0) return;
            if (snap.NetEC_True < -1e-6 && snap.StoredChargeAmount > 1e-9)
            {
                // Use cached value for proto to avoid drift in SC/TS one-shot
                if (snap.FromProto && RBAlarmSessionCache.ProtoComputedExpUT.TryGetValue(snap.PersistentId, out var cached))
                {
                    snap.ExpUT = cached;
                    RBAlarmUtil.Dbg($"Using cached ExpUT (proto) for '{snap.VesselName}' => {snap.ExpUT:F0}.");
                    return;
                }

                double secondsToEmpty = (snap.StoredChargeAmount * 3600.0) / Math.Abs(snap.NetEC_True);
                double lead = RealBatterySettings.LowPowerLeadSeconds;
                snap.ExpUT = Planetarium.GetUniversalTime() + Math.Max(0.0, secondsToEmpty - lead);
                RBAlarmUtil.Dbg($"Computed ExpUT for '{snap.VesselName}' => {snap.ExpUT:F0} (retro-compat).");

                if (snap.FromProto)
                {
                    RBAlarmSessionCache.ProtoComputedExpUT[snap.PersistentId] = snap.ExpUT;
                    RBAlarmUtil.Dbg($"Cached ExpUT for proto vessel pid={snap.PersistentId}.");
                }
            }
        }

        private static double SafeParse(ConfigNode n, string key, double defVal)
            => (n.HasValue(key) && double.TryParse(n.GetValue(key), out var v)) ? v : defVal;
    }

    // ======================================================================
    // =============================== AGENTS ================================
    // ======================================================================

    // Space Center agent: one-shot sync + debounced resync on vessel topology changes
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class RB_LowPowerAlarmAgent_SC : MonoBehaviour
    {
        private bool uiReady = false;
        private bool sceneSynced = false;   // do it once per scene
        private bool debouncePending = false;

        private void Start()
        {
            RBAlarmUtil.Log("SC agent Start()");
            // Subscribe to vessel topology changes that can happen while in SC
            GameEvents.onVesselRecovered.Add(OnVesselRecovered_SC);     // (ProtoVessel pv, bool quick)
            GameEvents.onVesselTerminated.Add(OnVesselTerminated_SC);   // (ProtoVessel pv)
            GameEvents.onVesselDestroy.Add(OnVesselDestroyed_SC);       // (Vessel v)
            StartCoroutine(WaitForAlarmClockThenSyncOnce());
        }

        private IEnumerator WaitForAlarmClockThenSyncOnce()
        {
            int safetyFrames = 120; // ~2 seconds @ 60 FPS
            while (safetyFrames-- > 0 && (AlarmClockScenario.Instance == null || AlarmClockScenario.Instance.alarms == null))
            {
                RBAlarmUtil.Dbg($"Waiting AlarmClock... frames left={safetyFrames}");
                yield return null;
            }

            uiReady = AlarmClockScenario.Instance != null && AlarmClockScenario.Instance.alarms != null;
            RBAlarmUtil.Log($"AlarmClock UI ready = {uiReady}");

            if (uiReady && !sceneSynced)
            {
                RBAlarmSync.SyncAllOnce("[SC initial]");
                sceneSynced = true;
                RBAlarmUtil.Log("One-shot sync completed in SpaceCenter. Standing by for debounced events.");
            }
            else RBAlarmUtil.Warn("AlarmClock not ready after wait; skipping initial SC sync.");

            yield break;
        }

        private void OnDestroy()
        {
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered_SC);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated_SC);
            GameEvents.onVesselDestroy.Remove(OnVesselDestroyed_SC);
        }

        // --- SC event handlers with correct signatures ---
        private void OnVesselRecovered_SC(ProtoVessel pv, bool quick)
            => ScheduleDebouncedResync($"Recovered pv='{pv?.vesselName}' quick={quick}");

        private void OnVesselTerminated_SC(ProtoVessel pv)
            => ScheduleDebouncedResync($"Terminated pv='{pv?.vesselName}'");

        private void OnVesselDestroyed_SC(Vessel v)
            => ScheduleDebouncedResync($"Destroyed v='{v?.vesselName}'");

        private void ScheduleDebouncedResync(string reason)
        {
            if (debouncePending) return;
            debouncePending = true;
            RBAlarmUtil.Dbg($"[SC] Vessel topology changed → {reason} → scheduling debounced resync.");
            StartCoroutine(DebouncedResync());
        }

        private IEnumerator DebouncedResync()
        {
            yield return new WaitForSecondsRealtime(1.0f); // debounce
            uiReady = AlarmClockScenario.Instance != null && AlarmClockScenario.Instance.alarms != null;
            if (uiReady)
            {
                RBAlarmSync.SyncAllOnce("[SC debounced]");
                RBAlarmUtil.Log("Resync done (debounced) in SpaceCenter.");
            }
            debouncePending = false;
        }
    }

    // Tracking Station agent: same pattern as SC
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class RB_LowPowerAlarmAgent_TS : MonoBehaviour
    {
        private bool uiReady = false;
        private bool sceneSynced = false;
        private bool debouncePending = false;

        private void Start()
        {
            RBAlarmUtil.Log("TS agent Start()");
            GameEvents.onVesselTerminated.Add(OnVesselTerminated_TS);  // (ProtoVessel pv)
            GameEvents.onVesselDestroy.Add(OnVesselDestroyed_TS);      // (Vessel v)
            StartCoroutine(WaitForAlarmClockThenSyncOnce());
        }

        private IEnumerator WaitForAlarmClockThenSyncOnce()
        {
            int safetyFrames = 120;
            while (safetyFrames-- > 0 && (AlarmClockScenario.Instance == null || AlarmClockScenario.Instance.alarms == null))
            {
                RBAlarmUtil.Dbg($"[TS] Waiting AlarmClock... frames left={safetyFrames}");
                yield return null;
            }
            uiReady = AlarmClockScenario.Instance != null && AlarmClockScenario.Instance.alarms != null;
            RBAlarmUtil.Log($"[TS] AlarmClock UI ready = {uiReady}");

            if (uiReady && !sceneSynced)
            {
                RBAlarmSync.SyncAllOnce("[TS initial]");
                sceneSynced = true;
                RBAlarmUtil.Log("One-shot sync completed in TrackingStation. Standing by for debounced events.");
            }

            yield break;
        }

        private void OnDestroy()
        {
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated_TS);
            GameEvents.onVesselDestroy.Remove(OnVesselDestroyed_TS);
        }

        // --- TS event handlers with correct signatures ---
        private void OnVesselTerminated_TS(ProtoVessel pv)
            => ScheduleDebouncedResyncTS($"Terminated pv='{pv?.vesselName}'");

        private void OnVesselDestroyed_TS(Vessel v)
            => ScheduleDebouncedResyncTS($"Destroyed v='{v?.vesselName}'");

        private void ScheduleDebouncedResyncTS(string reason)
        {
            if (debouncePending) return;
            debouncePending = true;
            RBAlarmUtil.Dbg($"[TS] Vessel topology changed → {reason} → scheduling debounced resync.");
            StartCoroutine(DebouncedResync());
        }

        private IEnumerator DebouncedResync()
        {
            yield return new WaitForSecondsRealtime(1.0f);
            uiReady = AlarmClockScenario.Instance != null && AlarmClockScenario.Instance.alarms != null;
            if (uiReady)
            {
                RBAlarmSync.SyncAllOnce("[TS debounced]");
                RBAlarmUtil.Log("[TS] Resync done (debounced).");
            }
            debouncePending = false;
        }
    }

    // Periodic toast messages in SC/TRACK/FLIGHT if depletion is imminent for off-scene vessels
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class RB_LowPowerMessageAgent : MonoBehaviour
    {
        private double nextTickRT = 0;
        // Session guard: show each vessel's toast at most once per session
        private static readonly HashSet<uint> ToastShown = new HashSet<uint>();

        private void Update()
        {
            //if (!RealBatterySettings.UseLowPowerMessage) return;

            // Pace the check in real time
            double leadSec = Math.Max(30.0, RealBatterySettings.LowPowerLeadSeconds);
            if (Time.realtimeSinceStartup < nextTickRT) return;
            nextTickRT = Time.realtimeSinceStartup + Math.Min(leadSec, 180.0);

            RBAlarmUtil.Dbg($"Toast tick. Next in ~{Math.Min(leadSec, 180.0):F0}s real-time.");

            double now = Planetarium.GetUniversalTime();
            int total = 0, activeSkipped = 0, noSnap = 0, protoUsed = 0, candidates = 0, shown = 0, notYet = 0, alreadyShown = 0;
            string scene = HighLogic.LoadedScene.ToString();

            foreach (var v in FlightGlobals.Vessels)
            {
                total++;
                if (v == null || v.vesselType == VesselType.EVA) continue;

                // Skip active vessel in Flight to avoid spam
                if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && v.id == FlightGlobals.ActiveVessel.id)
                {
                    activeSkipped++;
                    continue;
                }

                // Get snapshot (RAM or ProtoVessel)
                var lite = RBAlarmSync.GetBestEffortSnapshot(v);
                if (lite == null) { noSnap++; continue; }
                if (lite.FromProto) protoUsed++;

                // Retro-compat fill-in (may read from proto cache)
                if (lite.ExpUT <= 0) RBAlarmSync.ComputeExpUTIfMissing(lite);

                // Trigger policy: show when now >= ExpUT (pre-warning moment)
                if (lite.ExpUT > 0 && now >= lite.ExpUT)
                {
                    candidates++;
                    if (!ToastShown.Contains(v.persistentId))
                    {
                        string title = "RealBattery";
                        string body = RBToastLib.FormatRandom(v.vesselName);
                        var msg = new MessageSystem.Message(
                            title, body,
                            MessageSystemButton.MessageButtonColor.RED,
                            MessageSystemButton.ButtonIcons.ALERT);
                        MessageSystem.Instance.AddMessage(msg);
                        ToastShown.Add(v.persistentId);
                        shown++;
                        RBAlarmUtil.Log($"Toast shown for '{v.vesselName}' (ExpUT={lite.ExpUT:F0}, now={now:F0}).");
                    }
                    else alreadyShown++;
                }
                else notYet++;
            }

            // End-of-Update summary log for diagnostics
            RBAlarmUtil.Dbg(
                $"[Toast Summary] scene={scene} nowUT={now:F1} leadSec={leadSec:F0} " +
                $"vessels={total} activeSkipped={activeSkipped} noSnap={noSnap} protoUsed={protoUsed} " +
                $"candidates(now>=ExpUT)={candidates} shown={shown} alreadyShown={alreadyShown} notYet={notYet}");
        }
    }
}