# RealBattery Power Ledger (ContractVersion 3)

For third-party mods that manage their **own** background/offline energy accounting (e.g. a
rover autopilot that keeps driving while the vessel is unloaded, or any mod that simulates
activity independently of KSP's live per-tick resource flow) and need to reconcile that
accounting against RealBattery once the vessel is loaded again. Everything speaks plain
**ElectricCharge (EC)** units.

Implementation: `source/Core/RealBatteryPowerLedger.cs`. Consumer template:
`RealBatteryPowerLedgerWrapper.cs` (same folder) — copy it into your own mod, no compile-time
reference to RealBattery.dll needed.

## This is a reporting contract, not a command one

`ReportConsumedEc` tells RealBattery **"I already consumed this much energy"** — it is not a
request asking RealBattery to hand energy over on demand. Only call it for energy your own mod
has already accounted for as spent (e.g. a rover that drove through the night on a virtual
battery budget your mod tracks itself). RealBattery is the sole authority on how that
translates into `StoredCharge` drain, wear and `BatteryLife` — you never need to know any of
those internals to use this contract.

If what you actually need is "give me some energy right now, live, this tick" — that's not
what this contract is for. A vessel that's loaded and drawing power through the normal KSP
resource system (`Part.RequestResource` on `ElectricCharge`) is **already** automatically
compatible with RealBattery, with zero integration code: RealBattery's own live simulation
picks up any electrical deficit/surplus on the vessel every `FixedUpdate`. This contract exists
specifically for the case that live loop can't reach: energy your mod accounted for while the
vessel was *not* loaded, or independently of KSP's live resource flow.

## Quick start

1. Copy `RealBatteryPowerLedgerWrapper.cs` into your mod's source tree.
2. Call `RealBatteryPowerLedgerWrapper.Init()` once, early (e.g. a `MainMenu` `KSPAddon`).
3. Use the static methods like a normal API — they return `0.0` if RealBattery isn't
   installed, so you don't need to guard every call site yourself.

## Reference

| Method | Returns | Since |
|---|---|---|
| `GetDischargeableEcPerSec(Vessel vessel)` | Sum of rated discharge rate (EC/s), already derated | v1 |
| `GetAvailableEc(Vessel vessel)` | Currently stored energy (EC) | v1 |
| `ReportConsumedEc(Vessel vessel, double ecConsumed, double deltaTimeSeconds)` | EC actually covered | v1 |
| `GetMaxEc(Vessel vessel)` | Nominal (rated) capacity (EC), undiminished by wear/thermal | v2 |
| `GetEffectiveMaxEc(Vessel vessel)` | Usable-now capacity (EC), derated by wear + `ThermalCapFactor` | v2 |
| `GetNetEcPerSecGross(Vessel vessel)` | Net vessel EC balance including solar (EC/s, negative = draining) | v2 |
| `GetNetEcPerSecTrue(Vessel vessel)` | Net vessel EC balance excluding solar — conservative/worst-case | v2 |
| `GetSecondsToEmpty(Vessel vessel)` | Seconds to depletion at the current gross rate | v2 |
| `GetNetEcPerSecLive(Vessel vessel)` | True instantaneous net EC/s for a *loaded* vessel, no snapshot involved | v3 |

All of the above only consider **eligible** RealBattery parts: not `BatteryDisabled`, not
`FixedOutput` (one-shot thermal batteries don't participate in this contract — they aren't a
rechargeable background budget), with a `StoredCharge` resource present. Non-rechargeable
("primary") chemistries and `InfiniteCycles` (SMES-style) batteries *do* participate, with no
special distinction exposed.

All read/report methods require a **loaded** vessel (`vessel.parts` populated). `ReportConsumedEc`
on a vessel that isn't loaded returns `0.0` and logs one warning per vessel (not spammed) — it
does not throw. There is currently no support for reporting against an unloaded/`ProtoVessel`-backed
vessel; report at the point your own mod reconciles state against a *loaded* vessel (see the
BonVoyage example below for where that point naturally is).

### The `v2` net-EC and depletion methods, in more detail

`GetNetEcPerSecGross` / `GetNetEcPerSecTrue` don't recompute anything on call — they read RB's
own most recently captured `BackgroundSimulator.VesselEnergySnapshot` for that vessel (captured
on scene switch, vessel switch, game save, and once on flight-scene load), the same figures
RB's own low-power alarm (`ExpUT`) is computed from. That means:
- They can be a few seconds to minutes stale for a vessel that's stayed loaded and active
  without switching away — not a new source of uncertainty, just the same one RB's own alarms
  already accept.
- They return `double.NaN` if no snapshot exists yet for the vessel (e.g. queried the same
  frame it first loads) — check for `NaN` before using the value.

`GetSecondsToEmpty` is a convenience built directly from `GetAvailableEc` /
`GetNetEcPerSecGross` — the same quantity behind the low-power alarm's `ExpUT`, but as a plain
duration with **no alarm lead-time subtracted** (`RealBatterySettings.LowPowerLeadSeconds` is a
player-configurable alarm-timing concern, not part of this figure). Returns
`double.PositiveInfinity` at a net surplus/balance (never runs out at the current rate), or
`double.NaN` if no data is available.

### `GetNetEcPerSecLive` (v3) vs. `GetNetEcPerSecGross`/`GetNetEcPerSecTrue`

`GetNetEcPerSecLive` is the true, unsmoothed instantaneous net EC/s for a vessel that is
**currently loaded** — the sum of each eligible battery's raw `lastECpower` for the physics
tick that just ran, the same figure `BackgroundSimulator` itself treats as ground truth when
calibrating its own solar/consumption estimate. It is not the value a part's PAW shows
(`GUI_power`, deliberately smoothed so the on-screen readout doesn't flicker) — it's the
unsmoothed number underneath that, safe to use in your own math.

Use `GetNetEcPerSecLive` whenever you only ever care about the vessel the player is currently
flying (an RPM/MAS IVA prop is the clearest example — IVA only ever shows the active vessel).
Use `GetNetEcPerSecGross`/`GetNetEcPerSecTrue` for anything else (a vessel that may not be
loaded, or where you specifically want RB's solar-inclusive/exclusive background estimate
rather than the live figure). `GetNetEcPerSecLive` returns `0.0` for a vessel that isn't loaded
— it has no unloaded-vessel fallback of its own.

### Where the v2/v3 aggregates actually come from

`GetMaxEc`, `GetEffectiveMaxEc`, `GetAvailableEc`, `GetDischargeableEcPerSec`, and
`GetNetEcPerSecLive` are O(1) reads of a single aggregate that `RealBatteryLoadMaster` (the
`VesselModule` that already drives RB's live EC/SC transfer every `FixedUpdate`) recomputes
once per physics tick from its own battery list — not a fresh walk of the vessel's parts on
every call. Every caller shares the same computation: RB's own UI, an RPM/MAS prop, and any
third-party mod calling this contract all read the exact same number for the same tick, and
none of them pay for more than one pass over the vessel's batteries per tick no matter how many
callers ask. The one cost: a value can lag by up to one physics tick behind a structural change
(a battery added/removed mid-flight) — the same latency RealBatteryLoadMaster's own EC/SC
transfer logic already has, not a new one introduced by this contract.

## Units and semantics

- **EC only.** RealBattery's internal `StoredCharge` resource, its fixed 3600:1 ratio to EC,
  `SC_SOC`, and `WearCounter` are implementation details you never touch directly.
- **Request semantics**, same contract as `Part.RequestResource`: `ReportConsumedEc` returns
  how much was *actually* covered, which can be less than `ecConsumed` if the batteries can't
  cover it (empty or disabled) — always check the return value, don't assume the full amount
  was covered.
- **Pro-quota distribution.** Spread across every eligible battery's effective capacity share
  (derated by `BatteryLife`/`ThermalCapFactor`) — no single part is drained first.
- **Wear is always credited**, exactly as RealBattery's own live simulation would for the same
  energy moved (including the Engineer bonus). There is no opt-out: the energy was genuinely
  consumed, so the wear is not negotiable by the caller.
- **`deltaTimeSeconds` is for plausibility checking only**, not for any computation. RealBattery
  logs a warning (never blocks) if `ecConsumed` exceeds 1.5× what `GetDischargeableEcPerSec`
  could have delivered over that period — a sign your own accounting and RealBattery's rated
  discharge rate have diverged, worth investigating.
- **Idempotency is your responsibility.** This contract holds no state between calls (besides
  the one-time unloaded-vessel warning). If you call `ReportConsumedEc` twice for the same
  energy, RealBattery will drain it twice. Reset whatever internal counter you're reporting
  from immediately after a successful report — the same way you'd reset a "pending" balance
  after any other resource request.
- **No day-time recharge counterpart in this version.** RealBattery's own simulation (live and
  in background) already owns solar/other recharge for its batteries — if your mod doesn't
  generate real energy itself, there is nothing to report in that direction. A recharge-report
  method may be added later (additively, see Versioning) if a real use case needs it.

## Versioning

`RealBatteryPowerLedger.ContractVersion` (currently `3`) increments only for additive,
non-breaking changes — existing method signatures are frozen once shipped. Check
`RealBatteryPowerLedgerWrapper.ContractVersion` after `Init()` if your mod depends on a
feature added in a later version.

## Example

```csharp
if (RealBatteryPowerLedgerWrapper.Init())
{
    double ecUsed = 1234.0; // energy your mod already accounted for as spent
    double covered = RealBatteryPowerLedgerWrapper.ReportConsumedEc(vessel, ecUsed, 3600.0);
    if (covered < ecUsed - 0.01)
    {
        // the batteries couldn't fully cover it — covered is how much was actually applied;
        // reflect the shortfall back into your own accounting instead of discarding it
    }
}
```

## For BonVoyage specifically

This contract is the RealBattery-side half of the integration proposed to LisiasT (see
`M6_BonVoyage_PR/MESSAGE_TO_LISIAST.md` and `PLAN_BV_API_Integration.md` §B2). The intended
shape on BV's side is a `RealBatteryPowerSupply : Batteries` that:

- reports `GetAvailableEc(vessel)` as `Batteries.MaxAvailableEC` during `SystemCheck`, and
- calls `ReportConsumedEc(vessel, MaxUsedEC - CurrentEC, <elapsed time>)` from the
  reconciliation hook proposed for `Batteries` (called from `BVController.ProcessResources()`,
  the same moment the vessel becomes loaded again), then advances `CurrentEC` by exactly the
  amount *covered* — never blindly to `MaxUsedEC` — so a deficit only partially covered stays
  correctly outstanding instead of being silently written off.

BV never needs to know about `StoredCharge`, the 3600:1 ratio, wear, or `BatteryLife` — all of
that stays entirely on this side of the contract.
