#!/usr/bin/env python3
r"""
derive_weapons.py - build Cranberry's server-ready ``weapons`` dataset.

INPUT
  * The August client's datasheets already extracted into ``--data-dir``
    (default C:\Aug2017\out\data_aug), produced by tools/pack/packread.py:
        ClientItemDefinitions.txt   ClientItemDatasheetData.txt  ClientDatasheets.txt
        Datasheets.txt              ItemClasses.txt              EquipmentSlotDefinitions.txt
        EquipSlotItemClasses.txt    FireModeDisplayStats.txt     FireModeEffectGroups.txt
        ProjectileDisplayStats.txt  ProjectileToPenTypes.txt     PenTypes.txt
        PenTypeMaterials.txt        MaterialTypes.txt            ResistTypes.txt
        ResistInfo.txt              ArmorInfo.txt                DamageLevelInfo.txt
        DamageLevelMappings.txt     DamageTables.txt             Reticles.txt
        FirstPersonAttachments.txt  DatasheetProperties.txt      ItemDatasheetPropertyMap.txt
        AbilityEx.txt               ActorProjectileDefinitions.xml
  * ``item-names-en_us.json`` and ``locale-en_us.json`` in the same directory
    (tools/data/item-names.py, tools/locale/localedat.py).
  * Optionally ``H1Z1.exe`` (--exe), read *only* to re-verify the ReferenceData column-name
    runs this tool cites; the runs are also embedded so the tool works without it.

OUTPUT
  One JSON document (default C:\Aug2017\out\data_aug\derived\weapons.json) in Cranberry's
  own schema ``cranberry/weapons`` - see SCHEMA below. Every leaf carries a provenance
  entry in the document's own ``fieldProvenance`` map, so any number in the file can be
  traced back to a sheet column, a locale key, an exe offset, or an explicit derivation.

  Nothing is invented. Where the August client does not carry a value the server needs,
  the field is absent and the gap is listed in ``serverSideGaps`` with the evidence for
  why it is missing and who has to author it.

SCHEMA (this project's own design; version 1)

  schema / schemaVersion / clientBuild / generated / counts
  fieldProvenance   {json pointer-ish path -> source}
  enums
      penetrationTypes      PenTypes.txt                    id -> calibre name
      materialTypes         MaterialTypes.txt               id -> surface name
      damageTypes           ResistTypes.txt                 id -> damage-category name
                                                            (``retired`` marks the ``zzz`` rows)
      reticles              Reticles.txt
      holsterSlots          EquipmentSlotDefinitions.txt    the weapon-bearing slots only
      itemCategories        Datasheets.txt                  TYPE_NAME per datasheet id
      fireEffectTags        FireModeEffectGroups.TAG        the distinct server-visible shot events
      muzzleAttachPoints    ActorProjectileDefinitions.xml
  penetration[]     one entry per pen type: the per-material ricochet / penetration-depth
                    table. This is the only real wallbang/ricochet data in the client and
                    it is complete.
  projectiles[]     ProjectileToPenTypes joined to PenTypes and ProjectileDisplayStats.
  mitigation
      resistProfiles[]      ResistInfo (percent-then-flat mitigation, keyed by damage type)
      armourProfiles[]      ArmorInfo (directional armour by FACING)
      vehicleArmour[]       VehicleArmorMappings
      vehicleDamageLevels[] DamageLevelInfo + DamageLevelMappings
      damageTables[]        DamageTables
  equipSlotItemClasses[]    which item class may occupy which weapon holster slot
  fireGroups[]      per fire group: the fire / dryfire effect ids and the (zeroed) UI stats
  weapons[]         one entry per WEAPON_ID - the join key the server should key on
  weaponItems[]     one entry per catalogue item whose CODE_FACTORY_NAME is ``Weapon``
  ammoItems[]       the ammunition catalogue referenced by the weapons' ammo text
  serverSideGaps[]  every value the combat system needs that this client does not carry

FORMAT FACTS THIS TOOL RELIES ON (each with its evidence)

  F1  Datasheet dialect: ``^``-delimited, ``#``-prefixed header, ``*`` marks key columns,
      one trailing empty field per line. Evidence: tools/data/sheet.py header comment
      (all 293 ``.txt`` assets in pack1-index-aug.txt parsed with it).

  F2  ``ClientItemDefinitions.PARAM1`` on a row whose ``CODE_FACTORY_NAME`` is ``Weapon``
      IS the weapon-definition id. Evidence: it equals
      ``ClientItemDatasheetData.WEAPON_ID`` for all 160 rows that have both (checked at
      run time by this tool, see ``counts.param1WeaponIdAgreement``). This is what lets
      the 53 weapon items with no datasheet row still resolve to a weapon.

  F3  ``ClientItemDefinitions.DATASHEET_ID`` indexes ``Datasheets.txt``, whose
      ``TYPE_NAME`` is the item's broad category (FirearmItemDatasheet /
      MeleeItemDatasheet / ThrowableItemDatasheet / MedicalItemDatasheet / ...).
      Evidence: Datasheets.txt ids 15..25; item 2 (M1911A1) carries 16 =
      ``FirearmItemDatasheet``. NOTE this is a *different* table from
      ``ClientItemDatasheetData.DATASHEET_ID``, which indexes ``ClientDatasheets.txt``
      (a UI display template, values 0/2/11 only).

  F4  ``ClientItemDefinitions.PASSIVE_EQUIP_SLOT_ID`` indexes
      ``EquipmentSlotDefinitions.ID`` and names the holster the weapon hangs in
      (76/77/80 ``R_LongWeapon_*``, 78/79/81 ``R_ShortWeapon_*``, 82/83/84
      ``R_bowWeapon_*``, 106/107 ``*_MeleeWeapon_Rear_Melee_Diag``), with
      ``BONE_ATTACH_NAME_OVERRIDE`` giving the attach bone.
      ``ACTIVE_EQUIP_SLOT_ID`` is 7 = ``RHand`` for all 213 weapon items.

  F5  The weapon's calibre is carried in the client's own English item description as a
      literal ``[Ammo Type: <name>]`` tail, and ``<name>`` is the *name of a real
      catalogue item* (".45 Round" = item 1428, "12 Gauge Buckshot Shell" = 1511, ...).
      This is the only client-carried weapon -> ammunition link in the build.

  F6  The pen-type <-> ammunition correspondence is NOT carried by the client. This tool
      makes it with the explicit calibre table ``AMMO_TO_PEN_TYPE`` below; every emitted
      link records ``basis`` so the inference is visible. Anything not in that table is
      left unresolved and reported as a gap.

  F7  The client ships the weapon *schema* but not the weapon *numbers*: every damage,
      falloff, cone-of-fire and armour-amount column in the shipped sheets is zero (the
      two 3500/500 rows in FireModeDisplayStats and the 3500 on item 100 "DEV SUPER BOW"
      are leftover test values, reported in ``counts.nonZeroDisplayStatRows``).

  F8  The real weapon tables reach the client as **ReferenceData**, not as pack files.
      The client's ReferenceData dispatcher names its handlers at file offset 0x3117568:
      ``ItemClasses``, ``DynamicAppearanceDefinitions``, ``ItemCategories``,
      ``ProfileDefinitions``, ``ProjectileDefinitions``, ``WeaponDefinitions``, followed
      by ``Received ReferenceData type=%s, but no handler!``. The column names of those
      wire tables are still in the binary's schema string pool, and this tool reads them
      back (``REFERENCE_DATA_SCHEMA_RUNS``) so ``serverSideGaps`` can name every field
      the server has to author. No ``WeaponDefinitions``/``FireModes``/``Projectiles``
      ``.txt`` filename exists anywhere in that pool, which is why the sheets are absent
      from the packs.

Usage:
    python tools/data/derive_weapons.py
    python tools/data/derive_weapons.py --data-dir C:\Aug2017\out\data_aug \
        --out C:\Aug2017\out\data_aug\derived\weapons.json --indent 1
    python tools/data/derive_weapons.py --no-exe        # skip the binary re-verification
    python tools/data/derive_weapons.py --summary       # print counts + gaps, write nothing
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable

_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE))                       # tools/data  -> sheet.py
sys.path.insert(0, str(_HERE.parent / "locale"))     # tools/locale -> localedat.py

from sheet import Sheet                               # noqa: E402
from localedat import text_key                        # noqa: E402

SCHEMA_NAME = "cranberry/weapons"
SCHEMA_VERSION = 1
CLIENT_BUILD = "0.0.118.208059"

DEFAULT_DATA_DIR = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = DEFAULT_DATA_DIR / "derived" / "weapons.json"
DEFAULT_EXE = Path(r"C:\Aug2017\Client\H1Z1.exe")

#: The item description tail that carries the calibre (fact F5).
AMMO_TYPE_RE = re.compile(r"\[Ammo Type:\s*([^\]]+)\]", re.IGNORECASE)

#: Strip the client's inline markup out of a display name / description.
MARKUP_RE = re.compile(r"<[^>]+>")

#: fact F6 - this tool's own calibre correspondence, ammo item name -> PenTypes.ID.
#: The left side is an item name from ClientItemDefinitions; the right side a
#: PenTypes.DESCRIPTION. Both are quoted verbatim so the inference is auditable.
AMMO_TO_PEN_TYPE: dict[str, tuple[int, str]] = {
    ".45 round":                 (5,  '".45 Round" -> PenTypes 5 ".45 ACP"'),
    ".223 round":                (3,  '".223 Round" -> PenTypes 3 ".223"'),
    ".308 round":                (8,  '".308 Round" -> PenTypes 8 ".308 FMJ"'),
    "12 gauge buckshot shell":   (4,  '"12 Gauge Buckshot Shell" -> PenTypes 4 "12GA shotgun"'),
    ".44 round":                 (6,  '".44 Round" -> PenTypes 6 ".44 Magnum"'),
    ".380 round":                (7,  '".380 Round" -> PenTypes 7 ".380"'),
    "9mm round":                 (9,  '"9mm Round" -> PenTypes 9 ".9mm"'),
    "7.62x39 round":             (10, '"7.62x39 Round" -> PenTypes 10 "Rifle.AK47.762Bullet"'),
}

#: The ReferenceData schema column-name runs in H1Z1.exe (fact F8). Each entry is
#: (label, first file offset, end file offset exclusive, note). The runs are re-read from
#: the binary when --exe is available and compared against EXPECTED_RUN_HEADS.
REFERENCE_DATA_SCHEMA_RUNS: list[tuple[str, int, int, str]] = [
    ("WeaponDefinitions", 0x35E0298, 0x35E09D0,
     "fire-group, weapon, ammo-slot and weapon->fire-group columns, in that order; the "
     "sub-table boundaries inside the run are inferred from adjacency, not proven"),
    ("ProjectileDefinitions", 0x35E73A8, 0x35E78D8,
     "flight, gravity/drag, indirect-damage radius and tracer columns"),
    ("FireModes", 0x35E79D8, 0x35E9000,
     "per-fire-mode behaviour: ammo, burst, timing, pellets, recoil, sway, zoom, cameras"),
    ("AimAssist", 0x35E9098, 0x35E92E0,
     "matches the AimAssist.<Name> client settings at file offset 0x30FDCE8"),
    ("ConeOfFire", 0x35E9398, 0x35E9580,
     "per-player-state cone/cylinder of fire bloom and recovery"),
]

#: One sentinel name per run so a re-read of a different binary fails loudly.
EXPECTED_RUN_HEADS = {
    "WeaponDefinitions": "ITEM_ID",
    "ProjectileDefinitions": "LOS_FLAG",
    "FireModes": "HIDE_UNAVAILABLE",
    "AimAssist": "CONE_ANGLE",
    "ConeOfFire": "ROTATION",
}

SCHEMA_STRING_RE = re.compile(rb"[A-Z][A-Z0-9_]{2,63}\x00")

#: The ReferenceData handler names, and the offset of the block that lists them (fact F8).
REFERENCE_DATA_HANDLERS = [
    "ItemClasses", "DynamicAppearanceDefinitions", "ItemCategories",
    "ProfileDefinitions", "ProjectileDefinitions", "WeaponDefinitions",
]
REFERENCE_DATA_HANDLER_OFFSET = 0x3117568


# ---------------------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------------------


def _int(value: str | None, default: int | None = 0) -> int | None:
    """Datasheet cell -> int. Empty stays ``default`` so 'unset' never becomes a fake 0."""
    if value is None or value == "":
        return default
    try:
        return int(value)
    except ValueError:
        return default


def _num(value: str | None) -> float | int | None:
    """Datasheet cell -> int when integral, float otherwise, None when empty."""
    if value is None or value == "":
        return None
    try:
        f = float(value)
    except ValueError:
        return None
    return int(f) if f.is_integer() else f


def _flag(value: str | None) -> bool:
    return value not in (None, "", "0")


def _clean(text: str | None) -> str | None:
    """Drop the client's inline markup and the <br> line breaks from a display string."""
    if not text:
        return None
    out = text.replace("<br><br>", "\n").replace("<br>", "\n")
    out = MARKUP_RE.sub("", out).strip()
    return out or None


