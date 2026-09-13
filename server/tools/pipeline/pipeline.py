#!/usr/bin/env python3
r"""
pipeline.py - the one build-time data pipeline: extract, derive, generate, check, manifest.

WHY THIS EXISTS (S8 §0.1, §7; docs/96)

  Before this file the server's data came out of two disconnected chains. Five ``derive_*.py``
  documents produced 4.4 MB of provenance-annotated JSON that **nothing read**, while nine other
  generators re-joined the raw datasheets with six private copies of the same ``^`` splitter and
  wrote 14 ``.g.cs`` files and 5 ``Data\*`` files. No output recorded what it was made from, so a
  ``.g.cs`` could sit two client-extractions out of date and nothing would say so.

  This file is the single entry point. It knows every generator, every input those generators
  read, and the grade of each input - whether the value came out of the August client, out of an
  owner ruling, out of the owner's own Z1 tree (D53), or out of a capture of somebody else's
  server. It stamps that into the head of every generated ``.g.cs`` and into
  ``tools/pipeline/manifest.json``, and ``check`` fails the build when an input or a generator has
  moved since the file was written.

  "Retail by construction" then stops being a claim a document makes and becomes a property the
  build checks.

SUBCOMMANDS

  extract    re-extract the client assets ``inputs.json`` names, CRC-verified, via
             tools/pack/packread.py. Writes only into the extraction directories.
  derive     run the five tools/data/derive_*.py joins. ``--stage DIR`` writes elsewhere and
             diffs instead of overwriting.
  generate   run every registered generator into a staging directory, compare with the checked-in
             output, and install it with a provenance header. **A generator whose body differs
             from the checked-in file is reported and NOT installed** unless --write-changed is
             given: concurrent lanes build against this tree.
  check      recompute every generator's and every input's hash and compare with the provenance
             header of each generated file and with manifest.json. Non-zero exit lists the stale
             files. This is what the MSBuild ``CranberryDataCheck=true`` target runs.
  manifest   rewrite tools/pipeline/inputs.json (every input asset, hashed and graded),
             tools/pipeline/manifest.json (every generated file) and docs/DATA-MANIFEST.md.

GRADES (the same vocabulary in inputs.json, manifest.json and every header)

  CLIENT              extracted from the August client's own packs / binary. Retail by construction.
  CLIENT+RULING       client data selected or shaped by an owner decision; the Dxx is named.
  RULING              an owner decision with no client source; the Dxx is named.
  Z1                  a value from the owner's own Z1 tree, adopted under D53.
  CAPTURED            bytes recorded off a wire or off a third-party server.
  THIRD_PARTY_SHAPED  the shape (not the file) follows a third-party artefact. Flagged, never silent.

Usage:
    python tools/pipeline/pipeline.py generate
    python tools/pipeline/pipeline.py check
    python tools/pipeline/pipeline.py manifest
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zlib
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

PIPELINE_DIR = Path(__file__).resolve().parent
REPO = PIPELINE_DIR.parents[1]                       # C:\Aug2017\Server
AUG = Path(os.environ["CRANBERRY_DATA_ROOT"]).resolve() if os.environ.get("CRANBERRY_DATA_ROOT") else next(
    (parent for parent in REPO.parents if (parent / "out" / "data_aug").is_dir()), REPO.parent)
OUT = AUG / "out"
DATA_AUG = OUT / "data_aug"
DERIVED = DATA_AUG / "derived"
WORLD = OUT / "world_aug"
#: the 136 datasheets + 15 XML files the earlier extraction list missed (S8 section 4.1)
UNEXTRACTED = OUT / "overhaul-20260901" / "unextracted"
#: family dispatcher decompiles - gen-zone-opcodes.py reads the sub-opcode WIDTH out of these
GHIDRA = OUT / "ghidra-aug"

CLIENT_BUILD = "0.0.118.208059"

#: The six family-dispatcher decompiles gen-zone-opcodes.py reads the sub-opcode WIDTH out of
#: (overhaul plan lane 2B, D181). Each was dumped by an earlier lane; none is dumped here.
DISPATCHER_DUMPS: list[Path] = [
    GHIDRA / "doors-prompt-string" / "callers_14140c480" / "FUN_14129ad10_14129ad10.c",
    GHIDRA / "character-dispatch" / "_140af9ca0" / "FUN_140af9ca0_140af9ca0.c",
    GHIDRA / "loot-research" / "clientupdate-dispatch" / "_140afc660" / "FUN_140afc660_140afc660.c",
    GHIDRA / "fable-character-login" / "_140b81830_1420cd050" / "FUN_140b81830_140b81830.c",
    OUT / "inventory-research" / "loadout-packet" / "_140b03170" / "FUN_140d37650_140d37650.c",
    GHIDRA / "matchflow-b5"
    / "_140bba510_14136ddf0_140c9e170_140a2d040_140a3ca40_140f25600"
    / "FUN_140bba510_140bba510.c",
]

INPUTS_JSON = PIPELINE_DIR / "inputs.json"
MANIFEST_JSON = PIPELINE_DIR / "manifest.json"
DOCS_MANIFEST = REPO / "docs" / "DATA-MANIFEST.md"

HEADER_BEGIN = "// <cranberry-provenance>"
HEADER_END = "// </cranberry-provenance>"

GRADES = {
    "CLIENT": "extracted from the August client's own packs or binary - retail by construction",
    "CLIENT+RULING": "client data selected or shaped by an owner decision (the Dxx is named)",
    "RULING": "an owner decision; the client carries no such value (the Dxx is named)",
    "Z1": "a value from the owner's own Z1 tree, adopted under D53",
    "CAPTURED": "bytes recorded off a wire or off a third-party server",
    "THIRD_PARTY_SHAPED": "the shape follows a third-party artefact - flagged, never silent",
}


# ======================================================================================
# paths
# ======================================================================================


def rel(path: Path) -> str:
    """A stable, printable name for a path: repo- or C:\\Aug2017-relative where possible."""
    path = Path(path)
    for root, prefix in ((REPO, ""), (AUG, "")):
        try:
            r = path.resolve().relative_to(root.resolve())
        except ValueError:
            continue
        return (prefix + r.as_posix()) if root is REPO else r.as_posix()
    return str(path).replace("\\", "/")


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def crc32_of(path: Path) -> str:
    crc = 0
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            crc = zlib.crc32(chunk, crc)
    return f"{crc & 0xFFFFFFFF:08x}"


def now_utc() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ======================================================================================
# the inputs the build depends on
# ======================================================================================

#: (directory, recursive, grade, third-party?, source note, is-a-pack-asset?)
INPUT_TREES: list[tuple[Path, bool, str, bool, str, bool]] = [
    (DATA_AUG, False, "CLIENT", False,
     "extracted from the August client's Assets_*.pack by tools/pack/packread.py "
     "(list: out/data_aug/client-assets.lst, CRC-verified)", True),
    (DATA_AUG / "adr", True, "CLIENT", False,
     "actor definitions extracted from the client's packs (docs/55)", True),
    (DATA_AUG / "dme", True, "CLIENT", False,
     "meshes extracted from the client's packs (docs/92)", True),
    (DERIVED, False, "CLIENT", False,
     "tools/data/derive_*.py join over the client's own datasheets; carries the "
     "per-field provenance map and the serverSideGaps list", False),
    (OUT / "overhaul-20260901" / "unextracted", False, "CLIENT", False,
     "the 136 datasheets + 15 XML definition files the earlier extraction list missed, "
     "extracted 2026-09-01 by packread (CRC-verified, S8 §4.1)", True),
    (OUT / "lighting-research", False, "CLIENT", False,
     "hand extraction during docs/38; outside client-assets.lst (S8 defect T2). "
     "EnvironmentPresets.cs transcribes these by hand (S8 defect T7)", True),
    (OUT / "appearance-tints", False, "CLIENT", False,
     "hand extraction during docs/54; outside client-assets.lst (S8 defect T2)", True),
    (OUT / "wave7-prep" / "adr", True, "CLIENT", False,
     "the createAsKinematic census of docs/55; outside client-assets.lst (S8 defect T2)", True),
    (OUT / "wave7-prep" / "tex", True, "CLIENT", False,
     "docs/55 texture census; outside client-assets.lst", True),
    (OUT / "wave7-prep" / "mat", True, "CLIENT", False,
     "docs/55 material census; outside client-assets.lst", True),
    (OUT / "vibrancy", False, "CLIENT", False,
     "the client's own option schema, hand-extracted for docs/50; outside client-assets.lst", True),
    (WORLD, False, "CLIENT", False,
     "the client's Z2/LoginZone world files and this repo's tools/zone parses of them", False),
    (REPO / "rulings", False, "RULING", False,
     "the owner's rulings as data (docs/101, lane 2C): one row per non-client number with the "
     "docs/01 D-row that decided it, the Z1 file:line or docs section it came from, and its "
     "grade. Hand-maintained; gen-rulings.py turns them into Generated/Rulings.g.cs", False),
]

#: single files that are not in any of the trees above
INPUT_FILES: list[tuple[Path, str, bool, str, bool]] = [
    (Path(r"C:\h1z1project\reference\h1z1-server\src\servers\ZoneServer2016\entities\destroyable.ts"),
     "THIRD_PARTY_SHAPED", True,
     "D329: getMaxHealth supplies adopted 2000 glass / 5000 wood tuning only; August DTO "
     "wire and map placements were independently derived from the client", False),
    (Path(r"C:\Z1\Data\2016\dataSources\ServerItemDefinitions.json"), "Z1", False,
     "PICKUP_EFFECT item mapping adopted under D329 and the owner's 2026-09-06 bug-fix request; "
     "joined only when August NAME_ID, ITEM_CLASS and CODE_FACTORY_NAME match, and the effect "
     "exists as an August SFX_Item_PickUp asset", False),
    (Path(r"C:\h1z1project\reference\h1z1-server\data\2016\dataSources\AccountCrates.json"),
     "THIRD_PARTY_SHAPED", True, "crate memberships and weights adopted under D329; filtered to supported August cosmetics", False),
    (Path(r"C:\Project\out\wave20260823-r41\survivorsrest\containers.html"),
     "THIRD_PARTY_SHAPED", True, "cached SurvivorsRest Nemesis crate membership adopted under D329; weights are server design", False),
    (REPO / "src" / "Cranberry.Zone" / "Vehicles" / "VehicleSkinCatalog.cs", "CLIENT", False,
     "August vehicle cosmetic account IDs used to join the economy catalogue", False),
    (REPO / "tools" / "world" / "data" / "z1br-retail-vehicle-markers.json", "CLIENT", False,
     "Complete official Z2.zone v7 vehicle markers, client 1.0.326.439939; 591 playable, 10 lobby", False),
    (REPO / "tools" / "world" / "data" / "z1br-retail-august-placements.json", "RULING", False,
     "Owner-approved 550-location August adaptation: exact retail X/Z and orientation, "
     "audited support heights and 41 explicit geometry exclusions", False),
    (REPO / "tools" / "world" / "data" / "z2-vehicle-locations.json", "Z1", False,
     "the owner's unchanged 403-row 2017 vehicle-location reference, reused under D329", False),
    (REPO / "tools" / "world" / "data" / "z1br-vehicle-observations.json", "CAPTURED", False,
     "44 offline-decrypted ClientProtocol_1315 observations; only two are on main Z2; "
     "PracticeZone and elevated pregame records do not establish August match spawns", False),
    (REPO / "tools" / "world" / "data" / "z1br-vehicle-august-adoption.json", "RULING", False,
     "2026-09-11 placement adaptation: replace the nearby West Peaks jeep and add the captured "
     "barn ATV after August geometry checks; not a recovered full August retail catalogue", False),
    (REPO / "tools" / "world" / "data" / "august-vehicle-mesh-bounds.json", "CLIENT", False,
     "14 August ADR Base meshes: CRC-verified DMOD local bounds, extracted by vehicle_geometry.py "
     "for conservative oriented-bounds exclusions; not detailed physics collision meshes", False),
    (REPO / "src" / "Cranberry.Zone" / "Data" / "Loot" / "z2-terrain.bin", "CLIENT", False,
     "August CNK0 v3 one-metre height/material samples; standalone extraction and provenance "
     "are tools/world/airdrop_terrain.py and the adjacent provenance JSON. Vehicle placement "
     "uses only the solid-terrain proof of complete burial, without snapping bridge heights", False),
    (OUT / "pack-index.txt", "CLIENT", False,
     "index of all 256 August Assets_*.pack archives (50,502 assets): name, pack, offset, "
     "size, crc32 - the CRC authority every extraction is checked against", False),
    (OUT / "registrations-1148.json", "CLIENT", False,
     "the client's own packet-id registration table, recovered from the 28 registration "
     "functions of H1Z1.exe " + CLIENT_BUILD + " with Ghidra (docs/06)", False),
    (REPO / "tools" / "data" / "firemodes-columns.tsv", "CLIENT", False,
     "the 134 float/byte columns of the client's own FireModes datasheet-row loader "
     "(H1Z1.exe " + CLIENT_BUILD + " 0x142226260..0x14222a800), name -> rec+ offset. Extracted "
     "mechanically from out/w14-adsfov/disasm-firemodes-loader.txt; docs/99 CORRECTION 3 and "
     "docs/107 section 9.3. Read by Cranberry.Tests' WeaponListColumnTableTests, which pins "
     "WeaponListLayouts.FireModeColumns against it - no generator reads it", False),
    (REPO / "tools" / "data" / "firemodes-columns-ids.tsv", "CLIENT", False,
     "the 45 integer/id/duration columns of the same loader - the ones it stores through its own "
     "string-to-int helper (LEA RDX,[RBX + off] then CALL 0x14007022f) rather than with a MOVSS. "
     "This is the route that binds REFIRE_TIME_MS, the five reload columns, AMMO_SLOT and the "
     "heat family; docs/99 CORRECTION 3. Read by WeaponListColumnTableTests", False),
    (REPO / "tools" / "data" / "firemodes-columns-full.tsv", "CLIENT", False,
     "the same loader's every column-fetch site, including the 21 bit-packed flags of "
     "rec+0x20..0x22 and the sites whose store it could not place. It lists the one site "
     "14222980e twice - correctly as a dword and again as a spurious bit of rec+0x22 - and "
     "WeaponListColumnTableTests pins that disagreement rather than hiding it", False),
    (OUT / "wave9-bridge" / "opcode-map-1087-to-1148.json", "Z1", False,
     "join of the owner's own 1087 Ghidra registration table with the 1148 one "
     "(clean-room rule 4; D101)", False),
    (Path(r"C:\Z1\Server\Data\dynamicAppearanceFriend.bin"), "CAPTURED", True,
     "a capture of the friend server's DynamicAppearanceDefinitions ReferenceData "
     "(6,625,396 B, MD5-gated by D22). The August client ships NO appearance table, so every "
     "worn colourway Cranberry sends today comes from here or from Cranberry's own starter "
     "palette. S8 §5.4 puts the replacement question to the owner", False),
    (Path(r"C:\Z1\Data\2017\dataSources\dynamicappearance_1087.json"), "CAPTURED", True,
     "the 2017 reference table gen-skin-catalog.py uses to repair the capture's known bad "
     "shader-group/gender fields. Its path shape is the one docs/00 §Forbidden names; it is "
     "declared here rather than being silent about it (S8 §8)", False),
] + [
    (dump, "CLIENT", False,
     "a Ghidra decompile of one packet family's dispatcher in H1Z1.exe " + CLIENT_BUILD +
     ", dumped by an earlier lane. gen-zone-opcodes.py reads the family's sub-opcode WIDTH out "
     "of the C type at the selector read site and fails rather than guessing if the cited line "
     "moves (D181)", False)
    for dump in DISPATCHER_DUMPS
]

#: files inside the trees above that are tooling residue or a report, not inputs
INPUT_SKIP_SUFFIXES = {".py", ".pyc", ".log", ".err", ".md"}
INPUT_SKIP_NAMES = {"__pycache__"}

#: per-file grade overrides that beat the tree they sit in
INPUT_OVERRIDES: dict[str, tuple[str, bool, str]] = {
    rel(OUT / "appearance-tints" / "z1-appearance-parsed.json"): (
        "CAPTURED", True,
        "the APPEARANCE lane's parse of the D22-gated dynamic-appearance source - 3,551 "
        "appearance rows and 1,667 semantic rows recorded off the friend server, not shipped by "
        "the August client. It is what fills AugustWornMeshCatalog.g.cs (S8 §0.2, §5.4)"),
    rel(REPO / "rulings" / "loot-gates.json"): (
        "THIRD_PARTY_SHAPED", False,
        "the five per-family ground-loot gates 0.1267 / 0.0850 / 0.25 / 0.30 / 0.60 (D68). "
        "docs/39 §1.1 and docs/65 §2.2 trace those numbers to a reference server's seven Z2 "
        "ground tables; D68 adopted them under D53 because the owner authored, ran and graded "
        "that world. The file is graded by its worst row rather than by its majority, on "
        "purpose - the flag must never be silent (docs/101 §4)"),
}

KIND_BY_SUFFIX = {
    ".txt": "datasheet", ".xml": "xml", ".tsv": "index", ".lst": "asset-list",
    ".json": "json", ".jsonl": "jsonl", ".zone": "world", ".adr": "actor",
    ".dds": "texture", ".dma": "material", ".dme": "mesh", ".tga": "texture",
    ".bin": "binary", ".log": "log", ".err": "log", ".md": "note", ".cnk": "terrain",
}


def kind_of(path: Path) -> str:
    if path.parent.name == "derived":
        return "derived-json"
    return KIND_BY_SUFFIX.get(path.suffix.lower(), "file")


def scan_inputs() -> list[dict]:
    """Every input asset the server's data comes from, hashed and graded."""
    seen: dict[str, dict] = {}

    def add(path: Path, grade: str, third: bool, note: str, pack_asset: bool) -> None:
        if not path.is_file():
            return
        if path.suffix.lower() in INPUT_SKIP_SUFFIXES:
            return
        key = rel(path)
        if key in seen:
            return
        if key in INPUT_OVERRIDES:
            grade, third, note = INPUT_OVERRIDES[key]
        seen[key] = {
            "path": key,
            "absolutePath": str(path),
            "kind": kind_of(path),
            "sha256": sha256_of(path),
            "crc32": crc32_of(path),
            "size": path.stat().st_size,
            "grade": grade,
            "thirdParty": third,
            "packAsset": pack_asset,
            "source": note,
            "usedBy": [],
        }

    for root, recursive, grade, third, note, pack_asset in INPUT_TREES:
        if not root.is_dir():
            continue
        walk = sorted(root.rglob("*") if recursive else root.glob("*"))
        for path in walk:
            if any(part in INPUT_SKIP_NAMES for part in path.parts):
                continue
            if path.is_file():
                add(path, grade, third, note, pack_asset)

    for path, grade, third, note, pack_asset in INPUT_FILES:
        add(path, grade, third, note, pack_asset)

    # which generator reads which input
    for entry in GENERATORS:
        for source in entry["inputs"]:
            key = rel(Path(source))
            if key in seen and entry["id"] not in seen[key]["usedBy"]:
                seen[key]["usedBy"].append(entry["id"])

    return [seen[k] for k in sorted(seen)]


