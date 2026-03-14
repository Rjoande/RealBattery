using System;
using System.Reflection;

namespace RealBattery
{
    internal static class SystemHeatBridge
    {
        // Cache reflected members to avoid repeated lookup costs.
        private static bool _cached;
        private static Type _moduleType;
        private static PropertyInfo _loopTempProp;
        private static MethodInfo _addFluxMethod;
        private static PropertyInfo _moduleUsedProp;

        public static bool Available => RealBatterySettings.SystemHeatAvailable; // already guarded :contentReference[oaicite:3]{index=3}

        private static void EnsureCache()
        {
            if (_cached) return;
            _cached = true;

            try
            {
                // Type name used by SystemHeat module on parts.
                _moduleType = Type.GetType("SystemHeat.ModuleSystemHeat, SystemHeat", throwOnError: false);

                if (_moduleType == null) return;

                _loopTempProp = _moduleType.GetProperty("currentLoopTemperature", BindingFlags.Instance | BindingFlags.Public);
                _addFluxMethod = _moduleType.GetMethod(
                    "AddFlux",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(float), typeof(float), typeof(bool) },
                    modifiers: null
                );
                _moduleUsedProp = _moduleType.GetProperty("moduleUsed", BindingFlags.Instance | BindingFlags.Public);
            }
            catch
            {
                // Swallow: bridge must never break RB load.
                _moduleType = null;
            }
        }

        // Returns the PartModule instance if present on the part.
        public static PartModule GetModule(Part part)
        {
            if (part == null) return null;
            if (!Available) return null;

            EnsureCache();
            if (_moduleType == null) return null;

            // KSP provides a string lookup that doesn't require the type.
            return part.Modules.GetModule("ModuleSystemHeat");
        }

        public static bool TryGetLoopTempK(PartModule sh, out float tempK)
        {
            tempK = 0f;
            if (sh == null) return false;

            EnsureCache();
            if (_loopTempProp == null) return false;

            try
            {
                object v = _loopTempProp.GetValue(sh, null);
                if (v == null) return false;
                tempK = Convert.ToSingle(v);
                return true;
            }
            catch { return false; }
        }

        public static void MarkUsed(PartModule sh)
        {
            if (sh == null) return;

            EnsureCache();
            if (_moduleUsedProp == null) return;

            try
            {
                _moduleUsedProp.SetValue(sh, true, null);
            }
            catch
            {
                // Never throw.
            }
        }

        public static void AddFlux(PartModule sh, string source, float targetK, float fluxW, bool additive)
        {
            if (sh == null) return;

            EnsureCache();
            if (_addFluxMethod == null) return;

            try
            {
                // Mark module used if possible.
                if (_moduleUsedProp != null) _moduleUsedProp.SetValue(sh, true, null);

                _addFluxMethod.Invoke(sh, new object[] { source, targetK, fluxW, additive });
            }
            catch
            {
                // Never throw.
            }
        }
    }
}