# RealBattery module templates
Below are sample configurations for RealBattery modules and B9PartSwitch subtypes. Feel free to copy and paste the modules you like to create your own custom patches.

## Patch outline
These are basic patch structures with logical operators to determine battery capacity in multiple parts at once. The parts must contain `ElectricCharge` and `StoredCharge` resources. Amount is optional since they will be overwritten by b9PartSwitch, but it is advised to use the stats of the first subtype for visual consistency. In the example are Lead-acid battery stats.
This is the same structure used by the patches included with RealBattery. It is recommended to always use B9PartSwitch to simplify the automatic calculation of mass and cost using `tankTypes` -- it works also with single-chemistry parts. **Important:** reset `cost` and `mass` for standalone batteries only!

### Generic patch structure
```
@PART
{
    RBbaseVolume = #$/RESOURCE[ElectricCharge]/maxAmount$
    @RBbaseVolume *= 0.3

    @cost = 0 //comment or remove this line if the battery is within another part (like pods...)
    @mass = 0 //comment or remove this line if the battery is within another part (like pods...)

    @RESOURCE[ElectricCharge]
    {
        @maxAmount = #$../RBbaseVolume$
        @maxAmount *= 0.1
        @amount = #$../RBbaseVolume$
        @amount *= 0.1
    }
    RESOURCE
    {
        name = StoredCharge
        maxAmount = #$../RBbaseVolume$
        @maxAmount *= 0.02
        amount = #$../RBbaseVolume$
        @amount *= 0.02
    }
}
```

### Filters for standalone battery parts
```
:HAS[@RESOURCE[ElectricCharge],!RESOURCE[StoredCharge],!MODULE[RealBattery],!MODULE[ModuleEnginesFX],!MODULE[ModuleGenerator],!MODULE[ModuleResourceConverter],!MODULE[ModuleSystemHeatFissionFuelContainer],!MODULE[ModuleCommand]]
```

### Filters for crewed parts
```
:HAS[@RESOURCE[ElectricCharge],!RESOURCE[StoredCharge],!MODULE[RealBattery],!MODULE[ModuleEnginesFX],!MODULE[ModuleGenerator],!MODULE[ModuleResourceConverter],!MODULE[ModuleSystemHeatFissionFuelContainer],#CrewCapacity[>0]]
```

### Filters for probe and avionics parts
```
:HAS[@RESOURCE[ElectricCharge],!RESOURCE[StoredCharge],!MODULE[RealBattery],!MODULE[ModuleEnginesFX],!MODULE[ModuleGenerator],!MODULE[ModuleResourceConverter],!MODULE[ModuleSystemHeatFissionFuelContainer],@MODULE[ModuleCommand]:HAS[#minimumCrew[0]]]
```

