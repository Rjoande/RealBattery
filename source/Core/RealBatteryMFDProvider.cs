using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RealBattery
{
    // ============================================================================
    //  RealBatteryMFDProvider — MFD Extended "BMS" bay content (L1, active vessel).
    //  Bay renamed from "BATT" to "BMS" on the MFDExtension host side (2026-08-27) — display
    //  label only, the internal page/file identifiers below (MFDExt_BATT) are unchanged.
    //
    //  Wired via MAS's `textmethod = RealBatteryMFDProvider:GetL1Text` (see
    //  GameData/RealBattery/MFDExtension/MFDExt_BATT.cfg) — the same reflection-based
    //  bridge MAS itself uses to reuse RPM PAGEHANDLER classes (DPAI_RPM:getPageText,
    //  InternalVesselView:ShowMenu — verified against real examples under
    //  GameData/MOARdV/MFD and GameData/MOARdV/MAS_JSI/MFDPages in the dev workspace).
    //  Must be an InternalModule (prop-level), not a PartModule — it's instantiated as
    //  a sibling MODULE on the MAS_JSI_BasicMFD prop itself, not on a vessel part.
    //
    //  Returns a plain multi-line string with MAS's inline `[#RRGGBB]` color tags
    //  baked in directly (confirmed real syntax, e.g. MOARdV/MFD/MFD2_Plan.cfg:
    //  "text = Stage dV: [#ffff9b]...[#afd3ff]m/s") — no separate colored TEXT nodes
    //  needed, no $&$ value-substitution templating (that's only for MAS's own
    //  `text = ...` field parsing, irrelevant here since textmethod returns the
    //  final string as-is).
    //
    //  All figures come from RealBatteryPowerLedger (ContractVersion 3) for the
    //  vessel-wide aggregates, and directly from RealBattery instances (same
    //  assembly, internal access) for the per-battery table — no new public API
    //  surface needed for this display-only consumer.
    // ============================================================================
    public class RealBatteryMFDProvider : InternalModule
    {
        private const string ColCyan = "#00FFFF";
        private const string ColGreen = "#00FF00";
        private const string ColYellow = "#FFFF00";
        private const string ColAmber = "#FFA500";
        private const string ColRed = "#FF0000";
        private const string ColMagenta = "#FF00FF";
        private const string ColWhite = "#FFFFFF";

        // Per-battery table column widths (character columns, plain-text length —
        // computed before any [#RRGGBB] tag is wrapped around a cell's content). The header
        // row is built from these exact same constants/separators as the data rows below, so
        // the two can never drift out of alignment the way hand-placed extra spaces did.
        private const int ColWidthIdx = 2;
        private const int ColWidthChem = 8;    // longest active BatteryTypeDisplayName today: "Hf-178m2", "Graphene", "Hf-#####"
        private const int ColWidthSoc = 5;     // "100%" right-aligned, 1 char headroom
        private const int ColWidthHealth = 6;  // must fit the "HEALTH" header word itself (6 chars)
        private const string ColSep = " ";     // single-space gap between # / CHEM / SOC / HEALTH
        private const string StatusGap = "   "; // wider breathing room before the STATUS word/value

        // Magnitude ladders for dynamic unit selection, base unit at index 1 (kW/kWh) since
        // RB's own EC/StoredCharge figures are already expressed in that unit by convention.
        // Cutoff rule: step up a tier once the value in the current tier exceeds 1000, step
        // down once it's at or below 0.1 (and a smaller tier exists) — cascades through
        // multiple tiers in one call if needed (e.g. 0.00001 kW -> 0.01 W, not stuck at "0.00 kW").
        private static readonly string[] PowerUnits = { "W", "kW", "MW", "GW" };
        private static readonly string[] EnergyUnits = { "Wh", "kWh", "MWh", "GWh" };
        private const int BaseUnitIndex = 1; // kW / kWh

        // Real (unpaused, warp-independent) time toggle for STATUS blink/alternation — ties the
        // rhythm to what the player actually sees on screen, not to game time (which can be
        // paused or sped up by timewarp while the MFD prop keeps rendering).
        private static bool ToggleEvery(float periodSeconds) => ((int)(Time.time / periodSeconds) & 1) == 0;

        private static void SelectUnit(double referenceValue, string[] units, out double scale, out string unit)
        {
            int idx = BaseUnitIndex;
            double av = Math.Abs(referenceValue);
            while (av > 1000.0 && idx < units.Length - 1)
            {
                av /= 1000.0;
                idx++;
            }
            while (av > 0.0 && av <= 0.1 && idx > 0)
            {
                av *= 1000.0;
                idx--;
            }
            scale = Math.Pow(1000.0, idx - BaseUnitIndex);
            unit = units[idx];
        }

        // Power (NET RATE): a single live value, free to pick whatever unit best fits it right now.
        private static string FormatPower(double kW)
        {
            SelectUnit(kW, PowerUnits, out double scale, out string unit);
            return $"{(kW / scale):+0.00;-0.00;0.00} {unit}";
        }

        // Energy (RESERVE): unit chosen ONCE from the physical total and shared by both numbers
        // in the "available / total" pair — stays constant as SOC changes (100% and 1% of the
        // same pack both read in the same unit), instead of the available figure drifting to a
        // smaller unit just because it's a small fraction of the total.
        private static string FormatEnergyPair(double availableKWh, double maxKWh)
        {
            SelectUnit(maxKWh, EnergyUnits, out double scale, out string unit);
            return $"{(availableKWh / scale):0.0} / {(maxKWh / scale):0.0} {unit}";
        }

        // IMPORTANT: MAS splits text into rows on Environment.NewLine ONLY
        // (MdVTextMesh.SetText -> Utility.LineSeparator = { Environment.NewLine },
        // i.e. "\r\n" on Windows). A plain '\n' is NOT a line break for MAS — the
        // whole string renders as one clipped row (confirmed in game 2026-08-23:
        // only the header line was visible). Hence AppendLine/Environment.NewLine
        // throughout, never '\n'.
        public string GetL1Text(int screenWidth, int screenHeight)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
                return "BATTERY MANAGER" + Environment.NewLine + Environment.NewLine + "No active vessel.";

            var sb = new StringBuilder();
            sb.AppendLine(Truncate($"BATTERY MANAGER   {vessel.vesselName}", screenWidth));
            sb.AppendLine(new string('-', Math.Min(screenWidth, 39)));

            // Physical presence, not eligibility/capacity: a vessel where every battery is
            // disabled or in runaway still HAS RealBattery parts — GetEffectiveMaxEc alone
            // would read 0 in that exact case (all ineligible) and wrongly claim there are
            // none at all, hiding the per-battery table below that exists specifically to
            // surface that state. Bug found in game 2026-08-25: a runaway battery made the
            // whole screen fall back to "No RealBattery parts" instead of showing the table.
            List<RealBattery> batteries = new List<RealBattery>();
            if (vessel.parts != null)
            {
                foreach (Part part in vessel.parts)
                {
                    if (!part.Modules.Contains("RealBattery")) continue;
                    RealBattery rbPart = part.Modules.GetModule<RealBattery>();
                    if (rbPart != null) batteries.Add(rbPart);
                }
            }

            if (batteries.Count == 0)
            {
                sb.AppendLine("No RealBattery parts on this vessel.");
                return sb.ToString();
            }

            double availableEc = RealBatteryPowerLedger.GetAvailableEc(vessel);
            double maxEc = RealBatteryPowerLedger.GetMaxEc(vessel);
            double netLive = RealBatteryPowerLedger.GetNetEcPerSecLive(vessel);

            // --- Autonomy: worst case among currently-discharging batteries, not a vessel-wide
            // average. GetNetEcPerSecLive/availableEc give the *aggregate* rate/energy — fine for
            // NET RATE below, but useless here when packs have very different C-rates: e.g. a
            // small high-Crate SMES and a large low-Crate Li-ion both maxed out average out to a
            // number that matches neither battery's real depletion time. The first individual pack
            // to hit zero is the first moment the situation actually changes (load redistributes
            // across the survivors) — that's the honest, actionable figure to show.
            double minSecondsToEmpty = double.PositiveInfinity;
            foreach (var rb in batteries)
            {
                if (rb.lastECpower >= -1e-6) continue; // not currently discharging
                PartResource ownSc = rb.part.Resources.Get("StoredCharge");
                if (ownSc == null) continue;
                double ownAvailableEc = ownSc.amount * RealBattery.EC2SCratio;
                double ownSeconds = ownAvailableEc / Math.Abs(rb.lastECpower);
                if (ownSeconds < minSecondsToEmpty) minSecondsToEmpty = ownSeconds;
            }

            if (!double.IsPositiveInfinity(minSecondsToEmpty))
                sb.AppendLine($"AUTONOMY   [{ColCyan}]{FormatDuration(minSecondsToEmpty)}[{ColWhite}]");
            else
                sb.AppendLine($"AUTONOMY   [{ColGreen}]stable[{ColWhite}]");

            // --- Net rate + status ---
            string status = netLive < -0.01 ? "DISCHARGE" : netLive > 0.01 ? "CHARGE" : "IDLE";
            string statusColor = netLive < -0.01 ? ColRed : ColGreen;
            sb.AppendLine($"NET RATE   [{ColCyan}]{FormatPower(netLive)}[{ColWhite}]  [{statusColor}]{status}[{ColWhite}]");

            // --- Reserve ---
            // Physical total (sc.maxAmount, undiminished by wear/thermal derating), not the
            // effective/derated capacity: a vessel with every battery dead or thermally capped
            // would otherwise read "100%" of a capacity that has itself shrunk to ~0 — technically
            // full, practically empty. GetMaxEc reports the real, physical kWh regardless.
            double socPct = maxEc > 1e-6 ? (availableEc / maxEc) * 100.0 : 0.0;
            double availableKWh = availableEc / RealBattery.EC2SCratio;
            double maxKWh = maxEc / RealBattery.EC2SCratio;
            sb.AppendLine($"RESERVE    [{ColCyan}]{socPct:0}%[{ColWhite}]  ({FormatEnergyPair(availableKWh, maxKWh)})");

            sb.AppendLine(new string('-', Math.Min(screenWidth, 39)));
            sb.AppendLine(
                "#".PadRight(ColWidthIdx) + ColSep +
                "CHEM".PadRight(ColWidthChem) + ColSep +
                "SOC".PadLeft(ColWidthSoc) + ColSep +
                "HEALTH".PadLeft(ColWidthHealth) + StatusGap +
                "STATUS");

            // batteries already enumerated above (presence check) — deliberately NOT filtered
            // through RealBatteryPowerLedger.EnumerateEligibleBatteries (which excludes disabled/
            // FixedOutput batteries): a disabled or overheated battery is exactly what a
            // pilot needs to see here, flagged via the STATUS column instead of hidden.
            int batteryIndex = 0;
            foreach (var rb in batteries)
            {
                batteryIndex++;
                // InfiniteCycles (SMES/cryo) batteries don't wear by cycles — BatteryLife stays
                // pinned at 1.0 forever for them. ThermalCapFactor (their thermal derating, not
                // cumulative damage) is the actual "how much of this battery is usable right now"
                // figure, same substitution RealBattery.cs's own PAW BatteryHealthStatus already
                // makes (#LOC_RB_BatteryHealth_efficiency) for these chemistries.
                double healthPct = (rb.InfiniteCycles ? rb.ThermalCapFactor : rb.BatteryLife) * 100.0;
                string healthColor = healthPct < 80.0 ? ColYellow : ColGreen;

                // Most severe condition wins — faults (RUNAWAY/OVERHEAT/OFFLINE, colored by
                // severity) outrank advisories (SHUTDOWN/WARMUP/COOLDOWN/WARM/COLD, always
                // Cyan — routine KeepWarm state, not a problem). Quiet dark cockpit: blank
                // when there's nothing worth flagging at all.
                //
                // IsSpent sits right after RUNAWAY, same as BatteryChargeStatus's own chain:
                // a dead battery reporting OVERHEAT is misleading noise (nothing left to burn),
                // so it falls straight to blank/OFFLINE instead — HEALTH already shows ~0%.
                string batteryStatusText;
                string batteryStatusColor;
                if (rb.isRunaway)
                {
                    // Blinks at 1 Hz (on/off every 0.5s) — the one condition urgent enough to
                    // demand attention rather than just sit there colored red.
                    bool blinkOn = ToggleEvery(0.5f);
                    batteryStatusText = blinkOn ? "RUNAWAY" : "";
                    batteryStatusColor = ColRed;
                }
                else if (rb.IsSpent)
                {
                    if (rb.BatteryDisabled)
                    {
                        batteryStatusText = "OFFLINE";
                        batteryStatusColor = ColMagenta;
                    }
                    else
                    {
                        batteryStatusText = "";
                        batteryStatusColor = ColWhite;
                    }
                }
                else if (rb.IsOverheating && rb.BatteryDisabled)
                {
                    // PCM tripped: both conditions are simultaneously true and equally worth
                    // knowing (OVERHEAT explains WHY it's off) — alternate them every 1s instead
                    // of letting one silently hide the other.
                    bool showOverheat = ToggleEvery(1f);
                    batteryStatusText = showOverheat ? "OVERHEAT" : "OFFLINE";
                    batteryStatusColor = showOverheat ? ColAmber : ColMagenta;
                }
                else if (rb.IsOverheating)
                {
                    batteryStatusText = "OVERHEAT";
                    batteryStatusColor = ColAmber;
                }
                else if (rb.BatteryDisabled)
                {
                    batteryStatusText = "OFFLINE";
                    batteryStatusColor = ColMagenta;
                }
                else if (rb.controlledShutdownActive)
                {
                    batteryStatusText = "SHUTDOWN";
                    batteryStatusColor = ColCyan;
                }
                else if (rb.keepWarmActive)
                {
                    batteryStatusText = rb.KeepWarmMode == "cryo" ? "COOLDOWN" : "WARMUP";
                    batteryStatusColor = ColCyan;
                }
                else if (rb.KeepWarmMode != "false")
                {
                    // Steady-state (post-ramp) KeepWarm maintenance — routine upkeep, not a ramp.
                    batteryStatusText = rb.KeepWarmMode == "cryo" ? "COLD" : "WARM";
                    batteryStatusColor = ColCyan;
                }
                else
                {
                    batteryStatusText = "";
                    batteryStatusColor = ColWhite;
                }

                string chemName = string.IsNullOrEmpty(rb.BatteryTypeDisplayName) ? (rb.ChemistryID ?? "?") : rb.BatteryTypeDisplayName;
                string idxCol = batteryIndex.ToString().PadRight(ColWidthIdx);
                string chemCol = Truncate(chemName, ColWidthChem).PadRight(ColWidthChem);
                string socCol = $"{(rb.SC_SOC * 100.0):0}%".PadLeft(ColWidthSoc);
                string healthCol = $"{healthPct:0}%".PadLeft(ColWidthHealth);
                string statusCol = string.IsNullOrEmpty(batteryStatusText) ? "" : $"[{batteryStatusColor}]{batteryStatusText}[{ColWhite}]";

                sb.AppendLine(
                    idxCol + ColSep + chemCol + ColSep + socCol + ColSep +
                    $"[{healthColor}]{healthCol}[{ColWhite}]" + StatusGap + statusCol);
            }

            return sb.ToString();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || maxLen <= 0 || s.Length <= maxLen) return s;
            return s.Substring(0, Math.Max(0, maxLen));
        }

        private static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return "--";

            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1)
                return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{span.Minutes}m {span.Seconds}s";
        }
    }
}