def _prune(obj: Any) -> Any:
    """Drop None values and empty containers so an absent client value is *absent*."""
    if isinstance(obj, dict):
        out = {}
        for k, v in obj.items():
            v = _prune(v)
            if v is None or v == {} or v == []:
                continue
            out[k] = v
        return out
    if isinstance(obj, list):
        return [_prune(v) for v in obj]
    return obj


class Data:
    """Every sheet this tool reads, loaded once and indexed."""

    def __init__(self, data_dir: Path) -> None:
        self.dir = data_dir
        self.missing: list[str] = []
        self.read: dict[str, int] = {}

    def rows(self, name: str) -> list[dict[str, str]]:
        path = self.dir / name
        if not path.is_file():
            self.missing.append(name)
            return []
        rows = list(Sheet(path))
        self.read[name] = len(rows)
        return rows

    def by(self, name: str, key: str) -> dict[str, dict[str, str]]:
        return {r[key]: r for r in self.rows(name)}

    def json(self, name: str) -> Any:
        path = self.dir / name
        if not path.is_file():
            self.missing.append(name)
            return {}
        loaded = json.loads(path.read_text(encoding="utf-8"))
        self.read[name] = len(loaded)
        return loaded

    def text(self, name: str) -> str:
        path = self.dir / name
        if not path.is_file():
            self.missing.append(name)
            return ""
        blob = path.read_text(encoding="latin-1")
        self.read[name] = len(blob)
        return blob


# ---------------------------------------------------------------------------------------
# the binary's ReferenceData schema (fact F8)
# ---------------------------------------------------------------------------------------


def read_schema_runs(exe: Path | None) -> tuple[list[dict[str, Any]], list[str]]:
    """
    Return the ReferenceData column-name runs plus any verification warnings.

    With no exe the runs come back empty and a warning says so - the caller then reports
    the gap without the field list rather than inventing one.
    """
    warnings: list[str] = []
    if exe is None:
        return [], ["--no-exe: the ReferenceData column names were not read from the binary"]
    if not exe.is_file():
        return [], [f"exe not found, ReferenceData column names not read: {exe}"]

    blob = exe.read_bytes()
    runs: list[dict[str, Any]] = []
    for label, start, end, note in REFERENCE_DATA_SCHEMA_RUNS:
        names = [m.group()[:-1].decode("ascii")
                 for m in SCHEMA_STRING_RE.finditer(blob[start:end])]
        head = EXPECTED_RUN_HEADS[label]
        if not names or names[0] != head:
            warnings.append(
                f"{label}: expected the run at 0x{start:07x} to start with {head!r}, "
                f"got {names[0] if names else '(nothing)'!r} - run skipped"
            )
            continue
        runs.append({
            "table": label,
            "fileOffsetStart": f"0x{start:07x}",
            "fileOffsetEnd": f"0x{end:07x}",
            "note": note,
            "columns": names,
        })

    at = blob[REFERENCE_DATA_HANDLER_OFFSET:REFERENCE_DATA_HANDLER_OFFSET + 0x120]
    for handler in REFERENCE_DATA_HANDLERS:
        if handler.encode("ascii") not in at:
            warnings.append(
                f"ReferenceData handler {handler!r} not found at "
                f"0x{REFERENCE_DATA_HANDLER_OFFSET:07x}"
            )
    return runs, warnings


# ---------------------------------------------------------------------------------------
# the build
# ---------------------------------------------------------------------------------------