## Main RealBattery module
This will be the baseline of you module. If you only want one tipe of battery in you part, just populate this module with the values from `MODULE:DATA` listed in the `SUBTYPES` section (shown below). Remember to specify `BatteryTypeDisplayName` (can be copied from the subtype's `title`). `BatteryTypeDisplayName` and `Crate` are optional with B9PartSwitch, but are encouraged for consistency in the editor tooltip.

```
MODULE
{
    name = RealBattery
    BatteryTypeDisplayName = Default //optional
    Crate = 1 //optional
    HighEClevel = 0.95
    LowEClevel = 0.90
}
```

## B9PartSwitch main module
```
MODULE
{
    name = ModuleB9PartSwitch
    moduleID = batterySwitch
    switcherDescription = #LOC_RB_batterySwitcherDescription
    switcherDescriptionPlural = #LOC_RB_batterySwitcherDescriptionPlural
    baseVolume = #$../RBbaseVolume$
}
```

## B9 Subtypes
You can also copy the values from the `DATA` node to the main RealBattery module (shown above), if you want to use just one chemistry in your part. Remember to remove (or comment with `//`) the value `upgradeRequired = ...` from the first subtype!

        ### Thermal battery (non-rechargeable)
        ```
        SUBTYPE
        {
            name = TBat
            title = #LOC_RB_title_TBat
            descriptionSummary = #LOC_RB_descSum_TBat
            descriptionDetail = #LOC_RB_descDet_TBat
            tankType = TBat
            defaultSubtypePriority = 2
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = TBat
                }
            }
        }
        ```

        ### Lead-acid

        ```
        SUBTYPE
        {
            name = PbAc
            title = #LOC_RB_title_PbAc
            descriptionSummary = #LOC_RB_descSum_PbAc
            descriptionDetail = #LOC_RB_descDet_PbAc
            tankType = PbAc
            defaultSubtypePriority = 3

            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = PbAc
                }
            }
        }
        ```

        ### Nickel-Cadmium
        ```
        SUBTYPE
        {
            name = NiCd
            title = #LOC_RB_title_NiCd
            descriptionSummary = #LOC_RB_descSum_NiCd
            descriptionDetail = #LOC_RB_descDet_NiCd
            tankType = NiCd
            upgradeRequired = RB_UpgradeNiCd
            defaultSubtypePriority = 4
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = NiCd
                }
            }
        }
        ```

        ### Nickel-Zinc
        ```
        SUBTYPE
        {
            name = NiZn
            title = #LOC_RB_title_NiZn
            descriptionSummary = #LOC_RB_descSum_NiZn
            descriptionDetail = #LOC_RB_descDet_NiZn
            tankType = NiZn
            defaultSubtypePriority = 5
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = NiZn
                }
            }
        }
        ```

        ### Silver-oxide (non-rechargeable)
        ```
        SUBTYPE
        {
            name = AgOx
            title = #LOC_RB_title_AgOx
            descriptionSummary = #LOC_RB_descSum_AgOx
            descriptionDetail = #LOC_RB_descDet_AgOx
            tankType = AgOx
            upgradeRequired = RB_UpgradeAgZn
            defaultSubtypePriority = 6
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = AgOx
                }
            }
        }
        ```

        ### Silver-Zinc
        ```
        SUBTYPE
        {
            name = AgZn
            title = #LOC_RB_title_AgZn
            descriptionSummary = #LOC_RB_descSum_AgZn
            descriptionDetail = #LOC_RB_descDet_AgZn
            tankType = AgZn
            upgradeRequired = RB_UpgradeAgZn
            defaultSubtypePriority = 6
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = AgZn
                }
            }
        }
        ```

        ### Nickel-Hydrogen
        ```
        SUBTYPE
        {
            name = NiH2
            title = #LOC_RB_title_NiH2
            descriptionSummary = #LOC_RB_descSum_NiH2
            descriptionDetail = #LOC_RB_descDet_NiH2
            tankType = NiH2
            defaultSubtypePriority = 7
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = NiH2
                }
            }
        }
        ```

        ### ZEBRA battery
        ```
        SUBTYPE
        {
            name = RBZebra
            title = #LOC_RB_title_Zebra
            descriptionSummary = #LOC_RB_descSum_Zebra
            descriptionDetail = #LOC_RB_descDet_Zebra
            tankType = RBZebra
            upgradeRequired = RB_UpgradeZebra
            defaultSubtypePriority = 8
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = RBZebra
                }
            }
        }
        ```

        ### Lithium-ion
        ```
        SUBTYPE
        {
            name = Li_ion
            title = #LOC_RB_title_Li_ion
            descriptionSummary = #LOC_RB_descSum_Li_ion
            descriptionDetail = #LOC_RB_descDet_Li_ion
            tankType = Li_ion
            upgradeRequired = RB_UpgradeLiIon
            defaultSubtypePriority = 9
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = Li_ion
                }
            }
        }
        ```

        ### Lithium-polymer
        ```
        SUBTYPE
        {
            name = Li_poly
            title = #LOC_RB_title_Li_poly
            descriptionSummary = #LOC_RB_descSum_Li_poly
            descriptionDetail = #LOC_RB_descDet_Li_poly
            tankType = Li_poly
            upgradeRequired = RB_UpgradeLiPoly
            defaultSubtypePriority = 10
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = Li_poly
                }
            }
        }
        ```

        ### Graphene
        ```
        SUBTYPE
        {
            name = Graphene
            title = #LOC_RB_title_Graphene
            descriptionSummary = #LOC_RB_descSum_Graphene
            descriptionDetail = #LOC_RB_descDet_Graphene
            tankType = Graphene
            upgradeRequired = RB_UpgradeGraphene
            defaultSubtypePriority = 11
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = Graphene
                }
            }
        }
        ```

        ### Solid-State Battery
        ```
        SUBTYPE
        {
            name = SSB
            title = #LOC_RB_title_SSB
            descriptionSummary = #LOC_RB_descSum_SSB
            descriptionDetail = #LOC_RB_descDet_SSB
            tankType = SSB
            upgradeRequired = RB_UpgradeSSB
            defaultSubtypePriority = 12
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = SSB
                }
            }
        }
        ```

        ### Hafnium-178m2 (non-rechargeable)
        ```
        SUBTYPE
        {
            name = NukeCell
            title = #LOC_RB_short_Nuke
            descriptionSummary = #LOC_RB_descSum_Nuke
            descriptionDetail = #LOC_RB_descDet_Nuke
            tankType = NukeCell
            upgradeRequired = RB_UpgradeNuke
            defaultSubtypePriority = 1
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = NukeCell
                }
            }
        }
        ```

        ### VANADIUM REDOX FLOW BATTERY  (VRFB)
        SUBTYPE
        {
            name = VRFB
            title = #LOC_RB_title_VRFB
            descriptionSummary = #LOC_RB_descSum_VRFB
            descriptionDetail = #LOC_RB_descDet_VRFB
            tankType = RB_VRFB
            upgradeRequired = RB_UpgradeVRFB
            defaultSubtypePriority = 8
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = VRFB
                }
            }
        }

        ### MAGNESIUM-ANTIMONY LIQUID METAL BATTERY
        
        SUBTYPE
        {
            name = MgSb
            title = #LOC_RB_title_MgSb
            descriptionSummary = #LOC_RB_descSum_MgSb
            descriptionDetail = #LOC_RB_descDet_MgSb
            tankType = RB_MgSb
            upgradeRequired = RB_UpgradeMgSb
            defaultSubtypePriority = 9
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = MgSb
                }
            }
        }

        ### SUPERCONDUCTING MAGNETIC ENERGY STORAGE
        SUBTYPE
        {
            name = SMES
            title = #LOC_RB_title_SMES
            descriptionSummary = #LOC_RB_descSum_SMES
            descriptionDetail = #LOC_RB_descDet_SMES
            tankType = RB_SMES
            upgradeRequired = RB_UpgradeSMES
            defaultSubtypePriority = 1
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = SMES
                }
            }
        }

        ### HOLMIUM-166m
        SUBTYPE
        {
            name = HoCell
            title = #LOC_RB_title_HoCell
            descriptionSummary = #LOC_RB_descSum_HoCell
            descriptionDetail = #LOC_RB_descDet_HoCell
            tankType = RB_HoCell
            upgradeRequired = RB_UpgradeHoCell
            defaultSubtypePriority = 1
            MODULE
            {
                IDENTIFIER
                {
                    name = RealBattery
                }
                DATA
                {
                    ChemistryID = HoCell
                }
            }
        }

// TODO:
// PARTUPGRADE => partIcon
// decals
// #LOC keys