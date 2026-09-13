#!/usr/bin/env python3
r"""gen-doors - build Cranberry's Z2 door dataset from the client's own world file.

INPUT
    ``C:\Aug2017\out\world_aug\z2-objects.jsonl``   501,987 ZONE v5 object placements,
                                                     parsed out of the client's Z2.zone by
                                                     tools/zone/zoneread.py
    ``C:\Aug2017\out\data_aug\Models.txt``           the client's actor-model catalogue -
                                                     the door mesh each proxy stands for
    ``out\overhaul-20260901\unextracted\Doors.txt``  the client's own 17-row door table:
                                                     ``ID^EFFECT_ON_OPEN^EFFECT_ON_CLOSE^
                                                     TRANSITION_TIME_MS``
    ``C:\Aug2017\out\data_aug\derived\effects.json`` the 1,047 composite effects
                                                     (tools/data/derive_effects.py), which
                                                     turn those effect ids into names
    ``C:\Aug2017\out\world_aug\Z2Areas.xml``         (optional) POI labels, same
                                                     containment rule zonespawners.py uses

OUTPUT
    ``src/Cranberry.Zone/Data/Doors/z2-doors.bin``   CRDR v2 - the playable door
                                                     placements in a fixed 24-byte record,
                                                     already sorted into the runtime's grid
    ``src/Cranberry.Zone/Generated/AugustDoorTable.g.cs``
                                                     the 17 Doors.txt rows with their effect
                                                     NAMES, and the row each door family
                                                     resolves to

WHAT IS DERIVED (docs/42; every one of these fails this build if it stops holding)

  D1  A door is an INVISIBLE PLACEMENT MARKER, and NOTHING else in the world is an
      interactable door. The eleven marker actors in KINDS below - the eight
      ``Common_DPO_DoorProxy_*`` and the three ``Hospital_*_Placer`` - all carry
      ``<Invisible value="1"/>`` and are all described in Models.txt with the identical
      marker text "DO NOT DELETE!! Used to place objects in the world"
      (``out/data_aug/Models.txt:944,946,948`` for the three hospital rows); the 1,756
      door-*mesh* placements are ordinary static geometry and are disjoint from the markers
      (docs/42 s7a, s7c). This tool therefore selects on the marker actor name alone.
      **2026-09-03 (docs/114, AUDIT-doors s2a/s2d):** the three hospital placers used to be
      absent from KINDS, so every hospital in Z2 had open, non-interactable doorways. They
      are the owner's own Z1 map under D53 (``C:\Z1\Server\Zone\ZoneWorldObjects.cs:3190-3192``),
      and the ids are independently confirmed by Models.txt itself: each ``_Placer`` row sits
      immediately after the mesh row it places and shares its base name
      (9887 ``Hospital_Door01`` / 9888 ``Hospital_Door01_Placer``, 9889 / 9890,
      9891 / 9892), exactly the convention the eight DPO families follow.
  D2  The count. Z2.zone holds exactly 4,147 markers over the eleven names in KINDS below.
      **All 4,147 are playable.** --max-height survives as a lever only; its default is 600
      and it drops nothing.
      **2026-09-03 (docs/114, AUDIT-doors s2c):** it used to default to 400, which dropped
      five Industrial proxies at y ~ 506 as "the off-map lobby set piece". Those five are
      the pre-match LOBBY's own doors, and they are EXACTLY the five doors the friend's live
      server spawns in the owner's 2026-08-22 admin capture - matched position to 0.070 m
      and yaw to the last decimal on all five (capture records #5/#6/#8/#9/#10 in
      ``C:\Project\out\ingest-admin-20260822-part1\ops\cPacketIdAddLightweightNpc.txt``
      against Z2 instances 1796604819, 1435455604, 1796604901, 1796604902, 1796604910).
      Cranberry's own StagingSpawn stands 48-57 m from two of them. Both numbers are
      asserted, not discovered: a change in either is a client change.
  D3  The key. Every marker's ZONE ``instanceId`` is distinct across all 4,147, so it is a
      stable per-door key across restarts and across regenerations of this file.
  D4  The pose. Doors are yaw-only: rot[0] is the ZONE Euler yaw and rot[1]/rot[2] are 0 on
      almost every one. Only rot[0] is carried; the tool reports how many placements it had
      to flatten so a future client build cannot silently change that.
  D5  The mesh. The door model spawned at each marker is the Models.txt row named in KINDS.
      Ten of the eleven are exact name matches to their marker; ``Office`` ->
      ``Doors_Commercial_01`` is by elimination (docs/42 s7d, marked [lead] there and here).
      The ids are resolved out of Models.txt by NAME, never hard-coded, so a wrong id is
      impossible and a missing row is a hard failure.
  D6  The collision twin (CRDR v2, docs/55 s1e + the BUILD section). Collision in 1148 is a
      property of the actor definition and of NOTHING on the wire: an actor gets a physics
      body only from its own ``<CollisionData ... createAsKinematic=.../>`` element. Exactly
      33 of the client's 3,406 actors are ``createAsKinematic="1"`` and 29 of them are
      ``Common_Props_Doors_*`` - the authoring convention for "this collision body moves at
      runtime", which is what a door is. Every mesh in the ``Doors_*`` family this tool
      spawns is ``createAsKinematic="0" useBoundingBox="0"``, i.e. static level geometry.
      So each kind also names the kinematic actor the runtime may spawn in its place; that
      id is resolved by NAME exactly as the mesh id is, and ``None`` means "this family has
      no usable twin" and is written as 0.

  D7  The ``Doors.txt`` ROW ID per family, resolved rather than typed (overhaul plan lane 2B,
      S8 defect T4). ``Doors.txt`` is a SOUND table and nothing else (docs/42 s6c): a row is
      ``ID^EFFECT_ON_OPEN^EFFECT_ON_CLOSE^TRANSITION_TIME_MS`` and the two effect columns are
      composite-effect ids into ``ActorCompositeEffectDefinitions.xml``, where they have NAMES:
      row 2 is ``SFX_Door_Wood_Open`` / ``SFX_Door_Wood_Close``, row 12 is
      ``SFX_Door_Metal_Industrial_*``, row 13 is ``SFX_Door_Office_*``. So each family below
      names the swing SOUND it wants, and the row id is looked up: the LOWEST ``Doors.txt`` row
      whose ``EFFECT_ON_OPEN`` is that effect. Five of the seventeen rows share the wood pair
      (2, 9, 14, 15, 16, 18), which is why "lowest" is part of the rule and not an accident.
      The pairing is checked as well - the close effect must be the ``_Close`` twin of the open
      one - so a client change that re-points a row fails this build instead of silently
      swapping a sound.

WHAT IS CRANBERRY'S OWN CHOICE

  C1  WHICH sound each door family gets. The row selects the open/close effect, an id with no
      row plays no sound, and ANY id > 0 makes a working door, so this is a taste choice with
      no protocol consequence. It is now expressed in the client's own vocabulary - a composite
      effect NAME - instead of as a row number (D7).
  C2  The file format, the 128 x 128 / 64 m grid and the record layout. Same shape as
      ``z2-loot-spawns.bin`` (CRLP) so the two datasets read alike.

USAGE
    python gen-doors.py
    python gen-doors.py --objects ... --models ... --areas ... --out ...  --check
"""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "zone"))