# ======================================================================================
# the generators
# ======================================================================================

def _sheets(*names: str) -> list[Path]:
    return [DATA_AUG / n for n in names]


ZONE = REPO / "src" / "Cranberry.Zone"
PROTOCOL = REPO / "src" / "Cranberry.Protocol"


#: Every registered generator. ``argv`` is built with ``{stage}`` = the staging directory.
#: ``outputs`` maps the checked-in path to the file name the generator writes into ``{stage}``.
GENERATORS: list[dict] = [
    {
        "id": "inventory-slots",
        "generator": REPO / "tools" / "data" / "gen-inventory-slots.py",
        "argv": [str(DATA_AUG), "{stage}"],
        "outputs": {
            ZONE / "Inventory" / "InventorySlots.g.cs": "InventorySlots.g.cs",
            ZONE / "Inventory" / "InventoryItemFacts.g.cs": "InventoryItemFacts.g.cs",
        },
        "inputs": _sheets(
            "EquipmentSlotDefinitions.txt", "EquipSlotItemClasses.txt", "Loadouts.txt",
            "LoadoutSlots.txt", "LoadoutSlotItemClasses.txt", "ContainerDefinitions.txt",
            "ClientItemDefinitions.txt"),
        "grade": "CLIENT",
        "rulings": ["D33", "D72", "D141"],
        "note": "the slot/loadout/container model and the nine item columns the resolver reads; "
                "the client's own FUN_140d35510 predicate is driven by these exact sheets",
    },
    {
        "id": "footwear",
        "generator": REPO / "tools" / "world" / "gen-footwear.py",
        "argv": ["{stage}"],
        "outputs": {ZONE / "Movement" / "FootwearItems.g.cs": "FootwearItems.g.cs"},
        "inputs": [DATA_AUG / "ClientItemDefinitions.txt"],
        "grade": "CLIENT",
        "rulings": ["D351"],
        "note": "63 footwear tiers from August item class 25005 and PASSIVE_ABILITY_ID; "
                "speed and shred behaviour are applied separately by the owner ruling D351",
    },
    {
        "id": "item-use-options",
        "generator": REPO / "tools" / "data" / "gen-item-use-options.py",
        "argv": [str(DATA_AUG), "{stage}"],
        "outputs": {ZONE / "Inventory" / "ItemUseOptions.g.cs": "ItemUseOptions.g.cs"},
        "inputs": [DERIVED / "loot.json"],
        "grade": "CLIENT",
        "rulings": ["D113", "D116"],
        "note": "reads the derive layer: loot.json useOptions / useOptionGroups / "
                "items[].useOptionGroupId (docs/96 §4 pins the reconstruction as exact)",
    },
    {
        "id": "item-class-mappings",
        "generator": REPO / "tools" / "data" / "gen-item-class-mappings.py",
        "argv": ["--out-dir", "{stage}"],
        "outputs": {ZONE / "Inventory" / "ItemClassMappings.g.cs": "ItemClassMappings.g.cs"},
        "inputs": [DERIVED / "loot.json"],
        "grade": "CLIENT",
        "rulings": ["D72"],
        "note": "reads the derive layer: loot.json items[].mappedClassIds, which is "
                "ItemClassMappings.txt already folded per item",
    },
    {
        "id": "weapon-firegroups",
        "generator": REPO / "tools" / "data" / "gen-weapon-firegroups.py",
        "argv": [],
        "env": {"AUG_OUT": "{stage}/AugustWeaponFacts.g.cs"},
        "outputs": {ZONE / "Weapons" / "AugustWeaponFacts.g.cs": "AugustWeaponFacts.g.cs"},
        "inputs": [DERIVED / "weapons.json"],
        "grade": "CLIENT",
        "rulings": ["D46", "D94"],
        "note": "reads the derive layer: weapons.json weaponItems x weapons.timing reproduces "
                "all 160 ClientItemDatasheetData rows exactly (docs/96 §4)",
    },
    {
        "id": "weapon-lists",
        "generator": REPO / "tools" / "data" / "gen-weapon-lists.py",
        "argv": ["--out-dir", "{stage}"],
        "outputs": {
            ZONE / "Weapons" / "AugustFireModeFacts.g.cs": "AugustFireModeFacts.g.cs",
            ZONE / "Weapons" / "AugustProjectileFacts.g.cs": "AugustProjectileFacts.g.cs",
        },
        "inputs": [DERIVED / "weapon-lists.json"],
        "grade": "CLIENT+RULING",
        "rulings": ["D143", "D159", "D212", "D230", "D231", "D232", "D233", "D234"],
        "note": "reads the derive layer: weapon-lists.json fireModes / projectiles. Every VALUE is "
                "the client's own datasheet cell (CLIP_SIZE, REFIRE_TIME_MS, RELOAD_TIME_MS, "
                "PROJECTILE_ID, PEN_TYPE_ID); the one server-chosen number is the fire-mode id "
                "itself, D159 fireGroupId*2+index, because the August client ships no fire-mode "
                "table at all. Record LAYOUTS are hand-written C# (WeaponListLayouts) - they come "
                "out of the binary, not out of a sheet (docs/99). D212 adds the ADS zoom: the mechanism is [P] (FireMode.DefaultZoom = rec+0x128) but no August sheet carries a zoom column, so AdsZoom is Cranberry's number. D230 adds the ADS FIRST-PERSON switch (FORCE_FP_SCOPE = rec+0x2dc, the only data-driven path into the first-person camera in this build), D231 its absolute aimed FOV (AdsFpCameraFov = the client's own default verticalFOV 65 over AdsZoom, since no sheet carries a field-of-view column either), and D232 adopts the owner's own Z1 iron-sights ramp of 266 ms under D53. D233 (2026-09-03 evening, owner ruling) flips AdsFirstPerson to false and retunes AdsZoom 1.5 -> 1.2: right-click ADS is a THIRD-PERSON over-the-shoulder aim, not first person and not a big zoom, so FORCE_FP_SCOPE ships 0 and the modest zoom is the aim - CRANBERRY_WEAPON_ADS_FP=1 restores the FP swap. D234 (report 1, the fire sound) adds EffectGroup: rec+0x104 is a FireModeEffectGroups GROUP_ID (not a composite id - the client resolves group+'fire' itself), shipped as the fire-group id for groups with a non-zero 'fire' row (1..21/23/24); groups > 24 (the AK-47's 51) are an unrecoverable gap and ship 0",
    },
    {
        "id": "ability-facts",
        "generator": REPO / "tools" / "data" / "gen-ability-facts.py",
        "argv": [],
        "env": {"AUG_OUT": "{stage}/AugustAbilityFacts.g.cs"},
        "outputs": {ZONE / "Combat" / "AugustAbilityFacts.g.cs": "AugustAbilityFacts.g.cs"},
        "inputs": _sheets("ClientItemDefinitions.txt"),
        "grade": "CLIENT",
        "rulings": ["D132"],
        "note": "ACTIVATABLE_ABILITY_ID per item. Still on the raw sheet: no derived document "
                "carries that column (docs/96 §4, gap G1)",
    },
    {
        "id": "armour-facts",
        "generator": REPO / "tools" / "data" / "gen-armour-facts.py",
        "argv": [str(DERIVED / "loot.json"), "{stage}/AugustArmourFacts.g.cs"],
        "outputs": {ZONE / "Combat" / "AugustArmourFacts.g.cs": "AugustArmourFacts.g.cs"},
        "inputs": [DERIVED / "loot.json"],
        "grade": "CLIENT",
        "rulings": ["D92"],
        "note": "reads the derive layer: loot.json items[] isArmor / itemClassId / bulk / modelName",
    },
    {
        "id": "pickup-effects",
        "generator": REPO / "tools" / "data" / "gen-pickup-effects.py",
        "argv": ["--source", str(Path(r"C:\Z1\Data\2016\dataSources\ServerItemDefinitions.json")),
                 "--client-data", str(DATA_AUG), "--output", "{stage}/PickupEffects.g.cs"],
        "outputs": {ZONE / "Loot" / "PickupEffects.g.cs": "PickupEffects.g.cs"},
        "inputs": [Path(r"C:\Z1\Data\2016\dataSources\ServerItemDefinitions.json"),
                   DATA_AUG / "ClientItemDefinitions.txt", DATA_AUG / "ActorCompositeEffectDefinitions.xml"],
        "grade": "Z1",
        "rulings": ["D329"],
        "gradeNotes": {
            "Z1": "per-item PICKUP_EFFECT associations are adopted from the owner's local Z1 data",
            "CLIENT": "item identity must match NAME_ID, ITEM_CLASS and CODE_FACTORY_NAME; all selected "
                      "effects must exist as SFX_Item_PickUp assets in August. IS_ARMOR corrects "
                      "imported helmet sounds on non-armour hats/clothing to clothing effect 5148. "
                      "Generic 5151, clothing 5148 and helmet 5153 are verified by shipped name",
        },
        "note": "Pickup sound mapping used once after a successful ground grant. Unknown or mismatched "
                "items fall back to the August generic pickup sound rather than guessing an old-build id",
    },
    {
        "id": "string-hash-values",
        "generator": REPO / "tools" / "data" / "gen-string-hash-values.py",
        "argv": [str(DATA_AUG / "StringHashToValue.txt"), "{stage}/StringHashValues.g.cs"],
        "outputs": {ZONE / "StringHashValues.g.cs": "StringHashValues.g.cs"},
        "inputs": _sheets("StringHashToValue.txt"),
        "grade": "CLIENT+RULING",
        "rulings": ["D344"],
        "note": "the 573-row table SendZoneDetails and 0xfb carry on the wire; the generator "
                "self-checks five hashes the binary bakes in. The September 7 video follow-up "
                "supersedes D344: PelletPatternsEnabled=1 now consumes the 17-pellet pattern "
                "and group supplied by AugustShotgunPattern after the weapon-table overlays. "
                "Pattern geometry and damage remain explicit reconstruction choices; see docs/video-fixes-20260907.md",
    },
    {
        "id": "skin-catalog",
        "generator": REPO / "tools" / "data" / "gen-skin-catalog.py",
        "argv": [str(DATA_AUG), "{stage}/AugustSkinCatalog.g.cs"],
        "outputs": {ZONE / "AugustSkinCatalog.g.cs": "AugustSkinCatalog.g.cs"},
        "inputs": _sheets(
            "ClientItemDefinitions.txt", "Models.txt", "SkinItemSlot.txt",
            "SkinItemSlotItem.txt", "AcctItemConversions.txt") + [
            Path(r"C:\Z1\Server\Data\dynamicAppearanceFriend.bin"),
            Path(r"C:\Z1\Data\2017\dataSources\dynamicappearance_1087.json"),
        ],
        "grade": "CAPTURED",
        "thirdParty": True,
        "rulings": ["D22", "D53"],
        "note": "THE APPEARANCE HOLE (S8 §0.2, §5.4). The client ships no appearance table, so "
                "the item -> model -> shader-group link comes from a capture of a third-party "
                "server. Options (a) declare the capture, (b) author Cranberry's own palette "
                "from the .adr texture aliases, (c) both - an owner ruling, not a derivation",
    },
    {
        "id": "menu-economy",
        "generator": REPO / "tools" / "data" / "gen-menu-economy.py",
        "argv": ["--data", str(DATA_AUG), "--out", "{stage}/EconomyCatalogData.g.cs"],
        "outputs": {ZONE / "Economy" / "EconomyCatalogData.g.cs": "EconomyCatalogData.g.cs"},
        "inputs": [DERIVED / "economy.json", DATA_AUG / "item-names-en_us.json",
                   DATA_AUG / "ClientItemDefinitions.txt", ZONE / "AugustSkinCatalog.g.cs",
                   ZONE / "Vehicles" / "VehicleSkinCatalog.cs",
                   Path(r"C:\h1z1project\reference\h1z1-server\data\2016\dataSources\AccountCrates.json"),
                   Path(r"C:\Project\out\wave20260823-r41\survivorsrest\containers.html"),
                   REPO / "tools" / "data" / "locked_special_crates.py"],
        "grade": "CLIENT+RULING",
        "thirdParty": True,
        "rulings": ["D27", "D329"],
        "gradeNotes": {
            "THIRD_PARTY_SHAPED": "reference crate memberships and weights adopted explicitly; Nemesis membership from cached SurvivorsRest",
            "RULING": "server design: 250 Crowns to unlock; 100 Scrap per Scrapyard roll; Nomad badge membership and rarity weights; Nemesis rarity weights",
        },
        "note": "saved account cosmetics, distinct locked/unlocked crate pools and Scrapyard rewards. August IDs, scrap values, eligibility and metadata are client data; pool provenance is recorded per family. See docs/menu-economy-codec-evidence.md",
    },
    {
        "id": "asset-index",
        "generator": REPO / "tools" / "appearance" / "gen-asset-index.py",
        "argv": ["--out", "{stage}/AugustAssetIndex.g.cs"],
        "outputs": {ZONE / "Appearance" / "AugustAssetIndex.g.cs": "AugustAssetIndex.g.cs"},
        "inputs": [DATA_AUG / "pack-index-aug.tsv"],
        "grade": "CLIENT",
        "rulings": ["D83"],
        "note": "the 3,406 .adr names the client actually ships, with the pack's own spelling",
    },
    {
        "id": "loot-tables",
        "generator": REPO / "tools" / "data" / "gen-loot-tables.py",
        "argv": ["--out-dir", "{stage}"],
        "outputs": {
            ZONE / "Data" / "Loot" / "z2-loot-tables.json": "z2-loot-tables.json",
            ZONE / "Data" / "Loot" / "z2-loot-spawns.bin": "z2-loot-spawns.bin",
            # D274 (lane LOOT, 2026-09-03): the airdrop crate's own roster, kept out of
            # z2-loot-tables.json so the ground roster's "nothing outside these 72 ids may reach
            # the floor" assertions stay exactly that.
            ZONE / "Data" / "Loot" / "z2-airdrop.json": "z2-airdrop.json",
        },
        "inputs": [
            WORLD / "z2-item-spawners.json", DATA_AUG / "ClientItemDefinitions.txt",
            DATA_AUG / "Models.txt", DATA_AUG / "ContentPacks.txt", DERIVED / "loot.json",
        ],
        "grade": "CLIENT+RULING",
        "rulings": ["D20", "D31", "D39", "D42", "D53", "D68", "D69"],
        "gradeNotes": {
            "THIRD_PARTY_SHAPED": "the five per-family gates 0.1267 / 0.0850 / 0.25 / 0.30 / 0.60 "
                                  "in LootDensityOptions: docs/39 §1.1 and docs/65 §2.2 trace them "
                                  "to a reference server's seven Z2 ground tables; D68 adopted "
                                  "them under D53. The positions, ids, models and clip sizes in "
                                  "this file are the client's",
        },
        "note": "168,322 marker positions and the KOTK-2017 roster. The marker geometry is the "
                "client's Z2.zone; the roster, shares and box sizes are the owner's [OWN]",
    },
    {
        "id": "appearance-catalog",
        "generator": REPO / "tools" / "appearance" / "gen-appearance-catalog.py",
        "argv": ["--out", "{stage}"],
        "outputs": {
            ZONE / "Appearance" / "AugustModelCatalog.g.cs": "AugustModelCatalog.g.cs",
            ZONE / "Appearance" / "AugustWornMeshCatalog.g.cs": "AugustWornMeshCatalog.g.cs",
        },
        "inputs": [
            DATA_AUG / "Models.txt", DATA_AUG / "pack-index-aug.tsv",
            OUT / "appearance-tints" / "z1-appearance-parsed.json",
            ZONE / "Data" / "Loot" / "z2-loot-tables.json",
        ],
        "grade": "CAPTURED",
        "thirdParty": True,
        "rulings": ["D22", "D44", "D84"],
        "note": "AugustModelCatalog is pure client; AugustWornMeshCatalog's ModelId per "
                "(item, gender, shader group) comes from the same capture as skin-catalog. "
                "This generator still carries its own datasheet() splitter - tools/appearance "
                "is outside this lane's ownership (docs/96 §4, gap G6)",
    },
    {
        "id": "authored-appearance",
        "generator": REPO / "tools" / "appearance" / "gen-authored-appearance.py",
        "argv": ["--out", "{stage}"],
        "outputs": {
            ZONE / "Appearance" / "AugustAuthoredAppearance.g.cs":
                "AugustAuthoredAppearance.g.cs",
        },
        "inputs": [
            DATA_AUG / "Models.txt",
            DATA_AUG / "TintSemanticTables.txt",
            DERIVED / "loot.json",
            DATA_AUG / "adr" / "SurvivorMale_Back_Backpack_Mansport_Tintable.adr",
            DATA_AUG / "adr" / "SurvivorFemale_Back_Backpack_Mansport_Tintable.adr",
            DATA_AUG / "adr" / "SurvivorMale_Chest_Shirt_TintTshirt.adr",
            DATA_AUG / "adr" / "SurvivorFemale_Chest_Shirt_TintTshirt.adr",
            DATA_AUG / "adr" / "SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr",
            DATA_AUG / "adr" / "SurvivorFemale_Head_Helmet_Motorcycle_Tintable.adr",
            DATA_AUG / "adr" / "SurvivorMale_Feet_Conveys_Tintable.adr",
            DATA_AUG / "adr" / "SurvivorFemale_Feet_Conveys_Tintable.adr",
            OUT / "appearance-tints" / "SurvivorMale_Back_Backpack_ManSport_C.dds",
            OUT / "appearance-tints" / "SurvivorMale_Chest_Shirt_TintTshirt_Plain_C.dds",
            OUT / "appearance-tints" / "SurvivorMale_Head_Helmet_MotorCycleHelmet_C.dds",
            OUT / "appearance-tints" / "SurvivorMale_Feet_Conveys_Tintable_C.dds",
        ],
        "grade": "CLIENT+RULING",
        "rulings": ["D22", "D190", "D203"],
        "gradeNotes": {
            "RULING": "the HUE of each of the thirteen colourways (PALETTE in the generator) and "
                      "the roster of which item wears which mesh. The August client ships every "
                      "one of these items' NAME - \"Black Backpack\", \"Green Backpack\" - and no "
                      "colourway at all, because the colourway was server-authored in retail "
                      "(docs/106 §5). D203 is the owner decision that Cranberry authors its own "
                      "rather than importing the capture's",
        },
        "note": "the thirteen ground-loot wearables the boot census reports as GREY get "
                "Cranberry's OWN appearance rows. The ModelId and GenderId are the client's "
                "Models.txt; the highlight/midtone/shadow ramp SHAPE is measured off the mesh's "
                "own _C.dds colour map by tools/appearance/ddstint.py (p92/p50/p08 luminance "
                "percentiles, docs/69 §3.4); the alpha and the neutral B branch are "
                "TintSemanticTables row 1 Default. Nothing is taken from the D22-gated capture, "
                "whose 26 rows for these items the runtime filter drops anyway",
    },
    {
        "id": "zone-opcodes",
        "generator": REPO / "tools" / "opcodes" / "gen-zone-opcodes.py",
        "argv": [str(OUT / "registrations-1148.json"), "{stage}/ZoneOpcodes.g.cs"],
        "outputs": {ZONE / "ZoneOpcodes.g.cs": "ZoneOpcodes.g.cs"},
        "inputs": [OUT / "registrations-1148.json"] + DISPATCHER_DUMPS,
        "grade": "CLIENT",
        "rulings": ["D181"],
        "note": "the 241 base packet ids the client registers with itself, plus the sub-opcode "
                "WIDTH of six families read off their own dispatcher decompiles (the "
                "registration table lists members, not framing, so it cannot answer that): "
                "0x09=2, 0x0f=1, 0x11=2, 0x82=1 after a u32 gameTime, 0x86=1, 0xce=2",
    },
    {
        "id": "effect-catalog",
        "generator": REPO / "tools" / "data" / "gen-effect-catalog.py",
        "argv": ["--out-dir", "{stage}"],
        "outputs": {ZONE / "Generated" / "AugustEffectCatalog.g.cs": "AugustEffectCatalog.g.cs"},
        "inputs": [DERIVED / "effects.json"],
        "grade": "CLIENT",
        "rulings": ["D100", "D179"],
        "note": "the 1,047 composite effects of the client's own "
                "ActorCompositeEffectDefinitions.xml, by id and by name. CompositeEffectGate is "
                "an ALLOW list over this table (S8 section 6.4 - the gate's own remark that this "
                "build ships no composite-effect datasheet was wrong); id 0 is absent from the "
                "table, so D100's rule now falls out of the client's data",
    },
    {
        "id": "august-strings",
        "generator": REPO / "tools" / "data" / "gen-august-strings.py",
        "argv": ["--out-dir", "{stage}"],
        "outputs": {ZONE / "Generated" / "AugustStrings.g.cs": "AugustStrings.g.cs"},
        "inputs": [DERIVED / "strings.json"],
        "grade": "CLIENT",
        "rulings": ["D179"],
        "note": "21 locale strings with their en_us text: 13 anchored on the client's own "
                "CodeStringMappings.txt message name, 8 on the exact en_us text (docs/47 section "
                "4e's locale-key inversion, reproduced in derive_strings.py). GasAlerts, "
                "MatchAlerts and InteractionStringPackets read these instead of literals",
    },
    {
        "id": "z1-opcode-map",
        "generator": REPO / "tools" / "bridge" / "gen-z1-opcode-map.py",
        "argv": [str(OUT / "wave9-bridge" / "opcode-map-1087-to-1148.json"),
                 "{stage}/Z1OpcodeMap.g.cs"],
        "outputs": {PROTOCOL / "Z1OpcodeMap.g.cs": "Z1OpcodeMap.g.cs"},
        "inputs": [OUT / "wave9-bridge" / "opcode-map-1087-to-1148.json"],
        "grade": "Z1",
        "rulings": ["D101"],
        "note": "1087 -> 1148 translation so a ported Z1 writer keeps its own opcode constants; "
                "the 1087 half is the owner's own earlier Ghidra work (clean-room rule 4)",
    },
    {
        "id": "destructibles",
        "generator": REPO / "tools" / "world" / "destructibles.py",
        "argv": ["--out", "{stage}", "--placements", str(WORLD / "z2-objects.jsonl"),
                 "--effects", str(DERIVED / "effects.json"), "--zone", str(WORLD / "Z2.zone")],
        "outputs": {
            ZONE / "Data" / "Destructibles" / "z2-destructibles.json": "z2-destructibles.json",
            ZONE / "Data" / "Destructibles" / "z2-destructibles.provenance.json": "z2-destructibles.provenance.json",
        },
        "inputs": [WORLD / "z2-objects.jsonl", WORLD / "Z2.zone", DERIVED / "effects.json",
                   Path(r"C:\h1z1project\reference\h1z1-server\src\servers\ZoneServer2016\entities\destroyable.ts")],
        "grade": "CLIENT+RULING",
        "rulings": ["D329"],
        "note": "45,932 exact August Z2 objects in sixteen authored glass/wood/barrel/wall-blocker families. "
                "Client placements and effects; 2000 glass / 5000 wood / 1000 barrel / 4000 wall health "
                "are declared server tuning, not client health data. Only the 451 WallHoleBlocked01 "
                "panels are removable; surrounding WallHole01 geometry remains intact. "
                "Health reference source/hash is retained in the adjacent provenance JSON.",
    },
    {
        "id": "glass-melee",
        "generator": REPO / "tools" / "world" / "glass_melee.py",
        "argv": ["--out", "{stage}"],
        "outputs": {
            ZONE / "Data" / "Destructibles" / "z2-glass-panes.json": "z2-glass-panes.json",
            ZONE / "Data" / "Destructibles" / "z2-glass-panes.provenance.json": "z2-glass-panes.provenance.json",
        },
        "inputs": [WORLD / "z2-objects.jsonl", REPO / "tools" / "world" / "vehicle_geometry.py"],
        "grade": "CLIENT",
        "rulings": [],
        "note": "15,407 August window panes: ADR/DME bounds and Z2 placement transforms. "
                "Exact actor/mesh SHA-256 hashes are retained in the adjacent provenance file.",
    },
    {
        "id": "doors",
        "generator": REPO / "tools" / "data" / "gen-doors.py",
        "argv": ["--out", "{stage}/z2-doors.bin", "--out-cs", "{stage}/AugustDoorTable.g.cs"],
        "outputs": {
            ZONE / "Data" / "Doors" / "z2-doors.bin": "z2-doors.bin",
            ZONE / "Generated" / "AugustDoorTable.g.cs": "AugustDoorTable.g.cs",
        },
        "inputs": [WORLD / "z2-objects.jsonl", DATA_AUG / "Models.txt", WORLD / "Z2Areas.xml",
                   UNEXTRACTED / "Doors.txt", DERIVED / "effects.json"],
        "grade": "CLIENT+RULING",
        "rulings": ["D75", "D76", "D77", "D78", "D79", "D80", "D81", "D82", "D180"],
        "gradeNotes": {
            "RULING": "which SWING SOUND each of the eight door families gets is Cranberry's "
                      "choice (C1). It is now named as a composite-effect NAME "
                      "(SFX_Door_Wood_Open) instead of a Doors.txt row number, and the row id is "
                      "resolved through the client's own two tables - S8 defect T4 closed, D180. "
                      "The kinematic-twin names are still chosen by hand (docs/55)",
        },
        "note": "4,147 playable invisible door-placement markers out of the client's own Z2.zone - "
                "the eight Common_DPO_DoorProxy_* families and the three Hospital_*_Placer "
                "families, which carry the identical Models.txt marker text - meshes resolved by "
                "NAME through Models.txt, Doors.txt row ids resolved by composite-effect name "
                "through derived/effects.json. 2026-09-03 (docs/112): +39 hospital doors and +5 "
                "LOBBY doors (--max-height 400 -> 600), and every one of the 4,103 rows that were "
                "already here is unchanged field for field",
    },
    {
        "id": "vehicle-anchors",
        "generator": REPO / "tools" / "world" / "vehicle_anchors.py",
        "argv": ["--out", "{stage}"],
        "outputs": {
            ZONE / "Data" / "Vehicles" / "vehicle-roster.json": "vehicle-roster.json",
            ZONE / "Data" / "Vehicles" / "z2-vehicle-anchors.json": "z2-vehicle-anchors.json",
        },
        "inputs": [REPO / "tools" / "world" / "data" / "z1br-retail-vehicle-markers.json",
                   REPO / "tools" / "world" / "data" / "z1br-retail-august-placements.json",
                   REPO / "tools" / "world" / "vehicle_retail.py",
                   REPO / "tools" / "world" / "vehicle_retail_extract.py",
                   REPO / "tools" / "world" / "vehicle_retail_audit.py",
                   REPO / "tools" / "world" / "vehicle_structures.py",
                   REPO / "tools" / "world" / "data" / "august-vehicle-mesh-bounds.json",
                   REPO / "tools" / "world" / "vehicle_geometry.py",
                   REPO / "tools" / "world" / "vehicle_terrain.py",
                   ZONE / "Data" / "Loot" / "z2-terrain.bin",
                   WORLD / "z2-objects.jsonl", WORLD / "z2-areas.json",
                   DERIVED / "vehicles.json", DATA_AUG / "Models.txt"],
        "grade": "CLIENT+RULING+CAPTURED",
        "rulings": ["D35", "D43", "D50", "D329"],
        "gradeNotes": {
            "RULING": "every retained pad is occupied by default; population policy is "
                      "VehicleSpawnPlanner's, not this file's - the client's Vehicles.txt is zero "
                      "on MASS, HEALTH, fuel burn and every other refereeing number",
        },
        "note": "Whole-map retail Z2.zone v7 markers, replacing all previous spawn inputs. "
                "550 accepted / 41 explicitly excluded August geometry decisions preserve exact "
                "source X/Z, heading, pitch, roll and marker family. Y is audited August support "
                "plus 0.1 m, not recovered retail settled Y. All 37 supported observed positions "
                "match retail marker X/Z exactly. Roster remains August-client-derived; visual "
                "mesh validation is not a full PhysX simulation.",
    },
    {
        "id": "gas-weight-areas",
        "generator": REPO / "tools" / "world" / "gas_weight_areas.py",
        "argv": ["{stage}"],
        "outputs": {
            ZONE / "Generated" / "AugustGasWeightAreas.g.cs": "AugustGasWeightAreas.g.cs",
        },
        "inputs": [WORLD / "z2-areas.json"],
        "grade": "CLIENT",
        "rulings": ["D277"],
        "note": "the nine GasWeightArea.<Poi> boxes of the client's own Z2.zone - the only "
                "statement the August client makes about WHERE a safe zone belongs, and what "
                "Daybreak's 2017-06-29 whole-map ring rework shipped into the world file. Every "
                "number is the client's; which box a match ends in, and how the boxes are "
                "weighted, is rulings/gas.json's CentrePlan / PoiWeightExponent (D277)",
    },
    {
        "id": "rulings",
        "generator": REPO / "tools" / "pipeline" / "generators" / "gen-rulings.py",
        "argv": ["{stage}"],
        "outputs": {ZONE / "Generated" / "Rulings.g.cs": "Rulings.g.cs"},
        "inputs": [REPO / "rulings" / f"{name}.json" for name in (
            "movement", "gas", "descent", "sky", "loot-gates", "loot-roster",
            "vehicles-plan", "crafting", "starter", "throwables")]
            + [ZONE / "Data" / "Loot" / "z2-loot-tables.json"],
        "grade": "RULING",
        "rulings": [
            "D16", "D18", "D19", "D20", "D30", "D31", "D32", "D35", "D37", "D42", "D43",
            "D47", "D48", "D50", "D53", "D54", "D55", "D56", "D59", "D61", "D62", "D63",
            "D65", "D66", "D68", "D69", "D70", "D72", "D73", "D97", "D98", "D99", "D116",
            "D120", "D123", "D125", "D127", "D141",
            # docs/118 - the retail crafting/shred/drop lane
            "D256", "D257", "D258", "D260",
            # docs/120 - the throwables lane
            "D300", "D301", "D304", "D306", "D307", "D308", "D350", "D351",
        ],
        "gradeNotes": {
            "THIRD_PARTY_SHAPED": "rulings/loot-gates.json's five per-family gates (D68). "
                                  "docs/39 §1.1 and docs/65 §2.2 trace them to a reference "
                                  "server's Z2 ground tables; the row's own grade field says so, "
                                  "so the flag survives into the generated file's doc comments",
            "CLIENT": "some rows are the client's own numbers hand-transcribed into C# - the "
                      "parachute terminal-velocity rails, Resources.txt fuel MAX, the "
                      "DamageLevelInfo fractions, the starter item ids. They carry grade CLIENT "
                      "and are declared here so a reader can tell them apart from a ruling, not "
                      "because they are rulings",
        },
        "note": "S8 §7.5 step 3 / overhaul plan §3 lane 2C: the owner's rulings stop being C# "
                "literals. 184 rows over 9 subsystems; 165 become constants the value classes "
                "read, 19 are documentation-only (the number already ships inside another graded "
                "artefact, or its home file belongs to another lane). z2-loot-tables.json is an "
                "input because the generator cross-checks 25 restated loot numbers against it and "
                "fails rather than letting the two copies drift",
    },
]

