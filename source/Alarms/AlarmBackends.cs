// File: RBAlarmBackends.cs
// Namespace: RealBattery
// Comments in English as requested.

using Expansions.Missions;
using KSP.Localization;
using RealBattery;
using System;
using System.Linq;
using System.Security.Policy;

namespace RealBattery
{
    internal interface IRBAlarmBackend
    {
        bool IsReady { get; }
        void InitIfNeeded();
        bool HasAlarm(uint vesselId);
        bool TryGetAlarmUT(uint vesselId, out double ut);
        void CreateOrReplace(uint vesselId, string vesselName, double expUT, string title, string notes);
        void Delete(uint vesselId);
    }

    internal static class RBAlarmBackendProvider
    {
        private static IRBAlarmBackend impl;
        private static bool isKACActive;
        private const string TagPrefix = "[RealBattery][LowPower]";

        public static IRBAlarmBackend Get()
        {
            // Decide desired backend *now*, based on current settings & availability
            bool kacInstalled = RealBatterySettings.KACIsInstalled();
            bool wantKAC = RealBatterySettings.UseLowPowerAlarm && RealBatterySettings.UseKAC && kacInstalled;
            
            // If we have an impl already but the "kind" changed, switch (with cleanup)
            if (impl != null && isKACActive != wantKAC)
            {
                RBAlarmUtil.Log($"[Backend] Switching backend → {(wantKAC ? "KAC" : "Stock")}");
                // Cleanup alarms from the backend we are leaving
                try
                {
                    if (wantKAC)
                        CleanupStockAlarms(); // going to KAC → remove our stock alarms
                    else
                        CleanupKacAlarms();   // going to Stock → remove our KAC alarms
                }
                catch (Exception ex)
                {
                    RBAlarmUtil.Warn($"[Backend] Cleanup during switch failed: {ex.GetType().Name} {ex.Message}");
                }
                impl = null; // force re-create below
            }
                
            if (impl == null)
            {
                if (wantKAC)
                {
                    var kac = new KacAlarmBackend();
                    kac.InitIfNeeded();
                    if (kac.IsReady)
                    {
                        impl = kac;
                        isKACActive = true;
                        RBAlarmUtil.Dbg("[Backend] Using KAC backend.");
                    }
                    else
                    {
                        RBAlarmUtil.Warn("[KAC] Wrapper not ready; falling back to Stock Alarm Clock.");
                    }
                }
                    
                if (impl == null)
                {
                    var stock = new StockAlarmBackend();
                    stock.InitIfNeeded();
                    impl = stock;
                    isKACActive = false;
                    RBAlarmUtil.Dbg("[Backend] Using Stock backend.");
                    // If we *wanted* KAC but it's not ready, also ensure any stale KAC alarms are removed
                    if (wantKAC) { try { CleanupKacAlarms(); } catch { /* ignore */ } }
                }
            }
            return impl;
        }

        // --- Cleanup helpers to keep only the selected backend's alarms alive ---
        private static void CleanupStockAlarms()
        {
            if (AlarmClockScenario.Instance?.alarms?.Values == null) return;
            int removed = 0;
            // Copy to array to avoid modifying during enumeration (safety)
            var list = AlarmClockScenario.Instance.alarms.Values.ToArray();
            foreach (var obj in list)
            {
                var al = obj as AlarmTypeRaw;
                if (al?.description != null && al.description.StartsWith(TagPrefix))
                {
                    AlarmClockScenario.DeleteAlarm(al);
                    removed++;
                }
            }
            if (removed > 0) RBAlarmUtil.Log($"[Backend] Cleaned {removed} stock alarm(s) tagged by RealBattery.");
        }

        private static void CleanupKacAlarms()
        {
            try
            {
                KACWrapper.KACWrapper.InitKACWrapper();
                if (!KACWrapper.KACWrapper.APIReady) return;
                int removed = 0;
                // Snapshot list to avoid collection mutation issues
                var alarms = KACWrapper.KACWrapper.KAC.Alarms.ToArray();
                foreach (var a in alarms)
                {
                    if (!string.IsNullOrEmpty(a.Notes) && a.Notes.StartsWith(TagPrefix))
                {
                    KACWrapper.KACWrapper.KAC.DeleteAlarm(a.ID);
                    removed++;
                }
            }
            if (removed > 0) RBAlarmUtil.Log($"[Backend] Cleaned {removed} KAC alarm(s) tagged by RealBattery.");
        }
        catch
        {
            // Swallow any exception here; cleanup is best-effort.
        }
    }
}

    // ---------------- Stock backend (uses AlarmClockScenario) ----------------

    internal class StockAlarmBackend : IRBAlarmBackend
    {
        public bool IsReady { get; private set; }

        public void InitIfNeeded()
        {
            IsReady = AlarmClockScenario.Instance != null && AlarmClockScenario.Instance.alarms != null;
            RBAlarmUtil.Dbg($"[Stock] Init IsReady={IsReady}");
        }

         private string MakeTag(uint vesselId) => $"[RealBattery][LowPower] vesselId={vesselId}";

