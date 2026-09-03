![logo](assets/images/RealBattery-logo-3.png)

**RealBattery Recharged** is a complete overhaul of the stock electric system in Kerbal Space Program, originally designed by Blackliner. 
It brings a more realistic and engaging battery simulation, adding depth to spacecraft design and power management without overcomplicating gameplay.

## Features

*RealBattery Recharged* replaces the simplistic stock EC storage system with a more nuanced model: batteries **store energy** (StoredCharge) and **supply power** (ElectricCharge) up to their rated capacity.  
Discharge and recharge occur dynamically based on system demand, charge level, and efficiency.

Each battery reports its energy/power density, charge/discharge efficiency, current state (Idle / Charging / Discharging), and more.  
The new system rewards thoughtful planning, buffering for peak loads, and progressive tech improvements.

### This mod includes:
- A dynamic battery module applied to all parts containing ElectricCharge
- Realistic simulation of power (kW) and energy (kWh), based on internal capacity and output limits
- Discharge and recharge mechanics with efficiency losses and power buffering
- A range of battery chemistries inspired by real-world technologies
- Tech-tree progression with unlockable battery upgrades per part
- Seamless integration with B9PartSwitch and localization support
- Optional language files to rename ElectricCharge and StoredCharge

> See also the [Changelog](https://github.com/Rjoande/RealBattery/blob/master/RealBattery/Changelog.md) for a list of newly-released features.

### Extras
Optional patches are included in the release to enhance immersion and realism. Extra patches are modular and can be removed or disabled if undesired:

- **Alternator Fix**: disables alternators on most rocket engines (for realism), but enables them on multi-mode engines like the *RAPIER*.
- **EC-to-current** and **EC-to-kW** localization packs: rename *Electric Charge* to *Electric Current*, or alternatively to *kW/kWh* (replacing EC/SC with power units). **Only install one at a time!** See the [wiki](https://github.com/Rjoande/RealBattery/wiki/Understanding-EC-and-SC-Units) for details.
- **Electric-pump-fed Engines**: the *Goldfish* and *Angora* engines from [Near Future Launch Vehicles](https://github.com/post-kerbin-mining-corporation/NearFutureLaunchVehicles/releases) now require electric charge to operate.
- **Fuel Cell Output**: fuel cells internal buffer aligns with actual electrical output.

To install an extra, simply place the corresponding patch into your `GameData` folder. See the `Extras/` folder in the release package for details.

## Dependencies

### Required:
- [Module Manager](https://github.com/sarbian/ModuleManager/releases)
- [B9PartSwitch](https://github.com/blowfishpro/B9PartSwitch/releases)
- [Community Resource Pack](https://github.com/UmbraSpaceIndustries/CommunityResourcePack/releases)

### Suggested/Recommended:
- [SystemHeat](https://github.com/post-kerbin-mining-corporation/SystemHeat/releases)
- [Community Tech Tree](https://github.com/post-kerbin-mining-corporation/CommunityTechTree/releases)
- [System Monitor (Dynamic Battery Storage)](https://github.com/post-kerbin-mining-corporation/DynamicBatteryStorage/releases)
- [DangIt! Continued](https://github.com/linuxgurugamer/DangIt/releases)
- [Bon Voyage](https://github.com/jarosm/KSP-BonVoyage) (requires [Harmony](https://github.com/KSPModdingLibs/HarmonyKSP))
- [Conformal Decals](https://git.offworldcolonies.nexus/drewcassidy/KSP-Conformal-Decals/releases)
- [LoadingTipsPlus](https://forum.kerbalspaceprogram.com/topic/142840-110x-loadingtipsplus-v198-17th-oct-2020/) (bundled with RealBattery since v3)
- [MFD Extended](https://github.com/Rjoande/MFD-Extension) *(adds an in-IVA battery management screen — see below; RealBattery works identically without it)*

## Installation

1. Remove any previous `RealBattery` install.
3. Download the latest release from the [Releases](https://github.com/Rjoande/RealBattery/releases) page.
2. Extract into your `GameData` folder.
4. Ensure [dependencies](#dependencies) are installed.
5. (Optional) Install the [extras](#extras) as you like.

> Starting a new save is recommended to enjoy the full tech progression. Installing mid-career is possible, but may affect in-flight vessels' power balance.

## External Mod Compatibility

RealBattery Recharged dynamically applies to any part with ElectricCharge, including most modded parts.  
Known compatibility or special support includes:

- Bon Voyage
- Airplane Plus
- Planetside Exploration Technologies
- Artemis Construction Kit
- Shuttle Orbiter Construction Kit
- Buran Orbiter Construction Kit
- Bluedog Design Bureau
- Grounded - Modular Vehicles
- HabTech 2
- Knes
- Mk22 Cockpits
- Mk3 Expansion
- Near Future Technologies
- OPT Spaceplane Continued
- reDIRECT
- Restock+
- Starship Expansion Project 
- Stockalike Station Parts Redux
- Tantares
- Tundra Exploration 
- Universal Storage II

> Parts from mods not listed above still benefit from RealBattery's automatic patching system. These will receive a default, generic set of subtypes. A number of third-party modules is also supported for background simulation. This includes *SystemHeat, CryoTanks, Near Future Tech, SpaceDust, SCANsat, Snacks*.

### Bon Voyage compatibility

Bon Voyage's rover autopilot needs to know how much battery capacity a vessel has and drain it as the rover travels. Because RealBattery replaces stock `ElectricCharge` storage with its own `StoredCharge` resource, Bon Voyage can't see that capacity without a small integration. There are two ways this is provided, and you don't need to do anything for either of them:

- **Native support** (preferred): a pull request to [Bon Voyage /L](https://github.com/net-lisias-ksp/BonVoyage) teaches it to read RealBattery's `StoredCharge` directly. If you're running a Bon Voyage build that already includes it, that's what you're using — nothing else is involved.
- **Fallback bridge**: for players on a Bon Voyage build that predates native support, RealBattery ships an optional companion plugin, `RealBatteryBVBridge.dll`, that applies the same integration at runtime via [HarmonyKSP](https://github.com/KSPModdingLibs/HarmonyKSP). It only activates if both Bon Voyage and HarmonyKSP are installed, and automatically detects and steps aside if it finds native support already present — so upgrading Bon Voyage never causes the two to conflict. If HarmonyKSP isn't installed, RealBattery itself is entirely unaffected; only this optional bridge is inactive.

### MFD Extended integration

If [MFD Extended](https://github.com/Rjoande/MFD-Extension) is installed, RealBattery ships its own **BMS** bay on the shared IVA multi-function display — no configuration needed, it's detected automatically. It has two screens, reached by pressing the BMS button on the monitor:

- **Battery Management** (the default view): the active vessel's overall charge, net power, and time to depletion, plus a per-battery table with chemistry, charge, health, and any fault state (overheating, thermal runaway, disabled, etc.).
- **Fleet Overview**: press the BMS button again to switch here. Shows every RealBattery-equipped vessel in the save at a glance — charge, net rate, and time-to-empty, marked as live data or an estimate depending on whether that vessel is currently loaded — sorted alphabetically with your own ship pinned at the top. Press BMS again to go back.

Both screens support scrolling through longer lists with the monitor's own onscreen keys. Without MFD Extended installed, RealBattery behaves identically in every other respect.

## Contributing

Pull requests, translations, and feedback are welcome!  
Please fork the repository and submit a PR against the `master` branch.  
To report bugs or suggest features, open an issue or post in the [KSP Forum Thread](https://forum.kerbalspaceprogram.com/topic/227955-112x-realbattery-recharged-v2-realistic-battery-simulation/).

## Translations

Localization is supported. If you'd like to help translate RealBattery into your language, check out the `/Localization` folder and submit a pull request.

Current languages:
- English (`en-us`)
- Italian (`it-it`)
- Spanish (`en-es`)
- French (`fr-fr`)
- Simplified Chinese (`zh-cn`) by **Aebestach**

## Licensing

Original mod by **Blackliner**, expanded and maintained by **Rjoande**.

Licensed under the **MIT licence**.