# The one datasheet reader (docs/96); Models.txt was split on '^' by hand here.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "pipeline"))
from readers.sheet import read_rows  # noqa: E402

MAGIC = b"CRDR"
VERSION = 2
HEADER_BYTES = 64
RECORD_BYTES = 24
KIND_BYTES = 16

DIMENSION = 128
CELL_METRES = 64.0
ORIGIN_X = -4096.0
ORIGIN_Z = -4096.0

NO_AREA = 0xFFFF

PROXY_PREFIX = "Common_DPO_DoorProxy_"

# D5 + D6 + C1. (family name, MARKER actor Models.txt name, door mesh Models.txt name,
#     swing sound, expected count, kinematic collision twin)
#
# The marker actor is named in full rather than derived from PROXY_PREFIX because the client's
# door markers are not one naming family: the eight ``Common_DPO_DoorProxy_*`` and the three
# ``Hospital_*_Placer`` carry the same <Invisible value="1"/> and the same Models.txt marker text
# and differ only in the name their artist gave them (D1).
# The expected count is the measured Z2 census (docs/42 s7b) and is asserted: this tool is
# a transcription of one specific client's world file, and a silent count drift would mean
# it is transcribing something else.
#
# THE TWIN COLUMN (D6). The rule that picked each one, applied in this order and recorded in
# docs/55 so it can be re-run:
#
#   Gate    the twin must share the ``Doors_*`` hinge convention - hinge at local x ~ 0, the
#           leaf running to -x, base at y ~ 0 - and its LOD0 height must be within 10 mm of
#           the visual mesh's. Measured from the .dme DMOD v4 headers under
#           ``C:\Aug2017\out\data_aug\dme``. This gate is an ELIMINATION, not a ranking:
#           it removes the 2.466-2.468 m kinematic subset (Office/Industrial/Push/Restaurant/
#           Freezer and the three washroom doors), which would stand 21 cm proud of a 2.26 m
#           Z2 door frame, and it removes both families that cannot fit at all (below).
#   Choice  among the survivors - all 2.258 m tall and 1.019-1.042 m wide - pick the one whose
#           MaterialType and name match the visual mesh. Width is NOT a discriminator: every
#           survivor is 0.28-0.31 m narrower than the Doors_* leaf it replaces, so the door
#           leaves a ~0.29 m gap at the latch jamb whichever is chosen. That gap is the known,
#           accepted cost of this fix and is why DoorCollisionMode is switchable.
#
# The two families with NO twin, and why - neither is a judgement call:
#   Camper         Vehicles_Camper01_Door is 0.126 x 2.349 x 1.336 m with the leaf running
#                  along +Z and its origin 0.9 m above the model base. Every kinematic door is
#                  x-hinged with its base at y = 0. Common_Props_Doors_CamperDoor01 would spawn
#                  rotated 90 deg and floating. 463 doors keep the visual mesh and stay
#                  walk-through.
#   BathroomStall  Doors_Office_BathroomStall is a 1.602 m half-height stall door; the shortest
#                  kinematic actor is 2.258 m. 1 door on all of Z2.
# THE SOUND COLUMN (D7 + C1). Not a Doors.txt row number: the NAME of the composite effect the
# family's open swing should play. gen-doors resolves it to the lowest Doors.txt row whose
# EFFECT_ON_OPEN is that effect, so the row ids in z2-doors.bin come out of the client's own two
# tables and nothing here is a typed id. The eight names below reproduce the row ids this file
# carried by hand until 2026-09-02 exactly (2, 13, 4, 2, 7, 12, 2, 13); the C# side pins that in
# AugustDoorTableTests, and z2-doors.bin is byte-identical across the change.
KINDS: list[tuple[str, str, str, str, int, str | None]] = [
    ("ResidentialFront", PROXY_PREFIX + "ResidentialFront.adr",
     "Doors_Residential_Front.adr", "SFX_Door_Wood_Open", 1262,
     "Common_Props_Doors_ResidentialFront01.adr"),        # WOOD 2.258, name match
    ("Office", PROXY_PREFIX + "Office.adr",
     "Doors_Commercial_01.adr", "SFX_Door_Office_Open", 905,   # [lead], docs/42 s7d
     "Common_Props_Doors_BuisnessDoorMetal01.adr"),       # the only 2.258 business leaf left
    ("Camper", PROXY_PREFIX + "Camper.adr",
     "Vehicles_Camper01_Door.adr", "SFX_Door_Placeable_Metal_Open", 463, None),
    ("Residential", PROXY_PREFIX + "Residential.adr",
     "Doors_Residential_Interior.adr", "SFX_Door_Wood_Open", 429,
     "Common_Props_Doors_ResidentialDoor01.adr"),         # WOOD 2.258, name match
    ("CommercialGlass", PROXY_PREFIX + "CommercialGlass.adr",
     "Doors_Business_Glass.adr", "SFX_Door_Glass_Business_Open", 393,
     "Common_Props_Doors_BuisnessDoorGlass01.adr"),       # GLASS 2.258, name + the glass row
    ("Industrial", PROXY_PREFIX + "Industrial.adr",
     "Doors_Industrial_01.adr", "SFX_Door_Metal_Industrial_Open", 369,
     "Common_Props_Doors_BuisnessDoorMetal01.adr"),       # IndustrialDoor01 is 21.6 cm too tall
    ("Cabin", PROXY_PREFIX + "Cabin.adr",
     "Doors_Cabin_01.adr", "SFX_Door_Wood_Open", 286,
     "Common_Props_Doors_ResidentialFront01.adr"),        # WOOD 2.258, depth 0.192 vs 0.193
    ("BathroomStall", PROXY_PREFIX + "BathroomStall.adr",
     "Doors_Office_BathroomStall.adr", "SFX_Door_Office_Open", 1, None),
    # --- 2026-09-03, docs/114 gap 2. The three hospital placers (D1). APPENDED, never
    # inserted: the kind index is a byte in every 24-byte door record, so adding a family at
    # the end is the one edit that leaves all 4,108 pre-existing rows byte-identical.
    # No kinematic twin exists for any of the three - the client's 33 createAsKinematic="1"
    # actors are all Common_Props_Doors_* / washroom doors and none is a hospital leaf - and
    # since docs/85 s2a / D77 the collision lever is the spawn record's +0x1b1 bit 4 rather
    # than the model, so the visual mesh is the correct and complete choice here.
    ("HospitalSingle", "Hospital_Door01_Placer.adr",
     "Hospital_Door01.adr", "SFX_Door_Office_Open", 25, None),
    ("HospitalDouble", "Hospital_DoorDouble01_Placer.adr",
     "Hospital_DoorDouble01.adr", "SFX_Door_Metal_Business_Open", 10, None),
    ("HospitalDoubleMetal", "Hospital_DoorDoubleMetal01_Placer.adr",
     "Hospital_DoorDoubleMetal01.adr", "SFX_Door_Metal_Business_Open", 4, None),
]