        // Find the actual alarm instance matching our tag (if any)
        private AlarmTypeRaw FindAlarm(uint vesselId)
        {
            if (!IsReady) return null;
            var vals = AlarmClockScenario.Instance.alarms?.Values;
            if (vals == null) return null;
            for (int i = 0; i<vals.Count; i++)
            {
                var al = vals.ElementAt(i) as AlarmTypeRaw;
                if (al == null) continue;
                if (al.vesselId != vesselId) continue;
                if (al.description != null && al.description.StartsWith(MakeTag(vesselId)))
                    return al;
            }
            return null;
        }

        public bool HasAlarm(uint vesselId)
        {
            return FindAlarm(vesselId) != null;
        }

        public bool TryGetAlarmUT(uint vesselId, out double ut)
        {
            ut = 0;
            var al = FindAlarm(vesselId);
            if (al == null) return false;
            ut = al.ut;
            return true;
        }

        public void CreateOrReplace(uint vesselId, string vesselName, double expUT, string title, string notes)
        {
            if (!IsReady) return;

            // Delete previous (by instance)
            var prev = FindAlarm(vesselId);
            if (prev != null) AlarmClockScenario.DeleteAlarm(prev);

            // Make description with technical tag + witty line
            string desc = $"{MakeTag(vesselId)}";

            var alarm = new AlarmTypeRaw
            {
                title = title,
                description = desc,
                actions =
                {
                    warp = AlarmActions.WarpEnum.KillWarp,
                    message = AlarmActions.MessageEnum.Yes,
                    deleteWhenDone = true
                },
                ut = expUT,
                vesselId = vesselId
            };
            AlarmClockScenario.AddAlarm(alarm);
            RBAlarmUtil.Log($"[Stock] Alarm set for '{vesselName}' @UT={expUT:F0}");
        }

        public void Delete(uint vesselId)
        {
            if (!IsReady) return;
            var al = FindAlarm(vesselId);
            if (al != null)
            {
                AlarmClockScenario.DeleteAlarm(al);
                RBAlarmUtil.Dbg($"[Stock] Deleted alarm for pid={vesselId}");
            }
        }
    }

    // ---------------- KAC backend (uses KACWrapper) ----------------
    // Requires KACWrapper.cs from the KAC API docs with namespace updated to RealBattery.KACWrap
    // Docs: https://triggerau.github.io/KerbalAlarmClock/api.html  (Wrapper + APIReady + Create/Delete) 
    internal class KacAlarmBackend : IRBAlarmBackend
    {
        public bool IsReady { get; private set; }

        public void InitIfNeeded()
        {
            KACWrapper.KACWrapper.InitKACWrapper(); // per docs: call after Awake
            IsReady = KACWrapper.KACWrapper.APIReady;
            RBAlarmUtil.Log($"[KAC] Wrapper init, APIReady={IsReady}");
        }

        private string MakeTag(uint vesselId) => $"[RealBattery][LowPower] vesselId={vesselId}";

        private KACWrapper.KACWrapper.KACAPI.KACAlarm FindAlarm(uint vesselId)
        {
            if (!IsReady) return null;
            foreach (var a in KACWrapper.KACWrapper.KAC.Alarms)
            {
                if (a.Notes != null && a.Notes.StartsWith(MakeTag(vesselId)))
                    return a;
            }
            return null;
        }

        public bool HasAlarm(uint vesselId) => FindAlarm(vesselId) != null;

        public bool TryGetAlarmUT(uint vesselId, out double ut)
        {
            ut = 0;
            var a = FindAlarm(vesselId);
            if (a == null) return false;
            ut = a.AlarmTime;
            return true;
        }

        public void CreateOrReplace(uint vesselId, string vesselName, double expUT, string title, string notes)
        {
            if (!IsReady) return;

            // Delete existing if present
            var prev = FindAlarm(vesselId);
            if (prev != null) KACWrapper.KACWrapper.KAC.DeleteAlarm(prev.ID);

            // Create raw alarm at ExpUT (lead is already applied on our side)
            var id = KACWrapper.KACWrapper.KAC.CreateAlarm(
                KACWrapper.KACWrapper.KACAPI.AlarmTypeEnum.Raw,
                title,
                expUT
            );

            if (!string.IsNullOrEmpty(id))
            {
                var alarm = KACWrapper.KACWrapper.KAC.Alarms.First(z => z.ID == id);
                alarm.Notes = $"{MakeTag(vesselId)}";
                alarm.VesselID = vesselId.ToString();
                alarm.AlarmAction = KACWrapper.KACWrapper.KACAPI.AlarmActionEnum.KillWarp;
                alarm.AlarmMargin = 0;
                RBAlarmUtil.Log($"[KAC] Alarm set for '{vesselName}' @UT={expUT:F0}");
            }
        }

        public void Delete(uint vesselId)
        {
            if (!IsReady) return;
            var a = FindAlarm(vesselId);
            if (a != null)
            {
                KACWrapper.KACWrapper.KAC.DeleteAlarm(a.ID);
                RBAlarmUtil.Dbg($"[KAC] Deleted alarm for pid={vesselId}");
            }

        }
    }
}