DERIVERS = [
    ("loot", REPO / "tools" / "data" / "derive_loot.py", DERIVED / "loot.json"),
    ("weapons", REPO / "tools" / "data" / "derive_weapons.py", DERIVED / "weapons.json"),
    ("vehicles", REPO / "tools" / "data" / "derive_vehicles.py", DERIVED / "vehicles.json"),
    ("stats", REPO / "tools" / "data" / "derive_stats.py", DERIVED / "stats.json"),
    ("economy", REPO / "tools" / "data" / "derive_economy.py", DERIVED / "economy.json"),
    # Overhaul lane 2D. Additive on purpose: weapons.json is an input of `weapon-firegroups`, and
    # adding keys to it would move that generator's stamped input hash without changing a byte of
    # its output (docs/96 byte-identical rule, docs/99 section 5).
    ("weapon-lists", REPO / "tools" / "data" / "derive_weapon_lists.py",
     DERIVED / "weapon-lists.json"),
    # Overhaul lane 2B (docs/104). Both documents are deliberately CLOCK-FREE: their identity is
    # the sha256 of their own inputs, so re-running `derive` cannot mark the generators that read
    # them stale for no reason (docs/96 section 8, the timestamp defect).
    ("effects", REPO / "tools" / "data" / "derive_effects.py", DERIVED / "effects.json"),
    ("strings", REPO / "tools" / "data" / "derive_strings.py", DERIVED / "strings.json"),
]

