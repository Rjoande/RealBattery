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
        // Physical button ids this prop's UP/DOWN/HOME cluster uses for in-page list scrolling —
        // same ids and same mechanism (RPM_MODULE/buttonClickMethod, page-scoped, not the Lua
        // COLLIDER_EVENT overrides that claim the bay-select row) as MFDExtension's own CAS bay
        // (src/Cas/MFDExtCasModule.cs), read directly from that real, working implementation
        // rather than guessed. No mute-equivalent button here — CAS's buttonMute is a DangIt-only
        // concern with nothing to map to in this mod.
        [KSPField] public int buttonUp = 0;
        [KSPField] public int buttonDown = 1;
        [KSPField] public int buttonHome = 4;

        // Per-prop-instance scroll position, one per page — a second BMS-bearing monitor on the
        // same vessel would scroll independently, same reasoning as CasModule's own scrollOffset
        // field.
        private int l1ScrollOffset;
        private int l2ScrollOffset;

        // This prop's REAL visible rows (40x20) — NOT the screenHeight a textmethod is actually
        // called with, which MAS passes as a fixed (40,32) regardless of the prop's true screen
        // size (verified against MAS's real source and confirmed in game by MFDExtension's own
        // CAS bay, CasAggregator.cs — rows past the 20th are silently never shown). Trusting the
        // parameter here would silently overflow the real screen by 12 rows.
        private const int VisibleRows = 20;
        private const int StatusLineRows = 1;
        // Separator between body and status line, footer-style (a rule + the status line, always
        // together — never one without the other) — matches CAS's own current layout, tested and
        // confirmed in game 2026-08-30 on that page (CasAggregator.BuildPage/AppendStatusLine).
        private const int StatusSeparatorRows = 1;
        // +1 each for the blank line between the column header and the first content row
        // (replaces the old dash rule there — Pietro's call, 2026-08-30). L2's own-ship row no
        // longer has a rule after it either (removed, same request) — just the row itself.
        private const int L1FixedHeaderRows = 8; // title, rule, AUTONOMY, NET RATE, RESERVE, rule, column header, blank line
        private const int L2FixedHeaderRows = 4; // title, rule, column header, blank line

        private static int L1BodyBudget => Math.Max(1, VisibleRows - L1FixedHeaderRows - StatusSeparatorRows - StatusLineRows);
        private static int L2BodyBudget(bool hasActiveRow) =>
            Math.Max(1, VisibleRows - L2FixedHeaderRows - (hasActiveRow ? 1 : 0) - StatusSeparatorRows - StatusLineRows);

        private static int CountRealBatteryParts(Vessel vessel)
        {
            if (vessel?.parts == null) return 0;
            int count = 0;
            foreach (Part part in vessel.parts)
                if (part.Modules.Contains("RealBattery")) count++;
            return count;
        }

        // Guards BEFORE incrementing, same design as CasAggregator.TryScrollDown/
        // MFDExtCasModule.ButtonProcessor's UP case — scrollOffset never goes out of range even
        // momentarily, rather than relying solely on the render method's own clamp to correct it
        // on the next poll. Re-scans the vessel on every DOWN press rather than caching the last
        // render's count: a button press is a rare, human-paced event, so this costs nothing (same
        // justification CasAggregator itself gives for re-collecting its own entries in
        // TryScrollDown instead of reusing BuildPage's last result).
        public void ButtonProcessorL1(int buttonID)
        {
            if (buttonID == buttonDown)
            {
                int maxScroll = Math.Max(0, CountRealBatteryParts(FlightGlobals.ActiveVessel) - L1BodyBudget);
                if (l1ScrollOffset < maxScroll) l1ScrollOffset++;
            }
            else if (buttonID == buttonUp)
            {
                if (l1ScrollOffset > 0) l1ScrollOffset--;
            }
            else if (buttonID == buttonHome)
            {
                l1ScrollOffset = 0;
            }
        }

        public void ButtonProcessorL2(int buttonID)
        {
            if (buttonID == buttonDown)
            {
                Vessel active = FlightGlobals.ActiveVessel;
                if (active == null) return;
                bool hasActiveRow = BuildFleetRow(active, isActive: true).HasValue;
                int otherCount = BuildOtherFleetRows(active).Count;
                int maxScroll = Math.Max(0, otherCount - L2BodyBudget(hasActiveRow));
                if (l2ScrollOffset < maxScroll) l2ScrollOffset++;
            }
            else if (buttonID == buttonUp)
            {
                if (l2ScrollOffset > 0) l2ScrollOffset--;
            }
            else if (buttonID == buttonHome)
            {
                l2ScrollOffset = 0;
            }
        }

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

        // Fleet overview (L2) column widths. Sum with the 4 single-space separators below comes to
        // 39 in the worst case (a full-width name + "STALE", the longest COMM value), fitting the
        // same 40-column budget as L1.
        private const int FleetColWidthName = 11;
        private const int FleetColWidthSoc = 4;
        private const int FleetColWidthNet = 8;
        private const int FleetColWidthEnd = 7;  // worst case "00h:00m" / "00m:00s"

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

        // Same dynamic unit selection as FormatPower, but shorter (1 decimal, no space before the
        // unit) — used in the fleet overview (L2) where every row has to fit a much narrower
        // RATE column than L1's single-vessel header line.
        private static string FormatPowerCompact(double kW)
        {
            SelectUnit(kW, PowerUnits, out double scale, out string unit);
            return $"{(kW / scale):+0.0;-0.0;0.0}{unit}";
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

        // Worst case among currently-discharging batteries on a LOADED vessel, not a vessel-wide
        // average — a small high-Crate battery and a large low-Crate one both maxed out would
        // average to a rate that matches neither's real depletion time. The first individual pack
        // to hit zero is the first moment the situation actually changes (load redistributes across
        // the survivors), so it's the honest, actionable figure. Shared by L1's own AUTONOMY line
        // and L2's per-vessel END column (for LIVE rows) — same definition, one place, instead of
        // two copies drifting apart. Returns PositiveInfinity if nothing is currently discharging
        // (including an unloaded vessel, where this can't be computed at all — see the ExpUT-based
        // path in BuildFleetRow instead).
        private static double ComputeMinSecondsToEmpty(Vessel vessel)
        {
            double minSeconds = double.PositiveInfinity;
            if (vessel?.parts == null) return minSeconds;

            foreach (Part part in vessel.parts)
            {
                if (!part.Modules.Contains("RealBattery")) continue;
                RealBattery rb = part.Modules.GetModule<RealBattery>();
                if (rb == null || rb.lastECpower >= -1e-6) continue; // not currently discharging

                PartResource sc = part.Resources.Get("StoredCharge");
                if (sc == null) continue;

                double availableEc = sc.amount * RealBattery.EC2SCratio;
                double seconds = availableEc / Math.Abs(rb.lastECpower);
                if (seconds < minSeconds) minSeconds = seconds;
            }

            return minSeconds;
        }

        // A name that overflows its column scrolls (marquee) instead of being cut short forever —
        // same mechanic and tuning as MFDExtension's own CasAggregator.ScrollingTitle (real,
        // working precedent), driven by Time.realtimeSinceStartup (immune to warp/pause) rather
        // than the textmethod's own poll rate, since the two are unrelated. Adapted for a column
        // that ISN'T the last thing on its line (unlike CAS's own use, where nothing follows the
        // scrolling field): always returns exactly `width` characters, padding short text instead
        // of returning it as-is, so the columns after it never lose alignment.
        private const float ScrollCharsPerSecond = 4f;
        private const string ScrollGap = "   "; // seam marking the wrap instead of jumping straight back
        private static string ScrollingText(string text, int width)
        {
            if (width <= 0) return string.Empty;
            text = text ?? string.Empty;
            if (text.Length <= width) return text.PadRight(width);

            string loop = text + ScrollGap;
            int period = loop.Length;
            int offset = (int)(Time.realtimeSinceStartup * ScrollCharsPerSecond) % period;

            var window = new StringBuilder(width);
            for (int i = 0; i < width; i++)
                window.Append(loop[(offset + i) % period]);
            return window.ToString();
        }

        // Mirrors CasAggregator.AppendBody's "+N MORE" behavior for a flat (non-tiered) list: if
        // everything from scrollOffset to the end already fits within bodyBudget, show it all and
        // no MORE line is needed; otherwise reserve the body's last row for a "+N MORE" line
        // instead of squeezing in one more (and only one more) entry, so the truncation is visible
        // right where it happens — not just inferable from the "X-Y of N" status line below it.
        private static int ResolveShownCount(int totalFromOffset, int bodyBudget, out bool hasMore)
        {
            if (totalFromOffset <= bodyBudget)
            {
                hasMore = false;
                return totalFromOffset;
            }
            hasMore = true;
            return Math.Max(0, bodyBudget - 1);
        }

        // Λ (U+039B, greek capital lambda) / ○ (U+25CB, white circle) — not "^"/"O": the caret
        // renders much smaller than "v" in the monitor's font, and a monospaced capital "O" reads
        // as an oval or a zero. Both glyphs verified on screen in game (2026-08-30, on CAS, the
        // page this text is kept in sync with — CasAggregator.AppendStatusLine).
        private const string KeyLegend = "ΛV: scroll  ○: home";

        // Two halves, not one phrase: "X-Y of N" stays left, the key legend goes right, the gap
        // between them filled with spaces — so position and legend read as two distinct pieces of
        // information rather than a single run-on line. Same house-style status line as
        // MFDExtension's own CAS bay (CasAggregator.AppendStatusLine), shown whenever there's at
        // least one scrollable entry (total > 0).
        private static string BuildScrollStatusLine(int total, int scrollOffset, int shownCount, int screenWidth)
        {
            int first = scrollOffset + 1;
            int last = scrollOffset + shownCount;
            string position = $"{first}-{last} of {total}";

            int gap = screenWidth - position.Length - KeyLegend.Length;
            string line = gap > 0
                ? position + new string(' ', gap) + KeyLegend
                : position + " " + KeyLegend;
            if (line.Length > screenWidth) line = line.Substring(0, screenWidth);
            return line;
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

            // --- Autonomy: worst case among currently-discharging batteries (ComputeMinSecondsToEmpty,
            // shared with L2's own END column for LIVE vessels) — see that method's own remarks for
            // why this beats a vessel-wide average. FormatEndTimer (also shared with L2) keeps this
            // consistent with L2 and correct under a non-stock home body: Kerbin-day/year lengths
            // from RealBatterySettings, not a hardcoded 24h/365d calendar.
            double minSecondsToEmpty = ComputeMinSecondsToEmpty(vessel);

            if (!double.IsPositiveInfinity(minSecondsToEmpty))
                sb.AppendLine($"AUTONOMY   [{ColCyan}]{FormatEndTimer(minSecondsToEmpty)}[{ColWhite}]");
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
            sb.AppendLine(); // blank line, replaces a dash rule here (Pietro's call, 2026-08-30)

            // batteries already enumerated above (presence check) — deliberately NOT filtered
            // through RealBatteryPowerLedger.EnumerateEligibleBatteries (which excludes disabled/
            // FixedOutput batteries): a disabled or overheated battery is exactly what a
            // pilot needs to see here, flagged via the STATUS column instead of hidden.
            //
            // Scrollable window + status line (buttonUp/buttonDown/buttonHome via
            // ButtonProcessorL1) — same flat-list scroll mechanic as L2's own fleet body below,
            // itself modeled on MFDExtension's own CAS bay. Re-clamped here too, not only in
            // ButtonProcessorL1's own guard — a battery added/removed elsewhere (staging, EVA
            // construction) between button presses could otherwise leave a stale offset pointing
            // past the end.
            int bodyBudget = L1BodyBudget;
            int maxScroll = Math.Max(0, batteries.Count - bodyBudget);
            l1ScrollOffset = Math.Max(0, Math.Min(l1ScrollOffset, maxScroll));

            int totalFromOffsetL1 = batteries.Count - l1ScrollOffset;
            int shownCount = ResolveShownCount(totalFromOffsetL1, bodyBudget, out bool hasMoreL1);
            int batteryIndex = l1ScrollOffset;
            for (int rowIdx = 0; rowIdx < shownCount; rowIdx++)
            {
                RealBattery rb = batteries[l1ScrollOffset + rowIdx];
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

            int rowsUsedL1 = shownCount + (hasMoreL1 ? 1 : 0);
            if (hasMoreL1)
            {
                // Aligned under CHEM, not column 0 — reads as a continuation of the battery list
                // it's summarizing rather than an unrelated line floating at the row's left edge.
                string indent = new string(' ', ColWidthIdx + ColSep.Length);
                sb.AppendLine($"{indent}+{totalFromOffsetL1 - shownCount} MORE");
            }

            // Pad with blank lines up to the body budget so the footer (separator + status line)
            // anchors to the bottom of the screen instead of riding up against the last content
            // row whenever the body doesn't fill it — same fix as CasAggregator's own
            // status-line anchoring.
            for (int i = rowsUsedL1; i < bodyBudget; i++)
                sb.AppendLine();

            // Footer: a rule (own width, matching the header rule above — not CAS's own 40, which
            // is just that page's own line width) then the status line, always together.
            sb.AppendLine(new string('-', Math.Min(screenWidth, 39)));
            sb.AppendLine(BuildScrollStatusLine(batteries.Count, l1ScrollOffset, shownCount, screenWidth));

            return sb.ToString();
        }

        // ============================================================================
        //  L2 — Fleet overview. One row per vessel in the save that has RealBattery parts, loaded
        //  or not, sorted alphabetically — the active vessel is pinned as its own row right under
        //  the header instead (no marker glyph needed to mark it: position + color already do).
        //  Reachable from L1 by pressing the bay's own button again
        //  (MFDExt_OwnButtonOverrides["MFDExt_BATT"], see
        //  GameData/RealBattery/MFDExtension/MFDExt_BATT_Nav.lua) — pressing it again from here
        //  returns to L1 via MFDExt_Redirect's default behavior (jumps to a bay's own page from
        //  anywhere else), no extra Lua needed for the return trip.
        //
        //  Deliberately read-only for this first version (no vessel selection/switching). A loaded
        //  vessel gets exact figures from RealBatteryPowerLedger plus a real per-battery discharge
        //  timer (ComputeMinSecondsToEmpty, same definition as L1's own AUTONOMY); an unloaded one
        //  gets the same best-effort snapshot (RAM if visited this session, else parsed from the
        //  save's ProtoVessel) already trusted by the low-power alarm system
        //  (AlarmManager.RBAlarmSync), reused as-is rather than re-deriving a second, competing
        //  notion of "vessel battery state". No per-battery detail (RUNAWAY/OVERHEAT/etc.) for
        //  unloaded vessels — that would require parsing ProtoPartModuleSnapshot fields, out of
        //  scope for this pass.
        // ============================================================================
        public string GetL2Text(int screenWidth, int screenHeight)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                return "BATTERY MANAGER   FLEET" + Environment.NewLine + Environment.NewLine + "No active vessel.";

            FleetRow? activeRow = BuildFleetRow(active, isActive: true);
            List<FleetRow> otherRows = BuildOtherFleetRows(active);

            var sb = new StringBuilder();
            sb.AppendLine(Truncate("BATTERY MANAGER   FLEET", screenWidth));
            sb.AppendLine(new string('-', Math.Min(screenWidth, 39)));

            if (!activeRow.HasValue && otherRows.Count == 0)
            {
                sb.AppendLine("No RealBattery vessels in this save.");
                return sb.ToString();
            }

            sb.AppendLine(
                "VESSEL".PadRight(FleetColWidthName) + ColSep +
                "SOC".PadLeft(FleetColWidthSoc) + ColSep +
                "NET".PadLeft(FleetColWidthNet) + ColSep +
                "END".PadLeft(FleetColWidthEnd) + ColSep +
                "COMM");
            sb.AppendLine(); // blank line, replaces a dash rule here (Pietro's call, 2026-08-30)

            if (activeRow.HasValue)
            {
                // No rule after the own-ship row anymore (removed, same request) — it flows
                // straight into the rest of the fleet below.
                sb.AppendLine(BuildFleetDataRow(activeRow.Value));
            }

            // Purely alphabetical — no criticality ordering. The pilot already sees which vessels
            // need attention via SOC's own color (red/yellow), not via list position.
            otherRows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Scrollable window + status line (buttonUp/buttonDown/buttonHome via
            // ButtonProcessorL2) — same flat-list scroll mechanic as L1's own battery table above,
            // itself modeled on MFDExtension's own CAS bay. The own-ship row/rule above (when
            // present) are fixed, never part of the scrollable region — only the alphabetical
            // "rest of the fleet" body scrolls. Re-clamped here too, not only in ButtonProcessorL2's
            // own guard — a vessel appearing/disappearing between button presses (recovered,
            // terminated, newly gaining a RealBattery snapshot) could otherwise leave a stale
            // offset pointing past the end.
            int bodyBudget = L2BodyBudget(activeRow.HasValue);
            int maxScroll = Math.Max(0, otherRows.Count - bodyBudget);
            l2ScrollOffset = Math.Max(0, Math.Min(l2ScrollOffset, maxScroll));

            int totalFromOffsetL2 = otherRows.Count - l2ScrollOffset;
            int shownCount = ResolveShownCount(totalFromOffsetL2, bodyBudget, out bool hasMoreL2);
            for (int i = 0; i < shownCount; i++)
                sb.AppendLine(BuildFleetDataRow(otherRows[l2ScrollOffset + i]));

            int rowsUsedL2 = shownCount + (hasMoreL2 ? 1 : 0);
            if (hasMoreL2)
                sb.AppendLine($"+{totalFromOffsetL2 - shownCount} MORE");

            if (otherRows.Count > 0)
            {
                // Pad with blank lines up to the body budget so the footer (separator + status
                // line) anchors to the bottom of the screen instead of riding up against the last
                // content row whenever the body doesn't fill it — same fix as CasAggregator's own
                // status-line anchoring.
                for (int i = rowsUsedL2; i < bodyBudget; i++)
                    sb.AppendLine();

                // Footer: a rule (own width, matching the header rule above — not CAS's own 40,
                // which is just that page's own line width) then the status line, always together
                // — never an orphaned rule with no status line when otherRows is empty.
                sb.AppendLine(new string('-', Math.Min(screenWidth, 39)));
                sb.AppendLine(BuildScrollStatusLine(otherRows.Count, l2ScrollOffset, shownCount, screenWidth));
            }

            return sb.ToString();
        }

        // Extracted so ButtonProcessorL2's own DOWN guard can recompute the exact same list (just
        // for its Count) without duplicating the enumeration/filtering logic.
        private static List<FleetRow> BuildOtherFleetRows(Vessel active)
        {
            var otherRows = new List<FleetRow>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v.id == active.id) continue;
                switch (v.vesselType)
                {
                    case VesselType.EVA:
                    case VesselType.Flag:
                    case VesselType.SpaceObject:
                    case VesselType.Unknown:
                    case VesselType.Debris:
                        continue;
                }

                FleetRow? row = BuildFleetRow(v, isActive: false);
                if (row.HasValue) otherRows.Add(row.Value);
            }
            return otherRows;
        }

        private struct FleetRow
        {
            public bool IsActive;
            public bool IsLive;
            public string Name;
            public double SocPct;
            public double NetKW;
            public double EndSeconds; // PositiveInfinity = stable, no timer to show
        }

        private static string BuildFleetDataRow(FleetRow row)
        {
            // Pure charge-level thresholds (not tied to ExpUT/trend) — a battery sitting at 8%
            // reads as critical regardless of whether it happens to be charging or discharging
            // right now. Aligned with MFDExtension's own VVEFIS tank palette
            // (FuelEngineReader.TankEmptyThreshold/TankCautionThreshold, src/Shared/ — 1%/10%,
            // grounded in the FAR/CS-23.1337 "functionally empty" convention) rather than an
            // independently-chosen pair, so a battery reads the same severity here as it would
            // as a fuel-tank-style fill on that other screen. Amber (not yellow) for the caution
            // band, matching VVEFISSeverity.CautionAmber/ColAmber's existing use elsewhere in
            // this file (OVERHEAT) — one amber, not a second near-identical yellow.
            string socColor = row.SocPct < 1.0 ? ColRed : row.SocPct < 10.0 ? ColAmber : ColWhite;
            string commText = row.IsLive ? "LIVE" : "STALE";
            string commColor = row.IsLive ? ColGreen : ColWhite;

            // Amber once the timer is within the player's own configured low-power lead time —
            // the same window the alarm itself uses to warn ahead of actual depletion. Not
            // colored once FormatEndTimer has already hidden it (EndSeconds <= 0 -> "--"): an
            // amber dash would read as a lingering alarm on a field that's deliberately gone
            // blank, defeating the point of hiding it.
            bool endIsUrgent = !double.IsPositiveInfinity(row.EndSeconds) &&
                                row.EndSeconds > 0.0 &&
                                row.EndSeconds <= RealBatterySettings.LowPowerLeadSeconds;
            string endColor = endIsUrgent ? ColAmber : ColWhite;

            // Scrolls (marquee) instead of a hard, permanent truncation when a vessel's name
            // overflows the column — the pilot's own request: the full name eventually shows
            // rather than staying forever hidden past the column budget.
            string nameCol = ScrollingText(row.Name, FleetColWidthName);
            string socCol = $"{row.SocPct:0}%".PadLeft(FleetColWidthSoc);
            string netCol = FormatPowerCompact(row.NetKW).PadLeft(FleetColWidthNet);
            string endCol = FormatEndTimer(row.EndSeconds).PadLeft(FleetColWidthEnd);

            return
                (row.IsActive ? $"[{ColCyan}]{nameCol}[{ColWhite}]" : nameCol) + ColSep +
                $"[{socColor}]{socCol}[{ColWhite}]" + ColSep +
                netCol + ColSep +
                $"[{endColor}]{endCol}[{ColWhite}]" + ColSep +
                $"[{commColor}]{commText}[{ColWhite}]";
        }

        // Loaded vessel: exact figures from RealBatteryPowerLedger (same source L1 uses) plus a
        // real per-battery discharge timer (ComputeMinSecondsToEmpty — same definition L1's own
        // AUTONOMY uses, not a vessel-wide average). Not loaded: AlarmManager's own best-effort
        // snapshot (RAM if visited this session, else parsed from the save's ProtoVessel) — the
        // same data the low-power alarm already trusts, timer from ExpUT minus current UT. Returns
        // null if this vessel has no RealBattery capacity either way.
        private static FleetRow? BuildFleetRow(Vessel v, bool isActive)
        {
            if (v.loaded)
            {
                double maxEc = RealBatteryPowerLedger.GetMaxEc(v);
                if (maxEc <= 1e-6) return null;

                return new FleetRow
                {
                    IsActive = isActive,
                    IsLive = true,
                    Name = v.vesselName,
                    SocPct = (RealBatteryPowerLedger.GetAvailableEc(v) / maxEc) * 100.0,
                    NetKW = RealBatteryPowerLedger.GetNetEcPerSecLive(v),
                    EndSeconds = ComputeMinSecondsToEmpty(v)
                };
            }

            RBLiteSnapshot snap = RBAlarmSync.GetBestEffortSnapshot(v);
            if (snap == null) return null;

            double now = Planetarium.GetUniversalTime();

            // Project StoredCharge forward from the snapshot's own timestamp — left as the raw
            // captured value, SOC would silently freeze at whatever it was when the vessel was
            // last switched away from/went off-rails/the game was saved (KSP doesn't tick an
            // unloaded vessel's parts, and the ProtoVessel's own resource amounts are equally
            // frozen until the vessel loads and ApplySnapshot reconciles them).
            double elapsedSeconds = Math.Max(0.0, now - snap.Timestamp);
            double projectedStoredCharge = snap.StoredChargeAmount + (snap.NetEC_Gross * elapsedSeconds) / 3600.0;
            projectedStoredCharge = Math.Max(0.0, Math.Min(snap.StoredChargeMaxAmount, projectedStoredCharge));

            // Raw seconds-to-empty from the projected charge above — deliberately NOT
            // snap.ExpUT (which already has RealBatterySettings.LowPowerLeadSeconds subtracted,
            // baked in for the alarm system's own purposes). Reusing ExpUT here made a STALE
            // row's END hit "--" a full lead time before the battery was actually empty, and its
            // amber window twice as wide as a LIVE row's for the same physical situation — this
            // matches ComputeMinSecondsToEmpty's LIVE semantics exactly instead (true physical
            // time to empty, no lead time involved), so both row types mean the same thing.
            double endSeconds = (snap.NetEC_Gross < -1e-6 && projectedStoredCharge > 1e-9)
                ? (projectedStoredCharge * 3600.0) / Math.Abs(snap.NetEC_Gross)
                : double.PositiveInfinity;

            return new FleetRow
            {
                IsActive = isActive,
                IsLive = false,
                Name = v.vesselName,
                SocPct = snap.StoredChargeMaxAmount > 1e-6 ? (projectedStoredCharge / snap.StoredChargeMaxAmount) * 100.0 : 0.0,
                NetKW = snap.NetEC_Gross,
                EndSeconds = endSeconds
            };
        }

        // Two-resolution duration, always right-aligned, worst case 7 characters:
        // below the hour, MMm:SSs; from an hour to a day, HHh:MMm; a day and up, a single unit
        // with one decimal (d, then y). Day/year length come from RealBatterySettings
        // (GetHoursPerDay/GetDaysPerYear, derived from the home body) — never hardcoded 24h/365d,
        // so this reads correctly under a Kopernicus home world with a different day or year
        // length, not just stock Kerbin.
        private static string FormatEndTimer(double seconds)
        {
            // seconds <= 0 (not just < 0): once a countdown actually reaches zero it stops being
            // useful information — hide it the same way "not applicable" already is, rather than
            // sitting on a permanent "00m:00s" (Pietro's call, 2026-08-30 — applies to both L1's
            // AUTONOMY and L2's END, sharing this one formatter).
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                return "--";

            double daySeconds = RealBatterySettings.GetHoursPerDay() * 3600.0;
            double yearSeconds = daySeconds * RealBatterySettings.GetDaysPerYear();

            if (seconds >= yearSeconds)
                return $"{(seconds / yearSeconds):0.0}y";
            if (seconds >= daySeconds)
                return $"{(seconds / daySeconds):0.0}d";
            if (seconds >= 3600.0)
            {
                int h = (int)(seconds / 3600.0);
                int m = (int)((seconds % 3600.0) / 60.0);
                return $"{h:00}h:{m:00}m";
            }
            else
            {
                int m = (int)(seconds / 60.0);
                int s = (int)(seconds % 60.0);
                return $"{m:00}m:{s:00}s";
            }
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || maxLen <= 0 || s.Length <= maxLen) return s;
            return s.Substring(0, Math.Max(0, maxLen));
        }

    }
}
