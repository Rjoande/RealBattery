using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RealBattery
{
    internal static class SystemHeatBridge
    {
        // Cache reflected members to avoid repeated lookup costs.
        private static bool _cached;
        private static Type _moduleType;
        private static MethodInfo _addFluxMethod;
        private static MethodInfo _setEnabledMethod;
        private static FieldInfo _moduleIdField;
        private static PropertyInfo _loopProp;
        private static PropertyInfo _loopConvFluxProp;
        private static PropertyInfo _loopConvTempProp;

        // moduleID of the ModuleSystemHeat that RealBattery's own patch adds to battery parts.
        internal const string ModuleId = "ModuleSystemHeatRealBattery";

        // Per-module BaseField cache for currentLoopTemperature, keyed by PartModule instance.
        // ConditionalWeakTable ties entry lifetime to the key (the SystemHeat module) so entries
        // for destroyed parts are collected automatically — no manual cache invalidation needed.
        private static readonly ConditionalWeakTable<PartModule, BaseField> _loopTempFieldCache =
            new ConditionalWeakTable<PartModule, BaseField>();

        // Same cache for nominalLoopTemperature.
        private static readonly ConditionalWeakTable<PartModule, BaseField> _loopNominalFieldCache =
            new ConditionalWeakTable<PartModule, BaseField>();

        public static bool Available => RealBatterySettings.SystemHeatAvailable; // already guarded by the AssemblyLoader check there

        private static void EnsureCache()
        {
            if (_cached) return;
            _cached = true;

            try
            {
                // Type name used by SystemHeat module on parts.
                _moduleType = Type.GetType("SystemHeat.ModuleSystemHeat, SystemHeat", throwOnError: false);

                if (_moduleType == null) return;

                _addFluxMethod = _moduleType.GetMethod(
                    "AddFlux",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(float), typeof(float), typeof(bool) },
                    modifiers: null
                );
                _setEnabledMethod = _moduleType.GetMethod(
                    "SetSystemHeatModuleEnabled",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(bool) },
                    modifiers: null
                );
                _moduleIdField = _moduleType.GetField("moduleID", BindingFlags.Instance | BindingFlags.Public);

                // HeatLoop exposes the convection terms SystemHeat's own radiators use.
                _loopProp = _moduleType.GetProperty("Loop", BindingFlags.Instance | BindingFlags.Public);
                Type loopType = _loopProp?.PropertyType;
                _loopConvFluxProp = loopType?.GetProperty("ConvectionFlux", BindingFlags.Instance | BindingFlags.Public);
                _loopConvTempProp = loopType?.GetProperty("ConvectionTemperature", BindingFlags.Instance | BindingFlags.Public);
            }
            catch
            {
                // Swallow: bridge must never break RB load.
                _moduleType = null;
            }
        }

        // Returns RealBattery's own ModuleSystemHeat on the part (matched by moduleID), so a
        // heat module that another mod put on the same part is never driven or switched off by
        // mistake. Falls back to the first ModuleSystemHeat for parts configured without our ID.
        public static PartModule GetModule(Part part)
        {
            if (part == null) return null;
            if (!Available) return null;

            EnsureCache();
            if (_moduleType == null) return null;

            PartModule first = null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule pm = part.Modules[i];
                if (pm == null || pm.moduleName != "ModuleSystemHeat") continue;
                if (first == null) first = pm;
                if (_moduleIdField != null && (_moduleIdField.GetValue(pm) as string) == ModuleId)
                    return pm;
            }
            return first;
        }

        private static BaseField ResolveLoopTempField(PartModule sh) =>
            sh.Fields?["currentLoopTemperature"] ?? sh.Fields?["loopTemperature"];

        public static bool TryGetLoopTempK(PartModule sh, out float tempK)
        {
            tempK = 0f;
            if (sh == null) return false;

            BaseField f = _loopTempFieldCache.GetValue(sh, ResolveLoopTempField);
            if (f == null) return false;

            var v = f.GetValue(sh);
            if (v == null) return false;
            tempK = Convert.ToSingle(v);
            return true;
        }

        private static BaseField ResolveLoopNominalField(PartModule sh) =>
            sh.Fields?["nominalLoopTemperature"];

        // Nominal (target) temperature of the loop the module is on.
        public static bool TryGetLoopNominalK(PartModule sh, out float nominalK)
        {
            nominalK = 0f;
            if (sh == null) return false;

            BaseField f = _loopNominalFieldCache.GetValue(sh, ResolveLoopNominalField);
            if (f == null) return false;

            var v = f.GetValue(sh);
            if (v == null) return false;
            nominalK = Convert.ToSingle(v);
            return true;
        }

        // Adds the module to (or removes it from) SystemHeat's simulation through SystemHeat's
        // own switch, which also shows/hides the module's PAW group. Returns false when the call
        // could not be made, so the caller can retry on a later tick.
        public static bool SetEnabled(PartModule sh, bool enabled)
        {
            if (sh == null) return false;

            EnsureCache();
            if (_setEnabledMethod == null) return false;

            try
            {
                _setEnabledMethod.Invoke(sh, new object[] { enabled });
                return true;
            }
            catch
            {
                // Never throw (e.g. SystemHeat's simulator not available yet).
                return false;
            }
        }

        // Reads the loop's convection terms: convFlux is SystemHeat's convective coefficient
        // (kW per K per m2 of convective area, before the part and area factors) and ambientK the
        // external temperature (space temperature in vacuum). False when the loop is not ready.
        public static bool TryGetLoopConvection(PartModule sh, out float convFlux, out float ambientK)
        {
            convFlux = 0f;
            ambientK = 0f;
            if (sh == null) return false;

            EnsureCache();
            if (_loopProp == null || _loopConvFluxProp == null || _loopConvTempProp == null) return false;

            try
            {
                object loop = _loopProp.GetValue(sh, null);
                if (loop == null) return false;
                convFlux = (float)_loopConvFluxProp.GetValue(loop, null);
                ambientK = (float)_loopConvTempProp.GetValue(loop, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // useForNominal: SystemHeat's actual parameter name
        // (AddFlux(string id, float sourceTemperature, float flux, bool useForNominal)). The flux
        // is REPLACED per source id on every call, not summed — useForNominal only controls
        // whether this source's temperature contributes to the loop's nominal-temperature average.
        public static void AddFlux(PartModule sh, string source, float targetK, float fluxW, bool useForNominal)
        {
            if (sh == null) return;

            EnsureCache();
            if (_addFluxMethod == null) return;

            try
            {
                _addFluxMethod.Invoke(sh, new object[] { source, targetK, fluxW, useForNominal });
            }
            catch
            {
                // Never throw.
            }
        }
    }
}