#: The families whose actor holds TWO leaves. The client's swing rotates the whole actor about
#: one pivot (AUDIT-doors s3), so a two-leaf actor cannot look right opened; the runtime keeps
#: them behind their own switch (ZoneOptions.SpawnHospitalDoubleDoors). Named here so the two
#: sides agree on which families they are.
DOUBLE_LEAF_KINDS = ("HospitalDouble", "HospitalDoubleMetal")

EXPECTED_TOTAL = 4147
EXPECTED_PLAYABLE = 4147

DEFAULT_OBJECTS = Path(r"C:\Aug2017\out\world_aug\z2-objects.jsonl")
DEFAULT_MODELS = Path(r"C:\Aug2017\out\data_aug\Models.txt")
DEFAULT_AREAS = Path(r"C:\Aug2017\out\world_aug\Z2Areas.xml")
DEFAULT_DOORS = Path(r"C:\Aug2017\out\overhaul-20260901\unextracted\Doors.txt")
DEFAULT_EFFECTS = Path(r"C:\Aug2017\out\data_aug\derived\effects.json")
DEFAULT_OUT = (
    Path(__file__).resolve().parent.parent.parent
    / "src" / "Cranberry.Zone" / "Data" / "Doors" / "z2-doors.bin"
)
DEFAULT_OUT_CS = (
    Path(__file__).resolve().parent.parent.parent
    / "src" / "Cranberry.Zone" / "Generated" / "AugustDoorTable.g.cs"
)