#: outputs that cannot carry a comment header. Their provenance lives in manifest.json only.
NO_HEADER_SUFFIXES = {".bin", ".json"}


def entry_by_id(gid: str) -> dict:
    for e in GENERATORS:
        if e["id"] == gid:
            return e
    raise SystemExit(f"no such generator id: {gid}")


# ======================================================================================
# the provenance header
# ======================================================================================


def dominant_eol(body: bytes) -> bytes:
    return b"\r\n" if b"\r\n" in body else b"\n"


def split_header(data: bytes) -> tuple[bytes | None, bytes]:
    """Return (header bytes, body bytes). The header is the leading provenance block."""
    begin = HEADER_BEGIN.encode()
    end = HEADER_END.encode()
    if not data.startswith(begin):
        return None, data
    idx = data.find(end)
    if idx < 0:
        return None, data
    idx += len(end)
    # swallow the newline that closes the block
    if data[idx:idx + 2] == b"\r\n":
        idx += 2
    elif data[idx:idx + 1] == b"\n":
        idx += 1
    return data[:idx], data[idx:]


def parse_header(data: bytes) -> dict | None:
    """Read a provenance header back into {generator, generatorSha, inputs{path: sha}, ...}."""
    head, _ = split_header(data)
    if head is None:
        return None
    out: dict = {"inputs": {}, "rulings": [], "outputs": []}
    for raw in head.decode("utf-8", "replace").replace("\r\n", "\n").split("\n"):
        line = raw.strip()
        if not line.startswith("//"):
            continue
        line = line[2:].strip()
        if line.startswith("generator:"):
            name, _, sha = line[len("generator:"):].strip().rpartition(" sha256=")
            out["generator"] = name.strip()
            out["generatorSha"] = sha.strip()
        elif line.startswith("input:"):
            rest = line[len("input:"):].strip()
            name, _, tail = rest.partition(" sha256=")
            out["inputs"][name.strip()] = tail.split()[0] if tail else ""
        elif line.startswith("id:"):
            out["id"] = line[3:].strip()
        elif line.startswith("grade:"):
            out["grade"] = line[6:].strip()
        elif line.startswith("generatedUtc:"):
            out["generatedUtc"] = line[len("generatedUtc:"):].strip()
    return out


