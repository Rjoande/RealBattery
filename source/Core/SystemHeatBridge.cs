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
        private static FieldInfo _moduleUsedField;

        // Per-module BaseField cache for currentLoopTemperature, keyed by PartModule instance.
        // ConditionalWeakTable ties entry lifetime to the key (the SystemHeat module) so entries
        // for destroyed parts are collected automatically — no manual cache invalidation needed.
        private static readonly ConditionalWeakTable<PartModule, BaseField> _loopTempFieldCache =
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
                // moduleUsed is a plain public field on ModuleSystemHeat (not a KSPField, not a
                // property) — GetField, not GetProperty. Using GetProperty here always returned
                // null, silently no-opping MarkUsed()/AddFlux()'s "mark used" side effect.
                _moduleUsedField = _moduleType.GetField("moduleUsed", BindingFlags.Instance | BindingFlags.Public);
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

        public static void MarkUsed(PartModule sh)
        {
            if (sh == null) return;

            EnsureCache();
            if (_moduleUsedField == null) return;

            try
            {
                _moduleUsedField.SetValue(sh, true);
            }
            catch
            {
                // Never throw.
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
                // Mark module used if possible.
                _moduleUsedField?.SetValue(sh, true);

                _addFluxMethod.Invoke(sh, new object[] { source, targetK, fluxW, useForNominal });
            }
            catch
            {
                // Never throw.
            }
        }
    }
}
