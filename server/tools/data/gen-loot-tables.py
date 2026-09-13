#!/usr/bin/env python3
r"""gen-loot-tables - build Cranberry's Z2 ground-loot data from the client's own files.

INPUT
    ``C:\Aug2017\out\world_aug\z2-item-spawners.json``   168,322 spawn-marker placements
                                                         parsed out of the client's Z2.zone
                                                         by tools/zone/zonespawners.py
    ``C:\Aug2017\out\data_aug\ClientItemDefinitions.txt`` the client's item catalogue
    ``C:\Aug2017\out\data_aug\Models.txt``               the client's actor-model catalogue
    ``C:\Aug2017\out\data_aug\ContentPacks.txt``         per-title content gating
    ``C:\Aug2017\out\data_aug\derived\loot.json``        (optional) localised item names,
                                                         container capacities - for the
                                                         human-readable half of the output

OUTPUT (both are Cranberry's own formats, documented in docs/39-loot-accuracy.md)
    ``src/Cranberry.Zone/Data/Loot/z2-loot-tables.json`` CLTB v2 - the category -> item
                                                         tables, the per-category spawn
                                                         gate and the gun/ammunition
                                                         cluster table. Small,
                                                         hand-editable, and the ONE place
                                                         the odds and the density live.
    ``src/Cranberry.Zone/Data/Loot/z2-loot-spawns.bin``  CRLP v1 - the 168,322 placements
                                                         in a fixed 24-byte record, already
                                                         sorted into the runtime's grid.

WHAT IS DERIVED AND WHAT IS OURS
    Derived from the client (every one of these fails the build if it stops holding):

      D1  The spawn points. Position, rotation and instance id are the ZONE v5 object
          placements of the six marker models docs/29 5 identifies; this tool never
          invents, moves or drops a point.
      D2  The categories. Gear01 / Weapons01 / Backpack01 / FirstAidKit01 / Ammo01 /
          FireExtinguisher are the marker model names themselves
          (``ItemSpawner_KotK_<Category>.adr``), not a Cranberry taxonomy.
      D3  Item identity. Every ``itemDefinitionId`` is a real ``ClientItemDefinitions.txt``
          row; ``nameId`` is that row's NAME_ID, ``itemClass`` its ITEM_CLASS,
          ``passiveEquipSlotId`` its PASSIVE_EQUIP_SLOT_ID and ``maxStackSize`` its
          MAX_STACK_SIZE.
      D4  Ground model. ``groundModelId`` is a real ``Models.txt`` row id, resolved by the
          rules in ``resolve_ground_model`` below. The client's own DESCRIPTION column is
          the evidence that the ``_OnGround`` rows are the ground form: "NPC Spawn Assault
          Rifle" (23), "NPC Spawn AK-47" (9490), "On Ground M9Auto" (9423), "Magnum_OnGround"
          (9483), "Ground spawn kevlar vest" (9583), "Flashbang on ground" (9448).
      D5  KotK gating. An item whose CONTENT_ID names a ContentPacks row with
          ``LIVE_KOTK_UNLOCKED = 0`` is rejected outright.

    Owner-authored research, reused under D20 (docs/39 1 records the provenance line and
    what was deliberately NOT taken - no spawnChance, no pool total, no empty weight):

      O1  WHICH 72 client items are King-of-the-Kill 2017 ground loot, and the nine
          exclusions (.308 rifle and its ammunition, crossbow, camo tactical helmet,
          satchel, framed and black-military backpacks, the Just Survive melee family).
      O2  The relative weights inside each category.
      O3  The retail room census - 0-6 loose items per room, mean about 3 - and the room
          geometry it was counted with (4 m radius, +-2 m height).
      O4  The cluster rule ("one AR-15 with two boxes next to it with 30 ammo in each") and
          the per-calibre box sizes 30/30/15/7/7/6/6/5.
      O5  (WAVE 8, D53) The five per-family spawn gates of the world he actually runs -
          Weapons01 0.1267, Gear01 0.0850, Backpack01 0.2500, FirstAidKit01 0.3000,
          Ammo01 0.6000 - read out of his own tree. These replace X1; see SPAWN_CHANCES.

    Cranberry's own design choices:

      X1  (SUPERSEDED BY O5 IN WAVE 8) SPAWN_CHANCE = 0.27, one flat Cranberry-solved
          per-marker gate. Kept reachable as LootDensityOptions.SpawnChanceOverride and as
          PRE_WAVE8_SPAWN_CHANCE below; it is no longer what ships.
      X2  ``count`` - how many of a stacking item one spawn yields, clamped to the row's own
          MAX_STACK_SIZE.
      X3  ``kind`` - the coarse class the room caps and the tests reason about. Derived from
          ITEM_CLASS and PASSIVE_EQUIP_SLOT_ID but written out explicitly, because
          ITEM_CLASS 16053 is shared by ammunition AND the two medical items.
      X4  The grid the placements are sorted into (64 m cells). A runtime convenience, not a
          client fact.
      X5  Calibre-specific ammunition-box props over the generic box (docs/39 3.5).

Usage:
    python tools/data/gen-loot-tables.py                       # write both files
    python tools/data/gen-loot-tables.py --report              # print the audit, write nothing
    python tools/data/gen-loot-tables.py --out-dir <dir>
"""

from __future__ import annotations

import argparse
import collections
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheet import Sheet  # noqa: E402  (tools/data/sheet.py, this repo's datasheet reader)

# --------------------------------------------------------------------------------------
# Default locations. Everything under out\ is this project's own extraction of the client.
# --------------------------------------------------------------------------------------

DATA_AUG = Path(r"C:\Aug2017\out\data_aug")
WORLD_AUG = Path(r"C:\Aug2017\out\world_aug")
REPO = Path(__file__).resolve().parents[2]
DEFAULT_OUT = REPO / "src" / "Cranberry.Zone" / "Data" / "Loot"

SPAWNERS_JSON = WORLD_AUG / "z2-item-spawners.json"
ITEMDEFS_TXT = DATA_AUG / "ClientItemDefinitions.txt"
MODELS_TXT = DATA_AUG / "Models.txt"
CONTENTPACKS_TXT = DATA_AUG / "ContentPacks.txt"
LOOT_JSON = DATA_AUG / "derived" / "loot.json"

TABLES_FORMAT = "cranberry.loot-tables"
TABLES_VERSION = 2
SPAWNS_MAGIC = b"CRLP"
SPAWNS_VERSION = 1

# X4: the spatial grid the placements are pre-sorted into. 64 m cells over the +-4,096 m Z2
# terrain (docs/29 2.1) = 128 x 128 = 16,384 cells, ~10 points per cell on average. Chosen so
# that a 100 m query touches 25 cells; see docs/33 4 for the cost analysis.
GRID_CELL_METRES = 64.0
GRID_DIMENSION = 128
GRID_ORIGIN = -(GRID_DIMENSION * GRID_CELL_METRES) / 2.0  # -4096.0

NO_AREA = 0xFFFF