def build_header(entry: dict, inputs_meta: list[dict], generator_sha: str, eol: bytes) -> bytes:
    lines = [
        HEADER_BEGIN,
        "// Written by tools/pipeline/pipeline.py (docs/96). `pipeline.py check` recomputes every",
        "// hash below and fails the build when one has moved; do not edit this block by hand.",
        f"// id: {entry['id']}",
        f"// grade: {entry['grade']}"
        + ("  thirdParty: true" if entry.get("thirdParty") else ""),
        f"// clientBuild: {CLIENT_BUILD}",
        f"// generator: {rel(entry['generator'])} sha256={generator_sha}",
    ]
    for meta in inputs_meta:
        lines.append(
            f"// input: {meta['path']} sha256={meta['sha256']} crc32={meta['crc32']} "
            f"size={meta['size']} grade={meta['grade']}"
            + ("  thirdParty: true" if meta.get("thirdParty") else ""))
    rulings = ", ".join(entry.get("rulings") or []) or "(none)"
    lines.append(f"// rulings: {rulings}")
    for grade, why in (entry.get("gradeNotes") or {}).items():
        for i, chunk in enumerate(wrap(why, 92)):
            lines.append(f"// {grade + ': ' if i == 0 else '    '}{chunk}")
    lines.append(f"// generatedUtc: {now_utc()}")
    lines.append(HEADER_END)
    return eol.join(line.encode("utf-8") for line in lines) + eol


