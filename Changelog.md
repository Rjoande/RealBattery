# Changelog

## v2.3.0

### Major Changes

- **Low-power battery alerts**: Enable automatic alarms (stock or KAC) and inbox messages when a vessel is about to run out of power. This *should* also apply to vessels already in flight before 2.3, but it's not guaranteed.
- **Optional Stock Heat Simulation**: batteries can now produce and handle heat using the stock thermal system if *SystemHeat* is disabled or not installed. This option is selectable in the difficulty settings. *SystemHeat is no longer a hard dependency*.
- **Staging integration**: all batteries can now be armed for activation via staging, with a dedicated toggle and staging icon.
- **Thermal batteries overhaul**: thermal batteries now start inactive by default, provide continuous fixed power output once activated, and cannot be turned off afterward.
- **New KERBA batteries**: inspired from Zebra Batteries, they require pre-heating before activation, continuous power (or loop heat) to stay warm, and a controlled cooldown when manually shut down.
- **Self-runaway logic**: added hourly chance (configurable) in Hafnium batteries for a spontaneous meltdown. 
- **Tech-locked UI fields**: certain telemetry and diagnostics readouts (SoC, Time-to-Depletion, Health) now require dedicated upgrades to be unlocked in _Career_ and _Science_ modes; in Sandbox they remain fully visible.
- **Automatic battery shutdown**: once the *Protection Circuit Module* is unlocked, batteries will automatically disable themselves if they overheat beyond their safe temperature threshold.
- **EVA Battery Refurbishment**: Engineers on EVA can now restore worn-out batteries using SpareParts resource from the vessel, with cost scaled by battery capacity.

### Minor Improvements

- *Day length* for background simulation is now auto-detected from the homeworld. You can override it in settings (0 = Auto, otherwise 1–24 h).
- Added *polar mode* for background simulation while landed near poles (configurable).
- *Thermal runaway model*: batteries now generate heat autonomously once their temperature exceeds `TempRunaway`, even if disabled. Heat output scales with chemistry (defaults to DischargeRate; optional `RunawayHeatKW` field per subtype).
- *Engineer Bonus*: engineer level slightly boosts discharge output and reduces heat & degradation (up to 25% better); slight malus (-5%) with no engineers on board.
- *Battery health* field now shows remaining cycles left for rechargeable batteries.
- When a cell’s health drops below 80%, a localized system message (contract-style inbox alert) is generated once per part.
- *DischargeRate* is now shown correctly in the editor, and automatically updates when switching to a different subtype.
- RealBattery PAW group is now automatically hidden when the current B9 subtype does not include an active battery module (`moduleActive = false`).  
- Reorganized *Difficulty Settings* into three tabs.
- Rebalanced subtype costs to better follow career progression.
- Custom models & textures for tech nodes.
- General code cleanup and refactoring.
- Improved some localization strings.

---

## v2.2.4

- Further improved background simulation for partial night/day cycles (both orbital and surface).
- Internal code cleanup.

## v2.2.3

- Fixed poor synthax for Module Manager patches, for enhanced compatibility with other mods (thanks to **JadeOfMaar**).
- Added **Simplified Chinese** localization (thanks to **Aebestach**).

---

## v2.2.2

- Fixed a bug that caused errors in Module Manager when adding *SystemHeat* support to certain third-party batteries (notably BDB, OPT, HabTech, and US2).

---

## v2.2.1

- Fixed a bug causing Battery Health to drop to 0% if the vessel had been tested in the editor before launch.
- Battery Wear no longer applies in the editor.
- Self-discharge in runtime now only applies to disabled batteries.
- Batteries are now properly reset at launch (WearCounter and Health).
- *SimulationMode* now defaults to "Idle" instead of showing "NotFound" in the editor PAW.
- Improved runtime logging and code reliability.
- Fixed broken strings and improved localization.

---

## v2.2.0

### Major Changes

- **Battery Toggle**: Each battery can now be manually enabled/disabled via the PAW or action groups.
- **Background Simulation**: Batteries now charge and discharge even when vessels are unloaded. Includes support for some third-party modules.
- **Battery Obsolescence**: Batteries wear down over time based on usage, reducing their maximum capacity.
- **Self-Discharge**: Idle batteries gradually lose charge over time.
- **Thermal Simulation**: Batteries now produce heat when charging/discharging and can suffer damage if overheated. *SystemHeat is now a hard dependency*.
- **Difficulty Settings**: A new menu lets you toggle features such as self-discharge, thermal simulation, and verbose logging.

### Minor Improvements

- Improved localization and corrected language strings.
- In the VAB/SPH, batteries now simulate their expected input/output based on selected Simulation Mode. This improves compatibility with *DynamicBatteryStorage* and *SystemHeat*.
- The PAW now shows additional data: estimated time to full charge/discharge, battery health and state of charge.

---

## v 2.1.0

### Major Changes

- Added C-rate as upper limit of charge/discharge rate (see the Wiki for details).
- Added **Kerb-O-Power** nuclear battery type.
- Added `moduleActive` field to hide RealBattery stats in non-battery subtypes.
- Restored blackline's fallback to avoid crash on game load whit incorrect .cfg

### Minor Improvements

- Fixed `StoredCharge` bar not showing in the PAW while in VAB/SPH (thanks to **JadeOfMaar** for suggestion).
- Fixed wrong description for Nichel batteries upgrade.
- Fixed battery type for BDB's Atlas A and Titan II reentry nosecones (Thermal).
- Rebalanced Thermal Battery stats.
- Added _"Battery Manager"_ PAW collapsable group.
- Improved descriptions.