# --------------------------------------------------------------------------------------
# O5 - THE DENSITY DIAL, AND IT IS NOW THE OWNER'S OWN NUMBER, PER FAMILY.
#
# WAVE 8 (D53, docs/78 2.2). This block used to carry ONE Cranberry-solved gate, p = 0.27,
# with a written caveat that the owner's own five per-family spawn chances were excluded
# evidence that must never be fitted to. D53 lifts that bar: C:\Z1\Server\Data\kotk is the
# owner's own tree, those five numbers are the world he ran and graded "100% accurate and
# what I want", and his ruling this wave is "the values are correct and thats the way it is".
# So the gate is no longer SOLVED, it is TAKEN - and taken per spawner family, which is the
# shape his server always had and the shape this JSON field was already written for.
#
# HIS NUMBERS, and the arithmetic that turns them into Cranberry's one-number-per-family form.
# His tables carry an "empty" row INSIDE the pool; Cranberry folds that row into the gate
# instead (one editable number rather than two that have to be kept consistent), so
#
#     effective = spawnChance/100 * (1 - emptyWeight / totalPoolWeight)
#
#   family                          his sc  pool  empty  effective  markers   items
#   ItemSpawner_KotK_Weapons01          18   405    120     0.1267   47,967   6,076
#   ItemSpawner_KotK_Gear01             12   858    250     0.0850   96,722   8,225
#   ItemSpawner_KotK_Backpack01         25   100      0     0.2500   11,041   2,760
#   ItemSpawner_KotK_FirstAidKit01      30   100      0     0.3000   10,979   3,294
#   ItemSpawner_BattleRoyale_Ammo01     60   120      0     0.6000       72      43
#   weighted                                                0.1223  166,781  20,398
#
# His two remaining families - BattleRoyale_FirstAidKit01 at 100 % and ItemSpawnerHospital at
# 50 % - place ZERO markers in the August Z2.zone, so their gates are moot. Every figure above
# was recomputed from his own kotk2017-ground-set.json this session, not transcribed.
#
# WHAT IT CHANGES. Simulated end to end through Cranberry's own SplitMix64 gate and the
# 6-per-room symmetric cap at match seed 1:
#
#     gate           gated   after room cap   cap cost   ground entities incl. boxes
#     flat 0.27     44,830          43,095      3.9 %    59,816
#     HIS FIVE      20,158          20,128      0.1 %    30,818   <- ships
#
# Two consequences worth knowing. (a) The room caps go inert - 30 items out of 20,158 - so the
# long-running "which KOTK draw model does his live build actually run" question stops
# mattering: at his gate the world cannot build the sixteen-item room the caps exist to
# prevent. Keep the caps; they cost nothing now and they are still the guard. (b) The MIX
# shifts in his direction: Gear01 falls from 57 % of the floor to 40 % and FirstAidKit01 rises
# from 6.5 % to 16 % - fewer clothes, more medical.
#
# THE OLD DERIVATION, kept because it was honest and is still the fallback. docs/39 4.2 solved
# p from Cranberry's own measurement of the client's marker spacing (12.45 markers to a 4 m
# room over 4,000 samples) against O3, the owner's retail-footage room census (0-6 loose items,
# mean about 3), then corrected it in the build phase against the measured 10.1 % cap loss:
#
#     p     gated   suppressed        live   + cluster boxes   items per 4 m room
#   0.22   36,386    2,994 (8.2%)   33,392            50,296   2.37
#   0.25   41,424    4,180 (10.1%)  37,244            56,084   2.63
#   0.27   44,830    5,122 (11.4%)  39,708            59,816   2.80   <- pre-wave-8
#   0.30   49,915    6,705 (13.4%)  43,210            65,146   3.01
#
# That ladder is now the ROOM-CENSUS arm of the evidence and his running server is the LIVE
# arm, and the two disagree by about 2x. D53 and the owner's own instruction this wave both
# point at the running server, so the running server wins. If the floor turns out to feel too
# thin in play, LootDensityOptions.SpawnChanceOverride = 0.27 restores the entire pre-wave-8
# world in one line, without regenerating this file.
# --------------------------------------------------------------------------------------

# The owner's five effective per-family gates (O5, D53), keyed by TABLES key. A family that is
# not named here falls back to PRE_WAVE8_SPAWN_CHANCE; a family with no entries gets 0.0.
# --------------------------------------------------------------------------------------
# D270 (2026-09-03) - THE OWNER RULED THE FLOOR BACK UP TO 0.27.
#
#   "floor density: spawn chance 0.27 (his own footage: ~3 items per 4 m room) replacing the
#    weighted gate 0.1223"
#
# AUDIT-loot.md G1: at his five wave-8 gates the floor carried HALF what his own ten-room retail
# census says it should - 1.50 items in a 4 m room against a counted mean of about 3 - and
# AUDIT-loot.md G3: Gear01, which owns 96,722 of the 168,322 markers, had the LOWEST gate of the
# five (0.0850) and so produced the least, leaving clothing at 6.8 % of the floor in a game whose
# floors were famously covered in folded shirts and work boots.
#
# THE RULE, and it is one sentence: NO FAMILY SITS BELOW THE RULED DENSITY. The three gates that
# were under 0.27 come up to it; the two his own tree already put ABOVE it stay exactly where he
# put them, because raising the floor is the ruling and lowering anything is not.
#
#   family            wave 8   D270     why
#   Weapons01         0.1267   0.27     raised to the ruled density
#   Gear01            0.0850   0.27     raised to the ruled density - THIS IS THE CLOTHING GATE
#   Backpack01        0.2500   0.27     raised to the ruled density
#   FirstAidKit01     0.3000   0.3000   already above it; his number, untouched
#   Ammo01            0.6000   0.6000   already above it; the 72 client-paired markers, untouched
#   marker-weighted   0.1223   0.2721   = the ruled 0.27
#
# D271, the clothing gate, is the Gear01 row above and its number is THE AUDIT'S OWN Gear01 MARKER
# SHARE: 96,722 / 168,322 = 57.5 %. A family gated at the flat ruled density produces its own share
# of the markers as its share of the floor, so ruling Gear01 to 0.27 is exactly the statement
# "Gear01 gets the share of the floor the client's own marker field gives it". Measured at seed 1
# through this file's own gate and the symmetric room cap: Gear01 40.1 % -> 57.2 % of items, and
# clothing 6.9 % -> 12.3 % of ground objects (2,060 -> 6,145 garments map-wide, x3.0).
#
# WHAT IT COSTS. Measured, seed 1, one box per gun (D272):
#     gate set          gated    room-capped        items   + boxes   ground objects
#     wave 8           20,158    683   (3.4 %)     19,475    10,304          29,779
#     D270             45,205  5,186  (11.5 %)     40,019    10,052          50,071
# The room caps stop being inert and become the guard they were written to be again - which is
# what AUDIT-loot.md 4 "Not to change" predicted would happen and asked for.
#
# CLEAN-ROOM CONSEQUENCE, worth naming: three of the six THIRD_PARTY_SHAPED rows in the whole
# server (docs/101 4.5) are retired by this ruling. 0.27 is Cranberry's OWN solve (docs/39 4.2,
# D31) against the owner's own footage census; only FirstAidKit01 0.30 and Ammo01 0.60 keep the
# grade now.
# --------------------------------------------------------------------------------------

SPAWN_CHANCES: dict[str, float] = {
    "Weapons01":     0.2700,   # D270 - raised from his 0.1267 to the ruled density
    "Gear01":        0.2700,   # D271 - raised from his 0.0850; the clothing gate
    "Backpack01":    0.2700,   # D270 - raised from his 0.2500
    "FirstAidKit01": 0.3000,   # his 30 %, no empty row - already above 0.27, untouched
    "Ammo01":        0.6000,   # his 60 %, no empty row, on 72 markers - untouched
}

# The marker-weighted mean of the five above. Documentation and a test anchor only
# (Cranberry.Zone.Loot.LootDensityOptions.WeightedSpawnChance); it gates nothing.
# (96,722 + 47,967 + 11,041) x 0.27 + 10,979 x 0.30 + 72 x 0.60 = 45,384 over 166,781 markers.
WEIGHTED_SPAWN_CHANCE = 0.2721

# The pre-wave-8 flat gate, kept so the revert is one line rather than a regeneration.
PRE_WAVE8_SPAWN_CHANCE = 0.27

# The room caps that ride with it (O3). The runtime reads them from this file so the whole
# density model is retunable without a rebuild.
ROOM_RADIUS_METRES = 4.0
ROOM_HEIGHT_METRES = 2.0
MAX_ITEMS_PER_ROOM = 6
MAX_WEAPONS_PER_ROOM = 2
SINGLETON_KINDS = ["Backpack", "BodyArmor", "Helmet"]

# --------------------------------------------------------------------------------------
# O1 / O2 / X2 / X3 - THE ONE EDITABLE PLACE for what a marker may produce.
#
#   * "stem" is a MODEL_FILE_NAME base. resolve_ground_model() prefers "<stem>_OnGround.adr",
#     then "<stem>.adr", and fails if neither is a Models.txt row - so a typo here is a build
#     error, never a silently wrong model id on the wire.
#   * "prefer" pins one of a duplicated MODEL_FILE_NAME's two ids where the client's own
#     DESCRIPTION column says which row is meant (docs/39 3.1).
#   * "weight" is relative within its category. Sums are printed by --report.
#   * "count" is the stack size handed out; it is clamped to the item's own MAX_STACK_SIZE.
#   * "kind" drives the room caps (docs/39 4.3) and the regression tests.
#   * "why" is the justification that goes into the emitted file, so the table audits itself.
# --------------------------------------------------------------------------------------

WEAPON = "Weapon"
AMMUNITION = "Ammunition"
MEDICAL = "Medical"
CLOTHING = "Clothing"
HELMET = "Helmet"
BODY_ARMOR = "BodyArmor"
BACKPACK = "Backpack"
UTILITY = "Utility"
THROWABLE = "Throwable"

KINDS = [WEAPON, AMMUNITION, MEDICAL, CLOTHING, HELMET, BODY_ARMOR, BACKPACK, UTILITY, THROWABLE]


def E(item_id: int, stem: str, weight: int, count: int, kind: str, why: str,
      prefer: int | None = None) -> dict:
    """One table row. Keyword-free on purpose: the tables below read as tables."""
    if kind not in KINDS:
        raise SystemExit(f"item {item_id}: unknown kind {kind!r}")
    return {
        "item": item_id,
        "stem": stem,
        "weight": weight,
        "count": count,
        "kind": kind,
        "why": why,
        "prefer": prefer,
    }