def wrap(text: str, width: int) -> list[str]:
    words, line, out = text.split(), "", []
    for word in words:
        if line and len(line) + 1 + len(word) > width:
            out.append(line)
            line = word
        else:
            line = f"{line} {word}".strip()
    if line:
        out.append(line)
    return out


# ======================================================================================
# input metadata
# ======================================================================================


def input_meta(path: Path, grades: dict[str, dict]) -> dict:
    key = rel(path)
    known = grades.get(key)
    if not path.is_file():
        raise SystemExit(f"missing input: {path}")
    return {
        "path": key,
        "sha256": sha256_of(path),
        "crc32": crc32_of(path),
        "size": path.stat().st_size,
        "grade": (known or {}).get("grade", "CLIENT"),
        "thirdParty": bool((known or {}).get("thirdParty", False)),
    }


def load_input_grades() -> dict[str, dict]:
    """Grades from inputs.json when it exists, else from the declarations in this file."""
    if INPUTS_JSON.is_file():
        doc = json.loads(INPUTS_JSON.read_text(encoding="utf-8"))
        return {row["path"]: row for row in doc["inputs"]}
    out: dict[str, dict] = {}
    for path, grade, third, _note, _pack in INPUT_FILES:
        out[rel(path)] = {"grade": grade, "thirdParty": third}
    for root, _rec, grade, third, _note, _pack in INPUT_TREES:
        out.setdefault(rel(root), {"grade": grade, "thirdParty": third})
    return out


