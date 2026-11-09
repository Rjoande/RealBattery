using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RealBattery
{
    // Reflection bridge to KERBALISM.API (Kerbalism >= 3.x). Kerbalism keeps its own deferred,
    // vessel-wide resource cache and flags any producer that bypasses it on a loaded vessel
    // above 1000x warp ("incoherent behavior"), so with Kerbalism present every EC request of
    // RealBattery goes through this bridge instead of Part.RequestResource. All lookups are
    // resolved once; any missing member leaves the bridge inactive and RB on its stock path.
    internal static class KerbalismBridge
    {
        // Broker id shown in Kerbalism's resource tooltips. Kerbalism uses the string passed to
        // ProduceResource/ConsumeResource as both id and title, so keep it a stable plain name.
        public const string BrokerId = "RealBattery";

        private static bool _initialized;
        private static bool _active;

        private static Action<Vessel, string, double, string> _produce;
        private static Action<Vessel, string, double, string> _consume;
        private static Func<Vessel, string, double> _amount;
        private static Func<Vessel, string, double> _capacity;
        private static Func<Vessel, string, List<KeyValuePair<string[], double>>> _brokers;

        public static bool Active
        {
            get
            {
                if (!_initialized) Init();
                return _active;
            }
        }

        private static void Init()
        {
            _initialized = true;
            _active = false;

            if (!RealBatterySettings.KerbalismAvailable) return;

            try
            {
                Type api = Type.GetType("KERBALISM.API, Kerbalism", throwOnError: false);
                if (api == null)
                {
                    foreach (var la in AssemblyLoader.loadedAssemblies)
                    {
                        if (la.assembly == null) continue;
                        api = la.assembly.GetType("KERBALISM.API", false);
                        if (api != null) break;
                    }
                }
                if (api == null)
                {
                    RBLog.Warn("[KerbalismBridge] Kerbalism assembly found but KERBALISM.API type is missing; bridge disabled.");
                    return;
                }

                _produce  = Bind<Action<Vessel, string, double, string>>(api, "ProduceResource",
                    typeof(Vessel), typeof(string), typeof(double), typeof(string));
                _consume  = Bind<Action<Vessel, string, double, string>>(api, "ConsumeResource",
                    typeof(Vessel), typeof(string), typeof(double), typeof(string));
                _amount   = Bind<Func<Vessel, string, double>>(api, "ResourceAmount", typeof(Vessel), typeof(string));
                _capacity = Bind<Func<Vessel, string, double>>(api, "ResourceCapacity", typeof(Vessel), typeof(string));
                _brokers  = Bind<Func<Vessel, string, List<KeyValuePair<string[], double>>>>(api, "ResourceBrokers",
                    typeof(Vessel), typeof(string));

                _active = _produce != null && _consume != null && _amount != null && _capacity != null && _brokers != null;
                if (_active)
                    RBLog.Boot("[KerbalismBridge] Kerbalism detected: EC flows routed through KERBALISM.API, RealBattery background simulation delegated to Kerbalism.");
                else
                    RBLog.Warn("[KerbalismBridge] KERBALISM.API found but one or more resource methods are missing; bridge disabled.");
            }
            catch (Exception ex)
            {
                _active = false;
                RBLog.Warn($"[KerbalismBridge] Initialization failed: {ex.GetType().Name} {ex.Message}; bridge disabled.");
            }
        }

        private static T Bind<T>(Type api, string name, params Type[] args) where T : class
        {
            MethodInfo mi = api.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
            if (mi == null) return null;
            return Delegate.CreateDelegate(typeof(T), mi, false) as T;
        }

        public static void Produce(Vessel v, string resource, double quantity)
        {
            if (quantity > 0.0) _produce(v, resource, quantity, BrokerId);
        }

        public static void Consume(Vessel v, string resource, double quantity)
        {
            if (quantity > 0.0) _consume(v, resource, quantity, BrokerId);
        }

        public static double Amount(Vessel v, string resource) => _amount(v, resource);

        public static double Capacity(Vessel v, string resource) => _capacity(v, resource);

        // Net rate (units/s) of every Kerbalism broker on this resource except RealBattery's own,
        // as recorded by Kerbalism on its last step. Positive = others produce more than they use.
        // hasData is false when Kerbalism has no broker record yet for this vessel (first step
        // after a game load: the list is rebuilt at every Sync and never persisted).
        // Diagnostic dump of Kerbalism's broker list for a resource: "id:rate id:rate ...".
        public static string DescribeBrokers(Vessel v, string resource)
        {
            var list = _brokers(v, resource);
            if (list == null || list.Count == 0) return "none";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in list)
            {
                string id = kv.Key != null && kv.Key.Length >= 2 ? kv.Key[1] : "?";
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(id).Append(':').Append(kv.Value.ToString("+0.000;-0.000"));
            }
            return sb.ToString();
        }

        public static double NetRateOfOthers(Vessel v, string resource, out bool hasData)
        {
            double net = 0.0;
            hasData = false;
            var list = _brokers(v, resource);
            if (list == null) return 0.0;
            foreach (var kv in list)
            {
                hasData = true;
                string[] info = kv.Key;
                if (info != null && info.Length >= 2 && info[1] == BrokerId) continue;
                net += kv.Value;
            }
            return net;
        }
    }

    // Single entry point for RealBattery's ElectricCharge and RESOURCE_EXTRA requests on loaded
    // vessels. Without Kerbalism it is exactly Part.RequestResource. With Kerbalism the request
    // becomes a deferred Produce/Consume on the vessel cache, and the returned "accepted" amount
    // is clamped here against what the cache reports plus what RB already requested this physics
    // tick, so callers keep a meaningful return value (Kerbalism's API has none).
    // Sign convention is the stock one: positive = take from the vessel, negative = add to it.
    internal static class RBResources
    {
        private static double _ledgerTick = -1.0;
        private static readonly Dictionary<string, double> _ledger = new Dictionary<string, double>();

        // Net EC (units, this tick) that Kerbalism's other brokers are expected to add (+) or
        // remove (-) in the Sync that will also apply RB's request. Kerbalism clamps the summed
        // deferred total, so RB may legitimately produce beyond the current free room (or
        // consume beyond the current amount) by this much. Set per tick by RealBatteryLoadMaster.
        private static double _anticipatedTick = -1.0;
        private static Guid _anticipatedVessel;
        private static double _anticipatedNetEc;

        public static void SetAnticipatedNetEc(Vessel v, double netEcThisTick)
        {
            _anticipatedTick = Time.fixedTime;
            _anticipatedVessel = v.id;
            _anticipatedNetEc = netEcThisTick;
        }

        private static double AnticipatedNetEc(Vessel v, string resource)
        {
            if (resource != "ElectricCharge" || _anticipatedTick != Time.fixedTime || _anticipatedVessel != v.id) return 0.0;
            return _anticipatedNetEc;
        }

        public static double Request(Part part, int resourceId, double amount)
        {
            if (part == null || amount == 0.0) return 0.0;
            if (!KerbalismBridge.Active || part.vessel == null || !HighLogic.LoadedSceneIsFlight)
                return part.RequestResource(resourceId, amount);

            PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(resourceId);
            if (def == null) return part.RequestResource(resourceId, amount);
            return RequestViaKerbalism(part.vessel, def.name, amount);
        }

        public static double Request(Part part, string resourceName, double amount)
        {
            if (part == null || string.IsNullOrEmpty(resourceName) || amount == 0.0) return 0.0;
            if (!KerbalismBridge.Active || part.vessel == null || !HighLogic.LoadedSceneIsFlight)
                return part.RequestResource(resourceName, amount);
            return RequestViaKerbalism(part.vessel, resourceName, amount);
        }

        private static double RequestViaKerbalism(Vessel v, string resource, double amount)
        {
            // Time.fixedTime is constant across every FixedUpdate of one physics step.
            if (Time.fixedTime != _ledgerTick)
            {
                _ledgerTick = Time.fixedTime;
                _ledger.Clear();
            }

            string key = v.id.ToString("N") + "|" + resource;
            _ledger.TryGetValue(key, out double pendingNet); // + consumed, - produced so far this tick

            double anticipated = AnticipatedNetEc(v, resource);
            double granted;
            if (amount > 0.0)
            {
                double available = KerbalismBridge.Amount(v, resource) - pendingNet + Math.Max(0.0, anticipated);
                granted = Math.Max(0.0, Math.Min(amount, available));
                KerbalismBridge.Consume(v, resource, granted);
            }
            else
            {
                double room = KerbalismBridge.Capacity(v, resource) - KerbalismBridge.Amount(v, resource) + pendingNet
                            + Math.Max(0.0, -anticipated);
                double put = Math.Max(0.0, Math.Min(-amount, room));
                KerbalismBridge.Produce(v, resource, put);
                granted = -put;
            }

            _ledger[key] = pendingNet + granted;
            return granted;
        }
    }
}