def group(ids: list[int], stem: str, weight: int, kind: str, why: str) -> list[dict]:
    """A run of same-prop, same-weight roster items (the clothing layer, mostly)."""
    return [E(i, stem, weight, 1, kind, why) for i in ids]


# The KOTK-2017 retail ground roster (O1). Every id was verified against the August
# ClientItemDefinitions.txt: all 72 exist, all sit in CONTENT_ID 1 ("Live", so D5 passes),
# and every ground actor named here resolves in the August Models.txt at the same id the
# owner's 1087-era audit names - the roster ports to build 1148 with zero substitutions.

SHIRTS = [2127, 2128, 2137, 2138, 2139, 2140, 2141, 2142, 2143, 2145]
TROUSERS = [2173, 2174, 2175, 2176, 2177, 2178]
CAPS = [2096, 2100, 2102, 2104, 2105, 2106]
BEANIES = [2162, 2166]
SNEAKERS = [2215, 2216, 2217, 2218]
BOOTS = [2206, 2207, 2208, 2209]

# docs/54 s2 (APPEARANCE lane, 2026-08-30). A ground prop has NO colour lever on the wire, so its
# Models.txt row IS its colour; these two rows were the owner's "the AR-15's boxes are brown and
# the shotgun's are white". Shared between the Ammo01 table and the gun clusters so the two can
# never drift apart again.
AR15_BOX_WHY = (
    "AR-15 magazine - the owner's own example. Prop is Common_Props_AmmoBox02 (10), the "
    "olive-green can, mean RGB (78,99,65): the mesh the client's own ItemSpawner_AmmoBox02_M16A4 "
    "and ItemSpawner_BattleRoyale_AmmoBox02_M16A4 markers are built on. Reverses docs/39 3.5's "
    "judgement call, which sent the AK's tan box (10132, mean (94,77,54)) and made the AR-15's "
    "boxes read brown (docs/54 2.2).")
SHOTGUN_BOX_WHY = (
    "12GA Pump Shotgun full tube. Prop is Common_Props_AmmoBoxe01 (8023), the mesh the client's "
    "own ItemSpawner_AmmoBox02_12GaShotgun markers use. The previous 10137 "
    "Common_Props_AmmoBoxes_Shotgun.adr is named by Models.txt but ships in NONE of the 256 asset "
    "packs; the client logged 'Failed to load asset' ten times for it during the 2026-08-30 "
    "play-test (docs/54 2.3, LIVE-VERIFIED).")