def build(data_dir: Path, exe: Path | None) -> dict[str, Any]:
    d = Data(data_dir)
    warnings: list[str] = []

    # -- locale ------------------------------------------------------------------------
    locale = d.json("locale-en_us.json")
    item_names = d.json("item-names-en_us.json")

    def loc(string_id: str | int | None) -> str | None:
        n = _int(str(string_id) if string_id is not None else None, None)
        if not n:
            return None
        return locale.get(str(text_key(n)))

    # -- raw sheets --------------------------------------------------------------------
    item_defs = d.by("ClientItemDefinitions.txt", "ID")
    datasheet_data = d.by("ClientItemDatasheetData.txt", "ITEM_ID")
    datasheets = d.by("Datasheets.txt", "ID")
    client_datasheets = d.by("ClientDatasheets.txt", "ID")
    equip_slots = d.by("EquipmentSlotDefinitions.txt", "ID")
    item_classes = d.by("ItemClasses.txt", "ID")
    pen_types = d.by("PenTypes.txt", "ID")
    material_types = d.by("MaterialTypes.txt", "ID")
    resist_types = d.by("ResistTypes.txt", "ID")
    reticles = d.by("Reticles.txt", "ID")
    abilities = d.by("AbilityEx.txt", "ID")
    props = d.by("DatasheetProperties.txt", "ID")

    fire_mode_stats = d.by("FireModeDisplayStats.txt", "ID")
    projectile_stats = d.by("ProjectileDisplayStats.txt", "ID")

    # -- enums -------------------------------------------------------------------------
    enums: dict[str, Any] = {}

    enums["penetrationTypes"] = [
        {"id": _int(r["ID"]), "name": r["DESCRIPTION"]}
        for r in sorted(pen_types.values(), key=lambda r: _int(r["ID"]))
    ]
    enums["materialTypes"] = [
        {"id": _int(r["ID"]), "name": r["NAME"].strip()}
        for r in sorted(material_types.values(), key=lambda r: _int(r["ID"]))
    ]
    enums["damageTypes"] = [
        {
            "id": _int(r["ID"]),
            "name": r["DESCRIPTION"],
            # The client's own convention: a 'zzz' prefix marks a retired PlanetSide-era row.
            "retired": r["DESCRIPTION"].startswith("zzz"),
            "suppressPlayerHitFeedback": _flag(r["SUPPRESS_PLAYER_HIT_FEEDBACK"]),
            "suppressVehicleHitFeedback": _flag(r["SUPPRESS_VEHICLE_HIT_FEEDBACK"]),
        }
        for r in sorted(resist_types.values(), key=lambda r: _int(r["ID"]))
    ]
    enums["reticles"] = [
        {"id": _int(r["ID"]), "name": r["RETICLE_NAME"].strip()}
        for r in sorted(reticles.values(), key=lambda r: _int(r["ID"]))
    ]
    enums["itemCategories"] = [
        {"datasheetId": _int(r["ID"]), "typeName": r["TYPE_NAME"]}
        for r in sorted(datasheets.values(), key=lambda r: _int(r["ID"]))
    ]

    # Muzzle / attach points, from the XML asset (attribute soup, no schema needed).
    muzzles = []
    for m in re.finditer(r"<EffectDefinition\s+([^/>]+)/>", d.text("ActorProjectileDefinitions.xml")):
        attrs = dict(re.findall(r'(\w+)="([^"]*)"', m.group(1)))
        muzzles.append({
            "id": _int(attrs.get("id")),
            "name": attrs.get("name"),
            "boneName": attrs.get("boneName"),
            "targetBoneName": attrs.get("targetBoneName") or None,
        })
    enums["muzzleAttachPoints"] = muzzles

    # -- fire groups -------------------------------------------------------------------
    effects: dict[str, dict[str, int | None]] = {}
    tags: set[str] = set()
    for r in d.rows("FireModeEffectGroups.txt"):
        tags.add(r["TAG"])
        effects.setdefault(r["GROUP_ID"], {})[r["TAG"]] = _int(r["EFFECT_ID"])
    enums["fireEffectTags"] = sorted(tags)

    fire_group_ids = sorted(
        {*effects, *fire_mode_stats} | {r["FIRE_GROUP_ID"] for r in datasheet_data.values()}
        - {"0", ""},
        key=lambda s: _int(s),
    )
    fire_groups = []
    for gid in fire_group_ids:
        stats = fire_mode_stats.get(gid)
        fire_groups.append({
            "fireGroupId": _int(gid),
            "effectsByTag": effects.get(gid) or None,
            "hasDisplayStatsRow": stats is not None,
            # Kept raw because they are zero in this build (fact F7); present so a reader
            # can confirm that for themselves rather than take this tool's word for it.
            "displayStats": {
                "maxDamage": _num(stats["MAX_DAMAGE"]),
                "maxDamageRange": _num(stats["MAX_DAMAGE_RANGE"]),
                "minDamage": _num(stats["MIN_DAMAGE"]),
                "minDamageRange": _num(stats["MIN_DAMAGE_RANGE"]),
                "shieldBypassPct": _num(stats["SHIELD_BYPASS_PCT"]),
                "armorPenetration": _num(stats["ARMOR_PENETRATION"]),
                "maxIndirectDamage": _num(stats["MAX_DAMAGE_IND"]),
                "maxIndirectRadius": _num(stats["MAX_DAMAGE_IND_RADIUS"]),
                "minIndirectDamage": _num(stats["MIN_DAMAGE_IND"]),
                "minIndirectRadius": _num(stats["MIN_DAMAGE_IND_RADIUS"]),
            } if stats else None,
        })

    non_zero_display = [
        gid for gid, r in fire_mode_stats.items()
        if any(v not in ("", "0") for k, v in r.items() if k != "ID")
    ]

    # The same census for ClientItemDatasheetData's damage columns. This used to be a
    # hard-coded sentence ("0 in all 160 rows") sitting next to the computed FireModeDisplay
    # census above, and it was false: ITEM_ID 100 (WEAPON_ID 1383, "DEV SUPER BOW") carries
    # DAMAGE_FALLOFF 3500. Counting it here means the gap text can never drift from the
    # sheet, and the value itself is emitted on the weapon rather than dropped.
    DATASHEET_DAMAGE_COLUMNS = (
        "DIRECT_DAMAGE", "INDIRECT_DAMAGE", "DAMAGE_FALLOFF", "MIN_CONE_OF_FIRE",
    )
    non_zero_datasheet_damage = [
        {"itemId": _int(row["ITEM_ID"]), "weaponId": _int(row["WEAPON_ID"]) or None,
         "column": col, "value": _num(row[col])}
        for row in d.rows("ClientItemDatasheetData.txt")
        for col in DATASHEET_DAMAGE_COLUMNS
        if _num(row[col])
    ]
    non_zero_datasheet_damage.sort(key=lambda e: (e["itemId"], e["column"]))
    datasheet_damage_rows = len(d.rows("ClientItemDatasheetData.txt"))

    def _zero_everywhere(column: str) -> str:
        """Phrase for a column's zero-ness, counted rather than asserted."""
        hits = [e for e in non_zero_datasheet_damage if e["column"] == column]
        if not hits:
            return f"0 in all {datasheet_damage_rows} rows"
        return (f"0 in {datasheet_damage_rows - len(hits)} of {datasheet_damage_rows} rows ("
                + ", ".join(f"item {e['itemId']} = {e['value']:g}" for e in hits) + ")")

    # -- penetration -------------------------------------------------------------------
    pen_materials: dict[str, list[dict[str, Any]]] = {}
    for r in d.rows("PenTypeMaterials.txt"):
        mat = material_types.get(r["MATERIAL_TYPE_ID"])
        pen_materials.setdefault(r["PEN_TYPE_ID"], []).append({
            "materialTypeId": _int(r["MATERIAL_TYPE_ID"]),
            "material": mat["NAME"].strip() if mat else None,
            # BOUNCE=1: the round ricochets off this surface instead of entering it.
            "ricochets": _flag(r["BOUNCE"]),
            # MAX_DEPTH: metres of this material the round penetrates (0.1 - 0.5 in-build).
            "maxDepthMetres": _num(r["MAX_DEPTH"]),
            "entryPointEffectId": _int(r["ENTRY_POINT_EFFECT_ID"]) or None,
            # PGT=1 rows carry neither bounce nor depth: pass-through with no entry effect.
            "passThroughNoEffect": _flag(r["PGT"]),
        })
    penetration = []
    for r in sorted(pen_types.values(), key=lambda r: _int(r["ID"])):
        mats = sorted(pen_materials.get(r["ID"], []), key=lambda m: m["materialTypeId"])
        penetration.append({
            "penTypeId": _int(r["ID"]),
            "name": r["DESCRIPTION"],
            "materialCount": len(mats),
            "materials": mats,
        })

    # -- projectiles -------------------------------------------------------------------
    projectiles = []
    for r in sorted(d.rows("ProjectileToPenTypes.txt"), key=lambda r: _int(r["ID"])):
        pt = pen_types.get(r["PEN_TYPE_ID"])
        ps = projectile_stats.get(r["PROJECTILE_ID"])
        projectiles.append({
            "mappingId": _int(r["ID"]),
            "projectileId": _int(r["PROJECTILE_ID"]),
            "penTypeId": _int(r["PEN_TYPE_ID"]),
            "penTypeName": pt["DESCRIPTION"] if pt else None,
            "hasDisplayStatsRow": ps is not None,
            "displayStats": {
                "maxIndirectDamage": _num(ps["MAX_DAMAGE_IND"]),
                "maxIndirectRadius": _num(ps["MAX_DAMAGE_IND_RADIUS"]),
                "minIndirectDamage": _num(ps["MIN_DAMAGE_IND"]),
                "minIndirectRadius": _num(ps["MIN_DAMAGE_IND_RADIUS"]),
            } if ps else None,
        })

    # -- mitigation --------------------------------------------------------------------
    resist_profiles = []
    unresolved_resist_types = set()
    for r in sorted(d.rows("ResistInfo.txt"), key=lambda r: _int(r["ID"])):
        rt = resist_types.get(r["RESIST_TYPE"])
        if rt is None:
            unresolved_resist_types.add(r["RESIST_TYPE"])
        resist_profiles.append({
            "resistInfoId": _int(r["ID"]),
            "damageTypeId": _int(r["RESIST_TYPE"]),
            "damageType": rt["DESCRIPTION"] if rt else None,
            "resistPercent": _num(r["RESIST_PERCENT"]),
            "resistAmount": _num(r["RESIST_AMOUNT"]),
        })

    armour_profiles = [
        {
            "armorInfoId": _int(r["ID"]),
            # FACING is a hit-side code 0-6; the client carries no label for it.
            "facing": _int(r["FACING"]),
            "armorPercent": _num(r["ARMOR_PERCENT"]),
            "armorAmount": _num(r["ARMOR_AMOUNT"]),
        }
        for r in sorted(d.rows("ArmorInfo.txt"), key=lambda r: _int(r["ID"]))
    ]
    vehicle_armour = [
        {"vehicleId": _int(r["VEHICLE_ID"]), "armorInfoId": _int(r["ARMOR_INFO_ID"])}
        for r in d.rows("VehicleArmorMappings.txt")
    ]

    dl_targets: dict[str, list[int]] = {}
    for r in d.rows("DamageLevelMappings.txt"):
        dl_targets.setdefault(r["DAMAGE_LEVEL_ID"], []).append(_int(r["NPC_ID"]))
    damage_levels = [
        {
            "damageLevelId": _int(r["ID"]),
            "atHealthPercent": _num(r["DAMAGE_PERCENT"]),
            "moveInfoOverride": _int(r["MOVE_INFO_OVERRIDE"]) or None,
            "controlDampening": _num(r["CONTROL_DAMPENING"]) or None,
            "triggeredAbilityId": _int(r["TRIGGERED_ABILITY"]) or None,
            "stopsEngine": _flag(r["STOP_ENGINE"]),
            "npcIds": dl_targets.get(r["ID"]) or None,
        }
        for r in sorted(d.rows("DamageLevelInfo.txt"), key=lambda r: _int(r["ID"]))
    ]
    damage_tables = [
        {
            "id": _int(r["ID"]),
            "damageCategory": _int(r["DAMAGE_CATEGORY"]),
            "damageAmount": _num(r["DAMAGE_AMOUNT"]),
            "damagePerSecond": _num(r["DAMAGE_PER_SECOND"]),
        }
        for r in d.rows("DamageTables.txt")
    ]

    # -- holster slots and their eligible item classes (facts F4) ----------------------
    equip_slot_classes: dict[str, list[int]] = {}
    for r in d.rows("EquipSlotItemClasses.txt"):
        equip_slot_classes.setdefault(r["EQUIP_SLOT_ID"], []).append(_int(r["ITEM_CLASS"]))

    def slot_info(slot_id: str | None) -> dict[str, Any] | None:
        row = equip_slots.get(slot_id or "")
        if row is None:
            return None
        return {
            "id": _int(row["ID"]),
            "name": row["SLOT_NAME"],
            "attachBone": row["BONE_ATTACH_NAME_OVERRIDE"] or None,
        }

    # The item classes weapons actually use, plus the classes any weapon holster accepts.
    weapon_class_ids = {r["ITEM_CLASS"] for r in item_defs.values()
                        if r.get("CODE_FACTORY_NAME") == "Weapon"}
    weapon_class_ids |= {c for ids in equip_slot_classes.values() for c in map(str, ids)}
    enums["itemClasses"] = [
        {
            "id": _int(cid),
            "name": loc((item_classes.get(cid) or {}).get("NAME_ID")),
            # WIELD_TYPE is the client's own hand/stance code; it has no label table.
            "wieldType": _int((item_classes.get(cid) or {}).get("WIELD_TYPE")),
            "secondaryWieldType": _int((item_classes.get(cid) or {}).get("SECONDARY_WIELD_TYPE")),
            "inClientSheet": cid in item_classes,
        }
        for cid in sorted(weapon_class_ids, key=lambda s: _int(s))
    ]

    weapon_slot_ids = sorted(equip_slot_classes, key=lambda s: _int(s))
    enums["holsterSlots"] = [s for s in (slot_info(i) for i in weapon_slot_ids) if s]
    equip_slot_item_classes = [
        {
            "equipSlotId": _int(sid),
            "equipSlotName": (equip_slots.get(sid) or {}).get("SLOT_NAME"),
            "itemClassIds": sorted(set(equip_slot_classes[sid])),
        }
        for sid in weapon_slot_ids
    ]

    # -- ammunition catalogue (fact F5) ------------------------------------------------
    ammo_by_name: dict[str, str] = {}
    ammo_items: list[dict[str, Any]] = []
    for iid, meta in item_names.items():
        name = (meta.get("name") or "").strip()
        cfn = meta.get("code_factory_name")
        key = name.lower()
        is_cartridge = key in AMMO_TO_PEN_TYPE
        if not (is_cartridge or cfn == "Ammo"):
            continue
        pen_id, basis = AMMO_TO_PEN_TYPE.get(key, (None, None))
        # First id wins so a later duplicate name never silently re-points a weapon.
        ammo_by_name.setdefault(key, iid)
        ammo_items.append({
            "itemId": _int(iid),
            "name": name,
            "codeFactoryName": cfn,
            "itemClassId": _int(meta.get("item_class"), None),
            "description": _clean(meta.get("description")),
            "penTypeId": pen_id,
            "penTypeMatchBasis": basis,
        })
    ammo_items.sort(key=lambda a: a["itemId"])

    # -- per-item UI stat bars ---------------------------------------------------------
    bar_label_map = {
        "Damage": "damage", "Range": "range", "Fire Rate": "fireRate", "Recoil": "recoil",
        "Capacity": "capacity", "Swing Speed": "swingSpeed", "Duration": "duration",
        "Radius": "radius", "Protection": "protection", "Amount": "amount",
        "Durability": "durability", "Speed": "speed", "Sturdiness": "sturdiness",
        "Stealth": "stealth", "Healing": "healing", "Use Time": "useTime",
    }
    bar_order = list(bar_label_map.values())
    raw_bars: dict[str, dict[str, int | None]] = {}
    for r in d.rows("ItemDatasheetPropertyMap.txt"):
        prop = props.get(r["DATASHEET_PROPERTY_ID"])
        if prop is None:
            continue
        label = loc(prop["NAME_ID"])
        key = bar_label_map.get(label or "")
        if key is None:
            continue
        entry = raw_bars.setdefault(r["ITEM_ID"], {})
        entry[key] = _int(prop["VALUE"])
        entry["scale"] = _int(prop["VALUE_MAX"])
    # Fixed key order so the output is byte-stable across runs.
    bars: dict[str, dict[str, int | None]] = {}
    for iid, entry in raw_bars.items():
        ordered = {k: entry[k] for k in bar_order if k in entry}
        ordered["scale"] = entry.get("scale")
        bars[iid] = ordered

    # -- first-person mesh pairing -----------------------------------------------------
    fp_by_3p = {
        r["ATTACHMENT_MESH_3P"].strip().lower(): r["ATTACHMENT_MESH_1P"].strip()
        for r in d.rows("FirstPersonAttachments.txt")
    }

    # -- weapon items ------------------------------------------------------------------
    weapon_item_rows = [
        r for r in item_defs.values() if r.get("CODE_FACTORY_NAME") == "Weapon"
    ]
    weapon_item_rows.sort(key=lambda r: _int(r["ID"]))

    param1_agree = param1_disagree = 0
    weapon_items: list[dict[str, Any]] = []
    for r in weapon_item_rows:
        iid = r["ID"]
        ds = datasheet_data.get(iid)
        if ds is not None:
            if ds["WEAPON_ID"] == r["PARAM1"]:
                param1_agree += 1
            else:
                param1_disagree += 1
                warnings.append(
                    f"item {iid}: ClientItemDefinitions.PARAM1={r['PARAM1']} but "
                    f"ClientItemDatasheetData.WEAPON_ID={ds['WEAPON_ID']} (fact F2 broken)"
                )
        meta = item_names.get(iid, {})
        cat_row = datasheets.get(r["DATASHEET_ID"])
        model = (r["MODEL_NAME"] or "").strip()
        ability = abilities.get(r["ACTIVATABLE_ABILITY_ID"])
        weapon_items.append({
            "itemId": _int(iid),
            "weaponId": _int(r["PARAM1"]) or None,
            "name": _clean(meta.get("name")) or loc(r["NAME_ID"]),
            "description": _clean(meta.get("description")),
            "itemClassId": _int(r["ITEM_CLASS"]),
            "category": cat_row["TYPE_NAME"] if cat_row else None,
            "categoryDatasheetId": _int(r["DATASHEET_ID"]) or None,
            "hasDatasheetRow": ds is not None,
            "holsterSlot": slot_info(r["PASSIVE_EQUIP_SLOT_ID"]),
            "activeEquipSlot": slot_info(r["ACTIVE_EQUIP_SLOT_ID"]),
            "modelName3p": model or None,
            "modelName1p": fp_by_3p.get(model.lower()),
            "imageSetId": _int(r["IMAGE_SET_ID"]) or None,
            "rarityId": _int(r["RARITY"]) or None,
            "bulk": _int(r["BULK"]) or None,
            "maxStackSize": _int(r["MAX_STACK_SIZE"]),
            "reticleId": _int(r["USE_ITEM_RETICLE_ID"]) or None,
            "activatableAbilityId": _int(r["ACTIVATABLE_ABILITY_ID"]) or None,
            # AbilityEx.DISTANCE_MAX on the item's activatable ability: the reach the
            # client will let the player act at (2 m melee, 10 m hammer, 50 m demolition).
            "useDistanceMetres": _num(ability["DISTANCE_MAX"]) if ability else None,
            "interactionAnimationId": _int(r["INTERACTION_ANIMATION_ID"]) or None,
            "contentId": _int(r["CONTENT_ID"]) or None,
            "noTrade": _flag(r["NO_TRADE"]),
            "singleUse": _flag(r["SINGLE_USE"]),
            "canEquip": _flag(r["FLAG_CAN_EQUIP"]),
            "quickUse": _flag(r["FLAG_QUICK_USE"]),
            "displayBars": bars.get(iid) or None,
            # Unnamed in the client's own header; kept raw rather than guessed at.
            "clientItemParam2": _int(r["PARAM2"]) or None,
        })

    # -- weapons (keyed on WEAPON_ID) --------------------------------------------------
    by_weapon: dict[str, list[dict[str, Any]]] = {}
    for wi in weapon_items:
        if wi["weaponId"]:
            by_weapon.setdefault(str(wi["weaponId"]), []).append(wi)

    weapons: list[dict[str, Any]] = []
    timing_conflicts: list[str] = []
    unresolved_ammo: set[str] = set()

    for wid in sorted(by_weapon, key=lambda s: int(s)):
        variants = sorted(by_weapon[wid], key=lambda w: w["itemId"])
        sheets = [datasheet_data[str(v["itemId"])] for v in variants
                  if str(v["itemId"]) in datasheet_data]

        timing = ammo_text = fire_group = range_string = template = None
        datasheet_damage = None
        if sheets:
            distinct = {(s["REFIRE_TIME_MS"], s["RELOAD_TIME_MS"], s["CLIP_SIZE"],
                         s["FIRE_GROUP_ID"]) for s in sheets}
            if len(distinct) > 1:
                timing_conflicts.append(
                    f"weapon {wid}: {len(distinct)} different timing rows across its items "
                    f"({sorted(distinct)}) - the lowest item id wins"
                )
            s = sheets[0]
            refire = _int(s["REFIRE_TIME_MS"])
            timing = {
                "refireMs": refire,
                "reloadMs": _int(s["RELOAD_TIME_MS"]),
                "clipSize": _int(s["CLIP_SIZE"]),
                # derived, not a client value: 60000 / refire, 1 dp
                "roundsPerMinute": round(60000 / refire, 1) if refire else None,
            }
            fire_group = _int(s["FIRE_GROUP_ID"]) or None
            range_string = _int(s["RANGE_STRING_ID"]) or None
            # Emitted verbatim even though they are 0 on 159 of the 160 rows: dropping them
            # hid the one non-zero gameplay number this sheet carries (item 100 /
            # weapon 1383, DAMAGE_FALLOFF 3500). A consumer can see the zeros for what they
            # are - see serverSideGaps[weapon-damage].
            datasheet_damage = {
                "directDamage": _num(s["DIRECT_DAMAGE"]),
                "indirectDamage": _num(s["INDIRECT_DAMAGE"]),
                "damageFalloff": _num(s["DAMAGE_FALLOFF"]),
                "minConeOfFire": _num(s["MIN_CONE_OF_FIRE"]),
            }
            tpl = client_datasheets.get(s["DATASHEET_ID"])
            if tpl is not None:
                template = {
                    "clientDatasheetId": _int(tpl["ID"]),
                    "typeName": tpl["TYPE_NAME"],
                    # PARAM1..12 - the client's own header gives them no names.
                    "params": [_num(tpl[f"PARAM{i}"]) for i in range(1, 13)],
                }

        for v in variants:
            m = AMMO_TYPE_RE.search(v.get("description") or "")
            if m:
                ammo_text = m.group(1).strip()
                break

        ammo = None
        if ammo_text:
            key = ammo_text.lower()
            pen_id, basis = AMMO_TO_PEN_TYPE.get(key, (None, None))
            if pen_id is None:
                unresolved_ammo.add(ammo_text)
            ammo = {
                "ammoTypeText": ammo_text,
                "ammoItemId": _int(ammo_by_name.get(key), None),
                "penTypeId": pen_id,
                "penTypeName": (pen_types.get(str(pen_id)) or {}).get("DESCRIPTION"),
                "basis": basis or "no calibre match in AMMO_TO_PEN_TYPE",
            }

        named = next((v for v in variants if v["name"]), None)
        with_bars = next((v for v in variants if v["displayBars"]), None)
        cls_ids = sorted({v["itemClassId"] for v in variants if v["itemClassId"]})
        weapons.append({
            "weaponId": int(wid),
            "displayName": named["name"] if named else None,
            "category": next((v["category"] for v in variants if v["category"]), None),
            "itemClassIds": cls_ids,
            "holsterSlot": next((v["holsterSlot"] for v in variants if v["holsterSlot"]), None),
            "fireGroupId": fire_group,
            "fireEffectsByTag": effects.get(str(fire_group)) if fire_group else None,
            "timing": timing,
            "datasheetDamage": datasheet_damage,
            "ammo": ammo,
            "useDistanceMetres": next(
                (v["useDistanceMetres"] for v in variants if v["useDistanceMetres"]), None),
            "reticleId": next((v["reticleId"] for v in variants if v["reticleId"]), None),
            "displayBars": with_bars["displayBars"] if with_bars else None,
            "rangeStringId": range_string,
            "clientDisplayTemplate": template,
            "itemIds": [v["itemId"] for v in variants],
            "itemCount": len(variants),
            "itemsWithDatasheetRow": len(sheets),
        })

    # -- counts ------------------------------------------------------------------------
    counts = {
        "weapons": len(weapons),
        "weaponItems": len(weapon_items),
        "weaponItemsWithDatasheetRow": sum(1 for w in weapon_items if w["hasDatasheetRow"]),
        "weaponItemsWithoutDatasheetRow": sum(1 for w in weapon_items if not w["hasDatasheetRow"]),
        "weaponsWithTiming": sum(1 for w in weapons if w["timing"]),
        "weaponsWithoutTiming": sum(1 for w in weapons if not w["timing"]),
        "weaponsWithFireGroup": sum(1 for w in weapons if w["fireGroupId"]),
        "weaponsWithFireEffects": sum(1 for w in weapons if w["fireEffectsByTag"]),
        "weaponsWithAmmoLink": sum(1 for w in weapons if w["ammo"] and w["ammo"]["ammoItemId"]),
        "weaponsWithDisplayBars": sum(1 for w in weapons if w["displayBars"]),
        "fireGroups": len(fire_groups),
        "penetrationTypes": len(penetration),
        "penetrationMaterialRows": sum(p["materialCount"] for p in penetration),
        "projectiles": len(projectiles),
        "damageTypes": len(enums["damageTypes"]),
        "damageTypesLive": sum(1 for t in enums["damageTypes"] if not t["retired"]),
        "resistProfiles": len(resist_profiles),
        "armourProfiles": len(armour_profiles),
        "vehicleDamageLevels": len(damage_levels),
        "ammoItems": len(ammo_items),
        "materialTypes": len(enums["materialTypes"]),
        "param1WeaponIdAgreement": f"{param1_agree}/{param1_agree + param1_disagree}",
        "nonZeroDisplayStatRows": sorted(_int(g) for g in non_zero_display),
        "clientItemDatasheetDataRows": datasheet_damage_rows,
        "nonZeroDatasheetDamageCells": non_zero_datasheet_damage,
    }

    # -- the binary's schema, and the gaps ---------------------------------------------
    schema_runs, exe_warnings = read_schema_runs(exe)
    warnings.extend(exe_warnings)
    runs_by_table = {r["table"]: r for r in schema_runs}

    def gap(gid: str, title: str, why: str, evidence: str, owner: str,
            table: str | None = None, fields: Iterable[str] | None = None) -> dict[str, Any]:
        run = runs_by_table.get(table or "")
        return {
            "id": gid,
            "title": title,
            "why": why,
            "evidence": evidence,
            "cranberryOwner": owner,
            "referenceDataTable": table,
            "referenceDataOffset": run["fileOffsetStart"] if run else None,
            "missingFields": list(fields) if fields else (run["columns"] if run else None),
        }

    gaps = [
        gap("weapon-damage",
            "Per-shot damage, falloff and armour penetration",
            "The damage columns the client ships are zero on all but one cell in the whole "
            "build, so effectively no weapon here has a damage number the server could read "
            "back. The real values live in the server-authored FireModes table that reaches "
            "the client as ReferenceData.",
            "ClientItemDatasheetData: DIRECT_DAMAGE is "
            f"{_zero_everywhere('DIRECT_DAMAGE')}, INDIRECT_DAMAGE is "
            f"{_zero_everywhere('INDIRECT_DAMAGE')}, DAMAGE_FALLOFF is "
            f"{_zero_everywhere('DAMAGE_FALLOFF')}"
            + " (see counts.nonZeroDatasheetDamageCells; the values are emitted verbatim on "
              "weapons[].datasheetDamage); FireModeDisplayStats is 0 in all 176 rows except "
              f"groups {sorted(_int(g) for g in non_zero_display)} which carry the "
              "placeholder 3500/500; ProjectileDisplayStats is 0 in all 82 rows.",
            "Cranberry combat system: author a damage model keyed on weaponId. The client's "
            "0-10 `displayBars` (damage / range / fireRate / recoil / capacity) are the "
            "designers' own relative ranking of these same weapons and are the one in-client "
            "anchor for calibrating it - they are ordinal, not engineering units.",
            fields=["ClientItemDatasheetData.DIRECT_DAMAGE",
                    "ClientItemDatasheetData.INDIRECT_DAMAGE",
                    "ClientItemDatasheetData.DAMAGE_FALLOFF",
                    "FireModeDisplayStats.MAX_DAMAGE", "FireModeDisplayStats.MAX_DAMAGE_RANGE",
                    "FireModeDisplayStats.MIN_DAMAGE", "FireModeDisplayStats.MIN_DAMAGE_RANGE",
                    "FireModeDisplayStats.ARMOR_PENETRATION",
                    "FireModeDisplayStats.SHIELD_BYPASS_PCT",
                    "ProjectileDisplayStats.MAX_DAMAGE_IND",
                    "ProjectileDisplayStats.MAX_DAMAGE_IND_RADIUS",
                    "ProjectileDisplayStats.MIN_DAMAGE_IND",
                    "ProjectileDisplayStats.MIN_DAMAGE_IND_RADIUS",
                    "ArmorInfo.ARMOR_AMOUNT"]),
        gap("fire-mode-table",
            "The FireModes table itself",
            "No FireModes asset exists in the 50,502-entry pack index under any extension, and "
            "no FireModes filename appears in the binary's datasheet-name pool. The table is "
            "sent to the client as part of the WeaponDefinitions ReferenceData payload.",
            "Client ReferenceData handler list at file offset 0x3117568 "
            "(ItemClasses / DynamicAppearanceDefinitions / ItemCategories / ProfileDefinitions / "
            "ProjectileDefinitions / WeaponDefinitions) followed by "
            "'Received ReferenceData type=%s, but no handler!'; the column names survive in the "
            "schema string pool at file offset 0x35E79D8.",
            "Cranberry must author and serialise this table. `missingFields` is the exact "
            "column list the August client's schema expects.",
            table="FireModes"),
        gap("cone-of-fire",
            "Cone of fire and recoil",
            "The only cone-of-fire column in a shipped sheet, "
            f"ClientItemDatasheetData.MIN_CONE_OF_FIRE, is "
            f"{_zero_everywhere('MIN_CONE_OF_FIRE')}, and there is no recoil sheet at all. "
            "Both belong to the server-side fire-mode/state tables.",
            f"MIN_CONE_OF_FIRE is {_zero_everywhere('MIN_CONE_OF_FIRE')}; the cone/cylinder "
            "schema names are at file "
            "offset 0x35E9398 and the recoil names inside the FireModes run at 0x35E7E40.",
            "Cranberry combat system: author bloom and recoil curves per weapon. Shot "
            "validation on the server needs at least the max cone to bound a legitimate ray.",
            table="ConeOfFire"),
        gap("projectile-ballistics",
            "Projectile speed, gravity, drag and lifespan",
            "ProjectileToPenTypes gives the projectile ids and their calibre, but the flight "
            "model itself is in the server-authored ProjectileDefinitions ReferenceData table. "
            "The 14 projectile ids in the client (19000-19002, 70038-70075) resolve to no "
            "shipped sheet other than the all-zero ProjectileDisplayStats.",
            "ProjectileDefinitions is a named ReferenceData handler at file offset 0x3117568; "
            "its column names are at file offset 0x35E73A8. Projectiles.txt exists as a string "
            "at 0x316BBF0 but no such asset is in the pack index.",
            "Cranberry combat system: author muzzle velocity, gravity and drag per projectile "
            "so the server can reproduce the client's predicted trajectory for hit validation.",
            table="ProjectileDefinitions"),
        gap("weapon-fire-group-table",
            "The fire-group and weapon-definition tables",
            "FIRE_GROUP_ID is referenced by 142 of the 160 datasheet rows, but the table it "
            "points at (primary/secondary fire mode, chamber time, iron-sight transitions, "
            "equip/unequip times, ammo slots) is not shipped. WeaponMounts.txt is present but "
            "header-only, 0 rows.",
            "No FireGroups or Weapons asset of any kind in the pack index; the column names are "
            "at file offset 0x35E0298 and arrive as the WeaponDefinitions ReferenceData payload.",
            "Cranberry must author it. Note the shipped REFIRE_TIME_MS / RELOAD_TIME_MS / "
            "CLIP_SIZE in this dataset are real and should be carried straight into it.",
            table="WeaponDefinitions"),
        gap("ammo-per-fire-mode",
            "Which ammunition each fire mode consumes, and how much",
            "The only client-carried weapon->ammunition link is the '[Ammo Type: ...]' tail of "
            "the English item description, which covers the 8 cartridge calibres and no bow, "
            "crossbow or thrown weapon. AMMO_ITEM_ID / AMMO_SLOT / AMMO_PER_SHOT / BURST_COUNT / "
            "PELLETS_PER_SHOT are fire-mode columns and are server-side.",
            "Item descriptions in locale en_us (e.g. item 2 'M1911A1' -> '.45 Round'); the "
            "AMMO_* column names are in the FireModes run at file offset 0x35E7BC0. Bow and "
            "crossbow descriptions say only 'varying types of arrows'.",
            "Cranberry must define the ammo set per fire mode, including which of the five "
            "arrow items (112, 138, 1434, 3373, 3374) each bow accepts and the pellet count "
            "for the 12GA fire modes.",
            table="FireModes",
            fields=["AMMO_ITEM_ID", "AMMO_SLOT", "AMMO_PER_SHOT", "BURST_COUNT",
                    "PELLETS_PER_SHOT", "PELLET_SPREAD", "PELLET_PATTERN_GROUP_ID"]),
        gap("pen-type-to-ammo",
            "The pen-type <-> ammunition correspondence",
            "PenTypes names ten calibres and PenTypeMaterials gives their full per-material "
            "ricochet/depth behaviour, but nothing in the client joins a pen type to an "
            "ammunition item or a weapon. This dataset makes that link by matching the calibre "
            "name and records the basis on every entry; the two arrow/spear pen types (1, 2) "
            "are left unlinked.",
            "PenTypes.txt (10 rows) and ProjectileToPenTypes.txt (14 rows) share no column with "
            "any item, weapon or fire-group table.",
            "Cranberry should make the link explicit in its own weapon definitions rather than "
            "rely on this tool's name match.",
            fields=["PenTypes.ID -> ammunition item id"]),
        gap("player-armour-mitigation",
            "Player armour and helmet mitigation",
            "ResistInfo (1,290 profiles) and ArmorInfo (19 profiles) exist, but ArmorInfo's "
            "ARMOR_AMOUNT is 0 in all 19 rows and VehicleResistMappings is empty (0 rows) in "
            "this build, so nothing in the client binds a mitigation profile to a target. There "
            "is no player-armour mapping sheet at all.",
            "ArmorInfo.ARMOR_AMOUNT = 0 in all 19 rows; VehicleResistMappings.txt has 0 data "
            "rows; VehicleArmorMappings.txt has 6, all for vehicle 6.",
            "Cranberry must model player armour/helmet mitigation itself, driven off "
            "ClientItemDefinitions.IS_ARMOR and the equipment slots, and choose which "
            "ResistInfo profile (if any) applies to which target."),
        gap("range-string-id",
            "The meaning of RANGE_STRING_ID",
            "RANGE_STRING_ID takes only the values 673 and 674 and is not a Global.Text key - "
            "resolving 673/674 that way yields unrelated backpack strings. It is a UI range "
            "label from some other string table, not a distance.",
            "ClientItemDatasheetData.RANGE_STRING_ID distinct values {0, 673, 674}; the same "
            "column name appears in the server-side Weapons schema run at file offset 0x35E06F0.",
            "Not needed by the server. Carried raw in this dataset as `rangeStringId` so it is "
            "not mistaken for a range in metres."),
        gap("client-display-template-params",
            "The ClientDatasheets 'Firearms' template parameters",
            "ClientItemDatasheetData.DATASHEET_ID points at a ClientDatasheets row whose own "
            "header names its columns only PARAM1..PARAM12, so their semantics are not carried "
            "by the client. The column is also near-constant (0, 2 or 11 across all 160 rows), "
            "so the row is a shared UI template, not per-weapon data.",
            "ClientDatasheets.txt header '#TYPE_NAME^*ID^PARAM1^...^PARAM12^'; row 2 is "
            "'Firearms^2^75^225^0^7^60^135^1250^3500^1^1^1^0'.",
            "Do not use for authority. Kept raw under `clientDisplayTemplate.params`."),
        gap("armour-facing-labels",
            "What ArmorInfo.FACING 0-6 means",
            "FACING is a hit-side code with seven values and the client carries no label table "
            "for it.",
            "ArmorInfo.txt FACING distinct values 0..6; no FACING string in the datasheet-name "
            "pool.",
            "Cranberry defines its own hit-side enum and maps it onto these codes if it uses "
            "ArmorInfo at all."),
        gap("charge-and-cook-times",
            "Bow draw time and grenade cook time",
            "REFIRE_TIME_MS is the whole cadence story only for a weapon that fires the instant "
            "the trigger is pulled. A bow's draw and a grenade's cook are separate fire-mode "
            "columns and are server-side, which is why `timing.roundsPerMinute` reads 800 rpm "
            "for the Makeshift Bow (75 ms refire) - that is a floor on the cadence, not a rate.",
            "The three bows and the crossbow carry REFIRE_TIME_MS 75-500 with no charge column "
            "in any shipped sheet; CHARGE_UP_TIME_MS and COOK_TIME_MS are FireModes columns at "
            "file offsets 0x35E7CA0 and 0x35E7CB8.",
            "Cranberry must author draw and cook times before rate-of-fire validation can be "
            "applied to bows, crossbows and throwables.",
            table="FireModes",
            fields=["CHARGE_UP_TIME_MS", "COOK_TIME_MS", "FIRE_DELAY_MS", "FIRE_DURATION_MS",
                    "AUTO_FIRE_TIME_MS", "RELOAD_CHAMBER_TIME_MS", "RELOAD_LOOP_START_TIME_MS",
                    "RELOAD_LOOP_END_TIME_MS"]),
        gap("weapon-spawn-odds",
            "Weapon spawn odds and loot placement",
            "This dataset is the weapon catalogue only. Nothing in it says how often a weapon "
            "spawns or where; no client sheet carries loot weights for weapons.",
            "Out of scope for every sheet read here; the loot model is D20's subject.",
            "Cranberry loot system (D20), not the weapons dataset."),
    ]

    # A run-time gap: anything this tool could not resolve gets said out loud.
    if unresolved_ammo:
        gaps.append(gap(
            "unmatched-ammo-text",
            "Ammo-type text with no calibre match",
            "These '[Ammo Type: ...]' strings did not match any entry in this tool's "
            "AMMO_TO_PEN_TYPE table, so their weapons carry no pen type.",
            f"unmatched: {sorted(unresolved_ammo)}",
            "Extend AMMO_TO_PEN_TYPE, or resolve it in Cranberry's own weapon definitions."))
    if unresolved_resist_types:
        warnings.append(
            "ResistInfo rows reference "
            f"{len(unresolved_resist_types)} RESIST_TYPE ids absent from ResistTypes "
            f"({sorted(_int(x) for x in unresolved_resist_types)}) - dead PlanetSide-era rows")
    warnings.extend(timing_conflicts)

    # -- provenance --------------------------------------------------------------------
    provenance = {
        "enums.penetrationTypes[]": "PenTypes.txt ID, DESCRIPTION",
        "enums.materialTypes[]": "MaterialTypes.txt ID, NAME",
        "enums.damageTypes[]": "ResistTypes.txt ID, DESCRIPTION, SUPPRESS_*_HIT_FEEDBACK; "
                               "`retired` derived from the client's own 'zzz' name prefix",
        "enums.reticles[]": "Reticles.txt ID, RETICLE_NAME",
        "enums.holsterSlots[]": "EquipmentSlotDefinitions.txt ID, SLOT_NAME, "
                                "BONE_ATTACH_NAME_OVERRIDE, filtered to the slots named by "
                                "EquipSlotItemClasses.EQUIP_SLOT_ID",
        "enums.itemCategories[]": "Datasheets.txt ID, TYPE_NAME",
        "enums.itemClasses[]": "ItemClasses.txt ID, NAME_ID (-> locale), WIELD_TYPE, "
                               "SECONDARY_WIELD_TYPE, restricted to the classes weapons use "
                               "or that a weapon holster accepts; `inClientSheet` is false for "
                               "a class id referenced by an item but absent from ItemClasses.txt",
        "enums.fireEffectTags[]": "FireModeEffectGroups.txt TAG (distinct)",
        "enums.muzzleAttachPoints[]": "ActorProjectileDefinitions.xml EffectDefinition "
                                      "id/name/boneName/targetBoneName",
        "penetration[].penTypeId,.name": "PenTypes.txt ID, DESCRIPTION",
        "penetration[].materials[].materialTypeId,.material":
            "PenTypeMaterials.MATERIAL_TYPE_ID joined to MaterialTypes.ID, NAME",
        "penetration[].materials[].ricochets": "PenTypeMaterials.BOUNCE (1 = ricochet)",
        "penetration[].materials[].maxDepthMetres": "PenTypeMaterials.MAX_DEPTH",
        "penetration[].materials[].entryPointEffectId": "PenTypeMaterials.ENTRY_POINT_EFFECT_ID",
        "penetration[].materials[].passThroughNoEffect": "PenTypeMaterials.PGT",
        "projectiles[].projectileId,.penTypeId": "ProjectileToPenTypes.txt PROJECTILE_ID, PEN_TYPE_ID",
        "projectiles[].displayStats": "ProjectileDisplayStats.txt (all zero in this build - UI only)",
        "mitigation.resistProfiles[]": "ResistInfo.txt ID, RESIST_TYPE, RESIST_PERCENT, "
                                       "RESIST_AMOUNT; damageType joined via ResistTypes.ID",
        "mitigation.armourProfiles[]": "ArmorInfo.txt ID, FACING, ARMOR_PERCENT, ARMOR_AMOUNT",
        "mitigation.vehicleArmour[]": "VehicleArmorMappings.txt VEHICLE_ID, ARMOR_INFO_ID",
        "mitigation.vehicleDamageLevels[]": "DamageLevelInfo.txt + DamageLevelMappings.txt NPC_ID",
        "mitigation.damageTables[]": "DamageTables.txt",
        "equipSlotItemClasses[]": "EquipSlotItemClasses.txt EQUIP_SLOT_ID, ITEM_CLASS",
        "fireGroups[].effectsByTag": "FireModeEffectGroups.txt GROUP_ID, TAG, EFFECT_ID",
        "fireGroups[].displayStats": "FireModeDisplayStats.txt (zero in this build - UI only)",
        "weapons[].weaponId": "ClientItemDatasheetData.WEAPON_ID, equal to "
                              "ClientItemDefinitions.PARAM1 for all 160 rows carrying both",
        "weapons[].displayName": "ClientItemDefinitions.NAME_ID -> locale "
                                 "Global.Text.<id> (jenkins lookup2), lowest item id wins",
        "weapons[].category": "ClientItemDefinitions.DATASHEET_ID -> Datasheets.TYPE_NAME",
        "weapons[].itemClassIds": "ClientItemDefinitions.ITEM_CLASS over the weapon's items",
        "weapons[].holsterSlot": "ClientItemDefinitions.PASSIVE_EQUIP_SLOT_ID -> "
                                 "EquipmentSlotDefinitions",
        "weapons[].fireGroupId": "ClientItemDatasheetData.FIRE_GROUP_ID",
        "weapons[].fireEffectsByTag": "FireModeEffectGroups on that GROUP_ID",
        "weapons[].timing.refireMs": "ClientItemDatasheetData.REFIRE_TIME_MS (authoritative)",
        "weapons[].timing.reloadMs": "ClientItemDatasheetData.RELOAD_TIME_MS (authoritative)",
        "weapons[].timing.clipSize": "ClientItemDatasheetData.CLIP_SIZE (authoritative)",
        "weapons[].datasheetDamage": "ClientItemDatasheetData.DIRECT_DAMAGE / "
                                     ".INDIRECT_DAMAGE / .DAMAGE_FALLOFF / .MIN_CONE_OF_FIRE "
                                     "verbatim - zero on all but one cell in the build; see "
                                     "counts.nonZeroDatasheetDamageCells and "
                                     "serverSideGaps[weapon-damage]",
        "weapons[].timing.roundsPerMinute":
            "derived: 60000 / refireMs, 1 dp. A floor on the cadence, not a rate, for any "
            "weapon whose shot is gated by a server-side charge or cook time (bows, "
            "crossbows, throwables) - see the charge-and-cook-times gap",
        "weapons[].ammo.ammoTypeText": "'[Ammo Type: X]' tail of the item's locale description",
        "weapons[].ammo.ammoItemId": "that text matched to a ClientItemDefinitions item name",
        "weapons[].ammo.penTypeId": "derived by this tool's AMMO_TO_PEN_TYPE calibre table; "
                                    "see `basis` on each entry",
        "weapons[].useDistanceMetres": "AbilityEx.DISTANCE_MAX of the item's "
                                       "ACTIVATABLE_ABILITY_ID",
        "weapons[].reticleId": "ClientItemDefinitions.USE_ITEM_RETICLE_ID -> Reticles.ID",
        "weapons[].displayBars": "ItemDatasheetPropertyMap -> DatasheetProperties.VALUE / "
                                 "VALUE_MAX, labelled by NAME_ID -> locale. ORDINAL 0-10 UI "
                                 "bars, NOT engineering units",
        "weapons[].rangeStringId": "ClientItemDatasheetData.RANGE_STRING_ID (unresolved, see gaps)",
        "weapons[].clientDisplayTemplate": "ClientItemDatasheetData.DATASHEET_ID -> "
                                           "ClientDatasheets.txt row (PARAM1..12 unnamed)",
        "weaponItems[].itemId": "ClientItemDefinitions.ID where CODE_FACTORY_NAME = 'Weapon'",
        "weaponItems[].weaponId": "ClientItemDefinitions.PARAM1 (fact F2)",
        "weaponItems[].name,.description": "locale en_us via item-names-en_us.json, markup stripped",
        "weaponItems[].modelName3p": "ClientItemDefinitions.MODEL_NAME",
        "weaponItems[].modelName1p": "FirstPersonAttachments.ATTACHMENT_MESH_3P -> "
                                     "ATTACHMENT_MESH_1P, matched case-insensitively",
        "weaponItems[].bulk,.maxStackSize,.rarityId,.noTrade,.singleUse,.canEquip,.quickUse":
            "ClientItemDefinitions BULK, MAX_STACK_SIZE, RARITY, NO_TRADE, SINGLE_USE, "
            "FLAG_CAN_EQUIP, FLAG_QUICK_USE",
        "weaponItems[].clientItemParam2": "ClientItemDefinitions.PARAM2, unnamed in the client",
        "ammoItems[]": "ClientItemDefinitions rows whose CODE_FACTORY_NAME is 'Ammo' or whose "
                       "name is one of the eight calibres named in a weapon description",
        "serverSideGaps[].missingFields": "column-name runs read back out of H1Z1.exe's schema "
                                          "string pool at the cited file offsets",
    }

    doc = {
        "schema": SCHEMA_NAME,
        "schemaVersion": SCHEMA_VERSION,
        "clientBuild": CLIENT_BUILD,
        "generated": {
            "tool": "tools/data/derive_weapons.py",
            "utc": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "dataDir": str(data_dir),
            "exe": str(exe) if exe else None,
        },
        "summary": (
            "Weapons data for the Cranberry combat system, joined out of the August client's "
            "own datasheets. The client ships the weapon identity graph, the reload/refire/clip "
            "timings, the complete penetration and ricochet table, the damage-type enum and the "
            "UI stat bars. It does NOT ship usable damage, falloff, cone of fire, recoil or "
            "projectile ballistics: those columns exist but are zero on every row bar "
            f"{len(non_zero_datasheet_damage)} cell(s) (counts.nonZeroDatasheetDamageCells), "
            "and the live values reach the client from the server as ReferenceData and are "
            "Cranberry's to author. See serverSideGaps."
        ),
        "counts": counts,
        # every input actually opened, with its row count (or byte/entry count for the
        # JSON and XML inputs), so a reader can tell an empty sheet from an absent one
        "sources": dict(sorted(d.read.items())),
        "fieldProvenance": provenance,
        "enums": enums,
        "penetration": penetration,
        "projectiles": projectiles,
        "mitigation": {
            "resistProfiles": resist_profiles,
            "armourProfiles": armour_profiles,
            "vehicleArmour": vehicle_armour,
            "vehicleDamageLevels": damage_levels,
            "damageTables": damage_tables,
        },
        "equipSlotItemClasses": equip_slot_item_classes,
        "fireGroups": fire_groups,
        "weapons": weapons,
        "weaponItems": weapon_items,
        "ammoItems": ammo_items,
        "referenceDataSchema": {
            "note": (
                "Column names of the server-authored tables the August client expects as "
                "ReferenceData. Read out of H1Z1.exe's datasheet schema string pool; the "
                "handler names are at file offset 0x3117568. These are the fields Cranberry "
                "has to fill in - they are evidence of what is missing, not data."
            ),
            "handlers": REFERENCE_DATA_HANDLERS,
            "handlerListOffset": f"0x{REFERENCE_DATA_HANDLER_OFFSET:07x}",
            "runs": schema_runs,
        },
        "serverSideGaps": gaps,
        "warnings": warnings,
        "missingInputs": sorted(set(d.missing)),
    }
    return doc


