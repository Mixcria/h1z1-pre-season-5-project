#!/usr/bin/env python3
r"""derive_effects - the client's own composite-effect table as a derived document.

INPUT
    ``C:\Aug2017\out\data_aug\ActorCompositeEffectDefinitions.xml``   412,028 bytes,
        1,047 ``<EffectDefinition>`` elements wrapping 2,070 ``<Effect>`` children.
        Extracted from the August client's own packs on 2026-08-29 and declared in
        ``tools/pipeline/inputs.json`` since lane 2A; never read by anything until now
        (docs/96 section 8, S8 section 6.4).

OUTPUT
    ``C:\Aug2017\out\data_aug\derived\effects.json``

WHY (S8 section 6.4, overhaul plan section 3 lane 2B)

    ``CompositeEffectGate`` was written as a DENY list on the explicit grounds that "this build
    ships no composite-effect datasheet - the 2017 extraction has no ``Actor*Definitions.xml``
    at all". That remark is wrong: the file has been sitting in ``out\data_aug`` since
    2026-08-29. With the table in hand the gate can be what it should have been - an ALLOW
    list over the ids this client can actually resolve - and every effect id the server ever
    chooses to send (door swings via ``Doors.txt``, melee impacts, the parachute flare) can be
    looked up by the client's own NAME instead of being typed as a number.

WHAT IS DERIVED

  E1  The 1,047 definitions, each with ``id``, ``name``, ``loadType``, ``effectCount`` and the
      two optional time-of-day / lifetime attribute pairs the file carries on 21 of them.
      Ids are unique (1,047 distinct over the range 1..6,025) and so are names, so both are
      usable as keys. **Id 0 does not exist in the table** - which is the whole point of
      S8 section 6: the client's own "missing effect definition for Id #0" line is the table
      telling the truth about a row (``Vehicles.txt`` row 13) whose effect columns are 0.
  E2  The 2,070 ``<Effect>`` children per definition: ``effectType`` (FX 872, SOUND 661,
      DECAL 310, CAMERA 65, MATERIALTYPETRIGGER 60, LIGHT 46, PARTICLE 43, MESH 12,
      MODELMATERIAL 1), ``effectId`` into the per-type leaf table, ``triggerName`` and
      ``contextScope``. Carried so a later lane can answer "is this id a sound or a particle"
      without re-parsing the XML.

DETERMINISM
    This document carries no clock. ``pipeline.py check`` hashes it as an input of two
    generators, and a wall-clock ``generatedUtc`` would mark them stale on every re-derive for
    no reason (docs/96 section 8, the timestamp defect). The identity of the run is the input's
    own sha256, recorded in ``source``.

USAGE
    python tools/data/derive_effects.py --out C:\Aug2017\out\data_aug\derived\effects.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

SCHEMA = "cranberry.derived.effects/1"
CLIENT_BUILD = "0.0.118.208059"

DEFAULT_XML = Path(r"C:\Aug2017\out\data_aug\ActorCompositeEffectDefinitions.xml")
DEFAULT_OUT = Path(r"C:\Aug2017\out\data_aug\derived\effects.json")

#: E1/E2. Where every field in this document comes from, in the same shape the other five
#: derive_*.py documents use: ``Field -> File.ELEMENT@attribute``.
PROVENANCE = {
    "effects[].id": "ActorCompositeEffectDefinitions.xml.EffectDefinition@id",
    "effects[].name": "ActorCompositeEffectDefinitions.xml.EffectDefinition@name",
    "effects[].loadType": "ActorCompositeEffectDefinitions.xml.EffectDefinition@loadType",
    "effects[].effectCount": "ActorCompositeEffectDefinitions.xml.EffectDefinition@effectCount",
    "effects[].startTimeOfDay": "ActorCompositeEffectDefinitions.xml.EffectDefinition@startTimeOfDay",
    "effects[].stopTimeOfDay": "ActorCompositeEffectDefinitions.xml.EffectDefinition@stopTimeOfDay",
    "effects[].minLifeTime": "ActorCompositeEffectDefinitions.xml.EffectDefinition@minLifeTime",
    "effects[].defaultLifeTime": "ActorCompositeEffectDefinitions.xml.EffectDefinition@defaultLifeTime",
    "effects[].parts[].effectType": "ActorCompositeEffectDefinitions.xml.EffectDefinition.Effect@effectType",
    "effects[].parts[].effectId": "ActorCompositeEffectDefinitions.xml.EffectDefinition.Effect@effectId",
    "effects[].parts[].triggerName": "ActorCompositeEffectDefinitions.xml.EffectDefinition.Effect@triggerName",
    "effects[].parts[].contextScope": "ActorCompositeEffectDefinitions.xml.EffectDefinition.Effect@contextScope",
}

#: What this build's table cannot tell us, stated rather than implied.
SERVER_SIDE_GAPS = [
    "Which composite-effect id belongs at a given SEND SITE is not in this table. The table "
    "says which ids EXIST and what each one is made of; choosing one for a door, a melee hit "
    "or a flare is a server decision that must cite its own evidence (Doors.txt for doors, "
    "FireModes.MELEE_COMPOSITE_EFFECT_ID for melee).",
    "The leaf tables the parts index (ActorFxDefinitions.xml, ActorSoundEmitterDefinitions.xml "
    "and the other six) are extracted but not parsed here; parts[].effectId is carried as the "
    "raw index into them.",
    "loadType has no recovered meaning; it is carried verbatim (0 on 1,046 of 1,047 rows).",
]


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def optional_float(element: ET.Element, name: str) -> float | None:
    raw = element.get(name)
    return None if raw is None else float(raw)


def derive(xml_path: Path) -> dict:
    root = ET.parse(xml_path).getroot()
    if root.tag != "Definitions":
        raise SystemExit(f"{xml_path}: expected a <Definitions> root, got <{root.tag}>")

    effects: list[dict] = []
    type_counts: Counter[str] = Counter()
    for definition in root.findall("EffectDefinition"):
        identifier = definition.get("id")
        name = definition.get("name")
        if identifier is None or name is None:
            raise SystemExit(f"{xml_path}: an <EffectDefinition> has no id/name: {definition.attrib}")

        parts = []
        for part in definition.findall("Effect"):
            effect_type = part.get("effectType", "")
            type_counts[effect_type] += 1
            parts.append({
                "effectType": effect_type,
                "effectId": int(part.get("effectId", "0")),
                "triggerName": part.get("triggerName", ""),
                "contextScope": part.get("contextScope", ""),
            })

        row = {
            "id": int(identifier),
            "name": name,
            "loadType": int(definition.get("loadType", "0")),
            "effectCount": int(definition.get("effectCount", "0")),
            "parts": parts,
        }
        for optional in ("startTimeOfDay", "stopTimeOfDay", "minLifeTime", "defaultLifeTime"):
            value = optional_float(definition, optional)
            if value is not None:
                row[optional] = value
        effects.append(row)

    effects.sort(key=lambda row: row["id"])

    ids = [row["id"] for row in effects]
    names = [row["name"] for row in effects]
    if len(set(ids)) != len(ids):
        duplicated = sorted({i for i in ids if ids.count(i) > 1})
        raise SystemExit(f"E1 broken: duplicate effect ids {duplicated[:10]}")
    if len(set(names)) != len(names):
        raise SystemExit("E1 broken: duplicate effect names")
    if 0 in set(ids):
        raise SystemExit(
            "E1 broken: this table now defines id 0. S8 section 6 and D100 both rest on 0 being "
            "the no-effect sentinel and never a legal id; the gate's allow list must be revisited.")

    declared = sum(row["effectCount"] for row in effects)
    actual = sum(len(row["parts"]) for row in effects)

    return {
        "schema": SCHEMA,
        "clientBuild": CLIENT_BUILD,
        "note": "The August client's own composite-effect table. No clock: the identity of this "
                "document is source.sha256 (docs/96 section 8).",
        "source": {
            "path": "out/data_aug/ActorCompositeEffectDefinitions.xml",
            "sha256": sha256_of(xml_path),
            "size": xml_path.stat().st_size,
            "grade": "CLIENT",
        },
        "counts": {
            "definitions": len(effects),
            "parts": actual,
            "declaredEffectCountTotal": declared,
            "minId": min(ids),
            "maxId": max(ids),
            "partsByType": dict(sorted(type_counts.items())),
        },
        "provenance": PROVENANCE,
        "serverSideGaps": SERVER_SIDE_GAPS,
        "effects": effects,
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--xml", type=Path, default=DEFAULT_XML)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    document = derive(args.xml)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(document, indent=2, ensure_ascii=False, sort_keys=False) + "\n",
        encoding="utf-8", newline="\n")
    counts = document["counts"]
    print(f"[derive_effects] {counts['definitions']:,} composite effects "
          f"(ids {counts['minId']}..{counts['maxId']}), {counts['parts']:,} effect parts "
          f"-> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