TABLES: dict[str, dict] = {
    "Weapons01": {
        "note": "Held weapons only, and NO ammunition: ammunition reaches the floor from the "
                "72 Ammo01 markers and from the two boxes flanking every gun (docs/39 5), "
                "never as a loose stack in an empty room. Long guns lead the table at 48.4 % "
                "against melee's 11.2 % - the retail shape, and the opposite of the invented "
                "table it replaces.",
        "entries": [
            E(1374, "Weapons_PumpShotgun01", 48, 1, WEAPON,
              "12GA Pump Shotgun: ITEM_CLASS 25036, passive slot 76 (R_LongWeapon_1); ground "
              "form 9286 Weapons_PumpShotgun01_OnGround."),
            E(10, "Weapon_M16A4", 45, 1, WEAPON,
              "AR-15: the roster's base row (ITEM_CLASS 25036, passive slot 76); ground form "
              "23 Weapon_M16A4_OnGround, DESCRIPTION 'NPC Spawn Assault Rifle'."),
            E(2229, "Weapon_AK47", 45, 1, WEAPON,
              "AK-47: ITEM_CLASS 25036, passive slot 76; ground form 9490 "
              "Weapon_AK47_OnGround, DESCRIPTION 'NPC Spawn AK-47'."),
            E(1718, "Weapons_Pistol_44Magnum01", 25, 1, WEAPON,
              ".44 Magnum: ITEM_CLASS 4096, passive slot 78 (R_ShortWeapon_1); ground form "
              "9483, DESCRIPTION 'Magnum_OnGround'."),
            E(1986, "Weapons_Bow01", 24, 1, WEAPON,
              "Recurve Bow: ITEM_CLASS 25038, passive slot 82 (R_bowWeapon_1). Models.txt "
              "gives Weapons_Bow01_OnGround.adr two ids; 9420 carries DESCRIPTION "
              "'Bow.Recurve' where 9162 carries the bare file name, so this row pins 9420 "
              "rather than taking the lowest-id tie-break.", prefer=9420),
            E(2246, "Weapon_Crossbow01", 20, 1, WEAPON,
              "Crossbow - D273 (2026-09-03), the owner's ruling that it joins the floor roster. "
              "His own retail research names it live KOTK loot in this window: "
              "web-research-aug2017-kotk.md:129 'Recurve/Long bow, Crossbow | wooden / explosive / "
              "flaming arrows | present (KotK loot; Nov 2017 notes adjust \"crossbow\"/\"longbow\" "
              "spawn rates)' and :245 'Bow/crossbow exist in data | Confirmed as live loot'. It was "
              "excluded by O1 on his Z1 roster's UNPROVEN-BY-ABSENCE note; the dated research "
              "outranks that. ITEM_CLASS 25047, CONTENT_ID 1 (Live), CLIP_SIZE 1, WEAPON_ID 1414 - "
              "so Combat/AmmoTypes.cs:82 already resolves its ammunition to the Wooden Arrow and "
              "ShooterCombatState.DefaultMagazineFor already chambers exactly one (docs/102 1F). "
              "Ground form 9202 Weapon_Crossbow01_OnGround, DESCRIPTION "
              "'NPC_Spawn_Weapon_Crossbow01_OnGround'. Weight 20 against the Recurve Bow's 24: a "
              "RULING, the bow being the commoner of the two."),
            E(2, "Weapon_Pistol_45Auto", 22, 1, WEAPON,
              "M1911A1: the roster row, ITEM_CLASS 4096, passive slot 78. NOT row 1702, which "
              "carries PASSIVE_EQUIP_SLOT_ID 0 and so can never auto-equip (docs/39 7); "
              "ground form 17, DESCRIPTION 'NPC Spawn 45 Pistol'."),
            E(1997, "Weapons_M9Auto", 22, 1, WEAPON,
              "M9: ITEM_CLASS 4096, passive slot 78; ground form 9423, DESCRIPTION "
              "'On Ground M9Auto'."),
            E(1991, "Weapon_Pistol_380Auto", 22, 1, WEAPON,
              "R380: ITEM_CLASS 4096, passive slot 78; ground form 9422, DESCRIPTION "
              "'On Ground R51 (380Auto)'."),
            E(83, "Weapons_Machete01", 16, 1, WEAPON,
              "Machete: ITEM_CLASS 4098; the ONLY row in the whole sheet carrying passive "
              "slot 106 (R_MeleeWeapon_Rear_Melee_Diag); ground form 24 'NPC Spawn Machete'."),
            E(84, "Weapons_CombatKnife01", 16, 1, WEAPON,
              "Combat Knife: ITEM_CLASS 4098, passive slot 0 - this build has no back slot "
              "for it (docs/39 7); ground form 21 Weapons_CombatKnife01_OnGround."),
        ],
    },
    "Gear01": {
        "note": "The bulk marker (96,722 of 168,322) - the clothing layer, medical, headgear, "
                "shoes, utility, throwables, one vest and the backpack spillover. It carries "
                "NO ammunition: the previous table gave 56.5 % of the map's biggest category "
                "to loose rounds, which is exactly the 'bullets here and there' floor the "
                "owner reported (docs/39 2.3).",
        "entries": [
            # --- medical, 22.2 % ------------------------------------------------------
            # September 8 play-test ruling: bandages are starter/crafted/player loot only.
            E(2424, "Common_Props_FirstAidKit", 25, 1, MEDICAL,
              "Tactical First Aid Kit - the other half of that pair, model 9221."),
            # --- clothing layer, 15.8 % ----------------------------------------------
            *group(SHIRTS, "Common_Props_Clothes_FoldedShirt", 4, CLOTHING,
                   "Shirt (ITEM_CLASS 25002, passive slot 3); ground prop 9249 "
                   "Common_Props_Clothes_FoldedShirt, DESCRIPTION 'Shirt'."),
            *group(TROUSERS, "Common_Props_Clothes_FoldedPants", 4, CLOTHING,
                   "Trousers (ITEM_CLASS 25003, passive slot 4); ground prop 9736 "
                   "Common_Props_Clothes_FoldedPants."),
            *group(CAPS, "Common_Props_Clothes_BaseballCap", 4, CLOTHING,
                   "Cap (ITEM_CLASS 25000, passive slot 1); ground prop 66 "
                   "Common_Props_Clothes_BaseballCap, DESCRIPTION 'Baseball Cap'. A cap is "
                   "clothing, not the Helmet singleton the room cap limits."),
            *group(BEANIES, "Common_Props_Clothes_Beanie", 4, CLOTHING,
                   "Beanie (ITEM_CLASS 25000, passive slot 1); ground prop 67, DESCRIPTION "
                   "'Beanie'."),
            # --- headgear, 24.7 % -----------------------------------------------------
            E(2172, "Common_Props_Clothes_TacticalHelmet", 50, 1, HELMET,
              "Light Blue Tactical Helmet - the roster's only tactical helmet. Models.txt "
              "duplicates Common_Props_Clothes_TacticalHelmet.adr as 9418 and 9619 and "
              "neither row carries a DESCRIPTION, so the lowest-id tie-break stands (9418) "
              "until a live run says otherwise (docs/39 3.1)."),
            *group([2168, 2169, 2170, 2171], "Common_Props_Clothes_MotorcycleHelmet", 25, HELMET,
                   "Motorcycle Helmet (ITEM_CLASS 25000, passive slot 1); ground prop 68, "
                   "DESCRIPTION 'Motorcycle Helmet'."),
            # --- shoes, 9.9 % ---------------------------------------------------------
            *group(SNEAKERS, "Common_Props_Clothes_Conveys", 8, CLOTHING,
                   "Conveys sneakers (ITEM_CLASS 25005, passive slot 5); ground prop 9708."),
            *group(BOOTS, "Common_Props_Clothes_Workboots", 7, CLOTHING,
                   "Work boots (ITEM_CLASS 25005, passive slot 5); ground prop 9429."),
            # --- utility, 13.2 % ------------------------------------------------------
            E(134, "Common_Props_DuctTape_DuctTapeFullRoll", 60, 1, UTILITY,
              "Duct Tape - ITEM_CLASS 16052; ground prop 9431, DESCRIPTION 'Duct.Tape'."),
            E(1803, "BackpackOnGround_FannyPack", 20, 1, UTILITY,
              "Waist Pack - ITEM_CLASS 25013. The fanny-pack ground prop (9737) belongs to "
              "this row, not to the Satchel the invented table put on it (docs/39 2.2)."),
            # --- throwables, 7.9 % ----------------------------------------------------
            E(65, "Weapons_Grenades_HEGrenade", 12, 1, THROWABLE,
              "M67 Frag Grenade - ground prop 9476. Item 65 and not its duplicate 2243 (same "
              "NAME_ID 93): 2243 sits in CONTENT_ID 100 'Local Only', which D5 rejects."),
            E(2236, "Weapons_Grenades_SmokeGrenade", 10, 1, THROWABLE,
              "M83 Smoke Grenade - ground prop 9450."),
            E(2237, "Weapons_Grenades_GasGrenade", 10, 1, THROWABLE,
              "M47 Gas Grenade - ground prop 9479."),
            E(2235, "Weapons_Grenades_FlashBang", 8, 1, THROWABLE,
              "M-84 Stun Grenade - ground prop 9448, DESCRIPTION 'Flashbang on ground'."),
            E(14, "Weapons_MolotovCocktail", 8, 1, THROWABLE,
              "Molotov Cocktail - ground prop 9449 (NOT the _3P row 9439, the held mesh)."),
            # --- body armour, 3.0 % ---------------------------------------------------
            E(2271, "Common_Props_Armor_Kevlar_Basic", 18, 1, BODY_ARMOR,
              "Laminated Tactical Body Armor - the roster's only vest (passive slot 100); "
              "Models.txt 9583 DESCRIPTION is literally 'Ground spawn kevlar vest'."),
            # --- backpack spillover, 3.5 % --------------------------------------------
            *group([2112, 2113, 2114], "BackpackOnGround_ManSport", 4, BACKPACK,
                   "Civilian backpack spilling over onto the gear marker; ground prop 9706 "
                   "BackpackOnGround_ManSport."),
            *group([2115, 2116, 2117], "BackpackOnGround_ManSport", 3, BACKPACK,
                   "Civilian backpack spilling over onto the gear marker; ground prop 9706."),
        ],
    },
    "Backpack01": {
        "note": "Containers for the Backpack passive slot (EquipmentSlotDefinitions row 10). "
                "The military bag is the COMMONEST here at 40 %, not the rarest: the "
                "MAX_BULK-ordered weights the invented table used were Cranberry's guess, and "
                "the roster reverses them.",
        "entries": [
            E(2124, "BackpackOnGround001", 40, 1, BACKPACK,
              "Tan Military Backpack - ground prop 9093 BackpackOnGround001, DESCRIPTION "
              "'BackpackOnGround'. The roster drops the Black Military bag (2118)."),
            *group([2112, 2113, 2114, 2115, 2116, 2117], "BackpackOnGround_ManSport", 10, BACKPACK,
                   "Civilian backpack - ground prop 9706 BackpackOnGround_ManSport. The "
                   "roster drops the Satchel (2125) and the Framed Backpack (2111) and adds "
                   "the Blue and Grey (2114)."),
        ],
    },
    "FirstAidKit01": {
        "note": "September 8 play-test ruling: normal medical spawns contain first aid kits; "
                "bandages remain starter/crafted/player loot.",
        "entries": [
            E(2424, "Common_Props_FirstAidKit", 40, 1, MEDICAL,
              "Tactical First Aid Kit, ClientItemDefinitions 2424 -> Models.txt 9221."),
        ],
    },
    "Ammo01": {
        "note": "The ONLY table that draws loose ammunition, and Z2 places just 72 of these "
                "markers. Counts are the box = one magazine rule (docs/39 5), never a random "
                "3-to-15 dribble. .308 and its ammunition are absent: airdrop-only in this "
                "retail window.",
        "entries": [
            E(1429, "Common_Props_AmmoBox02", 26, 30, AMMUNITION, AR15_BOX_WHY),
            E(2325, "Common_Props_AmmoBoxes_AK47", 26, 30, AMMUNITION,
              "7.62x39 Round - one AK-47 magazine; matching AK47 box prop 10132."),
            E(1511, "Common_Props_AmmoBoxe01", 20, 6, AMMUNITION, SHOTGUN_BOX_WHY),
            E(1428, "Common_Props_AmmoBoxes_M1911", 14, 7, AMMUNITION,
              ".45 Round - one M1911A1 magazine; matching M1911 box prop 10133."),
            E(1998, "Common_Props_AmmoBoxes_M9_01", 14, 15, AMMUNITION,
              "9mm Round - one M9 magazine; matching M9 box prop 10134."),
            E(1719, "Common_Props_AmmoBoxes_Magnum", 10, 6, AMMUNITION,
              ".44 Round - one full cylinder; matching Magnum box prop 10135."),
            E(1992, "Common_Props_AmmoBox02", 10, 7, AMMUNITION,
              ".380 Round - one R380 magazine. No .380 box ships; the client's own "
              "'Generic Ammo Box' (10) is the prop."),
        ],
    },
    # NPCSpawner_FireExtinguisher is a placed marker (1,541 of them) but the August
    # ClientItemDefinitions.txt has NO fire-extinguisher item row - searched on name, on
    # CODE_FACTORY_NAME and on MODEL_NAME. It is an NPCSpawner, not an ItemSpawner, so it is
    # very likely a destructible world prop (Commercial_Props_FireExtinguisher.adr, model
    # 10078) rather than a pickup. Emitting an empty table records the finding instead of
    # inventing an item; the runtime rolls nothing for it.
    "FireExtinguisher": {
        "note": "EMPTY BY DERIVATION: no fire-extinguisher item exists in the August "
                "ClientItemDefinitions.txt, and the marker is an NPCSpawner rather than an "
                "ItemSpawner. Its 1,541 points are still shipped so the world model can place "
                "the prop later; nothing rolls here.",
        "entries": [],
    },
}

# --------------------------------------------------------------------------------------
# O4 - THE CLUSTER TABLE. "ONE AR-15 WITH TWO BOXES NEXT TO IT WITH 30 AMMO IN EACH."
#
# Every gun that lands on the floor is flanked by two boxes of its own calibre. The boxes are
# extra ground entities, not extra marker rolls: they never consume a marker, they are exempt
# from the room cap (they belong to their gun, not to the room's budget), and they are placed
# from the gun's own deterministic sub-stream.
#
# Box size = one magazine, from the client's own magazine capacities. Melee gets no cluster.
# --------------------------------------------------------------------------------------

CLUSTER_OFFSET_METRES = 0.5          # +-0.5 m perpendicular to the marker's yaw
CLUSTER_SECOND_BOX_YAW = 0.35        # radians, so a pair does not read as one mirrored object

