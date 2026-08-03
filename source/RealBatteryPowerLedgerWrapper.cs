/*
    RealBatteryPowerLedgerWrapper.cs — copy-paste template for third-party mods.

    Talks to RealBatteryPowerLedger (RealBattery.dll) purely via reflection, so your mod never
    needs a hard/compile-time reference to RealBattery.dll. If RealBattery isn't installed,
    every method below returns a safe default (0.0) instead of throwing.

    Usage:
        1. Copy this file into your own mod's source tree (rename the namespace if you like).
        2. Call RealBatteryPowerLedgerWrapper.Init() once (e.g. in a MainMenu KSPAddon) before
           using anything else — Installed will tell you whether RealBattery is present.
        3. Call the methods below like a normal static API; they're no-ops if RealBattery isn't
           installed, so you don't need to guard every call site yourself.

    Mirrors the same "resolve everything by name via reflection, never a hard reference"
    philosophy already used by RealBattery's own KACWrapper.cs (source/Alarms/KACWrapper.cs)
    for its Kerbal Alarm Clock integration.

    Contract reference: RealBatteryPowerLedger.cs (source/Core/RealBatteryPowerLedger.cs) and
    RealBatteryPowerLedger.md (this folder) in the RealBattery repo. ContractVersion this
    wrapper targets: 1. All methods speak plain ElectricCharge (EC) units — RealBattery's
    internal StoredCharge resource and its EC<->SC ratio are not your concern.

    IMPORTANT — this is a REPORTING contract, not a command one: ReportConsumedEc tells
    RealBattery "I consumed this much energy", it does not ask RealBattery to hand energy over.
    Only call it for energy your own mod has already accounted for as spent. RealBattery is the
    sole authority on how that translates into StoredCharge drain, wear and BatteryLife.
*/
using System;
using System.Reflection;

namespace RealBattery
{
    public static class RealBatteryPowerLedgerWrapper
    {
        /// <summary>True once Init() has run and found RealBattery installed with a compatible contract.</summary>
        public static bool Installed { get; private set; }

        /// <summary>ContractVersion reported by the installed RealBattery, or 0 if not installed.</summary>
        public static int ContractVersion { get; private set; }

        private static Func<Vessel, double> _getDischargeableEcPerSec;
        private static Func<Vessel, double> _getAvailableEc;
        private static Func<Vessel, double, double, double> _reportConsumedEc;

        /// <summary>
        /// Resolves RealBatteryPowerLedger via reflection. Call once (e.g. from a
        /// MainMenu-startup KSPAddon); safe to call more than once. Never throws.
        /// </summary>
        public static bool Init()
        {
            Installed = false;
            ContractVersion = 0;

            try
            {
                Type ledgerType = null;
                foreach (var loaded in AssemblyLoader.loadedAssemblies)
                {
                    if (loaded.name != "RealBattery") continue;
                    ledgerType = loaded.assembly.GetType("RealBattery.RealBatteryPowerLedger");
                    break;
                }
                if (ledgerType == null) return false;

                FieldInfo versionField = ledgerType.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static);
                ContractVersion = versionField != null ? (int)versionField.GetValue(null) : 0;

                _getDischargeableEcPerSec = Bind<Func<Vessel, double>>(ledgerType, "GetDischargeableEcPerSec");
                _getAvailableEc = Bind<Func<Vessel, double>>(ledgerType, "GetAvailableEc");
                _reportConsumedEc = Bind<Func<Vessel, double, double, double>>(ledgerType, "ReportConsumedEc");

                Installed = _getDischargeableEcPerSec != null && _getAvailableEc != null && _reportConsumedEc != null;
            }
            catch
            {
                Installed = false;
            }

            return Installed;
        }

        public static double GetDischargeableEcPerSec(Vessel vessel) => Installed ? _getDischargeableEcPerSec(vessel) : 0.0;
        public static double GetAvailableEc(Vessel vessel) => Installed ? _getAvailableEc(vessel) : 0.0;
        public static double ReportConsumedEc(Vessel vessel, double ecConsumed, double deltaTimeSeconds) => Installed ? _reportConsumedEc(vessel, ecConsumed, deltaTimeSeconds) : 0.0;

        private static TDelegate Bind<TDelegate>(Type type, string methodName) where TDelegate : class
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            return method == null ? null : (TDelegate)(object)Delegate.CreateDelegate(typeof(TDelegate), method);
        }
    }
}