#: D2/D7. The client's own door table is 17 rows; a change in the count is a client change.
EXPECTED_DOOR_ROWS = 17


def load_models(path: Path) -> dict[str, int]:
    """MODEL_FILE_NAME -> lowest numeric model id (tools/pipeline/readers/sheet.py)."""
    ids: dict[str, int] = {}
    for row in read_rows(path):
        if not row["ID"].isdigit():
            continue
        name = row["MODEL_FILE_NAME"]
        model_id = int(row["ID"])
        if name not in ids or model_id < ids[name]:
            ids[name] = model_id
    return ids


def load_door_rows(path: Path) -> list[dict[str, int]]:
    """D7: the client's own ``Doors.txt`` - ``ID^EFFECT_ON_OPEN^EFFECT_ON_CLOSE^TRANSITION_TIME_MS``."""
    rows = []
    for row in read_rows(path):
        if not row["ID"].isdigit():
            continue
        rows.append({
            "id": int(row["ID"]),
            "openEffect": int(row["EFFECT_ON_OPEN"]),
            "closeEffect": int(row["EFFECT_ON_CLOSE"]),
            "transitionMs": int(row["TRANSITION_TIME_MS"]),
        })
    rows.sort(key=lambda r: r["id"])
    if len(rows) != EXPECTED_DOOR_ROWS:
        raise SystemExit(
            f"D7 broken: {path} has {len(rows)} rows, expected {EXPECTED_DOOR_ROWS}")
    if len({r["id"] for r in rows}) != len(rows):
        raise SystemExit(f"D7 broken: {path} repeats a row id")
    return rows


