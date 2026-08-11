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
    wrapper targets: 3. All methods speak plain ElectricCharge (EC) units — RealBattery's
    internal StoredCharge resource and its EC<->SC ratio are not your concern.

    The five ContractVersion-2 read methods (GetMaxEc, GetEffectiveMaxEc, GetNetEcPerSecGross,
    GetNetEcPerSecTrue, GetSecondsToEmpty) and the ContractVersion-3 addition (GetNetEcPerSecLive)
    are bound only if the installed RealBattery reports a high-enough ContractVersion, and return
    0.0 like everything else in this wrapper when unbound — NOTE this collapses the real
    contract's NaN ("no data yet")/PositiveInfinity ("never runs out") sentinels (see
    RealBatteryPowerLedger.md) down to a single 0.0. If your mod needs to tell those apart,
    reference RealBattery.dll directly instead of going through this wrapper.

    GetNetEcPerSecLive vs. GetNetEcPerSecGross/True: if your prop/readout only ever cares about
    the vessel the player is currently flying (e.g. an RPM/MAS IVA prop), prefer
    GetNetEcPerSecLive — it's the true instantaneous rate for a loaded vessel, not a
    periodically-recaptured background estimate. GetNetEcPerSecGross/True remain the only source
    for a vessel that isn't currently loaded.

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

        // ContractVersion 2 (optional — null on an older RealBattery install)
        private static Func<Vessel, double> _getMaxEc;
        private static Func<Vessel, double> _getEffectiveMaxEc;
        private static Func<Vessel, double> _getNetEcPerSecGross;
        private static Func<Vessel, double> _getNetEcPerSecTrue;
        private static Func<Vessel, double> _getSecondsToEmpty;

        // ContractVersion 3 (optional — null on an older RealBattery install)
        private static Func<Vessel, double> _getNetEcPerSecLive;

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

                // Optional: only present on ContractVersion >= 2. Bind() already returns null
                // (not throws) for a method that doesn't exist, so this is safe against older installs.
                _getMaxEc = Bind<Func<Vessel, double>>(ledgerType, "GetMaxEc");
                _getEffectiveMaxEc = Bind<Func<Vessel, double>>(ledgerType, "GetEffectiveMaxEc");
                _getNetEcPerSecGross = Bind<Func<Vessel, double>>(ledgerType, "GetNetEcPerSecGross");
                _getNetEcPerSecTrue = Bind<Func<Vessel, double>>(ledgerType, "GetNetEcPerSecTrue");
                _getSecondsToEmpty = Bind<Func<Vessel, double>>(ledgerType, "GetSecondsToEmpty");

                // Optional: only present on ContractVersion >= 3.
                _getNetEcPerSecLive = Bind<Func<Vessel, double>>(ledgerType, "GetNetEcPerSecLive");

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

        // ContractVersion 2. Return 0.0 both when RealBattery isn't installed and when an
        // older (v1) RealBattery is installed without these methods — same "safe default,
        // never throws" contract as everything else in this wrapper.
        public static double GetMaxEc(Vessel vessel) => _getMaxEc != null ? _getMaxEc(vessel) : 0.0;
        public static double GetEffectiveMaxEc(Vessel vessel) => _getEffectiveMaxEc != null ? _getEffectiveMaxEc(vessel) : 0.0;
        public static double GetNetEcPerSecGross(Vessel vessel) => _getNetEcPerSecGross != null ? _getNetEcPerSecGross(vessel) : 0.0;
        public static double GetNetEcPerSecTrue(Vessel vessel) => _getNetEcPerSecTrue != null ? _getNetEcPerSecTrue(vessel) : 0.0;
        public static double GetSecondsToEmpty(Vessel vessel) => _getSecondsToEmpty != null ? _getSecondsToEmpty(vessel) : 0.0;

        // ContractVersion 3.
        public static double GetNetEcPerSecLive(Vessel vessel) => _getNetEcPerSecLive != null ? _getNetEcPerSecLive(vessel) : 0.0;

        private static TDelegate Bind<TDelegate>(Type type, string methodName) where TDelegate : class
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            return method == null ? null : (TDelegate)(object)Delegate.CreateDelegate(typeof(TDelegate), method);
        }
    }
}