# D286 (2026-09-03 evening) REVERSES D272 back to TWO boxes per gun: the owner played the one-box
# world and ruled "there's only one ammo box now next to guns? It should be two". So every gun on
# the floor is flanked by a PAIR of boxes of its own calibre again, which is the wave-8 world.
#
# D272 (2026-09-03) had cut the pair to one box on the strength of AUDIT-loot.md G2, which measured
# what two boxes did: 34.7 % of every ground object on the map was an ammunition box, boxes
# outnumbered weapons 1.77:1, the median nearest-neighbour distance was exactly 0.50 m (this
# offset), and the dominant non-singleton cluster on the floor was size 3 - one gun and its two
# boxes. That is the world the owner has now chosen back. The client only ever corroborated PAIRING
# for the 72 Ammo01 markers, which it lays out as 36 pairs 0.65-0.68 m apart, and those were never
# touched by either ruling: they are marker rolls, not cluster boxes.
#
# Measured at seed 1 with D270's gates: two boxes put ammunition at ~34.8 % of ground objects (one
# box was 20.2 %). D286 restores the ~34.8 % figure.
BOXES_PER_GUN = 2


CLUSTERS: list[tuple[int, int, str, int, str]] = [
    # weapon item, ammo item, ammo ground-model stem, rounds, why
    (10, 1429, "Common_Props_AmmoBox02", 30, AR15_BOX_WHY),
    (2229, 2325, "Common_Props_AmmoBoxes_AK47", 30, "AK-47 magazine."),
    (1997, 1998, "Common_Props_AmmoBoxes_M9_01", 15, "M9 magazine."),
    (2, 1428, "Common_Props_AmmoBoxes_M1911", 7, "M1911A1 magazine (pre-Combat-Update 2017)."),
    (1991, 1992, "Common_Props_AmmoBox02", 7, "R380 magazine; no .380 box prop ships."),
    (1718, 1719, "Common_Props_AmmoBoxes_Magnum", 6, ".44 Magnum full cylinder."),
    (1374, 1511, "Common_Props_AmmoBoxe01", 6, SHOTGUN_BOX_WHY),
    (1986, 112, "Projectile_LongBow01_Arrow_Apex", 5,
     "Recurve Bow - five-arrow quantum. Wooden Arrow (112, ITEM_CLASS 25018) reaches the "
     "floor ONLY here; it is not on Ammo01."),
    (2246, 112, "Projectile_LongBow01_Arrow_Apex", 5,
     "Crossbow (D273) - the same five-arrow quantum as the Recurve Bow. The box = one magazine "
     "rule would hand out ONE arrow here (the client's own CLIP_SIZE for 2246 is 1), which is a "
     "prop rather than a pickup; the bow's five-arrow quantum is the standing exception and the "
     "crossbow takes it too. The chamber is still 1: that is the client's number and it is "
     "ShooterCombatState.DefaultMagazineFor's, not this table's."),
]


# --------------------------------------------------------------------------------------
# D274 - THE AIRDROP, RETAIL'S SECOND LOOT CHANNEL. (AUDIT-loot.md G4/F4.)
#
# The owner ruled it built. Retail, dated, High confidence, from his own research
# (C:\Aug2017\out\stage1-assessment\web-research-aug2017-kotk.md:148):
#
#   "not player-callable (airdrop tickets removed Sept 2016). A plane flies over, banks upward
#    after dropping, crate descends on a parachute with a loud whistle; the crate lands and must
#    be unlocked (~8 s per Jan 2017 guide, Medium). Contents from June 29 2017: always laminated
#    armor, 50 % chance of a hunting rifle, plus medkits/ammo/guns. Internally called
#    'bombs'/'bombing runs': runs stop once 20 players remain (in-flight ones still land)."
#
# and :252  "hunting rifle world spawn | Hunting rifle was airdrop-only (50 % of drops); lammy
#            guaranteed in every drop".
#
# THE CLIENT CORROBORATES THE CHANNEL ITSELF, which is the part that matters for a clean room:
#   * locale string 15223: ".308 Hunting Rifle ... only find it in military crates dropped by
#     planes."  [P]
#   * Models.txt 9218 Common_Props_MilitaryCrate.adr, DESCRIPTION "Crate.Military for air drops."
#     9219 Common_Props_MilitaryCrate_Parachute.adr, "Crate.Military.Parachute for air drops."
#     9215 Vehicle_C130.adr, "AirDropC130".  All three ship in the packs.  [P]
#   * ClientItemDefinitions 1501 "Military Crate", ITEM_CLASS 25016 (World Container), PARAM1 51;
#     ContainerDefinitions row 51 = 100 slots, WITHDRAWAL_ONLY 1, REMOVE_WHEN_EMPTY 1.  [P]
#   * ActorCompositeEffectDefinitions 5038 PFX_Impact_AirDrop_Large.  [P]
#   * StringHashToValue.txt:12  Airdrops.DefaultMinPlayerCount = 1 - the ONLY Airdrops.* tunable
#     in the whole build.  [P]
#
# THE ROSTER BELOW IS A RULING. The client ships no loot table of any kind (AUDIT-loot.md 1.1),
# and the owner's own Z1 tree has NO airdrop implementation at all - ZoneRetailSpawn.cs:690
# `internal const bool RetailAirdrops = false;`, ZoneAdminCommands.cs:2745 "no airdrop manager -
# nothing in this server spawns an airdrop". So there is nothing to adopt under D53 except the
# ammunition counts, which his own note ZoneLootClusters.cs:173-176 sources to the retail Steam
# guide: "AR15 ... ammo (30-60)", "shotgun shells (12-24)", "Hunting Rifle ... ammo (12)". This
# table takes the TOP of each range - an airdrop is the good crate.
#
# SHAPE. A crate is a set of BUNDLES. `guaranteed` is handed out whole to every crate; `rifle` is
# handed out whole with probability AirdropOptions.RifleChance (retail's 50 %); `pool` is drawn
# from AirdropOptions.PoolDraws times by weight. A bundle is one or more item rows, so a gun and
# its ammunition can never be separated - the failure signature docs/39 5 exists to prevent.
# --------------------------------------------------------------------------------------

AIRDROP_FORMAT = "cranberry.airdrop"
AIRDROP_VERSION = 1

#: the crate itself. Every id here is the client's own (see the header above).
AIRDROP_CRATE = {
    "itemDefinitionId": 1501,          # "Military Crate", ITEM_CLASS 25016 World Container
    "containerDefinitionId": 51,       # 100 slots, withdrawal-only, removed when empty
    "groundStem": "Common_Props_MilitaryCrate",            # 9218 "Crate.Military for air drops."
    "descendingStem": "Common_Props_MilitaryCrate_Parachute",  # 9219 "...Parachute for air drops."
    "planeStem": "Vehicle_C130",                            # 9215 "AirDropC130"
    "landingEffectName": "PFX_Impact_AirDrop_Large",        # composite effect 5038
}


def B(weight: int, why: str, *items: tuple[int, str, int, str]) -> dict:
    """One weighted bundle. Each item is (itemDefinitionId, ground-model stem, count, kind)."""
    for _id, _stem, _count, kind in items:
        if kind not in KINDS:
            raise SystemExit(f"airdrop bundle {why!r}: unknown kind {kind!r}")
    return {"weight": weight, "why": why, "items": list(items)}


AIRDROP_BUNDLES: dict[str, list[dict]] = {
    # "always laminated armor" - the ONE guaranteed item retail names, plus the medical the same
    # sentence names ("plus medkits/ammo/guns"). RULING on the count, retail fact on the vest.
    "guaranteed": [
        B(1, "Laminated Tactical Body Armor - GUARANTEED in every drop from 2017-06-29 "
             "(web-research-aug2017-kotk.md:136, High). This is the only route to a vest that "
             "is not subject to D275's 5 % world chance.",
          (2271, "Common_Props_Armor_Kevlar_Basic", 1, BODY_ARMOR)),
        B(1, "Two Tactical First Aid Kits - retail's 'plus medkits'. The COUNT is a RULING; "
             "listing the row twice rather than asking for a stack of two is the client's own "
             "rule, not a style choice: 2424's MAX_STACK_SIZE is 1, so a count of 2 would be "
             "clamped to 1 and the crate would quietly hold half what this table says.",
          (2424, "Common_Props_FirstAidKit", 1, MEDICAL),
          (2424, "Common_Props_FirstAidKit", 1, MEDICAL)),
    ],
    # "50 % chance of a hunting rifle" - and this is the ONLY route by which the .308 and its
    # ammunition reach a player at all. Both are correctly excluded from the ground roster
    # BECAUSE they were airdrop-only; excluding them without building this removed the weapon
    # from the game (AUDIT-loot.md F10).
    "rifle": [
        B(1, ".308 Hunting Rifle + one magazine's worth of ammunition. Item 1373 and NOT 1899: "
             "1899 carries PASSIVE_EQUIP_SLOT_ID 0 and so can never auto-equip, exactly as "
             "docs/39 7 rules for the M1911's second row. Ground form 9204 "
             "Weapon_M24_OnGround, DESCRIPTION 'NPC_Spawn_Weapon_M24_OnGround'; the .308 box "
             "prop is 10190 Common_Props_AmmoBoxes_Sniper. Twelve rounds is the retail Steam "
             "guide's own count for a crate ('Hunting Rifle ... ammo (12)').",
          (1373, "Weapon_M24", 1, WEAPON),
          (1469, "Common_Props_AmmoBoxes_Sniper", 12, AMMUNITION)),
    ],
    # "plus medkits/ammo/guns". Weights are a RULING; the pairing is not negotiable.
    "pool": [
        B(20, "AR-15 with 60 rounds - the Steam guide's top of 'ammo (30-60)'.",
          (10, "Weapon_M16A4", 1, WEAPON), (1429, "Common_Props_AmmoBox02", 60, AMMUNITION)),
        B(20, "AK-47 with 60 rounds.",
          (2229, "Weapon_AK47", 1, WEAPON), (2325, "Common_Props_AmmoBoxes_AK47", 60, AMMUNITION)),
        B(20, "12GA Pump Shotgun with 24 shells - the top of 'shotgun shells (12-24)'.",
          (1374, "Weapons_PumpShotgun01", 1, WEAPON), (1511, "Common_Props_AmmoBoxe01", 24, AMMUNITION)),
        B(15, "Light Blue Tactical Helmet - the roster's tactical helmet.",
          (2172, "Common_Props_Clothes_TacticalHelmet", 1, HELMET)),
        B(15, "Tan Military Backpack - the +2000 bulk bag.",
          (2124, "BackpackOnGround001", 1, BACKPACK)),
    ],
}