# ---------------------------------------------------------------------------------------
# cli
# ---------------------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Build Cranberry's server-ready weapons dataset from the August client's "
                    "extracted datasheets.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--data-dir", type=Path, default=DEFAULT_DATA_DIR,
                    help=f"directory holding the extracted sheets (default {DEFAULT_DATA_DIR})")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT,
                    help=f"output JSON path (default {DEFAULT_OUT})")
    ap.add_argument("--exe", type=Path, default=DEFAULT_EXE,
                    help="client binary, read only to re-verify the cited schema offsets")
    ap.add_argument("--no-exe", action="store_true",
                    help="do not open the binary; omit the ReferenceData column lists")
    ap.add_argument("--indent", type=int, default=1, help="JSON indent (0 = compact)")
    ap.add_argument("--summary", action="store_true",
                    help="print counts, warnings and gap titles; write nothing")
    args = ap.parse_args(argv)

    if not args.data_dir.is_dir():
        sys.exit(f"no such data directory: {args.data_dir}")

    doc = build(args.data_dir, None if args.no_exe else args.exe)
    doc = _prune(doc)

    if args.summary:
        print(f"{SCHEMA_NAME} v{SCHEMA_VERSION}  build {CLIENT_BUILD}")
        for k, v in doc["counts"].items():
            print(f"  {k:<32} {v}")
        for w in doc.get("warnings", []):
            print(f"  ! {w}")
        print("  serverSideGaps:")
        for g in doc["serverSideGaps"]:
            print(f"    - {g['id']}: {g['title']}")
        return 0

    args.out.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(doc, ensure_ascii=False, indent=args.indent or None)
    args.out.write_text(text + "\n", encoding="utf-8", newline="\n")
    print(f"{args.out}  {len(text) + 1:,} bytes", file=sys.stderr)
    for k, v in doc["counts"].items():
        print(f"  {k:<32} {v}", file=sys.stderr)
    for w in doc.get("warnings", []):
        print(f"  ! {w}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
