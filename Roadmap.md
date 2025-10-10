# Roadmap – Future Ideas RealBattery Recharged

## Fix
- [x] Apply battery consumption/wear in the background based on discharge/recharge when the ship is **landed** or in **orbital shadow**.
- [x] Correct "Day" and "Night" labels in the SolarSim log.
- [ ] **Verify/display correct Discharge Rate** in the PAW editor.
- [x] Use a single consistent flow in `ApplyThermalEffects` (charge and discharge processed in the same logical path).

## Simulation and calculation improvements
- [x] More precise `ModuleEnergyEstimator`: panel output based on position, rotation, angle of incidence; tracking/static distinction; on surface.
- [x] Automatic day length detection on Kerbin. The slider in the settings allows override with values ​​from 0 to 24, where 0 activates automatic detection.
- [x] Low battery warning after exiting the scene, canceled if re-entered before the timeout. Option in settings.
- [ ] PAW fields visible only if technology/upgrades are unlocked (`PartTechAvailable`).
- [ ] Hide PAW group if `moduleActive = false` (non-battery).
- [ ] Engineer Bonus: Improves thermal performance and slows degradation (`ThermalLoss`, `WearCounter`).
- [ ] **BonVoyage Compatibility**: Mod helper temporarily converts **SC → EC** to allow BonVoyage to correctly estimate battery life.

## Thermals and Failures
- [ ] Automatic battery deactivation in overheat/runaway. Global setting.
- [ ] In the event of runaway, the battery either **turns off** or generates **heat every frame** until disabled.
- [x] Thermal batteries: produce heat but do not runaway (very high TempRunaway or thermal wear exclusion).

## New battery types
- [x] Hf-178m2 (inspired by the _Hafnium controversy_): replaces NukeCell.
- [x] KERBA (inspired by ZEBRA): rechargeable, high C-rate, low efficiency >60% SOC, 5–10 cycle life.
- [x] TBat: cannot be disabled, fixed drain every cycle (`BatteryDisabled = false` + override `FixedUpdate`).
- [x] Battery activation via staging (dedicated `KSPAction`, `activateOnStaging` field).

## UI & Interface
- [ ] Switch in PAW between `BatteryHealth` and `CyclesLeft` (toggle or `UI_ChooseOption`). Decimal formatting (<1) with `F1` or `F2`.
- [x] Optional SystemHeat mechanics: fallback to stock heat or completely disable it.

## Documentation & Support
- [ ] KSPedia: SC/EC system overview, battery types, flight usage, background simulation, third-party mod integration, icons/textures for chemistries.

## Aesthetics
- ~~[ ] Texture switch for batteries, only with ReStock/Restock+/NFE (via B9PartSwitch or `ModulePartVariants`).~~
- [ ] *Conformal Decals* stickers