def grade_for_generated_input(path: Path, grades: dict[str, dict]) -> dict:
    """A generator input that is itself a generated file inherits that generator's grade."""
    for entry in GENERATORS:
        for out_path in entry["outputs"]:
            if Path(out_path) == Path(path):
                return {"grade": entry["grade"], "thirdParty": bool(entry.get("thirdParty"))}
    known = grades.get(rel(path), {})
    # inputs.json can predate a source edit. Its classification must never overwrite
    # the fresh digest/size from input_meta when a reviewed catalogue is regenerated.
    return {"grade": known.get("grade", "CLIENT"), "thirdParty": bool(known.get("thirdParty", False))}


# ======================================================================================
# running a generator
# ======================================================================================


def run_generator(entry: dict, stage: Path) -> None:
    argv = [sys.executable, str(entry["generator"])]
    argv += [a.replace("{stage}", str(stage).replace("\\", "/")) for a in entry["argv"]]
    env = dict(os.environ)
    for key, value in (entry.get("env") or {}).items():
        env[key] = value.replace("{stage}", str(stage).replace("\\", "/"))
    proc = subprocess.run(argv, cwd=str(REPO), env=env, capture_output=True, text=True)
    if proc.returncode != 0:
        sys.stderr.write(proc.stdout + proc.stderr)
        raise SystemExit(f"[{entry['id']}] generator failed with exit {proc.returncode}")


# ======================================================================================
# subcommands
# ======================================================================================


def cmd_generate(args) -> int:
    grades = load_input_grades()
    ids = args.only or [e["id"] for e in GENERATORS]
    changed: list[str] = []
    stamped: list[str] = []
    unchanged: list[str] = []
    rows: list[dict] = []

    for gid in ids:
        entry = entry_by_id(gid)
        stage = Path(tempfile.mkdtemp(prefix=f"cranberry-{gid}-"))
        try:
            run_generator(entry, stage)
            generator_sha = sha256_of(entry["generator"])
            inputs_meta = []
            for source in entry["inputs"]:
                meta = input_meta(Path(source), grades)
                meta.update(grade_for_generated_input(Path(source), grades))
                inputs_meta.append(meta)

            row = {
                "id": gid,
                "outputs": [],
                "generator": rel(entry["generator"]),
                "generatorSha256": generator_sha,
                "inputs": inputs_meta,
                "rulings": entry.get("rulings") or [],
                "gradeNotes": entry.get("gradeNotes") or {},
                "grade": entry["grade"],
                "thirdParty": bool(entry.get("thirdParty")),
                "note": entry.get("note", ""),
                "generatedUtc": now_utc(),
                "clientBuild": CLIENT_BUILD,
            }

            for dest, staged_name in entry["outputs"].items():
                dest = Path(dest)
                produced = stage / staged_name
                if not produced.is_file():
                    raise SystemExit(f"[{gid}] generator wrote no {staged_name}")
                new_body = produced.read_bytes()
                wants_header = dest.suffix.lower() not in NO_HEADER_SUFFIXES

                if dest.is_file():
                    current = dest.read_bytes()
                    _, cur_body = split_header(current)
                    if cur_body.replace(b"\r\n", b"\n") == new_body.replace(b"\r\n", b"\n"):
                        body = cur_body          # byte-preserving: only the header may change
                        content_changed = False
                    else:
                        body = new_body
                        content_changed = True
                else:
                    body = new_body
                    content_changed = True

                if content_changed and not args.write_changed:
                    changed.append(rel(dest))
                    continue

                if wants_header:
                    header = build_header(entry, inputs_meta, generator_sha, dominant_eol(body))
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(header + body)
                else:
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(body)

                (stamped if wants_header else unchanged).append(rel(dest))
                row["outputs"].append({
                    "path": rel(dest),
                    "sha256": sha256_of(dest),
                    "size": dest.stat().st_size,
                    "header": wants_header,
                })
            rows.append(row)
        finally:
            shutil.rmtree(stage, ignore_errors=True)

    print(f"generate: {len(stamped)} file(s) stamped with a provenance header, "
          f"{len(unchanged)} manifest-only output(s), {len(changed)} left alone")
    for path in stamped + unchanged:
        print(f"  ok      {path}")
    for path in changed:
        print(f"  CHANGED {path}  (content differs from the checked-in file; not written - "
              f"re-run with --write-changed once you mean it)")

    if rows and not args.no_manifest:
        write_manifest(rows)
    return 1 if changed and args.strict else 0


def collect_manifest_rows() -> list[dict]:
    """Manifest rows for the tree as it stands, without running any generator."""
    grades = load_input_grades()
    rows = []
    for entry in GENERATORS:
        inputs_meta = []
        for source in entry["inputs"]:
            meta = input_meta(Path(source), grades)
            meta.update(grade_for_generated_input(Path(source), grades))
            inputs_meta.append(meta)
        outputs = []
        for dest in entry["outputs"]:
            dest = Path(dest)
            if not dest.is_file():
                continue
            outputs.append({
                "path": rel(dest),
                "sha256": sha256_of(dest),
                "size": dest.stat().st_size,
                "header": dest.suffix.lower() not in NO_HEADER_SUFFIXES,
            })
        rows.append({
            "id": entry["id"],
            "outputs": outputs,
            "generator": rel(entry["generator"]),
            "generatorSha256": sha256_of(entry["generator"]),
            "inputs": inputs_meta,
            "rulings": entry.get("rulings") or [],
            "gradeNotes": entry.get("gradeNotes") or {},
            "grade": entry["grade"],
            "thirdParty": bool(entry.get("thirdParty")),
            "note": entry.get("note", ""),
            "generatedUtc": now_utc(),
            "clientBuild": CLIENT_BUILD,
        })
    return rows


def write_manifest(rows: list[dict]) -> None:
    doc = {
        "schema": "cranberry-data-manifest/1",
        "clientBuild": CLIENT_BUILD,
        "generatedUtc": now_utc(),
        "grades": GRADES,
        "gradeHistogram": dict(Counter(r["grade"] for r in rows)),
        "generators": rows,
    }
    MANIFEST_JSON.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {rel(MANIFEST_JSON)} ({len(rows)} generators)")


def write_docs_manifest(rows: list[dict], inputs: list[dict]) -> None:
    gen_hist = Counter(r["grade"] for r in rows)
    in_hist = Counter(r["grade"] for r in inputs)
    lines: list[str] = []
    w = lines.append
    w("# DATA-MANIFEST - every generated file, what it was made from, and its grade")
    w("")
    w("Generated by `python tools/pipeline/pipeline.py manifest`. Do not edit by hand.")
    w(f"Client build {CLIENT_BUILD}. Written {now_utc()}.")
    w("")
    w("The machine-readable copies are `tools/pipeline/manifest.json` (generated files) and")
    w("`tools/pipeline/inputs.json` (every input asset the server's data comes from). The")
    w("pipeline, the grades and the switch that makes a stale file fail the build are described in")
    w("`docs/96-data-pipeline.md`.")
    w("")
    w("## Grades")
    w("")
    w("| Grade | Meaning |")
    w("|---|---|")
    for grade, meaning in GRADES.items():
        w(f"| `{grade}` | {meaning} |")
    w("")
    w("## Generated files")
    w("")
    w("| Output | Generator | Grade | Inputs | Rulings |")
    w("|---|---|---|---:|---|")
    for row in sorted(rows, key=lambda r: r["id"]):
        outs = "<br>".join(f"`{o['path']}`" for o in row["outputs"]) or "(not built)"
        grade = row["grade"] + (" **3rd-party**" if row["thirdParty"] else "")
        rulings = ", ".join(row["rulings"]) or "-"
        w(f"| {outs} | `{row['generator']}` | {grade} | {len(row['inputs'])} | {rulings} |")
    w("")
    w("## Inputs per generated file")
    w("")
    for row in sorted(rows, key=lambda r: r["id"]):
        w(f"### `{row['id']}` - {row['grade']}")
        w("")
        if row["note"]:
            w(row["note"] + ".")
            w("")
        w("| Input | Grade | Size | sha256 (first 16) |")
        w("|---|---|---:|---|")
        for meta in row["inputs"]:
            flag = " **3rd-party**" if meta.get("thirdParty") else ""
            w(f"| `{meta['path']}` | {meta['grade']}{flag} | {meta['size']:,} | "
              f"`{meta['sha256'][:16]}` |")
        for grade, why in (row.get("gradeNotes") or {}).items():
            w("")
            w(f"> **{grade}** - {why}.")
        w("")
    w("## Histograms")
    w("")
    w("| Grade | Generated files | Input assets |")
    w("|---|---:|---:|")
    for grade in GRADES:
        w(f"| `{grade}` | {gen_hist.get(grade, 0)} | {in_hist.get(grade, 0)} |")
    w(f"| **total** | **{sum(gen_hist.values())}** | **{sum(in_hist.values())}** |")
    w("")
    # docs/ is LF in this repo (.gitattributes `* text=auto eol=lf`); only docs/01 and docs/02
    # are CRLF, and neither of those is written here.
    DOCS_MANIFEST.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {rel(DOCS_MANIFEST)}")