def build_airdrop(models, spelling, items, gate, names) -> tuple[dict, list[str]]:
    """The airdrop crate's own file. Same D3/D4/D5 rules as the ground tables."""
    problems: list[str] = []

    def rows(bundles: list[dict], where: str) -> list[dict]:
        out = []
        for bundle in bundles:
            emitted = []
            for item_id, stem, count, kind in bundle["items"]:
                row = item_row(items, problems, where, item_id, gate)
                if row is None:
                    continue
                model_id, model_file = resolve_ground_model(models, spelling, stem, None)
                max_stack = int(row["MAX_STACK_SIZE"] or 1)
                clamped = max(1, min(count, max_stack))
                if clamped != count:
                    problems.append(
                        f"{where}: item {item_id} count {count} clamped to MAX_STACK_SIZE {max_stack}")
                emitted.append({
                    "itemDefinitionId": item_id,
                    "name": names.get(item_id, ""),
                    "nameId": int(row["NAME_ID"] or 0),
                    "groundModelId": model_id,
                    "groundModel": model_file,
                    "count": clamped,
                    "kind": kind,
                    "itemClass": int(row["ITEM_CLASS"] or 0),
                    "maxStackSize": max_stack,
                    "passiveEquipSlotId": int(row["PASSIVE_EQUIP_SLOT_ID"] or 0),
                })
            out.append({"weight": bundle["weight"], "why": bundle["why"], "items": emitted})
        return out

    crate_row = item_row(items, problems, "crate", AIRDROP_CRATE["itemDefinitionId"], gate)
    ground_id, ground_file = resolve_ground_model(
        models, spelling, AIRDROP_CRATE["groundStem"], None)
    descending_id, descending_file = resolve_ground_model(
        models, spelling, AIRDROP_CRATE["descendingStem"], None)
    plane_id, plane_file = resolve_ground_model(models, spelling, AIRDROP_CRATE["planeStem"], None)

    doc = {
        "format": AIRDROP_FORMAT,
        "formatVersion": AIRDROP_VERSION,
        "generator": "tools/data/gen-loot-tables.py",
        "zone": "Z2",
        "clientBuild": "0.0.118.208059",
        "ruling": "D274",
        "note": "Retail's second loot channel (AUDIT-loot.md G4). The crate, its models and its "
                "container definition are the client's own; the roster and every weight are a "
                "RULING - the client ships no loot table and the owner's Z1 tree has no airdrop "
                "implementation to adopt (ZoneRetailSpawn.cs:690 RetailAirdrops = false).",
        "crate": {
            "itemDefinitionId": AIRDROP_CRATE["itemDefinitionId"],
            "name": names.get(AIRDROP_CRATE["itemDefinitionId"], ""),
            "nameId": int(crate_row["NAME_ID"] or 0) if crate_row else 0,
            "itemClass": int(crate_row["ITEM_CLASS"] or 0) if crate_row else 0,
            "containerDefinitionId": AIRDROP_CRATE["containerDefinitionId"],
            "groundModelId": ground_id,
            "groundModel": ground_file,
            "descendingModelId": descending_id,
            "descendingModel": descending_file,
            "planeModelId": plane_id,
            "planeModel": plane_file,
            "landingEffectName": AIRDROP_CRATE["landingEffectName"],
            "why": "Models.txt 9218 'Crate.Military for air drops.', 9219 "
                   "'Crate.Military.Parachute for air drops.', 9215 'AirDropC130'; "
                   "ClientItemDefinitions 1501 'Military Crate' ITEM_CLASS 25016 PARAM1 51; "
                   "ContainerDefinitions 51 = 100 slots, WITHDRAWAL_ONLY, REMOVE_WHEN_EMPTY.",
        },
        "guaranteed": rows(AIRDROP_BUNDLES["guaranteed"], "airdrop.guaranteed"),
        "rifle": rows(AIRDROP_BUNDLES["rifle"], "airdrop.rifle"),
        "pool": rows(AIRDROP_BUNDLES["pool"], "airdrop.pool"),
    }
    return doc, problems


# --------------------------------------------------------------------------------------
# Client-data loading
# --------------------------------------------------------------------------------------


def load_models(path: Path) -> tuple[dict[str, list[int]], dict[str, str], list[str]]:
    """MODEL_FILE_NAME (lowercased) -> every model id that carries it, ascending.

    Nineteen names appear twice in the August Models.txt (e.g. Weapons_Bat01_OnGround.adr as 42
    and 9312). As tools/zone/zonespawners.py already does, the lower id wins by default and the
    clash is printed rather than silently resolved - but a table row may pin the other id with
    ``prefer`` when the client's own DESCRIPTION column says which row is meant.
    """
    ids: dict[str, list[int]] = {}
    spelling: dict[str, str] = {}
    for row in Sheet(path):
        original = row["MODEL_FILE_NAME"].strip()
        name = original.lower()
        if not name:
            continue
        ids.setdefault(name, []).append(int(row["ID"]))
        spelling.setdefault(name, original)
    clashes = [f"{spelling[n]} = {sorted(v)}" for n, v in ids.items() if len(v) > 1]
    for value in ids.values():
        value.sort()
    return ids, spelling, clashes


def load_items(path: Path) -> dict[int, dict[str, str]]:
    return {int(row["ID"]): row for row in Sheet(path)}


def load_kotk_gating(path: Path) -> dict[int, bool]:
    """CONTENT_ID -> is the pack unlocked on the live King of the Kill title (D5)."""
    gate: dict[int, bool] = {}
    for row in Sheet(path):
        gate[int(row["ID"])] = row["LIVE_KOTK_UNLOCKED"] == "1"
    return gate


def load_names(path: Path) -> dict[int, str]:
    """Localised item names out of the derived catalogue; cosmetic only, never load-bearing."""
    if not path.exists():
        return {}
    doc = json.loads(path.read_text(encoding="utf-8"))
    return {int(x["id"]): (x.get("name") or "") for x in doc.get("items", [])}


def resolve_ground_model(models, spelling, stem: str, prefer: int | None) -> tuple[int, str]:
    """D4. Prefer the client's own ``_OnGround`` row, else the bare actor.

    The client ships both forms for most loot props and its DESCRIPTION column says which is
    which ("NPC Spawn Assault Rifle" on Weapon_M16A4_OnGround, "Ground spawn kevlar vest" on
    Common_Props_Armor_Kevlar_Basic). Preferring _OnGround is Cranberry's reading of that
    naming; docs/13 2's rule - MODEL_NAME minus the _3P suffix - is the fallback and is the
    half that is proven on the wire (item 2423 -> model 9066).

    ``prefer`` pins one id of a duplicated name; it must be one of that name's real ids, so a
    stale override is a build error rather than a wrong model on the wire.
    """
    low = stem.lower()
    for candidate in (f"{low}_onground.adr", f"{low}.adr"):
        if candidate in models:
            available = models[candidate]
            if prefer is None:
                return available[0], spelling[candidate]
            if prefer not in available:
                raise SystemExit(
                    f"ground model stem {stem!r} resolves to {available}, which does not "
                    f"include the pinned preferModelId {prefer}")
            return prefer, spelling[candidate]
    raise SystemExit(f"ground model stem {stem!r} matches no Models.txt row")


# --------------------------------------------------------------------------------------
# Table build
# --------------------------------------------------------------------------------------