def load_effect_names(path: Path) -> dict[int, str]:
    """Composite-effect id -> name, out of tools/data/derive_effects.py's document."""
    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("schema") != "cranberry.derived.effects/1":
        raise SystemExit(f"{path}: unexpected schema {document.get('schema')!r}")
    return {row["id"]: row["name"] for row in document["effects"]}


def resolve_door_row(sound: str, door_rows: list[dict[str, int]],
                     effect_names: dict[int, str]) -> dict[str, int]:
    """D7: the lowest Doors.txt row whose EFFECT_ON_OPEN is the named composite effect."""
    effect_ids = [i for i, name in effect_names.items() if name == sound]
    if not effect_ids:
        raise SystemExit(
            f"D7 broken: ActorCompositeEffectDefinitions.xml has no effect named {sound!r}")
    if len(effect_ids) > 1:
        raise SystemExit(f"D7 broken: {sound!r} is not a unique effect name")
    effect_id = effect_ids[0]

    matching = [row for row in door_rows if row["openEffect"] == effect_id]
    if not matching:
        raise SystemExit(
            f"D7 broken: no Doors.txt row plays {sound!r} ({effect_id}) on open")
    row = matching[0]          # door_rows is sorted by id, so this is the lowest

    close_name = effect_names.get(row["closeEffect"])
    if close_name is None:
        raise SystemExit(
            f"D7 broken: Doors.txt row {row['id']} closes with effect {row['closeEffect']}, "
            "which the composite-effect table does not define")
    if not sound.lower().endswith("_open") or not close_name.lower().endswith("_close"):
        raise SystemExit(
            f"D7 broken: Doors.txt row {row['id']} pairs {sound!r} with {close_name!r}, which is "
            "not an Open/Close pair")
    if sound[:-len("_Open")].lower() != close_name[:-len("_Close")].lower():
        raise SystemExit(
            f"D7 broken: Doors.txt row {row['id']} pairs {sound!r} with {close_name!r} - "
            "different sound families")
    return row


def escape_cs(text: str) -> str:
    return text.replace("\\", "\\\\").replace("\"", "\\\"")


