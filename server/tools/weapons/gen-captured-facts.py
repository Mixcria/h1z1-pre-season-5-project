#!/usr/bin/env python3
r"""Emit the two C# data files lane A ships, from the owner-adopted 1087 sources (D312):

  src/Cranberry.Zone/Weapons/CapturedWeaponFacts.cs
      the friend's captured ReferenceData "WeaponDefinitions" (0x17 04), decoded with the 1087
      schema by decode-friend-table.py and RE-EXPRESSED against Cranberry's [P] 1148 record
      offsets - because the 1148 walk of that body does NOT consume it (docs/123).

  src/Cranberry.Zone/Weapons/Z1ProjectileFacts.cs
      all 131 rows of C:\Z1\Server\Data\projectileDefinitions.json in the 1087 schema's column
      order, which is field-for-field the [P] 1148 ProjectileDefinitionRecord order.

Run from the worktree root:
    python tools/weapons/gen-captured-facts.py

stdlib only. These two files are committed (they are not .g.cs and are not in the pipeline
manifest); re-run this script when a source moves.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("decode_friend_table", HERE / "decode-friend-table.py")
DFT = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(DFT)

# ------------------------------------------------------------------ what crosses into list 2
#
# Only columns whose NAME exists on both sides are crossed, and only the ones that decide how a
# shot FEELS: the cone, the recoil family, the pellet pattern, the fire and reload clocks, the
# sway/kick, and the two ids that reach lists 3 and 5. Deliberately NOT crossed:
#   TYPE           - D291-D293 own it; the 1087 TYPE enum is not the 1148 one.
#   AMMO_ITEM_ID / AMMO_SLOT - Cranberry writes August's own round ids and its one-slot layout.
#   EFFECT_GROUP   - a 1087 FireModeEffectGroups id; August has its own sheet (D234).
# Native camera fields are joined by their August loader names, never by 1087 offsets.
# They are applied only to armed weapon groups, leaving the confirmed binocular path intact.
GUN_PRESENTATION_PREFIXES = ("TP_", "FP_", "FIRST_PERSON_OFFSET_")
GUN_PRESENTATION_NAMES = {"ARMS_FOV_SCALAR", "FORCE_FP_SCOPE", "RETICLE_ID"}
CROSSED = [
    "MOVEMENT_MODIFIER", "TURN_MODIFIER", "DEFAULT_ZOOM",
    # fire and reload clocks
    "REFIRE_TIME_MS", "FIRE_COOLDOWN_DURATION_MS", "FIRE_DELAY_MS", "AUTO_FIRE_TIME_MS", "FIRE_DURATION_MS",
    "BURST_COUNT", "AMMO_PER_SHOT", "RANGE",
    "RELOAD_TIME_MS", "RELOAD_CHAMBER_TIME_MS", "RELOAD_AMMO_FILL_TIME_MS",
    "RELOAD_LOOP_START_TIME_MS", "RELOAD_LOOP_END_TIME_MS",
    # the pellet pattern
    "PELLETS_PER_SHOT", "PELLET_SPREAD",
    # the cone of fire
    "COF_RECOIL", "COF_SCALAR", "COF_SCALAR_MOVING", "COF_OVERRIDE",
    "CYLOF_RECOIL", "CYLOF_SCALAR", "CYLOF_SCALAR_MOVING", "CYLOF_OVERRIDE",
    # the recoil family
    "RECOIL_ANGLE_MIN", "RECOIL_ANGLE_MAX", "RECOIL_HORIZONTAL_TOLERANCE",
    "RECOIL_HORIZONTAL_MIN", "RECOIL_HORIZONTAL_MAX", "RECOIL_MAGNITUDE_MIN",
    "RECOIL_MAGNITUDE_MAX", "RECOIL_RECOVERY_DELAY_MS", "RECOIL_RECOVERY_RATE",
    "RECOIL_RECOVERY_ACCELERATION", "RECOIL_SHOTS_AT_MIN_MAGNITUDE",
    "RECOIL_MAX_TOTAL_MAGNITUDE", "RECOIL_INCREASE", "RECOIL_INCREASE_CROUCHED",
    "RECOIL_FIRST_SHOT_MODIFIER", "RECOIL_HORIZONTAL_MIN_INCREASE",
    "RECOIL_HORIZONTAL_MAX_INCREASE",
    # sway and the visible kick
    "SWAY_AMPLITUDE_X", "SWAY_AMPLITUDE_Y", "SWAY_PERIOD_X", "SWAY_PERIOD_Y",
    "SWAY_INITIAL_Y_OFFSET", "SWAY_CROUCH_SCALAR", "SWAY_PRONE_SCALAR",
    "ANIM_KICK_MAGNITUDE", "ANIM_RECOIL_MAGNITUDE",
    # the two links out of list 2
    "PLAYER_STATE_GROUP_ID", "AIM_ASSIST_CONFIG",
]

# The two flag bytes whose bit layout the 1087 and 1148 loaders agree on (docs/123 section 4):
# rec+0x20 (HIDE_UNAVAILABLE .. LASER_GUIDED) and rec+0x21 (CAN_STEADY_SWAY .. CAN_LOCKON_WHILE
# BUSY). The loader does not name rec+0x22 bits 0x40/0x20, but the August recoil
# consumer FUN_141485bf0 reads them directly: master recoil and horizontal recoil.
FLAGS0_OFFSET = 0x020
FLAGS1_OFFSET = 0x021
FLAGS2_OFFSET = 0x022
AUTOMATIC_BIT = 0x20
IRON_SIGHTS_BIT = 0x04


def flatten(node, out):
    """The 1087 fire-mode DATA tree, flattened to name -> value (its schemas are all inline)."""
    for key, value in node.items():
        if isinstance(value, dict):
            flatten(value, out)
        else:
            out[key] = value
    return out


def encode(value, kind):
    """One crossed value as the raw little-endian word the 1148 body writes at that offset."""
    if kind == "F32":
        return struct.unpack("<I", struct.pack("<f", float(value)))[0]
    if isinstance(value, bool):
        value = 1 if value else 0
    return int(round(float(value))) & 0xFFFFFFFF


def cs_uint_array(values):
    return "[" + ", ".join("0x%08xu" % v for v in values) + "]"


def build_weapon_facts(report, src_root: Path):
    table = report["table1087"]
    body = DFT.load_fire_mode_body(src_root)
    columns = DFT.load_fire_mode_columns(src_root)
    kind_by_offset = dict(body)
    offset_by_name = {}
    column_kind_by_name = {}
    for off, cols in columns.items():
        for name, kind, _bit in cols:
            if kind != "FlagBit":
                offset_by_name[name] = off
                column_kind_by_name[name] = kind

    # The LOADER's own kind (FireModeColumns: MOVSS = float, atoi = int) decides how a value is
    # encoded; the BODY's kind (FireModeBody) decides only how many bytes go on the wire. They
    # disagree on purpose - COF_RECOIL is a float the body reader takes with the 4-byte integer
    # reader - and taking the body's word for it would write int(0.28) = 0.
    crossed = []
    missing = []
    capture_names = set()
    for mode in table["FIRE_MODE_DEFINITIONS"]:
        capture_names.update(flatten(mode["DATA"]["DATA"], {}))
    presentation_names = sorted(name for name in capture_names
                                if name.startswith(GUN_PRESENTATION_PREFIXES)
                                or name in GUN_PRESENTATION_NAMES)
    for name in CROSSED + presentation_names:
        off = offset_by_name.get(name)
        if off is None or off not in kind_by_offset:
            missing.append(name)
            continue
        crossed.append((name, off, "F32" if column_kind_by_name[name] == "Float" else "U32"))

    weapons = {w["ID"]: w["DATA"] for w in table["WEAPON_DEFINITIONS"]}
    groups = {g["ID"]: g["DATA"] for g in table["FIRE_GROUP_DEFINITIONS"]}
    modes = {m["ID"]: flatten(m["DATA"]["DATA"], {}) for m in table["FIRE_MODE_DEFINITIONS"]}

    mode_rows = []
    for mode_id in sorted(modes):
        data = modes[mode_id]
        words = [encode(data[name], kind) if name in data else 0
                 for name, _off, kind in crossed]
        flags0 = int(data["unknownByte1"])
        flags1 = int(data["unknownByte2"])
        mode_rows.append((mode_id, words, flags0, flags1, int(data["unknownByte3"])))

    weapon_rows = []
    for weapon_id in sorted(weapons):
        data = weapons[weapon_id]
        for entry in data["FIRE_GROUPS"]:
            group_id = entry["FIRE_GROUP_ID"]
            group = groups.get(group_id)
            mode_ids = [m["FIRE_MODE_ID"] for m in group["FIRE_MODES"]] if group else []
            slot = data["AMMO_SLOTS"][0] if data["AMMO_SLOTS"] else None
            weapon_rows.append((
                weapon_id, group_id,
                0 if slot is None else slot["AMMO_ID"],
                0 if slot is None else slot["CLIP_SIZE"],
                data["ANIMATION_SET_NAME"],
                mode_ids))

    cone_rows = []
    for group in table["PLAYER_STATE_GROUP_DEFINITIONS"]:
        states = []
        for element in group["PLAYER_STATE_PROPERTIES"]:
            d = element["DATA"]
            # Both versions carry key + ID + FLAGS byte + 16 words = 73 bytes.
            # FUN_140a41f00 reads the byte at elem+0x20; the original 72-byte interpretation
            # was disproved by the native walk and caused a client crash (docs/123 section 7).
            words = [d["ID"]] + [
                d["MIN_CONE_OF_FIRE"], d["MAX_CONE_OF_FIRE"], d["COF_RECOVERY_RATE"],
                d["COF_TURN_PENALTY"], d["SHOTS_BEFORE_COF_PENALTY"],
                d["COF_RECOVERY_DELAY_THRESHOLD"], d["COF_RECOVERY_DELAY_MS"], d["COF_GROW_RATE"],
                d["MIN_CYL_OF_FIRE"], d["MAX_CYL_OF_FIRE"], d["CYLOF_RECOVERY_RATE"],
                d["CYLOF_TURN_PENALTY"], d["SHOTS_BEFORE_CYLOF_PENALTY"],
                d["CYLOF_RECOVERY_DELAY_THRESHOLD"], d["CYLOF_RECOVERY_DELAY_MS"],
                d["CYLOF_GROW_RATE"],
            ]
            kinds = ["U32"] + ["F32", "F32", "F32", "F32", "U32", "F32", "U32", "F32",
                               "F32", "F32", "F32", "F32", "U32", "F32", "U32", "F32"]
            states.append((element["GROUP_ID"],
                           [encode(v, k) for v, k in zip(words, kinds)],
                           int(d["FLAGS"])))
        cone_rows.append((group["ID"], states))

    aim_rows = []
    aim_kinds = ["F32"] * 16 + ["U32"] + ["F32"] * 6
    for row in table["AIM_ASSIST_DEFINITIONS"]:
        d = row["DATA"]
        values = list(d.values())
        aim_rows.append((row["ID"], [encode(v, k) for v, k in zip(values, aim_kinds)]))

    return crossed, missing, mode_rows, weapon_rows, cone_rows, aim_rows


def emit_weapon_facts(path: Path, report, crossed, mode_rows, weapon_rows, cone_rows, aim_rows,
                      kind_size=None):
    kind_size = kind_size or {}
    header = report["header"]
    lines = []
    add = lines.append
    add("// <auto-generated>")
    add("// Written by tools/weapons/gen-captured-facts.py from the friend's captured")
    add("// ReferenceData \"WeaponDefinitions\" packet (0x17 04), adopted under D312:")
    add("//   C:\\Z1\\Server\\Data\\friendWeaponDefinitions.bin  %d B on the wire, body md5 %s"
        % (header["wire_bytes"], header["body_md5"]))
    add("// The body is 1087 and does NOT walk with the 1148 layouts (docs/123 section 2), so every")
    add("// value here is RE-EXPRESSED against Cranberry's own [P] 1148 record offsets. Do not edit")
    add("// by hand; re-run the generator. This file is not in the pipeline manifest (docs/96).")
    add("// </auto-generated>")
    add("")
    add("namespace Cranberry.Zone.Weapons;")
    add("")
    add("/// <summary>One captured weapon row, joined to its fire group. See <see cref=\"CapturedWeaponFacts\"/>.</summary>")
    add("/// <param name=\"WeaponDefinitionId\">The capture's <c>WEAPON_DEFINITIONS.ID</c> - the same id space August uses.</param>")
    add("/// <param name=\"FireGroupId\">One of the weapon's <c>FIRE_GROUPS</c> entries.</param>")
    add("/// <param name=\"AmmoSlotAmmoId\">The capture's <c>AMMO_SLOTS[0].AMMO_ID</c>. <b>Always 1</b> - a 1087 slot index, not an August item id (docs/123 section 5).</param>")
    add("/// <param name=\"ClipSize\">The capture's <c>AMMO_SLOTS[0].CLIP_SIZE</c>.</param>")
    add("/// <param name=\"AnimationSetName\">The capture's <c>ANIMATION_SET_NAME</c>, carried for the audit only.</param>")
    add("/// <param name=\"FireModeIds\">The group's <c>FIRE_MODES</c>, in order: index 0 is the primary mode.</param>")
    add("public readonly record struct CapturedWeaponRow(")
    add("    uint WeaponDefinitionId,")
    add("    uint FireGroupId,")
    add("    uint AmmoSlotAmmoId,")
    add("    uint ClipSize,")
    add("    string AnimationSetName,")
    add("    uint[] FireModeIds);")
    add("")
    add("/// <summary>One captured fire mode, as raw 1148 words in <see cref=\"CapturedWeaponFacts.CrossedOffsets\"/> order.</summary>")
    add("/// <param name=\"FireModeId\">The capture's <c>FIRE_MODE_DEFINITIONS.ID</c>.</param>")
    add("/// <param name=\"Words\">One word per crossed offset, already encoded for that offset's wire kind.</param>")
    add("/// <param name=\"Flags0\">The capture's first flag byte, whose bit layout 1087 and 1148 share (<c>rec+0x20</c>).</param>")
    add("/// <param name=\"Flags1\">The capture's second flag byte (<c>rec+0x21</c>): <c>CONTINUOUS_RELOAD</c> lives here.</param>")
    add("/// <param name=\"Flags2\">Third captured flag byte; August FUN_141485bf0 confirms recoil bits 0x40/0x20.</param>")
    add("public readonly record struct CapturedFireModeRow(")
    add("    uint FireModeId,")
    add("    uint[] Words,")
    add("    byte Flags0,")
    add("    byte Flags1,")
    add("    byte Flags2);")
    add("")
    add("/// <summary>One player-state row of a captured cone-of-fire group.</summary>")
    add("/// <param name=\"StateId\">The capture's <c>GROUP_ID</c> - the player state (0 standing, 1 crouched, ...).</param>")
    add("/// <param name=\"Words\">The 17 words of a 1148 list-3 element: the group id then the 16 COF / CYLOF numbers.</param>")
    add("/// <param name=\"Flags\">The 1087 element's one-byte <c>FLAGS</c>, equal to <paramref name=\"StateId\"/> on every captured row; 1148 reads it as the one byte at <c>elem+0x20</c> (<c>FUN_140a41f00</c>) - it MUST ship, see docs/123 §7.</param>")
    add("public readonly record struct CapturedConeOfFireState(uint StateId, uint[] Words, byte Flags);")
    add("")
    add("/// <summary>One captured cone-of-fire (player-state) group - a 1148 list-3 record.</summary>")
    add("/// <param name=\"ConeOfFireId\">The capture's <c>PLAYER_STATE_GROUP_DEFINITIONS.ID</c>, which a fire mode's <c>PLAYER_STATE_GROUP_ID</c> names.</param>")
    add("/// <param name=\"States\">One element per player state.</param>")
    add("public readonly record struct CapturedConeOfFireRow(uint ConeOfFireId, CapturedConeOfFireState[] States);")
    add("")
    add("/// <summary>One captured aim-assist config - a 1148 list-5 record, 23 words, an exact shape match.</summary>")
    add("/// <param name=\"AimAssistId\">The capture's <c>AIM_ASSIST_DEFINITIONS.ID</c>, which a fire mode's <c>AIM_ASSIST_CONFIG</c> names.</param>")
    add("/// <param name=\"Words\">The 23 body words in the 1087 schema's own column order.</param>")
    add("public readonly record struct CapturedAimAssistRow(uint AimAssistId, uint[] Words);")
    add("")
    add("/// <summary>")
    add("/// <b>The friend's captured 1087 weapon table, re-expressed for the 1148 client (D312, docs/123).</b>")
    add("/// <para>")
    add("/// The capture is the byte source at the top of the authority ladder for how a shot feels - a")
    add("/// live server this client's own family shoots straight on. Its body does NOT walk with the")
    add("/// 1148 layouts (list 0's tail is one word longer in 1148 and 1148 has two lists 1087 does")
    add("/// not), so it cannot be replayed; what ships is this decode, written through Cranberry's own")
    add("/// list-2/3/5 writers. <c>CapturedWeaponTable</c> does the joining.")
    add("/// </para>")
    add("/// </summary>")
    add("public static class CapturedWeaponFacts")
    add("{")
    add("    /// <summary>md5 of the captured packet's inflated body - the D22 identity of this data.</summary>")
    add("    public const string SourceBodyMd5 = \"%s\";" % header["body_md5"])
    add("")
    add("    /// <summary>Bytes the captured body inflates to.</summary>")
    add("    public const int SourceBodyBytes = %d;" % header["uncompressed_length"])
    add("")
    add("    /// <summary>Bytes the captured packet occupies on the 1087 wire.</summary>")
    add("    public const int SourceWireBytes = %d;" % header["wire_bytes"])
    add("")
    for name, count in (("WeaponRowCount", len(report["table1087"]["WEAPON_DEFINITIONS"])),
                        ("FireGroupRowCount", len(report["table1087"]["FIRE_GROUP_DEFINITIONS"])),
                        ("FireModeRowCount", len(report["table1087"]["FIRE_MODE_DEFINITIONS"])),
                        ("ConeOfFireRowCount", len(report["table1087"]["PLAYER_STATE_GROUP_DEFINITIONS"])),
                        ("ProjectileMappingRowCount",
                         len(report["table1087"]["FIRE_MODE_PROJECTILE_MAPPING_DATA"])),
                        ("AimAssistRowCount", len(report["table1087"]["AIM_ASSIST_DEFINITIONS"]))):
        add("    /// <summary>Rows the capture carries: %d.</summary>" % count)
        add("    public const int %s = %d;" % (name, count))
        add("")
    add("    /// <summary><c>rec+0x20</c>, the flag byte 1087 and 1148 lay out alike.</summary>")
    add("    public const short Flags0Offset = 0x%03x;" % FLAGS0_OFFSET)
    add("")
    add("    /// <summary><c>rec+0x21</c>, the second shared flag byte (<c>CONTINUOUS_RELOAD</c> 0x02).</summary>")
    add("    public const short Flags1Offset = 0x%03x;" % FLAGS1_OFFSET)
    add("")
    add("    /// <summary>Third flag byte; only native-confirmed recoil bits are adopted.</summary>")
    add("    public const short Flags2Offset = 0x%03x;" % FLAGS2_OFFSET)
    add("")
    add("    /// <summary>The 1148 record offsets <see cref=\"CapturedFireModeRow.Words\"/> is ordered by.</summary>")
    add("    public static readonly short[] CrossedOffsets =")
    add("    [")
    for name, off, kind in crossed:
        add("        0x%03x,  // %s (%s, %d B)" % (off, name, "float" if kind == "F32" else "int", kind_size[off]))
    add("    ];")
    add("")
    add("    /// <summary>Additional camera/arms fields used only by armed weapon groups.</summary>")
    add("    public static readonly short[] GunPresentationOffsets =")
    add("    [")
    for name, off, _kind in crossed:
        if name.startswith(GUN_PRESENTATION_PREFIXES) or name in GUN_PRESENTATION_NAMES:
            add("        0x%03x,  // %s" % (off, name))
    add("    ];")
    add("")
    add("    /// <summary>The client's own column name of each <see cref=\"CrossedOffsets\"/> entry, same order.</summary>")
    add("    public static readonly string[] CrossedNames =")
    add("    [")
    for name, _off, _kind in crossed:
        add("        \"%s\"," % name)
    add("    ];")
    add("")
    add("    /// <summary>Every captured weapon row, joined to each of its fire groups.</summary>")
    add("    public static readonly CapturedWeaponRow[] Weapons =")
    add("    [")
    for weapon_id, group_id, ammo_id, clip, anim, mode_ids in weapon_rows:
        add("        new(%du, %du, %du, %du, \"%s\", [%s])," % (
            weapon_id, group_id, ammo_id, clip, anim,
            ", ".join("%du" % m for m in mode_ids)))
    add("    ];")
    add("")
    add("    /// <summary>Every captured fire mode, as 1148 words.</summary>")
    add("    public static readonly CapturedFireModeRow[] FireModes =")
    add("    [")
    for mode_id, words, flags0, flags1, flags2 in mode_rows:
        add("        new(%du, %s, 0x%02x, 0x%02x, 0x%02x)," % (
            mode_id, cs_uint_array(words), flags0, flags1, flags2))
    add("    ];")
    add("")
    add("    /// <summary>Every captured cone-of-fire group - list 3, which Cranberry has always shipped empty.</summary>")
    add("    public static readonly CapturedConeOfFireRow[] ConeOfFire =")
    add("    [")
    for cone_id, states in cone_rows:
        add("        new(%du," % cone_id)
        add("        [")
        for state_id, words, flags in states:
            add("            new(%du, %s, 0x%02x)," % (state_id, cs_uint_array(words), flags))
        add("        ]),")
    add("    ];")
    add("")
    add("    /// <summary>Every captured aim-assist config - list 5, which Cranberry has always shipped empty.</summary>")
    add("    public static readonly CapturedAimAssistRow[] AimAssist =")
    add("    [")
    for aim_id, words in aim_rows:
        add("        new(%du, %s)," % (aim_id, cs_uint_array(words)))
    add("    ];")
    add("}")
    add("")
    path.write_text("\n".join(lines), encoding="utf-8")
    return len(lines)


# ------------------------------------------------------------------------------- projectiles

# The 1087 projectileDefinitionSchema.json column order IS the [P] 1148 record order, field for
# field (docs/123 section 6). "kind" is how the 1148 body writes it.
PROJECTILE_COLUMNS = [
    ("ID", "U32"),                       # -> rec+0x18   the record's own id
    ("FLAGS1", "U8"), ("FLAGS2", "U8"),  # -> rec+0x20 / +0x21
    ("MODEL_FILE_NAME", "STR"),          # -> rec+0x28
    ("FP_MODEL_FILE_NAME", "STR"),       # -> rec+0x40  (Cranberry named this AttachmentOverride)
    ("AUDIO_GAME_OBJECT", "U32"),        # -> +0x58
    ("SPEED", "F32"),                    # -> +0x5c
    ("VELOCITY_INHERIT_SCALER", "F32"),  # -> +0x60
    ("ACCELERATION", "F32"),             # -> +0x64
    ("FLIGHT_TYPE", "U8"),               # -> +0x68  ONE BYTE
    ("PROJECTILE_EFFECT_ID", "U32"),     # -> +0x6c
    ("LAND_EFFECT_ID", "U32"),           # -> +0x70
    ("INDIRECT_DAMAGE_EFFECT_ID", "U32"),  # -> +0x74
    ("LIFESPAN", "F32"),                 # -> +0x78
    ("MAX_SPEED", "F32"),                # -> +0x7c
    ("TURN_RATE", "F32"),                # -> +0x80
    ("BONE_ATTACHMENT_OVERRIDE", "STR"),  # -> rec+0x88 (Cranberry named this FirstPersonModelFileName)
    ("DRAG", "F32"),                     # -> +0xa0
    ("GRAVITY", "F32"),                  # -> +0xa4
    ("DAMAGE", "F32"),                   # -> +0xa8  (the "unnamed 10.0f" of docs/120 section 3.2)
    ("TRACER_FREQUENCY", "U32"),         # -> +0xac
    ("TRACER_EFFECT_ID", "U32"),         # -> +0xb0
    ("FP_TRACER_FREQUENCY", "U32"),      # -> +0xb4
    ("FP_TRACER_EFFECT_ID", "U32"),      # -> +0xb8
    ("FP_TRACER_HIDE_RANGE", "F32"),     # -> +0xbc
    ("NPC_DEFINITION_ID", "U32"),        # -> +0xc0
    ("MAX_COUNT", "U32"),                # -> +0xc4
    ("DAMAGE_RADIUS", "F32"),            # -> +0xc8
    ("DROP_OFF_RADIUS", "F32"),          # -> +0xcc
    ("DROP_OFF_MODIFIER", "F32"),        # -> +0xd0
    ("TRIGGER_DETONATE_REQUIREMENT", "U8"),  # -> +0xd4  (i8)
    ("ARM_DISTANCE", "U32"),             # -> +0xd8
    ("DETONATE_DISTANCE", "U32"),        # -> +0xdc
    ("TETHER_DISTANCE", "F32"),          # -> +0xe0
    ("LOCKON_ACCELERATION", "F32"),      # -> +0xe4
    ("LOCKON_LIFESPAN", "F32"),          # -> +0xe8
    ("SCALE", "F32"),                    # -> +0xec
    ("LOSE_LOCKON_ANGLE", "U32"),        # -> +0xf0
    ("PLAYER_BULLET_RADIUS_LIST", "ARR"),  # -> +0x120 counted array
    ("ITEM_PICKUP_ID", "U32"),           # -> +0xf4
    ("BOUNCE_MODEL_ID", "U32"),          # -> +0xf8
    ("ANGULAR_VELOCITY_X_MIN", "F32"),   # -> +0x104
    ("ANGULAR_VELOCITY_X_MAX", "F32"),   # -> +0x108
    ("ANGULAR_VELOCITY_Y_MIN", "F32"),   # -> +0x10c
    ("ANGULAR_VELOCITY_Y_MAX", "F32"),   # -> +0x110
    ("ANGULAR_VELOCITY_Z_MIN", "F32"),   # -> +0x114
    ("ANGULAR_VELOCITY_Z_MAX", "F32"),   # -> +0x118
]

# Column indices of every EFFECT id in the record - the ids the August build has to know or the
# client queues a composite effect it has no definition for.
EFFECT_COLUMNS = ["PROJECTILE_EFFECT_ID", "LAND_EFFECT_ID", "INDIRECT_DAMAGE_EFFECT_ID",
                  "TRACER_EFFECT_ID", "FP_TRACER_EFFECT_ID"]


def cs_string(value: str) -> str:
    return "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"") + "\""


def emit_projectile_facts(path: Path, source: Path):
    raw = source.read_bytes()
    rows = json.loads(raw.decode("utf-8"))["PROJECTILE_DEFINITIONS"]
    digest = hashlib.sha256(raw).hexdigest()

    lines = []
    add = lines.append
    add("// <auto-generated>")
    add("// Written by tools/weapons/gen-captured-facts.py from the owner's own Z1 server data,")
    add("// adopted under D312:")
    add("//   C:\\Z1\\Server\\Data\\projectileDefinitions.json  %d rows, sha256 %s" % (len(rows), digest))
    add("// The 1087 projectileDefinitionSchema.json column order is field-for-field the [P] 1148")
    add("// ProjectileDefinitionRecord order (docs/123 section 6), so these rows need no re-ordering.")
    add("// Do not edit by hand; re-run the generator. Not in the pipeline manifest (docs/96).")
    add("// </auto-generated>")
    add("")
    add("namespace Cranberry.Zone.Weapons;")
    add("")
    add("/// <summary>One row of Z1's <c>projectileDefinitions.json</c>, in 1148 record order.</summary>")
    add("/// <param name=\"ProjectileId\">The <c>ProjectileDefinitions</c> id a list-4 row names.</param>")
    add("/// <param name=\"ModelFileName\">The <c>.adr</c> the client draws; empty makes it fall back to <c>InvisibleTriangle.adr</c>.</param>")
    add("/// <param name=\"FirstPersonModelFileName\">The capture's <c>FP_MODEL_FILE_NAME</c> - the record's SECOND string.</param>")
    add("/// <param name=\"BoneAttachmentOverride\">The capture's <c>BONE_ATTACHMENT_OVERRIDE</c> - the record's THIRD string.</param>")
    add("/// <param name=\"Words\">Every non-string field, in <see cref=\"Z1ProjectileFacts.Columns\"/> order, encoded for its wire kind.</param>")
    add("/// <param name=\"BulletRadii\">The <c>PLAYER_BULLET_RADIUS_LIST</c> counted array (<c>rec+0x120</c>).</param>")
    add("public readonly record struct Z1ProjectileRow(")
    add("    uint ProjectileId,")
    add("    string ModelFileName,")
    add("    string FirstPersonModelFileName,")
    add("    string BoneAttachmentOverride,")
    add("    uint[] Words,")
    add("    uint[] BulletRadii);")
    add("")
    add("/// <summary>")
    add("/// <b>Z1's 131 projectile definitions (D312).</b> Cranberry's own table is 14 records at")
    add("/// constructor defaults with no model and no effect; these are the rows the owner's working")
    add("/// server ships, and they are the reason a bullet has a tracer and a grenade an arc.")
    add("/// </summary>")
    add("public static class Z1ProjectileFacts")
    add("{")
    add("    /// <summary>sha256 of the source JSON - the identity of this data.</summary>")
    add("    public const string SourceSha256 = \"%s\";" % digest)
    add("")
    add("    /// <summary>Rows the source carries: %d.</summary>" % len(rows))
    add("    public const int RowCount = %d;" % len(rows))
    add("")
    add("    /// <summary>The non-string columns, in wire order; <see cref=\"Z1ProjectileRow.Words\"/> follows it.</summary>")
    add("    public static readonly string[] Columns =")
    add("    [")
    for name, kind in PROJECTILE_COLUMNS:
        if kind in ("STR", "ARR"):
            continue
        add("        \"%s\"," % name)
    add("    ];")
    add("")
    add("    /// <summary>Indices into <see cref=\"Columns\"/> that hold a composite-effect id.</summary>")
    add("    public static readonly int[] EffectColumnIndices =")
    word_names = [n for n, k in PROJECTILE_COLUMNS if k not in ("STR", "ARR")]
    add("        [" + ", ".join(str(word_names.index(n)) for n in EFFECT_COLUMNS) + "];")
    add("")
    add("    /// <summary>Every row, ascending by id.</summary>")
    add("    public static readonly Z1ProjectileRow[] Rows =")
    add("    [")
    for row in sorted(rows, key=lambda r: r["ID"]):
        d = row["DATA"]
        flat = flatten(d, {})
        words = []
        for name, kind in PROJECTILE_COLUMNS:
            if kind in ("STR", "ARR"):
                continue
            words.append(encode(flat[name], kind))
        radii = [int(x["PLAYER_BULLET_RADIUS"]) for x in d["PLAYER_BULLET_RADIUS_LIST"]]
        add("        new(%du, %s, %s, %s, %s, [%s])," % (
            row["ID"],
            cs_string(flat["MODEL_FILE_NAME"]),
            cs_string(flat["FP_MODEL_FILE_NAME"]),
            cs_string(flat["BONE_ATTACHMENT_OVERRIDE"]),
            cs_uint_array(words),
            ", ".join("%du" % r for r in radii)))
    add("    ];")
    add("}")
    add("")
    path.write_text("\n".join(lines), encoding="utf-8")
    return len(rows)


def main() -> int:
    ap = argparse.ArgumentParser(description="emit the captured-table and Z1-projectile C# facts")
    ap.add_argument("--input", default=r"C:\Z1\Server\Data\friendWeaponDefinitions.bin")
    ap.add_argument("--schema", default=r"C:\Z1\Server\Data\weaponDefinitionSchema.json")
    ap.add_argument("--projectiles", default=r"C:\Z1\Server\Data\projectileDefinitions.json")
    ap.add_argument("--source-root", default=".")
    args = ap.parse_args()

    src_root = Path(args.source_root).resolve()
    report = DFT.decode(Path(args.input).read_bytes(), src_root, args.schema)
    if not report["walk1087"]["clean"]:
        raise SystemExit("the 1087 walk did not consume the body: %s" % report["walk1087"]["error"])

    crossed, missing, mode_rows, weapon_rows, cone_rows, aim_rows = build_weapon_facts(
        report, src_root)
    if missing:
        print("WARNING: no 1148 offset for %s" % ", ".join(missing))

    out = src_root / "src/Cranberry.Zone/Weapons/CapturedWeaponFacts.cs"
    body = DFT.load_fire_mode_body(src_root)
    kind_size = dict((off, DFT._KIND_SIZE[kind]) for off, kind in body)
    written = emit_weapon_facts(out, report, crossed, mode_rows, weapon_rows, cone_rows, aim_rows,
                               kind_size)
    print("wrote %s (%d lines): %d crossed offsets, %d weapon rows, %d fire modes, "
          "%d cone groups, %d aim-assist rows"
          % (out, written, len(crossed), len(weapon_rows), len(mode_rows),
             len(cone_rows), len(aim_rows)))

    out2 = src_root / "src/Cranberry.Zone/Weapons/Z1ProjectileFacts.cs"
    count = emit_projectile_facts(out2, Path(args.projectiles))
    print("wrote %s: %d projectile rows" % (out2, count))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