def cmd_manifest(args) -> int:
    inputs = scan_inputs()
    doc = {
        "schema": "cranberry-data-inputs/1",
        "clientBuild": CLIENT_BUILD,
        "generatedUtc": now_utc(),
        "grades": GRADES,
        "gradeHistogram": dict(Counter(r["grade"] for r in inputs)),
        "totalBytes": sum(r["size"] for r in inputs),
        "note": "Every input asset the server's generated data comes from. `packAsset: true` "
                "means the file can be re-extracted from the client's own packs by "
                "`pipeline.py extract`; the rest are this repository's own parses, the client "
                "binary's registration table, or a declared capture.",
        "inputs": inputs,
    }
    INPUTS_JSON.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {rel(INPUTS_JSON)} ({len(inputs)} input assets, "
          f"{doc['totalBytes']:,} bytes)")

    rows = collect_manifest_rows()
    write_manifest(rows)
    if not args.no_docs:
        write_docs_manifest(rows, inputs)

    print("input grade histogram: " + ", ".join(
        f"{g}={n}" for g, n in sorted(doc["gradeHistogram"].items())))
    print("generated-file grade histogram: " + ", ".join(
        f"{g}={n}" for g, n in sorted(Counter(r["grade"] for r in rows).items())))
    return 0


def cmd_check(args) -> int:
    stale: list[str] = []
    notes: list[str] = []
    grades = load_input_grades()

    manifest = None
    if MANIFEST_JSON.is_file():
        manifest = {r["id"]: r for r in
                    json.loads(MANIFEST_JSON.read_text(encoding="utf-8"))["generators"]}

    for entry in GENERATORS:
        gid = entry["id"]
        generator_sha = sha256_of(entry["generator"])
        current_inputs = {}
        for source in entry["inputs"]:
            path = Path(source)
            if not path.is_file():
                stale.append(f"{gid}: input missing: {rel(path)}")
                continue
            current_inputs[rel(path)] = sha256_of(path)

        for dest in entry["outputs"]:
            dest = Path(dest)
            if not dest.is_file():
                stale.append(f"{gid}: output missing: {rel(dest)}")
                continue
            wants_header = dest.suffix.lower() not in NO_HEADER_SUFFIXES
            if wants_header:
                head = parse_header(dest.read_bytes())
                if head is None:
                    stale.append(f"{rel(dest)}: no provenance header "
                                 f"(run `pipeline.py generate`)")
                    continue
                if head.get("generatorSha") != generator_sha:
                    stale.append(f"{rel(dest)}: generator {rel(entry['generator'])} has changed "
                                 f"since this file was written")
                for name, sha in current_inputs.items():
                    recorded = head["inputs"].get(name)
                    if recorded is None:
                        stale.append(f"{rel(dest)}: input {name} is not in the header")
                    elif recorded != sha:
                        stale.append(f"{rel(dest)}: input {name} has changed since this file "
                                     f"was written")
                for name in head["inputs"]:
                    if name not in current_inputs:
                        notes.append(f"{rel(dest)}: header names an input the registry no longer "
                                     f"lists: {name}")
            else:
                if manifest is None:
                    stale.append(f"{rel(dest)}: no manifest.json to check against "
                                 f"(run `pipeline.py manifest`)")
                    continue
                row = manifest.get(gid)
                if row is None:
                    stale.append(f"{rel(dest)}: not in manifest.json")
                    continue
                recorded_out = next((o for o in row["outputs"] if o["path"] == rel(dest)), None)
                if recorded_out is None:
                    stale.append(f"{rel(dest)}: not in manifest.json")
                elif recorded_out["sha256"] != sha256_of(dest):
                    stale.append(f"{rel(dest)}: edited since the manifest was written")
                if row["generatorSha256"] != generator_sha:
                    stale.append(f"{rel(dest)}: generator {rel(entry['generator'])} has changed "
                                 f"since the manifest was written")
                for meta in row["inputs"]:
                    sha = current_inputs.get(meta["path"])
                    if sha is not None and sha != meta["sha256"]:
                        stale.append(f"{rel(dest)}: input {meta['path']} has changed since the "
                                     f"manifest was written")

    del grades
    for note in notes:
        print(f"note: {note}")
    if stale:
        print(f"pipeline check FAILED - {len(stale)} stale finding(s):", file=sys.stderr)
        for line in stale:
            print(f"  {line}", file=sys.stderr)
        print("Re-run `python tools/pipeline/pipeline.py generate` "
              "(and `manifest`), or set CranberryDataCheck=false to build anyway.",
              file=sys.stderr)
        return 1
    print(f"pipeline check OK - {sum(len(e['outputs']) for e in GENERATORS)} generated file(s) "
          f"are current against their inputs and generators")
    return 0


def cmd_derive(args) -> int:
    target = Path(args.stage) if args.stage else DERIVED
    target.mkdir(parents=True, exist_ok=True)
    differing = []
    for name, script, canonical in DERIVERS:
        if args.only and name not in args.only:
            continue
        dest = target / canonical.name
        proc = subprocess.run([sys.executable, str(script), "--out", str(dest)],
                              cwd=str(REPO), capture_output=True, text=True)
        if proc.returncode != 0:
            sys.stderr.write(proc.stdout + proc.stderr)
            return proc.returncode
        if args.stage and canonical.is_file():
            same = sha256_of(dest) == sha256_of(canonical)
            print(f"  {name:9s} {'same as' if same else 'DIFFERS from'} {rel(canonical)}")
            if not same:
                differing.append(name)
        else:
            print(f"  {name:9s} -> {rel(dest)} ({dest.stat().st_size:,} bytes)")
    if differing:
        print("derive: " + ", ".join(differing) + " would change; the generators that read them "
              "will change too - run `generate` and `manifest` after.")
    return 0


def cmd_extract(args) -> int:
    if not INPUTS_JSON.is_file():
        raise SystemExit("inputs.json is missing; run `pipeline.py manifest` first")
    doc = json.loads(INPUTS_JSON.read_text(encoding="utf-8"))
    index = OUT / "pack-index-aug.tsv"
    if not index.is_file():
        index = DATA_AUG / "pack-index-aug.tsv"
    groups: dict[str, list[str]] = {}
    for row in doc["inputs"]:
        if not row.get("packAsset"):
            continue
        path = Path(row["absolutePath"])
        groups.setdefault(str(path.parent), []).append(path.name)
    packread = REPO / "tools" / "pack" / "packread.py"
    failures = 0
    for directory, names in sorted(groups.items()):
        if args.only_dir and args.only_dir not in directory:
            continue
        print(f"extract -> {directory} ({len(names)} asset(s))")
        if args.dry_run:
            continue
        argv = [sys.executable, str(packread), "extract", "--index-file", str(index),
                "-o", directory] + sorted(names)
        proc = subprocess.run(argv, cwd=str(REPO), capture_output=True, text=True)
        sys.stdout.write(proc.stdout[-4000:])
        if proc.returncode != 0:
            sys.stderr.write(proc.stderr[-4000:])
            failures += 1
    return 1 if failures else 0


# ======================================================================================


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("extract", help="re-extract the client assets inputs.json names")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--only-dir", help="restrict to destination directories containing this text")
    p.set_defaults(fn=cmd_extract)

    p = sub.add_parser("derive", help="run the five derive_*.py joins")
    p.add_argument("--stage", help="write into this directory and diff instead of overwriting")
    p.add_argument("--only", action="append",
                   help="loot|weapons|vehicles|stats|economy|weapon-lists|effects|strings")
    p.set_defaults(fn=cmd_derive)

    p = sub.add_parser("generate", help="run the generators and stamp provenance headers")
    p.add_argument("--only", action="append", help="one generator id; repeatable")
    p.add_argument("--write-changed", action="store_true",
                   help="also install outputs whose content differs from the checked-in file")
    p.add_argument("--strict", action="store_true",
                   help="exit non-zero when an output would have changed")
    p.add_argument("--no-manifest", action="store_true")
    p.set_defaults(fn=cmd_generate)

    p = sub.add_parser("check", help="fail when a generated file is stale")
    p.set_defaults(fn=cmd_check)

    p = sub.add_parser("manifest", help="rewrite inputs.json, manifest.json and docs/DATA-MANIFEST.md")
    p.add_argument("--no-docs", action="store_true")
    p.set_defaults(fn=cmd_manifest)

    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