def write_door_table_cs(destination: Path, door_rows: list[dict[str, int]],
                        effect_names: dict[int, str],
                        resolved: list[tuple[str, str, str, dict[str, int], str | None]]) -> None:
    """``AugustDoorTable.g.cs`` - the client's 17 rows, and the row each family resolves to."""
    lines = [
        "// <auto-generated>",
        "// Generated by tools/data/gen-doors.py from the August client's own Doors.txt",
        "// (out/overhaul-20260901/unextracted/Doors.txt, 17 rows) joined to",
        "// out/data_aug/derived/effects.json. Do not edit. The provenance header above is",
        "// written by tools/pipeline/pipeline.py (docs/96).",
        "// </auto-generated>",
        "",
        "namespace Cranberry.Zone.Generated;",
        "",
        "/// <summary>One row of the client's <c>Doors.txt</c>.</summary>",
        "/// <param name=\"Id\">The row id, which is what <c>AddLightweightNpc +0x19c</c> carries.</param>",
        "/// <param name=\"OpenEffectId\">Composite effect played on the open swing.</param>",
        "/// <param name=\"CloseEffectId\">Composite effect played on the close swing.</param>",
        "/// <param name=\"OpenEffectName\">That effect's name in the client's own table.</param>",
        "/// <param name=\"CloseEffectName\">That effect's name in the client's own table.</param>",
        "/// <param name=\"TransitionMs\">The row's <c>TRANSITION_TIME_MS</c>.</param>",
        "public readonly record struct AugustDoorRow(",
        "    uint Id, uint OpenEffectId, uint CloseEffectId,",
        "    string OpenEffectName, string CloseEffectName, uint TransitionMs);",
        "",
        "/// <summary>Which <c>Doors.txt</c> row one Z2 door family resolves to, and why.</summary>",
        "/// <param name=\"Kind\">The proxy family name, as it appears in <c>z2-doors.bin</c>'s string table.</param>",
        "/// <param name=\"MeshName\">The <c>Models.txt</c> door mesh spawned at the proxy.</param>",
        "/// <param name=\"SoundName\">The composite effect this family's open swing should play (Cranberry's choice, C1).</param>",
        "/// <param name=\"RowId\">The <c>Doors.txt</c> row that plays it - resolved, never typed (D7).</param>",
        "public readonly record struct AugustDoorKind(",
        "    string Kind, string MeshName, string SoundName, uint RowId);",
        "",
        "/// <summary>",
        "/// The August client's own door table, and the row each Z2 door family resolves to.",
        "/// <para>",
        "/// <b>Why this exists (S8 defect T4, overhaul plan lane 2B).</b> <c>gen-doors.py</c> used to",
        "/// type the <c>Doors.txt</c> row id per family as a number, which is the one part of",
        "/// <c>z2-doors.bin</c> that did not come out of the client. <c>Doors.txt</c> is now extracted",
        "/// and read: each family names the swing SOUND it wants and the row id is looked up through",
        "/// the composite-effect table. <c>z2-doors.bin</c> is byte-identical across that change.",
        "/// </para>",
        "/// <para>",
        "/// <c>Doors.txt</c> is a SOUND table and nothing else (docs/42 s6c): the row selects the",
        "/// open/close effect, an id with no row plays no sound, and any id &gt; 0 makes a working door.",
        "/// </para>",
        "/// </summary>",
        "public static class AugustDoorTable",
        "{",
        f"    /// <summary>How many rows the client's <c>Doors.txt</c> has: {len(door_rows)}.</summary>",
        f"    public const int RowCount = {len(door_rows)};",
        "",
        "    /// <summary>Every row of the client's <c>Doors.txt</c>, ascending by id.</summary>",
        "    public static readonly IReadOnlyList<AugustDoorRow> Rows =",
        "    [",
    ]
    for row in door_rows:
        lines.append(
            f"        new({row['id']}u, {row['openEffect']}u, {row['closeEffect']}u, "
            f"\"{escape_cs(effect_names[row['openEffect']])}\", "
            f"\"{escape_cs(effect_names[row['closeEffect']])}\", {row['transitionMs']}u),")
    lines += [
        "    ];",
        "",
        "    /// <summary>",
        f"    /// The {len(resolved)} Z2 door families, with the row each one resolved to. The order is",
        "    /// <c>z2-doors.bin</c>'s kind order, so <c>Kinds[i]</c> is the file's kind index <c>i</c>.",
        "    /// </summary>",
        "    public static readonly IReadOnlyList<AugustDoorKind> Kinds =",
        "    [",
    ]
    for kind, mesh, sound, row, _twin in resolved:
        lines.append(
            f"        new(\"{escape_cs(kind)}\", \"{escape_cs(mesh)}\", "
            f"\"{escape_cs(sound)}\", {row['id']}u),")
    lines += [
        "    ];",
        "",
        "    /// <summary>",
        "    /// The families whose marker actor places a TWO-LEAF door mesh. The client's swing",
        "    /// rotates the whole actor about one pivot (<c>FUN_14149dd10</c>), so a two-leaf actor",
        "    /// cannot look right opened; the runtime keeps them behind their own switch.",
        "    /// </summary>",
        "    public static readonly IReadOnlyList<string> DoubleLeafKinds =",
        "    [",
    ]
    for kind in DOUBLE_LEAF_KINDS:
        lines.append(f"        \"{escape_cs(kind)}\",")
    lines += [
        "    ];",
        "",
        "    /// <summary>The <c>Doors.txt</c> row with that id, or null when the client has none.</summary>",
        "    public static AugustDoorRow? Row(uint id)",
        "    {",
        "        foreach (AugustDoorRow row in Rows)",
        "        {",
        "            if (row.Id == id)",
        "            {",
        "                return row;",
        "            }",
        "        }",
        "",
        "        return null;",
        "    }",
        "",
        "    /// <summary>The row a door family resolves to, or null when the family is unknown.</summary>",
        "    public static uint? RowIdFor(string kind)",
        "    {",
        "        foreach (AugustDoorKind row in Kinds)",
        "        {",
        "            if (string.Equals(row.Kind, kind, StringComparison.Ordinal))",
        "            {",
        "                return row.RowId;",
        "            }",
        "        }",
        "",
        "        return null;",
        "    }",
        "}",
        "",
    ]
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text("\n".join(lines), encoding="utf-8", newline="\n")


def axis_index(metres: float, origin: float) -> int:
    if not math.isfinite(metres):
        return DIMENSION // 2
    return max(0, min(DIMENSION - 1, int(math.floor((metres - origin) / CELL_METRES))))


