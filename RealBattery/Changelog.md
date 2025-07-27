# Changelog

## v2.2.2

- Fixed a bug that caused errors in Module Manager when adding SystemHeat support to certain third-party batteries (notably BDB, OPT, HabTech, and US2)

---

## v2.2.1

- Fixed a bug causing Battery Health to drop to 0% if the vessel had been tested in the editor before launch.
- Battery Wear no longer applies in the editor.
- Self-discharge in runtme now only applies to disabled batteries.
- Batteries are now properly reset at launch (WearCounter and Health).
- SimulationMode now defaults to "Idle" instead of showing *NotFound* in the editor PAW.
- Improved runtime logging and code reliability.
- Fixed broken strings and improved localization

---

## v2.2.0

### Major Changes

- **Battery Toggle**: Each battery can now be manually enabled/disabled via the PAW or action groups.
- **Background Simulation**: Batteries now charge and discharge even when vessels are unloaded. Includes support for some third-party modules.
- **Battery Obsolescence**: Batteries wear down over time based on usage, reducing their maximum capacity.
- **Self-Discharge**: Idle batteries gradually lose charge over time.
- **Thermal Simulation**: Batteries now produce heat when charging/discharging and can suffer damage if overheated. *SystemHeat is now a hard dependency.*
- **Difficulty Settings**: A new menu lets you toggle features such as self-discharge, thermal simulation, and verbose logging.

### Minor Improvements

- Improved localization and corrected language strings.
- In the VAB/SPH, batteries now simulate their expected input/output based on selected Simulation Mode. This improves compatibility with *DynamicBatteryStorage* and *SystemHeat*.
- The PAW now shows additional data: estimated time to full charge/discharge, battery health and state of charge

---

## v 2.1.0

### Major
- Added C-rate as upper limit of charge/discharge rate (see the Wiki for details)
- Added **Kerb-O-Power** nuclear battery type
- Added `moduleActive` field to hide RealBattery stats in non-battery subtypes
- Restored blackline's fallback to avoid crash on game load whit incorrect .cfg

### Minor
- Fixed `StoredCharge` bar not showing in the PAW while in VAB/SPH (thanks to **JadeOfMaar** for suggestion)
- Fixed wrong description for Nichel batteries upgrade.
- Fixed battery type for BDB's Atlas A and Titan II reentry nosecones (Thermal).
- Rebalanced Thermal Battery stats
- Added _"Battery Manager"_ PAW collapsable group
- Improved descriptions