#!/usr/bin/env python3
r"""
derive_stats.py - build Cranberry's server-ready ``stats`` dataset from the August
client's own datasheets.

    INPUT   C:\Aug2017\out\data_aug\*.txt / *.xml   (extracted from the client's
            Assets_*.pack files by tools/pack/packread.py; datasheet dialect per
            tools/data/sheet.py)
            C:\Aug2017\Client\Locale\en_us_data.dat + .dir  (via tools/locale/localedat.py)
    OUTPUT  C:\Aug2017\out\data_aug\derived\stats.json   (one document, Cranberry's own
            schema ``cranberry.stats/1`` - described in full below and echoed into the
            document's own ``schema`` / ``fieldProvenance`` blocks)

The "stats" system for Cranberry means: the vital resources a character carries
(health / stamina / toxicity / bleeding / food / water / ...), the tier ladders that turn
a resource value into a severity band, the character stat-id table that rides the
character-state packets, the screen and camera effects the server triggers by id, and the
scoring / leaderboard / experience tables.  Everything the client genuinely does not carry
is listed in ``serverSideGaps`` rather than guessed at.

=====================================================================================
SCHEMA  ``cranberry.stats/1``
=====================================================================================

Top level::

    schema            "cranberry.stats/1"
    generator         relative path of this script
    generatedUtc      ISO-8601 UTC timestamp of the run
    client            { build, locale }
    sources[]         { sheet, path, bytes, sha256, rows }  - every input, hashed
    fieldProvenance   { "<dotted output path>": "<Sheet>.<COLUMN>" | "<derivation>" }
    wire              opcode families this dataset feeds, from the client's own
                      registration table (out/registrations-1148.json)
    resourceTypes[]   { type, name, resourceIds[] }   - the wire enum
    resources[]       one entry per Resources.txt row, typed, named, tiers nested
    playerVitals      the named picks a KOTK match needs (health/stamina/toxicity/bleed
                      and the survival meters), each with the alternates it was picked from
    characterStats[]  CharacterStatDefinitions joined to the StatId.* name table
    screenEffects[]   ScreenEffects.txt, typed and grouped by TYPE
    cameraEffects[]   ActorCameraEffectDefinitions.xml
    indirectDamage[]  the ClientEffects rows that carry a real damage-over-time window
    scoring           { playerStats[], statManagers[], gameModes[], experienceAwards[],
                        experienceAwardTypes[] }
    profileResourceSets[]  ProfileResourceMappings joined to ProfileTypes, with the
                      dangling resource ids called out
    tierAbilities     the ResourceTiers -> AbilitySets -> AbilityEx expansion, so the
                      tier -> damage-over-time chain (and what it does *not* carry) is
                      inspectable
    localeResolution  { lang, resolved, unresolvedStringIds[], unresolvedByColumn, mapping }
    statIdNameTable   { "<stat id>": ["<StatId name>", ...] } from StringHashToValue
    serverSideGaps[]  { id, area, need, evidence, decision }
    counts            row counts for every section, for a quick integrity check

A ``resources[]`` entry::

    id                Resources.ID                  (the definition the server instantiates)
    type              Resources.RESOURCE_TYPE       (the enum that goes on the wire)
    typeName          Resources.TYPE_NAME           ("" on rows the client never named)
    name/description  NAME_ID/DESCRIPTION_ID resolved through the locale (null when 0)
    initialValue,maxValue
    regen             { perMs, delayMs, damageInterruptMs, tickMs, initiallyDisabled }
    burn              { perMs, tickMs, initiallyDisabled }
    tierTickMs
    broadcastRangeM   Resources.PACKET_BROADCAST_RANGE (0 = self only)
    flags             { onClient, doNotTick, doNotPersist, profileScope, notVehMountScope }
    valueMarkers      { lo, med, hi }   - 0 on all 155 rows in this build
    abilities         { activated, depleted }  ids into AbilityEx.txt
    compositeEffects  { activate, terminate, depleted }  ids into
                      ActorCompositeEffectDefinitions.xml (not extracted; ids only)
    tiers[]           { valueMin, activeAbilityId, activeAbilitySetId, activateCompEffectId,
                        terminateCompEffectId, activeCompEffectId, requirementId,
                        clientRequirementId, messageId, message, messagePeriodSec, uiData }
                      sorted ascending by valueMin; a value V selects the last tier whose
                      valueMin <= V.

=====================================================================================
FORMAT FACTS RELIED ON  (each with its citation)
=====================================================================================

1.  Datasheet dialect - ``^``-delimited, ``#`` header line, ``*`` key marker, a trailing
    ``^`` on every line, no quoting, no escaping, 7-bit ASCII.  Derived and verified in
    ``tools/data/sheet.py`` and ``Server/docs/28-locale-and-datasheets.md`` §1 against all
    36 sheets then on disk.  This script uses ``sheet.Sheet`` so it inherits that.

2.  ``ProfileResourceMappings.txt`` is the one sheet in this set with **no** trailing ``^``
    on any line.  ``sheet.Sheet`` drops a trailing empty field only when one is present, so
    it reads correctly; a naive ``split('^')[:-1]`` would drop RESOURCE_ID.  Observed on the
    extracted file (see the ``sources`` hashes in the output).

3.  ``NAME_ID`` / ``DESCRIPTION_ID`` / ``MESSAGE_ID`` are indices into the locale archive
    through ``locale_key(n) = jenkins_lookup2(ascii("Global.Text." + str(n)), initval=0)``.
    Derived in ``Server/docs/28-locale-and-datasheets.md`` §3; the format string
    ``Global.Text.%d`` is a literal in H1Z1.exe 0.0.118.208059 at VA ``0x143118638``.
    A string id of ``0`` means "unset", not "locale key 0".

4.  The resource wire enum is ``Resources.RESOURCE_TYPE``.  It is the value carried by the
    ``cPacketIdResourceEventBase`` family, top-level id ``0x8d``, sub-ids
    1 ``Resources::cResourceEventPacketIdSetCharacterResources``,
    2 ``…SetCharacterResource``, 3 ``…UpdateCharacterResource``,
    4 ``…RemoveCharacterResource`` - from this project's own registration dump
    ``C:\Aug2017\out\registrations-1148.json`` (registered in ``FUN_1413bfa90``).
    ``0xc7 cPacketIdResourcesBase`` is the second, sub-id-less resource family.

5.  Screen effects are addressed by ``ScreenEffects.ID`` on ``0xe1
    cPacketIdScreenEffectBase`` (top-level only, no sub-ids in the registration table).

6.  Experience rides ``0x87 cPacketIdExperienceBase``, sub-ids 1 ``SetExperience``,
    2 ``SetExperienceRanks``, 3 ``SetExperienceRateTier``, 4 ``DeathReport``
    (same registration dump).

7.  Character stat ids are the values of the ``StatId.*`` rows in
    ``StringHashToValue.txt``.  That sheet is the client's own name->value table
    (``tools/data/gen-string-hash-values.py``).  Exactly one id is ambiguous in this build
    - id 1 is both ``StatId.MaxHp`` and ``StatId.AudioAmpMultiplier`` - so the schema
    carries ``nameCandidates`` and leaves ``name`` null rather than choosing.

8.  ``ActorCameraEffectDefinitions.xml`` is a flat ``<Definitions><EffectDefinition …/>``
    document with all values in attributes; seconds for the time fields, metres for radius
    and falloff (Forgelight actor-effect XML, read as-is from the file).

9.  ``ScreenEffects`` durations/fades are milliseconds (``DURATION`` 8500 with
    ``FADE_OUT`` 4000 on id 1); colours are hex RGB or ARGB strings, empty meaning unset.
    ``ResourceTiers.MESSAGE_PERIOD_SEC`` is seconds.  Both read directly off the sheets.

Nothing here is copied from, compared against, or calibrated by any other server project.

=====================================================================================
Usage
=====================================================================================

    python tools/data/derive_stats.py
    python tools/data/derive_stats.py --lang de_de --out C:\Aug2017\out\data_aug\derived\stats-de.json
    python tools/data/derive_stats.py --data-dir C:\Aug2017\out\data_aug --summary

``--summary`` prints the row counts and the serverSideGaps list to stderr after writing.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Iterable

_TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_TOOLS / "data"))
sys.path.insert(0, str(_TOOLS / "locale"))

from localedat import LocaleData  # noqa: E402
from sheet import Sheet  # noqa: E402

SCHEMA = "cranberry.stats/1"
CLIENT_BUILD = "0.0.118.208059"

DEFAULT_DATA_DIR = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = DEFAULT_DATA_DIR / "derived" / "stats.json"

#: Sheets this derivation reads. Missing ones are reported, never faked.
INPUT_SHEETS = [
    "Resources.txt",
    "ResourceTiers.txt",
    "ProfileResourceMappings.txt",
    "ProfileTypes.txt",
    "CharacterStatDefinitions.txt",
    "StringHashToValue.txt",
    "ScreenEffects.txt",
    "ClientEffects.txt",
    "PlayerStats.txt",
    "PlayerStatManagers.txt",
    "PlayerStatManagerMap.txt",
    "GameModeDefinitions.txt",
    "GameModePlayerStatManagerMap.txt",
    "Experience.txt",
    "AbilitySets.txt",
    "AbilityEx.txt",
    "ActorCameraEffectDefinitions.xml",
]


# ---------------------------------------------------------------------------------
# small typed readers
# ---------------------------------------------------------------------------------


def as_int(raw: str, default: int = 0) -> int:
    raw = (raw or "").strip()
    if not raw:
        return default
    try:
        return int(raw)
    except ValueError:
        try:  # a few columns carry "1E-05"-style floats where an int is expected
            return int(float(raw))
        except ValueError:
            return default


def as_num(raw: str, default: float = 0.0) -> float | int:
    """Return an int when the value is integral, else a float. Handles ``1.9E-05``."""
    raw = (raw or "").strip()
    if not raw:
        return default
    try:
        v = float(raw)
    except ValueError:
        return default
    return int(v) if v.is_integer() and abs(v) < 2**53 else v


def as_bool(raw: str) -> bool:
    return (raw or "").strip() not in ("", "0")


def opt_str(raw: str) -> str | None:
    raw = (raw or "").strip()
    return raw or None


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


class Loc:
    """NAME_ID -> text, with 0 meaning 'unset'. Misses are counted, never invented."""

    def __init__(self, lang: str) -> None:
        self.data = LocaleData(lang)
        self.hits = 0
        self.misses: set[int] = set()

    def text(self, raw: str | int) -> str | None:
        sid = as_int(str(raw))
        if sid == 0:
            return None
        s = self.data.text(sid)
        if s is None:
            self.misses.add(sid)
            return None
        self.hits += 1
        return s


# ---------------------------------------------------------------------------------
# sections
# ---------------------------------------------------------------------------------


def read_resources(data_dir: Path, loc: Loc) -> tuple[list[dict], dict[int, list[dict]]]:
    """Resources.txt joined 1:N to ResourceTiers.txt on RESOURCE_ID."""
    tiers_by_resource: dict[int, list[dict]] = {}
    tiers_path = data_dir / "ResourceTiers.txt"
    if tiers_path.exists():
        for row in Sheet(tiers_path):
            rid = as_int(row["RESOURCE_ID"])
            tiers_by_resource.setdefault(rid, []).append(
                {
                    "valueMin": as_int(row["TIER_VALUE_MIN"]),
                    "name": loc.text(row["NAME_ID"]),
                    "description": loc.text(row["DESCRIPTION_ID"]),
                    "iconId": as_int(row["ICON_ID"]) or None,
                    "requirementId": as_int(row["REQUIREMENT_ID"]) or None,
                    "clientRequirementId": as_int(row["CLIENT_REQUIREMENT_ID"]) or None,
                    "activeAbilityId": as_int(row["ACTIVE_ABILITY_ID"]) or None,
                    "activeAbilitySetId": as_int(row["ACTIVE_ABILITY_SET_ID"]) or None,
                    "activateCompEffectId": as_int(row["ACTIVATE_COMP_EFFECT_ID"]) or None,
                    "terminateCompEffectId": as_int(row["TERMINATE_COMP_EFFECT_ID"]) or None,
                    "activeCompEffectId": as_int(row["ACTIVE_COMP_EFFECT_ID"]) or None,
                    "messageId": as_int(row["MESSAGE_ID"]) or None,
                    "message": loc.text(row["MESSAGE_ID"]),
                    "messagePeriodSec": as_int(row["MESSAGE_PERIOD_SEC"]) or None,
                    "uiData": opt_str(row["UI_DATA"]),
                }
            )
    for lst in tiers_by_resource.values():
        lst.sort(key=lambda t: t["valueMin"])

    resources: list[dict] = []
    for row in Sheet(data_dir / "Resources.txt"):
        rid = as_int(row["ID"])
        resources.append(
            {
                "id": rid,
                "type": as_int(row["RESOURCE_TYPE"]),
                "typeName": opt_str(row["TYPE_NAME"]),
                "codeFactoryName": opt_str(row["CODE_FACTORY_NAME"]),
                "name": loc.text(row["NAME_ID"]),
                "description": loc.text(row["DESCRIPTION_ID"]),
                "nameId": as_int(row["NAME_ID"]) or None,
                "descriptionId": as_int(row["DESCRIPTION_ID"]) or None,
                "initialValue": as_num(row["INITIAL_VALUE"]),
                "maxValue": as_num(row["MAX_VALUE"]),
                "regen": {
                    "perMs": as_num(row["REGEN_PER_MS"]),
                    "delayMs": as_int(row["REGEN_DELAY_MS"]),
                    "damageInterruptMs": as_int(row["REGEN_DAMAGE_INTERRUPT_MS"]),
                    "tickMs": as_int(row["REGEN_TICK_MSEC"]),
                    "initiallyDisabled": as_bool(row["FLAG_INIT_WITH_DISABLED_REGEN"]),
                },
                "burn": {
                    "perMs": as_num(row["BURN_PER_MSEC"]),
                    "tickMs": as_int(row["BURN_TICK_MSEC"]),
                    "initiallyDisabled": as_bool(row["FLAG_INIT_WITH_DISABLED_BURN"]),
                },
                "tierTickMs": as_int(row["TIER_TICK_MSEC"]),
                "broadcastRangeM": as_int(row["PACKET_BROADCAST_RANGE"]),
                "valueMarkers": {
                    "lo": as_num(row["VALUE_MARKER_LO"]),
                    "med": as_num(row["VALUE_MARKER_MED"]),
                    "hi": as_num(row["VALUE_MARKER_HI"]),
                },
                "flags": {
                    "onClient": as_bool(row["FLAG_ON_CLIENT"]),
                    "doNotTick": as_bool(row["FLAG_DONOT_TICK"]),
                    "doNotPersist": as_bool(row["FLAG_DONOT_PERSIST"]),
                    "profileScope": as_bool(row["FLAG_PROFILE_SCOPE"]),
                    "notVehMountScope": as_bool(row["FLAG_NOT_VEH_MOUNT_SCOPE"]),
                },
                "abilities": {
                    "activated": as_int(row["ACTIVATED_ABILITY_ID"]) or None,
                    "depleted": as_int(row["DEPLETED_ABILITY_ID"]) or None,
                },
                "compositeEffects": {
                    "activate": as_int(row["ACTIVATE_COMP_EFFECT_ID"]) or None,
                    "terminate": as_int(row["TERMINATE_COMP_EFFECT_ID"]) or None,
                    "depleted": as_int(row["DEPLETED_COMP_EFFECT_ID"]) or None,
                },
                "tiers": tiers_by_resource.get(rid, []),
            }
        )
    resources.sort(key=lambda r: r["id"])
    return resources, tiers_by_resource


def build_resource_types(resources: list[dict]) -> list[dict]:
    """
    RESOURCE_TYPE is the wire enum. Several rows can share one type (different definitions
    of the same kind of meter); the name comes from whichever rows filled TYPE_NAME in.
    """
    by_type: dict[int, dict] = {}
    for r in resources:
        e = by_type.setdefault(r["type"], {"type": r["type"], "name": None, "resourceIds": []})
        e["resourceIds"].append(r["id"])
        if r["typeName"] and not e["name"]:
            e["name"] = r["typeName"]
    return [by_type[k] for k in sorted(by_type)]


#: Which resource row Cranberry instantiates for a KOTK survivor, and why that row and not
#: another of the same RESOURCE_TYPE. The client does not answer this (see the
#: ``playerResourceSet`` gap), so the pick is Cranberry's, made from the value shape the
#: August KOTK UI expects, and it is recorded here with its reason rather than buried.
#:
#: Each entry is ``(label, resourceId, reason, criterion)``. ``criterion`` is the reason
#: expressed as a predicate over a normalised resource dict, and ``build_player_vitals``
#: EVALUATES it: it emits the full candidate set as ``selectionCandidates`` and fails the
#: build if the chosen id is not in it. That is the point of ``selectionIsCranberryChoice``
#: - a Cranberry choice has to be checkable, and a hand-written "the only ..." is not.
#:
#: An earlier revision asserted uniqueness in prose and got two of nine wrong: health said
#: "the only ... row at the 10000-point scale that also carries PACKET_BROADCAST_RANGE 30
#: and a 1 s tier tick; the other 40-odd health rows are ..." when RESOURCE_TYPE 1 has 77
#: rows (not 40-odd), ALL 77 carry PACKET_BROADCAST_RANGE 30 (so that clause selects
#: nothing), and the stated criterion matches TWO rows, 1 and 571; food said "the only
#: ResourceTypeFood row at the 10000-point scale" when 528 and 609 also carry MAX_VALUE
#: 10000. The values picked were right in both cases; the justification was not.
VITAL_PICKS: list[tuple[str, int, str, Any]] = [
    ("health", 1,
     "ResourceTypeHealth at the 10000-point scale with a 1 s tier tick. Two rows match "
     "(1 and 571); the lowest id wins. PACKET_BROADCAST_RANGE does not discriminate here - "
     "all 77 type-1 rows carry 30 - and the other type-1 rows are vehicles, animals and "
     "test entities",
     lambda r: r["type"] == 1 and r["maxValue"] == 10000 and r["tierTickMs"] == 1000),
    ("stamina", 70,
     "ResourceTypeStamina at the 100-point scale KOTK's bar uses; id 6 is the legacy "
     "600-point row and is the one the tier ladder is written against "
     "(ResourceTiers.RESOURCE_ID 6)",
     lambda r: r["type"] == 6 and r["maxValue"] == 100),
    ("toxicity", 611,
     "the only ResourceToxicity row (type 75); 0 -> 180000 with REGEN_PER_MS 1 and both "
     "burn and regen initially disabled",
     lambda r: r["type"] == 75),
    ("bleeding", 21,
     "the only Bleeding row (type 21) and the only vital with a full 5-band ResourceTiers "
     "ladder",
     lambda r: r["type"] == 21),
    ("food", 4,
     "ResourceTypeFood at the 10000-point scale. Three rows match (4, 528, 609); the "
     "lowest id wins. 528 and 609 differ only in INITIAL_VALUE 4000 and BURN_PER_MSEC "
     "0.001",
     lambda r: r["type"] == 4 and r["maxValue"] == 10000),
    ("water", 5, "the only ResourceTypeWater row", lambda r: r["type"] == 5),
    ("virus", 12, "the only ResourceTypeH1Z1Virus row; carries the 11-band tier ladder",
     lambda r: r["type"] == 12),
    ("comfort", 68, "the only ResourceTypeComfort row", lambda r: r["type"] == 68),
    ("onFire", 64,
     "ResourceTypeOnFire, 0 -> 30, BURN_PER_MSEC 0.001; id 582 is a duplicate, lowest id "
     "wins",
     lambda r: r["type"] == 64),
]


def build_player_vitals(resources: list[dict]) -> dict[str, Any]:
    by_id = {r["id"]: r for r in resources}
    out: dict[str, Any] = {}
    for label, rid, reason, criterion in VITAL_PICKS:
        r = by_id.get(rid)
        if r is None:
            continue
        # Evaluate the stated criterion instead of trusting the sentence. The candidate set
        # ships beside the pick, and a pick the criterion does not select is a build error -
        # if the sheet changes, this must fail loudly rather than keep a stale reason.
        candidates = sorted(o["id"] for o in resources if criterion(o))
        if rid not in candidates:
            raise SystemExit(
                f"derive_stats: playerVitals.{label} picks resource {rid}, but its stated "
                f"criterion selects {candidates}. Fix the pick or the criterion."
            )
        alternates = sorted(o["id"] for o in resources if o["type"] == r["type"] and o["id"] != rid)
        out[label] = {
            "resourceId": rid,
            "type": r["type"],
            "typeName": r["typeName"],
            "name": r["name"],
            "initialValue": r["initialValue"],
            "maxValue": r["maxValue"],
            "regen": r["regen"],
            "burn": r["burn"],
            "tierTickMs": r["tierTickMs"],
            "broadcastRangeM": r["broadcastRangeM"],
            "tierCount": len(r["tiers"]),
            "tierBands": [t["valueMin"] for t in r["tiers"]],
            "selectionReason": reason,
            "selectionIsCranberryChoice": True,
            # Computed from selectionReason's own predicate at build time, not asserted.
            "selectionCandidates": candidates,
            "selectionTiebreak": (
                "lowest id among selectionCandidates" if len(candidates) > 1
                else "criterion selects exactly one row"
            ),
            "sameTypeAlternates": alternates,
            "sameTypeAlternateCount": len(alternates),
        }
    return out


_STATID_RE = re.compile(r"^StatId\.(.+)$")


def read_character_stats(data_dir: Path, loc: Loc) -> tuple[list[dict], dict[int, list[str]]]:
    """CharacterStatDefinitions.ID joined to the StatId.* rows of StringHashToValue.txt."""
    names: dict[int, list[str]] = {}
    hv = data_dir / "StringHashToValue.txt"
    if hv.exists():
        for row in Sheet(hv):
            m = _STATID_RE.match(row["STRING"].strip())
            if m:
                names.setdefault(as_int(row["VALUE"]), []).append(m.group(1))
    for lst in names.values():
        lst.sort()

    stats: list[dict] = []
    for row in Sheet(data_dir / "CharacterStatDefinitions.txt"):
        sid = as_int(row["ID"])
        cand = names.get(sid, [])
        stats.append(
            {
                "id": sid,
                "name": cand[0] if len(cand) == 1 else None,
                "nameCandidates": cand,
                "nameAmbiguous": len(cand) > 1,
                "displayName": loc.text(row["NAME_ID"]),
                "description": loc.text(row["DESCRIPTION_ID"]),
                "imageId": as_int(row["IMAGE_ID"]) or None,
                "sendToRemoteClient": as_bool(row["SEND_TO_REMOTE_CLIENT"]),
            }
        )
    stats.sort(key=lambda s: s["id"])
    return stats, names


#: ScreenEffects.TYPE has no name column. These labels are Cranberry's, read off the rows'
#: own field usage (which columns are non-zero on the members of each group) and recorded
#: as an observation, not as a client string.
SCREEN_EFFECT_KINDS = {
    2: "fade",
    3: "fullScreenTint",
    4: "flash",
    5: "visionMode",
    6: "timedOverlay",
}


def read_screen_effects(data_dir: Path) -> list[dict]:
    out: list[dict] = []
    for row in Sheet(data_dir / "ScreenEffects.txt"):
        kind = as_int(row["TYPE"])
        e: dict[str, Any] = {
            "id": as_int(row["ID"]),
            "type": kind,
            "kind": SCREEN_EFFECT_KINDS.get(kind),
            "durationMs": as_int(row["DURATION"]),
            "fadeInMs": as_int(row["FADE_IN"]),
            "fadeOutMs": as_int(row["FADE_OUT"]),
            "scale": as_num(row["SCALE"]),
            "affects": {
                "driver": as_bool(row["AFFECT_DRIVER"]),
                "passenger": as_bool(row["AFFECT_PASSENGER"]),
                "gunner": as_bool(row["AFFECT_GUNNER"]),
            },
            "frameColor": opt_str(row["FRAME_COLOR"]),
            "effectColor": opt_str(row["EFFECT_COLOR"]),
        }
        if kind == 5 or as_num(row["THERMAL_RANGE"]) or as_num(row["INFRARED_RANGE"]):
            # The THERMAL_*/INFRARED_* block only drives anything on TYPE 5; three TYPE 3
            # tints (16/17/19) carry leftover non-zero values in it, so the block is emitted
            # with a flag rather than dropped or silently trusted.
            e["visionFieldsActive"] = kind == 5
            e["vision"] = {
                "thermalRange": as_num(row["THERMAL_RANGE"]),
                "infraredRange": as_num(row["INFRARED_RANGE"]),
                "thermalFade": as_num(row["THERMAL_FADE"]),
                "thermalColor": opt_str(row["THERMAL_COLOR"]),
                "infraredColor": opt_str(row["INFRARED_COLOR"]),
                "thermalWhitepoint": as_num(row["THERMAL_WHITEPOINT"]),
                "infraredWhitepoint": as_num(row["INFRARED_WHITEPOINT"]),
                "infraredScanlines": as_num(row["INFRARED_SCANLINES"]),
                "infraredOutlineColor": opt_str(row["INFRARED_OUTLINE_COLOR"]),
                "infraredOutlineAlpha": as_num(row["INFRARED_OUTLINE_ALPHA"]),
                "infraredOutlineWidth": as_num(row["INFRARED_OUTLINE_WIDTH"]),
                "highlights": {
                    "friendlies": as_bool(row["THERMAL_FRIENDLIES"]),
                    "enemies": as_bool(row["THERMAL_ENEMIES"]),
                    "vehicles": as_bool(row["THERMAL_VEHICLES"]),
                    "projectiles": as_bool(row["THERMAL_PROJECTILES"]),
                    "neutrals": as_bool(row["THERMAL_NEUTRALS"]),
                    "facilities": as_bool(row["THERMAL_FACILITIES"]),
                    "players": as_bool(row["THERMAL_PLAYERS"]),
                    "npcs": as_bool(row["THERMAL_NPCS"]),
                },
            }
        out.append(e)
    out.sort(key=lambda e: e["id"])
    return out


def read_camera_effects(data_dir: Path) -> list[dict]:
    path = data_dir / "ActorCameraEffectDefinitions.xml"
    if not path.exists():
        return []
    root = ET.parse(path).getroot()
    out = []
    for node in root.findall("EffectDefinition"):
        a = node.attrib
        out.append(
            {
                "id": as_int(a.get("id", "0")),
                "name": a.get("name"),
                "intensity": as_num(a.get("intensity", "0")),
                "radiusM": as_num(a.get("radius", "0")),
                "falloffM": as_num(a.get("falloff", "0")),
                "durationSec": as_num(a.get("duration", "0")),
                "easeInSec": as_num(a.get("easeInTime", "0")),
                "easeOutSec": as_num(a.get("easeOutTime", "0")),
                "easeIntensity": as_bool(a.get("easeIntensity", "0")),
                "easeFalloff": as_bool(a.get("easeFalloff", "0")),
                "periodSec": as_num(a.get("period", "0")),
            }
        )
    out.sort(key=lambda e: e["id"])
    return out


def read_indirect_damage(data_dir: Path) -> list[dict]:
    """
    ClientEffects.txt is 221/236 RequestAnimation rows. Only IndirectDamage and HealTool
    carry numbers a server tick needs; PARAM1/2 are the millisecond window and PARAM3/4 the
    magnitude pair, read off the two rows themselves (90002 = 4000/15000 ms, 5/15;
    90070 = 3000/8000 ms, 4/8). The meaning of the magnitude pair is not named anywhere in
    the client - see the ``indirectDamageMagnitudeUnits`` gap.
    """
    path = data_dir / "ClientEffects.txt"
    if not path.exists():
        return []
    out = []
    for row in Sheet(path):
        if row["TYPE_NAME"] not in ("IndirectDamage", "HealTool"):
            continue
        out.append(
            {
                "id": as_int(row["ID"]),
                "typeName": row["TYPE_NAME"],
                "abilityId": as_int(row["ABILITY_ID"]) or None,
                "serverEffectId": as_int(row["SERVER_EFFECT_ID"]) or None,
                "activeCompEffectId": as_int(row["ACTIVE_COMP_EFFECT_ID"]) or None,
                "params": [as_num(row[f"PARAM{i}"]) for i in range(1, 15)],
            }
        )
        # Only the IndirectDamage rows have the window/magnitude shape; HealTool uses PARAM1
        # for something else entirely (a single 6) and gets no interpretation here.
        if row["TYPE_NAME"] == "IndirectDamage":
            out[-1]["windowMs"] = [as_int(row["PARAM1"]), as_int(row["PARAM2"])]
            out[-1]["magnitudePair"] = [as_num(row["PARAM3"]), as_num(row["PARAM4"])]
    out.sort(key=lambda e: e["id"])
    return out


def read_scoring(data_dir: Path, loc: Loc) -> dict[str, Any]:
    managers = []
    mgr_by_id: dict[int, dict] = {}
    p = data_dir / "PlayerStatManagers.txt"
    if p.exists():
        for row in Sheet(p):
            m = {
                "id": as_int(row["ID"]),
                "scope": opt_str(row["TYPE_NAME"]),
                "managerType": as_int(row["PLAYER_STAT_MANAGER_TYPE"]),
                "contentId": as_int(row["CONTENT_ID"]),
                "statIds": [],
            }
            managers.append(m)
            mgr_by_id[m["id"]] = m

    # PlayerStatManagerMap keys on PLAYER_STAT_ID alone, so a stat sits in exactly one
    # manager. Stat 19 has no row: it is defined but never accumulated in this build.
    stat_to_mgr: dict[int, dict] = {}
    p = data_dir / "PlayerStatManagerMap.txt"
    if p.exists():
        for row in Sheet(p):
            sid = as_int(row["PLAYER_STAT_ID"])
            mid = as_int(row["PLAYER_STAT_MANAGER_ID"])
            stat_to_mgr[sid] = {
                "managerId": mid,
                "displayIndex": as_int(row["DISPLAY_INDEX"]),
                "contentId": as_int(row["CONTENT_ID"]),
            }
            if mid in mgr_by_id:
                mgr_by_id[mid]["statIds"].append(sid)
    for m in managers:
        m["statIds"].sort()

    player_stats = []
    p = data_dir / "PlayerStats.txt"
    if p.exists():
        for row in Sheet(p):
            sid = as_int(row["ID"])
            link = stat_to_mgr.get(sid)
            mgr = mgr_by_id.get(link["managerId"]) if link else None
            player_stats.append(
                {
                    "id": sid,
                    "tag": opt_str(row["UI_TAG"]),
                    "statType": as_int(row["PLAYER_STAT_TYPE"]),
                    "displayName": loc.text(row["NAME_ID"]),
                    "description": loc.text(row["DESCRIPTION_ID"]),
                    "leaderboard": as_bool(row["FLAG_LEADERBOARD"]),
                    "onClient": as_bool(row["FLAG_ON_CLIENT"]),
                    "contentId": as_int(row["CONTENT_ID"]),
                    "managerId": link["managerId"] if link else None,
                    "scope": mgr["scope"] if mgr else None,
                    "displayIndex": link["displayIndex"] if link else None,
                }
            )
    player_stats.sort(key=lambda s: s["id"])

    # Game mode -> stat managers -> the stats each mode accumulates.
    mode_managers: dict[int, list[int]] = {}
    p = data_dir / "GameModePlayerStatManagerMap.txt"
    if p.exists():
        for row in Sheet(p):
            mode_managers.setdefault(as_int(row["GAME_MODE_ID"]), []).append(
                as_int(row["PLAYER_STAT_MANAGER_ID"])
            )
    modes = []
    p = data_dir / "GameModeDefinitions.txt"
    if p.exists():
        for row in Sheet(p):
            mid = as_int(row["ID"])
            mgr_ids = sorted(set(mode_managers.get(mid, [])))
            modes.append(
                {
                    "id": mid,
                    "designName": opt_str(row["DESIGN_NAME"]),
                    "uiTag": opt_str(row["UI_TAG"]),
                    "gameModeTypeId": as_int(row["GAME_MODE_TYPE_ID"]),
                    "title": loc.text(row["TITLE_STRING_ID"]),
                    "description": loc.text(row["DESCRIPTION_STRING_ID"]),
                    "statManagerIds": mgr_ids,
                    "statIds": sorted(
                        {s for m in mgr_ids for s in mgr_by_id.get(m, {}).get("statIds", [])}
                    ),
                }
            )
    # Manager sets referenced by a game-mode id that GameModeDefinitions does not define.
    orphan_mode_ids = sorted(set(mode_managers) - {m["id"] for m in modes})

    awards = []
    award_types: dict[int, int] = {}
    p = data_dir / "Experience.txt"
    if p.exists():
        for row in Sheet(p):
            at = as_int(row["AWARD_TYPE_ID"])
            award_types[at] = award_types.get(at, 0) + 1
            awards.append(
                {
                    "id": as_int(row["ID"]),
                    "awardTypeId": at,
                    "xp": as_int(row["XP"]),
                    "notableEvent": as_bool(row["NOTABLE_EVENT"]),
                    "text": loc.text(row["STRING_ID"]),
                    "textSecondary": loc.text(row["STRING_ID_SECONDARY"]),
                    "stringId": as_int(row["STRING_ID"]) or None,
                    "stringIdSecondary": as_int(row["STRING_ID_SECONDARY"]) or None,
                }
            )
    awards.sort(key=lambda a: a["id"])

    return {
        "playerStats": player_stats,
        "leaderboardStatIds": sorted(s["id"] for s in player_stats if s["leaderboard"]),
        "statManagers": managers,
        "gameModes": modes,
        "gameModeIdsWithoutDefinition": orphan_mode_ids,
        "experienceAwards": awards,
        "experienceAwardTypes": [
            {"awardTypeId": k, "rowCount": v} for k, v in sorted(award_types.items())
        ],
        "playerScoreRows": count_rows(data_dir / "PlayerScore.txt"),
    }


def read_profile_resource_sets(data_dir: Path, loc: Loc, resources: list[dict]) -> list[dict]:
    """
    ProfileResourceMappings gives 'which resources does this archetype own'. In this build
    it is legacy content: 52 of the referenced resource ids have no Resources.txt row, and
    the Survivor profile's set does not contain health, stamina, toxicity or bleeding.
    Emitted so the join is visible, with the dangling ids named per row.
    """
    p = data_dir / "ProfileResourceMappings.txt"
    if not p.exists():
        return []
    known = {r["id"] for r in resources}
    names: dict[int, str | None] = {}
    tp = data_dir / "ProfileTypes.txt"
    if tp.exists():
        for row in Sheet(tp):
            names[as_int(row["ID"])] = loc.text(row["NAME_ID"])

    by_profile: dict[int, dict] = {}
    for row in Sheet(p):
        pid = as_int(row["PROFILE_ID"])
        e = by_profile.setdefault(
            pid,
            {
                "profileId": pid,
                "profileName": names.get(pid),
                "definedInProfileTypes": pid in names,
                "rank": as_int(row["PROFILE_RANK"]),
                "resourceIds": [],
                "unknownResourceIds": [],
            },
        )
        rid = as_int(row["RESOURCE_ID"])
        e["resourceIds"].append(rid)
        if rid not in known:
            e["unknownResourceIds"].append(rid)
    out = [by_profile[k] for k in sorted(by_profile)]
    for e in out:
        e["resourceIds"].sort()
        e["unknownResourceIds"].sort()
    return out


#: AbilityEx columns that would carry a magnitude if the client held one. Checked so the
#: "the tier ability sets carry no damage number" claim in serverSideGaps is reproduced by
#: the script rather than asserted by hand.
_ABILITY_MAGNITUDE_COLS = (
    "RESOURCE_TYPE", "RESOURCE_THRESHOLD", "RESOURCE_FIRST_COST", "RESOURCE_COST_PER_MSEC",
)


def read_tier_abilities(data_dir: Path, resources: list[dict]) -> dict[str, Any]:
    """
    Expand every ability / ability-set referenced by a ResourceTiers row through
    AbilitySets -> AbilityEx, and report what those ability rows actually carry.

    This is the tier -> damage-over-time chain: a resource value lands in a tier band, the
    band names an ACTIVE_ABILITY_SET_ID, the set names AbilityEx rows, and the server runs
    them (every one of them has FLAG_RUN_ON_SERVER=1). The point of resolving it here is to
    show, mechanically, that the AbilityEx rows hold scopes and resource costs but no damage
    magnitude - which is why bleedDamagePerTier is a serverSideGap.
    """
    sets_path = data_dir / "AbilitySets.txt"
    ex_path = data_dir / "AbilityEx.txt"
    if not sets_path.exists() or not ex_path.exists():
        return {}

    set_members: dict[int, list[int]] = {}
    for row in Sheet(sets_path):
        set_members.setdefault(as_int(row["ABILITY_SET_ID"]), []).append(as_int(row["ABILITY_ID"]))

    wanted_sets: set[int] = set()
    wanted_abilities: set[int] = set()
    for r in resources:
        for a in (r["abilities"]["activated"], r["abilities"]["depleted"]):
            if a:
                wanted_abilities.add(a)
        for t in r["tiers"]:
            if t["activeAbilitySetId"]:
                wanted_sets.add(t["activeAbilitySetId"])
            if t["activeAbilityId"]:
                wanted_abilities.add(t["activeAbilityId"])
    for s in wanted_sets:
        wanted_abilities.update(set_members.get(s, []))

    abilities: dict[int, dict] = {}
    for row in Sheet(ex_path):
        aid = as_int(row["ID"])
        if aid not in wanted_abilities:
            continue
        abilities[aid] = {
            "id": aid,
            "typeName": opt_str(row["TYPE_NAME"]),
            "abilityClassId": as_int(row["ABILITY_CLASS_ID"]) or None,
            "runsOnServer": as_bool(row["FLAG_RUN_ON_SERVER"]),
            "runsOnClient": as_bool(row["FLAG_RUN_ON_CLIENT"]),
            "expireMsec": as_int(row["EXPIRE_MSEC"]) or None,
            "reuseDelayMsec": as_int(row["REUSE_DELAY_MSEC"]) or None,
            "reqSetId": as_int(row["REQ_SET_ID"]) or None,
            "magnitudeColumns": {c: as_num(row[c]) for c in _ABILITY_MAGNITUDE_COLS
                                 if c in row and as_num(row[c])},
        }

    missing = sorted(wanted_abilities - set(abilities))
    return {
        "abilitySets": [
            {"abilitySetId": s, "abilityIds": sorted(set_members.get(s, []))}
            for s in sorted(wanted_sets)
        ],
        "abilities": [abilities[a] for a in sorted(abilities)],
        "abilityIdsNotInAbilityEx": missing,
        "abilitiesWithAnyMagnitudeColumnSet": sorted(
            a["id"] for a in abilities.values() if a["magnitudeColumns"]
        ),
    }


def count_rows(path: Path) -> int | None:
    if not path.exists():
        return None
    return sum(1 for _ in Sheet(path))


# ---------------------------------------------------------------------------------
# provenance and gaps
# ---------------------------------------------------------------------------------

FIELD_PROVENANCE: dict[str, str] = {
    "resources[].id": "Resources.ID",
    "resources[].type": "Resources.RESOURCE_TYPE (the value carried on zone opcode 0x8d)",
    "resources[].typeName": "Resources.TYPE_NAME (empty on the rows the client never named)",
    "resources[].codeFactoryName": "Resources.CODE_FACTORY_NAME",
    "resources[].name": "Resources.NAME_ID -> locale (Global.Text.<id>, docs/28 §3)",
    "resources[].description": "Resources.DESCRIPTION_ID -> locale",
    "resources[].nameId": "Resources.NAME_ID",
    "resources[].descriptionId": "Resources.DESCRIPTION_ID",
    "resources[].initialValue": "Resources.INITIAL_VALUE",
    "resources[].maxValue": "Resources.MAX_VALUE",
    "resources[].regen.perMs": "Resources.REGEN_PER_MS",
    "resources[].regen.delayMs": "Resources.REGEN_DELAY_MS",
    "resources[].regen.damageInterruptMs": "Resources.REGEN_DAMAGE_INTERRUPT_MS",
    "resources[].regen.tickMs": "Resources.REGEN_TICK_MSEC",
    "resources[].regen.initiallyDisabled": "Resources.FLAG_INIT_WITH_DISABLED_REGEN",
    "resources[].burn.perMs": "Resources.BURN_PER_MSEC",
    "resources[].burn.tickMs": "Resources.BURN_TICK_MSEC",
    "resources[].burn.initiallyDisabled": "Resources.FLAG_INIT_WITH_DISABLED_BURN",
    "resources[].tierTickMs": "Resources.TIER_TICK_MSEC",
    "resources[].broadcastRangeM": "Resources.PACKET_BROADCAST_RANGE (0 = self only)",
    "resources[].valueMarkers.*": "Resources.VALUE_MARKER_LO/MED/HI (0 on every row in this build)",
    "resources[].flags.onClient": "Resources.FLAG_ON_CLIENT",
    "resources[].flags.doNotTick": "Resources.FLAG_DONOT_TICK",
    "resources[].flags.doNotPersist": "Resources.FLAG_DONOT_PERSIST",
    "resources[].flags.profileScope": "Resources.FLAG_PROFILE_SCOPE",
    "resources[].flags.notVehMountScope": "Resources.FLAG_NOT_VEH_MOUNT_SCOPE",
    "resources[].abilities.activated": "Resources.ACTIVATED_ABILITY_ID -> AbilityEx.ID",
    "resources[].abilities.depleted": "Resources.DEPLETED_ABILITY_ID -> AbilityEx.ID",
    "resources[].compositeEffects.*": "Resources.ACTIVATE/TERMINATE/DEPLETED_COMP_EFFECT_ID "
                                      "-> ActorCompositeEffectDefinitions.xml (ids only, file not extracted)",
    "resources[].tiers[].valueMin": "ResourceTiers.TIER_VALUE_MIN (joined on RESOURCE_ID)",
    "resources[].tiers[].activeAbilityId": "ResourceTiers.ACTIVE_ABILITY_ID -> AbilityEx.ID",
    "resources[].tiers[].activeAbilitySetId": "ResourceTiers.ACTIVE_ABILITY_SET_ID -> AbilitySets.ABILITY_SET_ID",
    "resources[].tiers[].activateCompEffectId": "ResourceTiers.ACTIVATE_COMP_EFFECT_ID",
    "resources[].tiers[].terminateCompEffectId": "ResourceTiers.TERMINATE_COMP_EFFECT_ID",
    "resources[].tiers[].activeCompEffectId": "ResourceTiers.ACTIVE_COMP_EFFECT_ID",
    "resources[].tiers[].requirementId": "ResourceTiers.REQUIREMENT_ID",
    "resources[].tiers[].clientRequirementId": "ResourceTiers.CLIENT_REQUIREMENT_ID",
    "resources[].tiers[].messageId": "ResourceTiers.MESSAGE_ID",
    "resources[].tiers[].message": "ResourceTiers.MESSAGE_ID -> locale",
    "resources[].tiers[].messagePeriodSec": "ResourceTiers.MESSAGE_PERIOD_SEC",
    "resources[].tiers[].uiData": "ResourceTiers.UI_DATA",
    "resourceTypes[].type": "distinct Resources.RESOURCE_TYPE",
    "resourceTypes[].name": "Resources.TYPE_NAME of the first row that filled it",
    "resourceTypes[].resourceIds": "Resources.ID grouped by RESOURCE_TYPE",
    "playerVitals.*": "a Cranberry selection over resources[]; selectionReason states why "
                      "that row was chosen. Everything under it is copied from the named "
                      "Resources row, so its provenance is resources[].*",
    "characterStats[].id": "CharacterStatDefinitions.ID",
    "characterStats[].name": "StringHashToValue 'StatId.<name>' whose VALUE equals the id "
                             "(null when more than one name shares the id)",
    "characterStats[].nameCandidates": "all StringHashToValue StatId.* names with that VALUE",
    "characterStats[].displayName": "CharacterStatDefinitions.NAME_ID -> locale (0 on every row)",
    "characterStats[].description": "CharacterStatDefinitions.DESCRIPTION_ID -> locale (0 on every row)",
    "characterStats[].imageId": "CharacterStatDefinitions.IMAGE_ID",
    "characterStats[].sendToRemoteClient": "CharacterStatDefinitions.SEND_TO_REMOTE_CLIENT",
    "screenEffects[].id": "ScreenEffects.ID (the id sent on zone opcode 0xe1)",
    "screenEffects[].type": "ScreenEffects.TYPE",
    "screenEffects[].kind": "Cranberry label for ScreenEffects.TYPE, read off which columns "
                            "the rows in each group actually use (no name column exists)",
    "screenEffects[].durationMs": "ScreenEffects.DURATION",
    "screenEffects[].fadeInMs": "ScreenEffects.FADE_IN",
    "screenEffects[].fadeOutMs": "ScreenEffects.FADE_OUT",
    "screenEffects[].scale": "ScreenEffects.SCALE",
    "screenEffects[].affects.*": "ScreenEffects.AFFECT_DRIVER/PASSENGER/GUNNER",
    "screenEffects[].frameColor": "ScreenEffects.FRAME_COLOR",
    "screenEffects[].effectColor": "ScreenEffects.EFFECT_COLOR",
    "screenEffects[].vision.*": "ScreenEffects.THERMAL_*/INFRARED_* (only meaningful on TYPE 5)",
    "cameraEffects[].*": "ActorCameraEffectDefinitions.xml <EffectDefinition> attributes",
    "indirectDamage[].*": "ClientEffects rows with TYPE_NAME IndirectDamage or HealTool",
    "scoring.playerStats[].id": "PlayerStats.ID",
    "scoring.playerStats[].tag": "PlayerStats.UI_TAG (the stable string key)",
    "scoring.playerStats[].statType": "PlayerStats.PLAYER_STAT_TYPE",
    "scoring.playerStats[].displayName": "PlayerStats.NAME_ID -> locale",
    "scoring.playerStats[].description": "PlayerStats.DESCRIPTION_ID -> locale",
    "scoring.playerStats[].leaderboard": "PlayerStats.FLAG_LEADERBOARD",
    "scoring.playerStats[].onClient": "PlayerStats.FLAG_ON_CLIENT",
    "scoring.playerStats[].managerId": "PlayerStatManagerMap.PLAYER_STAT_MANAGER_ID keyed by PLAYER_STAT_ID",
    "scoring.playerStats[].scope": "PlayerStatManagers.TYPE_NAME of that manager",
    "scoring.playerStats[].displayIndex": "PlayerStatManagerMap.DISPLAY_INDEX",
    "scoring.statManagers[].scope": "PlayerStatManagers.TYPE_NAME",
    "scoring.statManagers[].managerType": "PlayerStatManagers.PLAYER_STAT_MANAGER_TYPE",
    "scoring.statManagers[].statIds": "PlayerStatManagerMap grouped by PLAYER_STAT_MANAGER_ID",
    "scoring.gameModes[].*": "GameModeDefinitions, plus GameModePlayerStatManagerMap for "
                             "statManagerIds and the statIds those managers hold",
    "scoring.experienceAwards[].xp": "Experience.XP",
    "scoring.experienceAwards[].awardTypeId": "Experience.AWARD_TYPE_ID",
    "scoring.experienceAwards[].notableEvent": "Experience.NOTABLE_EVENT",
    "scoring.experienceAwards[].text": "Experience.STRING_ID -> locale",
    "scoring.experienceAwards[].textSecondary": "Experience.STRING_ID_SECONDARY -> locale",
    "scoring.playerScoreRows": "row count of PlayerScore.txt (0 - the sheet is header-only)",
    "profileResourceSets[].profileId": "ProfileResourceMappings.PROFILE_ID",
    "profileResourceSets[].profileName": "ProfileTypes.NAME_ID -> locale, joined on ProfileTypes.ID",
    "profileResourceSets[].rank": "ProfileResourceMappings.PROFILE_RANK",
    "profileResourceSets[].resourceIds": "ProfileResourceMappings.RESOURCE_ID",
    "profileResourceSets[].unknownResourceIds": "those RESOURCE_IDs with no Resources.txt row",
    "tierAbilities.abilitySets[]": "AbilitySets.ABILITY_SET_ID/ABILITY_ID, restricted to the "
                                   "sets ResourceTiers references",
    "tierAbilities.abilities[]": "AbilityEx rows for those ability ids "
                                 "(TYPE_NAME, FLAG_RUN_ON_SERVER/CLIENT, EXPIRE_MSEC, "
                                 "REUSE_DELAY_MSEC, REQ_SET_ID, and any non-zero RESOURCE_* column)",
    "wire.*": "C:\\Aug2017\\out\\registrations-1148.json (this project's own dump of the "
              "client's packet-registration table, registered in FUN_1413bfa90)",
}


WIRE = {
    "resourceEvent": {
        "opcode": "0x8d",
        "family": "cPacketIdResourceEventBase",
        "subIds": {
            "1": "Resources::cResourceEventPacketIdSetCharacterResources",
            "2": "Resources::cResourceEventPacketIdSetCharacterResource",
            "3": "Resources::cResourceEventPacketIdUpdateCharacterResource",
            "4": "Resources::cResourceEventPacketIdRemoveCharacterResource",
        },
        "carries": "resources[].type as the resource identifier, resources[].id as the definition",
    },
    "resources": {
        "opcode": "0xc7",
        "family": "cPacketIdResourcesBase",
        "subIds": {},
        "carries": "second resource family; no sub-ids in the registration table",
    },
    "screenEffect": {
        "opcode": "0xe1",
        "family": "cPacketIdScreenEffectBase",
        "subIds": {},
        "carries": "screenEffects[].id",
    },
    "experience": {
        "opcode": "0x87",
        "family": "cPacketIdExperienceBase",
        "subIds": {
            "1": "Experience::cExperiencePacketIdSetExperience",
            "2": "Experience::cExperiencePacketIdSetExperienceRanks",
            "3": "Experience::cExperiencePacketIdSetExperienceRateTier",
            "4": "Experience::cExperiencePacketIdDeathReport",
        },
        "carries": "scoring.experienceAwards[].xp",
    },
    "characterState": {
        "opcode": "0xcf",
        "family": "cPacketIdCharacterStateBase",
        "subIds": {},
        "carries": "characterStats[] whose sendToRemoteClient is true",
    },
}


def build_gaps(doc: dict[str, Any]) -> list[dict[str, str]]:
    """
    Values a Cranberry stats system needs that the August client simply does not carry.
    Each entry names the evidence for the absence. Nothing here is filled with a guess.
    """
    res = {r["id"]: r for r in doc["resources"]}
    vit = doc["playerVitals"]
    survivor = next(
        (p for p in doc["profileResourceSets"] if p.get("profileName") == "Survivor"), None
    )
    survivor_txt = (
        f"ProfileResourceMappings profile {survivor['profileId']} (ProfileTypes 'Survivor') "
        f"maps to resource ids {survivor['resourceIds']}, of which "
        f"{survivor['unknownResourceIds']} have no Resources.txt row"
        if survivor
        else "ProfileTypes has no 'Survivor' row in this extraction"
    )
    dangling = sorted({r for p in doc["profileResourceSets"] for r in p["unknownResourceIds"]})

    # Which of the picked vitals any profile row actually claims, and whether the profile
    # that claims it is even a profile ProfileTypes defines.
    vital_refs: dict[str, list[str]] = {}
    for label, spec in doc["playerVitals"].items():
        rid = spec["resourceId"]
        owners = [
            f"profile {p['profileId']}"
            + (f" ('{p['profileName']}')" if p["profileName"] else " (not in ProfileTypes)")
            for p in doc["profileResourceSets"]
            if rid in p["resourceIds"]
        ]
        vital_refs[label] = owners
    unclaimed = sorted(k for k, v in vital_refs.items() if not v)
    claimed = {k: v for k, v in vital_refs.items() if v}

    ta = doc.get("tierAbilities") or {}
    bleed_sets = [t["activeAbilitySetId"] for t in res[21]["tiers"] if t["activeAbilitySetId"]]
    set_members = {s["abilitySetId"]: s["abilityIds"] for s in ta.get("abilitySets", [])}
    bleed_abilities = sorted({a for s in bleed_sets for a in set_members.get(s, [])})
    ability_by_id = {a["id"]: a for a in ta.get("abilities", [])}
    bleed_types = sorted({ability_by_id[a]["typeName"] for a in bleed_abilities
                          if a in ability_by_id})
    bleed_with_magnitude = [a for a in bleed_abilities
                            if ability_by_id.get(a, {}).get("magnitudeColumns")]

    gaps = [
        {
            "id": "playerResourceSet",
            "area": "vitals",
            "need": "the exact set of Resources.ID rows a KOTK survivor is spawned with",
            "evidence": survivor_txt
            + f"; across the whole sheet {len(dangling)} referenced resource ids have no "
              f"Resources.txt row. Of the vitals a match needs, {unclaimed} are referenced by "
              f"no profile row at all. The apparent hits ({claimed}) are incidental: profiles "
              f"2-23 all have the shape [2, <slot>, 201..206] with <slot> running 46..59 then "
              f"7..13, a sequence that happens to overlap low Resources ids, and profile 1 "
              f"([1, 2]) is not present in ProfileTypes at all",
            "decision": "Cranberry picks the set; playerVitals records the picks and the "
                        "reason for each. The picks are marked selectionIsCranberryChoice.",
        },
        {
            "id": "healthRegen",
            "area": "vitals",
            "need": "passive health regeneration rate and its post-damage delay",
            "evidence": f"Resources id 1: REGEN_PER_MS={res[1]['regen']['perMs']}, "
                        f"REGEN_DELAY_MS={res[1]['regen']['delayMs']}, "
                        f"REGEN_DAMAGE_INTERRUPT_MS={res[1]['regen']['damageInterruptMs']}, "
                        f"BURN_PER_MSEC={res[1]['burn']['perMs']} - all zero",
            "decision": "server-authored; the client only ticks what it is told (REGEN_TICK_MSEC "
                        f"{res[1]['regen']['tickMs']} ms is the display cadence)",
        },
        {
            "id": "staminaDrainAndRegen",
            "area": "vitals",
            "need": "stamina cost of sprinting/jumping and the recovery rate",
            "evidence": f"Resources id 70: REGEN_PER_MS={res[70]['regen']['perMs']}, "
                        f"BURN_PER_MSEC={res[70]['burn']['perMs']} - both zero; the only "
                        "stamina tier ladder (ResourceTiers.RESOURCE_ID 6) is written against "
                        "the legacy 600-point row and carries ability-set ids, not rates",
            "decision": "server-authored. The StatId.* speed-modifier stats "
                        "(CriticalStaminaPercent 52, LowStaminaPercent 54, NominalStaminaPercent 56 "
                        "and their SpeedModifier partners 53/55/57) name the knobs but the client "
                        "carries no values for them either - see characterStatValues.",
        },
        {
            "id": "characterStatValues",
            "area": "characterStats",
            "need": "base/min/max values for the 86 character stats (MaxHp, speed modifiers, "
                    "breath, aggro, flinch, ...)",
            "evidence": "CharacterStatDefinitions.txt has exactly five columns - ID, NAME_ID, "
                        "IMAGE_ID, DESCRIPTION_ID, SEND_TO_REMOTE_CLIENT - and NAME_ID, "
                        "IMAGE_ID and DESCRIPTION_ID are 0 on all 86 rows. There is no value "
                        "column anywhere in the sheet.",
            "decision": "server-authored. The client is a renderer for values the server sends; "
                        "the sheet only says which ids exist and which of them are broadcast.",
        },
        {
            "id": "bleedDamagePerTier",
            "area": "vitals",
            "need": "the damage-per-tick each bleed severity band applies",
            "evidence": f"ResourceTiers RESOURCE_ID 21 gives bands {vit['bleeding']['tierBands']} "
                        f"-> ACTIVE_ABILITY_SET_ID {bleed_sets}. Those sets resolve through "
                        f"AbilitySets to AbilityEx rows {bleed_abilities}, of TYPE_NAME "
                        f"{bleed_types}, and "
                        + (f"{len(bleed_with_magnitude)} of them fill a RESOURCE_* magnitude "
                           f"column ({bleed_with_magnitude})"
                           if bleed_with_magnitude
                           else "not one of them fills any RESOURCE_* magnitude column")
                        + " - AbilityEx carries scopes, requirement sets and resource costs, "
                          "never a damage number (see tierAbilities in this document).",
            "decision": "server-authored; the client only needs the tier index for its VFX and "
                        "the repeating HUD message (MESSAGE_ID 9875/9877/9879).",
        },
        {
            "id": "bleedOnset",
            "area": "vitals",
            "need": "what starts bleeding and how fast the value climbs",
            "evidence": f"Resources id 21: INITIAL_VALUE {res[21]['initialValue']}, "
                        f"MAX_VALUE {res[21]['maxValue']}, BURN_PER_MSEC {res[21]['burn']['perMs']}, "
                        f"REGEN_PER_MS {res[21]['regen']['perMs']} - the meter has no self-driven "
                        "movement at all",
            "decision": "server-authored (damage type -> bleed application, bandage -> reduction)",
        },
        {
            "id": "toxicityDamageCurve",
            "area": "vitals",
            "need": "how much extra damage a given toxicity value causes in the gas",
            "evidence": "Resources id 611 has no ResourceTiers rows at all (0 of 107 tier rows "
                        "reference it), and the locale text for DESCRIPTION_ID 14119 states the "
                        "effect - 'Full Toxicity will cause you to take extra damage in the "
                        "Toxic Gas' - without a number",
            "decision": "server-authored, consistent with D23 (gas phase count, timers and damage "
                        "curve are Cranberry's own design). The client does carry the fill rate: "
                        f"REGEN_PER_MS {res[611]['regen']['perMs']} over MAX_VALUE "
                        f"{res[611]['maxValue']} = {int(res[611]['maxValue']/max(res[611]['regen']['perMs'],1)/1000)} s "
                        "from empty to full while regen is enabled.",
        },
        {
            "id": "toxicityEnableTrigger",
            "area": "vitals",
            "need": "when to enable/disable the toxicity meter's regen",
            "evidence": f"Resources id 611 ships with FLAG_INIT_WITH_DISABLED_REGEN="
                        f"{res[611]['regen']['initiallyDisabled']} and FLAG_INIT_WITH_DISABLED_BURN="
                        f"{res[611]['burn']['initiallyDisabled']}: the resource is created inert. "
                        "Nothing in the client says what flips it.",
            "decision": "server-authored - the gas ring geometry is the trigger (D23); the flag pair "
                        "is the mechanism the client already understands.",
        },
        {
            "id": "foodWaterDrain",
            "area": "vitals",
            "need": "hunger/thirst drain per second and the damage taken at zero",
            "evidence": f"Resources id 4 (Food) and id 5 (Water): BURN_PER_MSEC "
                        f"{res[4]['burn']['perMs']}/{res[5]['burn']['perMs']}, REGEN_PER_MS "
                        f"{res[4]['regen']['perMs']}/{res[5]['regen']['perMs']}; neither has a "
                        "ResourceTiers row",
            "decision": "server-authored (and a Cranberry design decision whether a BR match runs "
                        "these meters at all)",
        },
        {
            "id": "screenEffectTriggers",
            "area": "screenEffects",
            "need": "which ScreenEffects.ID to send for which game state",
            "evidence": "ScreenEffects.txt has 30 columns and not one of them references a "
                        "resource, ability, tier or game state; nothing else in the extracted "
                        "sheets references a ScreenEffects.ID either (ClientEffects does not - "
                        "221 of its 236 rows are RequestAnimation)",
            "decision": "server-authored. The rows' own shape is the only guide: id 17 is the "
                        "only long green full-screen tint (30 s, EFFECT_COLOR 00FF00 over "
                        "FRAME_COLOR 0A0814) and id 19 the only other long tint (35 s, pink over "
                        "purple) - candidates for the two status overlays, but the client never "
                        "says so.",
        },
        {
            "id": "indirectDamageMagnitudeUnits",
            "area": "effects",
            "need": "the meaning of ClientEffects IndirectDamage PARAM3/PARAM4",
            "evidence": "the two rows (90002: 4000/15000 ms, 5/15; 90070: 3000/8000 ms, 4/8) have "
                        "no PARAM name columns anywhere in ClientEffects.txt and no locale strings "
                        "(NAME_ID/DESCRIPTION_ID are 0 on both)",
            "decision": "treat PARAM1/2 as the millisecond window only; supply the damage from the "
                        "server's own combat model",
        },
        {
            "id": "scoreFormula",
            "area": "scoring",
            "need": "how a match converts kills/placement/time into a score",
            "evidence": "PlayerScore.txt exists in Assets_089 with the columns ID, SCORE_TYPE, "
                        "PARAM1, POINTS, NAME_ID, REQUIREMENT_ID and **zero data rows** "
                        "(scoring.playerScoreRows in this document)",
            "decision": "Cranberry's own design; nothing to derive",
        },
        {
            "id": "gameModeStatWiring",
            "area": "scoring",
            "need": "which stat managers the KOTK BR game modes accumulate into",
            "evidence": "GameModePlayerStatManagerMap has rows for game-mode ids "
                        f"{sorted({m['id'] for m in doc['scoring']['gameModes'] if m['statManagerIds']} | set(doc['scoring']['gameModeIdsWithoutDefinition']))} "
                        f"only; ids {doc['scoring']['gameModeIdsWithoutDefinition']} are not "
                        "defined in GameModeDefinitions at all, and the Z2 battle-royale modes "
                        "actually shipped (5 Team2, 7 Team5, 13/20/39 BR.Z2) have no row in the "
                        "map, so their statManagerIds resolve empty",
            "decision": "Cranberry wires the modes it runs to the scopes it wants; the sheet's "
                        "one populated mode (1, BattleRoyale -> managers 1-7) is the shape to "
                        "follow, not a table to trust",
        },
        {
            "id": "leaderboardPeriods",
            "area": "scoring",
            "need": "the boundaries of the Daily/Weekly/Monthly/Season stat scopes",
            "evidence": "PlayerStatManagers.txt names the scopes (CharacterDaily/Weekly/Monthly/"
                        "Season) but has no date, offset or duration column; there is no other "
                        "sheet that carries one",
            "decision": "server-authored (a reset schedule Cranberry owns)",
        },
        {
            "id": "experienceAwardTriggers",
            "area": "scoring",
            "need": "which game event fires which Experience row",
            "evidence": "Experience.txt keys on ID with AWARD_TYPE_ID as the only classifier; "
                        "AWARD_TYPE_ID has no name table in any extracted sheet and no locale "
                        "string, and no sheet references an Experience.ID",
            "decision": "server-authored. XP is arguably not a KOTK BR system at all - it is "
                        "carried here because opcode 0x87 exists and the table is on disk.",
        },
        {
            "id": "spawnOdds",
            "area": "vitals",
            "need": "spawn/roll odds for anything in this system (e.g. bleed-on-hit chance)",
            "evidence": "the only chance column in the whole stats set is "
                        "ClientEffects.ACTIVATE_CHANCE, which is 0 on all 236 rows",
            "decision": "server-authored",
        },
        {
            "id": "compositeEffectContent",
            "area": "effects",
            "need": "what the ACTIVE/ACTIVATE/TERMINATE composite-effect ids actually play",
            "evidence": "the ids (e.g. 5106/5042/5105 on the bleed tiers) index "
                        "ActorCompositeEffectDefinitions.xml, 412 KB in Assets_031, deliberately "
                        "not extracted - it is client-side particle/sound composition, not server "
                        "state",
            "decision": "no gap for the server: it only ever sends the id. Listed so the dangling "
                        "reference is on the record.",
        },
    ]
    return gaps


# ---------------------------------------------------------------------------------
# driver
# ---------------------------------------------------------------------------------


def build(data_dir: Path, lang: str) -> dict[str, Any]:
    loc = Loc(lang)

    resources, _ = read_resources(data_dir, loc)
    character_stats, statid_names = read_character_stats(data_dir, loc)

    doc: dict[str, Any] = {
        "schema": SCHEMA,
        "generator": "Server/tools/data/derive_stats.py",
        "generatedUtc": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "client": {"build": CLIENT_BUILD, "locale": lang},
        "sources": [],
        "fieldProvenance": FIELD_PROVENANCE,
        "wire": WIRE,
        "resourceTypes": build_resource_types(resources),
        "resources": resources,
        "playerVitals": build_player_vitals(resources),
        "characterStats": character_stats,
        "screenEffects": read_screen_effects(data_dir),
        "cameraEffects": read_camera_effects(data_dir),
        "indirectDamage": read_indirect_damage(data_dir),
        "scoring": read_scoring(data_dir, loc),
        "profileResourceSets": read_profile_resource_sets(data_dir, loc, resources),
        "tierAbilities": read_tier_abilities(data_dir, resources),
    }

    for name in INPUT_SHEETS:
        p = data_dir / name
        if not p.exists():
            doc["sources"].append({"sheet": name, "path": str(p), "missing": True})
            continue
        entry: dict[str, Any] = {
            "sheet": name,
            "path": str(p),
            "bytes": p.stat().st_size,
            "sha256": sha256_of(p),
        }
        if p.suffix == ".txt":
            entry["rows"] = count_rows(p)
        doc["sources"].append(entry)
    ps = data_dir / "PlayerScore.txt"
    if ps.exists():
        doc["sources"].append(
            {
                "sheet": "PlayerScore.txt",
                "path": str(ps),
                "bytes": ps.stat().st_size,
                "sha256": sha256_of(ps),
                "rows": count_rows(ps),
                "note": "header-only; read solely to record that the score table is empty",
            }
        )

    doc["localeResolution"] = {
        "lang": lang,
        "resolved": loc.hits,
        "unresolvedStringIds": sorted(loc.misses),
        "mapping": "locale_key(n) = jenkins_lookup2(ascii('Global.Text.' + str(n)), 0) "
                   "- Server/docs/28-locale-and-datasheets.md §3",
    }
    # Attribute every unresolved string id to the section that referenced it, so a miss is
    # visibly a gap in the shipped locale archive and not a bug in the join.
    miss = set(loc.misses)
    if miss:
        where: dict[str, list[int]] = {}

        def _note(section: str, sid: int | None) -> None:
            if sid in miss:
                where.setdefault(section, []).append(sid)  # type: ignore[arg-type]

        for r in doc["resources"]:
            _note("Resources.NAME_ID/DESCRIPTION_ID", r["nameId"])
            _note("Resources.NAME_ID/DESCRIPTION_ID", r["descriptionId"])
            for t in r["tiers"]:
                _note("ResourceTiers.MESSAGE_ID", t["messageId"])
        for a in doc["scoring"]["experienceAwards"]:
            _note("Experience.STRING_ID/STRING_ID_SECONDARY", a["stringId"])
            _note("Experience.STRING_ID/STRING_ID_SECONDARY", a["stringIdSecondary"])
        doc["localeResolution"]["unresolvedByColumn"] = {
            k: sorted(set(v)) for k, v in sorted(where.items())
        }

    doc["statIdNameTable"] = {
        str(k): v for k, v in sorted(statid_names.items())
    }
    doc["serverSideGaps"] = build_gaps(doc)

    doc["counts"] = {
        "resourceTypes": len(doc["resourceTypes"]),
        "resources": len(doc["resources"]),
        "resourceTiers": sum(len(r["tiers"]) for r in doc["resources"]),
        "resourcesWithTiers": sum(1 for r in doc["resources"] if r["tiers"]),
        "playerVitals": len(doc["playerVitals"]),
        "characterStats": len(doc["characterStats"]),
        "characterStatsNamed": sum(1 for s in doc["characterStats"] if s["nameCandidates"]),
        "characterStatsBroadcast": sum(1 for s in doc["characterStats"] if s["sendToRemoteClient"]),
        "screenEffects": len(doc["screenEffects"]),
        "cameraEffects": len(doc["cameraEffects"]),
        "indirectDamage": len(doc["indirectDamage"]),
        "playerStats": len(doc["scoring"]["playerStats"]),
        "leaderboardStats": len(doc["scoring"]["leaderboardStatIds"]),
        "statManagers": len(doc["scoring"]["statManagers"]),
        "gameModes": len(doc["scoring"]["gameModes"]),
        "experienceAwards": len(doc["scoring"]["experienceAwards"]),
        "profileResourceSets": len(doc["profileResourceSets"]),
        "tierAbilitySets": len((doc.get("tierAbilities") or {}).get("abilitySets", [])),
        "tierAbilities": len((doc.get("tierAbilities") or {}).get("abilities", [])),
        "serverSideGaps": len(doc["serverSideGaps"]),
    }
    return doc


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Derive Cranberry's server-ready stats dataset from the August client's "
                    "datasheets and locale archive.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--data-dir", default=str(DEFAULT_DATA_DIR),
                    help="directory holding the extracted datasheets")
    ap.add_argument("--lang", default="en_us", help="locale to resolve names in")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="output JSON path")
    ap.add_argument("--indent", type=int, default=1, help="JSON indent (0 for compact)")
    ap.add_argument("--summary", action="store_true",
                    help="print counts and the serverSideGaps list to stderr after writing")
    args = ap.parse_args(argv)

    data_dir = Path(args.data_dir)
    if not data_dir.is_dir():
        sys.exit(f"no such data directory: {data_dir}")

    doc = build(data_dir, args.lang)

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=args.indent or None)
        fh.write("\n")

    print(f"{out}  ({out.stat().st_size:,} bytes)", file=sys.stderr)
    if args.summary:
        for k, v in doc["counts"].items():
            print(f"  {k:<26} {v}", file=sys.stderr)
        print("  serverSideGaps:", file=sys.stderr)
        for g in doc["serverSideGaps"]:
            print(f"    {g['id']:<30} [{g['area']}] {g['need']}", file=sys.stderr)
        miss = doc["localeResolution"]["unresolvedStringIds"]
        print(f"  locale: {doc['localeResolution']['resolved']} resolved, "
              f"{len(miss)} string id(s) unresolved{': ' + str(miss[:20]) if miss else ''}",
              file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