def item_row(items, problems, where: str, item_id: int, gate) -> dict[str, str] | None:
    row = items.get(item_id)
    if row is None:
        problems.append(f"{where}: item {item_id} is not a ClientItemDefinitions row")
        return None
    content_id = int(row["CONTENT_ID"] or 0)
    if not gate.get(content_id, True):
        problems.append(
            f"{where}: item {item_id} sits in content pack {content_id}, which is not "
            f"LIVE_KOTK_UNLOCKED (D5)")
        return None
    return row


def build_tables(models, spelling, items, gate, names, spawner_counts) -> tuple[dict, list[str]]:
    problems: list[str] = []
    categories = []

    for key, spec in TABLES.items():
        entries = []
        for e in spec["entries"]:
            row = item_row(items, problems, key, e["item"], gate)
            if row is None:
                continue

            model_id, model_file = resolve_ground_model(models, spelling, e["stem"], e["prefer"])
            max_stack = int(row["MAX_STACK_SIZE"] or 1)
            clamped = max(1, min(e["count"], max_stack))
            if clamped != e["count"]:
                problems.append(
                    f"{key}: item {e['item']} count {e['count']} clamped to MAX_STACK_SIZE "
                    f"{max_stack}")

            entries.append({
                "itemDefinitionId": e["item"],
                "name": names.get(e["item"], ""),
                "nameId": int(row["NAME_ID"] or 0),
                "groundModelId": model_id,
                "groundModel": model_file,
                "count": clamped,
                "weight": e["weight"],
                "kind": e["kind"],
                "itemClass": int(row["ITEM_CLASS"] or 0),
                "maxStackSize": max_stack,
                "passiveEquipSlotId": int(row["PASSIVE_EQUIP_SLOT_ID"] or 0),
                "codeFactory": row["CODE_FACTORY_NAME"],
                "why": e["why"],
            })

        categories.append({
            "key": key,
            "spawnerCount": spawner_counts.get(key, 0),
            # O5: the per-marker spawn gate, and since wave 8 it is the OWNER'S OWN number
            # for THIS family (D53) rather than one flat Cranberry-solved value. The field was
            # always per-category; this is the tune it was waiting for.
            "spawnChance": SPAWN_CHANCES.get(key, PRE_WAVE8_SPAWN_CHANCE) if entries else 0.0,
            "totalWeight": sum(x["weight"] for x in entries),
            "note": spec["note"],
            "entries": entries,
        })

    clusters = []
    for weapon_id, ammo_id, stem, count, why in CLUSTERS:
        weapon_row = item_row(items, problems, "clusters", weapon_id, gate)
        ammo_row = item_row(items, problems, "clusters", ammo_id, gate)
        if weapon_row is None or ammo_row is None:
            continue
        model_id, model_file = resolve_ground_model(models, spelling, stem, None)
        max_stack = int(ammo_row["MAX_STACK_SIZE"] or 1)
        clusters.append({
            "weaponItemDefinitionId": weapon_id,
            "weaponName": names.get(weapon_id, ""),
            "itemDefinitionId": ammo_id,
            "name": names.get(ammo_id, ""),
            "nameId": int(ammo_row["NAME_ID"] or 0),
            "groundModelId": model_id,
            "groundModel": model_file,
            "count": max(1, min(count, max_stack)),
            "why": why,
        })

    weapons = {
        e["itemDefinitionId"]
        for cat in categories for e in cat["entries"] if e["kind"] == WEAPON
    }
    clustered = {c["weaponItemDefinitionId"] for c in clusters}
    for missing in sorted(clustered - weapons):
        problems.append(f"clusters: weapon {missing} is not on any table")

    doc = {
        "format": TABLES_FORMAT,
        "formatVersion": TABLES_VERSION,
        "generator": "tools/data/gen-loot-tables.py",
        "zone": "Z2",
        "clientBuild": "0.0.118.208059",
        "sources": {
            "placements": str(SPAWNERS_JSON),
            "items": str(ITEMDEFS_TXT),
            "models": str(MODELS_TXT),
            "contentPacks": str(CONTENTPACKS_TXT),
        },
        "derived": [
            "D1 spawn points: ZONE v5 object placements of the six marker models (docs/29 5).",
            "D2 category keys: the marker model names themselves.",
            "D3 itemDefinitionId / nameId / itemClass / maxStackSize / passiveEquipSlotId: "
            "ClientItemDefinitions.txt rows.",
            "D4 groundModelId: Models.txt rows, _OnGround preferred over the bare actor.",
            "D5 gating: CONTENT_ID -> ContentPacks.LIVE_KOTK_UNLOCKED must be 1.",
        ],
        "ownerAuthored": [
            "O1 which 72 client items are KOTK-2017 ground loot, and the nine exclusions "
            "(.308 rifle and ammo, crossbow, camo tactical helmet, satchel, framed and "
            "black-military backpacks, the Just Survive melee family) - D20, docs/39 1.",
            "O2 the relative weights inside each category.",
            "O3 the retail room census (0-6 loose items, mean ~3) and its 4 m / +-2 m room.",
            "O4 the cluster rule and the per-calibre box sizes 30/30/15/7/7/6/6/5.",
            "O5 (D53, wave 8) the five per-family spawn gates of the world he actually runs: "
            "Weapons01 0.1267, Gear01 0.0850, Backpack01 0.2500, FirstAidKit01 0.3000, "
            "Ammo01 0.6000, weighted 0.1223 - his own spawnChance over his own pool and empty "
            "weights, folded into one number per family.",
        ],
        "designChoice": [
            "X1 (SUPERSEDED by O5 in wave 8) spawnChance 0.27: one flat gate solved in docs/39 "
            "4.2 from Cranberry's own measurement of the client's marker spacing against O3. It "
            "stays reachable as LootDensityOptions.SpawnChanceOverride = 0.27.",
            "X2 count: how many of a stacking item one spawn yields, clamped to MAX_STACK_SIZE.",
            "X3 kind: the coarse class the room caps and the tests reason about; ITEM_CLASS "
            "16053 alone cannot separate ammunition from the two medical items.",
            "X5 calibre-specific ammunition-box props over the generic box (docs/39 3.5).",
        ],
        # O3 + X1. The runtime reads the whole density model from here, so it is retunable
        # without a rebuild - exactly as the odds are.
        "density": {
            "roomRadiusMetres": ROOM_RADIUS_METRES,
            "roomHeightMetres": ROOM_HEIGHT_METRES,
            "maxItemsPerRoom": MAX_ITEMS_PER_ROOM,
            "maxWeaponsPerRoom": MAX_WEAPONS_PER_ROOM,
            "singletonKinds": SINGLETON_KINDS,
            "note": "O3, the owner's own retail footage census: a 4 m / +-2 m room holds 0-6 "
                    "loose items, mean about 3, at most one bag, one vest and one helmet. The "
                    "cap is a post-pass over the deterministic roll, evaluated from EVERY "
                    "member's own room, never a 'stop after N in the burst' - that would be "
                    "the blob-then-nothing defect in a new costume (docs/39 4.3).",
        },
        "clusters": {
            "offsetMetres": CLUSTER_OFFSET_METRES,
            "secondBoxYawOffsetRadians": CLUSTER_SECOND_BOX_YAW,
            # D286 (reverses D272). Read by the runtime (LootTables.BoxesPerGun ->
            # Z2LootLayout.ClusterFor), so the 1-vs-2 question is retuned by editing this file
            # rather than by a rebuild. The second box's yaw offset above is what the second box
            # uses whenever this is 2.
            "boxesPerGun": BOXES_PER_GUN,
            "note": "O4, as re-ruled by D286 (which reverses D272 back to two): every gun on the "
                    "floor carries TWO boxes of its "
                    "own calibre at 0.5 m perpendicular to the marker's yaw. D272 had cut it to "
                    "one because two made a third of every ground object an ammunition box "
                    "(AUDIT-loot.md G2); the owner played that world and ruled it back to two. "
                    "The boxes are extra entities, "
                    "not extra marker rolls, and are exempt from the room cap. There are no "
                    "loose rounds anywhere else: every ammunition stack in the world is a "
                    "whole box (docs/39 5). WAVE 8, the owner's own reason for 0.5 m: it is "
                    "chosen so the gun and BOTH boxes fall inside the client's 2 m proximity "
                    "ball together, i.e. so the [F] panel lists the set as a set. That couples "
                    "this offset to LootStreamOptions.PanelRadiusMetres - widen one and the "
                    "other has to move with it, or a gun's boxes stop being listed with it.",
            "boxes": clusters,
        },
        "categories": categories,
    }
    return doc, problems


# --------------------------------------------------------------------------------------
# Placement build
# --------------------------------------------------------------------------------------