def cell_of(x: float, z: float) -> int:
    return axis_index(z, ORIGIN_Z) * DIMENSION + axis_index(x, ORIGIN_X)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--objects", type=Path, default=DEFAULT_OBJECTS)
    p.add_argument("--models", type=Path, default=DEFAULT_MODELS)
    p.add_argument("--areas", type=Path, default=DEFAULT_AREAS,
                   help="Z2Areas.xml; omit or point at a missing file to skip POI labels")
    p.add_argument("--area-prefix", action="append", default=None,
                   help="area-name prefixes to label with, most meaningful first "
                        "(default: Loot. then GasWeightArea.)")
    p.add_argument("--doors", type=Path, default=DEFAULT_DOORS,
                   help="D7: the client's own Doors.txt (17 rows)")
    p.add_argument("--effects", type=Path, default=DEFAULT_EFFECTS,
                   help="D7: derive_effects.py's composite-effect document")
    p.add_argument("--max-height", type=float, default=600.0,
                   help="D2: drop markers above this Y. 600 drops NOTHING; the 400 this "
                        "defaulted to until 2026-09-03 dropped the five LOBBY doors the "
                        "friend's own server spawns (docs/114 gap 1)")
    p.add_argument("--out", type=Path, default=DEFAULT_OUT)
    p.add_argument("--out-cs", type=Path, default=DEFAULT_OUT_CS,
                   help="AugustDoorTable.g.cs")
    p.add_argument("--check", action="store_true",
                   help="verify the census assertions and write nothing")
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    models = load_models(args.models)
    door_rows = load_door_rows(args.doors)
    effect_names = load_effect_names(args.effects)
    kind_names = [k[0] for k in KINDS]
    kind_index = {marker: i for i, (_n, marker, *_rest) in enumerate(KINDS)}
    # D1: the fast substring filter over the 501,987-line world file. One marker name per
    # family, not one prefix, because the hospital placers are not in the DPO naming family.
    markers = tuple(marker[:-len(".adr")] for _n, marker, *_rest in KINDS)

    # D7: the row id per family, resolved out of Doors.txt + the composite-effect table.
    resolved: list[tuple[str, str, str, dict[str, int], str | None]] = [
        (name, mesh, sound, resolve_door_row(sound, door_rows, effect_names), twin)
        for name, _marker, mesh, sound, _expected, twin in KINDS
    ]

    kind_rows: list[tuple[int, int, int, int]] = []
    for (name, marker, mesh, _sound, _expected, twin), (_n, _m, _s, row, _t) in zip(
            KINDS, resolved, strict=True):
        door_id = row["id"]
        proxy_model = marker
        if proxy_model not in models:
            raise SystemExit(f"Models.txt has no row named {proxy_model}")
        if mesh not in models:
            raise SystemExit(f"Models.txt has no row named {mesh}")
        if twin is not None and twin not in models:
            raise SystemExit(f"Models.txt has no row named {twin} (collision twin for {name})")
        # D6: 0 means "no usable kinematic twin"; the runtime then spawns the visual mesh and
        # that family stays walk-through until the client's own actor set gains one.
        kind_rows.append((models[proxy_model], models[mesh], door_id,
                          models[twin] if twin is not None else 0))

    area_index = None
    if args.areas and args.areas.exists():
        import zonespawners  # noqa: PLC0415 - optional, and only on the derivation host
        prefixes = args.area_prefix or ["Loot.", "GasWeightArea."]
        area_index = zonespawners.AreaIndex(args.areas, prefixes)

    total = 0
    dropped_height = 0
    flattened = 0
    seen_ids: set[int] = set()
    per_kind = [0] * len(KINDS)
    doors: list[tuple[float, float, float, float, int, int, int]] = []
    area_names: list[str] = []
    area_lookup: dict[str, int] = {}

    with args.objects.open(encoding="utf-8") as handle:
        for line in handle:
            if not any(marker in line for marker in markers):
                # 501,987 lines; the substring test skips 99.2 % of them without a JSON parse.
                continue
            obj = json.loads(line)
            index = kind_index.get(obj["model"])
            if index is None:
                # A marker substring can match an actor that is not itself a marker (the
                # Hospital_Door01 MESH placements contain "Hospital_Door01"). Those are
                # ordinary static geometry, disjoint from the markers by D1, and are skipped.
                continue
            total += 1
            if obj["id"] in seen_ids:
                raise SystemExit(f"D3 broken: instance id {obj['id']} appears twice")
            seen_ids.add(obj["id"])

            x, y, z = obj["pos"]
            if y > args.max_height:
                dropped_height += 1
                continue

            rot = obj["rot"]
            if abs(rot[1]) > 1e-4 or abs(rot[2]) > 1e-4:
                flattened += 1

            area = NO_AREA
            if area_index is not None:
                name = area_index.find(x, y, z)
                if name is not None:
                    if name not in area_lookup:
                        area_lookup[name] = len(area_names)
                        area_names.append(name)
                    area = area_lookup[name]

            per_kind[index] += 1
            doors.append((x, y, z, rot[0], obj["id"] & 0xFFFFFFFF, area, index))

    print(f"{total:,} door markers, {dropped_height} above y={args.max_height:g} dropped, "
          f"{len(doors):,} playable, {flattened} with a non-zero pitch/roll flattened to yaw")
    collidable = 0
    for ((name, _marker, mesh, sound, expected, twin), (_n, _m, _s, row, _t), count,
         (proxy_model, mesh_id, door_id, twin_id)) in zip(
            KINDS, resolved, per_kind, kind_rows, strict=True):
        placed = expected
        flag = "" if count == placed else f"  ** expected {placed} **"
        if twin_id:
            collidable += count
        print(f"  {name:<18} proxy {proxy_model:>6} -> model {mesh_id:>6} {mesh:<32} "
              f"Doors.txt {door_id:>2}  {count:>5}{flag}")
        print(f"  {'':<18} sound {sound} -> row {row['id']} "
              f"({row['openEffect']}/{row['closeEffect']}, {row['transitionMs']} ms)")
        print(f"  {'':<18} collision twin {twin_id:>6} "
              f"{twin if twin is not None else '(none - this family stays walk-through)'}")
    print(f"  {collidable:,} of {len(doors):,} doors "
          f"({100.0 * collidable / max(len(doors), 1):.1f} %) have a kinematic collision twin")

    if total != EXPECTED_TOTAL:
        raise SystemExit(f"D2 broken: {total} markers, expected {EXPECTED_TOTAL}")
    if len(doors) != EXPECTED_PLAYABLE:
        raise SystemExit(f"D2 broken: {len(doors)} playable doors, expected {EXPECTED_PLAYABLE}")

    if args.check:
        print("--check: assertions hold, nothing written")
        return 0

    if len(area_names) > NO_AREA:
        raise SystemExit(f"{len(area_names)} named areas does not fit a u16 index")

    doors.sort(key=lambda d: (cell_of(d[0], d[2]), d[4]))

    counts = [0] * (DIMENSION * DIMENSION)
    for door in doors:
        counts[cell_of(door[0], door[2])] += 1
    offsets = [0] * (DIMENSION * DIMENSION + 1)
    running = 0
    for i, count in enumerate(counts):
        offsets[i] = running
        running += count
    offsets[-1] = running

    strings = b"".join(name.encode("utf-8") + b"\0" for name in kind_names + area_names)

    xs = [d[0] for d in doors]
    ys = [d[1] for d in doors]
    zs = [d[2] for d in doors]

    header = struct.pack(
        "<4siiiifffii6f",
        MAGIC, VERSION, len(doors), len(KINDS), DIMENSION, CELL_METRES,
        ORIGIN_X, ORIGIN_Z, len(area_names), len(strings),
        min(xs), min(ys), min(zs), max(xs), max(ys), max(zs))
    assert len(header) == HEADER_BYTES, len(header)

    body = bytearray()
    for proxy_model, mesh_id, door_id, twin_id in kind_rows:
        body += struct.pack("<IIII", proxy_model, mesh_id, door_id, twin_id)
    body += struct.pack(f"<{len(offsets)}i", *offsets)
    for x, y, z, yaw, instance, area, index in doors:
        body += struct.pack("<ffffIHBB", x, y, z, yaw, instance, area, index, 0)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    payload = header + strings + bytes(body)
    args.out.write_bytes(payload)
    print(f"wrote {args.out} ({len(payload):,} bytes, {len(strings)}-byte string table, "
          f"{len(area_names)} named areas)")

    write_door_table_cs(args.out_cs, door_rows, effect_names, resolved)
    print(f"wrote {args.out_cs} ({len(door_rows)} Doors.txt rows, {len(resolved)} families)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
