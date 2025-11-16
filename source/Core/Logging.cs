using UnityEngine;

namespace RealBattery
{
    /// <summary>
    /// Central logging facade for RealBattery.
    /// - Verbose() prints only if the in-game "verbose logs" setting is ON.
    /// - Boot() always prints (useful very early, before a game is loaded).
    /// </summary>
    public static class RBLog
    {
        /// <summary>True if the user enabled verbose logs in settings.</summary>
        public static bool VerboseEnabled
        {
            get
            {
                // Safe even when no game is loaded; returns false -> Verbose() stays quiet.
                var sim = HighLogic.CurrentGame?.Parameters?.CustomParams<RBParams_Debug>();
                return sim != null && sim.enableVerboseLoadLogs;
            }
        }

        /// <summary>Verbose log (guarded by the setting).</summary>
        public static void Verbose(string msg)
        {
            if (VerboseEnabled)
                Debug.Log($"[RealBattery][VERBOSE] {msg}");
        }

        /// <summary>Use for messages that should always appear.</summary>
        public static void Info(string msg)
            => Debug.Log($"[RealBattery] {msg}");

        public static void Warn(string msg)
            => Debug.LogWarning($"[RealBattery][WARN] {msg}");

        public static void Error(string msg)
            => Debug.LogError($"[RealBattery][ERR] {msg}");

        /// <summary>
        /// Always-on log for very early startup (e.g., database load) when no game is present yet.
        /// Use this sparingly.
        /// </summary>
        public static void Boot(string msg)
            => Debug.Log($"[RealBattery][BOOT] {msg}");
    }
}
