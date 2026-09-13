#!/usr/bin/env python3
r"""
derive_vehicles.py - join the August client's vehicle datasheets into one server-ready
dataset for Cranberry's vehicle system.

INPUT
    C:\Aug2017\out\data_aug\*.txt      the client's own ``^``-delimited datasheets, pulled
                                       out of Assets_*.pack by tools/pack/packread.py
    C:\Aug2017\out\data_aug\locale-en_us.json
                                       locale key -> English text, written by
                                       tools/locale/localedat.py dump

OUTPUT
    C:\Aug2017\out\data_aug\derived\vehicles.json
                                       one JSON document, schema ``cranberry/vehicles/1``
                                       (described below and self-described in the file's
                                       own ``schema`` block)

    Nothing is invented. Every scalar in the output is a value read out of a named sheet
    column, and the ``provenance`` block names the sheet and column for every field path.
    Where the server needs a number the client does not carry, the field is NOT emitted
    with a guess - it is listed in ``serverSideGaps`` instead.

WHY THIS TOOL EXISTS
    ``Vehicles.txt`` is the sheet whose name suggests it holds vehicle stats, and in this
    build it is almost entirely zeroes: MASS, HEALTH, EXPLODE_*, RAM_*, GROUND_INFO,
    AIR_INFO, WATER_INFO, DEPLOY_INFO, ENTER_TIME, EXIT_TIME, SWITCH_SEAT_TIME and
    RESPAWN_TIME are 0 on all 8 rows. The real ground-vehicle numbers live in six other
    sheets (MoveInfo, EngineInfo, DynamicsInfo, WheelInfo, TireInfo, SuspensionInfo) that
    carry no vehicle id at all, and the seat roster, destruction data and cosmetics live in
    another nine. This tool performs those joins once, resolves the string ids through the
    locale, and writes the result in Cranberry's own shape.

FORMAT FACTS RELIED ON (each with its evidence)

  D-V1  Datasheet dialect: CRLF records, single ``^`` separator, a trailing ``^`` on every
        line (so a naive split yields one surplus empty field), ``#`` header prefix, ``*``
        key-column marker, no quoting, no escaping, ASCII.
        Evidence: docs/28-locale-and-datasheets.md sec.1, verified byte-wise over every
        sheet in out\data_aug. Implemented once in tools/data/sheet.py, imported here.

  D-V2  String ids resolve as ``locale_key(n) = jenkins_lookup2("Global.Text." + str(n), 0)``.
        Evidence: docs/28 sec.3; ``Global.Text.%d`` at VA 0x143118638 with 12 xrefs in the
        locale helper cluster 0x140b98540..0x140b9d1xx. Implemented in
        tools/locale/localedat.py::text_key, imported here. ``0`` means "unset", not key 0.
        Checked here on the vehicle name ids: 16 -> "OffRoader", 8932 -> "PickupTruck",
        8953 -> "PoliceCar", 12552 -> "ATV", 9031 -> "Parachute", 9111 -> "Observer",
        13545 -> "Ignition OffRoader", 13548 -> "Ignition ATV".

  D-V3  MoveInfo rows are addressed as ``family base + mode ordinal``.
        ``VehicleMoveInfoMappings.txt`` gives each land vehicle a run of MOVE_INFO_IDs
        ``B .. B+7`` with B a multiple of ten (veh 1 -> 10..17, 2 -> 20..27, 3 -> 30..37,
        5 -> 50..57; veh 16 -> 50..57). **Not always eight rows**: vehicle 15 "Ignition
        OffRoader" has seven (10,11,12,13,15,16,17 - MoveInfo 14 exists but is not mapped
        to it), so the count is read per vehicle, never assumed. Where eight are present
        their MOVEMENT_MODE values run 1, 2, 9, 8, 10, 10, 11, 12 in ordinal order; vehicle
        15 has one mode-10 row instead of two. The rows of a family do **not** differ only
        in MOVEMENT_MODE - see ``describe_mode``, which diffs them column by column. This
        tool derives B per vehicle rather than hard-coding it, and requires B to also exist
        in DynamicsInfo before calling it a physics family.

  D-V4  EngineInfo / DynamicsInfo / SuspensionInfo / TireInfo / WheelInfo are keyed by that
        same family base B (EngineInfo and DynamicsInfo hold exactly B; the three per-corner
        sheets hold B, B+1, B+2, B+3). Neither EngineInfo nor DynamicsInfo has a name column
        and NO sheet in the client maps a vehicle id to them, so this join is an INFERENCE.
        Its corroboration: the per-corner sheets *do* carry name strings
        (``OffRoaderFrontLeftWheel``, ``OffRoaderSuspensionFront``, ``ATVRearRightTire``),
        those names are exactly the strings the matching APEX collision asset
        ``Common_<Family>_COL.apx`` looks up by ``<value name="Wheel"/"Suspension"/"Tire"
        type="String">``, and the family name recovered from them (OffRoader / PickupTruck /
        PoliceCar / ATV) agrees with the vehicle the base was derived from in D-V3 on all
        four families. Every affected field is tagged ``joinConfidence: "inferred"``.
        Binary corroboration that these are runtime property sets: the literals
        ``VehicleDynamicsInfo``, ``VehicleEngineInfo``, ``VehicleSuspensionInfo``,
        ``VehicleTireInfo``, ``VehicleWheelInfo`` at 0x14360c6f0..0x14360c758 and
        ``Vehicle %i did not supply MoveInfo`` at 0x1435e62d0 (tools/pe/exeview.py findstr).

  D-V5  The family NAME is derived, not assumed: it is the longest common prefix of the
        non-empty ``WHEEL_FIRST..WHEEL_FOURTH`` strings of the family's WheelInfo rows.
        base 10 -> "OffRoader", 20 -> "PickupTruck", 30 -> "PoliceCar", 50 -> "ATV".

  D-V6  Destruction is keyed on an NPC id, not a vehicle id: ``DestroyedInfoMappings`` is
        ``NPC_ID -> DESTROYED_INFO_ID``. The vehicle side of that join is recovered through
        the model: ``DestroyedInfo.DESTROYED_MODEL`` -> ``Models.MODEL_FILE_NAME`` gives
        ``Common_OffRoader_Destroyed.adr``, ``Common_PickupTruck_Destroyed.adr``,
        ``Common_Vehicle_PoliceCar01_Destroyed.adr``, ``Vehicle_Common_ATV01_Destroyed.adr``
        - i.e. the file name contains the family name from D-V5. Where a family has more
        DestroyedInfo rows than vehicles (OffRoader has 1 and 7, ATV has 5 and 6, and the
        pairs are byte-identical), the rows are paired with the family's vehicles in id
        order. Tagged ``joinConfidence: "inferred"``.

  D-V7  ``VehicleHideSlotInfoMappings``' second column is named ``EQUIP_SLOT`` but holds
        1..4, which is the ID range of ``VehicleHideSlotInfo`` (whose own EQUIP_SLOT column
        holds 63, 42, 43, 47 = VehLightTrim, Armor, HoodOrnament, VehicleCosmetic in
        ``EquipmentSlotDefinitions``). Read as EquipmentSlotDefinitions ids, 1..4 would be
        Head/Hands/Chest/Legs and ``VehicleHideSlotInfo`` would be referenced by nothing at
        all. The mapping is therefore taken as vehicle -> VehicleHideSlotInfo.ID and the
        header name treated as stale. Tagged ``joinConfidence: "inferred"``.

  D-V8  ``SpawnInfo`` / ``VehicleSpawnMappings`` are dead for this build: the mappings name
        only vehicles 11 and 2000, neither of which exists in ``Vehicles.txt``, and all 20
        SpawnInfo rows are bone names with zero offsets. Not joined; recorded as a gap.

  D-V9  ``VehicleResourceMappings``, ``VehicleResistMappings``, ``VehicleItemClasses``,
        ``VehicleCargoMappings``, ``VehicleAbilityLines``, ``VehicleSkillSets`` and
        ``ResourceInfo`` are header-only (0 rows) in this build. Fuel therefore reaches a
        vehicle only through ``Resources.txt`` id 50 / ``ResourceTypeFuel``, bound by
        ``ProfileResourceMappings`` profile 6. That row's ``BURN_PER_MSEC`` and
        ``BURN_TICK_MSEC`` are both 0, so the drain rate is not in the client.

USAGE
    python tools/data/derive_vehicles.py
    python tools/data/derive_vehicles.py --data-dir C:\Aug2017\out\data_aug \
                                         --out C:\Aug2017\out\data_aug\derived\vehicles.json
    python tools/data/derive_vehicles.py --lang de_de --locale ...\locale-de_de.json
    python tools/data/derive_vehicles.py --summary        # counts only, no file written

The tool is read-only against everything except its ``--out`` path.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import sys
from pathlib import Path
from typing import Any, Callable, Iterable, Sequence

# --- reuse this project's own readers rather than re-implementing either format ---------
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE))                       # tools/data  -> sheet.py
sys.path.insert(0, str(_HERE.parent / "locale"))     # tools/locale -> localedat.py

from sheet import Sheet                              # noqa: E402  (D-V1)
from localedat import text_key                       # noqa: E402  (D-V2)

SCHEMA = "cranberry/vehicles/1"
DEFAULT_DATA_DIR = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = DEFAULT_DATA_DIR / "derived" / "vehicles.json"
CLIENT_BUILD = "0.0.118.208059"


# ======================================================================================
# small helpers
# ======================================================================================

def num(value: str | None) -> int | float | None:
    """A numeric cell. Empty means 'unset' (docs/28 sec.1) and becomes None, not 0."""
    if value is None or value == "":
        return None
    try:
        f = float(value)
    except ValueError:
        return None
    i = int(f)
    return i if f == i else f


def flag(value: str | None) -> bool | None:
    n = num(value)
    return None if n is None else bool(n)


def text(value: str | None) -> str | None:
    return value if value else None


def ident(value: str | None) -> str | None:
    """An id cell: 0 means 'no reference', not 'row 0' (docs/28 sec.1)."""
    n = num(value)
    return None if not n else n


class Table:
    """One sheet, fully materialised. These sheets are tens of rows, not megabytes."""

    def __init__(self, data_dir: Path, name: str, optional: bool = False) -> None:
        self.name = name
        self.rows: list[dict[str, str]] = []
        #: the sheet's declared column names, in file order - needed by anything that has to
        #: compare two rows across every column rather than a named subset.
        self.columns: list[str] = []
        self.present = False
        path = data_dir / name
        if not path.exists():
            if not optional:
                raise SystemExit(f"missing required sheet: {path}")
            return
        self.present = True
        sheet = Sheet(path)
        self.columns = list(sheet.columns)
        self.rows = list(sheet)

    def by(self, column: str) -> dict[str, dict[str, str]]:
        return {r[column]: r for r in self.rows}

    def group(self, column: str) -> dict[str, list[dict[str, str]]]:
        out: dict[str, list[dict[str, str]]] = {}
        for r in self.rows:
            out.setdefault(r[column], []).append(r)
        return out

    def __len__(self) -> int:
        return len(self.rows)


class Provenance:
    """
    Collects ``field path -> "Sheet.txt:COLUMN"`` while the document is built, so the
    provenance block is generated by the same code that emits the values and cannot drift
    from them.
    """

    def __init__(self) -> None:
        self.map: dict[str, str] = {}

    def note(self, path: str, source: str) -> None:
        prev = self.map.get(path)
        if prev and prev != source:
            self.map[path] = f"{prev} | {source}"
        else:
            self.map[path] = source


# A field spec is (json_field, SHEET_COLUMN, cast). ``pick`` applies it and records where
# every emitted field came from.
Spec = tuple[str, str, Callable[[str | None], Any]]


def pick(row: dict[str, str], specs: Iterable[Spec], prov: Provenance,
         sheet: str, path: str) -> dict[str, Any]:
    out: dict[str, Any] = {}
    for field, column, cast in specs:
        out[field] = cast(row.get(column))
        prov.note(f"{path}.{field}", f"{sheet}:{column}")
    return out


def common_prefix(names: list[str]) -> str:
    if not names:
        return ""
    first = names[0]
    for i in range(len(first)):
        c = first[i]
        for other in names[1:]:
            if i >= len(other) or other[i] != c:
                return first[:i]
    return first


# ======================================================================================
# the derivation
# ======================================================================================

# Columns of MoveInfo.txt that describe purely client presentation and are deliberately not
# carried into the server dataset (camera shake, mouse/keyboard feel, dead zone, VFX ids).
MOVEINFO_CLIENT_ONLY = [
    "MOUSE_STRAFE_ROLL", "MOUSE_TURN_ROLL", "KEYBOARD_STRAFE_ROLL", "KEYBOARD_TURN_ROLL",
    "DEAD_ZONE_SIZE", "DEAD_ZONE_RATE", "DEAD_ZONE_SENSITIVITY",
    "DEAD_ZONE_INFLUENCE_EXPONENT", "CAMERA_SHAKE_INTENSITY", "CAMERA_SHAKE_SPEED",
    "CAMERA_SHAKE_CHANGE_SPEED", "CAMERA_SHAKE_INITIAL_INTENSITY",
    "INWARD_PITCH_MOD", "INWARD_YAW_MOD", "INWARD_ROLL_MOD",
    "KEY_FORWARD_EFFECT", "KEY_BACKWARD_EFFECT", "KEY_UP_EFFECT", "KEY_DOWN_EFFECT",
    "KEY_LEFT_EFFECT", "KEY_RIGHT_EFFECT", "KEY_BRAKE_EFFECT", "IDLE_EFFECT",
    "MOVING_EFFECT", "HOVER_EFFECT", "WAKE_EFFECT", "DEBRIS_EFFECT",
]

MOVEINFO_SPECS: list[Spec] = [
    ("moveInfoId", "ID", num),
    ("movementMode", "MOVEMENT_MODE", num),
    ("movementMedium", "MOVEMENT_MEDIUM", num),
    ("vehicleArchetype", "VEHICLE_ARCHETYPE", num),
    ("physicsInfo", "PHYSICS_INFO", num),
    ("controller", "CONTROLLER", num),
    ("speedScalar", "SPEED_SCALAR", num),
    ("agilityScalar", "AGILITY_SCALAR", num),
    ("maxForward", "MAX_FORWARD", num),
    ("maxReverse", "MAX_REVERSE", num),
    ("maxStrafe", "MAX_STRAFE", num),
    ("maxRise", "MAX_RISE", num),
    ("maxDive", "MAX_DIVE", num),
    ("maxRpmScalar", "MAX_RPM_SCALAR", num),
    ("accelForward", "ACCEL_FORWARD", num),
    ("accelReverse", "ACCEL_REVERSE", num),
    ("accelStrafe", "ACCEL_STRAFE", num),
    ("accelRise", "ACCEL_RISE", num),
    ("accelDive", "ACCEL_DIVE", num),
    ("brakeForward", "BRAKE_FORWARD", num),
    ("brakeReverse", "BRAKE_REVERSE", num),
    ("brakeStrafe", "BRAKE_STRAFE", num),
    ("estimatedMaxSpeed", "ESTIMATED_MAX_SPEED", num),
    ("hillClimb", "HILL_CLIMB", num),
    ("hillClimbDecayRange", "HILL_CLIMB_DECAY_RANGE", num),
    ("hillClimbMinPower", "HILL_CLIMB_MIN_POWER", num),
    ("minimumThrottle", "MINIMUM_THROTTLE", num),
    ("minTerminalVelocity", "MIN_TERM_VELOCITY", num),
    ("maxTerminalVelocity", "MAX_TERM_VELOCITY", num),
    ("landingGearHeight", "LANDING_GEAR_HEIGHT", num),
    ("flightCeiling", "FLIGHT_CEILING", num),
    ("diveDepth", "DIVE_DEPTH", num),
    ("changeModeSpeedPercent", "CHANGE_MODE_SPEED_PERCENT", num),
    ("turnRampUp", "TURN_RAMP_UP", num),
    ("rollMax", "ROLL_MAX", num),
    ("pitchMax", "PITCH_MAX", num),
    ("minTraction", "MIN_TRACTION", num),
    ("linearRedirect", "LINEAR_REDIRECT", num),
    ("linearDampening", "LINEAR_DAMPENING", num),
    ("moveYawRate", "MOVE_YAW_RATE", num),
    ("movePitchRate", "MOVE_PITCH_RATE", num),
    ("moveRollRate", "MOVE_ROLL_RATE", num),
    ("stillYawRate", "STILL_YAW_RATE", num),
    ("autoPilot", "AUTO_PILOT", num),
]

MOVEINFO_STEER_SPECS: list[Spec] = [
    ("factor", "STEER_FACTOR", num),
    ("exponent", "STEER_EXPONENT", num),
    ("spinFactor", "STEER_SPIN_FACTOR", num),
    ("spinExponent", "STEER_SPIN_EXPONENT", num),
    ("leanFactor", "STEER_LEAN_FACTOR", num),
    ("leanTurnFactor", "STEER_LEAN_TURN_FACTOR", num),
    ("compensateFactor", "STEER_COMPENSATE_FACTOR", num),
    ("burstFactorX", "STEER_BURST_FACTOR_X", num),
    ("burstFactorY", "STEER_BURST_FACTOR_Y", num),
    ("burstFactorZ", "STEER_BURST_FACTOR_Z", num),
    ("burstSpeed", "STEER_BURST_SPEED", num),
    ("burnoutAvoidanceSlipMax", "BURNOUT_AVOIDANCE_SLIP_MAX", num),
    ("burnoutAvoidanceFactor", "BURNOUT_AVOIDANCE_FACTOR", num),
]

MOVEINFO_DAMPENING_SPECS: list[Spec] = [
    ("x", "DAMPENING_X", num),
    ("y", "DAMPENING_Y", num),
    ("z", "DAMPENING_Z", num),
    ("angularScalar", "ANGULAR_DAMPENING_SCALAR", num),
    ("angularX", "ANGULAR_DAMPENING_X", num),
    ("angularY", "ANGULAR_DAMPENING_Y", num),
    ("angularZ", "ANGULAR_DAMPENING_Z", num),
]

VEHICLE_WORLD_SPECS: list[Spec] = [
    ("decaySeconds", "DECAY", num),
    ("sizeClass", "VEHICLE_SIZE", num),
    ("friction", "FRICTION", num),
    ("upsideDownFriction", "UPSIDE_DOWN_FRICTION", num),
    ("restitution", "RESTITUTION", num),
    ("upsideDownUndo", "UPSIDE_DOWN_UNDO", num),
    ("upsideDownDamagePulse", "UPSIDE_DOWN_DAMAGE_PULSE", num),
    ("collisionResistance", "COLLISION_RESISTANCE", num),
    ("impactDamageBlocked", "IMPACT_DAMAGE_BLOCKED", num),
    ("impactDamageMultiplier", "IMPACT_DAMAGE_MULTIPLIER", num),
    ("impactDamageInflictedMultiplier", "IMPACT_DAMAGE_INFLICTED_MULT", num),
    ("maxDismountSpeedOverride", "MAX_DISMOUNT_SPEED_OVERRIDE", num),
    ("landingHeight", "LANDING_HEIGHT", num),
    ("lowAltitudeHeight", "LOW_ALTITUDE_HEIGHT", num),
    ("canMeleeOccupants", "CAN_MELEE_OCCUPANTS", flag),
    ("clientRunRequirementSetId", "CLIENT_RUN_REQ_SET_ID", ident),
]

SEAT_SPECS: list[Spec] = [
    ("seatInfoId", "ID", num),
    ("index", "SEAT", num),
    ("seatType", "SEAT_TYPE", num),
    ("canFire", "CAN_FIRE", flag),
    ("canTarget", "CAN_TARGET", flag),
    ("canBail", "CAN_BAIL", flag),
    ("locked", "LOCKED", flag),
    ("enclosed", "ENCLOSED_SEAT", flag),
    ("detached", "DETACHED_SEAT", flag),
    ("playerVisible", "PLAYER_VISIBLE", flag),
    ("kickCorpse", "KICK_CORPSE", flag),
    ("isController", "CONTROLLER", flag),
    ("damagePercent", "DAMAGE_PERCENT", num),
    ("weaponMountId", "WEAPON_MOUNT", ident),
    ("secondaryWeaponMountId", "SECONDARY_WEAPON_MOUNT", ident),
    ("requirementId", "REQUIREMENT_ID", ident),
    ("clientDismountRequirementId", "CLIENT_DISMOUNT_REQUIREMENT_ID", ident),
    ("loopNextSeatInfoId", "SEAT_LOOP_NEXT_SEAT_ID", ident),
    ("isLoopEntrySeat", "SEAT_LOOP_ENTRY_SEAT", flag),
]

ENGINE_SPECS: list[Spec] = [
    ("engineInfoId", "ID", num),
    ("peakTorque", "PEAK_TORQUE", num),
    ("torqueCurveY", "TORQUE_CURVE_Y", num),
    ("engagedClutchDamp", "ENGAGED_CLUTCH_DAMP", num),
    ("disengagedClutchDamp", "DISENGAGED_CLUTCH_DAMP", num),
    ("clutchStrength", "CLUTCH_STRENGTH", num),
    ("switchGearSeconds", "SWITCH_GEAR_TIME", num),
]

DYNAMICS_SPECS: list[Spec] = [
    ("dynamicsInfoId", "ID", num),
    ("maxVelocity", "MAX_VELOCITY", num),
    ("turnTorque", "TURN_TORQUE", num),
    ("turnRate", "TURN_RATE", num),
    ("centerOfGravityY", "CENTER_OF_GRAVITY_Y", num),
]

# StringHashToValue rows Cranberry's vehicle system actually consumes. Everything else that
# mentions "Vehicle" in that sheet is camera/audio/UI and is emitted under clientOnly.
GLOBAL_SERVER_KEYS = {
    "Vehicle.DefaultMaxDismountSpeed",
    "Vehicle.DefaultMinDismountDamageSpeed",
    "Vehicle.DefaultDismountClientRequirement",
    "Vehicle.BatteryClassId",
    "Vehicle.SparkplugsClassId",
    "Vehicle.KeyClassId",
    "Vehicle.KeyItemId",
    "Vehicle.BundleGroupId",
    "VehicleInteractionCooldownMs",
    "VehicleSeatSwapCooldownMs",
    "ServerLogging.Vehicle.Mounting",
}

# --------------------------------------------------------------------------------------
# MOVEMENT_MODE profiling
#
# The client ships no enum-name strings for MOVEMENT_MODE (tools/pe/exeview.py findstr
# 'MovementMode' returns nothing; 'MOVEMENT_MODE' returns only the datasheet column name at
# 0x14360d440), so no meaning is asserted for a mode number. What IS emitted is measured
# from the row in hand - never looked up by mode number.
#
# An earlier revision keyed a hand-written sentence off MOVEMENT_MODE alone and shipped, for
# example, "byte-identical to mode 1 on every family" for mode 9. That is false on all four
# land families: mode 9 differs from mode 1 on 7 columns (MOVE_ROLL_RATE 0->0.5,
# STILL_ROLL_RATE 0->0.5, ANGULAR_DAMPENING_SCALAR 0->1, ANGULAR_DAMPENING_X 0->0.5,
# ANGULAR_DAMPENING_Y 0->1, ANGULAR_DAMPENING_Z 0->1, MOVING_EFFECT 1067/361->0), and mode 8
# differs from mode 2 on those plus TURN_RAMP_UP 1->0, plus MAX_FORWARD 100->85 on the ATV
# family. Only MOVING_EFFECT of those is client-only. Likewise the mode-1 sentence "ACCEL_-
# FORWARD 0, ESTIMATED_MAX_SPEED equals MAX_FORWARD" is false for MoveInfo 130 (parachute:
# ACCEL_FORWARD 5, MAX_FORWARD 18, ESTIMATED_MAX_SPEED 85) and 1337 (observer: 2 / 100 / 0),
# both of which are mode 1. So the profile is now computed per row and every claim in it is
# checked against that row before it is emitted.
# --------------------------------------------------------------------------------------

#: Columns excluded when comparing two MoveInfo rows for equality: the identity of the row
#: and the mode value being profiled.
_MODE_DIFF_EXCLUDE = ("ID", "MOVEMENT_MODE")


def _num_or_none(value: str | None) -> float | int | None:
    """Parse a sheet cell as a number, narrowing to int when it is whole. Returns ``None``
    for a blank or non-numeric cell, which then fails every trait predicate rather than
    producing a claim about a value that is not there."""
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    return int(f) if f == int(f) else f


#: Traits are (name, predicate) pairs evaluated against the row. A trait appears in the
#: output only when its predicate is True for THAT row, so a trait list is a statement about
#: the row and not about its mode number. ``None`` from ``_num_or_none`` fails every
#: predicate, so a blank or non-numeric cell never produces a claim.
_MODE_TRAITS: list[tuple[str, Any]] = [
    ("accelForwardZero", lambda g: g("ACCEL_FORWARD") == 0),
    ("estimatedMaxSpeedEqualsMaxForward",
     lambda g: g("ESTIMATED_MAX_SPEED") is not None
     and g("ESTIMATED_MAX_SPEED") == g("MAX_FORWARD")),
    ("estimatedMaxSpeedZero", lambda g: g("ESTIMATED_MAX_SPEED") == 0),
    ("immobileMaxForwardZero", lambda g: g("MAX_FORWARD") == 0),
    ("throttleCappedByMaxRpmScalar",
     lambda g: g("MAX_RPM_SCALAR") is not None and 0 < g("MAX_RPM_SCALAR") < 1),
    ("hillClimbDisabled",
     lambda g: g("HILL_CLIMB") == 0 and g("HILL_CLIMB_MIN_POWER") == 0),
]


def describe_mode(row: dict, siblings: dict[str, dict], columns: Sequence[str]) -> dict:
    """Profile one MoveInfo row against the other rows of the same vehicle.

    ``siblings`` maps MOVE_INFO_ID -> row for every mode of this vehicle, including ``row``
    itself. Returns:

    ``movementMode``            the raw value, carried through verbatim.
    ``identicalToMoveInfoIds``  sibling ids equal on every column but ID/MOVEMENT_MODE.
    ``nearestSibling``          when nothing is identical: the sibling with the fewest
                                differing columns, and the names of those columns.
    ``traits``                  the subset of _MODE_TRAITS that actually holds for this row.

    Nothing here is looked up by mode number, so the profile cannot contradict the values
    printed beside it.
    """
    compare = [c for c in columns if c not in _MODE_DIFF_EXCLUDE]
    rid = row.get("ID")

    identical: list[int] = []
    nearest_id = None
    nearest_cols: list[str] = []
    for sid, srow in siblings.items():
        if sid == rid:
            continue
        diff = [c for c in compare if row.get(c) != srow.get(c)]
        if not diff:
            identical.append(int(sid))
        elif nearest_id is None or len(diff) < len(nearest_cols):
            nearest_id, nearest_cols = int(sid), diff

    g = lambda col: _num_or_none(row.get(col))  # noqa: E731
    out: dict[str, Any] = {
        "movementMode": _num_or_none(row.get("MOVEMENT_MODE")),
        "identicalToMoveInfoIds": sorted(identical),
        "traits": [name for name, pred in _MODE_TRAITS if pred(g)],
    }
    if not identical and nearest_id is not None:
        out["nearestSibling"] = {
            "moveInfoId": nearest_id,
            "differingColumns": nearest_cols,
        }
    return out


def build(data_dir: Path, locale_path: Path, lang: str) -> tuple[dict[str, Any], dict[str, int]]:
    prov = Provenance()

    # ---- load ------------------------------------------------------------------------
    T = lambda n, optional=False: Table(data_dir, n, optional)  # noqa: E731
    vehicles = T("Vehicles.txt")
    move_map = T("VehicleMoveInfoMappings.txt")
    move_info = T("MoveInfo.txt")
    engine = T("EngineInfo.txt")
    dynamics = T("DynamicsInfo.txt")
    wheel = T("WheelInfo.txt")
    tire = T("TireInfo.txt")
    susp = T("SuspensionInfo.txt")
    seat_map = T("VehicleSeatMappings.txt")
    seat_info = T("SeatInfo.txt")
    seat_disallow = T("VehicleSeatDisallowMappings.txt")
    destroyed_map = T("DestroyedInfoMappings.txt")
    destroyed = T("DestroyedInfo.txt")
    dpart_map = T("DestroyedPartMappings.txt")
    dpart = T("DestroyedPart.txt")
    damage_map = T("DamageLevelMappings.txt")
    damage_lvl = T("DamageLevelInfo.txt")
    models = T("Models.txt")
    audio = T("VehicleAudioSetting.txt")
    skin_veh = T("VehicleSkinVehicles.txt")
    skin_mods = T("VehicleSkinMods.txt")
    skin_points = T("VehicleSkinModPoints.txt")
    hide_info = T("VehicleHideSlotInfo.txt")
    hide_map = T("VehicleHideSlotInfoMappings.txt")
    equip_slots = T("EquipmentSlotDefinitions.txt")
    resources = T("Resources.txt")
    profile_res = T("ProfileResourceMappings.txt")
    items = T("ClientItemDefinitions.txt")
    item_classes = T("ItemClasses.txt")
    containers = T("ContainerDefinitions.txt")
    consts = T("StringHashToValue.txt")
    veh_sets = T("VehicleSets.txt")

    empty_sheets = {
        n: len(t) for n, t in [
            ("VehicleResourceMappings.txt", T("VehicleResourceMappings.txt", True)),
            ("VehicleResistMappings.txt", T("VehicleResistMappings.txt", True)),
            ("VehicleItemClasses.txt", T("VehicleItemClasses.txt", True)),
            ("VehicleCargoMappings.txt", T("VehicleCargoMappings.txt", True)),
            ("VehicleAbilityLines.txt", T("VehicleAbilityLines.txt", True)),
            ("VehicleSkillSets.txt", T("VehicleSkillSets.txt", True)),
            ("ResourceInfo.txt", T("ResourceInfo.txt", True)),
        ] if len(t) == 0
    }

    locale: dict[str, str] = json.loads(locale_path.read_text(encoding="utf-8"))

    def loc(string_id: str | int | None) -> str | None:
        """D-V2. 0 / empty means 'unset'."""
        n = num(str(string_id)) if string_id is not None else None
        if not n:
            return None
        return locale.get(str(text_key(int(n))))

    # ---- indexes ---------------------------------------------------------------------
    move_by_id = move_info.by("ID")
    engine_by_id = engine.by("ID")
    dyn_by_id = dynamics.by("ID")
    wheel_by_id = wheel.by("ID")
    tire_by_id = tire.by("ID")
    susp_by_id = susp.by("ID")
    seat_by_id = seat_info.by("ID")
    destroyed_by_id = destroyed.by("ID")
    dpart_by_id = dpart.by("ID")
    damage_by_id = damage_lvl.by("ID")
    models_by_id = models.by("ID")
    audio_by_id = audio.by("ID")
    equip_by_id = equip_slots.by("ID")
    hide_by_id = hide_info.by("ID")
    items_by_id = items.by("ID")
    skinpoint_by_id = skin_points.by("ID")

    move_ids_for_vehicle = {k: [r["MOVE_INFO_ID"] for r in v]
                            for k, v in move_map.group("VEHICLE_ID").items()}
    seat_ids_for_vehicle = {k: [r["SEAT_INFO_ID"] for r in v]
                            for k, v in seat_map.group("VEHICLE_ID").items()}
    disallow_for_seat: dict[str, list[int]] = {}
    for r in seat_disallow.rows:
        disallow_for_seat.setdefault(r["SEAT_INFO_ID"], []).append(int(r["PROFILE_CLASSIFICATION_ID"]))
    npcs_for_destroyed: dict[str, list[int]] = {}
    for r in destroyed_map.rows:
        npcs_for_destroyed.setdefault(r["DESTROYED_INFO_ID"], []).append(int(r["NPC_ID"]))
    parts_for_destroyed: dict[str, list[str]] = {}
    for r in dpart_map.rows:
        parts_for_destroyed.setdefault(r["DESTROYED_INFO_ID"], []).append(r["DESTROYED_PART_ID"])
    damage_for_npc: dict[str, list[str]] = {}
    for r in damage_map.rows:
        damage_for_npc.setdefault(r["NPC_ID"], []).append(r["DAMAGE_LEVEL_ID"])
    hide_for_vehicle: dict[str, list[str]] = {}
    for r in hide_map.rows:                                      # D-V7
        hide_for_vehicle.setdefault(r["VEHICLE_ID"], []).append(r["EQUIP_SLOT"])
    skins_for_vehicle = skin_mods.group("VEHICLE_ID")
    skinveh_by_id = skin_veh.by("VEHICLE_ID")

    # ---- D-V3/D-V4/D-V5: physics families -------------------------------------------
    def family_base(vehicle_id: str) -> int | None:
        ids = sorted(int(x) for x in move_ids_for_vehicle.get(vehicle_id, []))
        for candidate in ids:
            if candidate % 10:
                continue
            span = {candidate + k for k in range(8)}
            if len(span & set(ids)) >= 4 and str(candidate) in dyn_by_id:
                return candidate
        return None

    corner_slots = ["FIRST", "SECOND", "THIRD", "FOURTH"]

    def family_rows(by_id: dict[str, dict[str, str]], base: int) -> list[dict[str, str]]:
        return [by_id[str(base + k)] for k in range(4) if str(base + k) in by_id]

    def slot_of(row: dict[str, str], prefix: str) -> tuple[int | None, str | None]:
        for i, slot in enumerate(corner_slots, 1):
            v = row.get(f"{prefix}_{slot}", "")
            if v:
                return i, v
        return None, None

    def family_name(base: int) -> str | None:                    # D-V5
        names = [n for _, n in (slot_of(r, "WHEEL") for r in family_rows(wheel_by_id, base)) if n]
        return common_prefix(names) or None

    def axle_side(name: str, fam: str) -> tuple[str | None, str | None]:
        """Derived from the name string, not assumed from the slot index."""
        tail = name[len(fam):] if name.startswith(fam) else name
        axle = "front" if "Front" in tail else ("rear" if "Rear" in tail else None)
        side = "left" if "Left" in tail else ("right" if "Right" in tail else None)
        return axle, side

    # ---- D-V6: destruction, keyed through the model file name -----------------------
    def destroyed_family(info_row: dict[str, str]) -> str | None:
        model = models_by_id.get(info_row.get("DESTROYED_MODEL", ""))
        if not model:
            return None
        fname = model.get("MODEL_FILE_NAME", "")
        for base in sorted({b for b in (family_base(v["ID"]) for v in vehicles.rows) if b}):
            fam = family_name(base)
            if fam and fam.lower() in fname.lower():
                return fam
        return None

    infos_by_family: dict[str, list[str]] = {}
    for info in destroyed.rows:
        fam = destroyed_family(info)
        if fam:
            infos_by_family.setdefault(fam, []).append(info["ID"])
    for k in infos_by_family:
        infos_by_family[k].sort(key=int)

    vehicles_by_family: dict[str, list[str]] = {}
    for v in vehicles.rows:
        b = family_base(v["ID"])
        fam = family_name(b) if b else None
        if fam:
            vehicles_by_family.setdefault(fam, []).append(v["ID"])
    for k in vehicles_by_family:
        vehicles_by_family[k].sort(key=int)

    destroyed_for_vehicle: dict[str, str] = {}
    for fam, vids in vehicles_by_family.items():
        for i, vid in enumerate(vids):
            infos = infos_by_family.get(fam, [])
            if i < len(infos):
                destroyed_for_vehicle[vid] = infos[i]

    # ---- per-vehicle documents -------------------------------------------------------
    VP = "vehicles[]"
    kind_of_type = {"5": "land", "8": "parachute", "10": "observer"}
    out_vehicles: list[dict[str, Any]] = []
    mode_rows = 0
    seat_rows = 0

    for v in sorted(vehicles.rows, key=lambda r: int(r["ID"])):
        vid = v["ID"]
        base = family_base(vid)
        fam = family_name(base) if base is not None else None

        doc: dict[str, Any] = {"id": int(vid)}
        prov.note(f"{VP}.id", "Vehicles.txt:ID")

        doc["name"] = loc(v["NAME_ID"])
        doc["description"] = loc(v["DESCRIPTION_ID"])
        doc["nameStringId"] = num(v["NAME_ID"])
        doc["descriptionStringId"] = num(v["DESCRIPTION_ID"])
        prov.note(f"{VP}.name", "Vehicles.txt:NAME_ID -> locale-%s.json (D-V2)" % lang)
        prov.note(f"{VP}.description", "Vehicles.txt:DESCRIPTION_ID -> locale-%s.json (D-V2)" % lang)
        prov.note(f"{VP}.nameStringId", "Vehicles.txt:NAME_ID")
        prov.note(f"{VP}.descriptionStringId", "Vehicles.txt:DESCRIPTION_ID")

        doc["kind"] = kind_of_type.get(v["VEHICLE_TYPE"])
        doc["vehicleType"] = num(v["VEHICLE_TYPE"])
        doc["controlType"] = num(v["VEHICLE_CONTROL_TYPE"])
        doc["propulsionType"] = num(v["PROPULSION_TYPE"])
        prov.note(f"{VP}.kind", "Vehicles.txt:VEHICLE_TYPE (5=land, 8=parachute, 10=observer)")
        prov.note(f"{VP}.vehicleType", "Vehicles.txt:VEHICLE_TYPE")
        prov.note(f"{VP}.controlType", "Vehicles.txt:VEHICLE_CONTROL_TYPE")
        prov.note(f"{VP}.propulsionType", "Vehicles.txt:PROPULSION_TYPE")

        doc["physicsFamily"] = (
            None if base is None else
            {"base": base, "name": fam, "joinConfidence": "inferred",
             "evidence": "D-V3/D-V4/D-V5"}
        )
        prov.note(f"{VP}.physicsFamily.base",
                  "derived from VehicleMoveInfoMappings.txt:MOVE_INFO_ID (D-V3)")
        prov.note(f"{VP}.physicsFamily.name",
                  "derived from WheelInfo.txt:WHEEL_FIRST..WHEEL_FOURTH (D-V5)")

        doc["world"] = pick(v, VEHICLE_WORLD_SPECS, prov, "Vehicles.txt", f"{VP}.world")

        # -- drive modes ---------------------------------------------------------------
        modes: list[dict[str, Any]] = []
        mode_ids = sorted(move_ids_for_vehicle.get(vid, []), key=int)
        # Every MoveInfo row this vehicle owns, so a mode can be profiled against its own
        # family rather than against a hard-coded expectation (see describe_mode).
        siblings = {i: move_by_id[i] for i in mode_ids if i in move_by_id}
        for ordinal, mid in enumerate(mode_ids):
            row = move_by_id.get(mid)
            if row is None:
                continue
            m = pick(row, MOVEINFO_SPECS, prov, "MoveInfo.txt", f"{VP}.drive.modes[]")
            m["ordinal"] = ordinal
            m["steer"] = pick(row, MOVEINFO_STEER_SPECS, prov, "MoveInfo.txt",
                              f"{VP}.drive.modes[].steer")
            m["dampening"] = pick(row, MOVEINFO_DAMPENING_SPECS, prov, "MoveInfo.txt",
                                  f"{VP}.drive.modes[].dampening")
            m["modeProfile"] = describe_mode(row, siblings, move_info.columns)
            modes.append(m)
            mode_rows += 1
        prov.note(f"{VP}.drive.modes[].ordinal",
                  "position in VehicleMoveInfoMappings.txt:MOVE_INFO_ID ascending (D-V3)")
        prov.note(f"{VP}.drive.modes[].modeProfile",
                  "computed from THIS MoveInfo.txt row: column-wise diff against the other "
                  "MoveInfo rows of the same vehicle, plus the value predicates that hold "
                  "for the row. Not looked up by MOVEMENT_MODE - no enum names ship in the "
                  "client (exeview findstr 'MovementMode' returns nothing)")

        drive: dict[str, Any] = {"modes": modes}
        default_mode = next((m for m in modes if m["movementMode"] == 1), None)
        drive["defaultMoveInfoId"] = default_mode["moveInfoId"] if default_mode else None
        prov.note(f"{VP}.drive.defaultMoveInfoId",
                  "MoveInfo.txt row of this vehicle whose MOVEMENT_MODE is 1")

        if base is not None and str(base) in dyn_by_id:
            drive["dynamics"] = pick(dyn_by_id[str(base)], DYNAMICS_SPECS, prov,
                                     "DynamicsInfo.txt", f"{VP}.drive.dynamics")
            drive["dynamics"]["joinConfidence"] = "inferred"   # D-V4
        else:
            drive["dynamics"] = None
        doc["drive"] = drive

        # -- engine --------------------------------------------------------------------
        if base is not None and str(base) in engine_by_id:
            erow = engine_by_id[str(base)]
            eng = pick(erow, ENGINE_SPECS, prov, "EngineInfo.txt", f"{VP}.engine")
            eng["gearRatios"] = {
                "reverse": num(erow["REVERSE_GEAR"]),
                "first": num(erow["FIRST_GEAR"]),
                "second": num(erow["SECOND_GEAR"]),
                "third": num(erow["THIRD_GEAR"]),
                "fourth": num(erow["FOURTH_GEAR"]),
            }
            eng["joinConfidence"] = "inferred"                 # D-V4
            for g, c in [("reverse", "REVERSE_GEAR"), ("first", "FIRST_GEAR"),
                         ("second", "SECOND_GEAR"), ("third", "THIRD_GEAR"),
                         ("fourth", "FOURTH_GEAR")]:
                prov.note(f"{VP}.engine.gearRatios.{g}", f"EngineInfo.txt:{c}")
            doc["engine"] = eng
        else:
            doc["engine"] = None

        # -- wheels / tires / suspension ----------------------------------------------
        wheels: list[dict[str, Any]] = []
        if base is not None:
            for wrow in family_rows(wheel_by_id, base):
                slot, wname = slot_of(wrow, "WHEEL")
                axle, side = axle_side(wname or "", fam or "")
                entry: dict[str, Any] = {
                    "wheelInfoId": num(wrow["ID"]),
                    "slot": slot,
                    "assetName": wname,
                    "axle": axle,
                    "side": side,
                    "maxBrake": num(wrow["MAX_BRAKE"]),
                    "maxHandBrake": num(wrow["MAX_HAND_BRAKE"]),
                    "maxSteerDegrees": num(wrow["MAX_STEER"]),
                }
                trow = tire_by_id.get(wrow["ID"])
                if trow:
                    tslot, tname = slot_of(trow, "TIRE")
                    entry["tire"] = {
                        "tireInfoId": num(trow["ID"]), "slot": tslot, "assetName": tname,
                        "longitudinalStiffness": num(trow["LONG_STIFF"]),
                        "camberStiffness": num(trow["CAMBER_STIFF"]),
                    }
                else:
                    entry["tire"] = None
                wheels.append(entry)
        for f, c in [("wheelInfoId", "ID"), ("maxBrake", "MAX_BRAKE"),
                     ("maxHandBrake", "MAX_HAND_BRAKE"), ("maxSteerDegrees", "MAX_STEER")]:
            prov.note(f"{VP}.wheels[].{f}", f"WheelInfo.txt:{c}")
        prov.note(f"{VP}.wheels[].slot", "WheelInfo.txt: index of the non-empty WHEEL_FIRST..WHEEL_FOURTH cell")
        prov.note(f"{VP}.wheels[].assetName", "WheelInfo.txt:WHEEL_FIRST..WHEEL_FOURTH (the APEX lookup string, D-V4)")
        prov.note(f"{VP}.wheels[].axle", "derived from WheelInfo.txt asset name ('Front'/'Rear' token)")
        prov.note(f"{VP}.wheels[].side", "derived from WheelInfo.txt asset name ('Left'/'Right' token)")
        for f, c in [("tireInfoId", "ID"), ("longitudinalStiffness", "LONG_STIFF"),
                     ("camberStiffness", "CAMBER_STIFF")]:
            prov.note(f"{VP}.wheels[].tire.{f}", f"TireInfo.txt:{c}")
        prov.note(f"{VP}.wheels[].tire.assetName", "TireInfo.txt:TIRE_FIRST..TIRE_FOURTH")
        prov.note(f"{VP}.wheels[].tire.slot", "TireInfo.txt: index of the non-empty TIRE_* cell")
        doc["wheels"] = wheels

        suspension: list[dict[str, Any]] = []
        if base is not None:
            for srow in family_rows(susp_by_id, base):
                sslot, sname = slot_of(srow, "SUSPENSION")
                axle, _ = axle_side(sname or "", fam or "")
                suspension.append({
                    "suspensionInfoId": num(srow["ID"]),
                    "slot": sslot,
                    "assetName": sname,
                    "axle": axle,
                    "springFrequency": num(srow["SPRING_FREQUENCY"]),
                    "springDamperRatio": num(srow["SPRING_DAMPER_RATIO"]),
                })
        for f, c in [("suspensionInfoId", "ID"), ("springFrequency", "SPRING_FREQUENCY"),
                     ("springDamperRatio", "SPRING_DAMPER_RATIO")]:
            prov.note(f"{VP}.suspension[].{f}", f"SuspensionInfo.txt:{c}")
        prov.note(f"{VP}.suspension[].assetName", "SuspensionInfo.txt:SUSPENSION_FIRST..SUSPENSION_FOURTH")
        prov.note(f"{VP}.suspension[].slot", "SuspensionInfo.txt: index of the non-empty SUSPENSION_* cell")
        prov.note(f"{VP}.suspension[].axle", "derived from SuspensionInfo.txt asset name")
        doc["suspension"] = suspension

        # -- seats ---------------------------------------------------------------------
        seats: list[dict[str, Any]] = []
        for sid in sorted(seat_ids_for_vehicle.get(vid, []), key=int):
            srow = seat_by_id.get(sid)
            if srow is None:
                continue
            s = pick(srow, SEAT_SPECS, prov, "SeatInfo.txt", f"{VP}.seats[]")
            s["isDriver"] = (s["index"] == 0)
            s["exposedToFire"] = (s["enclosed"] is False)
            s["disallowedProfileClassifications"] = sorted(disallow_for_seat.get(sid, []))
            seats.append(s)
            seat_rows += 1
        seats.sort(key=lambda s: (s["index"] if s["index"] is not None else 99))
        prov.note(f"{VP}.seats[].isDriver", "SeatInfo.txt:SEAT == 0")
        prov.note(f"{VP}.seats[].exposedToFire", "SeatInfo.txt:ENCLOSED_SEAT == 0")
        prov.note(f"{VP}.seats[].disallowedProfileClassifications",
                  "VehicleSeatDisallowMappings.txt:PROFILE_CLASSIFICATION_ID")
        prov.note(f"{VP}.seats[] membership", "VehicleSeatMappings.txt:VEHICLE_ID -> SEAT_INFO_ID")
        doc["seats"] = seats
        doc["seatCount"] = len(seats)
        prov.note(f"{VP}.seatCount", "count of VehicleSeatMappings.txt rows for this VEHICLE_ID")

        # -- destruction ---------------------------------------------------------------
        info_id = destroyed_for_vehicle.get(vid)
        if info_id and info_id in destroyed_by_id:
            irow = destroyed_by_id[info_id]
            model = models_by_id.get(irow["DESTROYED_MODEL"], {})
            npc_ids = sorted(npcs_for_destroyed.get(info_id, []))
            levels: list[dict[str, Any]] = []
            for npc in npc_ids:
                for lid in damage_for_npc.get(str(npc), []):
                    lrow = damage_by_id.get(lid)
                    if lrow:
                        levels.append({
                            "damageLevelId": num(lrow["ID"]),
                            "npcId": npc,
                            "damagePercent": num(lrow["DAMAGE_PERCENT"]),
                            "moveInfoOverride": ident(lrow["MOVE_INFO_OVERRIDE"]),
                            "controlDampening": num(lrow["CONTROL_DAMPENING"]),
                            "stopEngine": flag(lrow["STOP_ENGINE"]),
                            "triggeredAbilityId": ident(lrow["TRIGGERED_ABILITY"]),
                        })
            levels.sort(key=lambda x: -(x["damagePercent"] or 0))
            parts = []
            for pid in parts_for_destroyed.get(info_id, []):
                prow = dpart_by_id.get(pid)
                if prow:
                    parts.append({
                        "destroyedPartId": num(prow["ID"]),
                        "projectileId": num(prow["PROJECTILE"]),
                        "offset": [num(prow["OFFSET_X"]), num(prow["OFFSET_Y"]),
                                   num(prow["OFFSET_Z"])],
                        "socket": text(prow["SOCKET"]),
                    })
            doc["destruction"] = {
                "destroyedInfoId": num(info_id),
                "joinConfidence": "inferred",                  # D-V6
                "evidence": "D-V6",
                "npcIds": npc_ids,
                "destroyedModelId": num(irow["DESTROYED_MODEL"]),
                "destroyedModelFile": text(model.get("MODEL_FILE_NAME")),
                "corpseExpireSeconds": num(irow["CORPSE_EXPIRE_SECONDS"]),
                "triggeredAbilityId": ident(irow["TRIGGERED_ABILITY"]),
                "suicideTriggeredAbilityId": ident(irow["SUICIDE_TRIGGERED_ABILITY"]),
                "deathCompositeEffectId": ident(irow["DEATH_COMPOSITE_EFFECT"]),
                "corpseCompositeEffectId": ident(irow["CORPSE_COMPOSITE_EFFECT"]),
                "damageLevels": levels,
                "debrisParts": parts,
            }
        else:
            doc["destruction"] = None
        for f, c in [("destroyedInfoId", "ID"), ("destroyedModelId", "DESTROYED_MODEL"),
                     ("corpseExpireSeconds", "CORPSE_EXPIRE_SECONDS"),
                     ("triggeredAbilityId", "TRIGGERED_ABILITY"),
                     ("suicideTriggeredAbilityId", "SUICIDE_TRIGGERED_ABILITY"),
                     ("deathCompositeEffectId", "DEATH_COMPOSITE_EFFECT"),
                     ("corpseCompositeEffectId", "CORPSE_COMPOSITE_EFFECT")]:
            prov.note(f"{VP}.destruction.{f}", f"DestroyedInfo.txt:{c}")
        prov.note(f"{VP}.destruction.destroyedModelFile", "Models.txt:MODEL_FILE_NAME")
        prov.note(f"{VP}.destruction.npcIds", "DestroyedInfoMappings.txt:NPC_ID (D-V6)")
        prov.note(f"{VP}.destruction.damageLevels[]",
                  "DamageLevelMappings.txt:NPC_ID -> DamageLevelInfo.txt")
        prov.note(f"{VP}.destruction.debrisParts[]",
                  "DestroyedPartMappings.txt -> DestroyedPart.txt")

        # -- cosmetics / appearance ----------------------------------------------------
        hidden = []
        for hid in sorted(hide_for_vehicle.get(vid, []), key=int):
            hrow = hide_by_id.get(hid)
            if hrow is None:
                continue
            slot = equip_by_id.get(hrow["EQUIP_SLOT"], {})
            hidden.append({
                "hideSlotInfoId": num(hrow["ID"]),
                "equipSlotId": num(hrow["EQUIP_SLOT"]),
                "equipSlotName": text(slot.get("SLOT_NAME")),
                "hideFirstPerson": flag(hrow["HIDE_FIRST_PERSON"]),
                "hideThirdPerson": flag(hrow["HIDE_THIRD_PERSON"]),
            })
        skins = []
        for srow in skins_for_vehicle.get(vid, []):
            item = items_by_id.get(srow["ITEM_ID"], {})
            point = skinpoint_by_id.get(srow["MOD_POINT"], {})
            skins.append({
                "skinModId": num(srow["ID"]),
                "modPointId": num(srow["MOD_POINT"]),
                "modPointName": loc(point.get("NAME_ID")),
                "itemId": num(srow["ITEM_ID"]),
                "itemName": loc(item.get("NAME_ID")),
                "itemClass": ident(item.get("ITEM_CLASS")),
            })
        skins.sort(key=lambda s: s["itemId"] or 0)
        sv = skinveh_by_id.get(vid)
        appearance = None
        if sv:
            appearance = {
                "iconId": ident(sv["ICON_ID"]),
                "displayName": loc(sv["NAME_ID"]),
                "displayDescription": loc(sv["DESCRIPTION_ID"]),
                "npcProxyId": ident(sv["NPC_PROXY_ID"]),
                "staticViewLocation": text(sv["STATIC_VIEW_LOCATION"]),
            }
        doc["cosmetics"] = {"hiddenSlots": hidden, "skins": skins, "appearance": appearance}
        prov.note(f"{VP}.cosmetics.hiddenSlots[]",
                  "VehicleHideSlotInfoMappings.txt -> VehicleHideSlotInfo.txt -> "
                  "EquipmentSlotDefinitions.txt:SLOT_NAME (D-V7)")
        prov.note(f"{VP}.cosmetics.skins[]",
                  "VehicleSkinMods.txt -> ClientItemDefinitions.txt:NAME_ID / "
                  "VehicleSkinModPoints.txt")
        prov.note(f"{VP}.cosmetics.appearance", "VehicleSkinVehicles.txt")

        # -- client-only leftovers, kept so nothing is silently dropped ----------------
        arow = audio_by_id.get(v["AUDIO_ID"])
        doc["clientOnly"] = {
            "audioSettingId": ident(v["AUDIO_ID"]),
            "audioGearBands": (
                None if arow is None else
                [{"gear": g,
                  "minVelocity": num(arow[f"GEAR{g}_MIN_VEL"]),
                  "maxVelocity": num(arow[f"GEAR{g}_MAX_VEL"]),
                  "minRpm": num(arow[f"GEAR{g}_MIN_RPM"]),
                  "maxRpm": num(arow[f"GEAR{g}_MAX_RPM"]),
                  "maxAccel": num(arow[f"GEAR{g}_MAX_ACCEL"])} for g in range(1, 6)]
            ),
            "treadEffectId": ident(v["TREAD_EFFECT"]),
            "minimapRange": num(v["MINIMAP_RANGE"]),
            "thermalRange": num(v["THERMAL_RANGE"]),
            "infraredRange": num(v["INFRARED_RANGE"]),
        }
        prov.note(f"{VP}.clientOnly.audioSettingId", "Vehicles.txt:AUDIO_ID")
        prov.note(f"{VP}.clientOnly.audioGearBands", "VehicleAudioSetting.txt:GEAR1..GEAR5_*")
        prov.note(f"{VP}.clientOnly.treadEffectId", "Vehicles.txt:TREAD_EFFECT")
        prov.note(f"{VP}.clientOnly.minimapRange", "Vehicles.txt:MINIMAP_RANGE")
        prov.note(f"{VP}.clientOnly.thermalRange", "Vehicles.txt:THERMAL_RANGE")
        prov.note(f"{VP}.clientOnly.infraredRange", "Vehicles.txt:INFRARED_RANGE")

        out_vehicles.append(doc)

    # ---- shared: fuel ----------------------------------------------------------------
    fuel_rows = [r for r in resources.rows if r["TYPE_NAME"] == "ResourceTypeFuel"]
    fuel_profiles = sorted({int(r["PROFILE_ID"]) for r in profile_res.rows
                            if r["RESOURCE_ID"] in {x["ID"] for x in fuel_rows}})
    primary_fuel = next((r for r in fuel_rows
                         if r["ID"] in {p["RESOURCE_ID"] for p in profile_res.rows}), None)

    def fuel_doc(r: dict[str, str]) -> dict[str, Any]:
        return {
            "resourceId": num(r["ID"]),
            "resourceType": num(r["RESOURCE_TYPE"]),
            "typeName": text(r["TYPE_NAME"]),
            "name": loc(r["NAME_ID"]),
            "description": loc(r["DESCRIPTION_ID"]),
            "initialValue": num(r["INITIAL_VALUE"]),
            "maxValue": num(r["MAX_VALUE"]),
            "burnPerMsec": num(r["BURN_PER_MSEC"]),
            "burnTickMsec": num(r["BURN_TICK_MSEC"]),
            "regenPerMs": num(r["REGEN_PER_MS"]),
            "regenDamageInterruptMs": num(r["REGEN_DAMAGE_INTERRUPT_MS"]),
            "packetBroadcastRange": num(r["PACKET_BROADCAST_RANGE"]),
            "flagOnClient": flag(r["FLAG_ON_CLIENT"]),
            "flagProfileScope": flag(r["FLAG_PROFILE_SCOPE"]),
            "flagNotVehicleMountScope": flag(r["FLAG_NOT_VEH_MOUNT_SCOPE"]),
            "flagDoNotTick": flag(r["FLAG_DONOT_TICK"]),
            "flagDoNotPersist": flag(r["FLAG_DONOT_PERSIST"]),
        }

    for f, c in [("resourceId", "ID"), ("resourceType", "RESOURCE_TYPE"),
                 ("typeName", "TYPE_NAME"), ("initialValue", "INITIAL_VALUE"),
                 ("maxValue", "MAX_VALUE"), ("burnPerMsec", "BURN_PER_MSEC"),
                 ("burnTickMsec", "BURN_TICK_MSEC"), ("regenPerMs", "REGEN_PER_MS"),
                 ("regenDamageInterruptMs", "REGEN_DAMAGE_INTERRUPT_MS"),
                 ("packetBroadcastRange", "PACKET_BROADCAST_RANGE"),
                 ("flagOnClient", "FLAG_ON_CLIENT"),
                 ("flagProfileScope", "FLAG_PROFILE_SCOPE"),
                 ("flagNotVehicleMountScope", "FLAG_NOT_VEH_MOUNT_SCOPE"),
                 ("flagDoNotTick", "FLAG_DONOT_TICK"),
                 ("flagDoNotPersist", "FLAG_DONOT_PERSIST")]:
        prov.note(f"shared.fuel.resource.{f}", f"Resources.txt:{c}")
    prov.note("shared.fuel.resource.name", "Resources.txt:NAME_ID -> locale (D-V2)")
    prov.note("shared.fuel.resource.description", "Resources.txt:DESCRIPTION_ID -> locale (D-V2)")
    prov.note("shared.fuel.boundToProfileIds", "ProfileResourceMappings.txt:PROFILE_ID")
    prov.note("shared.fuel.displayName",
              "Resources.txt:NAME_ID of the first ResourceTypeFuel row that carries one "
              "(id 396/538/541/589/607 = 253 'Fuel'); the bound row id 50 has NAME_ID 0")
    prov.note("shared.fuel.displayDescription",
              "Resources.txt:DESCRIPTION_ID 1218 -> locale (D-V2)")

    part_slot_ids = ["69", "70", "71"]
    part_slots = []
    for sid in part_slot_ids:
        srow = equip_by_id.get(sid)
        if srow:
            part_slots.append({
                "equipSlotId": num(srow["ID"]),
                "slotName": text(srow["SLOT_NAME"]),
                "name": loc(srow["NAME_ID"]),
                "groupId": ident(srow["GROUP_ID"]),
            })
    prov.note("shared.vehicleParts.slots[]",
              "EquipmentSlotDefinitions.txt:ID/SLOT_NAME/NAME_ID (ids 69/70/71)")

    class_by_id = item_classes.by("ID")
    part_class_ids = {"25019", "25020", "25021", "25076"}
    part_items = []
    for r in items.rows:
        if r["ITEM_CLASS"] in part_class_ids:
            part_items.append({
                "itemId": num(r["ID"]),
                "itemClass": num(r["ITEM_CLASS"]),
                "itemClassName": loc(class_by_id.get(r["ITEM_CLASS"], {}).get("NAME_ID")),
                "name": loc(r["NAME_ID"]),
                "codeFactory": text(r["CODE_FACTORY_NAME"]),
                "param1": num(r["PARAM1"]),
            })
    prov.note("shared.vehicleParts.items[]",
              "ClientItemDefinitions.txt:ID/ITEM_CLASS/NAME_ID/CODE_FACTORY_NAME/PARAM1 "
              "filtered to the classes named by StringHashToValue.txt Vehicle.*ClassId")

    # storage: item 1541 is the only EquippableContainer in a vehicle item class; its
    # PARAM1 is the ContainerDefinitions id.
    storage = None
    trunk = items_by_id.get("1541")
    if trunk:
        crow = containers.by("ID").get(trunk["PARAM1"])
        storage = {
            "itemId": num(trunk["ID"]),
            "itemClass": num(trunk["ITEM_CLASS"]),
            "itemClassName": loc(class_by_id.get(trunk["ITEM_CLASS"], {}).get("NAME_ID")),
            "containerDefinitionId": num(trunk["PARAM1"]),
            "maximumSlots": num(crow["MAXIMUM_SLOTS"]) if crow else None,
            "maxBulk": num(crow["MAX_BULK"]) if crow else None,
            "allowedItemClassId": ident(crow["ALLOWED_ITEM_CLASS_ID"]) if crow else None,
            "note": "no sheet in this build binds a container to a vehicle id "
                    "(VehicleCargoMappings.txt is header-only); the binding is server-side",
        }
    prov.note("shared.storage.containerDefinitionId",
              "ClientItemDefinitions.txt:PARAM1 of item 1541 (EquippableContainer, "
              "ITEM_CLASS 25019)")
    prov.note("shared.storage.maximumSlots", "ContainerDefinitions.txt:MAXIMUM_SLOTS")

    # ---- shared: named constants -----------------------------------------------------
    globals_server: dict[str, Any] = {}
    globals_client: dict[str, Any] = {}
    # StringHashToValue.txt is a two-column name^value sheet; take the columns positionally
    # so a renamed header does not break the read.
    name_col, value_col = consts.rows[0].keys().__iter__().__next__(), list(consts.rows[0])[1]
    for r in consts.rows:
        name = r[name_col]
        if "vehicle" not in name.lower():
            continue
        value = r[value_col]
        target = globals_server if name in GLOBAL_SERVER_KEYS else globals_client
        target[name] = num(value) if num(value) is not None else value
    prov.note("shared.constants.server", "StringHashToValue.txt (name^value), Vehicle* rows")
    prov.note("shared.constants.client", "StringHashToValue.txt (name^value), Vehicle* rows")

    # ---- shared: vehicle sets --------------------------------------------------------
    known_ids = {r["ID"] for r in vehicles.rows}
    sets: dict[str, dict[str, Any]] = {}
    for r in veh_sets.rows:
        e = sets.setdefault(r["ID"], {"setId": int(r["ID"]), "vehicleIds": [],
                                      "unknownVehicleIds": []})
        (e["vehicleIds"] if r["VEHICLE_ID"] in known_ids
         else e["unknownVehicleIds"]).append(int(r["VEHICLE_ID"]))
    prov.note("shared.vehicleSets[]", "VehicleSets.txt:ID/VEHICLE_ID")

    # ---- gaps ------------------------------------------------------------------------
    server_side_gaps = SERVER_SIDE_GAPS

    # ---- assemble --------------------------------------------------------------------
    doc = {
        "schema": SCHEMA,
        "schemaNotes": SCHEMA_NOTES,
        "generator": {
            "tool": "Server/tools/data/derive_vehicles.py",
            "generatedUtc": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "clientBuild": CLIENT_BUILD,
            "locale": lang,
            "dataDir": str(data_dir),
            "sourceSheets": sorted({
                "Vehicles.txt", "VehicleMoveInfoMappings.txt", "MoveInfo.txt",
                "EngineInfo.txt", "DynamicsInfo.txt", "WheelInfo.txt", "TireInfo.txt",
                "SuspensionInfo.txt", "VehicleSeatMappings.txt", "SeatInfo.txt",
                "VehicleSeatDisallowMappings.txt", "DestroyedInfoMappings.txt",
                "DestroyedInfo.txt", "DestroyedPartMappings.txt", "DestroyedPart.txt",
                "DamageLevelMappings.txt", "DamageLevelInfo.txt", "Models.txt",
                "VehicleAudioSetting.txt", "VehicleSkinVehicles.txt", "VehicleSkinMods.txt",
                "VehicleSkinModPoints.txt", "VehicleHideSlotInfo.txt",
                "VehicleHideSlotInfoMappings.txt", "EquipmentSlotDefinitions.txt",
                "Resources.txt", "ProfileResourceMappings.txt",
                "ClientItemDefinitions.txt", "ItemClasses.txt", "ContainerDefinitions.txt",
                "StringHashToValue.txt", "VehicleSets.txt",
                f"locale-{lang}.json",
            }),
            "emptySheetsInThisBuild": empty_sheets,
        },
        "formatFacts": FORMAT_FACTS,
        "counts": {},
        "vehicles": out_vehicles,
        "shared": {
            "fuel": {
                "displayName": next((loc(r["NAME_ID"]) for r in fuel_rows if num(r["NAME_ID"])), None),
                "displayDescription": next((loc(r["DESCRIPTION_ID"]) for r in fuel_rows
                                            if num(r["DESCRIPTION_ID"])), None),
                "primaryResource": fuel_doc(primary_fuel) if primary_fuel else None,
                "otherFuelResources": [fuel_doc(r) for r in fuel_rows
                                       if primary_fuel is None or r["ID"] != primary_fuel["ID"]],
                "boundToProfileIds": fuel_profiles,
                "note": "Fuel reaches a vehicle only as a profile resource. No per-vehicle "
                        "fuel column exists anywhere in this build and "
                        "VehicleResourceMappings.txt is header-only (D-V9).",
            },
            "vehicleParts": {"slots": part_slots, "items": part_items},
            "storage": storage,
            "constants": {"server": globals_server, "client": globals_client},
            "vehicleSets": [sets[k] for k in sorted(sets, key=int)],
            "moveInfoClientOnlyColumns": MOVEINFO_CLIENT_ONLY,
        },
        "serverSideGaps": server_side_gaps,
        "unresolved": UNRESOLVED,
        "provenance": dict(sorted(prov.map.items())),
    }
    counts = {
        "vehicles": len(out_vehicles),
        "driveModes": mode_rows,
        "seats": seat_rows,
        "wheels": sum(len(v["wheels"]) for v in out_vehicles),
        "suspensionUnits": sum(len(v["suspension"]) for v in out_vehicles),
        "damageLevels": sum(len((v["destruction"] or {}).get("damageLevels", []))
                            for v in out_vehicles),
        "debrisParts": sum(len((v["destruction"] or {}).get("debrisParts", []))
                           for v in out_vehicles),
        "skins": sum(len(v["cosmetics"]["skins"]) for v in out_vehicles),
        "hiddenSlots": sum(len(v["cosmetics"]["hiddenSlots"]) for v in out_vehicles),
        "fuelResources": len(fuel_rows),
        "vehiclePartItems": len(part_items),
        "vehicleSets": len(sets),
        "serverConstants": len(globals_server),
        "clientConstants": len(globals_client),
        "provenanceEntries": len(prov.map),
        "serverSideGaps": len(server_side_gaps),
    }
    doc["counts"] = counts
    return doc, counts


# ======================================================================================
# static prose blocks (kept out of build() so the flow above stays readable)
# ======================================================================================

SCHEMA_NOTES = {
    "shape": "vehicles[] is the primary array, one entry per row of Vehicles.txt. shared{} "
             "holds what is not per-vehicle (fuel resource, part slots, storage container, "
             "named constants, vehicle sets). serverSideGaps[] names every value the server "
             "needs that this client does not carry; nothing there is guessed.",
    "nulls": "null means the client cell was empty or 0 where 0 is the sheet's 'unset' "
             "sentinel (docs/28 sec.1). It never means 'we did not look'.",
    "joinConfidence": "'inferred' marks a join that no sheet states explicitly. See the "
                      "formatFacts entry named in the sibling 'evidence' field.",
    "units": "Speeds, accelerations and brake values are carried through verbatim in the "
             "client's own units; the sheets declare none. DECAY, CORPSE_EXPIRE_SECONDS and "
             "SWITCH_GEAR_TIME are seconds by name; *Msec constants are milliseconds by name.",
    "clientOnly": "Fields under clientOnly and shared.constants.client are presentation "
                  "only. They are emitted so a reader can see they were considered and "
                  "excluded, not because the server needs them.",
}

FORMAT_FACTS = [
    {"id": "D-V1", "fact": "Datasheet dialect: CRLF, '^' separator with a trailing '^' on "
                           "every line, '#' header, '*' key marker, no quoting/escaping, ASCII.",
     "evidence": "docs/28-locale-and-datasheets.md sec.1; implemented in tools/data/sheet.py"},
    {"id": "D-V2", "fact": "locale_key(n) = jenkins_lookup2('Global.Text.' + str(n), 0); "
                           "n = 0 means unset.",
     "evidence": "docs/28 sec.3; 'Global.Text.%d' at VA 0x143118638, 12 xrefs in the locale "
                 "helper cluster; implemented in tools/locale/localedat.py"},
    {"id": "D-V3", "fact": "MoveInfo ids are 'family base B + mode ordinal', B a multiple of "
                           "ten. Land vehicles carry up to 8 rows, whose MOVEMENT_MODE "
                           "values run 1,2,9,8,10,10,11,12 in ordinal order - but the count "
                           "is per vehicle, not fixed: vehicle 15 has 7 rows "
                           "(10,11,12,13,15,16,17) and one mode-10 row, not two.",
     "evidence": "VehicleMoveInfoMappings.txt joined to MoveInfo.txt; row set and mode order "
                 "read per vehicle by this tool, not hard-coded"},
    {"id": "D-V4", "fact": "EngineInfo/DynamicsInfo hold B; WheelInfo/TireInfo/SuspensionInfo "
                           "hold B..B+3. No sheet maps a vehicle id to them - INFERRED.",
     "evidence": "corner sheets carry the APEX lookup strings (WHEEL_*/TIRE_*/SUSPENSION_*) "
                 "that Common_<Family>_COL.apx resolves by name; family names agree with the "
                 "base derived in D-V3 on all four families. Binary: VehicleDynamicsInfo / "
                 "VehicleEngineInfo / VehicleSuspensionInfo / VehicleTireInfo / "
                 "VehicleWheelInfo at 0x14360c6f0-0x14360c758; 'Vehicle %i did not supply "
                 "MoveInfo' at 0x1435e62d0"},
    {"id": "D-V5", "fact": "Family name = longest common prefix of the family's WheelInfo "
                           "name strings: 10 OffRoader, 20 PickupTruck, 30 PoliceCar, 50 ATV.",
     "evidence": "WheelInfo.txt:WHEEL_FIRST..WHEEL_FOURTH"},
    {"id": "D-V6", "fact": "Destruction is keyed on NPC_ID. The vehicle side is recovered via "
                           "DestroyedInfo.DESTROYED_MODEL -> Models.MODEL_FILE_NAME, whose "
                           "name contains the D-V5 family name. Where a family has more "
                           "DestroyedInfo rows than vehicles (OffRoader 1 and 7, ATV 5 and 6 "
                           "- byte-identical pairs) rows are paired with vehicles in id "
                           "order. INFERRED.",
     "evidence": "DestroyedInfoMappings.txt, DestroyedInfo.txt, Models.txt; corroborated by "
                 "DamageLevelMappings.txt, whose only NPC ids (1, 998, 1008, 1672) are one "
                 "per family"},
    {"id": "D-V7", "fact": "VehicleHideSlotInfoMappings' second column is named EQUIP_SLOT but "
                           "holds VehicleHideSlotInfo.ID (1..4). INFERRED.",
     "evidence": "VehicleHideSlotInfo.EQUIP_SLOT holds 63/42/43/47 = VehLightTrim / Armor / "
                 "HoodOrnament / VehicleCosmetic; under the literal reading "
                 "VehicleHideSlotInfo would be referenced by no sheet at all"},
    {"id": "D-V8", "fact": "SpawnInfo.txt / VehicleSpawnMappings.txt are dead for this build.",
     "evidence": "the mappings name only vehicles 11 and 2000, neither in Vehicles.txt; all "
                 "20 SpawnInfo rows are bone names with zero offsets"},
    {"id": "D-V9", "fact": "VehicleResourceMappings, VehicleResistMappings, VehicleItemClasses, "
                           "VehicleCargoMappings, VehicleAbilityLines, VehicleSkillSets and "
                           "ResourceInfo are header-only (0 rows) in this build.",
     "evidence": "row counts, reported under generator.emptySheetsInThisBuild"},
    {"id": "D-V10", "fact": "Vehicles.txt carries no physics in this build: MASS, HEALTH, "
                            "EXPLODE_*, RAM_*, GROUND_INFO, WATER_INFO, AIR_INFO, DEPLOY_INFO, "
                            "ENTER_TIME, EXIT_TIME, SWITCH_SEAT_TIME, RESPAWN_TIME, "
                            "REGEN_RATE, REGEN_PERCENT and COOLDOWN_TIME are 0 on all 8 rows.",
     "evidence": "only 36 of the 125 columns are non-zero anywhere in the sheet"},
]

# Every entry here is a value Cranberry's vehicle system needs that this client does not
# carry. No number is invented; 'clientValue' records what the client does say (usually 0).
SERVER_SIDE_GAPS = [
    {"key": "vehicle.maxHealth",
     "need": "hit points per vehicle, for damage and destruction",
     "clientValue": "Vehicles.txt:HEALTH = 0 on all 8 rows",
     "alsoChecked": "VehicleResistMappings.txt (0 rows), VehicleArmorMappings.txt (only "
                    "VEHICLE_ID 6, which is not in Vehicles.txt)"},
    {"key": "vehicle.mass",
     "need": "collision response and ram outcomes",
     "clientValue": "Vehicles.txt:MASS = 0 on all 8 rows",
     "alsoChecked": "no mass column in any of the six physics sheets; Vehicles.txt:"
                    "UPSIDE_DOWN_UNDO (280/600/850/100) orders like mass but is unlabelled "
                    "and is emitted verbatim without interpretation"},
    {"key": "vehicle.explosionDamage / vehicle.explosionRadius",
     "need": "damage dealt when a destroyed vehicle explodes",
     "clientValue": "Vehicles.txt:EXPLODE_DAMAGE = 0, EXPLODE_RANGE = 0 on all 8 rows"},
    {"key": "vehicle.ramDamage (person / vehicle) and its speed thresholds",
     "need": "damage from driving into a player or another vehicle",
     "clientValue": "Vehicles.txt:RAM_PERSON_DAMAGE, RAM_VEHICLE_DAMAGE, RAM_PERSON_SPEED, "
                    "RAM_VEHICLE_SPEED all 0"},
    {"key": "vehicle.impactDamage base value",
     "need": "damage taken from collisions; the client ships only a multiplier",
     "clientValue": "Vehicles.txt:IMPACT_DAMAGE_MULTIPLIER = 1 and "
                    "IMPACT_DAMAGE_INFLICTED_MULT = 1, with no base damage column"},
    {"key": "vehicle.enterSeconds / exitSeconds / switchSeatSeconds",
     "need": "mount, dismount and seat-change durations",
     "clientValue": "Vehicles.txt:ENTER_TIME, EXIT_TIME, SWITCH_SEAT_TIME all 0",
     "partialCover": "StringHashToValue.txt gives VehicleInteractionCooldownMs 1000 and "
                     "VehicleSeatSwapCooldownMs 250 - cooldowns between actions, not the "
                     "durations of the actions"},
    {"key": "vehicle.respawnSeconds",
     "need": "how long before a destroyed vehicle can respawn",
     "clientValue": "Vehicles.txt:RESPAWN_TIME = 0 on all 8 rows"},
    {"key": "vehicle.spawnPoints and spawn odds",
     "need": "where vehicles appear on Z2 and how many of each",
     "clientValue": "no spawn table exists. VehicleSpawnMappings.txt/SpawnInfo.txt are dead "
                    "(D-V8); VehicleSets.txt groups vehicle ids with no semantic column and "
                    "most of its members are not in Vehicles.txt"},
    {"key": "fuel.burnPerMsec / fuel.burnTickMsec",
     "need": "the fuel drain rate while a vehicle is running",
     "clientValue": "Resources.txt id 50 (ResourceTypeFuel): BURN_PER_MSEC = 0, "
                    "BURN_TICK_MSEC = 0. Every other ResourceTypeFuel row is also 0.",
     "alsoChecked": "VehicleResourceMappings.txt is header-only, so no per-vehicle override "
                    "exists either (D-V9)"},
    {"key": "fuel.perVehicleCapacityOrConsumption",
     "need": "different tank sizes / consumption per vehicle",
     "clientValue": "none. Fuel is one profile-scoped resource (id 50, INITIAL 1000, MAX "
                    "10000) shared by whatever profile mounts it; no vehicle id appears "
                    "beside a fuel resource anywhere in the build"},
    {"key": "fuel.amountGrantedPerRefuelItem",
     "need": "how much a Biofuel / Ethanol item adds to the tank",
     "clientValue": "ClientItemDefinitions.txt items 73 (Biofuel) and 1384 (Ethanol) carry "
                    "PARAM1=0, PARAM2=1, PARAM3=0 - no amount. The locale text for "
                    "DESCRIPTION_ID 1218 describes fuel in prose only."},
    {"key": "vehicle.ignitionRequirements (battery / spark plugs / key)",
     "need": "which parts a vehicle needs before it will start, and what happens without them",
     "clientValue": "EquipmentSlotDefinitions ids 69 Fuel / 70 Battery / 71 Sparkplugs exist "
                    "and StringHashToValue names Vehicle.BatteryClassId 25020, "
                    "Vehicle.SparkplugsClassId 25021, Vehicle.KeyClassId 25076, "
                    "Vehicle.KeyItemId 3460 - but EquipSlotItemClasses.txt has no row for "
                    "slots 69/70/71 and no sheet states a gating rule"},
    {"key": "vehicle.damageStateToMoveInfoMode",
     "need": "which MoveInfo MOVEMENT_MODE (11 / 12, the RPM-capped rows) a given damage "
             "level selects",
     "clientValue": "DamageLevelInfo.txt:MOVE_INFO_OVERRIDE = 0 on all 12 rows, and "
                    "CONTROL_DAMPENING, STOP_ENGINE, TRIGGERED_ABILITY, COMPOSITE_EFFECT and "
                    "WARN_STRING are 0 as well. Only DAMAGE_PERCENT (75 / 50 / 25) is filled."},
    {"key": "vehicle.repairAndRegen",
     "need": "repair amounts and passive regeneration",
     "clientValue": "Vehicles.txt:REGEN_RATE = 0, REGEN_PERCENT = 0, COOLDOWN_TIME = 0"},
    {"key": "seat.damageShare",
     "need": "how much of a vehicle's incoming damage an occupant takes",
     "clientValue": "SeatInfo.txt:DAMAGE_PERCENT = 0 on all 26 rows"},
    {"key": "vehicle.storageContainerBinding",
     "need": "which container definition a given vehicle's trunk uses, and its slot count "
             "per vehicle",
     "clientValue": "VehicleCargoMappings.txt is header-only. The only reachable vehicle "
                    "container is ClientItemDefinitions item 1541 (class 25019 'Offroader "
                    "Trunk') -> ContainerDefinitions 62, 40 slots; nothing binds it to a "
                    "vehicle id"},
    {"key": "vehicle.lootTableInside",
     "need": "what, if anything, spawns inside a vehicle",
     "clientValue": "no vehicle-to-item table ships; VehicleItemClasses.txt is header-only"},
    {"key": "vehicle.decayUnits",
     "need": "confirmation that Vehicles.txt:DECAY is seconds",
     "clientValue": "DECAY = 259200 for the four land families and 1200 for parachute and "
                    "observer. 259200 s is exactly 72 h and 1200 s exactly 20 min, which is "
                    "consistent with seconds, but the sheet declares no unit."},
]

UNRESOLVED = [
    {"key": "MoveInfo.MOVEMENT_MODE enum",
     "state": "values 1, 2, 8, 9, 10, 11, 12, 13 are carried through verbatim, each row "
              "profiled against its own family by describe_mode. The client ships no "
              "enum-name strings: tools/pe/exeview.py findstr 'MovementMode' returns nothing "
              "and 'MOVEMENT_MODE' returns only the datasheet column name at 0x14360d440. "
              "Mode 9 is NOT byte-identical to mode 1 (nor 8 to 2): on all four land "
              "families mode 9 differs from mode 1 on 7 columns - MOVE_ROLL_RATE, "
              "STILL_ROLL_RATE, ANGULAR_DAMPENING_SCALAR/_X/_Y/_Z and MOVING_EFFECT - and "
              "mode 8 differs from mode 2 on those plus TURN_RAMP_UP, plus MAX_FORWARD "
              "100->85 on the ATV family. Only MOVING_EFFECT of those is client-only, so "
              "the pairs are distinct to the server. Mode 10 does appear twice per family "
              "(one row keeps MAX_FORWARD, one is 0) except on vehicle 15."},
    {"key": "Vehicles.UPSIDE_DOWN_UNDO",
     "state": "280 OffRoader / 600 PickupTruck / 850 PoliceCar / 100 ATV / 35 Ignition ATV / "
              "1 parachute and observer. Emitted verbatim; the unit is not declared and no "
              "binary string names it."},
    {"key": "vehicle 15 'Ignition OffRoader' is missing MoveInfo 14",
     "state": "VehicleMoveInfoMappings gives vehicle 15 the ids 10,11,12,13,15,16,17 - seven "
              "rows, not the eight every other land vehicle has. MoveInfo 14 exists (mode 10, "
              "MAX_FORWARD 85). Reproduced faithfully rather than patched."},
    {"key": "vehicles 4, 6, 7, 8, 9, 10, 11, 12 referenced by VehicleSets.txt",
     "state": "not present in Vehicles.txt; emitted under vehicleSets[].unknownVehicleIds. "
              "VehicleArmorMappings.txt references only vehicle 6, so its six ArmorInfo rows "
              "are unreachable in this build."},
]


# ======================================================================================
# cli
# ======================================================================================

def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Join the August client's vehicle datasheets into "
                    "out/data_aug/derived/vehicles.json (schema cranberry/vehicles/1).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--data-dir", type=Path, default=DEFAULT_DATA_DIR,
                    help="directory holding the extracted datasheets "
                         f"(default: {DEFAULT_DATA_DIR})")
    ap.add_argument("--locale", type=Path, default=None,
                    help="locale JSON written by localedat.py dump "
                         "(default: <data-dir>/locale-<lang>.json)")
    ap.add_argument("--lang", default="en_us", help="locale tag, for the default --locale "
                                                    "path and the output header")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT,
                    help=f"output JSON path (default: {DEFAULT_OUT})")
    ap.add_argument("--indent", type=int, default=2, help="JSON indent (0 = compact)")
    ap.add_argument("--summary", action="store_true",
                    help="print the counts and gaps but write no file")
    args = ap.parse_args(argv)

    locale_path = args.locale or (args.data_dir / f"locale-{args.lang}.json")
    if not locale_path.exists():
        raise SystemExit(f"missing locale json: {locale_path}\n"
                         f"  make it with: python tools/locale/localedat.py "
                         f"--lang {args.lang} dump --out {locale_path}")

    doc, counts = build(args.data_dir, locale_path, args.lang)

    if not args.summary:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        with args.out.open("w", encoding="utf-8", newline="\n") as fh:
            json.dump(doc, fh, ensure_ascii=False,
                      indent=(args.indent or None),
                      separators=None if args.indent else (",", ":"))
            fh.write("\n")
        size = os.path.getsize(args.out)
        print(f"wrote {args.out}  ({size:,} bytes)", file=sys.stderr)

    for k, v in counts.items():
        print(f"  {k:<22} {v}", file=sys.stderr)
    print(f"  serverSideGaps keys: "
          f"{', '.join(g['key'] for g in doc['serverSideGaps'])}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
