# RealBattery module templates
Below are sample configurations for RealBattery modules and B9PartSwitch subtypes. Feel free to copy and paste the modules you like to create your own custom patches.

## Patch outline
These are basic patch structures with logical operators to determine battery capacity in multiple parts at once. The parts must contain `ElectricCharge` and `StoredCharge` resources. Amount is optional since they will be overwritten by b9PartSwitch, but it is advised to use the stats of the first subtype for visual consistency. In the example are Lead-acid battery stats.
This is the same structure used by the patches included with RealBattery. It is recommended to always use B9PartSwitch to simplify the automatic calculation of mass and cost using `tankTypes` -- it works also with single-chemistry parts. **Important:** reset `cost` and `mass` for standalone batteries only!

### Generic patch structure
```
@PART[]
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

### Lead-acid

```
SUBTYPE
{
    name = PbAc
    title = #LOC_RB_title_PbAc
    descriptionSummary = #LOC_RB_descSum_PbAc
    descriptionDetail = #LOC_RB_descDet_PbAc
    tankType = PbAc
    defaultSubtypePriority = 1

    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_PbAc

            Crate = 0.2

            SelfDischargeRate = 0.045
            CycleDurability = 75

            ThermalLoss = 0.3
            TempOverheat = 390
            TempRunaway = 490

            ChargeEfficiencyCurve
            {
                key = 0.0 0.55
                key = 0.3 0.65
                key = 0.5 0.70
                key = 0.8 0.60
                key = 1.0 0.50
            }
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
    defaultSubtypePriority = 2
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_AgOx

            HighEClevel = 2
            Crate = 1

            SelfDischargeRate = 0.025
            CycleDurability = 1

            ThermalLoss = 0.3
            TempOverheat = 370
            TempRunaway = 470

            ChargeEfficiencyCurve
            {
                key = 0.0 0.0
                key = 1.0 0.0
            }
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
    defaultSubtypePriority = 3
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_AgZn

            Crate = 1.5

            SelfDischargeRate = 0.025
            CycleDurability = 50

            ThermalLoss = 0.3
            TempOverheat = 370
            TempRunaway = 470

            ChargeEfficiencyCurve
            {
                key = 0.0 0.60
                key = 0.3 0.75
                key = 0.6 0.85
                key = 0.85 0.70
                key = 1.0 0.50
            }
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
    descriptionDetail = #LOC_RB_descDet_AgZn
    tankType = NiZn
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
            BatteryTypeDisplayName = #LOC_RB_short_NiZn

            Crate = 0.7

            SelfDischargeRate = 0.04
            CycleDurability = 800

            ThermalLoss = 0.15
            TempOverheat = 340
            TempRunaway = 440

            ChargeEfficiencyCurve
            {
                key = 0.0  1.0
                key = 0.6  0.98
                key = 0.85 0.90
                key = 0.95 0.70
                key = 1.0  0.5
            }
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
            BatteryTypeDisplayName = #LOC_RB_short_NiCd

            Crate = 0.5

            SelfDischargeRate = 0.03
            CycleDurability = 500

            ThermalLoss = 0.2
            TempOverheat = 340
            TempRunaway = 440

            ChargeEfficiencyCurve
            {
                key = 0.0 0.70
                key = 0.5 0.75
                key = 0.9 0.80
                key = 1.0 0.65
            }
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
    //upgradeRequired = RB_UpgradeNiCd
    defaultSubtypePriority = 5
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_NiH2

            Crate = 0.3

            SelfDischargeRate = 0.15
            CycleDurability = 20000

            ThermalLoss = 0.15
            TempOverheat = 360
            TempRunaway = 460

            ChargeEfficiencyCurve
            {
                key = 0.0 0.80
                key = 0.2 0.85
                key = 0.8 0.85
                key = 1.0 0.80
            }
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
    defaultSubtypePriority = 6
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_Li_ion

            Crate = 1

            SelfDischargeRate = 0.01
            CycleDurability = 1000

            ThermalLoss = 0.15
            TempOverheat = 435
            TempRunaway = 535

            ChargeEfficiencyCurve
            {
                key = 0.0 0.80
                key = 0.3 0.85
                key = 0.6 0.90
                key = 0.85 0.85
                key = 1.0 0.80
            }
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
    defaultSubtypePriority = 7
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_Li_poly

            Crate = 3

            SelfDischargeRate = 0.015
            CycleDurability = 500

            ThermalLoss = 0.25
            TempOverheat = 420
            TempRunaway = 470

            ChargeEfficiencyCurve
            {
                key = 0.0 0.90
                key = 0.2 0.95
                key = 0.8 0.95
                key = 1.0 0.90
            }
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
    defaultSubtypePriority = 8
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_title_Graphene

            Crate = 5

            SelfDischargeRate = 0.002
            CycleDurability = 2500

            ThermalLoss = 0.03
            TempOverheat = 450
            TempRunaway = 550

            ChargeEfficiencyCurve
            {
                key = 0.0   0.90
                key = 0.25  0.96
                key = 0.5   0.97
                key = 0.75  0.96
                key = 1.0   0.92
            }
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
    defaultSubtypePriority = 9
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_SSB

            Crate = 2

            SelfDischargeRate = 0.003
            CycleDurability = 10000

            ThermalLoss = 0.05
            TempOverheat = 470
            TempRunaway = 570

            ChargeEfficiencyCurve
            {
                key = 0.0 0.90
                key = 0.2 0.95
                key = 0.8 0.95
                key = 1.0 0.90
            }
        }
    }
}
```

### Thermal battery (non-rechargeable)
```
SUBTYPE
{
    name = TBat
    title = #LOC_RB_title_TBat
    descriptionSummary = #LOC_RB_descSum_TBat
    descriptionDetail = #LOC_RB_descDet_TBat
    tankType = TBat
    defaultSubtypePriority = 0
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_TBat
            
            HighEClevel = 2
            Crate = 20

            SelfDischargeRate = 0
            CycleDurability = 1

            ThermalLoss = 0.6
            TempOverheat = 900
            TempRunaway = 1150

            ChargeEfficiencyCurve
            {
                key = 0.0 0.0
                key = 1.0 0.0
            }
        }
    }
}
```
### Nuclear battery (non-rechargeable)
```
SUBTYPE
{
    name = NukeCell
    title = #LOC_RB_short_Nuke
    descriptionSummary = #LOC_RB_descSum_Nuke
    descriptionDetail = #LOC_RB_descDet_Nuke
    tankType = NukeCell
    upgradeRequired = RB_UpgradeNuke
    defaultSubtypePriority = 0
    MODULE
    {
        IDENTIFIER
        {
            name = RealBattery
        }
        DATA
        {
            BatteryTypeDisplayName = #LOC_RB_short_Nuke

            HighEClevel = 2
            Crate = 0.001

            SelfDischargeRate = 0.0001            
            CycleDurability = 1

            ThermalLoss = 0.4
            TempOverheat = 1000
            TempRunaway = 1200

            ChargeEfficiencyCurve
            {
                key = 0.0 0.0
                key = 1.0 0.0
            }
        }
    }
}
```