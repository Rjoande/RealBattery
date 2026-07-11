# RealBatteryExpansion — Migration Manifest (Phase 0 audit, READ-ONLY)

**Generated:** 2026-06-27 · **Status:** Phase 0 complete, awaiting Pietro review.
**Scope:** migrate `HoCell, VRFB, MgSb, SMES` out of core into `RealBattery/GameData/RealBatteryExpansion/`.
**No files were changed by this audit** (other than this manifest).

> ⚠️ This audit found **two blockers beyond the plan's three critical checks**, plus the
> confirmed cross-boundary EVAupgrade chain. See [§5 Critical Checks](#5-critical-checks). The
> migration is materially larger and more entangled than `migrate-expansions.yaml` assumes — read
> §4 and §5 before approving Phase 1.

---

## 1. Path verification vs `meta.source_files`

| Plan path | Real path | Status |
|---|---|---|
| `GameData/RealBattery/settings/Chemistries.cfg` | `RealBattery/GameData/RealBattery/settings/Chemistries.cfg` | ⚠️ extra `RealBattery/` prefix |
| `GameData/RealBattery/settings/B9TankTypes.cfg` | `RealBattery/GameData/RealBattery/settings/B9TankTypes.cfg` | ⚠️ extra prefix |
| `GameData/RealBattery/settings/upgrades_subtypes.cfg` | `RealBattery/GameData/RealBattery/settings/upgrades_subtypes.cfg` | ⚠️ extra prefix |
| `GameData/RealBattery/Localization/<lang>.cfg` | `RealBattery/GameData/RealBattery/`**`localization`**`/<lang>.cfg` | ⚠️ extra prefix **+ lowercase folder** |

- Languages present: `en-us, it-it, es-es, fr-fr, zh-cn` — all 5 ✅
- **Confirmed target root (Pietro):** `RealBattery/GameData/RealBatteryExpansion/` (sibling of core
  `RealBattery` inside `GameData`).

---

## 2. Definition blocks to migrate (file + line range)

### 2.1 `settings/Chemistries.cfg` — `REALBATTERY_CHEMISTRY` (with section-header comments)
| Chem | Section header | Node body |
|---|---|---|
| HoCell | 624–629 | 630–669 |
| VRFB   | 671–675 | 676–716 |
| MgSb   | 718–721 | 722–763 |
| SMES   | 765–769 | 770–809 |

### 2.2 `settings/B9TankTypes.cfg` — `B9_TANK_TYPE` (no header comments in this file)
| Tank type name | Lines |
|---|---|
| RB_HoCell | 275–294 |
| RB_VRFB   | 296–315 |
| RB_MgSb   | 317–336 |
| RB_SMES   | 338–357 |

### 2.3 `settings/upgrades_subtypes.cfg` — dummy `PART` + `PARTUPGRADE` + CTT overrides
| Chem | Dummy PART | PARTUPGRADE | CTT `@PARTUPGRADE:NEEDS[CommunityTechTree]` |
|---|---|---|---|
| VRFB   | RB_VRFB 250–279   | RB_UpgradeVRFB 460–472   | 571–574 |
| MgSb   | RB_MgSb 281–310   | RB_UpgradeMgSb 488–500   | 581–584 |
| SMES   | RB_SMES 312–341   | RB_UpgradeSMES 502–514   | 586–589 |
| HoCell | RB_HoCell 343–372 | RB_UpgradeHoCell 530–542 | 596–599 |

---

## 3. Assets (model + textures) — to relocate into `<Chem>/Assets/`

All four PART `MODEL { model = RealBattery/assets/battery/<Chem>/model }` references resolve to
existing folders under `RealBattery/GameData/RealBattery/assets/battery/`:

| Chem | Asset folder contents | Referenced by (PART `model =`) |
|---|---|---|
| HoCell | `model.mu`, `ksp_l_batteryPack_diff.dds`, `ksp_l_batteryPack_normal.dds` | `RealBattery/assets/battery/HoCell/model` (upgrades_subtypes.cfg:350) |
| SMES   | `model.mu`, `ksp_l_batteryPack_diff.dds`, `ksp_l_batteryPack_normal.dds` | `RealBattery/assets/battery/SMES/model` (upgrades_subtypes.cfg:319) |
| MgSb   | `model.mu`, `model000.dds` | `RealBattery/assets/battery/MgSb/model` (upgrades_subtypes.cfg:288) |
| VRFB   | `model.mu`, `model000.dds` | `RealBattery/assets/battery/VRFB/model` (upgrades_subtypes.cfg:257) |

- No explicit `texture =` overrides in the PART/PARTUPGRADE nodes; textures travel with the `.mu`
  folder. New path after move: `RealBatteryExpansion/<Chem>/Assets/model`.
- ⚠️ The dummy PARTs are **icon-only** parts. The migrated SUBTYPEs in the part patches (§4) do
  **not** carry their own models — they rely on the host part's geometry. The `.mu` assets are used
  only by the dummy icon PARTs + PARTUPGRADE `partIcon`. Confirm during Phase 2 that nothing else in
  core references these asset paths.

---

## 4. SUBTYPE injection sites — **the real surface area**

The plan assumes a few isolated per-chemistry `@PART` patches. In reality each migrated chemistry's
SUBTYPE is embedded inside **shared multi-chemistry `ModuleB9PartSwitch` blocks** that also contain
core chemistries. Sites below (file : SUBTYPE `name=` line → host `@PART`):

### VRFB  (tankType `RB_VRFB`, upgradeRequired `RB_UpgradeVRFB`)
| File | Line | Host part(s) |
|---|---|---|
| 01_Stock/RB_Stock_Batteries.cfg | 136 | `batteryPack` |
| 01_Stock/RB_Stock_Batteries.cfg | 567 | `ksp_r_largeBatteryPack` |
| 01_Stock/RB_Stock_Batteries.cfg | 771 | `batteryBank,batteryBankLarge` |
| 01_Stock/RB_Stock_Crewed_Shuttle-cupola.cfg | 86 | `mk3Cockpit_Shuttle,cupola` |
| 01_Stock/RB_Stock_Probes_core_HECS2.cfg | 86 | `HECS2_ProbeCore` |
| 02_Mods/Mk3Expansion.cfg | 86 | `M3X...Cockpit` (NEEDS Mk3Expansion) |
| 02_Mods/NearFuture.cfg | 411 | `command-125-1` (NEEDS NearFutureSpacecraft) |
| 02_Mods/NearFuture_Batteries.cfg | 159, 409, 613 | B-800 / B-6K&12K / nflv stacks |
| 02_Mods/ReStockPlus_Batteries.cfg | 136 | `restock-battery-1875-1` |

### MgSb  (tankType `RB_MgSb`, upgradeRequired `RB_UpgradeMgSb`)
| File | Line | Host part(s) |
|---|---|---|
| 01_Stock/RB_Stock_Batteries.cfg | 182 | `batteryPack` |
| 01_Stock/RB_Stock_Batteries.cfg | 613 | `ksp_r_largeBatteryPack` |
| 01_Stock/RB_Stock_Batteries.cfg | 817 | `batteryBank,batteryBankLarge` |
| 02_Mods/NearFuture_Batteries.cfg | 205, 455, 659, 924 | B-800 / B-6K&12K / nfex / nflv |
| 02_Mods/ReStockPlus_Batteries.cfg | 182, 386 | Z-2.5K / Z-10K |

### SMES  (tankType `RB_SMES`, upgradeRequired `RB_UpgradeSMES`)
| File | Line | Host part(s) |
|---|---|---|
| 01_Stock/RB_Stock_Batteries.cfg | 840 | `batteryBank,batteryBankLarge` |
| 02_Mods/NearFuture_Batteries.cfg | 947 | `nflv-battery-stack-*` |
| 02_Mods/ReStockPlus_Batteries.cfg | 205, 409 | Z-2.5K / Z-10K |

### HoCell  (tankType `RB_HoCell`, upgradeRequired `RB_UpgradeHoCell`)
| File | Line | Host part(s) |
|---|---|---|
| 01_Stock/RB_Stock_Batteries.cfg | 386 | `batteryBankMini` |
| 01_Stock/RB_Stock_Batteries.cfg | 886 | `batteryBank,batteryBankLarge` |
| 01_Stock/RB_Stock_Probes_guidance.cfg | 154 | `probeStackSmall,probeStackLarge` |
| 02_Mods/NearFuture.cfg | 612 | NFS command pods |
| 02_Mods/NearFuture_Batteries.cfg | 251, 501 | B-800 / B-6K&12K |
| 02_Mods/ReStockPlus.cfg | 241 | `restock-mk2-pod` |
| 02_Mods/ReStockPlus_Batteries.cfg | 251 | Z-2.5K |
| 02_Mods/OPT.cfg | 306 | `mk3Cockpit_Airliner` (NEEDS OPT) |

> **ModuleManager pass directives observed on hosts:** stock hosts use bare `@PART[...]`; mod hosts
> use `:NEEDS[<mod>]`. None of the migrated-subtype hosts carry `:FOR/:AFTER/:BEFORE` on the part
> header itself (those live on the generic `zzz_RealBattery`/`ZZZ_REALBATTERY` passes elsewhere).
> Preserve `:NEEDS[...]` exactly when consolidating.

---

## 5. Critical Checks

### ✅ Check ① — Shared PARTUPGRADE across chemistries: **PASS (none shared)**
Each chemistry owns a 1:1 PARTUPGRADE (`RB_UpgradeVRFB/MgSb/SMES/HoCell`), referenced only by its
own subtypes' `upgradeRequired`. No PARTUPGRADE is shared between two chemistries.
*(But see Blocker B — these upgrades are referenced by CORE parts.)*

### 🟢 Check ② — EVAupgrade cross-boundary chain: **RESOLVED (Pietro-confirmed approach)**
One cross-boundary edge type, **`RBZebra` (CORE) → `MgSb` (migrated)**, at 8 sites in 3 files:

| File | Line | Source subtype | EVAupgrade target |
|---|---|---|---|
| 01_Stock/RB_Stock_Batteries.cfg | 537 | RBZebra (core) | MgSb |
| 01_Stock/RB_Stock_Batteries.cfg | 741 | RBZebra (core) | MgSb |
| 02_Mods/NearFuture_Batteries.cfg | 129 | RBZebra (core) | MgSb |
| 02_Mods/NearFuture_Batteries.cfg | 379 | RBZebra (core) | MgSb |
| 02_Mods/NearFuture_Batteries.cfg | 583 | RBZebra (core) | MgSb |
| 02_Mods/NearFuture_Batteries.cfg | 872 | RBZebra (core) | MgSb |
| 02_Mods/ReStockPlus_Batteries.cfg | 106 | RBZebra (core) | MgSb |
| 02_Mods/ReStockPlus_Batteries.cfg | 333 | RBZebra (core) | MgSb |

- `VRFB → SMES` edges (RB_Stock_Batteries.cfg:787, ReStockPlus_Batteries.cfg:152) — see Check ③-A:
  these are a **mistake** and will be removed (VRFB → `none`), not kept.
- No migrated→core edges exist. Nothing else points into VRFB, MgSb, SMES or HoCell as a target.

**Confirmed resolution (two parts):**
1. **Core side** — set `EVAupgrade = none` on the CORE `RBZebra` subtype at all 8 sites. RBZebra
   becomes terminal in core; removing the expansion leaves a valid (if shorter) chain.
2. **Expansion side** — `MgSb_patches.cfg` re-establishes `RBZebra → MgSb` **only for the parts that
   originally had it**, via a B9PS DATA-edit patch gated on the expansion. Validated MM syntax in
   [§9 Pattern 2](#9-proposed-unified-expansion-patch-patterns-for-approval).

**Affected host parts for the RBZebra→MgSb compat patch (union of the 8 sites):**
`ksp_r_largeBatteryPack`, `batteryBank`, `batteryBankLarge` (stock) ·
`battery-rad-125`, `battery-0625`, `battery-125`, `battery-25`, `nflv-battery-stack-5-1`,
`nflv-battery-stack-75-1` (NearFuture) · `restock-battery-1875-1`, `restock-battery-375-1` (ReStock).
*(Parts whose RBZebra already → `none`, e.g. Benjee10_MMSEV, are NOT in this list.)*

### ⚠️ Check ③ — Subtype divergence across target parts: **DIVERGENT for VRFB & HoCell (full diff done)**
All migrated SUBTYPE bodies were read at every site in §4 and diffed field-by-field. Results:

- `tankType`, `upgradeRequired`, `title`/`descSum`/`descDet`, IDENTIFIER block — **identical** at every site.
- `defaultSubtypePriority` — **constant per chemistry** (VRFB=8, MgSb=9, SMES=1, HoCell=1). Does NOT
  diverge. *(Corrects an earlier draft note in this manifest that claimed it did.)*
- **No** site adds `addedMass`/`addedCost`/`volumeMultiplier`/`NODE`/transform fields — all rely on `tankType`.

Genuine divergences and their confirmed resolutions:

| # | Chem | Divergence | Sites | Resolution |
|---|---|---|---|---|
| A | **VRFB** | `EVAupgrade`: `SMES` vs `none` | `SMES` at RB_Stock_Batteries.cfg:787 + ReStockPlus_Batteries.cfg:152; `none` at other 9 | 🟢 **collapses** — VRFB→`SMES` is a **bug** (Pietro), all VRFB fixed to `none`. VRFB body becomes uniform → single consolidated patch. |
| B | **HoCell** | extra DATA field `moduleActive = true` | only OPT.cfg:323 | 🟢 OPT site kept as its **own** patch node (Pietro-confirmed); consolidated HoCell patch covers the rest. |
| C | (cosmetic) | trailing `EVAupgrade` comment wording varies | Shuttle-cupola.cfg:102 etc. | normalize on consolidation; non-semantic. |

**Net effect after fixes:** **VRFB, MgSb, SMES** each consolidate into **one** uniform patch. **HoCell**
needs **two** (general + OPT-with-`moduleActive`). No chemistry needs more than that.

**VRFB is terminal — `EVAupgrade = none` at every site (Pietro r2).** Decision: **do not** add a
`NiH2 → VRFB` chain and **do not** override any existing `NiH2` upgrade (those stay `NiH2 → Li_ion`).
VRFB is reached by direct B9PS purchase only. Consequently the VRFB package needs **no** EVAupgrade
compat patch — just the uniform Pattern-1 add-subtype patch with `EVAupgrade = none`. The NiH2 ∩ VRFB
part analysis is therefore moot and no NiH2 subtype is touched.

---

### 🟢 Blockers A + B (decals — added to scope by Pietro) — RESOLVED by migrating decal subtypes
`parts/RealBatteryLabel.cfg` (`RB_Label`) and `parts/RealBatteryLabel-small.cfg` (`RB_Label_small`)
are **core, always-loaded** ConformalDecals parts. Each holds B9PS `variant<Chem>` subtypes that
reference both migrated **loc keys** (Blocker A) and migrated **PARTUPGRADEs** (Blocker B,
`upgradeRequired = RB_Upgrade<Chem>`).

**Decal subtypes to migrate (remove from core, re-add via expansion `@PART` patch — [§9 Pattern 3]):**
| Chem | Variant subtypes (RealBatteryLabel.cfg lines) | Loc keys referenced | atlas tileIndex |
|---|---|---|---|
| VRFB   | variantVRFB (430), variantVRFB_toasted (447) | title_VRFB, title_VRFB_toasted, descDet_decal_VRFB | 22, 23 |
| MgSb   | variantMgSb (498), variantMgSb_toasted (515) | title_MgSb, title_MgSb_toasted, descDet_decal_MgSb, **descDet_decal_LMB ⚠** | 26, 27 |
| SMES   | variantSMES (532), variantSMES_toasted (549) | title_SMES, title_SMES_toasted, descDet_decal_SMES | 28, 29 |
| HoCell | variantHoCell (617), variantHoCell_toasted (634), variantHoCell_classified (651) | title_HoCell, title_HoCell_toasted, short_HoCell_classified, descDet_decal_HoCell, descDet_decal_HoCell_classified | 33, 34, 35 |

**`RealBatteryLabel-small.cfg` (`RB_Label_small`) is NOT a mirror of the above** — different variant
names, loc-key families, and atlas tiles. Its migrated-chem variants (10 total, lines 418–681):
| Chem | Variant subtypes (lines) | Loc keys referenced | atlas tileIndex |
|---|---|---|---|
| VRFB   | variantVRFB (420), variant-vintVRFB (437) | short_VRFB, descSum_decal-small_VRFB, short_vint_VRFB | 94, 95 |
| MgSb   | variantMgSb (486), variant-vintMgSb (503) | short_MgSb, descSum_decal-small_MgSb, short_vint_MgSb | 98, 99 |
| SMES   | variantSMES (519), variant-vintSMES (536) | short_SMES, descSum_decal-small_SMES, short_vint_SMES | 100, 101 |
| HoCell | variantHoCell (618), variantHoCell_classified (635), variant-vintHoCell (652), variant-vintHoCell_classified (668) | short_HoCell, descSum_decal-small_HoCell, short_HoCell_classified, descSum_decal-small_HoCell_classified, short_vint_HoCell, short_vint_HoCell_classified | 106, 107, 108, 109 |

Differences vs `RB_Label`: small label uses **`variant-vint<Chem>`** (vintage) instead of
**`variant<Chem>_toasted`**; titles use **`short_<Chem>`** not `title_<Chem>`; descriptions use
**`descSum_decal-small_<Chem>`** not `descDet_decal_<Chem>`; its own atlas tile range **94–109**
with **`tileSize 512, 152`** (vs the big label's 22–35 @ `512, 257`). Same shared atlas file though.
The `_LMB` bug does **not** affect the small label (its `variantMgSb` correctly uses `descSum_decal-small_MgSb`).

Together the two decal parts consume **all** the non-battery loc families: big label → `title_*`,
`title_*_toasted`, `descDet_decal_*`; small label → `short_*`, `short_vint_*`, `descSum_decal-small_*`
(plus `_classified` on HoCell in both). This accounts for every one of the 40 migrated keys in §6.

- **Why this resolves A+B:** once the `variant<Chem>` subtypes live in the expansion (added by an
  `@PART[RB_Label]:NEEDS[RealBatteryExpansion&ConformalDecals]` patch) and their loc keys move with
  them (added by an `@PART[RB_Label]:NEEDS[RealBattery,ConformalDecals]:FOR[RealBatteryExpansion]`
  patch), **core `RB_Label` no longer references any migrated loc key or PARTUPGRADE** → removing the
  expansion leaves core valid. ✅ Phase 5 "self-contained" satisfied.
- **Texture coupling — FLAGGED FOR A SEPARATE JOB (per Pietro).** The migrated decal variants still
  select tiles from the **shared core atlas** `RealBattery/assets/decals/atlas` (`tileIndex = 22..35`,
  `tileSize 512,257`). For this migration they keep `tileIndex` (expansion→core atlas dependency,
  acceptable since the expansion needs core). The conversion to **one texture per subtype** (so the
  expansion owns its decal art and the core atlas can drop tiles 22–35) is deferred to a separate task.
- **⚠ Pre-existing bug to fix while here:** `variantMgSb` (RealBatteryLabel.cfg:502) points at
  `#LOC_RB_descDet_decal_LMB`, which **does not exist** in any localization file (its `_toasted`
  sibling correctly uses `descDet_decal_MgSb`). Not caused by us. Fix to `descDet_decal_MgSb` during
  the decal migration. *(There is no `_LMB`/`_Vanadium`/`_Holmium`/`_Super` key family — confirmed.)*

---

## 6. Localization inventory (per language)

Each language file contains **exactly 40** migrated-chemistry keys — **symmetric across all 5
languages** (en-us, it-it, es-es, fr-fr, zh-cn = 40 each → 200 total). No language is missing a key
at the group level. Per-chemistry key families (en-us line refs):

| Family | VRFB | MgSb | SMES | HoCell |
|---|---|---|---|---|
| `title_<Chem>` | 299 | 304 | 309 | 314 |
| `short_<Chem>` | 300 | 305 | 310 | 315 |
| `descSum_<Chem>` | 301 | 306 | 311 | 316 |
| `descDet_<Chem>` | 302 | 307 | 312 | 317 |
| `title_<Chem>_toasted` | 336 | 337 | 338 | 339 |
| `short_<Chem>_classified` | — | — | — | 340 |
| `short_vint_<Chem>` | 356 | 357 | 358 | 359 |
| `short_vint_<Chem>_classified` | — | — | — | 360 |
| `descDet_decal_<Chem>` | 376 | 377 | 378 | 379 |
| `descDet_decal_<Chem>_classified` | — | — | — | 380 |
| `descSum_decal-small_<Chem>` | 396 | 397 | 398 | 399 |
| `descSum_decal-small_<Chem>_classified` | — | — | — | 400 |
| `Upgrade<Chem>_Title` | 414 | 415 | 416 | 413 |
| **count / chem** | **9** | **9** | **9** | **13** |

(HoCell has 4 extra `_classified` keys. Total 9+9+9+13 = 40. ✓)
⚠️ The `_toasted`, `_classified`, `descDet_decal_*`, `descSum_decal-small_*` families are used by the
**core decal parts**, not the battery subtypes. Per the A+B resolution they **migrate to the
expansion loc files together with the decal `variant<Chem>` subtypes** (Phase 4). All 40 keys per
language move to the expansion; none stay in core.

---

## 7. Out-of-scope but flagged

- **`RealBattery/RealBattery - v3 Legacy Configs/`** — a separate legacy copy outside `GameData/`
  (contains its own `RB_Stock_Batteries.cfg`, etc.). Not loaded by KSP from that path; not migrated.
  Noted so it isn't mistaken for a live duplicate.
- **`GameData/RealBattery/library.md`** — documentation file (sample SUBTYPE snippets per chemistry,
  non-functional / not load-time). Each migrated chemistry has a `### …` doc section with a SUBTYPE
  code block + loc-key references: **VRFB** (### at line 370), **MgSb** (### 422), **SMES** (### 448),
  **HoCell** (### 500). 📌 **DONE (Pietro):** the four template sections were **removed** from core `library.md` (not migrated
  into the expansion — a single subtype is copy-pasteable from each `<Chem>_patches.cfg`). Core doc now
  lists only core chemistries; 0 residual refs. See §10.
- **`NukeCell` / `QuantumCell`** — explicitly **not** in scope; untouched. Note `NukeCell` sits
  adjacent to HoCell in every file and shares the `batteryBank` switch blocks — take care not to
  disturb it when extracting HoCell.

---

## 8. Decision status (Pietro round 1)

| # | Item | Status |
|---|---|---|
| 1 | Corrected paths + target root `RealBattery/GameData/RealBatteryExpansion/` | ✅ confirmed |
| 2 | Check ② — core `RBZebra → none` + expansion compat patch restores `RBZebra → MgSb` | ✅ confirmed (apply Phase 3) |
| 3 | Check ③-A — all VRFB → `none` (VRFB→SMES was a bug); VRFB body becomes uniform | ✅ confirmed |
| 4 | Check ③-A — `NiH2 → VRFB` chain | ❌ **dropped (Pietro r2)** — VRFB terminal (`none`); existing `NiH2 → Li_ion` left untouched |
| 5 | Check ③-B — OPT HoCell (`moduleActive = true`) kept as its own patch node | ✅ confirmed |
| 6 | Blockers A+B — migrate decal `variant<Chem>` subtypes + loc keys to expansion | ✅ confirmed |
| 7 | Decal per-subtype textures (drop shared atlas) | ⏸️ deferred to a **separate job** |
| 8 | Pre-existing dangling key `descDet_decal_LMB` | fix to `descDet_decal_MgSb` during decal migration |
| 9 | Pattern-3 `NEEDS` separator — use `,` not `&` (project convention) | ✅ confirmed |

**No open questions remain.** All Phase-0 decisions are resolved; **Phase 1 (scaffold) authorized and
in progress.**

---

## 9. Proposed unified expansion patch patterns (for approval)

All expansion add/edit patches run in pass **`:FOR[RealBatteryExpansion]`** so they execute *after*
core's legacy (`@PART[...]` no-pass) patches have built the battery switch + base subtypes. Missing
parts/mods make a patch silently no-op, so one patch may list stock + modded parts together; only the
core mod gate (`:NEEDS[RealBattery]`) is required.

### Pattern 1 — Add a migrated chemistry's SUBTYPE (the main consolidation patch)
*(Example: MgSb. VRFB and SMES are identical in shape; HoCell uses this for all parts except OPT.)*
```
@PART[<all hosts for this chem>]:NEEDS[RealBattery]:FOR[RealBatteryExpansion]
{
    @MODULE[ModuleB9PartSwitch]:HAS[#moduleID[batterySwitch]]
    {
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
                IDENTIFIER { name = RealBattery }
                DATA
                {
                    ChemistryID = MgSb
                    EVAupgrade  = none
                }
            }
        }
    }
}
```
- HoCell-on-OPT variant: same block **plus** `moduleActive = true` inside `DATA`, as a separate
  `@PART[mk3Cockpit_Airliner]:NEEDS[RealBattery&OPT]:FOR[RealBatteryExpansion]` patch.

### Pattern 2 — Cross-boundary EVAupgrade compat (RBZebra→MgSb only)
*(Core legacy patch has already set RBZebra's `EVAupgrade = none`; this re-points it back to MgSb, and
exists only while the MgSb/expansion is installed. **Only used by the MgSb package** — VRFB is terminal,
so no NiH2→VRFB or other compat patch exists.)*
```
@PART[<only the affected hosts>]:NEEDS[RealBattery]:FOR[RealBatteryExpansion]
{
    @MODULE[ModuleB9PartSwitch]:HAS[#moduleID[batterySwitch]]
    {
        @SUBTYPE[RBZebra]
        {
            @MODULE:HAS[@IDENTIFIER[RealBattery]]
            {
                @DATA { @EVAupgrade = MgSb }
            }
        }
    }
}
```
- ✅ **Syntax validated** against the real config: `moduleID = batterySwitch` is correct and the
  `:HAS[#moduleID[batterySwitch]]` filter is **required** (some parts also carry `meshSwitchStyle` /
  `realnameMeshSwitchStyle` B9PS modules). `@IDENTIFIER[RealBattery]` matches `IDENTIFIER { name = RealBattery }`.
- ⚠️ Your draft used `:NEEDS[RealBattery]` with no pass. That works only because `RealBatteryExpansion`
  sorts after `RealBattery` alphabetically. Using **`:FOR[RealBatteryExpansion]`** makes the
  after-core ordering explicit and folder-name-independent. Recommended.

### Pattern 3 — Migrate decal variants (resolves A+B); core copies are deleted
*(`NEEDS` uses `,` as the separator per project/modding-community convention, not `&`.)*
```
@PART[RB_Label]:NEEDS[RealBattery,ConformalDecals]:FOR[RealBatteryExpansion]
{
    @MODULE[ModuleB9PartSwitch]                 // RB_Label has a single B9PS (no moduleID)
    {
        SUBTYPE
        {
            name = variantMgSb
            title = #LOC_RB_title_MgSb
            descriptionDetail = #LOC_RB_descDet_decal_MgSb   // fixes the _LMB bug
            upgradeRequired = RB_UpgradeMgSb
            primaryColor = #3D3D3D
            secondaryColor = #FF6600
            MODULE
            {
                IDENTIFIER { name = ModuleConformalDecal }
                DATA { tileIndex = 26 }          // still core atlas — texture split deferred
            }
        }
        // + variantMgSb_toasted, etc.
    }
}
```
- A **separate, non-identical** `@PART[RB_Label_small]` patch is required (not a copy): it uses
  `variant-vint<Chem>` names, `short_<Chem>` / `short_vint_<Chem>` / `descSum_decal-small_<Chem>` loc
  keys, and tileIndex 94–109 (`tileSize 512, 152`). HoCell has 4 variants there, not 3.
- Core `RealBatteryLabel.cfg` / `-small.cfg` have their `variant<Chem>` / `variant-vint<Chem>`
  subtypes **removed** so core no longer references migrated loc keys / PARTUPGRADEs.

---

**Phase 0 closed.** All patterns approved; NiH2→VRFB dropped.
**Phase 1 closed** — scaffold tree + empty per-chemistry cfg files created.

### Phase 2 progress (definitions + assets, per chemistry)
- ✅ **HoCell** — `HoCell.cfg` populated (REALBATTERY_CHEMISTRY + B9_TANK_TYPE `RB_HoCell` + dummy PART
  + PARTUPGRADE `RB_UpgradeHoCell` + CTT override); model+2 textures moved to `HoCell/Assets/`; PART
  `model =` repointed to `RealBatteryExpansion/HoCell/Assets/model`; all 5 original blocks deleted from
  `settings/Chemistries.cfg`, `B9TankTypes.cfg`, `upgrades_subtypes.cfg`; empty source asset folder
  removed. Verified: no `HoCell` left in `settings/`; asset path resolves. **(Phase 3/4 will handle
  HoCell's patches + loc — still present in core, as expected.)**
- ✅ **VRFB** — `VRFB.cfg` populated (REALBATTERY_CHEMISTRY + B9_TANK_TYPE `RB_VRFB` + dummy PART +
  PARTUPGRADE `RB_UpgradeVRFB` + CTT override); `model.mu` + `model000.dds` moved to `VRFB/Assets/`;
  PART `model =` repointed to `RealBatteryExpansion/VRFB/Assets/model`; all 4 original blocks deleted
  from the three `settings/` files; empty source asset folder removed. Verified: no `VRFB` left in
  `settings/`; asset path resolves; boundaries clean.
- ✅ **MgSb** — `MgSb.cfg` populated (REALBATTERY_CHEMISTRY + B9_TANK_TYPE `RB_MgSb` + dummy PART +
  PARTUPGRADE `RB_UpgradeMgSb` + CTT override); `model.mu` + `model000.dds` moved to `MgSb/Assets/`;
  PART `model =` repointed to `RealBatteryExpansion/MgSb/Assets/model`; all 4 original blocks deleted
  from the three `settings/` files; empty source asset folder removed. Verified: no `MgSb` left in
  `settings/`; asset path resolves; boundaries clean.
- ✅ **SMES** — `SMES.cfg` populated (REALBATTERY_CHEMISTRY + B9_TANK_TYPE `RB_SMES` + dummy PART +
  PARTUPGRADE `RB_UpgradeSMES` + CTT override); `model.mu` + 2 textures moved to `SMES/Assets/`; PART
  `model =` repointed to `RealBatteryExpansion/SMES/Assets/model`; all 4 original blocks deleted from
  the three `settings/` files (SMES was the last block in Chemistries.cfg + B9TankTypes.cfg — EOF clean,
  original no-trailing-newline style preserved in Chemistries.cfg); empty source asset folder removed.
  Verified: no `SMES` left in `settings/` except one illustrative comment in Chemistries.cfg:14
  (`InfiniteCycles ... (SMES)` field-reference example — harmless); asset path resolves; boundaries clean.

**✅ PHASE 2 COMPLETE (all 4 chemistries).** Source `assets/battery/` now contains only the 8 core
chemistries (AgZn, Graphene, LiIon, LiPoly, NiCd, Nuke, SSB, Zebra). Remaining for Phase 3 (patches +
EVAupgrade repair) and Phase 4 (localization): the migrated chems still appear in patch + loc files,
as designed.

> Note: the new expansion `.cfg` files were written with LF line endings while the core repo uses
> CRLF. Functionally irrelevant to KSP/ModuleManager, but flag for a `.gitattributes` rule or a
> normalization pass if a clean git diff matters. (Patch files under `patches/` are LF; `settings/`
> files are CRLF — pre-existing mixed state, not introduced here.)

### Phase 3 progress (consolidate patches + EVAupgrade repair, per chemistry)
- ✅ **HoCell** — `HoCell_patches.cfg` written with **Patch A** (one consolidated patch over the 13
  uniform host parts: `batteryBankMini, batteryBank, batteryBankLarge, probeStackSmall, probeStackLarge,
  nflv-drone-core-5-1, nflv-drone-core-75-1, battery-rad-125, battery-0625, restock-drone-core-375-1,
  restock-drone-core-1875-1, restock-drone-core-0625-1, restock-battery-1875-1`, matched via
  `@MODULE[ModuleB9PartSwitch]:HAS[#moduleID[batterySwitch]]`) and **Patch B** (OPT generic wildcard,
  `moduleActive = true`, matched via `@MODULE[…]:HAS[@SUBTYPE[Graphene]]` since core renames OPT's
  `Battery` subtype to `Li_poly`). All 9 original HoCell SUBTYPE blocks deleted from the 6 core patch
  files (RB_Stock_Batteries ×2, Probes_guidance, NearFuture, NearFuture_Batteries ×2, ReStockPlus,
  ReStockPlus_Batteries, OPT). Verified: 0 `HoCell` refs left in `patches/`; boundaries clean; no
  EVAupgrade dangling (HoCell terminal, no inbound edge). **Manifest §4 host labels were imprecise —
  actual sections corrected here** (e.g. OPT:306 was the wildcard patch, not `mk3Cockpit_Airliner`;
  ReStockPlus:241 was `restock-drone-core-*`, not `restock-mk2-pod`; NF_Batteries:501 was `battery-0625`).
  ⚠️ **Patch B (OPT) needs an in-game check** — wildcard + tankType transformation is the fragile spot.
- ✅ **VRFB** — `VRFB_patches.cfg` written: single consolidated patch over the 19 host parts
  (`batteryPack, ksp_r_largeBatteryPack, batteryBank, batteryBankLarge, mk3Cockpit_Shuttle, cupola,
  HECS2_ProbeCore, M3X_ShovelCockpit, M3X_InlineCockpit, M3X_CyclopsCockpit, nfex-probe-chfr-1/cyl-1/
  plto-1/sqr-1, battery-rad-125, battery-0625, battery-125, battery-25, restock-battery-1875-1`) via
  `:HAS[#moduleID[batterySwitch]]`, `EVAupgrade = none` (terminal). The two former VRFB→SMES bug edges
  collapsed to `none`; SMES is now purchase-only. All 11 original VRFB SUBTYPE blocks deleted across 7
  files (RB_Stock_Batteries ×3 — all distinct EVAupgrade variants; NearFuture_Batteries ×3 via
  replace_all; Mk3Expansion, ReStockPlus_Batteries, Probes_core_HECS2, Shuttle-cupola, NearFuture ×1).
  Verified: 0 `VRFB` refs left in `patches/`; no dangling `EVAupgrade = VRFB`; boundaries clean.
  (Note: RB_Stock_Batteries has mixed blank-line whitespace — some subtypes use an 8-space blank, one
  used an empty blank; deletions anchored per-block accordingly.)
- ✅ **SMES** — `SMES_patches.cfg` written: single consolidated patch over 6 host parts
  (`batteryBank, batteryBankLarge, nflv-battery-stack-5-1, nflv-battery-stack-75-1,
  restock-battery-1875-1, restock-battery-375-1`) via `:HAS[#moduleID[batterySwitch]]`,
  `EVAupgrade = none` (terminal, purchase-only). All 4 original SMES SUBTYPE blocks deleted
  (RB_Stock_Batteries ×1, ReStockPlus_Batteries ×2 via replace_all, NearFuture_Batteries ×1).
  Verified: 0 `SMES` refs left in `patches/`; no dangling `EVAupgrade = SMES`; boundaries clean.
- ✅ **MgSb** — `MgSb_patches.cfg` written with **two** patches: **Patch A** (consolidated add over 12
  host parts `batteryPack, ksp_r_largeBatteryPack, batteryBank, batteryBankLarge, battery-rad-125,
  battery-0625, battery-125, battery-25, nflv-battery-stack-5-1, nflv-battery-stack-75-1,
  restock-battery-1875-1, restock-battery-375-1`, `EVAupgrade = none`) and **Patch B** (RBZebra→MgSb
  compat, Pattern 2, over the 11 parts that had that chain — batteryPack excluded, no Zebra tier).
  Core repair: all 8 RBZebra `EVAupgrade` set `MgSb`→`none` (RB_Stock_Batteries ×2, NearFuture_Batteries
  ×4, ReStockPlus_Batteries ×2) so core is valid standalone. All 9 MgSb SUBTYPE blocks deleted
  (RB_Stock ×3 — bare + 2×`// target SUBTYPE name`; NearFuture_Batteries ×4 & ReStockPlus_Batteries ×2
  via replace_all). Verified: 0 `MgSb` refs left in `patches/`; all 8 RBZebra edges now `none`; RBZebra
  subtypes intact; boundaries clean.

**✅ PHASE 3 COMPLETE (all 4 chemistries).** Every migrated chemistry's SUBTYPE additions are now in
the expansion; core patches contain none of HoCell/VRFB/MgSb/SMES. EVAupgrade integrity: VRFB/SMES/HoCell
terminal (`none`); RBZebra→MgSb lives only in the expansion (core = `none`). Remaining: **Phase 4
(localization)** then **Phase 5 (final sweep)**.

### Phase 4 progress (localization + decal subtypes, per chemistry)
Combined unit per chemistry (loc-key move + decal-subtype move must travel together, else core decals
dangle). `_LMB` treated as `_MgSb` per Pietro (applies to MgSb only). Localization files are LF.
- ✅ **HoCell** — `HoCell_localization.cfg` generated (13 keys × 5 langs = 65, verbatim from core,
  13 per language verified); all 13 HoCell keys removed from each of the 5 core lang files (0 remaining).
  Decal subtypes migrated into `HoCell_patches.cfg`: 3 on `RB_Label` (variantHoCell/_toasted/_classified,
  tiles 33–35) + 4 on `RB_Label_small` (variantHoCell/_classified, variant-vintHoCell/_classified,
  tiles 106–109), each via `@PART[...]:NEEDS[RealBattery,ConformalDecals]:FOR[RealBatteryExpansion]`;
  removed from core `RealBatteryLabel.cfg` / `-small.cfg` (both close cleanly). Verified: **core fully
  clean of HoCell** except `library.md` (non-functional doc, §7). tileIndex still points at the shared
  core atlas (per-subtype texture split deferred).
- ✅ **VRFB** — `VRFB_localization.cfg` generated (9 keys × 5 langs = 45, verified 9/language); all
  VRFB keys removed from the 5 core lang files (0 remaining). Decal subtypes migrated into
  `VRFB_patches.cfg`: 2 on `RB_Label` (variantVRFB/_toasted, tiles 22–23) + 2 on `RB_Label_small`
  (variantVRFB, variant-vintVRFB, tiles 94–95); removed from core labels (variantSSB now follows
  cleanly in both). Verified: core clean of VRFB except `library.md` (Phase 5).
- ✅ **MgSb** — `MgSb_localization.cfg` generated (9 keys × 5 langs = 45, 9/language); all MgSb keys
  removed from the 5 core lang files (0 remaining). Decal subtypes migrated into `MgSb_patches.cfg`:
  2 on `RB_Label` (variantMgSb/_toasted, tiles 26–27) + 2 on `RB_Label_small` (variantMgSb,
  variant-vintMgSb, tiles 98–99); removed from core labels (variantSMES now follows cleanly).
  **`_LMB` fix applied:** the big-label variantMgSb now uses `#LOC_RB_descDet_decal_MgSb` (was the
  non-existent `_LMB`). Verified: core clean of MgSb (except `library.md`); **no functional `_LMB`
  reference remains anywhere** (only a provenance comment + this manifest mention it).
- ✅ **SMES** — `SMES_localization.cfg` generated (9 keys × 5 langs = 45, 9/language); all SMES keys
  removed from the 5 core lang files (0 remaining). Decal subtypes migrated into `SMES_patches.cfg`:
  2 on `RB_Label` (variantSMES/_toasted, tiles 28–29) + 2 on `RB_Label_small` (variantSMES,
  variant-vintSMES, tiles 100–101); removed from core labels (variantNuke now follows cleanly).
  Bidirectional loc check: 9 defined = 9 referenced, no dangling, no orphans.

**✅ PHASE 4 COMPLETE (all 4 chemistries).** All 5 core lang files have **0** references to
HoCell/VRFB/MgSb/SMES; both decal label parts (`RB_Label`, `RB_Label_small`) have **0**; each
expansion chemistry ships its own `<Chem>_localization.cfg` (HoCell 13×5, VRFB/MgSb/SMES 9×5) and its
decal subtypes in `<Chem>_patches.cfg`. Remaining migrated-chem traces in core are **only**:
`library.md` (scheduled §10 Phase 5), the `Chemistries.cfg:14` `InfiniteCycles` doc comment that cites
"(SMES)", and the compiled `RealBattery.dll` `IsSMES` property (code, property-based, not a config/
ChemistryID dependency — expansion stays removable). Remaining: **Phase 5 (final sweep + library.md)**.

---

## 10. Phase 5 — scheduled tasks (final sweep)

The plan's Phase 5 verification checks (from `migrate-expansions.yaml`) **plus** these scheduled items:

- ✅ **`library.md` doc cleanup (DONE).** Pietro's revised decision: do **not** migrate the templates
  into the expansion (a single subtype is trivially copy-pasteable from each `<Chem>_patches.cfg`).
  The 4 migrated-chem `### ` template sections were **removed** from core `GameData/RealBattery/library.md`
  (VRFB, MgSb, SMES, HoCell; 103 lines). Verified: 0 residual references anywhere in the file (intro,
  headers, comments, section list); remaining sections flow Graphene → Solid-State → Hafnium-178m2;
  clean EOF (`library.md` 523 → 420 lines).
- ✅ **Decal per-subtype textures (DONE).** Pietro restored per-subtype DDS in
  `RealBattery/assets/decals/{decal,decal-small,decal-vintage}/`. The whole decal system was converted
  from the shared atlas (`isMain=true` + `autoTile`/`tileSize`/`tileIndex`) to one texture per subtype
  (main module: `TEXTURE{ isMain=true; textureUrl=…-Main }`; each subtype: `DATA{ TEXTURE{ isMain=false;
  textureUrl=… } }`). Scope: **core** `RealBatteryLabel.cfg` (27 subtypes) + `RealBatteryLabel-small.cfg`
  (28) + main modules; **expansion** patches (HoCell 7, VRFB/MgSb/SMES 4 each = 19). 19 migrated-chem
  textures moved to `RealBatteryExpansion/<Chem>/Assets/decals/` (file count = ref count per chem).
  Verified: all **76** `textureUrl`s resolve to an existing `.dds`; **0** `atlas`/`tileIndex`/`autoTile`/
  `tileSize` residue in any cfg. (Transform done via a Python script; per-file CRLF/LF preserved.)
  **Follow-ups (DONE):** `RealBattery/assets/decals/atlas.dds` (12.5 MB) **deleted** (unreferenced);
  expansion decal textures **renamed to drop numeric prefixes** (`decal-31-MgSb`→`decal-MgSb`,
  `decal-small-…`/`decal-vintage-…` likewise) with patch `textureUrl`s updated; expansion **models +
  their textures moved** `…/Assets/` → `…/Assets/PartUpgrade/` and each dummy `PART` `model =` repointed
  to `…/Assets/PartUpgrade/model`. Final per-chem layout: `Assets/PartUpgrade/` (model.mu + model
  textures) + `Assets/decals/` (per-subtype decals). Re-verified: model `.mu` + all 76 textureUrls resolve.
- **Whole-repo verification** (plan Phase 5): no duplicate ChemistryID; no core @PART references a
  migrated SUBTYPE; no `#LOC_RB_*_<Chem>` for migrated chems left in core loc; every EVAupgrade resolves
  or `= none`; every migrated asset path resolves under its `Assets/`; each expansion subfolder is
  self-contained (removing it leaves core valid). Output pass/fail per check with file+line.

### ✅ PHASE 5 VERIFICATION — ALL CHECKS PASSED (scripted sweep)
- **1. No duplicate ChemistryID** — 17 defs, 17 unique; core (13) and expansion (4) disjoint.
- **2/3/6. Core free of migrated + expansion refs** — clean; **no core file references `RealBatteryExpansion`**
  → every expansion subfolder is self-contained (removable, core stays valid). Only non-config traces:
  `Chemistries.cfg:14` "(SMES)" doc comment (not a reference) and DLL `IsSMES` (property-based code).
- **4. EVAupgrade integrity** — 287 values; all resolve to an existing SUBTYPE or `none`
  (incl. expansion `RBZebra→MgSb`, valid since Patch A adds MgSb to all 11 Patch-B parts).
- **5. Asset paths** — 80 refs (76 `textureUrl.dds` + 4 `model.mu`) all resolve at the new
  `Assets/PartUpgrade/` + `Assets/decals/` locations.

**MIGRATION COMPLETE — pending Pietro sign-off.** This manifest is a temporary report and may be
deleted once the migration is signed off.