def cell_of(x: float, z: float) -> int:
    cx = int((x - GRID_ORIGIN) // GRID_CELL_METRES)
    cz = int((z - GRID_ORIGIN) // GRID_CELL_METRES)
    cx = min(max(cx, 0), GRID_DIMENSION - 1)
    cz = min(max(cz, 0), GRID_DIMENSION - 1)
    return (cz * GRID_DIMENSION) + cx


def build_spawns(spawner_doc: dict, category_order: list[str]) -> tuple[bytes, dict]:
    """CRLP v1. Fixed 24-byte records, pre-sorted by grid cell, with a cell offset table."""
    category_index = {key: i for i, key in enumerate(category_order)}
    areas: list[str] = []
    area_index: dict[str, int] = {}

    rows = spawner_doc["spawners"]
    packed: list[tuple[int, float, float, float, float, int, int, int]] = []
    skipped_category: collections.Counter[str] = collections.Counter()

    for row in rows:
        key = row["category"]
        ci = category_index.get(key)
        if ci is None:
            skipped_category[key] += 1
            continue

        x, y, z = (float(v) for v in row["pos"])
        yaw = float(row["rot"][0])
        area = row.get("area")
        if area is None:
            ai = NO_AREA
        else:
            ai = area_index.get(area)
            if ai is None:
                ai = len(areas)
                if ai >= NO_AREA:
                    raise SystemExit("more than 65,534 named areas; widen the area index")
                area_index[area] = ai
                areas.append(area)

        packed.append((cell_of(x, z), x, y, z, yaw, int(row["id"]) & 0xFFFFFFFF, ai, ci))

    # X4: sorting here is what lets the runtime index the file directly - no load-time sort,
    # no separate index array, and every cell's points contiguous in memory.
    packed.sort(key=lambda r: r[0])

    counts = [0] * (GRID_DIMENSION * GRID_DIMENSION)
    for rec in packed:
        counts[rec[0]] += 1
    offsets = [0] * (len(counts) + 1)
    running = 0
    for i, c in enumerate(counts):
        offsets[i] = running
        running += c
    offsets[-1] = running

    strings = bytearray()
    for name in category_order:
        strings += name.encode("utf-8") + b"\0"
    for name in areas:
        strings += name.encode("utf-8") + b"\0"

    xs = [r[1] for r in packed]
    ys = [r[2] for r in packed]
    zs = [r[3] for r in packed]

    header = struct.pack(
        "<4sIIIIfffII6f",
        SPAWNS_MAGIC,
        SPAWNS_VERSION,
        len(packed),
        len(category_order),
        GRID_DIMENSION,
        GRID_CELL_METRES,
        GRID_ORIGIN,
        GRID_ORIGIN,
        len(areas),
        len(strings),
        min(xs), min(ys), min(zs), max(xs), max(ys), max(zs),
    )

    body = bytearray()
    body += header
    body += strings
    body += struct.pack(f"<{len(offsets)}I", *offsets)
    record = struct.Struct("<ffffIHBB")
    for _cell, x, y, z, yaw, instance_id, ai, ci in packed:
        body += record.pack(x, y, z, yaw, instance_id, ai, ci, 0)

    stats = {
        "points": len(packed),
        "areas": len(areas),
        "skippedByCategory": dict(skipped_category),
        "occupiedCells": sum(1 for c in counts if c),
        "maxCellCount": max(counts) if counts else 0,
        "headerBytes": len(header) + 4 * len(offsets),
        "bytes": len(body),
    }
    return bytes(body), stats


# --------------------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--spawners", type=Path, default=SPAWNERS_JSON)
    ap.add_argument("--items", type=Path, default=ITEMDEFS_TXT)
    ap.add_argument("--models", type=Path, default=MODELS_TXT)
    ap.add_argument("--content-packs", type=Path, default=CONTENTPACKS_TXT)
    ap.add_argument("--names", type=Path, default=LOOT_JSON)
    ap.add_argument("--out-dir", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--report", action="store_true", help="print the audit and write nothing")
    args = ap.parse_args(argv)

    models, spelling, clashes = load_models(args.models)
    items = load_items(args.items)
    gate = load_kotk_gating(args.content_packs)
    names = load_names(args.names)

    print(f"Models.txt        {len(models):>7,} distinct actor names"
          + (f"  ({len(clashes)} duplicate name(s))" if clashes else ""))
    print(f"item definitions  {len(items):>7,} rows")
    print(f"content packs     {len(gate):>7,} rows, "
          f"{sum(1 for v in gate.values() if not v)} not live on KotK")

    spawner_doc = json.loads(args.spawners.read_text(encoding="utf-8"))
    spawner_counts = spawner_doc["countsByCategory"]
    print(f"placements        {spawner_doc['spawnerCount']:>7,} spawn markers in {spawner_doc['zone']}")

    tables, problems = build_tables(models, spelling, items, gate, names, spawner_counts)
    airdrop, airdrop_problems = build_airdrop(models, spelling, items, gate, names)
    problems += airdrop_problems

    print()
    expected_items = 0.0
    for cat in tables["categories"]:
        live = cat["spawnerCount"] * cat["spawnChance"]
        expected_items += live
        print(f"  {cat['key']:<16} {cat['spawnerCount']:>7,} markers  "
              f"{len(cat['entries']):>2} items  weight {cat['totalWeight']:>5}  "
              f"p={cat['spawnChance']:.2f}  ~{live:,.0f} on the floor")
        for e in cat["entries"]:
            share = (e["weight"] / cat["totalWeight"] * 100.0) if cat["totalWeight"] else 0.0
            print(f"      {e['itemDefinitionId']:>5}  {e['name'][:30]:<30} {e['kind']:<11}"
                  f"x{e['count']:<4} model {e['groundModelId']:<6} w{e['weight']:<4} {share:5.1f}%")

    boxes = tables["clusters"]["boxes"]
    guns = sum(
        cat["spawnerCount"] * cat["spawnChance"]
        * sum(e["weight"] for e in cat["entries"]
              if e["kind"] == WEAPON and e["itemDefinitionId"]
              in {b["weaponItemDefinitionId"] for b in boxes})
        / max(1, cat["totalWeight"])
        for cat in tables["categories"] if cat["totalWeight"]
    )
    print(f"\n  clusters        {len(boxes)} gun -> ammunition pairing(s), "
          f"{CLUSTER_OFFSET_METRES} m out, {BOXES_PER_GUN} box per gun (D272); "
          f"~{guns:,.0f} clustered gun(s) => ~{BOXES_PER_GUN * guns:,.0f} extra box(es)")
    for b in boxes:
        print(f"      {b['weaponItemDefinitionId']:>5} {b['weaponName'][:22]:<22} -> "
              f"{b['itemDefinitionId']:>5} {b['name'][:22]:<22} x{b['count']:<3} "
              f"model {b['groundModelId']}")

    print(f"\n  airdrop (D274)  crate item {airdrop['crate']['itemDefinitionId']} "
          f"({airdrop['crate']['name']}), ground model {airdrop['crate']['groundModelId']}, "
          f"container def {airdrop['crate']['containerDefinitionId']}")
    for section in ("guaranteed", "rifle", "pool"):
        for bundle in airdrop[section]:
            listing = ", ".join(
                f"{i['itemDefinitionId']} x{i['count']}" for i in bundle["items"])
            print(f"      {section:<11} w{bundle['weight']:<3} {listing}")

    print(f"\n  expected world census: ~{expected_items:,.0f} items before the room caps, "
          f"~{BOXES_PER_GUN * guns:,.0f} cluster boxes")

    for key in spawner_counts:
        if key not in TABLES:
            problems.append(f"Z2 places category {key!r}, which has no table")

    if problems:
        print("\nPROBLEMS")
        for p in problems:
            print(f"  ! {p}")

    payload, stats = build_spawns(spawner_doc, list(TABLES.keys()))
    print(f"\nplacement file    {stats['points']:,} points, {stats['areas']} named areas, "
          f"{stats['occupiedCells']:,} occupied cells, densest cell {stats['maxCellCount']:,}, "
          f"{stats['bytes']:,} bytes")
    if stats["skippedByCategory"]:
        print(f"  ! points with no table: {stats['skippedByCategory']}")

    if args.report:
        return 1 if problems else 0

    args.out_dir.mkdir(parents=True, exist_ok=True)
    tables_path = args.out_dir / "z2-loot-tables.json"
    spawns_path = args.out_dir / "z2-loot-spawns.bin"
    airdrop_path = args.out_dir / "z2-airdrop.json"
    tables_path.write_text(json.dumps(tables, indent=1) + "\n", encoding="utf-8", newline="\n")
    spawns_path.write_bytes(payload)
    airdrop_path.write_text(json.dumps(airdrop, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"\nwrote {tables_path} ({tables_path.stat().st_size:,} bytes)")
    print(f"wrote {spawns_path} ({spawns_path.stat().st_size:,} bytes)")
    print(f"wrote {airdrop_path} ({airdrop_path.stat().st_size:,} bytes)")
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
