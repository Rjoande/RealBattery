# Roadmap & Future Ideas

## Fix
- [x] Apply battery consumption/wear in the background based on discharge/recharge when the ship is **grounded** or in **orbital shadow**.
- [x] Correct the "Day" and "Night" labels in the SolarSim log in the background.
- [ ] Display correct `Discharge Rate` in the PAW editor.
- [x] Use a single, consistent flow in `ApplyThermalEffects` (charge and discharge processed in the same logical path).

## Simulation and calculation improvements
- [x] More precise `ModuleEnergyEstimator`: output panels based on position, rotation, angle of incidence; tracking/static distinction; on surface.
- [ ] Automatic day length detection on Kerbin. The slider in the settings allows overriding with values ​​0-24, where 0 activates automatic detection.
- [ ] Low battery warning after scene exit, with cancellation if re-entering before the timeout. Option in the settings.
- [ ] PAW fields visible only if technology/upgrades are unlocked (`PartTechAvailable`).
- [ ] Hide PAW group if `moduleActive = false` (non-battery).
- [ ] Engineer Bonus: Improves thermal performance and slows degradation (`ThermalLoss`, `WearCounter`).
- [ ] **BonVoyage Compatibility**: Mod helper temporarily converts **SC → EC** to allow BonVoyage to correctly estimate battery life.

##Thermals and Failure
- [ ] Automatic battery shutdown in case of overheating/out of control. Global setting.
- [ ] In the event of a runaway, the battery either **shuts down** or generates **heat every frame** until disabled.
- [ ] Thermal batteries: produce heat but do not runaway (very high `TempRunaway` or thermal wear exclusion).

## New Battery Types
- [ ] Hf-178m2 (inspired by the _Hafnium controversy_): replaces NukeCell.
- [ ] KERBA (inspired by ZEBRA): rechargeable, high C-rate, low efficiency >60% SOC, 5–10 cycle life.
- [ ] TBat: Cannot be disabled, fixed discharge every cycle (`BatteryDisabled = false` + override `FixedUpdate`).
- [ ] Battery activation via staging (dedicated `KSPAction`, `activateOnStaging` field).

## User Interface and Interface
- [ ] Toggle between `BatteryHealth` and `CyclesLeft` in PAW (toggle `UI_ChooseOption`). Decimal formatting (<1) with `F1` or `F2`.
- [x] Optional SystemHeat mechanics: fallback to stock heat or completely disable it.

## Documentation and Support
- [ ] KSPedia: SC/EC system overview, battery types, in-flight use, background simulation, third-party mod integration, icons/textures for chemistries.

## Aesthetics
- [ ] Battery texture change, only with ReStock/Restock+/NFE (via B9PartSwitch or `ModulePartVariants`).

